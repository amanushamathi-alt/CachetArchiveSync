using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests.Auth;

/// <summary>
/// <c>WebApplicationFactory&lt;Program&gt;</c> against the REAL
/// <c>Cachet.ArchiveSync.Api/Program.cs</c> (now that it exposes
/// <c>public partial class Program {}</c>) — used purely as a coverage-closing supplement to
/// <see cref="TestHostFactory"/>, whose hand-built host was necessary before that shim existed but
/// leaves the real DI/middleware wiring (cookie scheme, <c>OnValidatePrincipal</c> session
/// revocation, <c>CachetFullAccess</c>/<c>CachetAuthenticated</c> policies) at 0% line coverage.
///
/// No real SQL Server or B2 endpoint is ever reached: <see cref="IUserRepository"/>,
/// <see cref="ICredentialProtector"/>, <see cref="IUserB2KeyAuthorizer"/>, and
/// <see cref="IAuditLogService"/> are all NSubstitute fakes, swapped in for the real DI
/// registrations via <see cref="WebApplicationFactory{TEntryPoint}.ConfigureWebHost"/>'s standard
/// <c>ConfigureTestServices</c>-equivalent pattern — the documented way to replace a real
/// dependency in a <c>WebApplicationFactory</c>-hosted app without touching <c>Program.cs</c>.
///
/// The Data Protection key ring path (<c>Auth:DataProtection:KeyRingPath</c>, required by
/// <c>Program.cs</c> or it throws at startup) is overridden to a per-instance temp directory so
/// test runs never read or write the checked-in <c>../.dataprotection-keys</c> folder next to the
/// real appsettings.Development.json.
/// </summary>
public sealed class RealProgramWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string CookieName = "CachetAuth";

    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
    public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();

    private readonly string _keyRingPath =
        Path.Combine(Path.GetTempPath(), "cachet-archivesync-tests-keyring-" + Guid.NewGuid().ToString("N"));

    // CAS-002 §6.3a: same real, mutable per-user counter approach as TestHostFactory — an
    // NSubstitute static .Returns(...) can't model "incremented by logout, re-read by the next
    // request's OnValidatePrincipal", which is exactly what these tests exercise through the real
    // pipeline.
    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    public RealProgramWebApplicationFactory()
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

    public int RegisterSessionTokenVersion(int userId, int startingVersion = 0)
    {
        if (!_sessionTokenVersions.ContainsKey(userId))
        {
            _sessionTokenVersions[userId] = startingVersion;
        }

        return _sessionTokenVersions[userId];
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // WebApplicationFactory defaults to the "Development" environment, so
        // appsettings.Development.json (which sets Auth:CookieName/SlidingExpirationHours/
        // DataProtection:KeyRingPath — required by Program.cs or it throws at startup) is already
        // picked up; only the key ring path is overridden below to avoid touching the real one.
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:DataProtection:KeyRingPath"] = _keyRingPath
            });
        });

        builder.ConfigureServices(services =>
        {
            // Real DB/B2-backed registrations from Program.cs, replaced with the fakes above —
            // this is the only change from the real app's DI graph; every other registration
            // (cookie scheme, OnValidatePrincipal, authorization policies, controllers) runs
            // exactly as Program.cs wires it.
            services.RemoveAll<IUserRepository>();
            services.AddSingleton(UserRepository);

            services.RemoveAll<ICredentialProtector>();
            services.AddSingleton(CredentialProtector);

            services.RemoveAll<IUserB2KeyAuthorizer>();
            services.AddSingleton(B2KeyAuthorizer);

            services.RemoveAll<IAuditLogService>();
            services.AddSingleton(AuditLogService);
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
