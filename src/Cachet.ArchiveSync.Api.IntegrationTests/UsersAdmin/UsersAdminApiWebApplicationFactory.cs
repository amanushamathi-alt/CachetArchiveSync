using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests.UsersAdmin;

/// <summary>
/// <c>WebApplicationFactory&lt;Program&gt;</c> against the REAL <c>Cachet.ArchiveSync.Api/Program.cs</c>
/// (CAS-020 §6's four admin-provisioning endpoints), modeled directly on
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.Quarantine.QuarantineApiWebApplicationFactory</c> (same
/// pattern: swap only the DB/B2-backed registrations Program.cs makes; run every other real
/// middleware/DI wiring — cookie scheme, <c>OnValidatePrincipal</c>, the
/// <c>AdminProvisioningTokenFilter</c> gate itself — unmodified).
///
/// Unlike the other factories in this project, <c>ICredentialProtector</c> is NOT swapped with a
/// bare NSubstitute fake: CAS-020's SC-4 requires proving a value this factory's
/// <c>PUT /admin/users/{id}</c> encrypts is later decryptable by the SAME purpose-string protector
/// <c>LoginService</c> uses at sign-in, so the REAL <c>DataProtectionCredentialProtector</c>
/// registration from <c>Program.cs</c> is left in place — only its key ring's storage location is
/// redirected to a per-instance temp directory (same technique
/// <c>Auth.RealProgramWebApplicationFactory</c> already uses), giving genuine, round-trippable
/// in-process cryptography without ever touching the checked-in <c>.dataprotection-keys</c> folder.
///
/// <c>Admin:ProvisioningKey</c> (required by <c>Program.cs</c>'s fail-fast check, §9) is set via the
/// <c>Admin__ProvisioningKey</c> OS environment variable (CAS-019's naming convention), not
/// <c>ConfigureAppConfiguration</c>'s in-memory collection: <c>Program.cs</c> reads this value
/// directly off <c>builder.Configuration</c> BEFORE <c>builder.Build()</c> runs, and
/// <c>WebApplicationFactory</c>'s <c>ConfigureAppConfiguration</c> hook only takes effect at
/// <c>Build()</c> time — too late for this particular pre-Build fail-fast read (unlike
/// <c>Auth:DataProtection:KeyRingPath</c>, whose equivalent check passes only because
/// appsettings.Development.json already supplies a default; no such default exists, or should
/// exist, for a secret-shaped admin token — §9 explicitly calls out "never a literal value in
/// appsettings.json"). Environment variables, by contrast, are already loaded into
/// <c>builder.Configuration</c> synchronously inside <c>WebApplication.CreateBuilder(args)</c>
/// itself, so setting the process environment variable in this factory's constructor (before the
/// host is ever built) is visible in time.
/// </summary>
public sealed class UsersAdminApiWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string CookieName = "CachetAuth";
    public const string AdminProvisioningKey = "test-admin-provisioning-key-value";
    public const string AdminHeaderName = "X-Admin-Provisioning-Key";

    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();

    private readonly string _keyRingPath =
        Path.Combine(Path.GetTempPath(), "cachet-archivesync-tests-keyring-" + Guid.NewGuid().ToString("N"));

    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    public UsersAdminApiWebApplicationFactory()
    {
        // See class XML doc — must be set before the host is built, so this happens here rather
        // than via ConfigureAppConfiguration.
        Environment.SetEnvironmentVariable("Admin__ProvisioningKey", AdminProvisioningKey);

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
            services.RemoveAll<IUserRepository>();
            services.AddSingleton(UserRepository);

            services.RemoveAll<IUserB2KeyAuthorizer>();
            services.AddSingleton(B2KeyAuthorizer);

            services.RemoveAll<IAuditLogService>();
            services.AddSingleton(AuditLogService);

            // ICredentialProtector is deliberately left as Program.cs's real
            // DataProtectionCredentialProtector — see class XML doc.
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
