using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="TaskDetailService"/> (CAS-026 §6.2 <c>GET /tasks/{taskId}</c>, FR-19) —
/// the spec's §13 Test Plan "TaskDetailServiceTests" assignment: V4, V5, SC-3. Both
/// <see cref="ITaskQueueRepository"/> and <see cref="IUserRepository"/> are mocked (no live SQL
/// Server needed for this service-layer logic — the ownership/not-found/resultDetail branching all
/// lives in C#, not in <c>dbo.task_queue_get</c>, §0).
/// </summary>
public sealed class TaskDetailServiceTests
{
    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
        public ILogger<TaskDetailService> Logger { get; } = Substitute.For<ILogger<TaskDetailService>>();

        public TaskDetailService CreateSut() => new(TaskQueueRepository, UserRepository, Logger);
    }

    private static TaskQueueRow Task(
        int taskQueueId, string taskType, string status, int submittedByUserId, string? resultDetail = null) => new()
        {
            TaskQueueId = taskQueueId,
            TaskType = taskType,
            Status = status,
            SubmittedByUserId = submittedByUserId,
            SubmittedAt = new DateTimeOffset(2026, 9, 24, 14, 5, 3, TimeSpan.Zero),
            ResultDetail = resultDetail
        };

    private static UserAccount User(int userId, string username) => new()
    {
        UserId = userId,
        Username = username,
        PasswordHash = "hash",
        B2ApplicationKeyId = "ct:keyid",
        B2ApplicationKey = "ct:key",
        IsActive = true,
        SessionTokenVersion = 1
    };

    // ---- SC-3: resultDetail is present for a Failed task, null for a non-Failed task -----------------

    [Fact]
    public async Task Test_SC3_GetReturnsResultDetailOnlyWhenPresent()
    {
        // Arrange — a Failed row carries dbo.task_queue.result_detail.
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .GetAsync(100234, Arg.Any<CancellationToken>())
            .Returns(Task(100234, "KeywordSearch", "Failed", submittedByUserId: 7,
                resultDetail: "B2 authorization failed during cross-check (see Audit Log)."));
        fixture.UserRepository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(User(7, "m.alvarez"));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetAsync(100234, actingUserId: 7, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(TaskDetailOutcome.Found);
        result.Response!.Status.Should().Be("Failed");
        result.Response.ResultDetail.Should().Be("B2 authorization failed during cross-check (see Audit Log).");
        result.Response.SubmittedBy.Should().Be("m.alvarez");
    }

    [Fact]
    public async Task Test_SC3_GetOmitsResultDetailForNonFailedTaskEvenIfColumnPopulated()
    {
        // Arrange — a Completed row that (hypothetically) still carries a stale result_detail value;
        // the service must null it out regardless, per §10 SC-3.
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .GetAsync(100235, Arg.Any<CancellationToken>())
            .Returns(Task(100235, "KeywordSearch", "Completed", submittedByUserId: 7,
                resultDetail: "stale value that must never surface for a Completed task"));
        fixture.UserRepository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(User(7, "m.alvarez"));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetAsync(100235, actingUserId: 7, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(TaskDetailOutcome.Found);
        result.Response!.Status.Should().Be("Completed");
        result.Response.ResultDetail.Should().BeNull();
    }

    // ---- V-4: a taskId that exists but belongs to a different user returns NotOwner, never Found -----

    [Fact]
    public async Task Test_V4_GetReturns403ForNonOwnedTask()
    {
        // Arrange — task exists, submitted by user 9; caller is user 7.
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .GetAsync(100234, Arg.Any<CancellationToken>())
            .Returns(Task(100234, "KeywordSearch", "Completed", submittedByUserId: 9));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetAsync(100234, actingUserId: 7, CancellationToken.None);

        // Assert — NotOwner, and no username lookup is performed for a row the caller may not see.
        result.Outcome.Should().Be(TaskDetailOutcome.NotOwner);
        result.Response.Should().BeNull();
        await fixture.UserRepository.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- V-5: a nonexistent taskId returns NotFound -----------------------------------------------------

    [Fact]
    public async Task Test_V5_GetReturns404ForUnknownTask()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(999999, Arg.Any<CancellationToken>()).Returns((TaskQueueRow?)null);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.GetAsync(999999, actingUserId: 7, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(TaskDetailOutcome.NotFound);
        result.Response.Should().BeNull();
        await fixture.UserRepository.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
