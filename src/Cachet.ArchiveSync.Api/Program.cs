using System.Security.Claims;
using Cachet.ArchiveSync.Api.B2;
using Cachet.ArchiveSync.Api.Content;
using Cachet.ArchiveSync.Api.Filters;
using Cachet.ArchiveSync.Api.Reporting;
using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Api.Storage;
using Cachet.ArchiveSync.Api.Swagger;
using Cachet.ArchiveSync.Core.Configuration;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// CAS-040 §8.1: Windows-1252 fallback for the TXT preview decoder — registered once at startup.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

// CAS-041 §2: B2 system settings from dbo.configuration_detail, loaded once at startup. Added last,
// so a non-blank DB value overrides the environment variable; a missing/blank row falls back to it.
builder.Configuration.Sources.Add(new DatabaseConfigurationSource
{
    ConnectionString = builder.Configuration.GetConnectionString("CachetArchiveSync"),
    Enabled = builder.Configuration.GetValue<bool?>("ConfigurationDatabase:Enabled") ?? true,
    LoadGroup = ConfigurationDetailLoader.Load,
    AllowedNamesByGroup = new Dictionary<string, IReadOnlyCollection<string>>
    {
        ["B2_Keys"] = new[] { "B2__SystemKeyId", "B2__SystemApplicationKey", "B2__BucketName" }
    }
});

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // CAS-020 post-review Swagger amendment (2026-09-23): lets Swagger UI's "Authorize" button
    // capture X-Admin-Provisioning-Key and attach it to /admin/users* calls made via "Try it
    // out" -- without this, Swagger has no input for the header and every admin call 401s.
    // AdminProvisioningKeyOperationFilter scopes this to only those four endpoints.
    options.AddSecurityDefinition(AdminProvisioningKeyOperationFilter.SchemeId, new OpenApiSecurityScheme
    {
        Name = "X-Admin-Provisioning-Key",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Static admin token required for /admin/users* endpoints (CAS-020 §12)."
    });
    options.OperationFilter<AdminProvisioningKeyOperationFilter>();
});

// CAS-002 §6.2/§6.6: shared cookie-authentication scheme and Data Protection key ring between
// Api and Web. Cookie attributes and sliding expiration are read from config (§9); no B2 or
// audit-log dependency here, so this wiring is not part of any BLOCKED item for this spec.
const string AuthScheme = "CachetAuth";

