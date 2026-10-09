using System.Security.Cryptography;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// <see cref="IWorkerTaskProcessor"/> for <c>task_type = 'B2MirrorSync'</c> (CAS-037 §6/§7/§12).
/// Enumerates the whole configured B2 bucket (paginated <c>b2_list_file_names</c>), skips any
/// object whose <c>dbo.file_index.sha1_hash</c> already matches the object's current
/// <c>contentSha1</c> (§7 V3), downloads new/changed objects into the local B2-mirror directory
/// resolved via <see cref="IBucketMirrorRootRepository"/> (CAS-011), and upserts one
/// <c>dbo.file_index</c> row (<c>source = 'B2'</c>) per enumerated, non-hidden object. A single
/// object's failure does not abort the run (§7 V4); a missing/inactive mirror-root mapping fails
/// the whole task outright (§7 V5) via the unhandled <see cref="InvalidOperationException"/>
/// <see cref="IBucketMirrorRootRepository.GetRootPathAsync"/> throws, propagated to this
/// processor's own outer catch (deliberately not caught locally, per §7 V5).
/// CAS-059 changes: enumeration is limited to the mapping's <c>b2_path_prefix</c> (same sync
/// folder as the rest of the app); an object is skipped only when its index SHA-1 matches AND the
/// mirror file exists with the expected size (a Copy-created row has the SHA-1 but no local file);
/// downloads go to a temporary file, are verified, then replace the mirror file; and the
/// destination path must stay inside the mirror root.
/// </summary>
public sealed class B2MirrorSyncTaskProcessor : IWorkerTaskProcessor
{
    private const string ActingProcessIdentity = "worker-svc";
    private const string TabModule = "O-Drive Sync";
    private const string Source = "B2";

    public string TaskType => "B2MirrorSync";

    private readonly IFileIndexRepository _fileIndexRepository;
    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly TaskProgressReporterFactory _progressReporterFactory;
    private readonly IB2Client _b2Client;
    private readonly IBucketMirrorRootRepository _bucketMirrorRootRepository;
    private readonly IB2PathPrefixProvider _prefixProvider;
    private readonly IAuditLogService _auditLogService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<B2MirrorSyncTaskProcessor> _logger;

