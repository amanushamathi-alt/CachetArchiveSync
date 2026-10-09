using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cachet.ArchiveSync.Api.Filters;

/// <summary>
/// CAS-020 §3/§12 — validates the <c>X-Admin-Provisioning-Key</c> request header against the
/// <c>Admin:ProvisioningKey</c> config value (§9) before any action method body runs. Applied at
/// the controller level to <see cref="Controllers.UsersAdminController"/> only — a separate,
/// non-session admin control, never <c>[Authorize]</c> cookie auth (§12). A missing/incorrect
/// token short-circuits with <c>401 admin-token-invalid</c> before the request reaches the
/// action, and therefore before any database write (§10 SC-3). Never logs the header's value
/// (§11).
/// </summary>
public sealed class AdminProvisioningTokenFilter : IAsyncActionFilter
{
    private const string HeaderName = "X-Admin-Provisioning-Key";

    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminProvisioningTokenFilter> _logger;

    public AdminProvisioningTokenFilter(IConfiguration configuration, ILogger<AdminProvisioningTokenFilter> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // §9: "fails fast at startup if unset" is enforced in Program.cs; this is a defensive
        // re-check, not the primary fail-fast path.
        var expected = _configuration["Admin:ProvisioningKey"];
        if (string.IsNullOrEmpty(expected))
        {
            throw new InvalidOperationException(
                "Configuration key 'Admin:ProvisioningKey' must be set (CAS-020 §9).");
        }

        var hasHeader = context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var provided);

        if (!hasHeader || !string.Equals(provided.ToString(), expected, StringComparison.Ordinal))
        {
            _logger.LogWarning("Admin request rejected: missing or incorrect {HeaderName}", HeaderName);

            context.Result = new ObjectResult(new ProblemDetails
            {
                Type = "https://cachet.tracecounts.internal/errors/admin-token-invalid",
                Title = "Missing or incorrect X-Admin-Provisioning-Key header.",
                Status = StatusCodes.Status401Unauthorized
            })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
            return;
        }

        await next();
    }
}
