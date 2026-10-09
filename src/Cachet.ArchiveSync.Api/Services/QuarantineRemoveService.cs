using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IQuarantineRemoveService"/> implementation of CAS-014 §2.2/§8 — "Remove Item"
/// (BRD §12.3 items 3, 7). Each requested <c>fileId</c> (a <c>quarantine_entry_id</c>) is handled
/// independently: resolve → hard-delete the catalog row via
/// <c>dbo.quarantine_entry_del</c> → write an audit row, all before that file's result is recorded
/// (§11). No B2 call, no <c>dbo.file_index</c> mutation, ever (§9 V5) — this constructor
/// deliberately depends only on <see cref="IQuarantineEntryRepository"/> and
/// <see cref="IAuditLogService"/>, unlike <see cref="IQuarantinePermanentDeleteService"/>.
/// </summary>
public sealed class QuarantineRemoveService : IQuarantineRemoveService
{
    private const string ActingProcessIdentity = "api-svc";
    private const string TabModule = "Quarantine";

    private const string ResultSuccess = "Success";
    private const string ResultFailed = "Failed";

    private readonly IQuarantineEntryRepository _quarantineEntryRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<QuarantineRemoveService> _logger;

    public QuarantineRemoveService(
        IQuarantineEntryRepository quarantineEntryRepository,
        IAuditLogService auditLogService,
        ILogger<QuarantineRemoveService> logger)
    {
        _quarantineEntryRepository = quarantineEntryRepository;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<QuarantineRemoveResult> RemoveAsync(QuarantineRemoveRequest request, int actingUserId, CancellationToken ct)
    {
        // §9 V1: fileIds must be non-empty.
        if (request.FileIds.Count == 0)
        {
            return new QuarantineRemoveResult { Outcome = QuarantineRemoveOutcome.ValidationFailed };
        }

        var results = new List<QuarantineRemoveResultItem>();
        var notFoundCount = 0;
        var succeededCount = 0;
        var failedCount = 0;

        foreach (var fileId in request.FileIds)
        {
            // §9 V2: resolve the quarantine_entry_id to an Active row. No row => per-file
            // file-not-found, no hard-delete or audit attempted for this file.
            var entry = await _quarantineEntryRepository.GetActiveForDeleteAsync(fileId, ct);
            if (entry is null)
            {
                results.Add(new QuarantineRemoveResultItem
                {
                    FileId = fileId,
                    Result = ResultFailed,
                    Reason = "file-not-found"
                });
                notFoundCount++;
                failedCount++;
                continue;
            }

            // §7/§9 V3: hard-delete the catalog row. A false return means the row was no longer
            // Active at delete time (concurrent request) — reported the same way as not-found.
            var removed = await _quarantineEntryRepository.HardDeleteActiveAsync(fileId, ct);
            if (!removed)
            {
                _logger.LogWarning(
                    "Remove Item failed for quarantine entry {QuarantineEntryId}: row was no longer Active at delete time (concurrent update).",
                    fileId);

                results.Add(new QuarantineRemoveResultItem
                {
                    FileId = fileId,
                    Result = ResultFailed,
                    Reason = "file-not-found"
                });
                failedCount++;
                continue;
            }

            // §11/§9 V4: audited before this file's result is recorded.
            await _auditLogService.WriteAsync(new AuditLogEntry
            {
                CreatedBy = ActingProcessIdentity,
                UpdatedBy = ActingProcessIdentity,
                UserId = actingUserId,
                TabModule = TabModule,
                Action = AuditAction.QuarantineRemoved,
                FilePath = entry.Path,
                Result = AuditResult.Success
            }, ct);

            results.Add(new QuarantineRemoveResultItem { FileId = fileId, Result = ResultSuccess });
            succeededCount++;
        }

        _logger.LogInformation(
            "Remove Item batch by user {UserId}: {SucceededCount} succeeded, {FailedCount} failed",
            actingUserId, succeededCount, failedCount);

        // §8/§9 V2: whole-request 404 only if every requested file failed specifically because it
        // did not resolve to an Active quarantine_entry row (not merely "all failed").
        if (notFoundCount == request.FileIds.Count)
        {
            return new QuarantineRemoveResult { Outcome = QuarantineRemoveOutcome.AllFilesNotFound };
        }

        return new QuarantineRemoveResult
        {
            Outcome = QuarantineRemoveOutcome.Completed,
            Response = new QuarantineRemoveResponse { Results = results }
        };
    }
}
