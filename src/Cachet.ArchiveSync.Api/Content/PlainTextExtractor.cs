namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// <see cref="ITextExtractor"/> for plain-text types — txt, csv, ach, xml, htm, html, bai2, log
/// (CAS-012 §5/§14) — a plain read, no extraction library needed. Markup in xml/htm/html is
/// searched as-is. Detects a byte-order mark if present; otherwise reads as UTF-8.
/// </summary>
internal sealed class PlainTextExtractor : ITextExtractor
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { "txt", "csv", "ach", "xml", "htm", "html", "bai2", "log" };

    public bool CanHandle(string fileExtension) => IsPlainText(fileExtension);

    internal static bool IsPlainText(string fileExtension) => Extensions.Contains(Normalize(fileExtension));

    public string? ExtractText(byte[] fileBytes, string fileExtension)
    {
        using var stream = new MemoryStream(fileBytes);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string Normalize(string fileExtension) => fileExtension.TrimStart('.');
}
