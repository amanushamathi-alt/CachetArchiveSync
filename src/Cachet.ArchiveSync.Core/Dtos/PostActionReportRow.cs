namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// One row of the Post-Action Outcome Report (CAS-010 §3/§6.2) — one <c>dbo.audit_log</c> row
/// (via <c>IAuditLogRepository.ListAsync</c>, CAS-009) that matched the §6.2 state-changing
/// <c>AuditAction</c> allow-list. <see cref="FileName"/>/<see cref="FilePath"/> are null when the
/// underlying audit row's <c>file_path</c> is null (e.g. a task-level action with no single
/// associated file, such as <c>KeywordSearchSubmitted</c>).
/// </summary>
public sealed record PostActionReportRow
{
    public string? FileName { get; init; }

    public string? FilePath { get; init; }

    public required string ActionTaken { get; init; }

    public required string SourceTab { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string Result { get; init; }
}
