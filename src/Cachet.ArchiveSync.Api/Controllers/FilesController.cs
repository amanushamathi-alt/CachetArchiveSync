using System.Security.Claims;
using System.Text;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cachet.ArchiveSync.Api.Controllers;

/// <summary>
/// <c>GET /files/{fileIndexId}</c>, <c>/view</c>, <c>/download</c> (CAS-040 §6, proposed
/// FR-28/FR-28a/FR-28b via CR-001). Explicit route strings, matching
/// <see cref="QuarantineController"/>'s and <see cref="ReportsController"/>'s style. All the
/// resolution, rendering and audit logic lives in <see cref="IFileContentService"/>; by the time
/// this controller sees a success result, the audit row is already written (V10). No B2 client,
/// credential or presigned URL is a dependency here (§12, SC-12). Range requests, ETag and
/// Last-Modified are never emitted (§4).
/// </summary>
[ApiController]
[Route("")]
public sealed class FilesController : ControllerBase
{
    private const string ErrorTypeBase = "https://cachet.tracecounts.internal/errors/";

    // §6.2 — exact CSP values.
    private const string HtmlPreviewCsp =
        "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'; sandbox";
    private const string DefaultViewCsp = "frame-ancestors 'self'";

    private readonly IFileContentService _fileContentService;
    private readonly IFileBrowseService _fileBrowseService;
    private readonly ILogger<FilesController> _logger;

    public FilesController(
        IFileContentService fileContentService, IFileBrowseService fileBrowseService, ILogger<FilesController> logger)
    {
        _fileContentService = fileContentService;
        _fileBrowseService = fileBrowseService;
        _logger = logger;
    }

