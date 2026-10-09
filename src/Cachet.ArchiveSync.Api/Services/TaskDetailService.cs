using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="ITaskDetailService"/> implementation of CAS-026 §6.2 — <c>GET /tasks/{taskId}</c>
/// (FR-19). Reuses <see cref="ITaskQueueRepository.GetAsync"/> (already wired to
/// <c>dbo.task_queue_get</c>, §0) plus <see cref="IUserRepository.GetByIdAsync"/> for the
/// <c>submittedBy</c> username — no new stored procedure (§0/§3). Not audited (§12).
/// </summary>
public sealed class TaskDetailService : ITaskDetailService
{
    private const string FailedStatus = "Failed";

    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<TaskDetailService> _logger;

    public TaskDetailService(
        ITaskQueueRepository taskQueueRepository,
        IUserRepository userRepository,
        ILogger<TaskDetailService> logger)
    {
        _taskQueueRepository = taskQueueRepository;
        _userRepository = userRepository;
        _logger = logger;
    }

    public async Task<TaskDetailResult> GetAsync(int taskId, int actingUserId, CancellationToken ct)
    {
        var task = await _taskQueueRepository.GetAsync(taskId, ct);
        if (task is null)
        {
            return new TaskDetailResult { Outcome = TaskDetailOutcome.NotFound };
        }

        // §7 V4: NotOwner is checked before any further lookup, regardless of the row's own state.
        if (task.SubmittedByUserId != actingUserId)
        {
            return new TaskDetailResult { Outcome = TaskDetailOutcome.NotOwner };
        }

        var submitter = await _userRepository.GetByIdAsync(task.SubmittedByUserId, ct);

        _logger.LogInformation("Task detail requested by user {UserId}: taskId={TaskId}", actingUserId, taskId);

        return new TaskDetailResult
        {
            Outcome = TaskDetailOutcome.Found,
            Response = new TaskDetailResponse
            {
                TaskId = task.TaskQueueId,
                Type = task.TaskType,
                SubmittedBy = submitter?.Username ?? string.Empty,
                SubmittedAt = task.SubmittedAt,
                StartTime = task.SubmittedAt,
                Status = task.Status,
                IsAcknowledged = task.ResultAcknowledgedAt is not null,
                // §10 SC-3: resultDetail is populated only for a Failed row, null otherwise —
                // regardless of what dbo.task_queue.result_detail happens to hold for other statuses.
                ResultDetail = task.Status == FailedStatus ? task.ResultDetail : null
            }
        };
    }
}
