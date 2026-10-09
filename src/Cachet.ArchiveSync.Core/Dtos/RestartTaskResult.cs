namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Outcome of a restart attempt against <c>dbo.task_queue_restart_ins</c> (CAS-016 §7), used at
/// both the repository and service/controller layers. The stored procedure checks ownership
/// (<see cref="NotOwner"/>) before task state (<see cref="NotRestartable"/>) inside its own
/// transaction, so a non-owner's response never reveals the task's current status (§7/§11).
/// </summary>
public enum RestartTaskOutcome
{
    /// <summary>A new <c>Queued</c> row was inserted, lineage-linked to the original <c>Failed</c> row (Model B, §2) — <c>202 Accepted</c>.</summary>
    Restarted,

    /// <summary><c>taskId</c> does not exist in <c>dbo.task_queue</c> — <c>404 Not Found</c>.</summary>
    NotFound,

    /// <summary><c>taskId</c> exists but is not currently in status <c>Failed</c> — <c>409 Conflict</c>.</summary>
    NotRestartable,

    /// <summary><c>taskId</c> exists but does not belong to the requesting user — <c>403 Forbidden</c>.</summary>
    NotOwner
}

/// <summary>
/// Result of one <see cref="Interfaces.ITaskQueueRepository.RestartAsync"/> /
/// <see cref="Interfaces.ITaskManagerService.RestartAsync"/> call (CAS-016 §5/§7/§8).
/// </summary>
public sealed record RestartTaskResult
{
    public required RestartTaskOutcome Outcome { get; init; }

    /// <summary>The newly-inserted <c>task_queue_id</c> — set only when <see cref="Outcome"/> is <see cref="RestartTaskOutcome.Restarted"/>.</summary>
    public int? NewTaskQueueId { get; init; }

    /// <summary>CAS-054 — the watermark W the new task resumes from; null when it was a full restart.</summary>
    public int? ResumedFromFileIndexId { get; init; }

    /// <summary>CAS-054 — <c>search_result</c> rows copied to the new task (0 when not resumed).</summary>
    public int CarriedOverCount { get; init; }

    /// <summary>CAS-054 — <c>'Resumed'</c>, <c>'NoCheckpoint'</c> or <c>'CheckpointTooOld'</c>; null if the procedure did not report one.</summary>
    public string? ResumeNote { get; init; }
}
