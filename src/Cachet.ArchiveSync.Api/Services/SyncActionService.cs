using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="ISyncActionService"/> implementation of CAS-007 §6 — <c>POST /sync/actions/copy</c>,
/// <c>POST /sync/actions/update</c>, <c>POST /sync/actions/purge</c> validate+submit. Each
/// requested <c>fileId</c> is resolved to its task-scoped <c>sync_result</c> row and validated
/// against the endpoint's expected <c>comparison_type</c> (§7 V4/V5/V6) before any
/// <c>dbo.task_queue</c> row is inserted — a single unresolved <c>fileId</c> fails the whole
/// request.
/// </summary>
public sealed class SyncActionService : ISyncActionService
{
    private const string ActingProcessIdentity = "api-svc";
    private const string TabModule = "O-Drive Sync";
    private const string SourceTaskType = "ContentScan";

    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly ISyncResultRepository _syncResultRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SyncActionService> _logger;

    public SyncActionService(
        ITaskQueueRepository taskQueueRepository,
        ISyncResultRepository syncResultRepository,
        IAuditLogService auditLogService,
        IConfiguration configuration,
        ILogger<SyncActionService> logger)
    {
        _taskQueueRepository = taskQueueRepository;
        _syncResultRepository = syncResultRepository;
        _auditLogService = auditLogService;
        _configuration = configuration;
        _logger = logger;
    }

    public Task<SyncActionSubmitResult> SubmitCopyAsync(SyncActionRequest request, int actingUserId, CancellationToken ct) =>
        SubmitAsync("SyncCopy", "New", AuditAction.SyncCopySubmitted, request.TaskId, request.FileIds, actingUserId, ct);

    public Task<SyncActionSubmitResult> SubmitUpdateAsync(SyncActionRequest request, int actingUserId, CancellationToken ct) =>
        SubmitAsync("SyncUpdate", "Modified", AuditAction.SyncUpdateSubmitted, request.TaskId, request.FileIds, actingUserId, ct);

    public async Task<SyncActionSubmitResult> SubmitPurgeAsync(SyncPurgeRequest request, int actingUserId, CancellationToken ct)
    {
        // §7 V7: confirm must be true, checked before any other validation — the whole request is
        // rejected before any task is queued.
        if (!request.Confirm)
        {
            return new SyncActionSubmitResult { Outcome = SyncActionSubmitOutcome.ConfirmationRequired };
        }

        return await SubmitAsync(
            "SyncPurge", "Removed", AuditAction.SyncPurgeSubmitted, request.TaskId, request.FileIds, actingUserId, ct);
    }

    private async Task<SyncActionSubmitResult> SubmitAsync(
        string taskType,
        string expectedComparisonType,
        AuditAction submittedAction,
        int sourceTaskId,
        IReadOnlyList<int> fileIds,
        int actingUserId,
        CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return new SyncActionSubmitResult { Outcome = SyncActionSubmitOutcome.ValidationFailed };
        }

        // §9/SC-15: batch cap checked before any task is queued.
        var maxBatchSize = _configuration.GetValue<int?>("Sync:MaxBatchSize") ?? 100;
        if (fileIds.Count > maxBatchSize)
        {
            return new SyncActionSubmitResult { Outcome = SyncActionSubmitOutcome.ValidationFailed };
        }

        var sourceTask = await _taskQueueRepository.GetAsync(sourceTaskId, ct);
        if (sourceTask is null || sourceTask.TaskType != SourceTaskType)
        {
            return new SyncActionSubmitResult { Outcome = SyncActionSubmitOutcome.TaskNotFound };
        }

        // §7 V4/V5/V6: every fileId must resolve to a Pending sync_result row of the expected
        // comparison_type, scoped to sourceTaskId — checked up front, a single unresolved fileId
        // fails the whole request.
        foreach (var fileId in fileIds)
        {
            var row = await _syncResultRepository.GetAsync(fileId, ct);
            if (row is null
                || row.TaskQueueId != sourceTaskId
                || row.SyncStatus != "Pending"
                || row.ComparisonType != expectedComparisonType)
            {
                return new SyncActionSubmitResult { Outcome = SyncActionSubmitOutcome.FileNotFound };
            }
        }

        var submittedAt = DateTimeOffset.UtcNow;
        var payload = new SyncActionRequestParametersDto { SourceTaskId = sourceTaskId, FileIds = fileIds };
        var requestParameters = JsonSerializer.Serialize(payload);

        var taskId = await _taskQueueRepository.InsertAsync(
            ActingProcessIdentity, ActingProcessIdentity, taskType, actingUserId, requestParameters, ct);

        // §12: audited before the 202 response.
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = actingUserId,
            TabModule = TabModule,
            Action = submittedAction,
            TaskQueueId = taskId,
            Result = AuditResult.Success
        }, ct);

        _logger.LogInformation(
            "{ActionType} task {TaskId} submitted by user {UserId} for {FileCount} files",
            taskType, taskId, actingUserId, fileIds.Count);

        return new SyncActionSubmitResult
        {
            Outcome = SyncActionSubmitOutcome.Accepted,
            Response = new TaskSubmittedResponse
            {
                TaskId = taskId,
                Type = taskType,
                Status = "Queued",
                SubmittedAt = submittedAt
            }
        };
    }
}
