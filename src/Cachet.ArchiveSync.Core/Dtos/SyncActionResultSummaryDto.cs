namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Typed payload serialized into <c>dbo.task_queue.result_detail</c> for <c>task_type</c> values
/// <c>SyncCopy</c>/<c>SyncUpdate</c>/<c>SyncPurge</c> (CAS-007 §11, per CAS-004 §5's resolved
/// action/result-catalog convention). Per-file outcomes live on <c>dbo.sync_result.sync_status</c>
/// (§5) — this summary is the task-level roll-up only.
/// </summary>
public sealed record SyncActionResultSummaryDto
{
    public int? SucceededCount { get; init; }

    public int? FailedCount { get; init; }

    /// <summary>Populated only when the task itself failed outright (e.g. malformed
    /// <c>request_parameters</c>), not for ordinary per-file failures (§7 SC-9).</summary>
    public string? FailureReason { get; init; }
}
