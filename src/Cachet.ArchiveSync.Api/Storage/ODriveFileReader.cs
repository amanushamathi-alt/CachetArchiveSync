using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Storage;

/// <summary>
/// Filesystem-backed <see cref="IODriveFileReader"/> implementation (CAS-007 §3/§6, updated by
/// CAS-013 §2). Resolves the local root per <c>bucketName</c> via
/// <see cref="IBucketRootMappingRepository"/> (CAS-011's <c>dbo.odrive_bucket_mapping</c> table)
/// instead of a single flat configuration key — <c>Sync:ODriveMirrorRootPath</c> has been removed
/// (CAS-013 §14, no fallback default).
/// </summary>
public sealed class ODriveFileReader : IODriveFileReader
{
    private readonly IBucketRootMappingRepository _bucketRootMappingRepository;

    public ODriveFileReader(IBucketRootMappingRepository bucketRootMappingRepository)
    {
        _bucketRootMappingRepository = bucketRootMappingRepository;
    }

    public async Task<byte[]> ReadAllBytesAsync(string bucketName, string relativePath, CancellationToken ct)
    {
        var rootPath = await _bucketRootMappingRepository.GetRootPathAsync(bucketName, ct);
        if (rootPath is null)
        {
            // Non-path-leaking (no path value included) — caller (SyncCopy/SyncUpdate) treats
            // this as a per-file SyncActionFailedLocal failure, not a whole-batch failure
            // (CAS-013 §9 V1).
            throw new InvalidOperationException(
                "No active odrive_bucket_mapping row found for the requested bucket.");
        }

        var fullPath = Path.Combine(rootPath, relativePath);
        return await File.ReadAllBytesAsync(fullPath, ct);
    }
}
