namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Entity mirror of one <c>dbo.search_result</c> row (CAS-006 §5) — columns only, no joined
/// display fields (see <see cref="SearchResultRow"/> for the repository's read-side projection
/// that also carries the joined <c>dbo.file_index</c> display columns <c>search_result_get</c>/
/// <c>search_result_lst</c> return).
/// </summary>
public sealed record SearchResult
{
    public required int SearchResultId { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string UpdatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required int TaskQueueId { get; init; }

    public required int FileIndexId { get; init; }

    /// <summary><c>Filename</c> or <c>Content</c> (CAS-006 §5).</summary>
    public required string MatchType { get; init; }

    public string? MatchSnippet { get; init; }

    /// <summary><c>Flagged</c>/<c>Needs Review</c>/<c>Informational</c> (CAS-006 §5).</summary>
    public required string MatchStatus { get; init; }
}
