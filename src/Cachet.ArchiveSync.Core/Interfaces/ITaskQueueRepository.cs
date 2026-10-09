using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Wraps <c>dbo.task_queue_ins</c>/<c>_claim</c>/<c>_upd</c> (CAS-004 Appendix B, first authored
/// by CAS-006 §5) plus a read-by-id lookup. Generic across every <c>task_type</c>, not just
/// <c>KeywordSearch</c> — this is the general claim-loop skeleton CAS-006 §3 asks for.
/// </summary>
public interface ITaskQueueRepository
{
    /// <summary>Inserts a new <c>Queued</c> row (CAS-006 §6 — <c>POST /search</c>'s submission step).</summary>
    Task<int> InsertAsync(
        string createdBy,
        string updatedBy,
        string taskType,
        int submittedByUserId,
        string? requestParameters,
        CancellationToken ct);

    /// <summary>
    /// Claims the oldest <c>Queued</c> row of any <c>task_type</c> (<c>READPAST</c>/<c>UPDLOCK</c>,
    /// CAS-004 Appendix B step 2) and transitions it to <c>Running</c>. Returns null if no row is
    /// currently queued.
    /// </summary>
    Task<ClaimedTaskRow?> ClaimNextAsync(string updatedBy, CancellationToken ct);

    /// <summary>Status/<c>result_detail</c> transition (CAS-004 Appendix B steps 3a/3b).</summary>
    Task UpdateStatusAsync(
        int taskQueueId, string updatedBy, string status, string? resultDetail, CancellationToken ct);

    /// <summary>
    /// CAS-029 §3.4/§5 — calls <c>dbo.task_queue_progress_upd</c>: a guarded (<c>Running</c>-only),
    /// monotonic progress write. Returns <c>true</c> when the row was updated, <c>false</c> when
    /// <c>rows_affected = 0</c> (the task is no longer <c>Running</c> or does not exist). Only the
    /// task id and two integer counts are sent — never a term, path, or file name (§3.4).
    /// </summary>
    Task<bool> UpdateProgressAsync(
        int taskQueueId, string updatedBy, int itemsProcessed, int itemsTotal, CancellationToken ct);

    /// <summary>
    /// Reads one row by id. See <see cref="TaskQueueRow"/>'s XML doc — implemented against a
    /// stored procedure (<c>dbo.task_queue_get</c>) that has not yet been authored by db-author
    /// (BLOCKED, flagged in the implementer's report).
    /// </summary>
    Task<TaskQueueRow?> GetAsync(int taskQueueId, CancellationToken ct);

    /// <summary>
    /// CAS-016 §7 — calls <c>dbo.task_queue_restart_ins</c>. Inserts a new <c>Queued</c> row
    /// lineage-linked to the original <c>Failed</c> row (Model B, §2); the original row is never
    /// mutated. Ownership (<paramref name="requestingUserId"/>) and <c>Failed</c>-state checks are
    /// enforced inside the stored procedure's own transaction, not merely by this call (§11) —
    /// <see cref="RestartTaskOutcome.NotOwner"/> is checked ahead of
    /// <see cref="RestartTaskOutcome.NotRestartable"/>.
    /// </summary>
    Task<RestartTaskResult> RestartAsync(
        int taskQueueId,
        int requestingUserId,
        string createdBy,
        string updatedBy,
        CancellationToken ct,
        int? resumeMaxAgeHours = null);

    /// <summary>
    /// CAS-054 §7 — calls <c>dbo.task_queue_resume_watermark_upd</c>: a guarded (<c>Running</c>
    /// <c>KeywordSearch</c> only), monotonic write of the resume watermark. Returns <c>true</c> when
    /// the row was updated, <c>false</c> when <c>rows_affected = 0</c> (not Running, or not newer —
    /// not an error). Only the task id and one integer are sent.
    /// </summary>
    Task<bool> UpdateResumeWatermarkAsync(
        int taskQueueId, string updatedBy, int resumeWatermarkFileIndexId, CancellationToken ct);

    /// <summary>
    /// CAS-028 §3.4 — calls <c>dbo.task_queue_sweep_interrupted</c>. In one transaction, flips every
    /// <c>Running</c> row to <c>Failed</c> (<c>result_detail = {"FailureReason": failureReason}</c>)
    /// and writes one <c>TaskInterruptedByRestart</c> audit row per swept task. Returns the swept
    /// rows (empty when nothing was <c>Running</c>). CAS-055 — when both
    /// <paramref name="resumeMaxAgeHours"/> and <paramref name="autoResumeMaxAttempts"/> are supplied,
    /// eligible Keyword Search rows are also auto-resumed inside the same transaction; <c>null</c>
    /// (default) = no auto-resume, today's behavior.
    /// </summary>
    Task<IReadOnlyList<SweptTaskRow>> SweepInterruptedAsync(
        string updatedBy, string failureReason, CancellationToken ct,
        int? resumeMaxAgeHours = null, int? autoResumeMaxAttempts = null);

    /// <summary>
    /// CAS-030 §5 — calls <c>dbo.task_queue_lst</c>: the caller's own rows, newest first
    /// (<c>submitted_at DESC, task_queue_id DESC</c>), optionally filtered by
    /// <paramref name="taskType"/>/<paramref name="status"/>, plus the filtered pre-paging count.
    /// Always scoped by <paramref name="submittedByUserId"/>.
    /// </summary>
    Task<(IReadOnlyList<TaskQueueListRow> Items, int TotalCount)> ListAsync(
        int submittedByUserId, string? taskType, string? status, int page, int pageSize, CancellationToken ct);

    /// <summary>CAS-030 §5 — calls <c>dbo.task_queue_summary_get</c> (own-scoped badge counts).</summary>
    Task<TaskSummaryResponse> GetSummaryAsync(int submittedByUserId, CancellationToken ct);

    /// <summary>
    /// CAS-030 §5 — calls <c>dbo.task_queue_acknowledge_upd</c> and maps its <c>@outcome</c>
    /// (<c>0|1|2</c>) to <see cref="AcknowledgeTaskOutcome"/>. Ownership and finished-state checks
    /// are enforced inside the procedure.
    /// </summary>
    Task<AcknowledgeTaskOutcome> AcknowledgeAsync(
        int taskQueueId, int submittedByUserId, string updatedBy, CancellationToken ct);

    /// <summary>
    /// CAS-037 §5/§7 V1 — calls <c>dbo.task_queue_active_by_type_get</c>. A global (not scoped to
    /// any one user) concurrency guard: <c>true</c> when any row of <paramref name="taskType"/> is
    /// currently <c>Queued</c> or <c>Running</c>. Deliberately not a reuse of <see cref="ListAsync"/>
    /// (scoped to <c>submitted_by_user_id</c>) — this check must be global.
    /// </summary>
    Task<bool> HasActiveTaskOfTypeAsync(string taskType, CancellationToken ct);

    /// <summary>
    /// CAS-056 §7 — calls <c>dbo.task_queue_cancel_req</c> and maps its <c>outcome</c> row
    /// (<c>0|1|2|3</c>). Ownership, task-type and state checks, and the audit row, all happen inside
    /// the procedure's own transaction; not-found and not-owned are the same outcome.
    /// </summary>
    Task<CancelTaskResult> CancelAsync(
        int taskQueueId, int requestingUserId, string updatedBy, CancellationToken ct);

    /// <summary>
    /// CAS-056 §7 — calls <c>dbo.task_queue_cancel_requested_get</c>: the Worker's cheap PK-lookup
    /// poll of <c>is_cancel_requested</c>. <c>false</c> when the row does not exist.
    /// </summary>
    Task<bool> IsCancelRequestedAsync(int taskQueueId, CancellationToken ct);
    /// <summary>
    /// CAS-026 §5/§6.3 — calls <c>dbo.task_queue_summary_get</c>. The two Task Manager header badge
    /// counts (FR-22) for <paramref name="submittedByUserId"/>.
    /// </summary>
    //Task<TaskQueueSummaryRow> GetSummaryAsync(int submittedByUserId, CancellationToken ct);
}