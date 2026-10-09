namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Read-side projection returned by <c>dbo.sync_result_get</c>/<c>dbo.sync_result_lst</c>
/// (CAS-007 §5) — <c>sync_result</c>'s own columns joined to <c>dbo.file_index</c>'s display
/// fields for both the O-Drive-side and B2-side FK roles. Public in <c>Core</c> because the
/// service layer and the Worker's four task processors all need it (projection into the three
/// results panels, and per-file source data for Copy/Update/Purge).
/// </summary>
public sealed record SyncResultRow
{
    public required int SyncResultId { get; init; }

    public required int TaskQueueId { get; init; }

    /// <summary><c>New</c>, <c>Removed</c>, or <c>Modified</c> (CAS-007 §5).</summary>
    public required string ComparisonType { get; init; }

    public int? OdriveFileIndexId { get; init; }

    public int? B2FileIndexId { get; init; }

    public bool? Sha1Changed { get; init; }

    /// <summary><c>Pending</c>, <c>Synced</c>, or <c>Failed</c> (CAS-007 §5).</summary>
    public required string SyncStatus { get; init; }

    public int? SyncActionTaskQueueId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public string? OdriveFileName { get; init; }

    public string? OdriveRelativePath { get; init; }

    /// <summary>
    /// <c>dbo.file_index.bucket_name</c> for the O-Drive-side row (CAS-013 §2/§5) — used to resolve
    /// the per-bucket local root before reading this file's bytes in <c>SyncCopy</c>/
    /// <c>SyncUpdate</c>. Never surfaced in an API response (CAS-013 §8).
    /// </summary>
    public string? OdriveBucketName { get; init; }

    public long? OdriveFileSizeBytes { get; init; }

    public DateTimeOffset? OdriveModifiedAt { get; init; }

    public string? B2FileName { get; init; }

    public string? B2RelativePath { get; init; }

    public long? B2FileSizeBytes { get; init; }

    public DateTimeOffset? B2ModifiedAt { get; init; }
}
