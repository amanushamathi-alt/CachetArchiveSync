using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// CAS-029 §3.4 / §13 "TaskProgressReporterTests" — throttle (SC-8), concurrency (SC-9), fault
/// tolerance (V6), stop-on-not-running (V7) and the immediate start write. The reporter is built
/// through <see cref="TaskProgressReporterFactory"/> (the production construction path) over a
/// mocked <see cref="ITaskQueueRepository"/> and a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class TaskProgressReporterTests
{
    private const int TaskQueueId = 100777;
    private const string WorkerIdentity = "worker-svc";

    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public FakeTimeProvider Time { get; } = new();
        public ILogger<TaskProgressReporter> Logger { get; } = Substitute.For<ILogger<TaskProgressReporter>>();

        public Fixture()
        {
            TaskQueueRepository
                .UpdateProgressAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(true);
        }

        public TaskProgressReporter CreateSut(int intervalSeconds = 15) =>
            new TaskProgressReporterFactory(
                    TaskQueueRepository,
                    Time,
                    Options.Create(new WorkerOptions { ProgressReportIntervalSeconds = intervalSeconds }),
                    Logger)
                .Create(TaskQueueId);

        public List<(int Processed, int Total)> ProgressWrites() =>
            TaskQueueRepository.ReceivedCalls()
                .Where(c => c.GetMethodInfo().Name == nameof(ITaskQueueRepository.UpdateProgressAsync))
                .Select(c => ((int)c.GetArguments()[2]!, (int)c.GetArguments()[3]!))
                .ToList();
    }

    private static List<(LogLevel Level, string Message, Exception? Exception)> LoggedEntries(ILogger logger)
    {
        var entries = new List<(LogLevel, string, Exception?)>();
        foreach (var call in logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[0] is not LogLevel level || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                entries.Add((level, message, args[3] as Exception));
            }
        }

        return entries;
    }

    // ---- §3.4: StartAsync writes (0, itemsTotal) immediately ----------------------------------------

    [Fact]
    public async Task Test_Reporter_StartWritesZeroOfTotalImmediately()
    {
        // Arrange
        var fixture = new Fixture();
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(42, CancellationToken.None);

        // Assert — exactly one write, (0, 42), attributed to worker-svc, with no time having passed.
        await fixture.TaskQueueRepository.Received(1).UpdateProgressAsync(
            TaskQueueId, WorkerIdentity, 0, 42, Arg.Any<CancellationToken>());
        fixture.ProgressWrites().Should().Equal((0, 42));
    }

    // ---- SC-8: throttled by Worker:ProgressReportIntervalSeconds -----------------------------------

    [Fact]
    public async Task Test_SC8_Reporter_ThrottlesByInterval()
    {
        // Part 1 — 1,000 items finished within one interval → exactly 2 writes (start and final).
        var burst = new Fixture();
        var burstSut = burst.CreateSut(intervalSeconds: 15);

        await burstSut.StartAsync(1000, CancellationToken.None);
        for (var i = 0; i < 1000; i++)
        {
            await burstSut.ItemCompletedAsync(CancellationToken.None);
        }

        burst.Time.Advance(TimeSpan.FromSeconds(14));
        await burstSut.CompleteAsync(CancellationToken.None);

        burst.ProgressWrites().Should().Equal(new[] { (0, 1000), (1000, 1000) },
            "no intermediate write may happen before the 15 s interval has elapsed");

        // Part 2 — advancing time 15 s before an item finishes → exactly one intermediate write
        // carrying the current count.
        var timed = new Fixture();
        var timedSut = timed.CreateSut(intervalSeconds: 15);

        await timedSut.StartAsync(10, CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            await timedSut.ItemCompletedAsync(CancellationToken.None);
        }

        timed.Time.Advance(TimeSpan.FromSeconds(15));
        await timedSut.ItemCompletedAsync(CancellationToken.None); // 4th item: interval elapsed → write
        await timedSut.ItemCompletedAsync(CancellationToken.None); // 5th item: interval restarted → no write

        timed.ProgressWrites().Should().Equal(new[] { (0, 10), (4, 10) });
    }

    // ---- SC-9: parallel increments, never more than one write in flight, exact final ---------------

    [Fact]
    public async Task Test_SC9_Reporter_ParallelIncrements_SingleWriteInFlight_ExactFinal()
    {
        // Arrange — every write is artificially delayed; each write advances the fake clock past the
        // interval so that callers keep finding the interval elapsed and contend for the guard.
        var fixture = new Fixture();
        var inFlight = 0;
        var maxInFlight = 0;
        fixture.TaskQueueRepository
            .UpdateProgressAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, now);
                await Task.Delay(2);
                fixture.Time.Advance(TimeSpan.FromSeconds(16));
                Interlocked.Decrement(ref inFlight);
                return true;
            });

        var sut = fixture.CreateSut(intervalSeconds: 15);
        await sut.StartAsync(4000, CancellationToken.None);

        // Act — 8 parallel callers × 500 items.
        var callers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 500; i++)
            {
                await sut.ItemCompletedAsync(CancellationToken.None);
            }
        }));
        await Task.WhenAll(callers);
        await sut.CompleteAsync(CancellationToken.None);

        // Assert
        maxInFlight.Should().Be(1, "the CompareExchange guard allows at most one write in flight");
        var writes = fixture.ProgressWrites();
        writes.Count.Should().BeGreaterThan(2, "the elapsed interval must have produced intermediate writes under contention");
        writes.Should().OnlyContain(w => w.Total == 4000 && w.Processed >= 0 && w.Processed <= 4000);
        writes[^1].Should().Be((4000, 4000), "the final write reports exactly the number of completed items");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    // ---- V6: a write that throws is swallowed and logged; the caller never sees it ----------------

    [Fact]
    public async Task Test_V6_Reporter_WriteThrows_SwallowedAndLogged()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .UpdateProgressAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated SQL outage"));
        var sut = fixture.CreateSut();

        // Act
        var act = async () =>
        {
            await sut.StartAsync(3, CancellationToken.None);
            await sut.ItemCompletedAsync(CancellationToken.None);
            fixture.Time.Advance(TimeSpan.FromSeconds(15));
            await sut.ItemCompletedAsync(CancellationToken.None);
            await sut.ItemCompletedAsync(CancellationToken.None);
            await sut.CompleteAsync(CancellationToken.None);
        };

        // Assert — never thrown to the caller; writes keep being attempted (a failure is not a stop).
        await act.Should().NotThrowAsync();
        fixture.ProgressWrites().Should().Equal(new[] { (0, 3), (2, 3), (3, 3) });

        var warnings = LoggedEntries(fixture.Logger).Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(3);
        warnings.Should().OnlyContain(w =>
            w.Message == $"Progress write failed for task {TaskQueueId}; continuing without it"
            && w.Exception is InvalidOperationException);
    }

    // ---- V7: rows_affected = 0 → one Warning, no further writes ------------------------------------

    [Fact]
    public async Task Test_V7_Reporter_TaskNoLongerRunning_StopsWriting()
    {
        // Arrange — the SP reports 0 rows affected (task is no longer Running).
        var fixture = new Fixture();
        fixture.TaskQueueRepository
            .UpdateProgressAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(5, CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromSeconds(60));
        await sut.ItemCompletedAsync(CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromSeconds(60));
        await sut.ItemCompletedAsync(CancellationToken.None);
        await sut.CompleteAsync(CancellationToken.None);

        // Assert — only the start write reached the repository; exactly one Warning was logged.
        fixture.ProgressWrites().Should().Equal((0, 5));
        LoggedEntries(fixture.Logger).Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)
            .Should().Equal($"Task {TaskQueueId} is no longer Running; progress reporting stopped");
    }
}
