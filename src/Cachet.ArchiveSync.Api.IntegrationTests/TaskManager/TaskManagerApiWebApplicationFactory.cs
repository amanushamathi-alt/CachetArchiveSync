using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests.TaskManager;

/// <summary>
/// <c>WebApplicationFactory&lt;Program&gt;</c> against the REAL <c>Cachet.ArchiveSync.Api/Program.cs</c>
/// (CAS-016 §8's <c>POST /tasks/{taskId}/restart</c>), modeled directly on
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.Quarantine.QuarantineApiWebApplicationFactory</c> /
/// <c>AuditLog.AuditLogApiWebApplicationFactory</c> (same pattern: swap only the DB-backed
/// registrations Program.cs makes, run every other real middleware/DI wiring — cookie scheme,
/// <c>OnValidatePrincipal</c>, <c>CachetAuthenticated</c> policy — unmodified). A real session must
/// be established via <c>POST /login</c> before calling the restart endpoint, so the Auth-surface
/// fakes are swapped too, alongside <see cref="ITaskQueueRepository"/> and
/// <see cref="IAuditLogService"/> — the two dependencies <see cref="Services.TaskManagerService"/>
/// resolves through. No real SQL Server or B2 endpoint is ever reached.
/// </summary>
public sealed class TaskManagerApiWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string CookieName = "CachetAuth";

    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
    public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
    public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();

    private readonly string _keyRingPath =
        Path.Combine(Path.GetTempPath(), "cachet-archivesync-tests-keyring-" + Guid.NewGuid().ToString("N"));

    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    // CAS-030 §13: when set, the REAL TaskQueueRepository (Dapper -> LocalDB stored procedures) is
    // kept instead of the ITaskQueueRepository fake, so the own-scoped task endpoints run against
    // the real dbo.task_queue_lst / _summary_get / _acknowledge_upd / _get procedures.
    private readonly string? _realDbConnectionString;

    public TaskManagerApiWebApplicationFactory()
        : this(realDbConnectionString: null)
    {
    }

    public TaskManagerApiWebApplicationFactory(string? realDbConnectionString)
    {
        _realDbConnectionString = realDbConnectionString;

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
                ["B2:BucketName"] = "cachet-archive-prod"
            });

            if (_realDbConnectionString is not null)
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:CachetArchiveSync"] = _realDbConnectionString
                });
            }
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

            if (_realDbConnectionString is null)
            {
                services.RemoveAll<ITaskQueueRepository>();
                services.AddSingleton(TaskQueueRepository);
            }
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
