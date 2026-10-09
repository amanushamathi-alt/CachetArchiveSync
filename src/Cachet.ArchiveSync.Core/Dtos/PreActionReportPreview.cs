namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// <c>GET /reports/pre-action/preview</c> response shape (CAS-038 §6, matching CAS-032 §6.2's
/// contract exactly). <see cref="Rows"/> is built by the same shared row-aggregation method
/// <c>PreActionReportService.GenerateAsync</c> uses for the downloaded file (CAS-038 §3 V2/SC-3) —
/// nothing is persisted server-side and no audit row is written for a preview (CAS-038 §12).
/// </summary>
public sealed record PreActionReportPreview
{
    public required DateTimeOffset GeneratedAt { get; init; }

    public required string GeneratedBy { get; init; }

    public required IReadOnlyList<PreActionReportRow> Rows { get; init; }
}
