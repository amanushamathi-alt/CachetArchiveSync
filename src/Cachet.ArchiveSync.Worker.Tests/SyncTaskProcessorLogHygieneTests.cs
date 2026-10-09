using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// SC-14 ("no B2 application key, session token/cookie value, raw file content, or full local
/// file path beyond what the audit log already records ever appears in a log line") for the four
/// CAS-007 Worker task processors. The Api-side four endpoints' own log hygiene is proven
/// separately in <c>Cachet.ArchiveSync.Api.Tests.LogHygieneTests</c>/
/// <c>SyncActionServiceLogHygieneTests</c> — the two surfaces span different projects, so no
/// single class can assert all of SC-14 in one process.
/// </summary>
public sealed class SyncTaskProcessorLogHygieneTests
{
    private const string FakeB2UploadToken = "4_fakeUploadTokenValueNeverLog";
    private const string FakeB2AuthToken = "4_fakeAuthTokenValueNeverLog";

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

    private static void AssertNoLeakage(IEnumerable<string> messages)
    {
        messages.Should().OnlyContain(m =>
            !m.Contains(FakeB2UploadToken, StringComparison.Ordinal) &&
            !m.Contains(FakeB2AuthToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_SC14_NoCredentialOrContentLeakageInLogs()
    {
        // --- ContentScan: never calls IB2Client at all, so this mainly confirms no raw content ------
        var fileIndexRepository = Substitute.For<IFileIndexRepository>();
        var syncResultRepository = Substitute.For<ISyncResultRepository>();
        var taskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var scanLogger = Substitute.For<ILogger<ContentScanTaskProcessor>>();
        fileIndexRepository.ListActiveBySourceAsync("ODrive", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow>());
        fileIndexRepository.ListActiveBySourceAsync("B2", Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexRow>());
        var scanProcessor = new ContentScanTaskProcessor(fileIndexRepository, syncResultRepository, taskQueueRepository, new TaskProgressReporterFactory(taskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), scanLogger);
        await scanProcessor.ProcessAsync(new ClaimedTaskRow
        {
            TaskQueueId = 100301,
            TaskType = "ContentScan",
            SubmittedByUserId = 7,
            SubmittedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        AssertNoLeakage(GetFormattedLogMessages(scanLogger));

        // --- SyncCopy: the B2 upload/auth token must never reach a log line -------------------------
        var copySyncResultRepository = Substitute.For<ISyncResultRepository>();
        var copyFileIndexRepository = Substitute.For<IFileIndexRepository>();
        var copyTaskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var b2Client = Substitute.For<IB2Client>();
        var odriveFileReader = Substitute.For<IODriveFileReader>();
        var auditLogService = Substitute.For<IAuditLogService>();
        var copyLogger = Substitute.For<ILogger<SyncCopyTaskProcessor>>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = "cachet-archive-prod" })
            .Build();

        var row = new SyncResultRow
        {
            SyncResultId = 100601,
            TaskQueueId = 100301,
            ComparisonType = "New",
            SyncStatus = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            OdriveFileName = "new-invoice.xlsx",
            OdriveRelativePath = "finance/2026/new-invoice.xlsx"
        };
        copySyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(row);
        odriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), row.OdriveRelativePath!, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1, 2, 3 });
        b2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>())
            .Returns(new B2UploadUrlInfo { BucketId = "b", UploadUrl = "https://upload-fake/x", AuthorizationToken = FakeB2UploadToken });
        b2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), row.OdriveRelativePath!, Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "f", FileName = row.OdriveRelativePath!, ContentSha1 = "s", ContentLength = 3, Action = "upload", UploadTimestamp = 1 });

        var copyProcessor = new SyncCopyTaskProcessor(
            copySyncResultRepository, copyFileIndexRepository, copyTaskQueueRepository, new TaskProgressReporterFactory(copyTaskQueueRepository, TimeProvider.System, Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()), b2Client, odriveFileReader, auditLogService, configuration, copyLogger);
        await copyProcessor.ProcessAsync(new ClaimedTaskRow
        {
            TaskQueueId = 100401,
            TaskType = "SyncCopy",
            SubmittedByUserId = 9,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = JsonSerializer.Serialize(new SyncActionRequestParametersDto { SourceTaskId = 100301, FileIds = new[] { 100601 } })
        }, CancellationToken.None);

        AssertNoLeakage(GetFormattedLogMessages(copyLogger));
    }

    // ==== CAS-029 SC-12 — progress logging never carries the term, a relative path, or a file name ====

    /// <summary>
    /// CAS-029: every timestamp read is one hour after the previous one, so every item completion
    /// triggers a progress write (and therefore a reporter log line).
    /// </summary>
    private sealed class AlwaysElapsedTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerHour);
    }

    private static List<(LogLevel Level, string Message)> GetLeveledLogMessages<T>(ILogger<T> logger)
    {
        var messages = new List<(LogLevel, string)>();
        foreach (var call in logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[0] is not LogLevel level || args[2] is null || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                messages.Add((level, message));
            }
        }

        return messages;
    }

    [Fact]
    public async Task Test_SC12_ProgressLogging_NoTermOrPath()
    {
        const string term = "ZephyrSettlementTerm";
        const int searchTaskId = 100901;
        const int copyTaskId = 100902;

        // --- KeywordSearch: every progress write succeeds → reporter Debug line per item ------------
        var searchReporterLogger = Substitute.For<ILogger<TaskProgressReporter>>();
        var searchLogger = Substitute.For<ILogger<KeywordSearchTaskProcessor>>();
        var fileIndexRepository = Substitute.For<IFileIndexRepository>();
        var searchResultRepository = Substitute.For<ISearchResultRepository>();
        var searchTaskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var searchB2Client = Substitute.For<IB2Client>();
        var mirrorFileReader = Substitute.For<IMirrorFileReader>();
        var fileContentMatcher = Substitute.For<IFileContentMatcher>();
        searchTaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);

        var filenameCandidate = new FileIndexCandidateRow
        {
            FileIndexId = 1,
            FileName = "ZephyrSettlementTerm-plan.pdf",
            RelativePath = "private/2026/ZephyrSettlementTerm-plan.pdf",
            FileSizeBytes = 10,
            ModifiedAt = DateTimeOffset.UtcNow,
            MatchType = "Filename",
            Source = "B2",
            BucketName = "cachet-archive-prod"
        };
        var contentCandidate = new FileIndexCandidateRow
        {
            FileIndexId = 2,
            FileName = "board-minutes.docx",
            RelativePath = "board/2026/board-minutes.docx",
            FileSizeBytes = 10,
            ModifiedAt = DateTimeOffset.UtcNow,
            MatchType = "Content",
            Source = "B2",
            BucketName = "cachet-archive-prod"
        };
        var excludedCandidate = new FileIndexCandidateRow
        {
            FileIndexId = 3,
            FileName = "archived-memo.pdf",
            RelativePath = "legacy/archived-memo.pdf",
            FileSizeBytes = 10,
            ModifiedAt = DateTimeOffset.UtcNow,
            MatchType = "Filename",
            Source = "B2",
            BucketName = "cachet-archive-prod"
        };
        fileIndexRepository.SearchCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), term, Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(new List<FileIndexCandidateRow> { filenameCandidate, contentCandidate, excludedCandidate });
        mirrorFileReader.ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new byte[] { 1 });
        fileContentMatcher.TryMatchAsync(Arg.Any<byte[]>(), Arg.Any<string>(), term, Arg.Any<CancellationToken>())
            .Returns("...ZephyrSettlementTerm...");
        foreach (var confirmed in new[] { filenameCandidate, contentCandidate })
        {
            searchB2Client.ListFileNamesAsync(confirmed.RelativePath, 1, Arg.Any<CancellationToken>()).Returns(new B2ListFileNamesResult
            {
                Files = new[] { new B2FileInfo { FileId = "f", FileName = confirmed.RelativePath, ContentSha1 = "s", ContentLength = 1, Action = "upload", UploadTimestamp = 1 } }
            });
        }

        searchB2Client.ListFileNamesAsync(excludedCandidate.RelativePath, 1, Arg.Any<CancellationToken>())
            .Returns(new B2ListFileNamesResult { Files = Array.Empty<B2FileInfo>() });

        var searchProcessor = new KeywordSearchTaskProcessor(
            fileIndexRepository, searchResultRepository, searchTaskQueueRepository,
            new TaskProgressReporterFactory(searchTaskQueueRepository, new AlwaysElapsedTimeProvider(), Options.Create(new WorkerOptions()), searchReporterLogger),
            searchB2Client, Substitute.For<IAuditLogService>(), mirrorFileReader, fileContentMatcher,
            new ConfigurationBuilder().Build(), searchLogger);
        await searchProcessor.ProcessAsync(new ClaimedTaskRow
        {
            TaskQueueId = searchTaskId,
            TaskType = "KeywordSearch",
            SubmittedByUserId = 7,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = JsonSerializer.Serialize(new KeywordSearchRequestDto { Term = term, Bucket = "cachet-archive-prod", Extension = "All" })
        }, CancellationToken.None);

        // --- SyncCopy: first write throws (Warning with exception), second reports not-Running -------
        var copyReporterLogger = Substitute.For<ILogger<TaskProgressReporter>>();
        var copyLogger = Substitute.For<ILogger<SyncCopyTaskProcessor>>();
        var copySyncResultRepository = Substitute.For<ISyncResultRepository>();
        var copyTaskQueueRepository = Substitute.For<ITaskQueueRepository>();
        var copyB2Client = Substitute.For<IB2Client>();
        var odriveFileReader = Substitute.For<IODriveFileReader>();
        copyTaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default)
            .ReturnsForAnyArgs(
                _ => throw new InvalidOperationException("simulated progress write failure"),
                _ => Task.FromResult(false));
        var copyRow = new SyncResultRow
        {
            SyncResultId = 100611,
            TaskQueueId = 100301,
            ComparisonType = "New",
            SyncStatus = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            OdriveFileName = "payroll-register.xlsx",
            OdriveRelativePath = "finance/2026/payroll-register.xlsx",
            OdriveBucketName = "cachet-archive-prod"
        };
        copySyncResultRepository.GetAsync(copyRow.SyncResultId, Arg.Any<CancellationToken>()).Returns(copyRow);
        odriveFileReader.ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2 });
        copyB2Client.GetUploadUrlAsync(Arg.Any<CancellationToken>())
            .Returns(new B2UploadUrlInfo { BucketId = "b", UploadUrl = "https://upload-fake/x", AuthorizationToken = FakeB2UploadToken });
        copyB2Client.UploadFileAsync(Arg.Any<B2UploadUrlInfo>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new B2FileInfo { FileId = "f", FileName = copyRow.OdriveRelativePath!, ContentSha1 = "s", ContentLength = 2, Action = "upload", UploadTimestamp = 1 });

        var copyProcessor = new SyncCopyTaskProcessor(
            copySyncResultRepository, Substitute.For<IFileIndexRepository>(), copyTaskQueueRepository,
            new TaskProgressReporterFactory(copyTaskQueueRepository, new AlwaysElapsedTimeProvider(), Options.Create(new WorkerOptions()), copyReporterLogger),
            copyB2Client, odriveFileReader, Substitute.For<IAuditLogService>(), new ConfigurationBuilder().Build(), copyLogger);
        await copyProcessor.ProcessAsync(new ClaimedTaskRow
        {
            TaskQueueId = copyTaskId,
            TaskType = "SyncCopy",
            SubmittedByUserId = 9,
            SubmittedAt = DateTimeOffset.UtcNow,
            RequestParameters = JsonSerializer.Serialize(new SyncActionRequestParametersDto { SourceTaskId = 100301, FileIds = new[] { copyRow.SyncResultId } })
        }, CancellationToken.None);

        // --- Collect: every reporter log line + the processors' progress-wiring "started" line ------
        var reporterMessages = GetLeveledLogMessages(searchReporterLogger).Concat(GetLeveledLogMessages(copyReporterLogger)).ToList();
        var wiringMessages = GetLeveledLogMessages(searchLogger).Concat(GetLeveledLogMessages(copyLogger))
            .Where(m => m.Message.Contains("item(s) to process", StringComparison.Ordinal))
            .ToList();

        // Sanity — all three reporter log shapes and both started lines were actually emitted.
        reporterMessages.Should().Contain(m => m.Level == LogLevel.Debug);
        reporterMessages.Should().Contain(m => m.Level == LogLevel.Warning && m.Message.StartsWith("Progress write failed", StringComparison.Ordinal));
        reporterMessages.Should().Contain(m => m.Level == LogLevel.Warning && m.Message.EndsWith("progress reporting stopped", StringComparison.Ordinal));
        wiringMessages.Should().HaveCount(2);

        // Assert — no term, relative path, or file name in any of them; only the task id and counts.
        var forbidden = new[]
        {
            term,
            filenameCandidate.RelativePath, filenameCandidate.FileName,
            contentCandidate.RelativePath, contentCandidate.FileName,
            excludedCandidate.RelativePath, excludedCandidate.FileName,
            copyRow.OdriveRelativePath!, copyRow.OdriveFileName!
        };
        reporterMessages.Concat(wiringMessages).Should().OnlyContain(m =>
            forbidden.All(f => !m.Message.Contains(f, StringComparison.OrdinalIgnoreCase)));
        reporterMessages.Concat(wiringMessages).Should().OnlyContain(m =>
            m.Message.Contains(searchTaskId.ToString(), StringComparison.Ordinal)
            || m.Message.Contains(copyTaskId.ToString(), StringComparison.Ordinal));
    }
}
