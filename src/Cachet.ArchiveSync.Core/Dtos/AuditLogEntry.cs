using Cachet.ArchiveSync.Core.Enums;

namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Parameter object for a single <c>dbo.audit_log</c> row write (CAS-005 §5/§7). Carries exactly
/// the fields <c>dbo.audit_log_ins</c> takes, minus the identity/db-defaulted columns
/// (<c>audit_log_id</c>, <c>created_at</c>, <c>updated_at</c> — SC-2).
/// </summary>
public sealed record AuditLogEntry
{
    /// <summary>Acting process identity (e.g. "api-svc", "worker-svc" — CAS-005 §15).</summary>
    public required string CreatedBy { get; init; }

    /// <summary>Acting process identity (e.g. "api-svc", "worker-svc" — CAS-005 §15).</summary>
    public required string UpdatedBy { get; init; }

    /// <summary>Business-meaningful acting/submitting user, regardless of writing process.</summary>
    public required int UserId { get; init; }

    /// <summary>Must be one of the CK_audit_log_tab_module fixed values (CAS-005 §7 V1).</summary>
    public required string TabModule { get; init; }

    public required AuditAction Action { get; init; }

    public string? FilePath { get; init; }

    public int? TaskQueueId { get; init; }

    public required AuditResult Result { get; init; }

    public string? ResultDetail { get; init; }
}
