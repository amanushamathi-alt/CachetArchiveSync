using System.Collections.Concurrent;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Worker.Tests;

/// <summary>
/// Unit tests for <see cref="Worker"/> (CAS-006 §3) — the general claim-loop skeleton. Drives the
/// real <c>BackgroundService.StartAsync</c>/<c>StopAsync</c> lifecycle against an
/// <see cref="IServiceScopeFactory"/> built from a real <see cref="ServiceCollection"/> holding
/// NSubstitute fakes (<see cref="Worker"/> resolves its dependencies per-iteration via
/// <c>IServiceScopeFactory.CreateAsyncScope()</c>, so a plain constructor-injected fake cannot
/// stand in for it). <see cref="ITaskQueueRepository.ClaimNextAsync"/>'s "no task queued" case
/// causes a 5-second <c>Task.Delay</c> in real code — these tests cancel the token that delay
/// observes rather than waiting it out, matching <c>Worker.ExecuteAsync</c>'s own
/// <c>stoppingToken</c>-aware cancellation.
/// </summary>
public sealed class WorkerTests
{
    private sealed class Fixture
    {
        public ITaskQueueRepository TaskQueueRepository { get; } = Substitute.For<ITaskQueueRepository>();
        public ILogger<Worker> Logger { get; } = Substitute.For<ILogger<Worker>>();
        // CAS-028: lock held, empty startup sweep, default options — so the existing claim/dispatch
        // tests reach the claim loop unchanged.
        public IWorkerInstanceLock InstanceLock { get; } = Substitute.For<IWorkerInstanceLock>();
        public IAuditLogService AuditLogService { get; } = Substitute.For<IAuditLogService>();
        public IHostApplicationLifetime Lifetime { get; } = Substitute.For<IHostApplicationLifetime>();
        // CAS-028: tests that need the loop to cycle quickly set PollingIntervalSeconds = 1 and/or a
        // specific MaxDegreeOfParallelism; the pre-existing tests keep the defaults (5 s / 4).
        public WorkerOptions Settings { get; set; } = new();
        private readonly List<IWorkerTaskProcessor> _processors = new();

        public Fixture()
        {
            InstanceLock.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns(true);
            InstanceLock.IsHeldAsync(Arg.Any<CancellationToken>()).Returns(true);
            TaskQueueRepository
                .SweepInterruptedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>())
                .Returns(Array.Empty<SweptTaskRow>());
        }

        public IWorkerTaskProcessor AddProcessor(string taskType)
        {
            var processor = Substitute.For<IWorkerTaskProcessor>();
            processor.TaskType.Returns(taskType);
            _processors.Add(processor);
            return processor;
        }

        public void AddProcessor(IWorkerTaskProcessor processor) => _processors.Add(processor);

        /// <summary>Extra DI registrations (e.g. a scoped processor) applied by <see cref="CreateSut"/>.</summary>
        public List<Action<IServiceCollection>> ExtraRegistrations { get; } = new();

