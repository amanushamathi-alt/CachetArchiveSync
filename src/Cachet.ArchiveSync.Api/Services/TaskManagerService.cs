using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="ITaskManagerService"/> implementation of CAS-016 §8 — thin wrapper over
/// <see cref="ITaskQueueRepository.RestartAsync"/> (<c>dbo.task_queue_restart_ins</c>, §7) plus
/// the FR-23 audit-log write. Ownership/<c>Failed</c>-state enforcement happens inside the stored
/// procedure's own transaction (§7/§11) — this service only maps the repository's outcome and
/// audits before returning success; it never re-checks ownership/state itself.
/// CAS-030 §6 adds the own-scoped read surface (list/get/summary) and acknowledge. None of them is
/// audited (CAS-030 §12): the reads are not FR-23 actions, and acknowledge only sets a
/// notification flag (§15 Q2).
/// </summary>
public sealed class TaskManagerService : ITaskManagerService
{
    private const string TabModule = "Task Manager";

    // CAS-005 §15: acting process identity for audit_log.created_by/updated_by — user
    // attribution is carried separately via audit_log.user_id, matching QuarantineService's
    // existing "api-svc" convention for this same distinction.
    private const string ProcessIdentity = "api-svc";

    private const string FailedStatus = "Failed";
    private const string KeywordSearchTaskType = "KeywordSearch";

    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<TaskManagerService> _logger;
    private readonly int _resumeMaxAgeHours;

    // CAS-054 §2.6/§9 V7: default 24, valid 1-720.
    private const int DefaultResumeMaxAgeHours = 24;

    public TaskManagerService(
        ITaskQueueRepository taskQueueRepository,
        IAuditLogService auditLogService,
        ILogger<TaskManagerService> logger,
        IConfiguration? configuration = null)
    {
        _taskQueueRepository = taskQueueRepository;
        _auditLogService = auditLogService;
        _logger = logger;

        _resumeMaxAgeHours = configuration?.GetValue<int?>("Worker:ResumeMaxAgeHours") ?? DefaultResumeMaxAgeHours;
        if (_resumeMaxAgeHours is < 1 or > 720)
        {
            throw new InvalidOperationException(
                "Configuration key 'Worker:ResumeMaxAgeHours' must be between 1 and 720.");
        }
    }

