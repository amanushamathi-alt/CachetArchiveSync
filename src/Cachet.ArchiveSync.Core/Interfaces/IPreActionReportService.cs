using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>Outcome of one report-generation attempt (CAS-010 §7 V2/V6, CAS-015 §9 V7) — shared by
/// <see cref="IPreActionReportService"/> and <see cref="IPostActionReportService"/>.</summary>
public enum ReportGenerationOutcome
{
    Generated,

    /// <summary>PDF/XLSX write failure, or (Post-Action only) the audit-log cap was hit exactly
    /// (§7 V2/V6) — mapped by the controller to <c>500 report-generation-failed</c>.</summary>
    GenerationFailed,

    /// <summary>The request's <c>format</c> field was missing, empty, or not exactly <c>"pdf"</c>
    /// or <c>"xlsx"</c> (CAS-015 §9 V7) — mapped by the controller to <c>400 validation-failed</c>.</summary>
    ValidationFailed
}

/// <summary>
/// Result of one <see cref="IPreActionReportService.GenerateAsync"/> call (CAS-015 §2/§8) — on
/// <see cref="ReportGenerationOutcome.Generated"/>, carries the rendered file's bytes/content-type/
/// download filename directly (no persisted <c>reportId</c>/download URL).
/// </summary>
public sealed record PreActionReportResult
{
    public required ReportGenerationOutcome Outcome { get; init; }

    public string? ValidationError { get; init; }

    public byte[]? FileBytes { get; init; }

    public string? ContentType { get; init; }

    public string? DownloadFileName { get; init; }
}

/// <summary>
/// <c>POST /reports/pre-action</c> (CAS-010 §3/§6.1, FR-25; CAS-015 §2/§8) — aggregates every active
/// <c>dbo.search_result</c> row (all tasks) and every <c>Active</c> <c>dbo.quarantine_entry</c> row,
/// renders the single requested format (<c>request.Format</c>) to bytes in memory, writes an audit
/// row, and returns the rendered file directly — nothing is persisted server-side (CAS-015 §2).
/// </summary>
public interface IPreActionReportService
{
    Task<PreActionReportResult> GenerateAsync(
        ReportGenerationRequest request, int actingUserId, string actingUsername, CancellationToken ct);

    /// <summary>
    /// <c>GET /reports/pre-action/preview</c> (CAS-038 §3/§6) — builds rows via the exact same
    /// shared aggregation method <see cref="GenerateAsync"/> uses (CAS-038 §3 V2/SC-3) and returns
    /// them as JSON instead of a rendered file. No audit row is written (CAS-038 §12).
    /// </summary>
    Task<PreActionReportPreview> PreviewAsync(int actingUserId, string actingUsername, CancellationToken ct);
}
