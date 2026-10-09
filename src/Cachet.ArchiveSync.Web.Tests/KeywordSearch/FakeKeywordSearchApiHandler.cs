using System.Net;

namespace Cachet.ArchiveSync.Web.Tests.KeywordSearch;

/// <summary>
/// CAS-022: stands in for the real <c>Cachet.ArchiveSync.Api</c> process behind the "CachetApi"
/// named <see cref="HttpClient"/> that <c>KeywordSearch.razor</c> calls (per this spec's test-writer
/// instructions — mock the external Api, never reach a real process/DB). Configured per-test via the
/// <c>On*</c> delegates; <see cref="OnGetResults"/> additionally receives a 1-based call count so a
/// test can script the "409 while incomplete, then 200 once done" polling sequence (§10 SC-2/SC-3).
/// Any route/verb with no scenario configured fails loudly (502) rather than silently succeeding.
/// </summary>
public sealed class FakeKeywordSearchApiHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetQuarantine { get; set; }

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostSearch { get; set; }

    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetResults { get; set; }

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostQuarantineAdd { get; set; }

    // CAS-024 SC-9: QuarantineQueueModal's own POST /quarantine/delete call, exercised when the
    // modal is opened from this page's trigger button.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostQuarantineDelete { get; set; }

    // CAS-030 §8.2/§8.3/§13: the own-scoped Task Manager surface each screen reads on open.
    // GET /tasks defaults to an EMPTY list (§13) so every pre-existing CAS-022/023 test keeps its
    // "no prior task" initial state; GET /tasks/{taskId} has no default (fails loudly, 502);
    // POST /tasks/{taskId}/acknowledge defaults to 204 and is always recorded.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetTasks { get; set; }

    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetTask { get; set; }

    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnPostAcknowledge { get; set; }

    // CAS-031 SC-1: AuditLogModal's GET /audit-log, when opened from this page's Audit Log button.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetAuditLog { get; set; }

    // CAS-044: GET files/browse (the Files explorer). No default: an unconfigured call fails loudly (502),
    // which the explorer renders as its generic error state.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetFilesBrowse { get; set; }

    // CAS-048: GET search/{taskId}/browse (the results explorer) and GET search/{taskId}/browse/result-ids
    // (folder checkbox). No default: an unconfigured call fails loudly (502).
    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetSearchBrowse { get; set; }

    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetSearchResultIds { get; set; }

    // CAS-051: Files-mode quarantine endpoints (CAS-050 §8). No default: an unconfigured call fails
    // loudly (502), which the explorer renders as its generic quarantine error.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetQuarantineBrowseState { get; set; }

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostQuarantineBrowseAdd { get; set; }

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostQuarantineBrowseRemove { get; set; }

    // CAS-057: POST /tasks/{taskId}/cancel (CAS-056 §8). No default: an unconfigured call fails loudly (502).
    // Every call is recorded in CancelledTaskIds (before the delegate runs) so a test can prove "one POST".
    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnPostCancel { get; set; }

    public List<int> CancelledTaskIds { get; } = new();

    public List<int> AcknowledgedTaskIds { get; } = new();

    public int ResultsCallCount { get; private set; }

    public List<HttpRequestMessage> Requests { get; } = new();

    public List<string?> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);

        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/files/browse", StringComparison.OrdinalIgnoreCase)
            && OnGetFilesBrowse is not null)
        {
            return await OnGetFilesBrowse(request);
        }

        // CAS-048: /search/{id}/browse and /search/{id}/browse/result-ids (matched before the generic
        // "/results" rule; neither path contains it).
        var searchBrowse = System.Text.RegularExpressions.Regex.Match(
            path, @"/search/(?<id>\d+)/browse(?<ids>/result-ids)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (request.Method == HttpMethod.Get && searchBrowse.Success)
        {
            var browseTaskId = int.Parse(searchBrowse.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (searchBrowse.Groups["ids"].Success && OnGetSearchResultIds is not null)
            {
                return await OnGetSearchResultIds(request, browseTaskId);
            }

            if (!searchBrowse.Groups["ids"].Success && OnGetSearchBrowse is not null)
            {
                return await OnGetSearchBrowse(request, browseTaskId);
            }
        }

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/quarantine/browse/state", StringComparison.OrdinalIgnoreCase)
            && OnGetQuarantineBrowseState is not null)
        {
            return await OnGetQuarantineBrowseState(request);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/quarantine/browse", StringComparison.OrdinalIgnoreCase)
            && OnPostQuarantineBrowseAdd is not null)
        {
            return await OnPostQuarantineBrowseAdd(request);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/quarantine/browse/remove", StringComparison.OrdinalIgnoreCase)
            && OnPostQuarantineBrowseRemove is not null)
        {
            return await OnPostQuarantineBrowseRemove(request);
        }

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase)
            && OnGetQuarantine is not null)
        {
            return await OnGetQuarantine(request);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/search", StringComparison.OrdinalIgnoreCase)
            && OnPostSearch is not null)
        {
            return await OnPostSearch(request);
        }

        if (request.Method == HttpMethod.Get
            && path.Contains("/results", StringComparison.OrdinalIgnoreCase)
            && OnGetResults is not null)
        {
            ResultsCallCount++;
            return await OnGetResults(request, ResultsCallCount);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/search/results/quarantine", StringComparison.OrdinalIgnoreCase)
            && OnPostQuarantineAdd is not null)
        {
            return await OnPostQuarantineAdd(request);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/quarantine/delete", StringComparison.OrdinalIgnoreCase)
            && OnPostQuarantineDelete is not null)
        {
            return await OnPostQuarantineDelete(request);
        }

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/audit-log", StringComparison.OrdinalIgnoreCase)
            && OnGetAuditLog is not null)
        {
            return await OnGetAuditLog(request);
        }

        // CAS-030: /tasks, /tasks/{id}, /tasks/{id}/acknowledge.
        var taskRoute = System.Text.RegularExpressions.Regex.Match(
            path, @"/tasks(?:/(?<id>-?\d+))?(?<ack>/acknowledge)?(?<cancel>/cancel)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (taskRoute.Success)
        {
            var hasId = taskRoute.Groups["id"].Success;
            var isAck = taskRoute.Groups["ack"].Success;
            var isCancel = taskRoute.Groups["cancel"].Success;

            if (request.Method == HttpMethod.Post && hasId && isCancel)
            {
                var cancelTaskId = int.Parse(taskRoute.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture);
                CancelledTaskIds.Add(cancelTaskId);
                if (OnPostCancel is not null)
                {
                    return await OnPostCancel(request, cancelTaskId);
                }
            }

            if (request.Method == HttpMethod.Get && !hasId && !isAck)
            {
                return OnGetTasks is not null
                    ? await OnGetTasks(request)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = System.Net.Http.Json.JsonContent.Create(new Cachet.ArchiveSync.Core.Dtos.PagedResult<Cachet.ArchiveSync.Core.Dtos.TaskListItem>
                        {
                            Items = Array.Empty<Cachet.ArchiveSync.Core.Dtos.TaskListItem>(),
                            Page = 1,
                            PageSize = 1,
                            TotalCount = 0,
                        }),
                    };
            }

            if (request.Method == HttpMethod.Get && hasId && !isAck && OnGetTask is not null)
            {
                return await OnGetTask(request, int.Parse(taskRoute.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture));
            }

            if (request.Method == HttpMethod.Post && hasId && isAck)
            {
                var ackTaskId = int.Parse(taskRoute.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture);
                AcknowledgedTaskIds.Add(ackTaskId);
                return OnPostAcknowledge is not null
                    ? await OnPostAcknowledge(request, ackTaskId)
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = $"FakeKeywordSearchApiHandler: no scenario configured for {request.Method} {request.RequestUri}",
        };
    }
}
