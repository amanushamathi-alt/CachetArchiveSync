namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Outcome of <see cref="Interfaces.IMirrorFileReader.OpenReadAsync"/> — CAS-040 §6 resolution
/// pipeline steps 3–6.
/// </summary>
public enum MirrorFileOpenOutcome
{
    Opened,

    /// <summary>No active <c>dbo.odrive_bucket_mapping</c> row for <c>(bucket_name, source)</c> (V6).</summary>
    MappingMissing,

    /// <summary>The resolved path escapes the mirror root or is a reparse point (V7).</summary>
    PathRejected,

    /// <summary>The physical file does not exist beneath the mirror root (V8).</summary>
    MissingFromMirror
}

/// <summary>
/// A read-only handle on one mirror file (CAS-040 §6 step 6). When <see cref="Outcome"/> is
/// <see cref="MirrorFileOpenOutcome.Opened"/>, <see cref="Stream"/> is open with
/// <c>FileShare.Read | FileShare.Delete</c> so a concurrent Permanent Delete or Purge is never
/// blocked (§12). Carries no path — the absolute path never leaves the reader (§11).
/// </summary>
public sealed class MirrorFileHandle : IDisposable, IAsyncDisposable
{
    private MirrorFileHandle(MirrorFileOpenOutcome outcome, Stream? stream, long length)
    {
        Outcome = outcome;
        Stream = stream;
        Length = length;
    }

    public MirrorFileOpenOutcome Outcome { get; }

    /// <summary>Open, seekable read stream; <c>null</c> unless <see cref="Outcome"/> is
    /// <see cref="MirrorFileOpenOutcome.Opened"/>.</summary>
    public Stream? Stream { get; }

    /// <summary>Physical length in bytes at open time; 0 unless opened.</summary>
    public long Length { get; }

    public static MirrorFileHandle Opened(Stream stream) => new(MirrorFileOpenOutcome.Opened, stream, stream.Length);

    public static MirrorFileHandle Failed(MirrorFileOpenOutcome outcome)
    {
        if (outcome == MirrorFileOpenOutcome.Opened)
        {
            throw new ArgumentException("A failed handle cannot have the Opened outcome.", nameof(outcome));
        }

        return new MirrorFileHandle(outcome, null, 0);
    }

    public void Dispose() => Stream?.Dispose();

    public ValueTask DisposeAsync() => Stream?.DisposeAsync() ?? ValueTask.CompletedTask;
}
