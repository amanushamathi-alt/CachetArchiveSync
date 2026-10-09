namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Typed payload serialized into <c>dbo.task_queue.result_detail</c> for <c>task_type =
/// 'B2MirrorSync'</c> (CAS-037 §6.4/§11), and into the completion
/// <see cref="Enums.AuditAction.B2MirrorSyncCompleted"/> row's <c>result_detail</c> (§12), per
/// CAS-004 §5's resolved action/result-catalog convention (same shape family as
/// <see cref="SyncActionResultSummaryDto"/>/<see cref="ContentScanResultSummaryDto"/>). On a
/// <c>Completed</c> task the four counts are populated (even when all are zero) and
/// <see cref="FailureReason"/> is null; on a <c>Failed</c> task (§7 V5, or an unhandled error) only
/// <see cref="FailureReason"/> is populated.
/// </summary>
public sealed record MirrorSyncResultSummaryDto
{
    public int? NewCount { get; init; }

    public int? UpdatedCount { get; init; }

    public int? SkippedCount { get; init; }

    public int? FailedCount { get; init; }

    /// <summary>Populated only when the task itself failed outright (§7 V5, or an unhandled error),
    /// not for ordinary per-object failures (§7 V4, rolled into <see cref="FailedCount"/>).</summary>
    public string? FailureReason { get; init; }
}
