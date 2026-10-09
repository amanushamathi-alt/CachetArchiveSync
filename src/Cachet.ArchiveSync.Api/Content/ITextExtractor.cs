namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// One in-scope file-type's text extractor (CAS-012 §5/§14) — dispatched by
/// <see cref="FileContentMatcher"/> per <c>fileExtension</c>. Internal: an implementation detail of
/// <see cref="FileContentMatcher"/>, not part of this project's shared <c>Core</c> contract.
/// </summary>
internal interface ITextExtractor
{
    /// <summary>True if this extractor knows how to extract text for <paramref name="fileExtension"/>
    /// (with or without a leading dot, compared case-insensitively).</summary>
    bool CanHandle(string fileExtension);

    /// <summary>
    /// Extracts this file's full text content, entirely in memory. Returns <c>null</c> or an empty
    /// string if the file has no extractable text. Throws if the bytes cannot be parsed as the
    /// declared <paramref name="fileExtension"/> (corrupt/unsupported content) — the caller
    /// (<see cref="FileContentMatcher"/>) treats any such exception as a skip for this one
    /// candidate (CAS-012 §7 V10), not a task failure.
    /// </summary>
    string? ExtractText(byte[] fileBytes, string fileExtension);
}
