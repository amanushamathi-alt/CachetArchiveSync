using System.Text;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// CAS-040 §8.1 TXT encoding detection for the <c>InlineText</c> preview:
/// 1. BOM — UTF-8 <c>EF BB BF</c>, UTF-16LE <c>FF FE</c>, UTF-16BE <c>FE FF</c>;
/// 2. strict UTF-8 (<c>throwOnInvalidBytes</c>), after trimming up to 3 trailing bytes of an
///    incomplete sequence at the cap boundary;
/// 3. Windows-1252 (<see cref="CodePagesEncodingProvider"/>, registered once at Api startup).
/// The output never contains U+FFFD from a failed strict decode — that case always falls back.
/// </summary>
public static class TextPreviewDecoder
{
    public const string Utf8 = "utf-8";
    public const string Utf16Le = "utf-16le";
    public const string Utf16Be = "utf-16be";
    public const string Windows1252 = "windows-1252";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes <paramref name="bytes"/> (already capped by the caller at <c>TextMaxBytes</c>).
    /// <paramref name="truncatedAtCap"/> is <c>true</c> when the file continues past
    /// <paramref name="bytes"/>, which permits trimming a split trailing character.
    /// </summary>
    public static (string Text, string SourceEncoding) Decode(ReadOnlySpan<byte> bytes, bool truncatedAtCap)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            var body = bytes[3..];
            if (truncatedAtCap)
            {
                body = body[..(body.Length - IncompleteUtf8TailLength(body))];
            }

            // A BOM declares the encoding: decode leniently rather than second-guess it.
            return (Encoding.UTF8.GetString(body), Utf8);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (DecodeUtf16(bytes[2..], new UnicodeEncoding(bigEndian: false, byteOrderMark: false), bigEndian: false, truncatedAtCap), Utf16Le);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (DecodeUtf16(bytes[2..], new UnicodeEncoding(bigEndian: true, byteOrderMark: false), bigEndian: true, truncatedAtCap), Utf16Be);
        }

        var candidate = truncatedAtCap ? bytes[..(bytes.Length - IncompleteUtf8TailLength(bytes))] : bytes;
        try
        {
            return (StrictUtf8.GetString(candidate), Utf8);
        }
        catch (DecoderFallbackException)
        {
            // Step 3 — not valid UTF-8. Decode the full (untrimmed) bytes: every byte is a whole
            // character in Windows-1252.
            return (Encoding.GetEncoding(1252).GetString(bytes), Windows1252);
        }
    }

    /// <summary>
    /// Length (0–3) of an incomplete UTF-8 sequence at the end of <paramref name="bytes"/>: the
    /// trailing lead byte plus continuation bytes, when fewer bytes follow the lead than it
    /// declares. Returns 0 when the tail is complete or is not a recognisable lead/continuation run.
    /// </summary>
    private static int IncompleteUtf8TailLength(ReadOnlySpan<byte> bytes)
    {
        var maxBack = Math.Min(3, bytes.Length);
        for (var back = 1; back <= maxBack; back++)
        {
            var b = bytes[^back];
            if ((b & 0xC0) == 0x80)
            {
                // Continuation byte — keep scanning back for its lead byte.
                continue;
            }

            var expected = (b & 0xE0) == 0xC0 ? 2
                : (b & 0xF0) == 0xE0 ? 3
                : (b & 0xF8) == 0xF0 ? 4
                : 1;
            return expected > back ? back : 0;
        }

        return 0;
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> body, Encoding encoding, bool bigEndian, bool truncatedAtCap)
    {
        if (truncatedAtCap)
        {
            if (body.Length % 2 == 1)
            {
                body = body[..^1];
            }

            // Drop a high surrogate split from its low surrogate at the cap boundary.
            if (body.Length >= 2)
            {
                var last = bigEndian ? (body[^2] << 8) | body[^1] : (body[^1] << 8) | body[^2];
                if (char.IsHighSurrogate((char)last))
                {
                    body = body[..^2];
                }
            }
        }

        return encoding.GetString(body);
    }
}
