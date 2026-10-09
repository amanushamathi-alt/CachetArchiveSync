using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of <see cref="IB2MirrorSyncService.SubmitAsync"/> (CAS-037 §6/§7 V1).</summary>
public enum B2MirrorSyncSubmitOutcome
{
    Accepted,

    /// <summary>A <c>B2MirrorSync</c> task is already <c>Queued</c>/<c>Running</c> (§7 V1) — maps to
    /// <c>409</c>, no task row inserted (§10 SC-6).</summary>
    AlreadyRunning
}

/// <summary>Result of one <see cref="IB2MirrorSyncService.SubmitAsync"/> call.</summary>
public sealed record B2MirrorSyncSubmitResult
{
    public required B2MirrorSyncSubmitOutcome Outcome { get; init; }

    public TaskSubmittedResponse? Response { get; init; }
}

/// <summary>
/// <c>POST /sync/mirror-sync</c> submit (CAS-037 §6). No request body — the sync scope is always
/// the whole configured B2 bucket. <see cref="SubmitAsync"/> enforces the §7 V1 global concurrency
/// guard (via <c>ITaskQueueRepository.HasActiveTaskOfTypeAsync</c>) before inserting a task row, and
/// writes the <c>B2MirrorSyncSubmitted</c> audit row before returning (§12) — the caller does not
/// need to audit this call itself.
/// </summary>
public interface IB2MirrorSyncService
{
    Task<B2MirrorSyncSubmitResult> SubmitAsync(int submittedByUserId, CancellationToken ct);
}
