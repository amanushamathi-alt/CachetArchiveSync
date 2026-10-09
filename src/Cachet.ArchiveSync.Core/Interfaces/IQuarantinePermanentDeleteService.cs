using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of <see cref="IQuarantinePermanentDeleteService.DeleteAsync"/> (CAS-008 §6.4).</summary>
public enum QuarantineDeleteOutcome
{
    /// <summary><c>fileIds</c> was empty (§7 V1) — <c>422 validation-failed</c>.</summary>
    ValidationFailed,

    /// <summary><c>confirm</c> missing/<c>false</c> (§7 V2), checked before any other validation
    /// or deletion attempt — <c>422 confirmation-required</c>.</summary>
    ConfirmationRequired,

    /// <summary>Every requested <c>fileId</c> failed to resolve to an <c>Active</c>
    /// <c>quarantine_entry</c> row (§7 V3) — whole-request <c>404 file-not-found</c>.</summary>
    AllFilesNotFound,

    /// <summary>Request processed — see <see cref="QuarantineDeleteResult.Response"/>'s per-file
    /// <c>results[]</c> for outcomes (may include per-file <c>Failed</c> entries) —
    /// <c>200 OK</c>.</summary>
    Completed,

    /// <summary>
    /// CAS-018 §2 step 3 — the acting user's live <c>b2_authorize_account</c> re-check (using
    /// their own stored key, not the system key) failed outright, or succeeded without
    /// <c>deleteFiles</c> in its capability set. The whole request is aborted before any
    /// requested <c>fileId</c> is touched (no B2 call, no local mutation) — whole-request
    /// <c>403 Forbidden</c>.
    /// </summary>
    AuthorizationDenied
}

/// <summary>Result of one <see cref="IQuarantinePermanentDeleteService.DeleteAsync"/> call.</summary>
public sealed record QuarantineDeleteResult
{
    public required QuarantineDeleteOutcome Outcome { get; init; }

    public QuarantineDeleteResponse? Response { get; init; }
}

/// <summary>
/// <c>POST /quarantine/delete</c> — Permanent Delete (CAS-008 §6.4, FR-16/FR-17). Per file,
/// independently: deletes every version of the B2 object (CAS-007's <c>DeleteAllVersionsAsync</c>
/// pattern, reused), soft-deletes the corresponding <c>dbo.file_index</c> row, transitions
/// <c>dbo.quarantine_entry.status</c> to <c>Deleted</c>, and writes an audit row — all before this
/// file's result is recorded in the response (§12). No recovery/rollback exists once a file's B2
/// versions are deleted (FR-17) — a per-file failure after that point is audited, not undone.
/// </summary>
public interface IQuarantinePermanentDeleteService
{
    Task<QuarantineDeleteResult> DeleteAsync(QuarantineDeleteRequest request, int actingUserId, CancellationToken ct);
}
