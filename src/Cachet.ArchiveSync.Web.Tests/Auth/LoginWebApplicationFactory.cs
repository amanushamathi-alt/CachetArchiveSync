using System.Globalization;
using System.Security.Claims;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cachet.ArchiveSync.Web.Tests.Auth;

/// <summary>
/// <c>WebApplicationFactory&lt;Program&gt;</c> against the REAL <c>Cachet.ArchiveSync.Web/Program.cs</c>
/// (CAS-021 §8.2/§8.5/§6 — the login/logout minimal-API handlers, the shared "CachetAuth" cookie
/// scheme, <c>OnValidatePrincipal</c> session revocation, and the <c>FallbackPolicy</c>/
/// <c>AuthorizeRouteView</c> route gate), matching the precedent already established by
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.Auth.RealProgramWebApplicationFactory</c>.
///
/// No real Api process, SQL Server, or B2 endpoint is ever reached:
/// <list type="bullet">
/// <item><description><see cref="ApiHandler"/> replaces the "CachetApi" named <c>HttpClient</c>'s
/// primary <see cref="HttpMessageHandler"/> (the one thing standing in for a real Api process, per
/// this spec's test-writer instructions).</description></item>
/// <item><description><see cref="UserRepository"/> replaces Web's own <c>IUserRepository</c> (used
/// only by <c>OnValidatePrincipal</c>'s <c>dbo.users_get_session_token_version</c> check, CAS-002
/// §6.3a) with an NSubstitute fake backed by a real, mutable per-user counter — the same "why not a
/// static <c>.Returns(...)</c>" reasoning as <c>RealProgramWebApplicationFactory</c>.</description></item>
/// </list>
///
/// The Data Protection key ring path is overridden to a per-instance temp directory so test runs
/// never read/write the checked-in <c>../.dataprotection-keys</c> folder next to the real
/// <c>appsettings.Development.json</c>.
/// </summary>
public sealed class LoginWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string AuthScheme = "CachetAuth";

    public FakeCachetApiHandler ApiHandler { get; } = new();
    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public CapturingLoggerProvider CapturedLogs { get; } = new();

    private readonly string _keyRingPath =
        Path.Combine(Path.GetTempPath(), "cachet-web-tests-keyring-" + Guid.NewGuid().ToString("N"));

    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    public LoginWebApplicationFactory()
    {
        UserRepository
            .GetSessionTokenVersionAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var userId = callInfo.Arg<int>();
                return Task.FromResult<int?>(_sessionTokenVersions.TryGetValue(userId, out var v) ? v : null);
            });
    }

    /// <summary>Seeds/overwrites the shared <c>dbo.users.session_token_version</c> value
    /// <c>OnValidatePrincipal</c> reads for this user. Mirrors what a real Api-side logout call
    /// would have bumped via its own <c>IncrementSessionTokenVersionAsync</c> (CAS-002 §6.3/§6.3a) —
    /// Web's own <see cref="UserRepository"/> is read-only for that column (see
    /// <c>Cachet.ArchiveSync.Web/Data/UserRepository.cs</c>), so tests simulate the write side of
    /// the shared table directly.</summary>
    public void RegisterSessionTokenVersion(int userId, int version) => _sessionTokenVersions[userId] = version;

    /// <summary>
    /// Mints a session cookie value using THIS host's own "CachetAuth" cookie-authentication options
    /// (same <c>TicketDataFormat</c>/key ring a real Api process would share under CAS-002 §6.6),
    /// standing in for "Api already authenticated this user and returned a Set-Cookie header" without
    /// a second real process. Forces the test host to finish starting if it has not already.
    /// </summary>
    public string CreateSessionSetCookieHeader(int userId, string sessionVer, string b2Capability)
    {
        _ = Server; // Ensure the host is built before resolving options/services below.

        var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthScheme);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString(CultureInfo.InvariantCulture)),
            new Claim("b2_capability", b2Capability),
            new Claim("session_ver", sessionVer),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthScheme));
        var ticket = new AuthenticationTicket(principal, AuthScheme);

        var protectedValue = options.TicketDataFormat.Protect(ticket);
        return $"{options.Cookie.Name}={protectedValue}; path=/; httponly";
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // WebApplicationFactory defaults to loading appsettings.Development.json (Auth:CookieName/
        // SlidingExpirationHours/DataProtection:KeyRingPath, Api:BaseUrl — all required by Program.cs
        // or it throws at startup) only once the environment is explicitly Development.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:DataProtection:KeyRingPath"] = _keyRingPath,
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUserRepository>();
            services.AddSingleton(UserRepository);

            // Swaps the real "CachetApi" HttpClient's primary handler for the controllable fake —
            // the CookieForwardingHandler Program.cs also attaches keeps running around it unchanged.
            services.AddHttpClient("CachetApi").ConfigurePrimaryHttpMessageHandler(() => ApiHandler);

            services.AddLogging(loggingBuilder => loggingBuilder.AddProvider(CapturedLogs));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                if (Directory.Exists(_keyRingPath))
                {
                    Directory.Delete(_keyRingPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of a per-instance temp directory; never fail a test over it.
            }
        }
    }
}