    public B2MirrorSyncTaskProcessor(
        IFileIndexRepository fileIndexRepository,
        ITaskQueueRepository taskQueueRepository,
        TaskProgressReporterFactory progressReporterFactory,
        IB2Client b2Client,
        IBucketMirrorRootRepository bucketMirrorRootRepository,
        IB2PathPrefixProvider prefixProvider,
        IAuditLogService auditLogService,
        IConfiguration configuration,
        ILogger<B2MirrorSyncTaskProcessor> logger)
    {
        _fileIndexRepository = fileIndexRepository;
        _taskQueueRepository = taskQueueRepository;
        _progressReporterFactory = progressReporterFactory;
        _b2Client = b2Client;
        _bucketMirrorRootRepository = bucketMirrorRootRepository;
        _prefixProvider = prefixProvider;
        _auditLogService = auditLogService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task ProcessAsync(ClaimedTaskRow task, CancellationToken ct)
    {
        var newCount = 0;
        var updatedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;

        try
        {
            var bucketName = RequireConfig(_configuration, "B2:BucketName");

            // §7 V5: a missing/inactive mapping row throws InvalidOperationException here — left
            // uncaught so it reaches this method's own outer catch and fails the whole task.
            var rootPath = await _bucketMirrorRootRepository.GetRootPathAsync(bucketName, Source, ct);

            // CAS-059: only the configured sync folder. No active mapping row fails the task
            // outright, same as a missing mirror root (§7 V5).
            var prefix = await _prefixProvider.GetPrefixAsync(bucketName, ct)
                ?? throw new InvalidOperationException(
                    "No active odrive_bucket_mapping row (b2_path_prefix) found for the configured bucket.");
            var prefixWithSlash = prefix.Length == 0 ? string.Empty : prefix + "/";

            var pageSize = _configuration.GetValue<int?>("B2:MirrorSyncPageSize") ?? 1000;

            // §6.1: paginated enumeration into an in-memory list. B2 returns names in sorted order,
            // so names under the prefix are one contiguous run starting at the prefix itself: start
            // there and stop at the first name outside it.
            var allFiles = new List<B2FileInfo>();
            string? startFileName = prefixWithSlash.Length == 0 ? null : prefixWithSlash;
            do
            {
                var page = await _b2Client.ListFileNamesAsync(startFileName, pageSize, ct);
                var inPrefix = page.Files.TakeWhile(f => f.FileName.StartsWith(prefixWithSlash, StringComparison.Ordinal)).ToList();
                allFiles.AddRange(inPrefix);
                startFileName = inPrefix.Count < page.Files.Count ? null : page.NextFileName;
            }
            while (startFileName is not null);

            // §7 V2: only "upload" (a real, complete file) is ever downloaded/indexed.
            // CAS-059: a name ending in "/" is a zero-byte folder marker (e.g. "D/Shares/Operations/"),
            // not a file — it resolves to a directory and can never be written, so it is skipped too.
            // Same filters as scripts/index_refresh (common.py is_excluded / validate_relative_path):
            // excluded names are ignored silently; names that cannot be stored on Windows or in
            // dbo.file_index, or that differ only by case from an earlier name, are skipped with a warning.
            var uploadFiles = new List<B2FileInfo>();
            var seenIgnoringCase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in allFiles)
            {
                if (file.Action != "upload" || file.FileName.EndsWith('/') || IsExcludedName(file.FileName))
                {
                    continue;
                }

                var skipReason = InvalidPathReason(file.FileName)
                    ?? (seenIgnoringCase.Add(file.FileName) ? null : "differs only by case from another object (cannot coexist on Windows)");
                if (skipReason is not null)
                {
                    // The name may contain control characters (that is one reason to skip it), so it is
                    // logged with them replaced — a CR/LF in an object name cannot forge log lines.
                    _logger.LogWarning(
                        "B2MirrorSync task {TaskId}: object {RelativePath} skipped: {Reason}",
                        task.TaskQueueId, ForLog(file.FileName), skipReason);
                    continue;
                }

                uploadFiles.Add(file);
            }

            // §6.4 step: existing dbo.file_index rows for this source, keyed the same way
            // ContentScanTaskProcessor keys its own comparison maps.
            var existingRows = await _fileIndexRepository.ListActiveBySourceAsync(Source, ct);
            var existingByKey = existingRows.ToDictionary(
                row => (row.BucketName ?? string.Empty, row.RelativePath), row => row);

            var progress = _progressReporterFactory.Create(task.TaskQueueId);
            await progress.StartAsync(uploadFiles.Count, ct);
            _logger.LogInformation(
                "Task {TaskId} ({TaskType}) started: {ItemsTotal} object(s) enumerated",
                task.TaskQueueId, TaskType, uploadFiles.Count);

            foreach (var b2File in uploadFiles)
            {
                // relative_path already includes the file name (this project's existing
                // convention, confirmed against MirrorFileReader/SyncCopyTaskProcessor) — the B2
                // FileName field is used directly as relative_path.
                var relativePath = b2File.FileName;
                var fileName = Path.GetFileName(relativePath);

                try
                {
                    existingByKey.TryGetValue((bucketName, relativePath), out var existingRow);
                    var destinationPath = ResolveInsideRoot(rootPath, relativePath);

                    // §7 V3 + CAS-059: skip only when the index SHA-1 matches AND the mirror file
                    // really holds this B2 version. SyncCopy/SyncUpdate write the SHA-1 to the index
                    // without writing the mirror file, so a SHA-1 match alone is not enough.
                    if (existingRow is not null
                        && existingRow.Sha1Hash is not null
                        && string.Equals(existingRow.Sha1Hash, b2File.ContentSha1, StringComparison.OrdinalIgnoreCase)
                        && await IsLocalCopyCurrentAsync(destinationPath, b2File, ct))
                    {
                        // Unchanged — no download, last_seen_at touch only.
                        await _fileIndexRepository.UpdateAsync(
                            existingRow.FileIndexId, ActingProcessIdentity,
                            sha1Hash: null, fileSizeBytes: null, modifiedAt: null,
                            lastSeenAt: DateTimeOffset.UtcNow, isActive: null, ct);
                        skippedCount++;
                    }
                    else
                    {
                        await DownloadVerifiedAsync(b2File, destinationPath, ct);

                        var modifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(b2File.UploadTimestamp);

                        if (existingRow is not null)
                        {
                            await _fileIndexRepository.UpdateAsync(
                                existingRow.FileIndexId, ActingProcessIdentity,
                                sha1Hash: b2File.ContentSha1, fileSizeBytes: b2File.ContentLength,
                                modifiedAt: modifiedAt, lastSeenAt: DateTimeOffset.UtcNow, isActive: true, ct);
                            updatedCount++;
                        }
                        else
                        {
                            await _fileIndexRepository.InsertAsync(
                                ActingProcessIdentity, ActingProcessIdentity, Source, bucketName,
                                relativePath, fileName, ToIndexExtension(fileName),
                                b2File.ContentLength, modifiedAt, b2File.ContentSha1,
                                DateTimeOffset.UtcNow, ct);
                            newCount++;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // §7 V4: one object's failure does not abort the run.
                    failedCount++;
                    _logger.LogWarning(
                        ex, "B2MirrorSync task {TaskId}: object {RelativePath} failed; continuing",
                        task.TaskQueueId, relativePath);
                }

                await progress.ItemCompletedAsync(ct);
            }

            await progress.CompleteAsync(ct);

            var summary = new MirrorSyncResultSummaryDto
            {
                NewCount = newCount,
                UpdatedCount = updatedCount,
                SkippedCount = skippedCount,
                FailedCount = failedCount
            };
            var summaryJson = JsonSerializer.Serialize(summary);

            // §12: one completion audit row, written before the Completed transition. No
            // per-object audit rows (would flood dbo.audit_log at full-bucket scale).
            await _auditLogService.WriteAsync(new AuditLogEntry
            {
                CreatedBy = ActingProcessIdentity,
                UpdatedBy = ActingProcessIdentity,
                UserId = task.SubmittedByUserId,
                TabModule = TabModule,
                Action = AuditAction.B2MirrorSyncCompleted,
                TaskQueueId = task.TaskQueueId,
                Result = AuditResult.Success,
                ResultDetail = summaryJson
            }, ct);

            await _taskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, ActingProcessIdentity, "Completed", summaryJson, ct);

            _logger.LogInformation(
                "B2MirrorSync task {TaskId} completed: {NewCount} new, {UpdatedCount} updated, {SkippedCount} unchanged, {FailedCount} failed",
                task.TaskQueueId, newCount, updatedCount, skippedCount, failedCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // §12: no dedicated audit row for a total task-level failure, same pattern as
            // SyncCopyTaskProcessor's outer catch.
            var failureSummary = new MirrorSyncResultSummaryDto
            {
                FailureReason = "Unhandled error during B2MirrorSync processing."
            };
            await _taskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, ActingProcessIdentity, "Failed", JsonSerializer.Serialize(failureSummary), ct);
            _logger.LogWarning(ex, "B2MirrorSync task {TaskId} failed outright.", task.TaskQueueId);
        }
    }

    // CAS-059: download to "<file>.cachet-partial", verify size and SHA-1, then replace the mirror
    // file. On any failure the partial file is removed and the existing mirror file is untouched.
    private async Task DownloadVerifiedAsync(B2FileInfo b2File, string destinationPath, CancellationToken ct)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var partialPath = destinationPath + ".cachet-partial";
        try
        {
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long bytesWritten = 0;
            using (var downloaded = await _b2Client.DownloadFileByIdAsync(b2File.FileId, ct))
            await using (var partialStream = new FileStream(
                partialPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await downloaded.Content.ReadAsync(buffer, ct)) > 0)
                {
                    sha1.AppendData(buffer, 0, read);
                    await partialStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytesWritten += read;
                }
            }

            if (bytesWritten != b2File.ContentLength)
            {
                throw new InvalidDataException(
                    $"Downloaded size {bytesWritten} does not match the B2 object size {b2File.ContentLength}.");
            }

            var expectedSha1 = ExpectedSha1(b2File.ContentSha1);
            if (expectedSha1 is not null
                && !string.Equals(Convert.ToHexString(sha1.GetHashAndReset()), expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Downloaded content does not match the B2 object's SHA-1.");
            }

            File.Move(partialPath, destinationPath, overwrite: true);
            File.SetLastWriteTimeUtc(
                destinationPath, DateTimeOffset.FromUnixTimeMilliseconds(b2File.UploadTimestamp).UtcDateTime);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    // B2 reports contentSha1 as 40 hex chars, "unverified:<40 hex>" (SHA-1 supplied by the uploader),
    // or "none" (large multi-part files). Only a real hex SHA-1 can be checked; otherwise size only.
    private static string? ExpectedSha1(string? contentSha1)
    {
        if (string.IsNullOrEmpty(contentSha1))
        {
            return null;
        }

        var value = contentSha1.StartsWith("unverified:", StringComparison.OrdinalIgnoreCase)
            ? contentSha1["unverified:".Length..]
            : contentSha1;
        return value.Length == 40 && value.All(Uri.IsHexDigit) ? value : null;
    }

    // scripts/index_refresh/common.py EXCLUDED_NAME_PATTERNS — matched on the file name, ignoring case
    // as fnmatch does on Windows. .bzEmpty is the placeholder B2 creates for a folder made in its web UI.
    private static readonly string[] ExcludedNames = { "Thumbs.db", "desktop.ini", ".DS_Store", ".bzEmpty" };

    private static bool IsExcludedName(string relativePath)
    {
        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return ExcludedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith("~$", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".cachet-partial", StringComparison.OrdinalIgnoreCase);
    }

    // scripts/index_refresh/common.py validate_relative_path: limits follow dbo.file_index column sizes
    // (relative_path 1000, file_name 500, file_extension 10) and the B2 object-name limit (1024 UTF-8 bytes).
    private static string? InvalidPathReason(string relativePath)
    {
        var segments = relativePath.Split('/');
        if (segments.Any(s => s is "" or "." or ".."))
        {
            return "unsafe path segment";
        }

        // "\" is an ordinary character in a B2 name but a separator on Windows: "a/b\c.txt" would land
        // on the same mirror file as "a/b/c.txt", so it is rejected too (stricter than the script).
        if (relativePath.Any(ch => ch < 32 || "<>:\"|?*\\".Contains(ch)))
        {
            return "character not allowed in a Windows path";
        }

        if (relativePath.Length > 1000)
        {
            return "relative path longer than 1000";
        }

        if (System.Text.Encoding.UTF8.GetByteCount(relativePath) > 1024)
        {
            return "relative path longer than 1024 bytes in UTF-8 (B2 object-name limit)";
        }

        var name = segments[^1];
        if (name.Length > 500)
        {
            return "file name longer than 500";
        }

        return (ToIndexExtension(name)?.Length ?? 0) > 10 ? "extension longer than 10" : null;
    }

    private static string ForLog(string value) =>
        string.Create(value.Length, value, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });

    // CAS-059: the mirror file holds this B2 version when its size matches and either
    //  - its last-write time equals the B2 upload time (set by every download here) — cheap path; or
    //  - its content hashes to the B2 SHA-1. That covers files placed by refresh_b2_index.py (which sets
    //    the source's last-modified time); the upload time is then adopted, so the next run takes
    //    the cheap path.
    // A same-size SyncUpdate leaves the old content with an older time, fails the hash, and is
    // downloaded again. With no usable B2 SHA-1 ("none"), a time mismatch means download.
    private static async Task<bool> IsLocalCopyCurrentAsync(string path, B2FileInfo b2File, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != b2File.ContentLength)
        {
            return false;
        }

        var uploadedUtc = DateTimeOffset.FromUnixTimeMilliseconds(b2File.UploadTimestamp).UtcDateTime;
        if (info.LastWriteTimeUtc == uploadedUtc)
        {
            return true;
        }

        var expectedSha1 = ExpectedSha1(b2File.ContentSha1);
        if (expectedSha1 is null)
        {
            return false;
        }

        string actualSha1;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true))
        {
            actualSha1 = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct));
        }

        if (!string.Equals(actualSha1, expectedSha1, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            File.SetLastWriteTimeUtc(path, uploadedUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still current; only the next run's cheap path is lost.
        }

        return true;
    }

    // B2 names use "/". Reject any name that would resolve outside the mirror root (e.g. "..").
    // Defense in depth: InvalidPathReason already skips "." / ".." segments and "\" before this is reached.
    internal static string ResolveInsideRoot(string rootPath, string relativePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        // A drive or share root ("E:\") keeps its trailing separator after the trim; do not add a second one.
        var rootWithSeparator = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("B2 object name resolves outside the B2 mirror root.");
        }

        return fullPath;
    }

    // dbo.file_index.file_extension holds the Keyword Search filter's form ("TXT", "DOCX"): no dot,
    // upper-case — same helper as SyncCopyTaskProcessor's ToIndexExtension.
    private static string? ToIndexExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        return extension.Length == 0 ? null : extension;
    }

    private static string RequireConfig(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Configuration key '{key}' must be set (CAS-037 §6.1) — same as B2Client.cs's " +
                "own construction-time check (CAS-001 §7 V1).");
        }

        return value;
    }
}
