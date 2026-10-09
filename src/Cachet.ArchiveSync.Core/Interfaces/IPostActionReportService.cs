using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Result of one <see cref="IPostActionReportService.GenerateAsync"/> call (CAS-015 §2/§8) — on
/// <see cref="ReportGenerationOutcome.Generated"/>, carries the rendered file's bytes/content-type/
/// download filename directly (no persisted <c>reportId</c>/download URL).
/// </summary>
public sealed record PostActionReportResult
{
    public required ReportGenerationOutcome Outcome { get; init; }

    public string? ValidationError { get; init; }

    public byte[]? FileBytes { get; init; }

    public string? ContentType { get; init; }

    public string? DownloadFileName { get; init; }
}

/// <summary>Outcome of one <see cref="IPostActionReportService.PreviewAsync"/> call (CAS-038 §6/§7
/// V5). Unlike <see cref="ReportGenerationOutcome"/>, there is no <c>ValidationFailed</c> member —
/// the preview endpoint takes no <c>format</c> field to validate.</summary>
public enum ReportPreviewOutcome
{
    Generated,

    /// <summary>The underlying audit-log read hit the internal 100,000-row cap exactly (CAS-038
    /// §7 V5, same ambiguous-truncation rule as <see cref="ReportGenerationOutcome.GenerationFailed"/>)
    /// — mapped by the controller to <c>500 report-generation-failed</c>.</summary>
    GenerationFailed
}

/// <summary>Result of one <see cref="IPostActionReportService.PreviewAsync"/> call (CAS-038 §6).</summary>
public sealed record PostActionReportPreviewResult
{
    public required ReportPreviewOutcome Outcome { get; init; }

    /// <summary>Populated only when <see cref="Outcome"/> is <see cref="ReportPreviewOutcome.Generated"/>.</summary>
    public PostActionReportPreview? Response { get; init; }
}

/// <summary>
/// <c>POST /reports/post-action</c> (CAS-010 §3/§6.2, FR-26; CAS-015 §2/§8) — sources rows from
/// <c>IAuditLogRepository.ListAsync</c> (CAS-009), filtered to the §6.2 state-changing
/// <c>AuditAction</c> allow-list, renders the single requested format to bytes in memory, writes an
/// audit row, and returns the rendered file directly — nothing is persisted server-side (CAS-015 §2).
/// §7 V6: fails loud (<see cref="ReportGenerationOutcome.GenerationFailed"/>) rather than silently
/// truncating if the underlying audit-log read hits its internal 100,000-row cap exactly.
/// </summary>
public interface IPostActionReportService
{
    Task<PostActionReportResult> GenerateAsync(
        ReportGenerationRequest request, int actingUserId, string actingUsername, CancellationToken ct);

    /// <summary>
    /// <c>GET /reports/post-action/preview</c> (CAS-038 §3/§6) — builds rows via the exact same
    /// shared aggregation method <see cref="GenerateAsync"/> uses (CAS-038 §3 V2/SC-3) and returns
    /// them as JSON instead of a rendered file. Reuses the same 100,000-row cap fail-loud behavior
    /// (CAS-038 §7 V5) as <see cref="GenerateAsync"/>'s §7 V6. No audit row is written (CAS-038 §12).
    /// </summary>
    Task<PostActionReportPreviewResult> PreviewAsync(int actingUserId, string actingUsername, CancellationToken ct);
}
