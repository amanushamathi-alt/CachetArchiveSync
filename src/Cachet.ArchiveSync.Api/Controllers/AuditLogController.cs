using System.Security.Claims;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cachet.ArchiveSync.Api.Controllers;

/// <summary>
/// <c>GET /audit-log</c> (CAS-009 §6, FR-23/FR-24). Explicit route string, matching
/// <see cref="QuarantineController"/>'s/<see cref="SearchController"/>'s style. Not audited —
/// listing the audit log is a read, not an action FR-23 requires to be logged (§12).
/// </summary>
[ApiController]
[Route("")]
public sealed class AuditLogController : ControllerBase
{
    private readonly IAuditLogListService _auditLogListService;

    public AuditLogController(IAuditLogListService auditLogListService)
    {
        _auditLogListService = auditLogListService;
    }

    /// <summary>CAS-009 §6 — FR-23/FR-24, not audited (read-only, §12).</summary>
    [HttpGet("audit-log")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> List(
        [FromQuery] string? tab,
        [FromQuery] DateTimeOffset? dateFrom,
        [FromQuery] DateTimeOffset? dateTo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await _auditLogListService.ListAsync(tab, dateFrom, dateTo, page, pageSize, userId.Value, ct);

        return result.Outcome switch
        {
            AuditLogListOutcome.ValidationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/validation-failed",
                title: result.ValidationError ?? "Validation failed.",
                statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => Ok(result.Response)
        };
    }

    private int? GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim is not null && int.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
