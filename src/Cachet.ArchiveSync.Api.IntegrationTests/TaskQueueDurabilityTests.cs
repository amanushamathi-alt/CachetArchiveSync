using System.Text.Json;
using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Core.Dtos;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Real-DB end-to-end durability test (CAS-028 §3.4, §3.8 T1→T2→T5→T6; §13 "TaskQueueDurabilityTests"
/// — SC-9): enqueue → claim → abandon (simulated Worker crash: nothing writes a terminal status) →
/// startup sweep → <c>Failed</c> → CAS-016 restart → the new <c>Queued</c> row is claimable. Drives
/// the real <see cref="TaskQueueRepository"/> methods the Api and Worker call, against the real
/// procedures. Same connection-string / seed-user / Skip convention as
/// <see cref="TaskQueueRestartRepositoryTests"/>.
///
/// Skip-marked in this sandbox — same reason as every other CAS-* real-DB suite in this project. Un-skip
/// and run manually against a real instance with the CAS-016 and CAS-028 migrations deployed.
/// </summary>
[Collection(TaskQueueRealDbCollection.Name)]
public sealed class TaskQueueDurabilityTests : IDisposable
{
    private const string SkipReason =
        "Requires a live SQL Server instance against the CachetArchiveSync database, with the " +
        "CAS-016 and CAS-028 migrations deployed — not available in this sandbox; run manually " +
        "against a real instance before merging.";

    /// <summary>The fixed text the Worker passes (CAS-028 §3.4; <c>Worker.InterruptedByRestartReason</c>).</summary>
    private const string InterruptedReason =
        "Interrupted by a service restart - please restart or resubmit this task.";

    /// <summary>
    /// Back-dated submitted_at so the test's row heads the FIFO claim queue regardless of any other
    /// Queued rows already in a shared test database (setup only — the claim order itself is SC-2).
    /// </summary>
    private static readonly DateTime HeadOfQueueUtc = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _connectionString;
    private readonly int _seedUserId;
    private readonly List<int> _createdTaskIds = new();

    public TaskQueueDurabilityTests()
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

    private async Task MoveToHeadOfQueueAsync(int taskQueueId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET submitted_at = @at WHERE task_queue_id = @id",
            new { at = HeadOfQueueUtc, id = taskQueueId });
    }

    private async Task<(string status, string? resultDetail, int? restartedFrom)> ReadRowAsync(int taskQueueId)
    {
        await using var connection = new SqlConnection(_connectionString);
        return await connection.QuerySingleAsync<(string, string?, int?)>(
            "SELECT status, result_detail, restarted_from_task_queue_id FROM dbo.task_queue WHERE task_queue_id = @id",
            new { id = taskQueueId });
    }

    [Fact(Skip = SkipReason)]
    public async Task Test_SC9_CrashedTask_SweptToFailed_ThenRestartable()
    {
        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // T1 — enqueue (the Api submission path's repository call).
        var originalId = await repository.InsertAsync(
            "test-fixture", "test-fixture", "KeywordSearch", _seedUserId,
            """{"keyword":"cas026-durability"}""", CancellationToken.None);
        _createdTaskIds.Add(originalId);
        await MoveToHeadOfQueueAsync(originalId);

        // T2 — a Worker slot claims it.
        var claimed = await repository.ClaimNextAsync("worker-svc", CancellationToken.None);
        claimed.Should().NotBeNull();
        claimed!.TaskQueueId.Should().Be(originalId);

        // Simulated crash — the process dies mid-task: no terminal status is ever written.
        (await ReadRowAsync(originalId)).status.Should().Be("Running");

        // T5 — the next Worker start sweeps the orphan.
        var swept = await repository.SweepInterruptedAsync("worker-svc", InterruptedReason, CancellationToken.None);
        swept.Should().Contain(s => s.TaskQueueId == originalId);

        var afterSweep = await ReadRowAsync(originalId);
        afterSweep.status.Should().Be("Failed");
        using (var document = JsonDocument.Parse(afterSweep.resultDetail!))
        {
            document.RootElement.GetProperty("FailureReason").GetString().Should().Be(InterruptedReason);
        }

        await using (var auditConnection = new SqlConnection(_connectionString))
        {
            (await auditConnection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM dbo.audit_log WHERE action = N'TaskInterruptedByRestart' AND task_queue_id = @id",
                new { id = originalId })).Should().Be(1);
        }

        // T6 — CAS-016 restart now reaches the crashed task.
        var restart = await repository.RestartAsync(
            originalId, _seedUserId, "test-fixture", "test-fixture", CancellationToken.None);
        restart.Outcome.Should().Be(RestartTaskOutcome.Restarted);
        restart.NewTaskQueueId.Should().NotBeNull();
        var newId = restart.NewTaskQueueId!.Value;
        _createdTaskIds.Add(newId);

        var newRow = await ReadRowAsync(newId);
        newRow.status.Should().Be("Queued");
        newRow.restartedFrom.Should().Be(originalId);
        (await ReadRowAsync(originalId)).status.Should().Be("Failed", "the original row is never mutated by a restart");

        // The new row is claimable.
        await MoveToHeadOfQueueAsync(newId);
        var reclaimed = await repository.ClaimNextAsync("worker-svc", CancellationToken.None);
        reclaimed.Should().NotBeNull();
        reclaimed!.TaskQueueId.Should().Be(newId);
        reclaimed.RequestParameters.Should().Be("""{"keyword":"cas026-durability"}""");
        (await ReadRowAsync(newId)).status.Should().Be("Running");
    }

    public void Dispose()
    {
        if (_createdTaskIds.Count == 0 || string.IsNullOrEmpty(_connectionString))
        {
            return;
        }

        // Leave nothing claimable/sweepable behind: this test's non-terminal rows → Cancelled.
        using var connection = new SqlConnection(_connectionString);
        connection.Execute(
            "UPDATE dbo.task_queue SET status = N'Cancelled', completed_at = SYSUTCDATETIME() " +
            "WHERE task_queue_id IN @ids AND status IN (N'Queued', N'Running')",
            new { ids = _createdTaskIds });
    }
}
