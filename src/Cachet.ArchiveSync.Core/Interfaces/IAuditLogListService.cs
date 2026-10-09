using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of <see cref="IAuditLogListService.ListAsync"/> (CAS-009 §6/§7).</summary>
public enum AuditLogListOutcome
{
    Found,

    /// <summary><c>tab</c>/<c>dateFrom</c>/<c>dateTo</c>/<c>page</c>/<c>pageSize</c> failed
    /// §7 V1–V4 — <c>422 validation-failed</c>.</summary>
    ValidationFailed
}

/// <summary>Result of one <see cref="IAuditLogListService.ListAsync"/> call.</summary>
public sealed record AuditLogListResult
{
    public required AuditLogListOutcome Outcome { get; init; }

    public PagedResult<AuditLogListItem>? Response { get; init; }

    /// <summary>Populated only when <see cref="Outcome"/> is <see cref="AuditLogListOutcome.ValidationFailed"/>.</summary>
    public string? ValidationError { get; init; }
}

/// <summary>
/// <c>GET /audit-log</c> read path (CAS-009 §6, FR-23/FR-24). Not audited — read-only, matching
/// <see cref="IQuarantineListService"/>'s precedent (§12). The write path
/// (<see cref="IAuditLogService"/>) is untouched by this interface.
/// </summary>
public interface IAuditLogListService
{
    Task<AuditLogListResult> ListAsync(
        string? tab,
        DateTimeOffset? dateFrom,
        DateTimeOffset? dateTo,
        int page,
        int pageSize,
        int actingUserId,
        CancellationToken ct);
}
