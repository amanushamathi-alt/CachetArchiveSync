namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Read-side projection returned by <c>dbo.search_result_get</c>/<c>dbo.search_result_lst</c>
/// (CAS-006 §5) — <see cref="SearchResult"/>'s own columns joined to the <c>dbo.file_index</c>
/// display fields (<c>file_name</c>, <c>relative_path</c>, <c>file_size_bytes</c>,
/// <c>modified_at</c>) the <c>GET /search/{taskId}/results</c> response (§6.2) needs. Public in
/// <c>Core</c> (not a repository-private row type like <c>UserRepository.UserRow</c>) because the
/// service layer needs it to project into <see cref="FilenameMatchItem"/>/<see cref="ContentMatchItem"/>.
/// </summary>
public sealed record SearchResultRow
{
    public required int SearchResultId { get; init; }

    public required int TaskQueueId { get; init; }

    public required int FileIndexId { get; init; }

    public required string MatchType { get; init; }

    public string? MatchSnippet { get; init; }

    public required string MatchStatus { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string FileName { get; init; }

    public required string RelativePath { get; init; }

    public required long FileSizeBytes { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    public string? BucketName { get; init; }

    public string? FileExtension { get; init; }
}
