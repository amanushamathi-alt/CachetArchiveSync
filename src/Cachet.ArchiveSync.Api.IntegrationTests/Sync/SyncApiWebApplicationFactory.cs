using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests.Sync;

/// <summary>
/// <c>WebApplicationFactory&lt;Program&gt;</c> against the REAL <c>Cachet.ArchiveSync.Api/Program.cs</c>
/// (CAS-007 §6's five O-Drive Sync endpoints), modeled directly on
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.Search.SearchApiWebApplicationFactory</c> (same
/// pattern: swap only the DB/B2-backed registrations Program.cs makes, run every other real
/// middleware/DI wiring — cookie scheme, <c>OnValidatePrincipal</c>, <c>CachetAuthenticated</c>/
/// <c>CachetFullAccess</c> policies — unmodified). In addition to the Auth-surface fakes (needed
/// here too, since a real session must be established via <c>POST /login</c> before calling any
/// authorized endpoint), this factory also swaps <see cref="ITaskQueueRepository"/> and
/// <see cref="ISyncResultRepository"/> — the two repositories
/// <see cref="Services.SyncScanService"/>/<see cref="Services.SyncActionService"/> depend on. No
/// real SQL Server or B2 endpoint is ever reached.
/// </summary>
public sealed class SyncApiWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string CookieName = "CachetAuth";

    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
    public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
    public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
    public ISyncResultRepository SyncResultRepository { get; } = Substitute.For<ISyncResultRepository>();

    private readonly string _keyRingPath =
        Path.Combine(Path.GetTempPath(), "cachet-archivesync-tests-keyring-" + Guid.NewGuid().ToString("N"));

    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    public SyncApiWebApplicationFactory()
    {
        // CAS-020 §9: Program.cs fails fast at startup without Admin:ProvisioningKey, and (as in
        // UsersAdminApiWebApplicationFactory) only an environment variable is visible at that point.
        // Test-only value, never a real secret; without it these tests fail when run in isolation.
        Environment.SetEnvironmentVariable("Admin__ProvisioningKey", "test-admin-provisioning-key-value");

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
                ["Auth:DataProtection:KeyRingPath"] = _keyRingPath,
                ["B2:BucketName"] = "cachet-archive-prod",
                ["Sync:MaxBatchSize"] = "100"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IUserRepository>();
            services.AddSingleton(UserRepository);

            services.RemoveAll<ICredentialProtector>();
            services.AddSingleton(CredentialProtector);

            services.RemoveAll<IUserB2KeyAuthorizer>();
            services.AddSingleton(B2KeyAuthorizer);

            services.RemoveAll<IAuditLogService>();
            services.AddSingleton(AuditLogService);

            services.RemoveAll<ITaskQueueRepository>();
            services.AddSingleton(TaskQueueRepository);

            services.RemoveAll<ISyncResultRepository>();
            services.AddSingleton(SyncResultRepository);
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
