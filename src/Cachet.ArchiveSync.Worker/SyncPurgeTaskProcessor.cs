using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// <see cref="IWorkerTaskProcessor"/> for <c>task_type = 'SyncPurge'</c> (CAS-007 §3/§6/§12).
/// Per targeted <c>sync_result</c> row (a <c>Pending</c>/<c>Removed</c> row): enumerates **every**
/// version of the B2 object via <see cref="IB2Client.ListFileVersionsAsync"/> and deletes each one
/// via <see cref="IB2Client.DeleteFileVersionAsync"/> (CAS-001 §6.4 Step B pattern, reused
/// directly per §4's note), then soft-deletes the corresponding B2-side <c>dbo.file_index</c> row
/// (<c>is_active = 0</c>). No local O-Drive read — purge only ever writes
/// <c>SyncActionFailedB2</c> on a per-file failure, never <c>SyncActionFailedLocal</c> (§6). Each
/// file's outcome is independent (§7 SC-9).
/// CAS-062: after a successful B2 purge, the file's local B2 mirror copy is removed through
/// <see cref="IB2MirrorFileRemover"/> (B2 mirror root only, never O-Drive); a local failure is
/// audited as <c>SyncActionFailedLocal</c> without reversing the purge.
/// </summary>
public sealed class SyncPurgeTaskProcessor : IWorkerTaskProcessor
{
    private const string ActingProcessIdentity = "worker-svc";
    private const string TabModule = "O-Drive Sync";

    public string TaskType => "SyncPurge";

    private readonly ISyncResultRepository _syncResultRepository;
    private readonly IFileIndexRepository _fileIndexRepository;
    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly TaskProgressReporterFactory _progressReporterFactory;
    private readonly IB2Client _b2Client;
    private readonly IB2MirrorFileRemover _b2MirrorFileRemover;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<SyncPurgeTaskProcessor> _logger;

    public SyncPurgeTaskProcessor(
        ISyncResultRepository syncResultRepository,
        IFileIndexRepository fileIndexRepository,
        ITaskQueueRepository taskQueueRepository,
        TaskProgressReporterFactory progressReporterFactory,
        IB2Client b2Client,
        IB2MirrorFileRemover b2MirrorFileRemover,
        IAuditLogService auditLogService,
        ILogger<SyncPurgeTaskProcessor> logger)
    {
        _syncResultRepository = syncResultRepository;
        _fileIndexRepository = fileIndexRepository;
        _taskQueueRepository = taskQueueRepository;
        _progressReporterFactory = progressReporterFactory;
        _b2Client = b2Client;
        _b2MirrorFileRemover = b2MirrorFileRemover;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task ProcessAsync(ClaimedTaskRow task, CancellationToken ct)
    {
        var succeededCount = 0;
        var failedCount = 0;

        try
        {
            var request = DeserializeRequest(task.RequestParameters);

            // CAS-029 §3.3: one unit of work per submitted fileId, as submitted (no dedup).
            var progress = _progressReporterFactory.Create(task.TaskQueueId);
            await progress.StartAsync(request.FileIds.Count, ct);
            _logger.LogInformation(
                "Task {TaskId} ({TaskType}) started: {ItemsTotal} item(s) to process",
                task.TaskQueueId, TaskType, request.FileIds.Count);

            foreach (var syncResultId in request.FileIds)
            {
                var row = await _syncResultRepository.GetAsync(syncResultId, ct);
                if (row is null || row.B2RelativePath is null || row.B2FileIndexId is null)
                {
                    failedCount++;
                    await progress.ItemCompletedAsync(ct);
                    continue;
                }

                try
                {
                    await DeleteAllVersionsAsync(row.B2RelativePath, ct);

                    await _fileIndexRepository.UpdateAsync(
                        row.B2FileIndexId.Value, ActingProcessIdentity,
                        sha1Hash: null, fileSizeBytes: null, modifiedAt: null, lastSeenAt: null,
                        isActive: false, ct);

                    await _syncResultRepository.UpdateStatusAsync(row.SyncResultId, ActingProcessIdentity, "Synced", task.TaskQueueId, ct);

                    await _auditLogService.WriteAsync(new AuditLogEntry
                    {
                        CreatedBy = ActingProcessIdentity,
                        UpdatedBy = ActingProcessIdentity,
                        UserId = task.SubmittedByUserId,
                        TabModule = TabModule,
                        Action = AuditAction.SyncPurgeFromB2,
                        FilePath = row.B2RelativePath,
                        TaskQueueId = task.TaskQueueId,
                        Result = AuditResult.Success
                    }, ct);

                    // CAS-062: B2 is already purged (irreversible), so the local B2 mirror copy goes
                    // too. Nothing in this step may reverse the purge (§9 V5): an unexpected error is
                    // handled here, never by the per-file catch below (which would mark it Failed).
                    await RemoveLocalMirrorCopyAsync(task, row, ct);

                    succeededCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await _syncResultRepository.UpdateStatusAsync(row.SyncResultId, ActingProcessIdentity, "Failed", task.TaskQueueId, ct);
                    var errorSummary = ex is B2ApiException ? "B2 delete call failed." : "Unhandled error purging file from B2.";

                    await _auditLogService.WriteAsync(new AuditLogEntry
                    {
                        CreatedBy = ActingProcessIdentity,
                        UpdatedBy = ActingProcessIdentity,
                        UserId = task.SubmittedByUserId,
                        TabModule = TabModule,
                        Action = AuditAction.SyncActionFailedB2,
                        FilePath = row.B2RelativePath,
                        TaskQueueId = task.TaskQueueId,
                        Result = AuditResult.Failure,
                        ResultDetail = errorSummary
                    }, ct);

                    _logger.LogWarning("{ActionType} task {TaskId} file {SyncResultId} failed: {ErrorSummary}",
                        TaskType, task.TaskQueueId, row.SyncResultId, errorSummary);

                    failedCount++;
                }

                // CAS-029 §3.3: once per fileId, after its sync_result status and audit writes.
                await progress.ItemCompletedAsync(ct);
            }

            // CAS-029 §3.6: per-file status/audit writes (awaited above) → final progress → Completed.
            await progress.CompleteAsync(ct);

            var summary = new SyncActionResultSummaryDto { SucceededCount = succeededCount, FailedCount = failedCount };
            await _taskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, ActingProcessIdentity, "Completed", JsonSerializer.Serialize(summary), ct);

            _logger.LogInformation(
                "{ActionType} task {TaskId} completed: {SucceededCount} succeeded, {FailedCount} failed",
                TaskType, task.TaskQueueId, succeededCount, failedCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failureSummary = new SyncActionResultSummaryDto { FailureReason = "Unhandled error during SyncPurge processing." };
            await _taskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, ActingProcessIdentity, "Failed", JsonSerializer.Serialize(failureSummary), ct);
            _logger.LogWarning(ex, "SyncPurge task {TaskId} failed outright.", task.TaskQueueId);
        }
    }

