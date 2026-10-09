using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Reporting;

/// <summary>
/// <see cref="IReportDocumentWriter"/> implementation (CAS-010 §3) — the single abstraction both
/// <c>PreActionReportService</c> and <c>PostActionReportService</c> depend on. Delegates the actual
/// rendering to <see cref="PdfSharpReportWriter"/> (PdfSharp) and <see cref="ClosedXmlReportWriter"/>
/// (ClosedXML) respectively, so each format's rendering logic stays in its own file.
/// </summary>
public sealed class ReportDocumentWriter : IReportDocumentWriter
{
    private readonly PdfSharpReportWriter _pdfWriter;
    private readonly ClosedXmlReportWriter _xlsxWriter;

    public ReportDocumentWriter(PdfSharpReportWriter pdfWriter, ClosedXmlReportWriter xlsxWriter)
    {
        _pdfWriter = pdfWriter;
        _xlsxWriter = xlsxWriter;
    }

    public Task<byte[]> WritePdfAsync(ReportDocument document, CancellationToken ct) =>
        _pdfWriter.WritePdfAsync(document, ct);

    public Task<byte[]> WriteXlsxAsync(ReportDocument document, CancellationToken ct) =>
        _xlsxWriter.WriteXlsxAsync(document, ct);
}
