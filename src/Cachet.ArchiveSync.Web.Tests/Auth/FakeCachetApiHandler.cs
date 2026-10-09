using System.Net;

namespace Cachet.ArchiveSync.Web.Tests.Auth;

/// <summary>
/// CAS-021: stands in for the real <c>Cachet.ArchiveSync.Api</c> process behind the "CachetApi"
/// named <see cref="HttpClient"/> Web's <c>Program.cs</c>/<c>Login.razor</c> call — swapped in as
/// the named client's primary <see cref="HttpMessageHandler"/> via
/// <see cref="LoginWebApplicationFactory"/> so tests never reach a real Api process (per this
/// spec's test-writer instructions). Per-test scenarios are configured via <see cref="OnLogin"/>/
/// <see cref="OnLogout"/> delegates; any other route/verb combination fails loudly (502) rather
/// than silently succeeding, so an un-configured scenario is never mistaken for a passing test.
/// </summary>
public sealed class FakeCachetApiHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnLogin { get; set; }
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnLogout { get; set; }

    // CAS-024: GET /quarantine and POST /quarantine/delete, for QuarantineQueueFlowIntegrationTests
    // (route/auth-level and SC-10 log-hygiene coverage of QuarantineQueueModal.razor against the real
    // Web pipeline) — same optional, per-test-configured pattern as OnLogin/OnLogout above.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetQuarantine { get; set; }
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostQuarantineDelete { get; set; }

    // CAS-040: GET files/{id}/view|download, for FileViewer/FilePassThroughIntegrationTests (the §8.5
    // Web pass-through endpoints) — same optional, per-test-configured pattern as above. Throwing
    // from the delegate simulates an unreachable Api.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetFile { get; set; }

    /// <summary>Every request this fake received, of any route (CAS-040 SC-18: zero when unauthenticated).</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        Requests.Add(request);
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        if (request.Method == HttpMethod.Post
            && path.EndsWith("login", StringComparison.OrdinalIgnoreCase)
            && OnLogin is not null)
        {
            return await OnLogin(request);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("logout", StringComparison.OrdinalIgnoreCase)
            && OnLogout is not null)
        {
            return await OnLogout(request);
        }

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase)
            && OnGetQuarantine is not null)
        {
            return await OnGetQuarantine(request);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/quarantine/delete", StringComparison.OrdinalIgnoreCase)
            && OnPostQuarantineDelete is not null)
        {
            return await OnPostQuarantineDelete(request);
        }

        if (request.Method == HttpMethod.Get
            && System.Text.RegularExpressions.Regex.IsMatch(path, @"/files/\d+/(view|download)$")
            && OnGetFile is not null)
        {
            return await OnGetFile(request);
        }

        return new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = $"FakeCachetApiHandler: no scenario configured for {request.Method} {request.RequestUri}",
        };
    }
}
