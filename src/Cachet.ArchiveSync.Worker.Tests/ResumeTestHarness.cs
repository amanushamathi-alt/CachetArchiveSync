using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// CAS-054 test harness: a <see cref="KeywordSearchTaskProcessor"/> over NSubstitute fakes with an
/// in-memory <c>dbo.search_result</c> store, serial candidate processing (deterministic) and a clock
/// on which every progress/watermark interval has always elapsed. Content candidates are
/// <c>.txt</c> files whose "mirror bytes" are their own relative path, so a match is decided purely by
/// membership in <see cref="MatchingFileIndexIds"/>.
/// </summary>
internal sealed class ResumeTestHarness
{
    public const string Term = "pioneer";
    public const string Bucket = "cachet-archive-prod";

    public IFileIndexRepository FileIndexRepository { get; } = Substitute.For<IFileIndexRepository>();
    public ISearchResultRepository SearchResultRepository { get; } = Substitute.For<ISearchResultRepository>();
    public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
    public IB2Client B2Client { get; } = Substitute.For<IB2Client>();
    public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
    public IMirrorFileReader MirrorFileReader { get; } = Substitute.For<IMirrorFileReader>();
    public IFileContentMatcher FileContentMatcher { get; } = Substitute.For<IFileContentMatcher>();

    /// <summary>In-memory dbo.search_result: (task_queue_id, file_index_id, match_type).</summary>
    public ConcurrentBag<(int TaskQueueId, int FileIndexId, string MatchType)> Store { get; } = new();

    public HashSet<int> MatchingFileIndexIds { get; } = new();

    /// <summary>Clock on which every timestamp read is an hour after the previous one.</summary>
    public sealed class AlwaysElapsedTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerHour);
    }

    public ResumeTestHarness()
    {
        TaskQueueRepository.UpdateProgressAsync(default, default!, default, default, default).ReturnsForAnyArgs(true);
        TaskQueueRepository.UpdateResumeWatermarkAsync(default, default!, default, default).ReturnsForAnyArgs(true);

        MirrorFileReader
            .ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Encoding.UTF8.GetBytes(ci.ArgAt<string>(2)));
        FileContentMatcher
            .TryMatchAsync(Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var path = Encoding.UTF8.GetString(ci.Arg<byte[]>());
                return MatchingFileIndexIds.Any(id => path == PathFor(id)) ? "...pioneer..." : (string?)null;
            });

        B2Client.ListFileNamesAsync(Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
            .Returns(ci => new B2ListFileNamesResult
            {
                Files = new[]
                {
                    new B2FileInfo
                    {
                        FileId = "id", FileName = ci.ArgAt<string>(0), ContentSha1 = "x",
                        ContentLength = 1, Action = "upload", UploadTimestamp = 1
                    }
                }
            });

        SearchResultRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Store.Add((ci.ArgAt<int>(2), ci.ArgAt<int>(3), ci.ArgAt<string>(4)));
                return 1;
            });

        SearchResultRepository
            .ListAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var rows = Store.Where(r => r.TaskQueueId == ci.ArgAt<int>(0)
                    && (ci.ArgAt<string?>(1) is null || r.MatchType == ci.ArgAt<string?>(1))).ToList();
                return ((IReadOnlyList<SearchResultRow>)new List<SearchResultRow>(), rows.Count);
            });
    }

    public static string PathFor(int fileIndexId) => $"docs/f{fileIndexId}.txt";

    public static FileIndexCandidateRow Candidate(int fileIndexId) => new()
    {
        FileIndexId = fileIndexId,
        FileName = $"f{fileIndexId}.txt",
        RelativePath = PathFor(fileIndexId),
        FileSizeBytes = 100,
        ModifiedAt = DateTimeOffset.UtcNow,
        MatchType = "Content",
        Source = "B2",
        BucketName = Bucket
    };

    /// <summary>Candidates with file_index_id 1..<paramref name="count"/>.</summary>
    public void SetCandidates(int count) =>
        FileIndexRepository
            .SearchCandidatesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateOnly?>(),
                Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(ci => (IReadOnlyList<FileIndexCandidateRow>)Enumerable.Range(1, count).Select(Candidate).ToList());

    public static ClaimedTaskRow Task(int taskQueueId, int? resumeWatermark = null, int? restartedFrom = null) => new()
    {
        TaskQueueId = taskQueueId,
        TaskType = "KeywordSearch",
        SubmittedByUserId = 7,
        SubmittedAt = DateTimeOffset.UtcNow,
        ResumeWatermarkFileIndexId = resumeWatermark,
        RestartedFromTaskQueueId = restartedFrom,
        RequestParameters = JsonSerializer.Serialize(new KeywordSearchRequestDto
        {
            Term = Term,
            Bucket = Bucket,
            Extension = "All"
        })
    };

    public KeywordSearchTaskProcessor CreateSut()
    {
        var time = new AlwaysElapsedTimeProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["B2:CrossCheckMaxConcurrency"] = "1",
            ["Worker:ContentMatchMaxConcurrency"] = "1"
        }).Build();

        return new KeywordSearchTaskProcessor(
            FileIndexRepository, SearchResultRepository, TaskQueueRepository,
            new TaskProgressReporterFactory(TaskQueueRepository, time, Options.Create(new WorkerOptions()),
                Substitute.For<ILogger<TaskProgressReporter>>()),
            B2Client, AuditLogService, MirrorFileReader, FileContentMatcher, configuration,
            Substitute.For<ILogger<KeywordSearchTaskProcessor>>(), time);
    }

    public List<(int Processed, int Total)> ProgressWrites() =>
        TaskQueueRepository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateProgressAsync))
            .Select(c => ((int)c.GetArguments()[2]!, (int)c.GetArguments()[3]!))
            .ToList();

    public List<int> WatermarkWrites() =>
        TaskQueueRepository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateResumeWatermarkAsync))
            .Select(c => (int)c.GetArguments()[2]!)
            .ToList();

    public KeywordSearchResultSummaryDto CompletedSummary() =>
        JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(
            TaskQueueRepository.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateStatusAsync)
                    && (string)c.GetArguments()[2]! == "Completed")
                .Select(c => (string)c.GetArguments()[3]!)
                .Single())!;

    public string? FinalStatus() =>
        TaskQueueRepository.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateStatusAsync))
            .Select(c => (string)c.GetArguments()[2]!)
            .LastOrDefault();

    public IEnumerable<int> StoredFileIndexIds(int taskQueueId) =>
        Store.Where(r => r.TaskQueueId == taskQueueId).Select(r => r.FileIndexId).OrderBy(i => i);
}
