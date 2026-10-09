using System.Net;
using System.Net.Http.Json;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Tests.TaskManager;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cachet.ArchiveSync.Web.Tests.KeywordSearch;

/// <summary>
/// CAS-027 §13 — proves SC-2 (Task Manager's "View Results" deep link for a Completed
/// <c>KeywordSearch</c> row) and V-3 (an invalid/out-of-scope deep-link <c>taskId</c> shows the
/// existing generic error banner, never an unhandled exception or blank page), per this spec's own
/// §13 test-class mapping.
///
/// <para>
/// <b>Two components, one TestContext each:</b> SC-2's own text spans two components —
/// <c>TaskManagerModal.razor</c>'s navigation call, and <c>KeywordSearch.razor</c>'s auto-populate
/// behavior once it receives that <c>TaskId</c>. bUnit does not run ASP.NET Core's real routing
/// pipeline (no actual page-to-page navigation occurs), and each screen's own fake Api handler
/// answers a disjoint set of routes on the same "CachetApi" named client — so a single shared
/// <see cref="IHttpClientFactory"/> registration cannot serve both at once. The navigation half is
/// therefore proved in its own short-lived, purpose-built <c>Bunit.TestContext</c>; the
/// auto-populate half is proved against `this` class's own <c>KeywordSearch.razor</c>-oriented setup
/// (mirroring <c>KeywordSearchPageTests</c>' own conventions for this page).
/// </para>
///
/// <para>
/// <b>TaskId as a direct component parameter:</b> the implementer's own §8.3 code comment notes that
/// <c>[SupplyParameterFromQuery]</c> binding is exercised by ASP.NET Core's routing/query-string
/// infrastructure at runtime, which bUnit does not run in isolation. These tests instead supply
/// <c>TaskId</c> directly as a component parameter — proving <c>KeywordSearch.razor</c>'s own
/// <c>OnInitializedAsync</c> contract ("if TaskId is present, poll it") independently of how the
/// framework happens to populate that parameter.
/// </para>
/// </summary>
public sealed class KeywordSearchDeepLinkTests : TestContext
{
    private const string ConfiguredBucket = "cachet-archive-prod";
    private static readonly TimeSpan PollWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeKeywordSearchApiHandler _apiHandler = new();

