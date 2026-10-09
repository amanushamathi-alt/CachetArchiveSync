using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// <see cref="IFileContentMatcher"/> implementation (CAS-012 §2/§5/§14): dispatches to one
/// <see cref="ITextExtractor"/> per in-scope file extension (PDF, DOC/DOCX, XLS/XLSX, and plain-text types TXT/CSV/ACH/XML/HTM/HTML/BAI2/LOG), then
/// performs a simple case-insensitive substring match against <c>term</c> and returns a bounded
/// ±100-character snippet around the first match (§7 V12) — the same window convention the
/// removed SQL <c>CHARINDEX</c>/<c>SUBSTRING</c> logic used. No pre-built external index (§14).
///
/// Never logs the extracted text, the file's content, or the search term (§11) — an extraction
/// failure is logged only as a short, fixed warning naming the file extension, and treated as a
/// skip for that one candidate (§7 V10), not a task failure.
/// </summary>
public sealed class FileContentMatcher : IFileContentMatcher
{
    // §7 V12 / §2e — same ±100-character window the removed SQL CHARINDEX/SUBSTRING logic used.
    private const int SnippetWindowChars = 100;

    // The only file types content search applies to; every other type is excluded from the
    // candidate set before its bytes are read. Keep in sync with the extractors' CanHandle.
    // Documents: pdf, doc, docx, xls, xlsx. Plain text: txt, csv, ach, xml, htm, html, bai2, log
    // (xml/htm/html are searched as raw text, markup included).
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "pdf", "doc", "docx", "xls", "xlsx",
            "txt", "csv", "ach", "xml", "htm", "html", "bai2", "log"
        };

    public static bool IsSupportedExtension(string fileExtension) =>
        SupportedExtensions.Contains(fileExtension.TrimStart('.'));

    /// <summary>True for the plain-text types that <see cref="TryMatchStreamAsync"/> can scan in chunks.</summary>
    public static bool IsStreamableExtension(string fileExtension) => PlainTextExtractor.IsPlainText(fileExtension);

    private readonly IReadOnlyList<ITextExtractor> _extractors;
    private readonly ILogger<FileContentMatcher> _logger;

    public FileContentMatcher(ILogger<FileContentMatcher> logger)
    {
        _logger = logger;
        _extractors = new ITextExtractor[]
        {
            new PdfTextExtractor(),
            new WordTextExtractor(),
            new ExcelTextExtractor(),
            new PlainTextExtractor()
        };
    }

    public async Task<string?> TryMatchStreamAsync(Stream content, string fileExtension, string term, CancellationToken ct)
    {
        if (!IsStreamableExtension(fileExtension) || string.IsNullOrEmpty(term))
        {
            return null;
        }

        using var reader = new StreamReader(content, detectEncodingFromByteOrderMarks: true, bufferSize: 81920, leaveOpen: true);
        var buffer = new char[65536];
        var window = string.Empty;

        // Keep enough of the previous chunk that a match straddling a chunk boundary is still found
        // and still has a full 100-character lead-in for the snippet.
        var keep = term.Length - 1 + SnippetWindowChars;

        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            window += new string(buffer, 0, read);

            var matchIndex = window.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (matchIndex >= 0)
            {
                // Read on until the snippet's trailing context is available (or EOF).
                while (window.Length < matchIndex + term.Length + SnippetWindowChars
                    && (read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    window += new string(buffer, 0, read);
                }

                var start = Math.Max(0, matchIndex - SnippetWindowChars);
                var end = Math.Min(window.Length, matchIndex + term.Length + SnippetWindowChars);
                return window[start..end];
            }

            if (window.Length > keep)
            {
                window = window[^keep..];
            }
        }

        return null;
    }

    /// <summary>
    /// One-line description of an extraction failure: each exception in the chain as
    /// <c>Type: message</c>, no stack trace, bounded length. Contains no file content.
    /// </summary>
    public static string Describe(Exception? ex)
    {
        var parts = new List<string>();
        for (; ex is not null && parts.Count < 4; ex = ex.InnerException)
        {
            parts.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        var text = string.Join(" -> ", parts);
        return text.Length > 400 ? text[..400] + "..." : text;
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or an inner exception) means the file is encrypted: PdfPig's
    /// encrypted-document error, NPOI's encrypted-document error, or NPOI's unimplemented-decryption
    /// stub that older encrypted .xls files hit.
    /// </summary>
    private static bool IsEncrypted(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException
                or NPOI.EncryptedDocumentException
                || (ex is NotImplementedException && ex.StackTrace?.Contains("StreamEncryptionInfo", StringComparison.Ordinal) == true))
            {
                return true;
            }
        }

        return false;
    }

    public Task<string?> TryMatchAsync(byte[] fileBytes, string fileExtension, string term, CancellationToken ct)
    {
        var result = Match(fileBytes, fileExtension, term);

        switch (result.Failure)
        {
            case ContentMatchFailure.Encrypted:
                // Expected, not a fault: password-protected/encrypted files cannot be read by these
                // libraries. One short line (the caller's scope adds the file name and path), no stack trace.
                _logger.LogWarning(
                    "Content extraction skipped: encrypted or password-protected file with extension {FileExtension}.",
                    fileExtension);
                break;
            case ContentMatchFailure.Error:
                // §11: never log extracted text, file content, or the search term — only a short,
                // fixed warning naming the extension.
                _logger.LogWarning(
                    "Content extraction failed for a file with extension {FileExtension}; skipping content match for this candidate. {ExtractionError}",
                    fileExtension, Describe(result.Error));
                break;
        }

        return Task.FromResult(result.Snippet);
    }

    /// <summary>
    /// Pure extract-and-match step with no logging: lets a caller that cannot share this process's
    /// logger (the out-of-process extractor host) report the outcome itself. Never throws for an
    /// extraction failure; the failure kind is on the result.
    /// </summary>
    public ContentMatchResult Match(byte[] fileBytes, string fileExtension, string term)
    {
        // .xlsx: stream the XML parts (flat memory, stops at the first match) instead of loading the
        // whole workbook. Anything that is not a readable zip package (OLE-encrypted, corrupt, or a
        // binary .xls with an .xlsx name) falls through to the NPOI path below, which also classifies
        // encrypted files.
        if (string.Equals(fileExtension.TrimStart('.'), "xlsx", StringComparison.OrdinalIgnoreCase)
            && XlsxStreamingMatcher.IsZipPackage(fileBytes))
        {
            try
            {
                return new ContentMatchResult(XlsxStreamingMatcher.FindSnippet(fileBytes, term), ContentMatchFailure.None, null);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException)
            {
                // fall through
            }
        }

        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(fileExtension));
        if (extractor is null)
        {
            return default;
        }

        string? text;
        try
        {
            text = extractor.ExtractText(fileBytes, fileExtension);
        }
        catch (Exception ex) when (IsEncrypted(ex))
        {
            return new ContentMatchResult(null, ContentMatchFailure.Encrypted, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A damaged legacy binary file (.xls/.doc) the parser rejects may still hold readable text;
            // scan its raw bytes rather than leaving it invisible to search.
            var ext = fileExtension.TrimStart('.');
            if (ext.Equals("xls", StringComparison.OrdinalIgnoreCase) || ext.Equals("doc", StringComparison.OrdinalIgnoreCase))
            {
                var raw = RawBinaryTermScan.FindSnippet(fileBytes, term);
                if (raw is not null)
                {
                    return new ContentMatchResult(raw, ContentMatchFailure.None, null);
                }
            }

            return new ContentMatchResult(null, ContentMatchFailure.Error, ex);
        }

        if (string.IsNullOrEmpty(text))
        {
            return default;
        }

        var matchIndex = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (matchIndex < 0)
        {
            return default;
        }

        var start = Math.Max(0, matchIndex - SnippetWindowChars);
        var end = Math.Min(text.Length, matchIndex + term.Length + SnippetWindowChars);
        return new ContentMatchResult(text[start..end], ContentMatchFailure.None, null);
    }
}

/// <summary>Why <see cref="FileContentMatcher.Match"/> produced no snippet, when it failed.</summary>
public enum ContentMatchFailure
{
    None = 0,
    Encrypted = 1,
    Error = 2
}

/// <summary>Outcome of <see cref="FileContentMatcher.Match"/>: a snippet (match), or no snippet with an
/// optional failure kind and the exception that caused it.</summary>
public readonly record struct ContentMatchResult(string? Snippet, ContentMatchFailure Failure, Exception? Error);
