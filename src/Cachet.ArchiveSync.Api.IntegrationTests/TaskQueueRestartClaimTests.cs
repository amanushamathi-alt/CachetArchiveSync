using System.Data;
using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB test proving CAS-016 SC-2 — a row created by <c>dbo.task_queue_restart_ins</c> is
/// claimable by <c>dbo.task_queue_claim</c> exactly like any freshly-submitted row, with no Worker
/// code change required (§2). A <c>TaskQueueClaimConcurrencyTests</c>-style extension per the spec's
/// §13 Test Plan, reusing that class's connection-string/seed-user setup convention rather than
/// duplicating it into a shared base — this project's existing real-DB suites each own their setup
/// inline (see also <c>TaskQueueRestartRepositoryTests</c>).
///
/// Skip-marked in this sandbox — same reason as every other CAS-* real-DB suite in this project.
/// </summary>
public sealed class TaskQueueRestartClaimTests
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-016 migration deployed — not available in this sandbox; run manually against a real " +
        "instance before merging.";

    private readonly string _connectionString;
    private readonly int _seedUserId;

    public TaskQueueRestartClaimTests()
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

    [Fact(Skip = SkipReason)]
    public async Task Test_SC2_RestartedRowClaimableAndProcessableLikeFreshSubmission()
    {
        // Arrange — a Failed KeywordSearch task with a real, deserializable request_parameters
        // payload, then restarted via the repository under test (not raw SQL), so this test exactly
        // exercises the production code path a real restart request would take.
        const string requestParameters = """{"keyword":"invoice","caseSensitive":false}""";
        int originalTaskId;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            var insertParameters = new DynamicParameters();
            insertParameters.Add("@created_by", "test-fixture");
            insertParameters.Add("@updated_by", "test-fixture");
            insertParameters.Add("@task_type", "KeywordSearch");
            insertParameters.Add("@submitted_by_user_id", _seedUserId);
            insertParameters.Add("@request_parameters", requestParameters);
            insertParameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
            await setupConnection.ExecuteAsync(new CommandDefinition(
                "dbo.task_queue_ins", insertParameters, commandType: CommandType.StoredProcedure));
            originalTaskId = insertParameters.Get<int>("@task_queue_id");

            await setupConnection.ExecuteAsync(
                "UPDATE dbo.task_queue SET status = 'Failed', completed_at = SYSUTCDATETIME(), " +
                "result_detail = 'B2 API timeout after 3 retries' WHERE task_queue_id = @id",
                new { id = originalTaskId });
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act — restart, then claim exactly like a Worker's claim loop would.
        var restartResult = await repository.RestartAsync(
            originalTaskId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None);
        restartResult.Outcome.Should().Be(Core.Dtos.RestartTaskOutcome.Restarted);

        var claimed = await repository.ClaimNextAsync("worker-instance-1", CancellationToken.None);

        // Assert — the restarted row is exactly what gets claimed: same task_queue_id, same
        // task_type, same request_parameters, same submitting user, now Running. No Worker code
        // change is exercised here (§2) — this is the same TaskQueueRepository.ClaimNextAsync call
        // any *TaskProcessor.cs uses for a fresh submission.
        claimed.Should().NotBeNull();
        claimed!.TaskQueueId.Should().Be(restartResult.NewTaskQueueId);
        claimed.TaskType.Should().Be("KeywordSearch");
        claimed.RequestParameters.Should().Be(requestParameters);
        claimed.SubmittedByUserId.Should().Be(_seedUserId);

        await using var assertConnection = new SqlConnection(_connectionString);
        var status = await assertConnection.QuerySingleAsync<string>(
            "SELECT status FROM dbo.task_queue WHERE task_queue_id = @id",
            new { id = claimed.TaskQueueId });
        status.Should().Be("Running");
    }
}
