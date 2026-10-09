using FluentAssertions;
using NSubstitute;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// CAS-054 §13: end-to-end resume behavior of <see cref="KeywordSearchTaskProcessor"/> over fake
/// queue/repositories with an in-memory search_result store (see <see cref="ResumeTestHarness"/>).
/// </summary>
public sealed class KeywordSearchResumeTests
{
    // ---- SC-6: a resumed run produces the same result set as an uninterrupted run -------------------

    [Fact]
    public async Task Test_SC6_ResumedRunMatchesUninterruptedRun()
    {
        // Arrange - 12 candidates; files 2, 5, 6, 9 and 11 contain the term.
        var matches = new[] { 2, 5, 6, 9, 11 };

        var uninterrupted = new ResumeTestHarness();
        uninterrupted.SetCandidates(12);
        uninterrupted.MatchingFileIndexIds.UnionWith(matches);
        await uninterrupted.CreateSut().ProcessAsync(ResumeTestHarness.Task(100), CancellationToken.None);

        // The interrupted original (task 100) was killed after W = 7; restart_ins carried its
        // matches at or below W (2, 5, 6) to the new task 200. Rows above W are NOT carried over.
        var resumed = new ResumeTestHarness();
        resumed.SetCandidates(12);
        resumed.MatchingFileIndexIds.UnionWith(matches);
        foreach (var carried in new[] { 2, 5, 6 })
        {
            resumed.Store.Add((200, carried, "Content"));
        }

        // Act
        await resumed.CreateSut().ProcessAsync(
            ResumeTestHarness.Task(200, resumeWatermark: 7, restartedFrom: 100), CancellationToken.None);

        // Assert - identical result set, no duplicates, same match count in the summary.
        resumed.StoredFileIndexIds(200).Should().Equal(uninterrupted.StoredFileIndexIds(100));
        resumed.StoredFileIndexIds(200).Should().OnlyHaveUniqueItems();
        resumed.CompletedSummary().ContentMatchCount.Should().Be(uninterrupted.CompletedSummary().ContentMatchCount);
        resumed.CompletedSummary().ContentMatchCount.Should().Be(matches.Length);
    }

    // ---- Coverage: an un-resumed task is untouched (no carry-over read, no ResumedFrom) --------------

    [Fact]
    public async Task Test_NoWatermark_RunsAllCandidatesAndLeavesResumedFromNull()
    {
        // Arrange
        var harness = new ResumeTestHarness();
        harness.SetCandidates(4);
        harness.MatchingFileIndexIds.UnionWith(new[] { 1, 4 });

        // Act
        await harness.CreateSut().ProcessAsync(ResumeTestHarness.Task(100), CancellationToken.None);

        // Assert
        harness.StoredFileIndexIds(100).Should().Equal(1, 4);
        var summary = harness.CompletedSummary();
        summary.ResumedFromTaskQueueId.Should().BeNull();
        summary.ContentMatchCount.Should().Be(2);
        await harness.SearchResultRepository.DidNotReceiveWithAnyArgs().ListAsync(default, default, default, default, default);
        harness.ProgressWrites().Last().Should().Be((4, 4));
    }

    // ---- Coverage / SC-1 (processor level): watermark writes are monotonic, never beyond the max id ---

