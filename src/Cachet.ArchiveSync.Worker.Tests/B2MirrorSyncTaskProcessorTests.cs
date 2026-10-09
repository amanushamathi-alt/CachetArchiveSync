using System.Security.Cryptography;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// Unit tests for <see cref="B2MirrorSyncTaskProcessor"/> (CAS-037 §6/§7/§12) — the spec's §13 Test
/// Plan "B2MirrorSyncTaskProcessorTests" assignment: SC-1..SC-5, SC-7, V2, V4, V5 — plus the CAS-059
/// tests (disk-check skip rule, b2_path_prefix scope, verified download, path containment). Every dependency
/// (<see cref="IFileIndexRepository"/>, <see cref="ITaskQueueRepository"/>, <see cref="IB2Client"/>,
/// <see cref="IBucketMirrorRootRepository"/>, <see cref="IAuditLogService"/>) is an NSubstitute
/// fake — never a real B2 call. Tests that exercise the actual download-and-write branch use a real
/// temporary directory as the mirror root (the processor itself uses <see cref="Directory"/>/
/// <see cref="FileStream"/> directly, with no filesystem seam to mock), cleaned up in a
/// try/finally.
/// </summary>
public sealed class B2MirrorSyncTaskProcessorTests
{
    private const string ConfiguredBucket = "cachet-archive-prod";

    private sealed class Fixture
    {
        public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public IB2Client B2Client { get; } = Substitute.For<IB2Client>();
        public IBucketMirrorRootRepository BucketMirrorRootRepository { get; } = Substitute.For<IBucketMirrorRootRepository>();
        public IB2PathPrefixProvider PrefixProvider { get; } = Substitute.For<IB2PathPrefixProvider>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public ILogger<B2MirrorSyncTaskProcessor> Logger { get; } = Substitute.For<ILogger<B2MirrorSyncTaskProcessor>>();
        public IConfiguration Configuration { get; }

        public Fixture(IDictionary<string, string?>? additionalConfig = null)
        {
            var values = new Dictionary<string, string?> { ["B2:BucketName"] = ConfiguredBucket };
            if (additionalConfig is not null)
            {
                foreach (var kvp in additionalConfig)
                {
                    values[kvp.Key] = kvp.Value;
                }
            }

            Configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

            // Default: bucket-root prefix (""), so the pre-CAS-059 tests see the whole listing.
            PrefixProvider.GetPrefixAsync(ConfiguredBucket, Arg.Any<CancellationToken>()).Returns(string.Empty);
        }

        public B2MirrorSyncTaskProcessor CreateSut(TaskProgressReporterFactory? progressReporterFactoryOverride = null) =>
            new(FileIndexRepository, TaskQueueRepository,
                progressReporterFactoryOverride ?? new TaskProgressReporterFactory(TaskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()),
                B2Client, BucketMirrorRootRepository, PrefixProvider, AuditLogService, Configuration, Logger);
    }

    private static ClaimedTaskRow ClaimedTask(int taskQueueId = 100701) => new()
    {
        TaskQueueId = taskQueueId,
        TaskType = "B2MirrorSync",
        SubmittedByUserId = 9,
        SubmittedAt = DateTimeOffset.UtcNow,
        RequestParameters = null
    };

    private static B2FileInfo UploadFile(
        string relativePath, string contentSha1, long contentLength = 3,
        long uploadTimestampMs = 1_700_000_000_000, string action = "upload") => new()
        {
            FileId = "b2-file-" + relativePath,
            FileName = relativePath,
            ContentSha1 = contentSha1,
            ContentLength = contentLength,
            Action = action,
            UploadTimestamp = uploadTimestampMs
        };

    private static FileIndexRow ExistingRow(int fileIndexId, string bucketName, string relativePath, string? sha1Hash) => new()
    {
        FileIndexId = fileIndexId,
        Source = "B2",
        BucketName = bucketName,
        RelativePath = relativePath,
        FileName = Path.GetFileName(relativePath),
        FileSizeBytes = 100,
        ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
        Sha1Hash = sha1Hash
    };

    private static B2DownloadedFile Downloaded(B2FileInfo file, byte[] bytes) => new()
    {
        FileName = file.FileName,
        ContentSha1 = file.ContentSha1,
        ContentLength = bytes.Length,
        Content = new MemoryStream(bytes)
    };

    private static string ExpectedExtension(string relativePath) =>
        Path.GetExtension(relativePath).TrimStart('.').ToUpperInvariant();

