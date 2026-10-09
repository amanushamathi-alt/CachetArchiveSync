namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One row swept <c>Running</c> → <c>Failed</c> by <c>dbo.task_queue_sweep_interrupted</c>
/// (CAS-028 §3.4/§5) — exactly the columns of that procedure's final <c>SELECT</c>
/// (<c>task_queue_id</c>, <c>task_type</c>, <c>submitted_by_user_id</c>). Used by the Worker for
/// logging only.
/// </summary>
public sealed record SweptTaskRow
{
    public required int TaskQueueId { get; init; }

    public required string TaskType { get; init; }

    public required int SubmittedByUserId { get; init; }

    /// <summary>CAS-055 — id of the auto-resumed <c>Queued</c> task created for this row; null when not resumed.</summary>
    public int? ResumedAsTaskQueueId { get; init; }

    /// <summary>CAS-055 — true when auto-resume was withheld because the lineage hit the attempt cap.</summary>
    public bool AutoResumeSuppressed { get; init; }

    /// <summary>CAS-055 — count of match results carried over to the resumed task; null when not resumed.</summary>
    public int? CarriedOverCount { get; init; }
}
