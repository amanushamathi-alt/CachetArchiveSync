namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// CAS-062 — removes one file's local copy from the B2 mirror folder
/// (<c>dbo.odrive_bucket_mapping.b2_mirror_root_path</c> for the configured <c>B2:BucketName</c>,
/// source <c>B2</c>) after the caller has already deleted the object from Backblaze B2. Used by
/// Purge from B2 (<c>SyncPurgeTaskProcessor</c>) and Quarantine Permanent Delete
/// (<c>QuarantinePermanentDeleteService</c>). Never resolves the O-Drive root (§9 V3) and never
/// throws for a file-system or mapping problem — the outcome says what happened.
/// </summary>
public interface IB2MirrorFileRemover
{
    /// <summary>
    /// Deletes the file at B2 mirror root + <paramref name="objectName"/> (B2 key, '/'-separated).
    /// The path must resolve strictly inside the root and must not traverse a reparse point (§9 V2).
    /// </summary>
    Task<B2MirrorRemoveResult> RemoveAsync(string objectName, CancellationToken ct);
}

/// <summary>CAS-062 — what <see cref="IB2MirrorFileRemover.RemoveAsync"/> did.</summary>
public enum B2MirrorRemoveOutcome
{
    /// <summary>The local copy existed and was deleted.</summary>
    Removed,

    /// <summary>There was no local copy (never mirrored, or already removed) — not an error (§9 V4).</summary>
    NotPresent,

    /// <summary>The local copy could not be removed; nothing outside the mirror root was touched.</summary>
    Failed
}

/// <summary>CAS-062 — outcome plus a fixed, path-free reason for <see cref="B2MirrorRemoveOutcome.Failed"/>.</summary>
public sealed record B2MirrorRemoveResult(B2MirrorRemoveOutcome Outcome, string? FailureReason = null)
{
    public static readonly B2MirrorRemoveResult Removed = new(B2MirrorRemoveOutcome.Removed);

    public static readonly B2MirrorRemoveResult NotPresent = new(B2MirrorRemoveOutcome.NotPresent);

    public static B2MirrorRemoveResult Failed(string reason) => new(B2MirrorRemoveOutcome.Failed, reason);
}
