using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of <see cref="ITaskDetailService.GetAsync"/> (CAS-026 §6.2/§7).</summary>
public enum TaskDetailOutcome
{
    Found,

    /// <summary><c>taskId</c> does not exist in <c>dbo.task_queue</c> — <c>404 Not Found</c>.</summary>
    NotFound,

    /// <summary><c>taskId</c> exists but does not belong to the requesting user — <c>403 Forbidden</c>
    /// (§7 V4 — never <c>404</c>, to stay consistent with CAS-016's "don't leak whether the row
    /// exists" posture for another user's resource, though here there is no status to hide, just
    /// ownership).</summary>
    NotOwner
}

/// <summary>Result of one <see cref="ITaskDetailService.GetAsync"/> call.</summary>
public sealed record TaskDetailResult
{
    public required TaskDetailOutcome Outcome { get; init; }

    public TaskDetailResponse? Response { get; init; }
}

/// <summary>
/// <c>GET /tasks/{taskId}</c> read path (CAS-026 §6.2, FR-19) — single-task detail including
/// <c>resultDetail</c> for a Failed row's View Details affordance (UI-UX §5.2). Reuses
/// <see cref="ITaskQueueRepository.GetAsync"/> (already wired to <c>dbo.task_queue_get</c>, §0)
/// plus <see cref="IUserRepository.GetByIdAsync"/> for the <c>submittedBy</c> username — no new
/// stored procedure needed for this one lookup (§0/§3). Not audited (§12 — a plain read).
/// </summary>
public interface ITaskDetailService
{
    Task<TaskDetailResult> GetAsync(int taskId, int actingUserId, CancellationToken ct);
}
