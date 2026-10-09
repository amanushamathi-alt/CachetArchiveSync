using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// One <c>task_queue.task_type</c>'s processing logic (CAS-006 §3). <see cref="KeywordSearchTaskProcessor"/>
/// is the first implementation — this seam exists so <see cref="Worker"/>'s claim loop can dispatch
/// to a future task type (ContentScan, SyncCopy, SyncUpdate, SyncPurge) without being rewritten,
/// per CAS-006 §3's explicit ask, not a speculative abstraction.
/// </summary>
public interface IWorkerTaskProcessor
{
    /// <summary>Must match one of <c>dbo.task_queue</c>'s <c>CK_task_queue_task_type</c> values.</summary>
    string TaskType { get; }

    /// <summary>
    /// Processes one already-claimed (status <c>Running</c>) task through to a terminal status
    /// (<c>Completed</c>/<c>Failed</c>) — the implementation owns calling
    /// <c>ITaskQueueRepository.UpdateStatusAsync</c> itself.
    /// </summary>
    Task ProcessAsync(ClaimedTaskRow task, CancellationToken ct);
}
