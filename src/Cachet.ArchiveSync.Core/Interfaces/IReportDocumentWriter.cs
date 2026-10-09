using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Shared document-writer abstraction (CAS-010 §3, CAS-015 §5/§8) — renders a generic
/// <see cref="ReportDocument"/> shape to PDF (PdfSharp) or XLSX (ClosedXML), used by both the
/// Pre-Action and Post-Action report services. Each method renders entirely in memory and returns
/// the rendered bytes to the caller — no filesystem path is ever involved (CAS-015 §2/§9 V8:
/// no report file is written to disk after generation).
/// </summary>
public interface IReportDocumentWriter
{
    Task<byte[]> WritePdfAsync(ReportDocument document, CancellationToken ct);

    Task<byte[]> WriteXlsxAsync(ReportDocument document, CancellationToken ct);
}
