using System.Data;
using System.Text.RegularExpressions;
using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Core.Dtos;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB tests for the CAS-029 migration, <c>dbo.task_queue_progress_upd</c> (via
/// <see cref="TaskQueueRepository.UpdateProgressAsync"/>) and the amended <c>dbo.task_queue_get</c>
/// projection (via <see cref="TaskQueueRepository.GetAsync"/>) — CAS-029 §5, §13
/// "TaskQueueProgressTests": SC-1, SC-2, SC-11, V-2, V-3, V-4, V-5. Same connection-string /
/// seed-user / Skip convention as <see cref="TaskQueueSweepTests"/> and
/// <see cref="TaskQueueClaimTests"/>; rows are seeded via <c>dbo.task_queue_ins</c> and then
/// adjusted directly for setup. Joins the CAS-028 non-parallel collection because the startup
/// sweep fails every <c>Running</c> row, which these tests rely on.
///
/// Skip-marked in this sandbox — same reason as every other CAS-* real-DB suite in this project. Un-skip
/// and run manually against a real instance with the full migration chain (through CAS-029) deployed.
/// </summary>
[Collection(TaskQueueRealDbCollection.Name)]
public sealed class TaskQueueProgressTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-029 migration deployed — not available in this sandbox; run manually against a real " +
        "instance before merging.";

    private const string MigrationFileName = "20260924_120000__CAS-029-long-running-job-execution-progress.sql";
    private const string WorkerIdentity = "worker-svc";

    private readonly string _connectionString;
    private readonly int _seedUserId;
    private readonly List<int> _createdTaskIds = new();

    public TaskQueueProgressTests()
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

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Database", "migrations")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root (folder containing Database/migrations) not found.");
    }

    /// <summary>Inserts a Queued row (dbo.task_queue_ins), then directly moves it to <paramref name="status"/> for setup.</summary>
    private async Task<int> InsertTaskAsync(SqlConnection connection, string status)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@task_type", "KeywordSearch");
        parameters.Add("@submitted_by_user_id", _seedUserId);
        parameters.Add("@request_parameters", (string?)null);
        parameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", parameters, commandType: CommandType.StoredProcedure));
        var taskQueueId = parameters.Get<int>("@task_queue_id");
        _createdTaskIds.Add(taskQueueId);

        if (status == "Running")
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.task_queue SET status = N'Running', started_at = SYSUTCDATETIME() WHERE task_queue_id = @id",
                new { id = taskQueueId });
        }
        else if (status != "Queued")
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.task_queue SET status = @status, completed_at = SYSUTCDATETIME() WHERE task_queue_id = @id",
                new { status, id = taskQueueId });
        }

        return taskQueueId;
    }

    private sealed record ProgressSnapshot(
        string Status, int? ItemsProcessed, int? ItemsTotal, DateTime? ProgressUpdatedAt,
        DateTime UpdatedAt, DateTime? CompletedAt, string? ResultDetail);

    private static async Task<ProgressSnapshot> ReadSnapshotAsync(SqlConnection connection, int taskQueueId) =>
        await connection.QuerySingleAsync<ProgressSnapshot>(
            "SELECT status AS Status, items_processed AS ItemsProcessed, items_total AS ItemsTotal, " +
            "progress_updated_at AS ProgressUpdatedAt, updated_at AS UpdatedAt, completed_at AS CompletedAt, " +
            "result_detail AS ResultDetail FROM dbo.task_queue WHERE task_queue_id = @id",
            new { id = taskQueueId });

    // ---- SC-1: migration applies cleanly; columns + constraint + proc exist; GetAsync round trip ---

    [Fact(Skip = SkipReason)]
    public async Task Test_SC1_Migration_ProgressColumnsAndProc_RoundTripViaGetAsync()
    {
        // Arrange — the migration file, re-targeted at the connection's database, split on GO.
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        var script = (await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "Database", "migrations", MigrationFileName)))
            .Replace("USE [CachetArchiveSync];", $"USE [{connection.Database}];");
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(batch => !string.IsNullOrWhiteSpace(batch))
            .ToList();
        batches.Should().NotBeEmpty();

        // Act 1 — re-apply on top of the deployed chain (idempotent: COL_LENGTH / NOT EXISTS / CREATE OR ALTER).
        var applyTwice = async () =>
        {
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var batch in batches)
                {
                    await connection.ExecuteAsync(batch);
                }
            }
        };

        // Assert 1 — schema objects.
        await applyTwice.Should().NotThrowAsync();
        var columns = (await connection.QueryAsync<(string name, string type, bool nullable)>(
                "SELECT c.name, t.name, c.is_nullable FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
                "WHERE c.object_id = OBJECT_ID(N'dbo.task_queue') AND c.name IN (N'items_processed', N'items_total', N'progress_updated_at')"))
            .ToList();
        columns.Should().BeEquivalentTo(new[]
        {
            ("items_processed", "int", true),
            ("items_total", "int", true),
            ("progress_updated_at", "datetime2", true)
        });
        (await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'CK_task_queue_progress' AND parent_object_id = OBJECT_ID(N'dbo.task_queue')"))
            .Should().Be(1);
        (await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.procedures WHERE SCHEMA_NAME(schema_id) = N'dbo' AND name = N'task_queue_progress_upd'"))
            .Should().Be(1);

        // Act 2 / Assert 2 — a fresh row maps NULL progress through GetAsync.
        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());
        var runningId = await InsertTaskAsync(connection, "Running");
        var fresh = await repository.GetAsync(runningId, CancellationToken.None);
        fresh.Should().NotBeNull();
        fresh!.ItemsProcessed.Should().BeNull();
        fresh.ItemsTotal.Should().BeNull();
        fresh.ProgressUpdatedAt.Should().BeNull();

        // A progress write is mapped back by GetAsync.
        (await repository.UpdateProgressAsync(runningId, WorkerIdentity, 5, 10, CancellationToken.None)).Should().BeTrue();
        var written = await repository.GetAsync(runningId, CancellationToken.None);
        written!.ItemsProcessed.Should().Be(5);
        written.ItemsTotal.Should().Be(10);
        written.ProgressUpdatedAt.Should().NotBeNull();
        written.ProgressUpdatedAt!.Value.Offset.Should().Be(TimeSpan.Zero);

        // Act 3 / Assert 3 — a CAS-016 restart row starts with NULL progress; the original keeps its values.
        await repository.UpdateStatusAsync(runningId, WorkerIdentity, "Failed", "{\"FailureReason\":\"test\"}", CancellationToken.None);
        var restart = await repository.RestartAsync(runningId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None);
        restart.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        _createdTaskIds.Add(restart.NewTaskQueueId!.Value);

        var restarted = await repository.GetAsync(restart.NewTaskQueueId.Value, CancellationToken.None);
        restarted!.ItemsProcessed.Should().BeNull();
        restarted.ItemsTotal.Should().BeNull();
        restarted.ProgressUpdatedAt.Should().BeNull();
        var original = await repository.GetAsync(runningId, CancellationToken.None);
        original!.ItemsProcessed.Should().Be(5);
        original.ItemsTotal.Should().Be(10);
    }

    // ---- SC-2: terminal writes via task_queue_upd leave progress unchanged ---------------------------

    [Theory(Skip = SkipReason)]
    [InlineData("Completed", "{\"FilenameMatchCount\":7,\"ContentMatchCount\":0,\"ExcludedCount\":0}")]
    [InlineData("Failed", "{\"FailureReason\":\"Unhandled error during Keyword Search processing.\"}")]
    public async Task Test_SC2_TaskQueueUpd_TerminalStatus_RetainsProgress(string terminalStatus, string resultDetail)
    {
        // Arrange — a Running row with progress (7, 10).
        await using var connection = new SqlConnection(_connectionString);
        var id = await InsertTaskAsync(connection, "Running");
        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());
        (await repository.UpdateProgressAsync(id, WorkerIdentity, 7, 10, CancellationToken.None)).Should().BeTrue();
        var before = await ReadSnapshotAsync(connection, id);

        // Act
        await repository.UpdateStatusAsync(id, WorkerIdentity, terminalStatus, resultDetail, CancellationToken.None);

        // Assert — status changed; progress values retained, not cleared.
        var after = await ReadSnapshotAsync(connection, id);
        after.Status.Should().Be(terminalStatus);
        after.ItemsProcessed.Should().Be(7);
        after.ItemsTotal.Should().Be(10);
        after.ProgressUpdatedAt.Should().Be(before.ProgressUpdatedAt);
    }

    // ---- SC-11: guarded and monotonic end to end ------------------------------------------------------

    [Fact(Skip = SkipReason)]
    public async Task Test_SC11_ProgressProc_GuardedAndMonotonic()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var id = await InsertTaskAsync(connection, "Running");
        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act / Assert — (5, 10) accepted.
        (await repository.UpdateProgressAsync(id, WorkerIdentity, 5, 10, CancellationToken.None)).Should().BeTrue();
        var afterFirst = await ReadSnapshotAsync(connection, id);
        afterFirst.ItemsProcessed.Should().Be(5);
        afterFirst.ItemsTotal.Should().Be(10);

        // (3, 10) is ignored for items_processed (never regresses).
        await repository.UpdateProgressAsync(id, WorkerIdentity, 3, 10, CancellationToken.None);
        (await ReadSnapshotAsync(connection, id)).ItemsProcessed.Should().Be(5);

        // After Completed, a further write returns 0 rows affected and changes nothing.
        await repository.UpdateStatusAsync(id, WorkerIdentity, "Completed", "{}", CancellationToken.None);
        var completed = await ReadSnapshotAsync(connection, id);
        (await repository.UpdateProgressAsync(id, WorkerIdentity, 8, 10, CancellationToken.None)).Should().BeFalse();
        (await ReadSnapshotAsync(connection, id)).Should().Be(completed);
    }

    // ---- V-2: invalid parameters → RAISERROR severity 16, no row changed ------------------------------

    [Theory(Skip = SkipReason)]
    [InlineData(false, WorkerIdentity, 1, 10, "@task_queue_id is required.")]
    [InlineData(true, null, 1, 10, "@updated_by is required.")]
    [InlineData(true, "", 1, 10, "@updated_by is required.")]
    [InlineData(true, "   ", 1, 10, "@updated_by is required.")]
    [InlineData(true, WorkerIdentity, null, 10, "@items_processed is required and must be >= 0.")]
    [InlineData(true, WorkerIdentity, -1, 10, "@items_processed is required and must be >= 0.")]
    [InlineData(true, WorkerIdentity, 1, null, "@items_total is required and must be >= 0.")]
    [InlineData(true, WorkerIdentity, 0, -1, "@items_total is required and must be >= 0.")]
    [InlineData(true, WorkerIdentity, 11, 10, "@items_processed must not exceed @items_total.")]
    public async Task Test_V2_ProgressProc_InvalidParameters_Raises(
        bool passTaskId, string? updatedBy, int? itemsProcessed, int? itemsTotal, string expectedMessage)
    {
        // Arrange — a Running row that a valid write would change.
        await using var connection = new SqlConnection(_connectionString);
        var id = await InsertTaskAsync(connection, "Running");
        var before = await ReadSnapshotAsync(connection, id);

        var parameters = new DynamicParameters();
        parameters.Add("@task_queue_id", passTaskId ? id : null, DbType.Int32);
        parameters.Add("@updated_by", updatedBy, DbType.String);
        parameters.Add("@items_processed", itemsProcessed, DbType.Int32);
        parameters.Add("@items_total", itemsTotal, DbType.Int32);

        // Act — called directly: the repository's int parameters cannot express NULL.
        var act = async () => await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "dbo.task_queue_progress_upd", parameters, commandType: CommandType.StoredProcedure));

        // Assert
        var thrown = (await act.Should().ThrowAsync<SqlException>()).Which;
        thrown.Class.Should().Be(16);
        thrown.Message.Should().Contain(expectedMessage);
        (await ReadSnapshotAsync(connection, id)).Should().Be(before);
    }

    // ---- V-3: only a Running row is updated -----------------------------------------------------------

    [Theory(Skip = SkipReason)]
    [InlineData("Queued")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task Test_V3_ProgressProc_NonRunningRow_NoChange(string status)
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var id = await InsertTaskAsync(connection, status);
        var before = await ReadSnapshotAsync(connection, id);
        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var updated = await repository.UpdateProgressAsync(id, WorkerIdentity, 1, 2, CancellationToken.None);

        // Assert — rows_affected = 0 and the row is untouched.
        updated.Should().BeFalse();
        (await ReadSnapshotAsync(connection, id)).Should().Be(before);
    }

    // ---- V-4: items_processed never decreases; total and timestamp still refresh ----------------------

    [Fact(Skip = SkipReason)]
    public async Task Test_V4_ProgressProc_LowerValue_DoesNotRegress()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var id = await InsertTaskAsync(connection, "Running");
        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());
        (await repository.UpdateProgressAsync(id, WorkerIdentity, 6, 10, CancellationToken.None)).Should().BeTrue();
        var first = await ReadSnapshotAsync(connection, id);
        await Task.Delay(50); // DATETIME2(3): make a refreshed timestamp observable

        // Act — a lower processed value (e.g. an out-of-order concurrent write).
        var updated = await repository.UpdateProgressAsync(id, WorkerIdentity, 2, 12, CancellationToken.None);

        // Assert
        updated.Should().BeTrue();
        var second = await ReadSnapshotAsync(connection, id);
        second.ItemsProcessed.Should().Be(6, "items_processed never decreases");
        second.ItemsTotal.Should().Be(12, "items_total still refreshes");
        second.ProgressUpdatedAt.Should().BeAfter(first.ProgressUpdatedAt!.Value, "progress_updated_at still refreshes");
    }

    // ---- V-5: CK_task_queue_progress holds at the table level -----------------------------------------

    [Theory(Skip = SkipReason)]
    [InlineData(11, 10)]
    [InlineData(-1, 10)]
    [InlineData(0, -1)]
    public async Task Test_V5_TaskQueue_ProgressCheckConstraint_RejectsProcessedAboveTotal(int itemsProcessed, int itemsTotal)
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var id = await InsertTaskAsync(connection, "Running");
        var before = await ReadSnapshotAsync(connection, id);

        // Act — a direct, violating UPDATE bypassing the procedure's validation.
        var act = async () => await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET items_processed = @p, items_total = @t WHERE task_queue_id = @id",
            new { p = itemsProcessed, t = itemsTotal, id });

        // Assert
        (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("CK_task_queue_progress");
        (await ReadSnapshotAsync(connection, id)).Should().Be(before);
    }

    public void Dispose()
    {
        // Leave nothing claimable/sweepable behind: this test's non-terminal rows → Cancelled.
        if (_createdTaskIds.Count == 0 || string.IsNullOrEmpty(_connectionString))
        {
            return;
        }

        using var connection = new SqlConnection(_connectionString);
        connection.Execute(
            "UPDATE dbo.task_queue SET status = N'Cancelled', completed_at = SYSUTCDATETIME() " +
            "WHERE task_queue_id IN @ids AND status IN (N'Queued', N'Running')",
            new { ids = _createdTaskIds });
    }
}
