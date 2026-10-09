using FluentAssertions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// Unit tests for <see cref="ResumeWatermarkTracker"/> (CAS-054 §2.1/§9 V5/§10 SC-1/§13).
/// </summary>
public sealed class ResumeWatermarkTrackerTests
{
    // ---- V5: watermark is contiguous under out-of-order completion ---------------------------------

    [Fact]
    public void Test_V5_WatermarkIsContiguousUnderOutOfOrderCompletion()
    {
        // Arrange - candidates (ascending file_index_id) at positions 0..3.
        var sut = new ResumeWatermarkTracker(new[] { 10, 20, 30, 40 });

        // Act / Assert - higher candidates finishing first never move the watermark.
        sut.MarkCompleted(2).Should().BeNull("position 0 and 1 are still running");
        sut.MarkCompleted(3).Should().BeNull("a still-running lower candidate holds the watermark");
        sut.MarkCompleted(0).Should().Be(10, "only the prefix [0] is complete");
        sut.Watermark.Should().Be(10);
        sut.MarkCompleted(1).Should().Be(40, "completing the gap releases the whole prefix");
    }

    [Fact]
    public void Test_V5_WatermarkIsContiguousUnderOutOfOrderCompletion_SharedFileIndexIdNeverSplit()
    {
        // Arrange - a Filename and a Content row of the same file share file_index_id 2.
        var sut = new ResumeWatermarkTracker(new[] { 1, 2, 2, 3 });

        // Act / Assert - resume skips by file_index_id <= W, so W must not land between the pair.
        sut.MarkCompleted(0).Should().Be(1);
        sut.MarkCompleted(1).Should().Be(1, "the second row with file_index_id 2 is still outstanding");
        sut.MarkCompleted(2).Should().Be(2);
        sut.MarkCompleted(3).Should().Be(3);
    }

    [Fact]
    public void Test_V5_WatermarkIsContiguousUnderOutOfOrderCompletion_InitialWatermarkIsFloor()
    {
        // Arrange - inherited watermark 5; remaining candidates above it.
        var sut = new ResumeWatermarkTracker(new[] { 6, 7 }, initialWatermark: 5);

        // Act / Assert
        sut.Watermark.Should().Be(5);
        sut.MarkCompleted(1).Should().Be(5, "position 0 is still running; the floor is kept");
        sut.MarkCompleted(0).Should().Be(7);
    }

    [Fact]
    public void Test_V5_WatermarkIsContiguousUnderOutOfOrderCompletion_EmptyCandidateList()
    {
        // Arrange / Act
        var sut = new ResumeWatermarkTracker(Array.Empty<int>());

        // Assert
        sut.Watermark.Should().BeNull();
    }

    // ---- SC-1: advances monotonically, never beyond the contiguous completed prefix ----------------

    [Fact]
    public void Test_SC1_WatermarkAdvancesOnlyOverContiguousCompletedCandidates()
    {
        // Arrange - 200 candidates (ids 3,6,9,...), completed in a seeded random order.
        const int Count = 200;
        var ids = Enumerable.Range(1, Count).Select(i => i * 3).ToArray();
        var rng = new Random(54);
        var order = Enumerable.Range(0, Count).OrderBy(_ => rng.Next()).ToArray();

        var sut = new ResumeWatermarkTracker(ids);
        var completed = new bool[Count];
        int? previous = null;

        foreach (var position in order)
        {
            // Act
            completed[position] = true;
            var watermark = sut.MarkCompleted(position);

            // Assert - brute-force contiguous prefix bound.
            var prefix = 0;
            while (prefix < Count && completed[prefix])
            {
                prefix++;
            }

            var bound = prefix == 0 ? (int?)null : ids[prefix - 1];
            watermark.Should().Be(bound, "ids are unique, so the watermark equals the contiguous prefix end");

            if (previous is not null)
            {
                watermark.Should().BeGreaterOrEqualTo(previous.Value, "the watermark never decreases");
            }

            previous = watermark;
        }

        sut.Watermark.Should().Be(ids[^1]);
    }

    [Fact]
    public void Test_SC1_WatermarkAdvancesOnlyOverContiguousCompletedCandidates_RepeatedCompletionIsIdempotent()
    {
        // Arrange
        var sut = new ResumeWatermarkTracker(new[] { 1, 2, 3 });
        sut.MarkCompleted(0);
        sut.MarkCompleted(1);

        // Act
        var again = sut.MarkCompleted(0);

        // Assert
        again.Should().Be(2);
    }
}
