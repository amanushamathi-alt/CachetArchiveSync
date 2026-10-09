using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="ISyncScanService"/> implementation of CAS-007 §6/§6.2 — <c>POST /sync/scan</c>
/// validate+submit and <c>GET /sync/{taskId}/results</c> read.
/// </summary>
public sealed class SyncScanService : ISyncScanService
{
    private const string ActingProcessIdentity = "api-svc";
    private const string TabModule = "O-Drive Sync";
    private const string TaskType = "ContentScan";

    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly ISyncResultRepository _syncResultRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<SyncScanService> _logger;

    public SyncScanService(
        ITaskQueueRepository taskQueueRepository,
        ISyncResultRepository syncResultRepository,
        IAuditLogService auditLogService,
        ILogger<SyncScanService> logger)
    {
        _taskQueueRepository = taskQueueRepository;
        _syncResultRepository = syncResultRepository;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<SyncScanSubmitResult> SubmitAsync(int submittedByUserId, CancellationToken ct)
    {
        var submittedAt = DateTimeOffset.UtcNow;

        // §3: no request body — the comparison scope is always the full mirror pair.
        var taskId = await _taskQueueRepository.InsertAsync(
            ActingProcessIdentity, ActingProcessIdentity, TaskType, submittedByUserId, requestParameters: null, ct);

        // §12/CLAUDE.md: audited before the response, even though the API design doc's §5.1 itself
        // carries no explicit [audited] tag (§12's reconciliation note, same as CAS-006 §12).
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = submittedByUserId,
            TabModule = TabModule,
            Action = AuditAction.ContentScanSubmitted,
            TaskQueueId = taskId,
            Result = AuditResult.Success
        }, ct);

        _logger.LogInformation("ContentScan task {TaskId} submitted by user {UserId}", taskId, submittedByUserId);

        return new SyncScanSubmitResult
        {
            Outcome = SyncScanSubmitOutcome.Accepted,
            Response = new TaskSubmittedResponse
            {
                TaskId = taskId,
                Type = TaskType,
                Status = "Queued",
                SubmittedAt = submittedAt
            }
        };
    }

    public async Task<SyncResultsQueryResult> GetResultsAsync(
        int taskId,
        int newOnODrivePage,
        int newOnODrivePageSize,
        int removedFromODrivePage,
        int removedFromODrivePageSize,
        int modifiedOnODrivePage,
        int modifiedOnODrivePageSize,
        bool excludeSynced,
        CancellationToken ct)
    {
        var task = await _taskQueueRepository.GetAsync(taskId, ct);

        if (task is null || task.TaskType != TaskType)
        {
            return new SyncResultsQueryResult { Outcome = SyncResultsOutcome.TaskNotFound };
        }

        if (task.Status is "Queued" or "Running")
        {
            return new SyncResultsQueryResult { Outcome = SyncResultsOutcome.TaskNotComplete };
        }

        if (task.Status == "Completed")
        {
            var (newItems, newTotal) = await _syncResultRepository.ListAsync(
                taskId, "New", newOnODrivePage, newOnODrivePageSize, excludeSynced, ct);
            var (removedItems, removedTotal) = await _syncResultRepository.ListAsync(
                taskId, "Removed", removedFromODrivePage, removedFromODrivePageSize, excludeSynced, ct);
            var (modifiedItems, modifiedTotal) = await _syncResultRepository.ListAsync(
                taskId, "Modified", modifiedOnODrivePage, modifiedOnODrivePageSize, excludeSynced, ct);

            var response = new SyncScanResultsResponse
            {
                TaskId = taskId,
                Status = "Completed",
                NewOnODrive = new PagedResult<NewOnODriveItem>
                {
                    Items = newItems.Select(MapNewItem).ToList(),
                    Page = newOnODrivePage,
                    PageSize = newOnODrivePageSize,
                    TotalCount = newTotal
                },
                RemovedFromODrive = new PagedResult<RemovedFromODriveItem>
                {
                    Items = removedItems.Select(MapRemovedItem).ToList(),
                    Page = removedFromODrivePage,
                    PageSize = removedFromODrivePageSize,
                    TotalCount = removedTotal
                },
                ModifiedOnODrive = new PagedResult<ModifiedOnODriveItem>
                {
                    Items = modifiedItems.Select(MapModifiedItem).ToList(),
                    Page = modifiedOnODrivePage,
                    PageSize = modifiedOnODrivePageSize,
                    TotalCount = modifiedTotal
                }
            };

            return new SyncResultsQueryResult { Outcome = SyncResultsOutcome.Found, Response = response };
        }

        // Failed (or, defensively, Cancelled — out of scope generic Task Manager cancel, §4).
        var failureReason = TryGetFailureReason(task.ResultDetail);
        return new SyncResultsQueryResult
        {
            Outcome = SyncResultsOutcome.Found,
            Response = new SyncScanResultsResponse
            {
                TaskId = taskId,
                Status = "Failed",
                FailureReason = failureReason
            }
        };
    }

    private static string? TryGetFailureReason(string? resultDetail)
    {
        if (string.IsNullOrWhiteSpace(resultDetail))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ContentScanResultSummaryDto>(resultDetail)?.FailureReason;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NewOnODriveItem MapNewItem(SyncResultRow row) => new()
    {
        FileId = row.SyncResultId,
        // CAS-040 §6.5
        FileIndexId = row.OdriveFileIndexId ?? 0,
        Name = row.OdriveFileName ?? string.Empty,
        Path = row.OdriveRelativePath ?? string.Empty,
        Size = row.OdriveFileSizeBytes ?? 0,
        ModifiedAt = row.OdriveModifiedAt ?? default,
        SyncStatus = row.SyncStatus
    };

    private static RemovedFromODriveItem MapRemovedItem(SyncResultRow row) => new()
    {
        FileId = row.SyncResultId,
        // CAS-040 §6.5 — B2 mirror id
        FileIndexId = row.B2FileIndexId ?? 0,
        Name = row.B2FileName ?? string.Empty,
        Path = row.B2RelativePath ?? string.Empty,
        LastSeen = row.B2ModifiedAt ?? default,
        SyncStatus = row.SyncStatus
    };

    private static ModifiedOnODriveItem MapModifiedItem(SyncResultRow row) => new()
    {
        FileId = row.SyncResultId,
        // CAS-040 §6.5 — O-Drive id (CR-001 D5: O-Drive is authoritative)
        FileIndexId = row.OdriveFileIndexId ?? 0,
        Name = row.OdriveFileName ?? string.Empty,
        Path = row.OdriveRelativePath ?? string.Empty,
        ModifiedAt = row.OdriveModifiedAt ?? default,
        Sha1Changed = row.Sha1Changed ?? false,
        SyncStatus = row.SyncStatus
    };
}
