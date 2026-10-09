using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Storage;

/// <summary>
/// Filesystem-backed <see cref="IMirrorFileReader"/> implementation (CAS-012 §2/§5). Resolves the
/// mirror root via <see cref="IBucketMirrorRootRepository"/> (CAS-011's
/// <c>dbo.odrive_bucket_mapping</c>), then reads the file the same way <see cref="ODriveFileReader"/>
/// (CAS-007) does — <c>Path.Combine</c> + <c>File.ReadAllBytesAsync</c> — but with a table-driven,
/// source-aware root instead of a single flat config key.
/// </summary>
public sealed class MirrorFileReader : IMirrorFileReader
{
    private readonly IBucketMirrorRootRepository _bucketMirrorRootRepository;

    // Mapping root per (source, bucket), cached for this reader's (scoped) lifetime so a scan of
    // hundreds of thousands of files does one mapping lookup, not one per file.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Source, string Bucket), string> _rootCache = new();

    public MirrorFileReader(IBucketMirrorRootRepository bucketMirrorRootRepository)
    {
        _bucketMirrorRootRepository = bucketMirrorRootRepository;
    }

    public async Task<byte[]> ReadAllBytesAsync(string source, string bucketName, string relativePath, CancellationToken ct)
    {
        if (!_rootCache.TryGetValue((source, bucketName), out var rootPath))
        {
            rootPath = await _bucketMirrorRootRepository.GetRootPathAsync(bucketName, source, ct);
            _rootCache[(source, bucketName)] = rootPath;
        }

        var fullPath = Path.Combine(rootPath, relativePath);
        return await File.ReadAllBytesAsync(fullPath, ct);
    }

    /// <summary>
    /// CAS-040 §6 steps 3–6. The path is built from DB values only (mapping root +
    /// <c>relative_path</c>), then must resolve strictly beneath the resolved root (trailing
    /// separator, so <c>D:\mirror2</c> never matches root <c>D:\mirror</c>) and must not traverse a
    /// reparse point (V7) before existence (V8) and open (step 6) are attempted. No path value is
    /// logged or returned (§11).
    /// </summary>
    public async Task<MirrorFileHandle> OpenReadAsync(FileIndexRecord file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file.BucketName))
        {
            return MirrorFileHandle.Failed(MirrorFileOpenOutcome.MappingMissing);
        }

        string rootPath;
        try
        {
            rootPath = await _bucketMirrorRootRepository.GetRootPathAsync(file.BucketName, file.Source, ct);
        }
        catch (InvalidOperationException)
        {
            // No active mapping row (or an unrecognized source) — V6. The repository's message is
            // deliberately path-free.
            return MirrorFileHandle.Failed(MirrorFileOpenOutcome.MappingMissing);
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return MirrorFileHandle.Failed(MirrorFileOpenOutcome.MappingMissing);
        }

        string fullRoot;
        string fullPath;
        try
        {
            fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            fullPath = Path.GetFullPath(Path.Combine(rootPath, file.RelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return MirrorFileHandle.Failed(MirrorFileOpenOutcome.PathRejected);
        }

        // Step 4 — strict prefix containment (V7).
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return MirrorFileHandle.Failed(MirrorFileOpenOutcome.PathRejected);
        }

        // Step 4 — reparse-point rejection (V7): the file itself and every directory between the
        // root and the file, so a junction/symlink inside the mirror cannot redirect the read
        // outside it.
        if (TraversesReparsePoint(fullRoot, fullPath))
        {
            return MirrorFileHandle.Failed(MirrorFileOpenOutcome.PathRejected);
        }

        // Step 5 (V8).
        if (!File.Exists(fullPath))
        {
            return MirrorFileHandle.Failed(MirrorFileOpenOutcome.MissingFromMirror);
        }

        // Step 6 — FileShare.Delete so a concurrent Permanent Delete / Purge is never blocked (§12).
        try
        {
            var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return MirrorFileHandle.Opened(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between the existence check and the open.
            return MirrorFileHandle.Failed(MirrorFileOpenOutcome.MissingFromMirror);
        }
    }

    private static bool TraversesReparsePoint(string fullRoot, string fullPath)
    {
        var current = fullPath;
        while (current.Length > fullRoot.Length)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    return true;
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Length >= current.Length)
            {
                break;
            }

            current = parent;
        }

        return false;
    }
}
