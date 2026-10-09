using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// CAS-049 §8 — folder/file view of one completed KeywordSearch task's matches, plus folder-to-ids
/// resolution for selection. Ownership comes from the caller's claim-derived user id. Not audited (§11).
/// </summary>
public interface ISearchResultBrowseService
{
    /// <summary>Maximum ids <see cref="GetResultIdsByFolderAsync"/> returns (CAS-049 §8, Q8).</summary>
    public const int ResultIdsLimit = 1000;

    Task<SearchResultBrowseResult> BrowseSearchResultsAsync(
        int taskId, int userId, string? folder, bool foldersOnly, CancellationToken ct);

    Task<SearchResultIdsResult> GetResultIdsByFolderAsync(
        int taskId, int userId, string? folder, CancellationToken ct);
}

/// <summary>CAS-049 outcomes, mapped by <c>SearchController</c> to status codes/ProblemDetails.</summary>
public enum SearchResultBrowseOutcome
{
    Success,

    /// <summary>V2 — 400 <c>validation-failed</c>.</summary>
    ValidationFailed,

    /// <summary>V1 — 404 <c>task-not-found</c> (missing, not the caller's, or not a KeywordSearch task).</summary>
    TaskNotFound,

    /// <summary>409 <c>task-not-complete</c> (any status other than Completed).</summary>
    TaskNotComplete,

    /// <summary>V4 — 404 <c>folder-not-found</c>.</summary>
    FolderNotFound,

    /// <summary>V8 — 400 <c>selection-too-large</c> with <c>{ "limit": 1000 }</c> (result-ids only).</summary>
    SelectionTooLarge,

    /// <summary>500 <c>bucket-not-configured</c>.</summary>
    BucketNotConfigured,

    /// <summary>500 <c>mirror-mapping-missing</c>.</summary>
    MappingMissing,

    /// <summary>V6 — 500 <c>browse-failed</c>.</summary>
    BrowseFailed
}

public sealed record SearchResultBrowseResult
{
    public required SearchResultBrowseOutcome Outcome { get; init; }

    public FolderBrowseResponse? Response { get; init; }
}

public sealed record SearchResultIdsResult
{
    public required SearchResultBrowseOutcome Outcome { get; init; }

    public SearchResultIdsResponse? Response { get; init; }
}
