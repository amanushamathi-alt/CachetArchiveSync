using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Minimal, user-scoped mirror of two of <see cref="IB2Client"/>'s operations
/// (<c>b2_list_file_versions</c>, <c>b2_delete_file_version</c>) — used only by
/// <c>QuarantinePermanentDeleteService</c>'s Permanent Delete path (CAS-018 §2/§5) to execute
/// these two B2 calls under the acting user's own, freshly-authorized
/// <see cref="B2AuthorizationInfo"/> instead of <see cref="IB2Client"/>'s server-held system key.
///
/// Deliberately not an extension of <see cref="IB2Client"/> (CAS-018 §4 blast-radius avoidance):
/// <see cref="IB2Client"/> stays system-key-only for its other five consumers (search cross-check,
/// Sync Copy/Update/Purge, mirror population) and is not modified by this spec at all.
///
/// Unlike <see cref="IB2Client"/>, which resolves and caches its bucket id internally per call,
/// <see cref="ListFileVersionsAsync"/> takes the bucket id as an explicit parameter rather than
/// resolving one internally from the per-user authorization — obtained once per request via
/// <see cref="GetBucketIdAsync"/>, which resolves it using the server-held SYSTEM key (CAS-018
/// §14: bucket identity is a fact about the bucket, not about which key is asking, and this
/// project targets exactly one B2 bucket). The <see cref="B2AuthorizationInfo"/> passed to
/// <see cref="ListFileVersionsAsync"/>/<see cref="DeleteFileVersionAsync"/> is used only for those
/// calls' Authorization header — never for bucket resolution (CAS-018 §2 step 4, §11).
///
/// This interface's implementation must never be reachable from a code path where the caller
/// could substitute the system key or another user's key for the acting user's own — the
/// <see cref="B2AuthorizationInfo"/> passed in must originate only from this request's own live
/// <c>IUserB2KeyAuthorizer.AuthorizeAsync</c> call (CAS-018 §11).
/// </summary>
public interface IUserScopedB2DeleteClient
{
    /// <summary>
    /// Resolves (and caches, per-process) the single configured bucket's id via the server-held
    /// system key — mirrors <c>B2Client.GetBucketIdAsync</c> exactly (CAS-018 §14), independent of
    /// any per-user authorization.
    /// </summary>
    Task<string> GetBucketIdAsync(CancellationToken ct);

    /// <summary>
    /// Calls <c>b2_list_file_versions</c> using <paramref name="authorization"/>'s token (the
    /// acting user's own, live authorization — CAS-018 §2 step 2) against
    /// <paramref name="bucketId"/> (from <see cref="GetBucketIdAsync"/>). Mirrors
    /// <see cref="IB2Client.ListFileVersionsAsync"/>'s shape.
    /// </summary>
    Task<B2ListFileVersionsResult> ListFileVersionsAsync(
        B2AuthorizationInfo authorization,
        string bucketId,
        string? startFileName,
        string? prefix,
        int maxFileCount,
        CancellationToken ct);

    /// <summary>
    /// Calls <c>b2_delete_file_version</c> for exactly one specific version using
    /// <paramref name="authorization"/>'s token — mirrors <see cref="IB2Client.DeleteFileVersionAsync"/>'s
    /// shape exactly (B2's wire call for this operation does not take a bucket id).
    /// </summary>
    Task DeleteFileVersionAsync(
        B2AuthorizationInfo authorization,
        string fileName,
        string fileId,
        CancellationToken ct);
}
