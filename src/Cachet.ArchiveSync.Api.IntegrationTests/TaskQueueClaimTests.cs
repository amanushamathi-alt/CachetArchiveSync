using System.Data;
using System.Text.RegularExpressions;
using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// CAS-028 real-DB suites (<see cref="TaskQueueClaimTests"/>, <see cref="TaskQueueSweepTests"/>,
/// <see cref="TaskQueueDurabilityTests"/>) share one non-parallel collection: the claim takes the
/// oldest <c>Queued</c> row and the sweep fails <b>every</b> <c>Running</c> row in the database, so
/// these tests must not interleave with each other or with the other claim suites
/// (<see cref="TaskQueueClaimConcurrencyTests"/>, <see cref="SyncTaskQueueClaimConcurrencyTests"/>).
/// <c>DisableParallelization</c> runs this collection on its own, after the parallel collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TaskQueueRealDbCollection
{
    public const string Name = "CAS-028 task_queue real-DB";
}

/// <summary>
/// Real-DB tests for the CAS-028 migration and <c>dbo.task_queue_claim</c>'s FIFO order
/// (CAS-028 §5, §13 "TaskQueueClaimTests" — SC-1, SC-2). Same connection-string / seed-user /
/// Skip convention as <see cref="TaskQueueClaimConcurrencyTests"/> and
/// <see cref="TaskQueueRestartRepositoryTests"/>; rows are seeded via <c>dbo.task_queue_ins</c> and
/// then adjusted directly for setup.
///
/// Skip-marked in this sandbox — same reason as every other CAS-* real-DB suite in this project. Un-skip
/// and run manually against a real instance with the full migration chain (through CAS-028) deployed.
/// </summary>
[Collection(TaskQueueRealDbCollection.Name)]
public sealed class TaskQueueClaimTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-028 migration deployed — not available in this sandbox; run manually against a real " +
        "instance before merging.";

    private const string MigrationFileName = "20260924_090000__CAS-028-task-queue-infrastructure.sql";

    private static readonly string[] Cas026Procedures =
    {
        "task_queue_claim", "task_queue_sweep_interrupted", "worker_instance_lock_acquire", "worker_instance_lock_held"
    };

    private readonly string _connectionString;
    private readonly int _seedUserId;
    private readonly List<int> _createdTaskIds = new();

    public TaskQueueClaimTests()
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

    /// <summary>Inserts a Queued row (dbo.task_queue_ins), then directly sets status/submitted_at for setup.</summary>
    private async Task<int> InsertTaskAsync(SqlConnection connection, string status, DateTime? submittedAtUtc)
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

        await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET status = @status, submitted_at = COALESCE(@submittedAt, submitted_at) " +
            "WHERE task_queue_id = @id",
            new { status, submittedAt = submittedAtUtc, id = taskQueueId });

        return taskQueueId;
    }

    // ---- SC-1: the CAS-028 migration applies cleanly, is idempotent, and creates all four procs ----

    [Fact(Skip = SkipReason)]
    public async Task Test_SC1_Cas026Migration_AppliesIdempotently_AllProcsExist()
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

        // Act — apply, then re-apply (idempotency: CREATE OR ALTER).
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

        // Assert
        await applyTwice.Should().NotThrowAsync();
        var existing = await connection.QueryAsync<string>(
            "SELECT p.name FROM sys.procedures p WHERE SCHEMA_NAME(p.schema_id) = N'dbo' AND p.name IN @names",
            new { names = Cas026Procedures });
        existing.Should().BeEquivalentTo(Cas026Procedures);
    }

    // ---- SC-2: claim takes the oldest Queued row (submitted_at, then task_queue_id) → Running ------

    [Fact(Skip = SkipReason)]
    public async Task Test_SC2_Claim_ReturnsOldestQueuedFirst_AndMarksRunning()
    {
        // Arrange — back-dated rows so they head the FIFO queue regardless of any other Queued rows in
        // the database. Expected order: oldest (C), then the tie A/B broken by task_queue_id (A < B).
        // A Running row older than all of them must be ignored.
        int olderRunningId, tieFirstId, tieSecondId, oldestQueuedId;
        await using (var setupConnection = new SqlConnection(_connectionString))
        {
            olderRunningId = await InsertTaskAsync(setupConnection, "Running", new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            tieFirstId = await InsertTaskAsync(setupConnection, "Queued", new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            tieSecondId = await InsertTaskAsync(setupConnection, "Queued", new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            oldestQueuedId = await InsertTaskAsync(setupConnection, "Queued", new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        }

        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Act
        var first = await repository.ClaimNextAsync("worker-svc", CancellationToken.None);
        var second = await repository.ClaimNextAsync("worker-svc", CancellationToken.None);
        var third = await repository.ClaimNextAsync("worker-svc", CancellationToken.None);

        // Assert — FIFO with deterministic tie-break.
        new[] { first?.TaskQueueId, second?.TaskQueueId, third?.TaskQueueId }
            .Should().Equal(oldestQueuedId, tieFirstId, tieSecondId);
        first!.SubmittedAt.UtcDateTime.Should().Be(new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Utc));

        await using var assertConnection = new SqlConnection(_connectionString);
        var rows = (await assertConnection.QueryAsync<(int id, string status, DateTime? startedAt, string updatedBy)>(
                "SELECT task_queue_id, status, started_at, updated_by FROM dbo.task_queue WHERE task_queue_id IN @ids",
                new { ids = new[] { tieFirstId, tieSecondId, oldestQueuedId } }))
            .ToList();
        rows.Should().HaveCount(3).And.OnlyContain(r => r.status == "Running" && r.startedAt != null && r.updatedBy == "worker-svc");

        var ignored = await assertConnection.QuerySingleAsync<(string status, DateTime? startedAt)>(
            "SELECT status, started_at FROM dbo.task_queue WHERE task_queue_id = @id", new { id = olderRunningId });
        ignored.status.Should().Be("Running");
        ignored.startedAt.Should().BeNull("the claim never touched the pre-existing Running row");
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
