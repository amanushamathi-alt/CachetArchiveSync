using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// CAS-056 §2.2 — per-task poller of <c>task_queue.is_cancel_requested</c>
/// (<see cref="ITaskQueueRepository.IsCancelRequestedAsync"/>). Polls every <c>interval</c> from
/// construction; the first time the flag is seen it fires <see cref="Token"/> once and stops polling.
/// A poll failure is logged (task id only) and swallowed — a flaky read must never fail the search,
/// the next tick simply retries. <see cref="Dispose"/> stops the loop cleanly; disposing does not
/// fire <see cref="Token"/>.
///
/// Concrete sealed class with no interface (P4), like <see cref="TaskProgressReporter"/>: tests
/// construct it directly with a mocked repository and a fake <see cref="TimeProvider"/>.
/// </summary>
public sealed class CancelRequestWatcher : IDisposable
{
    private readonly int _taskQueueId;
    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancelRequested = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly PeriodicTimer _timer;
    private readonly Task _loop;
    private int _disposed;

    public CancelRequestWatcher(
        int taskQueueId,
        ITaskQueueRepository taskQueueRepository,
        TimeProvider timeProvider,
        TimeSpan interval,
        ILogger logger)
    {
        _taskQueueId = taskQueueId;
        _taskQueueRepository = taskQueueRepository;
        _logger = logger;
        _timer = new PeriodicTimer(interval, timeProvider);
        _loop = Task.Run(PollAsync);
    }

    /// <summary>Fires once when a cancel request has been observed for the task.</summary>
    public CancellationToken Token => _cancelRequested.Token;

    private async Task PollAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_stop.Token))
            {
                try
                {
                    if (await _taskQueueRepository.IsCancelRequestedAsync(_taskQueueId, _stop.Token))
                    {
                        _logger.LogInformation("Cancel requested for task {TaskId}; stopping", _taskQueueId);
                        _cancelRequested.Cancel();
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Id only - no term/path.
                    _logger.LogWarning(ex, "Cancel-flag poll failed for task {TaskId}; will retry", _taskQueueId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed - expected.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        _timer.Dispose();

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // PollAsync swallows its own failures; nothing further to report on shutdown.
        }

        _stop.Dispose();
        _cancelRequested.Dispose();
    }
}
