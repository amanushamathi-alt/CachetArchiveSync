using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Bunit;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Components.Layout;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Web.Tests.Layout;

/// <summary>
/// CAS-030 §13 "TaskManagerBadgeTests" — SC-5, SC-6, V9 (badge variant), V10 (badge variant) for
/// <c>TaskManagerBadge.razor</c> (§8.1, FR-22), via bUnit. The "CachetApi" named client is redirected
/// to <see cref="ScriptedSummaryHandler"/>, which answers <c>GET /tasks/summary</c> from a per-call
/// script so each test controls exactly what every poll tick sees. The poll interval is injected via
/// <see cref="IOptions{TaskPollingOptions}"/> (§8.4) at 1 s so the real <see cref="PeriodicTimer"/>
/// ticks quickly.
/// </summary>
public sealed class TaskManagerBadgeTests : TestContext
{
    private static readonly TimeSpan TickWait = TimeSpan.FromSeconds(5);

    private readonly ScriptedSummaryHandler _handler = new();
    private readonly CapturingLoggerProvider _loggerProvider = new();
    private readonly TaskPollingOptions _pollingOptions = new() { TaskManagerPollIntervalSeconds = 1 };

    public TaskManagerBadgeTests()
    {
        var client = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(client));
        Services.AddLogging(builder => builder.AddProvider(_loggerProvider));
        Services.AddSingleton<IOptions<TaskPollingOptions>>(Options.Create(_pollingOptions));
    }

    private static Func<HttpResponseMessage> Summary(int active, int unacknowledgedTerminal) => () =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new TaskSummaryResponse
            {
                ActiveCount = active,
                UnacknowledgedTerminalCount = unacknowledgedTerminal,
            }),
        };

    private static Func<HttpResponseMessage> Status(HttpStatusCode status) => () => new HttpResponseMessage(status);

    private static Func<HttpResponseMessage> RawBody(string body) => () =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static Func<HttpResponseMessage> NetworkFailure() => () =>
        throw new HttpRequestException("simulated: Api unreachable");

    private static string? CountText(IRenderedComponent<TaskManagerBadge> cut) =>
        cut.FindAll(".cachet-task-badge-count").SingleOrDefault()?.TextContent;

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<TaskManagerBadge> cut) => cut.Find("button");

    private static string LiveRegionText(IRenderedComponent<TaskManagerBadge> cut) =>
        cut.Find("[aria-live=polite]").TextContent;

    // -----------------------------------------------------------------------------------------
    // SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC5_Badge_RendersImmediatelyPollsAndStopsOnDispose()
    {
        // Arrange — a 2 s interval, so a count visible well under 2 s can only come from the
        // on-init call, never from a tick.
        _pollingOptions.TaskManagerPollIntervalSeconds = 2;
        _handler.Script(Summary(1, 2), Summary(3, 2));
        _handler.Fallback = Summary(3, 2);

        // Act 1 — first render.
        var started = DateTime.UtcNow;
        var cut = RenderComponent<TaskManagerBadge>();
        cut.WaitForState(() => CountText(cut) == "3", TimeSpan.FromSeconds(1));

        // Assert 1 — called on init with no wait for a tick; count = activeCount + unacknowledgedTerminalCount.
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2), "the first call happens on init, not on the first tick");
        _handler.CallCount.Should().Be(1);
        _handler.RequestPaths.First().Should().EndWith("/tasks/summary");
        Button(cut).TextContent.Should().Contain("Task Manager");
        // CAS-048 §2.3 (absorbs CAS-033): the button is enabled and hosts the modal, so the former
        // disabled / "Coming soon" assertions are replaced.
        Button(cut).HasAttribute("disabled").Should().BeFalse("the Task Manager button is enabled (CAS-048 §2.3)");
        Button(cut).HasAttribute("title").Should().BeFalse("the 'Coming soon' title is removed");

        // Act 2 — the next tick refreshes the count.
        cut.WaitForState(() => CountText(cut) == "5", TickWait);
        _handler.CallCount.Should().BeGreaterThanOrEqualTo(2);

        // Act 3 — dispose, then wait well past two further intervals.
        DisposeComponents();
        var callsAtDispose = _handler.CallCount;
        await Task.Delay(TimeSpan.FromSeconds(4.5));

        // Assert 3 — no poll after disposal.
        _handler.CallCount.Should().Be(callsAtDispose, "the poll loop must stop on DisposeAsync (§8.0)");
    }

    [Fact]
    public void Test_SC5_Badge_ZeroCountShowsNoNumber()
    {
        // Coverage of SC-5's display rule (§8.1: no number at 0, Quarantine Queue / Audit Log pattern).
        _handler.Fallback = Summary(0, 0);

        var cut = RenderComponent<TaskManagerBadge>();
        cut.WaitForState(() => _handler.CallCount >= 1, TimeSpan.FromSeconds(1));

        CountText(cut).Should().BeNull();
        Button(cut).GetAttribute("aria-label").Should().Be("Task Manager");
        Button(cut).GetAttribute("class").Should().NotContain("cachet-task-badge-attention");
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC6_Badge_TerminalTransitionShowsAttentionState()
    {
        // Arrange — Running -> Completed: the total stays 1, the terminal count goes 0 -> 1.
        _handler.Script(Summary(1, 0));
        _handler.Fallback = Summary(0, 1);

        // Act 1
        var cut = RenderComponent<TaskManagerBadge>();
        cut.WaitForState(() => CountText(cut) == "1", TimeSpan.FromSeconds(1));

        // Assert 1 — in progress only: no attention state, nothing announced.
        Button(cut).GetAttribute("class").Should().NotContain("cachet-task-badge-attention");
        Button(cut).GetAttribute("aria-label").Should().Be("Task Manager — 1 in progress");
        LiveRegionText(cut).Should().BeEmpty();

        // Act 2 — the next tick sees the task finish.
        cut.WaitForState(() => LiveRegionText(cut) == "A task has finished.", TickWait);

        // Assert 2
        CountText(cut).Should().Be("1", "the §1.1 total does not change when a task finishes");
        Button(cut).GetAttribute("class").Should().Contain("cachet-task-badge-attention");
        Button(cut).GetAttribute("aria-label").Should().Be("Task Manager — 1 finished task not yet viewed, 0 in progress");
        cut.Markup.Should().NotContain("cachet-ks-toast").And.NotContain("toast", "no toast — accessibility announcement only (§8.1)");
    }

    [Fact]
    public void Test_SC6_Badge_PluralFinishedLabel()
    {
        // Coverage of the plural aria-label wording (§8.1 example: "2 finished tasks not yet viewed, 1 in progress").
        _handler.Fallback = Summary(1, 2);

        var cut = RenderComponent<TaskManagerBadge>();
        cut.WaitForState(() => CountText(cut) == "3", TimeSpan.FromSeconds(1));

        Button(cut).GetAttribute("aria-label").Should().Be("Task Manager — 2 finished tasks not yet viewed, 1 in progress");
        LiveRegionText(cut).Should().BeEmpty("a first observation is not an increase between two polls");
    }

    // -----------------------------------------------------------------------------------------
    // V9 (badge variant)
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)] // 401 on the immediate on-init call
    [InlineData(2)] // 401 on the first tick
    public async Task Test_V9_PollReceives401_StopsPollingAndShowsSessionExpired(int unauthorizedOnCall)
    {
        // Arrange
        if (unauthorizedOnCall == 1)
        {
            _handler.Script(Status(HttpStatusCode.Unauthorized));
        }
        else
        {
            _handler.Script(Summary(1, 2), Status(HttpStatusCode.Unauthorized));
        }

        _handler.Fallback = Summary(4, 4);

        // Act
        var cut = RenderComponent<TaskManagerBadge>();
        if (unauthorizedOnCall == 2)
        {
            cut.WaitForState(() => CountText(cut) == "3", TimeSpan.FromSeconds(1));
        }

        cut.WaitForState(() => _handler.CallCount >= unauthorizedOnCall && CountText(cut) is null, TickWait);
        var callsAfter401 = _handler.CallCount;
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Assert — the badge hides its number rather than showing a stale one, and stops polling.
        _handler.CallCount.Should().Be(callsAfter401, "a 401 stops the badge's poll loop (V9)");
        CountText(cut).Should().BeNull();
        Button(cut).GetAttribute("aria-label").Should().Be("Task Manager");
        Button(cut).GetAttribute("class").Should().NotContain("cachet-task-badge-attention");
        _loggerProvider.Messages.Should().Contain(m =>
            m.Contains("TaskManagerBadge: poll stopped on 401 from GET /tasks/summary", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------
    // V10 (badge variant)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V10_TransientPollFailure_KeepsLastStateAndRetries()
    {
        // Arrange — a good first read, then every kind of transient failure, then recovery.
        _handler.Script(
            Summary(1, 2),
            NetworkFailure(),
            Status(HttpStatusCode.InternalServerError),
            Status(HttpStatusCode.BadGateway),
            RawBody("{not-json"),
            RawBody("null"));
        _handler.Fallback = Summary(0, 1);

        // Act
        var cut = RenderComponent<TaskManagerBadge>();
        cut.WaitForState(() => CountText(cut) == "3", TimeSpan.FromSeconds(1));

        // Assert — through every failed tick the last rendered state is kept and no error is shown.
        cut.WaitForState(() => _handler.CallCount >= 6, TimeSpan.FromSeconds(10));
        CountText(cut).Should().Be("3", "a transient failure keeps the last rendered state (V10)");
        cut.Markup.Should().NotContainAny("error", "Error", "Something went wrong", "session has expired");

        // Assert — the loop kept retrying and recovered on the next good tick.
        cut.WaitForState(() => CountText(cut) == "1", TickWait);
        _loggerProvider.Messages.Count(m =>
                m.Contains("TaskManagerBadge: transient poll failure on GET /tasks/summary", StringComparison.Ordinal))
            .Should().BeGreaterThanOrEqualTo(4, "network error, 500, 502 and an unreadable body are each logged at Warning");
    }

    [Fact]
    public void Test_V10_SuccessfulTickLogsNothing()
    {
        // §11: the badge logs nothing on a successful tick (no per-tick noise).
        _handler.Fallback = Summary(1, 0);

        var cut = RenderComponent<TaskManagerBadge>();
        cut.WaitForState(() => _handler.CallCount >= 2, TickWait);

        _loggerProvider.Messages.Should().NotContain(m => m.Contains("TaskManagerBadge", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------
    // CAS-027 SC-7 / V-4 — ported from MainLayoutTests. CAS-027 §8.7 put the header badge in
    // MainLayout.razor; the badge now lives in this component (CAS-030 §8.1), so its CAS-027
    // spec-mapped tests run against it here, names unchanged.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC7_BadgeReflectsSummaryCounts()
    {
        _handler.Fallback = Summary(2, 3);

        var cut = RenderComponent<TaskManagerBadge>();
        cut.WaitForState(() => CountText(cut) is not null, TimeSpan.FromSeconds(3));

        CountText(cut).Should().Be("5", "the badge must show inProgressCount + unviewedCompletedCount (CAS-027 §8.7)");

        // A fresh component instance whose summary sums to zero must show no count at all.
        _handler.Fallback = Summary(0, 0);
        var callsBefore = _handler.CallCount;
        var zeroCut = RenderComponent<TaskManagerBadge>();
        zeroCut.WaitForState(() => _handler.CallCount > callsBefore, TimeSpan.FromSeconds(3));

        CountText(zeroCut).Should().BeNull("no badge/count shown at all when the sum is zero (CAS-027 §8.7)");
    }

    [Fact]
    public void Test_V4_BadgeCountMatchesSummaryResponse()
    {
        // First fetch (immediate, on init): 1 in progress, 0 unviewed; a later poll tick changes the
        // counts, and the badge must refresh to reflect it (CAS-027 §7 V4).
        _handler.Script(Summary(1, 0));
        _handler.Fallback = Summary(4, 2);

        var cut = RenderComponent<TaskManagerBadge>();
        cut.WaitForState(() => CountText(cut) == "1", TimeSpan.FromSeconds(3));

        cut.WaitForState(() => CountText(cut) == "6", TickWait);

        _handler.CallCount.Should().BeGreaterThan(
            1, "the badge must be refreshed at least once per configured poll interval, not fetched only once (CAS-027 §7 V4)");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-14 — the badge hosts the modal; a task is acknowledged only when the user opens it.
    // -----------------------------------------------------------------------------------------

    private static TaskListItem ListItem(int taskId, string type, string status, string submittedBy, string? searchMode = null) => new()
    {
        TaskId = taskId,
        Type = type,
        SubmittedBy = submittedBy,
        SubmittedAt = DateTimeOffset.UtcNow,
        StartTime = DateTimeOffset.UtcNow,
        Status = status,
        IsAcknowledged = false,
        SearchMode = searchMode,
    };

    private void ServeTasks(params TaskListItem[] items) =>
        _handler.TasksResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PagedResult<TaskListItem> { Items = items, Page = 1, PageSize = 100, TotalCount = items.Length }),
        };

    private static AngleSharp.Dom.IElement TaskRow(IRenderedComponent<TaskManagerBadge> cut, string marker) =>
        cut.FindAll(".cachet-tm-table tbody tr").First(r => r.TextContent.Contains(marker, StringComparison.Ordinal));

    /// <summary>
    /// CAS-048 §2.3: the CAS-027 SC-8 test, retargeted from <c>MainLayout</c> (no longer a host) to the badge
    /// that now hosts the modal. The old rule "opening the modal clears the unviewed count" is replaced:
    /// opening the modal clears nothing; opening a task acknowledges only that task, so only the
    /// unviewed-finished component of the badge drops and the in-progress component is unaffected.
    /// </summary>
    [Fact]
    public void Test_SC8_OpeningModalClearsUnviewedCountOnly()
    {
        // A long interval, so a count change can only come from the refresh after an acknowledge, not a tick.
        _pollingOptions.TaskManagerPollIntervalSeconds = 60;
        _handler.Fallback = () => Summary(2, 3 - _handler.AckedTaskIds.Count)();
        ServeTasks(
            ListItem(11, "KeywordSearch", "Completed", "done-a"),
            ListItem(12, "KeywordSearch", "Completed", "done-b"),
            ListItem(13, "ContentScan", "Completed", "done-c"),
            ListItem(14, "KeywordSearch", "Running", "run-a"),
            ListItem(15, "ContentScan", "Queued", "run-b"));

        var cut = RenderComponent<TaskManagerBadge>();
        cut.WaitForState(() => CountText(cut) == "5", TimeSpan.FromSeconds(3));

        Button(cut).Click();
        cut.WaitForState(() => cut.FindAll(".cachet-tm-table tbody tr").Count == 5, TimeSpan.FromSeconds(3));

        // Opening the modal does not clear the badge and acknowledges nothing.
        CountText(cut).Should().Be("5", "opening the modal no longer clears the unviewed count (CAS-048 §2.3)");
        _handler.AckedTaskIds.Should().BeEmpty();

        // Opening one task acknowledges only that task; only the unviewed component of the badge drops.
        TaskRow(cut, "done-b").Click();
        cut.WaitForState(() => CountText(cut) == "4", TimeSpan.FromSeconds(3));

        _handler.AckedTaskIds.Should().Equal(12);
        Button(cut).GetAttribute("aria-label").Should().Be(
            "Task Manager — 2 finished tasks not yet viewed, 2 in progress",
            "inProgressCount (2) is unaffected; only the unviewed-finished count dropped by one");
    }

    [Fact]
    public void Test_SC14_BadgeHostsModalAckOnOpen()
    {
        _pollingOptions.TaskManagerPollIntervalSeconds = 60;
        _handler.Fallback = () => Summary(1, 2 - _handler.AckedTaskIds.Count)();
        ServeTasks(
            ListItem(21, "KeywordSearch", "Completed", "done-filename", searchMode: "Filename"),
            ListItem(22, "KeywordSearch", "Failed", "failed-row"),
            ListItem(23, "ContentScan", "Running", "running-row"));

        var cut = RenderComponent<TaskManagerBadge>();
        cut.WaitForState(() => CountText(cut) == "3", TimeSpan.FromSeconds(3));

        // The badge hosts exactly one modal itself, and its button is enabled.
        cut.FindComponents<Web.Components.Shared.TaskManagerModal>().Should().ContainSingle();
        Button(cut).HasAttribute("disabled").Should().BeFalse();
        Button(cut).HasAttribute("title").Should().BeFalse();
        cut.FindAll(".cachet-tm-modal").Should().BeEmpty("the modal is closed until the button is clicked");

        // Opening the modal does not clear the badge.
        Button(cut).Click();
        cut.WaitForState(() => cut.FindAll(".cachet-tm-table tbody tr").Count == 3, TimeSpan.FromSeconds(3));
        CountText(cut).Should().Be("3");
        _handler.AckedTaskIds.Should().BeEmpty();

        // Opening a task acknowledges that task only; the badge refreshes straight away.
        var navigationManager = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        TaskRow(cut, "done-filename").Click();
        cut.WaitForState(() => CountText(cut) == "2", TimeSpan.FromSeconds(3));
        _handler.AckedTaskIds.Should().Equal(21);
        navigationManager.Uri.Should().Be("http://localhost/?taskId=21");
        cut.FindAll(".cachet-tm-modal").Should().BeEmpty("opening a task closes the modal");

        // View Details on the Failed task counts as opening it (the detail call itself is not under test).
        Button(cut).Click();
        cut.WaitForState(() => cut.FindAll(".cachet-tm-table tbody tr").Count == 3, TimeSpan.FromSeconds(3));
        TaskRow(cut, "failed-row").QuerySelectorAll("button").First(b => b.TextContent.Trim() == "View Details").Click();
        cut.WaitForState(() => CountText(cut) == "1", TimeSpan.FromSeconds(3));
        _handler.AckedTaskIds.Should().Equal(21, 22);

        // The Running task is inert: it can neither be opened nor acknowledged.
        var click = () => TaskRow(cut, "running-row").Click();
        click.Should().Throw<MissingEventHandlerException>();
        _handler.AckedTaskIds.Should().Equal(21, 22);
        _handler.OtherRequests.Should().NotContain(r => r.Contains("mark-viewed"));
    }

    // -----------------------------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------------------------

    /// <summary>Answers each request from <see cref="Script"/> in order, then from <see cref="Fallback"/>.</summary>
    private sealed class ScriptedSummaryHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpResponseMessage>> _script = new();
        private int _callCount;

        public Func<HttpResponseMessage> Fallback { get; set; } =
            () => new HttpResponseMessage(HttpStatusCode.BadGateway);

        public int CallCount => Volatile.Read(ref _callCount);

        public ConcurrentQueue<string> RequestPaths { get; } = new();

        public void Script(params Func<HttpResponseMessage>[] responses)
        {
            foreach (var response in responses)
            {
                _script.Enqueue(response);
            }
        }

        /// <summary>CAS-048: the tasks the modal lists (<c>GET /tasks</c>).</summary>
        public Func<HttpResponseMessage> TasksResponse { get; set; } =
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PagedResult<TaskListItem> { Items = Array.Empty<TaskListItem>(), Page = 1, PageSize = 100, TotalCount = 0 }) };

        /// <summary>CAS-048: task ids acknowledged through <c>POST /tasks/{id}/acknowledge</c>.</summary>
        public ConcurrentQueue<int> AckedTaskIds { get; } = new();

        public ConcurrentQueue<string> OtherRequests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestPath = request.RequestUri!.AbsolutePath;
            if (!requestPath.EndsWith("/tasks/summary", StringComparison.Ordinal))
            {
                OtherRequests.Enqueue($"{request.Method} {requestPath}");
                if (request.Method == HttpMethod.Get && requestPath.EndsWith("/tasks", StringComparison.Ordinal))
                {
                    return Task.FromResult(TasksResponse());
                }

                if (request.Method == HttpMethod.Post && requestPath.EndsWith("/acknowledge", StringComparison.Ordinal))
                {
                    var segments = requestPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    AckedTaskIds.Enqueue(int.Parse(segments[^2], System.Globalization.CultureInfo.InvariantCulture));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            Interlocked.Increment(ref _callCount);
            RequestPaths.Enqueue(request.RequestUri!.AbsolutePath);
            var next = _script.TryDequeue(out var scripted) ? scripted : Fallback;
            return Task.FromResult(next());
        }
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
