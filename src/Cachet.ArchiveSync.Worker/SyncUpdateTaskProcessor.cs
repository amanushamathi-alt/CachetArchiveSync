using System.Security.Cryptography;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// <see cref="IWorkerTaskProcessor"/> for <c>task_type = 'SyncUpdate'</c> (CAS-007 §3/§6/§12).
/// Per targeted <c>sync_result</c> row (a <c>Pending</c>/<c>Modified</c> row): reads the O-Drive
/// file's current bytes from the local mirror, uploads to the existing B2 object's <c>fileName</c>
/// (B2's own versioning supersedes the prior content — no separate "overwrite" call exists), and
/// updates that <c>dbo.file_index</c> row's <c>sha1_hash</c>/<c>file_size_bytes</c>/
/// <c>modified_at</c> in place (no new row inserted). Each file's outcome is independent (§7 SC-9).
/// </summary>
public sealed class SyncUpdateTaskProcessor : IWorkerTaskProcessor
{
    private const string ActingProcessIdentity = "worker-svc";
    private const string TabModule = "O-Drive Sync";

    public string TaskType => "SyncUpdate";

    private readonly ISyncResultRepository _syncResultRepository;
    private readonly IFileIndexRepository _fileIndexRepository;
    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly TaskProgressReporterFactory _progressReporterFactory;
    private readonly IB2Client _b2Client;
    private readonly IODriveFileReader _odriveFileReader;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<SyncUpdateTaskProcessor> _logger;

    public SyncUpdateTaskProcessor(
        ISyncResultRepository syncResultRepository,
        IFileIndexRepository fileIndexRepository,
        ITaskQueueRepository taskQueueRepository,
        TaskProgressReporterFactory progressReporterFactory,
        IB2Client b2Client,
        IODriveFileReader odriveFileReader,
        IAuditLogService auditLogService,
        ILogger<SyncUpdateTaskProcessor> logger)
    {
        _syncResultRepository = syncResultRepository;
        _fileIndexRepository = fileIndexRepository;
        _taskQueueRepository = taskQueueRepository;
        _progressReporterFactory = progressReporterFactory;
        _b2Client = b2Client;
        _odriveFileReader = odriveFileReader;
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
                if (row is null || row.OdriveRelativePath is null || row.B2FileIndexId is null)
                {
                    failedCount++;
                    await progress.ItemCompletedAsync(ct);
                    continue;
                }

                var b2FileName = row.B2RelativePath ?? row.OdriveRelativePath;

                try
                {
                    byte[] content;
                    try
                    {
                        content = await _odriveFileReader.ReadAllBytesAsync(row.OdriveBucketName ?? string.Empty, row.OdriveRelativePath, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await _syncResultRepository.UpdateStatusAsync(row.SyncResultId, ActingProcessIdentity, "Failed", task.TaskQueueId, ct);
                        await WriteFailureAuditAsync(AuditAction.SyncActionFailedLocal, row.OdriveRelativePath, task, ex.Message);
                        failedCount++;
                        await progress.ItemCompletedAsync(ct);
                        continue;
                    }

                    var sha1Hex = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();

                    var uploadUrl = await _b2Client.GetUploadUrlAsync(ct);
                    using var uploadStream = new MemoryStream(content, writable: false);
                    await _b2Client.UploadFileAsync(uploadUrl, b2FileName, uploadStream, sha1Hex, content.LongLength, ct);

                    await _fileIndexRepository.UpdateAsync(
                        row.B2FileIndexId.Value, ActingProcessIdentity, sha1Hex, content.LongLength,
                        row.OdriveModifiedAt, DateTimeOffset.UtcNow, isActive: null, ct);

                    await _syncResultRepository.UpdateStatusAsync(row.SyncResultId, ActingProcessIdentity, "Synced", task.TaskQueueId, ct);

                    await _auditLogService.WriteAsync(new AuditLogEntry
                    {
                        CreatedBy = ActingProcessIdentity,
                        UpdatedBy = ActingProcessIdentity,
                        UserId = task.SubmittedByUserId,
                        TabModule = TabModule,
                        Action = AuditAction.SyncUpdateInB2,
                        FilePath = b2FileName,
                        TaskQueueId = task.TaskQueueId,
                        Result = AuditResult.Success
                    }, ct);

                    succeededCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await _syncResultRepository.UpdateStatusAsync(row.SyncResultId, ActingProcessIdentity, "Failed", task.TaskQueueId, ct);
                    var errorSummary = ex is B2ApiException ? "B2 upload call failed." : "Unhandled error updating file in B2.";
                    await WriteFailureAuditAsync(AuditAction.SyncActionFailedB2, b2FileName, task, errorSummary);
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
            var failureSummary = new SyncActionResultSummaryDto { FailureReason = "Unhandled error during SyncUpdate processing." };
            await _taskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, ActingProcessIdentity, "Failed", JsonSerializer.Serialize(failureSummary), ct);
            _logger.LogWarning(ex, "SyncUpdate task {TaskId} failed outright.", task.TaskQueueId);
        }
    }

    private async Task WriteFailureAuditAsync(AuditAction action, string? filePath, ClaimedTaskRow task, string errorSummary)
    {
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = task.SubmittedByUserId,
            TabModule = TabModule,
            Action = action,
            FilePath = filePath,
            TaskQueueId = task.TaskQueueId,
            Result = AuditResult.Failure,
            ResultDetail = errorSummary
        }, CancellationToken.None);

        _logger.LogWarning(
            "{ActionType} task {TaskId} file failed: {ErrorSummary}", TaskType, task.TaskQueueId, errorSummary);
    }

    private static SyncActionRequestParametersDto DeserializeRequest(string? requestParameters)
    {
        if (string.IsNullOrWhiteSpace(requestParameters))
        {
            throw new InvalidOperationException("task_queue.request_parameters was empty for a SyncUpdate task.");
        }

        return JsonSerializer.Deserialize<SyncActionRequestParametersDto>(requestParameters)
            ?? throw new InvalidOperationException("task_queue.request_parameters could not be deserialized as SyncActionRequestParametersDto.");
    }
}
