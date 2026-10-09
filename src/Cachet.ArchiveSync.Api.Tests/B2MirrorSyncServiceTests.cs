using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="B2MirrorSyncService"/> (CAS-037 §6/§7 V1/§12) — the spec's §13 Test
/// Plan "B2MirrorSyncServiceTests" assignment: V1, SC-6. Every dependency
/// (<see cref="ITaskQueueRepository"/>, <see cref="IAuditLogService"/>) is an NSubstitute fake —
/// this service never calls <see cref="IB2Client"/> at all (that only happens in the Worker's
/// <c>B2MirrorSyncTaskProcessor</c>).
/// </summary>
public sealed class B2MirrorSyncServiceTests
{
    private const string TaskType = "B2MirrorSync";

    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<B2MirrorSyncService> Logger { get; } = Substitute.For<ILogger<B2MirrorSyncService>>();

        public B2MirrorSyncService CreateSut() => new(TaskQueueRepository, AuditLogService, Logger);
    }

    // ---- V-1: at most one B2MirrorSync task may be Queued/Running at a time -------------------------

    [Fact]
    public async Task Test_V1_RejectsConcurrentMirrorSyncSubmission()
    {
        // Arrange — the concurrency guard reports a B2MirrorSync task is already active.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.HasActiveTaskOfTypeAsync(TaskType, Arg.Any<CancellationToken>()).Returns(true);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitAsync(submittedByUserId: 42, CancellationToken.None);

        // Assert — rejected, nothing inserted, nothing audited.
        result.Outcome.Should().Be(B2MirrorSyncSubmitOutcome.AlreadyRunning);
        result.Response.Should().BeNull();
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-6: a second submission while one is already Queued/Running inserts no task row ----------

    [Fact]
    public async Task Test_SC6_ConcurrentSubmissionRejected()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository.HasActiveTaskOfTypeAsync(TaskType, Arg.Any<CancellationToken>()).Returns(true);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitAsync(submittedByUserId: 7, CancellationToken.None);

        // Assert — Accepted's fields are absent; specifically, no task_queue row was inserted.
        result.Outcome.Should().Be(B2MirrorSyncSubmitOutcome.AlreadyRunning);
        await fixture.TaskQueueRepository.Received(0).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), TaskType, Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: the accepted path inserts the task and audits before returning -------------

    [Fact]
    public async Task Test_SubmitAsync_NotActive_InsertsTaskAndAuditsBeforeReturningAccepted()
    {
        // Arrange — no B2MirrorSync task currently active.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.HasActiveTaskOfTypeAsync(TaskType, Arg.Any<CancellationToken>()).Returns(false);
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), TaskType, 99, Arg.Is<string?>(s => s == null), Arg.Any<CancellationToken>())
            .Returns(100777);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitAsync(submittedByUserId: 99, CancellationToken.None);

        // Assert — accepted, correct response shape, audited before the caller gets a result (§12).
        result.Outcome.Should().Be(B2MirrorSyncSubmitOutcome.Accepted);
        result.Response.Should().NotBeNull();
        result.Response!.TaskId.Should().Be(100777);
        result.Response.Type.Should().Be(TaskType);
        result.Response.Status.Should().Be("Queued");

        Received.InOrder(() =>
        {
            fixture.TaskQueueRepository.InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), TaskType, 99, Arg.Is<string?>(s => s == null), Arg.Any<CancellationToken>());
            fixture.AuditLogService.WriteAsync(
                Arg.Is<AuditLogEntry>(e =>
                    e.Action == AuditAction.B2MirrorSyncSubmitted &&
                    e.TaskQueueId == 100777 &&
                    e.UserId == 99 &&
                    e.Result == AuditResult.Success),
                Arg.Any<CancellationToken>());
        });
    }
}
