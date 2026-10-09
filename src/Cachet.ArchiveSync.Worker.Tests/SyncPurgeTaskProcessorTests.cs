using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// Unit tests for <see cref="SyncPurgeTaskProcessor"/> (CAS-007 §3/§6/§12) — the spec's §13 Test
/// Plan "SyncPurgeTaskProcessorTests" assignment: SC-8, SC-9 (Purge case).
/// </summary>
public sealed class SyncPurgeTaskProcessorTests
{
    private sealed class Fixture
    {
        public ISyncResultRepository SyncResultRepository { get; } = Substitute.For<ISyncResultRepository>();
        public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public IB2Client B2Client { get; } = Substitute.For<IB2Client>();
        public IB2MirrorFileRemover B2MirrorFileRemover { get; } = Substitute.For<IB2MirrorFileRemover>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<SyncPurgeTaskProcessor> Logger { get; } = Substitute.For<ILogger<SyncPurgeTaskProcessor>>();

        public Fixture()
        {
            // CAS-062 default: no local mirror copy.
            B2MirrorFileRemover.RemoveAsync(default!, default).ReturnsForAnyArgs(B2MirrorRemoveResult.NotPresent);
        }

        public SyncPurgeTaskProcessor CreateSut(TaskProgressReporterFactory? progressReporterFactoryOverride = null) =>
            new(SyncResultRepository, FileIndexRepository, TaskQueueRepository, progressReporterFactoryOverride ?? new TaskProgressReporterFactory(TaskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), B2Client, B2MirrorFileRemover, AuditLogService, Logger);
    }

    private static ClaimedTaskRow ClaimedTask(int taskQueueId = 100403, params int[] fileIds) => new()
    {
        TaskQueueId = taskQueueId,
        TaskType = "SyncPurge",
        SubmittedByUserId = 9,
        SubmittedAt = DateTimeOffset.UtcNow,
        RequestParameters = JsonSerializer.Serialize(new SyncActionRequestParametersDto { SourceTaskId = 100301, FileIds = fileIds })
    };

    private static SyncResultRow RemovedRow(int syncResultId, int b2FileIndexId, string relativePath = "contracts/2015/old-contract.pdf") => new()
    {
        SyncResultId = syncResultId,
        TaskQueueId = 100301,
        ComparisonType = "Removed",
        SyncStatus = "Pending",
        CreatedAt = DateTimeOffset.UtcNow,
        B2FileIndexId = b2FileIndexId,
        B2FileName = Path.GetFileName(relativePath),
        B2RelativePath = relativePath
    };

    private static B2FileInfo Version(string fileName, string fileId) => new()
    {
        FileId = fileId,
        FileName = fileName,
        ContentSha1 = "sha1",
        ContentLength = 1,
        Action = "upload",
        UploadTimestamp = 1
    };

    // ---- SC-8: deletes every B2 version returned by ListFileVersionsAsync, then soft-deletes -------