    public KeywordSearchDeepLinkTests()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));
        // CAS-048: the results explorer reads GET search/{taskId}/browse (and .../browse/result-ids).
        _apiHandler.OnGetSearchBrowse = FileExplorer.BrowseFixtures.ServeResults;
        _apiHandler.OnGetSearchResultIds = FileExplorer.BrowseFixtures.ServeResultIds;

        var httpClient = new HttpClient(_apiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(httpClient));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:BucketName"] = ConfiguredBucket })
            .Build());
        Services.AddLogging();
        // CAS-030 §8.4: poll interval is config-bound (default 10 s); 1 s keeps the poll-dependent waits
        // below PollWaitTimeout under whole-solution load (same as KeywordSearchPageTests).
        Services.AddSingleton<Microsoft.Extensions.Options.IOptions<Web.TaskPollingOptions>>(
            Microsoft.Extensions.Options.Options.Create(new Web.TaskPollingOptions { TaskManagerPollIntervalSeconds = 1 }));
        Services.AddTransient<Web.Http.FilesBrowseClient>(); // CAS-044: Files is the default mode
        Services.AddTransient<Web.Http.SearchResultsBrowseClient>(); // CAS-048: results explorer
        Services.AddTransient<Web.Http.QuarantineBrowseClient>(); // CAS-051: injected by FileExplorer

        // KeywordSearch.razor embeds QuarantineQueueModal, which reads the CachetFullAccess
        // ("b2_capability") claim from a cascading Task<AuthenticationState> — same bUnit.TestDoubles
        // setup KeywordSearchPageTests (CAS-022/CAS-024) already established for this page.
        // CAS-051 §2.8: the results-mode "Send to Quarantine" button is rendered for Full Access only.
        var authContext = this.AddTestAuthorization();
        authContext.SetAuthorized("full-access-user");
        authContext.SetClaims(new System.Security.Claims.Claim("b2_capability", "Full Access"));
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };

    private static SearchResultsResponse CompletedResponse(IReadOnlyList<FilenameMatchItem>? filenameItems = null) => new()
    {
        TaskId = 1,
        Status = "Completed",
        FilenameMatches = new PagedResult<FilenameMatchItem>
        {
            Items = filenameItems ?? Array.Empty<FilenameMatchItem>(),
            Page = 1,
            PageSize = 50,
            TotalCount = filenameItems?.Count ?? 0,
        },
        ContentMatches = new PagedResult<ContentMatchItem>
        {
            Items = Array.Empty<ContentMatchItem>(),
            Page = 1,
            PageSize = 50,
            TotalCount = 0,
        },
    };

    private static FilenameMatchItem SampleFilenameItem(int fileId = 11) => new()
    {
        FileId = fileId,
        FileIndexId = fileId + 500_000,
        Name = "invoice.pdf",
        Path = "/o-drive/2026/invoice.pdf",
        Size = 4096,
        ModifiedAt = DateTimeOffset.UtcNow.AddDays(-3),
        Status = "Flagged",
    };

    private IRenderedComponent<Web.Components.Pages.KeywordSearch> RenderPage(int? taskId = null, bool selectKeywordMode = true)
    {
        // [SupplyParameterFromQuery] parameters cannot be set directly via bUnit's component-parameter
        // builder (it throws, directing callers to navigate via NavigationManager instead) — bUnit
        // supplies them from the current URI's query string when the component is rendered.
        if (taskId is not null)
        {
            var navigationManager = Services.GetRequiredService<NavigationManager>();
            navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameter("TaskId", taskId.Value));
        }

        var cut = RenderComponent<Web.Components.Pages.KeywordSearch>();

        if (selectKeywordMode)
        {
            // CAS-044: Files is the default mode on every open; these tests exercise Keyword Search.
            cut.Find("#ks-mode-keyword").Change("keyword");
        }

        if (selectKeywordMode)
        {
            cut.WaitForState(
                () => _apiHandler.Requests.ToList().Any(r => r.Method == HttpMethod.Get
                        && r.RequestUri!.AbsolutePath.TrimEnd('/').EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase))
                    || cut.Markup.Contains("Quarantine is empty")
                    || cut.Markup.Contains("Quarantined On")
                    || cut.Markup.Contains("Something went wrong"),
                TimeSpan.FromSeconds(3));
        }
        else
        {
            // CAS-048 §2.4: a ?taskId= open selects the task's mode on its own, so nothing is clicked here;
            // wait for the (Files) explorer's first load or the first render of the page chrome.
            cut.WaitForState(() => cut.FindAll("tr.fe-row, .fe-error, .cachet-ks-notice").Count > 0, TimeSpan.FromSeconds(3));
        }

        return cut;
    }

    private static SearchResultsResponse ResultsCompleted(int taskId = 1, int total = 3) => new()
    {
        TaskId = taskId,
        Status = "Completed",
        FilenameMatches = new PagedResult<FilenameMatchItem>
        {
            Items = Array.Empty<FilenameMatchItem>(),
            Page = 1,
            PageSize = 1,
            TotalCount = total,
        },
        ContentMatches = new PagedResult<ContentMatchItem>
        {
            Items = Array.Empty<ContentMatchItem>(),
            Page = 1,
            PageSize = 1,
            TotalCount = 0,
        },
    };

    // The results explorer (not the Files one) has the Send to Quarantine button above it.
    private static bool ResultsExplorerReady(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.FindAll("#ks-send-to-quarantine").Count > 0 && cut.FindAll("#fe-root tr.fe-row").Count > 0;

    private static TaskDetailResponse SearchTask(int taskId, string status, string? mode, string term = "invoice", DateOnly? from = null, DateOnly? to = null, bool isAcknowledged = false) => new()
    {
        TaskId = taskId,
        Type = "KeywordSearch",
        SubmittedBy = "full-access-user",
        SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
        StartTime = DateTimeOffset.UtcNow.AddMinutes(-3),
        CompletedAt = status == "Completed" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
        Status = status,
        IsAcknowledged = isAcknowledged,
        SearchParameters = new TaskSearchParameters { Term = term, Extension = "All", FromDate = from, ToDate = to, Mode = mode },
    };

    private static TaskListItem ListItem(TaskDetailResponse task) => new()
    {
        TaskId = task.TaskId,
        Type = task.Type,
        SubmittedBy = task.SubmittedBy,
        SubmittedAt = task.SubmittedAt,
        StartTime = task.StartTime,
        CompletedAt = task.CompletedAt,
        Status = task.Status,
        IsAcknowledged = task.IsAcknowledged,
        SearchMode = task.SearchParameters?.Mode,
    };

    private void ServeTasks(params TaskDetailResponse[] tasks)
    {
        _apiHandler.OnGetTasks = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new PagedResult<TaskListItem>
        {
            Items = tasks.Select(ListItem).ToList(),
            Page = 1,
            PageSize = 100,
            TotalCount = tasks.Length,
        }));
        _apiHandler.OnGetTask = (_, id) => Task.FromResult(tasks.SingleOrDefault(t => t.TaskId == id) is { } task
            ? JsonResponse(HttpStatusCode.OK, task with { IsAcknowledged = task.IsAcknowledged || _apiHandler.AcknowledgedTaskIds.Contains(id) })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>CAS-039 A4 §2.6c: the filter bar's defaults — the term box and date fields empty and
    /// Extension "All". CAS-048 §2.4: a deep link selects the task's mode (<paramref name="mode"/>:
    /// "keyword", "filename" or "date").</summary>
    private static void AssertFilterBarDefaults(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string mode = "keyword")
    {
        foreach (var radio in new[] { "keyword", "filename", "date" })
        {
            cut.Find($"#ks-mode-{radio}").HasAttribute("checked").Should().Be(radio == mode, $"{mode} is the selected mode");
        }

        if (mode == "date")
        {
            cut.FindAll("#ks-term").Should().BeEmpty("Date mode renders no term input");
            (cut.Find("#ks-from-date").GetAttribute("value") ?? string.Empty).Should().BeEmpty();
            (cut.Find("#ks-to-date").GetAttribute("value") ?? string.Empty).Should().BeEmpty();
        }
        else
        {
            (cut.Find("#ks-term").GetAttribute("value") ?? string.Empty).Should().Be(string.Empty, "the term box is empty on every open (CAS-039 A4)");
            cut.FindAll("#ks-from-date").Should().BeEmpty();
            cut.FindAll("#ks-to-date").Should().BeEmpty();
        }

        cut.Find("#ks-extension").GetAttribute("value").Should().Be("All");
    }

    // -----------------------------------------------------------------------------------------
    // SC-2
    // -----------------------------------------------------------------------------------------

    // CAS-048 §5a: rewritten — the modal no longer calls mark-viewed (V10) and requests pageSize=100; the
    // destination page fills the results explorer (not the removed panels) from the deep-linked task.
    [Fact]
    public async Task Test_SC2_ViewResultsDeepLinksKeywordSearchAndAutoPopulates()
    {
        // Part 1 (navigation): TaskManagerModal's "View Results" on a Completed KeywordSearch row
        // navigates to /?taskId={id} (§0 point 2, §8.3).
        using (var modalContext = new TestContext())
        {
            var taskApiHandler = new FakeTaskManagerApiHandler();
            taskApiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new PagedResult<TaskListItem>
            {
                Items = new[]
                {
                    new TaskListItem
                    {
                        TaskId = 77,
                        Type = "KeywordSearch",
                        SubmittedBy = "jdoe",
                        SubmittedAt = DateTimeOffset.UtcNow,
                        StartTime = DateTimeOffset.UtcNow,
                        IsAcknowledged = false,
                        Status = "Completed",
                    },
                },
                Page = 1,
                PageSize = 100,
                TotalCount = 1,
            }));

            var taskHttpClient = new HttpClient(taskApiHandler) { BaseAddress = new Uri("http://localhost/api/") };
            modalContext.Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(taskHttpClient));
            modalContext.Services.AddLogging();

            var modalCut = modalContext.RenderComponent<Web.Components.Shared.TaskManagerModal>();
            await modalCut.InvokeAsync(() => modalCut.Instance.OpenAsync());

            modalCut.FindAll("button").First(b => b.TextContent.Contains("View Results")).Click();

            var navigationManager = modalContext.Services.GetRequiredService<NavigationManager>();
            navigationManager.Uri.Should().Be("http://localhost/?taskId=77");
            taskApiHandler.AcknowledgedTaskIds.Should().Equal(77);
            taskApiHandler.MarkViewedCallCount.Should().Be(0);
        }

        // Part 2 (auto-populate): KeywordSearch.razor, given TaskId=77, loads that task's stored
        // rows without a fresh POST /search submission (§0 point 2). CAS-030 §8.2 replaced this
        // spec's §8.3 "poll the deep-linked task" path: the page first resolves the task through
        // GET /tasks/{taskId}; CAS-048 then reads the Completed task's rows through browse.
        _apiHandler.OnGetTask = (_, id) => Task.FromResult(id == 77
            ? JsonResponse(HttpStatusCode.OK, new TaskDetailResponse
            {
                TaskId = 77,
                Type = "KeywordSearch",
                SubmittedBy = "full-access-user",
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                StartTime = DateTimeOffset.UtcNow.AddMinutes(-3),
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Status = "Completed",
                IsAcknowledged = false,
                SearchParameters = new TaskSearchParameters { Term = "invoice", Extension = "PDF" },
            })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(77, 3)));

        var cut = RenderPage(taskId: 77, selectKeywordMode: false);

        cut.WaitForState(() => ResultsExplorerReady(cut), PollWaitTimeout);

        cut.FindAll("#fe-root tr.fe-row .fe-name").Select(n => n.TextContent).Should().Equal("Alpha", "Beta");
        _apiHandler.Requests.Should().NotContain(
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.OrdinalIgnoreCase),
            "the deep-linked task must be read from stored rows alone, with no fresh POST /search submission (§0 point 2, SC-2)");
        _apiHandler.Requests.Should().Contain(
            r => r.RequestUri!.AbsolutePath == "/api/search/77/browse",
            "the explorer reads the deep-linked taskId (77), not a freshly-submitted one");
    }

    // -----------------------------------------------------------------------------------------
    // V-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V3_InvalidDeepLinkTaskIdShowsErrorBanner()
    {
        // A 404 covers both cases in this rule's text ("that 404s or belongs to another user") — the
        // Api scopes GET /search/{taskId}/results to the caller's own tasks, so an out-of-scope
        // taskId and another user's taskId are indistinguishable 404s at this screen's level;
        // CAS-030 §8.2 step 1 / §7 V7 (which replaced this spec's PollTaskAsync-based deep-link path)
        // now resolves the task via GET /tasks/{taskId} first, and a 404 there shows V7's specific
        // "That task could not be found." notice rather than the generic error banner.
        _apiHandler.OnGetTask = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        IRenderedComponent<Web.Components.Pages.KeywordSearch>? cut = null;
        var act = () => cut = RenderPage(taskId: 999999);

        act.Should().NotThrow("a 404 for an out-of-scope/other-user's taskId (§7 V3) must never surface as an unhandled exception or blank page");
        var renderedCut = cut!;
        renderedCut.WaitForState(() => renderedCut.Markup.Contains("That task could not be found."), PollWaitTimeout);
        renderedCut.Find(".cachet-ks-notice").TextContent.Should().Be("That task could not be found.");
        _apiHandler.ResultsCallCount.Should().Be(0, "an unresolved deep-link task loads no results (CAS-030 §7 V7)");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 SC-7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC7_ReloadedDateTaskRestoresModeDatesAndHeader()
    {
        static TaskDetailResponse CompletedTask(int taskId, DateOnly? fromDate, DateOnly? toDate) => new()
        {
            TaskId = taskId,
            Type = "KeywordSearch",
            SubmittedBy = "full-access-user",
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            StartTime = DateTimeOffset.UtcNow.AddMinutes(-3),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status = "Completed",
            IsAcknowledged = true,
            SearchParameters = new TaskSearchParameters
            {
                Term = "invoice",
                Extension = "PDF",
                FromDate = fromDate,
                ToDate = toDate,
            },
        };

        var tasks = new Dictionary<int, TaskDetailResponse>
        {
            [91] = CompletedTask(91, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
            [92] = CompletedTask(92, null, null),
        };
        _apiHandler.OnGetTask = (_, id) => Task.FromResult(tasks.TryGetValue(id, out var task)
            ? JsonResponse(HttpStatusCode.OK, task)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(1, 3)));

        // Part 1 — a task with fromDate/toDate keeps the header suffix; the filter bar keeps its defaults.
        var dated = RenderPage(taskId: 91, selectKeywordMode: false);
        dated.WaitForState(() => dated.FindAll(".cachet-ks-context-header").Count > 0, PollWaitTimeout);

        // prefill superseded by CAS-039 A4: the reloaded task's term, dates and extension are not copied
        // into the filter bar. A legacy keyword + dates task opens as Keyword Search (CAS-048 §2.3).
        AssertFilterBarDefaults(dated);
        dated.Find(".cachet-ks-context-header").TextContent.Should()
            .Contain("Showing results for \"invoice\" (PDF), created 09/01/2026 – 09/30/2026, submitted");

        // Part 2 — a task without dates stays in Keyword mode with the unchanged header.
        var undated = RenderPage(taskId: 92, selectKeywordMode: false);
        undated.WaitForState(() => undated.FindAll(".cachet-ks-context-header").Count > 0, PollWaitTimeout);

        undated.Find("#ks-mode-keyword").HasAttribute("checked").Should().BeTrue();
        undated.Find("#ks-mode-date").HasAttribute("checked").Should().BeFalse();
        undated.FindAll("#ks-from-date").Should().BeEmpty();
        undated.FindAll("#ks-to-date").Should().BeEmpty();
        var header = undated.Find(".cachet-ks-context-header").TextContent;
        header.Should().Contain("Showing results for \"invoice\" (PDF), submitted");
        header.Should().NotContain("created");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 A2 SC-11
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_A2_SC11_ReloadedDateOnlyTaskHeader()
    {
        static TaskDetailResponse CompletedTask(int taskId, TaskSearchParameters? searchParameters) => new()
        {
            TaskId = taskId,
            Type = "KeywordSearch",
            SubmittedBy = "full-access-user",
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            StartTime = DateTimeOffset.UtcNow.AddMinutes(-3),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status = "Completed",
            IsAcknowledged = true,
            SearchParameters = searchParameters,
        };

        var tasks = new Dictionary<int, TaskDetailResponse>
        {
            [93] = CompletedTask(93, new TaskSearchParameters
            {
                Term = string.Empty,
                Extension = "PDF",
                FromDate = new DateOnly(2025, 1, 1),
                ToDate = new DateOnly(2025, 1, 31),
            }),
            [94] = CompletedTask(94, null),
        };
        _apiHandler.OnGetTask = (_, id) => Task.FromResult(tasks.TryGetValue(id, out var task)
            ? JsonResponse(HttpStatusCode.OK, task)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(1, 0)));

        // Part 1 — a reloaded date-only task (Term "", both dates): "all files" header; a legacy date-only task
        // opens as Date Search (CAS-048 §2.3), and its filter bar keeps empty fields (CAS-039 A4).
        var dateOnly = RenderPage(taskId: 93, selectKeywordMode: false);
        dateOnly.WaitForState(() => dateOnly.FindAll(".cachet-ks-context-header").Count > 0, PollWaitTimeout);

        AssertFilterBarDefaults(dateOnly, "date");
        dateOnly.Find("p.cachet-ks-context-header").TextContent.Should()
            .Contain("Showing results for all files (PDF), created 01/01/2025 – 01/31/2025, submitted");

        // Part 2 (regression guard) — a reloaded Completed task whose SearchParameters is null must
        // NOT be treated as date-only: no context header, and it opens as Keyword Search.
        var noParams = RenderPage(taskId: 94, selectKeywordMode: false);
        noParams.WaitForState(() => ResultsExplorerReady(noParams), PollWaitTimeout);

        noParams.FindAll(".cachet-ks-context-header").Should().BeEmpty();
        noParams.Find("#ks-mode-keyword").HasAttribute("checked").Should().BeTrue();
        noParams.Markup.Should().NotContain("all files");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 A3 SC-13
    // -----------------------------------------------------------------------------------------

    // CAS-048 §5a: rewritten — a legacy keyword + date task now opens as Keyword Search (only a blank-term
    // date task is Date Search); the header still names its term and dates.
    [Fact]
    public void Test_A3_SC13_ReloadedKeywordPlusDateTaskOpensDateModeWithTermHeader()
    {
        _apiHandler.OnGetTask = (_, id) => Task.FromResult(id == 95
            ? JsonResponse(HttpStatusCode.OK, new TaskDetailResponse
            {
                TaskId = 95,
                Type = "KeywordSearch",
                SubmittedBy = "full-access-user",
                SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                StartTime = DateTimeOffset.UtcNow.AddMinutes(-3),
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Status = "Completed",
                IsAcknowledged = true,
                SearchParameters = new TaskSearchParameters
                {
                    Term = "invoice",
                    Extension = "PDF",
                    FromDate = new DateOnly(2025, 1, 1),
                    ToDate = new DateOnly(2025, 1, 31),
                },
            })
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(95, 3)));

        var cut = RenderPage(taskId: 95, selectKeywordMode: false);
        cut.WaitForState(() => ResultsExplorerReady(cut), PollWaitTimeout);

        AssertFilterBarDefaults(cut);
        cut.Find("p.cachet-ks-context-header").TextContent.Should()
            .Contain("Showing results for \"invoice\" (PDF), created 01/01/2025 – 01/31/2025, submitted");
        cut.Markup.Should().NotContain("Content matching needs a keyword.");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-10
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Filename", "filename", "Filename Search")]
    [InlineData("Keyword", "keyword", "Keyword Search")]
    [InlineData("Date", "date", "Date Search")]
    public async Task Test_SC10_CompletedTaskClickRestoresModeAndResults(string taskMode, string radio, string typeLabel)
    {
        // Task 5 is Running (the page is polling it); task 11 is a Completed task in the mode under test.
        var running = SearchTask(5, "Running", "Keyword");
        var completed = taskMode == "Date"
            ? SearchTask(11, "Completed", taskMode, term: string.Empty, from: new DateOnly(2025, 1, 1), to: new DateOnly(2025, 1, 31))
            : SearchTask(11, "Completed", taskMode);
        ServeTasks(completed, running);
        _apiHandler.OnGetResults = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("/5/")
            ? new HttpResponseMessage(HttpStatusCode.Conflict)
            : JsonResponse(HttpStatusCode.OK, ResultsCompleted(11, 3)));

        // The page is already open on "/" — deep-linked to the Running task, so it is polling.
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("TaskId", 5));
        var cut = RenderComponent<Web.Components.Pages.KeywordSearch>();
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), PollWaitTimeout);
        await WaitUntilAsync(() => _apiHandler.ResultsCallCount >= 1, PollWaitTimeout);

        // Open the Task Manager from the page and click the Completed row.
        cut.FindComponent<Web.Components.Layout.TaskManagerBadge>().Find("button").Click();
        cut.WaitForState(() => cut.FindAll(".cachet-tm-table tbody tr").Count == 2, TimeSpan.FromSeconds(3));
        var row = cut.FindAll(".cachet-tm-table tbody tr").Single(r => r.TextContent.Contains("Completed"));
        row.TextContent.Should().Contain(typeLabel);
        row.Click();

        // The modal closed and navigated to the task's deep link; only that task was acknowledged.
        cut.FindAll(".cachet-tm-modal").Should().BeEmpty();
        Services.GetRequiredService<NavigationManager>().Uri.Should().Be("http://localhost/?taskId=11");
        _apiHandler.AcknowledgedTaskIds.Should().Equal(11);

        // The router re-parameterizes the page already on "/" (bUnit does not run the router): the page must
        // reload for the changed ?taskId= instead of ignoring it (CAS-048 §2.4).
        if (cut.Instance.TaskId != "11")
        {
            await cut.InvokeAsync(() => cut.Instance.SetParametersAsync(
                ParameterView.FromDictionary(new Dictionary<string, object?> { ["TaskId"] = "11" })));
        }

        cut.WaitForState(() => ResultsExplorerReady(cut), PollWaitTimeout);

        cut.Find($"#ks-mode-{radio}").HasAttribute("checked").Should().BeTrue("the task's mode is selected");
        cut.FindAll("#fe-root tr.fe-row .fe-name").Select(n => n.TextContent).Should().Equal("Alpha", "Beta");
        cut.Markup.Should().NotContain("Search in progress");
        _apiHandler.Requests.Should().Contain(r => r.RequestUri!.AbsolutePath == "/api/search/11/browse");

        // The earlier poll was cancelled: no further results calls for task 5.
        int Task5Calls() => _apiHandler.Requests.ToList().Count(r => r.RequestUri!.AbsolutePath == "/api/search/5/results");
        var before = Task5Calls();
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Task5Calls().Should().Be(before, "reloading for the new ?taskId= cancels the earlier poll");
        _apiHandler.AcknowledgedTaskIds.Should().Equal(11);
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-12
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("legacy-keyword", "keyword")]
    [InlineData("legacy-keyword-with-dates", "keyword")]
    [InlineData("legacy-date-only", "date")]
    public void Test_SC12_LegacyTaskOpensAsToday(string shape, string radio)
    {
        var task = shape switch
        {
            "legacy-date-only" => SearchTask(21, "Completed", null, term: string.Empty, from: new DateOnly(2025, 1, 1), to: new DateOnly(2025, 1, 31)),
            "legacy-keyword-with-dates" => SearchTask(21, "Completed", null, from: new DateOnly(2025, 1, 1), to: new DateOnly(2025, 1, 31)),
            _ => SearchTask(21, "Completed", null),
        };
        ServeTasks(task);
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(21, 3)));

        var cut = RenderPage(taskId: 21, selectKeywordMode: false);
        cut.WaitForState(() => ResultsExplorerReady(cut), PollWaitTimeout);

        cut.Find($"#ks-mode-{radio}").HasAttribute("checked").Should().BeTrue("a legacy task (mode null) opens as Keyword Search; a blank-term date task as Date Search");
        cut.FindAll("#fe-root tr.fe-row .fe-name").Select(n => n.TextContent).Should().Equal("Alpha", "Beta");
        _apiHandler.Requests.Should().Contain(r => r.RequestUri!.AbsolutePath == "/api/search/21/browse");
        cut.FindAll("#ks-send-to-quarantine").Should().ContainSingle();
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 V4
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("404404")]   // GET /tasks/{id} 404
    [InlineData("880")]      // own task, but a ContentScan (not a search task)
    public void Test_V4_BadOrWrongTypeTaskIdShowsNotice(string taskIdQuery)
    {
        var scan = new TaskDetailResponse
        {
            TaskId = 880,
            Type = "ContentScan",
            SubmittedBy = "full-access-user",
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            Status = "Completed",
            IsAcknowledged = false,
        };
        ServeTasks(scan);
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(1, 3)));

        Services.GetRequiredService<NavigationManager>().NavigateTo($"/?taskId={Uri.EscapeDataString(taskIdQuery)}");
        var cut = RenderComponent<Web.Components.Pages.KeywordSearch>();
        cut.WaitForState(() => cut.Markup.Contains("That task could not be found."), TimeSpan.FromSeconds(3));

        cut.Find(".cachet-ks-notice").TextContent.Should().Be("That task could not be found.");
        cut.FindAll("#ks-send-to-quarantine").Should().BeEmpty("no results explorer for a task that was not found");
        _apiHandler.ResultsCallCount.Should().Be(0);
        _apiHandler.AcknowledgedTaskIds.Should().BeEmpty();
        _apiHandler.Requests.ToList().Should().NotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/browse") && r.RequestUri.AbsolutePath.StartsWith("/api/search/"));
    }

    // CAS-048 V4 also names "a 404/409 from browse", while V7 says browse 404/409/5xx show the generic message
    // with Retry. The implementation follows V7 (the explorer's own error state); this pins that behaviour:
    // no Api detail, no crash, a Retry that recovers. Spec ambiguity reported to the author.
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public void Test_V4_BadOrWrongTypeTaskIdShowsNotice_BrowseErrorShowsExplorerErrorWithRetry(HttpStatusCode status)
    {
        ServeTasks(SearchTask(31, "Completed", "Keyword"));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(31, 3)));
        _apiHandler.OnGetSearchBrowse = (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"detail\":\"internal path C:\\\\secret\"}", System.Text.Encoding.UTF8, "application/json"),
        });

        var cut = RenderPage(taskId: 31, selectKeywordMode: false);
        cut.WaitForState(() => cut.FindAll("#ks-send-to-quarantine").Count > 0 && cut.FindAll("#fe-root .fe-error").Count > 0, PollWaitTimeout);

        cut.Find("#fe-root .fe-error").TextContent.Should().Contain("The folder list could not be loaded. Try again.");
        cut.Markup.Should().NotContain("secret").And.NotContain("internal path");

        _apiHandler.OnGetSearchBrowse = FileExplorer.BrowseFixtures.ServeResults;
        cut.Find("#fe-retry").Click();
        cut.WaitForState(() => ResultsExplorerReady(cut), PollWaitTimeout);
    }

    // -----------------------------------------------------------------------------------------
    // CAS-057 V9 / V10 — reload and deep link of a Cancelled task
    // -----------------------------------------------------------------------------------------

    private static SearchResultsResponse ResultsCancelled(int taskId, int matches) => new()
    {
        TaskId = taskId,
        Status = "Cancelled",
        IsPartial = true,
        ItemsProcessed = 40,
        ItemsTotal = 120,
        FilenameMatches = new PagedResult<FilenameMatchItem>
        {
            Items = Array.Empty<FilenameMatchItem>(),
            Page = 1,
            PageSize = 1,
            TotalCount = matches,
        },
        ContentMatches = new PagedResult<ContentMatchItem>
        {
            Items = Array.Empty<ContentMatchItem>(),
            Page = 1,
            PageSize = 1,
            TotalCount = 0,
        },
    };

    [Theory]
    [InlineData("plain-with-matches")]
    [InlineData("plain-no-matches")]
    [InlineData("deeplink-no-matches")]
    public async Task Test_V9_ReloadCancelledTask(string scenario)
    {
        var matches = scenario == "plain-with-matches" ? 3 : 0;
        ServeTasks(SearchTask(41, "Cancelled", "Keyword"));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCancelled(41, matches)));

        if (scenario == "plain-with-matches")
        {
            // A plain page open reloads the latest task: partial results + notice, as for a Completed task.
            var cut = RenderPage();
            cut.WaitForState(() => ResultsExplorerReady(cut) && cut.FindAll(".cachet-ks-notice").Count > 0, PollWaitTimeout);

            cut.Find(".cachet-ks-notice").TextContent.Should().Be(
                "Search cancelled — 40 of 120 items were checked. Showing the 3 matches found so far; these results are incomplete.");
            cut.FindAll("#fe-root tr.fe-row .fe-name").Select(n => n.TextContent).Should().Equal("Alpha", "Beta");
            cut.Find("p.cachet-ks-context-header").TextContent.Should().Contain("Showing partial results for \"invoice\" (All), submitted");
            _apiHandler.Requests.Should().Contain(r => r.RequestUri!.AbsolutePath == "/api/search/41/browse");
            return;
        }

        if (scenario == "plain-no-matches")
        {
            // A plain open must not nag about an old cancel: the unchanged empty initial state.
            var cut = RenderPage();
            await WaitUntilAsync(() => _apiHandler.ResultsCallCount >= 1, PollWaitTimeout);
            await cut.InvokeAsync(() => { }); // flush any continuation still queued on the renderer

            cut.FindAll(".cachet-ks-notice").Should().BeEmpty();
            cut.FindAll("#fe-root").Should().BeEmpty();
            cut.FindAll(".cachet-ks-context-header").Should().BeEmpty();
            cut.FindAll(".cachet-ks-error-banner").Should().BeEmpty();
            return;
        }

        // A deep link to the same cancelled task shows the notice alone.
        var deepLinked = RenderPage(taskId: 41, selectKeywordMode: false);
        deepLinked.WaitForState(() => deepLinked.FindAll(".cachet-ks-notice").Count > 0, PollWaitTimeout);

        deepLinked.Find(".cachet-ks-notice").TextContent.Should().Be(
            "Search cancelled — 40 of 120 items were checked; no matches were found.");
        deepLinked.FindAll("#fe-root").Should().BeEmpty("no results panel without matches");
        deepLinked.FindAll(".cachet-ks-context-header").Should().BeEmpty();
        deepLinked.Find("#ks-mode-keyword").HasAttribute("checked").Should().BeTrue("a deep link selects the task's mode");
    }

    [Fact]
    public void Test_V10_NoAcknowledgeForCancelled()
    {
        // Not acknowledged server-side, with matches, opened by deep link (the only path that acknowledges
        // a Completed task): a Cancelled task must still never be acknowledged.
        ServeTasks(SearchTask(51, "Cancelled", "Keyword", isAcknowledged: false));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCancelled(51, 3)));

        var cut = RenderPage(taskId: 51, selectKeywordMode: false);
        cut.WaitForState(() => ResultsExplorerReady(cut) && cut.FindAll(".cachet-ks-notice").Count > 0, PollWaitTimeout);

        _apiHandler.AcknowledgedTaskIds.Should().BeEmpty();
        _apiHandler.Requests.ToList().Should().NotContain(
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/acknowledge", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Single-client <see cref="IHttpClientFactory"/> stand-in — this component only ever
    /// requests the "CachetApi" named client, so no name-based routing is needed. Mirrors every
    /// other test class in this test project.</summary>
    // -----------------------------------------------------------------------------------------
    // CAS-044 SC-3 (deep-link case): a ?taskId= open still starts in Files mode; the reloaded task's
    // results are kept and appear once Keyword Search is selected.
    // -----------------------------------------------------------------------------------------

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met before the timeout elapsed.");
            }

            await Task.Delay(100);
        }
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
