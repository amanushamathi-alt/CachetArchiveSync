using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests.Reporting;

/// <summary>
/// <c>WebApplicationFactory&lt;Program&gt;</c> against the REAL <c>Cachet.ArchiveSync.Api/Program.cs</c>
/// (CAS-010 §6's Reporting endpoints; CAS-015 §2/§5/§8/§9 — on-demand generation, no server-side
/// persistence), modeled directly on
/// <c>Cachet.ArchiveSync.Api.IntegrationTests.Quarantine.QuarantineApiWebApplicationFactory</c> (same
/// pattern: swap only the DB/B2-backed registrations Program.cs makes, run every other real
/// middleware/DI wiring — cookie scheme, <c>OnValidatePrincipal</c>, <c>CachetAuthenticated</c>
/// policy — unmodified). In addition to the Auth-surface fakes (a real session must be established
/// via <c>POST /login</c> before calling any authorized endpoint), this factory also swaps
/// <see cref="ISearchResultRepository"/>, <see cref="IQuarantineEntryRepository"/>,
/// <see cref="IAuditLogRepository"/>, and <see cref="IReportDocumentWriter"/> — the dependencies
/// <see cref="Services.PreActionReportService"/>/<see cref="Services.PostActionReportService"/>
/// resolve through. CAS-015 removed <c>IReportRepository</c> and the
/// <c>Reports:LocalStorageRootPath</c> config key entirely (§5) — this factory no longer registers
/// either. <see cref="IReportDocumentWriter"/> is faked here (not the real PdfSharp/ClosedXML
/// writers) because the "bytes are real and parseable" claim is already proven at the unit level
/// (<c>Cachet.ArchiveSync.Api.Tests.PreActionReportServiceTests</c>/<c>PostActionReportServiceTests</c>);
/// this factory exists to prove the HTTP-level route/verb/content-type/auth surface, not re-prove
/// file rendering. No real SQL Server or B2 endpoint is ever reached.
/// </summary>
public sealed class ReportsApiWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string CookieName = "CachetAuth";

    public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
    public ICredentialProtector CredentialProtector { get; } = Substitute.For<ICredentialProtector>();
    public IUserB2KeyAuthorizer B2KeyAuthorizer { get; } = Substitute.For<IUserB2KeyAuthorizer>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
    public ISearchResultRepository SearchResultRepository { get; } = Substitute.For<ISearchResultRepository>();
    public IQuarantineEntryRepository QuarantineEntryRepository { get; } = Substitute.For<IQuarantineEntryRepository>();
    public IAuditLogRepository AuditLogRepository { get; } = Substitute.For<IAuditLogRepository>();
    public IReportDocumentWriter ReportDocumentWriter { get; } = Substitute.For<IReportDocumentWriter>();

    private readonly string _keyRingPath =
        Path.Combine(Path.GetTempPath(), "cachet-archivesync-tests-keyring-" + Guid.NewGuid().ToString("N"));

    private readonly Dictionary<int, int> _sessionTokenVersions = new();

    public ReportsApiWebApplicationFactory()
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

        // CAS-015 §5/§9 V8: the writer's signature is (document, ct) -> byte[], no file path at
        // all — this factory's fake returns small, distinguishable byte payloads per format so
        // tests can assert the exact bytes streamed back to the HTTP client.
        ReportDocumentWriter.WritePdfAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // "%PDF" magic bytes, distinguishable in assertions
        ReportDocumentWriter.WriteXlsxAsync(Arg.Any<ReportDocument>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 0x50, 0x4B, 0x03, 0x04 }); // "PK\x03\x04" zip magic bytes (xlsx is a zip)
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

            services.RemoveAll<ISearchResultRepository>();
            services.AddSingleton(SearchResultRepository);

            services.RemoveAll<IQuarantineEntryRepository>();
            services.AddSingleton(QuarantineEntryRepository);

            services.RemoveAll<IAuditLogRepository>();
            services.AddSingleton(AuditLogRepository);

            services.RemoveAll<IReportDocumentWriter>();
            services.AddSingleton(ReportDocumentWriter);
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
