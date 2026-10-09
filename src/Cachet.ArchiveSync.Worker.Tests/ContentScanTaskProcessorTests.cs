using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// Unit tests for <see cref="ContentScanTaskProcessor"/> (CAS-007 §3/§5/§12) — the spec's §13 Test
/// Plan "ContentScanTaskProcessorTests" assignment: SC-2, SC-3, V9, V10. Every dependency
/// (<see cref="IFileIndexRepository"/>, <see cref="ISyncResultRepository"/>,
/// <see cref="ITaskQueueRepository"/>) is an NSubstitute fake; this processor never depends on
/// <see cref="IB2Client"/> at all (§3 — local-mirror-vs-local-mirror only comparison).
/// </summary>
public sealed class ContentScanTaskProcessorTests
{
    private sealed class Fixture
    {
        public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
        public ISyncResultRepository SyncResultRepository { get; } = Substitute.For<ISyncResultRepository>();
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public ILogger<ContentScanTaskProcessor> Logger { get; } = Substitute.For<ILogger<ContentScanTaskProcessor>>();

        public ContentScanTaskProcessor CreateSut(TaskProgressReporterFactory? progressReporterFactoryOverride = null) =>
            new(FileIndexRepository, SyncResultRepository, TaskQueueRepository, progressReporterFactoryOverride ?? new TaskProgressReporterFactory(TaskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), Logger);
    }

    private static ClaimedTaskRow ClaimedTask(int taskQueueId = 100301) => new()
    {
        TaskQueueId = taskQueueId,
        TaskType = "ContentScan",
        SubmittedByUserId = 7,
        SubmittedAt = DateTimeOffset.UtcNow,
        RequestParameters = null
    };

    private static FileIndexRow Row(int fileIndexId, string source, string bucket, string relativePath, string? sha1) => new()
    {
        FileIndexId = fileIndexId,
        Source = source,
        BucketName = bucket,
        RelativePath = relativePath,
        FileName = Path.GetFileName(relativePath),
        FileSizeBytes = 1000,
        ModifiedAt = DateTimeOffset.UtcNow,
        Sha1Hash = sha1
    };

    // ---- SC-2: classifies all four cases (only-O-Drive/only-B2/matching-SHA1/differing-SHA1) --------

    [Fact]
    public async Task Test_SC2_ContentScanProcessorClassifiesAllFourCases()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();

        var onlyOnODrive = Row(1, "ODrive", "cachet-archive-prod", "finance/2026/new-invoice.xlsx", "aaa1");
        var onlyOnB2 = Row(2, "B2", "cachet-archive-prod", "contracts/2015/old-contract.pdf", "bbb2");
        var matchingBothSides_ODrive = Row(3, "ODrive", "cachet-archive-prod", "unchanged/report.pdf", "ccc3");
        var matchingBothSides_B2 = Row(3, "B2", "cachet-archive-prod", "unchanged/report.pdf", "ccc3");
        var differingBothSides_ODrive = Row(4, "ODrive", "cachet-archive-prod", "hr/policy.docx", "ddd4-new");
        var differingBothSides_B2 = Row(5, "B2", "cachet-archive-prod", "hr/policy.docx", "ddd4-old");

