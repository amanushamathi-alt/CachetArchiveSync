namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Resolves a bucket's O-Drive-mirror-side local root path from <c>dbo.odrive_bucket_mapping</c>
/// (CAS-011 §7, as-built). Used by <see cref="Api.Storage.ODriveFileReader"/> (CAS-013 §2) to
/// resolve <c>dbo.file_index.bucket_name</c> to an absolute local filesystem root, joined with
/// <c>relative_path</c> to read a <c>SyncCopy</c>/<c>SyncUpdate</c> source file's bytes.
///
/// Deliberately not a reuse of CAS-012's <c>IBucketMirrorRootRepository</c> (which is source-aware,
/// selecting either <c>odrive_root_path</c> or <c>b2_mirror_root_path</c>) — this spec never needs
/// the B2-mirror-side column, so a narrower, single-column interface is kept separate per Karpathy
/// P4 (CAS-013 §5).
/// </summary>
public interface IBucketRootMappingRepository
{
    /// <summary>
    /// Returns <c>odrive_root_path</c> for the active <c>dbo.odrive_bucket_mapping</c> row matching
    /// <paramref name="bucketName"/>, or <c>null</c> if no active mapping row exists (CAS-013 §9
    /// V1) — the caller is responsible for turning a <c>null</c> result into a clear,
    /// non-path-leaking failure.
    /// </summary>
    Task<string?> GetRootPathAsync(string bucketName, CancellationToken ct);
}
