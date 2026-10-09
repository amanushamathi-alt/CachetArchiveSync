using System.Collections.Concurrent;
using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB concurrency test for <see cref="TaskQueueRepository.ClaimNextAsync"/> /
/// <c>dbo.task_queue_claim</c> (CAS-006 §5/§13 Test Plan "TaskQueueClaimConcurrencyTests" — SC-12).
/// Reuses CAS-003 SC-3's <c>READPAST</c>/<c>UPDLOCK</c> claim pattern (see that procedure's own
/// header comment), now exercised end-to-end against a real <c>KeywordSearch</c> task type instead
/// of a synthetic row. No prior concurrency test exists elsewhere in this codebase to extend — this
/// is the first one written against the real claim procedure.
///
/// Skip-marked in this sandbox — same reason and setup convention as the other CAS-006 real-DB
/// suites in this project (no live SQL Server instance reachable here). Un-skip and run manually
/// against a real instance with the CAS-006 migration deployed.
/// </summary>
public sealed class TaskQueueClaimConcurrencyTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database — not " +
        "available in this sandbox; run manually against a real instance before merging.";

    private readonly string _connectionString;
    private readonly int _seedUserId;

    public TaskQueueClaimConcurrencyTests()
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

    private async Task<int> InsertQueuedTaskAsync(SqlConnection connection)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@task_type", "KeywordSearch");
        parameters.Add("@submitted_by_user_id", _seedUserId);
        parameters.Add("@request_parameters", (string?)null);
        parameters.Add("@task_queue_id", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", parameters, commandType: System.Data.CommandType.StoredProcedure));
        return parameters.Get<int>("@task_queue_id");
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_SC12_ConcurrentWorkersNeverDoubleClaimSameTask()
    {
        // Arrange — seed N distinct Queued KeywordSearch tasks.
        const int taskCount = 20;
        var seededTaskIds = new List<int>();
        await using (var seedConnection = new SqlConnection(_connectionString))
        {
            for (var i = 0; i < taskCount; i++)
            {
                seededTaskIds.Add(await InsertQueuedTaskAsync(seedConnection));
            }
        }

        var claimedTaskIds = new ConcurrentBag<int>();

        // Act — two "Worker instances" claiming concurrently until the queue of seeded tasks is
        // exhausted (each repository instance owns its own SqlConnection per call, matching the
        // real TaskQueueRepository implementation — no shared connection between the two loops).
        async Task ClaimLoopAsync(string workerIdentity)
        {
            var repository = new TaskQueueRepository(BuildRepositoryConfiguration());
            while (true)
            {
                var claimed = await repository.ClaimNextAsync(workerIdentity, CancellationToken.None);
                if (claimed is null || !seededTaskIds.Contains(claimed.TaskQueueId))
                {
                    // Either the queue (of tasks this test seeded) is empty, or some other
                    // process's row was claimed — stop this loop either way.
                    if (claimed is null)
                    {
                        break;
                    }

                    claimedTaskIds.Add(claimed.TaskQueueId);
                    continue;
                }

                claimedTaskIds.Add(claimed.TaskQueueId);
            }
        }

        await Task.WhenAll(
            Task.Run(() => ClaimLoopAsync("worker-instance-1")),
            Task.Run(() => ClaimLoopAsync("worker-instance-2")));

        // Assert — every seeded task was claimed exactly once; no task_queue_id appears twice
        // across the two concurrent claim loops (the READPAST/UPDLOCK guarantee SC-12 proves).
        var claimedSeededIds = claimedTaskIds.Where(seededTaskIds.Contains).ToList();
        claimedSeededIds.Should().OnlyHaveUniqueItems();
        claimedSeededIds.Should().BeEquivalentTo(seededTaskIds);
    }

    public void Dispose()
    {
        // Real run: clean up any inserted task_queue test rows here.
    }
}
