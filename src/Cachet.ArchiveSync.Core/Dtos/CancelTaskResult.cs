namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Outcome of <c>dbo.task_queue_cancel_req</c> (CAS-056 §7), used at both the repository and
/// service/controller layers. Ownership is enforced inside the procedure; not-found and not-owned
/// are deliberately the same outcome (no enumeration, CAS-030 V3).
/// </summary>
public enum CancelTaskOutcome
{
    /// <summary>Procedure outcome <c>0</c> — <c>Running</c>; flag set (or already set) — <c>202 Accepted</c>.</summary>
    CancelRequested,

    /// <summary>Procedure outcome <c>1</c> — <c>Queued</c>; cancelled immediately — <c>200 OK</c>.</summary>
    Cancelled,

    /// <summary>Procedure outcome <c>2</c> — missing or not owned — <c>404 task-not-found</c>.</summary>
    NotFound,

    /// <summary>Procedure outcome <c>3</c> — terminal, or not a <c>KeywordSearch</c> task — <c>409 task-not-cancellable</c>.</summary>
    NotCancellable
}

/// <summary>
/// Result of one <see cref="Interfaces.ITaskQueueRepository.CancelAsync"/> /
/// <see cref="Interfaces.ITaskManagerService.CancelAsync"/> call (CAS-056 §5/§8).
/// </summary>
public sealed record CancelTaskResult
{
    public required CancelTaskOutcome Outcome { get; init; }
}
