namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// Extracts a file's text content (per file type, CAS-012 §2/§5/§14) and checks whether
/// <paramref name="term"/> is present, entirely in Worker process memory — no extracted text is
/// ever persisted or logged (§11). Used by <c>KeywordSearchTaskProcessor</c> for <c>Content</c>
/// candidates, after <see cref="IMirrorFileReader"/> has read the file's bytes.
/// </summary>
public interface IFileContentMatcher
{
    /// <summary>
    /// Returns a bounded snippet (±100 characters around the first match, §7 V12) if
    /// <paramref name="term"/> is found in <paramref name="fileBytes"/>'s extracted text for the
    /// given <paramref name="fileExtension"/>; otherwise <c>null</c> (§7 V11 — the candidate is
    /// dropped without a B2 cross-check). Also returns <c>null</c> for a file type this matcher does
    /// not know how to extract text from, or whose content cannot be extracted (§7 V10 — treated as
    /// a skip, not a failure, by the caller).
    /// </summary>
    Task<string?> TryMatchAsync(byte[] fileBytes, string fileExtension, string term, CancellationToken ct);

    /// <summary>
    /// Same contract as <see cref="TryMatchAsync"/> for plain-text file types only, but reads
    /// <paramref name="content"/> in chunks (stops at the first match) so a very large text file is
    /// never held in memory whole. Returns <c>null</c> for a non-plain-text extension.
    /// </summary>
    Task<string?> TryMatchStreamAsync(Stream content, string fileExtension, string term, CancellationToken ct);
}
