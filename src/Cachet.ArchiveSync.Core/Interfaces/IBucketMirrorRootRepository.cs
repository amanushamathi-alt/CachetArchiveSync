namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Resolves a bucket's local mirror root path from <c>dbo.odrive_bucket_mapping</c> (CAS-011 §7,
/// as-built — one row per <c>bucket_name</c> carrying both an <c>odrive_root_path</c> and a
/// <c>b2_mirror_root_path</c> column). Used by <see cref="IMirrorFileReader"/> (CAS-012 §2/§5) to
/// resolve a <c>FileIndexCandidateRow</c>'s <c>(Source, BucketName)</c> to an absolute local
/// filesystem root, joined with <c>RelativePath</c> to read the candidate's physical file.
/// </summary>
public interface IBucketMirrorRootRepository
{
    /// <summary>
    /// Returns the mirror root path for <paramref name="bucketName"/> — <c>odrive_root_path</c>
    /// when <paramref name="source"/> is <c>ODrive</c>, <c>b2_mirror_root_path</c> when
    /// <paramref name="source"/> is <c>B2</c>. Throws <see cref="InvalidOperationException"/>
    /// (a clear, non-path-leaking message — no path value is included) if no active mapping row
    /// exists for <paramref name="bucketName"/>; the caller (CAS-012 §7 V10) treats this as an
    /// unreadable-file skip, not a task failure.
    /// </summary>
    Task<string> GetRootPathAsync(string bucketName, string source, CancellationToken ct);
}
