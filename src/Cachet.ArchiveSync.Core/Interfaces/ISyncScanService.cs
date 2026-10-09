using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of <see cref="ISyncScanService.SubmitAsync"/> (CAS-007 §6).</summary>
public enum SyncScanSubmitOutcome
{
    Accepted
}

/// <summary>Result of one <see cref="ISyncScanService.SubmitAsync"/> call.</summary>
public sealed record SyncScanSubmitResult
{
    public required SyncScanSubmitOutcome Outcome { get; init; }

    public TaskSubmittedResponse? Response { get; init; }
}

/// <summary>Outcome of <see cref="ISyncScanService.GetResultsAsync"/> (CAS-007 §6.2).</summary>
public enum SyncResultsOutcome
{
    Found,

    /// <summary>Unknown <c>taskId</c>, or <c>taskId</c> belongs to a different <c>task_type</c> (§7 V1) — <c>404 task-not-found</c>.</summary>
    TaskNotFound,

    /// <summary>Task is <c>Queued</c>/<c>Running</c> (§7 V2) — <c>409 task-not-complete</c>.</summary>
    TaskNotComplete
}

/// <summary>Result of one <see cref="ISyncScanService.GetResultsAsync"/> call.</summary>
public sealed record SyncResultsQueryResult
{
    public required SyncResultsOutcome Outcome { get; init; }

    public SyncScanResultsResponse? Response { get; init; }
}

/// <summary>
/// <c>POST /sync/scan</c> submit and <c>GET /sync/{taskId}/results</c> read (CAS-007 §6/§6.2).
/// <see cref="SubmitAsync"/> writes the <c>ContentScanSubmitted</c> audit row before returning
/// (§12) — the caller does not need to audit this call itself.
/// </summary>
public interface ISyncScanService
{
    Task<SyncScanSubmitResult> SubmitAsync(int submittedByUserId, CancellationToken ct);

    Task<SyncResultsQueryResult> GetResultsAsync(
        int taskId,
        int newOnODrivePage,
        int newOnODrivePageSize,
        int removedFromODrivePage,
        int removedFromODrivePageSize,
        int modifiedOnODrivePage,
        int modifiedOnODrivePageSize,
        bool excludeSynced,
        CancellationToken ct);
}