var cookieName = builder.Configuration["Auth:CookieName"] ?? "CachetAuth";
var slidingHours = builder.Configuration.GetValue<double?>("Auth:SlidingExpirationHours") ?? 8;
var keyRingPathConfig = builder.Configuration["Auth:DataProtection:KeyRingPath"];
if (string.IsNullOrWhiteSpace(keyRingPathConfig))
{
    throw new InvalidOperationException(
        "Configuration key 'Auth:DataProtection:KeyRingPath' must be set (CAS-002 §9).");
}
var keyRingPath = Path.GetFullPath(keyRingPathConfig, builder.Environment.ContentRootPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName("Cachet.ArchiveSync");

builder.Services.AddAuthentication(AuthScheme)
    .AddCookie(AuthScheme, options =>
    {
        options.Cookie.Name = cookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // Bug fix (2026-09-23, reported against CAS-002/CAS-021's login flow): pin the cookie to
        // the site root explicitly. Left unset, ASP.NET Core defaults Cookie.Path to the current
        // request's PathBase when the Set-Cookie header is built — under an IIS virtual
        // application (e.g. /cachet_archive_sync_api/), that scopes the cookie to that sub-path.
        // Web forwards this Set-Cookie header verbatim onto its own (root-hosted) response, so a
        // path-scoped cookie is stored by the browser but never sent back on requests to Web's
        // own pages ("/", "/login") — the user appears to sign in successfully (redirect fires)
        // but lands back on /login with no session. Pinning to "/" makes the cookie valid across
        // the whole site regardless of which IIS virtual path Api itself is hosted under.
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(slidingHours);
        options.SlidingExpiration = true;
        // §6.2: direct Api callers get a plain 401/403, not a browser redirect to a login page.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        // CAS-002 §6.3a: server-side session revocation. Registered identically in Api's and
        // Web's Program.cs. Reads the session_ver claim off the incoming principal, compares it
        // against the live dbo.users.session_token_version value, and rejects the principal on
        // any mismatch or lookup failure — a rejected principal behaves exactly like an expired
        // one (§6.2's existing 401/redirect-to-login behavior). IUserRepository is resolved from
        // RequestServices because OnValidatePrincipal is not itself DI-constructed.
        options.Events.OnValidatePrincipal = async context =>
        {
            var userIdClaim = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var sessionVerClaim = context.Principal?.FindFirst("session_ver")?.Value;

            if (userIdClaim is null
                || sessionVerClaim is null
                || !int.TryParse(userIdClaim, out var userId)
                || !int.TryParse(sessionVerClaim, out var sessionVer))
            {
                context.RejectPrincipal();
                return;
            }

            var userRepository = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();

            int? currentVersion;
            try
            {
                currentVersion = await userRepository.GetSessionTokenVersionAsync(userId, context.HttpContext.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // BLOCKED (implementer, CAS-002): §6.3a is silent on what to do if the DB lookup
                // itself fails (vs. a clean version mismatch). Failing closed (reject) rather than
                // failing open (let a stale/unverifiable session through) matches this spec's
                // "fail closed" posture elsewhere (§6.1's password/format-error handling) and
                // Security Analysis' general defense-in-depth stance, but the spec text does not
                // itself state this narrow case.
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Cachet.ArchiveSync.Api.Auth.OnValidatePrincipal");
                logger.LogWarning(ex, "Session revocation check failed for {UserId}; rejecting principal (fail closed)", userId);
                context.RejectPrincipal();
                return;
            }

            if (currentVersion is null || currentVersion.Value != sessionVer)
            {
                context.RejectPrincipal();
            }
        };
    });

// CAS-002 §6.4: resolves CAS-001 §12's deferred policy-name item. Verbatim from spec.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CachetFullAccess", policy =>
        policy.RequireClaim("b2_capability", "Full Access"));

    // Any authenticated session qualifies — Read Only is the floor, not a separate claim check.
    options.AddPolicy("CachetAuthenticated", policy =>
        policy.RequireAuthenticatedUser());
});

// CAS-005: shared audit-log write path (FR-23) — used by other modules' controllers/services.
builder.Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();

// CAS-009 §6: GET /audit-log read path (FR-23/FR-24). Untouched write path above.
builder.Services.AddScoped<IAuditLogListService, AuditLogListService>();

// CAS-002 §6.1: POST /login's two-stage check and claim derivation.
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ILoginService, LoginService>();

// CAS-006 §6/§6.2/§6.3: Keyword Search + Quarantine-add endpoints.
builder.Services.AddScoped<ISearchResultRepository, SearchResultRepository>();
builder.Services.AddScoped<IQuarantineEntryRepository, QuarantineEntryRepository>();
builder.Services.AddScoped<ITaskQueueRepository, TaskQueueRepository>();
builder.Services.AddScoped<IFileIndexRepository, FileIndexRepository>();
builder.Services.AddScoped<IKeywordSearchService, KeywordSearchService>();
builder.Services.AddScoped<IQuarantineService, QuarantineService>();

// CAS-012 §2/§5: filesystem-based Content Match reads — resolves a candidate's physical mirror
// file (O-Drive or B2 mirror root, per dbo.odrive_bucket_mapping) and matches it against the
// search term in application code, replacing the removed SQL CONTAINS/content_text path.
builder.Services.AddScoped<IBucketMirrorRootRepository, BucketMirrorRootRepository>();
builder.Services.AddScoped<IMirrorFileReader, MirrorFileReader>();
// CAS-062: Quarantine Permanent Delete also removes the file's local B2 mirror copy.
builder.Services.AddScoped<IB2MirrorFileRemover, B2MirrorFileRemover>();
builder.Services.AddScoped<IFileContentMatcher, FileContentMatcher>();

