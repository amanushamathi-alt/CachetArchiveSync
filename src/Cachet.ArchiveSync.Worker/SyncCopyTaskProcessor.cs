using System.Security.Cryptography;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// <see cref="IWorkerTaskProcessor"/> for <c>task_type = 'SyncCopy'</c> (CAS-007 §3/§6/§12).
/// Per targeted <c>sync_result</c> row (a <c>Pending</c>/<c>New</c> row): reads the O-Drive file's
/// bytes from the local mirror, uploads to B2 via the system-key <see cref="IB2Client"/> (never
/// <c>IUserB2KeyAuthorizer</c> — SC-12), inserts the new B2-side <c>dbo.file_index</c> row, and
/// sets <c>sync_result.sync_status</c>. Each file's outcome is independent — one failure does not
/// stop the batch (§7 SC-9).
/// </summary>
public sealed class SyncCopyTaskProcessor : IWorkerTaskProcessor
{
    private const string ActingProcessIdentity = "worker-svc";
    private const string TabModule = "O-Drive Sync";

    public string TaskType => "SyncCopy";

    private readonly ISyncResultRepository _syncResultRepository;
    private readonly IFileIndexRepository _fileIndexRepository;
    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly TaskProgressReporterFactory _progressReporterFactory;
    private readonly IB2Client _b2Client;
    private readonly IODriveFileReader _odriveFileReader;
    private readonly IAuditLogService _auditLogService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SyncCopyTaskProcessor> _logger;

    public SyncCopyTaskProcessor(
        ISyncResultRepository syncResultRepository,
        IFileIndexRepository fileIndexRepository,
        ITaskQueueRepository taskQueueRepository,
        TaskProgressReporterFactory progressReporterFactory,
        IB2Client b2Client,
        IODriveFileReader odriveFileReader,
        IAuditLogService auditLogService,
        IConfiguration configuration,
        ILogger<SyncCopyTaskProcessor> logger)
    {
        _syncResultRepository = syncResultRepository;
        _fileIndexRepository = fileIndexRepository;
        _taskQueueRepository = taskQueueRepository;
        _progressReporterFactory = progressReporterFactory;
        _b2Client = b2Client;
        _odriveFileReader = odriveFileReader;
        _auditLogService = auditLogService;
        _configuration = configuration;
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
                if (row is null || row.OdriveRelativePath is null || row.OdriveFileName is null)
                {
                    failedCount++;
                    await progress.ItemCompletedAsync(ct);
                    continue;
                }

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

                    var sha1Hex = ComputeSha1Hex(content);

                    var uploadUrl = await _b2Client.GetUploadUrlAsync(ct);
                    using var uploadStream = new MemoryStream(content, writable: false);
                    await _b2Client.UploadFileAsync(uploadUrl, row.OdriveRelativePath, uploadStream, sha1Hex, content.LongLength, ct);

                    // CAS-013 §2/§9 V2: the new B2-side file_index row inherits the source row's
                    // bucket_name; falls back to the single global B2:BucketName config value only
                    // if the O-Drive-side row somehow has no bucket, logging a warning so that
                    // silent legacy-data gap stays visible.
                    var b2BucketName = row.OdriveBucketName;
                    if (b2BucketName is null)
                    {
                        b2BucketName = _configuration["B2:BucketName"];
                        _logger.LogWarning(
                            "SyncCopy task {TaskId}: sync_result {SyncResultId}'s O-Drive-side file_index row has no bucket_name; falling back to B2:BucketName config value for the new B2-side row.",
                            task.TaskQueueId, row.SyncResultId);
                    }

                    await _fileIndexRepository.InsertAsync(
                        ActingProcessIdentity, ActingProcessIdentity, "B2", b2BucketName,
                        row.OdriveRelativePath, row.OdriveFileName, ToIndexExtension(row.OdriveFileName),
                        content.LongLength, row.OdriveModifiedAt ?? DateTimeOffset.UtcNow, sha1Hex,
                        DateTimeOffset.UtcNow, ct);

                    await _syncResultRepository.UpdateStatusAsync(row.SyncResultId, ActingProcessIdentity, "Synced", task.TaskQueueId, ct);

                    await _auditLogService.WriteAsync(new AuditLogEntry
                    {
                        CreatedBy = ActingProcessIdentity,
                        UpdatedBy = ActingProcessIdentity,
                        UserId = task.SubmittedByUserId,
                        TabModule = TabModule,
                        Action = AuditAction.SyncCopyToB2,
                        FilePath = row.OdriveRelativePath,
                        TaskQueueId = task.TaskQueueId,
                        Result = AuditResult.Success
                    }, ct);

                    succeededCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await _syncResultRepository.UpdateStatusAsync(row.SyncResultId, ActingProcessIdentity, "Failed", task.TaskQueueId, ct);
                    var errorSummary = ex is B2ApiException ? "B2 upload call failed." : "Unhandled error copying file to B2.";
                    await WriteFailureAuditAsync(AuditAction.SyncActionFailedB2, row.OdriveRelativePath, task, errorSummary);
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
            var failureSummary = new SyncActionResultSummaryDto { FailureReason = "Unhandled error during SyncCopy processing." };
            await _taskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, ActingProcessIdentity, "Failed", JsonSerializer.Serialize(failureSummary), ct);
            _logger.LogWarning(ex, "SyncCopy task {TaskId} failed outright.", task.TaskQueueId);
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

    // dbo.file_index.file_extension holds the Keyword Search filter's form ("TXT", "DOCX"): no dot,
    // upper-case — dbo.file_index_search compares it by equality against the selected Extension.
    private static string? ToIndexExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        return extension.Length == 0 ? null : extension;
    }

    private static string ComputeSha1Hex(byte[] content) =>
        Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();

    private static SyncActionRequestParametersDto DeserializeRequest(string? requestParameters)
    {
        if (string.IsNullOrWhiteSpace(requestParameters))
        {
            throw new InvalidOperationException("task_queue.request_parameters was empty for a SyncCopy task.");
        }

        return JsonSerializer.Deserialize<SyncActionRequestParametersDto>(requestParameters)
            ?? throw new InvalidOperationException("task_queue.request_parameters could not be deserialized as SyncActionRequestParametersDto.");
    }
}
