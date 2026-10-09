using System.Text;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// CAS-056 §13 "KeywordSearchCancelTests": the Worker's cooperative-cancel path in
/// <see cref="KeywordSearchTaskProcessor"/> (V6-V9, SC-2, SC-4). Built on <see cref="ResumeTestHarness"/>'s fakes
/// (serial/low-concurrency, in-memory search_result store) with a <see cref="FakeTimeProvider"/> driving the
/// <see cref="CancelRequestWatcher"/>'s poll timer, so there are no real sleeps: every wait is a
/// <see cref="TaskCompletionSource"/> gate or a fake-clock <c>Advance</c>. Methods named <c>Cov_*</c> are not map items.
/// </summary>
public sealed class KeywordSearchCancelTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(20);

    private enum BlockOn
    {
        /// <summary>The content (mirror) read of the blocked candidate hangs until its token is cancelled.</summary>
        Read,

        /// <summary>The B2 cross-check call of the blocked candidate hangs until its token is cancelled.</summary>
        B2
    }

    /// <summary>One processor run over fakes, paused inside a chosen candidate until the test cancels it.</summary>
    private sealed class Scenario
    {
        private readonly object _gate = new();
        private readonly List<string> _events = new();
        private volatile bool _flag;

        public ResumeTestHarness Harness { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public CancellationTokenSource Host { get; } = new();
        public TaskCompletionSource BlockedStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FlagObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BlockedTokenCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken BlockedToken { get; private set; }
        public List<string> ReadPaths { get; } = new();
        public List<string> B2Paths { get; } = new();
        public Task Run { get; private set; } = Task.CompletedTask;

        public IReadOnlyList<string> Events
        {
            get { lock (_gate) { return _events.ToList(); } }
        }

        public void Record(string evt)
        {
            lock (_gate)
            {
                _events.Add(evt);
            }
        }

        public Scenario(int candidates, int[] matching, int blockAtFileIndexId, BlockOn blockOn, int concurrency = 1)
        {
            Harness.SetCandidates(candidates);
            Harness.MatchingFileIndexIds.UnionWith(matching);
            var blockedPath = ResumeTestHarness.PathFor(blockAtFileIndexId);

            async Task<byte[]> Read(NSubstitute.Core.CallInfo ci)
            {
                var path = ci.ArgAt<string>(2);
                lock (_gate) { ReadPaths.Add(path); }

                if (blockOn == BlockOn.Read && path == blockedPath)
                {
                    await HangAsync(ci.Arg<CancellationToken>());
                }

                return Encoding.UTF8.GetBytes(path);
            }

            async Task<B2ListFileNamesResult> B2(NSubstitute.Core.CallInfo ci)
            {
                var path = ci.ArgAt<string>(0);
                lock (_gate) { B2Paths.Add(path); }

                if (blockOn == BlockOn.B2 && path == blockedPath)
                {
                    await HangAsync(ci.Arg<CancellationToken>());
                }

                return new B2ListFileNamesResult
                {
                    Files = new[]
                    {
                        new B2FileInfo
                        {
                            FileId = "id", FileName = path, ContentSha1 = "x",
                            ContentLength = 1, Action = "upload", UploadTimestamp = 1
                        }
                    }
                };
            }

            Harness.MirrorFileReader
                .ReadAllBytesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci => Read(ci));
            Harness.B2Client
                .ListFileNamesAsync(Arg.Any<string>(), 1, Arg.Any<CancellationToken>())
                .Returns(ci => B2(ci));

            Harness.TaskQueueRepository
                .IsCancelRequestedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    if (_flag)
                    {
                        FlagObserved.TrySetResult();
                    }

                    return Task.FromResult(_flag);
                });
            Harness.TaskQueueRepository
                .UpdateProgressAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Record("progress");
                    return Task.FromResult(true);
                });
            Harness.TaskQueueRepository
                .UpdateStatusAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    Record("status:" + ci.ArgAt<string>(2));
                    return Task.CompletedTask;
                });
            Harness.AuditLogService
                .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    Record("audit:" + ci.Arg<AuditLogEntry>().Action);
                    return Task.FromResult(1);
                });

            Concurrency = concurrency;
        }

        public int Concurrency { get; }

        private async Task HangAsync(CancellationToken token)
        {
            BlockedToken = token;
            token.Register(() => BlockedTokenCancelled.TrySetResult());
            BlockedStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }

        public KeywordSearchTaskProcessor CreateSut(string pollSeconds = "2")
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["B2:CrossCheckMaxConcurrency"] = Concurrency.ToString(),
                ["Worker:ContentMatchMaxConcurrency"] = Concurrency.ToString(),
                ["Worker:CancelPollIntervalSeconds"] = pollSeconds
            }).Build();

            return new KeywordSearchTaskProcessor(
                Harness.FileIndexRepository, Harness.SearchResultRepository, Harness.TaskQueueRepository,
                new TaskProgressReporterFactory(Harness.TaskQueueRepository, new ResumeTestHarness.AlwaysElapsedTimeProvider(),
                    Options.Create(new WorkerOptions()), Substitute.For<ILogger<TaskProgressReporter>>()),
                Harness.B2Client, Harness.AuditLogService, Harness.MirrorFileReader, Harness.FileContentMatcher, configuration,
                Substitute.For<ILogger<KeywordSearchTaskProcessor>>(), Time);
        }

        public void Start(KeywordSearchTaskProcessor? sut = null) =>
            Run = Task.Run(() => (sut ?? CreateSut()).ProcessAsync(ResumeTestHarness.Task(100), Host.Token));

        /// <summary>Raises the flag and advances the fake clock one poll interval; waits until the watcher has seen it.</summary>
        public async Task RaiseFlagAsync(int seconds = 2)
        {
            _flag = true;
            Time.Advance(TimeSpan.FromSeconds(seconds));
            await FlagObserved.Task.WaitAsync(Safety);
        }
    }

    private static KeywordSearchResultSummaryDto SummaryOf(IEnumerable<NSubstitute.Core.ICall> calls, string status) =>
        JsonSerializer.Deserialize<KeywordSearchResultSummaryDto>(
            calls.Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateStatusAsync)
                    && (string)c.GetArguments()[2]! == status)
                .Select(c => (string)c.GetArguments()[3]!)
                .Single())!;

    private static IEnumerable<NSubstitute.Core.ICall> CallsOf(Scenario s) => s.Harness.TaskQueueRepository.ReceivedCalls();

    // ---- V6: the Worker observes the flag within the poll interval and stops dispatching candidates ----------

    [Fact]
    public async Task Test_V6_WorkerStopsAfterFlag()
    {
        // Arrange - 1000 candidates; the 3rd candidate's content read hangs so the run is parked mid-search.
        var s = new Scenario(1000, new[] { 1, 2, 3 }, blockAtFileIndexId: 3, BlockOn.Read);
        s.Start();
        await s.BlockedStarted.Task.WaitAsync(Safety);

        // Act 1 - flag raised but less than one poll interval elapses: not observed yet.
        s.Time.Advance(TimeSpan.FromSeconds(1));
        await s.Harness.TaskQueueRepository.DidNotReceive().IsCancelRequestedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        s.Run.IsCompleted.Should().BeFalse();

        // Act 2 - the interval completes: the poll sees the flag and the run stops.
        await s.RaiseFlagAsync(seconds: 1);
        await s.Run.WaitAsync(Safety);

        // Assert - no candidate beyond the in-flight one was ever dispatched.
        s.ReadPaths.Should().Equal(
            ResumeTestHarness.PathFor(1), ResumeTestHarness.PathFor(2), ResumeTestHarness.PathFor(3));
        s.Harness.FinalStatus().Should().Be("Cancelled");
        s.Host.IsCancellationRequested.Should().BeFalse("a user cancel is not a host shutdown");
    }

    // ---- SC-2: a running search stops promptly; in-flight B2 calls get the (cancelled) linked token ----------

    [Fact]
    public async Task Test_SC2_RunningCancelStopsPromptly()
    {
        // Arrange - a large candidate set; the 5th candidate's B2 cross-check hangs.
        var s = new Scenario(5000, Enumerable.Range(1, 5000).ToArray(), blockAtFileIndexId: 5, BlockOn.B2);
        s.Start();
        await s.BlockedStarted.Task.WaitAsync(Safety);
        s.BlockedToken.IsCancellationRequested.Should().BeFalse();

        // Act - one poll interval after the cancel call.
        await s.RaiseFlagAsync();
        await s.Run.WaitAsync(Safety);

        // Assert - the in-flight B2 call was cancelled through the linked token; the host token never was.
        await s.BlockedTokenCancelled.Task.WaitAsync(Safety);
        s.BlockedToken.IsCancellationRequested.Should().BeTrue();
        s.Host.Token.IsCancellationRequested.Should().BeFalse();

        // ... and nothing beyond the in-flight step ran: 5 of 5000 candidates were touched.
        s.B2Paths.Should().HaveCount(5);
        s.ReadPaths.Should().HaveCount(5);
        s.Harness.StoredFileIndexIds(100).Should().Equal(1, 2, 3, 4);
        s.Harness.FinalStatus().Should().Be("Cancelled");
        s.Run.IsFaulted.Should().BeFalse();
    }

    // ---- SC-4: Cancelled row shape - four counts, final progress, partial rows kept, audit-first order ---------

    [Fact]
    public async Task Test_SC4_CancelledRowShape()
    {
        // Arrange - 10 candidates, 1-3 match, the 4th hangs.
        var s = new Scenario(10, new[] { 1, 2, 3 }, blockAtFileIndexId: 4, BlockOn.Read);
        s.Start();
        await s.BlockedStarted.Task.WaitAsync(Safety);

        // Act
        await s.RaiseFlagAsync();
        await s.Run.WaitAsync(Safety);

        // Assert - status Cancelled with result_detail = the four counts.
        var summary = SummaryOf(CallsOf(s), "Cancelled");
        summary.FilenameMatchCount.Should().Be(0);
        summary.ContentMatchCount.Should().Be(3);
        summary.ExcludedCount.Should().Be(0);
        summary.IndeterminateCount.Should().Be(0);
        var json = JsonSerializer.Serialize(summary);
        foreach (var key in new[] { "FilenameMatchCount", "ContentMatchCount", "ExcludedCount", "IndeterminateCount" })
        {
            json.Should().Contain($"\"{key}\":");
        }

        // The matches found before the stop are retained (no rollback).
        s.Harness.StoredFileIndexIds(100).Should().Equal(1, 2, 3);

        // Final progress is (processed, total): the 3 finished candidates of 10.
        s.Harness.ProgressWrites().Last().Should().Be((3, 10));

        // Terminal order: TaskCancelled audit -> final progress -> Cancelled status; exactly one of each.
        var tail = s.Events.SkipWhile(e => e != "audit:" + AuditAction.TaskCancelled).ToList();
        tail.Should().Equal("audit:" + AuditAction.TaskCancelled, "progress", "status:Cancelled");
        s.Events.Count(e => e == "audit:" + AuditAction.TaskCancelled).Should().Be(1);
        s.Events.Count(e => e == "status:Cancelled").Should().Be(1);
        s.Events.Should().NotContain("status:Completed").And.NotContain("status:Failed");

        // The audit row carries ids/integers only.
        await s.Harness.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.TaskCancelled && e.TaskQueueId == 100 && e.UserId == 7 &&
                e.Result == AuditResult.Success && e.FilePath == null &&
                !e.ResultDetail!.Contains(ResumeTestHarness.Term)),
            Arg.Any<CancellationToken>());
    }

    // ---- V7: audit failure leaves the row Running - no status is written ------------------------------------

    [Fact]
    public async Task Test_V7_AuditFailureWritesNoCancelledStatus()
    {
        // Arrange - the TaskCancelled audit write throws.
        var s = new Scenario(10, new[] { 1, 2, 3 }, blockAtFileIndexId: 4, BlockOn.Read);
        s.Harness.AuditLogService
            .WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.TaskCancelled), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("audit store unavailable"));
        s.Start();
        await s.BlockedStarted.Task.WaitAsync(Safety);

        // Act
        await s.RaiseFlagAsync();
        await s.Run.WaitAsync(Safety);

        // Assert - ProcessAsync returned normally, and NO status of any kind was written, and no final progress
        // after the failed audit: the row stays Running for the startup sweep (FR-23).
        s.Run.IsCompletedSuccessfully.Should().BeTrue();
        await s.Harness.TaskQueueRepository.DidNotReceiveWithAnyArgs()
            .UpdateStatusAsync(default, default!, default!, default, default);
        s.Events.Should().NotContain(e => e.StartsWith("status:"));
    }

    // ---- V8: a candidate is never half-dispositioned on cancel -----------------------------------------------

    [Fact]
    public async Task Test_V8_CancelNeverSplitsACandidateDisposition()
    {
        // Arrange - two concurrent slots. Candidate 1 is confirmed ABSENT by B2 and parked inside its disposition
        // (soft-delete); candidate 2's content read hangs and so observes the cancel.
        var s = new Scenario(4, new[] { 1, 2, 3, 4 }, blockAtFileIndexId: 2, BlockOn.Read, concurrency: 2);
        s.Harness.B2Client
            .ListFileNamesAsync(ResumeTestHarness.PathFor(1), 1, Arg.Any<CancellationToken>())
            .Returns(new B2ListFileNamesResult { Files = Array.Empty<B2FileInfo>() });
        var softDeleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSoftDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispositionTokens = new List<CancellationToken>();
        s.Harness.FileIndexRepository
            .SoftDeleteAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                lock (dispositionTokens) { dispositionTokens.Add(ci.Arg<CancellationToken>()); }
                s.Record("softdelete:" + ci.ArgAt<int>(0));
                softDeleteStarted.TrySetResult();
                await releaseSoftDelete.Task;
            });
        s.Start();
        await Task.WhenAll(s.BlockedStarted.Task, softDeleteStarted.Task).WaitAsync(Safety);

        // Act - the cancel is observed (candidate 2's read token fires) WHILE candidate 1 is mid-disposition ...
        await s.RaiseFlagAsync();
        await s.BlockedTokenCancelled.Task.WaitAsync(Safety);
        s.Run.IsCompleted.Should().BeFalse("the loop drains the in-flight disposition before ending");
        // ... then the disposition is allowed to finish.
        releaseSoftDelete.SetResult();
        await s.Run.WaitAsync(Safety);

        // Assert - candidate 1: soft-delete THEN its audit, both written, using the host token (not cancelled).
        var order = s.Events.Where(e => e.StartsWith("softdelete:") || e == "audit:" + AuditAction.CrossCheckExcluded).ToList();
        order.Should().Equal("softdelete:1", "audit:" + AuditAction.CrossCheckExcluded);
        dispositionTokens.Should().ContainSingle().Which.Should().Be(s.Host.Token);
        await s.Harness.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.CrossCheckExcluded && e.FilePath == ResumeTestHarness.PathFor(1)),
            s.Host.Token);

        // Candidate 2 (read cancelled, cross-check never completed): untouched - no insert, no soft-delete, no audit, no B2 call.
        s.Harness.StoredFileIndexIds(100).Should().BeEmpty();
        await s.Harness.FileIndexRepository.DidNotReceive().SoftDeleteAsync(2, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await s.Harness.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.FilePath == ResumeTestHarness.PathFor(2)), Arg.Any<CancellationToken>());
        await s.Harness.B2Client.DidNotReceive().ListFileNamesAsync(ResumeTestHarness.PathFor(2), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Candidates 3-4 were never dispatched; the task still ends Cancelled with the excluded count recorded.
        s.ReadPaths.Should().BeEquivalentTo(new[] { ResumeTestHarness.PathFor(1), ResumeTestHarness.PathFor(2) });
        SummaryOf(CallsOf(s), "Cancelled").ExcludedCount.Should().Be(1);
        s.Harness.FinalStatus().Should().Be("Cancelled");
    }

    [Fact]
    public async Task Cov_CancelDuringInsert_InsertCompletesWithHostToken()
    {
        // Arrange - candidate 1 is confirmed by B2 and parked inside its search_result insert; candidate 2's read hangs.
        var s = new Scenario(3, new[] { 1, 2, 3 }, blockAtFileIndexId: 2, BlockOn.Read, concurrency: 2);
        var insertStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var insertTokens = new List<CancellationToken>();
        s.Harness.SearchResultRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                lock (insertTokens) { insertTokens.Add(ci.Arg<CancellationToken>()); }
                insertStarted.TrySetResult();
                await releaseInsert.Task;
                s.Harness.Store.Add((ci.ArgAt<int>(2), ci.ArgAt<int>(3), ci.ArgAt<string>(4)));
                return 1;
            });
        s.Start();
        await Task.WhenAll(s.BlockedStarted.Task, insertStarted.Task).WaitAsync(Safety);

        // Act
        await s.RaiseFlagAsync();
        await s.BlockedTokenCancelled.Task.WaitAsync(Safety);
        releaseInsert.SetResult();
        await s.Run.WaitAsync(Safety);

        // Assert - the in-flight insert was not cancelled and its row is counted and retained.
        insertTokens.Should().ContainSingle().Which.Should().Be(s.Host.Token);
        s.Harness.StoredFileIndexIds(100).Should().Equal(1);
        SummaryOf(CallsOf(s), "Cancelled").ContentMatchCount.Should().Be(1);
    }

    // ---- V9: the flag arrives too late - the task completes normally -----------------------------------------

    [Fact]
    public async Task Test_V9_LateCancelCompletesNormally()
    {
        // Arrange - the flag is set, but the poll interval never elapses before the last candidate finishes.
        var s = new Scenario(6, new[] { 2, 4 }, blockAtFileIndexId: -1, BlockOn.Read);
        s.Harness.TaskQueueRepository
            .IsCancelRequestedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        // Act - fake clock never advanced: the watcher never polls.
        await s.CreateSut().ProcessAsync(ResumeTestHarness.Task(100), s.Host.Token);

        // Assert - Completed with the full results; no Cancelled status, no TaskCancelled audit.
        s.Harness.FinalStatus().Should().Be("Completed");
        s.Harness.CompletedSummary().ContentMatchCount.Should().Be(2);
        s.Harness.StoredFileIndexIds(100).Should().Equal(2, 4);
        s.Events.Should().NotContain("status:Cancelled");
        await s.Harness.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.TaskCancelled), Arg.Any<CancellationToken>());
        s.Harness.ProgressWrites().Last().Should().Be((6, 6));
    }

    // ---- Host shutdown is unchanged: it still writes no status ------------------------------------------------

    [Fact]
    public async Task Cov_HostShutdown_StillWritesNoStatus()
    {
        // Arrange
        var s = new Scenario(10, new[] { 1, 2, 3 }, blockAtFileIndexId: 4, BlockOn.Read);
        s.Start();
        await s.BlockedStarted.Task.WaitAsync(Safety);

        // Act - the HOST token fires (service stopping), not the cancel flag.
        s.Host.Cancel();
        var act = () => s.Run.WaitAsync(Safety);

        // Assert - propagates as cancellation; no Cancelled/Failed/Completed status, no TaskCancelled audit.
        await act.Should().ThrowAsync<OperationCanceledException>();
        await s.Harness.TaskQueueRepository.DidNotReceiveWithAnyArgs()
            .UpdateStatusAsync(default, default!, default!, default, default);
        await s.Harness.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.TaskCancelled), Arg.Any<CancellationToken>());
    }
}
