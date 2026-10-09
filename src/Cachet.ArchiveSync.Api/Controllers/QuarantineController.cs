using System.Security.Claims;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cachet.ArchiveSync.Api.Controllers;

/// <summary>
/// <c>GET /quarantine</c> / <c>POST /quarantine</c> / <c>POST /quarantine/delete</c> (CAS-008 §6,
/// FR-14/FR-15/FR-16/FR-17). Explicit route strings, matching <see cref="SearchController"/>'s and
/// <see cref="SyncController"/>'s style. Kept separate from <see cref="SearchController"/> (§15,
/// resolved 2026-09-22) since Quarantine is a distinct resource with a distinct auth-policy mix —
/// list/state require <c>CachetAuthenticated</c>; every write (add, remove, delete, browse add/remove)
/// requires <c>CachetFullAccess</c> (CAS-050 §2.8/§11, BRD §12.5 item 8).
/// </summary>
[ApiController]
[Route("")]
public sealed class QuarantineController : ControllerBase
{
    private readonly IQuarantineListService _quarantineListService;
    private readonly IQuarantineService _quarantineService;
    private readonly IQuarantinePermanentDeleteService _quarantinePermanentDeleteService;
    private readonly IQuarantineRemoveService _quarantineRemoveService;
    private readonly IQuarantineBrowseService _quarantineBrowseService;

    public QuarantineController(
        IQuarantineListService quarantineListService,
        IQuarantineService quarantineService,
        IQuarantinePermanentDeleteService quarantinePermanentDeleteService,
        IQuarantineRemoveService quarantineRemoveService,
        IQuarantineBrowseService quarantineBrowseService)
    {
        _quarantineListService = quarantineListService;
        _quarantineService = quarantineService;
        _quarantinePermanentDeleteService = quarantinePermanentDeleteService;
        _quarantineRemoveService = quarantineRemoveService;
        _quarantineBrowseService = quarantineBrowseService;
    }

    // Upper bound keeps the SP's (@page - 1) * @page_size inside INT (1,000,000 * 50 = 50M) — CAS-050 review D1.
    private const int MaxPage = 1_000_000;

