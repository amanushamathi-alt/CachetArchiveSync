using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="ITaskBadgeService"/> implementation of CAS-026 §6.3/§6.4 — <c>GET /tasks/summary</c>
/// and <c>POST /tasks/mark-viewed</c> (FR-22). Neither call is audited (§12).
/// </summary>
public sealed class TaskBadgeService : ITaskBadgeService
{
    // CAS-005 §15: process identity for dbo.users.updated_by — user attribution is the row being
    // updated itself (@user_id), matching TaskManagerService's existing "api-svc" convention.
    private const string ProcessIdentity = "api-svc";

    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<TaskBadgeService> _logger;

    public TaskBadgeService(
        ITaskQueueRepository taskQueueRepository,
        IUserRepository userRepository,
        ILogger<TaskBadgeService> logger)
    {
        _taskQueueRepository = taskQueueRepository;
        _userRepository = userRepository;
        _logger = logger;
    }

    // CAS-030 §5 superseded CAS-026's summary shape — the repository already returns the response DTO.
    public Task<TaskSummaryResponse> GetSummaryAsync(int actingUserId, CancellationToken ct) =>
        _taskQueueRepository.GetSummaryAsync(actingUserId, ct);

    public async Task<MarkViewedResponse> MarkViewedAsync(int actingUserId, CancellationToken ct)
    {
        // dbo.users_mark_task_manager_viewed sets the column via SYSUTCDATETIME() but returns no
        // output value; capturing "now" here (rather than re-reading the row) is an acceptable
        // approximation for this per-user UI-state bookkeeping field (§12 — not audited, no
        // correctness dependency: the badge's own count is computed server-side, elsewhere, from
        // the persisted column, not from this echoed value).
        var markedViewedAt = DateTimeOffset.UtcNow;

        await _userRepository.MarkTaskManagerViewedAsync(actingUserId, ProcessIdentity, ct);

        _logger.LogInformation(
            "Task manager marked viewed by user {UserId} at {MarkedViewedAt}", actingUserId, markedViewedAt);

        return new MarkViewedResponse { MarkedViewedAt = markedViewedAt };
    }
}
