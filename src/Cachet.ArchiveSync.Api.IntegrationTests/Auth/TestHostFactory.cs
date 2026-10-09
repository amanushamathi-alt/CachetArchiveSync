using System.Security.Claims;
using Cachet.ArchiveSync.Api.Controllers;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests.Auth;

/// <summary>
/// Self-hosted <c>TestServer</c> that mounts the REAL <see cref="AuthController"/> behind the
/// exact cookie-authentication and authorization-policy configuration CAS-002 §6.2/§6.4 specifies
/// (copied verbatim from <c>Cachet.ArchiveSync.Api/Program.cs</c>), plus two policy-only
/// <c>/test/*</c> probe endpoints used by SC-2/SC-4/SC-5 (CAS-002 has not yet built any real
/// <c>CachetAuthenticated</c>/<c>CachetFullAccess</c>-gated business endpoint to test against, per
/// the orchestrator's scope-reality note for this run).
///
/// Deliberately does NOT use <c>WebApplicationFactory&lt;Program&gt;</c>: <c>Program.cs</c> uses
/// top-level statements, which the compiler emits as an <c>internal</c> entry-point class not
/// visible to this test assembly, and this project's own test-writer scope does not include
/// modifying <c>Program.cs</c> (implementation code) to add a testability shim. Flagged for the
/// reviewer/implementer: adding <c>public partial class Program {}</c> to the bottom of both
/// <c>Cachet.ArchiveSync.Api/Program.cs</c> and <c>Cachet.ArchiveSync.Web/Program.cs</c> would let
/// a future test suite use <c>WebApplicationFactory&lt;Program&gt;</c> directly instead of this
/// hand-built host; not done here since it is a (trivial, behavior-free) implementation change.
///
/// No real database or B2 call is ever made: <see cref="IUserRepository"/>,
/// <see cref="ICredentialProtector"/>, <see cref="IUserB2KeyAuthorizer"/>, and
/// <see cref="IAuditLogService"/> are all NSubstitute fakes the caller configures per test, so
/// <see cref="Services.LoginService"/> (the real implementation) runs its actual two-stage-check
/// and claim-derivation logic against those fakes.
/// </summary>
public sealed class TestHostFactory : IAsyncDisposable
{
    public const string CookieName = "CachetAuth";
    private const string AuthScheme = "CachetAuth";

    private IHost? _host;

    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
    public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
    public ManualTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    // CAS-002 §6.3a: backs UserRepository's GetSessionTokenVersionAsync/
    // IncrementSessionTokenVersionAsync fakes with a real, mutable per-user counter — an
    // NSubstitute static .Returns(...) value can't model "incremented by logout, re-read by the
    // next request's OnValidatePrincipal", which is exactly the behavior SC-3 exercises.
    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    public TestHostFactory()
    {
        UserRepository
            .GetSessionTokenVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var userId = callInfo.Arg<int>();
                return Task.FromResult<int?>(_sessionTokenVersions.TryGetValue(userId, out var v) ? v : null);
            });

        UserRepository
            .IncrementSessionTokenVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var userId = callInfo.Arg<int>();
                var newVersion = (_sessionTokenVersions.TryGetValue(userId, out var v) ? v : 0) + 1;
                _sessionTokenVersions[userId] = newVersion;
                return Task.FromResult(newVersion);
            });
    }

    /// <summary>Registers a user's starting <c>session_token_version</c> (default 0, matching the
    /// column's DB default) so <see cref="UserRepository"/>'s login-time <c>GetByUsernameAsync</c>
    /// fake (configured per-test via <c>SeedUser</c>) and the revocation-check fakes above agree.</summary>
    public int RegisterSessionTokenVersion(int userId, int startingVersion = 0)
    {
        if (!_sessionTokenVersions.ContainsKey(userId))
        {
            _sessionTokenVersions[userId] = startingVersion;
        }

        return _sessionTokenVersions[userId];
    }

    public async Task<HttpClient> StartAsync()
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();

                    // Fakes in place of the real DB/B2 dependencies (see class XML doc).
                    services.AddSingleton(UserRepository);
                    services.AddSingleton(CredentialProtector);
                    services.AddSingleton(B2KeyAuthorizer);
                    services.AddSingleton(AuditLogService);
                    services.AddScoped<ILoginService, Services.LoginService>();

                    // CAS-002 §6.2 verbatim, with a controllable clock (§10 SC-2) in place of
                    // wall-clock time. No PersistKeysToFileSystem call: a single in-process test
                    // host needs only the framework's default ephemeral key ring — CAS-002 §6.6's
                    // shared-key-ring requirement is about Web<->Api forwarding across two real
                    // processes, which SC-7 covers separately at the CookieForwardingHandler level
                    // (Cachet.ArchiveSync.Web.Tests), not by standing up two hosts here.
                    services.AddAuthentication(AuthScheme)
                        .AddCookie(AuthScheme, options =>
                        {
                            options.Cookie.Name = CookieName;
                            options.Cookie.HttpOnly = true;
                            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                            options.Cookie.SameSite = SameSiteMode.Strict;
                            options.ExpireTimeSpan = TimeSpan.FromHours(8);
                            options.SlidingExpiration = true;
                            options.TimeProvider = Clock;
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
                            // CAS-002 §6.3a verbatim (Api Program.cs) — this is the mechanism
                            // SC-3 actually exercises, so the test host must run the real logic,
                            // not merely trust that Program.cs has it.
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
                                    context.RejectPrincipal();
                                    return;
                                }

                                if (currentVersion is null || currentVersion.Value != sessionVer)
                                {
                                    context.RejectPrincipal();
                                }
                            };
                        });

                    // CAS-002 §6.4 verbatim.
                    services.AddAuthorization(options =>
                    {
                        options.AddPolicy("CachetFullAccess", policy =>
                            policy.RequireClaim("b2_capability", "Full Access"));
                        options.AddPolicy("CachetAuthenticated", policy =>
                            policy.RequireAuthenticatedUser());
                    });

                    services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
                });

                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();

                        // §6.4 policy probes — see class XML doc for why these stand in for a real
                        // gated business endpoint that does not exist in this codebase yet.
                        endpoints.MapGet("/test/authenticated-ping", () => Results.Ok())
                            .RequireAuthorization("CachetAuthenticated");
                        endpoints.MapGet("/test/full-access-ping", () => Results.Ok())
                            .RequireAuthorization("CachetFullAccess");
                    });
                });
            });

        _host = await hostBuilder.StartAsync();
        return _host.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
