using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests.Quarantine;

/// <summary>
/// <c>WebApplicationFactory&lt;Program&gt;</c> against the REAL <c>Cachet.ArchiveSync.Api/Program.cs</c>
/// (CAS-008 §6's three Quarantine endpoints), modeled directly on
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.Sync.SyncApiWebApplicationFactory</c> (same pattern:
/// swap only the DB/B2-backed registrations Program.cs makes, run every other real middleware/DI
/// wiring — cookie scheme, <c>OnValidatePrincipal</c>, <c>CachetAuthenticated</c>/
/// <c>CachetFullAccess</c> policies — unmodified). In addition to the Auth-surface fakes (needed
/// here too, since a real session must be established via <c>POST /login</c> before calling any
/// authorized endpoint), this factory also swaps <see cref="IQuarantineEntryRepository"/>,
/// <see cref="IFileIndexRepository"/>, <see cref="IB2Client"/>, and (CAS-018 §5)
/// <see cref="IUserScopedB2DeleteClient"/> — the dependencies
/// <see cref="Services.QuarantineListService"/>/<see cref="Services.QuarantinePermanentDeleteService"/>
/// resolve through. No real SQL Server or B2 endpoint is ever reached.
///
/// CAS-018: <c>Program.cs</c> registers a real <see cref="IUserScopedB2DeleteClient"/> that makes
/// live outbound HTTP calls and requires <c>B2:SystemKeyId</c>/<c>B2:SystemApplicationKey</c>
/// configuration this factory does not set — it must be swapped for a substitute here, exactly
/// like <see cref="IB2Client"/>, or any test reaching <c>POST /quarantine/delete</c>'s per-file
/// loop would fail construction/attempt a real network call.
/// </summary>
public sealed class QuarantineApiWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string CookieName = "CachetAuth";

    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
    public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
    public IQuarantineEntryRepository QuarantineEntryRepository { get; } = Substitute.For<IQuarantineEntryRepository>();
    public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
    public IB2Client B2Client { get; } = Substitute.For<IB2Client>();
    public IUserScopedB2DeleteClient UserScopedB2DeleteClient { get; } = Substitute.For<IUserScopedB2DeleteClient>();

    /// <summary>CAS-053: the quarantine browse routes resolve the bucket's fixed B2 path prefix.</summary>
    public const string B2PathPrefix = "D/Shares/Operations";

    public IB2PathPrefixProvider PrefixProvider { get; } = Substitute.For<IB2PathPrefixProvider>();

    /// <summary>CAS-062: Permanent Delete's local B2 mirror step; default "no local copy".</summary>
    public IB2MirrorFileRemover B2MirrorFileRemover { get; } = Substitute.For<IB2MirrorFileRemover>();

    /// <summary>
    /// <see cref="Services.QuarantineService"/> (CAS-006, reused unchanged for <c>POST /quarantine</c>
    /// — §0) also depends on these two repositories to resolve <c>taskId</c>/<c>fileIds</c>.
    /// </summary>
    public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
    public ISearchResultRepository SearchResultRepository { get; } = Substitute.For<ISearchResultRepository>();

    private readonly string _keyRingPath =
        Path.Combine(Path.GetTempPath(), "cachet-archivesync-tests-keyring-" + Guid.NewGuid().ToString("N"));

    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    public QuarantineApiWebApplicationFactory()
    {
        // CAS-018 §14: bucket identity does not vary by acting user — a sensible default so tests
        // that don't care about bucket resolution specifically don't each need to set this up.
        UserScopedB2DeleteClient.GetBucketIdAsync(Arg.Any<CancellationToken>()).Returns("bucket-1");
        PrefixProvider.GetPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(B2PathPrefix);
        B2MirrorFileRemover.RemoveAsync(default!, default).ReturnsForAnyArgs(B2MirrorRemoveResult.NotPresent);

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

            services.RemoveAll<IQuarantineEntryRepository>();
            services.AddSingleton(QuarantineEntryRepository);

            services.RemoveAll<IFileIndexRepository>();
            services.AddSingleton(FileIndexRepository);

            services.RemoveAll<IB2Client>();
            services.AddSingleton(B2Client);

            services.RemoveAll<IUserScopedB2DeleteClient>();
            services.AddSingleton(UserScopedB2DeleteClient);

            services.RemoveAll<ITaskQueueRepository>();
            services.AddSingleton(TaskQueueRepository);

            services.RemoveAll<ISearchResultRepository>();
            services.AddSingleton(SearchResultRepository);

            services.RemoveAll<IB2PathPrefixProvider>();
            services.AddSingleton(PrefixProvider);

            services.RemoveAll<IB2MirrorFileRemover>();
            services.AddSingleton(B2MirrorFileRemover);
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
