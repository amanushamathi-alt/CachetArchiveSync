using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Wraps the <c>dbo.audit_log_ins</c> / <c>dbo.audit_log_lst</c> stored procedure calls
/// (CAS-005 §3, CAS-009 §5).
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>Inserts one <c>dbo.audit_log</c> row and returns the new <c>audit_log_id</c>.</summary>
    Task<int> InsertAsync(AuditLogEntry entry, CancellationToken ct);

    /// <summary>
    /// Paginated, filtered read of <c>dbo.audit_log</c> via <c>dbo.audit_log_lst</c> (CAS-009 §5/§6).
    /// <paramref name="tabModule"/> null/omitted means no tab filter (the stored procedure's own
    /// 'All' sentinel normalization). <paramref name="dateFrom"/>/<paramref name="dateTo"/> are an
    /// inclusive range against <c>created_at</c>.
    /// </summary>
    Task<(IReadOnlyList<AuditLogRow> Items, int TotalCount)> ListAsync(
        string? tabModule, DateTimeOffset? dateFrom, DateTimeOffset? dateTo, int page, int pageSize, CancellationToken ct);
}
