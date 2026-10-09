using System.Globalization;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IQuarantinePermanentDeleteService"/> implementation of CAS-008 §6.4 — Permanent
/// Delete (FR-16/FR-17). Each requested <c>fileId</c> (a <c>quarantine_entry_id</c>) is handled
/// independently: resolve → delete every B2 object version (CAS-007's
/// <c>SyncPurgeTaskProcessor.DeleteAllVersionsAsync</c> pattern, reused here since this is an
/// Api-side, not Worker-side, action — §6.4) → soft-delete the local <c>dbo.file_index</c> row →
/// transition <c>dbo.quarantine_entry.status</c> to <c>Deleted</c> → write an audit row, all
/// before that file's result is recorded (§12). A failure at any step stops that file's own
/// pipeline but never the batch (§6.4, §7 V3-V6) and is audited with a non-success result — no
/// step ever un-does a prior step's success (FR-17's no-rollback design).
///
/// CAS-018 §2: before the per-file loop, this service performs a live, fresh
/// <c>b2_authorize_account</c> re-check of the acting user's own stored B2 key (never the system
/// key, never the stale login-time <c>b2_capability</c> claim). If that check fails, or succeeds
/// without <c>deleteFiles</c>, the entire request is aborted — no B2 call, no local mutation, for
/// any requested <c>fileId</c> — with one audit entry and a whole-request
/// <see cref="QuarantineDeleteOutcome.AuthorizationDenied"/> outcome. Otherwise, every B2 call in
/// the per-file loop below (Step 1 of each file's pipeline) is routed through
/// <see cref="IUserScopedB2DeleteClient"/> using that live authorization — never
/// <see cref="IB2Client"/>/the system key (CAS-018 §11).
///
/// CAS-062: after the <c>dbo.file_index</c> soft-delete, the file's local B2 mirror copy is removed
/// through <see cref="IB2MirrorFileRemover"/> (B2 mirror root only — the O-Drive source is never
/// touched). A local failure returns <c>local-delete-failed</c> and leaves the entry Active.
/// </summary>
public sealed class QuarantinePermanentDeleteService : IQuarantinePermanentDeleteService
{
    private const string ActingProcessIdentity = "api-svc";
    private const string TabModule = "Quarantine";

    private const string ResultSuccess = "Success";
    private const string ResultFailed = "Failed";

    private const string DeleteFilesCapability = "deleteFiles";

    private readonly IQuarantineEntryRepository _quarantineEntryRepository;
    private readonly IFileIndexRepository _fileIndexRepository;
    private readonly IUserRepository _userRepository;
    private readonly ICredentialProtector _credentialProtector;
    private readonly IUserB2KeyAuthorizer _userB2KeyAuthorizer;
    private readonly IUserScopedB2DeleteClient _userScopedB2DeleteClient;
    private readonly IB2MirrorFileRemover _b2MirrorFileRemover;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<QuarantinePermanentDeleteService> _logger;

