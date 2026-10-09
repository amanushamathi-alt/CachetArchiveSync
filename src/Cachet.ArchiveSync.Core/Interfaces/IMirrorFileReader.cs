using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Reads a file's bytes from whichever local mirror root (O-Drive or B2) CAS-011's
/// <c>dbo.odrive_bucket_mapping</c> table resolves for a given <paramref name="source"/>/
/// <paramref name="bucketName"/> pair (CAS-012 §2/§5). Used by <c>KeywordSearchTaskProcessor</c>
/// to read a <c>Content</c> candidate's physical file before matching it against the search term
/// (<see cref="IFileContentMatcher"/>).
///
/// Deliberately a separate abstraction from <see cref="IODriveFileReader"/> (CAS-007) rather than
/// an extension of it — CAS-007's reader resolves a root from a single flat config key
/// (<c>Sync:ODriveMirrorRootPath</c>), while this reader is table-driven and source-aware (O-Drive
/// or B2 mirror, per bucket). See CAS-012 §14 for the deferred, non-blocking consolidation note.
/// </summary>
public interface IMirrorFileReader
{
    /// <summary>
    /// Reads the full contents of the file at <paramref name="relativePath"/> beneath the mirror
    /// root resolved for <paramref name="source"/>/<paramref name="bucketName"/>
    /// (<see cref="IBucketMirrorRootRepository"/>). Throws on a missing file, permission error, or
    /// unresolvable mapping — the caller (CAS-012 §7 V10) treats any such failure as a skip for
    /// that one candidate, not a task failure.
    /// </summary>
    Task<byte[]> ReadAllBytesAsync(string source, string bucketName, string relativePath, CancellationToken ct);

    /// <summary>
    /// CAS-040 §6 pipeline steps 3–6 — resolves <paramref name="file"/>'s mirror root by
    /// <c>(BucketName, Source)</c>, applies the mirror-root containment and reparse-point checks
    /// (V7), confirms the file exists (V8), and opens it read-only with
    /// <c>FileShare.Read | FileShare.Delete</c>. Never throws for V6–V8; the outcome is on the
    /// returned handle, which the caller must dispose.
    /// </summary>
    Task<MirrorFileHandle> OpenReadAsync(FileIndexRecord file, CancellationToken ct);
}