    public async Task<RestartTaskResult> RestartAsync(int taskId, int requestingUserId, CancellationToken ct)
    {
        var result = await _taskQueueRepository.RestartAsync(
            taskId, requestingUserId, ProcessIdentity, ProcessIdentity, ct, _resumeMaxAgeHours);

        if (result.Outcome != RestartTaskOutcome.Restarted)
        {
            return result;
        }

        // §11/FR-23: audited before the 202 response. task_queue_id references the NEW row, not
        // the original (§9 V3, §10 SC-4).
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ProcessIdentity,
            UpdatedBy = ProcessIdentity,
            UserId = requestingUserId,
            TabModule = TabModule,
            Action = AuditAction.TaskRestart,
            TaskQueueId = result.NewTaskQueueId,
            Result = AuditResult.Success,
            ResultDetail = BuildRestartResultDetail(taskId, result)
        }, ct);

        _logger.LogInformation(
            "Task restart: task {OriginalTaskId} restarted as {NewTaskId} by user {UserId}",
            taskId, result.NewTaskQueueId, requestingUserId);

        return result;
    }

    /// <summary>
    /// CAS-054 §2.7/§11 - integers and task ids only; never a file name, path or term.
    /// </summary>
    private string BuildRestartResultDetail(int originalTaskId, RestartTaskResult result)
    {
        if (result.ResumedFromFileIndexId is not null)
        {
            return $"Resumed from file_index_id {result.ResumedFromFileIndexId}; "
                + $"{result.CarriedOverCount} match result(s) carried over from task {originalTaskId}.";
        }

        return result.ResumeNote == "CheckpointTooOld"
            ? $"Full restart (checkpoint older than {_resumeMaxAgeHours} h)"
            : "Full restart (no checkpoint)";
    }

    /// <summary>CAS-030 §6 <c>GET /tasks</c> — own-scoped in <c>dbo.task_queue_lst</c> (§12).</summary>
    public async Task<PagedResult<TaskListItem>> ListAsync(
        int requestingUserId, string? taskType, string? status, int page, int pageSize, CancellationToken ct)
    {
        var (rows, totalCount) = await _taskQueueRepository.ListAsync(
            requestingUserId, taskType, status, page, pageSize, ct);

        var items = rows
            .Select(row => new TaskListItem
            {
                TaskId = row.TaskQueueId,
                Type = row.TaskType,
                SubmittedBy = row.Username,
                SubmittedAt = row.SubmittedAt,
                StartTime = row.StartedAt,
                CompletedAt = row.CompletedAt,
                Status = row.Status,
                IsAcknowledged = row.ResultAcknowledgedAt is not null
            })
            .ToList();

        _logger.LogInformation(
            "Task list read: user {UserId}, type {TaskType}, page {Page}, count {Count}",
            requestingUserId, taskType, page, items.Count);

        return new PagedResult<TaskListItem>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    /// <summary>
    /// CAS-030 §6 <c>GET /tasks/{taskId}</c>. Ownership is enforced here against
    /// <c>task_queue_get</c>'s <c>submitted_by_user_id</c> (§12); a missing and a not-owned task
    /// both return <c>null</c> (V3). <c>request_parameters</c> is never passed through — only the
    /// fixed <c>term</c>/<c>extension</c> projection, for <c>KeywordSearch</c> only (SC-4).
    /// </summary>
    public async Task<TaskDetailResponse?> GetAsync(
        int taskId, int requestingUserId, string requestingUsername, CancellationToken ct)
    {
        var row = await _taskQueueRepository.GetAsync(taskId, ct);
        if (row is null || row.SubmittedByUserId != requestingUserId)
        {
            return null;
        }

        return new TaskDetailResponse
        {
            TaskId = row.TaskQueueId,
            Type = row.TaskType,
            SubmittedBy = requestingUsername,
            SubmittedAt = row.SubmittedAt,
            StartTime = row.StartedAt,
            CompletedAt = row.CompletedAt,
            Status = row.Status,
            IsAcknowledged = row.ResultAcknowledgedAt is not null,
            ItemsProcessed = row.ItemsProcessed,
            ItemsTotal = row.ItemsTotal,
            ResultDetail = row.Status == FailedStatus ? row.ResultDetail : null,
            SearchParameters = row.TaskType == KeywordSearchTaskType
                ? TryProjectSearchParameters(row.RequestParameters)
                : null
        };
    }

    /// <summary>CAS-030 §6 <c>GET /tasks/summary</c>. Logs nothing on success (§11 — no per-tick noise).</summary>
    public Task<TaskSummaryResponse> GetSummaryAsync(int requestingUserId, CancellationToken ct) =>
        _taskQueueRepository.GetSummaryAsync(requestingUserId, ct);

    /// <summary>
    /// CAS-030 §6 <c>POST /tasks/{taskId}/acknowledge</c>. Ownership and finished-state checks
    /// happen inside <c>dbo.task_queue_acknowledge_upd</c> (§5); not audited (§12, §15 Q2).
    /// </summary>
    public async Task<AcknowledgeTaskOutcome> AcknowledgeAsync(int taskId, int requestingUserId, CancellationToken ct)
    {
        var outcome = await _taskQueueRepository.AcknowledgeAsync(taskId, requestingUserId, ProcessIdentity, ct);

        if (outcome == AcknowledgeTaskOutcome.Acknowledged)
        {
            _logger.LogInformation("Task acknowledged: task {TaskId} by user {UserId}", taskId, requestingUserId);
        }
        else
        {
            _logger.LogWarning("Task acknowledge rejected: task {TaskId}, outcome {Outcome}", taskId, outcome);
        }

        return outcome;
    }

    /// <summary>
    /// CAS-056 §2.1 <c>POST /tasks/{taskId}/cancel</c>. Ownership, state and the audit row are all
    /// inside <c>dbo.task_queue_cancel_req</c>'s transaction (FR-23), so there is no audit write
    /// here. Logs the task id and outcome only — never a search term.
    /// </summary>
    public async Task<CancelTaskResult> CancelAsync(int taskId, int requestingUserId, CancellationToken ct)
    {
        var result = await _taskQueueRepository.CancelAsync(taskId, requestingUserId, ProcessIdentity, ct);

        _logger.LogInformation(
            "Task cancel: task {TaskId}, outcome {Outcome}, user {UserId}",
            taskId, result.Outcome, requestingUserId);

        return result;
    }

    /// <summary>
    /// Fixed projection (CAS-030 §6/§12), widened by CAS-038 §2.7 with the optional date range
    /// (null for keyword-only and pre-CAS-038 payloads without the keys). Returns <c>null</c> when
    /// the stored payload is missing or unreadable; the raw string is never logged (§11 — a term can
    /// be sensitive).
    /// </summary>
    private static TaskSearchParameters? TryProjectSearchParameters(string? requestParameters)
    {
        if (string.IsNullOrWhiteSpace(requestParameters))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<KeywordSearchRequestDto>(requestParameters);
            return payload is null
                ? null
                : new TaskSearchParameters
                {
                    Term = payload.Term,
                    Extension = payload.Extension,
                    FromDate = payload.FromDate,
                    ToDate = payload.ToDate,
                    Mode = payload.Mode
                };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
