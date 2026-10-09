using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Services;

/// <summary>
/// <see cref="IAuditLogListService"/> implementation of CAS-009 §6 — <c>GET /audit-log</c>
/// (FR-23/FR-24). A plain read; not audited (§12 — listing the audit log is not itself an
/// auditable action, matching CAS-008's <c>GET /quarantine</c> precedent).
/// </summary>
public sealed class AuditLogListService : IAuditLogListService
{
    private static readonly HashSet<string> AllowedTabs = new(StringComparer.Ordinal)
    {
        "Auth", "Keyword Search", "O-Drive Sync", "Quarantine", "Task Manager", "Audit Log", "Reports"
    };

    private const int MinPageSize = 1;
    private const int MaxPageSize = 200;

    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<AuditLogListService> _logger;

    public AuditLogListService(IAuditLogRepository auditLogRepository, ILogger<AuditLogListService> logger)
    {
        _auditLogRepository = auditLogRepository;
        _logger = logger;
    }

    public async Task<AuditLogListResult> ListAsync(
        string? tab,
        DateTimeOffset? dateFrom,
        DateTimeOffset? dateTo,
        int page,
        int pageSize,
        int actingUserId,
        CancellationToken ct)
    {
        var validationError = Validate(tab, dateFrom, dateTo, page, pageSize);
        if (validationError is not null)
        {
            return new AuditLogListResult { Outcome = AuditLogListOutcome.ValidationFailed, ValidationError = validationError };
        }

        var tabFilter = string.IsNullOrEmpty(tab) || tab == "All" ? null : tab;

        var (rows, totalCount) = await _auditLogRepository.ListAsync(tabFilter, dateFrom, dateTo, page, pageSize, ct);

        var items = rows.Select(row => new AuditLogListItem
        {
            User = row.Username,
            Tab = row.TabModule,
            Action = row.Action,
            FilePath = row.FilePath,
            Timestamp = row.CreatedAt,
            Result = row.Result,
            ResultDetail = row.ResultDetail
        }).ToList();

        _logger.LogInformation(
            "Audit log list requested by user {UserId}: tab={Tab}, page={Page}, pageSize={PageSize}, returned={ReturnedCount}, totalCount={TotalCount}",
            actingUserId, tab, page, pageSize, items.Count, totalCount);

        return new AuditLogListResult
        {
            Outcome = AuditLogListOutcome.Found,
            Response = new PagedResult<AuditLogListItem>
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount
            }
        };
    }

    private static string? Validate(string? tab, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, int page, int pageSize)
    {
        if (!string.IsNullOrEmpty(tab) && tab != "All" && !AllowedTabs.Contains(tab))
        {
            return "'tab' must be one of Auth, Keyword Search, O-Drive Sync, Quarantine, Task Manager, Audit Log, Reports, or All.";
        }

        if (dateFrom is not null && dateTo is not null && dateTo < dateFrom)
        {
            return "'dateTo' must not be earlier than 'dateFrom'.";
        }

        if (page < 1)
        {
            return "'page' must be >= 1.";
        }

        if (pageSize < MinPageSize || pageSize > MaxPageSize)
        {
            return $"'pageSize' must be between {MinPageSize} and {MaxPageSize}.";
        }

        return null;
    }
}
