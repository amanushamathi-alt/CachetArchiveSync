using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IB2MirrorSyncService"/> implementation of CAS-037 §6 — <c>POST /sync/mirror-sync</c>
/// validate+submit. Never calls <see cref="IB2Client"/> directly (that happens only in the Worker's
/// <c>B2MirrorSyncTaskProcessor</c>) — this service's only job is the §7 V1 concurrency check, the
/// <c>task_queue</c> insert, and the submission audit write, same shape as
/// <see cref="SyncScanService.SubmitAsync"/>.
/// </summary>
public sealed class B2MirrorSyncService : IB2MirrorSyncService
{
    private const string ActingProcessIdentity = "api-svc";
    private const string TabModule = "O-Drive Sync";
    private const string TaskType = "B2MirrorSync";

    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<B2MirrorSyncService> _logger;

    public B2MirrorSyncService(
        ITaskQueueRepository taskQueueRepository,
        IAuditLogService auditLogService,
        ILogger<B2MirrorSyncService> logger)
    {
        _taskQueueRepository = taskQueueRepository;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<B2MirrorSyncSubmitResult> SubmitAsync(int submittedByUserId, CancellationToken ct)
    {
        // §7 V1: at most one B2MirrorSync task Queued/Running at a time, checked before insert.
        var isActive = await _taskQueueRepository.HasActiveTaskOfTypeAsync(TaskType, ct);
        if (isActive)
        {
            _logger.LogInformation(
                "B2MirrorSync submission by user {UserId} rejected: a B2MirrorSync task is already Queued/Running.",
                submittedByUserId);

            return new B2MirrorSyncSubmitResult { Outcome = B2MirrorSyncSubmitOutcome.AlreadyRunning };
        }

        var submittedAt = DateTimeOffset.UtcNow;

        // §6: no request body — the sync scope is always the whole configured B2 bucket.
        var taskId = await _taskQueueRepository.InsertAsync(
            ActingProcessIdentity, ActingProcessIdentity, TaskType, submittedByUserId, requestParameters: null, ct);

        // §12/CLAUDE.md: audited before the response.
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = submittedByUserId,
            TabModule = TabModule,
            Action = AuditAction.B2MirrorSyncSubmitted,
            TaskQueueId = taskId,
            Result = AuditResult.Success
        }, ct);

        _logger.LogInformation("B2MirrorSync task {TaskId} submitted by user {UserId}", taskId, submittedByUserId);

        return new B2MirrorSyncSubmitResult
        {
            Outcome = B2MirrorSyncSubmitOutcome.Accepted,
            Response = new TaskSubmittedResponse
            {
                TaskId = taskId,
                Type = TaskType,
                Status = "Queued",
                SubmittedAt = submittedAt
            }
        };
    }
}
