using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.Extensions.Options;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using NPOI.XWPF.UserModel;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// One rendered preview body (UTF-8 bytes) and whether a §9 cap truncated it.
/// </summary>
public sealed record OfficePreview(byte[] Body, bool Truncated);

/// <summary>
/// CAS-040 §8.1 server-side preview conversions (option A: NPOI + the existing b2xtranslator DOC
/// path). Synchronous and uncancellable mid-parse by nature; <see cref="PreviewConversionLimiter"/>
/// bounds concurrency and wall-clock time around every call. Substitutable so the V9
/// <c>Timeout</c> path can be tested with a fake slow renderer (§13).
/// </summary>
public interface IOfficePreviewRenderer
{
    /// <summary>DOCX → encoded structural HTML (paragraphs, h1–h6, strong/em, tables).</summary>
    OfficePreview RenderDocx(byte[] fileBytes);

    /// <summary>XLSX (<paramref name="isXlsx"/>) or XLS → one encoded HTML table per sheet.</summary>
    OfficePreview RenderSpreadsheet(byte[] fileBytes, bool isXlsx);

    /// <summary>Legacy DOC → UTF-8 plain text via <see cref="WordTextExtractor"/>, capped at
    /// <c>TextMaxBytes</c>.</summary>
    OfficePreview ExtractLegacyDocText(byte[] fileBytes);
}

/// <summary>
/// <see cref="IOfficePreviewRenderer"/> implementation. The HTML is <b>generated</b>: every string
/// taken from the document is <see cref="HtmlEncoder"/>-encoded, and no attribute, URL, style,
/// image, link or script is ever taken from the document (§12 stored-XSS control) — hyperlinks
/// render as their text only. Formulas are never evaluated; spreadsheet cells show the
/// <b>cached</b> result (§12).
/// </summary>
public sealed class OfficePreviewRenderer : IOfficePreviewRenderer
{
    private const string TruncatedBanner = "Preview truncated. Download the file for the full content.";

    // Fixed, application-authored stylesheet (the response CSP allows only inline style).
    private const string Stylesheet =
        "body{font-family:Segoe UI,Arial,sans-serif;font-size:13px;color:#1b2430;margin:16px;}" +
        "p{margin:0 0 8px;white-space:pre-wrap;}" +
        "table{border-collapse:collapse;margin:0 0 12px;}" +
        "th,td{border:1px solid #c9d1dc;padding:3px 6px;vertical-align:top;white-space:pre-wrap;text-align:left;}" +
        "th{background:#eef2f6;font-weight:600;}" +
        "section{margin:0 0 20px;}" +
        "h2.sheet{font-size:14px;margin:0 0 6px;}" +
        ".banner{background:#fff4d6;border:1px solid #e5c56b;padding:6px 10px;margin:0 0 12px;}";

    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    private readonly FilePreviewOptions _options;

    public OfficePreviewRenderer(IOptions<FilePreviewOptions> options)
    {
        _options = options.Value;
    }

    public OfficePreview RenderDocx(byte[] fileBytes)
    {
        using var stream = new MemoryStream(fileBytes, writable: false);
        using var document = new XWPFDocument(stream);

        var body = new StringBuilder();
        var budget = _options.MaxHtmlBytes;
        var used = 0;
        var truncated = false;

        foreach (var element in document.BodyElements)
        {
            string fragment;
            if (element is XWPFParagraph paragraph)
            {
                fragment = RenderParagraph(document, paragraph);
            }
            else if (element is XWPFTable table)
            {
                fragment = RenderTable(table);
            }
            else
            {
                // Content controls and other body elements are not rendered (§4 — structural only).
                continue;
            }

            var size = Encoding.UTF8.GetByteCount(fragment);
            if (used + size > budget)
            {
                truncated = true;
                break;
            }

            body.Append(fragment);
            used += size;
        }

        return new OfficePreview(Encoding.UTF8.GetBytes(WrapDocument(body.ToString(), truncated)), truncated);
    }

