namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Result of <c>dbo.task_queue_summary_get</c> (CAS-026 §5) — the two Task Manager header badge
/// counts (FR-22) for one user. Backs <c>GET /tasks/summary</c> (§6.3).
/// </summary>
public sealed record TaskQueueSummaryRow
{
    public required int InProgressCount { get; init; }

    public required int UnviewedCompletedCount { get; init; }
}
