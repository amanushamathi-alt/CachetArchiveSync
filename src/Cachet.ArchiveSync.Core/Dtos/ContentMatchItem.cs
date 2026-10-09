namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One item of <c>GET /search/{taskId}/results</c>'s <c>contentMatches.items</c> array
/// (CAS-006 §6.2, FR-07). <see cref="FileId"/> is a <c>search_result_id</c>, task-scoped
/// (§1.7) — not the same value as the file's <c>file_index_id</c>.
/// </summary>
public sealed record ContentMatchItem
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

    /// <summary>Content-match snippet (FR-07). Never the full extracted file text (CAS-012 §11) —
    /// bounded to the same length <c>dbo.search_result.match_snippet</c> allows.</summary>
    public required string Snippet { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    /// <summary>One of <c>Flagged</c>/<c>Needs Review</c>/<c>Informational</c> (CAS-006 §5).</summary>
    public required string Status { get; init; }
}
