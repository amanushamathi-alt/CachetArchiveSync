using System.Security.Claims;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cachet.ArchiveSync.Api.Controllers;

/// <summary>
/// <c>POST /reports/pre-action</c>, <c>POST /reports/post-action</c> (CAS-010 §6, FR-25/FR-26;
/// CAS-015 §2/§8 — on-demand generation, no server-side persistence). Explicit route strings,
/// matching <see cref="QuarantineController"/>'s and <see cref="AuditLogController"/>'s style. Both
/// routes require only <c>CachetAuthenticated</c> (§12 — report generation is read/aggregate, not
/// destructive). The former <c>GET /reports/{reportId}/pdf</c>/<c>/xlsx</c> retrieval routes are
/// removed outright (CAS-015 §8 SC-4) — there is no <c>reportId</c> to retrieve by, since nothing
/// survives past the response.
/// </summary>
[ApiController]
[Route("")]
public sealed class ReportsController : ControllerBase
{
    private readonly IPreActionReportService _preActionReportService;
    private readonly IPostActionReportService _postActionReportService;

    public ReportsController(
        IPreActionReportService preActionReportService,
        IPostActionReportService postActionReportService)
    {
        _preActionReportService = preActionReportService;
        _postActionReportService = postActionReportService;
    }

    /// <summary>CAS-010 §6.1 — FR-25, audited (§12); CAS-015 §8 — streams the requested
    /// <c>format</c> directly in the response instead of returning a JSON envelope.</summary>
    [HttpPost("reports/pre-action")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> PreAction([FromBody] ReportGenerationRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var username = GetUsername();
        if (userId is null || username is null)
        {
            return Unauthorized();
        }

        var result = await _preActionReportService.GenerateAsync(request, userId.Value, username, ct);

        return result.Outcome switch
        {
            ReportGenerationOutcome.ValidationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/validation-failed",
                title: result.ValidationError ?? "Validation failed.",
                statusCode: StatusCodes.Status400BadRequest),
            ReportGenerationOutcome.GenerationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/report-generation-failed",
                title: "Report generation failed.",
                statusCode: StatusCodes.Status500InternalServerError),
            _ => File(result.FileBytes!, result.ContentType!, result.DownloadFileName)
        };
    }

    /// <summary>CAS-010 §6.2 — FR-26, audited (§12); CAS-015 §8 — streams the requested
    /// <c>format</c> directly in the response instead of returning a JSON envelope.</summary>
    [HttpPost("reports/post-action")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> PostAction([FromBody] ReportGenerationRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var username = GetUsername();
        if (userId is null || username is null)
        {
            return Unauthorized();
        }

        var result = await _postActionReportService.GenerateAsync(request, userId.Value, username, ct);

        return result.Outcome switch
        {
            ReportGenerationOutcome.ValidationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/validation-failed",
                title: result.ValidationError ?? "Validation failed.",
                statusCode: StatusCodes.Status400BadRequest),
            ReportGenerationOutcome.GenerationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/report-generation-failed",
                title: "Report generation failed.",
                statusCode: StatusCodes.Status500InternalServerError),
            _ => File(result.FileBytes!, result.ContentType!, result.DownloadFileName)
        };
    }

    /// <summary>CAS-038 §6 — read-only preview backing CAS-032's <c>ReportModal.razor</c> (the
    /// on-screen table shown before a Pre-Action Impact Report is downloaded). Built from the exact
    /// same row-aggregation method <see cref="PreAction"/> uses (§3 V2/SC-3); not audited (§12).
    /// <paramref name="activeTabLabel"/> is accepted for URL compatibility with the Web caller but is
    /// cosmetic only — it never filters rows or appears in the response body (§7 V6).</summary>
    [HttpGet("reports/pre-action/preview")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> PreActionPreview([FromQuery] string? activeTabLabel, CancellationToken ct)
    {
        var userId = GetUserId();
        var username = GetUsername();
        if (userId is null || username is null)
        {
            return Unauthorized();
        }

        var response = await _preActionReportService.PreviewAsync(userId.Value, username, ct);

        return Ok(response);
    }

    /// <summary>CAS-038 §6 — read-only preview backing CAS-032's <c>ReportModal.razor</c> (the
    /// on-screen table shown before a Post-Action Outcome Report is downloaded). Built from the
    /// exact same row-aggregation method <see cref="PostAction"/> uses (§3 V2/SC-3), including its
    /// 100,000-row cap fail-loud rule (§7 V5); not audited (§12).
    /// <paramref name="activeTabLabel"/> is accepted for URL compatibility with the Web caller but is
    /// cosmetic only — it never filters rows or appears in the response body (§7 V6).</summary>
    [HttpGet("reports/post-action/preview")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> PostActionPreview([FromQuery] string? activeTabLabel, CancellationToken ct)
    {
        var userId = GetUserId();
        var username = GetUsername();
        if (userId is null || username is null)
        {
            return Unauthorized();
        }

        var result = await _postActionReportService.PreviewAsync(userId.Value, username, ct);

        return result.Outcome switch
        {
            ReportPreviewOutcome.GenerationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/report-generation-failed",
                title: "Report generation failed.",
                statusCode: StatusCodes.Status500InternalServerError),
            _ => Ok(result.Response)
        };
    }

    private int? GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim is not null && int.TryParse(userIdClaim, out var userId) ? userId : null;
    }

    private string? GetUsername() => User.FindFirst(ClaimTypes.Name)?.Value;
}
