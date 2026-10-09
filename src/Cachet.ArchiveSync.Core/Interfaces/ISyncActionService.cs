using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of a <see cref="ISyncActionService"/> submit call (CAS-007 §6).</summary>
public enum SyncActionSubmitOutcome
{
    Accepted,

    /// <summary><c>fileIds</c> empty or over <c>Sync:MaxBatchSize</c> (§7 V3, SC-15) — <c>422 validation-failed</c>.</summary>
    ValidationFailed,

    /// <summary>Unknown <c>taskId</c> (§6 status table) — <c>404 task-not-found</c>.</summary>
    TaskNotFound,

    /// <summary>A <c>fileId</c> does not resolve to a <c>Pending</c> row of the expected
    /// <c>comparison_type</c> scoped to <c>taskId</c> (§7 V4/V5/V6) — <c>404 file-not-found</c>.</summary>
    FileNotFound,

    /// <summary><c>POST /sync/actions/purge</c> only: <c>confirm</c> missing/<c>false</c> (§7 V7) — <c>422 confirmation-required</c>.</summary>
    ConfirmationRequired
}

/// <summary>Result of one <see cref="ISyncActionService"/> submit call.</summary>
public sealed record SyncActionSubmitResult
{
    public required SyncActionSubmitOutcome Outcome { get; init; }

    public TaskSubmittedResponse? Response { get; init; }
}

/// <summary>
/// <c>POST /sync/actions/copy</c>/<c>update</c>/<c>purge</c> validate+submit (CAS-007 §6). Each
/// method writes its own <c>*Submitted</c> audit row before returning (§12) — the caller does not
/// need to audit this call itself. Split from <see cref="ISyncScanService"/> along the same
/// security-posture seam CAS-006 already used to split <c>IKeywordSearchService</c>/
/// <c>IQuarantineService</c> (Karpathy P4 — the pattern has now appeared twice).
/// </summary>
public interface ISyncActionService
{
    Task<SyncActionSubmitResult> SubmitCopyAsync(SyncActionRequest request, int actingUserId, CancellationToken ct);

    Task<SyncActionSubmitResult> SubmitUpdateAsync(SyncActionRequest request, int actingUserId, CancellationToken ct);

    Task<SyncActionSubmitResult> SubmitPurgeAsync(SyncPurgeRequest request, int actingUserId, CancellationToken ct);
}
