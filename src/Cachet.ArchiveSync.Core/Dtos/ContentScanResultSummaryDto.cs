namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Typed payload serialized into <c>dbo.task_queue.result_detail</c> for
/// <c>task_type = 'ContentScan'</c> (CAS-007 §5 step 6, per CAS-004 §5's resolved
/// action/result-catalog convention). On a <c>Completed</c> task the three counts are populated
/// (even when all are zero, SC-3) and <see cref="FailureReason"/> is null; on a <c>Failed</c>
/// task only <see cref="FailureReason"/> is populated.
/// </summary>
public sealed record ContentScanResultSummaryDto
{
    public int? NewCount { get; init; }

    public int? RemovedCount { get; init; }

    public int? ModifiedCount { get; init; }

    public string? FailureReason { get; init; }
}
