using System.Data;
using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Core.Dtos;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB tests for <see cref="TaskQueueRepository.ListAsync"/> / <c>dbo.task_queue_lst</c>
/// (CAS-026 §5/§13 Test Plan "TaskQueueListRepositoryTests" — SC-1, V6). Modeled directly on
/// <see cref="TaskQueueRestartRepositoryTests"/> (CAS-016) — same connection-string/seed-user
/// convention, same "insert via <c>dbo.task_queue_ins</c>, then manipulate directly for setup" style
/// this project's existing real-DB suites use. The "own rows only, newest-first, correctly paginated"
/// guarantee (SC-1) and the "never another user's rows" guarantee (V6) are both enforced inside the
/// stored procedure's own <c>WHERE</c>/<c>ORDER BY</c>/<c>OFFSET-FETCH</c> clauses (§5), not in C#
/// (<see cref="Services.TaskListService"/> only passes parameters through, proven separately by the
/// mocked-repository <c>TaskListServiceTests</c>) — so this is the layer where SC-1/V6 are actually
/// proven, not merely simulated.
///
/// Skip-marked in this sandbox — same reason as every other CAS-* real-DB suite in this project (no
/// live SQL Server instance reachable here). Un-skip and run manually against a real instance with
/// the CAS-026 migration (<c>20260924_150000__CAS-026-task-manager-list-detail-api.sql</c>) deployed.
/// </summary>
public sealed class TaskQueueListRepositoryTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-026 migration deployed — not available in this sandbox; run manually against a real " +
        "instance before merging.";

    private readonly string _connectionString;
    private readonly int _seedUserId;
    private readonly int _otherUserId;

    public TaskQueueListRepositoryTests()
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

    /// <summary>Inserts a Queued row (dbo.task_queue_ins), then directly sets status/submitted_at for
    /// test setup — mirroring <see cref="TaskQueueRestartRepositoryTests"/>'s own InsertTaskAsync
    /// convention of arriving at a desired state via direct UPDATE rather than depending on a worker's
    /// own claim/complete contract here.</summary>
    private async Task<int> InsertTaskAsync(
        SqlConnection connection, string taskType, string status, DateTime submittedAt, int submittedByUserId)
    {
        var insertParameters = new DynamicParameters();
        insertParameters.Add("@created_by", "test-fixture");
        insertParameters.Add("@updated_by", "test-fixture");
        insertParameters.Add("@task_type", taskType);
        insertParameters.Add("@submitted_by_user_id", submittedByUserId);
        insertParameters.Add("@request_parameters", (string?)null);
        insertParameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", insertParameters, commandType: CommandType.StoredProcedure));
        var taskQueueId = insertParameters.Get<int>("@task_queue_id");

        await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET status = @status, submitted_at = @submittedAt WHERE task_queue_id = @id",
            new { status, submittedAt, id = taskQueueId });

        return taskQueueId;
    }

    // ---- SC-1: own tasks, newest-first by submitted_at, correctly paginated --------------------------

    [Fact(Skip = SkipReason)]
    public async Task Test_SC1_ListReturnsOwnTasksNewestFirstPaginated()
    {
        // Arrange — three tasks for the seed user, inserted out of chronological order, plus one
        // task belonging to a different user (must never appear in the seed user's list).
        int oldestId, middleId, newestId;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            middleId = await InsertTaskAsync(setupConnection, "KeywordSearch", "Completed",
                new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc), _seedUserId);
            newestId = await InsertTaskAsync(setupConnection, "ContentScan", "Running",
                new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc), _seedUserId);
            oldestId = await InsertTaskAsync(setupConnection, "SyncCopy", "Failed",
                new DateTime(2026, 9, 18, 6, 0, 0, DateTimeKind.Utc), _seedUserId);
            await InsertTaskAsync(setupConnection, "KeywordSearch", "Completed",
                new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc), _otherUserId);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act — page 1 of 2.
        var (page1Items, page1TotalCount) = await repository.ListAsync(
            _seedUserId, taskType: null, status: null, page: 1, pageSize: 2, CancellationToken.None);

        // Assert — newest-first ordering, correct page size, totalCount reflects the full filtered set.
        page1Items.Should().HaveCount(2);
        page1Items[0].TaskQueueId.Should().Be(newestId);
        page1Items[1].TaskQueueId.Should().Be(middleId);
        page1TotalCount.Should().Be(3);

        // Act — page 2.
        var (page2Items, page2TotalCount) = await repository.ListAsync(
            _seedUserId, taskType: null, status: null, page: 2, pageSize: 2, CancellationToken.None);

        // Assert
        page2Items.Should().ContainSingle(i => i.TaskQueueId == oldestId);
        page2TotalCount.Should().Be(3);
    }

    // ---- V-6: never another user's rows, regardless of filter values supplied ------------------------

    [Fact(Skip = SkipReason)]
    public async Task Test_V6_ListNeverReturnsAnotherUsersTasks()
    {
        // Arrange — same status ("Completed") for both users, so a status filter alone cannot be
        // mistaken for the ownership scoping this test actually proves.
        int seedTaskId;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            seedTaskId = await InsertTaskAsync(setupConnection, "KeywordSearch", "Completed",
                new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc), _seedUserId);
            await InsertTaskAsync(setupConnection, "KeywordSearch", "Completed",
                new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc), _otherUserId);
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act — unfiltered list and status-filtered list, both scoped to the seed user.
        var (unfilteredItems, unfilteredTotalCount) = await repository.ListAsync(
            _seedUserId, taskType: null, status: null, page: 1, pageSize: 50, CancellationToken.None);
        var (filteredItems, filteredTotalCount) = await repository.ListAsync(
            _seedUserId, taskType: null, status: "Completed", page: 1, pageSize: 50, CancellationToken.None);

        // Assert — only the seed user's own row appears, in either call.
        unfilteredItems.Should().OnlyContain(i => i.TaskQueueId == seedTaskId);
        unfilteredTotalCount.Should().Be(1);
        filteredItems.Should().OnlyContain(i => i.TaskQueueId == seedTaskId);
        filteredTotalCount.Should().Be(1);
    }

    public void Dispose()
    {
        // Real run: clean up any inserted task_queue test rows here.
    }
}
