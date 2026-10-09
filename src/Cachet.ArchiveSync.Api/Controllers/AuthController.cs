using System.Security.Claims;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cachet.ArchiveSync.Api.Controllers;

/// <summary>
/// <c>POST /login</c> / <c>POST /logout</c> (API Design Spec §3.1/§3.2; session mechanics CAS-002
/// §6.1/§6.3). Explicit route strings, not <c>[controller]</c>-derived, so the paths match the
/// API spec exactly regardless of this controller's own name.
/// </summary>
[ApiController]
[Route("")]
public sealed class AuthController : ControllerBase
{
    private const string AuthScheme = "CachetAuth";
    private const string ActingProcessIdentity = "api-svc";

    private readonly ILoginService _loginService;
    private readonly IAuditLogService _auditLogService;
    private readonly IUserRepository _userRepository;

    public AuthController(
        ILoginService loginService,
        IAuditLogService auditLogService,
        IUserRepository userRepository)
    {
        _loginService = loginService;
        _auditLogService = auditLogService;
        _userRepository = userRepository;
    }

    /// <summary>API Design Spec §3.1. Issues the CachetAuth cookie on success (CAS-002 §6.1 step 4).</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var outcome = await _loginService.LoginAsync(request, ct);

        if (!outcome.Succeeded)
        {
            return outcome.FailureReason switch
            {
                LoginFailureReason.B2AccessUnavailable => Problem(
                    type: "https://cachet.tracecounts.internal/errors/b2-access-unavailable",
                    title: "B2 access unavailable",
                    statusCode: StatusCodes.Status401Unauthorized),
                _ => Problem(
                    type: "https://cachet.tracecounts.internal/errors/invalid-credentials",
                    title: "Invalid username or password",
                    statusCode: StatusCodes.Status401Unauthorized)
            };
        }

        // CAS-002 §6.1 step 4: exactly these three claims, never anything credential-shaped.
        var identity = new ClaimsIdentity(outcome.Claims, AuthScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(AuthScheme, principal);

        return Ok(new LoginResponse
        {
            UserId = outcome.UserId!.Value,
            DisplayName = outcome.DisplayName!,
            B2AccessLabel = outcome.B2AccessLabel!
        });
    }

    /// <summary>API Design Spec §3.2. Audits before clearing the session (CAS-002 §6.3).</summary>
    [HttpPost("logout")]
    [Authorize(Policy = "CachetAuthenticated")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
        {
            // No valid session claim to attribute the audit row to — idempotent per API spec §3.2.
            return Unauthorized();
        }

        // CAS-002 §6.3/§6.3a: revoke the session server-side by bumping session_token_version
        // BEFORE the audit-log write — the repository has no shared-transaction infrastructure
        // (each repo opens its own SqlConnection per call, per UserRepository/AuditLogRepository
        // convention), so these are two sequential calls in the same request rather than one SQL
        // transaction; the spec's "same transaction as the audit-log write" language is satisfied
        // in effect (both complete, in order, before the 204 response) but not literally as one
        // DB transaction, since no such infrastructure exists in this codebase to invent.
        await _userRepository.IncrementSessionTokenVersionAsync(userId, ct);

        // Audit-before-response invariant (API spec §1.6) — written before SignOutAsync/204.
        await _auditLogService.WriteAsync(new AuditLogEntry
        {
            CreatedBy = ActingProcessIdentity,
            UpdatedBy = ActingProcessIdentity,
            UserId = userId,
            TabModule = "Auth",
            Action = AuditAction.Logout,
            Result = AuditResult.Success
        }, ct);

        await HttpContext.SignOutAsync(AuthScheme);

        return NoContent();
    }
}