    private static string CreateTempRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cas037-tests-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a per-test temp directory; never fail a test over it.
        }
    }

    private static string? CompletedResultDetail(ITaskQueueRepository repository) =>
        repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateStatusAsync)
                && (string)c.GetArguments()[2]! == "Completed")
            .Select(c => (string?)c.GetArguments()[3])
            .SingleOrDefault();

    private static List<(int Processed, int Total)> ProgressWrites(ITaskQueueRepository repository) =>
        repository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateProgressAsync))
            .Select(c => ((int)c.GetArguments()[2]!, (int)c.GetArguments()[3]!))
            .ToList();

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

    /// <summary>Every timestamp read is one hour after the previous one, so the progress reporter's
    /// interval has always elapsed and every <c>ItemCompletedAsync</c> writes (CAS-029 pattern,
    /// reused from <c>SyncCopyTaskProcessorTests</c>).</summary>
    private sealed class AlwaysElapsedTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerHour);
    }

    // ---- SC-1: N new objects insert exactly N new dbo.file_index rows, correctly populated --------

    [Fact]
    public async Task Test_SC1_NewObjectsInsertedCorrectly()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var file1 = UploadFile("finance/2026/invoice.pdf", "sha1-one", contentLength: 4);
            var file2 = UploadFile("hr/2026/policy.docx", "sha1-two", contentLength: 5);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file1, file2 }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(file1.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file1, new byte[] { 1, 2, 3, 4 }));
            fixture.B2Client.DownloadFileByIdAsync(file2.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file2, new byte[] { 1, 2, 3, 4, 5 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — one InsertAsync per new object, all fields correctly populated.
            await fixture.FileIndexRepository.Received(1).InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), "B2", ConfiguredBucket, file1.FileName, Path.GetFileName(file1.FileName),
                ExpectedExtension(file1.FileName), file1.ContentLength, DateTimeOffset.FromUnixTimeMilliseconds(file1.UploadTimestamp),
                file1.ContentSha1, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
            await fixture.FileIndexRepository.Received(1).InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), "B2", ConfiguredBucket, file2.FileName, Path.GetFileName(file2.FileName),
                ExpectedExtension(file2.FileName), file2.ContentLength, DateTimeOffset.FromUnixTimeMilliseconds(file2.UploadTimestamp),
                file2.ContentSha1, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());

            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.NewCount.Should().Be(2);
            summary.UpdatedCount.Should().Be(0);
            summary.FailedCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void WriteMirrorFile(string root, string relativePath, byte[] bytes, long? lastWriteUnixMs = null)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        if (lastWriteUnixMs is not null)
        {
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(lastWriteUnixMs.Value).UtcDateTime);
        }
    }

    private static string Sha1Hex(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();

    // ---- SC-2 / V-3: unchanged rerun downloads nothing, only touches last_seen_at ------------------

    [Fact]
    public async Task Test_SC2_UnchangedRerunSkipsAllDownloads()
    {
        // Arrange — two B2 objects whose dbo.file_index.sha1_hash already equals contentSha1 and
        // whose mirror files are already on disk with the expected size (CAS-059).
        var fixture = new Fixture();
        var task = ClaimedTask();
        var file1 = UploadFile("finance/2026/invoice.pdf", "SHA1-ONE");
        var file2 = UploadFile("hr/2026/policy.docx", "sha1-two");
        var existing1 = ExistingRow(501, ConfiguredBucket, file1.FileName, "sha1-one"); // case-insensitive match
        var existing2 = ExistingRow(502, ConfiguredBucket, file2.FileName, "sha1-two");
        var root = CreateTempRoot();
        try
        {
            // Mirror files as a previous Mirror Sync left them: expected size, last-write = upload time.
            WriteMirrorFile(root, file1.FileName, new byte[] { 1, 2, 3 }, file1.UploadTimestamp);
            WriteMirrorFile(root, file2.FileName, new byte[] { 1, 2, 3 }, file2.UploadTimestamp);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>())
                .Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file1, file2 }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow> { existing1, existing2 });
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — no downloads at all, no inserts, only last_seen_at-only updates.
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            await fixture.FileIndexRepository.DidNotReceive().InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
            // Note: sha1Hash and modifiedAt are declared with explicit Arg.Is null-matchers (rather
            // than a raw `null` literal) because NSubstitute cannot disambiguate a raw literal from
            // a matcher used elsewhere for the SAME parameter type in one call (string is also
            // updatedBy's type; DateTimeOffset? is also lastSeenAt's type).
            await fixture.FileIndexRepository.Received(1).UpdateAsync(
                existing1.FileIndexId, Arg.Any<string>(), Arg.Is<string?>(s => s == null), null,
                Arg.Is<DateTimeOffset?>(d => d == null), Arg.Any<DateTimeOffset?>(), null, Arg.Any<CancellationToken>());
            await fixture.FileIndexRepository.Received(1).UpdateAsync(
                existing2.FileIndexId, Arg.Any<string>(), Arg.Is<string?>(s => s == null), null,
                Arg.Is<DateTimeOffset?>(d => d == null), Arg.Any<DateTimeOffset?>(), null, Arg.Any<CancellationToken>());

            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.NewCount.Should().Be(0);
            summary.UpdatedCount.Should().Be(0);
            summary.SkippedCount.Should().Be(2);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- SC-3: a changed contentSha1 is re-downloaded and the row fully updated ---------------------

    [Fact]
    public async Task Test_SC3_ChangedObjectRedownloadedAndUpdated()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var file = UploadFile("finance/2026/invoice.pdf", "new-sha1", contentLength: 9);
            var existing = ExistingRow(601, ConfiguredBucket, file.FileName, "old-sha1");
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow> { existing });
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — re-downloaded, and the row's sha1/size/modified/last_seen all updated.
            await fixture.B2Client.Received(1).DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>());
            await fixture.FileIndexRepository.Received(1).UpdateAsync(
                existing.FileIndexId, Arg.Any<string>(), file.ContentSha1, file.ContentLength,
                DateTimeOffset.FromUnixTimeMilliseconds(file.UploadTimestamp), Arg.Any<DateTimeOffset?>(), true, Arg.Any<CancellationToken>());

            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.UpdatedCount.Should().Be(1);
            summary.NewCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- SC-4: downloaded bytes are written under the mirror root at exactly relative_path ---------

    [Fact]
    public async Task Test_SC4_DownloadedFileWrittenAtExpectedPath()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var bytes = new byte[] { 9, 8, 7, 6, 5 };
            var file = UploadFile("legal/2026/nested/contract.pdf", "sha1-x", contentLength: bytes.Length);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, bytes));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — bytes land at exactly Path.Combine(root, relativePath).
            var expectedPath = Path.Combine(root, file.FileName);
            File.Exists(expectedPath).Should().BeTrue();
            (await File.ReadAllBytesAsync(expectedPath)).Should().Equal(bytes);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- SC-5: measurable, monotonically increasing progress throughout the run --------------------

    [Fact]
    public async Task Test_SC5_ProgressReportedThroughoutRun()
    {
        // Arrange — three new objects; a time provider on which the report interval has always
        // elapsed, so every item completion writes progress (same pattern as the CAS-029 tests for
        // the other processors).
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var files = new[]
            {
                UploadFile("a/one.txt", "s1"),
                UploadFile("a/two.txt", "s2"),
                UploadFile("a/three.txt", "s3")
            };
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = files, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            foreach (var file in files)
            {
                fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                    .Returns(_ => Downloaded(file, new byte[] { 1, 2, 3 }));
            }
            fixture.TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
            var factory = new TaskProgressReporterFactory(
                fixture.TaskQueueRepository, new AlwaysElapsedTimeProvider(), Options.Create(new WorkerOptions()),
                Substitute.For<ILogger<TaskProgressReporter>>());
            var sut = fixture.CreateSut(factory);

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — progress starts at (0, 3), advances by exactly one per item, ends at (3, 3):
            // never a stuck "Running" with no detail (FR-22a).
            var writes = ProgressWrites(fixture.TaskQueueRepository);
            writes.Should().Equal((0, 3), (1, 3), (2, 3), (3, 3), (3, 3));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- V-2: hide/folder/start objects are never downloaded or written to dbo.file_index ----------

    [Fact]
    public async Task Test_V2_NonUploadActionObjectsSkipped()
    {
        // Arrange — one real upload plus one of each non-upload action B2 can return.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var uploadFile = UploadFile("a/real-file.txt", "sha1-real", contentLength: 3);
            var hideFile = UploadFile("a/hidden-file.txt", "sha1-hidden", action: "hide");
            var folderFile = UploadFile("a/some-folder/", "n/a", action: "folder");
            var startFile = UploadFile("a/incomplete-large-file.bin", "n/a", action: "start");
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { hideFile, folderFile, startFile, uploadFile }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(uploadFile.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(uploadFile, new byte[] { 1, 2, 3 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — only the "upload" object was downloaded/inserted; the other three untouched.
            await fixture.B2Client.Received(1).DownloadFileByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(hideFile.FileId, Arg.Any<CancellationToken>());
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(folderFile.FileId, Arg.Any<CancellationToken>());
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(startFile.FileId, Arg.Any<CancellationToken>());
            await fixture.FileIndexRepository.Received(1).InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), "B2", ConfiguredBucket, uploadFile.FileName, Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());

            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.NewCount.Should().Be(1);
            summary.UpdatedCount.Should().Be(0);
            summary.SkippedCount.Should().Be(0);
            summary.FailedCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- V-4: one object's failure does not abort the run --------------------------------------------

    [Fact]
    public async Task Test_V4_PerFileFailureDoesNotAbortRun()
    {
        // Arrange — the first object's download throws, the second succeeds.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var failingFile = UploadFile("a/fails.txt", "sha1-fail");
            var okFile = UploadFile("a/ok.txt", "sha1-ok", contentLength: 2);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { failingFile, okFile }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(failingFile.FileId, Arg.Any<CancellationToken>())
                .Throws(new IOException("simulated download failure"));
            fixture.B2Client.DownloadFileByIdAsync(okFile.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(okFile, new byte[] { 1, 2 }));
            var sut = fixture.CreateSut();

            // Act
            var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — does not throw, the good file still succeeds, task still reaches Completed.
            await act.Should().NotThrowAsync();
            await fixture.FileIndexRepository.Received(1).InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), "B2", ConfiguredBucket, okFile.FileName, Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
            await fixture.FileIndexRepository.DidNotReceive().InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), "B2", ConfiguredBucket, failingFile.FileName, Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
            await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
                task.TaskQueueId, Arg.Any<string>(), "Completed", Arg.Any<string?>(), Arg.Any<CancellationToken>());

            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.NewCount.Should().Be(1);
            summary.FailedCount.Should().Be(1);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- V-5: a missing/inactive mirror-root mapping fails the whole task outright -------------------

    [Fact]
    public async Task Test_V5_MissingMirrorRootFailsTask()
    {
        // Arrange — IBucketMirrorRootRepository throws (no active odrive_bucket_mapping row).
        var fixture = new Fixture();
        var task = ClaimedTask();
        fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("No active odrive_bucket_mapping row found for the requested bucket."));
        var sut = fixture.CreateSut();

        // Act
        var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

        // Assert — never throws out of ProcessAsync; task fails outright before enumeration; no
        // completion audit row (§12 — only a Completed run gets one).
        await act.Should().NotThrowAsync();
        await fixture.B2Client.DidNotReceive().ListFileNamesAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Failed",
            Arg.Is<string?>(d => d != null && d.Contains("FailureReason")),
            Arg.Any<CancellationToken>());

        var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(
            fixture.TaskQueueRepository.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateStatusAsync)
                    && (string)c.GetArguments()[2]! == "Failed")
                .Select(c => (string?)c.GetArguments()[3])
                .Single()!)!;
        summary.FailureReason.Should().NotBeNullOrEmpty();
    }

    // ---- SC-7: no B2 key value ever appears in a log line or audit row for this flow -----------------

    [Fact]
    public async Task Test_SC7_NoKeyLeakage()
    {
        // Arrange — decoy system-key config values this processor has no reason to ever read; a
        // passing test proves neither one leaks into a log line or the completion audit row's
        // ResultDetail (same "no leakage" pattern as B2ClientTests/SyncTaskProcessorLogHygieneTests).
        const string FakeSystemKeyId = "9999999999990000000000099";
        const string FakeSystemApplicationKey = "K999NeverLoggedApplicationKeyValue";
        var fixture = new Fixture(new Dictionary<string, string?>
        {
            ["B2:SystemKeyId"] = FakeSystemKeyId,
            ["B2:SystemApplicationKey"] = FakeSystemApplicationKey
        });
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var file = UploadFile("finance/2026/report.pdf", "sha1-new", contentLength: 3);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, new byte[] { 1, 2, 3 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — no log line...
            var messages = GetFormattedLogMessages(fixture.Logger);
            messages.Should().OnlyContain(m =>
                !m.Contains(FakeSystemKeyId, StringComparison.Ordinal) &&
                !m.Contains(FakeSystemApplicationKey, StringComparison.Ordinal));

            // ...and no audit_log row's ResultDetail carries the key or its id.
            var auditEntries = fixture.AuditLogService.ReceivedCalls()
                .Select(c => (AuditLogEntry)c.GetArguments()[0]!)
                .ToList();
            auditEntries.Should().NotBeEmpty();
            auditEntries.Should().OnlyContain(e =>
                e.ResultDetail == null ||
                (!e.ResultDetail.Contains(FakeSystemKeyId, StringComparison.Ordinal) &&
                 !e.ResultDetail.Contains(FakeSystemApplicationKey, StringComparison.Ordinal)));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- Branch coverage: missing B2:BucketName config fails the task outright (RequireConfig) ------

    [Fact]
    public async Task Test_ProcessAsync_MissingBucketNameConfig_FailsTask()
    {
        // Arrange — no B2:BucketName configured at all.
        var fixture = new Fixture(new Dictionary<string, string?> { ["B2:BucketName"] = null });
        var task = ClaimedTask();
        var sut = fixture.CreateSut();

        // Act
        var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        await fixture.BucketMirrorRootRepository.DidNotReceive().GetRootPathAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
            task.TaskQueueId, Arg.Any<string>(), "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- Branch coverage: the outer catch never lets an unhandled exception escape ProcessAsync -----

    [Fact]
    public async Task Test_ProcessAsync_UnhandledExceptionDuringEnumeration_MarksTaskFailed()
    {
        // Arrange
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>())
                .Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Throws(new InvalidOperationException("simulated B2 enumeration failure"));
            var sut = fixture.CreateSut();

            // Act
            var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

            // Assert
            await act.Should().NotThrowAsync();
            await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
                task.TaskQueueId, Arg.Any<string>(), "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- CAS-059: a Copy-created row (SHA-1 already in the index) with no mirror file is downloaded --

    [Fact]
    public async Task Test_CAS059_ShaMatchButMirrorFileMissing_IsDownloaded()
    {
        // Arrange — the index row's SHA-1 equals contentSha1 (as SyncCopy leaves it), but the
        // mirror file does not exist.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var bytes = new byte[] { 4, 5, 6, 7 };
            var file = UploadFile("D/Shares/Operations/NewFiles/copied.txt", Sha1Hex(bytes), contentLength: bytes.Length);
            var existing = ExistingRow(701, ConfiguredBucket, file.FileName, file.ContentSha1);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow> { existing });
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, bytes));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — downloaded to the mirror, row updated (not skipped).
            (await File.ReadAllBytesAsync(Path.Combine(root, file.FileName))).Should().Equal(bytes);
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.UpdatedCount.Should().Be(1);
            summary.SkippedCount.Should().Be(0);
            summary.FailedCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Test_CAS059_ShaMatchButMirrorFileSizeDiffers_IsDownloaded()
    {
        // Arrange — SHA-1 matches but the mirror file is truncated (wrong size).
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            var file = UploadFile("a/truncated.txt", Sha1Hex(bytes), contentLength: bytes.Length);
            var existing = ExistingRow(702, ConfiguredBucket, file.FileName, file.ContentSha1);
            WriteMirrorFile(root, file.FileName, new byte[] { 1, 2 });
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow> { existing });
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, bytes));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert
            await fixture.B2Client.Received(1).DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>());
            (await File.ReadAllBytesAsync(Path.Combine(root, file.FileName))).Should().Equal(bytes);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- CAS-059: same-size content change (e.g. after Update in B2) is detected --------------------

    [Fact]
    public async Task Test_CAS059_SameSizeUpdatedContent_Redownloaded()
    {
        // Arrange — SyncUpdate left the index on the NEW SHA-1 (= B2), but the mirror still has the
        // OLD content of the same size, dated before the new upload.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var oldBytes = new byte[] { 1, 1, 1 };
            var newBytes = new byte[] { 2, 2, 2 };
            var file = UploadFile("a/updated.txt", Sha1Hex(newBytes), contentLength: 3, uploadTimestampMs: 1_700_000_500_000);
            var existing = ExistingRow(801, ConfiguredBucket, file.FileName, file.ContentSha1);
            WriteMirrorFile(root, file.FileName, oldBytes, lastWriteUnixMs: 1_700_000_000_000);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow> { existing });
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, newBytes));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — the new content replaced the old one and is dated to the upload time.
            var mirrorPath = Path.Combine(root, file.FileName);
            (await File.ReadAllBytesAsync(mirrorPath)).Should().Equal(newBytes);
            File.GetLastWriteTimeUtc(mirrorPath).Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(file.UploadTimestamp).UtcDateTime);
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.UpdatedCount.Should().Be(1);
            summary.SkippedCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Test_CAS059_CurrentFileWithOtherDate_VerifiedByHashAndDateAdopted()
    {
        // Arrange — a correct mirror file placed by the refresh script (different last-write time).
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var bytes = new byte[] { 5, 6, 7 };
            var file = UploadFile("a/script-placed.txt", Sha1Hex(bytes), contentLength: 3);
            var existing = ExistingRow(802, ConfiguredBucket, file.FileName, file.ContentSha1);
            WriteMirrorFile(root, file.FileName, bytes, lastWriteUnixMs: 1_600_000_000_000);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow> { existing });
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — no download, counted as skipped, upload time adopted for the next run's cheap path.
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            File.GetLastWriteTimeUtc(Path.Combine(root, file.FileName))
                .Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(file.UploadTimestamp).UtcDateTime);
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.SkippedCount.Should().Be(1);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- CAS-059: path containment works for a drive-root mirror root --------------------------------

    [Theory]
    [InlineData(@"E:\", @"E:\D\Shares\Operations\a.txt")]
    [InlineData(@"E:\B2mirror", @"E:\B2mirror\D\Shares\Operations\a.txt")]
    [InlineData(@"E:\B2mirror\", @"E:\B2mirror\D\Shares\Operations\a.txt")]
    [InlineData(@"\\svsws10\B2mirror", @"\\svsws10\B2mirror\D\Shares\Operations\a.txt")]
    [InlineData(@"\\svsws10\B2mirror\", @"\\svsws10\B2mirror\D\Shares\Operations\a.txt")]
    public void Test_CAS059_ResolveInsideRoot_RootForms(string root, string expected)
    {
        B2MirrorSyncTaskProcessor.ResolveInsideRoot(root, "D/Shares/Operations/a.txt").Should().Be(expected);
    }

    [Fact]
    public void Test_CAS059_ResolveInsideRoot_SiblingOfRootRejected()
    {
        // "E:\B2mirror-other" must not count as inside "E:\B2mirror".
        var act = () => B2MirrorSyncTaskProcessor.ResolveInsideRoot(@"E:\B2mirror", "../B2mirror-other/x.txt");
        act.Should().Throw<InvalidOperationException>();
    }

    // ---- CAS-059: only objects under b2_path_prefix are processed -------------------------------------

    [Fact]
    public async Task Test_CAS059_OnlyObjectsUnderPrefixProcessed()
    {
        // Arrange — prefix "D/Shares/Operations"; listing starts at the prefix and returns two
        // in-prefix objects followed by one outside it (B2 sorted order) plus a NextFileName.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            fixture.PrefixProvider.GetPrefixAsync(ConfiguredBucket, Arg.Any<CancellationToken>()).Returns("D/Shares/Operations");
            var inside1 = UploadFile("D/Shares/Operations/a.txt", "s-a");
            var inside2 = UploadFile("D/Shares/Operations/sub/b.txt", "s-b");
            var outside = UploadFile("D/Shares/Other/c.txt", "s-c");
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync("D/Shares/Operations/", Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { inside1, inside2, outside }, NextFileName = "D/Shares/Other/d.txt" });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(inside1, new byte[] { 1, 2, 3 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — the outside object is never downloaded or indexed, and paging stops there.
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(outside.FileId, Arg.Any<CancellationToken>());
            await fixture.B2Client.Received(1).ListFileNamesAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
            await fixture.FileIndexRepository.DidNotReceive().InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), "B2", ConfiguredBucket, outside.FileName, Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.NewCount.Should().Be(2);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Test_CAS059_PageFullyInsidePrefix_NextPageFetched()
    {
        // Arrange — page 1 is entirely inside the prefix and has a NextFileName, so page 2 must be
        // fetched; page 2 ends with the first object outside the prefix, which stops paging.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            fixture.PrefixProvider.GetPrefixAsync(ConfiguredBucket, Arg.Any<CancellationToken>()).Returns("D/Shares/Operations");
            var a = UploadFile("D/Shares/Operations/a.txt", "s-a");
            var b = UploadFile("D/Shares/Operations/b.txt", "s-b");
            var c = UploadFile("D/Shares/Operations/c.txt", "s-c");
            var outside = UploadFile("D/Shares/Other/d.txt", "s-d");
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync("D/Shares/Operations/", Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { a, b }, NextFileName = c.FileName });
            fixture.B2Client.ListFileNamesAsync(c.FileName, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { c, outside }, NextFileName = "D/Shares/Other/e.txt" });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(a, new byte[] { 1, 2, 3 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — exactly two list calls, all three in-prefix objects processed, the outside one not.
            await fixture.B2Client.Received(2).ListFileNamesAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
            await fixture.B2Client.Received(1).DownloadFileByIdAsync(c.FileId, Arg.Any<CancellationToken>());
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(outside.FileId, Arg.Any<CancellationToken>());
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.NewCount.Should().Be(3);
            summary.FailedCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Test_CAS059_NoPrefixMapping_FailsTask()
    {
        // Arrange — no active mapping row: the prefix provider returns null.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.PrefixProvider.GetPrefixAsync(ConfiguredBucket, Arg.Any<CancellationToken>()).Returns((string?)null);
            var sut = fixture.CreateSut();

            // Act
            var act = async () => await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — fails before any enumeration.
            await act.Should().NotThrowAsync();
            await fixture.B2Client.DidNotReceive().ListFileNamesAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
            await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(
                task.TaskQueueId, Arg.Any<string>(), "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- CAS-059: verified download — a SHA-1 mismatch never replaces the mirror file -----------------

    [Fact]
    public async Task Test_CAS059_Sha1Mismatch_ExistingFileKeptAndNoPartialLeft()
    {
        // Arrange — an existing mirror file with an old SHA-1; the download's bytes do not match
        // the B2 object's (real-format) contentSha1.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var oldBytes = new byte[] { 9, 9, 9 };
            var file = UploadFile("a/report.txt", Sha1Hex(new byte[] { 1, 2, 3 }), contentLength: 3);
            var existing = ExistingRow(703, ConfiguredBucket, file.FileName, Sha1Hex(oldBytes));
            WriteMirrorFile(root, file.FileName, oldBytes);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow> { existing });
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, new byte[] { 7, 7, 7 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — counted as failed, old file untouched, no .cachet-partial left, row not updated.
            var mirrorPath = Path.Combine(root, file.FileName);
            (await File.ReadAllBytesAsync(mirrorPath)).Should().Equal(oldBytes);
            File.Exists(mirrorPath + ".cachet-partial").Should().BeFalse();
            await fixture.FileIndexRepository.DidNotReceive().UpdateAsync(
                existing.FileIndexId, Arg.Any<string>(), file.ContentSha1, Arg.Any<long?>(),
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.FailedCount.Should().Be(1);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- CAS-059: an object name that escapes the mirror root is rejected ----------------------------

    [Fact]
    public async Task Test_CAS059_NameOutsideMirrorRoot_Rejected()
    {
        // Arrange — a crafted B2 name with "..".
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var file = UploadFile("../escape.txt", "s-x");
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — never downloaded, nothing written outside the root, not indexed. The name is
            // skipped by the path-name rules (V12) before the containment check (V9) is reached.
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
            File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape.txt")).Should().BeFalse();
            await fixture.FileIndexRepository.DidNotReceive().InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.NewCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- CAS-059: same name filters as scripts/index_refresh (excluded names, unusable names) --------

    private async Task<(Fixture Fixture, MirrorSyncResultSummaryDto Summary)> RunWithFilesAsync(string root, params B2FileInfo[] files)
    {
        var fixture = new Fixture();
        fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
        fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new B2ListFileNamesResult { Files = files, NextFileName = null });
        fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow>());
        fixture.B2Client.DownloadFileByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Downloaded(files[0], new byte[] { 1, 2, 3 }));

        await fixture.CreateSut().ProcessAsync(ClaimedTask(), CancellationToken.None);

        var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
        return (fixture, summary);
    }

    [Theory]
    [InlineData("D/Shares/Operations/NewFolder/.bzEmpty")]
    [InlineData("a/Thumbs.db")]
    [InlineData("a/THUMBS.DB")]
    [InlineData("a/desktop.ini")]
    [InlineData("a/.DS_Store")]
    [InlineData("a/~$Report.docx")]
    [InlineData("a/report.pdf.cachet-partial")]
    public async Task Test_CAS059_ExcludedNames_Ignored(string excludedName)
    {
        // Arrange — the excluded object first, then a normal one.
        var root = CreateTempRoot();
        try
        {
            var excluded = UploadFile(excludedName, "s-x");
            var normal = UploadFile("a/normal.txt", "s-n");

            // Act
            var (fixture, summary) = await RunWithFilesAsync(root, normal, excluded);

            // Assert — never downloaded or indexed, and not counted at all (same as the refresh script).
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(excluded.FileId, Arg.Any<CancellationToken>());
            summary.NewCount.Should().Be(1);
            summary.FailedCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData("a/report:v2.txt")]
    [InlineData("a/what?.txt")]
    [InlineData("a//double-slash.txt")]
    [InlineData("a/./dot.txt")]
    [InlineData("a/file.extensiontoolong")]
    [InlineData("a/b\\c.txt")]
    [InlineData("a/..\\..\\escape.txt")]
    [InlineData("a/line\r\nbreak.txt")]
    public async Task Test_CAS059_UnusableNames_SkippedNotFailed(string unusableName)
    {
        var root = CreateTempRoot();
        try
        {
            var unusable = UploadFile(unusableName, "s-u");
            var normal = UploadFile("a/normal.txt", "s-n");

            var (fixture, summary) = await RunWithFilesAsync(root, normal, unusable);

            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(unusable.FileId, Arg.Any<CancellationToken>());
            summary.NewCount.Should().Be(1);
            summary.FailedCount.Should().Be(0);
            GetFormattedLogMessages(fixture.Logger).Should().Contain(m => m.Contains("skipped", StringComparison.Ordinal));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Test_CAS059_SkippedNameWithControlChars_LoggedWithoutThem()
    {
        // Arrange — an object name carrying CR/LF (a forged-looking log line).
        var root = CreateTempRoot();
        try
        {
            var forged = UploadFile("a/x\r\n[ERR] fake entry.txt", "s-f");
            var normal = UploadFile("a/normal.txt", "s-n");

            // Act
            var (fixture, _) = await RunWithFilesAsync(root, normal, forged);

            // Assert — the skip warning is written, and no log line contains a CR or LF.
            var messages = GetFormattedLogMessages(fixture.Logger);
            messages.Should().Contain(m => m.Contains("skipped", StringComparison.Ordinal) && m.Contains("fake entry", StringComparison.Ordinal));
            messages.Should().OnlyContain(m => !m.Contains('\r') && !m.Contains('\n'));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Test_CAS059_NameTooLong_Skipped()
    {
        var root = CreateTempRoot();
        try
        {
            var longName = UploadFile("a/" + new string('x', 501) + ".txt", "s-l");
            var normal = UploadFile("a/normal.txt", "s-n");

            var (fixture, summary) = await RunWithFilesAsync(root, normal, longName);

            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(longName.FileId, Arg.Any<CancellationToken>());
            summary.NewCount.Should().Be(1);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Test_CAS059_NamesDifferingOnlyByCase_LaterOneSkipped()
    {
        var root = CreateTempRoot();
        try
        {
            var first = UploadFile("a/Report.pdf", "s-1");
            var caseTwin = UploadFile("a/report.pdf", "s-2");

            var (fixture, summary) = await RunWithFilesAsync(root, first, caseTwin);

            await fixture.B2Client.Received(1).DownloadFileByIdAsync(first.FileId, Arg.Any<CancellationToken>());
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(caseTwin.FileId, Arg.Any<CancellationToken>());
            summary.NewCount.Should().Be(1);
            summary.FailedCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- CAS-059: folder-marker objects ("…/") are ignored, not counted as failures ------------------

    [Fact]
    public async Task Test_CAS059_FolderMarkerObject_Ignored()
    {
        // Arrange — B2 returns the zero-byte "D/Shares/Operations/" marker before a real file.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            fixture.PrefixProvider.GetPrefixAsync(ConfiguredBucket, Arg.Any<CancellationToken>()).Returns("D/Shares/Operations");
            var marker = UploadFile("D/Shares/Operations/", "da39a3ee5e6b4b0d3255bfef95601890afd80709", contentLength: 0);
            var file = UploadFile("D/Shares/Operations/a.txt", "s-a");
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync("D/Shares/Operations/", Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { marker, file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, new byte[] { 1, 2, 3 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — the marker is neither downloaded, indexed nor counted as failed.
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(marker.FileId, Arg.Any<CancellationToken>());
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.NewCount.Should().Be(1);
            summary.FailedCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- CAS-059: a sibling folder sharing the prefix text is outside the sync folder ----------------

    [Fact]
    public async Task Test_CAS059_SiblingFolderWithSamePrefixText_Excluded()
    {
        // Arrange — "D/Shares/Operations2/…" sorts right after "D/Shares/Operations/…" in B2 order.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            fixture.PrefixProvider.GetPrefixAsync(ConfiguredBucket, Arg.Any<CancellationToken>()).Returns("D/Shares/Operations");
            var inside = UploadFile("D/Shares/Operations/a.txt", "s-a");
            var sibling = UploadFile("D/Shares/Operations2/b.txt", "s-b");
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync("D/Shares/Operations/", Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { inside, sibling }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(inside.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(inside, new byte[] { 1, 2, 3 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert
            await fixture.B2Client.DidNotReceive().DownloadFileByIdAsync(sibling.FileId, Arg.Any<CancellationToken>());
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.NewCount.Should().Be(1);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    // ---- CAS-059: verified download — size mismatch, "none" and "unverified:" SHA-1 values -----------

    [Fact]
    public async Task Test_CAS059_DownloadSizeMismatch_Rejected()
    {
        // Arrange — B2 says 5 bytes, the download delivers 3 (truncated).
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var file = UploadFile("a/short.txt", "none", contentLength: 5);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, new byte[] { 1, 2, 3 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert — nothing written, no partial left, not indexed, counted as failed.
            var mirrorPath = Path.Combine(root, file.FileName);
            File.Exists(mirrorPath).Should().BeFalse();
            File.Exists(mirrorPath + ".cachet-partial").Should().BeFalse();
            await fixture.FileIndexRepository.DidNotReceive().InsertAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.FailedCount.Should().Be(1);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unverified:{0}")]
    public async Task Test_CAS059_NoneOrUnverifiedSha1_Accepted(string contentSha1Format)
    {
        // Arrange — "none" (large multi-part upload: size-only check) and "unverified:<hex>" (SHA-1
        // supplied by the uploader: checked against the hex part).
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var bytes = new byte[] { 3, 1, 4, 1 };
            var file = UploadFile("a/large.bin", string.Format(contentSha1Format, Sha1Hex(bytes)), contentLength: bytes.Length);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, bytes));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert
            (await File.ReadAllBytesAsync(Path.Combine(root, file.FileName))).Should().Equal(bytes);
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.NewCount.Should().Be(1);
            summary.FailedCount.Should().Be(0);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task Test_CAS059_UnverifiedSha1Mismatch_Rejected()
    {
        // Arrange — "unverified:<hex>" whose hex does not match the downloaded bytes.
        var fixture = new Fixture();
        var task = ClaimedTask();
        var root = CreateTempRoot();
        try
        {
            var file = UploadFile("a/tampered.bin", "unverified:" + Sha1Hex(new byte[] { 9, 9, 9 }), contentLength: 3);
            fixture.BucketMirrorRootRepository.GetRootPathAsync(ConfiguredBucket, "B2", Arg.Any<CancellationToken>()).Returns(root);
            fixture.B2Client.ListFileNamesAsync(null, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(new B2ListFileNamesResult { Files = new[] { file }, NextFileName = null });
            fixture.FileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
                .Returns(new List<FileIndexRow>());
            fixture.B2Client.DownloadFileByIdAsync(file.FileId, Arg.Any<CancellationToken>())
                .Returns(_ => Downloaded(file, new byte[] { 1, 2, 3 }));
            var sut = fixture.CreateSut();

            // Act
            await sut.ProcessAsync(task, CancellationToken.None);

            // Assert
            File.Exists(Path.Combine(root, file.FileName)).Should().BeFalse();
            var summary = JsonSerializer.Deserialize<MirrorSyncResultSummaryDto>(CompletedResultDetail(fixture.TaskQueueRepository)!)!;
            summary.FailedCount.Should().Be(1);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }
}
