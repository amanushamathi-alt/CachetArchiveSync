using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests.Files;

/// <summary>
/// <c>WebApplicationFactory&lt;Program&gt;</c> against the REAL <c>Cachet.ArchiveSync.Api/Program.cs</c>
/// for CAS-040 §6's <c>/files/{fileIndexId}</c> endpoints, modeled on
/// <see cref="Reporting.ReportsApiWebApplicationFactory"/>: only the DB/B2-backed registrations are
/// swapped (auth-surface fakes so <c>POST /login</c> can mint a real session, plus
/// <see cref="IFileIndexRepository"/>, <see cref="IBucketMirrorRootRepository"/> and
/// <see cref="IAuditLogService"/>). The real <c>MirrorFileReader</c>, <c>FileContentService</c>,
/// renderer, limiter and <c>FilesController</c> run unmodified against a per-instance temp mirror
/// directory (<see cref="MirrorRoot"/>), deleted on dispose.
/// </summary>
public sealed class FilesApiWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string CookieName = "CachetAuth";
    public const string Bucket = "cachet-archive-prod";

    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
    public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
    public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
    public IBucketMirrorRootRepository BucketMirrorRootRepository { get; } = Substitute.For<IBucketMirrorRootRepository>();

    public string MirrorRoot { get; }

    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "cas040-files-api-" + Guid.NewGuid().ToString("N"));

    private readonly string _keyRingPath;

    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    public FilesApiWebApplicationFactory()
    {
        MirrorRoot = Path.Combine(_tempRoot, "mirror");
        _keyRingPath = Path.Combine(_tempRoot, "keyring");
        Directory.CreateDirectory(MirrorRoot);

        BucketMirrorRootRepository.GetRootPathAsync(Bucket, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MirrorRoot);
        AuditLogService.WriteAsync(Arg.Any<Cachet.ArchiveSync.Core.Dtos.AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1);

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
                ["B2:BucketName"] = Bucket
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

            services.RemoveAll<IFileIndexRepository>();
            services.AddSingleton(FileIndexRepository);

            services.RemoveAll<IBucketMirrorRootRepository>();
            services.AddSingleton(BucketMirrorRootRepository);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of a per-instance temp directory; never fail a test over it.
            }
        }
    }
}
