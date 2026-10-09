namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>GET /sync/{taskId}/results</c> <c>200 OK</c> response body (CAS-007 §6.2). On a
/// <c>Completed</c> task, the three panels are populated and <see cref="FailureReason"/> is null.
/// On a <c>Failed</c> task, all three panels are null and <see cref="FailureReason"/> is
/// populated — same proposed shape CAS-006 §6.2 already established for
/// <c>SearchResultsResponse</c>, kept consistent across modules (§12 note).
/// </summary>
public sealed record SyncScanResultsResponse
{
    public required int TaskId { get; init; }

    /// <summary><c>"Completed"</c> or <c>"Failed"</c> — never returned for <c>Queued</c>/<c>Running</c>
    /// (those return <c>409</c> instead, §7 V2).</summary>
    public required string Status { get; init; }

    public PagedResult<NewOnODriveItem>? NewOnODrive { get; init; }

    public PagedResult<RemovedFromODriveItem>? RemovedFromODrive { get; init; }

    public PagedResult<ModifiedOnODriveItem>? ModifiedOnODrive { get; init; }

    public string? FailureReason { get; init; }
}