        public Worker CreateSut()
        {
            var services = new ServiceCollection();
            services.AddSingleton(TaskQueueRepository);
            services.AddSingleton(AuditLogService);
            foreach (var processor in _processors)
            {
                services.AddSingleton(processor);
            }

            foreach (var register in ExtraRegistrations)
            {
                register(services);
            }

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            return new Worker(scopeFactory, InstanceLock, Options.Create(Settings), Lifetime, Logger);
        }
    }

    private static ClaimedTaskRow ClaimedTask(string taskType = "KeywordSearch", int taskQueueId = 100234) => new()
    {
        TaskQueueId = taskQueueId,
        TaskType = taskType,
        SubmittedByUserId = 7,
        SubmittedAt = DateTimeOffset.UtcNow
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(10);
        }
    }

    // ---- Claims a task and dispatches it to the matching processor by task_type -------------------

    [Fact]
    public async Task Test_ExecuteAsync_ClaimedTask_DispatchesToMatchingProcessor()
    {
        // Arrange — one matching processor, one non-matching processor registered (proves
        // dispatch is by TaskType, not "whatever is registered first").
        var fixture = new Fixture();
        var keywordProcessor = fixture.AddProcessor("KeywordSearch");
        var otherProcessor = fixture.AddProcessor("ContentScan");
        var task = ClaimedTask();
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(task, (ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => keywordProcessor.ReceivedCalls().Any(), TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Assert
        await keywordProcessor.Received(1).ProcessAsync(task, Arg.Any<CancellationToken>());
        await otherProcessor.DidNotReceive().ProcessAsync(Arg.Any<ClaimedTaskRow>(), Arg.Any<CancellationToken>());
    }

    // ---- No task available: loops/waits without throwing -------------------------------------------

    [Fact]
    public async Task Test_ExecuteAsync_NoTaskClaimed_WaitsWithoutThrowing()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.AddProcessor("KeywordSearch");
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => fixture.TaskQueueRepository.ReceivedCalls().Any(), TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Assert — the loop entered its poll-delay branch and StopAsync completed cleanly, so the
        // hosted service's execute task never faulted.
        await fixture.TaskQueueRepository.Received().ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        sut.ExecuteTask.Should().NotBeNull();
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- No IWorkerTaskProcessor registered for the claimed task_type: logs and keeps looping ------

    [Fact]
    public async Task Test_ExecuteAsync_NoProcessorForTaskType_LogsWarningAndContinues()
    {
        // Arrange — the claimed task's type ("SyncCopy") has no matching registered processor.
        var fixture = new Fixture();
        fixture.AddProcessor("KeywordSearch");
        var unmatchedTask = ClaimedTask(taskType: "SyncCopy");
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(unmatchedTask, (ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => fixture.Logger.ReceivedCalls().Any(c => c.GetArguments().ElementAtOrDefault(0) is LogLevel.Warning),
            TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Assert — loop kept running (proven by the second, null-returning ClaimNextAsync call
        // being reachable) rather than crashing or exiting.
        await fixture.TaskQueueRepository.Received().ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- An exception thrown by a processor is caught, logged, and the loop keeps running ----------

    [Fact]
    public async Task Test_ExecuteAsync_ProcessorThrows_CatchesLogsAndKeepsLoopAlive()
    {
        // Arrange
        var fixture = new Fixture();
        var processor = fixture.AddProcessor("KeywordSearch");
        var task = ClaimedTask();
        processor.ProcessAsync(task, Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("simulated processor failure"));
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(task, (ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => fixture.Logger.ReceivedCalls().Any(c => c.GetArguments().ElementAtOrDefault(0) is LogLevel.Error),
            TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Assert — the exception never escaped ExecuteAsync (no fault), and the processor was
        // reached exactly once (a second claim attempt after the error would return null and delay).
        await processor.Received(1).ProcessAsync(task, Arg.Any<CancellationToken>());
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- stoppingToken cancellation stops the loop cleanly ------------------------------------------

    [Fact]
    public async Task Test_ExecuteAsync_StoppingTokenCancelled_StopsLoopCleanly()
    {
        // Arrange — ClaimNextAsync always returns null, so the loop is parked in its 5-second
        // poll-delay when StopAsync cancels the underlying stoppingToken.
        var fixture = new Fixture();
        fixture.AddProcessor("KeywordSearch");
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => fixture.TaskQueueRepository.ReceivedCalls().Any(), TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        // Assert — StopAsync returned (it awaits ExecuteTask with a timeout internally) and the
        // task completed without propagating OperationCanceledException as a fault.
        sut.ExecuteTask.Should().NotBeNull();
        sut.ExecuteTask!.IsCompleted.Should().BeTrue();
        sut.ExecuteTask.IsFaulted.Should().BeFalse();
    }

    // =================================================================================================
    // CAS-028 — bounded-parallel slot model, startup sweep, dispatch-boundary safety net, shutdown,
    // single-instance lock (§3.3–§3.7). Every test below uses PollingIntervalSeconds = 1 wherever the
    // loop must cycle, bounded waits (WaitUntilAsync, 5 s ceiling) and no fixed sleeps beyond a short
    // "nothing else happened" grace period.
    // =================================================================================================

    private const string WorkerIdentity = "worker-svc";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static bool ReceivedLog(ILogger<Worker> logger, LogLevel level) =>
        logger.ReceivedCalls().Any(c => c.GetArguments().ElementAtOrDefault(0) is LogLevel l && l == level);

    private static bool ProcessorReceivedTask(IWorkerTaskProcessor processor, int taskQueueId) =>
        processor.ReceivedCalls().Any(c =>
            c.GetMethodInfo().Name == nameof(IWorkerTaskProcessor.ProcessAsync)
            && c.GetArguments()[0] is ClaimedTaskRow row && row.TaskQueueId == taskQueueId);

    private static int CountCalls(object substitute, string methodName) =>
        substitute.ReceivedCalls().Count(c => c.GetMethodInfo().Name == methodName);

    /// <summary>Processor whose tasks block until the test opens each task's gate; tracks peak concurrency.</summary>
    private sealed class GatedProcessor : IWorkerTaskProcessor
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _gates = new();
        private int _current;
        private int _peak;

        public string TaskType => "KeywordSearch";

        public ConcurrentQueue<int> Started { get; } = new();

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public void Open(int taskQueueId) => Gate(taskQueueId).TrySetResult();

        public void OpenAll()
        {
            foreach (var id in Started)
            {
                Open(id);
            }
        }

        private TaskCompletionSource Gate(int taskQueueId) =>
            _gates.GetOrAdd(taskQueueId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public async Task ProcessAsync(ClaimedTaskRow task, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _current);
            int snapshot;
            while (now > (snapshot = Volatile.Read(ref _peak))
                   && Interlocked.CompareExchange(ref _peak, now, snapshot) != snapshot)
            {
            }

            Started.Enqueue(task.TaskQueueId);
            try
            {
                await Gate(task.TaskQueueId).Task.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    /// <summary>
    /// Processor that runs until the stopping token is cancelled, then takes a little longer to wind
    /// down before rethrowing the cancellation — proves StopAsync awaits in-flight work (SC-12).
    /// </summary>
    private sealed class ShutdownAwareProcessor : IWorkerTaskProcessor
    {
        private int _finishedAfterCancellation;

        public string TaskType => "KeywordSearch";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FinishedAfterCancellation => Volatile.Read(ref _finishedAfterCancellation) == 1;

        public async Task ProcessAsync(ClaimedTaskRow task, CancellationToken ct)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None);
                Interlocked.Exchange(ref _finishedAfterCancellation, 1);
                throw;
            }
        }
    }

    // ---- SC-4: at most MaxDegreeOfParallelism tasks run at once; the 5th waits for a free slot -----

    [Fact]
    public async Task Test_SC4_ExecuteAsync_RespectsMaxDegreeOfParallelism()
    {
        // Arrange — 5 queued tasks, parallelism 4, processors block until released.
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1, MaxDegreeOfParallelism = 4 } };
        var processor = new GatedProcessor();
        fixture.AddProcessor(processor);
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ClaimedTask(taskQueueId: 1), ClaimedTask(taskQueueId: 2), ClaimedTask(taskQueueId: 3),
                     ClaimedTask(taskQueueId: 4), ClaimedTask(taskQueueId: 5), null);
        var sut = fixture.CreateSut();

        try
        {
            // Act 1 — let the loop fill every slot.
            await sut.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => processor.Started.Count == 4, WaitTimeout);
            await Task.Delay(TimeSpan.FromMilliseconds(300)); // grace: nothing else may start

            // Assert 1 — exactly 4 running; the 5th was not even claimed (the loop waits for a slot first).
            processor.Started.Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
            CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)).Should().Be(4);

            // Act 2 — finish one task; its slot frees and the 5th is claimed and started.
            processor.Open(1);
            await WaitUntilAsync(() => processor.Started.Count == 5, WaitTimeout);

            // Assert 2
            processor.Started.Last().Should().Be(5);
            processor.PeakConcurrency.Should().Be(4);
        }
        finally
        {
            processor.OpenAll();
            await sut.StopAsync(CancellationToken.None);
        }

        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- SC-5: a slot is released after success, processor throw, and missing processor -----------

    [Fact]
    public async Task Test_SC5_ExecuteAsync_SlotReleasedAfterEveryOutcome()
    {
        // Arrange — ONE slot, so any outcome that failed to release it would stall the loop forever
        // and task 4 would never be reached. 1 = success, 2 = processor throws, 3 = no processor.
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1, MaxDegreeOfParallelism = 1 } };
        var processor = fixture.AddProcessor("KeywordSearch");
        processor
            .ProcessAsync(Arg.Is<ClaimedTaskRow>(t => t.TaskQueueId == 2), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("simulated processor failure")));
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ClaimedTask(taskQueueId: 1), ClaimedTask(taskQueueId: 2),
                     ClaimedTask(taskType: "SyncCopy", taskQueueId: 3), ClaimedTask(taskQueueId: 4), null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => ProcessorReceivedTask(processor, 4), WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert — every outcome freed the single slot, so all four were claimed and task 4 ran.
        CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)).Should().BeGreaterThanOrEqualTo(4);
        ProcessorReceivedTask(processor, 1).Should().BeTrue();
        ProcessorReceivedTask(processor, 2).Should().BeTrue();
        ProcessorReceivedTask(processor, 3).Should().BeFalse();
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(2, WorkerIdentity, "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.Received(1).UpdateStatusAsync(3, WorkerIdentity, "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- SC-6: the startup sweep runs exactly once, before the first claim -------------------------

    [Fact]
    public async Task Test_SC6_ExecuteAsync_SweepRunsBeforeFirstClaim()
    {
        // Arrange — default 5 s poll: after the first (empty) claim the loop parks in its delay, so
        // stopping right after it leaves exactly one claim call to order against the sweep.
        var fixture = new Fixture();
        fixture.AddProcessor("KeywordSearch");
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)) > 0, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert
        await fixture.TaskQueueRepository.Received(1).SweepInterruptedAsync(
            WorkerIdentity, Worker.InterruptedByRestartReason, Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>());
        Received.InOrder(() =>
        {
            fixture.TaskQueueRepository.SweepInterruptedAsync(
                WorkerIdentity, Worker.InterruptedByRestartReason, Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>());
            fixture.TaskQueueRepository.ClaimNextAsync(WorkerIdentity, Arg.Any<CancellationToken>());
        });
    }

    // ---- SC-10: processor exception at the boundary → TaskFailed audit THEN Failed status ----------

    [Fact]
    public async Task Test_SC10_DispatchBoundary_ProcessorThrows_AuditThenFailed()
    {
        // Arrange
        const string exceptionText = "Sensitive detail: C:\\mirror\\contracts\\secret.pdf";
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1 } };
        var processor = fixture.AddProcessor("KeywordSearch");
        var task = ClaimedTask();
        processor.ProcessAsync(task, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException(exceptionText)));
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(task, (ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.UpdateStatusAsync)) > 0, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert — audit first, then the status write; result_detail carries no exception text.
        Received.InOrder(() =>
        {
            fixture.AuditLogService.WriteAsync(
                Arg.Is<AuditLogEntry>(e =>
                    e.Action == AuditAction.TaskFailed
                    && e.Result == AuditResult.Failure
                    && e.TaskQueueId == task.TaskQueueId
                    && e.UserId == task.SubmittedByUserId
                    && e.TabModule == "Keyword Search"),
                Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, WorkerIdentity, "Failed",
                Arg.Is<string?>(d => d != null && !d.Contains(exceptionText)),
                Arg.Any<CancellationToken>());
        });
    }

    // ---- SC-11: boundary audit write throws → no Failed status written, slot still released --------

    [Fact]
    public async Task Test_SC11_DispatchBoundary_AuditFails_StatusNotWritten()
    {
        // Arrange — ONE slot: task 2 can only be reached if task 1's slot was released.
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1, MaxDegreeOfParallelism = 1 } };
        var processor = fixture.AddProcessor("KeywordSearch");
        processor
            .ProcessAsync(Arg.Is<ClaimedTaskRow>(t => t.TaskQueueId == 1), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("simulated processor failure")));
        fixture.AuditLogService
            .WriteAsync(Arg.Is<AuditLogEntry>(e => e.TaskQueueId == 1), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("simulated audit outage")));
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ClaimedTask(taskQueueId: 1), ClaimedTask(taskQueueId: 2), null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => ProcessorReceivedTask(processor, 2), WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert — fail-closed: the audit was attempted, but no status write for task 1 (it stays
        // Running for the next startup sweep); the slot was released (task 2 ran).
        await fixture.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.TaskQueueId == 1), Arg.Any<CancellationToken>());
        await fixture.TaskQueueRepository.DidNotReceive().UpdateStatusAsync(
            1, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        ReceivedLog(fixture.Logger, LogLevel.Error).Should().BeTrue();
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- SC-12: StopAsync awaits in-flight tasks; no status/audit write for shutdown cancellation --

    [Fact]
    public async Task Test_SC12_StopAsync_AwaitsInFlight_NoStatusWriteOnShutdown()
    {
        // Arrange
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1 } };
        var processor = new ShutdownAwareProcessor();
        fixture.AddProcessor(processor);
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ClaimedTask(), (ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        await sut.StartAsync(CancellationToken.None);
        await processor.Started.Task.WaitAsync(WaitTimeout);

        // Act
        await sut.StopAsync(CancellationToken.None);

        // Assert — StopAsync returned only after the in-flight task finished winding down, and the
        // cancelled task was deliberately left Running (no status write, no audit) for the sweep.
        processor.FinishedAfterCancellation.Should().BeTrue();
        await fixture.TaskQueueRepository.DidNotReceive().UpdateStatusAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        sut.ExecuteTask!.IsCompleted.Should().BeTrue();
        sut.ExecuteTask.IsFaulted.Should().BeFalse();
    }

    // ---- SC-14: standby waits while the lock is held elsewhere, then acquires → sweeps → claims -----

    [Fact]
    public async Task Test_SC14_ExecuteAsync_StandbyAcquiresThenSweepsThenClaims()
    {
        // Arrange — the other instance holds the lock for the first two attempts, then releases it.
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1 } };
        fixture.AddProcessor("KeywordSearch");
        fixture.InstanceLock.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns(false, false, true);
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)) > 0, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert
        Received.InOrder(() =>
        {
            fixture.InstanceLock.TryAcquireAsync(Arg.Any<CancellationToken>());
            fixture.InstanceLock.TryAcquireAsync(Arg.Any<CancellationToken>());
            fixture.InstanceLock.TryAcquireAsync(Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.SweepInterruptedAsync(
                WorkerIdentity, Worker.InterruptedByRestartReason, Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>());
            fixture.TaskQueueRepository.ClaimNextAsync(WorkerIdentity, Arg.Any<CancellationToken>());
        });
        ReceivedLog(fixture.Logger, LogLevel.Warning).Should().BeTrue();
    }

    // ---- V-3: a failing sweep is retried and no claim happens until one succeeds -------------------

    [Fact]
    public async Task Test_V3_ExecuteAsync_SweepThrows_DoesNotClaimUntilSweepSucceeds()
    {
        // Arrange — sweep fails twice (e.g. SQL Server not reachable at boot), then succeeds.
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1 } };
        fixture.AddProcessor("KeywordSearch");
        fixture.TaskQueueRepository
            .SweepInterruptedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>())
            .Returns(
                Task.FromException<IReadOnlyList<SweptTaskRow>>(new InvalidOperationException("simulated DB unavailable")),
                Task.FromException<IReadOnlyList<SweptTaskRow>>(new InvalidOperationException("simulated DB unavailable")),
                Task.FromResult<IReadOnlyList<SweptTaskRow>>(Array.Empty<SweptTaskRow>()));
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.SweepInterruptedAsync)) >= 2, WaitTimeout);

        // Assert (while the sweep is still failing) — no claim yet.
        CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)).Should().Be(0);
        ReceivedLog(fixture.Logger, LogLevel.Error).Should().BeTrue();

        await WaitUntilAsync(
            () => CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)) > 0, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert — claiming began only after the third (successful) sweep.
        Received.InOrder(() =>
        {
            fixture.TaskQueueRepository.SweepInterruptedAsync(WorkerIdentity, Worker.InterruptedByRestartReason, Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>());
            fixture.TaskQueueRepository.SweepInterruptedAsync(WorkerIdentity, Worker.InterruptedByRestartReason, Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>());
            fixture.TaskQueueRepository.SweepInterruptedAsync(WorkerIdentity, Worker.InterruptedByRestartReason, Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>());
            fixture.TaskQueueRepository.ClaimNextAsync(WorkerIdentity, Arg.Any<CancellationToken>());
        });
    }

    // ---- V-5: boundary result_detail is fixed {"FailureReason":…} JSON, never exception text -------

    [Fact]
    public async Task Test_V5_DispatchBoundary_ProcessorThrows_ResultDetailIsFixedJsonWithoutExceptionText()
    {
        // Arrange
        const string exceptionText = "Login failed for user 'svc'; path \\\\fileserver\\odrive\\hr\\salaries.xlsx";
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1 } };
        var processor = fixture.AddProcessor("KeywordSearch");
        var task = ClaimedTask();
        processor.ProcessAsync(task, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException(exceptionText)));
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(task, (ClaimedTaskRow?)null);
        string? capturedResultDetail = null;
        fixture.TaskQueueRepository
            .UpdateStatusAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<string?>(d => capturedResultDetail = d), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        AuditLogEntry? capturedAudit = null;
        fixture.AuditLogService
            .WriteAsync(Arg.Do<AuditLogEntry>(e => capturedAudit = e), Arg.Any<CancellationToken>())
            .Returns(1);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => capturedResultDetail is not null, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert — exact fixed JSON object with a single FailureReason property.
        capturedResultDetail.Should().Be("{\"FailureReason\":\"Unexpected error while processing this task.\"}");
        using (var document = JsonDocument.Parse(capturedResultDetail!))
        {
            document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            document.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("FailureReason");
            document.RootElement.GetProperty("FailureReason").GetString().Should().Be(Worker.UnexpectedErrorReason);
        }

        capturedResultDetail.Should().NotContain("Login failed").And.NotContain("salaries").And.NotContain(nameof(InvalidOperationException));
        capturedAudit.Should().NotBeNull();
        capturedAudit!.ResultDetail.Should().Be(Worker.UnexpectedErrorReason);
    }

    // ---- V-6: claimed task_type with no registered processor → TaskFailed audit, then Failed ------

    [Fact]
    public async Task Test_V6_ExecuteAsync_NoProcessorForTaskType_AuditsThenMarksFailed()
    {
        // Arrange — only KeywordSearch is registered; the claimed task is SyncCopy.
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1 } };
        var keywordProcessor = fixture.AddProcessor("KeywordSearch");
        var task = ClaimedTask(taskType: "SyncCopy");
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(task, (ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.UpdateStatusAsync)) > 0, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert
        Received.InOrder(() =>
        {
            fixture.AuditLogService.WriteAsync(
                Arg.Is<AuditLogEntry>(e =>
                    e.Action == AuditAction.TaskFailed
                    && e.Result == AuditResult.Failure
                    && e.TaskQueueId == task.TaskQueueId
                    && e.UserId == task.SubmittedByUserId
                    && e.TabModule == "O-Drive Sync"),
                Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, WorkerIdentity, "Failed",
                "{\"FailureReason\":\"This task type is not supported by the worker.\"}",
                Arg.Any<CancellationToken>());
        });
        await keywordProcessor.DidNotReceive().ProcessAsync(Arg.Any<ClaimedTaskRow>(), Arg.Any<CancellationToken>());
    }

    // ---- V-7: an instance that never acquires the lock never sweeps and never claims ----------------

    [Fact]
    public async Task Test_V7_ExecuteAsync_LockNotAcquired_NeverSweepsOrClaims()
    {
        // Arrange
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1 } };
        fixture.AddProcessor("KeywordSearch");
        fixture.InstanceLock.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns(false);
        var sut = fixture.CreateSut();

        // Act — let it retry at least once (proves it keeps standing by rather than giving up).
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => CountCalls(fixture.InstanceLock, nameof(IWorkerInstanceLock.TryAcquireAsync)) >= 2, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert
        await fixture.TaskQueueRepository.DidNotReceive().SweepInterruptedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>());
        await fixture.TaskQueueRepository.DidNotReceive().ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        ReceivedLog(fixture.Logger, LogLevel.Warning).Should().BeTrue();
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- V-8: lock lost (held check false, or throws) → no further claim, Critical, StopApplication

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Test_V8_ExecuteAsync_LockLost_StopsClaimingAndStopsHost(bool heldCheckThrows)
    {
        // Arrange — held for the first claim, lost before the second (two tasks are available).
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1 } };
        fixture.AddProcessor("KeywordSearch");
        fixture.InstanceLock.IsHeldAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(true),
            heldCheckThrows
                ? Task.FromException<bool>(new InvalidOperationException("simulated lock connection drop"))
                : Task.FromResult(false));
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ClaimedTask(taskQueueId: 1), ClaimedTask(taskQueueId: 2), null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => fixture.Lifetime.ReceivedCalls().Any(), WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert
        await fixture.TaskQueueRepository.Received(1).ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        fixture.Lifetime.Received(1).StopApplication();
        ReceivedLog(fixture.Logger, LogLevel.Critical).Should().BeTrue();
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // =================================================================================================
    // CAS-028 coverage top-up — Worker.cs error branches (§3.3 slot always released, §3.4 sweep,
    // §3.5 boundary fail-closed, §3.7 lock acquisition, §11 logging). Log assertions match only the
    // level and a fixed message fragment; they never depend on task parameters (§11 log hygiene).
    // =================================================================================================

    private static bool ReceivedLogContaining(ILogger<Worker> logger, LogLevel level, string fragment) =>
        logger.ReceivedCalls().Any(c =>
            c.GetArguments().ElementAtOrDefault(0) is LogLevel l && l == level
            && (c.GetArguments().ElementAtOrDefault(2)?.ToString() ?? string.Empty).Contains(fragment));

    /// <summary>Scoped processor whose scope disposal throws — an error outside the processor boundary.</summary>
    private sealed class ThrowOnDisposeProcessor : IWorkerTaskProcessor, IAsyncDisposable
    {
        private readonly ConcurrentQueue<int> _processed;

        public ThrowOnDisposeProcessor(ConcurrentQueue<int> processed) => _processed = processed;

        public string TaskType => "KeywordSearch";

        public Task ProcessAsync(ClaimedTaskRow task, CancellationToken ct)
        {
            _processed.Enqueue(task.TaskQueueId);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => throw new InvalidOperationException("simulated scope disposal failure");
    }

    // ---- §3.7: lock acquisition throws → error logged, retried; no sweep/claim until acquired ------

    [Fact]
    public async Task Test_ExecuteAsync_LockAcquireThrows_RetriesWithoutSweepingOrClaiming()
    {
        // Arrange — the lock connection fails on the first attempt, then acquisition succeeds.
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1 } };
        fixture.AddProcessor("KeywordSearch");
        fixture.InstanceLock.TryAcquireAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromException<bool>(new InvalidOperationException("simulated lock connection failure")),
            Task.FromResult(true));
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => ReceivedLog(fixture.Logger, LogLevel.Error), WaitTimeout);

        // Assert (while the lock is not yet acquired) — nothing swept or claimed.
        CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.SweepInterruptedAsync)).Should().Be(0);
        CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)).Should().Be(0);

        await WaitUntilAsync(
            () => CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)) > 0, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert — retried, then acquire → sweep → claim.
        ReceivedLogContaining(fixture.Logger, LogLevel.Error, "lock acquisition failed").Should().BeTrue();
        Received.InOrder(() =>
        {
            fixture.InstanceLock.TryAcquireAsync(Arg.Any<CancellationToken>());
            fixture.InstanceLock.TryAcquireAsync(Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.SweepInterruptedAsync(
                WorkerIdentity, Worker.InterruptedByRestartReason, Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>());
            fixture.TaskQueueRepository.ClaimNextAsync(WorkerIdentity, Arg.Any<CancellationToken>());
        });
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- §3.4 / §11: sweep resolves interrupted rows → Warning logged, claim loop proceeds ---------

    [Fact]
    public async Task Test_ExecuteAsync_SweepReturnsInterruptedTasks_LogsWarningAndProceedsToClaim()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.AddProcessor("KeywordSearch");
        fixture.TaskQueueRepository
            .SweepInterruptedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>())
            .Returns(new[]
            {
                new SweptTaskRow { TaskQueueId = 501, TaskType = "KeywordSearch", SubmittedByUserId = 7 },
                new SweptTaskRow { TaskQueueId = 502, TaskType = "ContentScan", SubmittedByUserId = 8 }
            });
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)) > 0, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert
        ReceivedLogContaining(fixture.Logger, LogLevel.Warning, "Startup sweep failed 2 interrupted task(s)").Should().BeTrue();
        ReceivedLogContaining(fixture.Logger, LogLevel.Warning, "501").Should().BeTrue();
        ReceivedLogContaining(fixture.Logger, LogLevel.Warning, "502").Should().BeTrue();
        await fixture.TaskQueueRepository.Received(1).SweepInterruptedAsync(
            WorkerIdentity, Worker.InterruptedByRestartReason, Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>());
        await fixture.TaskQueueRepository.Received().ClaimNextAsync(WorkerIdentity, Arg.Any<CancellationToken>());
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- §3.3: ClaimNextAsync throws → slot released, error logged, later claim succeeds ----------

    [Fact]
    public async Task Test_ExecuteAsync_ClaimThrows_ReleasesSlotLogsAndRetries()
    {
        // Arrange — ONE slot: the later claim can only happen if the failed claim released it.
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1, MaxDegreeOfParallelism = 1 } };
        var processor = fixture.AddProcessor("KeywordSearch");
        var task = ClaimedTask(taskQueueId: 7001);
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<ClaimedTaskRow?>(new InvalidOperationException("simulated deadlock victim")),
                Task.FromResult<ClaimedTaskRow?>(task),
                Task.FromResult<ClaimedTaskRow?>(null));
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => ProcessorReceivedTask(processor, 7001), WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert
        ReceivedLogContaining(fixture.Logger, LogLevel.Error, "while claiming the next task").Should().BeTrue();
        CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)).Should().BeGreaterThanOrEqualTo(2);
        await processor.Received(1).ProcessAsync(task, Arg.Any<CancellationToken>());
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- §3.3 / §3.5: error in the slot outside the processor boundary → logged, slot released,
    //      no Failed status and no audit (left Running for the next startup sweep) -----------------

    [Fact]
    public async Task Test_ExecuteAsync_SlotErrorOutsideProcessorBoundary_LogsReleasesSlotNoFailedStatus()
    {
        // Arrange — scoped processor whose DI-scope disposal throws after ProcessAsync succeeds; the
        // throw surfaces from the slot's `await using` scope, i.e. the slot's outer catch. ONE slot:
        // task 2 can only run if task 1's slot was released.
        var processed = new ConcurrentQueue<int>();
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1, MaxDegreeOfParallelism = 1 } };
        fixture.ExtraRegistrations.Add(s => s.AddScoped<IWorkerTaskProcessor>(_ => new ThrowOnDisposeProcessor(processed)));
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ClaimedTask(taskQueueId: 8001), ClaimedTask(taskQueueId: 8002), null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => processed.Contains(8002)
                  && fixture.Logger.ReceivedCalls().Count(c =>
                      c.GetArguments().ElementAtOrDefault(0) is LogLevel.Error
                      && (c.GetArguments().ElementAtOrDefault(2)?.ToString() ?? string.Empty).Contains("execution slot")) >= 2,
            WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert
        processed.Should().ContainInOrder(8001, 8002);
        ReceivedLogContaining(fixture.Logger, LogLevel.Error, "Unhandled error in the execution slot for task 8001").Should().BeTrue();
        await fixture.TaskQueueRepository.DidNotReceive().UpdateStatusAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await fixture.AuditLogService.DidNotReceive().WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // ---- §3.5: TaskFailed audit succeeds but the Failed status write throws → logged, slot released

    [Fact]
    public async Task Test_DispatchBoundary_StatusWriteFailsAfterAudit_LogsErrorAndReleasesSlot()
    {
        // Arrange — ONE slot: task 2 can only run if task 1's slot was released.
        var fixture = new Fixture { Settings = new WorkerOptions { PollingIntervalSeconds = 1, MaxDegreeOfParallelism = 1 } };
        var processor = fixture.AddProcessor("KeywordSearch");
        processor
            .ProcessAsync(Arg.Is<ClaimedTaskRow>(t => t.TaskQueueId == 1), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("simulated processor failure")));
        fixture.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1);
        fixture.TaskQueueRepository
            .UpdateStatusAsync(1, Arg.Any<string>(), "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("simulated status write outage")));
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ClaimedTask(taskQueueId: 1), ClaimedTask(taskQueueId: 2), null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => ProcessorReceivedTask(processor, 2), WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert — audit then attempted status write; the failure was logged, not propagated.
        Received.InOrder(() =>
        {
            fixture.AuditLogService.WriteAsync(
                Arg.Is<AuditLogEntry>(e => e.TaskQueueId == 1 && e.Action == AuditAction.TaskFailed),
                Arg.Any<CancellationToken>());
            fixture.TaskQueueRepository.UpdateStatusAsync(
                1, WorkerIdentity, "Failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        });
        ReceivedLogContaining(fixture.Logger, LogLevel.Error, "Status write failed for task 1").Should().BeTrue();
        sut.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // =================================================================================================
    // CAS-055 §2.7 / §5 - the Worker passes its auto-resume settings to the sweep and logs one warning
    // per auto-resumed / suppressed task (ids only).
    // =================================================================================================

    [Fact]
    public async Task Test_CAS055_Sweep_ReceivesConfiguredResumeSettings()
    {
        // Arrange
        var fixture = new Fixture { Settings = new WorkerOptions { ResumeMaxAgeHours = 48, AutoResumeMaxAttempts = 5 } };
        fixture.AddProcessor("KeywordSearch");
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)) > 0, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert
        await fixture.TaskQueueRepository.Received(1).SweepInterruptedAsync(
            WorkerIdentity, Worker.InterruptedByRestartReason, Arg.Any<CancellationToken>(), 48, 5);
    }

    [Fact]
    public async Task Test_CAS055_Sweep_LogsOneWarningPerResumedAndSuppressedTask()
    {
        // Arrange
        var fixture = new Fixture();
        fixture.AddProcessor("KeywordSearch");
        fixture.TaskQueueRepository
            .SweepInterruptedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>())
            .Returns(new[]
            {
                new SweptTaskRow { TaskQueueId = 601, TaskType = "KeywordSearch", SubmittedByUserId = 7, ResumedAsTaskQueueId = 650, CarriedOverCount = 3 },
                new SweptTaskRow { TaskQueueId = 602, TaskType = "KeywordSearch", SubmittedByUserId = 7, AutoResumeSuppressed = true },
                new SweptTaskRow { TaskQueueId = 603, TaskType = "ContentScan", SubmittedByUserId = 8 }
            });
        fixture.TaskQueueRepository
            .ClaimNextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ClaimedTaskRow?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => CountCalls(fixture.TaskQueueRepository, nameof(ITaskQueueRepository.ClaimNextAsync)) > 0, WaitTimeout);
        await sut.StopAsync(CancellationToken.None);

        // Assert - summary line + exactly one resumed + one suppressed line.
        ReceivedLogContaining(fixture.Logger, LogLevel.Warning, "auto-resumed interrupted task 601 as task 650").Should().BeTrue();
        ReceivedLogContaining(fixture.Logger, LogLevel.Warning, "did not auto-resume interrupted task 602").Should().BeTrue();
        ReceivedLogContaining(fixture.Logger, LogLevel.Warning, "task 603").Should().BeFalse();
    }
}