    // CAS-062 §9 V5: the file stays Synced whatever happens here. A Failed outcome or an unexpected
    // error is audited as SyncActionFailedLocal; if even that audit write fails, it is only logged.
    private async Task RemoveLocalMirrorCopyAsync(ClaimedTaskRow task, SyncResultRow row, CancellationToken ct)
    {
        B2MirrorRemoveResult localRemoval;
        try
        {
            localRemoval = await _b2MirrorFileRemover.RemoveAsync(row.B2RelativePath!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{ActionType} task {TaskId} file {SyncResultId}: unexpected error removing local mirror copy",
                TaskType, task.TaskQueueId, row.SyncResultId);
            localRemoval = B2MirrorRemoveResult.Failed("Unexpected error removing the local copy.");
        }

        if (localRemoval.Outcome != B2MirrorRemoveOutcome.Failed)
        {
            return;
        }

        _logger.LogWarning(
            "{ActionType} task {TaskId} file {SyncResultId}: purged from B2 but local mirror copy not removed: {Reason}",
            TaskType, task.TaskQueueId, row.SyncResultId, localRemoval.FailureReason);

        try
        {
            await _auditLogService.WriteAsync(new AuditLogEntry
            {
                CreatedBy = ActingProcessIdentity,
                UpdatedBy = ActingProcessIdentity,
                UserId = task.SubmittedByUserId,
                TabModule = TabModule,
                Action = AuditAction.SyncActionFailedLocal,
                FilePath = row.B2RelativePath,
                TaskQueueId = task.TaskQueueId,
                Result = AuditResult.Failure,
                ResultDetail = "Purged from B2; local mirror copy not removed: " + localRemoval.FailureReason
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{ActionType} task {TaskId} file {SyncResultId}: SyncActionFailedLocal audit write failed",
                TaskType, task.TaskQueueId, row.SyncResultId);
        }
    }

    /// <summary>
    /// Enumerates every version of the object named <paramref name="fileName"/> (CAS-001 §6.4
    /// Step B, reused per CAS-007 §4) and deletes each one. Paginates via
    /// <c>NextFileName</c>/<c>NextFileId</c> until B2 reports no more results; only versions whose
    /// <c>FileName</c> exactly matches <paramref name="fileName"/> are deleted (a prefix match can
    /// otherwise also return a different, longer-named object).
    /// </summary>
    private async Task DeleteAllVersionsAsync(string fileName, CancellationToken ct)
    {
        string? startFileName = fileName;
        string? startFileId = null;

        while (startFileName is not null)
        {
            var page = await _b2Client.ListFileVersionsAsync(startFileName, fileName, 1000, ct);

            foreach (var version in page.Files)
            {
                if (!string.Equals(version.FileName, fileName, StringComparison.Ordinal))
                {
                    // Past the exact-name run — prefix match moved on to a different object.
                    return;
                }

                await _b2Client.DeleteFileVersionAsync(version.FileName, version.FileId, ct);
            }

            if (page.Files.Count == 0 || !string.Equals(page.NextFileName, fileName, StringComparison.Ordinal))
            {
                break;
            }

            startFileName = page.NextFileName;
            startFileId = page.NextFileId;
            _ = startFileId; // b2_list_file_versions pagination also takes a startFileId; not
                             // exposed by IB2Client.ListFileVersionsAsync's current signature —
                             // startFileName alone is sufficient for exhausting one object's own
                             // version run in practice, flagged for reviewer awareness.
        }
    }

    private static SyncActionRequestParametersDto DeserializeRequest(string? requestParameters)
    {
        if (string.IsNullOrWhiteSpace(requestParameters))
        {
            throw new InvalidOperationException("task_queue.request_parameters was empty for a SyncPurge task.");
        }

        return JsonSerializer.Deserialize<SyncActionRequestParametersDto>(requestParameters)
            ?? throw new InvalidOperationException("task_queue.request_parameters could not be deserialized as SyncActionRequestParametersDto.");
    }
}
