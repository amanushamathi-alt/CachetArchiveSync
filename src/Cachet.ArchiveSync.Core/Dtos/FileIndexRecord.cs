namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One <c>dbo.file_index</c> row as returned by <c>dbo.file_index_get</c> (CAS-040 §5) — looked up
/// by <c>file_index_id</c>, inactive rows included (the caller maps <see cref="IsActive"/> =
/// <c>false</c> to 410, §6.4). Backs the File Viewer's resolution pipeline (§6 step 1).
/// </summary>
public sealed record FileIndexRecord
{
    public required int FileIndexId { get; init; }

    /// <summary><c>ODrive</c> or <c>B2</c> (CAS-003 §5).</summary>
    public required string Source { get; init; }

    public string? BucketName { get; init; }

    public required string RelativePath { get; init; }

    public required string FileName { get; init; }

    public string? FileExtension { get; init; }

    public required long FileSizeBytes { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }

    public required bool IsActive { get; init; }
}
