using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Web.Tests.AuditLog;

/// <summary>
/// CAS-031 §13: stands in for the real <c>Cachet.ArchiveSync.Api</c> process behind the "CachetApi"
/// named <see cref="HttpClient"/> that <c>AuditLogModal.razor</c> calls, mirroring
/// <c>FakeQuarantineQueueApiHandler</c> (CAS-024). Records every request (method + URI) so tests can
/// assert exact query strings (V1/V3/SC-4/SC-5/SC-6/SC-9). <see cref="OnGetAuditLog"/> defaults to an
/// empty 200 page; the <c>Return*</c>/<see cref="Hold"/> helpers configure 200 / empty / non-2xx /
/// throw / held (in-flight) responses. Any other route/verb fails loudly (502).
/// </summary>
public sealed class FakeAuditLogApiHandler : HttpMessageHandler
{
    /// <summary>Receives the request and the 1-based count of GET /audit-log calls so far.</summary>
    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>> OnGetAuditLog { get; set; } =
        (request, _) => Task.FromResult(PageResponse(Array.Empty<AuditLogListItem>(), 0, PageOf(request)));

    public List<(HttpMethod Method, Uri Uri)> Requests { get; } = new();

    public int AuditLogCallCount { get; private set; }

    /// <summary>Always answers 200 with the given items/totalCount; <c>page</c> echoes the request's page param.</summary>
    public void ReturnPage(IReadOnlyList<AuditLogListItem> items, int totalCount) =>
        OnGetAuditLog = (request, _) => Task.FromResult(PageResponse(items, totalCount, PageOf(request)));

    public void ReturnStatus(HttpStatusCode status) =>
        OnGetAuditLog = (_, _) => Task.FromResult(new HttpResponseMessage(status));

    public void Throw() =>
        OnGetAuditLog = (_, _) => throw new HttpRequestException("simulated: Api unreachable");

    /// <summary>Holds every subsequent response until the returned source is completed.</summary>
    public TaskCompletionSource<HttpResponseMessage> Hold()
    {
        var tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnGetAuditLog = (_, _) => tcs.Task;
        return tcs;
    }

    public static HttpResponseMessage PageResponse(IReadOnlyList<AuditLogListItem> items, int totalCount, int page = 1) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PagedResult<AuditLogListItem>
            {
                Items = items,
                Page = page,
                PageSize = 50,
                TotalCount = totalCount,
            }),
        };

    private static int PageOf(HttpRequestMessage request)
    {
        var query = request.RequestUri?.Query.TrimStart('?') ?? string.Empty;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("page=", StringComparison.Ordinal)
                && int.TryParse(pair["page=".Length..], out var page))
            {
                return page;
            }
        }

        return 1;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method, request.RequestUri!));

        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        if (request.Method == HttpMethod.Get && path.EndsWith("/audit-log", StringComparison.OrdinalIgnoreCase))
        {
            AuditLogCallCount++;
            return await OnGetAuditLog(request, AuditLogCallCount);
        }

        return new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = $"FakeAuditLogApiHandler: no scenario configured for {request.Method} {request.RequestUri}",
        };
    }
}
