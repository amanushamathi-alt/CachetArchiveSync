namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Typed payload serialized into <c>dbo.task_queue.result_detail</c> for
/// <c>task_type = 'KeywordSearch'</c> (CAS-006 §3, per CAS-004 §5's resolved
/// action/result-catalog convention). On a <c>Completed</c> task, the count fields are populated
/// and <see cref="FailureReason"/> is null; on a <c>Failed</c> task, only
/// <see cref="FailureReason"/> is populated. Serialized/deserialized with
/// <c>System.Text.Json</c> at the repository boundary only.
/// </summary>
public sealed record KeywordSearchResultSummaryDto
{
    public int? FilenameMatchCount { get; init; }

    public int? ContentMatchCount { get; init; }

    /// <summary>Count of local candidates excluded by the B2 cross-check (§7 V9, §11 telemetry).</summary>
    public int? ExcludedCount { get; init; }

    /// <summary>Count of candidates whose B2 cross-check could not be resolved (transient B2/network
    /// failure surviving all retries) — omitted from results, neither confirmed nor excluded. Distinct
    /// from <see cref="ExcludedCount"/> (§7 V1, §11 telemetry).</summary>
    public int? IndeterminateCount { get; init; }

    /// <summary>CAS-054 §2.5 — the original task this resumed task continued from; null when not resumed.</summary>
    public int? ResumedFromTaskQueueId { get; init; }

    public string? FailureReason { get; init; }
}
