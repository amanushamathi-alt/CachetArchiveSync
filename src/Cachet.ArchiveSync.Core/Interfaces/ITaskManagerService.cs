using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// <c>POST /tasks/{taskId}/restart</c> (CAS-016 §8, FR-23). Writes the <c>TaskRestart</c> audit
/// row before returning on a successful restart (§11) — the caller does not need to audit this
/// call itself. Also hosts the CAS-030 §6 own-scoped Task Manager read surface and acknowledge;
/// none of those are audited (CAS-030 §12).
/// </summary>
public interface ITaskManagerService
{
    Task<RestartTaskResult> RestartAsync(int taskId, int requestingUserId, CancellationToken ct);

    /// <summary>
    /// CAS-030 §6 <c>GET /tasks</c> — the caller's own tasks, newest first. Arguments are
    /// validated by the caller (V1/V2) before this is invoked.
    /// </summary>
    Task<PagedResult<TaskListItem>> ListAsync(
        int requestingUserId, string? taskType, string? status, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// CAS-030 §6 <c>GET /tasks/{taskId}</c>. Returns <c>null</c> both when the task does not exist
    /// and when it is not owned by <paramref name="requestingUserId"/> (V3 — no enumeration).
    /// <paramref name="requestingUsername"/> fills <c>submittedBy</c>: a returned task is always the
    /// caller's own, so no user-row read is needed.
    /// </summary>
    Task<TaskDetailResponse?> GetAsync(
        int taskId, int requestingUserId, string requestingUsername, CancellationToken ct);

    /// <summary>CAS-030 §6 <c>GET /tasks/summary</c> — own-scoped badge counts (FR-22).</summary>
    Task<TaskSummaryResponse> GetSummaryAsync(int requestingUserId, CancellationToken ct);

    /// <summary>CAS-030 §6 <c>POST /tasks/{taskId}/acknowledge</c> — not audited (§12, §15 Q2).</summary>
    Task<AcknowledgeTaskOutcome> AcknowledgeAsync(int taskId, int requestingUserId, CancellationToken ct);

    /// <summary>
    /// CAS-056 §2.1 <c>POST /tasks/{taskId}/cancel</c>. Ownership/state checks and the audit row are
    /// inside <c>dbo.task_queue_cancel_req</c>'s transaction — this call adds no audit write.
    /// </summary>
    Task<CancelTaskResult> CancelAsync(int taskId, int requestingUserId, CancellationToken ct);
}
