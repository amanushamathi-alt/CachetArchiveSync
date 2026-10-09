using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using Bunit;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cachet.ArchiveSync.Web.Tests.TaskManager;

/// <summary>
/// CAS-027 §13 — proves every Success Criterion and Validation owned directly by
/// <c>TaskManagerModal.razor</c> (SC-1, SC-4, SC-5, SC-6, SC-9, SC-10, V1, V2, V5, V6) via bUnit,
/// with the "CachetApi" named <see cref="HttpClient"/> redirected to
/// <see cref="FakeTaskManagerApiHandler"/> so no real Api process/DB is ever reached — matching
/// <c>QuarantineQueueModalTests</c>' (CAS-024) own conventions for a shared, non-routed, @ref-opened
/// modal component in this test project.
///
/// <para>
/// SC-2/SC-3 (the "View Results" navigation + destination-screen auto-populate pair, spanning this
/// component AND <c>KeywordSearch.razor</c>/<c>ODriveSyncContentScan.razor</c>) and V-3 (the invalid
/// deep-link error banner on those same two host screens) are proved in
/// <c>KeywordSearchDeepLinkTests</c>/<c>ODriveSyncContentScanDeepLinkTests</c> instead, per this
/// spec's own §13 test-class mapping. SC-7/SC-8/V-4 (the <c>MainLayout.razor</c> header badge) are
/// proved in <c>MainLayoutTests</c>.
/// </para>
///
/// <para>
/// This spec (<c>touches.api: false</c>) introduces no new Api endpoint — CAS-026's four endpoints
/// and CAS-016's restart endpoint are consumed as-is — so there is no new integration-test surface to
/// add here; SC-1, SC-6, SC-9, and V-5 (which this spec's own §13 draft originally assigned to a
/// <c>TaskManagerEndToEndTests</c> <c>WebApplicationFactory</c> class) are instead proved as bUnit
/// component tests below, exercising this Web-layer component against the same fake Api boundary as
/// every other test in this class.
/// </para>
/// </summary>
public sealed class TaskManagerModalTests : TestContext
{
    private readonly FakeTaskManagerApiHandler _apiHandler = new();
    private readonly CapturingLoggerProvider _loggerProvider = new();

    public TaskManagerModalTests()
    {
        var httpClient = new HttpClient(_apiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(httpClient));
        Services.AddLogging(builder => builder.AddProvider(_loggerProvider));

        // CAS-048: the Task Manager button is hosted by TaskManagerBadge, whose summary poll needs the interval option.
        Services.AddSingleton<Microsoft.Extensions.Options.IOptions<TaskPollingOptions>>(
            Microsoft.Extensions.Options.Options.Create(new TaskPollingOptions { TaskManagerPollIntervalSeconds = 60 }));
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };

    private static TaskListItem SampleTask(
        int taskId, string type, string status, string? submittedBy = null, DateTimeOffset? startTime = null, string? searchMode = null) => new()
        {
            TaskId = taskId,
            Type = type,
            SubmittedBy = submittedBy ?? $"user-{taskId}",
            SubmittedAt = startTime ?? DateTimeOffset.UtcNow,
            StartTime = startTime ?? DateTimeOffset.UtcNow,
            IsAcknowledged = false,
            Status = status,
            SearchMode = searchMode,
        };

    private static PagedResult<TaskListItem> PageOf(params TaskListItem[] items) => new()
    {
        Items = items,
        Page = 1,
        PageSize = 100,
        TotalCount = items.Length,
    };

    private IRenderedComponent<Web.Components.Shared.TaskManagerModal> RenderModal() =>
        RenderComponent<Web.Components.Shared.TaskManagerModal>();

    private static async Task OpenAsync(IRenderedComponent<Web.Components.Shared.TaskManagerModal> cut) =>
        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

    private static IElement RowFor(IRenderedFragment cut, string rowMarker) =>
        cut.FindAll(".cachet-tm-table tbody tr").First(r => r.TextContent.Contains(rowMarker, StringComparison.OrdinalIgnoreCase));

    private static IElement RowButton(
        IRenderedFragment cut, string rowMarker, string buttonLabel) =>
        RowFor(cut, rowMarker).QuerySelectorAll("button").First(b => b.TextContent.Trim() == buttonLabel);

