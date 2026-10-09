using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of a single <see cref="IQuarantineEntryRepository.InsertAsync"/> call.</summary>
public enum QuarantineInsertOutcome
{
    Added,

    /// <summary>The given <c>search_result_id</c> does not exist (§7 V7 — whole-request <c>404 file-not-found</c>).</summary>
    SearchResultNotFound,

    /// <summary>The file already has an <c>Active</c> quarantine entry (§7 V8 — per-file skip).</summary>
    AlreadyActive
}

/// <summary>Result of one <see cref="IQuarantineEntryRepository.InsertAsync"/> call.</summary>
public sealed record QuarantineInsertResult
{
    public required QuarantineInsertOutcome Outcome { get; init; }

    public int? QuarantineEntryId { get; init; }
}

/// <summary>
/// Wraps <c>dbo.quarantine_entry_ins</c> (CAS-006 §5) — add-only (§0/§4). The stored procedure
/// resolves <c>search_result_id → file_index_id</c> and raises a friendly error (not just the
/// filtered-unique-index violation) for the "not found" and "already active" cases, by design, so
/// this repository can map those to <see cref="QuarantineInsertOutcome"/> without a raw
/// constraint-violation exception reaching the service layer (§7 V7/V8).
/// </summary>
public interface IQuarantineEntryRepository
{
    Task<QuarantineInsertResult> InsertAsync(
        string createdBy, string updatedBy, int searchResultId, CancellationToken ct);

    /// <summary>
    /// Every <c>Active</c> <c>dbo.quarantine_entry</c> row, joined to <c>dbo.file_index</c> for
    /// display fields (CAS-008 §5/§6, FR-14). Backs <c>GET /quarantine</c>. Not paginated (§6.1).
    /// </summary>
    Task<IReadOnlyList<QuarantineEntryListRow>> ListActiveAsync(CancellationToken ct);

    /// <summary>CAS-050 §7 — one page of Active rows plus <c>@total_count</c>, via <c>dbo.quarantine_entry_lst</c>.</summary>
    Task<QuarantineEntryListPage> ListActivePagedAsync(int page, int pageSize, CancellationToken ct);

    /// <summary>CAS-050 §7 — <c>dbo.quarantine_entry_ins_browse_file</c>. Outcomes: Added / SearchResultNotFound (file not found or inactive) / AlreadyActive.</summary>
    Task<QuarantineInsertResult> InsertBrowseFileAsync(
        string createdBy, string updatedBy, int fileIndexId, CancellationToken ct);

    /// <summary>CAS-050 §7 — <c>dbo.quarantine_entry_ins_browse_folder</c>, one set-based statement. <c>null</c> = folder holds no active files.</summary>
    Task<QuarantineBrowseFolderAddRow?> InsertBrowseFolderAsync(
        string createdBy, string updatedBy, string source, string bucketName, string folderPath, CancellationToken ct);

    /// <summary>CAS-050 §7 — <c>dbo.quarantine_entry_del_browse_file</c> hard delete; rows deleted (0 or 1).</summary>
    Task<int> HardDeleteBrowseFileAsync(int fileIndexId, CancellationToken ct);

    /// <summary>CAS-050 §7 — <c>dbo.quarantine_entry_del_browse_folder</c> hard delete (all origins); rows deleted, or <c>null</c> if the folder holds no active files.</summary>
    Task<int?> HardDeleteBrowseFolderAsync(string source, string bucketName, string folderPath, CancellationToken ct);

    /// <summary>CAS-050 §7 — <c>dbo.quarantine_entry_browse_state_lst</c> (two result sets).</summary>
    Task<QuarantineBrowseStateResponse> GetBrowseStateAsync(
        string source, string bucketName, string folderPath, CancellationToken ct);

    /// <summary>
    /// Resolves one <c>quarantine_entry_id</c> to its <c>Active</c> row (CAS-008 §6.4, §7 V3),
    /// including the <c>file_index_id</c> needed for the local soft-delete step, via
    /// <c>dbo.quarantine_entry_get</c>. Returns <c>null</c> if the id is unknown or the row is not
    /// currently <c>Active</c> (the procedure itself applies no status filter — the repository
    /// filters on <c>Active</c>) — mapped to a per-file <c>file-not-found</c> outcome by the
    /// caller. See <see cref="Dtos.QuarantineEntryForDeleteRow"/>'s XML doc.
    /// </summary>
    Task<QuarantineEntryForDeleteRow?> GetActiveForDeleteAsync(int quarantineEntryId, CancellationToken ct);

    /// <summary>
    /// Transitions one <c>Active</c> <c>dbo.quarantine_entry</c> row to <c>Deleted</c>
    /// (CAS-008 §5/§6.4, FR-16/FR-17), via <c>dbo.quarantine_entry_delete_upd</c>. Returns
    /// <c>false</c> if the row was not <c>Active</c> at the moment of the update (already
    /// transitioned by a concurrent request) — callers must not treat this as a partial success.
    /// Called only after the file's B2 object versions and <c>dbo.file_index</c> row have already
    /// been handled by the caller (§6.4's confirmed step ordering).
    /// </summary>
    Task<bool> TryTransitionToDeletedAsync(int quarantineEntryId, string updatedBy, CancellationToken ct);

    /// <summary>
    /// Hard-deletes one <c>Active</c> <c>dbo.quarantine_entry</c> row by <c>quarantine_entry_id</c>
    /// (CAS-014 §2.1/§7, BRD §12.3 item 3), via <c>dbo.quarantine_entry_del</c> — a genuine physical
    /// <c>DELETE</c>, not a status flag. No B2 call and no <c>dbo.file_index</c> mutation. Returns
    /// <c>false</c> if the id is unknown or the row was not <c>Active</c> at delete time (mapped to a
    /// per-file <c>file-not-found</c> outcome by the caller).
    /// </summary>
    Task<bool> HardDeleteActiveAsync(int quarantineEntryId, CancellationToken ct);
}
