using System.Text;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// Last-resort search of a legacy binary Office file (.xls/.doc) that its parser cannot open (corrupt or
/// non-standard records). The text of these formats is stored as plain Latin-1 or UTF-16LE runs inside
/// the file, so the term can still be found by scanning the raw bytes. Not a structured read: there is no
/// cell/paragraph context, only the nearby printable characters.
/// </summary>
internal static class RawBinaryTermScan
{
    private const int SnippetWindowChars = 100;

    // Corrupt files are small; refuse very large ones rather than decoding them whole into memory.
    private const int MaxBytes = 20 * 1024 * 1024;

    public static string? FindSnippet(byte[] bytes, string term)
    {
        if (string.IsNullOrEmpty(term) || bytes.Length == 0 || bytes.Length > MaxBytes)
        {
            return null;
        }

        // Latin-1 maps each byte to one char (covers BIFF "compressed" and Word 8-bit text).
        var snippet = Search(Encoding.Latin1.GetString(bytes), term);
        if (snippet is not null)
        {
            return snippet;
        }

        // UTF-16LE text can start on an even or an odd byte offset.
        snippet = Search(Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1), term);
        if (snippet is not null)
        {
            return snippet;
        }

        return bytes.Length > 1
            ? Search(Encoding.Unicode.GetString(bytes, 1, (bytes.Length - 1) & ~1), term)
            : null;
    }

    private static string? Search(string text, string term)
    {
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = Math.Max(0, index - SnippetWindowChars);
        var end = Math.Min(text.Length, index + term.Length + SnippetWindowChars);
        return Clean(text[start..end]);
    }

    // Raw bytes around a hit are mostly binary: keep printable characters only, collapse the rest.
    private static string Clean(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var lastWasGap = true;
        foreach (var c in raw)
        {
            if (!char.IsControl(c) && c != '�' && (c < 0x7F || c > 0x9F))
            {
                builder.Append(c);
                lastWasGap = false;
            }
            else if (!lastWasGap)
            {
                builder.Append(' ');
                lastWasGap = true;
            }
        }

        return builder.ToString().Trim();
    }
}
