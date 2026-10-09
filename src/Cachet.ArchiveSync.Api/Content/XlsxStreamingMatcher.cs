using System.IO.Compression;
using System.Xml;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// Finds a term in an <c>.xlsx</c> by streaming its XML parts instead of loading the workbook object
/// model. NPOI's <c>XSSFWorkbook</c> materialises every cell, so a large or sparse-but-styled sheet needs
/// gigabytes and minutes; this reads shared strings, inline strings and numeric cell values with an
/// <see cref="XmlReader"/>, holds only a small text window, and stops at the first match.
///
/// Searched: shared-string table, inline strings, and non-shared-string cell values (numbers, formula
/// string results, booleans) as stored. Numbers are matched as stored (e.g. <c>1234.5</c>), not as the
/// cell's display format (e.g. <c>$1,234.50</c>) — the one difference from the NPOI path.
/// </summary>
internal static class XlsxStreamingMatcher
{
    private static readonly byte[] ZipSignature = { 0x50, 0x4B }; // "PK"

    /// <summary>True when the bytes look like a zip package (an OOXML file) rather than OLE2 or garbage.</summary>
    public static bool IsZipPackage(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == ZipSignature[0] && bytes[1] == ZipSignature[1];

    /// <summary>Snippet for the first match, or null. Throws on an unreadable package (caller falls back).</summary>
    public static string? FindSnippet(byte[] bytes, string term)
    {
        var finder = new StreamingSnippetFinder(term);

        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);

        var sharedStrings = zip.GetEntry("xl/sharedStrings.xml");
        if (sharedStrings is not null && Scan(sharedStrings, finder))
        {
            return finder.Snippet();
        }

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && Scan(entry, finder))
            {
                return finder.Snippet();
            }
        }

        return finder.Snippet();
    }

    /// <summary>Streams one XML part into the finder. Returns true as soon as the finder is satisfied.</summary>
    private static bool Scan(ZipArchiveEntry entry, StreamingSnippetFinder finder)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = true
        };

        using var reader = XmlReader.Create(entry.Open(), settings);

        var cellIsSharedString = false;
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "c":
                        // t="s": the <v> that follows is an index into the shared strings, not content.
                        cellIsSharedString = reader.GetAttribute("t") == "s";
                        break;

                    case "t":
                    case "v" when !cellIsSharedString:
                        // ReadElementContentAsString leaves the reader on the next node, so continue
                        // without a further Read().
                        if (finder.Append(reader.ReadElementContentAsString()) || finder.Append(" "))
                        {
                            return true;
                        }

                        continue;
                }
            }

            reader.Read();
        }

        return false;
    }
}
