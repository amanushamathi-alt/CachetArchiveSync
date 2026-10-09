using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// CAS-054 §10 SC-10 / §13: application and migration deploy independently. A new Worker against an
/// old database (the watermark procedure is missing) and a claim row without a watermark (the
/// column is missing / NULL) must both still complete a search - no resume, no failure.
/// </summary>
public sealed class WorkerResumeCompatibilityTests
{
    [Theory]
    [InlineData("ProcedureMissing")]
    [InlineData("ColumnMissing")]
    public async Task Test_SC10_WorkerToleratesMissingWatermarkColumnOrProcedure(string scenario)
    {
        // Arrange
        var harness = new ResumeTestHarness();
        harness.SetCandidates(5);
        harness.MatchingFileIndexIds.UnionWith(Enumerable.Range(1, 5));

        if (scenario == "ProcedureMissing")
        {
            // Old DB: dbo.task_queue_resume_watermark_upd does not exist -> SqlException-like failure.
            harness.TaskQueueRepository
                .UpdateResumeWatermarkAsync(default, default!, default, default)
                .ThrowsForAnyArgs(new InvalidOperationException("Could not find stored procedure 'dbo.task_queue_resume_watermark_upd'."));
        }

        // ColumnMissing: the claim result set has no resume_watermark_file_index_id -> null on the row.
        var task = ResumeTestHarness.Task(100, resumeWatermark: null);

        // Act
        var act = async () => await harness.CreateSut().ProcessAsync(task, CancellationToken.None);

        // Assert - completes normally, every candidate checked, nothing treated as resumed.
        await act.Should().NotThrowAsync();
        harness.FinalStatus().Should().Be("Completed");
        harness.StoredFileIndexIds(100).Should().Equal(1, 2, 3, 4, 5);
        var summary = harness.CompletedSummary();
        summary.ResumedFromTaskQueueId.Should().BeNull();
        summary.FailureReason.Should().BeNull();
        harness.ProgressWrites().Last().Should().Be((5, 5));

        if (scenario == "ProcedureMissing")
        {
            harness.WatermarkWrites().Should().NotBeEmpty("the Worker did attempt the write and survived its failure");
        }
    }
}
