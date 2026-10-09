namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// CAS-028 §3.7 (OQ-1) — "exactly one active Worker per database" guard. A Worker that has not
/// acquired this lock never sweeps and never claims (V7); a Worker that loses it stops claiming
/// and stops the host (V8). The lock is released only by disposing the implementation (host
/// shutdown), never explicitly while tasks are in flight.
/// </summary>
public interface IWorkerInstanceLock : IAsyncDisposable
{
    /// <summary>Attempts the lock without waiting. True when this instance now holds it.</summary>
    Task<bool> TryAcquireAsync(CancellationToken ct);

    /// <summary>True only when this instance currently holds the lock; any failure counts as not held.</summary>
    Task<bool> IsHeldAsync(CancellationToken ct);
}
