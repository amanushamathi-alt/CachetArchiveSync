using System.Security.Claims;
using Cachet.ArchiveSync.Core.Configuration;
using Cachet.ArchiveSync.Core.Interfaces;
using Cachet.ArchiveSync.Web;
using Cachet.ArchiveSync.Web.Components;
using Cachet.ArchiveSync.Web.Data;
using Cachet.ArchiveSync.Web.Http;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// CAS-041 §2: Api:BaseUrl and B2:BucketName from dbo.configuration_detail, loaded once at startup.
// Added last, so a non-blank DB value overrides the environment variable; a missing/blank row falls
// back to it. Web deliberately never receives the B2 system key (it never calls B2 — CAS-019/CAS-022),
// so only B2__BucketName is allow-listed from the B2_Keys group.
builder.Configuration.Sources.Add(new DatabaseConfigurationSource
{
    ConnectionString = builder.Configuration.GetConnectionString("CachetArchiveSync"),
    Enabled = builder.Configuration.GetValue<bool?>("ConfigurationDatabase:Enabled") ?? true,
    LoadGroup = ConfigurationDetailLoader.Load,
    AllowedNamesByGroup = new Dictionary<string, IReadOnlyCollection<string>>
    {
        ["Api_Url"] = new[] { "Api__BaseUrl" },
        ["B2_Keys"] = new[] { "B2__BucketName" }
    }
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// CAS-002 §6.6: Blazor Server circuit reads the signed-in user via the standard
// AuthenticationStateProvider/HttpContext.User mechanism, cascaded to components.
builder.Services.AddCascadingAuthenticationState();

// CAS-002 §6.3a: Web-side session-revocation check, used only by OnValidatePrincipal below —
// see Data/UserRepository.cs for why this narrow direct-DB-access exception exists.
builder.Services.AddScoped<IUserRepository, UserRepository>();

// CAS-002 §6.2/§6.6: shared cookie-authentication scheme and Data Protection key ring between
// Api and Web. Web hosts the cookie — the browser's only direct relationship is with Web.
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
        // Bug fix (2026-09-23) — same fix as Api's Program.cs: pin the cookie to the site root
        // explicitly rather than letting it default to Web's own PathBase. Web is normally
        // root-hosted so this is a no-op in practice, but keeping both sides pinned avoids the
        // two processes ever silently disagreeing on the cookie's scope.
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(slidingHours);
        options.SlidingExpiration = true;
        // CAS-021 §8.5: a direct, non-Blazor 401 (e.g. a stale API-style request) redirects to
        // the sign-in page this project actually builds, instead of the default /Account/Login.
        options.LoginPath = "/login";
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
                // failing open matches this spec's "fail closed" posture elsewhere and Security
                // Analysis' general defense-in-depth stance, but the spec text does not itself
                // state this narrow case. Same choice made identically in Api's Program.cs.
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Cachet.ArchiveSync.Web.Auth.OnValidatePrincipal");
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

    // CAS-021 §8.5 originally set a global FallbackPolicy here so any page without its own
    // [Authorize]/[AllowAnonymous] attribute defaulted to requiring a session. Removed
    // 2026-09-23: a FallbackPolicy applies to every ASP.NET Core endpoint that lacks explicit
    // authorization metadata — including the framework's own endpoint serving
    // _framework/blazor.web.js, which carries no [AllowAnonymous]. On the (unauthenticated)
    // login page, that endpoint was being redirected to /login, so the browser's <script> tag
    // received the login page's own HTML instead of JavaScript ("Uncaught SyntaxError:
    // Unexpected token '<'"), breaking Blazor's client runtime on that page. Per-page
    // protection now lives directly on each protected page instead (e.g. KeywordSearch.razor's
    // own [Authorize(Policy = "CachetAuthenticated")] attribute), enforced by AuthorizeRouteView
    // (Routes.razor) at the component level — this does not touch unrelated framework endpoints
    // the way a FallbackPolicy does. Any new page added to this project must carry its own
    // [Authorize]/[AllowAnonymous] attribute; there is no longer an implicit default.
});

// CAS-002 §6.6: server-side calls from Blazor Server component code to Api forward the current
// circuit's session cookie (CookieForwardingHandler) so Api recognizes the same authenticated
// session — no second login, no separate service-to-service credential.
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CookieForwardingHandler>();
var apiBaseUrl = builder.Configuration["Api:BaseUrl"];
if (string.IsNullOrWhiteSpace(apiBaseUrl))
{
    throw new InvalidOperationException(
        "Configuration key 'Api:BaseUrl' must be set (CAS-002 §6.6) — the Api base address this " +
        "Web project's server-side HttpClient calls.");
}
builder.Services.AddHttpClient("CachetApi", client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
}).AddHttpMessageHandler<CookieForwardingHandler>();

// CAS-044 §5: typed GET files/browse wrapper for the Files explorer (uses the "CachetApi" client above).
builder.Services.AddTransient<FilesBrowseClient>();
builder.Services.AddTransient<SearchResultsBrowseClient>();
// CAS-051 §5: typed client for the Files-browser quarantine endpoints (CAS-050 §8).
builder.Services.AddTransient<QuarantineBrowseClient>();

