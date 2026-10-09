namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Outcome of <c>dbo.task_queue_acknowledge_upd</c> (CAS-030 §5), used at both the repository and
/// service/controller layers. Ownership is enforced inside the procedure; not-found and not-owned
/// are deliberately the same outcome (V3).
/// </summary>
public enum AcknowledgeTaskOutcome
{
    /// <summary>Procedure outcome <c>0</c> — acknowledged now, or already acknowledged (V5) — <c>204</c>.</summary>
    Acknowledged,

    /// <summary>Procedure outcome <c>1</c> — not found or not owned (V3) — <c>404 task-not-found</c>.</summary>
    NotFound,

    /// <summary>Procedure outcome <c>2</c> — <c>Queued</c>/<c>Running</c>/<c>Cancelled</c> (V4) — <c>409 task-not-terminal</c>.</summary>
    NotTerminal
}
