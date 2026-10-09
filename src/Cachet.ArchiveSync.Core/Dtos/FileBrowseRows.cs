namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Raw result of <c>dbo.file_index_browse_lst</c> (CAS-043 §5): folder names are single path
/// segments and file rows are index metadata only. Consumed by <c>FileBrowseService</c>.
/// </summary>
public sealed record FileBrowsePage
{
    public required IReadOnlyList<FileBrowseFolderRow> Folders { get; init; }

    /// <summary>Empty when the call was <c>foldersOnly</c> (the SP emits no files result set).</summary>
    public required IReadOnlyList<FileBrowseFileRow> Files { get; init; }

    public required int TotalFolders { get; init; }

    public required int TotalFiles { get; init; }
}

public sealed record FileBrowseFolderRow(
    string FolderName,
    DateTimeOffset? LatestModifiedAt,
    bool HasSubfolders = true);

public sealed record FileBrowseFileRow(
    int FileIndexId,
    string FileName,
    string? FileExtension,
    long FileSizeBytes,
    DateTimeOffset ModifiedAt);
