using System.Collections.Concurrent;
using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB concurrency test for <see cref="TaskQueueRepository.ClaimNextAsync"/> /
/// <c>dbo.task_queue_claim</c> (CAS-007 §13 Test Plan "SyncTaskQueueClaimConcurrencyTests" —
/// SC-13). Reuses CAS-003 SC-3/CAS-006 SC-12's <c>READPAST</c>/<c>UPDLOCK</c> claim pattern (see
/// <see cref="TaskQueueClaimConcurrencyTests"/>, first written for CAS-006), now exercised across
/// the four <c>task_type</c> values this spec introduces (<c>ContentScan</c>/<c>SyncCopy</c>/
/// <c>SyncUpdate</c>/<c>SyncPurge</c>) instead of just <c>KeywordSearch</c> — the claim procedure
/// is generic across <c>task_type</c> (CAS-006 §3), so this proves no CAS-007-specific regression
/// in that genericity.
///
/// Skip-marked in this sandbox — same reason and setup convention as
/// <see cref="TaskQueueClaimConcurrencyTests"/> (no live SQL Server instance reachable here). Un-skip
/// and run manually against a real instance with the CAS-007 migration deployed.
/// </summary>
public sealed class SyncTaskQueueClaimConcurrencyTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database — not " +
        "available in this sandbox; run manually against a real instance before merging.";

    private static readonly string[] SyncTaskTypes = { "ContentScan", "SyncCopy", "SyncUpdate", "SyncPurge" };

    private readonly string _connectionString;
    private readonly int _seedUserId;

    public SyncTaskQueueClaimConcurrencyTests()
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

    private async Task<int> InsertQueuedTaskAsync(SqlConnection connection, string taskType)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@task_type", taskType);
        parameters.Add("@submitted_by_user_id", _seedUserId);
        parameters.Add("@request_parameters", (string?)null);
        parameters.Add("@task_queue_id", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", parameters, commandType: System.Data.CommandType.StoredProcedure));
        return parameters.Get<int>("@task_queue_id");
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_SC13_ConcurrentWorkersNeverDoubleClaimSyncTasks()
    {
        // Arrange — seed 5 Queued tasks of each of the four CAS-007 task types (20 total).
        var seededTaskIds = new List<int>();
        await using (var seedConnection = new SqlConnection(_connectionString))
        {
            foreach (var taskType in SyncTaskTypes)
            {
                for (var i = 0; i < 5; i++)
                {
                    seededTaskIds.Add(await InsertQueuedTaskAsync(seedConnection, taskType));
                }
            }
        }

        var claimedTaskIds = new ConcurrentBag<int>();

        // Act — two "Worker instances" claiming concurrently until the seeded queue is exhausted
        // (each repository instance owns its own SqlConnection per call, matching the real
        // TaskQueueRepository implementation — no shared connection between the two loops).
        async Task ClaimLoopAsync(string workerIdentity)
        {
            var repository = new TaskQueueRepository(BuildRepositoryConfiguration());
            while (true)
            {
                var claimed = await repository.ClaimNextAsync(workerIdentity, CancellationToken.None);
                if (claimed is null)
                {
                    break;
                }

                claimedTaskIds.Add(claimed.TaskQueueId);
            }
        }

        await Task.WhenAll(
            Task.Run(() => ClaimLoopAsync("worker-instance-1")),
            Task.Run(() => ClaimLoopAsync("worker-instance-2")));

        // Assert — every seeded task, across all four task types, was claimed exactly once; no
        // task_queue_id appears twice across the two concurrent claim loops.
        var claimedSeededIds = claimedTaskIds.Where(seededTaskIds.Contains).ToList();
        claimedSeededIds.Should().OnlyHaveUniqueItems();
        claimedSeededIds.Should().BeEquivalentTo(seededTaskIds);
    }

    public void Dispose()
    {
        // Real run: clean up any inserted task_queue test rows here.
    }
}
