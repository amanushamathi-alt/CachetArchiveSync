namespace Cachet.ArchiveSync.Core.Enums;

/// <summary>
/// Audit-trail outcome catalog (FR-23). Member names are PascalCase, no punctuation; the value
/// written to <c>dbo.audit_log.result</c> is the literal result of <see cref="Enum.ToString()"/>
/// (CAS-005 §15, resolved 2026-09-22).
/// </summary>
public enum AuditResult
{
    Success,
    Failure
}
