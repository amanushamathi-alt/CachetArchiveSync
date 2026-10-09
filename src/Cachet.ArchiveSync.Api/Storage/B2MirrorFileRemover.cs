using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Storage;

/// <summary>
/// Filesystem-backed <see cref="IB2MirrorFileRemover"/> (CAS-062 §2/§9). Resolves the B2 mirror
/// root for the configured <c>B2:BucketName</c> through <see cref="IBucketMirrorRootRepository"/>
/// with source <c>B2</c> only, applies the same containment and reparse-point checks as
/// <see cref="MirrorFileReader.OpenReadAsync"/> (CAS-040 V7), then deletes the file. A drive or
/// share root keeps its own trailing separator (CAS-059 SC-19). No path value is logged or returned.
/// </summary>
public sealed class B2MirrorFileRemover : IB2MirrorFileRemover
{
    private const string Source = "B2";

    private readonly IBucketMirrorRootRepository _bucketMirrorRootRepository;
    private readonly IConfiguration _configuration;

    public B2MirrorFileRemover(IBucketMirrorRootRepository bucketMirrorRootRepository, IConfiguration configuration)
    {
        _bucketMirrorRootRepository = bucketMirrorRootRepository;
        _configuration = configuration;
    }

    public async Task<B2MirrorRemoveResult> RemoveAsync(string objectName, CancellationToken ct)
    {
        var bucketName = _configuration["B2:BucketName"];
        if (string.IsNullOrWhiteSpace(bucketName) || string.IsNullOrWhiteSpace(objectName))
        {
            return B2MirrorRemoveResult.Failed("B2 mirror root or object name not available.");
        }

        // Mirror Sync's name rules (CAS-059 V12), plus trailing '.'/' ': any name Windows would fold
        // onto a different object's mirror file is refused, never deleted — "a/b\c.txt" or
        // "a//b.txt" or "x/../b.txt" or "b.txt::$DATA" or "b.txt." can all resolve to "…/b.txt".
        if (!IsSafeObjectName(objectName))
        {
            return B2MirrorRemoveResult.Failed("Object name is not a valid mirror path.");
        }

        string rootPath;
        try
        {
            rootPath = await _bucketMirrorRootRepository.GetRootPathAsync(bucketName, Source, ct);
        }
        catch (InvalidOperationException)
        {
            // No active mapping row; the repository's message is path-free.
            return B2MirrorRemoveResult.Failed("No active B2 mirror mapping.");
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return B2MirrorRemoveResult.Failed("No active B2 mirror mapping.");
        }

        string fullRoot;
        string fullPath;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            fullPath = Path.GetFullPath(Path.Combine(fullRoot, objectName.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return B2MirrorRemoveResult.Failed("Object name is not a valid mirror path.");
        }

        // §9 V2 — strictly beneath the root (a drive/share root already ends with a separator).
        var rootWithSeparator = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return B2MirrorRemoveResult.Failed("Object name resolves outside the B2 mirror root.");
        }

        try
        {
            if (!File.Exists(fullPath))
            {
                return B2MirrorRemoveResult.NotPresent;
            }

            // A junction/symlink inside the mirror must not redirect the delete outside it.
            if (TraversesReparsePoint(fullRoot, fullPath))
            {
                return B2MirrorRemoveResult.Failed("Mirror path traverses a reparse point.");
            }

            File.Delete(fullPath);
            return B2MirrorRemoveResult.Removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return B2MirrorRemoveResult.Failed("File is locked or access was denied.");
        }
    }

    private static bool IsSafeObjectName(string objectName)
    {
        if (objectName.Any(ch => char.IsControl(ch) || "\\<>:\"|?*".Contains(ch)))
        {
            return false;
        }

        return objectName.Split('/').All(segment =>
            segment.Length > 0 && segment != "." && segment != ".." && !segment.EndsWith('.') && !segment.EndsWith(' '));
    }

    private static bool TraversesReparsePoint(string fullRoot, string fullPath)
    {
        var current = fullPath;
        while (current.Length > fullRoot.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
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
