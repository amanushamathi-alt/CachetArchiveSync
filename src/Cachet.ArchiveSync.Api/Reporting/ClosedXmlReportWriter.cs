using Cachet.ArchiveSync.Core.Dtos;
using ClosedXML.Excel;

namespace Cachet.ArchiveSync.Api.Reporting;

/// <summary>
/// ClosedXML rendering of a <see cref="ReportDocument"/> to a single-worksheet <c>.xlsx</c> in-memory
/// buffer (CAS-010 §3; CAS-015 §5/§8 — never written to disk). A title/metadata header row pair,
/// then one header row and one row per <see cref="ReportDocument.Rows"/> entry.
/// </summary>
public sealed class ClosedXmlReportWriter
{
    public Task<byte[]> WriteXlsxAsync(ReportDocument document, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Report");

        worksheet.Cell(1, 1).Value = document.Title;
        worksheet.Cell(1, 1).Style.Font.Bold = true;
        worksheet.Cell(1, 1).Style.Font.FontSize = 14;

        worksheet.Cell(2, 1).Value = $"Generated {document.GeneratedAt:u} by {document.GeneratedBy}";

        var headerRowIndex = 4;
        for (var col = 0; col < document.ColumnHeaders.Count; col++)
        {
            var cell = worksheet.Cell(headerRowIndex, col + 1);
            cell.Value = document.ColumnHeaders[col];
            cell.Style.Font.Bold = true;
        }

        var rowIndex = headerRowIndex + 1;
        foreach (var row in document.Rows)
        {
            ct.ThrowIfCancellationRequested();

            for (var col = 0; col < row.Count; col++)
            {
                worksheet.Cell(rowIndex, col + 1).Value = row[col];
            }
            rowIndex++;
        }

        worksheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return Task.FromResult(stream.ToArray());
    }
}
