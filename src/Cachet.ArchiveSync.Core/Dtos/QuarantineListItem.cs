namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One item of <c>GET /quarantine</c>'s <c>200 OK</c> <c>items</c> array (CAS-008 §6, FR-14).
/// <see cref="FileId"/> is the <c>quarantine_entry_id</c> — the file's stable identity while
/// quarantined (§1.7) — a different value than the <c>search_result_id</c> used to add it, and the
/// value <c>POST /quarantine/delete</c>'s <c>fileIds</c> expects.
/// </summary>
public sealed record QuarantineListItem
{
    public required int FileId { get; init; }

    /// <summary>
    /// CAS-040 §6.5 — the file's <c>dbo.file_index.file_index_id</c>, used to open it in the File
    /// Viewer (<c>GET /files/{fileIndexId}</c>). Additive; <see cref="FileId"/>'s semantics are
    /// unchanged.
    /// </summary>
    public required int FileIndexId { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required DateTimeOffset QuarantinedOn { get; init; }

    /// <summary><c>match_status_snapshot</c>, frozen at add-time — not re-derived from any later
    /// search (§6).</summary>
    public required string Status { get; init; }

    /// <summary>CAS-050 §8 — <c>"Files"</c> when added from the Files browser, else <c>"Keyword Search"</c>.</summary>
    public required string SourceTab { get; init; }
}
