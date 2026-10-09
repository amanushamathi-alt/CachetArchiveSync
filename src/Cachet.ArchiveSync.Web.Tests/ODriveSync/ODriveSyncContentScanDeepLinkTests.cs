using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Tests.TaskManager;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cachet.ArchiveSync.Web.Tests.ODriveSync;

/// <summary>
/// CAS-027 §13 — proves SC-3 (Task Manager's "View Results" deep link for a Completed
/// <c>ContentScan</c> row navigates to <c>/odrive-sync?taskId={id}</c> and auto-populates), per this
/// spec's own §13 test-class mapping. Mirrors <c>KeywordSearchDeepLinkTests</c>' (CAS-027) own
/// two-TestContext technique and rationale for this SC's "navigate, then auto-populate" pair spanning
/// <c>TaskManagerModal.razor</c> and <c>ODriveSyncContentScan.razor</c> — see that class's own doc
/// comment for the full explanation, not repeated here.
/// </summary>
public sealed class ODriveSyncContentScanDeepLinkTests : TestContext
{
    private static readonly TimeSpan PollWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly FakeODriveSyncApiHandler _apiHandler = new();

    public ODriveSyncContentScanDeepLinkTests()
    {
        var httpClient = new HttpClient(_apiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(httpClient));
        Services.AddLogging();
        // CAS-045: the page now injects IConfiguration (ODriveSync:MaxSelectionPerAction, default 100).
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var authContext = this.AddTestAuthorization();
        authContext.SetAuthorized("full-access-user");
        authContext.SetClaims(new Claim("b2_capability", "Full Access"));

        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };

    private static SyncScanResultsResponse CompletedResults(IReadOnlyList<NewOnODriveItem>? newItems = null) => new()
    {
        TaskId = 88,
        Status = "Completed",
        NewOnODrive = new PagedResult<NewOnODriveItem>
        {
            Items = newItems ?? Array.Empty<NewOnODriveItem>(),
            Page = 1,
            PageSize = 50,
            TotalCount = newItems?.Count ?? 0,
        },
        RemovedFromODrive = new PagedResult<RemovedFromODriveItem> { Items = Array.Empty<RemovedFromODriveItem>(), Page = 1, PageSize = 50, TotalCount = 0 },
        ModifiedOnODrive = new PagedResult<ModifiedOnODriveItem> { Items = Array.Empty<ModifiedOnODriveItem>(), Page = 1, PageSize = 50, TotalCount = 0 },
    };

    private static NewOnODriveItem SampleNewItem(int fileId = 21) => new()
    {
        FileId = fileId,
        FileIndexId = fileId + 500_000,
        Name = "new-file.pdf",
        Path = "/o-drive/2026/new-file.pdf",
        Size = 2048,
        ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
        SyncStatus = "Pending",
    };

    private IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> RenderPage(int? taskId = null)
    {
        // [SupplyParameterFromQuery] parameters cannot be set directly via bUnit's component-parameter
        // builder (it throws, directing callers to navigate via NavigationManager instead) — bUnit
        // supplies them from the current URI's query string when the component is rendered. The route
        // itself ("/odrive-sync") must also be navigated to first, since this page is not "/".
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/odrive-sync");
        if (taskId is not null)
        {
            navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameter("TaskId", taskId.Value));
        }