    [Fact]
    public async Task Test_SC8_SyncPurgeDeletesEveryB2VersionAndSoftDeletesFileIndex()
    {
        // Arrange — the targeted object has three versions.
        var fixture = new Fixture();
        var row = RemovedRow(100602, b2FileIndexId: 77);
        var task = ClaimedTask(fileIds: 100602);
        fixture.SyncResultRepository.GetAsync(100602, Arg.Any<CancellationToken>()).Returns(row);
        fixture.B2Client.ListFileVersionsAsync(row.B2RelativePath, row.B2RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new B2ListFileVersionsResult
            {
                Files = new[]
                {
                    Version(row.B2RelativePath!, "v1"),
                    Version(row.B2RelativePath!, "v2"),
                    Version(row.B2RelativePath!, "v3")
                },
                NextFileName = null,
                NextFileId = null
            });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — one DeleteFileVersionAsync call per version returned.
        await fixture.B2Client.Received(1).DeleteFileVersionAsync(row.B2RelativePath!, "v1", Arg.Any<CancellationToken>());
        await fixture.B2Client.Received(1).DeleteFileVersionAsync(row.B2RelativePath!, "v2", Arg.Any<CancellationToken>());
        await fixture.B2Client.Received(1).DeleteFileVersionAsync(row.B2RelativePath!, "v3", Arg.Any<CancellationToken>());
        await fixture.B2Client.Received(3).DeleteFileVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Assert — soft-deletes the B2-side file_index row (is_active = 0).
        await fixture.FileIndexRepository.Received(1).UpdateAsync(
            77, Arg.Any<string>(), Arg.Is<string?>(s => s == null), Arg.Is<long?>(l => l == null),
            Arg.Is<DateTimeOffset?>(d => d == null), Arg.Is<DateTimeOffset?>(d => d == null),
            Arg.Is<bool?>(b => b == false), Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100602, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncPurgeFromB2 && e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC8_SyncPurgePaginatesAcrossMultipleVersionPages()
    {
        // Arrange — B2 reports the version list across two pages for the same object.
        var fixture = new Fixture();
        var row = RemovedRow(100602, b2FileIndexId: 77);
        var task = ClaimedTask(fileIds: 100602);
        fixture.SyncResultRepository.GetAsync(100602, Arg.Any<CancellationToken>()).Returns(row);
        fixture.B2Client.ListFileVersionsAsync(row.B2RelativePath, row.B2RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                new B2ListFileVersionsResult { Files = new[] { Version(row.B2RelativePath!, "v1") }, NextFileName = row.B2RelativePath, NextFileId = "v2" },
                new B2ListFileVersionsResult { Files = new[] { Version(row.B2RelativePath!, "v2") }, NextFileName = null, NextFileId = null });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — both pages' versions were deleted.
        await fixture.B2Client.Received(1).DeleteFileVersionAsync(row.B2RelativePath!, "v1", Arg.Any<CancellationToken>());
        await fixture.B2Client.Received(1).DeleteFileVersionAsync(row.B2RelativePath!, "v2", Arg.Any<CancellationToken>());
    }

    // ---- SC-9 (Purge case): a per-file B2 failure is isolated, does not abort the rest of the batch -

    [Fact]
    public async Task Test_SC9_PerFileB2FailureIsolatedNotBatchAborting()
    {
        // Arrange — two files: the first fails the B2 delete call, the second succeeds.
        var fixture = new Fixture();
        var failingRow = RemovedRow(100602, 77, "contracts/fails.pdf");
        var succeedingRow = RemovedRow(100605, 78, "contracts/succeeds.pdf");
        var task = ClaimedTask(fileIds: new[] { 100602, 100605 });
        fixture.SyncResultRepository.GetAsync(100602, Arg.Any<CancellationToken>()).Returns(failingRow);
        fixture.SyncResultRepository.GetAsync(100605, Arg.Any<CancellationToken>()).Returns(succeedingRow);
        fixture.B2Client.ListFileVersionsAsync(failingRow.B2RelativePath, failingRow.B2RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new B2ListFileVersionsResult { Files = new[] { Version(failingRow.B2RelativePath!, "v1") } });
        fixture.B2Client.ListFileVersionsAsync(succeedingRow.B2RelativePath, succeedingRow.B2RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new B2ListFileVersionsResult { Files = new[] { Version(succeedingRow.B2RelativePath!, "v1") } });
        fixture.B2Client.DeleteFileVersionAsync(failingRow.B2RelativePath!, "v1", Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "leak-must-not-appear"));
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100602, Arg.Any<string>(), "Failed", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100605, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.Received(1).UpdateAsync(
            78, Arg.Any<string>(), Arg.Is<string?>(s => s == null), Arg.Is<long?>(l => l == null),
            Arg.Is<DateTimeOffset?>(d => d == null), Arg.Is<DateTimeOffset?>(d => d == null),
            Arg.Is<bool?>(b => b == false), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().UpdateAsync(
            77, Arg.Any<string>(), Arg.Is<string?>(s => s == null), Arg.Is<long?>(l => l == null),
            Arg.Is<DateTimeOffset?>(d => d == null), Arg.Is<DateTimeOffset?>(d => d == null),
            Arg.Is<bool?>(b => b == false), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.SyncActionFailedB2 &&
                e.ResultDetail != null && !e.ResultDetail.Contains("leak-must-not-appear")),
            Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ==== CAS-029 — progress tracking (§3.3, §3.6) ===================================================

    private static string? CompletedResultDetail(ITaskQueueRepository repository) =>
        repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateStatusAsync)
                && (string)c.GetArguments()[2]! == "Completed")
            .Select(c => (string?)c.GetArguments()[3])
            .Single();

    // ---- CAS-029 SC-5 (SyncPurge): per-file status + audit → final progress → Completed ------------

    [Fact]
    public async Task Test_SC5_SyncPurge_ResultsThenProgressThenCompleted()
    {
        // Arrange — file 1 is purged, file 2 fails on the B2 version listing.
        var fixture = new Fixture();
        var okRow = RemovedRow(100605, b2FileIndexId: 77, relativePath: "contracts/ok.pdf");
        var failRow = RemovedRow(100606, b2FileIndexId: 78, relativePath: "contracts/fail.pdf");
        var task = ClaimedTask(fileIds: new[] { 100605, 100606 });
        fixture.SyncResultRepository.GetAsync(100605, Arg.Any<CancellationToken>()).Returns(okRow);
        fixture.SyncResultRepository.GetAsync(100606, Arg.Any<CancellationToken>()).Returns(failRow);
        fixture.B2Client.ListFileVersionsAsync(okRow.B2RelativePath, okRow.B2RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new B2ListFileVersionsResult { Files = new[] { Version(okRow.B2RelativePath!, "v1") } });
        fixture.B2Client.ListFileVersionsAsync(failRow.B2RelativePath, failRow.B2RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "simulated"));
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — ordering (§3.6).
        Received.InOrder(() =>
        {
            fixture.SyncResultRepository.UpdateStatusAsync(100605, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
            fixture.AuditLogService.WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncPurgeFromB2), Arg.Any<CancellationToken>());
            fixture.SyncResultRepository.UpdateStatusAsync(100606, Arg.Any<string>(), "Failed", task.TaskQueueId, Arg.Any<CancellationToken>());
            fixture.AuditLogService.WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncActionFailedB2), Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.UpdateProgressAsync(task.TaskQueueId, Arg.Any<string>(), 2, 2, Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.UpdateStatusAsync(task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        });

        // Assert — summary counts equal the sync_result status writes.
        var resultRowWrites = fixture.SyncResultRepository.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(ISyncResultRepository.UpdateStatusAsync));
        var summary = JsonSerializer.Deserialize<SyncActionResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        (summary.SucceededCount + summary.FailedCount).Should().Be(resultRowWrites).And.Be(2);
        summary.SucceededCount.Should().Be(1);
        summary.FailedCount.Should().Be(1);
    }

    // ---- CAS-062: after the B2 purge, the local B2 mirror copy is removed ----------------------------

    private static (Fixture Fixture, SyncResultRow Row, ClaimedTaskRow Task) ArrangeSinglePurge(bool b2Fails = false)
    {
        var fixture = new Fixture();
        var row = RemovedRow(100602, b2FileIndexId: 77, relativePath: "D/Shares/Operations/old/report.pdf");
        var task = ClaimedTask(fileIds: 100602);
        fixture.SyncResultRepository.GetAsync(100602, Arg.Any<CancellationToken>()).Returns(row);
        var listCall = fixture.B2Client.ListFileVersionsAsync(row.B2RelativePath, row.B2RelativePath, Arg.Any<int>(), Arg.Any<CancellationToken>());
        if (b2Fails)
        {
            listCall.Throws(new B2ApiException(500, "internal_error", "B2 call failed."));
        }
        else
        {
            listCall.Returns(new B2ListFileVersionsResult { Files = new[] { Version(row.B2RelativePath!, "v1") } });
        }

        return (fixture, row, task);
    }

    [Fact]
    public async Task Test_CAS062_Purge_RemovesLocalMirrorFile()
    {
        // Arrange
        var (fixture, row, task) = ArrangeSinglePurge();
        fixture.B2MirrorFileRemover.RemoveAsync(row.B2RelativePath!, Arg.Any<CancellationToken>()).Returns(B2MirrorRemoveResult.Removed);

        // Act
        await fixture.CreateSut().ProcessAsync(task, CancellationToken.None);

        // Assert — remover called with the B2 key; Synced; success audit; no failure audit.
        await fixture.B2MirrorFileRemover.Received(1).RemoveAsync(row.B2RelativePath!, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100602, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncPurgeFromB2 && e.Result == AuditResult.Success), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncActionFailedLocal), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_CAS062_Purge_B2Failure_LocalUntouched()
    {
        // Arrange
        var (fixture, _, task) = ArrangeSinglePurge(b2Fails: true);

        // Act
        await fixture.CreateSut().ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.B2MirrorFileRemover.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100602, Arg.Any<string>(), "Failed", task.TaskQueueId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_CAS062_Purge_LocalRemovalFailed_AuditedNotReversed()
    {
        // Arrange
        var (fixture, row, task) = ArrangeSinglePurge();
        fixture.B2MirrorFileRemover.RemoveAsync(row.B2RelativePath!, Arg.Any<CancellationToken>())
            .Returns(B2MirrorRemoveResult.Failed("Local mirror copy could not be removed."));

        // Act
        await fixture.CreateSut().ProcessAsync(task, CancellationToken.None);

        // Assert — still Synced and counted as succeeded; the local failure is audited separately.
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100602, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.DidNotReceive().UpdateStatusAsync(100602, Arg.Any<string>(), "Failed", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncActionFailedLocal
                && e.Result == AuditResult.Failure
                && e.FilePath == row.B2RelativePath
                && e.ResultDetail!.StartsWith("Purged from B2; local mirror copy not removed:")),
            Arg.Any<CancellationToken>());
        var summary = JsonSerializer.Deserialize<SyncActionResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        summary.SucceededCount.Should().Be(1);
        summary.FailedCount.Should().Be(0);
    }

    [Fact]
    public async Task Test_CAS062_Purge_RemoverThrows_StaysSyncedNotReversed()
    {
        // Arrange — review F-1: an unexpected error (e.g. the mapping lookup's SQL call failing).
        var (fixture, row, task) = ArrangeSinglePurge();
        fixture.B2MirrorFileRemover.RemoveAsync(row.B2RelativePath!, Arg.Any<CancellationToken>())
            .Throws(new InvalidDataException("simulated database failure"));

        // Act
        await fixture.CreateSut().ProcessAsync(task, CancellationToken.None);

        // Assert — still Synced/succeeded; local failure audited; no SyncActionFailedB2.
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100602, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.DidNotReceive().UpdateStatusAsync(100602, Arg.Any<string>(), "Failed", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncActionFailedLocal), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncActionFailedB2), Arg.Any<CancellationToken>());
        var summary = JsonSerializer.Deserialize<SyncActionResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        summary.SucceededCount.Should().Be(1);
        summary.FailedCount.Should().Be(0);
    }

    [Fact]
    public async Task Test_CAS062_Purge_LocalFailureAuditThrows_StaysSynced()
    {
        // Arrange — the remover fails AND the SyncActionFailedLocal audit write itself throws.
        var (fixture, row, task) = ArrangeSinglePurge();
        fixture.B2MirrorFileRemover.RemoveAsync(row.B2RelativePath!, Arg.Any<CancellationToken>())
            .Returns(B2MirrorRemoveResult.Failed("File is locked or access was denied."));
        fixture.AuditLogService.WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncActionFailedLocal), Arg.Any<CancellationToken>())
            .Throws(new InvalidDataException("simulated audit failure"));

        // Act
        await fixture.CreateSut().ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.SyncResultRepository.DidNotReceive().UpdateStatusAsync(100602, Arg.Any<string>(), "Failed", Arg.Any<int>(), Arg.Any<CancellationToken>());
        var summary = JsonSerializer.Deserialize<SyncActionResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        summary.SucceededCount.Should().Be(1);
    }

    [Fact]
    public async Task Test_CAS062_Purge_LocalFileAbsent_NoFailureAudit()
    {
        // Arrange — default fixture remover: NotPresent.
        var (fixture, row, task) = ArrangeSinglePurge();

        // Act
        await fixture.CreateSut().ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.B2MirrorFileRemover.Received(1).RemoveAsync(row.B2RelativePath!, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Result == AuditResult.Failure), Arg.Any<CancellationToken>());
    }
}
