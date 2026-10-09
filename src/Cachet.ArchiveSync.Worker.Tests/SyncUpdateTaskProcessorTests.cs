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
/// Unit tests for <see cref="SyncUpdateTaskProcessor"/> (CAS-007 §3/§6/§12) — the spec's §13 Test
/// Plan "SyncUpdateTaskProcessorTests" assignment: SC-7, SC-9 (Update case).
/// </summary>
public sealed class SyncUpdateTaskProcessorTests
{
    private sealed class Fixture
    {
        public ISyncResultRepository SyncResultRepository { get; } = Substitute.For<ISyncResultRepository>();
        public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public IB2Client B2Client { get; } = Substitute.For<IB2Client>();
        public IODriveFileReader ODriveFileReader { get; } = Substitute.For<IODriveFileReader>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<SyncUpdateTaskProcessor> Logger { get; } = Substitute.For<ILogger<SyncUpdateTaskProcessor>>();

        public SyncUpdateTaskProcessor CreateSut(TaskProgressReporterFactory? progressReporterFactoryOverride = null) =>
            new(SyncResultRepository, FileIndexRepository, TaskQueueRepository, progressReporterFactoryOverride ?? new TaskProgressReporterFactory(TaskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), B2Client, ODriveFileReader, AuditLogService, Logger);
    }

    private static ClaimedTaskRow ClaimedTask(int taskQueueId = 100402, params int[] fileIds) => new()
    {
        TaskQueueId = taskQueueId,
        TaskType = "SyncUpdate",
        SubmittedByUserId = 9,
        SubmittedAt = DateTimeOffset.UtcNow,
        RequestParameters = JsonSerializer.Serialize(new SyncActionRequestParametersDto { SourceTaskId = 100301, FileIds = fileIds })
    };

    private static SyncResultRow ModifiedRow(int syncResultId, int b2FileIndexId, string relativePath = "hr/policy.docx", string? odriveBucketName = null) => new()
    {
        SyncResultId = syncResultId,
        TaskQueueId = 100301,
        ComparisonType = "Modified",
        SyncStatus = "Pending",
        CreatedAt = DateTimeOffset.UtcNow,
        Sha1Changed = true,
        OdriveFileName = Path.GetFileName(relativePath),
        OdriveRelativePath = relativePath,
        OdriveBucketName = odriveBucketName,
        OdriveModifiedAt = DateTimeOffset.UtcNow,
        B2FileIndexId = b2FileIndexId,
        B2FileName = Path.GetFileName(relativePath),
        B2RelativePath = relativePath
    };

    private static B2UploadUrlInfo UploadUrl() => new()
    {
        BucketId = "bucket-1",
        UploadUrl = "https://upload-fake.backblazeb2.com/some-url",
        AuthorizationToken = "upload-token-never-log"
    };

    // ---- SC-7: uploads to the existing B2 object, updates the file_index row in place ---------------

