using System.Data;
using System.Text.Json;
using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB tests for <see cref="TaskQueueRepository.SweepInterruptedAsync"/> /
/// <c>dbo.task_queue_sweep_interrupted</c> (CAS-028 §3.4/§5, §13 "TaskQueueSweepTests" — SC-7, SC-8,
/// V-4). Same connection-string / seed-user / Skip convention as
/// <see cref="TaskQueueRestartRepositoryTests"/>. Assertions are scoped to the rows each test seeds:
/// the sweep legitimately fails every <c>Running</c> row in the database.
///
/// Skip-marked in this sandbox — same reason as every other CAS-* real-DB suite in this project. Un-skip
/// and run manually against a real instance with the CAS-028 migration deployed.
/// </summary>
[Collection(TaskQueueRealDbCollection.Name)]
public sealed class TaskQueueSweepTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-028 migration deployed — not available in this sandbox; run manually against a real " +
        "instance before merging.";

    /// <summary>The fixed text the Worker passes (CAS-028 §3.4; <c>Worker.InterruptedByRestartReason</c>).</summary>
    private const string InterruptedReason =
        "Interrupted by a service restart - please restart or resubmit this task.";

    private const string ForcedFailureTrigger = "trg_cas026_test_force_audit_failure";

    private readonly string _connectionString;
    private readonly int _seedUserId;
    private readonly List<int> _createdTaskIds = new();

    public TaskQueueSweepTests()
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

    /// <summary>Inserts a Queued row (dbo.task_queue_ins), then directly moves it to <paramref name="status"/> for setup.</summary>
    private async Task<int> InsertTaskAsync(SqlConnection connection, string status, string taskType = "KeywordSearch")
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@task_type", taskType);
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
                "UPDATE dbo.task_queue SET status = @status, completed_at = SYSUTCDATETIME(), " +
                "result_detail = N'{\"FailureReason\":\"pre-existing\"}' WHERE task_queue_id = @id",
                new { status, id = taskQueueId });
        }

        return taskQueueId;
    }

    private sealed record TaskRowSnapshot(
        int TaskQueueId, string Status, DateTime? CompletedAt, string? ResultDetail, string UpdatedBy, DateTime UpdatedAt);

    private static async Task<Dictionary<int, TaskRowSnapshot>> ReadSnapshotsAsync(SqlConnection connection, IEnumerable<int> ids) =>
        (await connection.QueryAsync<TaskRowSnapshot>(
            "SELECT task_queue_id AS TaskQueueId, status AS Status, completed_at AS CompletedAt, " +
            "result_detail AS ResultDetail, updated_by AS UpdatedBy, updated_at AS UpdatedAt " +
            "FROM dbo.task_queue WHERE task_queue_id IN @ids",
            new { ids }))
        .ToDictionary(r => r.TaskQueueId);

    private sealed record AuditRow(
        int UserId, string TabModule, string Action, int? TaskQueueId, string Result, string? ResultDetail, string CreatedBy);

    private static async Task<List<AuditRow>> ReadInterruptedAuditRowsAsync(SqlConnection connection, IEnumerable<int> ids) =>
        (await connection.QueryAsync<AuditRow>(
            "SELECT user_id AS UserId, tab_module AS TabModule, action AS Action, task_queue_id AS TaskQueueId, " +
            "result AS Result, result_detail AS ResultDetail, created_by AS CreatedBy " +
            "FROM dbo.audit_log WHERE action = N'TaskInterruptedByRestart' AND task_queue_id IN @ids",
            new { ids }))
        .ToList();

    // ---- SC-7: only Running rows are failed, with FailureReason JSON + completed_at -----------------

    [Fact(Skip = SkipReason)]
    public async Task Test_SC7_SweepProc_FailsOnlyRunningRows()
    {
        // Arrange — one row in each of the five statuses.
        var idsByStatus = new Dictionary<string, int>();
        Dictionary<int, TaskRowSnapshot> before;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            foreach (var status in new[] { "Queued", "Running", "Completed", "Failed", "Cancelled" })
            {
                idsByStatus[status] = await InsertTaskAsync(setupConnection, status);
            }

            before = await ReadSnapshotsAsync(setupConnection, idsByStatus.Values);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var swept = await repository.SweepInterruptedAsync("worker-svc", InterruptedReason, CancellationToken.None);

        // Assert — the returned set holds the Running row and none of the others.
        var runningId = idsByStatus["Running"];
        swept.Should().ContainSingle(s => s.TaskQueueId == runningId)
            .Which.Should().Match<Cachet.ArchiveSync.Core.Dtos.SweptTaskRow>(s =>
                s.TaskType == "KeywordSearch" && s.SubmittedByUserId == _seedUserId);
        swept.Select(s => s.TaskQueueId).Should().NotIntersectWith(idsByStatus.Where(p => p.Key != "Running").Select(p => p.Value));

        await using var assertConnection = new SqlConnection(_connectionString);
        var after = await ReadSnapshotsAsync(assertConnection, idsByStatus.Values);

        var sweptRow = after[runningId];
        sweptRow.Status.Should().Be("Failed");
        sweptRow.CompletedAt.Should().NotBeNull();
        sweptRow.UpdatedBy.Should().Be("worker-svc");
        using (var document = JsonDocument.Parse(sweptRow.ResultDetail!))
        {
            document.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("FailureReason");
            document.RootElement.GetProperty("FailureReason").GetString().Should().Be(InterruptedReason);
        }

        foreach (var status in new[] { "Queued", "Completed", "Failed", "Cancelled" })
        {
            var id = idsByStatus[status];
            after[id].Should().Be(before[id], $"the {status} row must be untouched by the sweep");
        }
    }

    // ---- SC-8: one audit row per swept task, committed atomically with the status change ------------

    [Fact(Skip = SkipReason)]
    public async Task Test_SC8_SweepProc_AuditRowPerTask_Atomic()
    {
        // Arrange — two Running rows of different modules.
        int keywordId, scanId;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            keywordId = await InsertTaskAsync(setupConnection, "Running", "KeywordSearch");
            scanId = await InsertTaskAsync(setupConnection, "Running", "ContentScan");
        }

        var ids = new[] { keywordId, scanId };
        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act 1 — force the audit INSERT to fail inside the sweep's transaction.
        await using (var triggerConnection = new SqlConnection(_connectionString))
        {
            await triggerConnection.ExecuteAsync(
                $"CREATE OR ALTER TRIGGER dbo.{ForcedFailureTrigger} ON dbo.audit_log AFTER INSERT AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM inserted WHERE action = N'TaskInterruptedByRestart') " +
                "THROW 50026, N'CAS-028 SC-8 forced audit insert failure', 1; END");
            try
            {
                var forcedFailure = async () =>
                    await repository.SweepInterruptedAsync("worker-svc", InterruptedReason, CancellationToken.None);

                // Assert 1 — the sweep failed and rolled back: rows still Running, no audit rows.
                (await forcedFailure.Should().ThrowAsync<SqlException>())
                    .Which.Message.Should().Contain("forced audit insert failure");
            }
            finally
            {
                await triggerConnection.ExecuteAsync($"DROP TRIGGER IF EXISTS dbo.{ForcedFailureTrigger}");
            }
        }

        await using (var checkConnection = new SqlConnection(_connectionString))
        {
            var rolledBack = await ReadSnapshotsAsync(checkConnection, ids);
            rolledBack.Values.Should().OnlyContain(r => r.Status == "Running" && r.CompletedAt == null && r.ResultDetail == null);
            (await ReadInterruptedAuditRowsAsync(checkConnection, ids)).Should().BeEmpty();
        }

        // Act 2 — normal sweep.
        var swept = await repository.SweepInterruptedAsync("worker-svc", InterruptedReason, CancellationToken.None);

        // Assert 2 — exactly one correctly attributed audit row per swept task.
        swept.Select(s => s.TaskQueueId).Should().Contain(ids);
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadSnapshotsAsync(assertConnection, ids)).Values.Should().OnlyContain(r => r.Status == "Failed");
        var auditRows = await ReadInterruptedAuditRowsAsync(assertConnection, ids);
        auditRows.Should().HaveCount(2);
        auditRows.Should().ContainSingle(a => a.TaskQueueId == keywordId)
            .Which.Should().Be(new AuditRow(_seedUserId, "Keyword Search", "TaskInterruptedByRestart", keywordId,
                "Failure", InterruptedReason, "worker-svc"));
        auditRows.Should().ContainSingle(a => a.TaskQueueId == scanId)
            .Which.Should().Be(new AuditRow(_seedUserId, "O-Drive Sync", "TaskInterruptedByRestart", scanId,
                "Failure", InterruptedReason, "worker-svc"));
    }

    // ---- V-4: blank @updated_by / @failure_reason → RAISERROR severity 16, no rows changed ----------

    [Theory(Skip = SkipReason)]
    [InlineData(null, InterruptedReason, "@updated_by is required.")]
    [InlineData("", InterruptedReason, "@updated_by is required.")]
    [InlineData("   ", InterruptedReason, "@updated_by is required.")]
    [InlineData("worker-svc", null, "@failure_reason is required.")]
    [InlineData("worker-svc", "", "@failure_reason is required.")]
    [InlineData("worker-svc", "   ", "@failure_reason is required.")]
    public async Task Test_V4_SweepProc_BlankParameters_Raises(string? updatedBy, string? failureReason, string expectedMessage)
    {
        // Arrange — a Running row that a successful sweep would change.
        int runningId;
        TaskRowSnapshot before;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            runningId = await InsertTaskAsync(setupConnection, "Running");
            before = (await ReadSnapshotsAsync(setupConnection, new[] { runningId }))[runningId];
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var act = async () => await repository.SweepInterruptedAsync(updatedBy!, failureReason!, CancellationToken.None);

        // Assert
        var thrown = (await act.Should().ThrowAsync<SqlException>()).Which;
        thrown.Class.Should().Be(16);
        thrown.Message.Should().Contain(expectedMessage);

        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadSnapshotsAsync(assertConnection, new[] { runningId }))[runningId].Should().Be(before);
        (await ReadInterruptedAuditRowsAsync(assertConnection, new[] { runningId })).Should().BeEmpty();
    }

    // =================================================================================================
    // CAS-055 - auto-resume of interrupted Keyword Search tasks inside the sweep transaction. Requires the
    // CAS-054 and CAS-055 migrations deployed. Same Skip convention as above.
    // =================================================================================================

    private const string Cas055SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-054 and CAS-055 migrations deployed - not available in this sandbox; run manually against " +
        "a real instance before merging.";

    private const string AutoResumeFailureTrigger = "trg_cas055_test_force_resume_audit_failure";
    private const string RequestJson = "{\"Term\":\"pioneer\"}";

    private async Task<int[]> SeedFileIndexRowsAsync(SqlConnection connection, int count)
    {
        var ids = new int[count];
        for (var i = 0; i < count; i++)
        {
            var parameters = new DynamicParameters();
            parameters.Add("@created_by", "test-fixture");
            parameters.Add("@updated_by", "test-fixture");
            parameters.Add("@source", "B2");
            parameters.Add("@bucket_name", "cachet-archive-prod");
            var relativePath = $"cas055/{Guid.NewGuid():N}.txt";
            parameters.Add("@relative_path", relativePath);
            parameters.Add("@file_name", relativePath.Split('/').Last());
            parameters.Add("@file_extension", "txt");
            parameters.Add("@file_size_bytes", 1024L);
            parameters.Add("@modified_at", DateTime.UtcNow);
            parameters.Add("@file_index_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
            await connection.ExecuteAsync(new CommandDefinition(
                "dbo.file_index_ins", parameters, commandType: CommandType.StoredProcedure));
            ids[i] = parameters.Get<int>("@file_index_id");
        }

        return ids;
    }

    private async Task AddSearchResultAsync(int taskQueueId, int fileIndexId)
    {
        var repository = new SearchResultRepository(BuildRepositoryConfiguration());
        await repository.InsertAsync(
            "test-fixture", "test-fixture", taskQueueId, fileIndexId, "Content", "...pioneer...", "Needs Review",
            CancellationToken.None);
    }

    /// <summary>Inserts a Running row with the given checkpoint state (direct UPDATE for setup).</summary>
    private async Task<int> InsertRunningWithCheckpointAsync(
        SqlConnection connection, string taskType, int? watermark, double progressAgeHours = 1, int autoResumeCount = 0)
    {
        var id = await InsertTaskAsync(connection, "Running", taskType);
        await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET request_parameters = @requestJson, " +
            "resume_watermark_file_index_id = @watermark, auto_resume_count = @autoResumeCount, " +
            "progress_updated_at = DATEADD(MINUTE, @minutesAgoNegative, SYSUTCDATETIME()) WHERE task_queue_id = @id",
            new { requestJson = RequestJson, watermark, autoResumeCount, minutesAgoNegative = -(int)(progressAgeHours * 60), id });
        return id;
    }

    private async Task MakeRunningAsync(int id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET status = N'Running', started_at = SYSUTCDATETIME(), " +
            "progress_updated_at = SYSUTCDATETIME() WHERE task_queue_id = @id", new { id });
        _createdTaskIds.Add(id);
    }

    private sealed record ResumedRow(
        int TaskQueueId, string Status, int SubmittedByUserId, string TaskType, string? RequestParameters,
        int? Watermark, int AutoResumeCount, int? RestartedFrom);

    private static Task<ResumedRow> ReadResumedRowAsync(SqlConnection connection, int? id) =>
        connection.QuerySingleAsync<ResumedRow>(
            "SELECT task_queue_id AS TaskQueueId, status AS Status, submitted_by_user_id AS SubmittedByUserId, " +
            "task_type AS TaskType, request_parameters AS RequestParameters, " +
            "resume_watermark_file_index_id AS Watermark, auto_resume_count AS AutoResumeCount, " +
            "restarted_from_task_queue_id AS RestartedFrom FROM dbo.task_queue WHERE task_queue_id = @id",
            new { id });

    private static Task<int> CountChildrenAsync(SqlConnection connection, int originalId) =>
        connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.task_queue WHERE restarted_from_task_queue_id = @id", new { id = originalId });

    private static async Task<List<AuditRow>> ReadAuditRowsAsync(SqlConnection connection, string action, IEnumerable<int> ids) =>
        (await connection.QueryAsync<AuditRow>(
            "SELECT user_id AS UserId, tab_module AS TabModule, action AS Action, task_queue_id AS TaskQueueId, " +
            "result AS Result, result_detail AS ResultDetail, created_by AS CreatedBy " +
            "FROM dbo.audit_log WHERE action = @action AND task_queue_id IN @ids",
            new { action, ids })).ToList();

    private static async Task<string?> ReadFailureReasonAsync(SqlConnection connection, int id)
    {
        var detail = await connection.QuerySingleAsync<string?>(
            "SELECT result_detail FROM dbo.task_queue WHERE task_queue_id = @id", new { id });
        using var document = JsonDocument.Parse(detail!);
        return document.RootElement.GetProperty("FailureReason").GetString();
    }

    private static Task<DateTime?> ReadAcknowledgedAtAsync(SqlConnection connection, int id) =>
        connection.QuerySingleAsync<DateTime?>(
            "SELECT result_acknowledged_at FROM dbo.task_queue WHERE task_queue_id = @id", new { id });

    private Task<IReadOnlyList<Cachet.ArchiveSync.Core.Dtos.SweptTaskRow>> SweepWithAutoResumeAsync(
        int? maxAgeHours = 24, int? maxAttempts = 3) =>
        new TaskQueueRepository(BuildRepositoryConfiguration()).SweepInterruptedAsync(
            "worker-svc", InterruptedReason, CancellationToken.None, maxAgeHours, maxAttempts);

    // ---- V1: the new parameters are range-validated before the transaction; no row changes -------------

    [Theory(Skip = Cas055SkipReason)]
    [InlineData(0, 3, "@resume_max_age_hours must be between 1 and 720")]
    [InlineData(721, 3, "@resume_max_age_hours must be between 1 and 720")]
    [InlineData(24, -1, "@auto_resume_max_attempts must be between 0 and 10")]
    [InlineData(24, 11, "@auto_resume_max_attempts must be between 0 and 10")]
    public async Task Test_V1_SweepValidatesNewParameters(int maxAgeHours, int maxAttempts, string expectedMessage)
    {
        // Arrange
        int runningId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 1);
            runningId = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[0]);
        }

        // Act
        var act = async () => await SweepWithAutoResumeAsync(maxAgeHours, maxAttempts);

        // Assert
        var thrown = (await act.Should().ThrowAsync<SqlException>()).Which;
        thrown.Class.Should().Be(16);
        thrown.Message.Should().Contain(expectedMessage);
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadSnapshotsAsync(assertConnection, new[] { runningId }))[runningId].Status.Should().Be("Running");
        (await CountChildrenAsync(assertConnection, runningId)).Should().Be(0);
    }

    // ---- V2: only KeywordSearch + watermark + within age + under the cap are auto-resumed --------------

    [Fact(Skip = Cas055SkipReason)]
    public async Task Test_V2_OnlyEligibleRowsAreAutoResumed()
    {
        // Arrange
        int eligible, wrongType, noWatermark, tooOld;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 1);
            eligible = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[0], progressAgeHours: 1);
            wrongType = await InsertRunningWithCheckpointAsync(setup, "ContentScan", files[0], progressAgeHours: 1);
            noWatermark = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", null, progressAgeHours: 1);
            tooOld = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[0], progressAgeHours: 30);
        }

        // Act
        var swept = await SweepWithAutoResumeAsync(24, 3);

        // Assert
        swept.Single(s => s.TaskQueueId == eligible).ResumedAsTaskQueueId.Should().NotBeNull();
        foreach (var id in new[] { wrongType, noWatermark, tooOld })
        {
            var row = swept.Single(s => s.TaskQueueId == id);
            row.ResumedAsTaskQueueId.Should().BeNull();
            row.AutoResumeSuppressed.Should().BeFalse();
        }

        await using var assertConnection = new SqlConnection(_connectionString);
        (await CountChildrenAsync(assertConnection, eligible)).Should().Be(1);
        foreach (var id in new[] { wrongType, noWatermark, tooOld })
        {
            (await CountChildrenAsync(assertConnection, id)).Should().Be(0);
        }
    }

    // ---- V3: auto_resume_count = old + 1 on auto-resume; a manual restart resets it to 0 ---------------

    [Fact(Skip = Cas055SkipReason)]
    public async Task Test_V3_AutoResumeCountIncrementsAndManualRestartResets()
    {
        // Arrange
        int originalId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 1);
            originalId = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[0], autoResumeCount: 1);
        }

        // Act 1 - auto-resume.
        var swept = await SweepWithAutoResumeAsync(24, 3);
        var autoResumedId = swept.Single(s => s.TaskQueueId == originalId).ResumedAsTaskQueueId;

        // Assert 1
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadResumedRowAsync(assertConnection, autoResumedId)).AutoResumeCount.Should().Be(2);

        // Act 2 - manual restart of the (Failed, superseded) original.
        var restart = await new TaskQueueRepository(BuildRepositoryConfiguration()).RestartAsync(
            originalId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None, resumeMaxAgeHours: 24);

        // Assert 2
        restart.Outcome.Should().Be(Cachet.ArchiveSync.Core.Dtos.RestartTaskOutcome.Restarted);
        (await ReadResumedRowAsync(assertConnection, restart.NewTaskQueueId)).AutoResumeCount.Should().Be(0);
    }

    // ---- V4: at/over the cap -> not resumed, original reason kept, unacknowledged, Suppressed audited ---

    [Fact(Skip = Cas055SkipReason)]
    public async Task Test_V4_AttemptCapSuppressesAutoResume()
    {
        // Arrange
        int cappedId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 1);
            cappedId = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[0], autoResumeCount: 3);
        }

        // Act
        var swept = await SweepWithAutoResumeAsync(24, 3);

        // Assert
        var row = swept.Single(s => s.TaskQueueId == cappedId);
        row.ResumedAsTaskQueueId.Should().BeNull();
        row.AutoResumeSuppressed.Should().BeTrue();

        await using var assertConnection = new SqlConnection(_connectionString);
        (await CountChildrenAsync(assertConnection, cappedId)).Should().Be(0);
        (await ReadFailureReasonAsync(assertConnection, cappedId)).Should().Be(InterruptedReason);
        (await ReadAcknowledgedAtAsync(assertConnection, cappedId)).Should().BeNull("a human-needed failure must still raise the badge");
        (await ReadAuditRowsAsync(assertConnection, "TaskAutoResumeSuppressed", new[] { cappedId }))
            .Should().ContainSingle().Which.Result.Should().Be("Failure");
        (await ReadAuditRowsAsync(assertConnection, "TaskAutoResumed", new[] { cappedId })).Should().BeEmpty();
    }

    // ---- V5: a failure in the auto-resume part rolls back the whole sweep; a retry succeeds -------------

    [Fact(Skip = Cas055SkipReason)]
    public async Task Test_V5_AutoResumeFailureRollsBackSweepAndIsRetried()
    {
        // Arrange
        int originalId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 1);
            originalId = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[0]);
        }

        // Act 1 - force the TaskAutoResumed audit INSERT to fail.
        await using (var triggerConnection = new SqlConnection(_connectionString))
        {
            await triggerConnection.ExecuteAsync(
                $"CREATE OR ALTER TRIGGER dbo.{AutoResumeFailureTrigger} ON dbo.audit_log AFTER INSERT AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM inserted WHERE action = N'TaskAutoResumed') " +
                "THROW 50055, N'CAS-055 V5 forced auto-resume audit failure', 1; END");
            try
            {
                var act = async () => await SweepWithAutoResumeAsync(24, 3);

                // Assert 1 - whole transaction rolled back.
                (await act.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("forced auto-resume audit failure");
                await using var checkConnection = new SqlConnection(_connectionString);
                (await ReadSnapshotsAsync(checkConnection, new[] { originalId }))[originalId].Status.Should().Be("Running");
                (await CountChildrenAsync(checkConnection, originalId)).Should().Be(0);
                (await ReadInterruptedAuditRowsAsync(checkConnection, new[] { originalId })).Should().BeEmpty();
            }
            finally
            {
                await triggerConnection.ExecuteAsync($"DROP TRIGGER IF EXISTS dbo.{AutoResumeFailureTrigger}");
            }
        }

        // Act 2 - the Worker's retry loop calls the sweep again once the fault clears.
        var swept = await SweepWithAutoResumeAsync(24, 3);

        // Assert 2
        swept.Single(s => s.TaskQueueId == originalId).ResumedAsTaskQueueId.Should().NotBeNull();
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadSnapshotsAsync(assertConnection, new[] { originalId }))[originalId].Status.Should().Be("Failed");
        (await CountChildrenAsync(assertConnection, originalId)).Should().Be(1);
    }

    // ---- SC-1: sweep fails the original AND creates a Queued resumed task with watermark + matches <= W -

    [Fact(Skip = Cas055SkipReason)]
    public async Task Test_SC1_SweepCreatesResumedTaskWithWatermarkAndMatches()
    {
        // Arrange - matches for f1..f4, W = f2.
        int originalId;
        int[] files;
        await using (var setup = new SqlConnection(_connectionString))
        {
            files = await SeedFileIndexRowsAsync(setup, 4);
            originalId = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[1]);
            foreach (var f in files)
            {
                await AddSearchResultAsync(originalId, f);
            }
        }

        // Act
        var swept = await SweepWithAutoResumeAsync(24, 3);

        // Assert
        var sweptRow = swept.Single(s => s.TaskQueueId == originalId);
        sweptRow.CarriedOverCount.Should().Be(2);
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadSnapshotsAsync(assertConnection, new[] { originalId }))[originalId].Status.Should().Be("Failed");
        var resumed = await ReadResumedRowAsync(assertConnection, sweptRow.ResumedAsTaskQueueId);
        resumed.Status.Should().Be("Queued");
        resumed.SubmittedByUserId.Should().Be(_seedUserId);
        resumed.TaskType.Should().Be("KeywordSearch");
        resumed.RequestParameters.Should().Be(RequestJson);
        resumed.Watermark.Should().Be(files[1]);
        resumed.RestartedFrom.Should().Be(originalId);
        resumed.AutoResumeCount.Should().Be(1);
        (await assertConnection.QueryAsync<int>(
            "SELECT file_index_id FROM dbo.search_result WHERE task_queue_id = @id ORDER BY file_index_id",
            new { id = resumed.TaskQueueId })).Should().Equal(files[0], files[1]);
    }

    // ---- SC-4: after three consecutive auto-resumes of a lineage the next sweep suppresses -------------

    [Fact(Skip = Cas055SkipReason)]
    public async Task Test_SC4_ThirdConsecutiveInterruptionIsSuppressed()
    {
        // Arrange
        int currentId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 1);
            currentId = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[0]);
        }

        // Act - interruptions 1..3 are auto-resumed (count 1, 2, 3); each resumed task is made Running again.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var swept = await SweepWithAutoResumeAsync(24, 3);
            var resumedId = swept.Single(s => s.TaskQueueId == currentId).ResumedAsTaskQueueId;
            resumedId.Should().NotBeNull($"interruption {attempt} is under the cap");
            currentId = resumedId!.Value;
            await MakeRunningAsync(currentId);
        }

        var capped = await SweepWithAutoResumeAsync(24, 3);

        // Assert - the row now at count 3 is not resumed.
        var cappedRow = capped.Single(s => s.TaskQueueId == currentId);
        cappedRow.ResumedAsTaskQueueId.Should().BeNull();
        cappedRow.AutoResumeSuppressed.Should().BeTrue();
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadResumedRowAsync(assertConnection, currentId)).AutoResumeCount.Should().Be(3);
        (await CountChildrenAsync(assertConnection, currentId)).Should().Be(0);
        (await ReadFailureReasonAsync(assertConnection, currentId)).Should().Be(InterruptedReason);
        (await ReadAuditRowsAsync(assertConnection, "TaskAutoResumeSuppressed", new[] { currentId })).Should().ContainSingle();
    }

    // ---- SC-6: ineligible rows behave exactly as before (no new row, original reason, no extra audit) ---

    [Fact(Skip = Cas055SkipReason)]
    public async Task Test_SC6_IneligibleRowsBehaveAsBefore()
    {
        // Arrange
        int wrongType, noWatermark, tooOld;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 1);
            wrongType = await InsertRunningWithCheckpointAsync(setup, "ContentScan", files[0]);
            noWatermark = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", null);
            tooOld = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[0], progressAgeHours: 30);
        }

        var ids = new[] { wrongType, noWatermark, tooOld };

        // Act
        var swept = await SweepWithAutoResumeAsync(24, 3);

        // Assert
        swept.Where(s => ids.Contains(s.TaskQueueId)).Should().OnlyContain(
            s => s.ResumedAsTaskQueueId == null && !s.AutoResumeSuppressed && s.CarriedOverCount == null);
        await using var assertConnection = new SqlConnection(_connectionString);
        foreach (var id in ids)
        {
            (await CountChildrenAsync(assertConnection, id)).Should().Be(0);
            (await ReadFailureReasonAsync(assertConnection, id)).Should().Be(InterruptedReason);
            (await ReadAcknowledgedAtAsync(assertConnection, id)).Should().BeNull();
        }

        (await ReadAuditRowsAsync(assertConnection, "TaskAutoResumed", ids)).Should().BeEmpty();
        (await ReadAuditRowsAsync(assertConnection, "TaskAutoResumeSuppressed", ids)).Should().BeEmpty();
        (await ReadInterruptedAuditRowsAsync(assertConnection, ids)).Should().HaveCount(3);
    }

    // ---- SC-7: one TaskInterruptedByRestart + one TaskAutoResumed (new task id), no names/terms --------

    [Fact(Skip = Cas055SkipReason)]
    public async Task Test_SC7_AutoResumeAuditCompleteAndPrivacySafe()
    {
        // Arrange - W = f2, two matches <= W carried over.
        int originalId;
        int[] files;
        await using (var setup = new SqlConnection(_connectionString))
        {
            files = await SeedFileIndexRowsAsync(setup, 3);
            originalId = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[1]);
            await AddSearchResultAsync(originalId, files[0]);
            await AddSearchResultAsync(originalId, files[1]);
        }

        // Act
        var swept = await SweepWithAutoResumeAsync(24, 3);

        // Assert
        var newId = swept.Single(s => s.TaskQueueId == originalId).ResumedAsTaskQueueId!.Value;
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadInterruptedAuditRowsAsync(assertConnection, new[] { originalId })).Should().ContainSingle();
        var resumedAudit = (await ReadAuditRowsAsync(assertConnection, "TaskAutoResumed", new[] { newId }))
            .Should().ContainSingle().Subject;
        resumedAudit.UserId.Should().Be(_seedUserId);
        resumedAudit.TabModule.Should().Be("Keyword Search");
        resumedAudit.Result.Should().Be("Success");
        resumedAudit.CreatedBy.Should().Be("worker-svc");
        resumedAudit.ResultDetail.Should().Be(
            $"Automatically resumed from task {originalId} at file_index_id {files[1]}; 2 match result(s) carried over (auto-resume 1 of 3).");
        resumedAudit.ResultDetail.Should().NotContain("pioneer").And.NotContain("cas055/");
        (await ReadAuditRowsAsync(assertConnection, "TaskAutoResumed", new[] { originalId })).Should().BeEmpty();
    }

    // ---- SC-8: original names the resumed task and is acknowledged; resumed task is listed for the user -

    [Fact(Skip = Cas055SkipReason)]
    public async Task Test_SC8_OriginalMarkedSupersededAndResumedTaskListed()
    {
        // Arrange
        int originalId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 1);
            originalId = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[0]);
        }

        // Act
        var swept = await SweepWithAutoResumeAsync(24, 3);
        var newId = swept.Single(s => s.TaskQueueId == originalId).ResumedAsTaskQueueId!.Value;

        // Assert
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadFailureReasonAsync(assertConnection, originalId)).Should().Be(
            $"Interrupted by a service restart - automatically resumed as task {newId}.");
        (await ReadAcknowledgedAtAsync(assertConnection, originalId)).Should().NotBeNull();

        var (items, _) = await new TaskQueueRepository(BuildRepositoryConfiguration()).ListAsync(
            _seedUserId, "KeywordSearch", null, 1, 200, CancellationToken.None);
        items.Select(i => i.TaskQueueId).Should().Contain(newId);
    }

    // ---- SC-9: AutoResumeMaxAttempts = 0 (and NULL) disables auto-resume entirely -----------------------

    [Theory(Skip = Cas055SkipReason)]
    [InlineData(0)]
    [InlineData(null)]
    public async Task Test_SC9_ZeroAttemptsDisablesAutoResume(int? maxAttempts)
    {
        // Arrange
        int originalId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 1);
            originalId = await InsertRunningWithCheckpointAsync(setup, "KeywordSearch", files[0]);
        }

        // Act
        var swept = await SweepWithAutoResumeAsync(24, maxAttempts);

        // Assert
        var row = swept.Single(s => s.TaskQueueId == originalId);
        row.ResumedAsTaskQueueId.Should().BeNull();
        row.AutoResumeSuppressed.Should().BeFalse();
        await using var assertConnection = new SqlConnection(_connectionString);
        (await CountChildrenAsync(assertConnection, originalId)).Should().Be(0);
        (await ReadFailureReasonAsync(assertConnection, originalId)).Should().Be(InterruptedReason);
        (await ReadAuditRowsAsync(assertConnection, "TaskAutoResumed", new[] { originalId })).Should().BeEmpty();
        (await ReadAuditRowsAsync(assertConnection, "TaskAutoResumeSuppressed", new[] { originalId })).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            return;
        }

        using var connection = new SqlConnection(_connectionString);
        // Defensive: never leave the forced-failure trigger behind (SC-8 already drops it in finally).
        connection.Execute($"DROP TRIGGER IF EXISTS dbo.{ForcedFailureTrigger}");
        connection.Execute($"DROP TRIGGER IF EXISTS dbo.{AutoResumeFailureTrigger}");
        if (_createdTaskIds.Count > 0)
        {
            // Leave nothing claimable/sweepable behind: this test's non-terminal rows → Cancelled.
            connection.Execute(
                "UPDATE dbo.task_queue SET status = N'Cancelled', completed_at = SYSUTCDATETIME() " +
                "WHERE task_queue_id IN @ids AND status IN (N'Queued', N'Running')",
                new { ids = _createdTaskIds });
        }
    }
}