    /// <summary>
    /// CAS-043 §6 (proposed FR-29) — direct child folders and files of one folder, from
    /// <c>dbo.file_index</c> metadata. Not audited (§12). The literal <c>browse</c> segment does not
    /// match the <c>int</c> constraint on <c>files/{fileIndexId}</c>, so the routes do not collide.
    /// </summary>
    [HttpGet("files/browse")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> Browse(
        [FromQuery] string? folder,
        [FromQuery] string? foldersOnly = null,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        // V4 — parsed here (not by model binding) so the 400 carries the same ProblemDetails type
        // as the other validation failures; absent or empty means false.
        var foldersOnlyValue = false;
        if (!string.IsNullOrEmpty(foldersOnly) && !bool.TryParse(foldersOnly, out foldersOnlyValue))
        {
            return BrowseValidationProblem();
        }

        var result = await _fileBrowseService.BrowseAsync(folder, foldersOnlyValue, userId.Value, ct);
        return result.Outcome switch
        {
            FileBrowseOutcome.Success => Ok(result.Response),
            FileBrowseOutcome.ValidationFailed => BrowseValidationProblem(),
            FileBrowseOutcome.FolderNotFound => Problem(
                type: ErrorTypeBase + "folder-not-found",
                title: "Folder not found.",
                statusCode: StatusCodes.Status404NotFound),
            FileBrowseOutcome.BucketNotConfigured => Problem(
                type: ErrorTypeBase + "bucket-not-configured",
                title: "The archive bucket is not configured.",
                statusCode: StatusCodes.Status500InternalServerError),
            FileBrowseOutcome.MappingMissing => Problem(
                type: ErrorTypeBase + "mirror-mapping-missing",
                title: "No bucket mapping is configured for the archive.",
                statusCode: StatusCodes.Status500InternalServerError),
            _ => Problem(
                type: ErrorTypeBase + "browse-failed",
                title: "The folder could not be listed.",
                statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private ObjectResult BrowseValidationProblem() => Problem(
        type: ErrorTypeBase + "validation-failed",
        title: "'folder' must be a valid folder path of at most 1000 characters; 'foldersOnly' must be true or false.",
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>CAS-040 §6.1 — metadata and computed view mode. Not audited (§12, §15 Q5).</summary>
    [HttpGet("files/{fileIndexId:int:min(1)}")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> GetMetadata(int fileIndexId, [FromQuery] string? context, CancellationToken ct)
    {
        if (GetUserId() is null)
        {
            return Unauthorized();
        }

        var result = await _fileContentService.GetMetadataAsync(fileIndexId, context, ct);
        return result.Outcome == FileContentOutcome.Success ? Ok(result.Metadata) : ProblemFor(result);
    }

    /// <summary>CAS-040 §6.2 — read-only rendering; <c>FileViewed</c> audited before the first byte.</summary>
    [HttpGet("files/{fileIndexId:int:min(1)}/view")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> View(int fileIndexId, [FromQuery] string? context, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _fileContentService.GetViewAsync(fileIndexId, context, userId.Value, ct);
        if (result.Outcome != FileContentOutcome.Success)
        {
            return ProblemFor(result);
        }

        ApplySecurityHeaders();
        Response.Headers.ContentDisposition = BuildContentDisposition("inline", result.FileName!);
        Response.Headers.ContentSecurityPolicy =
            result.ViewMode == FileViewModes.HtmlPreview ? HtmlPreviewCsp : DefaultViewCsp;

        if (result.PreviewTruncated is { } truncated)
        {
            Response.Headers["X-Cachet-Preview-Truncated"] = truncated ? "true" : "false";
        }

        if (result.SourceEncoding is not null)
        {
            Response.Headers["X-Cachet-Source-Encoding"] = result.SourceEncoding;
        }

        return StreamResult(fileIndexId, result);
    }

    /// <summary>
    /// CAS-040 §6.3 — the original bytes, unchanged; <c>FileDownloaded</c> audited before the
    /// first byte. CR-001 D2 option (a): <c>CachetAuthenticated</c> (option (b) would change this
    /// one attribute to <c>CachetFullAccess</c>).
    /// </summary>
    [HttpGet("files/{fileIndexId:int:min(1)}/download")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> Download(int fileIndexId, [FromQuery] string? context, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _fileContentService.GetDownloadAsync(fileIndexId, context, userId.Value, ct);
        if (result.Outcome != FileContentOutcome.Success)
        {
            return ProblemFor(result);
        }

        ApplySecurityHeaders();
        Response.Headers.ContentDisposition = BuildContentDisposition("attachment", result.FileName!);

        return StreamResult(fileIndexId, result);
    }

    private FileStreamResult StreamResult(int fileIndexId, FileAccessResult result)
    {
        var stream = result.Content!;

        // §11 — log a client abort mid-stream (the Success audit row stays; the log is append-only).
        var registration = HttpContext.RequestAborted.Register(() =>
        {
            long bytesSent;
            try
            {
                bytesSent = stream.Position;
            }
            catch (ObjectDisposedException)
            {
                bytesSent = -1;
            }

            _logger.LogInformation(
                "File stream aborted by client for {FileIndexId} after {BytesSent} bytes", fileIndexId, bytesSent);
        });
        Response.RegisterForDispose(registration);

        // No FileDownloadName (Content-Disposition is set explicitly above), no range processing,
        // no ETag / Last-Modified (§4) — so exactly one audit row per view/download.
        return new FileStreamResult(stream, result.ContentType!)
        {
            EnableRangeProcessing = false
        };
    }

    private void ApplySecurityHeaders()
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.XFrameOptions = "SAMEORIGIN";
    }

    /// <summary>
    /// <c>&lt;type&gt;; filename="&lt;ascii-safe&gt;"; filename*=UTF-8''&lt;pct-encoded&gt;</c> (§6.2).
    /// The ASCII fallback replaces non-printable/non-ASCII characters, quotes and backslashes with
    /// <c>_</c>, so no document-originated value can break out of the header.
    /// </summary>
    private static string BuildContentDisposition(string dispositionType, string fileName)
    {
        var ascii = new StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            ascii.Append(ch is < (char)0x20 or > (char)0x7E or '"' or '\\' ? '_' : ch);
        }

        return $"{dispositionType}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private IActionResult ProblemFor(FileAccessResult result) => result.Outcome switch
    {
        FileContentOutcome.ValidationFailed => Problem(
            type: ErrorTypeBase + "validation-failed",
            title: "'context' is required and must be one of KeywordSearch, ODriveSync, Quarantine, FileBrowser.",
            statusCode: StatusCodes.Status400BadRequest),
        FileContentOutcome.FileNotFound => Problem(
            type: ErrorTypeBase + "file-not-found",
            title: "File not found.",
            statusCode: StatusCodes.Status404NotFound),
        FileContentOutcome.MissingFromMirror => Problem(
            type: ErrorTypeBase + "file-missing-from-mirror",
            title: "The file could not be found in the local mirror.",
            statusCode: StatusCodes.Status404NotFound),
        FileContentOutcome.NoLongerAvailable => Problem(
            type: ErrorTypeBase + "file-no-longer-available",
            title: "This file is no longer in the archive.",
            statusCode: StatusCodes.Status410Gone),
        FileContentOutcome.UnsupportedType => Problem(
            type: ErrorTypeBase + "file-type-not-supported",
            title: "This file type can't be opened here.",
            statusCode: StatusCodes.Status415UnsupportedMediaType),
        FileContentOutcome.MappingMissing => Problem(
            type: ErrorTypeBase + "mirror-mapping-missing",
            title: "No mirror mapping is configured for this file.",
            statusCode: StatusCodes.Status500InternalServerError),
        FileContentOutcome.PathRejected => Problem(
            type: ErrorTypeBase + "file-path-rejected",
            title: "The file path was rejected.",
            statusCode: StatusCodes.Status500InternalServerError),
        FileContentOutcome.PreviewNotAvailable => PreviewNotAvailable(result.PreviewReason),
        _ => Problem(
            type: ErrorTypeBase + "audit-write-failed",
            title: "The audit record could not be written; no content was sent.",
            statusCode: StatusCodes.Status500InternalServerError)
    };

    private ObjectResult PreviewNotAvailable(string? reason)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "A preview is not available for this file. It can still be downloaded.",
            type: ErrorTypeBase + "preview-not-available");
        problem.Extensions["reason"] = reason;

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status422UnprocessableEntity,
            ContentTypes = { "application/problem+json" }
        };
    }

    private int? GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim is not null && int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
