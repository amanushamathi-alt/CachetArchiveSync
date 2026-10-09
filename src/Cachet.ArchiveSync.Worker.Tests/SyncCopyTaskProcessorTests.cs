using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// Unit tests for <see cref="SyncCopyTaskProcessor"/> (CAS-007 §3/§6/§12) — the spec's §13 Test
/// Plan "SyncCopyTaskProcessorTests" assignment: SC-6, SC-9 (Copy case). Every dependency
/// (<see cref="ISyncResultRepository"/>, <see cref="IFileIndexRepository"/>,
/// <see cref="ITaskQueueRepository"/>, <see cref="IB2Client"/>, <see cref="IODriveFileReader"/>,
/// <see cref="IAuditLogService"/>) is an NSubstitute fake — never a real B2 call, never real disk
/// I/O.
/// </summary>
public sealed class SyncCopyTaskProcessorTests
{
    private const string ConfiguredBucket = "cachet-archive-prod";

    private sealed class Fixture
    {
        public ISyncResultRepository SyncResultRepository { get; } = Substitute.For<ISyncResultRepository>();
        public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public IB2Client B2Client { get; } = Substitute.For<IB2Client>();
        public IODriveFileReader ODriveFileReader { get; } = Substitute.For<IODriveFileReader>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<SyncCopyTaskProcessor> Logger { get; } = Substitute.For<ILogger<SyncCopyTaskProcessor>>();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = ConfiguredBucket })
            .Build();

        public SyncCopyTaskProcessor CreateSut(TaskProgressReporterFactory? progressReporterFactoryOverride = null) =>
            new(SyncResultRepository, FileIndexRepository, TaskQueueRepository, progressReporterFactoryOverride ?? new TaskProgressReporterFactory(TaskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), B2Client, ODriveFileReader, AuditLogService, Configuration, Logger);
    }

    // CAS-062: Purge's local-mirror step is not under test here — the remover reports "no local copy".
    private static IB2MirrorFileRemover NotPresentRemover()
    {
        var remover = Substitute.For<IB2MirrorFileRemover>();
        remover.RemoveAsync(default!, default).ReturnsForAnyArgs(B2MirrorRemoveResult.NotPresent);
        return remover;
    }

    private static ClaimedTaskRow ClaimedTask(int taskQueueId = 100401, params int[] fileIds) => new()
    {
        TaskQueueId = taskQueueId,
        TaskType = "SyncCopy",
        SubmittedByUserId = 9,
        SubmittedAt = DateTimeOffset.UtcNow,
        RequestParameters = JsonSerializer.Serialize(new SyncActionRequestParametersDto { SourceTaskId = 100301, FileIds = fileIds })
    };

    private static SyncResultRow NewRow(int syncResultId, string relativePath = "finance/2026/new-invoice.xlsx", string? odriveBucketName = null) => new()
    {
        SyncResultId = syncResultId,
        TaskQueueId = 100301,
        ComparisonType = "New",
        SyncStatus = "Pending",
        CreatedAt = DateTimeOffset.UtcNow,
        OdriveFileName = Path.GetFileName(relativePath),
        OdriveRelativePath = relativePath,
        OdriveBucketName = odriveBucketName,
        OdriveFileSizeBytes = 2048,
        OdriveModifiedAt = DateTimeOffset.UtcNow
    };

    private static B2UploadUrlInfo UploadUrl() => new()
    {
        BucketId = "bucket-1",
        UploadUrl = "https://upload-fake.backblazeb2.com/some-url",
        AuthorizationToken = "upload-token-never-log"
    };

    // ---- SC-6: valid copy uploads via IB2Client, inserts a new B2-side file_index row, sets Synced --

    [Fact]
    public async Task Test_SC6_SyncCopyUploadsAndUpdatesMirrorAndStatus()
    {
        // Arrange
        var fixture = new Fixture();
        var row = NewRow(100601);
        var task = ClaimedTask(fileIds: 100601);
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(row);
        fixture.ODriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), row.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3, 4 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), row.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 4, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-1", FileName = row.OdriveRelativePath!, ContentSha1 = "sha1", ContentLength = 4, Action = "upload", UploadTimestamp = 1 });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — uploaded, new B2-side file_index row inserted, sync_result marked Synced.
        await fixture.B2Client.Received(1).GetUploadUrlAsync(Arg.Any<CancellationToken>());
        await fixture.B2Client.Received(1).UploadFileAsync(
            Arg.Any<B2UploadUrlInfo>(), row.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 4, Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), "B2", ConfiguredBucket, row.OdriveRelativePath!, row.OdriveFileName!,
            Arg.Any<string?>(), 4, Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100601, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncCopyToB2 && e.Result == AuditResult.Success && e.FilePath == row.OdriveRelativePath),
            Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-9 (Copy case): a per-file B2 failure is isolated, does not abort the rest of the batch --

    [Fact]
    public async Task Test_SC9_PerFileB2FailureIsolatedNotBatchAborting()
    {
        // Arrange — two files: the first fails the B2 upload call, the second succeeds.
        var fixture = new Fixture();
        var failingRow = NewRow(100601, "finance/2026/fails-to-upload.xlsx");
        var succeedingRow = NewRow(100602, "finance/2026/succeeds.xlsx");
        var task = ClaimedTask(fileIds: new[] { 100601, 100602 });
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(failingRow);
        fixture.SyncResultRepository.GetAsync(100602, Arg.Any<CancellationToken>()).Returns(succeedingRow);
        fixture.ODriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 9, 9 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), failingRow.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "some B2-side detail that must never leak"));
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), succeedingRow.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-2", FileName = succeedingRow.OdriveRelativePath!, ContentSha1 = "sha1", ContentLength = 2, Action = "upload", UploadTimestamp = 1 });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — failing file marked Failed + SyncActionFailedB2 audit (no leaked B2 detail);
        // succeeding file still processed to Synced. The task itself still reaches Completed.
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100601, Arg.Any<string>(), "Failed", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100602, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.SyncActionFailedB2 &&
                e.Result == AuditResult.Failure &&
                e.ResultDetail != null && !e.ResultDetail.Contains("some B2-side detail")),
            Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed",
            Arg.Is<string?>(d => d != null && d.Contains("\"SucceededCount\":1") && d.Contains("\"FailedCount\":1")),
            Arg.Any<CancellationToken>());
    }

    // ---- CAS-013 SC-1: each row's bytes are read from ITS OWN bucket's root, not a shared root ------

    [Fact]
    public async Task Test_SC1_SyncCopyReadsFromCorrectPerBucketRoot()
    {
        // Arrange — two dbo.file_index fixtures with different bucket_name values (CAS-013 §10 SC-1).
        var fixture = new Fixture();
        var rowBucketA = NewRow(100601, "finance/2026/new-invoice.xlsx", odriveBucketName: "cachet-archive-bucket-a");
        var rowBucketB = NewRow(100602, "hr/2026/policy.docx", odriveBucketName: "cachet-archive-bucket-b");
        var task = ClaimedTask(fileIds: new[] { 100601, 100602 });
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(rowBucketA);
        fixture.SyncResultRepository.GetAsync(100602, Arg.Any<CancellationToken>()).Returns(rowBucketB);

        // Each bucket's read is wired only for its own (bucketName, relativePath) pair — an
        // unconfigured/wrong-bucket call would return a null byte[] and blow up downstream, so a
        // passing test proves the correct bucketName argument was threaded through per row.
        fixture.ODriveFileReader.ReadAllBytesAsync("cachet-archive-bucket-a", rowBucketA.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 1, 1 });
        fixture.ODriveFileReader.ReadAllBytesAsync("cachet-archive-bucket-b", rowBucketB.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 2, 2, 2, 2 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), rowBucketA.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 3, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-a", FileName = rowBucketA.OdriveRelativePath!, ContentSha1 = "sha1a", ContentLength = 3, Action = "upload", UploadTimestamp = 1 });
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), rowBucketB.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 4, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-b", FileName = rowBucketB.OdriveRelativePath!, ContentSha1 = "sha1b", ContentLength = 4, Action = "upload", UploadTimestamp = 1 });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — each row's own bucket root was used (not a single shared root), and both files
        // completed successfully because the correct per-bucket read occurred.
        await fixture.ODriveFileReader.Received(1).ReadAllBytesAsync("cachet-archive-bucket-a", rowBucketA.OdriveRelativePath!, Arg.Any<CancellationToken>());
        await fixture.ODriveFileReader.Received(1).ReadAllBytesAsync("cachet-archive-bucket-b", rowBucketB.OdriveRelativePath!, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100601, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100602, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
    }

    // ---- CAS-013 SC-2: an unmapped-bucket local read failure fails only that file, per-file --------
    // ---- independence holds (reuses CAS-007 SC-9's guarantee) ----------------------------------------

    [Fact]
    public async Task Test_SC2_UnmappedBucketFailsOnlyThatFilePerFileIndependenceHolds()
    {
        // Arrange — the first row's O-Drive-side bucket has no active odrive_bucket_mapping row, so
        // ODriveFileReader (mocked here at the processor level) throws the clear, non-path-leaking
        // InvalidOperationException it raises for that case (CAS-013 §2/§9 V1); the second row's
        // bucket is mapped and succeeds.
        var fixture = new Fixture();
        var unmappedRow = NewRow(100601, "finance/2026/orphaned-bucket-file.xlsx", odriveBucketName: "unmapped-bucket");
        var mappedRow = NewRow(100602, "hr/2026/policy.docx", odriveBucketName: "cachet-archive-bucket-a");
        var task = ClaimedTask(fileIds: new[] { 100601, 100602 });
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(unmappedRow);
        fixture.SyncResultRepository.GetAsync(100602, Arg.Any<CancellationToken>()).Returns(mappedRow);
        fixture.ODriveFileReader.ReadAllBytesAsync("unmapped-bucket", unmappedRow.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("No active odrive_bucket_mapping row found for the requested bucket."));
        fixture.ODriveFileReader.ReadAllBytesAsync("cachet-archive-bucket-a", mappedRow.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 7, 7 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), mappedRow.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 2, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-mapped", FileName = mappedRow.OdriveRelativePath!, ContentSha1 = "sha1", ContentLength = 2, Action = "upload", UploadTimestamp = 1 });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — only the unmapped-bucket file fails (SyncActionFailedLocal, sync_status =
        // 'Failed'); the other file in the same batch is unaffected and reaches Synced.
        await fixture.B2Client.DidNotReceive().UploadFileAsync(
            Arg.Any<B2UploadUrlInfo>(), unmappedRow.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100601, Arg.Any<string>(), "Failed", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncActionFailedLocal && e.Result == AuditResult.Failure && e.FilePath == unmappedRow.OdriveRelativePath),
            Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100602, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed",
            Arg.Is<string?>(d => d != null && d.Contains("\"SucceededCount\":1") && d.Contains("\"FailedCount\":1")),
            Arg.Any<CancellationToken>());
    }

    // ---- CAS-013 V2: the new B2-side file_index row inherits the source row's bucket_name -----------

    [Fact]
    public async Task Test_V2_NewB2FileIndexRowInheritsSourceBucketName()
    {
        // Arrange — the O-Drive-side row carries a bucket_name distinct from the single global
        // B2:BucketName config value, proving the stamped value is per-row, not global (CAS-013 §9 V2).
        var fixture = new Fixture();
        var row = NewRow(100601, odriveBucketName: "cachet-archive-bucket-a");
        row.OdriveBucketName.Should().NotBe(ConfiguredBucket);
        var task = ClaimedTask(fileIds: 100601);
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(row);
        fixture.ODriveFileReader.ReadAllBytesAsync("cachet-archive-bucket-a", row.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3, 4 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), row.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 4, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-1", FileName = row.OdriveRelativePath!, ContentSha1 = "sha1", ContentLength = 4, Action = "upload", UploadTimestamp = 1 });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — new file_index row stamped with the SOURCE row's bucket_name, not the global config.
        await fixture.FileIndexRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), "B2", "cachet-archive-bucket-a", row.OdriveRelativePath!, row.OdriveFileName!,
            Arg.Any<string?>(), 4, Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await fixture.FileIndexRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), "B2", ConfiguredBucket, Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    // ---- CAS-013 V2 (fallback branch): a null source bucket_name falls back to B2:BucketName, ------
    // ---- logging a warning so the legacy-data gap stays visible rather than silently hidden ---------

    [Fact]
    public async Task Test_V2_NewB2FileIndexRowFallsBackToConfiguredBucketWithWarningWhenSourceHasNoBucket()
    {
        // Arrange — the source row has no bucket_name at all (odriveBucketName: null).
        var fixture = new Fixture();
        var row = NewRow(100601, odriveBucketName: null);
        var task = ClaimedTask(fileIds: 100601);
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(row);
        fixture.ODriveFileReader.ReadAllBytesAsync(string.Empty, row.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3, 4 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), row.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), 4, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "b2-file-1", FileName = row.OdriveRelativePath!, ContentSha1 = "sha1", ContentLength = 4, Action = "upload", UploadTimestamp = 1 });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — falls back to the configured global bucket, and a warning is logged so the gap
        // is visible rather than silently absorbed.
        await fixture.FileIndexRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), "B2", ConfiguredBucket, row.OdriveRelativePath!, row.OdriveFileName!,
            Arg.Any<string?>(), 4, Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        GetFormattedLogMessages(fixture.Logger).Should().Contain(m => m.Contains("no bucket_name", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GetFormattedLogMessages<T>(ILogger<T> logger)
    {
        var messages = new List<string>();
        foreach (var call in logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[2] is null || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    // ---- Branch coverage: local O-Drive read failure -> SyncActionFailedLocal, not SyncActionFailedB2

    [Fact]
    public async Task Test_ProcessAsync_LocalReadFailure_WritesFailedLocalAudit()
    {
        // Arrange
        var fixture = new Fixture();
        var row = NewRow(100601);
        var task = ClaimedTask(fileIds: 100601);
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(row);
        fixture.ODriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), row.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Throws(new IOException("simulated local read failure"));
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.B2Client.DidNotReceive().GetUploadUrlAsync(Arg.Any<CancellationToken>());
        await fixture.SyncResultRepository.Received(1).UpdateStatusAsync(100601, Arg.Any<string>(), "Failed", task.TaskQueueId, Arg.Any<CancellationToken>());
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncActionFailedLocal && e.Result == AuditResult.Failure),
            Arg.Any<CancellationToken>());
    }

    // ==== CAS-029 — progress tracking (§3.3, §3.6) ===================================================

    private static string? CompletedResultDetail(ITaskQueueRepository repository) =>
        repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateStatusAsync)
                && (string)c.GetArguments()[2]! == "Completed")
            .Select(c => (string?)c.GetArguments()[3])
            .Single();

    private static List<(int Processed, int Total)> ProgressWrites(ITaskQueueRepository repository) =>
        repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateProgressAsync))
            .Select(c => ((int)c.GetArguments()[2]!, (int)c.GetArguments()[3]!))
            .ToList();

    /// <summary>
    /// CAS-029: a clock on which every timestamp read is one hour after the previous one, so the
    /// 15 s interval has always elapsed and every <c>ItemCompletedAsync</c> writes — making each
    /// per-file increment observable.
    /// </summary>
    private sealed class AlwaysElapsedTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerHour);
    }

    // ---- CAS-029 SC-5 (SyncCopy): per-file status + audit → final progress → Completed -------------

    [Fact]
    public async Task Test_SC5_SyncCopy_ResultsThenProgressThenCompleted()
    {
        // Arrange — file 1 succeeds, file 2 fails on the B2 upload.
        var fixture = new Fixture();
        var okRow = NewRow(100601, "finance/2026/ok.xlsx", ConfiguredBucket);
        var failRow = NewRow(100602, "finance/2026/fail.xlsx", ConfiguredBucket);
        var task = ClaimedTask(fileIds: new[] { 100601, 100602 });
        fixture.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(okRow);
        fixture.SyncResultRepository.GetAsync(100602, Arg.Any<CancellationToken>()).Returns(failRow);
        fixture.ODriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 });
        fixture.B2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), okRow.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "f1", FileName = okRow.OdriveRelativePath!, ContentSha1 = "s", ContentLength = 3, Action = "upload", UploadTimestamp = 1 });
        fixture.B2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), failRow.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "simulated"));
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — ordering (§3.6).
        Received.InOrder(() =>
        {
            fixture.SyncResultRepository.UpdateStatusAsync(100601, Arg.Any<string>(), "Synced", task.TaskQueueId, Arg.Any<CancellationToken>());
            fixture.AuditLogService.WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.SyncCopyToB2), Arg.Any<CancellationToken>());
            fixture.SyncResultRepository.UpdateStatusAsync(100602, Arg.Any<string>(), "Failed", task.TaskQueueId, Arg.Any<CancellationToken>());
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

    // ---- CAS-029 SC-7: every fileId counted once (missing row, per-file failure, success) ------------
    // One [Theory] across all three sync-action processors (CAS-029 §13).

    [Theory]
    [InlineData("SyncCopy")]
    [InlineData("SyncUpdate")]
    [InlineData("SyncPurge")]
    public async Task Test_SC7_SyncActions_EveryFileIdCountedOnce(string taskType)
    {
        // Arrange — 4 fileIds: (1) no sync_result row, (2) per-file failure (local read for
        // Copy/Update; B2 for Purge), (3) per-file B2 failure, (4) success.
        var syncResultRepository = Substitute.For<ISyncResultRepository>();
        var fileIndexRepository = Substitute.For<IFileIndexRepository>();
        var taskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var b2Client = Substitute.For<IB2Client>();
        var odriveFileReader = Substitute.For<IODriveFileReader>();
        var auditLogService = Substitute.For<IAuditLogService>();
        taskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        var factory = new TaskProgressReporterFactory(
            taskQueueRepository, new AlwaysElapsedTimeProvider(), Options.Create(new WorkerOptions()),
            Substitute.For<ILogger<TaskProgressReporter>>());

        static SyncResultRow AnyRow(int syncResultId, string relativePath) => new()
        {
            SyncResultId = syncResultId,
            TaskQueueId = 100301,
            ComparisonType = "New",
            SyncStatus = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            OdriveFileName = Path.GetFileName(relativePath),
            OdriveRelativePath = relativePath,
            OdriveBucketName = ConfiguredBucket,
            OdriveModifiedAt = DateTimeOffset.UtcNow,
            B2FileIndexId = syncResultId + 1000,
            B2FileName = Path.GetFileName(relativePath),
            B2RelativePath = relativePath
        };

        const string failFirstPath = "a/fail-first.txt";
        const string failSecondPath = "a/fail-second.txt";
        const string okPath = "a/ok.txt";
        syncResultRepository.GetAsync(1, Arg.Any<CancellationToken>()).Returns((SyncResultRow?)null);
        syncResultRepository.GetAsync(2, Arg.Any<CancellationToken>()).Returns(AnyRow(2, failFirstPath));
        syncResultRepository.GetAsync(3, Arg.Any<CancellationToken>()).Returns(AnyRow(3, failSecondPath));
        syncResultRepository.GetAsync(4, Arg.Any<CancellationToken>()).Returns(AnyRow(4, okPath));

        odriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2 });
        odriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), failFirstPath, Arg.Any<CancellationToken>()).Throws(new IOException("simulated read failure"));
        b2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>()).Returns(UploadUrl());
        b2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), okPath, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "f", FileName = okPath, ContentSha1 = "s", ContentLength = 2, Action = "upload", UploadTimestamp = 1 });
        b2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), failSecondPath, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "simulated"));
        b2Client.ListFileVersionsAsync(failFirstPath, failFirstPath, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "simulated"));
        b2Client.ListFileVersionsAsync(failSecondPath, failSecondPath, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new B2ApiException(500, "internal_error", "simulated"));
        b2Client.ListFileVersionsAsync(okPath, okPath, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new B2ListFileVersionsResult
            {
                Files = new[] { new B2FileInfo { FileId = "v1", FileName = okPath, ContentSha1 = "s", ContentLength = 2, Action = "upload", UploadTimestamp = 1 } }
            });

        IWorkerTaskProcessor sut = taskType switch
        {
            "SyncCopy" => new SyncCopyTaskProcessor(syncResultRepository, fileIndexRepository, taskQueueRepository, factory, b2Client,
                odriveFileReader, auditLogService, new ConfigurationBuilder().Build(), Substitute.For<ILogger<SyncCopyTaskProcessor>>()),
            "SyncUpdate" => new SyncUpdateTaskProcessor(syncResultRepository, fileIndexRepository, taskQueueRepository, factory, b2Client,
                odriveFileReader, auditLogService, Substitute.For<ILogger<SyncUpdateTaskProcessor>>()),
            _ => new SyncPurgeTaskProcessor(syncResultRepository, fileIndexRepository, taskQueueRepository, factory, b2Client,
                NotPresentRemover(), auditLogService, Substitute.For<ILogger<SyncPurgeTaskProcessor>>())
        };
        var task = new ClaimedTaskRow
        {
            TaskQueueId = 100499,
            TaskType = taskType,
            SubmittedByUserId = 9,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = JsonSerializer.Serialize(new SyncActionRequestParametersDto { SourceTaskId = 100301, FileIds = new[] { 1, 2, 3, 4 } })
        };

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — items_total = FileIds.Count; each fileId advanced the count by exactly one (every
        // item wrote, so a double count or a missed count would show); final (4, 4).
        ProgressWrites(taskQueueRepository).Should().Equal((0, 4), (1, 4), (2, 4), (3, 4), (4, 4), (4, 4));

        // Assert — the three non-success outcomes really happened, and the task still Completed.
        var summary = JsonSerializer.Deserialize<SyncActionResultSummaryDto>(CompletedResultDetail(taskQueueRepository)!)!;
        summary.SucceededCount.Should().Be(1);
        summary.FailedCount.Should().Be(3);
    }
}