    public QuarantinePermanentDeleteService(
        IQuarantineEntryRepository quarantineEntryRepository,
        IFileIndexRepository fileIndexRepository,
        IUserRepository userRepository,
        ICredentialProtector credentialProtector,
        IUserB2KeyAuthorizer userB2KeyAuthorizer,
        IUserScopedB2DeleteClient userScopedB2DeleteClient,
        IB2MirrorFileRemover b2MirrorFileRemover,
        IAuditLogService auditLogService,
        ILogger<QuarantinePermanentDeleteService> logger)
    {
        _quarantineEntryRepository = quarantineEntryRepository;
        _fileIndexRepository = fileIndexRepository;
        _userRepository = userRepository;
        _credentialProtector = credentialProtector;
        _userB2KeyAuthorizer = userB2KeyAuthorizer;
        _userScopedB2DeleteClient = userScopedB2DeleteClient;
        _b2MirrorFileRemover = b2MirrorFileRemover;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<QuarantineDeleteResult> DeleteAsync(QuarantineDeleteRequest request, int actingUserId, CancellationToken ct)
    {
        // §7 V1: fileIds must be non-empty.
        if (request.FileIds.Count == 0)
        {
            return new QuarantineDeleteResult { Outcome = QuarantineDeleteOutcome.ValidationFailed };
        }

        // §7 V2: confirm must be true, checked before any other validation or deletion attempt.
        if (!request.Confirm)
        {
            return new QuarantineDeleteResult { Outcome = QuarantineDeleteOutcome.ConfirmationRequired };
        }

        var actingUserIdentity = actingUserId.ToString(CultureInfo.InvariantCulture);

        // CAS-018 §2 steps 1-4: live pre-batch B2 authorization check using the acting user's own
        // key — fresh, not reused from login. Aborts the whole request before any file is touched
        // if the check fails outright or lacks deleteFiles.
        var liveAuthorization = await AuthorizeActingUserForDeleteAsync(actingUserId, ct);
        if (liveAuthorization is null)
        {
            await _auditLogService.WriteAsync(new AuditLogEntry
            {
                CreatedBy = ActingProcessIdentity,
                UpdatedBy = ActingProcessIdentity,
                UserId = actingUserId,
                TabModule = TabModule,
                Action = AuditAction.DeleteAuthorizationDenied,
                Result = AuditResult.Failure,
                ResultDetail = "Live B2 authorization check failed, or succeeded without deleteFiles."
            }, ct);

            _logger.LogWarning(
                "Permanent Delete request denied for user {UserId}: live B2 authorization check failed or lacked deleteFiles",
                actingUserId);

            return new QuarantineDeleteResult { Outcome = QuarantineDeleteOutcome.AuthorizationDenied };
        }

        // CAS-018 §14: bucket identity does not vary by acting user — resolved once (system key,
        // cached) and reused across every file in this request's loop.
        var bucketId = await _userScopedB2DeleteClient.GetBucketIdAsync(ct);

        var results = new List<QuarantineDeleteResultItem>();
        var notFoundCount = 0;
        var succeededCount = 0;
        var failedCount = 0;

        foreach (var fileId in request.FileIds)
        {
            // §7 V3: resolve the quarantine_entry_id to an Active row. No row => per-file
            // file-not-found, no B2 or local mutation attempted for this file.
            var entry = await _quarantineEntryRepository.GetActiveForDeleteAsync(fileId, ct);
            if (entry is null)
            {
                results.Add(new QuarantineDeleteResultItem
                {
                    FileId = fileId,
                    Result = ResultFailed,
                    Reason = "file-not-found"
                });
                notFoundCount++;
                failedCount++;
                continue;
            }

            // Step 1: delete every B2 object version (§6.4, §7 V4). On failure, dbo.file_index and
            // dbo.quarantine_entry are left unchanged for this file (§7 V5).
            try
            {
                await DeleteAllVersionsAsync(entry.Path, liveAuthorization, bucketId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var errorSummary = ex is B2ApiException ? "B2 delete call failed." : "Unhandled error deleting file from B2.";

                await _auditLogService.WriteAsync(new AuditLogEntry
                {
                    CreatedBy = ActingProcessIdentity,
                    UpdatedBy = ActingProcessIdentity,
                    UserId = actingUserId,
                    TabModule = TabModule,
                    Action = AuditAction.DeleteFailedB2,
                    FilePath = entry.Path,
                    Result = AuditResult.Failure,
                    ResultDetail = errorSummary
                }, ct);

                _logger.LogWarning(
                    "Permanent Delete failed for quarantine entry {QuarantineEntryId}: {ErrorSummary}",
                    fileId, errorSummary);

                results.Add(new QuarantineDeleteResultItem
                {
                    FileId = fileId,
                    Result = ResultFailed,
                    Reason = "b2-delete-failed"
                });
                failedCount++;
                continue;
            }

            // Step 2: soft-delete the local dbo.file_index row (§6.4). The B2 object is already
            // gone at this point — a failure here is not rolled back (§7 V6, FR-17).
            try
            {
                await _fileIndexRepository.UpdateAsync(
                    entry.FileIndexId, ActingProcessIdentity,
                    sha1Hash: null, fileSizeBytes: null, modifiedAt: null, lastSeenAt: null,
                    isActive: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                const string errorSummary = "Unhandled error soft-deleting local file_index row after B2 delete succeeded.";

                await _auditLogService.WriteAsync(new AuditLogEntry
                {
                    CreatedBy = ActingProcessIdentity,
                    UpdatedBy = ActingProcessIdentity,
                    UserId = actingUserId,
                    TabModule = TabModule,
                    Action = AuditAction.DeleteFailedLocal,
                    FilePath = entry.Path,
                    Result = AuditResult.Failure,
                    ResultDetail = errorSummary
                }, ct);

                _logger.LogWarning(
                    "Permanent Delete failed for quarantine entry {QuarantineEntryId}: {ErrorSummary}",
                    fileId, errorSummary);

                results.Add(new QuarantineDeleteResultItem
                {
                    FileId = fileId,
                    Result = ResultFailed,
                    Reason = "local-delete-failed"
                });
                failedCount++;
                continue;
            }

            // CAS-062 step 2b: remove the local B2 mirror copy (B2 mirror root only, never O-Drive;
            // entry.Path is the B2 key just deleted). Absent is fine. On failure the entry stays
            // Active, so a retry repeats the steps safely (B2 delete / soft-delete are idempotent).
            // An unexpected error is a local failure for this file only — never a batch abort.
            B2MirrorRemoveResult localRemoval;
            try
            {
                localRemoval = await _b2MirrorFileRemover.RemoveAsync(entry.Path, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Permanent Delete: unexpected error removing local mirror copy for quarantine entry {QuarantineEntryId}", fileId);
                localRemoval = B2MirrorRemoveResult.Failed("Unexpected error removing the local copy.");
            }

            if (localRemoval.Outcome == B2MirrorRemoveOutcome.Failed)
            {
                await _auditLogService.WriteAsync(new AuditLogEntry
                {
                    CreatedBy = ActingProcessIdentity,
                    UpdatedBy = ActingProcessIdentity,
                    UserId = actingUserId,
                    TabModule = TabModule,
                    Action = AuditAction.DeleteFailedLocal,
                    FilePath = entry.Path,
                    Result = AuditResult.Failure,
                    ResultDetail = "Deleted from B2; local mirror copy not removed: " + localRemoval.FailureReason
                }, ct);

                _logger.LogWarning(
                    "Permanent Delete failed for quarantine entry {QuarantineEntryId}: local mirror copy not removed: {Reason}",
                    fileId, localRemoval.FailureReason);

                results.Add(new QuarantineDeleteResultItem
                {
                    FileId = fileId,
                    Result = ResultFailed,
                    Reason = "local-delete-failed"
                });
                failedCount++;
                continue;
            }

            // Step 3: transition quarantine_entry.status -> Deleted (§6.4). A false return means a
            // concurrent request already moved this row out of Active — B2 and file_index are
            // already gone/soft-deleted for this file regardless, so this is reported the same way
            // as a local failure (no B2/local rollback exists, FR-17).
            var transitioned = await _quarantineEntryRepository.TryTransitionToDeletedAsync(fileId, actingUserIdentity, ct);
            if (!transitioned)
            {
                const string errorSummary = "quarantine_entry was no longer Active at status-transition time (concurrent update).";

                await _auditLogService.WriteAsync(new AuditLogEntry
                {
                    CreatedBy = ActingProcessIdentity,
                    UpdatedBy = ActingProcessIdentity,
                    UserId = actingUserId,
                    TabModule = TabModule,
                    Action = AuditAction.DeleteFailedLocal,
                    FilePath = entry.Path,
                    Result = AuditResult.Failure,
                    ResultDetail = errorSummary
                }, ct);

                _logger.LogWarning(
                    "Permanent Delete failed for quarantine entry {QuarantineEntryId}: {ErrorSummary}",
                    fileId, errorSummary);

                results.Add(new QuarantineDeleteResultItem
                {
                    FileId = fileId,
                    Result = ResultFailed,
                    Reason = "file-not-found"
                });
                failedCount++;
                continue;
            }

            // §12: audited before this file's result is recorded.
            await _auditLogService.WriteAsync(new AuditLogEntry
            {
                CreatedBy = ActingProcessIdentity,
                UpdatedBy = ActingProcessIdentity,
                UserId = actingUserId,
                TabModule = TabModule,
                Action = AuditAction.Deleted,
                FilePath = entry.Path,
                Result = AuditResult.Success
            }, ct);

            results.Add(new QuarantineDeleteResultItem { FileId = fileId, Result = ResultSuccess });
            succeededCount++;
        }

        _logger.LogInformation(
            "Permanent Delete batch by user {UserId}: {SucceededCount} succeeded, {FailedCount} failed",
            actingUserId, succeededCount, failedCount);

        // §6.4/§7 V3: whole-request 404 only if every requested file failed specifically because
        // it did not resolve to an Active quarantine_entry row (not merely "all failed").
        if (notFoundCount == request.FileIds.Count)
        {
            return new QuarantineDeleteResult { Outcome = QuarantineDeleteOutcome.AllFilesNotFound };
        }

        return new QuarantineDeleteResult
        {
            Outcome = QuarantineDeleteOutcome.Completed,
            Response = new QuarantineDeleteResponse { Results = results }
        };
    }

    /// <summary>
    /// Enumerates every version of the object named <paramref name="fileName"/> and deletes each
    /// one — reused verbatim (pattern only, no shared dependency) from
    /// <c>SyncPurgeTaskProcessor.DeleteAllVersionsAsync</c> (CAS-007 §4) since this is an Api-side
    /// action, not a Worker task, and this project's rule against introducing an Api→Worker
    /// dependency for one shared helper (Karpathy P4 — not yet a second *shared* call site across
    /// projects, only a second call site of the same *pattern*).
    ///
    /// CAS-018 §2 step 4: calls go through <see cref="IUserScopedB2DeleteClient"/> using
    /// <paramref name="authorization"/> (the acting user's own live authorization) rather than the
    /// system key. A failure here (e.g. the key is revoked mid-loop) flows into the caller's
    /// existing <c>DeleteFailedB2</c> path unchanged (CAS-018 §2/§11) — it is not caught here.
    /// </summary>
    private async Task DeleteAllVersionsAsync(
        string fileName, B2AuthorizationInfo authorization, string bucketId, CancellationToken ct)
    {
        string? startFileName = fileName;

        while (startFileName is not null)
        {
            var page = await _userScopedB2DeleteClient.ListFileVersionsAsync(
                authorization, bucketId, startFileName, fileName, 1000, ct);

            foreach (var version in page.Files)
            {
                if (!string.Equals(version.FileName, fileName, StringComparison.Ordinal))
                {
                    // Past the exact-name run — prefix match moved on to a different object.
                    return;
                }

                await _userScopedB2DeleteClient.DeleteFileVersionAsync(authorization, version.FileName, version.FileId, ct);
            }

            if (page.Files.Count == 0 || !string.Equals(page.NextFileName, fileName, StringComparison.Ordinal))
            {
                break;
            }

            startFileName = page.NextFileName;
        }
    }

    /// <summary>
    /// CAS-018 §2 steps 1-3: look up the acting user, decrypt their stored B2 key material (same
    /// decrypt path <c>LoginService.LoginAsync</c> uses — CAS-018 §2 step 1), and call
    /// <see cref="IUserB2KeyAuthorizer.AuthorizeAsync"/> fresh (never reused from login). Returns
    /// <c>null</c> — meaning "deny" — if the user row cannot be found (fail closed, same posture
    /// as this codebase's other auth checks), decryption fails, the live B2 call fails, or the
    /// resulting capability set does not include <c>deleteFiles</c>. Never logs the decrypted key
    /// or the returned authorization token (CAS-018 §11).
    /// </summary>
    private async Task<B2AuthorizationInfo?> AuthorizeActingUserForDeleteAsync(int actingUserId, CancellationToken ct)
    {
        var user = await _userRepository.GetByIdAsync(actingUserId, ct);
        if (user is null)
        {
            _logger.LogWarning("Permanent Delete: no dbo.users row found for acting user {UserId}", actingUserId);
            return null;
        }

        string b2KeyId;
        string b2ApplicationKey;
        try
        {
            b2KeyId = _credentialProtector.Unprotect(user.B2ApplicationKeyId);
            b2ApplicationKey = _credentialProtector.Unprotect(user.B2ApplicationKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Permanent Delete: could not decrypt stored B2 key material for user {UserId}", actingUserId);
            return null;
        }

        B2AuthorizationInfo liveAuthorization;
        try
        {
            liveAuthorization = await _userB2KeyAuthorizer.AuthorizeAsync(b2KeyId, b2ApplicationKey, ct);
        }
        catch (B2ApiException ex)
        {
            _logger.LogWarning(ex, "Permanent Delete: live B2 authorization failed for user {UserId}", actingUserId);
            return null;
        }

        if (!liveAuthorization.Capabilities.Contains(DeleteFilesCapability))
        {
            _logger.LogWarning(
                "Permanent Delete: live B2 authorization for user {UserId} lacks '{Capability}'",
                actingUserId, DeleteFilesCapability);
            return null;
        }

        return liveAuthorization;
    }
}
