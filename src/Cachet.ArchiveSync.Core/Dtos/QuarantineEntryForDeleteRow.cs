namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One <c>dbo.quarantine_entry</c> row resolved for <c>POST /quarantine/delete</c> (CAS-008 §6,
/// §7 V3) — carries the <c>file_index_id</c> needed to soft-delete the corresponding
/// <c>dbo.file_index</c> row (<see cref="Interfaces.IFileIndexRepository.UpdateAsync"/>) plus the
/// B2 object's relative path, for a single <c>quarantine_entry_id</c>, restricted to
/// <c>status = 'Active'</c> rows.
///
/// Resolved via <c>dbo.quarantine_entry_get</c> (db-author addendum,
/// Database/migrations/20260922_190000__CAS-008-quarantine-permanent-delete-api-addendum.sql),
/// joined to <c>dbo.file_index</c> for <c>file_index_id</c>/name/path so Permanent Delete's
/// per-file resolution (§7 V3) can call
/// <see cref="Interfaces.IFileIndexRepository.UpdateAsync"/> after the B2 delete succeeds (§6's
/// confirmed B2-delete → local-soft-delete → catalog-update ordering) without guessing
/// <c>file_index_id</c> from name/path. The procedure applies no status filter itself (returns any
/// row for a known id, including non-<c>Active</c> ones) so "unknown id" and "found but not
/// Active" stay distinguishable in principle to the caller;
/// <see cref="Interfaces.IQuarantineEntryRepository.GetActiveForDeleteAsync"/> applies the
/// <c>Active</c> filter and returns <c>null</c> for both cases, since both map to the same
/// per-file <c>file-not-found</c> outcome at the service layer (§7 V3).
/// </summary>
public sealed record QuarantineEntryForDeleteRow
{
    public required int QuarantineEntryId { get; init; }

    public required int FileIndexId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }
}