    [Fact]
    public async Task Test_SC7_SyncUpdateSupersedesB2ObjectAndUpdatesExistingRow()
    {
        // Arrange
        var fixture = new Fixture();
        var row = ModifiedRow(100603, b2FileIndexId: 55);
        var task = ClaimedTask(fileIds: 100603);
        fixture.SyncResultRepository.GetAsync(100603, Arg.Any<CancellationToken>()).Returns(row);
        fixture.ODriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), row.OdriveRelativePath!, Arg.Any<CancellationToken>()).Returns(new byte[] { 5, 6, 7 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), row.B2RelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 3, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-3", FileName = row.B2RelativePath!, ContentSha1 = "sha1", ContentLength = 3, Action = "upload", UploadTimestamp = 1 });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — uploads to the EXISTING object's fileName (no new file_index row inserted).
        await fixture.B2Client.Received(1).UploadFileAsync(
            Arg.Any<B2UploadUrlInfo>(), row.B2RelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 3, Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.Received(1).UpdateAsync(
            55, Arg.Any<string>(), Arg.Any<string?>(), Arg.Is<long?>(l => l == 3),
            Arg.Is<DateTimeOffset?>(d => d == row.OdriveModifiedAt), Arg.Any<DateTimeOffset?>(),
            Arg.Is<bool?>(b => b == null), Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100603, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncUpdateInB2 && e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
    }

    // ---- CAS-013 SC-1 (Update case): each row's bytes are read from ITS OWN bucket's root ------------

    [Fact]
    public async Task Test_SC1_SyncUpdateReadsFromCorrectPerBucketRoot()
    {
        // Arrange — two dbo.file_index fixtures with different bucket_name values, each mapped to a
        // distinct root (CAS-013 §10 SC-1, Update case; §13's "Equivalent bucket-argument-propagation
        // assertion for Update").
        var fixture = new Fixture();
        var rowBucketA = ModifiedRow(100603, 55, "hr/bucket-a-policy.docx", odriveBucketName: "cachet-archive-bucket-a");
        var rowBucketB = ModifiedRow(100604, 56, "hr/bucket-b-policy.docx", odriveBucketName: "cachet-archive-bucket-b");
        var task = ClaimedTask(fileIds: new[] { 100603, 100604 });
        fixture.SyncResultRepository.GetAsync(100603, Arg.Any<CancellationToken>()).Returns(rowBucketA);
        fixture.SyncResultRepository.GetAsync(100604, Arg.Any<CancellationToken>()).Returns(rowBucketB);
        fixture.ODriveFileReader.ReadAllBytesAsync("cachet-archive-bucket-a", rowBucketA.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 1 });
        fixture.ODriveFileReader.ReadAllBytesAsync("cachet-archive-bucket-b", rowBucketB.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 2, 2, 2 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), rowBucketA.B2RelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 2, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-a", FileName = rowBucketA.B2RelativePath!, ContentSha1 = "sha1a", ContentLength = 2, Action = "upload", UploadTimestamp = 1 });
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), rowBucketB.B2RelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 3, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-b", FileName = rowBucketB.B2RelativePath!, ContentSha1 = "sha1b", ContentLength = 3, Action = "upload", UploadTimestamp = 1 });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — each row's own bucket root was used, not a single shared root.
        await fixture.ODriveFileReader.Received(1).ReadAllBytesAsync("cachet-archive-bucket-a", rowBucketA.OdriveRelativePath!, Arg.Any<CancellationToken>());
        await fixture.ODriveFileReader.Received(1).ReadAllBytesAsync("cachet-archive-bucket-b", rowBucketB.OdriveRelativePath!, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100603, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100604, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
    }

    // ---- SC-9 (Update case): a per-file B2 failure is isolated, does not abort the rest of the batch

    [Fact]
    public async Task Test_SC9_PerFileB2FailureIsolatedNotBatchAborting()
    {
        // Arrange
        var fixture = new Fixture();
        var failingRow = ModifiedRow(100603, 55, "hr/fails.docx");
        var succeedingRow = ModifiedRow(100604, 56, "hr/succeeds.docx");
        var task = ClaimedTask(fileIds: new[] { 100603, 100604 });
        fixture.SyncResultRepository.GetAsync(100603, Arg.Any<CancellationToken>()).Returns(failingRow);
        fixture.SyncResultRepository.GetAsync(100604, Arg.Any<CancellationToken>()).Returns(succeedingRow);
        fixture.ODriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), failingRow.B2RelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "leak-must-not-appear"));
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), succeedingRow.B2RelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-4", FileName = succeedingRow.B2RelativePath!, ContentSha1 = "sha1", ContentLength = 1, Action = "upload", UploadTimestamp = 1 });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100603, Arg.Any<string>(), "Failed", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100604, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.SyncActionFailedB2 &&
                e.ResultDetail != null && !e.ResultDetail.Contains("leak-must-not-appear")),
            Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: an unprocessable sync_result row (stale/malformed) is skipped, failed ----
    // ---- as its own file, and does not touch the reader/B2 client at all -----------------------------

    [Fact]
    public async Task Test_UnprocessableSyncResultRow_SkippedAsFailedWithoutTouchingReaderOrB2()
    {
        // Arrange — the row exists but is missing OdriveRelativePath/B2FileIndexId (e.g. a stale or
        // malformed sync_result row), so it can't be processed at all; the batch's other file is
        // unaffected.
        var fixture = new Fixture();
        var unprocessableRow = new SyncResultRow
        {
            SyncResultId = 100603,
            TaskQueueId = 100301,
            ComparisonType = "Modified",
            SyncStatus = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            OdriveFileName = "policy.docx",
            OdriveRelativePath = null,
            B2FileIndexId = null
        };
        var okRow = ModifiedRow(100604, b2FileIndexId: 56, relativePath: "hr/succeeds.docx");
        var task = ClaimedTask(fileIds: new[] { 100603, 100604 });
        fixture.SyncResultRepository.GetAsync(100603, Arg.Any<CancellationToken>()).Returns(unprocessableRow);
        fixture.SyncResultRepository.GetAsync(100604, Arg.Any<CancellationToken>()).Returns(okRow);
        fixture.ODriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), okRow.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 3, 3 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), okRow.B2RelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 2, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-ok", FileName = okRow.B2RelativePath!, ContentSha1 = "sha1", ContentLength = 2, Action = "upload", UploadTimestamp = 1 });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — the unprocessable row is never read from disk or uploaded, and no status/audit
        // write happens for it (there is no valid SyncResultId path to update); the other row still
        // reaches Synced and the task still completes with the correct succeeded/failed tally.
        await fixture.ODriveFileReader.DidNotReceive().ReadAllBytesAsync(Arg.Any<string>(), Arg.Is<string>(p => p != okRow.OdriveRelativePath), Arg.Any<CancellationToken>());
        await fixture.B2Client.DidNotReceive().UploadFileAsync(
            Arg.Any<B2UploadUrlInfo>(), Arg.Is<string>(n => n != okRow.B2RelativePath), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100604, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed",
            Arg.Is<string?>(d => d != null && d.Contains("\"SucceededCount\":1") && d.Contains("\"FailedCount\":1")),
            Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: local O-Drive read failure -> SyncActionFailedLocal, not SyncActionFailedB2,
    // ---- and no B2 call is made for that file --------------------------------------------------------

    [Fact]
    public async Task Test_ProcessAsync_LocalReadFailure_WritesFailedLocalAuditAndSkipsB2()
    {
        // Arrange
        var fixture = new Fixture();
        var row = ModifiedRow(100603, b2FileIndexId: 55);
        var task = ClaimedTask(fileIds: 100603);
        fixture.SyncResultRepository.GetAsync(100603, Arg.Any<CancellationToken>()).Returns(row);
        fixture.ODriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), row.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Throws(new IOException("simulated local read failure"));
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.B2Client.DidNotReceive().GetUploadUrlAsync(Arg.Any<CancellationToken>());
        await fixture.B2Client.DidNotReceive().UploadFileAsync(
            Arg.Any<B2UploadUrlInfo>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100603, Arg.Any<string>(), "Failed", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncActionFailedLocal && e.Result == AuditResult.Failure),
            Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: the outer unhandled-exception catch — a malformed request_parameters ------
    // ---- causes DeserializeRequest to throw before any per-file processing occurs -------------------

    [Fact]
    public async Task Test_ProcessAsync_MalformedRequestParameters_MarksTaskFailedViaOuterCatch()
    {
        // Arrange — request_parameters is not valid JSON, so DeserializeRequest throws before the
        // per-file loop ever starts.
        var fixture = new Fixture();
        var task = new ClaimedTaskRow
        {
            TaskQueueId = 100402,
            TaskType = "SyncUpdate",
            SubmittedByUserId = 9,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = "{ this is not valid json"
        };
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — the whole task_queue row is marked Failed with a FailureReason, and no per-file
        // repository/reader/B2 calls ever happen.
        await fixture.SyncResultRepository.DidNotReceiveWithAnyArgs().GetAsync(default, default);
        await fixture.ODriveFileReader.DidNotReceiveWithAnyArgs().ReadAllBytesAsync(default!, default!, default);
        await fixture.B2Client.DidNotReceiveWithAnyArgs().GetUploadUrlAsync(default);
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Failed",
            Arg.Is<string?>(d => d != null && d.Contains("\"FailureReason\":\"Unhandled error during SyncUpdate processing.\"")),
            Arg.Any<CancellationToken>());
    }

    // ==== CAS-029 — progress tracking (§3.3, §3.6) ===================================================

    private static string? CompletedResultDetail(ITaskQueueRepository repository) =>
        repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateStatusAsync)
                && (string)c.GetArguments()[2]! == "Completed")
            .Select(c => (string?)c.GetArguments()[3])
            .Single();

    // ---- CAS-029 SC-5 (SyncUpdate): per-file status + audit → final progress → Completed -----------

    [Fact]
    public async Task Test_SC5_SyncUpdate_ResultsThenProgressThenCompleted()
    {
        // Arrange — file 1 succeeds, file 2 fails on the B2 upload.
        var fixture = new Fixture();
        var okRow = ModifiedRow(100603, b2FileIndexId: 55, relativePath: "hr/ok.docx");
        var failRow = ModifiedRow(100604, b2FileIndexId: 56, relativePath: "hr/fail.docx");
        var task = ClaimedTask(fileIds: new[] { 100603, 100604 });
        fixture.SyncResultRepository.GetAsync(100603, Arg.Any<CancellationToken>()).Returns(okRow);
        fixture.SyncResultRepository.GetAsync(100604, Arg.Any<CancellationToken>()).Returns(failRow);
        fixture.ODriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 5, 6, 7 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), okRow.B2RelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "f3", FileName = okRow.B2RelativePath!, ContentSha1 = "s", ContentLength = 3, Action = "upload", UploadTimestamp = 1 });
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), failRow.B2RelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "simulated"));
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — ordering (§3.6).
        Received.InOrder(() =>
        {
            fixture.SyncResultRepository.UpdateStatusAsync(100603, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
            fixture.AuditLogService.WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncUpdateInB2), Arg.Any<CancellationToken>());
            fixture.SyncResultRepository.UpdateStatusAsync(100604, Arg.Any<string>(), "Failed", task.TaskQueueId, Arg.Any<CancellationToken>());
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
}
