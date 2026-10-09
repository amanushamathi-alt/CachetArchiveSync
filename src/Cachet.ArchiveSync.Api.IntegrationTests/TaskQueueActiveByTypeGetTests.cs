using System.Data;
using System.Text.RegularExpressions;
using Cachet.ArchiveSync.Api.IntegrationTests.TaskManager;
using Cachet.ArchiveSync.Api.Repositories;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// CAS-037 §5/§13 "TaskQueueRepositoryTests" assignment (as this project's own real-DB repository
/// test class, one per new stored procedure — see <see cref="TaskQueueAcknowledgeRepositoryTests"/>
/// for the identical precedent for CAS-030's <c>dbo.task_queue_acknowledge_upd</c>): proves the new
/// <c>dbo.task_queue_active_by_type_get</c> procedure itself (via
/// <see cref="TaskQueueRepository.HasActiveTaskOfTypeAsync"/>) against real LocalDB — that it is
/// <c>true</c> exactly when a <c>B2MirrorSync</c> row is <c>Queued</c> or <c>Running</c>, scoped by
/// <c>task_type</c> (a <c>Queued</c>/<c>Running</c> row of a different type must not count), and
/// <c>false</c> once every such row is terminal. Runs whenever
/// <c>ConnectionStrings:CachetArchiveSync</c> is configured (<see cref="RealDbFactAttribute"/>) —
/// otherwise reported as Skipped, per that attribute's own doc. Applies the CAS-037 migration
/// itself first (idempotent — <c>IF EXISTS</c>/<c>CREATE OR ALTER</c>), same self-priming pattern
/// as <c>TaskQueueProgressTests.Test_SC1_...</c>, so this test does not silently no-op against a
/// shared dev database whose migration chain has not yet been advanced past CAS-030.
/// </summary>
[Collection(TaskQueueRealDbCollection.Name)]
public sealed class TaskQueueActiveByTypeGetTests : IAsyncLifetime
{
    private const string MirrorSyncType = "B2MirrorSync";
    private const string MigrationFileName = "20260928_140000__CAS-037-b2-mirror-population.sql";
    private readonly List<int> _createdTaskIds = new();
    private int _seedUserId;

    private static string ConnectionString => RealDbConfiguration.ConnectionString!;

    private IConfiguration BuildRepositoryConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = ConnectionString })
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

    private static async Task ApplyMigrationAsync(SqlConnection connection)
    {
        var script = (await File.ReadAllTextAsync(Path.Combine(FindRepoRoot(), "Database", "migrations", MigrationFileName)))
            .Replace("USE [CachetArchiveSync];", $"USE [{connection.Database}];");
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(batch => !string.IsNullOrWhiteSpace(batch));
        foreach (var batch in batches)
        {
            await connection.ExecuteAsync(batch);
        }
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await ApplyMigrationAsync(connection);

        _seedUserId = await connection.QuerySingleAsync<int>(
            "INSERT INTO dbo.users (created_by, updated_by, username, password_hash, b2_application_key_id, b2_application_key) " +
            "OUTPUT inserted.user_id VALUES (N'test-fixture', N'test-fixture', @username, N'x', N'ct:keyid', N'ct:key')",
            new { username = "cas037-" + Guid.NewGuid().ToString("N")[..12] });
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString);
        if (_createdTaskIds.Count > 0)
        {
            await connection.ExecuteAsync("DELETE FROM dbo.task_queue WHERE task_queue_id IN @ids", new { ids = _createdTaskIds });
        }
        await connection.ExecuteAsync("DELETE FROM dbo.users WHERE user_id = @id", new { id = _seedUserId });
    }

    private async Task<int> InsertTaskAsync(SqlConnection connection, string taskType, string status)
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
                "UPDATE dbo.task_queue SET status = @status, completed_at = SYSUTCDATETIME() WHERE task_queue_id = @id",
                new { status, id = taskQueueId });
        }

        return taskQueueId;
    }

    // ---- §5 new stored procedure: reflects Queued/Running, scoped by task_type, false once terminal ---

    [RealDbFact]
    public async Task Test_TaskQueueActiveByTypeGet_ReflectsQueuedAndRunning()
    {
        // Arrange
        await using var connection = new SqlConnection(ConnectionString);
        var repository = new TaskQueueRepository(BuildRepositoryConfiguration());

        // Assert — nothing active yet.
        (await repository.HasActiveTaskOfTypeAsync(MirrorSyncType, CancellationToken.None)).Should().BeFalse();

        // Act 1 — a Queued B2MirrorSync row makes it true.
        var queuedId = await InsertTaskAsync(connection, MirrorSyncType, "Queued");
        (await repository.HasActiveTaskOfTypeAsync(MirrorSyncType, CancellationToken.None)).Should().BeTrue();

        // Act 2 — a Queued/Running row of a DIFFERENT task_type must not count (global-per-type scope).
        var otherTypeRunningId = await InsertTaskAsync(connection, "ContentScan", "Running");
        (await repository.HasActiveTaskOfTypeAsync(MirrorSyncType, CancellationToken.None)).Should().BeTrue("only the B2MirrorSync row should be counted");
        (await repository.HasActiveTaskOfTypeAsync("ContentScan", CancellationToken.None)).Should().BeTrue("the ContentScan row is itself Running");

        // Act 3 — moving the B2MirrorSync row to Running keeps it active.
        await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET status = N'Running', started_at = SYSUTCDATETIME() WHERE task_queue_id = @id",
            new { id = queuedId });
        (await repository.HasActiveTaskOfTypeAsync(MirrorSyncType, CancellationToken.None)).Should().BeTrue();

        // Act 4 — completing the only B2MirrorSync row makes it false again, independent of the
        // still-Running ContentScan row.
        await repository.UpdateStatusAsync(queuedId, "test-fixture", "Completed", "{}", CancellationToken.None);
        (await repository.HasActiveTaskOfTypeAsync(MirrorSyncType, CancellationToken.None)).Should().BeFalse();
        (await repository.HasActiveTaskOfTypeAsync("ContentScan", CancellationToken.None)).Should().BeTrue();

        // Cleanup — leave nothing claimable behind for other real-DB suites in the shared collection.
        await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET status = N'Cancelled', completed_at = SYSUTCDATETIME() WHERE task_queue_id = @id",
            new { id = otherTypeRunningId });
    }
}
