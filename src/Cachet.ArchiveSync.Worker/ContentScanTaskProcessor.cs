using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// <see cref="IWorkerTaskProcessor"/> for <c>task_type = 'ContentScan'</c> (CAS-007 §3/§5/§12).
/// Sequence: load all active <c>dbo.file_index</c> rows for <c>source = 'ODrive'</c> and
/// <c>source = 'B2'</c> into in-memory maps keyed by <c>(bucket_name, relative_path)</c>, classify
/// each key into <c>New</c>/<c>Removed</c>/<c>Modified</c>/no-row per §5's Comparison Algorithm,
/// insert one <c>dbo.sync_result</c> row per disagreement, then transition the task to
/// <c>Completed</c>/<c>Failed</c>. Never calls <see cref="IB2Client"/> — this comparison is
/// local-mirror-vs-local-mirror only (§3, API design spec §5.1).
/// </summary>
public sealed class ContentScanTaskProcessor : IWorkerTaskProcessor
{
    private const string ActingProcessIdentity = "worker-svc";

    public string TaskType => "ContentScan";

    private readonly IFileIndexRepository _fileIndexRepository;
    private readonly ISyncResultRepository _syncResultRepository;
    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly TaskProgressReporterFactory _progressReporterFactory;
    private readonly ILogger<ContentScanTaskProcessor> _logger;

    public ContentScanTaskProcessor(
        IFileIndexRepository fileIndexRepository,
        ISyncResultRepository syncResultRepository,
        ITaskQueueRepository taskQueueRepository,
        TaskProgressReporterFactory progressReporterFactory,
        ILogger<ContentScanTaskProcessor> logger)
    {
        _fileIndexRepository = fileIndexRepository;
        _syncResultRepository = syncResultRepository;
        _taskQueueRepository = taskQueueRepository;
        _progressReporterFactory = progressReporterFactory;
        _logger = logger;
    }

    public async Task ProcessAsync(ClaimedTaskRow task, CancellationToken ct)
    {
        try
        {
            // §5 step 1: load both sides, keyed by (bucket_name, relative_path).
            var odriveRows = await _fileIndexRepository.ListActiveBySourceAsync("ODrive", ct);
            var b2Rows = await _fileIndexRepository.ListActiveBySourceAsync("B2", ct);

            var odriveMap = odriveRows.ToDictionary(Key, row => row);
            var b2Map = b2Rows.ToDictionary(Key, row => row);

            // CAS-029 §3.2 step 2: unit of work = one distinct key in the union of both maps.
            var itemsTotal = odriveMap.Count + b2Map.Keys.Count(key => !odriveMap.ContainsKey(key));
            var progress = _progressReporterFactory.Create(task.TaskQueueId);
            await progress.StartAsync(itemsTotal, ct);
            _logger.LogInformation(
                "Task {TaskId} ({TaskType}) started: {ItemsTotal} item(s) to process",
                task.TaskQueueId, TaskType, itemsTotal);

            var newCount = 0;
            var removedCount = 0;
            var modifiedCount = 0;

            // CAS-029 §3.2 step 3: two passes visiting each key exactly once (previously three
            // passes: New, Removed, Modified). The set of sync_result rows produced is unchanged;
            // only insert order may differ, and nothing depends on it.

            // Pass A, over the O-Drive keys.
            foreach (var (key, odriveRow) in odriveMap)
            {
                if (!b2Map.TryGetValue(key, out var b2Row))
                {
                    // §5 step 2: New on O-Drive — present in the O-Drive map, absent from the B2 map.
                    await _syncResultRepository.InsertAsync(
                        ActingProcessIdentity, ActingProcessIdentity, task.TaskQueueId,
                        "New", odriveRow.FileIndexId, null, null, ct);
                    newCount++;
                }
                else if (odriveRow.Sha1Hash is not null
                    && b2Row.Sha1Hash is not null
                    && !string.Equals(odriveRow.Sha1Hash, b2Row.Sha1Hash, StringComparison.OrdinalIgnoreCase))
                {
                    // §5 step 4: Modified on O-Drive — present in both with differing sha1_hash. A
                    // NULL hash on either side is skipped (§7 V10), an equal hash produces no row
                    // at all (§7 V9).
                    await _syncResultRepository.InsertAsync(
                        ActingProcessIdentity, ActingProcessIdentity, task.TaskQueueId,
                        "Modified", odriveRow.FileIndexId, b2Row.FileIndexId, true, ct);
                    modifiedCount++;
                }

                await progress.ItemCompletedAsync(ct);
            }

            // Pass B, over the B2 keys absent from O-Drive.
            foreach (var (key, b2Row) in b2Map)
            {
                if (odriveMap.ContainsKey(key))
                {
                    continue;
                }

                // §5 step 3: Removed from O-Drive — present in the B2 map, absent from the O-Drive map.
                await _syncResultRepository.InsertAsync(
                    ActingProcessIdentity, ActingProcessIdentity, task.TaskQueueId,
                    "Removed", null, b2Row.FileIndexId, null, ct);
                removedCount++;

                await progress.ItemCompletedAsync(ct);
            }

            var summary = new ContentScanResultSummaryDto
            {
                NewCount = newCount,
                RemovedCount = removedCount,
                ModifiedCount = modifiedCount
            };

            // CAS-029 §3.6: result rows (awaited above) → final progress → Completed.
            await progress.CompleteAsync(ct);

            await _taskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, ActingProcessIdentity, "Completed", JsonSerializer.Serialize(summary), ct);

            _logger.LogInformation(
                "ContentScan task {TaskId} completed: {NewCount} new, {RemovedCount} removed, {ModifiedCount} modified",
                task.TaskQueueId, newCount, removedCount, modifiedCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            const string errorSummary = "Unhandled error during Content Scan comparison.";
            var failureSummary = new ContentScanResultSummaryDto { FailureReason = errorSummary };

            await _taskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, ActingProcessIdentity, "Failed", JsonSerializer.Serialize(failureSummary), ct);

            _logger.LogWarning(ex, "ContentScan task {TaskId} failed: {ErrorSummary}", task.TaskQueueId, errorSummary);
        }
    }

    private static (string BucketName, string RelativePath) Key(FileIndexRow row) =>
        (row.BucketName ?? string.Empty, row.RelativePath);
}
