namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>GET /search/{taskId}/results</c> <c>200 OK</c> response body (CAS-006 §6.2). On a
/// <c>Completed</c> task, <see cref="FilenameMatches"/>/<see cref="ContentMatches"/> are
/// populated and <see cref="FailureReason"/> is null. On a <c>Failed</c> task, both panels are
/// null and <see cref="FailureReason"/> is populated (proposed shape, not BRD-specified — §6.2's
/// own note that the client needs *something* to render for a failed task).
/// </summary>
public sealed record SearchResultsResponse
{
    public required int TaskId { get; init; }

    /// <summary><c>"Completed"</c>, <c>"Failed"</c> or (CAS-056) <c>"Cancelled"</c> — this response shape is never returned for
    /// <c>Queued</c>/<c>Running</c> (those return <c>409</c> instead, §7 V5).</summary>
    public required string Status { get; init; }

    public PagedResult<FilenameMatchItem>? FilenameMatches { get; init; }

    public PagedResult<ContentMatchItem>? ContentMatches { get; init; }

    public string? FailureReason { get; init; }

    // CAS-056 §2.6 — additive and optional; populated only for a Cancelled task (so a client
    // that predates them, and the Completed/Failed shapes, are unchanged).

    /// <summary><c>true</c> only for a <c>Cancelled</c> task: the matches are those found before the stop.</summary>
    public bool IsPartial { get; init; }

    public int? ItemsProcessed { get; init; }

    public int? ItemsTotal { get; init; }
}