// CAS-030 §8.4/§9: one config-bound poll interval for the Task Manager badge and both screens'
// poll loops, validated at startup (V8 — out-of-range fails with OptionsValidationException).
builder.Services.AddOptions<TaskPollingOptions>()
    .Bind(builder.Configuration.GetSection(TaskPollingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// CAS-021 §6/§8.4: Web-hosted Sign Out handler for MainLayout.razor's plain <form> POST. Not a
// Blazor page — a real HTTP POST from the browser, so it can clear/replace the session cookie on
// a genuine response. Requires an authenticated session and a valid antiforgery token (§12 CSRF)
// before calling Api's already-authenticated POST /logout via the existing cookie-forwarding
// "CachetApi" client (CAS-002 §6.6).
app.MapPost("/logout", async (
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IHttpClientFactory httpClientFactory) =>
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        var client = httpClientFactory.CreateClient("CachetApi");
        var response = await client.PostAsync("logout", content: null, httpContext.RequestAborted);

        // Forward Api's expired Set-Cookie back to the browser verbatim (CAS-021 §8.2/§8.4).
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                httpContext.Response.Headers.Append("Set-Cookie", cookie);
            }
        }

        return Results.Redirect($"{httpContext.Request.PathBase}/login");
    })
    .RequireAuthorization("CachetAuthenticated");

// CAS-040 §8.5: File Viewer pass-through. The browser's only direct relationship is with Web
// (CAS-002 §6.6), so FileViewerModal's iframe and Download link navigate here, and these endpoints
// forward to Api's GET files/{id}/view|download through the same cookie-forwarding "CachetApi"
// client as /logout. GETs with no business-state change and a SameSite=Strict session cookie, so
// no antiforgery token (unlike /logout's POST). Unauthenticated requests are redirected to /login
// by the cookie scheme before Api is ever called (SC-18). No B2 dependency (SC-12).
app.MapGet("/files/{fileIndexId:int:min(1)}/view", (
        int fileIndexId,
        HttpContext httpContext,
        IHttpClientFactory httpClientFactory) =>
        ProxyFileAsync(fileIndexId, "view", httpContext, httpClientFactory))
    .RequireAuthorization("CachetAuthenticated");

app.MapGet("/files/{fileIndexId:int:min(1)}/download", (
        int fileIndexId,
        HttpContext httpContext,
        IHttpClientFactory httpClientFactory) =>
        ProxyFileAsync(fileIndexId, "download", httpContext, httpClientFactory))
    .RequireAuthorization("CachetAuthenticated");

app.Run();

// CAS-040 §8.5: streams Api's body through (never buffered), forwards only the allowlisted
// headers (never Set-Cookie), always sets the four security headers, and replaces any Api non-2xx
// body with a fixed message — Api's ProblemDetails body is never forwarded.
static async Task ProxyFileAsync(int fileIndexId, string operation, HttpContext httpContext, IHttpClientFactory httpClientFactory)
{
    // Fixed, static content — nothing interpolated (§8.5).
    const string ViewErrorPage =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Preview unavailable</title>" +
        "<style>body{font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#1b2430;margin:16px;}</style>" +
        "</head><body><p>This preview could not be displayed.</p></body></html>";
    const string DownloadErrorMessage = "The file could not be downloaded.";
    const string HtmlPreviewCsp =
        "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'; sandbox";

    string[] forwardedResponseHeaders = { "Content-Security-Policy", "X-Cachet-Preview-Truncated", "X-Cachet-Source-Encoding" };

    var response = httpContext.Response;
    var ct = httpContext.RequestAborted;

    // Always set, whatever Api sent.
    response.Headers.XContentTypeOptions = "nosniff";
    response.Headers.CacheControl = "no-store, no-cache, max-age=0";
    response.Headers["Referrer-Policy"] = "no-referrer";
    response.Headers.XFrameOptions = "SAMEORIGIN";

    // context is forwarded unchanged; Api owns its validation (V2).
    var context = httpContext.Request.Query["context"];
    var apiUri = context.Count > 0
        ? $"files/{fileIndexId}/{operation}?context={Uri.EscapeDataString(context.ToString())}"
        : $"files/{fileIndexId}/{operation}";

    var client = httpClientFactory.CreateClient("CachetApi");
    HttpResponseMessage apiResponse;
    try
    {
        apiResponse = await client.GetAsync(apiUri, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    catch (HttpRequestException)
    {
        // Api unreachable — same fixed body as a non-2xx, with a gateway status.
        await WriteFixedErrorAsync(StatusCodes.Status502BadGateway);
        return;
    }

    using (apiResponse)
    {
        if (!apiResponse.IsSuccessStatusCode)
        {
            await WriteFixedErrorAsync((int)apiResponse.StatusCode);
            return;
        }

        response.StatusCode = (int)apiResponse.StatusCode;

        var contentHeaders = apiResponse.Content.Headers;
        if (contentHeaders.ContentType is not null)
        {
            response.ContentType = contentHeaders.ContentType.ToString();
        }

        if (contentHeaders.ContentDisposition is not null)
        {
            response.Headers.ContentDisposition = contentHeaders.ContentDisposition.ToString();
        }

        if (contentHeaders.ContentLength is { } length)
        {
            response.ContentLength = length;
        }

        foreach (var name in forwardedResponseHeaders)
        {
            if (apiResponse.Headers.TryGetValues(name, out var values))
            {
                response.Headers[name] = values.ToArray();
            }
        }

        await using var body = await apiResponse.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(response.Body, ct);
    }

    async Task WriteFixedErrorAsync(int statusCode)
    {
        response.StatusCode = statusCode;
        if (operation == "view")
        {
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.ContentSecurityPolicy = HtmlPreviewCsp;
            await response.WriteAsync(ViewErrorPage, ct);
        }
        else
        {
            response.ContentType = "text/plain; charset=utf-8";
            await response.WriteAsync(DownloadErrorMessage, ct);
        }
    }
}

// Exposes the top-level-statement entry point to WebApplicationFactory<Program> in the test
// assemblies (CAS-002 test-writer note); adds no runtime behavior.
public partial class Program { }