        fixture.FileIndexRepository.ListActiveBySourceAsync("ODrive", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { onlyOnODrive, matchingBothSides_ODrive, differingBothSides_ODrive });
        fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { onlyOnB2, matchingBothSides_B2, differingBothSides_B2 });

        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — New: only-on-O-Drive.
        await fixture.SyncResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, "New", onlyOnODrive.FileIndexId, null, null, Arg.Any<CancellationToken>());

        // Assert — Removed: only-on-B2.
        await fixture.SyncResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, "Removed", null, onlyOnB2.FileIndexId, null, Arg.Any<CancellationToken>());

        // Assert — Modified: differing SHA-1 on both sides.
        await fixture.SyncResultRepository.Received(1).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId, "Modified",
            differingBothSides_ODrive.FileIndexId, differingBothSides_B2.FileIndexId, true, Arg.Any<CancellationToken>());

        // Assert — matching SHA-1 pair produces no row at all (exactly 3 inserts total).
        await fixture.SyncResultRepository.Received(3).InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());

        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- SC-3: task completes with per-comparison_type counts, even when zero disagreements found ---

    [Fact]
    public async Task Test_SC3_ScanTaskCompletesEvenWithZeroDisagreements()
    {
        // Arrange — identical single row on both sides, same SHA-1: no disagreements at all.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var odriveRow = Row(10, "ODrive", "cachet-archive-prod", "unchanged/report.pdf", "same-hash");
        var b2Row = Row(11, "B2", "cachet-archive-prod", "unchanged/report.pdf", "same-hash");
        fixture.FileIndexRepository.ListActiveBySourceAsync("ODrive", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { odriveRow });
        fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { b2Row });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — Completed, zero inserts, result_detail carries the (zero) counts.
        await fixture.SyncResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId,
            Arg.Any<string>(),
            "Completed",
            Arg.Is<string?>(detail => IsZeroCountCompletionSummary(detail)),
            Arg.Any<CancellationToken>());
    }

    private static bool IsZeroCountCompletionSummary(string? detail)
    {
        if (detail is null)
        {
            return false;
        }

        var summary = JsonSerializer.Deserialize<ContentScanResultSummaryDto>(detail);
        return summary is not null
            && summary.NewCount == 0
            && summary.RemovedCount == 0
            && summary.ModifiedCount == 0
            && summary.FailureReason is null;
    }

    // ---- V9: an unchanged pair (equal SHA-1 on both sides) never produces a sync_result row ---------

    [Fact]
    public async Task Test_V9_UnchangedFilePairProducesNoSyncResultRow()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        var odriveRow = Row(20, "ODrive", "cachet-archive-prod", "unchanged/report.pdf", "identical-hash");
        var b2Row = Row(21, "B2", "cachet-archive-prod", "unchanged/report.pdf", "identical-hash");
        fixture.FileIndexRepository.ListActiveBySourceAsync("ODrive", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { odriveRow });
        fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { b2Row });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await fixture.SyncResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), "Modified", Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
    }

    // ---- V10: a Modified pair where either side's sha1_hash is NULL is skipped, not flagged ---------

    [Theory]
    [InlineData(null, "some-hash")]
    [InlineData("some-hash", null)]
    [InlineData(null, null)]
    public async Task Test_V10_UnhashedFilePairSkippedNotFlaggedModified(string? odriveSha1, string? b2Sha1)
    {
        // Arrange — present on both sides (would otherwise be a candidate for Modified), but at
        // least one side has not yet been hashed by the (out-of-scope) mirror-population process.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var odriveRow = Row(30, "ODrive", "cachet-archive-prod", "unhashed/file.bin", odriveSha1);
        var b2Row = Row(31, "B2", "cachet-archive-prod", "unhashed/file.bin", b2Sha1);
        fixture.FileIndexRepository.ListActiveBySourceAsync("ODrive", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { odriveRow });
        fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { b2Row });
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — no row inserted at all for this key (not treated as New/Removed/Modified).
        await fixture.SyncResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
    }

    // ---- CAS-013 SC-3: a copied file's B2-side bucket_name round-trips through the NEXT ContentScan --
    // ---- with no false Removed/New pair — chains a real SyncCopyTaskProcessor into a real -----------
    // ---- ContentScanTaskProcessor, capturing what SyncCopy actually stamped rather than assuming it --

    [Fact]
    public async Task Test_SC3_CopiedFileBucketNameRoundTripsThroughNextContentScan()
    {
        // Arrange — run a real SyncCopyTaskProcessor for a source row whose O-Drive-side bucket_name
        // ("cachet-archive-bucket-a") differs from the single global B2:BucketName config value
        // ("cachet-archive-prod-default") — if the old unconditional-global-config-stamp bug were
        // still present, the new B2-side row would get the WRONG bucket_name here.
        const string sourceBucketName = "cachet-archive-bucket-a";
        const string globalConfiguredBucket = "cachet-archive-prod-default";
        const string relativePath = "finance/2026/round-trip-invoice.xlsx";

        var copySyncResultRepository = Substitute.For<ISyncResultRepository>();
        var copyFileIndexRepository = Substitute.For<IFileIndexRepository>();
        var copyTaskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var b2Client = Substitute.For<IB2Client>();
        var odriveFileReader = Substitute.For<IODriveFileReader>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var copyLogger = Substitute.For<ILogger<SyncCopyTaskProcessor>>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = globalConfiguredBucket })
            .Build();

        var sourceRow = new SyncResultRow
        {
            SyncResultId = 100601,
            TaskQueueId = 100301,
            ComparisonType = "New",
            SyncStatus = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            OdriveFileName = Path.GetFileName(relativePath),
            OdriveRelativePath = relativePath,
            OdriveBucketName = sourceBucketName,
            OdriveModifiedAt = DateTimeOffset.UtcNow
        };
        copySyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(sourceRow);
        odriveFileReader.ReadAllBytesAsync(sourceBucketName, relativePath, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 4, 2, 0 });
        b2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>())
            .Returns(new B2UploadUrlInfo { BucketId = "b", UploadUrl = "https://upload-fake/x", AuthorizationToken = "tok" });
        b2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), relativePath, Arg.Any<Stream>(), Arg.Any<string>(), 3, Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "f", FileName = relativePath, ContentSha1 = "s", ContentLength = 3, Action = "upload", UploadTimestamp = 1 });

        string? capturedB2BucketName = null;
        string? capturedSha1 = null;
        copyFileIndexRepository
            .InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), "B2", Arg.Any<string?>(), relativePath, Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(callInfo =>
            {
                capturedB2BucketName = callInfo.ArgAt<string?>(3);
                capturedSha1 = callInfo.ArgAt<string?>(9);
            });

        var copyProcessor = new SyncCopyTaskProcessor(
            copySyncResultRepository, copyFileIndexRepository, copyTaskQueueRepository, new TaskProgressReporterFactory(copyTaskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), b2Client, odriveFileReader, auditLogService, configuration, copyLogger);

        // Act (1) — SyncCopy runs and inserts the new B2-side dbo.file_index row.
        await copyProcessor.ProcessAsync(new ClaimedTaskRow
        {
            TaskQueueId = 100401,
            TaskType = "SyncCopy",
            SubmittedByUserId = 9,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = JsonSerializer.Serialize(new SyncActionRequestParametersDto { SourceTaskId = 100301, FileIds = new[] { 100601 } })
        }, CancellationToken.None);

        capturedB2BucketName.Should().Be(sourceBucketName, "the new B2-side row must inherit the SOURCE row's bucket_name, not the global config value");
        capturedB2BucketName.Should().NotBe(globalConfiguredBucket);
        capturedSha1.Should().NotBeNullOrEmpty();

        // Arrange (2) — feed the O-Drive-side source row and a B2-side row using the ACTUAL captured
        // bucket_name (not an assumption) into a fresh ContentScan, at the same relative_path and
        // sha1_hash (an unmodified copy has identical content on both sides).
        var scanFixture = new Fixture();
        var odriveSideRow = Row(1, "ODrive", sourceBucketName, relativePath, capturedSha1);
        var b2SideRow = Row(2, "B2", capturedB2BucketName!, relativePath, capturedSha1);
        scanFixture.FileIndexRepository.ListActiveBySourceAsync("ODrive", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { odriveSideRow });
        scanFixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { b2SideRow });
        var scanSut = scanFixture.CreateSut();

        // Act (2) — run ContentScan against the two mirror-side rows.
        await scanSut.ProcessAsync(ClaimedTask(100302), CancellationToken.None);

        // Assert — the (bucket_name, relative_path) join key matched on both sides (CAS-013 §1/§2's
        // "already correct, no filesystem access involved" claim), so no false Removed/New pair was
        // produced for this file — a broken bucket_name propagation would instead have produced BOTH
        // a Removed (O-Drive-side key not found among B2-side keys) and a New (B2-side key not found
        // among O-Drive-side keys) row here.
        await scanFixture.SyncResultRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: unhandled failure marks the task Failed, never throws out of ProcessAsync

    // ==== CAS-029 — progress tracking (§3.2, §3.6) ===================================================

    private static List<(int Processed, int Total)> ProgressWrites(ITaskQueueRepository repository) =>
        repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateProgressAsync))
            .Select(c => ((int)c.GetArguments()[2]!, (int)c.GetArguments()[3]!))
            .ToList();

    private static string? CompletedResultDetail(ITaskQueueRepository repository) =>
        repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateStatusAsync)
                && (string)c.GetArguments()[2]! == "Completed")
            .Select(c => (string?)c.GetArguments()[3])
            .Single();

    // ---- CAS-029 SC-5 (ContentScan): result rows → final progress → Completed -----------------------

    [Fact]
    public async Task Test_SC5_ContentScan_ResultsThenProgressThenCompleted()
    {
        // Arrange — one New, one Modified, one Removed key (union = 3 keys).
        var fixture = new Fixture();
        var task = ClaimedTask();
        var newOnODrive = Row(1, "ODrive", "cachet-archive-prod", "finance/new.xlsx", "aaa");
        var modifiedODrive = Row(2, "ODrive", "cachet-archive-prod", "hr/policy.docx", "new-hash");
        var modifiedB2 = Row(3, "B2", "cachet-archive-prod", "hr/policy.docx", "old-hash");
        var removedB2 = Row(4, "B2", "cachet-archive-prod", "contracts/old.pdf", "ddd");
        fixture.FileIndexRepository.ListActiveBySourceAsync("ODrive", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { newOnODrive, modifiedODrive });
        fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { modifiedB2, removedB2 });
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — ordering (§3.6): every sync_result insert, then the final (3, 3), then Completed.
        Received.InOrder(() =>
        {
            fixture.SyncResultRepository.InsertAsync(Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId,
                "New", newOnODrive.FileIndexId, null, null, Arg.Any<CancellationToken>());
            fixture.SyncResultRepository.InsertAsync(Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId,
                "Modified", modifiedODrive.FileIndexId, modifiedB2.FileIndexId, true, Arg.Any<CancellationToken>());
            fixture.SyncResultRepository.InsertAsync(Arg.Any<string>(), Arg.Any<string>(), task.TaskQueueId,
                "Removed", null, removedB2.FileIndexId, null, Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.UpdateProgressAsync(task.TaskQueueId, Arg.Any<string>(), 3, 3, Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.UpdateStatusAsync(task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        });

        // Assert — summary counts equal the result-row writes.
        var resultRowWrites = fixture.SyncResultRepository.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(ISyncResultRepository.InsertAsync));
        var summary = JsonSerializer.Deserialize<ContentScanResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        (summary.NewCount + summary.RemovedCount + summary.ModifiedCount).Should().Be(resultRowWrites).And.Be(3);
        summary.NewCount.Should().Be(1);
        summary.ModifiedCount.Should().Be(1);
        summary.RemovedCount.Should().Be(1);
    }

    // ---- CAS-029 SC-6: items_total = |ODrive ∪ B2 keys|; output set unchanged by the restructure ----

    [Fact]
    public async Task Test_SC6_ContentScan_UnionTotal_OutputSetUnchanged()
    {
        // Arrange — New, Removed, Modified, equal-hash, null-hash (O-Drive side), null-hash (B2 side),
        // plus a same-path/different-bucket pair (distinct keys: one New, one Removed).
        var fixture = new Fixture();
        var task = ClaimedTask();
        var newOnly = Row(1, "ODrive", "bucket-a", "finance/new.xlsx", "aaa");
        var modifiedO = Row(2, "ODrive", "bucket-a", "hr/policy.docx", "NEW-HASH");
        var modifiedB = Row(12, "B2", "bucket-a", "hr/policy.docx", "old-hash");
        var equalO = Row(3, "ODrive", "bucket-a", "unchanged/report.pdf", "ccc3");
        var equalB = Row(13, "B2", "bucket-a", "unchanged/report.pdf", "ccc3");
        var nullHashO = Row(4, "ODrive", "bucket-a", "legacy/no-hash-odrive.doc", null);
        var nullHashOB = Row(14, "B2", "bucket-a", "legacy/no-hash-odrive.doc", "eee");
        var nullHashBO = Row(5, "ODrive", "bucket-a", "legacy/no-hash-b2.doc", "fff");
        var nullHashB = Row(15, "B2", "bucket-a", "legacy/no-hash-b2.doc", null);
        var removedOnly = Row(16, "B2", "bucket-a", "contracts/old.pdf", "ggg");
        var otherBucketO = Row(6, "ODrive", "bucket-a", "shared/same-path.txt", "hhh");
        var otherBucketB = Row(17, "B2", "bucket-b", "shared/same-path.txt", "hhh");

        fixture.FileIndexRepository.ListActiveBySourceAsync("ODrive", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { newOnly, modifiedO, equalO, nullHashO, nullHashBO, otherBucketO });
        fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow> { modifiedB, equalB, nullHashOB, nullHashB, removedOnly, otherBucketB });
        fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — the (comparison_type, odrive_id, b2_id, sha1_changed) set matches the pre-change
        // (three-pass) algorithm's classification rules (CAS-007 §5; V9 equal-hash and V10 null-hash
        // produce no row).
        var produced = fixture.SyncResultRepository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ISyncResultRepository.InsertAsync))
            .Select(c => ((string)c.GetArguments()[3]!, (int?)c.GetArguments()[4], (int?)c.GetArguments()[5], (bool?)c.GetArguments()[6]))
            .ToList();
        produced.Should().BeEquivalentTo(new List<(string, int?, int?, bool?)>
        {
            ("New", newOnly.FileIndexId, null, null),
            ("New", otherBucketO.FileIndexId, null, null),
            ("Modified", modifiedO.FileIndexId, modifiedB.FileIndexId, true),
            ("Removed", null, removedOnly.FileIndexId, null),
            ("Removed", null, otherBucketB.FileIndexId, null)
        });

        // Assert — union of keys: 6 O-Drive keys + 2 B2-only keys = 8; start (0, 8), final (8, 8).
        var writes = ProgressWrites(fixture.TaskQueueRepository);
        writes.First().Should().Be((0, 8));
        writes.Last().Should().Be((8, 8));
        writes.Should().OnlyContain(w => w.Total == 8);
    }

    [Fact]
    public async Task Test_ProcessAsync_UnhandledException_MarksTaskFailed()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        fixture.FileIndexRepository.ListActiveBySourceAsync("ODrive", Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("simulated database failure"));
        var sut = fixture.CreateSut();

        // Act
        var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
