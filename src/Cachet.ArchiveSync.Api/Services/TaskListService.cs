using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="ITaskListService"/> implementation of CAS-026 §6.1 — <c>GET /tasks</c> (FR-19). A
/// plain read; not audited (§12). Validates before calling the repository, mirroring
/// <see cref="AuditLogListService"/>'s <c>Validate()</c> pattern.
/// </summary>
public sealed class TaskListService : ITaskListService
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "Queued", "Running", "Completed", "Failed", "Cancelled"
    };

    private const int MinPageSize = 1;
    private const int MaxPageSize = 200;

    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly ILogger<TaskListService> _logger;

    public TaskListService(ITaskQueueRepository taskQueueRepository, ILogger<TaskListService> logger)
    {
        _taskQueueRepository = taskQueueRepository;
        _logger = logger;
    }

    public async Task<TaskListResult> ListAsync(
        string? status,
        int page,
        int pageSize,
        int actingUserId,
        CancellationToken ct)
    {
        var validationError = Validate(status, page, pageSize);
        if (validationError is not null)
        {
            return new TaskListResult { Outcome = TaskListOutcome.ValidationFailed, ValidationError = validationError };
        }

        // CAS-026 code-review fix (2026-09-24): an empty-string status must be normalized to null
        // here, same as AuditLogListService's own tabFilter normalization — otherwise "" reaches
        // dbo.task_queue_lst's own @status IS NOT NULL AND @status NOT IN (...) guard, which
        // RAISERRORs for "" and surfaces as an unhandled 500 instead of a validation failure.
        var statusFilter = string.IsNullOrEmpty(status) ? null : status;

        // CAS-030 §5 added the taskType filter and acknowledgement fields; CAS-026 never filters by type.
        var (rows, totalCount) = await _taskQueueRepository.ListAsync(actingUserId, null, statusFilter, page, pageSize, ct);

        var items = rows.Select(row => new TaskListItem
        {
            TaskId = row.TaskQueueId,
            Type = row.TaskType,
            SubmittedBy = row.Username,
            SubmittedAt = row.SubmittedAt,
            StartTime = row.SubmittedAt,
            Status = row.Status,
            IsAcknowledged = row.ResultAcknowledgedAt is not null
        }).ToList();

        _logger.LogInformation(
            "Task list requested by user {UserId}: status={Status}, page={Page}, pageSize={PageSize}, returned={ReturnedCount}, totalCount={TotalCount}",
            actingUserId, status, page, pageSize, items.Count, totalCount);

        return new TaskListResult
        {
            Outcome = TaskListOutcome.Found,
            Response = new PagedResult<TaskListItem>
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount
            }
        };
    }

    private static string? Validate(string? status, int page, int pageSize)
    {
        if (!string.IsNullOrEmpty(status) && !AllowedStatuses.Contains(status))
        {
            return "'status' must be one of Queued, Running, Completed, Failed, or Cancelled.";
        }

        if (page < 1)
        {
            return "'page' must be >= 1.";
        }

        if (pageSize < MinPageSize || pageSize > MaxPageSize)
        {
            return $"'pageSize' must be between {MinPageSize} and {MaxPageSize}.";
        }

        return null;
    }
}