    [Fact]
    public async Task Test_WatermarkWrites_AreMonotonicAndBoundedByLastCandidate()
    {
        // Arrange
        var harness = new ResumeTestHarness();
        harness.SetCandidates(8);
        harness.MatchingFileIndexIds.UnionWith(Enumerable.Range(1, 8));

        // Act
        await harness.CreateSut().ProcessAsync(ResumeTestHarness.Task(100), CancellationToken.None);

        // Assert - serial processing + an always-elapsed clock: one write per advance, ending at 8.
        var writes = harness.WatermarkWrites();
        writes.Should().NotBeEmpty();
        writes.Should().BeInAscendingOrder();
        writes.Should().OnlyHaveUniqueItems();
        writes.Last().Should().Be(8);
        await harness.TaskQueueRepository.Received().UpdateResumeWatermarkAsync(
            100, "worker-svc", Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- Production defect probe (SC-1 / SC-2 in practice) ----------------------------------------------
    // KeywordSearchTaskProcessor's "content did not match / unreadable" branch calls
    // progress.ItemCompletedAsync directly instead of CompleteCandidateAsync(position), so the tracker
    // is never told the candidate is dispositioned and the watermark can never pass it. On a real
    // archive almost every Content candidate is a non-match, so the watermark would stay (near) null.

    [Fact]
    public async Task Test_DefectProbe_WatermarkAdvancesPastDroppedContentCandidates()
    {
        // Arrange - 6 candidates, none matches the term.
        var harness = new ResumeTestHarness();
        harness.SetCandidates(6);

        // Act
        await harness.CreateSut().ProcessAsync(ResumeTestHarness.Task(100), CancellationToken.None);

        // Assert
        harness.WatermarkWrites().Should().NotBeEmpty();
        harness.WatermarkWrites().Last().Should().Be(6);
    }

    // ---- Coverage: a resumed task only writes watermarks above its inherited floor --------------------

    [Fact]
    public async Task Test_WatermarkWrites_OfResumedTaskStayAboveInheritedWatermark()
    {
        // Arrange
        var harness = new ResumeTestHarness();
        harness.SetCandidates(8);
        harness.MatchingFileIndexIds.UnionWith(Enumerable.Range(1, 8));

        // Act
        await harness.CreateSut().ProcessAsync(
            ResumeTestHarness.Task(200, resumeWatermark: 5, restartedFrom: 100), CancellationToken.None);

        // Assert
        var writes = harness.WatermarkWrites();
        writes.Should().NotBeEmpty();
        writes.Should().OnlyContain(w => w > 5);
        writes.Last().Should().Be(8);
    }

    // =================================================================================================
    // CAS-055 - the resumed row created by the startup sweep is an ordinary Queued task carrying the
    // watermark; the claim loop runs it with no user action (resume itself is CAS-054).
    // =================================================================================================

    // ---- CAS-055 SC-2: the auto-resumed task runs to Completed unattended, continuing from the skip ----

    [Fact]
    public async Task Test_SC2_ResumedTaskIsClaimedAndCompletedUnattended()
    {
        // Arrange - the sweep created task 200 (restarted_from = 100, watermark 5, matches <= 5 carried).
        var harness = new ResumeTestHarness();
        harness.SetCandidates(8);
        harness.MatchingFileIndexIds.UnionWith(new[] { 2, 7 });
        harness.Store.Add((200, 2, "Content"));

        // Act - exactly what the claim loop does with the claimed row; no restart/user call involved.
        await harness.CreateSut().ProcessAsync(
            ResumeTestHarness.Task(200, resumeWatermark: 5, restartedFrom: 100), CancellationToken.None);

        // Assert
        harness.FinalStatus().Should().Be("Completed");
        harness.ProgressWrites().Should().NotBeEmpty().And.OnlyContain(p => p.Total == 8);
        harness.StoredFileIndexIds(200).Should().Equal(2, 7);
    }

    // ---- CAS-055 SC-3: an auto-resumed run equals an uninterrupted run (static index) -----------------

    [Fact]
    public async Task Test_SC3_AutoResumedRunMatchesUninterruptedRun()
    {
        // Arrange - same shape as CAS-054 SC-6 but through the sweep-created row (second hop:
        // auto_resume_count would be 2; the processor sees only watermark + restartedFrom).
        var matches = new[] { 1, 4, 8, 10 };
        var uninterrupted = new ResumeTestHarness();
        uninterrupted.SetCandidates(10);
        uninterrupted.MatchingFileIndexIds.UnionWith(matches);
        await uninterrupted.CreateSut().ProcessAsync(ResumeTestHarness.Task(100), CancellationToken.None);

        var resumed = new ResumeTestHarness();
        resumed.SetCandidates(10);
        resumed.MatchingFileIndexIds.UnionWith(matches);
        foreach (var carried in new[] { 1, 4 })
        {
            resumed.Store.Add((300, carried, "Content"));
        }

        // Act
        await resumed.CreateSut().ProcessAsync(
            ResumeTestHarness.Task(300, resumeWatermark: 6, restartedFrom: 200), CancellationToken.None);

        // Assert
        resumed.StoredFileIndexIds(300).Should().Equal(uninterrupted.StoredFileIndexIds(100));
        resumed.StoredFileIndexIds(300).Should().OnlyHaveUniqueItems();
        resumed.CompletedSummary().ContentMatchCount.Should().Be(matches.Length);
    }
}
