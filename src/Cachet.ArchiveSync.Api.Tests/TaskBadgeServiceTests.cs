using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="TaskBadgeService"/> (CAS-026 §6.3/§6.4 <c>GET /tasks/summary</c> /
/// <c>POST /tasks/mark-viewed</c>, FR-22) — the spec's §13 Test Plan "TaskSummaryServiceTests"
/// assignment (V7), plus branch coverage for <see cref="TaskBadgeService.MarkViewedAsync"/>.
///
/// Scope note (mirrors <see cref="TaskListServiceTests"/>'s/<see cref="TaskDetailServiceTests"/>'s
/// precedent): the "NULL <c>task_manager_last_viewed_at</c> means every Completed/Failed row counts
/// as unviewed" rule (§7 V7) is implemented inside <c>dbo.task_queue_summary_get</c> itself (CAS-026
/// §5), not in this service — <see cref="ITaskQueueRepository"/> is mocked here to simulate that
/// procedure's contracted output, proving the service passes the repository's counts straight through
/// to <see cref="TaskSummaryResponse"/> without re-deriving or clobbering them. The SQL behavior
/// itself is out of this unit-test layer's reach (no live SQL Server in this sandbox) and is not
/// separately assigned a repository-level test by CAS-026 §13.
/// </summary>
public sealed class TaskBadgeServiceTests
{
    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public IUserRepository UserRepository { get; } = Substitute.For<IUserRepository>();
        public ILogger<TaskBadgeService> Logger { get; } = Substitute.For<ILogger<TaskBadgeService>>();

        public TaskBadgeService CreateSut() => new(TaskQueueRepository, UserRepository, Logger);
    }

    // ---- V-7: a never-viewed user (NULL task_manager_last_viewed_at) gets every Completed/Failed
    // row counted as unviewed, not zero — proven here by passing through what dbo.task_queue_
    // summary_get is contracted to return in that case, per §5's design note. --------------------------

    [Fact]
    public async Task Test_V7_SummaryTreatsNullLastViewedAsNeverViewed()
    {
        // Arrange — simulates dbo.task_queue_summary_get's NULL-last-viewed-at branch: every
        // Completed/Failed row for this user counts as unviewed (3), not zero.
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .GetSummaryAsync(7, Arg.Any<CancellationToken>())
            .Returns(new TaskSummaryResponse { ActiveCount = 1, UnacknowledgedTerminalCount = 3 });
        var sut = fixture.CreateSut();

        // Act
        var response = await sut.GetSummaryAsync(actingUserId: 7, CancellationToken.None);

        // Assert — the service must not zero this out or otherwise reinterpret it.
        response.UnacknowledgedTerminalCount.Should().Be(3);
        response.UnacknowledgedTerminalCount.Should().NotBe(0);
        response.ActiveCount.Should().Be(1);
        await fixture.TaskQueueRepository.Received(1).GetSummaryAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_GetSummaryAsync_PassesActingUserIdThroughAndMapsCounts()
    {
        // Arrange — a viewed user (some baseline scenario): both counts pass through unchanged.
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .GetSummaryAsync(42, Arg.Any<CancellationToken>())
            .Returns(new TaskSummaryResponse { ActiveCount = 2, UnacknowledgedTerminalCount = 0 });
        var sut = fixture.CreateSut();

        // Act
        var response = await sut.GetSummaryAsync(actingUserId: 42, CancellationToken.None);

        // Assert
        response.ActiveCount.Should().Be(2);
        response.UnacknowledgedTerminalCount.Should().Be(0);
        await fixture.TaskQueueRepository.Received(1).GetSummaryAsync(42, Arg.Any<CancellationToken>());
    }

    // ---- MarkViewedAsync branch coverage (V-8's HTTP-level scoping is proven by the integration
    // suite; this proves the service calls through with the acting user's id and returns a
    // populated MarkedViewedAt). ------------------------------------------------------------------------

    [Fact]
    public async Task Test_MarkViewedAsync_CallsRepositoryWithActingUserIdAndReturnsMarkedViewedAt()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var before = DateTimeOffset.UtcNow;
        var response = await sut.MarkViewedAsync(actingUserId: 7, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        // Assert
        response.MarkedViewedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        await fixture.UserRepository.Received(1).MarkTaskManagerViewedAsync(7, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
