using System.Security.Claims;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cachet.ArchiveSync.Api.Controllers;

/// <summary>
/// <c>POST /tasks/{taskId}/restart</c> (CAS-016 §8) — the first Task Manager API surface in this
/// codebase. CAS-030 §6 adds <c>GET /tasks</c>, <c>GET /tasks/{taskId}</c>, <c>GET /tasks/summary</c>
/// and <c>POST /tasks/{taskId}/acknowledge</c>, all own-scoped by the claim-derived user id
/// CAS-056 §2.1 adds <c>POST /tasks/{taskId}/cancel</c> (Keyword Search only; own-scoped).
/// </summary>
[ApiController]
[Route("")]
public sealed class TaskManagerController : ControllerBase
{
    // CAS-030 §7 V2 — exact, case-sensitive values enforced by CK_task_queue_*.
    private static readonly HashSet<string> AllowedTaskTypes = new(StringComparer.Ordinal)
    {
        "KeywordSearch", "ContentScan", "SyncCopy", "SyncUpdate", "SyncPurge"
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "Queued", "Running", "Completed", "Failed", "Cancelled"
    };

    private const string ValidationFailedType = "https://cachet.tracecounts.internal/errors/validation-failed";
    private const string TaskNotFoundType = "https://cachet.tracecounts.internal/errors/task-not-found";
    private const string TaskNotFoundTitle = "Task not found.";

    private readonly ITaskManagerService _taskManagerService;

    public TaskManagerController(ITaskManagerService taskManagerService)
    {
        _taskManagerService = taskManagerService;
    }

    /// <summary>CAS-016 §8 — audited before the 202 response (§11, FR-23); submitter-only (§9 V5).</summary>
    [HttpPost("tasks/{taskId:int}/restart")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> Restart(int taskId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _taskManagerService.RestartAsync(taskId, userId.Value, ct);

        return result.Outcome switch
        {
            // §8/§11: NotOwner is returned regardless of the row's actual status, so a non-owner's
            // response never leaks whether the task is Failed/Queued/Running/etc.
            RestartTaskOutcome.NotOwner => Problem(
                type: "https://cachet.tracecounts.internal/errors/task-not-owner",
                title: "You may only restart a task you originally submitted.",
                statusCode: StatusCodes.Status403Forbidden),
            RestartTaskOutcome.NotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/task-not-found",
                title: "Task not found.",
                statusCode: StatusCodes.Status404NotFound),
            RestartTaskOutcome.NotRestartable => Problem(
                type: "https://cachet.tracecounts.internal/errors/task-not-restartable",
                title: "Task is not currently in a Failed state.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Accepted(new { taskId = result.NewTaskQueueId, restartedFromTaskId = taskId })
        };
    }

    /// <summary>CAS-030 §6 — the caller's own tasks, newest first (FR-19). Not audited (§12).</summary>
    [HttpGet("tasks")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> List(
        [FromQuery] string? type,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        // V1
        if (page < 1 || pageSize < 1 || pageSize > 100)
        {
            return Problem(
                type: ValidationFailedType,
                title: "page must be >= 1 and pageSize must be between 1 and 100.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // V2
        if ((type is not null && !AllowedTaskTypes.Contains(type))
            || (status is not null && !AllowedStatuses.Contains(status)))
        {
            return Problem(
                type: ValidationFailedType,
                title: "type or status is not a recognized value.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await _taskManagerService.ListAsync(userId.Value, type, status, page, pageSize, ct);
        return Ok(result);
    }

    /// <summary>
    /// CAS-030 §6 — badge counts (FR-22). Declared before <c>tasks/{taskId:int}</c> for readability;
    /// the <c>:int</c> constraint already keeps <c>summary</c> from matching that route.
    /// </summary>
    [HttpGet("tasks/summary")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _taskManagerService.GetSummaryAsync(userId.Value, ct);
        return Ok(result);
    }

    /// <summary>
    /// CAS-030 §6 — one of the caller's own tasks. A missing and a not-owned task return the same
    /// <c>404 task-not-found</c> body (V3).
    /// </summary>
    [HttpGet("tasks/{taskId:int}")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> Get(int taskId, CancellationToken ct)
    {
        var userId = GetUserId();
        var username = User.FindFirst(ClaimTypes.Name)?.Value;
        if (userId is null || string.IsNullOrEmpty(username))
        {
            return Unauthorized();
        }

        var result = await _taskManagerService.GetAsync(taskId, userId.Value, username, ct);

        return result is null ? TaskNotFound() : Ok(result);
    }

    /// <summary>
    /// CAS-030 §6 — records that the owner viewed a finished task (idempotent, V5). Not audited
    /// (§12, §15 Q2). 404 body identical to <see cref="Get"/>'s (V3).
    /// </summary>
    [HttpPost("tasks/{taskId:int}/acknowledge")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> Acknowledge(int taskId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var outcome = await _taskManagerService.AcknowledgeAsync(taskId, userId.Value, ct);

        return outcome switch
        {
            AcknowledgeTaskOutcome.NotFound => TaskNotFound(),
            AcknowledgeTaskOutcome.NotTerminal => Problem(
                type: "https://cachet.tracecounts.internal/errors/task-not-terminal",
                title: "Task is not in a Completed or Failed state.",
                statusCode: StatusCodes.Status409Conflict),
            _ => NoContent()
        };
    }

    /// <summary>
    /// CAS-056 §2.1 — cancels the caller's own Keyword Search. <c>200</c> Queued (cancelled now),
    /// <c>202</c> Running (flag set; <c>Cancelling</c> is a response label, not a stored status),
    /// <c>404</c> missing or not owned (body identical to <see cref="Get"/>'s), <c>409</c> terminal
    /// or not cancellable. Audited inside the stored procedure before the response (FR-23).
    /// </summary>
    [HttpPost("tasks/{taskId:int}/cancel")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> Cancel(int taskId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _taskManagerService.CancelAsync(taskId, userId.Value, ct);

        return result.Outcome switch
        {
            CancelTaskOutcome.Cancelled => Ok(new { taskId, status = "Cancelled" }),
            CancelTaskOutcome.CancelRequested => Accepted(new { taskId, status = "Cancelling" }),
            CancelTaskOutcome.NotFound => TaskNotFound(),
            _ => Problem(
                type: "https://cachet.tracecounts.internal/errors/task-not-cancellable",
                title: "Task cannot be cancelled.",
                statusCode: StatusCodes.Status409Conflict)
        };
    }

    private IActionResult TaskNotFound() => Problem(
        type: TaskNotFoundType,
        title: TaskNotFoundTitle,
        statusCode: StatusCodes.Status404NotFound);

    private int? GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim is not null && int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
