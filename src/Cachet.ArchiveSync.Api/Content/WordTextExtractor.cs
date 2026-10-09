using NPOI.XWPF.Extractor;
using NPOI.XWPF.UserModel;
using b2xtranslator.DocFileFormat;
using b2xtranslator.StructuredStorage.Reader;
using b2xtranslator.txt;
using b2xtranslator.txt.TextModel;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// <see cref="ITextExtractor"/> for <c>.doc</c>/<c>.docx</c> (CAS-012 §5/§14). <c>.docx</c> uses
/// NPOI (<c>XWPFDocument</c>). <c>.doc</c> (legacy binary Word 97/6.0/95) uses
/// GHSoftware.WordDocTextExtractor (b2xtranslator) instead — NPOI's .NET port never implemented
/// the <c>HWPF</c> module for any published version (verified against 1.2.3 through 2.8.1; the
/// namespace does not exist in any of them), so a second, purpose-built library is required for
/// this one legacy format specifically.
/// </summary>
internal sealed class WordTextExtractor : ITextExtractor
{
    public bool CanHandle(string fileExtension)
    {
        var ext = Normalize(fileExtension);
        return ext is "doc" or "docx";
    }

    public string? ExtractText(byte[] fileBytes, string fileExtension)
    {
        using var stream = new MemoryStream(fileBytes, writable: false);

        if (string.Equals(Normalize(fileExtension), "docx", StringComparison.OrdinalIgnoreCase))
        {
            using var document = new XWPFDocument(stream);
            var extractor = new XWPFWordExtractor(document);
            return extractor.Text;
        }

        using var reader = new StructuredStorageReader(stream, logger: null);
        var legacyDocument = new WordDocument(reader);
        var textDocument = TextDocument.Create(string.Empty, null, extractUrls: true);
        try
        {
            return DocTextExtractor.ConvertToString(legacyDocument, textDocument, extractUrls: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // b2xtranslator's section/paragraph mapping throws (e.g. IndexOutOfRangeException in
            // SectionPropertiesMapping) on some valid files whose text is perfectly readable. The main
            // document text straight from the piece table needs none of that mapping, so use it rather
            // than dropping the file from search. (Headers/footnotes/tables lose their layout only.)
            return PlainTextFromPieceTable(legacyDocument);
        }
    }

    private static string PlainTextFromPieceTable(WordDocument legacyDocument)
    {
        var builder = new System.Text.StringBuilder(legacyDocument.Text.Count);
        foreach (var c in legacyDocument.Text)
        {
            // Word control characters: paragraph/cell marks become newlines; other controls are dropped.
            if (c == '\r' || c == '\a')
            {
                builder.Append('\n');
            }
            else if (!char.IsControl(c) || c == '\t' || c == '\n')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string Normalize(string fileExtension) => fileExtension.TrimStart('.');
}
