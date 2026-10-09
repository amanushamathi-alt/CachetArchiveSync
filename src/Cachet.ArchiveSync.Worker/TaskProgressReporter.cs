using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// CAS-029 §3.4 — throttled, advisory progress reporter for one executing task. Writes measured
/// counts only (<c>items_processed</c> of <c>items_total</c>) via
/// <see cref="ITaskQueueRepository.UpdateProgressAsync"/> (<c>dbo.task_queue_progress_upd</c>).
/// Never estimates, never stores a percentage or ETA (§0.1), and only ever sends the task id and
/// two integers to the database — never a term, path, or file name.
///
/// Rules (§3.4): a write failure (anything but <see cref="OperationCanceledException"/>) is logged
/// and swallowed, so progress can never change a task's outcome (V6); once the procedure reports
/// the task is no longer <c>Running</c>, no further writes are made (V7). Intermediate writes are
/// time-triggered but evaluated only on item completion (no background timer), with at most one
/// intermediate write in flight — safe to call from parallel callers.
///
/// Concrete sealed class with no interface (P4): tests construct it directly with a mocked
/// <see cref="ITaskQueueRepository"/> and a fake <see cref="TimeProvider"/>.
/// </summary>
public sealed class TaskProgressReporter
{
    private const string ActingProcessIdentity = "worker-svc";

    private readonly int _taskQueueId;
    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly ILogger<TaskProgressReporter> _logger;

    private int _itemsTotal;
    private int _itemsProcessed;
    private long _lastWriteTimestamp;
    private int _writeInFlight;
    private int _stopped;

    public TaskProgressReporter(
        int taskQueueId,
        ITaskQueueRepository taskQueueRepository,
        TimeProvider timeProvider,
        TimeSpan interval,
        ILogger<TaskProgressReporter> logger)
    {
        _taskQueueId = taskQueueId;
        _taskQueueRepository = taskQueueRepository;
        _timeProvider = timeProvider;
        _interval = interval;
        _logger = logger;
    }

    /// <summary>Stores the total and writes <c>(0, itemsTotal)</c> immediately. Called once.</summary>
    public async Task StartAsync(int itemsTotal, CancellationToken ct)
    {
        _itemsTotal = itemsTotal;
        Interlocked.Exchange(ref _lastWriteTimestamp, _timeProvider.GetTimestamp());
        await WriteAsync(0, ct);
    }

    /// <summary>
    /// Counts one finished unit of work. Writes only if at least the configured interval has
    /// passed since the last write and no other write is in flight; otherwise returns without I/O.
    /// </summary>
    public async Task ItemCompletedAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _itemsProcessed);

        if (Volatile.Read(ref _stopped) != 0 || !IntervalElapsed())
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _writeInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Re-check under the guard: another caller may have written since the first check.
            if (!IntervalElapsed())
            {
                return;
            }

            await WriteAsync(Volatile.Read(ref _itemsProcessed), ct);
            Interlocked.Exchange(ref _lastWriteTimestamp, _timeProvider.GetTimestamp());
        }
        finally
        {
            Volatile.Write(ref _writeInFlight, 0);
        }
    }

    /// <summary>
    /// Forced final write <c>(count, itemsTotal)</c>. Awaited by the processor before its
    /// <c>Completed</c> status write (§3.6); not called on the failure path.
    /// </summary>
    public Task CompleteAsync(CancellationToken ct) => WriteAsync(Volatile.Read(ref _itemsProcessed), ct);

    private bool IntervalElapsed() =>
        _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastWriteTimestamp)) >= _interval;

    private async Task WriteAsync(int itemsProcessed, CancellationToken ct)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }

        try
        {
            var updated = await _taskQueueRepository.UpdateProgressAsync(
                _taskQueueId, ActingProcessIdentity, itemsProcessed, _itemsTotal, ct);

            if (!updated)
            {
                if (Interlocked.Exchange(ref _stopped, 1) == 0)
                {
                    _logger.LogWarning(
                        "Task {TaskId} is no longer Running; progress reporting stopped", _taskQueueId);
                }

                return;
            }

            _logger.LogDebug(
                "Task {TaskId} progress {ItemsProcessed}/{ItemsTotal}", _taskQueueId, itemsProcessed, _itemsTotal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Progress write failed for task {TaskId}; continuing without it", _taskQueueId);
        }
    }
}
