namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Raw result of <c>dbo.search_result_browse_lst</c> (CAS-049 §7). Folder rows reuse
/// <see cref="FileBrowseFolderRow"/>; file rows add the search result id and match status.
/// </summary>
public sealed record SearchResultBrowsePage
{
    public required IReadOnlyList<FileBrowseFolderRow> Folders { get; init; }

    /// <summary>Empty when the call was <c>foldersOnly</c> (the SP emits no files result set).</summary>
    public required IReadOnlyList<SearchResultBrowseFileRow> Files { get; init; }

    public required int TotalFolders { get; init; }

    public required int TotalFiles { get; init; }
}

public sealed record SearchResultBrowseFileRow(
    int FileIndexId,
    int SearchResultId,
    string FileName,
    string? FileExtension,
    long FileSizeBytes,
    DateTimeOffset ModifiedAt,
    string? MatchStatus);

/// <summary><c>GET /search/{taskId}/browse/result-ids</c> 200 body (CAS-049 §8).</summary>
public sealed record SearchResultIdsResponse
{
    public required IReadOnlyList<int> SearchResultIds { get; init; }

    public required int Count { get; init; }
}
