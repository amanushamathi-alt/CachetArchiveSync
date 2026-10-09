namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One claimed row as returned by <c>dbo.task_queue_claim</c>'s <c>OUTPUT</c> clause (CAS-006
/// §5) — exactly the columns that procedure outputs (<c>task_queue_id</c>, <c>task_type</c>,
/// <c>submitted_by_user_id</c>, <c>request_parameters</c>, <c>submitted_at</c>). The claim itself
/// already transitioned the row to <c>Running</c>; this DTO does not carry a <c>Status</c> field
/// since it is always <c>Running</c> by construction.
/// </summary>
public sealed record ClaimedTaskRow
{
    public required int TaskQueueId { get; init; }

    public required string TaskType { get; init; }

    public required int SubmittedByUserId { get; init; }

    public string? RequestParameters { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }

    /// <summary>
    /// CAS-054 — highest <c>file_index_id</c> W for which every candidate <c>&lt;= W</c> was fully
    /// dispositioned by an earlier run; null = no checkpoint (start at candidate 1).
    /// </summary>
    public int? ResumeWatermarkFileIndexId { get; init; }

    /// <summary>
    /// CAS-054 — <c>restarted_from_task_queue_id</c> of the claimed row, when the claim procedure
    /// returns it; used only to label a resumed summary. Null when absent.
    /// </summary>
    public int? RestartedFromTaskQueueId { get; init; }
}