// CAS-008 §6/§6.4: Quarantine list + Permanent Delete endpoints.
builder.Services.AddScoped<IQuarantineListService, QuarantineListService>();
builder.Services.AddScoped<IQuarantinePermanentDeleteService, QuarantinePermanentDeleteService>();
// CAS-014 §2.2/§8: Quarantine "Remove Item" endpoint (catalog-only hard delete, no B2/file_index).
builder.Services.AddScoped<IQuarantineRemoveService, QuarantineRemoveService>();
// CAS-050 §8: Files-browser Quarantine add/remove/state (one row per file, folder = set-based expansion).
builder.Services.AddScoped<IQuarantineBrowseService, QuarantineBrowseService>();

// CAS-007 §6/§6.2/§6.3: O-Drive Sync and Content Scan endpoints (scan/results + copy/update/purge).
builder.Services.AddScoped<ISyncResultRepository, SyncResultRepository>();
builder.Services.AddScoped<ISyncScanService, SyncScanService>();
builder.Services.AddScoped<ISyncActionService, SyncActionService>();
// CAS-013 §2/§5: per-bucket local O-Drive mirror root resolution (dbo.odrive_bucket_mapping),
// consumed by ODriveFileReader instead of the removed single flat Sync:ODriveMirrorRootPath key.
builder.Services.AddScoped<IBucketRootMappingRepository, BucketRootMappingRepository>();
builder.Services.AddScoped<IODriveFileReader, ODriveFileReader>();
// CAS-037 §6: POST /sync/mirror-sync submission. Never calls IB2Client directly — only the
// Worker's B2MirrorSyncTaskProcessor does; this service only needs the already-registered
// ITaskQueueRepository/IAuditLogService above.
builder.Services.AddScoped<IB2MirrorSyncService, B2MirrorSyncService>();
// Decrypts dbo.users.b2_application_key(_id) ciphertext — see ICredentialProtector's XML doc for
// why this is flagged as a best-effort implementation choice, not a spec-confirmed decision.
builder.Services.AddSingleton<ICredentialProtector, DataProtectionCredentialProtector>();
// Authorizes a caller-supplied (per-user) B2 key at login — see IUserB2KeyAuthorizer's XML doc
// for why this is deliberately not IB2Client (CAS-001 §7 V1 locks that client to the system key).
builder.Services.AddSingleton<IUserB2KeyAuthorizer, UserB2KeyAuthorizer>();

// CAS-001 §6.1: named HttpClient managed by IHttpClientFactory (pooled handler lifetime), wrapped
// by a single long-lived B2Client instance so its in-memory authorization-token cache is genuinely
// per-process (§6.1 token lifecycle), not re-created (and re-authenticated) on every resolution —
// which a default-lifetime typed client would do. B2Client's constructor itself enforces §7 V1
// (system key only, read from configuration — never a per-request value can reach it).
builder.Services.AddHttpClient("B2Client");
builder.Services.AddSingleton<IB2Client>(sp =>
{
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("B2Client");
    return new B2Client(httpClient, sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILogger<B2Client>>());
});

// CAS-018 §2/§5/§14: user-scoped B2 delete client for Permanent Delete only — executes
// b2_list_file_versions/b2_delete_file_version under the acting user's own live authorization,
// never the system key. Registered the same way as IB2Client (singleton, HttpClient resolved from
// the same pooled "B2Client" named client) so its cached system-key-derived bucket id is
// genuinely per-process, not re-resolved on every scope.
builder.Services.AddSingleton<IUserScopedB2DeleteClient>(sp =>
{
    var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("B2Client");
    return new UserScopedB2DeleteClient(
        httpClient, sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILogger<UserScopedB2DeleteClient>>());
});