    public OfficePreview RenderSpreadsheet(byte[] fileBytes, bool isXlsx)
    {
        using var stream = new MemoryStream(fileBytes, writable: false);
        using IWorkbook workbook = isXlsx ? new XSSFWorkbook(stream) : new HSSFWorkbook(stream);

        var formatter = new DataFormatter(CultureInfo.InvariantCulture);
        var body = new StringBuilder();
        var truncated = workbook.NumberOfSheets > _options.MaxSheets;
        var sheetCount = Math.Min(workbook.NumberOfSheets, _options.MaxSheets);

        for (var sheetIndex = 0; sheetIndex < sheetCount; sheetIndex++)
        {
            var sheet = workbook.GetSheetAt(sheetIndex);
            var hidden = workbook.IsSheetHidden(sheetIndex) || workbook.IsSheetVeryHidden(sheetIndex);

            var lastRow = sheet.LastRowNum;
            var rowLimit = Math.Min(lastRow, _options.MaxRowsPerSheet - 1);
            if (lastRow > rowLimit)
            {
                truncated = true;
            }

            var columnCount = 0;
            for (var r = 0; r <= rowLimit; r++)
            {
                var row = sheet.GetRow(r);
                if (row is not null && row.LastCellNum > columnCount)
                {
                    columnCount = row.LastCellNum;
                }
            }

            if (columnCount > _options.MaxColumns)
            {
                truncated = true;
                columnCount = _options.MaxColumns;
            }

            body.Append("<section><h2 class=\"sheet\">").Append(Encode(sheet.SheetName));
            if (hidden)
            {
                body.Append(" (hidden)");
            }

            body.Append("</h2><table><thead><tr><th></th>");
            for (var c = 0; c < columnCount; c++)
            {
                body.Append("<th>").Append(CellReference.ConvertNumToColString(c)).Append("</th>");
            }

            body.Append("</tr></thead><tbody>");
            if (sheet.PhysicalNumberOfRows > 0)
            {
                for (var r = 0; r <= rowLimit; r++)
                {
                    var row = sheet.GetRow(r);
                    body.Append("<tr><th>").Append((r + 1).ToString(CultureInfo.InvariantCulture)).Append("</th>");
                    for (var c = 0; c < columnCount; c++)
                    {
                        var cell = row?.GetCell(c);
                        body.Append("<td>").Append(Encode(cell is null ? string.Empty : FormatCell(formatter, cell))).Append("</td>");
                    }

                    body.Append("</tr>");
                }
            }

            body.Append("</tbody></table></section>");
        }

        return new OfficePreview(Encoding.UTF8.GetBytes(WrapDocument(body.ToString(), truncated)), truncated);
    }

    public OfficePreview ExtractLegacyDocText(byte[] fileBytes)
    {
        var text = new WordTextExtractor().ExtractText(fileBytes, "doc") ?? string.Empty;
        var (bytes, truncated) = CapUtf8(text, _options.TextMaxBytes);
        return new OfficePreview(bytes, truncated);
    }

    /// <summary>UTF-8 encodes <paramref name="text"/> and cuts it at <paramref name="maxBytes"/>
    /// on a character boundary (never splitting a multi-byte sequence).</summary>
    internal static (byte[] Bytes, bool Truncated) CapUtf8(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return (bytes, false);
        }

        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
        {
            cut--;
        }

        return (bytes[..cut], true);
    }

    private static string RenderParagraph(XWPFDocument document, XWPFParagraph paragraph)
    {
        var inner = new StringBuilder();
        foreach (var run in paragraph.Runs)
        {
            var text = run.ToString();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var encoded = Encode(text);
            if (run.IsBold)
            {
                encoded = "<strong>" + encoded + "</strong>";
            }

            if (run.IsItalic)
            {
                encoded = "<em>" + encoded + "</em>";
            }

            inner.Append(encoded);
        }

        var level = HeadingLevel(document, paragraph);
        var tag = level > 0 ? "h" + level.ToString(CultureInfo.InvariantCulture) : "p";
        return "<" + tag + ">" + inner + "</" + tag + ">";
    }

    private static string RenderTable(XWPFTable table)
    {
        var html = new StringBuilder("<table><tbody>");
        foreach (var row in table.Rows)
        {
            html.Append("<tr>");
            foreach (var cell in row.GetTableCells())
            {
                html.Append("<td>").Append(Encode(cell.GetText() ?? string.Empty)).Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table>");
        return html.ToString();
    }

    /// <summary>Heading 1–6 by style id (<c>Heading1</c>) or style name (<c>heading 1</c>).</summary>
    private static int HeadingLevel(XWPFDocument document, XWPFParagraph paragraph)
    {
        var styleId = paragraph.StyleID;
        if (string.IsNullOrEmpty(styleId))
        {
            return 0;
        }

        var styleName = document.GetStyles()?.GetStyle(styleId)?.Name;
        foreach (var candidate in new[] { styleId, styleName })
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }

            var normalized = candidate.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (normalized.Length == 8
                && normalized.StartsWith("heading", StringComparison.OrdinalIgnoreCase)
                && normalized[7] is >= '1' and <= '6')
            {
                return normalized[7] - '0';
            }
        }

        return 0;
    }

    /// <summary>Displayed cell text; formula cells use the cached result, never re-evaluated.</summary>
    private static string FormatCell(DataFormatter formatter, NPOI.SS.UserModel.ICell cell)
    {
        if (cell.CellType != CellType.Formula)
        {
            return formatter.FormatCellValue(cell);
        }

        switch (cell.CachedFormulaResultType)
        {
            case CellType.Numeric:
                return formatter.FormatRawCellContents(
                    cell.NumericCellValue, cell.CellStyle.DataFormat, cell.CellStyle.GetDataFormatString());
            case CellType.String:
                return cell.StringCellValue ?? string.Empty;
            case CellType.Boolean:
                return cell.BooleanCellValue ? "TRUE" : "FALSE";
            case CellType.Error:
                try
                {
                    return FormulaError.ForInt(cell.ErrorCellValue).String;
                }
                catch (ArgumentException)
                {
                    return "#ERROR";
                }
            default:
                return string.Empty;
        }
    }

    private static string WrapDocument(string body, bool truncated) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Preview</title><style>" + Stylesheet +
        "</style></head><body>" +
        (truncated ? "<p class=\"banner\">" + TruncatedBanner + "</p>" : string.Empty) +
        body + "</body></html>";

    private static string Encode(string value) => Encoder.Encode(value);
}
