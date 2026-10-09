using System.Net;

namespace Cachet.ArchiveSync.Web.Tests.ODriveSync;

/// <summary>
/// CAS-023: stands in for the real <c>Cachet.ArchiveSync.Api</c> process behind the "CachetApi"
/// named <see cref="HttpClient"/> that <c>ODriveSyncContentScan.razor</c> calls (per this spec's
/// test-writer instructions — mock the external Api, never reach a real process/DB). Configured
/// per-test via the <c>On*</c> delegates, mirroring <c>KeywordSearch</c>'s own
/// <c>FakeKeywordSearchApiHandler</c> precedent (CAS-022) for this project's five §6 endpoints:
/// <c>POST /sync/scan</c>, <c>GET /sync/{taskId}/results</c> (shared by both the scan poll loop and,
/// per the implementer's documented §8.4 deviation, all three sync-action poll loops), and
/// <c>POST /sync/actions/{copy,update,purge}</c>. Any route/verb with no scenario configured fails
/// loudly (502) rather than silently succeeding.
/// </summary>
public sealed class FakeODriveSyncApiHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostScan { get; set; }

    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetResults { get; set; }

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostCopy { get; set; }

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostUpdate { get; set; }

    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostPurge { get; set; }

    // CAS-060: POST /sync/mirror-sync, called by the page after a completed Copy. No default
    // (fails loudly, 502); every call is counted whether or not a scenario is configured.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnPostMirrorSync { get; set; }

    private int _mirrorSyncCallCount;

    public int MirrorSyncCallCount => Volatile.Read(ref _mirrorSyncCallCount);

    // CAS-024 SC-1: QuarantineQueueModal's trigger badge/own GET /quarantine call, and the modal's
    // list load when opened from this page.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetQuarantine { get; set; }

    // CAS-030 §8.2/§8.3/§13: the own-scoped Task Manager surface each screen reads on open.
    // GET /tasks defaults to an EMPTY list (§13) so every pre-existing CAS-022/023 test keeps its
    // "no prior task" initial state; GET /tasks/{taskId} has no default (fails loudly, 502);
    // POST /tasks/{taskId}/acknowledge defaults to 204 and is always recorded.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetTasks { get; set; }

    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnGetTask { get; set; }

    public Func<HttpRequestMessage, int, Task<HttpResponseMessage>>? OnPostAcknowledge { get; set; }

    // CAS-031 SC-2: AuditLogModal's GET /audit-log, when opened from this page's Audit Log button.
    public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnGetAuditLog { get; set; }

    public List<int> AcknowledgedTaskIds { get; } = new();

    public int ResultsCallCount { get; private set; }

    // CAS-045 §13: raw query string ("?a=b&...") of every GET /sync/{taskId}/results call, in order.
    private readonly object _queriesLock = new();
    private readonly List<string> _resultsQueries = new();

    public IReadOnlyList<string> ResultsQueries
    {
        get { lock (_queriesLock) { return _resultsQueries.ToList(); } }
    }

    // CAS-045 §13: "served totalCount" — UsePagedResults() answers every results call from these
    // mutable remaining-row totals, honouring the per-panel page/pageSize in the query string. A test
    // lowers a total to simulate rows that stopped being actionable (Synced, excluded server-side).
    public int NewTotal { get; set; }

    public int RemovedTotal { get; set; }

    public int ModifiedTotal { get; set; }

    public const int NewIdBase = 1_000_000;
    public const int RemovedIdBase = 2_000_000;
    public const int ModifiedIdBase = 3_000_000;

    public static string NewName(int index) => $"new-{index:D6}.pdf";

    public static string RemovedName(int index) => $"removed-{index:D6}.pdf";

    public static string ModifiedName(int index) => $"modified-{index:D6}.pdf";

    public void UsePagedResults(int removedTotal, int newTotal, int modifiedTotal)
    {
        RemovedTotal = removedTotal;
        NewTotal = newTotal;
        ModifiedTotal = modifiedTotal;
        OnGetResults = (request, _) =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var taskId = int.Parse(request.RequestUri.AbsolutePath.Split('/')[^2], System.Globalization.CultureInfo.InvariantCulture);

            (int Page, int PageSize) Paging(string prefix) => (
                int.Parse(query[$"{prefix}Page"] ?? "1", System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(query[$"{prefix}PageSize"] ?? "50", System.Globalization.CultureInfo.InvariantCulture));

            static IEnumerable<int> Slice(int page, int pageSize, int total) =>
                Enumerable.Range((page - 1) * pageSize + 1, Math.Max(0, Math.Min(pageSize, total - (page - 1) * pageSize)));

            var (np, ns) = Paging("newOnODrive");
            var (rp, rs) = Paging("removedFromODrive");
            var (mp, ms) = Paging("modifiedOnODrive");

            var response = new Cachet.ArchiveSync.Core.Dtos.SyncScanResultsResponse
            {
                TaskId = taskId,
                Status = "Completed",
                NewOnODrive = new Cachet.ArchiveSync.Core.Dtos.PagedResult<Cachet.ArchiveSync.Core.Dtos.NewOnODriveItem>
                {
                    Items = Slice(np, ns, NewTotal).Select(i => new Cachet.ArchiveSync.Core.Dtos.NewOnODriveItem
                    {
                        FileId = NewIdBase + i,
                        FileIndexId = NewIdBase + i + 500_000,
                        Name = NewName(i),
                        Path = $"/o-drive/{NewName(i)}",
                        Size = 1024,
                        ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
                        SyncStatus = "Pending",
                    }).ToList(),
                    Page = np,
                    PageSize = ns,
                    TotalCount = NewTotal,
                },
                RemovedFromODrive = new Cachet.ArchiveSync.Core.Dtos.PagedResult<Cachet.ArchiveSync.Core.Dtos.RemovedFromODriveItem>
                {
                    Items = Slice(rp, rs, RemovedTotal).Select(i => new Cachet.ArchiveSync.Core.Dtos.RemovedFromODriveItem
                    {
                        FileId = RemovedIdBase + i,
                        FileIndexId = RemovedIdBase + i + 500_000,
                        Name = RemovedName(i),
                        Path = $"/o-drive/{RemovedName(i)}",
                        LastSeen = DateTimeOffset.UtcNow.AddDays(-7),
                        SyncStatus = "Pending",
                    }).ToList(),
                    Page = rp,
                    PageSize = rs,
                    TotalCount = RemovedTotal,
                },
                ModifiedOnODrive = new Cachet.ArchiveSync.Core.Dtos.PagedResult<Cachet.ArchiveSync.Core.Dtos.ModifiedOnODriveItem>
                {
                    Items = Slice(mp, ms, ModifiedTotal).Select(i => new Cachet.ArchiveSync.Core.Dtos.ModifiedOnODriveItem
                    {
                        FileId = ModifiedIdBase + i,
                        FileIndexId = ModifiedIdBase + i + 500_000,
                        Name = ModifiedName(i),
                        Path = $"/o-drive/{ModifiedName(i)}",
                        ModifiedAt = DateTimeOffset.UtcNow.AddHours(-2),
                        Sha1Changed = true,
                        SyncStatus = "Pending",
                    }).ToList(),
                    Page = mp,
                    PageSize = ms,
                    TotalCount = ModifiedTotal,
                },
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(response),
            });
        };
    }

    public List<HttpRequestMessage> Requests { get; } = new();

    public List<string?> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        RequestBodies.Add(body);

        var path = request.RequestUri?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/sync/scan", StringComparison.OrdinalIgnoreCase)
            && OnPostScan is not null)
        {
            return await OnPostScan(request);
        }

        if (request.Method == HttpMethod.Get
            && path.Contains("/results", StringComparison.OrdinalIgnoreCase)
            && OnGetResults is not null)
        {
            ResultsCallCount++;
            lock (_queriesLock)
            {
                _resultsQueries.Add(request.RequestUri?.Query ?? string.Empty);
            }

            return await OnGetResults(request, ResultsCallCount);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/sync/actions/copy", StringComparison.OrdinalIgnoreCase)
            && OnPostCopy is not null)
        {
            return await OnPostCopy(request);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/sync/actions/update", StringComparison.OrdinalIgnoreCase)
            && OnPostUpdate is not null)
        {
            return await OnPostUpdate(request);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/sync/actions/purge", StringComparison.OrdinalIgnoreCase)
            && OnPostPurge is not null)
        {
            return await OnPostPurge(request);
        }

        if (request.Method == HttpMethod.Post
            && path.EndsWith("/sync/mirror-sync", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _mirrorSyncCallCount);
            if (OnPostMirrorSync is not null)
            {
                return await OnPostMirrorSync(request);
            }
        }

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase)
            && OnGetQuarantine is not null)
        {
            return await OnGetQuarantine(request);
        }

        if (request.Method == HttpMethod.Get
            && path.EndsWith("/audit-log", StringComparison.OrdinalIgnoreCase)
            && OnGetAuditLog is not null)
        {
            return await OnGetAuditLog(request);
        }

        // CAS-030: /tasks, /tasks/{id}, /tasks/{id}/acknowledge.
        var taskRoute = System.Text.RegularExpressions.Regex.Match(
            path, @"/tasks(?:/(?<id>-?\d+))?(?<ack>/acknowledge)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (taskRoute.Success)
        {
            var hasId = taskRoute.Groups["id"].Success;
            var isAck = taskRoute.Groups["ack"].Success;

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
            ReasonPhrase = $"FakeODriveSyncApiHandler: no scenario configured for {request.Method} {request.RequestUri}",
        };
    }
}
