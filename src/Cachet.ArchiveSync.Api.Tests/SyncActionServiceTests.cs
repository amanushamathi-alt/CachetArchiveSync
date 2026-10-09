using Cachet.ArchiveSync.Api.Services;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="SyncActionService"/> (CAS-007 §6 <c>POST /sync/actions/{copy,update,purge}</c>
/// validate+submit) — the spec's §13 Test Plan "SyncActionRequestValidatorTests" assignment (V3-V7,
/// SC-15), plus SC-10's submission-side half ("audits before the 202 response" — the per-file
/// completion-audit half of SC-10 is proven at the Worker processor layer, see
/// <c>Cachet.ArchiveSync.Worker.Tests.SyncActionProcessorSecurityTests</c>).
/// </summary>
public sealed class SyncActionServiceTests
{
    private const int SourceTaskId = 100301;

    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public ISyncResultRepository SyncResultRepository { get; } = Substitute.For<ISyncResultRepository>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<SyncActionService> Logger { get; } = Substitute.For<ILogger<SyncActionService>>();
        public IConfiguration Configuration { get; set; } = new ConfigurationBuilder().Build();

        public SyncActionService CreateSut() =>
            new(TaskQueueRepository, SyncResultRepository, AuditLogService, Configuration, Logger);
    }

    private static TaskQueueRow ValidSourceTask(int taskId = SourceTaskId) => new()
    {
        TaskQueueId = taskId,
        TaskType = "ContentScan",
        Status = "Completed",
        SubmittedByUserId = 1,
        SubmittedAt = DateTimeOffset.UtcNow
    };

    private static SyncResultRow PendingRow(int syncResultId, string comparisonType, int taskQueueId = SourceTaskId) => new()
    {
        SyncResultId = syncResultId,
        TaskQueueId = taskQueueId,
        ComparisonType = comparisonType,
        SyncStatus = "Pending",
        CreatedAt = DateTimeOffset.UtcNow,
        OdriveFileName = "new-invoice.xlsx",
        OdriveRelativePath = "finance/2026/new-invoice.xlsx"
    };

    // ---- V3: fileIds must be non-empty, for all three action endpoints -----------------------------

    [Theory]
    [InlineData("copy")]
    [InlineData("update")]
    [InlineData("purge")]
    public async Task Test_V3_SyncActionEmptyFileIdsRejected(string action)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        var result = action switch
        {
            "copy" => await sut.SubmitCopyAsync(new SyncActionRequest { TaskId = SourceTaskId, FileIds = Array.Empty<int>() }, 1, CancellationToken.None),
            "update" => await sut.SubmitUpdateAsync(new SyncActionRequest { TaskId = SourceTaskId, FileIds = Array.Empty<int>() }, 1, CancellationToken.None),
            _ => await sut.SubmitPurgeAsync(new SyncPurgeRequest { TaskId = SourceTaskId, FileIds = Array.Empty<int>(), Confirm = true }, 1, CancellationToken.None)
        };

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.ValidationFailed);
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- V4: copy fileId must be a Pending/New row scoped to taskId --------------------------------

    [Fact]
    public async Task Test_V4_CopyFileIdNotPendingNewReturns404()
    {
        // Arrange — the row exists but is Modified, not New.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(SourceTaskId, Arg.Any<CancellationToken>()).Returns(ValidSourceTask());
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(PendingRow(100601, "Modified"));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitCopyAsync(new SyncActionRequest { TaskId = SourceTaskId, FileIds = new[] { 100601 } }, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.FileNotFound);
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), "SyncCopy", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V4_CopyFileIdWrongTaskScope_Returns404()
    {
        // Arrange — correct comparison_type but belongs to a different taskId.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(SourceTaskId, Arg.Any<CancellationToken>()).Returns(ValidSourceTask());
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(PendingRow(100601, "New", taskQueueId: 999999));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitCopyAsync(new SyncActionRequest { TaskId = SourceTaskId, FileIds = new[] { 100601 } }, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.FileNotFound);
    }

    // ---- V5: update fileId must be a Pending/Modified row scoped to taskId -------------------------

    [Fact]
    public async Task Test_V5_UpdateFileIdNotPendingModifiedReturns404()
    {
        // Arrange — the row exists but is New, not Modified.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(SourceTaskId, Arg.Any<CancellationToken>()).Returns(ValidSourceTask());
        fixture.SyncResultRepository.GetAsync(100603, Arg.Any<CancellationToken>()).Returns(PendingRow(100603, "New"));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitUpdateAsync(new SyncActionRequest { TaskId = SourceTaskId, FileIds = new[] { 100603 } }, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.FileNotFound);
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), "SyncUpdate", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V5_UpdateFileIdAlreadySynced_Returns404()
    {
        // Arrange — correct comparison_type, but sync_status is no longer Pending.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(SourceTaskId, Arg.Any<CancellationToken>()).Returns(ValidSourceTask());
        fixture.SyncResultRepository.GetAsync(100603, Arg.Any<CancellationToken>())
            .Returns(PendingRow(100603, "Modified") with { SyncStatus = "Synced" });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitUpdateAsync(new SyncActionRequest { TaskId = SourceTaskId, FileIds = new[] { 100603 } }, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.FileNotFound);
    }

    // ---- V6: purge fileId must be a Pending/Removed row scoped to taskId ---------------------------

    [Fact]
    public async Task Test_V6_PurgeFileIdNotPendingRemovedReturns404()
    {
        // Arrange — the row exists but is New, not Removed.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(SourceTaskId, Arg.Any<CancellationToken>()).Returns(ValidSourceTask());
        fixture.SyncResultRepository.GetAsync(100602, Arg.Any<CancellationToken>()).Returns(PendingRow(100602, "New"));
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitPurgeAsync(
            new SyncPurgeRequest { TaskId = SourceTaskId, FileIds = new[] { 100602 }, Confirm = true }, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.FileNotFound);
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), "SyncPurge", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V6_PurgeUnknownFileId_Returns404()
    {
        // Arrange — the sync_result_id does not resolve at all.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(SourceTaskId, Arg.Any<CancellationToken>()).Returns(ValidSourceTask());
        fixture.SyncResultRepository.GetAsync(999999, Arg.Any<CancellationToken>()).Returns((SyncResultRow?)null);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitPurgeAsync(
            new SyncPurgeRequest { TaskId = SourceTaskId, FileIds = new[] { 999999 }, Confirm = true }, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.FileNotFound);
    }

    // ---- V7: purge confirm must be true, checked before any other validation -----------------------

    [Theory]
    [InlineData(false)]
    public async Task Test_V7_PurgeWithoutConfirmRejectedBeforeQueueing(bool confirm)
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = new SyncPurgeRequest { TaskId = SourceTaskId, FileIds = new[] { 100602 }, Confirm = confirm };

        // Act
        var result = await sut.SubmitPurgeAsync(request, 1, CancellationToken.None);

        // Assert — rejected before even the taskId/fileId lookups run.
        result.Outcome.Should().Be(SyncActionSubmitOutcome.ConfirmationRequired);
        await fixture.TaskQueueRepository.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V7_PurgeConfirmDefaultFalse_RejectedBeforeQueueing()
    {
        // Arrange — 'confirm' absent from the request entirely (bool default false, per DTO shape).
        var fixture = new Fixture();
        var sut = fixture.CreateSut();
        var request = new SyncPurgeRequest { TaskId = SourceTaskId, FileIds = new[] { 100602 } };

        // Act
        var result = await sut.SubmitPurgeAsync(request, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.ConfirmationRequired);
    }

    // ---- SC-15: a batch larger than Sync:MaxBatchSize is rejected before any task is queued ---------

    [Fact]
    public async Task Test_SC15_OversizedBatchRejectedBeforeQueueing()
    {
        // Arrange
        var fixture = new Fixture
        {
            Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:MaxBatchSize"] = "2" })
                .Build()
        };
        var sut = fixture.CreateSut();
        var request = new SyncActionRequest { TaskId = SourceTaskId, FileIds = new[] { 100601, 100602, 100603 } };

        // Act
        var result = await sut.SubmitCopyAsync(request, 1, CancellationToken.None);

        // Assert — rejected before even the taskId lookup runs.
        result.Outcome.Should().Be(SyncActionSubmitOutcome.ValidationFailed);
        await fixture.TaskQueueRepository.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC15_BatchAtDefaultMaxBatchSize_NotRejectedByBatchCap()
    {
        // Arrange — no Sync:MaxBatchSize configured, default of 100 applies; 100 fileIds is exactly
        // at the cap and must not be rejected on that basis alone (still needs valid rows to accept).
        var fixture = new Fixture();
        var fileIds = Enumerable.Range(100601, 100).ToArray();
        fixture.TaskQueueRepository.GetAsync(SourceTaskId, Arg.Any<CancellationToken>()).Returns(ValidSourceTask());
        foreach (var id in fileIds)
        {
            fixture.SyncResultRepository.GetAsync(id, Arg.Any<CancellationToken>()).Returns(PendingRow(id, "New"));
        }
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "SyncCopy", 1, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100401);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitCopyAsync(new SyncActionRequest { TaskId = SourceTaskId, FileIds = fileIds }, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.Accepted);
    }

    // ---- Branch coverage: unknown source taskId -----------------------------------------------------

    [Fact]
    public async Task Test_SubmitCopy_UnknownTaskId_ReturnsTaskNotFound()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(999999, Arg.Any<CancellationToken>()).Returns((TaskQueueRow?)null);
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitCopyAsync(new SyncActionRequest { TaskId = 999999, FileIds = new[] { 100601 } }, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.TaskNotFound);
    }

    [Fact]
    public async Task Test_SubmitCopy_SourceTaskWrongType_ReturnsTaskNotFound()
    {
        // Arrange — taskId exists but is not a ContentScan task.
        var fixture = new Fixture();
        fixture.TaskQueueRepository.GetAsync(SourceTaskId, Arg.Any<CancellationToken>())
            .Returns(ValidSourceTask() with { TaskType = "SyncCopy" });
        var sut = fixture.CreateSut();

        // Act
        var result = await sut.SubmitCopyAsync(new SyncActionRequest { TaskId = SourceTaskId, FileIds = new[] { 100601 } }, 1, CancellationToken.None);

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.TaskNotFound);
    }

    // ---- SC-10 (submission half): each of the three actions audits before the 202 response ----------

    [Theory]
    [InlineData("copy", "New", "SyncCopy", AuditAction.SyncCopySubmitted)]
    [InlineData("update", "Modified", "SyncUpdate", AuditAction.SyncUpdateSubmitted)]
    [InlineData("purge", "Removed", "SyncPurge", AuditAction.SyncPurgeSubmitted)]
    public async Task Test_SC10_AllThreeActionsAuditBeforeSubmitAndBeforeCompletion(
        string action, string comparisonType, string expectedTaskType, AuditAction expectedAuditAction)
    {
        // Arrange
        var fixture = new Fixture();
        var callOrder = new List<string>();
        fixture.TaskQueueRepository.GetAsync(SourceTaskId, Arg.Any<CancellationToken>()).Returns(ValidSourceTask());
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(PendingRow(100601, comparisonType));
        fixture.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), expectedTaskType, 1, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100401)
            .AndDoes(_ => callOrder.Add("insert-task"));
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));
        var sut = fixture.CreateSut();

        // Act
        var result = action switch
        {
            "copy" => await sut.SubmitCopyAsync(new SyncActionRequest { TaskId = SourceTaskId, FileIds = new[] { 100601 } }, 1, CancellationToken.None),
            "update" => await sut.SubmitUpdateAsync(new SyncActionRequest { TaskId = SourceTaskId, FileIds = new[] { 100601 } }, 1, CancellationToken.None),
            _ => await sut.SubmitPurgeAsync(new SyncPurgeRequest { TaskId = SourceTaskId, FileIds = new[] { 100601 }, Confirm = true }, 1, CancellationToken.None)
        };
        callOrder.Add("response-returned");

        // Assert
        result.Outcome.Should().Be(SyncActionSubmitOutcome.Accepted);
        result.Response!.TaskId.Should().Be(100401);
        result.Response.Type.Should().Be(expectedTaskType);

        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == expectedAuditAction &&
                e.TabModule == "O-Drive Sync" &&
                e.TaskQueueId == 100401 &&
                e.UserId == 1 &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());

        callOrder.Should().Equal("insert-task", "audit", "response-returned");
    }
}
