using System.Net;

namespace Cachet.ArchiveSync.Web.Tests.Quarantine;

/// <summary>
/// CAS-024: stands in for the real <c>Cachet.ArchiveSync.Api</c> process behind the "CachetApi"
/// named <see cref="HttpClient"/> that <c>QuarantineQueueModal.razor</c> calls (per this spec's
/// test-writer instructions — mock the external Api, never reach a real process/DB), mirroring
/// <c>FakeKeywordSearchApiHandler</c> (CAS-022) and <c>FakeODriveSyncApiHandler</c> (CAS-023) for
/// this component's own two §6 endpoints: <c>GET /quarantine</c> and <c>POST /quarantine/delete</c>.
/// Any route/verb with no scenario configured fails loudly (502) rather than silently succeeding.
/// </summary>
public sealed class FakeQuarantineQueueApiHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetQuarantine { get; set; }

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostDelete { get; set; }

    public int GetQuarantineCallCount { get; private set; }

    public List<HttpRequestMessage> Requests { get; } = new();

    public List<string?> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);

        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase)
            && OnGetQuarantine is not null)
        {
            GetQuarantineCallCount++;
            return await OnGetQuarantine(request);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/quarantine/delete", StringComparison.OrdinalIgnoreCase)
            && OnPostDelete is not null)
        {
            return await OnPostDelete(request);
        }

        return new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = $"FakeQuarantineQueueApiHandler: no scenario configured for {request.Method} {request.RequestUri}",
        };
    }
}