        return RenderComponent<Web.Components.Pages.ODriveSyncContentScan>();
    }

    // -----------------------------------------------------------------------------------------
    // SC-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC3_ViewResultsDeepLinksContentScanAndAutoPopulates()
    {
        // Part 1 (navigation): TaskManagerModal's "View Results" on a Completed ContentScan row
        // navigates to /odrive-sync?taskId={id} (§0 point 2, §8.3).
        using (var modalContext = new TestContext())
        {
            var taskApiHandler = new FakeTaskManagerApiHandler();
            taskApiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new PagedResult<TaskListItem>
            {
                Items = new[]
                {
                    new TaskListItem
                    {
                        TaskId = 88,
                        Type = "ContentScan",
                        SubmittedBy = "jdoe",
                        SubmittedAt = DateTimeOffset.UtcNow,
                        StartTime = DateTimeOffset.UtcNow,
                        IsAcknowledged = false,
                        Status = "Completed",
                    },
                },
                Page = 1,
                PageSize = 200,
                TotalCount = 1,
            }));
            taskApiHandler.OnPostMarkViewed = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var taskHttpClient = new HttpClient(taskApiHandler) { BaseAddress = new Uri("http://localhost/api/") };
            modalContext.Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(taskHttpClient));
            modalContext.Services.AddLogging();

            var modalCut = modalContext.RenderComponent<Web.Components.Shared.TaskManagerModal>();
            await modalCut.InvokeAsync(() => modalCut.Instance.OpenAsync());

            modalCut.FindAll("button").First(b => b.TextContent.Contains("View Results")).Click();

            var navigationManager = modalContext.Services.GetRequiredService<NavigationManager>();
            navigationManager.Uri.Should().Be("http://localhost/odrive-sync?taskId=88");
        }

        // Part 2 (auto-populate): ODriveSyncContentScan.razor, given TaskId=88, loads that scan's
        // stored results without a fresh POST /sync/scan submission (§0 point 2). CAS-030 §8.3 replaced
        // this spec's §8.3 "poll the deep-linked task" path: the page first resolves the task through
        // GET /tasks/{taskId}, and a Completed scan's results are read once via GET /sync/{taskId}/results.
        _apiHandler.OnGetTask = (_, id) => Task.FromResult(id == 88
            ? JsonResponse(HttpStatusCode.OK, new TaskDetailResponse
            {
                TaskId = 88,
                Type = "ContentScan",
                SubmittedBy = "full-access-user",
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                StartTime = DateTimeOffset.UtcNow.AddMinutes(-3),
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Status = "Completed",
                IsAcknowledged = false,
            })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults(new[] { SampleNewItem() })));

        var cut = RenderPage(taskId: 88);

        cut.WaitForState(() => PanelHeading.HasCount(cut, "New on O-Drive", 1), PollWaitTimeout);

        PanelHeading.HasCount(cut, "New on O-Drive", 1).Should().BeTrue();
        cut.Markup.Should().Contain("new-file.pdf");
        _apiHandler.Requests.Should().NotContain(
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/sync/scan", StringComparison.OrdinalIgnoreCase),
            "the deep-linked scan task must auto-populate via the existing PollScanAsync alone, with no fresh POST /sync/scan submission (§0 point 2, SC-3)");
        _apiHandler.Requests.Should().Contain(
            r => r.RequestUri!.AbsolutePath.Contains("/sync/88/results", StringComparison.OrdinalIgnoreCase),
            "PollScanAsync must be invoked with the deep-linked taskId (88), not a freshly-submitted one");
    }

    /// <summary>
    /// Coverage-gap closure (not a spec-mapped SC/V ID) — <c>ODriveSyncContentScan.razor</c>'s own
    /// non-success branch in its deep-link poll path is structurally identical to
    /// <c>KeywordSearch.razor</c>'s (whose §7 V3 test is the spec-mapped one, per this spec's own
    /// §13 table); this closes the same line-coverage gap on this screen's own file.
    /// </summary>
    [Fact]
    public void Test_InvalidContentScanDeepLinkTaskIdShowsErrorBanner()
    {
        // CAS-030 §8.3 / §7 V7: the deep-linked task is resolved via GET /tasks/{taskId} first, and a
        // 404 there shows V7's "That task could not be found." notice (not the generic error banner).
        _apiHandler.OnGetTask = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan>? cut = null;
        var act = () => cut = RenderPage(taskId: 999999);

        act.Should().NotThrow();
        var renderedCut = cut!;
        renderedCut.WaitForState(() => renderedCut.Markup.Contains("That task could not be found."), PollWaitTimeout);
        renderedCut.Find(".cachet-sync-notice").TextContent.Should().Be("That task could not be found.");
        _apiHandler.ResultsCallCount.Should().Be(0, "an unresolved deep-link task loads no results (CAS-030 §7 V7)");
    }

    /// <summary>Single-client <see cref="IHttpClientFactory"/> stand-in — this component only ever
    /// requests the "CachetApi" named client, so no name-based routing is needed. Mirrors every
    /// other test class in this test project.</summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
