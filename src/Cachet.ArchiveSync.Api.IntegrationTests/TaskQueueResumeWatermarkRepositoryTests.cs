using System.Data;
using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB tests for <see cref="TaskQueueRepository.UpdateResumeWatermarkAsync"/> /
/// <c>dbo.task_queue_resume_watermark_upd</c> (CAS-054 §7/§9 V1, V3/§13). Same skip convention and
/// connection/seed-user setup as <see cref="TaskQueueRestartRepositoryTests"/>; un-skip and run
/// against a real instance with the CAS-054 migration
/// (<c>20261006_150000__CAS-054-keyword-search-resume-watermark.sql</c>) deployed.
/// </summary>
public sealed class TaskQueueResumeWatermarkRepositoryTests
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-054 migration deployed - not available in this sandbox; run manually against a real " +
        "instance before merging.";

    private readonly string _connectionString;
    private readonly int _seedUserId;

    public TaskQueueResumeWatermarkRepositoryTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.IntegrationTests.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        _connectionString = configuration.GetConnectionString("CachetArchiveSync") ?? string.Empty;
        _seedUserId = int.TryParse(configuration["IntegrationTests:SeedUserId"], out var id) ? id : 1;
    }

    private TaskQueueRepository CreateRepository() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = _connectionString })
            .Build());

    private async Task<int> InsertTaskAsync(SqlConnection connection, string taskType = "KeywordSearch")
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@task_type", taskType);
        parameters.Add("@submitted_by_user_id", _seedUserId);
        parameters.Add("@request_parameters", """{"Term":"pioneer"}""");
        parameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", parameters, commandType: CommandType.StoredProcedure));
        return parameters.Get<int>("@task_queue_id");
    }

    private static Task SetStatusAsync(SqlConnection connection, int taskQueueId, string status) =>
        connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET status = @status WHERE task_queue_id = @id", new { status, id = taskQueueId });

    private static Task<int?> ReadWatermarkAsync(SqlConnection connection, int taskQueueId) =>
        connection.QuerySingleAsync<int?>(
            "SELECT resume_watermark_file_index_id FROM dbo.task_queue WHERE task_queue_id = @id", new { id = taskQueueId });

    // ---- V1: @task_queue_id, @updated_by and a positive watermark are required -----------------------

    [Fact(Skip = SkipReason)]
    public async Task Test_V1_ResumeWatermarkUpd_RejectsMissingArguments()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var taskId = await InsertTaskAsync(connection);
        await SetStatusAsync(connection, taskId, "Running");
        var repository = CreateRepository();

        // Act / Assert - non-positive watermark and blank updated_by are rejected by the procedure.
        var zero = async () => await repository.UpdateResumeWatermarkAsync(taskId, "worker-svc", 0, CancellationToken.None);
        var negative = async () => await repository.UpdateResumeWatermarkAsync(taskId, "worker-svc", -5, CancellationToken.None);
        var blankUser = async () => await repository.UpdateResumeWatermarkAsync(taskId, " ", 10, CancellationToken.None);
        await zero.Should().ThrowAsync<SqlException>();
        await negative.Should().ThrowAsync<SqlException>();
        await blankUser.Should().ThrowAsync<SqlException>();

        // Act / Assert - explicit NULLs (a caller outside the typed repository).
        foreach (var (id, user, watermark) in new (int?, string?, int?)[] { (null, "worker-svc", 10), (taskId, null, 10), (taskId, "worker-svc", null) })
        {
            var nullCall = async () => await connection.ExecuteAsync(new CommandDefinition(
                "dbo.task_queue_resume_watermark_upd",
                new { task_queue_id = id, updated_by = user, resume_watermark_file_index_id = watermark },
                commandType: CommandType.StoredProcedure));
            await nullCall.Should().ThrowAsync<SqlException>();
        }

        (await ReadWatermarkAsync(connection, taskId)).Should().BeNull("no rejected call may write");
    }

    // ---- V3: monotonic, and only while Running ---------------------------------------------------------

    [Fact(Skip = SkipReason)]
    public async Task Test_V3_WatermarkMonotonicAndOnlyWhileRunning()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var taskId = await InsertTaskAsync(connection);
        var repository = CreateRepository();

        // Act / Assert - Queued: not written.
        (await repository.UpdateResumeWatermarkAsync(taskId, "worker-svc", 10, CancellationToken.None)).Should().BeFalse();
        (await ReadWatermarkAsync(connection, taskId)).Should().BeNull();

        // Running: first write, newer write succeed; stale / equal writes are no-ops.
        await SetStatusAsync(connection, taskId, "Running");
        (await repository.UpdateResumeWatermarkAsync(taskId, "worker-svc", 10, CancellationToken.None)).Should().BeTrue();
        (await repository.UpdateResumeWatermarkAsync(taskId, "worker-svc", 5, CancellationToken.None)).Should().BeFalse();
        (await repository.UpdateResumeWatermarkAsync(taskId, "worker-svc", 10, CancellationToken.None)).Should().BeFalse();
        (await ReadWatermarkAsync(connection, taskId)).Should().Be(10);
        (await repository.UpdateResumeWatermarkAsync(taskId, "worker-svc", 20, CancellationToken.None)).Should().BeTrue();
        (await ReadWatermarkAsync(connection, taskId)).Should().Be(20);

        // Failed (after a sweep): frozen.
        await SetStatusAsync(connection, taskId, "Failed");
        (await repository.UpdateResumeWatermarkAsync(taskId, "worker-svc", 30, CancellationToken.None)).Should().BeFalse();
        (await ReadWatermarkAsync(connection, taskId)).Should().Be(20);
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_V3_WatermarkMonotonicAndOnlyWhileRunning_NonKeywordTaskIsNeverWritten()
    {
        // Arrange
        await using var connection = new SqlConnection(_connectionString);
        var taskId = await InsertTaskAsync(connection, "ContentScan");
        await SetStatusAsync(connection, taskId, "Running");

        // Act
        var written = await CreateRepository().UpdateResumeWatermarkAsync(taskId, "worker-svc", 10, CancellationToken.None);

        // Assert
        written.Should().BeFalse();
        (await ReadWatermarkAsync(connection, taskId)).Should().BeNull();
    }
}
