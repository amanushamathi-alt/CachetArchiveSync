using System.Net;

namespace Cachet.ArchiveSync.Web.Tests.Reports;

/// <summary>
/// CAS-032 §13: stands in for the real <c>Cachet.ArchiveSync.Api</c> process behind the "CachetApi"
/// named <see cref="HttpClient"/> that <c>ReportModal.razor</c> calls, mirroring
/// <c>FakeQuarantineQueueApiHandler</c> (CAS-024) for this component's four consumed endpoints:
/// the §6.2 preview GETs (<c>GET /reports/pre-action/preview</c>, <c>GET /reports/post-action/preview</c>)
/// and CAS-015's §6.1 generation POSTs (<c>POST /reports/pre-action</c>, <c>POST /reports/post-action</c>).
/// Each delegate receives the request's <see cref="CancellationToken"/> so a test can hold a response
/// on a <see cref="TaskCompletionSource{TResult}"/> (SC-2/SC-6/SC-9/V3). Any route/verb with no
/// scenario configured fails loudly (502) rather than silently succeeding.
/// </summary>
public sealed class FakeReportsApiHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnGetPreActionPreview { get; set; }

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnGetPostActionPreview { get; set; }

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnPostPreAction { get; set; }

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnPostPostAction { get; set; }

    public int PreviewCallCount { get; private set; }

    public int PostCallCount { get; private set; }

    public List<HttpRequestMessage> Requests { get; } = new();

    public List<string?> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);

        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/reports/pre-action/preview", StringComparison.OrdinalIgnoreCase)
            && OnGetPreActionPreview is not null)
        {
            PreviewCallCount++;
            return await OnGetPreActionPreview(request, cancellationToken);
        }

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/reports/post-action/preview", StringComparison.OrdinalIgnoreCase)
            && OnGetPostActionPreview is not null)
        {
            PreviewCallCount++;
            return await OnGetPostActionPreview(request, cancellationToken);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/reports/pre-action", StringComparison.OrdinalIgnoreCase)
            && OnPostPreAction is not null)
        {
            PostCallCount++;
            return await OnPostPreAction(request, cancellationToken);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/reports/post-action", StringComparison.OrdinalIgnoreCase)
            && OnPostPostAction is not null)
        {
            PostCallCount++;
            return await OnPostPostAction(request, cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = $"FakeReportsApiHandler: no scenario configured for {request.Method} {request.RequestUri}",
        };
    }
}
