using System.Globalization;
using System.Text;
using NPOI.POIFS.FileSystem;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// Text extraction for Excel 5.0/95 (BIFF5) workbooks, which NPOI's HSSF (built for BIFF8, Excel 97+)
/// cannot read: BIFF5 stores strings as plain 8-bit text (no option-flags byte), so HSSF misreads the
/// first Font record and throws "Expected to find a ContinueRecord…". An Excel 95 file keeps its data in an
/// OLE stream named <c>Book</c> (BIFF8 uses <c>Workbook</c>), which is how these files are recognised.
///
/// Reads cell text (LABEL, RSTRING, formula STRING results) and numbers (NUMBER, RK, MULRK) record by
/// record. No sheet/cell structure is kept — the output is just searchable text, like the other extractors.
/// </summary>
internal static class Biff5TextExtractor
{
    private static readonly byte[] OleSignature = { 0xD0, 0xCF, 0x11, 0xE0 };

    /// <summary>True for an OLE file whose data stream is <c>Book</c> and not <c>Workbook</c> (Excel 5.0/95).</summary>
    public static bool IsBiff5Workbook(byte[] bytes)
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 4).SequenceEqual(OleSignature))
        {
            return false;
        }

        try
        {
            var fs = new POIFSFileSystem(new MemoryStream(bytes, writable: false));
            var names = new List<string>();
            var entries = fs.Root.Entries;
            while (entries.MoveNext())
            {
                names.Add(entries.Current.Name);
            }

            return names.Contains("Book", StringComparer.OrdinalIgnoreCase)
                && !names.Contains("Workbook", StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string ExtractText(byte[] bytes)
    {
        var fs = new POIFSFileSystem(new MemoryStream(bytes, writable: false));

        // OLE entry names are case-sensitive and files in the wild use "Book" or "BOOK".
        string? bookName = null;
        var entries = fs.Root.Entries;
        while (entries.MoveNext())
        {
            if (string.Equals(entries.Current.Name, "Book", StringComparison.OrdinalIgnoreCase))
            {
                bookName = entries.Current.Name;
            }
        }

        var stream = fs.CreateDocumentInputStream(bookName ?? throw new InvalidDataException("No Book stream."));
        var data = new byte[stream.Available()];
        var read = 0;
        while (read < data.Length)
        {
            var n = stream.Read(data, read, data.Length - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }

        return ReadRecords(data.AsSpan(0, read));
    }

    private static string ReadRecords(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder();
        var pos = 0;

        while (pos + 4 <= data.Length)
        {
            int id = data[pos] | (data[pos + 1] << 8);
            int len = data[pos + 2] | (data[pos + 3] << 8);
            var start = pos + 4;
            if (start + len > data.Length)
            {
                break; // truncated trailing record
            }

            var rec = data.Slice(start, len);
            switch (id)
            {
                case 0x0204 when len >= 8: // LABEL: row, col, xf, cch(2), chars
                    AppendText(builder, rec, 6, 8);
                    break;
                case 0x00D6 when len >= 8: // RSTRING: row, col, xf, cch(2), chars, runs
                    AppendText(builder, rec, 6, 8);
                    break;
                case 0x0207 when len >= 2: // STRING (formula result): cch(2), chars
                    AppendText(builder, rec, 0, 2);
                    break;
                case 0x0203 when len >= 14: // NUMBER: row, col, xf, double
                    AppendNumber(builder, BitConverter.ToDouble(rec.Slice(6, 8)));
                    break;
                case 0x027E when len >= 10: // RK: row, col, xf, rk
                    AppendNumber(builder, DecodeRk(BitConverter.ToUInt32(rec.Slice(6, 4))));
                    break;
                case 0x00BD when len >= 6: // MULRK: row, firstCol, (xf, rk)*, lastCol
                    for (var o = 4; o + 6 <= len - 2; o += 6)
                    {
                        AppendNumber(builder, DecodeRk(BitConverter.ToUInt32(rec.Slice(o + 2, 4))));
                    }

                    break;
            }

            pos = start + len;
        }

        return builder.ToString();
    }

    private static void AppendText(StringBuilder builder, ReadOnlySpan<byte> rec, int lengthOffset, int textOffset)
    {
        int cch = rec[lengthOffset] | (rec[lengthOffset + 1] << 8);
        if (cch <= 0 || textOffset + cch > rec.Length)
        {
            return;
        }

        builder.Append(Encoding.Latin1.GetString(rec.Slice(textOffset, cch))).Append(' ');
    }

    private static void AppendNumber(StringBuilder builder, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        builder.Append(Math.Abs(value) < 1e15 && value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
    }

    // RK: bit0 = divide by 100, bit1 = 30-bit signed integer (else the top 30 bits of an IEEE double).
    private static double DecodeRk(uint rk)
    {
        double value = (rk & 0x02) != 0
            ? (int)rk >> 2
            : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
        return (rk & 0x01) != 0 ? value / 100 : value;
    }
}
