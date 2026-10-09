using System.Text.Json;
using Cachet.ArchiveSync.Api.Content;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Worker;

/// <summary>
/// <see cref="IWorkerTaskProcessor"/> for <c>task_type = 'KeywordSearch'</c> (CAS-006 §3/§6/§12;
/// content-match handling redesigned by CAS-012 §2). Sequence: query <c>dbo.file_index</c>
/// local-first (filename <c>LIKE</c>, term already confirmed by SQL; content candidates
/// unfiltered) → for each <c>Content</c> candidate, read its physical mirror file
/// (<see cref="IMirrorFileReader"/>) and check the term against its extracted text
/// (<see cref="IFileContentMatcher"/>) — a <c>null</c> result (non-match or unreadable file) drops
/// the candidate silently (CAS-012 §7 V10/V11) → every remaining candidate (all <c>Filename</c>
/// candidates, plus <c>Content</c> candidates confirmed to contain the term) is cross-checked
/// against B2 with bounded concurrency → insert confirmed <c>dbo.search_result</c> rows,
/// self-heal/audit candidates B2 itself excludes, and isolate a candidate whose cross-check call
/// could not be resolved after CAS-034's retries (a non-404 <see cref="B2ApiException"/>, an
/// <see cref="HttpRequestException"/>, or an internal timeout) as <c>Indeterminate</c> — omitted
/// from results, neither confirmed nor excluded, logged and audited
/// (<see cref="AuditAction.CrossCheckIndeterminate"/>) but never failing the whole task (CAS-035
/// §2) — → transition the task to <c>Completed</c>/<c>Failed</c>.
///
/// DEVIATION (implementer, CAS-006, flagged for reviewer): CAS-001 §6.3 step 2 describes the
/// cross-check as a <c>b2_get_file_info</c> call, which requires a B2-native <c>fileId</c>.
/// <c>dbo.file_index</c> (CAS-003) stores no such column — only <c>relative_path</c>/<c>file_name</c>
/// — so this processor cross-checks via <c>IB2Client.ListFileNamesAsync</c> instead
/// (<c>startFileName = candidate.RelativePath</c>, <c>maxFileCount = 1</c>), treating a match only
/// when the returned file's name equals the candidate's path exactly and its <c>action</c> is
/// <c>"upload"</c>. This stays within <see cref="IB2Client"/>'s already-defined surface (no
/// interface change) and preserves the same confirm/exclude semantics as §6.3, but is not a
/// literal reading of "calls b2_get_file_info" — flagged since neither CAS-001 nor CAS-003 names
/// a B2-file-id column to use instead.
/// </summary>
public sealed class KeywordSearchTaskProcessor : IWorkerTaskProcessor
{
    private const string ActingProcessIdentity = "worker-svc";
    private const string TabModule = "Keyword Search";
    private const string FilenameMatchStatus = "Flagged";
    private const string ContentMatchStatus = "Needs Review";

    // CAS-038 A2: a date-only search (no term) stores its Filename matches as 'Informational'
    // (already allowed by CK_search_result_match_status) instead of 'Flagged'.
    private const string DateOnlyMatchStatus = "Informational";

    // CAS-047 §2: search modes and the B2 source scope.
    private const string FilenameMode = "Filename";
    private const string KeywordMode = "Keyword";
    private const string B2Source = "B2";

    // CAS-054 §2.2/§14 Q6: resume watermark is persisted at most once per interval (code constant).
    private static readonly TimeSpan WatermarkWriteInterval = TimeSpan.FromSeconds(10);

    public string TaskType => "KeywordSearch";

    private readonly IFileIndexRepository _fileIndexRepository;
    private readonly ISearchResultRepository _searchResultRepository;
    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly TaskProgressReporterFactory _progressReporterFactory;
    private readonly IB2Client _b2Client;
    private readonly IAuditLogService _auditLogService;
    private readonly IMirrorFileReader _mirrorFileReader;
    private readonly IFileContentMatcher _fileContentMatcher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<KeywordSearchTaskProcessor> _logger;
    private readonly TimeProvider _timeProvider;

