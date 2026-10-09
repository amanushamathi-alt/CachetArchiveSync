using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of <see cref="IQuarantineRemoveService.RemoveAsync"/> (CAS-014 §8).</summary>
public enum QuarantineRemoveOutcome
{
    /// <summary><c>fileIds</c> was empty (§9 V1) — <c>422 validation-failed</c>.</summary>
    ValidationFailed,

    /// <summary>Every requested <c>fileId</c> failed to resolve to an <c>Active</c>
    /// <c>quarantine_entry</c> row (§9 V2) — whole-request <c>404 file-not-found</c>.</summary>
    AllFilesNotFound,

    /// <summary>Request processed — see <see cref="QuarantineRemoveResult.Response"/>'s per-file
    /// <c>results[]</c> for outcomes (may include per-file <c>Failed</c> entries) —
    /// <c>200 OK</c>.</summary>
    Completed
}

/// <summary>Result of one <see cref="IQuarantineRemoveService.RemoveAsync"/> call.</summary>
public sealed record QuarantineRemoveResult
{
    public required QuarantineRemoveOutcome Outcome { get; init; }

    public QuarantineRemoveResponse? Response { get; init; }
}

/// <summary>
/// <c>POST /quarantine/remove</c> — "Remove Item" (CAS-014 §2.2/§8, BRD §12.3 items 3, 7). Per file,
/// independently: resolves the row (must be currently <c>Active</c>), hard-deletes it via
/// <c>dbo.quarantine_entry_del</c>, and writes an audit row — before that file's result is recorded
/// in the response (§11). No B2 call and no <c>dbo.file_index</c> mutation, ever (§9 V5) — unlike
/// <see cref="IQuarantinePermanentDeleteService"/>, this is a catalog-only action.
/// </summary>
public interface IQuarantineRemoveService
{
    Task<QuarantineRemoveResult> RemoveAsync(QuarantineRemoveRequest request, int actingUserId, CancellationToken ct);
}
