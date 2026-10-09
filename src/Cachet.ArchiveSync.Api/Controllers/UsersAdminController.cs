using Cachet.ArchiveSync.Api.Filters;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Cachet.ArchiveSync.Api.Controllers;

/// <summary>
/// <c>POST /admin/users</c> / <c>PUT /admin/users/{userId}</c> /
/// <c>PATCH /admin/users/{userId}/deactivate</c> / <c>PATCH /admin/users/{userId}/reactivate</c>
/// (CAS-020 §3/§6). Admin-only account provisioning — gated by <see cref="AdminProvisioningTokenFilter"/>
/// (a static <c>X-Admin-Provisioning-Key</c> header, §12), not the cookie-session
/// <c>[Authorize]</c> policies every other controller uses.
///
/// Visible in Swagger (post-review amendment, 2026-09-23, confirmed with Rakesh Devarajan):
/// CAS-020 §12/§15 originally hid this controller from the Swagger "Try it out" surface per the
/// Security Analysis §5 recommendation. That recommendation only reduces discoverability — the
/// <see cref="AdminProvisioningTokenFilter"/> gate is unchanged and still enforces the same
/// authorization regardless of Swagger visibility. Made visible to allow internal Swagger-based
/// testing; see CAS-020 §12 for the updated note.
/// </summary>
[ApiController]
[Route("")]
[ServiceFilter(typeof(AdminProvisioningTokenFilter))]
public sealed class UsersAdminController : ControllerBase
{
    private readonly IUserProvisioningService _userProvisioningService;

    public UsersAdminController(IUserProvisioningService userProvisioningService)
    {
        _userProvisioningService = userProvisioningService;
    }

    /// <summary>CAS-020 §6 — create. §7 V1–V3/V6 validations, §10 SC-1/SC-2/SC-7/SC-9.</summary>
    [HttpPost("admin/users")]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest? request, CancellationToken ct)
    {
        var result = await _userProvisioningService.CreateAsync(request ?? new CreateUserRequest(), ct);

        return result.Outcome switch
        {
            UserCreateOutcome.ValidationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/validation-failed",
                title: "Request failed validation.",
                statusCode: StatusCodes.Status400BadRequest),
            UserCreateOutcome.UsernameAlreadyExists => Problem(
                type: "https://cachet.tracecounts.internal/errors/username-already-exists",
                title: "That username already exists.",
                statusCode: StatusCodes.Status409Conflict),
            UserCreateOutcome.B2KeyInvalid => Problem(
                type: "https://cachet.tracecounts.internal/errors/b2-key-invalid",
                title: "The supplied B2 key/key ID failed live authorization.",
                statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => StatusCode(StatusCodes.Status201Created, result.Response)
        };
    }

    /// <summary>CAS-020 §6 — partial edit (password and/or B2 key rotation). §7 V4–V6
    /// validations, §10 SC-4/SC-9.</summary>
    [HttpPut("admin/users/{userId:int}")]
    public async Task<IActionResult> Edit(int userId, [FromBody] EditUserRequest? request, CancellationToken ct)
    {
        var result = await _userProvisioningService.EditAsync(userId, request ?? new EditUserRequest(), ct);

        return result.Outcome switch
        {
            UserEditOutcome.ValidationFailed => Problem(
                type: "https://cachet.tracecounts.internal/errors/validation-failed",
                title: "Request failed validation.",
                statusCode: StatusCodes.Status400BadRequest),
            UserEditOutcome.UserNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/user-not-found",
                title: "User not found.",
                statusCode: StatusCodes.Status404NotFound),
            UserEditOutcome.B2KeyInvalid => Problem(
                type: "https://cachet.tracecounts.internal/errors/b2-key-invalid",
                title: "The supplied B2 key/key ID failed live authorization.",
                statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => Ok(result.Response)
        };
    }

    /// <summary>CAS-020 §6 — soft-deactivate, idempotent. §10 SC-5.</summary>
    [HttpPatch("admin/users/{userId:int}/deactivate")]
    public async Task<IActionResult> Deactivate(int userId, CancellationToken ct)
    {
        var result = await _userProvisioningService.DeactivateAsync(userId, ct);

        return result.Outcome switch
        {
            UserSetActiveOutcome.UserNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/user-not-found",
                title: "User not found.",
                statusCode: StatusCodes.Status404NotFound),
            _ => NoContent()
        };
    }

    /// <summary>CAS-020 §6 — soft-reactivate, idempotent. §10 SC-8.</summary>
    [HttpPatch("admin/users/{userId:int}/reactivate")]
    public async Task<IActionResult> Reactivate(int userId, CancellationToken ct)
    {
        var result = await _userProvisioningService.ReactivateAsync(userId, ct);

        return result.Outcome switch
        {
            UserSetActiveOutcome.UserNotFound => Problem(
                type: "https://cachet.tracecounts.internal/errors/user-not-found",
                title: "User not found.",
                statusCode: StatusCodes.Status404NotFound),
            _ => NoContent()
        };
    }
}
