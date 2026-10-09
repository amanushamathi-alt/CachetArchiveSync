using System.Data;
using Cachet.ArchiveSync.Api.IntegrationTests.TaskManager;
using Cachet.ArchiveSync.Api.Repositories;
using Cachet.ArchiveSync.Core.Dtos;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// CAS-030 §13 "TaskQueueAcknowledgeRepositoryTests": proves <c>dbo.task_queue_acknowledge_upd</c>
/// itself — not only <see cref="Services.TaskManagerService"/> — rejects a non-owner (§5, §12), via
/// the real <see cref="TaskQueueRepository.AcknowledgeAsync"/> against LocalDB. Runs whenever
/// <c>ConnectionStrings:CachetArchiveSync</c> is configured (<see cref="RealDbFactAttribute"/>).
/// Joins the CAS-028 non-parallel real-DB collection for the same reason as
/// <see cref="TaskManagerControllerTests"/>.
/// </summary>
[Collection(TaskQueueRealDbCollection.Name)]
public sealed class TaskQueueAcknowledgeRepositoryTests : IAsyncLifetime
{
    private readonly List<int> _userIds = new();

    private static string ConnectionString => RealDbConfiguration.ConnectionString!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_userIds.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            "DELETE FROM dbo.task_queue WHERE submitted_by_user_id IN @ids; DELETE FROM dbo.users WHERE user_id IN @ids;",
            new { ids = _userIds });
    }

    private async Task<int> InsertUserAsync(SqlConnection connection)
    {
        var userId = await connection.QuerySingleAsync<int>(
            "INSERT INTO dbo.users (created_by, updated_by, username, password_hash, b2_application_key_id, b2_application_key) " +
            "OUTPUT inserted.user_id VALUES (N'test-fixture', N'test-fixture', @username, N'x', N'ct:keyid', N'ct:key')",
            new { username = "cas030-" + Guid.NewGuid().ToString("N")[..12] });
        _userIds.Add(userId);
        return userId;
    }

    private static async Task<int> InsertCompletedTaskAsync(SqlConnection connection, int userId)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@created_by", "test-fixture");
        parameters.Add("@updated_by", "test-fixture");
        parameters.Add("@task_type", "KeywordSearch");
        parameters.Add("@submitted_by_user_id", userId);
        parameters.Add("@request_parameters", (string?)null);
        parameters.Add("@task_queue_id", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.task_queue_ins", parameters, commandType: CommandType.StoredProcedure));
        var taskId = parameters.Get<int>("@task_queue_id");

        await connection.ExecuteAsync(
            "UPDATE dbo.task_queue SET status = N'Completed', started_at = SYSUTCDATETIME(), completed_at = SYSUTCDATETIME() " +
            "WHERE task_queue_id = @taskId",
            new { taskId });
        return taskId;
    }

    private sealed record AckSnapshot(DateTime? ResultAcknowledgedAt, string UpdatedBy);

    private static Task<AckSnapshot> ReadSnapshotAsync(SqlConnection connection, int taskId) =>
        connection.QuerySingleAsync<AckSnapshot>(
            "SELECT result_acknowledged_at AS ResultAcknowledgedAt, updated_by AS UpdatedBy FROM dbo.task_queue WHERE task_queue_id = @taskId",
            new { taskId });

    [RealDbFact]
    public async Task Test_AcknowledgeUpd_OwnershipEnforcedInProcedure()
    {
        // Arrange
        await using var connection = new SqlConnection(ConnectionString);
        var ownerId = await InsertUserAsync(connection);
        var nonOwnerId = await InsertUserAsync(connection);
        var taskId = await InsertCompletedTaskAsync(connection, ownerId);
        var repository = new TaskQueueRepository(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CachetArchiveSync"] = ConnectionString })
            .Build());

        // Act — called directly on the repository, bypassing every service-level check.
        var nonOwnerOutcome = await repository.AcknowledgeAsync(taskId, nonOwnerId, "test-non-owner", CancellationToken.None);
        var afterNonOwner = await ReadSnapshotAsync(connection, taskId);
        var ownerOutcome = await repository.AcknowledgeAsync(taskId, ownerId, "test-owner", CancellationToken.None);
        var afterOwner = await ReadSnapshotAsync(connection, taskId);

        // Assert — the procedure returns "not found or not owned" (1) and leaves the row untouched.
        nonOwnerOutcome.Should().Be(AcknowledgeTaskOutcome.NotFound);
        afterNonOwner.ResultAcknowledgedAt.Should().BeNull();
        afterNonOwner.UpdatedBy.Should().Be("test-fixture");

        ownerOutcome.Should().Be(AcknowledgeTaskOutcome.Acknowledged);
        afterOwner.ResultAcknowledgedAt.Should().NotBeNull();
        afterOwner.UpdatedBy.Should().Be("test-owner");
    }
}