    public KeywordSearchTaskProcessor(
        IFileIndexRepository fileIndexRepository,
        ISearchResultRepository searchResultRepository,
        ITaskQueueRepository taskQueueRepository,
        TaskProgressReporterFactory progressReporterFactory,
        IB2Client b2Client,
        IAuditLogService auditLogService,
        IMirrorFileReader mirrorFileReader,
        IFileContentMatcher fileContentMatcher,
        IConfiguration configuration,
        ILogger<KeywordSearchTaskProcessor> logger,
        TimeProvider? timeProvider = null)
    {
        _fileIndexRepository = fileIndexRepository;
        _searchResultRepository = searchResultRepository;
        _taskQueueRepository = taskQueueRepository;
        _progressReporterFactory = progressReporterFactory;
        _b2Client = b2Client;
        _auditLogService = auditLogService;
        _mirrorFileReader = mirrorFileReader;
        _fileContentMatcher = fileContentMatcher;
        _configuration = configuration;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task ProcessAsync(ClaimedTaskRow task, CancellationToken ct)
    {
        try
        {
            var request = DeserializeRequest(task.RequestParameters);

            // CAS-038 A2: null/empty/whitespace term = date-only search (the SP handles "" itself).
            var hasTerm = !string.IsNullOrWhiteSpace(request.Term);

            if (request.Mode == FilenameMode)
            {
                await ProcessFilenameModeAsync(task, request, ct);
                return;
            }

            // CAS-047 §2/§9 V5: Keyword and Date are scoped to the B2 source; an absent mode
            // (legacy payload, V6) passes null = all sources, unchanged.
            var candidates = await _fileIndexRepository.SearchCandidatesAsync(
                request.Bucket, request.Extension, request.Term, request.FromDate, request.ToDate, ct,
                request.Mode is null ? null : B2Source);

            if (request.Mode == KeywordMode)
            {
                // CAS-047 §9 V5: Keyword stores content matches only — no Filename-type rows.
                candidates = candidates.Where(c => c.MatchType == "Content").ToList();
            }

            // Content candidates of a type content search cannot read (images, binaries, email,
            // PGP, ...) are dropped here, so they are neither counted in the progress total nor
            // read from the mirror. Filename-type rows are unaffected.
            candidates = candidates
                .Where(c => c.MatchType != "Content"
                    || FileContentMatcher.IsSupportedExtension(Path.GetExtension(c.FileName)))
                .ToList();

            // CAS-054 §2.3: contiguous-watermark tracking relies on ascending file_index_id order
            // (the SP's ORDER BY); the sort is stable and a no-op on already-ordered input.
            candidates = candidates.OrderBy(c => c.FileIndexId).ToList();

            // CAS-054 §2.4: a resumed task skips every candidate at or below the inherited watermark
            // (their results were carried over by task_queue_restart_ins) but reports the FULL count.
            var resumeWatermark = task.ResumeWatermarkFileIndexId;
            var itemsTotal = candidates.Count;
            var skippedCount = 0;
            if (resumeWatermark is not null)
            {
                var remaining = candidates.Where(c => c.FileIndexId > resumeWatermark.Value).ToList();
                skippedCount = candidates.Count - remaining.Count;
                candidates = remaining;
            }

            var tracker = new ResumeWatermarkTracker(
                candidates.Select(c => c.FileIndexId).ToList(), resumeWatermark);
            var lastPersistedWatermark = resumeWatermark;
            var watermarkWriteInFlight = 0;
            var lastWatermarkWriteTimestamp = _timeProvider.GetTimestamp();

            // CAS-029 §3.1 step 3: the unit of work is the candidate row, not the file.
            var progress = _progressReporterFactory.Create(task.TaskQueueId);
            await progress.StartAsync(itemsTotal, ct);
            for (var skipped = 0; skipped < skippedCount; skipped++)
            {
                // CAS-054 §2.4: items_processed continues from the skipped count (the reporter is
                // reused as-is, §6; its throttle keeps this to at most a few writes).
                await progress.ItemCompletedAsync(ct);
            }

            _logger.LogInformation(
                "Task {TaskId} ({TaskType}) started: {ItemsTotal} item(s) to process, {SkippedCount} skipped (resumed from file_index_id {ResumeWatermark})",
                task.TaskQueueId, TaskType, itemsTotal, skippedCount, resumeWatermark);

            // CAS-056 §2.2: the watcher polls task_queue.is_cancel_requested; `workCt` (host token +
            // cancel token) cancels the READS - the candidate loop, content matching and B2 calls.
            // DISPOSITION writes (search_result insert, soft-delete + audit, indeterminate audit,
            // progress/watermark) keep the host `ct`, so a candidate is either fully dispositioned
            // or untouched (V8).
            using var cancelWatcher = new CancelRequestWatcher(
                task.TaskQueueId, _taskQueueRepository, _timeProvider,
                TimeSpan.FromSeconds(_configuration.GetValue<int?>("Worker:CancelPollIntervalSeconds") ?? 2),
                _logger);
            using var workCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cancelWatcher.Token);
            var workCt = workCts.Token;

            // CAS-054 §2.1/§2.2: marks one candidate dispositioned, counts it for progress, then
            // persists the contiguous watermark at most once per interval. A failed watermark write
            // is logged and swallowed - it must never fail the search (SC-10).
            async Task CompleteCandidateAsync(int position)
            {
                var watermark = tracker.MarkCompleted(position);
                await progress.ItemCompletedAsync(ct);

                if (watermark is null
                    || watermark == lastPersistedWatermark
                    || _timeProvider.GetElapsedTime(Interlocked.Read(ref lastWatermarkWriteTimestamp)) < WatermarkWriteInterval
                    || Interlocked.CompareExchange(ref watermarkWriteInFlight, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    var toWrite = tracker.Watermark!.Value;
                    await _taskQueueRepository.UpdateResumeWatermarkAsync(
                        task.TaskQueueId, ActingProcessIdentity, toWrite, ct);
                    lastPersistedWatermark = toWrite;
                    Interlocked.Exchange(ref lastWatermarkWriteTimestamp, _timeProvider.GetTimestamp());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Interval is not reset, so the next completion retries; no term/path is logged.
                    TryLogWarning(
                        ex, "Resume watermark write failed for task {TaskId}; continuing without it", task.TaskQueueId);
                }
                finally
                {
                    Volatile.Write(ref watermarkWriteInFlight, 0);
                }
            }

            // File reads + text extraction are the expensive, CPU/IO-bound part, so they get their own
            // degree of parallelism (default: one per core). The B2 cross-check keeps its existing,
            // separate limit — only candidates that already matched the term ever reach B2.
            var contentConcurrency = Extraction.ExtractorHostPool.ResolveConcurrency(_configuration);
            var b2Concurrency = _configuration.GetValue<int?>("B2:CrossCheckMaxConcurrency") ?? 8;
            using var b2Semaphore = new SemaphoreSlim(b2Concurrency, b2Concurrency);

            var contentTimeout = TimeSpan.FromSeconds(Math.Max(1,
                _configuration.GetValue<int?>("Worker:ContentMatchTimeoutSeconds") ?? 120));
            // Identical files (same SHA-1 + size) are matched once per search; see MatchContentWithDedupeAsync.
            var contentCache = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<ContentOutcome>>>();

            var filenameMatchCount = 0;
            var contentMatchCount = 0;
            var excludedCount = 0;
            var indeterminateCount = 0;

            // Bounded worker pool instead of one pending task per candidate (310k+ on a full scan).
            // CAS-056: a user cancel (cancel token fired, host token not) ends the loop early; in-flight
            // candidates drain first (Parallel.ForEachAsync awaits them) and the terminal write below
            // records Cancelled. Host shutdown (ct) still propagates untouched (§3.4: no status write).
            var cancelledByUser = false;
            try
            {
                await Parallel.ForEachAsync(
                    candidates.Select((candidate, position) => (candidate, position)),
                    new ParallelOptions { MaxDegreeOfParallelism = contentConcurrency, CancellationToken = workCt },
                    async (item, _) =>
                {
                    var (candidate, position) = item;
                    {
                        string? contentMatchSnippet = null;

                        if (candidate.MatchType == "Content" && !hasTerm)
                        {
                            // CAS-038 A2 (defence in depth; the SP returns no Content rows without a
                            // term): dropped without content matching or a B2 cross-check, but still
                            // counted once for progress (CAS-029 §3.1 step 4e).
                            await CompleteCandidateAsync(position);
                            return;
                        }

                        if (candidate.MatchType == "Content")
                        {
                            contentMatchSnippet = (await MatchContentWithDedupeAsync(candidate, request.Term, contentCache, contentTimeout, workCt)).Snippet;

                            if (contentMatchSnippet is null)
                            {
                                // CAS-012 §7 V10/V11: an unreadable mirror file or a file whose
                                // extracted text does not contain the term is dropped silently — no
                                // B2 cross-check call, no search_result insert, no CrossCheckExcluded
                                // audit row (that action is reserved for candidates B2 itself
                                // rejects, per CAS-006 §12 — a content non-match/unreadable-file was
                                // never a confirmed local match to begin with).
                                // CAS-029 §3.1 step 4e: a dropped candidate still counts once.
                                await CompleteCandidateAsync(position);
                                return;
                            }
                        }

                        CrossCheckOutcome outcome;
                        await b2Semaphore.WaitAsync(workCt);
                        try
                        {
                            outcome = await CrossCheckAsync(candidate, workCt);
                        }
                        finally
                        {
                            b2Semaphore.Release();
                        }

                        switch (outcome)
                        {
                            case CrossCheckOutcome.Confirmed:
                                {
                                    // CAS-038 A2: date-only Filename matches are 'Informational'.
                                    var matchStatus = candidate.MatchType == "Content"
                                        ? ContentMatchStatus
                                        : hasTerm ? FilenameMatchStatus : DateOnlyMatchStatus;

                                    await _searchResultRepository.InsertAsync(
                                        ActingProcessIdentity,
                                        ActingProcessIdentity,
                                        task.TaskQueueId,
                                        candidate.FileIndexId,
                                        candidate.MatchType,
                                        candidate.MatchType == "Content" ? contentMatchSnippet : null,
                                        matchStatus,
                                        ct);

                                    if (candidate.MatchType == "Content")
                                    {
                                        Interlocked.Increment(ref contentMatchCount);
                                    }
                                    else
                                    {
                                        Interlocked.Increment(ref filenameMatchCount);
                                    }

                                    break;
                                }
                            case CrossCheckOutcome.ConfirmedAbsent:
                                {
                                    // §6/§7 V9/§12: self-heal (soft-delete) and audit before this candidate is
                                    // ever allowed to become a search_result row — never insert first.
                                    await _fileIndexRepository.SoftDeleteAsync(candidate.FileIndexId, ActingProcessIdentity, ct);

                                    await _auditLogService.WriteAsync(new AuditLogEntry
                                    {
                                        CreatedBy = ActingProcessIdentity,
                                        UpdatedBy = ActingProcessIdentity,
                                        UserId = task.SubmittedByUserId,
                                        TabModule = TabModule,
                                        Action = AuditAction.CrossCheckExcluded,
                                        FilePath = candidate.RelativePath,
                                        TaskQueueId = task.TaskQueueId,
                                        Result = AuditResult.Success,
                                        ResultDetail = "File not confirmed on B2 during Keyword Search cross-check."
                                    }, ct);

                                    Interlocked.Increment(ref excludedCount);
                                    break;
                                }
                            case CrossCheckOutcome.Indeterminate:
                            default:
                                {
                                    // CAS-035 §2.2: never a search_result insert, never a self-heal — the
                                    // B2 dependency call itself failed after CAS-034's retries, so there is
                                    // no confirmed evidence of either presence or absence. Audited
                                    // distinctly from CrossCheckExcluded so an auditor can tell "B2
                                    // confirmed gone" apart from "B2 could not be reached/erred" (§11).
                                    await _auditLogService.WriteAsync(new AuditLogEntry
                                    {
                                        CreatedBy = ActingProcessIdentity,
                                        UpdatedBy = ActingProcessIdentity,
                                        UserId = task.SubmittedByUserId,
                                        TabModule = TabModule,
                                        Action = AuditAction.CrossCheckIndeterminate,
                                        FilePath = candidate.RelativePath,
                                        TaskQueueId = task.TaskQueueId,
                                        Result = AuditResult.Success,
                                        ResultDetail = "B2 cross-check could not be resolved after retries."
                                    }, ct);

                                    Interlocked.Increment(ref indeterminateCount);
                                    break;
                                }
                        }

                        // CAS-029 §3.1 step 4e: once per candidate, after its disposition is written.
                        await CompleteCandidateAsync(position);
                    }
                });
            }
            catch (OperationCanceledException) when (cancelWatcher.Token.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                cancelledByUser = true;
            }

            // CAS-054 §2.5: match counts of a resumed task include the carried-over rows; read them
            // back from search_result (carried-over + newly inserted). Excluded/Indeterminate stay
            // post-resume only (§14 Q2).
            if (resumeWatermark is not null)
            {
                filenameMatchCount = (await _searchResultRepository.ListAsync(task.TaskQueueId, "Filename", 1, 1, ct)).TotalCount;
                contentMatchCount = (await _searchResultRepository.ListAsync(task.TaskQueueId, "Content", 1, 1, ct)).TotalCount;
            }

            var summary = new KeywordSearchResultSummaryDto
            {
                FilenameMatchCount = filenameMatchCount,
                ContentMatchCount = contentMatchCount,
                ExcludedCount = excludedCount,
                IndeterminateCount = indeterminateCount,
                ResumedFromTaskQueueId = resumeWatermark is null ? null : task.RestartedFromTaskQueueId
            };

            if (cancelledByUser)
            {
                await WriteCancelledAsync(task, progress, summary, ct);
                return;
            }

            // CAS-029 §3.6: result rows and audits (awaited above) → final progress → Completed.
            await progress.CompleteAsync(ct);

            await _taskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, ActingProcessIdentity, "Completed", JsonSerializer.Serialize(summary), ct);

            _logger.LogInformation(
                "KeywordSearch task {TaskId} completed: {FilenameMatchCount} filename / {ContentMatchCount} content matches, {ExcludedCount} excluded, {IndeterminateCount} indeterminate",
                task.TaskQueueId, filenameMatchCount, contentMatchCount, excludedCount, indeterminateCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never log the raw term (§11) or any exception payload that could carry B2 credential
            // material — only a short, fixed error summary.
            var errorSummary = ex is B2ApiException ? "B2 cross-check call failed." : "Unhandled error during Keyword Search processing.";

            var failureSummary = new KeywordSearchResultSummaryDto { FailureReason = errorSummary };

            await _taskQueueRepository.UpdateStatusAsync(
                task.TaskQueueId, ActingProcessIdentity, "Failed", JsonSerializer.Serialize(failureSummary), ct);

            _logger.LogWarning(ex, "KeywordSearch task {TaskId} failed: {ErrorSummary}", task.TaskQueueId, errorSummary);
        }
    }

    /// <summary>
    /// CAS-056 §2.3 terminal write for a user-cancelled task, in order: (a) <c>TaskCancelled</c>
    /// audit, (b) final progress, (c) <c>Cancelled</c> status. If the audit write throws, no status is
    /// written and the row stays <c>Running</c> for the next startup sweep (FR-23: never a visible
    /// <c>Cancelled</c> without its audit row). Logs ids and integers only.
    /// </summary>
    private async Task WriteCancelledAsync(
        ClaimedTaskRow task, TaskProgressReporter progress, KeywordSearchResultSummaryDto summary, CancellationToken ct)
    {
        try
        {
            await _auditLogService.WriteAsync(new AuditLogEntry
            {
                CreatedBy = ActingProcessIdentity,
                UpdatedBy = ActingProcessIdentity,
                UserId = task.SubmittedByUserId,
                TabModule = TabModule,
                Action = AuditAction.TaskCancelled,
                TaskQueueId = task.TaskQueueId,
                Result = AuditResult.Success,
                ResultDetail = $"Task {task.TaskQueueId} cancelled; "
                    + $"{summary.FilenameMatchCount} filename / {summary.ContentMatchCount} content match(es) retained."
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No status write: the row stays Running and the startup sweep resolves it.
            _logger.LogError(ex, "Cancel audit write failed for task {TaskId}; status left Running", task.TaskQueueId);
            return;
        }

        await progress.CompleteAsync(ct);

        await _taskQueueRepository.UpdateStatusAsync(
            task.TaskQueueId, ActingProcessIdentity, "Cancelled", JsonSerializer.Serialize(summary), ct);

        _logger.LogInformation(
            "KeywordSearch task {TaskId} cancelled: {FilenameMatchCount} filename / {ContentMatchCount} content matches retained, {ExcludedCount} excluded, {IndeterminateCount} indeterminate",
            task.TaskQueueId, summary.FilenameMatchCount, summary.ContentMatchCount, summary.ExcludedCount, summary.IndeterminateCount);
    }

    /// <summary>
    /// CAS-047 §2/§7 (Filename mode): one set-based insert of every B2 <c>file_name</c> match via
    /// <c>dbo.search_result_filename_ins</c> — no B2 call, no mirror read, no per-row round trip.
    /// The term is never logged (§11).
    /// </summary>
    private async Task ProcessFilenameModeAsync(ClaimedTaskRow task, KeywordSearchRequestDto request, CancellationToken ct)
    {
        var progress = _progressReporterFactory.Create(task.TaskQueueId);
        await progress.StartAsync(1, ct);
        _logger.LogInformation("Task {TaskId} ({TaskType}) started in Filename mode", task.TaskQueueId, TaskType);

        var inserted = await _searchResultRepository.InsertFilenameMatchesAsync(
            task.TaskQueueId, request.Bucket, request.Extension, request.Term.Trim(), ActingProcessIdentity, ct);

        await progress.ItemCompletedAsync(ct);
        await progress.CompleteAsync(ct);

        var summary = new KeywordSearchResultSummaryDto { FilenameMatchCount = inserted };

        await _taskQueueRepository.UpdateStatusAsync(
            task.TaskQueueId, ActingProcessIdentity, "Completed", JsonSerializer.Serialize(summary), ct);

        _logger.LogInformation(
            "KeywordSearch task {TaskId} completed in Filename mode: {FilenameMatchCount} filename matches",
            task.TaskQueueId, inserted);
    }

    /// <summary>Plain-text files at least this large are scanned as a stream, not read whole.</summary>
    private const long StreamingThresholdBytes = 16L * 1024 * 1024;

    /// <summary>
    /// <see cref="Snippet"/>: bounded match snippet or <c>null</c>. <see cref="Conclusive"/>: the file was
    /// actually read and matched (or timed out); <c>false</c> = unreadable/failed, so an identical
    /// copy elsewhere must be tried on its own rather than inheriting this result.
    /// </summary>
    private readonly record struct ContentOutcome(string? Snippet, bool Conclusive);

    /// <summary>
    /// Matches a <c>Content</c> candidate once per distinct file: candidates sharing a SHA-1 and size
    /// are byte-identical, so the first one does the read + extraction and the rest reuse its result.
    /// An inconclusive result (unreadable file) is never shared.
    /// </summary>
    private async Task<ContentOutcome> MatchContentWithDedupeAsync(
        FileIndexCandidateRow candidate,
        string term,
        System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<ContentOutcome>>> cache,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(candidate.Sha1Hash))
        {
            return await MatchContentAsync(candidate, term, timeout, ct);
        }

        var key = $"{candidate.Sha1Hash}:{candidate.FileSizeBytes}";
        var entry = cache.GetOrAdd(key, _ => new Lazy<Task<ContentOutcome>>(() => MatchContentAsync(candidate, term, timeout, ct)));
        var outcome = await entry.Value;
        if (outcome.Conclusive)
        {
            return outcome;
        }

        cache.TryRemove(new KeyValuePair<string, Lazy<Task<ContentOutcome>>>(key, entry));
        return await MatchContentAsync(candidate, term, timeout, ct);
    }

    /// <summary>
    /// CAS-012 §2/§7 V10/V11: reads a <c>Content</c> candidate's physical mirror file and checks
    /// whether <paramref name="term"/> is present in its extracted text. Returns a null snippet on a
    /// non-match, an unreadable file, an extraction failure or a per-file timeout — the caller treats
    /// all of those as a silent skip, not a task failure. Large plain-text files are scanned as a
    /// stream; everything else is read whole and extracted on the thread pool under a time limit.
    /// Never logs file content, extracted text, or the search term (§11); the only log lines are
    /// warnings for an exception or timeout, carrying the file name and path.
    /// </summary>
    private async Task<ContentOutcome> MatchContentAsync(FileIndexCandidateRow candidate, string term, TimeSpan timeout, CancellationToken ct)
    {
        // Content search covers the supported document and plain-text types only; skip every other
        // type without reading it (no mirror read, no extraction, no B2 cross-check).
        var fileExtension = Path.GetExtension(candidate.FileName).TrimStart('.');
        if (!FileContentMatcher.IsSupportedExtension(fileExtension))
        {
            return new ContentOutcome(null, true);
        }

        // Scope so any warning raised below (including from FileContentMatcher) names the file.
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["FileName"] = candidate.FileName,
            ["RelativePath"] = candidate.RelativePath,
            ["FileIndexId"] = candidate.FileIndexId
        });

        // Debug level: silent under the default Warning filter. Enable for this class in the Serilog
        // overrides to find which file(s) were in flight when the process died without any exception.
        _logger.LogDebug(
            "Content match: starting {FileName} at {RelativePath} ({FileSizeBytes} bytes)",
            candidate.FileName, candidate.RelativePath, candidate.FileSizeBytes);

        try
        {
            string? snippet;

            if (candidate.FileSizeBytes >= StreamingThresholdBytes && FileContentMatcher.IsStreamableExtension(fileExtension))
            {
                using var handle = await _mirrorFileReader.OpenReadAsync(new FileIndexRecord
                {
                    FileIndexId = candidate.FileIndexId,
                    Source = candidate.Source,
                    BucketName = candidate.BucketName,
                    RelativePath = candidate.RelativePath,
                    FileName = candidate.FileName,
                    FileExtension = fileExtension,
                    FileSizeBytes = candidate.FileSizeBytes,
                    ModifiedAt = candidate.ModifiedAt,
                    IsActive = true
                }, ct);

                if (handle.Stream is null)
                {
                    TryLogWarning(
                        null,
                        "Content match: could not open {FileName} at {RelativePath}: {Outcome}; skipping.",
                        candidate.FileName, candidate.RelativePath, handle.Outcome);
                    return new ContentOutcome(null, false);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                snippet = await _fileContentMatcher.TryMatchStreamAsync(handle.Stream, fileExtension, term, timeoutCts.Token);
            }
            else
            {
                // Document parsers hold the whole file plus several times its size in memory; a
                // file above the cap is skipped unread rather than risking the server's memory.
                var maxBytes = (long)Math.Max(1, _configuration.GetValue<int?>("Worker:ContentMatchMaxFileMb") ?? 100) * 1024 * 1024;
                if (candidate.FileSizeBytes > maxBytes)
                {
                    TryLogWarning(
                        null,
                        "Content match: {FileName} at {RelativePath} is {FileSizeBytes} bytes, above the {MaxMb} MB limit; skipped.",
                        candidate.FileName, candidate.RelativePath, candidate.FileSizeBytes, maxBytes / (1024 * 1024));
                    return new ContentOutcome(null, true);
                }

                var fileBytes = await _mirrorFileReader.ReadAllBytesAsync(
                    candidate.Source, candidate.BucketName, candidate.RelativePath, ct);

                // Extraction is synchronous CPU work: run it on the thread pool so it parallelises,
                // and bound it so one pathological file cannot hold a worker slot indefinitely. A
                // timed-out extraction cannot be aborted; its thread is abandoned and finishes on its own.
                // The matcher enforces the real per-file limit itself (the out-of-process one kills its
                // child); this wait is only a generous backstop for any other matcher implementation.
                snippet = await Task.Run(() => _fileContentMatcher.TryMatchAsync(fileBytes, fileExtension, term, ct), ct)
                    .WaitAsync(timeout + timeout + TimeSpan.FromSeconds(30), ct);
            }

            return new ContentOutcome(snippet, true);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            TryLogWarning(
                null,
                "Content match: {FileName} at {RelativePath} exceeded the {TimeoutSeconds}s time limit; skipped.",
                candidate.FileName, candidate.RelativePath, (int)timeout.TotalSeconds);
            return new ContentOutcome(null, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryLogWarning(
                ex,
                "Content match: failed to read or process {FileName} at {RelativePath} (file_index_id {FileIndexId}); skipped.",
                candidate.FileName, candidate.RelativePath, candidate.FileIndexId);
            return new ContentOutcome(null, false);
        }
    }

    /// <summary>
    /// A logging failure (a sink or provider that throws, e.g. the Windows Event Log out of resources)
    /// must never fail a search or propagate out of the per-file handler.
    /// </summary>
    private void TryLogWarning(Exception? exception, string message, params object?[] args)
    {
        try
        {
            _logger.LogWarning(exception, message, args);
        }
        catch (Exception)
        {
            // intentionally ignored
        }
    }

    /// <summary>
    /// CAS-035 §2.1: outcome of a single candidate's B2 cross-check call. <see cref="Confirmed"/>
    /// and <see cref="ConfirmedAbsent"/> are the pre-existing two outcomes; <see cref="Indeterminate"/>
    /// (new) isolates a transient B2/network failure that survives CAS-034's retries — the
    /// candidate is omitted from results but never treated as confirmed absent.
    /// </summary>
    private enum CrossCheckOutcome
    {
        Confirmed,
        ConfirmedAbsent,
        Indeterminate
    }

    private async Task<CrossCheckOutcome> CrossCheckAsync(FileIndexCandidateRow candidate, CancellationToken ct)
    {
        try
        {
            var result = await _b2Client.ListFileNamesAsync(candidate.RelativePath, 1, ct);
            var first = result.Files.FirstOrDefault();
            var confirmed = first is not null
                && string.Equals(first.FileName, candidate.RelativePath, StringComparison.Ordinal)
                && first.Action == "upload";
            return confirmed ? CrossCheckOutcome.Confirmed : CrossCheckOutcome.ConfirmedAbsent;
        }
        catch (B2NotFoundException)
        {
            return CrossCheckOutcome.ConfirmedAbsent;
        }
        catch (B2ApiException ex)
        {
            // Never pass the raw exception object to the logger (matches B2Client.cs's CAS-034
            // convention) — only a status code, so no logging-sink configuration can ever surface
            // a B2 error-response body.
            _logger.LogWarning(
                "B2 cross-check indeterminate for file_index_id {FileIndexId}: B2 returned status {StatusCode} after retries; candidate omitted from this search's results, not marked absent.",
                candidate.FileIndexId, ex.StatusCode);
            return CrossCheckOutcome.Indeterminate;
        }
        catch (HttpRequestException)
        {
            _logger.LogWarning(
                "B2 cross-check indeterminate for file_index_id {FileIndexId}: network failure reaching B2 after retries; candidate omitted from this search's results, not marked absent.",
                candidate.FileIndexId);
            return CrossCheckOutcome.Indeterminate;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // An internal timeout inside B2Client (CAS-034 §2's retry policy distinguishes this from
            // the caller's own ct firing) — not this task's own cancellation, so it must not
            // propagate as one. Treated the same as any other exhausted-retry failure.
            _logger.LogWarning(
                "B2 cross-check indeterminate for file_index_id {FileIndexId}: internal timeout after retries; candidate omitted from this search's results, not marked absent.",
                candidate.FileIndexId);
            return CrossCheckOutcome.Indeterminate;
        }
    }

    private static KeywordSearchRequestDto DeserializeRequest(string? requestParameters)
    {
        if (string.IsNullOrWhiteSpace(requestParameters))
        {
            throw new InvalidOperationException("task_queue.request_parameters was empty for a KeywordSearch task.");
        }

        return JsonSerializer.Deserialize<KeywordSearchRequestDto>(requestParameters)
            ?? throw new InvalidOperationException("task_queue.request_parameters could not be deserialized as KeywordSearchRequestDto.");
    }
}
