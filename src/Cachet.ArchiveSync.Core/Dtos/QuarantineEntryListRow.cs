namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One row of <c>dbo.quarantine_entry_lst</c> (CAS-008 §5) — an <c>Active</c> quarantine entry
/// joined to <c>dbo.file_index</c> for its display fields. Backs <c>GET /quarantine</c> (§6,
/// FR-14).
/// </summary>
public sealed record QuarantineEntryListRow
{
    public required int QuarantineEntryId { get; init; }

    /// <summary>CAS-040 §5/§6.5 — <c>fi.file_index_id</c>, appended to
    /// <c>dbo.quarantine_entry_lst</c>'s result set.</summary>
    public required int FileIndexId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required DateTimeOffset QuarantinedOn { get; init; }

    /// <summary><c>match_status_snapshot</c>, frozen at add-time (CAS-006 §5) — not re-derived
    /// from any later search (§6).</summary>
    public required string Status { get; init; }

    /// <summary>CAS-050 §7 — <c>source_tab</c>: <c>Files</c> or <c>Keyword Search</c>.</summary>
    public required string SourceTab { get; init; }
}
