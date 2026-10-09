using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Tests.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Web.Tests.ODriveSync;

/// <summary>
/// Shared arrangement for the CAS-045 bUnit classes (<c>ODriveSyncPagingTests</c>,
/// <c>ODriveSyncSelectionTests</c>, <c>ODriveSyncActionProgressTests</c>): a Full Access circuit, the
/// "CachetApi" client redirected to <see cref="FakeODriveSyncApiHandler"/> (which serves
/// <c>totalCount</c>-aware, per-panel paged results and captures every results query string), a 1-second
/// poll interval and an optional <c>ODriveSync:MaxSelectionPerAction</c> override.
/// </summary>
public abstract class ODriveSyncPagedTestBase : TestContext
{
    protected const int ScanTaskId = 100003;
    protected static readonly TimeSpan PollWaitTimeout = TimeSpan.FromSeconds(30);

    protected const string RemovedHeading = "Removed from O-Drive";
    protected const string NewHeading = "New on O-Drive";
    protected const string ModifiedHeading = "Modified files on O-Drive";

    protected readonly FakeODriveSyncApiHandler ApiHandler = new();

    protected ODriveSyncPagedTestBase(IDictionary<string, string?>? configuration = null)
    {
        var httpClient = new HttpClient(ApiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(httpClient));
        Services.AddLogging();
        Services.AddSingleton<IOptions<TaskPollingOptions>>(Options.Create(new TaskPollingOptions { TaskManagerPollIntervalSeconds = 1 }));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()).Build());

        var authContext = this.AddTestAuthorization();
        authContext.SetAuthorized("full-access-user");
        authContext.SetClaims(new Claim("b2_capability", "Full Access"));
    }

    protected static HttpResponseMessage Json<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };

    protected static TaskDetailResponse TaskDetail(int taskId, string type, string status) => new()
    {
        TaskId = taskId,
        Type = type,
        SubmittedBy = "full-access-user",
        SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-7),
        StartTime = status == "Queued" ? null : DateTimeOffset.UtcNow.AddMinutes(-7),
        CompletedAt = status is "Completed" or "Failed" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
        Status = status,
        IsAcknowledged = true, // already acknowledged: keeps POST /tasks/{id}/acknowledge out of these tests
    };

    protected static TaskSubmittedResponse Submitted(string type, int taskId) => new()
    {
        TaskId = taskId,
        Type = type,
        Status = "Queued",
        SubmittedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Opens the page on the user's latest Completed Content Scan (task 100003, the CAS-045 §1
    /// reproduction) with the given remaining-row totals, and waits until the three badges have rendered.
    /// No Copy/Update/Purge task is Running, so no action polling is resumed.</summary>
    protected IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> RenderCompletedScan(
        int removedTotal, int newTotal, int modifiedTotal)
    {
        ApiHandler.UsePagedResults(removedTotal, newTotal, modifiedTotal);

        var scan = TaskDetail(ScanTaskId, "ContentScan", "Completed");
        ApiHandler.OnGetTasks = _ => Task.FromResult(Json(HttpStatusCode.OK, new PagedResult<TaskListItem>
        {
            Items = new[]
            {
                new TaskListItem
                {
                    TaskId = scan.TaskId, Type = scan.Type, SubmittedBy = scan.SubmittedBy, SubmittedAt = scan.SubmittedAt,
                    StartTime = scan.StartTime, CompletedAt = scan.CompletedAt, Status = scan.Status, IsAcknowledged = true,
                },
            },
            Page = 1,
            PageSize = 1,
            TotalCount = 1,
        }));
        ApiHandler.OnGetTask = (_, id) => Task.FromResult(id == ScanTaskId
            ? Json(HttpStatusCode.OK, scan)
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        var cut = RenderComponent<Web.Components.Pages.ODriveSyncContentScan>();
        cut.WaitForState(() => PanelHeading.Count(cut, NewHeading) == Format(newTotal), TimeSpan.FromSeconds(5));
        return cut;
    }

    protected static string Format(int count) => count.ToString("N0");

    protected static IElement Panel(IRenderedFragment cut, string heading) =>
        cut.FindAll(".cachet-sync-panel").First(p => p.QuerySelector("h2")!.TextContent.Contains(heading, StringComparison.OrdinalIgnoreCase));

    /// <summary>The file names currently listed in a panel, in display order.</summary>
    protected static IReadOnlyList<string> RowNames(IRenderedFragment cut, string heading) =>
        Panel(cut, heading).QuerySelectorAll("tbody tr .cachet-cell-name").Select(e => e.TextContent.Trim()).ToList();

    protected static string RangeText(IRenderedFragment cut, string heading) =>
        Panel(cut, heading).QuerySelector(".cachet-sync-range")?.TextContent.Trim() ?? string.Empty;

    protected static IElement? PagerNav(IRenderedFragment cut, string heading) =>
        Panel(cut, heading).QuerySelector("nav[aria-label='Pagination']");

    protected static IElement ActionButton(IRenderedFragment cut, string heading, string label) =>
        Panel(cut, heading).QuerySelectorAll(".cachet-panel-actions button").First(b => b.TextContent.Contains(label, StringComparison.OrdinalIgnoreCase));

    protected static IElement HeaderCheckbox(IRenderedFragment cut, string heading) =>
        Panel(cut, heading).QuerySelector("thead input[type=checkbox]")!;

    protected static IReadOnlyList<IElement> RowCheckboxes(IRenderedFragment cut, string heading) =>
        Panel(cut, heading).QuerySelectorAll("tbody input[type=checkbox]").ToList();

    // Find + click run on the renderer's dispatcher so a background re-render (e.g. the tail of a
    // previous panel load) cannot invalidate the found element's event-handler id in between.
    protected static void GoToPage(IRenderedFragment cut, string heading, int page) =>
        cut.InvokeAsync(() => Panel(cut, heading).QuerySelector($"nav button[aria-label='Page {page}']")!.Click()).GetAwaiter().GetResult();

    protected static void ChangePageSize(IRenderedFragment cut, string heading, string value) =>
        Panel(cut, heading).QuerySelector("select[aria-label='Rows per page']")!.Change(value);

    protected static void ClickClearSelection(IRenderedFragment cut, string heading) =>
        Panel(cut, heading).QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Clear selection").Click();

    protected static string SummaryText(IRenderedFragment cut) =>
        cut.Find(".cachet-sync-summary").TextContent;

    protected static string Normalize(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\s+", " ").Trim();

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
