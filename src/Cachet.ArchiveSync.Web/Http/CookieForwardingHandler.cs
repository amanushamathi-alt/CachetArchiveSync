using Microsoft.AspNetCore.Http;

namespace Cachet.ArchiveSync.Web.Http;

/// <summary>
/// CAS-002 §6.6 "Consequence for dotnet implementation": forwards the current Blazor Server
/// circuit's incoming <c>Cookie</c> header onto every outgoing call to Api, so Api's identical
/// cookie-authentication scheme (same cookie name, shared Data Protection key ring) recognizes it
/// as the same session — no second login, no service-to-service token (§6.6's explicit rejection
/// of that alternative). Only ever runs server-side (this is a server-side <see cref="HttpClient"/>
/// handler); the cookie itself never reaches browser-side script.
/// </summary>
public sealed class CookieForwardingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CookieForwardingHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var cookieHeader = _httpContextAccessor.HttpContext?.Request.Headers.Cookie;
        if (cookieHeader is { Count: > 0 } values)
        {
            foreach (var value in values)
            {
                if (value is not null)
                {
                    request.Headers.TryAddWithoutValidation("Cookie", value);
                }
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