// CAS-010 §6: Reporting API — POST /reports/pre-action, POST /reports/post-action (FR-25/FR-26).
// CAS-015 §2/§5/§9: on-demand generation only — no IReportRepository/dbo.report persistence, no
// Reports:LocalStorageRootPath config dependency; each POST renders and streams its requested
// format directly, nothing survives past the response.
builder.Services.AddScoped<PdfSharpReportWriter>();
builder.Services.AddScoped<ClosedXmlReportWriter>();
builder.Services.AddScoped<IReportDocumentWriter, ReportDocumentWriter>();
builder.Services.AddScoped<IPreActionReportService, PreActionReportService>();
builder.Services.AddScoped<IPostActionReportService, PostActionReportService>();

// CAS-016 §5/§8: Task Manager restart endpoint (POST /tasks/{taskId}/restart) — reuses the
// already-registered ITaskQueueRepository/IAuditLogService above.
builder.Services.AddScoped<ITaskManagerService, TaskManagerService>();
// CAS-026 §6: Task Manager list/detail/badge read+write endpoints (GET /tasks, GET /tasks/{taskId},
// GET /tasks/summary, POST /tasks/mark-viewed — FR-19/FR-20/FR-22). Reuse the already-registered
// ITaskQueueRepository/IUserRepository above.
builder.Services.AddScoped<ITaskBadgeService, TaskBadgeService>();

// CAS-040 §6/§9: in-browser File Viewer and Download (GET /files/{fileIndexId}, /view, /download).
// Reuses the already-registered IFileIndexRepository/IMirrorFileReader/IAuditLogService above; no
// IB2Client, IUserScopedB2DeleteClient or ICredentialProtector dependency on this path (§12, SC-12).
builder.Services.AddOptions<FilePreviewOptions>()
    .Bind(builder.Configuration.GetSection(FilePreviewOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
// Singleton: the conversion semaphore must be process-wide to bound concurrent NPOI parses (§12).
builder.Services.AddSingleton<PreviewConversionLimiter>();
builder.Services.AddSingleton<IOfficePreviewRenderer, OfficePreviewRenderer>();
builder.Services.AddScoped<IFileContentService, FileContentService>();
// CAS-043 §3: Files browse (GET /files/browse) — index metadata only, prefix read from the bucket mapping.
builder.Services.AddScoped<IB2PathPrefixProvider, B2PathPrefixProvider>();
builder.Services.AddScoped<IFileBrowseRepository, FileBrowseRepository>();
builder.Services.AddScoped<IFileBrowseService, FileBrowseService>();
// CAS-049: browse a completed search's results as folders/files (GET /search/{taskId}/browse[/result-ids]).
builder.Services.AddScoped<ISearchResultBrowseRepository, SearchResultBrowseRepository>();
builder.Services.AddScoped<ISearchResultBrowseService, SearchResultBrowseService>();

// CAS-020 §9: fails fast at startup if unset, same pattern as Auth:DataProtection:KeyRingPath
// above — never a literal value in appsettings.json, env-var only (Admin__ProvisioningKey, CAS-019
// naming convention).
var adminProvisioningKey = builder.Configuration["Admin:ProvisioningKey"];
if (string.IsNullOrWhiteSpace(adminProvisioningKey))
{
    throw new InvalidOperationException(
        "Configuration key 'Admin:ProvisioningKey' must be set (CAS-020 §9).");
}

// CAS-020 §3/§6: admin-only user provisioning (create/edit/deactivate/reactivate), gated by
// AdminProvisioningTokenFilter rather than the cookie-session policies above.
builder.Services.AddScoped<IUserProvisioningService, UserProvisioningService>();
builder.Services.AddScoped<AdminProvisioningTokenFilter>();

var app = builder.Build();

// Configure the HTTP request pipeline.
// OpenAPI JSON document (/swagger/v1/swagger.json) and the interactive Swagger UI (/swagger) are
// both served in every environment, per explicit instruction (2026-09-23) overriding the earlier
// Development-only UI gate. No auth/network restriction added in front of it.
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Exposes the top-level-statement entry point to WebApplicationFactory<Program> in the test
// assemblies (CAS-002 test-writer note); adds no runtime behavior.
public partial class Program { }

