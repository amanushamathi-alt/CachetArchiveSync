using System.Security.Claims;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cachet.ArchiveSync.Api.Controllers;

/// <summary>
/// <c>POST /sync/scan</c> / <c>GET /sync/{taskId}/results</c> /
/// <c>POST /sync/actions/{copy,update,purge}</c> (CAS-007 §6) / <c>POST /sync/mirror-sync</c>
/// (CAS-037 §6). Explicit route strings, matching <see cref="SearchController"/>'s style.
/// Scan/results/mirror-sync require only an authenticated session (<c>CachetAuthenticated</c>); the
/// three write-side actions require <c>CachetFullAccess</c> (§12).
/// </summary>
[ApiController]
[Route("")]
public sealed class SyncController : ControllerBase
{
    private readonly ISyncScanService _syncScanService;
    private readonly ISyncActionService _syncActionService;
    private readonly IB2MirrorSyncService _b2MirrorSyncService;

    public SyncController(
        ISyncScanService syncScanService,
        ISyncActionService syncActionService,
        IB2MirrorSyncService b2MirrorSyncService)
    {
        _syncScanService = syncScanService;
        _syncActionService = syncActionService;
        _b2MirrorSyncService = b2MirrorSyncService;
    }

    /// <summary>CAS-007 §6 — queued, audited before the 202 response.</summary>
    [HttpPost("sync/scan")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> SubmitScan(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _syncScanService.SubmitAsync(userId.Value, ct);
        return Accepted(result.Response);
    }

    /// <summary>CAS-007 §6.2 — synchronous read of already-computed results.</summary>
    [HttpGet("sync/{taskId:int}/results")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> GetResults(
        int taskId,
        [FromQuery] int newOnODrivePage = 1,
        [FromQuery] int newOnODrivePageSize = 50,
        [FromQuery] int removedFromODrivePage = 1,
        [FromQuery] int removedFromODrivePageSize = 50,
        [FromQuery] int modifiedOnODrivePage = 1,
        [FromQuery] int modifiedOnODrivePageSize = 50,
        [FromQuery] bool excludeSynced = false,
        CancellationToken ct = default)
    {
        // CAS-045 §8.2 — reject invalid paging before any service/DB call; names the parameter only.
        var invalidParameter = FindInvalidPagingParameter(
            (nameof(newOnODrivePage), newOnODrivePage, nameof(newOnODrivePageSize), newOnODrivePageSize),
            (nameof(removedFromODrivePage), removedFromODrivePage, nameof(removedFromODrivePageSize), removedFromODrivePageSize),
            (nameof(modifiedOnODrivePage), modifiedOnODrivePage, nameof(modifiedOnODrivePageSize), modifiedOnODrivePageSize));
        if (invalidParameter is not null)
        {
            return Problem(
                type: "https://cachet.tracecounts.internal/errors/invalid-paging",
                title: $"'{invalidParameter}' is out of range: pages must be >= 1 and page sizes must be between 1 and 100.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _syncScanService.GetResultsAsync(
            taskId,
            newOnODrivePage, newOnODrivePageSize,
            removedFromODrivePage, removedFromODrivePageSize,
            modifiedOnODrivePage, modifiedOnODrivePageSize,
            excludeSynced,
            ct);

        return result.Outcome switch
        {
            SyncResultsOutcome.TaskNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/task-not-found",
                title: "Task not found.",
                statusCode: StatusCodes.Status404NotFound),
            SyncResultsOutcome.TaskNotComplete => Problem(
                type: "https://cachet.tracecounts.internal/errors/task-not-complete",
                title: "Task has not completed yet.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Ok(result.Response)
        };
    }

    /// <summary>CAS-007 §6 — queued, audited before the 202 response. Requires CachetFullAccess (§12).</summary>
    [HttpPost("sync/actions/copy")]
    [Authorize(Policy = "CachetFullAccess")]
    public async Task<IActionResult> SubmitCopy([FromBody] SyncActionRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _syncActionService.SubmitCopyAsync(request, userId.Value, ct);
        return MapActionResult(result);
    }

    /// <summary>CAS-007 §6 — queued, audited before the 202 response. Requires CachetFullAccess (§12).</summary>
    [HttpPost("sync/actions/update")]
    [Authorize(Policy = "CachetFullAccess")]
    public async Task<IActionResult> SubmitUpdate([FromBody] SyncActionRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _syncActionService.SubmitUpdateAsync(request, userId.Value, ct);
        return MapActionResult(result);
    }

    /// <summary>CAS-007 §6 — queued, audited before the 202 response. Requires CachetFullAccess (§12).</summary>
    [HttpPost("sync/actions/purge")]
    [Authorize(Policy = "CachetFullAccess")]
    public async Task<IActionResult> SubmitPurge([FromBody] SyncPurgeRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _syncActionService.SubmitPurgeAsync(request, userId.Value, ct);
        return MapActionResult(result);
    }

    /// <summary>
    /// CAS-037 §6 — queued, audited before the 202 response. No request body — the sync scope is
    /// always the whole configured B2 bucket. 409 when a B2MirrorSync task is already
    /// Queued/Running (§7 V1).
    /// </summary>
    [HttpPost("sync/mirror-sync")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> SubmitMirrorSync(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _b2MirrorSyncService.SubmitAsync(userId.Value, ct);

        return result.Outcome switch
        {
            B2MirrorSyncSubmitOutcome.AlreadyRunning => Problem(
                type: "https://cachet.tracecounts.internal/errors/mirror-sync-already-running",
                title: "A B2MirrorSync task is already queued or running.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Accepted(result.Response)
        };
    }

    private IActionResult MapActionResult(SyncActionSubmitResult result) => result.Outcome switch
    {
        SyncActionSubmitOutcome.ValidationFailed => Problem(
            type: "https://cachet.tracecounts.internal/errors/validation-failed",
            title: "'fileIds' must be non-empty and at most Sync:MaxBatchSize in length.",
            statusCode: StatusCodes.Status422UnprocessableEntity),
        SyncActionSubmitOutcome.ConfirmationRequired => Problem(
            type: "https://cachet.tracecounts.internal/errors/confirmation-required",
            title: "'confirm' must be true.",
            statusCode: StatusCodes.Status422UnprocessableEntity),
        SyncActionSubmitOutcome.TaskNotFound => Problem(
            type: "https://cachet.tracecounts.internal/errors/task-not-found",
            title: "Task not found.",
            statusCode: StatusCodes.Status404NotFound),
        SyncActionSubmitOutcome.FileNotFound => Problem(
            type: "https://cachet.tracecounts.internal/errors/file-not-found",
            title: "One or more fileIds do not resolve to an eligible sync_result row for this task.",
            statusCode: StatusCodes.Status404NotFound),
        _ => Accepted(result.Response)
    };

    private static string? FindInvalidPagingParameter(
        params (string PageName, int Page, string PageSizeName, int PageSize)[] panels)
    {
        foreach (var (pageName, page, pageSizeName, pageSize) in panels)
        {
            if (page < 1)
            {
                return pageName;
            }

            if (pageSize < 1 || pageSize > 100)
            {
                return pageSizeName;
            }
        }

        return null;
    }

    private int? GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim is not null && int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
