using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Cachet.ArchiveSync.Api.Reporting;

/// <summary>
/// PdfSharp implementation of <see cref="IReportDocumentWriter.WritePdfAsync"/> (CAS-010 §3; CAS-015
/// §5/§8 — renders to an in-memory buffer, never to disk). Draws a simple title/metadata header
/// followed by a plain row-per-line table, paginating onto a new <see cref="PdfPage"/> when the
/// current page is full — no rich styling beyond what §6's report contract requires (a defensible,
/// readable record, not a pixel-perfect layout).
/// </summary>
public sealed class PdfSharpReportWriter
{
    private const double PageMarginPoints = 40;
    private const double RowHeightPoints = 16;
    private const double TitleFontSize = 16;
    private const double MetaFontSize = 9;
    private const double HeaderFontSize = 9;
    private const double CellFontSize = 9;

    public Task<byte[]> WritePdfAsync(ReportDocument document, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var pdf = new PdfDocument();
        var titleFont = new XFont("Arial", TitleFontSize, XFontStyleEx.Bold);
        var metaFont = new XFont("Arial", MetaFontSize, XFontStyleEx.Regular);
        var headerFont = new XFont("Arial", HeaderFontSize, XFontStyleEx.Bold);
        var cellFont = new XFont("Arial", CellFontSize, XFontStyleEx.Regular);

        var columnCount = Math.Max(document.ColumnHeaders.Count, 1);

        PdfPage page = null!;
        XGraphics gfx = null!;
        double y = 0;
        double usableWidth = 0;
        double columnWidth = 0;

        void StartNewPage()
        {
            page = pdf.AddPage();
            gfx = XGraphics.FromPdfPage(page);
            usableWidth = page.Width.Point - (2 * PageMarginPoints);
            columnWidth = usableWidth / columnCount;
            y = PageMarginPoints;

            gfx.DrawString(document.Title, titleFont, XBrushes.Black,
                new XPoint(PageMarginPoints, y + TitleFontSize));
            y += TitleFontSize + 6;

            gfx.DrawString(
                $"Generated {document.GeneratedAt:u} by {document.GeneratedBy}",
                metaFont, XBrushes.Black, new XPoint(PageMarginPoints, y + MetaFontSize));
            y += MetaFontSize + 12;

            DrawRow(document.ColumnHeaders, headerFont);
        }

        void DrawRow(IReadOnlyList<string> cells, XFont font)
        {
            var x = PageMarginPoints;
            for (var i = 0; i < columnCount; i++)
            {
                var text = i < cells.Count ? cells[i] : string.Empty;
                gfx.DrawString(text, font, XBrushes.Black,
                    new XRect(x, y, columnWidth, RowHeightPoints), XStringFormats.TopLeft);
                x += columnWidth;
            }
            y += RowHeightPoints;
        }

        StartNewPage();

        foreach (var row in document.Rows)
        {
            ct.ThrowIfCancellationRequested();

            if (y + RowHeightPoints > page.Height.Point - PageMarginPoints)
            {
                StartNewPage();
            }

            DrawRow(row, cellFont);
        }

        using var stream = new MemoryStream();
        pdf.Save(stream, closeStream: false);
        return Task.FromResult(stream.ToArray());
    }
}
