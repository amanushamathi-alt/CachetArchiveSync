using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB round-trip tests for <see cref="QuarantineEntryRepository"/> /
/// <c>dbo.quarantine_entry_ins</c> plus <see cref="SearchResultRepository"/> and
/// <see cref="AuditLogRepository"/> (CAS-006 §5/§13 Test Plan "QuarantineEntryRepositoryTests" —
/// SC-9, SC-10). Proves the one thing a mocked <see cref="IQuarantineEntryRepository"/> cannot:
/// that the real <c>UQ_quarantine_entry_file_index_id_active</c> filtered unique index — not just
/// the application layer's own pre-check — is what actually prevents a second <c>Active</c> row
/// for the same file (§5's own note that this constraint "is respected, not merely relied upon to
/// throw").
///
/// Skip-marked in this sandbox — same reason and setup convention as
/// <see cref="AuditLogRepositoryTests"/>/<see cref="SearchResultRepositoryTests"/>. Un-skip and run
/// manually against a real instance with the CAS-006 migration deployed.
/// </summary>
[Collection("CAS-047 file_index real-DB")]
public sealed class QuarantineEntryRepositoryTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database — not " +
        "available in this sandbox; run manually against a real instance before merging.";

    private readonly string _connectionString;
    private readonly int _seedUserId;

    public QuarantineEntryRepositoryTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;
        _seedUserId = int.TryParse(configuration["IntegrationTests:SeedUserId"], out var id) ? id : 1;
    }

    private IConfiguration BuildRepositoryConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CachetArchiveSync"] = _connectionString
            })
            .Build();

    private async Task<int> SeedTaskQueueRowAsync(SqlConnection connection)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "api-svc");
        parameters.Add("@updated_by", "api-svc");
        parameters.Add("@task_type", "KeywordSearch");
        parameters.Add("@submitted_by_user_id", _seedUserId);
        parameters.Add("@request_parameters", (string?)null);
        parameters.Add("@task_queue_id", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", parameters, commandType: System.Data.CommandType.StoredProcedure));
        return parameters.Get<int>("@task_queue_id");
    }

    private async Task<int> SeedFileIndexRowAsync(SqlConnection connection, string relativePath)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@source", "B2");
        parameters.Add("@bucket_name", "cachet-archive-prod");
        parameters.Add("@relative_path", relativePath);
        parameters.Add("@file_name", relativePath.Split('/').Last());
        parameters.Add("@file_extension", "pdf");
        parameters.Add("@file_size_bytes", 1024L);
        parameters.Add("@modified_at", DateTime.UtcNow);
        parameters.Add("@file_index_id", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.file_index_ins", parameters, commandType: System.Data.CommandType.StoredProcedure));
        return parameters.Get<int>("@file_index_id");
    }

    private async Task<int> SeedSearchResultRowAsync(SqlConnection connection, int taskQueueId, int fileIndexId)
    {
        var repository = new SearchResultRepository(BuildRepositoryConfiguration());
        return await repository.InsertAsync("worker-svc", "worker-svc", taskQueueId, fileIndexId, "Filename", null, "Flagged", CancellationToken.None);
    }

    // ---- SC-9: valid add inserts an Active row + a QuarantineAdd audit row before the response ----

    [Fact(Skip = SkipReason)]
    public async Task Test_SC9_QuarantineAddInsertsAndAuditsBeforeResponse()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var fileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/sc9-{Guid.NewGuid():N}.pdf");
        var searchResultId = await SeedSearchResultRowAsync(connection, taskQueueId, fileIndexId);
        var repository = new QuarantineEntryRepository(BuildRepositoryConfiguration());
        var auditRepository = new AuditLogRepository(BuildRepositoryConfiguration());

        // Act — mirrors QuarantineService.AddAsync's real sequence: insert, then audit.
        var insertResult = await repository.InsertAsync("api-svc", "api-svc", searchResultId, CancellationToken.None);
        var auditLogId = await auditRepository.InsertAsync(new AuditLogEntry
        {
            CreatedBy = "api-svc",
            UpdatedBy = "api-svc",
            UserId = _seedUserId,
            TabModule = "Keyword Search",
            Action = AuditAction.QuarantineAdd,
            FilePath = $"contracts/sc9-{Guid.NewGuid():N}.pdf",
            TaskQueueId = taskQueueId,
            Result = AuditResult.Success
        }, CancellationToken.None);

        // Assert — an Active dbo.quarantine_entry row exists, and a matching audit row exists.
        insertResult.Outcome.Should().Be(QuarantineInsertOutcome.Added);
        insertResult.QuarantineEntryId.Should().NotBeNull();

        var row = await connection.QuerySingleAsync<dynamic>(
            "SELECT status, file_index_id FROM dbo.quarantine_entry WHERE quarantine_entry_id = @Id",
            new { Id = insertResult.QuarantineEntryId });
        ((string)row.status).Should().Be("Active");
        ((int)row.file_index_id).Should().Be(fileIndexId);

        auditLogId.Should().BeGreaterThan(0);
    }

    // ---- SC-10: the filtered unique index — not just the app layer — rejects a second Active row --

    [Fact(Skip = SkipReason)]
    public async Task Test_SC10_DuplicateQuarantineRejectedNotDoubleInserted()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var fileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/sc10-{Guid.NewGuid():N}.pdf");
        var firstSearchResultId = await SeedSearchResultRowAsync(connection, taskQueueId, fileIndexId);
        var repository = new QuarantineEntryRepository(BuildRepositoryConfiguration());

        var firstInsert = await repository.InsertAsync("api-svc", "api-svc", firstSearchResultId, CancellationToken.None);
        firstInsert.Outcome.Should().Be(QuarantineInsertOutcome.Added);

        // A second search_result row for the SAME file_index_id (a different task's match on the
        // same physical file) — the real scenario the filtered unique index on file_index_id guards.
        var secondTaskQueueId = await SeedTaskQueueRowAsync(connection);
        // dbo.search_result's own unique key is (task_queue_id, file_index_id, match_type), so a
        // second task can validly produce a second search_result row for the same file.
        var secondSearchResultRepository = new SearchResultRepository(BuildRepositoryConfiguration());
        var secondSearchResultId = await secondSearchResultRepository.InsertAsync(
            "worker-svc", "worker-svc", secondTaskQueueId, fileIndexId, "Filename", null, "Flagged", CancellationToken.None);

        // Act
        var secondInsert = await repository.InsertAsync("api-svc", "api-svc", secondSearchResultId, CancellationToken.None);

        // Assert — rejected by the constraint (mapped to AlreadyActive, §7 V8), not double-inserted.
        secondInsert.Outcome.Should().Be(QuarantineInsertOutcome.AlreadyActive);

        var activeRowCount = await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.quarantine_entry WHERE file_index_id = @FileIndexId AND status = 'Active'",
            new { FileIndexId = fileIndexId });
        activeRowCount.Should().Be(1);
    }

    // ---- CAS-008: ListActiveAsync / GetActiveForDeleteAsync / TryTransitionToDeletedAsync ----------
    // Real-DB round-trips for the three repository methods CAS-008 adds, backing GET /quarantine
    // (§13 Test Plan "QuarantineEntryRepositoryTests" — supports SC-1/V-7) and the per-file
    // resolution + status-transition steps of POST /quarantine/delete (supports SC-3/SC-8). Skipped
    // in this sandbox for the same reason as SC-9/SC-10 above.

    [Fact(Skip = SkipReason)]
    public async Task Test_ListActiveAsync_ReturnsOnlyActiveRowsCorrectlyShaped()
    {
        // Arrange — one Active entry and one Deleted entry for the same file, via db-author's
        // dbo.quarantine_entry_delete_upd, to prove the Deleted row is excluded (§7 V7).
        await using var connection = new SqlConnection(_connectionString);
        var repository = new QuarantineEntryRepository(BuildRepositoryConfiguration());
        var activeTaskQueueId = await SeedTaskQueueRowAsync(connection);
        var activeFileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/list-active-{Guid.NewGuid():N}.pdf");
        var activeSearchResultId = await SeedSearchResultRowAsync(connection, activeTaskQueueId, activeFileIndexId);
        var activeInsert = await repository.InsertAsync("api-svc", "api-svc", activeSearchResultId, CancellationToken.None);

        var deletedTaskQueueId = await SeedTaskQueueRowAsync(connection);
        var deletedFileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/list-deleted-{Guid.NewGuid():N}.pdf");
        var deletedSearchResultId = await SeedSearchResultRowAsync(connection, deletedTaskQueueId, deletedFileIndexId);
        var deletedInsert = await repository.InsertAsync("api-svc", "api-svc", deletedSearchResultId, CancellationToken.None);
        await repository.TryTransitionToDeletedAsync(deletedInsert.QuarantineEntryId!.Value, "api-svc", CancellationToken.None);

        // Act
        var rows = await repository.ListActiveAsync(CancellationToken.None);

        // Assert
        rows.Should().Contain(r => r.QuarantineEntryId == activeInsert.QuarantineEntryId);
        rows.Should().NotContain(r => r.QuarantineEntryId == deletedInsert.QuarantineEntryId);
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_GetActiveForDeleteAsync_UnknownOrNonActiveIdReturnsNull()
    {
        // Arrange — an id that does not exist at all, and a real id already transitioned to Deleted.
        await using var connection = new SqlConnection(_connectionString);
        var repository = new QuarantineEntryRepository(BuildRepositoryConfiguration());
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var fileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/get-for-delete-{Guid.NewGuid():N}.pdf");
        var searchResultId = await SeedSearchResultRowAsync(connection, taskQueueId, fileIndexId);
        var insertResult = await repository.InsertAsync("api-svc", "api-svc", searchResultId, CancellationToken.None);
        await repository.TryTransitionToDeletedAsync(insertResult.QuarantineEntryId!.Value, "api-svc", CancellationToken.None);

        // Act
        var unknownRow = await repository.GetActiveForDeleteAsync(999999999, CancellationToken.None);
        var nonActiveRow = await repository.GetActiveForDeleteAsync(insertResult.QuarantineEntryId!.Value, CancellationToken.None);

        // Assert — both map to the same per-file file-not-found outcome at the service layer (§7 V3).
        unknownRow.Should().BeNull();
        nonActiveRow.Should().BeNull();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_TryTransitionToDeletedAsync_ActiveRowTransitionsOnceThenReturnsFalse()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var repository = new QuarantineEntryRepository(BuildRepositoryConfiguration());
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var fileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/transition-{Guid.NewGuid():N}.pdf");
        var searchResultId = await SeedSearchResultRowAsync(connection, taskQueueId, fileIndexId);
        var insertResult = await repository.InsertAsync("api-svc", "api-svc", searchResultId, CancellationToken.None);
        var quarantineEntryId = insertResult.QuarantineEntryId!.Value;

        // Act
        var firstTransition = await repository.TryTransitionToDeletedAsync(quarantineEntryId, "api-svc", CancellationToken.None);
        var secondTransition = await repository.TryTransitionToDeletedAsync(quarantineEntryId, "api-svc", CancellationToken.None);

        // Assert — the row transitions exactly once; a repeat call (e.g. SC-8's already-Deleted
        // scenario) reports false, never a second success.
        firstTransition.Should().BeTrue();
        secondTransition.Should().BeFalse();

        var row = await connection.QuerySingleAsync<dynamic>(
            "SELECT status FROM dbo.quarantine_entry WHERE quarantine_entry_id = @Id",
            new { Id = quarantineEntryId });
        ((string)row.status).Should().Be("Deleted");
    }

    // ---- CAS-014: HardDeleteActiveAsync — a genuine physical DELETE, not a status transition -------
    // Real-DB round-trips for the new hard-delete repository method (§13 Test Plan
    // "QuarantineEntryRepositoryTests" — V3, SC-1, SC-2, SC-5). Skipped in this sandbox for the same
    // reason as CAS-006/CAS-008's existing Skip-marked methods above.

    [Fact(Skip = SkipReason)]
    public async Task Test_HardDeleteActiveAsync_ActiveRowIsPhysicallyRemoved()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var repository = new QuarantineEntryRepository(BuildRepositoryConfiguration());
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var fileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/hard-delete-{Guid.NewGuid():N}.pdf");
        var searchResultId = await SeedSearchResultRowAsync(connection, taskQueueId, fileIndexId);
        var insertResult = await repository.InsertAsync("api-svc", "api-svc", searchResultId, CancellationToken.None);
        var quarantineEntryId = insertResult.QuarantineEntryId!.Value;

        // Act
        var removed = await repository.HardDeleteActiveAsync(quarantineEntryId, CancellationToken.None);

        // Assert — the row is gone entirely (row count 0), not merely status = 'Deleted'/'Released'
        // (§9 V3, §10 SC-1) — this is what distinguishes Remove Item from Permanent Delete's
        // TryTransitionToDeletedAsync (which only flips status).
        removed.Should().BeTrue();
        var rowCount = await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.quarantine_entry WHERE quarantine_entry_id = @Id",
            new { Id = quarantineEntryId });
        rowCount.Should().Be(0);
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_HardDeleteActiveAsync_AlreadyRemovedRowReturnsFalse()
    {
        // Arrange — the row is hard-deleted once, then a repeat call targets the same, now-nonexistent id.
        await using var connection = new SqlConnection(_connectionString);
        var repository = new QuarantineEntryRepository(BuildRepositoryConfiguration());
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var fileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/hard-delete-repeat-{Guid.NewGuid():N}.pdf");
        var searchResultId = await SeedSearchResultRowAsync(connection, taskQueueId, fileIndexId);
        var insertResult = await repository.InsertAsync("api-svc", "api-svc", searchResultId, CancellationToken.None);
        var quarantineEntryId = insertResult.QuarantineEntryId!.Value;

        // Act
        var firstRemove = await repository.HardDeleteActiveAsync(quarantineEntryId, CancellationToken.None);
        var secondRemove = await repository.HardDeleteActiveAsync(quarantineEntryId, CancellationToken.None);

        // Assert — the row is removed exactly once; a repeat call reports false, never throws, and
        // never reports a second success (§10 SC-2).
        firstRemove.Should().BeTrue();
        secondRemove.Should().BeFalse();
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_RemovedFileCanBeReQuarantined_FilteredUniqueIndexNoLongerBlocks()
    {
        // Arrange — quarantine a file, hard-delete the catalog row via Remove Item, then attempt a
        // fresh add for the same file_index_id from a new search_result row.
        await using var connection = new SqlConnection(_connectionString);
        var repository = new QuarantineEntryRepository(BuildRepositoryConfiguration());
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var fileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/re-quarantine-{Guid.NewGuid():N}.pdf");
        var firstSearchResultId = await SeedSearchResultRowAsync(connection, taskQueueId, fileIndexId);
        var firstInsert = await repository.InsertAsync("api-svc", "api-svc", firstSearchResultId, CancellationToken.None);
        firstInsert.Outcome.Should().Be(QuarantineInsertOutcome.Added);

        var removed = await repository.HardDeleteActiveAsync(firstInsert.QuarantineEntryId!.Value, CancellationToken.None);
        removed.Should().BeTrue();

        // A second search_result row for the same file_index_id, as a later search hit would produce.
        var secondTaskQueueId = await SeedTaskQueueRowAsync(connection);
        var secondSearchResultId = await SeedSearchResultRowAsync(connection, secondTaskQueueId, fileIndexId);

        // Act
        var secondInsert = await repository.InsertAsync("api-svc", "api-svc", secondSearchResultId, CancellationToken.None);

        // Assert — the filtered unique index on file_index_id no longer sees an Active row for this
        // file, so the re-add succeeds instead of failing with AlreadyActive (§10 SC-5).
        secondInsert.Outcome.Should().Be(QuarantineInsertOutcome.Added);
        var activeRowCount = await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.quarantine_entry WHERE file_index_id = @FileIndexId AND status = 'Active'",
            new { FileIndexId = fileIndexId });
        activeRowCount.Should().Be(1);
    }

    // ---- CAS-040 §5/§6.5: dbo.quarantine_entry_lst projects fi.file_index_id ----------------------

    [Fact(Skip = SkipReason)]
    public async Task Test_QuarantineList_IncludesFileIndexId()
    {
        // Arrange — requires the CAS-040 migration (20261001_090000__CAS-040-file-viewer-download.sql).
        await using var connection = new SqlConnection(_connectionString);
        var repository = new QuarantineEntryRepository(BuildRepositoryConfiguration());
        var taskQueueId = await SeedTaskQueueRowAsync(connection);
        var fileIndexId = await SeedFileIndexRowAsync(connection, $"contracts/cas040-list-{Guid.NewGuid():N}.pdf");
        var searchResultId = await SeedSearchResultRowAsync(connection, taskQueueId, fileIndexId);
        var insert = await repository.InsertAsync("api-svc", "api-svc", searchResultId, CancellationToken.None);

        // Act
        var rows = await repository.ListActiveAsync(CancellationToken.None);

        // Assert — the row carries the file identity (file_index_id), distinct from the entry id.
        var row = rows.Should().ContainSingle(r => r.QuarantineEntryId == insert.QuarantineEntryId).Subject;
        row.FileIndexId.Should().Be(fileIndexId);
    }

    public void Dispose()
    {
        // Real run: clean up any inserted quarantine_entry/search_result/task_queue/file_index/audit_log test rows here.
    }
}