    // -----------------------------------------------------------------------------------------
    // SC-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC1_ModalListsOwnTasksWithRequiredColumns()
    {
        var newest = SampleTask(3, "KeywordSearch", "Completed", submittedBy: "user-newest");
        var middle = SampleTask(2, "ContentScan", "Running", submittedBy: "user-middle");
        var oldest = SampleTask(1, "SyncCopy", "Queued", submittedBy: "user-oldest");

        // GET /tasks is already newest-first per CAS-026's own contract (§8.1) — this modal must
        // render the server's order as-is, not re-sort client-side.
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(newest, middle, oldest)));

        var cut = RenderModal();
        await OpenAsync(cut);

        var rows = cut.FindAll(".cachet-tm-table tbody tr");
        rows.Should().HaveCount(3);
        rows[0].TextContent.Should().Contain("user-newest");
        rows[1].TextContent.Should().Contain("user-middle");
        rows[2].TextContent.Should().Contain("user-oldest");

        rows[0].TextContent.Should().Contain("Keyword Search", "Type must render the display label, never the raw enum (§8.2)");
        rows[0].TextContent.Should().Contain("Completed");
        rows[1].TextContent.Should().Contain("Content Scan");
        rows[1].TextContent.Should().Contain("Running");
        rows[2].TextContent.Should().Contain("Copy to B2");
        rows[2].TextContent.Should().Contain("Queued");

        _apiHandler.Requests.Should().Contain(
            r => r.RequestUri!.Query.Contains("pageSize=100"),
            "a single page of up to 100 tasks (the Api cap) rather than pager UI (§4/§8.1; CAS-048 V10)");
    }

    // -----------------------------------------------------------------------------------------
    // V-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V1_TaskTableHasExactlyFourRequiredColumns()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(SampleTask(1, "KeywordSearch", "Queued"))));

        var cut = RenderModal();
        await OpenAsync(cut);

        var headers = cut.FindAll(".cachet-tm-table thead th").Select(h => h.TextContent.Trim()).ToArray();
        headers.Should().HaveCount(5, "the four required columns plus one trailing (unlabeled) row-action column");
        headers.Take(4).Should().Equal("Type", "Submitted By", "Start Time", "Status");
        headers[4].Should().BeEmpty("the row-action column carries no header text — not an extra named column");

        cut.FindAll(".cachet-tm-modal input").Should().BeEmpty("no filter controls anywhere in the modal (§4/§7 V1)");
        cut.FindAll(".cachet-tm-modal select").Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // V-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V2_RowAffordancesMatchStatus()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(1, "KeywordSearch", "Queued", submittedBy: "row-queued"),
            SampleTask(2, "KeywordSearch", "Running", submittedBy: "row-running"),
            SampleTask(3, "KeywordSearch", "Completed", submittedBy: "row-completed"),
            SampleTask(4, "KeywordSearch", "Failed", submittedBy: "row-failed"),
            SampleTask(5, "KeywordSearch", "Cancelled", submittedBy: "row-cancelled"))));

        var cut = RenderModal();
        await OpenAsync(cut);

        RowFor(cut, "row-queued").QuerySelectorAll("button").Should().BeEmpty("no row action for Queued (§8.2)");
        RowFor(cut, "row-running").QuerySelectorAll("button").Should().BeEmpty("no row action for Running — no percent/ETA either (§4/§8.2)");
        RowFor(cut, "row-cancelled").QuerySelectorAll("button").Should().BeEmpty("Cancelled renders defensively with no row action (§8.2)");

        var completedButtons = RowFor(cut, "row-completed").QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToArray();
        completedButtons.Should().Equal("View Results");

        var failedButtons = RowFor(cut, "row-failed").QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToArray();
        failedButtons.Should().Contain("View Details");
        failedButtons.Should().Contain("Restart");
        failedButtons.Should().NotContain("View Results");
    }

    // -----------------------------------------------------------------------------------------
    // SC-4
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("SyncCopy")]
    [InlineData("SyncUpdate")]
    [InlineData("SyncPurge")]
    public async Task Test_SC4_ViewResultsForSyncActionsNavigatesWithoutTaskId(string syncType)
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(50, syncType, "Completed", submittedBy: "sync-row"))));

        var cut = RenderModal();
        await OpenAsync(cut);

        RowButton(cut, "sync-row", "View Results").Click();

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.Uri.Should().Be(
            "http://localhost/odrive-sync",
            $"{syncType} rows must navigate with no ?taskId= param — CAS-007's contract cannot resolve a sync " +
            "action's own taskId independently of its originating scan (§0 point 3)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC5_ViewDetailsShowsResultDetailInline()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(60, "KeywordSearch", "Failed", submittedBy: "failed-row"))));
        _apiHandler.OnGetTaskDetail = (_, taskId) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new TaskDetailResponse
        {
            TaskId = taskId,
            Type = "KeywordSearch",
            SubmittedBy = "failed-row",
            SubmittedAt = DateTimeOffset.UtcNow,
            StartTime = DateTimeOffset.UtcNow,
            IsAcknowledged = false,
            Status = "Failed",
            ResultDetail = "Worker crashed: OutOfMemoryException",
        }));

        var cut = RenderModal();
        await OpenAsync(cut);

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uriBeforeClick = navigationManager.Uri;

        RowButton(cut, "failed-row", "View Details").Click();

        cut.WaitForState(() => cut.Markup.Contains("Worker crashed: OutOfMemoryException"), TimeSpan.FromSeconds(3));

        cut.Markup.Should().Contain("Worker crashed: OutOfMemoryException");
        navigationManager.Uri.Should().Be(uriBeforeClick, "View Details must expand inline — no navigation (§8.4, SC-5)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC6_RestartCreatesNewQueuedRowVisibleAfterRefresh()
    {
        var loadCallCount = 0;
        _apiHandler.OnGetTasks = (_, _) =>
        {
            loadCallCount++;
            var items = loadCallCount == 1
                ? new[] { SampleTask(66, "KeywordSearch", "Failed", submittedBy: "failed-row-66") }
                : new[]
                {
                    SampleTask(67, "KeywordSearch", "Queued", submittedBy: "new-row-67"),
                    SampleTask(66, "KeywordSearch", "Failed", submittedBy: "failed-row-66"),
                };
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(items)));
        };
        _apiHandler.OnPostRestart = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));

        var cut = RenderModal();
        await OpenAsync(cut);
        cut.Markup.Should().NotContain("new-row-67");

        RowButton(cut, "failed-row-66", "Restart").Click();

        cut.WaitForState(() => cut.Markup.Contains("new-row-67"), TimeSpan.FromSeconds(3));

        cut.Markup.Should().Contain("new-row-67");
        RowFor(cut, "new-row-67").TextContent.Should().Contain("Queued");
        _apiHandler.RestartCallCount.Should().Be(1);
        _apiHandler.LastRestartTaskId.Should().Be(66);
    }

    // -----------------------------------------------------------------------------------------
    // SC-9
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// FR-20/SC-9's guarantee is that task state lives entirely server-side
    /// (<c>dbo.task_queue</c>) — a browser/circuit close-and-reopen is, from this Web-layer
    /// component's own perspective, nothing more than a brand-new component instance issuing a
    /// fresh <c>GET /tasks</c>. This proves <c>TaskManagerModal</c> makes no client-cached
    /// assumption of its own: a second, independent modal instance (standing in for a new
    /// browser session/circuit) reflects whatever the (fake, stand-in-for-persisted) backend
    /// currently returns — including a status that changed while "the browser was closed".
    /// </summary>
    [Fact]
    public async Task Test_SC9_TaskStateVisibleAfterNewSession()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(99, "KeywordSearch", "Queued", submittedBy: "session-row-99"))));

        var firstSessionCut = RenderModal();
        await OpenAsync(firstSessionCut);
        RowFor(firstSessionCut, "session-row-99").TextContent.Should().Contain("Queued");

        // The task finished while "the browser was closed" — the backend now reports Completed.
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(99, "KeywordSearch", "Completed", submittedBy: "session-row-99"))));

        var newSessionCut = RenderModal(); // a brand-new component instance/circuit
        await OpenAsync(newSessionCut);

        RowFor(newSessionCut, "session-row-99").TextContent.Should().Contain(
            "Completed",
            "a new session's Task Manager must reflect the task's current server-side state, not a stale/cached one (FR-20, SC-9)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-10
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC10_ProcessingTimeNoteAlwaysVisible()
    {
        const string ExpectedNoteFragment =
            "Search, scan, and sync tasks run in the background and can take anywhere from about 10";

        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf()));

        var emptyCut = RenderModal();
        await OpenAsync(emptyCut);
        emptyCut.Markup.Should().Contain(ExpectedNoteFragment, "the FR-22a note is static and always visible, even with no task at all");

        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(1, "KeywordSearch", "Running"))));

        var runningCut = RenderModal();
        await OpenAsync(runningCut);
        runningCut.Markup.Should().Contain(ExpectedNoteFragment, "the note remains visible regardless of whether a task is currently in flight");
    }

    // -----------------------------------------------------------------------------------------
    // V-5
    // -----------------------------------------------------------------------------------------

    // -----------------------------------------------------------------------------------------
    // V-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V6_RestartDisabledWhileInFlight()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(70, "KeywordSearch", "Failed", submittedBy: "inflight-row-70"))));

        var restartTcs = new TaskCompletionSource<HttpResponseMessage>();
        _apiHandler.OnPostRestart = (_, _) => restartTcs.Task;

        var cut = RenderModal();
        await OpenAsync(cut);

        RowButton(cut, "inflight-row-70", "Restart").Click();

        // RestartAsync adds the row to restartInFlight and calls StateHasChanged() BEFORE awaiting
        // the (still-pending) POST — so the button must already be disabled by the time Click()
        // returns to this synchronous caller.
        RowButton(cut, "inflight-row-70", "Restart").HasAttribute("disabled").Should().BeTrue(
            "Restart must disable immediately while its own request is outstanding (§7 V6)");

        // A second click while the first is still in flight must be a genuine no-op, proven at the
        // component's own guard (restartInFlight.Add) — not merely via the DOM disabled attribute,
        // which a synthetic bUnit click does not itself enforce.
        RowButton(cut, "inflight-row-70", "Restart").Click();
        _apiHandler.RestartCallCount.Should().Be(1, "no double-submit while a restart for this row is already outstanding (§7 V6)");

        restartTcs.SetResult(new HttpResponseMessage(HttpStatusCode.Accepted));

        cut.WaitForState(() => !RowButton(cut, "inflight-row-70", "Restart").HasAttribute("disabled"), TimeSpan.FromSeconds(3));
    }

    // -----------------------------------------------------------------------------------------
    // Coverage-gap closures (not spec-mapped SC/V IDs) — same precedent as QuarantineQueueModalTests'/
    // KeywordSearchPageTests'/ODriveSyncPageTests' own follow-up closures, closing branches §10/§7
    // don't individually name but §13's ≥85% line-coverage target requires.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_EmptyStateShowsExplicitCopy()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf()));

        var cut = RenderModal();
        await OpenAsync(cut);

        cut.Markup.Should().Contain("No tasks yet. Search, scan, and sync actions you submit will appear here.");
        cut.Markup.Should().NotContain("<table", "a zero-task list renders explanatory copy, not an empty table");
    }

    /// <summary>Closes the coverage gap on <c>LoadAsync</c>'s catch(HttpRequestException/
    /// TaskCanceledException) branch.</summary>
    [Fact]
    public async Task Test_LoadTasksApiUnreachableShowsGenericErrorNotCrash()
    {
        _apiHandler.OnGetTasks = (_, _) => throw new HttpRequestException("simulated: Api unreachable");

        var cut = RenderModal();
        var act = async () => await OpenAsync(cut);

        await act.Should().NotThrowAsync();
        cut.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    /// <summary>Closes the coverage gap on <c>LoadAsync</c>'s non-success-status branch.</summary>
    [Fact]
    public async Task Test_LoadTasksServerErrorShowsGenericErrorNotCrash()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var cut = RenderModal();
        var act = async () => await OpenAsync(cut);

        await act.Should().NotThrowAsync();
        cut.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    /// <summary>Closes the coverage gap on <c>ToggleDetailsAsync</c>'s collapse branch — clicking
    /// View Details a second time on the same row collapses the inline expansion.</summary>
    [Fact]
    public async Task Test_ViewDetailsCollapsesOnSecondClick()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(61, "KeywordSearch", "Failed", submittedBy: "collapse-row"))));
        _apiHandler.OnGetTaskDetail = (_, taskId) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new TaskDetailResponse
        {
            TaskId = taskId,
            Type = "KeywordSearch",
            SubmittedBy = "collapse-row",
            SubmittedAt = DateTimeOffset.UtcNow,
            StartTime = DateTimeOffset.UtcNow,
            IsAcknowledged = false,
            Status = "Failed",
            ResultDetail = "some detail",
        }));

        var cut = RenderModal();
        await OpenAsync(cut);

        RowButton(cut, "collapse-row", "View Details").Click();
        cut.WaitForState(() => cut.Markup.Contains("some detail"), TimeSpan.FromSeconds(3));

        RowButton(cut, "collapse-row", "View Details").Click();

        cut.Markup.Should().NotContain("some detail", "a second click on the same Failed row's View Details must collapse it");
    }

    /// <summary>Closes the coverage gap on <c>ToggleDetailsAsync</c>'s catch(HttpRequestException/
    /// TaskCanceledException) branch.</summary>
    [Fact]
    public async Task Test_ViewDetailsApiUnreachableShowsGenericErrorInline()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(62, "KeywordSearch", "Failed", submittedBy: "detail-error-row"))));
        _apiHandler.OnGetTaskDetail = (_, _) => throw new HttpRequestException("simulated: Api unreachable");

        var cut = RenderModal();
        await OpenAsync(cut);

        var act = () => RowButton(cut, "detail-error-row", "View Details").Click();

        act.Should().NotThrow();
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
    }

    /// <summary>Closes the coverage gap on <c>ToggleDetailsAsync</c>'s non-success-status branch.</summary>
    [Fact]
    public async Task Test_ViewDetailsServerErrorShowsGenericErrorInline()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(63, "KeywordSearch", "Failed", submittedBy: "detail-404-row"))));
        _apiHandler.OnGetTaskDetail = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var cut = RenderModal();
        await OpenAsync(cut);

        RowButton(cut, "detail-404-row", "View Details").Click();

        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
    }

    /// <summary>Closes the coverage gap on <c>RestartAsync</c>'s catch(HttpRequestException/
    /// TaskCanceledException) branch.</summary>
    [Fact]
    public async Task Test_RestartApiUnreachableShowsRowLevelErrorNotCrash()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(71, "KeywordSearch", "Failed", submittedBy: "restart-unreachable-row"))));
        _apiHandler.OnPostRestart = (_, _) => throw new HttpRequestException("simulated: Api unreachable");

        var cut = RenderModal();
        await OpenAsync(cut);

        var act = () => RowButton(cut, "restart-unreachable-row", "Restart").Click();

        act.Should().NotThrow();
        cut.WaitForState(
            () => RowFor(cut, "restart-unreachable-row").TextContent.Contains("Something went wrong. Please try again."),
            TimeSpan.FromSeconds(3));
        RowButton(cut, "restart-unreachable-row", "Restart").HasAttribute("disabled").Should().BeFalse(
            "a failed restart attempt must release the in-flight guard so the user can retry");
    }

    /// <summary>Closes the coverage gap on <c>RestartAsync</c>'s non-success-status branch (§8.5's
    /// 403/404/409 row-level generic message).</summary>
    [Fact]
    public async Task Test_RestartServerErrorShowsRowLevelErrorMessage()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(72, "KeywordSearch", "Failed", submittedBy: "restart-conflict-row"))));
        _apiHandler.OnPostRestart = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        var cut = RenderModal();
        await OpenAsync(cut);

        RowButton(cut, "restart-conflict-row", "Restart").Click();

        cut.WaitForState(
            () => RowFor(cut, "restart-conflict-row").TextContent.Contains("Something went wrong. Please try again."),
            TimeSpan.FromSeconds(3));
    }

    /// <summary>Closes the coverage gap on <c>Close</c> — dismisses the modal without side effects.</summary>
    [Fact]
    public async Task Test_CloseDismissesModalWithoutSideEffects()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(80, "KeywordSearch", "Queued"))));

        var cut = RenderModal();
        await OpenAsync(cut);

        cut.FindAll(".cachet-tm-footer button").First(b => b.TextContent.Contains("Close")).Click();

        cut.Markup.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-9: the Task Manager button (hosted by TaskManagerBadge) is enabled and opens the modal.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC9_TaskManagerOpensAndListsTasks()
    {
        _apiHandler.OnGetSummary = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new TaskSummaryResponse { ActiveCount = 1, UnacknowledgedTerminalCount = 1 }));
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(1, "KeywordSearch", "Completed", submittedBy: "row-filename", searchMode: "Filename"),
            SampleTask(2, "KeywordSearch", "Completed", submittedBy: "row-legacy", searchMode: null),
            SampleTask(3, "KeywordSearch", "Running", submittedBy: "row-date", searchMode: "Date"),
            SampleTask(4, "KeywordSearch", "Completed", submittedBy: "row-keyword", searchMode: "Keyword"),
            SampleTask(5, "ContentScan", "Queued", submittedBy: "row-scan"))));

        var cut = RenderComponent<Web.Components.Layout.TaskManagerBadge>();
        var button = cut.Find("button");

        button.TextContent.Should().Contain("Task Manager");
        button.HasAttribute("disabled").Should().BeFalse("the button is enabled (CAS-048 §2.3)");
        button.HasAttribute("title").Should().BeFalse("the 'Coming soon' title is removed");
        cut.FindAll(".cachet-tm-modal").Should().BeEmpty("the modal is closed until the button is clicked");

        button.Click();
        cut.WaitForState(() => cut.FindAll(".cachet-tm-table tbody tr").Count == 5, TimeSpan.FromSeconds(3));
        await Task.CompletedTask;

        RowFor(cut, "row-filename").QuerySelectorAll("td")[0].TextContent.Should().Be("Filename Search");
        RowFor(cut, "row-legacy").QuerySelectorAll("td")[0].TextContent.Should().Be("Keyword Search", "a null searchMode is a legacy keyword search");
        RowFor(cut, "row-date").QuerySelectorAll("td")[0].TextContent.Should().Be("Date Search");
        RowFor(cut, "row-keyword").QuerySelectorAll("td")[0].TextContent.Should().Be("Keyword Search");
        RowFor(cut, "row-scan").QuerySelectorAll("td")[0].TextContent.Should().Be("Content Scan");
        _apiHandler.Requests.Should().Contain(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/tasks");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-11
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC11_NonCompletedRowsInert()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(1, "KeywordSearch", "Queued", submittedBy: "row-queued"),
            SampleTask(2, "KeywordSearch", "Running", submittedBy: "row-running"),
            SampleTask(3, "KeywordSearch", "Failed", submittedBy: "row-failed"),
            SampleTask(4, "KeywordSearch", "Cancelled", submittedBy: "row-cancelled"),
            SampleTask(5, "KeywordSearch", "Completed", submittedBy: "row-completed"))));

        var cut = RenderModal();
        await OpenAsync(cut);

        foreach (var marker in new[] { "row-queued", "row-running", "row-failed", "row-cancelled" })
        {
            var row = RowFor(cut, marker);
            row.GetAttribute("aria-disabled").Should().Be("true", $"{marker} is not clickable");
            row.HasAttribute("tabindex").Should().BeFalse();
            row.HasAttribute("role").Should().BeFalse();
            row.GetAttribute("title").Should().Be("Available when the task completes");
            row.ClassList.Should().Contain("cachet-tm-row-inert").And.NotContain("cachet-tm-row-clickable");
        }

        // Failed still offers View Details and Restart.
        RowFor(cut, "row-failed").QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Equal("View Details", "Restart");

        // The Completed row is the only clickable one.
        var completed = RowFor(cut, "row-completed");
        completed.GetAttribute("tabindex").Should().Be("0");
        completed.GetAttribute("role").Should().Be("link");
        completed.HasAttribute("aria-disabled").Should().BeFalse();
        completed.ClassList.Should().Contain("cachet-tm-row-clickable");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 V5
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public async Task Test_V5_NonCompletedRowNotClickable(string status)
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(7, "KeywordSearch", status, submittedBy: "inert-row"))));
        var cut = RenderModal();
        await OpenAsync(cut);
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uriBefore = navigationManager.Uri;
        var requestsBefore = _apiHandler.Requests.Count;

        var row = RowFor(cut, "inert-row");
        var click = () => row.Click();
        var key = () => row.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        click.Should().Throw<MissingEventHandlerException>("a non-Completed row has no click handler at all");
        key.Should().Throw<MissingEventHandlerException>("nor a keyboard handler");
        navigationManager.Uri.Should().Be(uriBefore);
        _apiHandler.AcknowledgedTaskIds.Should().BeEmpty();
        _apiHandler.Requests.Count.Should().Be(requestsBefore, "nothing is sent when a non-Completed row is activated");
        cut.FindAll(".cachet-tm-modal").Should().ContainSingle("the modal stays open");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 V10
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V10_NoMarkViewedPageSize100AckOnOpenOnly()
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(1, "KeywordSearch", "Completed", submittedBy: "row-completed", searchMode: "Filename"),
            SampleTask(2, "KeywordSearch", "Failed", submittedBy: "row-failed"),
            SampleTask(3, "KeywordSearch", "Queued", submittedBy: "row-queued"),
            SampleTask(4, "SyncCopy", "Completed", submittedBy: "row-sync"))));
        _apiHandler.OnGetTaskDetail = (_, taskId) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new TaskDetailResponse
        {
            TaskId = taskId,
            Type = "KeywordSearch",
            SubmittedBy = "row-failed",
            SubmittedAt = DateTimeOffset.UtcNow,
            IsAcknowledged = false,
            Status = "Failed",
            ResultDetail = "boom",
        }));
        var cut = RenderModal();

        // Opening the modal lists tasks (pageSize <= 100) and acknowledges nothing.
        await OpenAsync(cut);
        var listRequest = _apiHandler.Requests.Single(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/tasks");
        var pageSize = System.Web.HttpUtility.ParseQueryString(listRequest.RequestUri!.Query)["pageSize"];
        int.Parse(pageSize!, System.Globalization.CultureInfo.InvariantCulture).Should().BeLessThanOrEqualTo(100);
        _apiHandler.MarkViewedCallCount.Should().Be(0);
        _apiHandler.AcknowledgedTaskIds.Should().BeEmpty("merely opening the modal does not clear the badge");

        // An inert row opens nothing and acknowledges nothing.
        var inert = () => RowFor(cut, "row-queued").Click();
        inert.Should().Throw<MissingEventHandlerException>();
        _apiHandler.AcknowledgedTaskIds.Should().BeEmpty();

        // Clicking a Completed row acknowledges that task only, once.
        RowFor(cut, "row-completed").Click();
        _apiHandler.AcknowledgedTaskIds.Should().Equal(1);

        // View Details on a Failed row acknowledges that task only.
        await OpenAsync(cut);
        RowButton(cut, "row-failed", "View Details").Click();
        cut.WaitForState(() => cut.Markup.Contains("boom"), TimeSpan.FromSeconds(3));
        _apiHandler.AcknowledgedTaskIds.Should().Equal(1, 2);

        // View Results acknowledges the opened task, once.
        RowButton(cut, "row-sync", "View Results").Click();
        _apiHandler.AcknowledgedTaskIds.Should().Equal(1, 2, 4);

        _apiHandler.MarkViewedCallCount.Should().Be(0, "POST /tasks/mark-viewed is never called");
        _apiHandler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.Contains("mark-viewed"));
    }

    // -----------------------------------------------------------------------------------------
    // Coverage-gap closures (not spec-mapped SC/V IDs): keyboard activation and acknowledge failures.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Enter", true)]
    [InlineData(" ", true)]
    [InlineData("a", false)]
    public async Task Test_CompletedRowKeyboardActivation(string key, bool expectOpen)
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(8, "ContentScan", "Completed", submittedBy: "key-row"))));
        var cut = RenderModal();
        await OpenAsync(cut);
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uriBefore = navigationManager.Uri;

        RowFor(cut, "key-row").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = key });

        navigationManager.Uri.Should().Be(expectOpen ? "http://localhost/odrive-sync?taskId=8" : uriBefore);
        _apiHandler.AcknowledgedTaskIds.Should().Equal(expectOpen ? new[] { 8 } : Array.Empty<int>());
        cut.FindAll(".cachet-tm-modal").Count.Should().Be(expectOpen ? 0 : 1, "the modal closes when a task is opened");
    }

    [Theory]
    [InlineData("500")]
    [InlineData("throws")]
    public async Task Test_AcknowledgeFailureIsLoggedNotShownAndNavigationStillHappens(string failure)
    {
        _apiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, PageOf(
            SampleTask(9, "KeywordSearch", "Completed", submittedBy: "ack-fail-row"))));
        _apiHandler.OnPostAcknowledge = (_, _) => failure == "500"
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
            : throw new HttpRequestException("simulated: Api unreachable");
        var cut = RenderModal();
        await OpenAsync(cut);

        RowFor(cut, "ack-fail-row").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().Be("http://localhost/?taskId=9");
        _loggerProvider.Messages.Should().Contain(m => m.Contains("acknowledge of task 9", StringComparison.Ordinal));
        cut.Markup.Should().NotContain("Something went wrong");
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
