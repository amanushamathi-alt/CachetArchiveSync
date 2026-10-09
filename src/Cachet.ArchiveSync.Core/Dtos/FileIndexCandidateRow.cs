namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One local-first search candidate from <c>dbo.file_index</c>, already tagged with which panel
/// it matched (CAS-006 §3): <c>Filename</c> (filename <c>LIKE</c>, term already confirmed by SQL)
/// or <c>Content</c> (CAS-012 §2 — an *unfiltered* content-scan candidate: every active,
/// in-scope-extension row for the requested bucket/extension, regardless of whether the search
/// term is actually present in the file). Produced before the B2 cross-check (§6, §12) — a row
/// here is a *candidate*, not yet a confirmed <see cref="SearchResult"/>.
///
/// For a <c>Content</c> row, <see cref="Source"/>/<see cref="BucketName"/>/<see cref="RelativePath"/>
/// are resolved by the Worker (via <see cref="Interfaces.IBucketMirrorRootRepository"/>/
/// <see cref="Interfaces.IMirrorFileReader"/>) to a physical mirror file, which is read and matched
/// against the search term in application code (CAS-012 §2) — SQL performs no term filtering or
/// snippet computation for <c>Content</c> rows; <see cref="MatchSnippet"/> is always <c>null</c>
/// for a <c>Content</c> row as returned by <see cref="Interfaces.IFileIndexRepository.SearchCandidatesAsync"/>
/// and is populated only after the Worker confirms a match.
/// </summary>
public sealed record FileIndexCandidateRow
{
    public required int FileIndexId { get; init; }

    public required string FileName { get; init; }

    public required string RelativePath { get; init; }

    public required long FileSizeBytes { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    /// <summary><c>Filename</c> or <c>Content</c> — which panel this candidate belongs to.</summary>
    public required string MatchType { get; init; }

    /// <summary>Content-match snippet; populated only when <see cref="MatchType"/> is <c>Content</c>
    /// and only after the Worker has confirmed the term is present in the file's extracted text
    /// (CAS-012 §2/§7 V12) — never populated by <see cref="Interfaces.IFileIndexRepository.SearchCandidatesAsync"/>
    /// itself.</summary>
    public string? MatchSnippet { get; init; }

    /// <summary><c>ODrive</c> or <c>B2</c> (CAS-012 §5/§7) — which physical mirror this candidate's
    /// file lives on, needed to resolve a local read path via
    /// <see cref="Interfaces.IBucketMirrorRootRepository"/>.</summary>
    public required string Source { get; init; }

    /// <summary>The B2 bucket this candidate's <see cref="Source"/> mirror is rooted at (CAS-012
    /// §5/§7) — combined with <see cref="Source"/> to resolve a mirror root path.</summary>
    public required string BucketName { get; init; }

    /// <summary>SHA-1 of the file as recorded in <c>dbo.file_index</c> (null when unknown). Two
    /// candidates with the same hash and size are byte-identical, so a search extracts/matches the
    /// content once and reuses the result.</summary>
    public string? Sha1Hash { get; init; }
}