    /// <summary>CAS-008 §6 — FR-14, not audited (read-only, §12). CAS-050 §8: paged — <c>pageSize</c>
    /// is 25 or 50 (default 25), <c>page</c> is 1..1,000,000 (default 1); anything else is 422 (§9 V7).</summary>
    [HttpGet("quarantine")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> GetActive(CancellationToken ct, [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        if (page < 1 || page > MaxPage || (pageSize != 25 && pageSize != 50))
        {
            return Problem(
                type: "https://cachet.tracecounts.internal/errors/validation-failed",
                title: $"'page' must be between 1 and {MaxPage} and 'pageSize' must be 25 or 50.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var response = await _quarantineListService.GetActiveAsync(userId.Value, page, pageSize, ct);
        return Ok(response);
    }

    /// <summary>CAS-050 §8 — Files-browser add (file, or all active files under a folder). Full Access
    /// only; one audit row per action, written before the 200 (FR-23).</summary>
    [HttpPost("quarantine/browse")]
    [Authorize(Policy = "CachetFullAccess")]
    public async Task<IActionResult> BrowseAdd([FromBody] QuarantineBrowseRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _quarantineBrowseService.AddAsync(request, userId.Value, ct);

        return result.Outcome switch
        {
            QuarantineBrowseAddOutcome.ValidationFailed => BrowseValidationProblem(),
            QuarantineBrowseAddOutcome.FileNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/file-not-found",
                title: "The file does not exist or is not active.",
                statusCode: StatusCodes.Status404NotFound),
            QuarantineBrowseAddOutcome.FolderNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/folder-not-found",
                title: "The folder holds no active files.",
                statusCode: StatusCodes.Status404NotFound),
            QuarantineBrowseAddOutcome.MappingMissing => BrowseMappingMissingProblem(),
            QuarantineBrowseAddOutcome.AlreadyQuarantined => Problem(
                type: "https://cachet.tracecounts.internal/errors/already-quarantined",
                title: "The file is already actively quarantined.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Ok(result.Response)
        };
    }

    /// <summary>CAS-050 §8 — Files-browser deselect (hard delete, all origins for a folder). Full Access
    /// only; one audit row per action, written before the 200 (FR-23).</summary>
    [HttpPost("quarantine/browse/remove")]
    [Authorize(Policy = "CachetFullAccess")]
    public async Task<IActionResult> BrowseRemove([FromBody] QuarantineBrowseRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _quarantineBrowseService.RemoveAsync(request, userId.Value, ct);

        return result.Outcome switch
        {
            QuarantineBrowseRemoveOutcome.ValidationFailed => BrowseValidationProblem(),
            QuarantineBrowseRemoveOutcome.MappingMissing => BrowseMappingMissingProblem(),
            QuarantineBrowseRemoveOutcome.NotQuarantined => Problem(
                type: "https://cachet.tracecounts.internal/errors/not-quarantined",
                title: "The file has no active quarantine entry.",
                statusCode: StatusCodes.Status404NotFound),
            QuarantineBrowseRemoveOutcome.FolderNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/folder-not-found",
                title: "The folder holds no active files.",
                statusCode: StatusCodes.Status404NotFound),
            _ => Ok(result.Response)
        };
    }

    /// <summary>CAS-050 §8 — quarantine state of one folder listing for the Files explorer. Read-only,
    /// not audited.</summary>
    [HttpGet("quarantine/browse/state")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> BrowseState(
        [FromQuery] string? source, [FromQuery] string? bucketName, [FromQuery] string? folderPath, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _quarantineBrowseService.GetStateAsync(source, bucketName, folderPath, ct);

        return result.Outcome switch
        {
            QuarantineBrowseStateOutcome.ValidationFailed => BrowseValidationProblem(),
            QuarantineBrowseStateOutcome.MappingMissing => BrowseMappingMissingProblem(),
            _ => Ok(result.Response)
        };
    }

    /// <summary>
    /// CAS-008 §0/§6 — literal route alias over the existing add operation
    /// (<see cref="IQuarantineService.AddAsync"/>, CAS-006 §6.3). Same policy/error mapping as
    /// <see cref="SearchController.AddToQuarantine"/> — no new validation, no new audit write (§7
    /// V8, §10 SC-2). CAS-050 §2.8: requires <c>CachetFullAccess</c>.
    /// </summary>
    [HttpPost("quarantine")]
    [Authorize(Policy = "CachetFullAccess")]
    public async Task<IActionResult> Add([FromBody] QuarantineAddRequest request, CancellationToken ct)
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

    /// <summary>CAS-008 §6.4 — Permanent Delete (FR-16/FR-17). Requires CachetFullAccess (§12) —
    /// a Read Only session never reaches this action's code.</summary>
    [HttpPost("quarantine/delete")]
    [Authorize(Policy = "CachetFullAccess")]
    public async Task<IActionResult> Delete([FromBody] QuarantineDeleteRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _quarantinePermanentDeleteService.DeleteAsync(request, userId.Value, ct);

        return result.Outcome switch
        {
            QuarantineDeleteOutcome.ValidationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/validation-failed",
                title: "'fileIds' must be non-empty.",
                statusCode: StatusCodes.Status422UnprocessableEntity),
            QuarantineDeleteOutcome.ConfirmationRequired => Problem(
                type: "https://cachet.tracecounts.internal/errors/confirmation-required",
                title: "'confirm' must be true.",
                statusCode: StatusCodes.Status422UnprocessableEntity),
            QuarantineDeleteOutcome.AllFilesNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/file-not-found",
                title: "None of the requested fileIds resolve to an active quarantine entry.",
                statusCode: StatusCodes.Status404NotFound),
            QuarantineDeleteOutcome.AuthorizationDenied => Problem(
                type: "https://cachet.tracecounts.internal/errors/authorization-denied",
                title: "Live B2 authorization for the acting user failed or lacks delete access.",
                statusCode: StatusCodes.Status403Forbidden),
            _ => Ok(result.Response)
        };
    }

    /// <summary>CAS-014 §2.2/§8 — "Remove Item" (BRD §12.3 items 3, 7). A catalog-only action, no B2
    /// write, no confirm gate. CAS-050 §2.8: requires CachetFullAccess (was CachetAuthenticated).</summary>
    [HttpPost("quarantine/remove")]
    [Authorize(Policy = "CachetFullAccess")]
    public async Task<IActionResult> Remove([FromBody] QuarantineRemoveRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _quarantineRemoveService.RemoveAsync(request, userId.Value, ct);

        return result.Outcome switch
        {
            QuarantineRemoveOutcome.ValidationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/validation-failed",
                title: "'fileIds' must be non-empty.",
                statusCode: StatusCodes.Status422UnprocessableEntity),
            QuarantineRemoveOutcome.AllFilesNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/file-not-found",
                title: "None of the requested fileIds resolve to an active quarantine entry.",
                statusCode: StatusCodes.Status404NotFound),
            _ => Ok(result.Response)
        };
    }

    private ObjectResult BrowseValidationProblem() => Problem(
        type: "https://cachet.tracecounts.internal/errors/validation-failed",
        title: "Invalid request: 'scope' must be File (needs 'fileIndexId') or Folder (needs 'source' ODrive|B2, 'bucketName' and a non-empty 'folderPath' without leading/trailing '/').",
        statusCode: StatusCodes.Status422UnprocessableEntity);

    /// <summary>CAS-053 — same ProblemDetails as <c>GET /files/browse</c> when no bucket mapping row exists.</summary>
    private ObjectResult BrowseMappingMissingProblem() => Problem(
        type: "https://cachet.tracecounts.internal/errors/mirror-mapping-missing",
        title: "No bucket mapping is configured for the archive.",
        statusCode: StatusCodes.Status500InternalServerError);

    private int? GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim is not null && int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
