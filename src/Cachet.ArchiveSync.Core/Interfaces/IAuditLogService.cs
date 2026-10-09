using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Shared audit-log write call surface (FR-23). Callers await this before returning their own
/// response to genuinely satisfy the audit-before-response invariant (CAS-005 §12).
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// Validates and writes one audit row. Throws <see cref="ArgumentException"/> if
    /// <see cref="AuditLogEntry.TabModule"/> is not a recognized value (CAS-005 §7 V1).
    /// </summary>
    Task<int> WriteAsync(AuditLogEntry entry, CancellationToken ct);
}
