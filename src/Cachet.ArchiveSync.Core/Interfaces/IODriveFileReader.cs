namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Reads a file's bytes from the local O-Drive mirror filesystem (CAS-007 §3/§6 — the
/// <c>SyncCopy</c>/<c>SyncUpdate</c> Worker processors "read the O-Drive file's content from the
/// local mirror path" before uploading to B2).
///
/// The local mirror root is resolved per <paramref name="bucketName"/> against CAS-011's
/// <c>dbo.odrive_bucket_mapping</c> table (CAS-013 §1/§2 — BRD §12.3 item 2), not a single flat
/// configuration key. See <see cref="Api.Storage.ODriveFileReader"/> (Api project) and
/// <see cref="IBucketRootMappingRepository"/> for the resolution mechanism.
/// </summary>
public interface IODriveFileReader
{
    /// <summary>
    /// Reads the full contents of the file at <paramref name="relativePath"/> beneath
    /// <paramref name="bucketName"/>'s configured O-Drive mirror root (CAS-013 §2/§9 V1).
    /// <paramref name="relativePath"/> is <c>dbo.file_index.relative_path</c> for the O-Drive-side
    /// row — the same value this codebase already treats as a B2 object's own <c>fileName</c> key
    /// (see <c>KeywordSearchTaskProcessor</c>'s cross-check), so no separate bucket segment is
    /// needed there. <paramref name="bucketName"/> is <c>dbo.file_index.bucket_name</c> for that
    /// same row, used only to resolve which local root to join <paramref name="relativePath"/>
    /// against.
    /// </summary>
    Task<byte[]> ReadAllBytesAsync(string bucketName, string relativePath, CancellationToken ct);
}
