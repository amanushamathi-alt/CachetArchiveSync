using System.Text;
using NPOI.SS.UserModel;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// <see cref="ITextExtractor"/> for <c>.xls</c>/<c>.xlsx</c> (CAS-012 §5/§14). Uses NPOI, same
/// rationale as <see cref="WordTextExtractor"/> — one library covers both the legacy binary Excel
/// format (<c>.xls</c>, <c>HSSFWorkbook</c>) and OOXML (<c>.xlsx</c>, <c>XSSFWorkbook</c>).
/// Extracts every cell's displayed text across every sheet — this is a simple substring/word match
/// (CAS-012 §14), not a structured/formula-aware read.
/// </summary>
internal sealed class ExcelTextExtractor : ITextExtractor
{
    public bool CanHandle(string fileExtension)
    {
        var ext = Normalize(fileExtension);
        return ext is "xls" or "xlsx";
    }

    public string? ExtractText(byte[] fileBytes, string fileExtension)
    {
        using var stream = new MemoryStream(fileBytes);

        // Excel 5.0/95 (BIFF5) workbooks: NPOI's HSSF only reads BIFF8 and throws on the first font
        // record, so read these with the dedicated BIFF5 reader.
        if (Biff5TextExtractor.IsBiff5Workbook(fileBytes))
        {
            return Biff5TextExtractor.ExtractText(fileBytes);
        }

        // WorkbookFactory detects the real format from the bytes, so a binary .xls saved with an
        // .xlsx extension (or the reverse) is still read instead of failing in the wrong reader.
        using IWorkbook workbook = WorkbookFactory.Create(stream);

        var builder = new StringBuilder();
        for (var sheetIndex = 0; sheetIndex < workbook.NumberOfSheets; sheetIndex++)
        {
            var sheet = workbook.GetSheetAt(sheetIndex);
            foreach (IRow row in sheet)
            {
                foreach (ICell cell in row)
                {
                    builder.Append(CellText(cell));
                    builder.Append(' ');
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// A cell's displayed text. NPOI's formatter can throw on a malformed date/number format (e.g. a
    /// numeric cell whose date style is invalid); one bad cell must not discard the whole workbook,
    /// so fall back to the cell's raw value.
    /// </summary>
    private static string CellText(ICell cell)
    {
        try
        {
            return cell.ToString() ?? string.Empty;
        }
        catch (Exception)
        {
            try
            {
                var type = cell.CellType == CellType.Formula ? cell.CachedFormulaResultType : cell.CellType;
                return type switch
                {
                    CellType.String => cell.StringCellValue,
                    CellType.Numeric => cell.NumericCellValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    CellType.Boolean => cell.BooleanCellValue.ToString(),
                    _ => string.Empty
                };
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    private static string Normalize(string fileExtension) => fileExtension.TrimStart('.');
}
