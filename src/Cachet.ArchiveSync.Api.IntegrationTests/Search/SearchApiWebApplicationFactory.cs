using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests.Search;

/// <summary>
/// <c>WebApplicationFactory&lt;Program&gt;</c> against the REAL <c>Cachet.ArchiveSync.Api/Program.cs</c>
/// (CAS-006 §6's three Keyword Search / Quarantine-add endpoints), modeled directly on
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.Auth.RealProgramWebApplicationFactory</c> (same
/// pattern: swap only the DB/B2-backed registrations Program.cs makes, run every other real
/// middleware/DI wiring — cookie scheme, <c>OnValidatePrincipal</c>, <c>CachetAuthenticated</c>
/// policy — unmodified). In addition to that class's Auth-surface fakes (needed here too, since a
/// real session must be established via <c>POST /login</c> before calling any
/// <c>[Authorize(Policy = "CachetAuthenticated")]</c> endpoint), this factory also swaps
/// <see cref="ITaskQueueRepository"/>, <see cref="ISearchResultRepository"/>, and
/// <see cref="IQuarantineEntryRepository"/> — the three CAS-006 repositories
/// <see cref="Services.KeywordSearchService"/>/<see cref="Services.QuarantineService"/> depend on.
/// No real SQL Server or B2 endpoint is ever reached.
/// </summary>
public sealed class SearchApiWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string CookieName = "CachetAuth";

    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
    public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
    public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
    public ISearchResultRepository SearchResultRepository { get; } = Substitute.For<ISearchResultRepository>();
    public IQuarantineEntryRepository QuarantineEntryRepository { get; } = Substitute.For<IQuarantineEntryRepository>();

    private readonly string _keyRingPath =
        Path.Combine(Path.GetTempPath(), "cachet-archivesync-tests-keyring-" + Guid.NewGuid().ToString("N"));

    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    public SearchApiWebApplicationFactory()
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
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:DataProtection:KeyRingPath"] = _keyRingPath,
                // CAS-006 §7 V2: the configured bucket every SearchControllerTests request uses.
                ["B2:BucketName"] = "cachet-archive-prod",
                ["Search:MaxTermLength"] = "200"
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

            services.RemoveAll<ISearchResultRepository>();
            services.AddSingleton(SearchResultRepository);

            services.RemoveAll<IQuarantineEntryRepository>();
            services.AddSingleton(QuarantineEntryRepository);
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
