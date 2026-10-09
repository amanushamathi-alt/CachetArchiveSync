using System.Security.Claims;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cachet.ArchiveSync.Api.Controllers;

/// <summary>
/// <c>POST /search</c> / <c>GET /search/{taskId}/results</c> / <c>POST /search/results/quarantine</c>
/// (CAS-006 §6). Explicit route strings, not <c>[controller]</c>-derived, matching
/// <see cref="AuthController"/>'s style. <c>POST /search</c> and the result reads require an
/// authenticated session (<c>CachetAuthenticated</c>); <c>POST /search/results/quarantine</c> requires
/// <c>CachetFullAccess</c> (CAS-050 §2.8 — all Quarantine writes are Full Access only).
/// </summary>
[ApiController]
[Route("")]
public sealed class SearchController : ControllerBase
{
    private const string ErrorTypeBase = "https://cachet.tracecounts.internal/errors/";

    private readonly IKeywordSearchService _keywordSearchService;
    private readonly IQuarantineService _quarantineService;

    public SearchController(IKeywordSearchService keywordSearchService, IQuarantineService quarantineService)
    {
        _keywordSearchService = keywordSearchService;
        _quarantineService = quarantineService;
    }

    /// <summary>CAS-006 §6 — queued, audited before the 202 response.</summary>
    [HttpPost("search")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> Submit([FromBody] SearchRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _keywordSearchService.SubmitAsync(request, userId.Value, ct);

        if (result.Outcome == SearchSubmitOutcome.ValidationFailed)
        {
            return Problem(
                type: "https://cachet.tracecounts.internal/errors/validation-failed",
                title: result.ValidationError ?? "Validation failed.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Accepted(result.Response);
    }

    /// <summary>CAS-006 §6.2 — synchronous read of already-computed results, no cross-check, no write.</summary>
    [HttpGet("search/{taskId:int}/results")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> GetResults(int taskId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var result = await _keywordSearchService.GetResultsAsync(taskId, page, pageSize, ct);

        return result.Outcome switch
        {
            SearchResultsOutcome.TaskNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/task-not-found",
                title: "Task not found.",
                statusCode: StatusCodes.Status404NotFound),
            SearchResultsOutcome.TaskNotComplete => Problem(
                type: "https://cachet.tracecounts.internal/errors/task-not-complete",
                title: "Task has not completed yet.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Ok(result.Response)
        };
    }

    /// <summary>
    /// CAS-049 §8 — folders and matched files of one completed search task. Not audited (§11). The
    /// service is bound per action (<c>[FromServices]</c>) so the controller constructor, and the tests
    /// that build it, stay unchanged.
    /// </summary>
    [HttpGet("search/{taskId:int}/browse")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> Browse(
        int taskId,
        [FromServices] ISearchResultBrowseService browseService,
        [FromQuery] string? folder,
        [FromQuery] string? foldersOnly = null,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        // V3 — parsed here so the 400 carries the same ProblemDetails type; absent or empty means false.
        var foldersOnlyValue = false;
        if (!string.IsNullOrEmpty(foldersOnly) && !bool.TryParse(foldersOnly, out foldersOnlyValue))
        {
            return BrowseValidationProblem();
        }

        var result = await browseService.BrowseSearchResultsAsync(taskId, userId.Value, folder, foldersOnlyValue, ct);
        return result.Outcome == SearchResultBrowseOutcome.Success ? Ok(result.Response) : BrowseProblemFor(result.Outcome);
    }

    /// <summary>CAS-049 §8 — ids of every match at any depth beneath a folder; capped at 1,000. Not audited.</summary>
    [HttpGet("search/{taskId:int}/browse/result-ids")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> GetResultIds(
        int taskId,
        [FromServices] ISearchResultBrowseService browseService,
        [FromQuery] string? folder,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await browseService.GetResultIdsByFolderAsync(taskId, userId.Value, folder, ct);
        return result.Outcome == SearchResultBrowseOutcome.Success ? Ok(result.Response) : BrowseProblemFor(result.Outcome);
    }

    private ObjectResult BrowseValidationProblem() => Problem(
        type: ErrorTypeBase + "validation-failed",
        title: "'folder' must be a valid folder path of at most 1000 characters; 'foldersOnly' must be true or false.",
        statusCode: StatusCodes.Status400BadRequest);

    private ObjectResult BrowseProblemFor(SearchResultBrowseOutcome outcome)
    {
        switch (outcome)
        {
            case SearchResultBrowseOutcome.ValidationFailed:
                return BrowseValidationProblem();
            case SearchResultBrowseOutcome.TaskNotFound:
                return Problem(
                    type: ErrorTypeBase + "task-not-found",
                    title: "Task not found.",
                    statusCode: StatusCodes.Status404NotFound);
            case SearchResultBrowseOutcome.TaskNotComplete:
                return Problem(
                    type: ErrorTypeBase + "task-not-complete",
                    title: "Task has not completed yet.",
                    statusCode: StatusCodes.Status409Conflict);
            case SearchResultBrowseOutcome.FolderNotFound:
                return Problem(
                    type: ErrorTypeBase + "folder-not-found",
                    title: "Folder not found.",
                    statusCode: StatusCodes.Status404NotFound);
            case SearchResultBrowseOutcome.SelectionTooLarge:
                var details = ProblemDetailsFactory.CreateProblemDetails(
                    HttpContext,
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Too many files beneath this folder to select at once.",
                    type: ErrorTypeBase + "selection-too-large");
                details.Extensions["limit"] = ISearchResultBrowseService.ResultIdsLimit;
                return new ObjectResult(details) { StatusCode = StatusCodes.Status400BadRequest };
            case SearchResultBrowseOutcome.BucketNotConfigured:
                return Problem(
                    type: ErrorTypeBase + "bucket-not-configured",
                    title: "The archive bucket is not configured.",
                    statusCode: StatusCodes.Status500InternalServerError);
            case SearchResultBrowseOutcome.MappingMissing:
                return Problem(
                    type: ErrorTypeBase + "mirror-mapping-missing",
                    title: "No bucket mapping is configured for the archive.",
                    statusCode: StatusCodes.Status500InternalServerError);
            default:
                return Problem(
                    type: ErrorTypeBase + "browse-failed",
                    title: "The folder could not be listed.",
                    statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>CAS-006 §6.3 — audited before the 200 response (§12), partial-success per §7 V8.</summary>
    [HttpPost("search/results/quarantine")]
    [Authorize(Policy = "CachetFullAccess")]
    public async Task<IActionResult> AddToQuarantine([FromBody] QuarantineAddRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _quarantineService.AddAsync(request, userId.Value, ct);

        return result.Outcome switch
        {
            QuarantineAddOutcome.ValidationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/validation-failed",
                title: "'fileIds' must be non-empty.",
                statusCode: StatusCodes.Status422UnprocessableEntity),
            QuarantineAddOutcome.TaskNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/task-not-found",
                title: "Task not found.",
                statusCode: StatusCodes.Status404NotFound),
            QuarantineAddOutcome.FileNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/file-not-found",
                title: "One or more fileIds do not belong to this task.",
                statusCode: StatusCodes.Status404NotFound),
            QuarantineAddOutcome.AllAlreadyQuarantined => Problem(
                type: "https://cachet.tracecounts.internal/errors/file-already-quarantined",
                title: "All requested files are already actively quarantined.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Ok(result.Response)
        };
    }

    private int? GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim is not null && int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
