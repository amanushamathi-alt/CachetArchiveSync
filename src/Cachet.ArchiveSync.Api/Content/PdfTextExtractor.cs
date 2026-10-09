using System.Text;
using UglyToad.PdfPig;

namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// <see cref="ITextExtractor"/> for <c>.pdf</c> (CAS-012 §5/§14), using UglyToad.PdfPig — a fully
/// managed PDF reader, so a malformed/hostile PDF surfaces as a catchable exception (skipped by
/// <see cref="FileContentMatcher"/>) rather than crashing the process. Replaces PdfOxide, whose
/// native Rust core aborted the Worker (0xc0000409 in pdf_oxide.dll) on some files; a native abort
/// cannot be caught by managed try/catch. A scanned/image-only PDF (no embedded text layer) yields
/// no text and simply produces zero content matches — accepted scope limitation, not a defect
/// (CAS-012 §14).
/// </summary>
internal sealed class PdfTextExtractor : ITextExtractor
{
    public bool CanHandle(string fileExtension) =>
        string.Equals(Normalize(fileExtension), "pdf", StringComparison.OrdinalIgnoreCase);

    public string? ExtractText(byte[] fileBytes, string fileExtension)
    {
        using var document = PdfDocument.Open(fileBytes);
        var text = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            text.Append(page.Text).Append('\n');
        }
        return text.ToString();
    }

    private static string Normalize(string fileExtension) => fileExtension.TrimStart('.');
}
