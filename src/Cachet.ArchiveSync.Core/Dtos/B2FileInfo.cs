namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// A single B2 object (or object version) as returned by <c>b2_get_file_info</c>,
/// <c>b2_list_file_names</c>, <c>b2_list_file_versions</c>, or <c>b2_upload_file</c>
/// (CAS-001 §6.1). Not the mirror-index entity (§5) — this is the live B2-side view only.
/// </summary>
public sealed record B2FileInfo
{
    public required string FileId { get; init; }

    public required string FileName { get; init; }

    /// <summary>SHA-1 hex digest B2 computed/verified for this object; drives §6.2's "skip unchanged" check.</summary>
    public required string ContentSha1 { get; init; }

    public required long ContentLength { get; init; }

    /// <summary>
    /// <c>"upload"</c> (a real, non-hidden file), <c>"hide"</c> (soft-deleted), <c>"folder"</c>,
    /// or <c>"start"</c> (an incomplete large-file upload) — §6.3 step 2 treats only
    /// <c>"upload"</c> as a confirmed match.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>B2's <c>uploadTimestamp</c> (milliseconds since epoch) — drives §6.2 change detection.</summary>
    public required long UploadTimestamp { get; init; }
}
