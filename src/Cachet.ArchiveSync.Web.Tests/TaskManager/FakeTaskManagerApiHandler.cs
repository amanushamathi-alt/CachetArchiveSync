using System.Net;

namespace Cachet.ArchiveSync.Web.Tests.TaskManager;

/// <summary>
/// CAS-027: stands in for the real <c>Cachet.ArchiveSync.Api</c> process behind the "CachetApi"
/// named <see cref="HttpClient"/> that both <c>TaskManagerModal.razor</c> and <c>MainLayout.razor</c>'s
/// header-badge poll call (per this spec's test-writer instructions — mock the external Api, never
/// reach a real process/DB), mirroring <c>FakeQuarantineQueueApiHandler</c> (CAS-024) and
/// <c>FakeKeywordSearchApiHandler</c>/<c>FakeODriveSyncApiHandler</c> (CAS-022/CAS-023) for this
/// screen's five consumed §6 endpoints: <c>GET /tasks</c> (CAS-026 §6.1), <c>GET /tasks/{taskId}</c>
/// (CAS-026 §6.2), <c>GET /tasks/summary</c> (CAS-026 §6.3), <c>POST /tasks/mark-viewed</c>
/// (CAS-026 §6.4), and <c>POST /tasks/{taskId}/restart</c> (CAS-016 §8). Any route/verb with no
/// scenario configured fails loudly (502) rather than silently succeeding.
/// </summary>
public sealed class FakeTaskManagerApiHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetTasks { get; set; }

    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetTaskDetail { get; set; }

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetSummary { get; set; }

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostMarkViewed { get; set; }

    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnPostRestart { get; set; }

    // CAS-048: POST /tasks/{taskId}/acknowledge (acknowledge on open). Defaults to 204; always recorded.
    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnPostAcknowledge { get; set; }

    public List<int> AcknowledgedTaskIds { get; } = new();

    public int GetTasksCallCount { get; private set; }

    public int MarkViewedCallCount { get; private set; }

    public int RestartCallCount { get; private set; }

    public int? LastRestartTaskId { get; private set; }

    public List<HttpRequestMessage> Requests { get; } = new();

    public List<string?> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);

        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/tasks/summary", StringComparison.OrdinalIgnoreCase)
            && OnGetSummary is not null)
        {
            return await OnGetSummary(request);
        }

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/tasks", StringComparison.OrdinalIgnoreCase)
            && OnGetTasks is not null)
        {
            GetTasksCallCount++;
            return await OnGetTasks(request, GetTasksCallCount);
        }

        // CAS-048 V10: the removed route is still counted so a test can assert it is never called.
        if (request.Method == HttpMethod.Post
            && path.EndsWith("/tasks/mark-viewed", StringComparison.OrdinalIgnoreCase))
        {
            MarkViewedCallCount++;
            if (OnPostMarkViewed is not null)
            {
                return await OnPostMarkViewed(request);
            }
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/acknowledge", StringComparison.OrdinalIgnoreCase))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3 && int.TryParse(segments[^2], out var ackTaskId))
            {
                AcknowledgedTaskIds.Add(ackTaskId);
                return OnPostAcknowledge is not null
                    ? await OnPostAcknowledge(request, ackTaskId)
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/restart", StringComparison.OrdinalIgnoreCase)
            && OnPostRestart is not null
            && TryExtractRestartTaskId(path, out var restartTaskId))
        {
            RestartCallCount++;
            LastRestartTaskId = restartTaskId;
            return await OnPostRestart(request, restartTaskId);
        }

        if (request.Method == HttpMethod.Get
            && OnGetTaskDetail is not null
            && TryExtractTaskDetailId(path, out var detailTaskId))
        {
            return await OnGetTaskDetail(request, detailTaskId);
        }

        return new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            ReasonPhrase = $"FakeTaskManagerApiHandler: no scenario configured for {request.Method} {request.RequestUri}",
        };
    }

    private static bool TryExtractRestartTaskId(string path, out int taskId)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3
            && string.Equals(segments[^1], "restart", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[^3], "tasks", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[^2], out taskId))
        {
            return true;
        }

        taskId = 0;
        return false;
    }

    private static bool TryExtractTaskDetailId(string path, out int taskId)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2
            && string.Equals(segments[^2], "tasks", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[^1], out taskId))
        {
            return true;
        }

        taskId = 0;
        return false;
    }
}
