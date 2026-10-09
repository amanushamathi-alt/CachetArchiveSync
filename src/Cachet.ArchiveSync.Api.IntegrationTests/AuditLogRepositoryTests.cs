using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB round-trip tests for <see cref="AuditLogRepository"/> / <c>dbo.audit_log_ins</c>
/// (CAS-005 §13 Test Plan). Proves SC-1 and SC-2, which cannot be proven with a mocked
/// repository since they assert what the *database* actually stores.
///
/// Skip-marked in this sandbox: no live SQL Server instance is reachable here, and this repo has
/// no existing Testcontainers/LocalDb fixture convention to fall back to. The test bodies are
/// written as if they would run against a real `CachetArchiveSync` database — connection string
/// read from the `ConnectionStrings:CachetArchiveSync` configuration key (environment variable
/// `ConnectionStrings__CachetArchiveSync` or a local `appsettings.IntegrationTests.json`, neither
/// of which exists yet in this sandbox). Un-skip and run manually against a real instance with
/// `dbo.audit_log_ins` deployed (Database/migrations/20260922_150244__CAS-005-audit-log-write-
/// service.sql) before merging.
/// </summary>
public sealed class AuditLogRepositoryTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database — not " +
        "available in this sandbox; run manually against a real instance before merging.";

    private readonly string _connectionString;
    private readonly int _seedUserId;

    public AuditLogRepositoryTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;

        // A pre-seeded test user id — dbo.audit_log.user_id is FK'd to dbo.users (CAS-003).
        // Real run: seed (or reuse a known fixture) user before executing these tests.
        _seedUserId = int.TryParse(configuration["IntegrationTests:SeedUserId"], out var id) ? id : 1;
    }

    private IConfiguration BuildRepositoryConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CachetArchiveSync"] = _connectionString
            })
            .Build();

    [Fact(Skip = SkipReason)]
    public async Task Test_SC1_WriteAsyncInsertsOneRow()
    {
        // Arrange
        var repository = new AuditLogRepository(BuildRepositoryConfiguration());
        var entry = new AuditLogEntry
        {
            CreatedBy = "api-svc",
            UpdatedBy = "api-svc",
            UserId = _seedUserId,
            TabModule = "Auth",
            Action = AuditAction.Login,
            FilePath = null,
            TaskQueueId = null,
            Result = AuditResult.Success,
            ResultDetail = "integration test row"
        };

        // Act
        var auditLogId = await repository.InsertAsync(entry, CancellationToken.None);

        // Assert — exactly one row, all passed fields populated as passed
        await using var connection = new SqlConnection(_connectionString);
        var rows = (await connection.QueryAsync<dynamic>(
            "SELECT * FROM dbo.audit_log WHERE audit_log_id = @Id",
            new { Id = auditLogId })).ToList();

        rows.Should().HaveCount(1);
        var row = rows[0];
        ((string)row.created_by).Should().Be(entry.CreatedBy);
        ((string)row.updated_by).Should().Be(entry.UpdatedBy);
        ((int)row.user_id).Should().Be(entry.UserId);
        ((string)row.tab_module).Should().Be(entry.TabModule);
        ((string)row.action).Should().Be(entry.Action.ToString());
        ((string)row.result).Should().Be(entry.Result.ToString());
        ((string)row.result_detail).Should().Be(entry.ResultDetail);
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_SC2_DbGeneratedFieldsNotCallerSupplied()
    {
        // Arrange
        var repository = new AuditLogRepository(BuildRepositoryConfiguration());
        var beforeInsertUtc = DateTime.UtcNow;
        var entry = new AuditLogEntry
        {
            CreatedBy = "worker-svc",
            UpdatedBy = "worker-svc",
            UserId = _seedUserId,
            TabModule = "O-Drive Sync",
            Action = AuditAction.Deleted,
            FilePath = @"O:\archive\sample.pdf",
            TaskQueueId = null,
            Result = AuditResult.Success,
            ResultDetail = null
        };

        // Act — note: AuditLogEntry has no audit_log_id/created_at/updated_at settable members at
        // all (SC-2's first assertion — this is a compile-time guarantee, confirmed here by the
        // absence of those properties on the DTO, not a runtime check).
        var auditLogId = await repository.InsertAsync(entry, CancellationToken.None);

        // Assert — the database generated audit_log_id (identity) and created_at/updated_at
        // (SYSUTCDATETIME() defaults), never supplied by the caller.
        auditLogId.Should().BeGreaterThan(0);

        await using var connection = new SqlConnection(_connectionString);
        var row = await connection.QuerySingleAsync<dynamic>(
            "SELECT created_at, updated_at FROM dbo.audit_log WHERE audit_log_id = @Id",
            new { Id = auditLogId });

        ((DateTime)row.created_at).Should().BeOnOrAfter(beforeInsertUtc);
        ((DateTime)row.updated_at).Should().BeOnOrAfter(beforeInsertUtc);
    }

    public void Dispose()
    {
        // Real run: clean up any inserted rows here (dbo.audit_log is append-only by design —
        // CAS-003 — so test cleanup, not application code, is responsible for removing test rows).
    }
}
