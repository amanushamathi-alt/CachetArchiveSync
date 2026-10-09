using System.Data;
using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Core.Dtos;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB tests for CAS-056 §13 "TaskQueueCancelRepositoryTests" and "TaskQueueSweepTests (extend)":
/// <see cref="TaskQueueRepository.CancelAsync"/> / <see cref="TaskQueueRepository.IsCancelRequestedAsync"/>
/// (<c>dbo.task_queue_cancel_req</c> outcomes 0-3, <c>dbo.task_queue_cancel_requested_get</c>) and the
/// amended <c>dbo.task_queue_sweep_interrupted</c> (V3, V4, V5, V10, SC-1, SC-5, SC-6, SC-7). Same
/// connection-string / seed-user / Skip convention as <see cref="TaskQueueSweepTests"/> and
/// <see cref="TaskQueueClaimConcurrencyTests"/>; assertions are scoped to the rows each test seeds.
///
/// Skip-marked, same reason as every other real-DB suite in this project: they need a live SQL Server
/// with the CAS-056 migration (<c>20261006_190000__CAS-056-cancel-keyword-search.sql</c>) deployed.
/// Methods named <c>Cov_*</c> are not map items (extra outcome / branch coverage).
/// </summary>
internal static class CancelRealDb
{
    public const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-056 migration deployed - run manually against a real instance before merging.";

    public static string ConnectionString(out int seedUserId)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        seedUserId = int.TryParse(configuration["IntegrationTests:SeedUserId"], out var id) ? id : 1;
        return configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;
    }

    public sealed record TaskRow(
        int TaskQueueId, string Status, bool IsCancelRequested, DateTime? StartedAt, DateTime? CompletedAt,
        string? ResultDetail);

    public sealed record AuditRow(
        int UserId, string TabModule, string Action, int? TaskQueueId, string Result, string? ResultDetail,
        string? FilePath);

    public static Task<TaskRow> ReadTaskAsync(SqlConnection connection, int id) =>
        connection.QuerySingleAsync<TaskRow>(
            "SELECT task_queue_id AS TaskQueueId, status AS Status, is_cancel_requested AS IsCancelRequested, " +
            "started_at AS StartedAt, completed_at AS CompletedAt, result_detail AS ResultDetail " +
            "FROM dbo.task_queue WHERE task_queue_id = @id", new { id });

    public static async Task<List<AuditRow>> ReadAuditAsync(SqlConnection connection, string action, params int[] ids) =>
        (await connection.QueryAsync<AuditRow>(
            "SELECT user_id AS UserId, tab_module AS TabModule, action AS Action, task_queue_id AS TaskQueueId, " +
            "result AS Result, result_detail AS ResultDetail, file_path AS FilePath " +
            "FROM dbo.audit_log WHERE action = @action AND task_queue_id IN @ids",
            new { action, ids })).ToList();

    public static async Task<int> InsertTaskAsync(
        SqlConnection connection, int userId, string status, string taskType = "KeywordSearch",
        string? requestParameters = null, bool flag = false)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@task_type", taskType);
        parameters.Add("@submitted_by_user_id", userId);
        parameters.Add("@request_parameters", requestParameters);
        parameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", parameters, commandType: CommandType.StoredProcedure));
        var id = parameters.Get<int>("@task_queue_id");

        if (status == "Running")
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.task_queue SET status = N'Running', started_at = SYSUTCDATETIME() WHERE task_queue_id = @id",
                new { id });
        }
        else if (status != "Queued")
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.task_queue SET status = @status, completed_at = SYSUTCDATETIME(), " +
                "result_detail = N'{\"FailureReason\":\"pre-existing\"}' WHERE task_queue_id = @id",
                new { status, id });
        }

        if (flag)
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.task_queue SET is_cancel_requested = 1 WHERE task_queue_id = @id", new { id });
        }

        return id;
    }

    public static void Cleanup(string connectionString, IReadOnlyCollection<int> ids)
    {
        if (string.IsNullOrEmpty(connectionString) || ids.Count == 0)
        {
            return;
        }

        using var connection = new SqlConnection(connectionString);
        // Leave nothing claimable/sweepable behind: this test's non-terminal rows -> Cancelled.
        connection.Execute(
            "UPDATE dbo.task_queue SET status = N'Cancelled', completed_at = SYSUTCDATETIME() " +
            "WHERE task_queue_id IN @ids AND status IN (N'Queued', N'Running')",
            new { ids });
    }
}

