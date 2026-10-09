namespace Cachet.ArchiveSync.Core.Dtos;

/// <summary>
/// Generic (title, columnHeaders, rows, generatedAt, generatedBy) shape consumed by
/// <see cref="Interfaces.IReportDocumentWriter"/> (CAS-010 §3) — shared between the Pre-Action and
/// Post-Action reports so the PDF/XLSX rendering logic is written once, not once per report type.
/// </summary>
public sealed record ReportDocument
{
    /// <summary>Report title. §6.1's optional, cosmetic-only <c>activeTabLabel</c> is folded into
    /// this field by the caller (e.g. "Pre-Action Impact Report — Keyword Search") rather than
    /// adding a separate subtitle field to this shared shape.</summary>
    public required string Title { get; init; }

    public required IReadOnlyList<string> ColumnHeaders { get; init; }

    /// <summary>Each inner list is one row, positionally aligned with <see cref="ColumnHeaders"/>.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required string GeneratedBy { get; init; }
}
