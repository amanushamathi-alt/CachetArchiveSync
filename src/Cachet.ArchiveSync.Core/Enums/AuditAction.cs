namespace Cachet.ArchiveSync.Core.Enums;

/// <summary>
/// Audit-trail action catalog (FR-23). Member names are PascalCase, no punctuation; the value
/// written to <c>dbo.audit_log.action</c> is the literal result of <see cref="Enum.ToString()"/>
/// (CAS-005 §15, resolved 2026-09-22 — no [Description]/explicit string mapping).
/// </summary>
public enum AuditAction
{
    Login,
    LoginFailed,
    Logout,
    CrossCheckExcluded,
    PermanentDeleteRequested,
    Deleted,
    DeleteFailedLocal,
    DeleteFailedB2,

    // CAS-006 §3 — appended, existing members not reordered (Enum.ToString() is the literal
    // dbo.audit_log.action value, CAS-005 §15).
    KeywordSearchSubmitted,
    QuarantineAdd,

    // CAS-007 §3 — appended, existing members not reordered (same rule as above).
    ContentScanSubmitted,
    SyncCopySubmitted,
    SyncCopyToB2,
    SyncUpdateSubmitted,
    SyncUpdateInB2,
    SyncPurgeSubmitted,
    SyncPurgeFromB2,
    SyncActionFailedLocal,
    SyncActionFailedB2,

    // CAS-010 §3 — appended, existing members not reordered (same rule as above).
    PreActionReportGenerated,
    PostActionReportGenerated,

    // CAS-014 §7/§11 — appended, existing members not reordered (same rule as above).
    QuarantineRemoved,

    // CAS-016 §11 — appended, existing members not reordered (same rule as above).
    TaskRestart,

    // CAS-018 §2/§11 — appended, existing members not reordered (same rule as above).
    DeleteAuthorizationDenied,

    // CAS-020 §10 SC-6/§12 — appended, existing members not reordered (same rule as above).
    // Admin-provisioning create/edit/deactivate/reactivate of a dbo.users row.
    UserCreated,
    UserUpdated,
    UserDeactivated,
    UserReactivated,

    // CAS-028 §12 — appended, existing members not reordered (same rule as above).
    // Worker-originated terminal transitions: dispatch-boundary failure (T4b) and startup sweep
    // (T5 — must equal dbo.task_queue_sweep_interrupted's N'TaskInterruptedByRestart' literal).
    TaskFailed,
    TaskInterruptedByRestart,

    // CAS-035 §2 — appended, existing members not reordered (same rule as above).
    CrossCheckIndeterminate,

    // CAS-037 §12 — appended, existing members not reordered (same rule as above).
    B2MirrorSyncSubmitted,
    B2MirrorSyncCompleted,

    // CAS-040 §3/§12 — appended, existing members not reordered (same rule as above).
    // In-browser File Viewer: one row per /files/{id}/view or /download (Success or Failure).
    FileViewed,
    FileDownloaded,

    // CAS-055 §2/§11 — appended, existing members not reordered (same rule as above).
    // Startup-sweep auto-resume of an interrupted Keyword Search, and its attempt-cap suppression.
    TaskAutoResumed,
    TaskAutoResumeSuppressed,

    // CAS-056 §2.7 — appended, existing members not reordered (same rule as above).
    // Keyword Search cancel: flag set on a Running task, and the terminal Cancelled transition
    // (must equal the literals in dbo.task_queue_cancel_req / dbo.task_queue_sweep_interrupted).
    TaskCancelRequested,
    TaskCancelled
}