[Collection(TaskQueueRealDbCollection.Name)]
public sealed class TaskQueueCancelRepositoryTests : IDisposable
{
    private const string SensitiveTerm = "acme-corp-merger-confidential-code-7734";

    private readonly string _connectionString;
    private readonly int _seedUserId;
    private readonly List<int> _createdTaskIds = new();

    public TaskQueueCancelRepositoryTests()
    {
        _connectionString = CancelRealDb.ConnectionString(out _seedUserId);
    }

    private int OtherUserId => _seedUserId + 1;

    private TaskQueueRepository NewRepository() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = _connectionString })
            .Build());

    private async Task<int> SeedAsync(string status, string taskType = "KeywordSearch", int? userId = null,
        string? requestParameters = null, bool flag = false)
    {
        await using var connection = new SqlConnection(_connectionString);
        var id = await CancelRealDb.InsertTaskAsync(connection, userId ?? _seedUserId, status, taskType, requestParameters, flag);
        _createdTaskIds.Add(id);
        return id;
    }

    // ---- SC-1 / V3: Queued -> Cancelled immediately, never run, never claimable ---------------------------

    [Fact(Skip = CancelRealDb.SkipReason)]
    public async Task Test_SC1_QueuedCancel()
    {
        // Arrange
        var id = await SeedAsync("Queued");

        // Act
        var result = await NewRepository().CancelAsync(id, _seedUserId, "api-svc", CancellationToken.None);

        // Assert - Cancelled without ever running.
        result.Outcome.Should().Be(CancelTaskOutcome.Cancelled);
        await using var connection = new SqlConnection(_connectionString);
        var row = await CancelRealDb.ReadTaskAsync(connection, id);
        row.Status.Should().Be("Cancelled");
        row.IsCancelRequested.Should().BeTrue();
        row.StartedAt.Should().BeNull("the task never ran");
        row.CompletedAt.Should().NotBeNull();
    }

    [Fact(Skip = CancelRealDb.SkipReason)]
    public async Task Test_V3_CancelQueuedIsImmediateAndUnclaimable()
    {
        // Arrange
        var id = await SeedAsync("Queued");
        var repository = NewRepository();

        // Act
        var result = await repository.CancelAsync(id, _seedUserId, "api-svc", CancellationToken.None);

        // Assert - drain the claim queue (bounded); the cancelled row must never be returned.
        result.Outcome.Should().Be(CancelTaskOutcome.Cancelled);
        var claimed = new List<int>();
        for (var i = 0; i < 200; i++)
        {
            var next = await repository.ClaimNextAsync("worker-svc", CancellationToken.None);
            if (next is null)
            {
                break;
            }

            claimed.Add(next.TaskQueueId);
            _createdTaskIds.Add(next.TaskQueueId);
        }

        claimed.Should().NotContain(id);
        await using var connection = new SqlConnection(_connectionString);
        (await CancelRealDb.ReadTaskAsync(connection, id)).Status.Should().Be("Cancelled");
    }

    // ---- V4: Running -> flag only, idempotent, one audit row ---------------------------------------------

    [Fact(Skip = CancelRealDb.SkipReason)]
    public async Task Test_V4_CancelRunningIsIdempotent()
    {
        // Arrange
        var id = await SeedAsync("Running");
        var repository = NewRepository();

        // Act
        var first = await repository.CancelAsync(id, _seedUserId, "api-svc", CancellationToken.None);
        var second = await repository.CancelAsync(id, _seedUserId, "api-svc", CancellationToken.None);

        // Assert
        first.Outcome.Should().Be(CancelTaskOutcome.CancelRequested);
        second.Outcome.Should().Be(CancelTaskOutcome.CancelRequested);
        await using var connection = new SqlConnection(_connectionString);
        var row = await CancelRealDb.ReadTaskAsync(connection, id);
        row.Status.Should().Be("Running", "the Worker, not the endpoint, ends a running task");
        row.IsCancelRequested.Should().BeTrue();
        row.CompletedAt.Should().BeNull();
        (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelRequested", id)).Should().ContainSingle(
            "a repeated request must not write a second audit row");
        (await repository.IsCancelRequestedAsync(id, CancellationToken.None)).Should().BeTrue();
    }

    // ---- V5: cancel vs claim - exactly one of Queued-direct / Running-flag ------------------------------

    [Fact(Skip = CancelRealDb.SkipReason)]
    public async Task Test_V5_CancelVsClaimIsAtomic()
    {
        // Arrange / Act - many rounds of a cancel racing a claim on the same Queued row. No deadlock (SQL error 1205)
        // is tolerated: both procedures must lock in the same order (index key first), so any exception fails the test.
        var repository = NewRepository();
        for (var round = 0; round < 25; round++)
        {
            var id = await SeedAsync("Queued");
            using var gate = new ManualResetEventSlim(false);

            var cancelTask = Task.Run(async () =>
            {
                gate.Wait();
                return await repository.CancelAsync(id, _seedUserId, "api-svc", CancellationToken.None);
            });
            var claimTask = Task.Run(async () =>
            {
                gate.Wait();
                var claimedIds = new List<int>();
                // Claim until our row has been taken or the queue is empty (other rows may be ahead of it).
                for (var i = 0; i < 200; i++)
                {
                    var next = await repository.ClaimNextAsync("worker-svc", CancellationToken.None);
                    if (next is null)
                    {
                        break;
                    }

                    claimedIds.Add(next.TaskQueueId);
                }

                return claimedIds;
            });
            gate.Set();
            var cancel = await cancelTask;
            var claimed = await claimTask;
            _createdTaskIds.AddRange(claimed);

            // Assert - exactly one wins; the flag is never lost.
            await using var connection = new SqlConnection(_connectionString);
            var row = await CancelRealDb.ReadTaskAsync(connection, id);
            row.IsCancelRequested.Should().BeTrue("cancel must never be lost (round {0})", round);
            if (cancel.Outcome == CancelTaskOutcome.Cancelled)
            {
                row.Status.Should().Be("Cancelled");
                claimed.Should().NotContain(id, "a directly cancelled task is never claimed");
                (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelled", id)).Should().ContainSingle();
            }
            else
            {
                cancel.Outcome.Should().Be(CancelTaskOutcome.CancelRequested);
                row.Status.Should().Be("Running");
                claimed.Should().Contain(id, "the claim won, so the Worker owns the running task and sees the flag");
                (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelRequested", id)).Should().ContainSingle();
            }
        }
    }

    // ---- SC-5: audit rows - fields, ids/integers only ---------------------------------------------------

    [Fact(Skip = CancelRealDb.SkipReason)]
    public async Task Test_SC5_AuditRows()
    {
        // Arrange - the stored request carries a sensitive term that must never reach an audit row.
        var requestJson = $"{{\"Term\":\"{SensitiveTerm}\"}}";
        var queuedId = await SeedAsync("Queued", requestParameters: requestJson);
        var runningId = await SeedAsync("Running", requestParameters: requestJson);
        var repository = NewRepository();

        // Act
        await repository.CancelAsync(queuedId, _seedUserId, "api-svc", CancellationToken.None);
        await repository.CancelAsync(runningId, _seedUserId, "api-svc", CancellationToken.None);

        // Assert
        await using var connection = new SqlConnection(_connectionString);
        var cancelled = (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelled", queuedId)).Should().ContainSingle().Subject;
        var requested = (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelRequested", runningId)).Should().ContainSingle().Subject;

        foreach (var (row, id) in new[] { (cancelled, queuedId), (requested, runningId) })
        {
            row.UserId.Should().Be(_seedUserId, "user_id is the submitter");
            row.TaskQueueId.Should().Be(id);
            row.TabModule.Should().Be("Keyword Search");
            row.Result.Should().Be("Success");
            row.FilePath.Should().BeNull("no path / file name in a cancel audit row");
            row.ResultDetail.Should().Contain(id.ToString());
            row.ResultDetail.Should().NotContain(SensitiveTerm);
        }
    }

    // ---- SC-6 (+ V1 at the SP level): not-owned is indistinguishable from missing ------------------------

    [Fact(Skip = CancelRealDb.SkipReason)]
    public async Task Test_SC6_OwnershipIsolation()
    {
        // Arrange - a Queued and a Running task owned by the seed user.
        var queuedId = await SeedAsync("Queued");
        var runningId = await SeedAsync("Running");
        var repository = NewRepository();

        // Act - another user cancels them; a missing id is cancelled by the owner.
        var otherQueued = await repository.CancelAsync(queuedId, OtherUserId, "api-svc", CancellationToken.None);
        var otherRunning = await repository.CancelAsync(runningId, OtherUserId, "api-svc", CancellationToken.None);
        var missing = await repository.CancelAsync(int.MaxValue - 7, _seedUserId, "api-svc", CancellationToken.None);

        // Assert - identical outcome, and nothing changed (no flag, no audit).
        otherQueued.Outcome.Should().Be(CancelTaskOutcome.NotFound);
        otherRunning.Outcome.Should().Be(CancelTaskOutcome.NotFound);
        missing.Outcome.Should().Be(CancelTaskOutcome.NotFound);
        await using var connection = new SqlConnection(_connectionString);
        var queued = await CancelRealDb.ReadTaskAsync(connection, queuedId);
        var running = await CancelRealDb.ReadTaskAsync(connection, runningId);
        (queued.Status, queued.IsCancelRequested).Should().Be(("Queued", false));
        (running.Status, running.IsCancelRequested).Should().Be(("Running", false));
        (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelled", queuedId, runningId)).Should().BeEmpty();
        (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelRequested", queuedId, runningId)).Should().BeEmpty();
    }

    // ---- Outcome 3 (V2 at the SP level): terminal or wrong type, state unchanged ----------------------------

    [Theory(Skip = CancelRealDb.SkipReason)]
    [InlineData("Completed", "KeywordSearch")]
    [InlineData("Failed", "KeywordSearch")]
    [InlineData("Cancelled", "KeywordSearch")]
    [InlineData("Queued", "ContentScan")]
    [InlineData("Running", "SyncCopy")]
    public async Task Cov_Sp_TerminalOrWrongType_Is3_AndUnchanged(string status, string taskType)
    {
        // Arrange
        var id = await SeedAsync(status, taskType);
        await using var connection = new SqlConnection(_connectionString);
        var before = await CancelRealDb.ReadTaskAsync(connection, id);

        // Act
        var result = await NewRepository().CancelAsync(id, _seedUserId, "api-svc", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(CancelTaskOutcome.NotCancellable);
        (await CancelRealDb.ReadTaskAsync(connection, id)).Should().Be(before);
        (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelled", id)).Should().BeEmpty();
        (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelRequested", id)).Should().BeEmpty();
    }

    [Fact(Skip = CancelRealDb.SkipReason)]
    public async Task Cov_IsCancelRequested_ReflectsFlag_AndFalseWhenMissing()
    {
        var unflagged = await SeedAsync("Running");
        var flagged = await SeedAsync("Running", flag: true);
        var repository = NewRepository();

        (await repository.IsCancelRequestedAsync(unflagged, CancellationToken.None)).Should().BeFalse();
        (await repository.IsCancelRequestedAsync(flagged, CancellationToken.None)).Should().BeTrue();
        (await repository.IsCancelRequestedAsync(int.MaxValue - 7, CancellationToken.None)).Should().BeFalse();
    }

    public void Dispose() => CancelRealDb.Cleanup(_connectionString, _createdTaskIds);
}

/// <summary>CAS-056 §2.5 / V10 / SC-7 - the amended <c>dbo.task_queue_sweep_interrupted</c>.</summary>
[Collection(TaskQueueRealDbCollection.Name)]
public sealed class TaskQueueCancelSweepTests : IDisposable
{
    private const string InterruptedReason =
        "Interrupted by a service restart - please restart or resubmit this task.";

    private readonly string _connectionString;
    private readonly int _seedUserId;
    private readonly List<int> _createdTaskIds = new();

    public TaskQueueCancelSweepTests()
    {
        _connectionString = CancelRealDb.ConnectionString(out _seedUserId);
    }

    private TaskQueueRepository NewRepository() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = _connectionString })
            .Build());

    private async Task<int> SeedRunningAsync(bool flag, int? watermark = null)
    {
        await using var connection = new SqlConnection(_connectionString);
        var id = await CancelRealDb.InsertTaskAsync(
            connection, _seedUserId, "Running", requestParameters: "{\"Term\":\"pioneer\"}", flag: flag);
        _createdTaskIds.Add(id);
        if (watermark is not null)
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.task_queue SET resume_watermark_file_index_id = @watermark, " +
                "progress_updated_at = SYSUTCDATETIME() WHERE task_queue_id = @id", new { watermark, id });
        }

        return id;
    }

    private async Task<int> SeedFileIndexRowAsync(SqlConnection connection)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@source", "B2");
        parameters.Add("@bucket_name", "cachet-archive-prod");
        var relativePath = $"cas056/{Guid.NewGuid():N}.txt";
        parameters.Add("@relative_path", relativePath);
        parameters.Add("@file_name", relativePath.Split('/').Last());
        parameters.Add("@file_extension", "txt");
        parameters.Add("@file_size_bytes", 1024L);
        parameters.Add("@modified_at", DateTime.UtcNow);
        parameters.Add("@file_index_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.file_index_ins", parameters, commandType: CommandType.StoredProcedure));
        return parameters.Get<int>("@file_index_id");
    }

    [Fact(Skip = CancelRealDb.SkipReason)]
    public async Task Test_SC7_SweepHonoursFlag()
    {
        // Arrange - a flagged Running row, swept with the default (no auto-resume) arguments.
        var id = await SeedRunningAsync(flag: true);

        // Act
        var swept = await NewRepository().SweepInterruptedAsync("worker-svc", InterruptedReason, CancellationToken.None);

        // Assert - Cancelled (never Failed), audited TaskCancelled, no restart-failure audit, not in the returned set.
        swept.Select(s => s.TaskQueueId).Should().NotContain(id);
        await using var connection = new SqlConnection(_connectionString);
        var row = await CancelRealDb.ReadTaskAsync(connection, id);
        row.Status.Should().Be("Cancelled");
        row.CompletedAt.Should().NotBeNull();
        var audit = (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelled", id)).Should().ContainSingle().Subject;
        audit.UserId.Should().Be(_seedUserId);
        audit.Result.Should().Be("Success");
        audit.ResultDetail.Should().Contain(id.ToString());
        (await CancelRealDb.ReadAuditAsync(connection, "TaskInterruptedByRestart", id)).Should().BeEmpty();
    }

    [Fact(Skip = CancelRealDb.SkipReason)]
    public async Task Test_V10_SweepCancelsFlaggedAndNeverResumesThem()
    {
        // Arrange - with auto-resume ENABLED, a flagged and an unflagged Running row both have a resumable checkpoint.
        int flaggedId, unflaggedId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var w1 = await SeedFileIndexRowAsync(setup);
            var w2 = await SeedFileIndexRowAsync(setup);
            flaggedId = await SeedRunningAsync(flag: true, watermark: w1);
            unflaggedId = await SeedRunningAsync(flag: false, watermark: w2);
        }

        // Act
        var swept = await NewRepository().SweepInterruptedAsync(
            "worker-svc", InterruptedReason, CancellationToken.None, resumeMaxAgeHours: 24, autoResumeMaxAttempts: 3);

        // Assert
        await using var connection = new SqlConnection(_connectionString);

        // Flagged: Cancelled, no child task, audited TaskCancelled, no interrupt/resume audit, not returned.
        (await CancelRealDb.ReadTaskAsync(connection, flaggedId)).Status.Should().Be("Cancelled");
        swept.Select(s => s.TaskQueueId).Should().NotContain(flaggedId);
        (await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.task_queue WHERE restarted_from_task_queue_id = @id", new { id = flaggedId }))
            .Should().Be(0, "a cancelled task is never auto-resumed");
        (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelled", flaggedId)).Should().ContainSingle();
        (await CancelRealDb.ReadAuditAsync(connection, "TaskInterruptedByRestart", flaggedId)).Should().BeEmpty();
        (await CancelRealDb.ReadAuditAsync(connection, "TaskAutoResumeSuppressed", flaggedId)).Should().BeEmpty();

        // Unflagged: unchanged behaviour - swept and auto-resumed (original ends Failed with the resume reason).
        var unflagged = swept.Should().ContainSingle(s => s.TaskQueueId == unflaggedId).Subject;
        unflagged.ResumedAsTaskQueueId.Should().NotBeNull();
        (await CancelRealDb.ReadTaskAsync(connection, unflaggedId)).Status.Should().Be("Failed");
        (await CancelRealDb.ReadAuditAsync(connection, "TaskCancelled", unflaggedId)).Should().BeEmpty();
        (await CancelRealDb.ReadAuditAsync(connection, "TaskInterruptedByRestart", unflaggedId)).Should().ContainSingle();
        _createdTaskIds.Add(unflagged.ResumedAsTaskQueueId!.Value);
    }

    public void Dispose() => CancelRealDb.Cleanup(_connectionString, _createdTaskIds);
}
