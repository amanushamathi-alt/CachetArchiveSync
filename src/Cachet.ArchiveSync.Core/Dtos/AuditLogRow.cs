namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Read-side projection returned by <c>dbo.audit_log_lst</c> (CAS-009 §5) — one <c>dbo.audit_log</c>
/// row joined to <c>dbo.users</c> for the display <c>username</c> (§12: never
/// <c>b2_application_key</c>/<c>b2_application_key_id</c>). Public in <c>Core</c> (not a
/// repository-private row type), matching <see cref="SearchResultRow"/>'s precedent, because the
/// service layer needs it to project into <see cref="AuditLogListItem"/>.
/// </summary>
public sealed record AuditLogRow
{
    public required int AuditLogId { get; init; }

    public required string Username { get; init; }

    public required string TabModule { get; init; }

    public required string Action { get; init; }

    public string? FilePath { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string Result { get; init; }

    public string? ResultDetail { get; init; }
}
