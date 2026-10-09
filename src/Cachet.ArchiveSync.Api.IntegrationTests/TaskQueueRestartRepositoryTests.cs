using System.Data;
using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Core.Dtos;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB tests for <see cref="TaskQueueRepository.RestartAsync"/> / <c>dbo.task_queue_restart_ins</c>
/// (CAS-016 §7/§13 Test Plan "TaskQueueRestartRepositoryTests" — SC-1, SC-3/V1, SC-5/V5, V4). Modeled
/// directly on <c>TaskQueueClaimConcurrencyTests</c> (CAS-006) — same connection-string/seed-user
/// convention, same "insert via <c>dbo.task_queue_ins</c>, then manipulate directly for setup" style
/// this project's existing real-DB suites use.
///
/// Skip-marked in this sandbox — same reason as every other CAS-* real-DB suite in this project (no
/// live SQL Server instance reachable here). Un-skip and run manually against a real instance with
/// the CAS-016 migration (<c>20260922_240000__CAS-016-task-queue-restart.sql</c>) deployed.
/// </summary>
public sealed class TaskQueueRestartRepositoryTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-016 migration deployed — not available in this sandbox; run manually against a real " +
        "instance before merging.";

    private readonly string _connectionString;
    private readonly int _seedUserId;
    private readonly int _otherUserId;

    public TaskQueueRestartRepositoryTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;
        _seedUserId = int.TryParse(configuration["IntegrationTests:SeedUserId"], out var id) ? id : 1;
        _otherUserId = _seedUserId + 1;
    }

    private IConfiguration BuildRepositoryConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CachetArchiveSync"] = _connectionString
            })
            .Build();

    /// <summary>Inserts a Queued row (dbo.task_queue_ins), then directly moves it to the desired
    /// terminal status for test setup — mirroring how a real Worker task processor would arrive
    /// there via dbo.task_queue_upd, without depending on that procedure's own contract here.</summary>
    private async Task<int> InsertTaskAsync(
        SqlConnection connection, string status, string? requestParameters, int submittedByUserId)
    {
        var insertParameters = new DynamicParameters();
        insertParameters.Add("@created_by", "test-fixture");
        insertParameters.Add("@updated_by", "test-fixture");
        insertParameters.Add("@task_type", "KeywordSearch");
        insertParameters.Add("@submitted_by_user_id", submittedByUserId);
        insertParameters.Add("@request_parameters", requestParameters);
        insertParameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", insertParameters, commandType: CommandType.StoredProcedure));
        var taskQueueId = insertParameters.Get<int>("@task_queue_id");

        if (status != "Queued")
        {
            await connection.ExecuteAsync(
                "UPDATE dbo.task_queue SET status = @status, completed_at = SYSUTCDATETIME() WHERE task_queue_id = @id",
                new { status, id = taskQueueId });
        }

        return taskQueueId;
    }

    private sealed record TaskQueueSnapshot(string Status, DateTime? CompletedAt, string? ResultDetail, string? RequestParameters);

    private async Task<TaskQueueSnapshot> ReadSnapshotAsync(SqlConnection connection, int taskQueueId) =>
        await connection.QuerySingleAsync<TaskQueueSnapshot>(
            "SELECT status AS Status, completed_at AS CompletedAt, result_detail AS ResultDetail, " +
            "request_parameters AS RequestParameters FROM dbo.task_queue WHERE task_queue_id = @id",
            new { id = taskQueueId });

    // ---- SC-1: a Failed row restarts into exactly one new linked Queued row, original untouched ----

    [Fact(Skip = SkipReason)]
    public async Task Test_SC1_RestartCreatesLinkedQueuedRowWithoutMutatingOriginal()
    {
        // Arrange
        int originalTaskId;
        TaskQueueSnapshot originalBefore;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            originalTaskId = await InsertTaskAsync(setupConnection, "Failed", """{"keyword":"invoice"}""", _seedUserId);
            await setupConnection.ExecuteAsync(
                "UPDATE dbo.task_queue SET result_detail = @detail WHERE task_queue_id = @id",
                new { detail = "B2 API timeout after 3 retries", id = originalTaskId });
            originalBefore = await ReadSnapshotAsync(setupConnection, originalTaskId);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var result = await repository.RestartAsync(
            originalTaskId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        result.NewTaskQueueId.Should().NotBeNull().And.NotBe(originalTaskId);

        await using var assertConnection = new SqlConnection(_connectionString);
        var newRow = await assertConnection.QuerySingleAsync<(string status, int? restartedFromTaskQueueId, string taskType, string? requestParameters)>(
            "SELECT status, restarted_from_task_queue_id AS restartedFromTaskQueueId, task_type AS taskType, " +
            "request_parameters AS requestParameters FROM dbo.task_queue WHERE task_queue_id = @id",
            new { id = result.NewTaskQueueId });
        newRow.status.Should().Be("Queued");
        newRow.restartedFromTaskQueueId.Should().Be(originalTaskId);
        newRow.taskType.Should().Be("KeywordSearch");
        newRow.requestParameters.Should().Be("""{"keyword":"invoice"}""");

        var originalAfter = await ReadSnapshotAsync(assertConnection, originalTaskId);
        originalAfter.Status.Should().Be(originalBefore.Status);
        originalAfter.CompletedAt.Should().Be(originalBefore.CompletedAt);
        originalAfter.ResultDetail.Should().Be(originalBefore.ResultDetail);

        var linkedRowCount = await assertConnection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.task_queue WHERE restarted_from_task_queue_id = @id",
            new { id = originalTaskId });
        linkedRowCount.Should().Be(1);
    }

    // ---- SC-3/V1: only a Failed row is restartable — every other status is rejected, no new row ----

    [Theory(Skip = SkipReason)]
    [InlineData("Queued")]
    [InlineData("Running")]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task Test_SC3_RestartRejectedForNonFailedStatuses(string status)
    {
        // Arrange
        int taskId;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            taskId = await InsertTaskAsync(setupConnection, status, requestParameters: null, _seedUserId);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var result = await repository.RestartAsync(
            taskId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.NotRestartable);
        result.NewTaskQueueId.Should().BeNull();

        await using var assertConnection = new SqlConnection(_connectionString);
        var linkedRowCount = await assertConnection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.task_queue WHERE restarted_from_task_queue_id = @id",
            new { id = taskId });
        linkedRowCount.Should().Be(0);
    }

    // ---- V4: the new row's request_parameters is byte-for-byte identical to the original's ---------

    [Fact(Skip = SkipReason)]
    public async Task Test_V4_RestartCopiesOriginalRequestParametersExactly()
    {
        // Arrange
        const string originalRequestParameters = """{"keyword":"invoice","caseSensitive":false,"scope":["contracts","invoices"]}""";
        int originalTaskId;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            originalTaskId = await InsertTaskAsync(setupConnection, "Failed", originalRequestParameters, _seedUserId);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var result = await repository.RestartAsync(
            originalTaskId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        await using var assertConnection = new SqlConnection(_connectionString);
        var newRequestParameters = await assertConnection.QuerySingleAsync<string?>(
            "SELECT request_parameters FROM dbo.task_queue WHERE task_queue_id = @id",
            new { id = result.NewTaskQueueId });
        newRequestParameters.Should().Be(originalRequestParameters);
    }

    // ---- SC-5/V5: a non-owner cannot restart another user's task, in any status, no new row ---------

    [Fact(Skip = SkipReason)]
    public async Task Test_SC5_RestartRejectedForNonOwningUser()
    {
        // Arrange — task submitted by _seedUserId; restart attempted by a different user.
        int taskId;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            taskId = await InsertTaskAsync(setupConnection, "Failed", requestParameters: null, _seedUserId);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var result = await repository.RestartAsync(
            taskId, _otherUserId, "test-fixture", "test-fixture", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.NotOwner);
        result.NewTaskQueueId.Should().BeNull();

        await using var assertConnection = new SqlConnection(_connectionString);
        var linkedRowCount = await assertConnection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.task_queue WHERE restarted_from_task_queue_id = @id",
            new { id = taskId });
        linkedRowCount.Should().Be(0);
    }

    // ---- §11: ownership is checked before task state, so a non-owner never learns the task's status

    [Fact(Skip = SkipReason)]
    public async Task Test_NotOwnerPrecedesNotFailed_ResponseHidesTaskStatusFromNonOwner()
    {
        // Arrange — task belongs to _seedUserId and is NOT Failed (Completed), so if the state
        // check ran first a non-owner restart attempt would surface NotRestartable instead of
        // NotOwner. §7/§11 requires NotOwner to win regardless.
        int taskId;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            taskId = await InsertTaskAsync(setupConnection, "Completed", requestParameters: null, _seedUserId);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var result = await repository.RestartAsync(
            taskId, _otherUserId, "test-fixture", "test-fixture", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.NotOwner);
    }

    // =================================================================================================
    // CAS-054 - resume from watermark. Same skip convention as above; requires the CAS-054 migration
    // (20261006_150000__CAS-054-keyword-search-resume-watermark.sql) deployed.
    // =================================================================================================

    private async Task<int> SeedFileIndexAsync(SqlConnection connection)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@source", "B2");
        parameters.Add("@bucket_name", "cachet-archive-prod");
        var relativePath = $"cas054/{Guid.NewGuid():N}.txt";
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

    /// <summary>Seeds <paramref name="count"/> file_index rows; ids are ascending (identity column).</summary>
    private async Task<int[]> SeedFileIndexRowsAsync(SqlConnection connection, int count)
    {
        var ids = new int[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = await SeedFileIndexAsync(connection);
        }

        return ids;
    }

    private async Task AddSearchResultAsync(int taskQueueId, int fileIndexId, string matchType = "Content", string? snippet = "...pioneer...")
    {
        var repository = new SearchResultRepository(BuildRepositoryConfiguration());
        await repository.InsertAsync(
            "test-fixture", "test-fixture", taskQueueId, fileIndexId, matchType, snippet,
            matchType == "Content" ? "Needs Review" : "Flagged", CancellationToken.None);
    }

    /// <summary>Inserts a Failed row of <paramref name="taskType"/> with the given watermark and
    /// last-progress age (hours ago), via direct UPDATE for setup.</summary>
    private async Task<int> InsertFailedTaskWithCheckpointAsync(
        SqlConnection connection, string taskType, int? watermark, double progressAgeHours, string? requestParameters = null)
    {
        var insertParameters = new DynamicParameters();
        insertParameters.Add("@created_by", "test-fixture");
        insertParameters.Add("@updated_by", "test-fixture");
        insertParameters.Add("@task_type", taskType);
        insertParameters.Add("@submitted_by_user_id", _seedUserId);
        insertParameters.Add("@request_parameters", requestParameters ?? """{"Term":"pioneer"}""");
        insertParameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", insertParameters, commandType: CommandType.StoredProcedure));
        var id = insertParameters.Get<int>("@task_queue_id");

        await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET status = N'Failed', completed_at = SYSUTCDATETIME(), " +
            "resume_watermark_file_index_id = @watermark, " +
            "progress_updated_at = DATEADD(MINUTE, @minutesAgoNegative, SYSUTCDATETIME()) WHERE task_queue_id = @id",
            new { watermark, minutesAgoNegative = -(int)(progressAgeHours * 60), id });
        return id;
    }

    private sealed record NewRowSnapshot(int? Watermark, string Status, int? RestartedFrom, string? RequestParameters);

    private static Task<NewRowSnapshot> ReadNewRowAsync(SqlConnection connection, int? taskQueueId) =>
        connection.QuerySingleAsync<NewRowSnapshot>(
            "SELECT resume_watermark_file_index_id AS Watermark, status AS Status, " +
            "restarted_from_task_queue_id AS RestartedFrom, request_parameters AS RequestParameters " +
            "FROM dbo.task_queue WHERE task_queue_id = @id",
            new { id = taskQueueId });

    private static Task<List<int>> ReadResultFileIndexIdsAsync(SqlConnection connection, int? taskQueueId) =>
        connection.QueryAsync<int>(
            "SELECT file_index_id FROM dbo.search_result WHERE task_queue_id = @id ORDER BY file_index_id",
            new { id = taskQueueId }).ContinueWith(t => t.Result.ToList());

    // ---- V2: restart carries the watermark and the matches at or below it, atomically ----------------

    [Fact(Skip = SkipReason)]
    public async Task Test_V2_RestartCarriesWatermarkAndResultsAtomically()
    {
        // Arrange - original has matches for f1..f4; W = f2.
        int originalId;
        int[] files;
        await using (var setup = new SqlConnection(_connectionString))
        {
            files = await SeedFileIndexRowsAsync(setup, 4);
            originalId = await InsertFailedTaskWithCheckpointAsync(setup, "KeywordSearch", watermark: files[1], progressAgeHours: 1);
            foreach (var f in files)
            {
                await AddSearchResultAsync(originalId, f);
            }
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var result = await repository.RestartAsync(
            originalId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None, resumeMaxAgeHours: 24);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        result.ResumedFromFileIndexId.Should().Be(files[1]);
        result.CarriedOverCount.Should().Be(2);
        result.ResumeNote.Should().Be("Resumed");

        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadNewRowAsync(assertConnection, result.NewTaskQueueId)).Watermark.Should().Be(files[1]);
        (await ReadResultFileIndexIdsAsync(assertConnection, result.NewTaskQueueId)).Should().Equal(files[0], files[1]);
        (await ReadResultFileIndexIdsAsync(assertConnection, originalId)).Should().Equal(files, "the original's rows are never touched");
    }

    // ---- V6: no watermark / non-Keyword task behaves exactly as before CAS-054 -----------------------

    [Theory(Skip = SkipReason)]
    [InlineData("KeywordSearch", false, """{"Term":"pioneer"}""")]                 // NULL watermark
    [InlineData("ContentScan", true, """{"Term":"pioneer"}""")]                    // non-Keyword, watermark set
    [InlineData("KeywordSearch", false, """{"Term":"pioneer","Mode":"Filename"}""")] // Filename mode never writes one
    public async Task Test_V6_RestartWithoutWatermarkBehavesAsBefore(string taskType, bool watermarkSet, string requestParameters)
    {
        // Arrange
        int originalId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 2);
            originalId = await InsertFailedTaskWithCheckpointAsync(
                setup, taskType, watermarkSet ? files[1] : null, progressAgeHours: 1, requestParameters);
            await AddSearchResultAsync(originalId, files[0]);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var result = await repository.RestartAsync(
            originalId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None, resumeMaxAgeHours: 24);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        result.ResumedFromFileIndexId.Should().BeNull();
        result.CarriedOverCount.Should().Be(0);
        result.ResumeNote.Should().Be("NoCheckpoint");

        await using var assertConnection = new SqlConnection(_connectionString);
        var newRow = await ReadNewRowAsync(assertConnection, result.NewTaskQueueId);
        newRow.Watermark.Should().BeNull();
        newRow.Status.Should().Be("Queued");
        newRow.RequestParameters.Should().Be(requestParameters);
        (await ReadResultFileIndexIdsAsync(assertConnection, result.NewTaskQueueId)).Should().BeEmpty();
    }

    // ---- SC-2: after the sweep marks the task Failed, restart inherits its last persisted watermark ---

    [Fact(Skip = SkipReason)]
    public async Task Test_SC2_RestartAfterSweepInheritsWatermark()
    {
        // Arrange - a Running KeywordSearch task persisted watermark 777 before the "crash".
        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());
        int originalId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            originalId = await InsertTaskAsync(setup, "Queued", """{"Term":"pioneer"}""", _seedUserId);
            await setup.ExecuteAsync(
                "UPDATE dbo.task_queue SET status = N'Running', started_at = SYSUTCDATETIME(), " +
                "progress_updated_at = SYSUTCDATETIME() WHERE task_queue_id = @id", new { id = originalId });
        }

        (await repository.UpdateResumeWatermarkAsync(originalId, "worker-svc", 777, CancellationToken.None)).Should().BeTrue();
        await repository.SweepInterruptedAsync("worker-svc", "Interrupted by a service restart.", CancellationToken.None);

        // Act
        var result = await repository.RestartAsync(
            originalId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None, resumeMaxAgeHours: 24);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        result.ResumedFromFileIndexId.Should().Be(777);
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadNewRowAsync(assertConnection, result.NewTaskQueueId)).Watermark.Should().Be(777);
    }

    // ---- SC-3: restarted results = original at/below W + re-checked above W, no duplicates -------------

    [Fact(Skip = SkipReason)]
    public async Task Test_SC3_RestartedTaskResultsEqualOriginalBelowWatermarkPlusRecheckedAbove()
    {
        // Arrange - original matched f1, f2, f4 (f4 finished out of order, above W = f2).
        int originalId;
        int[] files;
        await using (var setup = new SqlConnection(_connectionString))
        {
            files = await SeedFileIndexRowsAsync(setup, 4);
            originalId = await InsertFailedTaskWithCheckpointAsync(setup, "KeywordSearch", watermark: files[1], progressAgeHours: 1);
            foreach (var f in new[] { files[0], files[1], files[3] })
            {
                await AddSearchResultAsync(originalId, f);
            }
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());
        var result = await repository.RestartAsync(
            originalId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None, resumeMaxAgeHours: 24);

        // Act - the resumed Worker re-checks everything above W: f4 matches again, f3 does not.
        await AddSearchResultAsync(result.NewTaskQueueId!.Value, files[3]);

        // Assert - no unique-key conflict, no duplicates, exactly f1, f2 (carried) + f4 (re-checked).
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadResultFileIndexIdsAsync(assertConnection, result.NewTaskQueueId)).Should().Equal(files[0], files[1], files[3]);
        var duplicates = await assertConnection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM (SELECT file_index_id, match_type FROM dbo.search_result " +
            "WHERE task_queue_id = @id GROUP BY file_index_id, match_type HAVING COUNT(*) > 1) d",
            new { id = result.NewTaskQueueId });
        duplicates.Should().Be(0);
    }

    // ---- SC-7: restart of a task with no watermark / non-Keyword produces the same row and results ----

    [Fact(Skip = SkipReason)]
    public async Task Test_SC7_RestartWithoutWatermarkUnchanged()
    {
        // Arrange - a KeywordSearch Failed row with results but NULL watermark (pre-CAS-054 row).
        const string originalRequestParameters = """{"Term":"pioneer","Bucket":"cachet-archive-prod","Extension":"All"}""";
        int originalId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 2);
            originalId = await InsertFailedTaskWithCheckpointAsync(
                setup, "KeywordSearch", watermark: null, progressAgeHours: 1, originalRequestParameters);
            await AddSearchResultAsync(originalId, files[0]);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act - also covers an old caller that omits the new optional argument entirely.
        var result = await repository.RestartAsync(
            originalId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        await using var assertConnection = new SqlConnection(_connectionString);
        var newRow = await ReadNewRowAsync(assertConnection, result.NewTaskQueueId);
        newRow.Status.Should().Be("Queued");
        newRow.RestartedFrom.Should().Be(originalId);
        newRow.RequestParameters.Should().Be(originalRequestParameters);
        newRow.Watermark.Should().BeNull();
        (await ReadResultFileIndexIdsAsync(assertConnection, result.NewTaskQueueId)).Should().BeEmpty();
    }

    // ---- V7: resume applies only within Worker:ResumeMaxAgeHours of the last progress write ------------

    [Theory(Skip = SkipReason)]
    [InlineData(30.0, 24, false)]   // older than the limit -> full restart
    [InlineData(23.0, 24, true)]    // inside the limit -> resumes
    [InlineData(300.0, null, true)] // NULL limit = no age limit (direct SP callers)
    public async Task Test_V7_RestartOlderThanLimitIsFullRestart(double progressAgeHours, int? maxAgeHours, bool expectResumed)
    {
        // Arrange
        int originalId;
        int[] files;
        await using (var setup = new SqlConnection(_connectionString))
        {
            files = await SeedFileIndexRowsAsync(setup, 2);
            originalId = await InsertFailedTaskWithCheckpointAsync(setup, "KeywordSearch", watermark: files[1], progressAgeHours);
            await AddSearchResultAsync(originalId, files[0]);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var result = await repository.RestartAsync(
            originalId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None, maxAgeHours);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        await using var assertConnection = new SqlConnection(_connectionString);
        var newRow = await ReadNewRowAsync(assertConnection, result.NewTaskQueueId);
        var carried = await ReadResultFileIndexIdsAsync(assertConnection, result.NewTaskQueueId);
        if (expectResumed)
        {
            result.ResumeNote.Should().Be("Resumed");
            newRow.Watermark.Should().Be(files[1]);
            carried.Should().Equal(files[0]);
        }
        else
        {
            result.ResumeNote.Should().Be("CheckpointTooOld");
            result.ResumedFromFileIndexId.Should().BeNull();
            result.CarriedOverCount.Should().Be(0);
            newRow.Watermark.Should().BeNull();
            carried.Should().BeEmpty();
        }
    }

    // ---- SC-11: restart beyond the max age starts from candidate 1; the audit detail says so -----------

    [Fact(Skip = SkipReason)]
    public async Task Test_SC11_RestartBeyondMaxAgeIsFullRestart()
    {
        // Arrange - last progress 30 h ago, limit 24 h; real repository under the real service.
        int originalId;
        await using (var setup = new SqlConnection(_connectionString))
        {
            var files = await SeedFileIndexRowsAsync(setup, 2);
            originalId = await InsertFailedTaskWithCheckpointAsync(setup, "KeywordSearch", watermark: files[1], progressAgeHours: 30);
            await AddSearchResultAsync(originalId, files[0]);
        }

        var auditLog = Substitute.For<Cachet.ArchiveSync.Core.Interfaces.IAuditLogService>();
        var service = new Cachet.ArchiveSync.Api.Services.TaskManagerService(
            new TaskQueueRepository(BuildRepositoryConfiguration()),
            auditLog,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Cachet.ArchiveSync.Api.Services.TaskManagerService>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:ResumeMaxAgeHours"] = "24" })
                .Build());

        // Act
        var result = await service.RestartAsync(originalId, _seedUserId, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        result.ResumedFromFileIndexId.Should().BeNull();
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadNewRowAsync(assertConnection, result.NewTaskQueueId)).Watermark.Should().BeNull();
        (await ReadResultFileIndexIdsAsync(assertConnection, result.NewTaskQueueId)).Should().BeEmpty();
        await auditLog.Received(1).WriteAsync(
            Arg.Is<Cachet.ArchiveSync.Core.Dtos.AuditLogEntry>(e => e.ResultDetail == "Full restart (checkpoint older than 24 h)"),
            Arg.Any<CancellationToken>());
    }

    // =================================================================================================
    // CAS-055 - manual restart after a suppressed (cap-reached) auto-resume. Requires the CAS-054 and
    // CAS-055 migrations deployed. Same skip convention as above.
    // =================================================================================================

    // ---- CAS-055 SC-5 / V3: manual restart of a suppressed task resumes per CAS-054 and resets the count

    [Fact(Skip = SkipReason)]
    public async Task Test_SC5_ManualRestartAfterSuppressionResetsCount()
    {
        // Arrange - a Running task at the cap (count 3) is swept with the cap 3 -> suppressed, left Failed.
        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());
        int originalId;
        int[] files;
        await using (var setup = new SqlConnection(_connectionString))
        {
            files = await SeedFileIndexRowsAsync(setup, 3);
            originalId = await InsertTaskAsync(setup, "Queued", """{"Term":"pioneer"}""", _seedUserId);
            await setup.ExecuteAsync(
                "UPDATE dbo.task_queue SET status = N'Running', started_at = SYSUTCDATETIME(), " +
                "progress_updated_at = SYSUTCDATETIME(), resume_watermark_file_index_id = @w, auto_resume_count = 3 " +
                "WHERE task_queue_id = @id", new { w = files[1], id = originalId });
            await AddSearchResultAsync(originalId, files[0]);
        }

        var swept = await repository.SweepInterruptedAsync(
            "worker-svc", "Interrupted by a service restart - please restart or resubmit this task.",
            CancellationToken.None, 24, 3);
        swept.Single(s => s.TaskQueueId == originalId).AutoResumeSuppressed.Should().BeTrue();

        // Act
        var result = await repository.RestartAsync(
            originalId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None, resumeMaxAgeHours: 24);

        // Assert - resumed per CAS-054 and the lineage count is reset to 0.
        result.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        result.ResumedFromFileIndexId.Should().Be(files[1]);
        await using var assertConnection = new SqlConnection(_connectionString);
        (await ReadNewRowAsync(assertConnection, result.NewTaskQueueId)).Watermark.Should().Be(files[1]);
        (await assertConnection.QuerySingleAsync<int>(
            "SELECT auto_resume_count FROM dbo.task_queue WHERE task_queue_id = @id",
            new { id = result.NewTaskQueueId })).Should().Be(0);
        (await ReadResultFileIndexIdsAsync(assertConnection, result.NewTaskQueueId)).Should().Equal(files[0]);
    }

    public void Dispose()
    {
        // Real run: clean up any inserted task_queue test rows here.
    }
}
