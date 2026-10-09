using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Core.Interfaces;

/// <summary>
/// CAS-040 §6 — the File Viewer's shared resolution pipeline, behind
/// <c>GET /files/{fileIndexId}</c>, <c>/view</c> and <c>/download</c>. Never touches B2: no B2
/// client, credential or presigned URL is in scope (§12, SC-12). <c>/view</c> and
/// <c>/download</c> await their audit write before returning (V10).
/// </summary>
public interface IFileContentService
{
    /// <summary>§6.1 — pipeline steps 1–5 plus signature sniff and size caps. Not audited.</summary>
    Task<FileAccessResult> GetMetadataAsync(int fileIndexId, string? context, CancellationToken ct);

    /// <summary>§6.2 — renders the view-mode body and audits <c>FileViewed</c> before returning.</summary>
    Task<FileAccessResult> GetViewAsync(int fileIndexId, string? context, int actingUserId, CancellationToken ct);

    /// <summary>§6.3 — opens the original bytes and audits <c>FileDownloaded</c> before returning.</summary>
    Task<FileAccessResult> GetDownloadAsync(int fileIndexId, string? context, int actingUserId, CancellationToken ct);
}

/// <summary>CAS-040 §7 outcomes, mapped by <c>FilesController</c> to status codes/ProblemDetails.</summary>
public enum FileContentOutcome
{
    Success,

    /// <summary>V2 — <c>context</c> missing or unknown. 400, not audited.</summary>
    ValidationFailed,

    /// <summary>V3 — 404 <c>file-not-found</c>.</summary>
    FileNotFound,

    /// <summary>V4 — 410 <c>file-no-longer-available</c>.</summary>
    NoLongerAvailable,

    /// <summary>V5 — 415 <c>file-type-not-supported</c>.</summary>
    UnsupportedType,

    /// <summary>V6 — 500 <c>mirror-mapping-missing</c>.</summary>
    MappingMissing,

    /// <summary>V7 — 500 <c>file-path-rejected</c>.</summary>
    PathRejected,

    /// <summary>V8 — 404 <c>file-missing-from-mirror</c>.</summary>
    MissingFromMirror,

    /// <summary>V9 — 422 <c>preview-not-available</c> (<c>/view</c> only).</summary>
    PreviewNotAvailable,

    /// <summary>V10 — 500 <c>audit-write-failed</c>; no content is returned.</summary>
    AuditWriteFailed
}

/// <summary>
/// Result of one <see cref="IFileContentService"/> call. On a <c>/view</c> or <c>/download</c>
/// <see cref="FileContentOutcome.Success"/>, <see cref="Content"/> is an open stream the caller
/// must dispose (normally by handing it to a <c>FileStreamResult</c>); otherwise it is <c>null</c>.
/// </summary>
public sealed record FileAccessResult
{
    public required FileContentOutcome Outcome { get; init; }

    /// <summary>§6.1 body — set on a metadata success.</summary>
    public FileMetadataResponse? Metadata { get; init; }

    /// <summary>V9 <c>reason</c>: <c>TooLarge</c>, <c>SignatureMismatch</c>, <c>ConversionFailed</c>,
    /// <c>Timeout</c> or <c>ArchiveGuard</c>.</summary>
    public string? PreviewReason { get; init; }

    public Stream? Content { get; init; }

    /// <summary>Full <c>Content-Type</c> value, including any charset parameter.</summary>
    public string? ContentType { get; init; }

    /// <summary>Original file name, for <c>Content-Disposition</c>.</summary>
    public string? FileName { get; init; }

    /// <summary>View mode of a <c>/view</c> success (drives CSP and <c>X-Cachet-*</c> headers).</summary>
    public string? ViewMode { get; init; }

    /// <summary><c>X-Cachet-Preview-Truncated</c> (InlineText/ExtractedText/HtmlPreview).</summary>
    public bool? PreviewTruncated { get; init; }

    /// <summary><c>X-Cachet-Source-Encoding</c> (InlineText only).</summary>
    public string? SourceEncoding { get; init; }
}
