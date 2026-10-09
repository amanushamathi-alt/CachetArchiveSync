using System.Globalization;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IQuarantineService"/> implementation of CAS-006 §6.3 — add-only Quarantine path
/// (FR-09). Each requested <c>fileId</c> is resolved to its task-scoped <c>search_result</c> row
/// first (§7 V7 — a missing resolution fails the whole request), then each resolved file is
/// inserted independently (§7 V8 — an already-active file is skipped, not fatal to the batch).
/// </summary>
public sealed class QuarantineService : IQuarantineService
{
    private const string TabModule = "Keyword Search";

    private readonly ITaskQueueRepository _taskQueueRepository;
    private readonly ISearchResultRepository _searchResultRepository;
    private readonly IQuarantineEntryRepository _quarantineEntryRepository;
    private readonly IAuditLogService _auditLogService;
    private readonly ILogger<QuarantineService> _logger;

    public QuarantineService(
        ITaskQueueRepository taskQueueRepository,
        ISearchResultRepository searchResultRepository,
        IQuarantineEntryRepository quarantineEntryRepository,
        IAuditLogService auditLogService,
        ILogger<QuarantineService> logger)
    {
        _taskQueueRepository = taskQueueRepository;
        _searchResultRepository = searchResultRepository;
        _quarantineEntryRepository = quarantineEntryRepository;
        _auditLogService = auditLogService;
        _logger = logger;
    }

    public async Task<QuarantineAddResult> AddAsync(QuarantineAddRequest request, int actingUserId, CancellationToken ct)
    {
        if (request.FileIds.Count == 0)
        {
            return new QuarantineAddResult { Outcome = QuarantineAddOutcome.ValidationFailed };
        }

        var task = await _taskQueueRepository.GetAsync(request.TaskId, ct);
        if (task is null)
        {
            return new QuarantineAddResult { Outcome = QuarantineAddOutcome.TaskNotFound };
        }

        // §7 V7: every fileId must resolve to a search_result row scoped to taskId, checked
        // up front — a single unresolved fileId fails the whole request (distinct from V8's
        // per-file partial-success handling below).
        var resolved = new List<SearchResultRow>();
        foreach (var fileId in request.FileIds)
        {
            var row = await _searchResultRepository.GetAsync(fileId, ct);
            if (row is null || row.TaskQueueId != request.TaskId)
            {
                return new QuarantineAddResult { Outcome = QuarantineAddOutcome.FileNotFound };
            }

            resolved.Add(row);
        }

        // Business-actor identity for quarantine_entry.created_by/updated_by (schema design note:
        // this column doubles as "who quarantined this", unlike audit_log's created_by/updated_by
        // which hold the *process* identity — "api-svc" — with user attribution carried
        // separately in audit_log.user_id). No username-by-id lookup exists in this codebase
        // (IUserRepository only looks up by username), so the numeric user id is used verbatim as
        // the NVARCHAR(50) actor identity — a documented, best-effort choice (flagged for
        // reviewer sign-off), consistent with this project's precedent of flagging such stopgaps
        // rather than blocking (e.g. CAS-002's LoginResponse.DisplayName).
        var actingUserIdentity = actingUserId.ToString(CultureInfo.InvariantCulture);

        var items = new List<QuarantineAddResponseItem>();
        var warnings = new List<string>();
        var quarantinedOn = DateTimeOffset.UtcNow;

        foreach (var row in resolved)
        {
            var insertResult = await _quarantineEntryRepository.InsertAsync(
                actingUserIdentity, actingUserIdentity, row.SearchResultId, ct);

            switch (insertResult.Outcome)
            {
                case QuarantineInsertOutcome.Added:
                    items.Add(new QuarantineAddResponseItem
                    {
                        FileId = insertResult.QuarantineEntryId!.Value,
                        Name = row.FileName,
                        Path = row.RelativePath,
                        QuarantinedOn = quarantinedOn,
                        Status = row.MatchStatus
                    });
                    break;
                case QuarantineInsertOutcome.AlreadyActive:
                    warnings.Add($"fileId {row.SearchResultId} is already actively quarantined.");
                    break;
                case QuarantineInsertOutcome.SearchResultNotFound:
                default:
                    // Should not happen — already validated via GetAsync above in this same
                    // request. Surfacing loudly rather than silently dropping the file.
                    throw new InvalidOperationException(
                        $"search_result {row.SearchResultId} resolved during validation but was rejected by quarantine_entry_ins.");
            }
        }

        if (items.Count == 0)
        {
            // §6.3: whole-request 409 only if every requested file fails (all AlreadyActive).
            return new QuarantineAddResult { Outcome = QuarantineAddOutcome.AllAlreadyQuarantined };
        }

        // §12: audited before the response — one row per added file (FR-23's field list is
        // satisfied either way; per-file rows keep file_path granularity).
        foreach (var item in items)
        {
            await _auditLogService.WriteAsync(new AuditLogEntry
            {
                CreatedBy = "api-svc",
                UpdatedBy = "api-svc",
                UserId = actingUserId,
                TabModule = TabModule,
                Action = AuditAction.QuarantineAdd,
                FilePath = item.Path,
                TaskQueueId = request.TaskId,
                Result = AuditResult.Success
            }, ct);
        }

        _logger.LogInformation(
            "Quarantine add: {AddedCount} added, {WarningCount} skipped for task {TaskId}",
            items.Count, warnings.Count, request.TaskId);

        return new QuarantineAddResult
        {
            Outcome = QuarantineAddOutcome.Added,
            Response = new QuarantineAddResponse
            {
                Items = items,
                Warnings = warnings.Count > 0 ? warnings : null
            }
        };
    }
}
