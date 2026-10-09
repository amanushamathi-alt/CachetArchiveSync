using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Web.Tests.KeywordSearch;

/// <summary>
/// CAS-022 §13 — proves every Success Criterion (SC-1..SC-10) and Validation (V1..V5) for
/// <c>KeywordSearch.razor</c> via bUnit, with the "CachetApi" named <see cref="HttpClient"/>
/// redirected to <see cref="FakeKeywordSearchApiHandler"/> so no real Api process/DB is ever
/// reached.
///
/// <para>
/// <b>Polling and real time:</b> <c>KeywordSearch.razor</c>'s poll loop uses a genuine
/// <see cref="PeriodicTimer"/> on a fixed 5-second interval (CAS-022 §8.2 point 3 — a code
/// constant, not a configuration key, and this test-writer's brief explicitly says not to add a
/// poll-interval knob just to ease testing). Tests that need to observe the loop's first tick
/// (SC-2, SC-3, SC-4, SC-5, SC-6, SC-7, SC-9, V4) use <c>WaitForState</c> with a timeout above
/// 5 seconds and therefore take slightly over 5 real seconds to run — a deliberate trade-off per
/// the task brief ("use your judgment on the cleanest testable seam") rather than restructuring
/// the implementer's file.
/// </para>
/// </summary>
public sealed class KeywordSearchPageTests : TestContext
{
    private const string ConfiguredBucket = "cachet-archive-prod";
    private static readonly TimeSpan PollWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly FakeKeywordSearchApiHandler _apiHandler = new();
    private readonly TaskPollingOptions _pollingOptions = new() { TaskManagerPollIntervalSeconds = 1 };
    private readonly CapturingLoggerProvider _loggerProvider = new();

    public KeywordSearchPageTests()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));

        // CAS-048: the results explorer reads GET search/{taskId}/browse and .../browse/result-ids.
        _apiHandler.OnGetSearchBrowse = FileExplorer.BrowseFixtures.ServeResults;
        _apiHandler.OnGetSearchResultIds = FileExplorer.BrowseFixtures.ServeResultIds;

        var httpClient = new HttpClient(_apiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        var httpClientFactory = new SingleClientHttpClientFactory(httpClient);
        Services.AddSingleton<IHttpClientFactory>(httpClientFactory);
        Services.AddTransient<Web.Http.FilesBrowseClient>(); // CAS-044: Files is the default mode
        Services.AddTransient<Web.Http.SearchResultsBrowseClient>(); // CAS-048: results explorer
        Services.AddTransient<Web.Http.QuarantineBrowseClient>(); // CAS-051: injected by FileExplorer

        Services.AddSingleton<IConfiguration>(BuildConfiguration(ConfiguredBucket));
        Services.AddLogging(builder => builder.AddProvider(_loggerProvider));

        // CAS-030 §8.4: the poll interval is now bound from Worker:TaskManagerPollIntervalSeconds via
        // IOptions<TaskPollingOptions> (default 10 s). Tests use a 1 s interval so the CAS-022 waits
        // sized for the former 5 s constant keep ample margin; V8's 5-60 range is enforced only by
        // Program.cs's ValidateOnStart (proved in TaskPollingOptionsTests), not by this POCO.
        Services.AddSingleton<IOptions<TaskPollingOptions>>(Options.Create(_pollingOptions));

        // CAS-024: KeywordSearch.razor now embeds QuarantineQueueModal, whose Delete Permanently
        // gating reads the CachetFullAccess ("b2_capability") claim from a cascading
        // Task<AuthenticationState> — same bUnit.TestDoubles setup ODriveSyncPageTests (CAS-023)
        // already established for that identical cascading parameter. Full Access by default so
        // SC-9's delete flow is reachable; this page's own tests never depend on a Read Only session
        // (that gating is proven directly in QuarantineQueueModalTests, the modal's own test class).
        _authContext = this.AddTestAuthorization();
        _authContext.SetAuthorized("full-access-user");
        _authContext.SetClaims(new System.Security.Claims.Claim("b2_capability", "Full Access"));
    }

    private readonly TestAuthorizationContext _authContext;

    private static IConfiguration BuildConfiguration(string? bucketName) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["B2:BucketName"] = bucketName,
            })
            .Build();

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };

    private IRenderedComponent<Web.Components.Pages.KeywordSearch> RenderPage(bool selectKeywordMode = true)
    {
        var cut = RenderComponent<Web.Components.Pages.KeywordSearch>();
        // OnInitializedAsync's LoadQuarantineAsync must have resolved (one of its three terminal
        // render states) before a test starts interacting with the page.
        if (selectKeywordMode)
        {
            // CAS-044: Files is the default mode; the pre-existing tests exercise Keyword Search.
            cut.Find("#ks-mode-keyword").Change("keyword");
        }

        // The Quarantine panel now renders only alongside displayed results, so wait on the load itself
        // (GET /quarantine answered) rather than on panel text.
        cut.WaitForState(
            () => AnyRequest(HttpMethod.Get, "/quarantine")
                || cut.Markup.Contains("Quarantine is empty")
                || cut.Markup.Contains("Quarantined On")
                || cut.Markup.Contains("Something went wrong"),
            TimeSpan.FromSeconds(15));
        return cut;
    }

    // The Quarantine panel renders only for Full Access users and only once results are displayed, so tests
    // that exercise the panel first run (and complete) a search to get the results explorer on screen.
    private IRenderedComponent<Web.Components.Pages.KeywordSearch> RenderPageWithResults()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(taskId: 1, total: 3)));

        SubmitSearch(cut, "quarterly-report");
        // Rows rather than ExplorerHasRows: a Read Only user has no Send to Quarantine button.
        cut.WaitForState(() => cut.FindAll("#fe-root tr.fe-row").Count > 0, PollWaitTimeout);
        return cut;
    }

    private static IElement SearchInput(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.Find("#ks-term");

    private static IElement SearchButton(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.Find(".cachet-ks-search-btn");

    private static void SubmitSearch(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string term)
    {
        SearchInput(cut).Input(term);
        SearchButton(cut).Click();
    }

    private static SearchSubmittedResponse SubmittedResponse(int taskId = 1) => new()
    {
        TaskId = taskId,
        Type = "KeywordSearch",
        Status = "Queued",
        SubmittedAt = DateTimeOffset.UtcNow,
    };

    private static SearchResultsResponse CompletedResponse(
        IReadOnlyList<FilenameMatchItem>? filenameItems = null,
        IReadOnlyList<ContentMatchItem>? contentItems = null) => new()
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
                Items = contentItems ?? Array.Empty<ContentMatchItem>(),
                Page = 1,
                PageSize = 50,
                TotalCount = contentItems?.Count ?? 0,
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

    private static ContentMatchItem SampleContentItem(int fileId = 21) => new()
    {
        FileId = fileId,
        FileIndexId = fileId + 500_000,
        Name = "report.docx",
        Path = "/o-drive/2026/report.docx",
        Snippet = "...matching snippet...",
        ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
        Status = "Needs Review",
    };

    // -----------------------------------------------------------------------------------------
    // SC-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC1_SearchSubmissionShowsInProgressBannerAndDisablesButton()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        SubmitSearch(cut, "quarterly-report");

        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));

        cut.Markup.Should().Contain("Search in progress — running for");
        cut.Markup.Should().NotContain("Search submitted", "the in-progress banner is the single status message");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue(
            "the Search button must be disabled while its own task is outstanding (§8.1)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC2_PollingContinuesWhileTaskIncomplete()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        SubmitSearch(cut, "quarterly-report");
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));

        // A 409 response never calls StateHasChanged (the poll loop just `continue`s), so there is
        // no render to hook bUnit's WaitForState off of here — poll the handler's own call counter
        // on the wall clock instead of relying on a render event.
        await WaitUntilAsync(() => _apiHandler.ResultsCallCount >= 1, PollWaitTimeout);

        cut.Markup.Should().Contain("Search in progress", "a 409 must keep the in-progress banner, not stop polling");
        cut.FindAll("#fe-root").Should().BeEmpty("no results explorer until the task completes");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    // -----------------------------------------------------------------------------------------
    // SC-3
    // -----------------------------------------------------------------------------------------

    // CAS-048 §5a: rewritten — the completed task now populates the results explorer in place (same intent).
    [Fact]
    public void Test_SC3_CompletedTaskAutoPopulatesPanelsInPlace()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(taskId: 1, total: 3)));

        SubmitSearch(cut, "quarterly-report");
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));

        cut.WaitForState(() => ExplorerHasRows(cut), PollWaitTimeout);

        ExplorerRowNames(cut).Should().Equal("Alpha", "Beta");
        cut.Find("#fe-root .fe-status-count").TextContent.Should().Be("2 items in this folder · 3 matches in total");
        cut.Markup.Should().NotContain("Search in progress", "the in-progress banner must be removed once results are in");
    }

    // -----------------------------------------------------------------------------------------
    // SC-4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC4_FailedTaskShowsFailureBanner()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new SearchResultsResponse
        {
            TaskId = 1,
            Status = "Failed",
            FailureReason = "worker crashed",
        }));

        SubmitSearch(cut, "quarterly-report");
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));

        cut.WaitForState(() => cut.Markup.Contains("This search did not complete successfully."), PollWaitTimeout);

        cut.Markup.Should().Contain("This search did not complete successfully.");
        cut.Markup.Should().NotContain("Search in progress");
        cut.FindAll("#fe-root").Should().BeEmpty("no stale/partial results on a failed task");
    }

    // -----------------------------------------------------------------------------------------
    // SC-5 / V-4 (same rule, two required test names)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC5_SendToQuarantineButtonReflectsSelectionCount()
    {
        var cut = CompleteASearch();
        OpenResultFolder(cut, "Alpha");

        var button = SendButton(cut);
        button.HasAttribute("disabled").Should().BeTrue("0 files checked must leave the button disabled (V4)");
        button.TextContent.Should().Contain("Send to Quarantine (0)");

        CheckResultFile(cut, "a1.pdf");

        button = SendButton(cut);
        button.HasAttribute("disabled").Should().BeFalse("≥1 file checked must enable the button");
        button.TextContent.Should().Contain("Send to Quarantine (1)");
    }

    [Fact]
    public void Test_V4_SendToQuarantineDisabledUntilRowChecked()
    {
        var cut = CompleteASearch();
        OpenResultFolder(cut, "Alpha");

        SendButton(cut).HasAttribute("disabled").Should().BeTrue();

        CheckResultFile(cut, "a1.pdf");

        SendButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC6_SendToQuarantineCallsApiAndRefreshesQuarantinePanel()
    {
        var cut = CompleteASearch();
        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");

        QuarantineAddRequest? capturedRequest = null;
        _apiHandler.OnPostQuarantineAdd = async request =>
        {
            capturedRequest = await request.Content!.ReadFromJsonAsync<QuarantineAddRequest>();
            return JsonResponse(HttpStatusCode.OK, new QuarantineAddResponse
            {
                Items = new[]
                {
                    new QuarantineAddResponseItem
                    {
                        FileId = 900,
                        Name = "invoice.pdf",
                        Path = "/o-drive/2026/invoice.pdf",
                        QuarantinedOn = DateTimeOffset.UtcNow,
                        Status = "Flagged",
                    },
                },
            });
        };
        // This delegate replaces the constructor's default (which already served the *initial*
        // page-load GET /quarantine, returning an empty list). Its own first invocation is
        // therefore the post-send refresh (§8.4) — it must always return the newly-added row.
        var refreshCalls = 0;
        _apiHandler.OnGetQuarantine = _ =>
        {
            refreshCalls++;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
            {
                TotalCount = 0,
                Page = 1,
                PageSize = 25,
                Items = new[]
                {
                    new QuarantineListItem
                    { SourceTab = "Keyword Search",
                        FileId = 900,
                        FileIndexId = 500900,
                        Name = "invoice.pdf",
                        Path = "/o-drive/2026/invoice.pdf",
                        QuarantinedOn = DateTimeOffset.UtcNow,
                        Status = "Flagged",
                    },
                },
            }));
        };

        SendButton(cut).Click();

        cut.WaitForState(() => cut.Markup.Contains("Quarantined On"), TimeSpan.FromSeconds(3));

        capturedRequest.Should().NotBeNull();
        capturedRequest!.TaskId.Should().Be(1);
        capturedRequest.FileIds.Should().BeEquivalentTo(new[] { 9001 }, "the searchResultId from browse is what is sent");
        refreshCalls.Should().Be(1, "the panel must be refreshed via a fresh GET /quarantine after a successful send (§8.4)");
        cut.Markup.Should().Contain("invoice.pdf");
        cut.Markup.Should().Contain("Quarantined On");
        SendButton(cut).TextContent.Should().Contain("Send to Quarantine (0)", "the selection is cleared after a successful send");
    }

    // -----------------------------------------------------------------------------------------
    // SC-7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC7_PartialQuarantineFailureSurfacedWithoutLosingSuccesses()
    {
        var cut = CompleteASearch();
        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");

        _apiHandler.OnPostQuarantineAdd = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineAddResponse
        {
            Items = new[]
            {
                new QuarantineAddResponseItem
                {
                    FileId = 901,
                    Name = "invoice.pdf",
                    Path = "/o-drive/2026/invoice.pdf",
                    QuarantinedOn = DateTimeOffset.UtcNow,
                    Status = "Flagged",
                },
            },
            Warnings = new[] { "other-file.pdf was already quarantined and was skipped." },
        }));

        SendButton(cut).Click();

        cut.WaitForState(() => cut.Markup.Contains("cachet-ks-toast"), TimeSpan.FromSeconds(3));

        cut.Markup.Should().Contain("1 file(s) sent to Quarantine.", "the file(s) that did succeed must still be reported");
        cut.Markup.Should().Contain("other-file.pdf was already quarantined and was skipped.");
    }

    // -----------------------------------------------------------------------------------------
    // SC-7 (CAS-025 §3/§8.5 — status chip colors)
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// CAS-025 §10 SC-7 — the three status chip types (Flagged/Needs Review/Informational) render
    /// with distinct, correctly-mapped CSS classes. CAS-048 §5a: rewritten against the Quarantine panel,
    /// the only place those chips still render now that the Filename/Content Matches panels are gone.
    /// bUnit has no CSS engine, so "correct distinct colors" is the class-name contract
    /// <c>ChipClass(status)</c> guarantees.
    /// </summary>
    [Fact]
    public void Test_SC7_StatusChipsRenderCorrectColors()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[]
            {
                SampleQuarantineItem(1, "flagged.pdf", "Flagged"),
                SampleQuarantineItem(2, "needs-review.pdf", "Needs Review"),
                SampleQuarantineItem(3, "info.pdf", "Informational"),
            },
        }));

        var cut = RenderPageWithResults();

        cut.Find("span.cachet-ks-chip-flagged").TextContent.Should().Be("Flagged");
        cut.Find("span.cachet-ks-chip-needs-review").TextContent.Should().Be("Needs Review");
        cut.Find("span.cachet-ks-chip-informational").TextContent.Should().Be("Informational");

        new[] { "cachet-ks-chip-flagged", "cachet-ks-chip-needs-review", "cachet-ks-chip-informational" }
            .Should().OnlyHaveUniqueItems();
    }

    // -----------------------------------------------------------------------------------------
    // SC-8 / V-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC8_ExtensionDropdownMatchesFR04RequiredList()
    {
        var cut = RenderPage();

        var options = cut.Find("#ks-extension").QuerySelectorAll("option").Select(o => o.TextContent).ToArray();

        options.Should().Equal("All", "PDF", "DOC", "DOCX", "XLS", "XLSX", "TXT");
    }

    [Fact]
    public void Test_V3_ExtensionDropdownOffersExactlyTheAllowedSet()
    {
        var cut = RenderPage();

        var values = cut.Find("#ks-extension").QuerySelectorAll("option").Select(o => o.GetAttribute("value")).ToArray();

        values.Should().BeEquivalentTo(new[] { "All", "PDF", "DOC", "DOCX", "XLS", "XLSX", "TXT" });
    }

    // -----------------------------------------------------------------------------------------
    // SC-9
    // -----------------------------------------------------------------------------------------

    // CAS-048 §5a: rewritten — the empty result is the explorer's empty root ("No files matched." /
    // "No files in that date range."), not the two panels' per-panel copy.
    [Fact]
    public void Test_SC9_ZeroMatchCompletionShowsExplicitEmptyStateCopy()
    {
        var cut = RenderPage();
        _apiHandler.OnGetSearchBrowse = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, FileExplorer.BrowseFixtures.Listing(string.Empty)));
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse(1)));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(1, 0)));

        SubmitSearch(cut, "no-such-term");
        cut.WaitForState(() => cut.FindAll("#fe-root .fe-empty").Count > 0, PollWaitTimeout);

        cut.Find("#fe-root .fe-empty").TextContent.Should().Be("No files matched.");
        cut.Markup.Should().NotContain("<table", "a zero-match result renders explanatory copy, not an empty table");

        // Date mode uses its own copy.
        cut.WaitForState(() => !cut.Markup.Contains("Search in progress"), PollWaitTimeout);
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse(2)));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(2, 0)));
        SelectDateMode(cut);
        EnterDates(cut, "01/01/2025", "01/31/2025");
        SearchButton(cut).Click();
        cut.WaitForState(() => cut.FindAll("#fe-root .fe-empty").Count > 0 && cut.Find("#fe-root .fe-empty").TextContent == "No files in that date range.", PollWaitTimeout);
    }

    // -----------------------------------------------------------------------------------------
    // SC-10
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC10_NoCredentialOrContentLeakageInUiSurfaceOrLogs()
    {
        const string SensitiveB2Key = "raw-b2-appkey-marker-73f1-never-render";
        const string SensitiveSessionCookie = "session-cookie-marker-88ab-never-render";
        const string SensitiveSearchTerm = "top-secret-search-term-9f2c";

        // Even though KeywordSearch.razor never reads a key from configuration, prove the
        // property holds structurally: nothing resembling a B2 key/session value ever reaches
        // rendered markup, regardless of what else is present in the DI container.
        Services.AddSingleton<IConfiguration>(_ => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["B2:BucketName"] = ConfiguredBucket,
                ["B2:ApplicationKey"] = SensitiveB2Key,
            })
            .Build());

        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, CompletedResponse(new[] { SampleFilenameItem() })));

        SubmitSearch(cut, SensitiveSearchTerm);
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));
        cut.WaitForState(() => ExplorerHasRows(cut), PollWaitTimeout);

        cut.Markup.Should().NotContain(SensitiveB2Key);
        cut.Markup.Should().NotContain(SensitiveSessionCookie);

        var logText = string.Join("\n", _loggerProvider.Messages);
        logText.Should().NotContain(SensitiveB2Key);
        logText.Should().NotContain(SensitiveSessionCookie);
        logText.Should().NotContain(SensitiveSearchTerm, "§11 forbids logging the user-entered search term verbatim");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-024 SC-1 / SC-9 (QuarantineQueueModal trigger button + embedded-panel refresh)
    // -----------------------------------------------------------------------------------------

    private static QuarantineListItem SampleQuarantineItem(int fileId, string? name = null, string status = "Flagged") => new()
    {
        SourceTab = "Keyword Search",
        FileId = fileId,
        FileIndexId = fileId + 500_000,
        Name = name ?? $"quarantined-file-{fileId}.pdf",
        Path = $"/o-drive/quarantined-file-{fileId}.pdf",
        QuarantinedOn = DateTimeOffset.UtcNow.AddDays(-1),
        Status = status,
    };

    /// <summary>CAS-024 SC-1 — the "Quarantine Queue" trigger button on this host page opens the
    /// shared modal. The O-Drive Sync host's identically-named test is
    /// <c>ODriveSyncPageTests.Test_SC1_TriggerButtonOnBothPagesOpensModal</c>.</summary>
    [Fact]
    public void Test_SC1_TriggerButtonOnBothPagesOpensModal()
    {
        var cut = RenderPage();

        cut.Find(".cachet-ks-quarantine-trigger-btn").Click();

        cut.WaitForState(() => cut.Markup.Contains("cachet-qq-modal"), TimeSpan.FromSeconds(3));
        cut.Markup.Should().Contain("cachet-qq-modal", "clicking the trigger button must open QuarantineQueueModal");
    }

    /// <summary>CAS-024 SC-9 — a successful delete initiated from the modal, while opened on this
    /// page, also refreshes this page's own embedded read-only Quarantine panel via the
    /// <c>OnDeleteCompleted="LoadQuarantineAsync"</c> binding — not merely the modal's own list.</summary>
    [Fact]
    public void Test_SC9_SuccessfulDeleteRefreshesEmbeddedQuarantinePanelOnKeywordSearch()
    {
        var cut = RenderPage(); // initial LoadQuarantineAsync call (constructor's default: empty list)

        // Populate both the modal's own load and the page's own embedded panel with one item before
        // the delete is attempted.
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleQuarantineItem(900, name: "to-be-deleted.pdf") },
        }));

        cut.Find(".cachet-ks-quarantine-trigger-btn").Click();
        cut.WaitForState(() => cut.Markup.Contains("to-be-deleted.pdf"), TimeSpan.FromSeconds(3));

        var row = cut.FindAll(".cachet-qq-table tbody tr").First(r => r.TextContent.Contains("to-be-deleted.pdf"));
        row.QuerySelector("input[type=checkbox]")!.Change(true);

        var quarantineGetCallsBeforeDelete = _apiHandler.Requests.Count(
            r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase));

        _apiHandler.OnPostQuarantineDelete = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineDeleteResponse
        {
            Results = new[] { new QuarantineDeleteResultItem { FileId = 900, Result = "Success" } },
        }));
        // Once deleted, both the modal's own re-fetch and the page's embedded-panel refresh must see
        // an empty queue.
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));

        cut.FindAll(".cachet-qq-footer button").First(b => b.TextContent.Contains("Delete Permanently")).Click();
        cut.FindAll(".cachet-qq-confirm-modal button").First(b => b.TextContent.Contains("Yes, delete permanently")).Click();

        cut.WaitForState(
            () => cut.Markup.Contains("Quarantine is empty. Files sent to quarantine from Keyword Search"),
            TimeSpan.FromSeconds(3));

        var quarantineGetCallsAfterDelete = _apiHandler.Requests.Count(
            r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase));
        quarantineGetCallsAfterDelete.Should().BeGreaterThan(
            quarantineGetCallsBeforeDelete + 1,
            "both the modal's own re-fetch AND the page's OnDeleteCompleted=LoadQuarantineAsync refresh must call GET /quarantine (SC-9), not just one of them");
        cut.Markup.Should().NotContain("to-be-deleted.pdf", "the page's own embedded Quarantine panel must reflect the delete, not just the modal's list");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-052 — Quarantine panel pagination, source tab, total count, reload on modal close
    // -----------------------------------------------------------------------------------------

    private List<QuarantineListItem> ServeQuarantineStore(List<QuarantineListItem> store)
    {
        _apiHandler.OnGetQuarantine = Quarantine.QuarantinePagingFixtures.Serve(store);
        return store;
    }

    private static string[] PanelRowNames(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.FindAll(".cachet-ks-quarantine-panel tbody tr button.cachet-file-link").Select(b => b.TextContent.Trim()).ToArray();

    private static string Panel(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string testId) =>
        cut.Find($"[data-testid=ks-quarantine-{testId}]").TextContent.Trim();

    private static string QqText(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string testId) =>
        cut.Find($"[data-testid=qq-{testId}]").TextContent.Trim();

    [Fact]
    public void Test_SC3_Panel_IndependentPaging()
    {
        ServeQuarantineStore(Quarantine.QuarantinePagingFixtures.Store(60));
        var cut = RenderPageWithResults();

        PanelRowNames(cut).Should().HaveCount(25);
        Panel(cut, "range").Should().Be("1–25 of 60");

        cut.Find("[data-testid=ks-quarantine-next]").Click();
        Panel(cut, "range").Should().Be("26–50 of 60");
        PanelRowNames(cut).First().Should().Be("quarantined-file-26.pdf");

        // The modal opens on its own page 1 while the panel stays on page 2.
        cut.Find(".cachet-ks-quarantine-trigger-btn").Click();
        cut.WaitForState(() => cut.FindAll("[data-testid=qq-range]").Count > 0, TimeSpan.FromSeconds(3));
        QqText(cut, "range").Should().Be("1–25 of 60");
        Panel(cut, "range").Should().Be("26–50 of 60");

        // Paging the modal does not move the panel.
        cut.Find("[data-testid=qq-next]").Click();
        QqText(cut, "range").Should().Be("26–50 of 60");
        cut.Find("[data-testid=qq-next]").Click();
        QqText(cut, "range").Should().Be("51–60 of 60");
        Panel(cut, "range").Should().Be("26–50 of 60");
    }

    [Fact]
    public void Test_SC4_FilesItems_Visible()
    {
        var store = new List<QuarantineListItem>
        {
            Quarantine.QuarantinePagingFixtures.Item(1, "Files", "Informational"),
            Quarantine.QuarantinePagingFixtures.Item(2, "Files", "Informational"),
        };
        ServeQuarantineStore(store);
        var cut = RenderPageWithResults();

        PanelRowNames(cut).Should().Equal("quarantined-file-1.pdf", "quarantined-file-2.pdf");
        cut.FindAll(".cachet-ks-quarantine-source-tab").Select(td => td.TextContent.Trim()).Should().AllBe("Files");

        // A file added from the Files browser later appears on the next modal open (and panel load).
        store.Add(Quarantine.QuarantinePagingFixtures.Item(3, "Files", "Informational"));
        cut.Find(".cachet-ks-quarantine-trigger-btn").Click();
        cut.WaitForState(() => cut.FindAll(".cachet-qq-table tbody tr").Count == 3, TimeSpan.FromSeconds(3));
        cut.FindAll("td.cachet-qq-source-tab").Select(td => td.TextContent.Trim()).Should().AllBe("Files");
        cut.FindAll(".cachet-qq-table tbody tr button.cachet-file-link").Select(b => b.TextContent.Trim())
            .Should().Contain("quarantined-file-3.pdf");
    }

    [Fact]
    public void Test_SC6_Badge_Total()
    {
        ServeQuarantineStore(Quarantine.QuarantinePagingFixtures.Store(60));
        var cut = RenderPageWithResults();

        PanelRowNames(cut).Should().HaveCount(25);
        cut.Find(".cachet-ks-quarantine-trigger-btn .cachet-btn-count").TextContent.Trim().Should().Be("60");
        cut.Find(".cachet-ks-quarantine-panel .cachet-panel-count").TextContent.Trim().Should().Be("60");
    }

    [Fact]
    public void Test_SC12_Panel_ReloadsOnModalClose()
    {
        var store = Quarantine.QuarantinePagingFixtures.Store(3);
        ServeQuarantineStore(store);
        _apiHandler.OnPostQuarantineDelete = Quarantine.QuarantinePagingFixtures.ServeDelete(store);
        var cut = RenderPageWithResults();
        PanelRowNames(cut).Should().HaveCount(3);

        cut.Find(".cachet-ks-quarantine-trigger-btn").Click();
        cut.WaitForState(() => cut.FindAll(".cachet-qq-table tbody tr").Count == 3, TimeSpan.FromSeconds(3));
        cut.FindAll(".cachet-qq-table tbody tr input[type=checkbox]")[0].Change(true);
        cut.FindAll(".cachet-qq-footer button").First(b => b.TextContent.Contains("Delete Permanently")).Click();
        cut.FindAll(".cachet-qq-confirm-modal button").First(b => b.TextContent.Contains("Yes, delete permanently")).Click();
        cut.WaitForState(() => cut.FindAll(".cachet-qq-table tbody tr").Count == 2, TimeSpan.FromSeconds(3));

        // Simulate a change that only a reload on close can surface (no delete callback involved).
        store.RemoveAt(0);
        var getsBeforeClose = _apiHandler.Requests.Count(
            r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase));

        cut.FindAll(".cachet-qq-footer button").First(b => b.TextContent.Contains("Close")).Click();
        cut.WaitForState(() => cut.FindAll(".cachet-qq-modal").Count == 0, TimeSpan.FromSeconds(3));

        cut.WaitForAssertion(() =>
        {
            _apiHandler.Requests.Count(r => r.Method == HttpMethod.Get
                && r.RequestUri!.AbsolutePath.EndsWith("/quarantine", StringComparison.OrdinalIgnoreCase))
                .Should().BeGreaterThan(getsBeforeClose, "closing the modal must reload the panel (CAS-052 §2.8)");
            PanelRowNames(cut).Should().Equal("quarantined-file-3.pdf");
            cut.Find(".cachet-ks-quarantine-panel .cachet-panel-count").TextContent.Trim().Should().Be("1");
        }, TimeSpan.FromSeconds(3));
    }

    // -----------------------------------------------------------------------------------------
    // V-1
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("valid-term", false)]
    public void Test_V1_SearchButtonDisabledUntilTermNonEmptyAndWithinLength(string term, bool expectDisabled)
    {
        var cut = RenderPage();

        SearchInput(cut).Input(term);

        SearchButton(cut).HasAttribute("disabled").Should().Be(expectDisabled);
    }

    [Fact]
    public void Test_V1_SearchButtonDisabledUntilTermNonEmptyAndWithinLength_OverLengthTermDisabled()
    {
        var cut = RenderPage();

        SearchInput(cut).Input(new string('x', 201));

        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("a term over 200 characters must keep the Search button disabled (V1)");
        cut.Markup.Should().Contain("Search term must be at most 200 characters.");
    }

    // -----------------------------------------------------------------------------------------
    // V-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V2_BucketDropdownOffersOnlyConfiguredBucket()
    {
        var cut = RenderPage();

        var bucketSelect = cut.Find("#ks-bucket");
        var options = bucketSelect.QuerySelectorAll("option");

        options.Length.Should().Be(1, "no free-text bucket entry exists in this UI at all (§7 V2)");
        options[0].TextContent.Should().Be(ConfiguredBucket);
        bucketSelect.HasAttribute("disabled").Should().BeTrue("the single configured bucket is read-only, per construction");
    }

    [Fact]
    public void Test_V2_BucketDropdownShowsNotConfiguredAndBlocksSearch_WhenBucketMissing()
    {
        Services.AddSingleton<IConfiguration>(BuildConfiguration(bucketName: null));

        var cut = RenderPage();

        cut.Find("#ks-bucket").TextContent.Should().Contain("(bucket not configured)");
        SearchInput(cut).Input("a-valid-term");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue(
            "the screen must fail closed and never submit with an empty/wrong bucket value (§9)");
    }

    // -----------------------------------------------------------------------------------------
    // V-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V5_ServerErrorResponsesRenderInlineErrorNotCrash()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));

        var act = () => SubmitSearch(cut, "quarterly-report");

        act.Should().NotThrow("a 422 from POST /search must be surfaced as a screen-level error, never an unhandled exception (V5)");
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
        cut.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    [Fact]
    public void Test_V5_QuarantineLoadFailureRendersInlineErrorNotCrash()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => RenderPage();

        act.Should().NotThrow();
        var cut = RenderPageWithResults(); // the inline error lives in the Quarantine panel, shown only with results
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
        cut.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    // -----------------------------------------------------------------------------------------
    // Coverage-gap closures (not spec-mapped SC/V IDs) — same precedent as CAS-021's test-writer
    // follow-up: closes branches §10/§7 don't individually name but §13's ≥85% line-coverage
    // target requires.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_PollingTransientNetworkErrorIsSwallowedAndPollingContinues()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        _apiHandler.OnGetResults = (_, callCount) => callCount == 1
            ? throw new HttpRequestException("simulated: transient network failure")
            : Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted()));

        SubmitSearch(cut, "quarterly-report");
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));

        cut.WaitForState(() => ExplorerHasRows(cut), TimeSpan.FromSeconds(16));

        ExplorerHasRows(cut).Should().BeTrue("a single transient network error must not abandon the poll loop (§8.5)");
    }

    /// <summary>Closes the coverage gap on <c>HandleSearchAsync</c>'s catch(HttpRequestException)
    /// branch when Api itself is unreachable for the initial submit.</summary>
    [Fact]
    public void Test_SearchSubmitApiUnreachableShowsGenericErrorNotCrash()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => throw new HttpRequestException("simulated: Api unreachable");

        var act = () => SubmitSearch(cut, "quarterly-report");

        act.Should().NotThrow();
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Test_SendToQuarantineApiFailureShowsGenericErrorNotCrash()
    {
        var cut = CompleteASearch();
        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");

        _apiHandler.OnPostQuarantineAdd = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => SendButton(cut).Click();

        act.Should().NotThrow();
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
    }

    /// <summary>Closes the coverage gap on <c>DisposeAsync</c> — the poll loop's
    /// <see cref="CancellationTokenSource"/>/<see cref="PeriodicTimer"/> must be torn down cleanly
    /// when the component is removed mid-poll, without throwing.</summary>
    [Fact]
    public async Task Test_DisposeDuringActivePollDoesNotThrow()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        SubmitSearch(cut, "quarterly-report");
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));

        var act = () => cut.Instance.DisposeAsync().AsTask();

        await act.Should().NotThrowAsync();
    }

    // CAS-048 §5a: rewritten for the results explorer (unchecking a file removes it from the selection).
    [Fact]
    public void Test_UncheckingFilenameRowRemovesItFromSelection()
    {
        var cut = CompleteASearch();
        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");
        SendButton(cut).TextContent.Should().Contain("(1)");

        CheckResultFile(cut, "a1.pdf", isChecked: false);

        var button = SendButton(cut);
        button.TextContent.Should().Contain("(0)");
        button.HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>Closes the coverage gap on <c>LoadQuarantineAsync</c>'s
    /// catch(HttpRequestException/TaskCanceledException) branch (lines 277-281) for the initial
    /// page-load call.</summary>
    [Fact]
    public void Test_QuarantineInitialLoadApiUnreachableShowsGenericErrorNotCrash()
    {
        _apiHandler.OnGetQuarantine = _ => throw new HttpRequestException("simulated: Api unreachable");

        IRenderedComponent<Web.Components.Pages.KeywordSearch>? cut = null;
        var act = () => cut = RenderPageWithResults();

        act.Should().NotThrow();
        cut!.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    /// <summary>Closes the coverage gap on <c>HandleSearchAsync</c>'s own guard clause (lines
    /// 297-300) — invoked directly since the Search button's <c>disabled</c> attribute already
    /// prevents a real browser from reaching this call, but the branch itself still needs
    /// coverage as defensive code (e.g. a stale/rapid double-submit).</summary>
    [Fact]
    public async Task Test_HandleSearchAsyncGuardClauseReturnsEarlyWhenInvalid()
    {
        var cut = RenderPage();

        var method = typeof(Web.Components.Pages.KeywordSearch)
            .GetMethod("HandleSearchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var act = async () => await (Task)method.Invoke(cut.Instance, null)!;

        await act.Should().NotThrowAsync("the empty default search term must short-circuit before any HTTP call");
        _apiHandler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/search"));
    }

    /// <summary>Closes the coverage gap on <c>HandleSearchAsync</c>'s "submitted is null" branch
    /// (lines 337-340) — a 202 response whose body doesn't deserialize to
    /// <see cref="SearchSubmittedResponse"/>.</summary>
    [Fact]
    public void Test_SearchSubmitNullResponseBodyShowsGenericErrorNotCrash()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
        });

        var act = () => SubmitSearch(cut, "quarterly-report");

        act.Should().NotThrow();
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
    }

    /// <summary>Closes the coverage gap on <c>PollTaskAsync</c>'s unexpected-non-409-status branch
    /// (lines 393-399) and its null-result branch (lines 403-405).</summary>
    [Fact]
    public void Test_PollingUnexpectedStatusShowsGenericErrorBanner()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        SubmitSearch(cut, "quarterly-report");
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));

        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), PollWaitTimeout);

        cut.Markup.Should().Contain("Something went wrong. Please try again.");
        cut.Markup.Should().NotContain("Search in progress");
    }

    [Fact]
    public void Test_PollingNullResponseBodyIsSwallowedAndPollingContinues()
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        _apiHandler.OnGetResults = (_, callCount) => callCount == 1
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            })
            : Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted()));

        SubmitSearch(cut, "quarterly-report");
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));

        cut.WaitForState(() => ExplorerHasRows(cut), TimeSpan.FromSeconds(16));

        ExplorerHasRows(cut).Should().BeTrue("a null poll response body must not abandon the poll loop");
    }

    [Fact]
    public async Task Test_SendToQuarantineAsyncGuardClauseReturnsEarlyWithNoSelection()
    {
        var cut = RenderPage();

        var method = typeof(Web.Components.Pages.KeywordSearch)
            .GetMethod("SendToQuarantineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var act = async () => await (Task)method.Invoke(cut.Instance, null)!;

        await act.Should().NotThrowAsync();
        _apiHandler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/quarantine") && r.Method == HttpMethod.Post);
    }

    [Fact]
    public void Test_SendToQuarantineApiUnreachableShowsGenericErrorNotCrash()
    {
        var cut = CompleteASearch();
        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");

        _apiHandler.OnPostQuarantineAdd = _ => throw new HttpRequestException("simulated: Api unreachable");

        var act = () => SendButton(cut).Click();

        act.Should().NotThrow();
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
    }

    /// <summary>Closes the coverage gap on <c>FormatRelativeTime</c>'s null/minutes/hours branches
    /// (lines 506-524) — the UI-driven tests above only ever exercise the "less than a minute"
    /// branch, since a freshly-submitted search's elapsed time is always sub-second.</summary>
    [Theory]
    [InlineData(null, "just now")]
    [InlineData(0, "less than a minute")]
    [InlineData(5, "5 minutes")]
    [InlineData(1, "1 minute")]
    public void Test_FormatRelativeTimeCoversAllBranches(int? minutesAgo, string expectedSubstring)
    {
        var method = typeof(Web.Components.Pages.KeywordSearch)
            .GetMethod("FormatRelativeTime", BindingFlags.NonPublic | BindingFlags.Static)!;

        DateTimeOffset? at = minutesAgo is null ? null : DateTimeOffset.UtcNow.AddMinutes(-minutesAgo.Value);
        var result = (string)method.Invoke(null, new object?[] { at })!;

        result.Should().Contain(expectedSubstring);
    }

    [Fact]
    public void Test_FormatRelativeTimeHoursBranch()
    {
        var method = typeof(Web.Components.Pages.KeywordSearch)
            .GetMethod("FormatRelativeTime", BindingFlags.NonPublic | BindingFlags.Static)!;

        var result = (string)method.Invoke(null, new object?[] { DateTimeOffset.UtcNow.AddHours(-3) })!;

        result.Should().Contain("3 hours");
    }

    // =========================================================================================
    // CAS-030 §8.2 (FR-21) — reload on open, ?taskId= deep link, acknowledgement, V7, V9.
    // =========================================================================================

    private const string TaskNotFoundNotice = "That task could not be found.";
    private const string SessionExpiredMessage = "Your session has expired — sign in again to see results.";

    private static TaskDetailResponse TaskDetail(
        int taskId,
        string status = "Completed",
        string type = "KeywordSearch",
        bool isAcknowledged = false,
        string term = "invoice",
        string extension = "PDF",
        string? resultDetail = null,
        string? mode = null) => new()
        {
            TaskId = taskId,
            Type = type,
            SubmittedBy = "full-access-user",
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            StartTime = status == "Queued" ? null : DateTimeOffset.UtcNow.AddMinutes(-3),
            CompletedAt = status is "Completed" or "Failed" or "Cancelled" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
            Status = status,
            IsAcknowledged = isAcknowledged,
            ResultDetail = resultDetail,
            SearchParameters = type == "KeywordSearch" ? new TaskSearchParameters { Term = term, Extension = extension, Mode = mode } : null,
        };

    private static TaskListItem ListItemFor(TaskDetailResponse detail) => new()
    {
        TaskId = detail.TaskId,
        Type = detail.Type,
        SubmittedBy = detail.SubmittedBy,
        SubmittedAt = detail.SubmittedAt,
        StartTime = detail.StartTime,
        CompletedAt = detail.CompletedAt,
        Status = detail.Status,
        IsAcknowledged = detail.IsAcknowledged,
        SearchMode = detail.SearchParameters?.Mode,
    };

    /// <summary>GET /tasks returns <paramref name="latest"/> (when given); GET /tasks/{id} answers
    /// from <paramref name="tasks"/> and 404s any other id.</summary>
    private void StubTasks(TaskDetailResponse? latest, params TaskDetailResponse[] tasks)
    {
        if (latest is not null)
        {
            _apiHandler.OnGetTasks = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new PagedResult<TaskListItem>
            {
                Items = new[] { ListItemFor(latest) },
                Page = 1,
                PageSize = 1,
                TotalCount = 1,
            }));
        }

        var byId = tasks.Append(latest).Where(t => t is not null).ToDictionary(t => t!.TaskId, t => t!);
        _apiHandler.OnGetTask = (_, id) => Task.FromResult(byId.TryGetValue(id, out var task)
            ? JsonResponse(HttpStatusCode.OK, task)
            : JsonResponse(HttpStatusCode.NotFound, new { type = "https://cachet.tracecounts.internal/errors/task-not-found", title = "Task not found.", status = 404 }));
    }

    private static SearchResultsResponse CompletedResponseFor(int taskId, string nameMarker) => CompletedResponse(
        new[] { SampleFilenameItem(11) with { Name = $"{nameMarker}-filename.pdf" } },
        new[] { SampleContentItem(21) with { Name = $"{nameMarker}-content.docx" } }) with
    { TaskId = taskId };

    private IRenderedComponent<Web.Components.Pages.KeywordSearch> RenderPageAt(string? taskIdQuery, bool selectKeywordMode = true)
    {
        if (taskIdQuery is not null)
        {
            Services.GetRequiredService<NavigationManager>().NavigateTo($"/?taskId={Uri.EscapeDataString(taskIdQuery)}");
        }

        var cut = RenderComponent<Web.Components.Pages.KeywordSearch>();
        if (selectKeywordMode)
        {
            cut.Find("#ks-mode-keyword").Change("keyword"); // CAS-044: Files is the default mode
        }

        return cut;
    }

    private bool AnyRequest(HttpMethod method, string pathSuffix) =>
        _apiHandler.Requests.ToList().Any(r => r.Method == method
            && r.RequestUri!.AbsolutePath.TrimEnd('/').EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<string> ResultsPaths() =>
        _apiHandler.Requests.ToList()
            .Where(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("/results", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.RequestUri!.AbsolutePath)
            .ToList();

    // -----------------------------------------------------------------------------------------
    // SC-7
    // -----------------------------------------------------------------------------------------

    // CAS-048 §5a: rewritten — a reloaded Completed task fills the results explorer through browse (no
    // search rerun); the only results call is the best-effort "matches in total" figure. A plain open does
    // not acknowledge (V10: only a task the user opens is acknowledged).
    [Fact]
    public void Test_SC7_KeywordSearch_ReloadsCompletedTaskWithoutResubmitting()
    {
        // Arrange — the user's latest KeywordSearch task is Completed.
        StubTasks(TaskDetail(501, term: "invoice", extension: "PDF"));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(501, 3)));
        _apiHandler.OnPostSearch = _ => throw new InvalidOperationException("SC-7: reloading must never POST /search");

        // Act
        var cut = RenderPageAt(taskIdQuery: null);
        cut.WaitForState(() => ExplorerHasRows(cut), TimeSpan.FromSeconds(3));

        // Assert — the explorer is filled from the stored rows of task 501 through browse.
        ExplorerRowNames(cut).Should().Equal("Alpha", "Beta");
        BrowsePaths().Should().OnlyContain(p => p == "/api/search/501/browse");
        ResultsPaths().Should().Equal("/api/search/501/results");
        cut.Find("#fe-root .fe-status-count").TextContent.Should().Contain("3 matches in total");

        // The context header is labelled from searchParameters (§8.2 point 3).
        // prefill superseded by CAS-039 A4: the filter bar keeps its defaults (Keyword mode, empty
        // term, no date inputs, Extension All) instead of the task's term and extension.
        AssertFilterBarDefaults(cut);
        cut.Find(".cachet-ks-context-header").TextContent.Should()
            .Contain("Showing results for \"invoice\" (PDF), submitted 3 minutes ago.");

        // Latest task resolved through the own-scoped list, filtered by type (§8.2 step 1).
        _apiHandler.Requests.ToList().Should().Contain(r => r.Method == HttpMethod.Get
            && r.RequestUri!.AbsolutePath.EndsWith("/tasks")
            && r.RequestUri.Query == "?type=KeywordSearch&page=1&pageSize=1");

        // No POST /search, no new task, and (V10) no acknowledge on a silent plain open.
        AnyRequest(HttpMethod.Post, "/search").Should().BeFalse();
        _apiHandler.Requests.ToList().Where(r => r.Method == HttpMethod.Post).Should().BeEmpty();
        _apiHandler.AcknowledgedTaskIds.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // SC-8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_RunningTaskReload_RestoresFilterCriteriaAndKeywordBanner()
    {
        StubTasks(TaskDetail(502, status: "Running", term: "pioneer", extension: "PDF", mode: "Keyword"));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        var cut = RenderPageAt(taskIdQuery: null, selectKeywordMode: false);
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(3));

        cut.Find("#ks-term").GetAttribute("value").Should().Be("pioneer", "the running search's term stays in the filter bar");
        ((AngleSharp.Html.Dom.IHtmlSelectElement)cut.Find("#ks-extension")).Value.Should().Be("PDF");
        cut.Find("#ks-mode-keyword").HasAttribute("checked").Should().BeTrue();
        cut.Markup.Should().Contain("running for", "a running Keyword search shows the elapsed-time banner");
    }

    [Fact]
    public void Test_RunningFilenameTask_ShowsGenericBannerWithoutTimer()
    {
        StubTasks(TaskDetail(503, status: "Running", term: "invoice", extension: "All", mode: "Filename"));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        var cut = RenderPageAt(taskIdQuery: null, selectKeywordMode: false);
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(3));

        cut.Markup.Should().Contain("Search in progress. Results will appear here automatically when ready.");
        cut.Markup.Should().NotContain("running for");
    }

    [Fact]
    public void Test_ReadOnlyUser_NeverSeesQuarantinePanelOrQueueButton()
    {
        _authContext.SetClaims(new System.Security.Claims.Claim("b2_capability", "Read Only"));

        var cut = RenderPageWithResults();

        cut.FindAll(".cachet-ks-quarantine-panel").Should().BeEmpty();
        cut.FindAll(".cachet-ks-quarantine-trigger-btn").Should().BeEmpty();
    }

    [Fact]
    public void Test_QuarantinePanelHiddenUntilResultsAreDisplayed()
    {
        var cut = RenderPage();

        cut.FindAll(".cachet-ks-quarantine-panel").Should().BeEmpty("no search results are on screen yet");
    }

    [Fact]
    public void Test_SC8_KeywordSearch_ReloadsRunningTaskAndAutoPopulatesOnCompletion()
    {
        // Arrange — latest task is Running; the first poll tick is still 409, the second completes.
        StubTasks(TaskDetail(502, status: "Running"));
        _apiHandler.OnGetResults = (_, call) => Task.FromResult(call == 1
            ? new HttpResponseMessage(HttpStatusCode.Conflict)
            : JsonResponse(HttpStatusCode.OK, ResultsCompleted(502, 3)));
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var uriBefore = navigationManager.Uri;

        // Act 1
        var cut = RenderPageAt(taskIdQuery: null);
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(3));

        // Assert 1 — the existing in-progress banner, and nothing loaded yet.
        cut.Markup.Should().Contain("Search in progress — running for 3m");
        cut.FindAll("#fe-root").Should().BeEmpty();

        // Act 2 — polling resumes on the configured interval.
        cut.WaitForState(() => ExplorerHasRows(cut), PollWaitTimeout);

        // Assert 2 — explorer filled in place, no navigation, no resubmission.
        ExplorerRowNames(cut).Should().Equal("Alpha", "Beta");
        cut.Markup.Should().NotContain("Search in progress");
        navigationManager.Uri.Should().Be(uriBefore);
        ResultsPaths().Should().OnlyContain(p => p == "/api/search/502/results");
        BrowsePaths().Should().OnlyContain(p => p == "/api/search/502/browse");
        _apiHandler.ResultsCallCount.Should().BeGreaterThanOrEqualTo(2);
        AnyRequest(HttpMethod.Post, "/search").Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------
    // SC-9 (Keyword Search variant; the /odrive-sync variant is in ODriveSyncPageTests)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC9_DeepLink_PopulatesNamedTask()
    {
        // Arrange — the latest task is 600, but the deep link names 777.
        StubTasks(
            latest: TaskDetail(600, term: "latest-term", extension: "All"),
            TaskDetail(777, term: "contract", extension: "DOCX"));
        _apiHandler.OnGetResults = (request, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            ResultsCompleted(request.RequestUri!.AbsolutePath.Contains("/777/") ? 777 : 600, 3)));

        // Act
        var cut = RenderPageAt("777");
        cut.WaitForState(() => ExplorerHasRows(cut), TimeSpan.FromSeconds(3));

        // Assert — task 777's rows and parameters, never the latest task's.
        BrowsePaths().Should().OnlyContain(p => p == "/api/search/777/browse");
        ResultsPaths().Should().Equal("/api/search/777/results");
        // prefill superseded by CAS-039 A4: the filter bar keeps its defaults; the header names task 777.
        AssertFilterBarDefaults(cut);
        cut.Find(".cachet-ks-context-header").TextContent.Should().Contain("Showing results for \"contract\" (DOCX)");
        AnyRequest(HttpMethod.Get, "/tasks").Should().BeFalse("a deep link resolves GET /tasks/{taskId}, not the latest-task list");
    }

    // -----------------------------------------------------------------------------------------
    // V7
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("404404")]  // GET /tasks/{taskId} returns 404 (missing or another user's task)
    [InlineData("880")]     // own task, but a ContentScan (wrong type for this screen)
    public void Test_V7_DeepLinkUnknownOrWrongTypeTask_ShowsNotFoundNotice(string taskIdQuery)
    {
        // Arrange
        StubTasks(latest: TaskDetail(600), TaskDetail(880, type: "ContentScan"));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(600, 3)));

        // Act
        var cut = RenderPageAt(taskIdQuery);
        cut.WaitForState(() => cut.Markup.Contains(TaskNotFoundNotice), TimeSpan.FromSeconds(3));

        // Assert — the notice, no results, no acknowledge.
        cut.Find(".cachet-ks-notice").TextContent.Should().Be(TaskNotFoundNotice);
        _apiHandler.ResultsCallCount.Should().Be(0);
        _apiHandler.AcknowledgedTaskIds.Should().BeEmpty();
        cut.Find(".cachet-ks-results-prompt").TextContent.Should().Be("Enter a search…");
        cut.FindAll("#fe-root").Should().BeEmpty();
        cut.Markup.Should().NotContain("cachet-ks-context-header");

        // The filter bar stays usable.
        SearchInput(cut).HasAttribute("disabled").Should().BeFalse();
        SearchInput(cut).Input("new-term");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------
    // SC-11 (Keyword Search variant; the /odrive-sync variant is in ODriveSyncPageTests)
    // -----------------------------------------------------------------------------------------

    // CAS-048 V10 (rewritten): only a task the user explicitly opens (?taskId=) is acknowledged; the silent
    // latest-task reload on a plain open ("CompletedPlainOpen") acknowledges nothing.
    [Theory]
    [InlineData("Completed", false, 1)]
    [InlineData("Failed", false, 1)]
    [InlineData("RunningThenCompleted", false, 1)]
    [InlineData("Completed", true, 0)]   // already acknowledged
    [InlineData("CompletedPlainOpen", false, 0)]   // CAS-048 V10: a plain open never acknowledges
    [InlineData("Queued", false, 0)]     // never reaches a finished state during the test
    [InlineData("Running", false, 0)]
    [InlineData("Cancelled", false, 0)]
    [InlineData("NotFound", false, 0)]   // V7 deep link
    public async Task Test_SC11_Screens_AcknowledgeOnlyOwnRenderedTerminalTaskOnce(string scenario, bool alreadyAcknowledged, int expectedAcks)
    {
        // Arrange
        const int TaskId = 503;
        var status = scenario == "RunningThenCompleted" ? "Running" : scenario is "NotFound" or "CompletedPlainOpen" ? "Completed" : scenario;
        StubTasks(TaskDetail(TaskId, status: status, isAcknowledged: alreadyAcknowledged,
            resultDetail: status == "Failed" ? "Mirror directory unavailable." : null));
        _apiHandler.OnGetResults = (_, call) => Task.FromResult(
            scenario is "Queued" or "Running" || (scenario == "RunningThenCompleted" && call == 1)
                ? new HttpResponseMessage(HttpStatusCode.Conflict)
                // CAS-057: a Cancelled task's results are fetched (totals and counts) and answer Status "Cancelled".
                : scenario == "Cancelled"
                    ? JsonResponse(HttpStatusCode.OK, ResultsCancelled(TaskId, matches: 0))
                    : JsonResponse(HttpStatusCode.OK, ResultsCompleted(TaskId, 3)));

        // Act
        var taskIdQuery = scenario switch
        {
            "NotFound" => "999999",
            "CompletedPlainOpen" => null,
            _ => TaskId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var cut = RenderPageAt(taskIdQuery);
        switch (scenario)
        {
            case "Completed":
            case "RunningThenCompleted":
            case "CompletedPlainOpen":
                cut.WaitForState(() => ExplorerHasRows(cut), PollWaitTimeout);
                break;
            case "Failed":
                cut.WaitForState(() => cut.Markup.Contains("This search did not complete successfully."), TimeSpan.FromSeconds(3));
                cut.Find(".cachet-ks-error-banner > span").TextContent.Should().Be(
                    "This search did not complete successfully. Mirror directory unavailable.", "resultDetail is appended when present (§8.2)");
                break;
            case "Queued":
            case "Running":
                cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(3));
                break;
            case "Cancelled":
                cut.WaitForState(() => _apiHandler.Requests.ToList().Any(r => r.RequestUri!.AbsolutePath.EndsWith($"/tasks/{TaskId}")), TimeSpan.FromSeconds(3));
                break;
            case "NotFound":
                cut.WaitForState(() => cut.Markup.Contains(TaskNotFoundNotice), TimeSpan.FromSeconds(3));
                break;
        }

        if (expectedAcks > 0)
        {
            await WaitUntilAsync(() => _apiHandler.AcknowledgedTaskIds.Count >= 1, TimeSpan.FromSeconds(3));
        }

        // Force several more renders (and let poll ticks run) — the acknowledge must not repeat.
        SearchInput(cut).Input("re-render-1");
        SearchInput(cut).Input("re-render-2");
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Assert
        _apiHandler.AcknowledgedTaskIds.Should().HaveCount(expectedAcks);
        _apiHandler.AcknowledgedTaskIds.Should().NotContain(id => id != TaskId);
        if (scenario == "Cancelled")
        {
            // CAS-057 §2.8: a Cancelled task without matches shows no results (the prompt stays); the reload reads its
            // counts once (a deep link then shows the cancelled notice) and never acknowledges it (§2.9).
            cut.Find(".cachet-ks-results-prompt").TextContent.Should().Be("Enter a search…", "Cancelled without matches = no results to show");
            _apiHandler.ResultsCallCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task Test_SC11_AcknowledgeFailureIsLoggedNotShown()
    {
        // §8.5: an acknowledge failure (any non-204, or unreachable) is logged at Warning, never shown.
        StubTasks(TaskDetail(504));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(504, 3)));
        _apiHandler.OnPostAcknowledge = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var cut = RenderPageAt("504");
        cut.WaitForState(() => ExplorerHasRows(cut), TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => _loggerProvider.Messages.Any(m => m.Contains("acknowledge of task 504 returned 500")), TimeSpan.FromSeconds(3));

        cut.Markup.Should().NotContain("Something went wrong");
        ExplorerHasRows(cut).Should().BeTrue();
    }

    [Fact]
    public async Task Test_SC11_AcknowledgeUnreachableIsLoggedNotShown()
    {
        StubTasks(TaskDetail(505));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(505, 3)));
        _apiHandler.OnPostAcknowledge = (_, _) => throw new HttpRequestException("simulated: Api unreachable");

        var cut = RenderPageAt("505");
        cut.WaitForState(() => ExplorerHasRows(cut), TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => _loggerProvider.Messages.Any(m => m.Contains("acknowledge of task 505 failed to reach the Api")), TimeSpan.FromSeconds(3));

        cut.Markup.Should().NotContain("Something went wrong");
    }

    // -----------------------------------------------------------------------------------------
    // V9 (Keyword Search variant — "a 401 from any poll call ... stops that poll loop")
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("resultsPoll")]
    [InlineData("reloadList")]
    [InlineData("reloadDetail")]
    [InlineData("reloadResults")]
    public async Task Test_V9_PollReceives401_StopsPollingAndShowsSessionExpired(string where)
    {
        // Arrange
        StubTasks(TaskDetail(506, status: where == "resultsPoll" ? "Running" : "Completed"));
        if (where == "reloadList")
        {
            _apiHandler.OnGetTasks = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
        else if (where == "reloadDetail")
        {
            _apiHandler.OnGetTask = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        // Act
        var cut = RenderPageAt(taskIdQuery: null);
        cut.WaitForState(() => cut.Markup.Contains(SessionExpiredMessage), PollWaitTimeout);
        var resultsCallsAt401 = _apiHandler.ResultsCallCount;
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Assert — the message with a sign-in link; no stale "in progress"; the loop stopped.
        var banner = cut.FindAll(".cachet-ks-error-banner").Single(b => b.TextContent.Contains(SessionExpiredMessage));
        // PathBase-relative (resolves against App.razor's <base href>), same as ReportModal's sign-in link.
        banner.QuerySelector("a")!.GetAttribute("href").Should().Be("login");
        cut.Markup.Should().NotContain("Search in progress");
        // No explorer is mounted after a 401 (no results were ever loaded).
        cut.FindAll("#fe-root").Should().BeEmpty();
        _apiHandler.ResultsCallCount.Should().Be(resultsCallsAt401, "a 401 stops that poll loop (V9)");
        _apiHandler.AcknowledgedTaskIds.Should().BeEmpty();
        _loggerProvider.Messages.Should().Contain(m => m.Contains("KeywordSearch: poll stopped on 401 from"));
    }

    // ---- Coverage-gap closure (not a spec-mapped SC/V ID): transient failures during the reload keep
    //      the page usable (V10 / CAS-022 §8.5 behavior) rather than crashing it.

    [Theory]
    [InlineData("listThrows")]
    [InlineData("list500")]
    [InlineData("detailThrows")]
    [InlineData("detail500")]
    [InlineData("resultsThrows")]
    [InlineData("results500")]
    [InlineData("resultsNotCompleted")]
    public async Task Test_ReloadTransientFailureKeepsPageUsable(string failure)
    {
        // Arrange
        StubTasks(TaskDetail(507));
        switch (failure)
        {
            case "listThrows": _apiHandler.OnGetTasks = _ => throw new HttpRequestException("simulated"); break;
            case "list500": _apiHandler.OnGetTasks = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); break;
            case "detailThrows": _apiHandler.OnGetTask = (_, _) => throw new HttpRequestException("simulated"); break;
            case "detail500": _apiHandler.OnGetTask = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); break;
        }

        _apiHandler.OnGetResults = failure switch
        {
            "resultsThrows" => (_, _) => throw new HttpRequestException("simulated"),
            "results500" => (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            "resultsNotCompleted" => (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(507, 3) with { Status = "Failed" })),
            _ => (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(507, 3))),
        };

        // Act
        var cut = RenderPageAt(taskIdQuery: null);

        // Assert
        if (failure.StartsWith("results", StringComparison.Ordinal))
        {
            // CAS-048 §2.2: the explorer reads stored rows through browse; the results call only supplies the
            // best-effort "M matches in total", so its failure is logged and the total is omitted — it no
            // longer raises a banner (old behavior removed with the panels).
            cut.WaitForState(() => ExplorerHasRows(cut), TimeSpan.FromSeconds(3));
            cut.Markup.Should().NotContain("Something went wrong");
            cut.Find("#fe-root .fe-status-count").TextContent.Should().NotContain("matches in total");
        }
        else
        {
            var failingPath = failure.StartsWith("list", StringComparison.Ordinal) ? "/tasks" : "/tasks/507";
            await WaitUntilAsync(() => AnyRequest(HttpMethod.Get, failingPath), TimeSpan.FromSeconds(3));
            await Task.Delay(300); // let the reload finish handling the failed response
            cut.Find(".cachet-ks-results-prompt").TextContent.Should().Be("Enter a search…", "the empty initial state is kept");
            cut.Markup.Should().NotContain("Something went wrong");
            _apiHandler.ResultsCallCount.Should().Be(0);
        }

        cut.Markup.Should().NotContain(SessionExpiredMessage);
        _apiHandler.AcknowledgedTaskIds.Should().BeEmpty();
        SearchInput(cut).Input("still-usable");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Test_NewSearchAfterReloadClearsReloadedContext()
    {
        // §8.2: a new search submitted after a reload behaves exactly as today.
        StubTasks(TaskDetail(508, term: "old-term"));
        _apiHandler.OnGetResults = (request, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            ResultsCompleted(request.RequestUri!.AbsolutePath.Contains("/508/") ? 508 : 1, 3)));
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));

        var cut = RenderPageAt(taskIdQuery: null);
        cut.WaitForState(() => ExplorerHasRows(cut), TimeSpan.FromSeconds(3));
        BrowsePaths().Should().OnlyContain(p => p == "/api/search/508/browse");
        cut.Find(".cachet-ks-context-header").Should().NotBeNull();

        SubmitSearch(cut, "new-term");
        cut.WaitForState(() => BrowsePaths().Contains("/api/search/1/browse") && ExplorerHasRows(cut), PollWaitTimeout);

        cut.Markup.Should().NotContain("cachet-ks-context-header");
    }

    // =========================================================================================
    // CAS-031 SC-1 / SC-10 — Audit Log trigger button (the O-Drive Sync host's SC-2 is in
    // ODriveSyncPageTests).
    // =========================================================================================

    [Fact]
    public void Test_SC1_OpenFromKeywordSearchLoadsDefaultPage()
    {
        // Arrange
        _apiHandler.OnGetAuditLog = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new PagedResult<AuditLogListItem>
        {
            Items = new[]
            {
                new AuditLogListItem
                {
                    User = "reviewer1",
                    Tab = "Keyword Search",
                    Action = "Search",
                    FilePath = null,
                    Timestamp = new DateTimeOffset(2026, 9, 9, 14, 5, 7, TimeSpan.Zero),
                    Result = "Success",
                },
            },
            Page = 1,
            PageSize = 50,
            TotalCount = 1,
        }));
        var cut = RenderPage();
        cut.Markup.Should().NotContain("cachet-al-modal", "the modal is closed until the button is clicked");

        // Act
        cut.Find(".cachet-al-trigger-btn").Click();
        cut.WaitForState(() => cut.FindAll(".cachet-al-table").Count > 0, TimeSpan.FromSeconds(3));

        // Assert — the modal is open and issued exactly the default request.
        cut.Find(".cachet-al-modal").GetAttribute("role").Should().Be("dialog");
        cut.Find("#cachet-al-title").TextContent.Should().Be("Audit Log");
        var auditLogRequests = _apiHandler.Requests.ToList()
            .Where(r => r.RequestUri!.AbsolutePath.EndsWith("/audit-log", StringComparison.OrdinalIgnoreCase))
            .ToList();
        auditLogRequests.Should().ContainSingle();
        auditLogRequests[0].Method.Should().Be(HttpMethod.Get);
        auditLogRequests[0].RequestUri!.Query.Should().Be("?page=1&pageSize=50", "no tab/dateFrom/dateTo on the default load");
        cut.Find(".cachet-al-table tbody tr").TextContent.Should().Contain("reviewer1");
    }

    /// <summary>CAS-031 SC-10 — asserts the Audit Log button on BOTH host screens (Keyword Search and
    /// O-Drive Sync) in this single test, so the test name exists exactly once per §13.</summary>
    [Fact]
    public void Test_SC10_AuditLogButtonEnabledWithoutBadge()
    {
        var keywordSearch = RenderPage();
        var oDriveSync = RenderComponent<Web.Components.Pages.ODriveSyncContentScan>();

        foreach (var markupRoot in new[] { keywordSearch.Find(".cachet-toolbar"), oDriveSync.Find(".cachet-toolbar") })
        {
            var button = markupRoot.QuerySelectorAll("button").Single(b => b.TextContent.Contains("Audit Log"));

            button.ClassList.Should().Contain("cachet-al-trigger-btn");
            button.HasAttribute("disabled").Should().BeFalse("the Audit Log button is live (CAS-031 §3)");
            button.GetAttribute("title").Should().NotBe("Coming soon");
            button.HasAttribute("title").Should().BeFalse();
            button.ClassList.Should().NotContain("cachet-header-btn-disabled");
            button.QuerySelector(".cachet-btn-count").Should().BeNull("no count badge (CAS-031 §0 point 3)");
            button.TextContent.Trim().Should().Be("Audit Log");
        }
    }

    // -----------------------------------------------------------------------------------------
    // CAS-032 SC-1 (Pre-Action / Post-Action report trigger buttons; the O-Drive Sync host's
    // identically-named test is in its own page test class, per CAS-032 §13.2)
    // -----------------------------------------------------------------------------------------

    /// <summary>CAS-032 SC-1 — on this host page both report buttons are enabled (no longer the
    /// CAS-025 disabled placeholders) and each opens <c>ReportModal</c> in the matching mode with this
    /// host's tab label as the subtitle. No preview route is configured on this page's fake handler, so
    /// the modal settles in its §8.5 preview-error state (502) — the mode, subtitle and the preview GET
    /// route it issued are what this criterion asserts.</summary>
    [Fact(Skip = "Pre-/Post-Action Report buttons are hidden by request (see the commented block in the page Razor). Remove this Skip when the buttons are restored.")]
    public void Test_SC1_TriggersOnBothPagesOpenModalInCorrectMode()
    {
        var cut = RenderPage();

        foreach (var (label, title, route) in new[]
                 {
                     ("Pre-Action Report", "PRE-ACTION IMPACT REPORT", "/reports/pre-action/preview"),
                     ("Post-Action Report", "POST-ACTION OUTCOME REPORT", "/reports/post-action/preview"),
                 })
        {
            cut.Markup.Should().NotContain("cachet-report-modal", "the report modal is closed until a trigger is clicked");
            var button = cut.Find(".cachet-actionbar").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == label);
            button.HasAttribute("disabled").Should().BeFalse($"{label} is enabled for every authenticated session (§8.1)");
            button.HasAttribute("title").Should().BeFalse("the CAS-025 'Coming soon' title is removed");
            button.ClassList.Should().Contain("cachet-btn");
            button.ClassList.Should().NotContain("cachet-header-btn-disabled");
            var requestsBefore = _apiHandler.Requests.Count;

            button.Click();

            cut.WaitForState(() => cut.FindAll(".cachet-report-modal .cachet-report-error-banner").Count > 0, TimeSpan.FromSeconds(3));
            cut.Find("#cachet-report-title").TextContent.Should().Be(title);
            cut.Find(".cachet-report-subtitle").TextContent.Should().Be("Keyword Search");
            var previewRequest = _apiHandler.Requests.ToList().Skip(requestsBefore)
                .Single(r => r.RequestUri!.AbsolutePath.Contains("/reports/", StringComparison.OrdinalIgnoreCase));
            previewRequest.Method.Should().Be(HttpMethod.Get);
            previewRequest.RequestUri!.AbsolutePath.Should().EndWith(route);
            Uri.UnescapeDataString(previewRequest.RequestUri.Query).Should().Be("?activeTabLabel=Keyword Search");

            cut.Find(".cachet-report-close-btn").Click();
        }
    }

    // =========================================================================================
    // CAS-039 — "Keyword Search / Date Search" mode toggle with From/To dates (MM/dd/yyyy).
    // The component has no clock seam ("today" = DateOnly.FromDateTime(DateTime.UtcNow)), so per
    // CAS-039 §13 the V9 cases use 12/31/2099, and "valid range" cases not pinned by §10 use
    // January 2025 so they can never be in the future relative to the machine clock.
    // =========================================================================================

    private const string DateFormatHint = "Enter a date as MM/dd/yyyy.";
    private const string DateOrderHint = "From date must be on or before To date.";
    private const string DateFutureHint = "Dates cannot be in the future.";

    private static void SelectDateMode(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.Find("#ks-mode-date").Change("date");

    private static void SelectKeywordMode(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.Find("#ks-mode-keyword").Change("keyword");

    private static void EnterDates(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string fromDate, string toDate)
    {
        cut.Find("#ks-from-date").Input(fromDate);
        cut.Find("#ks-to-date").Input(toDate);
    }

    /// <summary>Stubs POST /search (202) and records the deserialized body plus its raw JSON.
    /// GET /search/{taskId}/results stays 409 unless the caller overrides it.</summary>
    private sealed class PostedSearch
    {
        public SearchRequest? Request { get; set; }

        public string? RawJson { get; set; }
    }

    private PostedSearch CapturePostedSearch()
    {
        var posted = new PostedSearch();
        _apiHandler.OnPostSearch = async request =>
        {
            posted.RawJson = await request.Content!.ReadAsStringAsync();
            posted.Request = await request.Content!.ReadFromJsonAsync<SearchRequest>();
            return JsonResponse(HttpStatusCode.Accepted, SubmittedResponse());
        };
        _apiHandler.OnGetResults ??= (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        return posted;
    }

    private static IReadOnlyList<string> DateHints(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.FindAll(".cachet-ks-date-hint").Select(h => h.TextContent.Trim()).ToList();

    // -----------------------------------------------------------------------------------------
    // CAS-039 SC-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC1_DefaultsToKeywordModeWithNoDateInputs()
    {
        var cut = RenderPage();

        var keywordRadio = cut.Find("#ks-mode-keyword");
        var dateRadio = cut.Find("#ks-mode-date");
        keywordRadio.GetAttribute("type").Should().Be("radio");
        keywordRadio.GetAttribute("name").Should().Be("ks-mode");
        dateRadio.GetAttribute("name").Should().Be("ks-mode");
        keywordRadio.HasAttribute("checked").Should().BeTrue("Keyword Search is selected by default on every fresh load (§2.1)");
        dateRadio.HasAttribute("checked").Should().BeFalse();
        cut.FindAll("label.cachet-ks-mode-option span").Select(s => s.TextContent.Trim())
            .Should().Equal("Files", "Filename Search", "Keyword Search", "Date Search"); // CAS-044 Files, CAS-048 Filename Search

        cut.FindAll("#ks-from-date").Should().BeEmpty("the date controls are not rendered in Keyword mode (§2.2)");
        cut.FindAll("#ks-to-date").Should().BeEmpty();
        cut.FindAll(".cachet-ks-date-row").Should().BeEmpty();
        cut.Markup.Should().NotContain("From date").And.NotContain("To date");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 SC-2
    // -----------------------------------------------------------------------------------------

    // SC-2 amended by A3: the name is retained for traceability, but the search term input is no
    // longer rendered in Date mode (§2.6b supersedes §2.3's "term input stays visible").
    [Fact]
    public void Test_SC2_DateModeShowsFromAndToInputsAndKeepsTerm()
    {
        var cut = RenderPage();

        SelectDateMode(cut);

        cut.Find("#ks-mode-date").HasAttribute("checked").Should().BeTrue();
        cut.Find("#ks-mode-keyword").HasAttribute("checked").Should().BeFalse();

        foreach (var (id, labelText) in new[] { ("ks-from-date", "From date"), ("ks-to-date", "To date") })
        {
            cut.Find($"label[for={id}]").TextContent.Trim().Should().Be(labelText);
            var input = cut.Find($"#{id}");
            input.GetAttribute("type").Should().Be("text");
            input.GetAttribute("placeholder").Should().Be("MM/dd/yyyy");
            input.GetAttribute("inputmode").Should().Be("numeric");
        }

        cut.FindAll("#ks-term").Should().BeEmpty("the search term input is not rendered in Date mode (SC-2 amended by A3, §2.6b)");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 SC-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC3_KeywordModePostsNullDates()
    {
        var cut = RenderPage();
        var posted = CapturePostedSearch();

        cut.Find("#ks-extension").Change("PDF");
        SubmitSearch(cut, "  quarterly-report  ");
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));

        posted.Request!.Term.Should().Be("quarterly-report", "the term is trimmed as today");
        posted.Request.Bucket.Should().Be(ConfiguredBucket);
        posted.Request.Extension.Should().Be("PDF");
        posted.Request.FromDate.Should().BeNull();
        posted.Request.ToDate.Should().BeNull();
        posted.RawJson.Should().Contain("\"fromDate\":null").And.Contain("\"toDate\":null",
            "the POST /search body carries fromDate: null, toDate: null in Keyword mode (§2.2)");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 SC-4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC4_DateModePostsParsedDates()
    {
        var cut = RenderPage();
        var posted = CapturePostedSearch();

        // CAS-039 A3: Date mode has no term input. 09/01/2026–09/30/2026 (pinned by §10) is 30 days
        // inclusive, so it is within the V10 31-day cap that always applies in Date mode.
        SelectDateMode(cut);
        EnterDates(cut, "09/01/2026", "09/30/2026");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse();
        SearchButton(cut).Click();
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));

        posted.Request!.Term.Should().Be(string.Empty, "Date mode posts Term = \"\" (A3 §2.6b)");
        posted.Request.Bucket.Should().Be(ConfiguredBucket);
        posted.Request.Extension.Should().Be("All");
        posted.Request.FromDate.Should().Be(new DateOnly(2026, 9, 1));
        posted.Request.ToDate.Should().Be(new DateOnly(2026, 9, 30));
        posted.RawJson.Should().Contain("\"fromDate\":\"2026-09-01\"").And.Contain("\"toDate\":\"2026-09-30\"",
            "parsed dates are sent as ISO DateOnly (§16)");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC5_SwitchingBackToKeywordClearsDates()
    {
        var cut = RenderPage();
        var posted = CapturePostedSearch();

        SelectDateMode(cut);
        EnterDates(cut, "01/01/2025", "01/31/2025");

        SelectKeywordMode(cut);

        cut.Find("#ks-mode-keyword").HasAttribute("checked").Should().BeTrue();
        cut.FindAll("#ks-from-date").Should().BeEmpty();
        cut.FindAll("#ks-to-date").Should().BeEmpty();
        cut.FindAll(".cachet-ks-date-hint").Should().BeEmpty();

        SubmitSearch(cut, "quarterly-report");
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));

        posted.Request!.FromDate.Should().BeNull("a hidden stale date is never sent (§2.4)");
        posted.Request.ToDate.Should().BeNull();

        SelectDateMode(cut);

        (cut.Find("#ks-from-date").GetAttribute("value") ?? string.Empty).Should().BeEmpty();
        (cut.Find("#ks-to-date").GetAttribute("value") ?? string.Empty).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC6_DateModeResultsFlowIdenticalToKeywordMode()
    {
        var cut = RenderPage();
        var posted = CapturePostedSearch();
        _apiHandler.OnGetResults = (_, call) => Task.FromResult(call == 1
            ? new HttpResponseMessage(HttpStatusCode.Conflict)
            : JsonResponse(HttpStatusCode.OK, ResultsCompleted(1, 3)));

        // CAS-039 A3: Date mode has no term input; the search is date-only.
        SelectDateMode(cut);
        EnterDates(cut, "01/01/2025", "01/31/2025");
        SearchButton(cut).Click();

        // Submit → in-progress banner + disabled Search, exactly as in Keyword mode.
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue();
        posted.Request!.Term.Should().Be(string.Empty, "Date mode posts Term = \"\" (A3 §2.6b)");
        posted.Request.FromDate.Should().Be(new DateOnly(2025, 1, 1));
        posted.Request.ToDate.Should().Be(new DateOnly(2025, 1, 31));
        posted.Request.Mode.Should().Be("Date");

        // Poll (409, then Completed) → the explorer is populated in place (CAS-048: no panels, no Content notice).
        cut.WaitForState(() => ExplorerHasRows(cut), PollWaitTimeout);
        ExplorerRowNames(cut).Should().Equal("Alpha", "Beta");
        cut.FindAll(".cachet-ks-panel h2").Select(h => h.TextContent).Should().NotContain(t => t.Contains("Matches"));
        cut.Markup.Should().NotContain("Search in progress");

        // Send to Quarantine → same request and panel refresh as Keyword mode.
        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");
        QuarantineAddRequest? capturedRequest = null;
        _apiHandler.OnPostQuarantineAdd = async request =>
        {
            capturedRequest = await request.Content!.ReadFromJsonAsync<QuarantineAddRequest>();
            return JsonResponse(HttpStatusCode.OK, new QuarantineAddResponse
            {
                Items = new[]
                {
                    new QuarantineAddResponseItem
                    {
                        FileId = 900,
                        Name = "invoice.pdf",
                        Path = "/o-drive/2026/invoice.pdf",
                        QuarantinedOn = DateTimeOffset.UtcNow,
                        Status = "Flagged",
                    },
                },
            });
        };
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleQuarantineItem(900, name: "invoice.pdf") },
        }));

        SendButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Quarantined On"), TimeSpan.FromSeconds(3));

        capturedRequest.Should().NotBeNull();
        capturedRequest!.TaskId.Should().Be(1);
        capturedRequest.FileIds.Should().BeEquivalentTo(new[] { 9001 });
        cut.Find(".cachet-ks-toast").TextContent.Should().Contain("1 file(s) sent to Quarantine.");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 V6
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-09-01", true)]
    [InlineData("9/1/2026", true)]
    [InlineData("13/01/2026", true)]
    [InlineData("09/01/2026", false)]
    public void Test_V6_DateModeRejectsNonMmDdYyyyInput(string fromDate, bool expectFormatHint)
    {
        // CAS-039 A3: Date mode has no term input. The valid case is a 1-day range (within V10).
        var cut = RenderPage();
        SelectDateMode(cut);

        EnterDates(cut, fromDate, "09/01/2026");

        cut.FindAll(".cachet-ks-date-hint-format").Should().HaveCount(expectFormatHint ? 1 : 0);
        if (expectFormatHint)
        {
            cut.Find(".cachet-ks-date-hint-format").TextContent.Trim().Should().Be(DateFormatHint);
        }

        SearchButton(cut).HasAttribute("disabled").Should().Be(expectFormatHint,
            "Search is enabled only when both fields parse exactly as MM/dd/yyyy (V6)");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 V7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V7_DateModeSearchDisabledUntilBothDatesValid()
    {
        var cut = RenderPage();
        SearchInput(cut).Input("quarterly-report");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse("precondition: the term alone is valid in Keyword mode");

        SelectDateMode(cut);
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("no dates entered (V7)");

        cut.Find("#ks-from-date").Input("01/01/2025");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("only From is valid (V7)");

        cut.Find("#ks-from-date").Input(string.Empty);
        cut.Find("#ks-to-date").Input("01/31/2025");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("only To is valid (V7)");
        DateHints(cut).Should().BeEmpty("V7 has no hint of its own");

        cut.Find("#ks-from-date").Input("01/01/2025");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse("both dates valid");
        DateHints(cut).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 V8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V8_FromAfterToDisablesSearchWithHint()
    {
        // CAS-039 A3: Date mode has no term input. From > To gives a negative range, so V10 is not
        // also triggered.
        var cut = RenderPage();
        SelectDateMode(cut);

        EnterDates(cut, "09/30/2026", "09/01/2026");

        cut.Find(".cachet-ks-date-hint-order").TextContent.Trim().Should().Be(DateOrderHint);
        cut.FindAll(".cachet-ks-date-hint-format").Should().BeEmpty();
        cut.FindAll(".cachet-ks-date-hint-range").Should().BeEmpty("V8 is isolated from V10");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("From must be on or before To (V8)");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 V9
    // -----------------------------------------------------------------------------------------

    // CAS-039 A3: V10 (31-day cap) now always applies in Date mode, so the "only To is in the
    // future" case can no longer use 01/01/2025–12/31/2099 without also tripping V10. It uses
    // yesterday → tomorrow (UTC, 3 days inclusive), computed at run time since there is no clock seam.
    public static TheoryData<string, string> V9FutureDateCases()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        string Format(DateOnly d) => d.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        return new TheoryData<string, string>
        {
            { Format(today.AddDays(-1)), Format(today.AddDays(1)) },
            { "12/31/2099", "12/31/2099" },
        };
    }

    [Theory]
    [MemberData(nameof(V9FutureDateCases))]
    public void Test_V9_FutureDateDisablesSearchWithHint(string fromDate, string toDate)
    {
        var cut = RenderPage();
        SelectDateMode(cut);

        EnterDates(cut, fromDate, toDate);

        cut.Find(".cachet-ks-date-hint-future").TextContent.Trim().Should().Be(DateFutureHint);
        cut.FindAll(".cachet-ks-date-hint-order").Should().BeEmpty();
        cut.FindAll(".cachet-ks-date-hint-format").Should().BeEmpty();
        cut.FindAll(".cachet-ks-date-hint-range").Should().BeEmpty("V9 is isolated from V10");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("neither date may be after today UTC (V9)");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 SC-8 (Amendment A1 — calendar picker)
    // The open itself is a browser-side delegated click listener (js/datepicker.js) that bUnit
    // cannot execute (verified manually per §16); this test proves the markup contract the
    // listener relies on and the ISO → MM/dd/yyyy write-back. bUnit's JSInterop is Strict by
    // default, so any component-initiated JS call would fail this test.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC8_CalendarButtonOpensPickerAndWritesMmDdYyyy()
    {
        var todayBefore = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var cut = RenderPage();
        SelectDateMode(cut);
        var todayAfter = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // Markup contract: calendar button + hidden native date input per field.
        foreach (var (field, label) in new[] { ("ks-from-date", "Choose From date"), ("ks-to-date", "Choose To date") })
        {
            var button = cut.Find($"#{field}-picker-btn");
            button.TagName.Should().Be("BUTTON");
            button.GetAttribute("type").Should().Be("button");
            button.GetAttribute("aria-label").Should().Be(label);
            button.GetAttribute("data-cachet-date-picker-for").Should().Be($"{field}-native");
            button.QuerySelector("svg")!.GetAttribute("aria-hidden").Should().Be("true");

            var native = cut.Find($"#{field}-native");
            native.GetAttribute("type").Should().Be("date");
            native.ClassList.Should().Contain("cachet-ks-date-native");
            native.GetAttribute("tabindex").Should().Be("-1");
            native.GetAttribute("aria-hidden").Should().Be("true");
            native.GetAttribute("max").Should().BeOneOf(new[] { todayBefore, todayAfter }, "the picker's max is today (UTC) (§2.3 A1)");
            (native.GetAttribute("value") ?? string.Empty).Should().BeEmpty("the text field holds no valid date yet");
        }

        // Picking an ISO date writes MM/dd/yyyy into the text field. (CAS-039 A3: no term input in
        // Date mode; 01/15/2025–01/31/2025 is within the V10 31-day cap.)
        cut.Find("#ks-from-date-native").Change("2025-01-15");
        cut.Find("#ks-from-date").GetAttribute("value").Should().Be("01/15/2025");
        cut.Find("#ks-to-date-native").Change("2025-01-31");
        cut.Find("#ks-to-date").GetAttribute("value").Should().Be("01/31/2025");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse("picked dates satisfy V6–V9 exactly as typed ones");
        DateHints(cut).Should().BeEmpty();

        // The native input opens on the field's current (valid) value.
        cut.Find("#ks-from-date-native").GetAttribute("value").Should().Be("2025-01-15");

        // Empty / unparseable picker values leave the text unchanged.
        cut.Find("#ks-from-date-native").Change(string.Empty);
        cut.Find("#ks-from-date").GetAttribute("value").Should().Be("01/15/2025");
        cut.Find("#ks-to-date-native").Change("not-a-date");
        cut.Find("#ks-to-date").GetAttribute("value").Should().Be("01/31/2025");

        // Typing a date keeps the native input in sync (yyyy-MM-dd).
        cut.Find("#ks-from-date").Input("01/10/2025");
        cut.Find("#ks-from-date-native").GetAttribute("value").Should().Be("2025-01-10");

        // Keyword mode renders no calendar buttons or native inputs.
        SelectKeywordMode(cut);
        cut.FindAll("[data-cachet-date-picker-for]").Should().BeEmpty();
        cut.FindAll(".cachet-ks-date-native").Should().BeEmpty();
    }

    // =========================================================================================
    // CAS-039 Amendment A2 — date-only search (keyword optional in Date mode, V10 31-day limit).
    // =========================================================================================

    private const string TermLengthHint = "Search term must be at most 200 characters.";
    private const string DateOnlyRangeHint = "Without a keyword, the date range can be at most 31 days.";
    private const string KeywordModePlaceholder = "Text to find inside files…";
    private const string DateZeroRowsCopy = "No files in that date range.";
    private const string KeywordZeroRowsCopy = "No files matched.";
    // -----------------------------------------------------------------------------------------
    // CAS-039 A2 — V1 (Date mode allows an empty term)
    // -----------------------------------------------------------------------------------------

    // A2 V1 superseded by A3: Date mode has no term input (§2.6b), so the A2 "optional term in Date
    // mode" rule is replaced by its A3 equivalent — the term is required in Keyword mode only, and
    // Date mode's Search depends on the dates alone. Name retained for traceability.
    [Fact]
    public void Test_A2_V1_DateModeAllowsEmptyTerm()
    {
        var cut = RenderPage();

        // Keyword mode with an empty term: Search disabled.
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("Keyword mode requires a term (V1)");

        // Date mode (no term input) with a valid 31-day range: Search enabled, no V1 hint.
        SelectDateMode(cut);
        cut.FindAll("#ks-term").Should().BeEmpty("Date mode renders no term input (A3 §2.6b)");
        EnterDates(cut, "01/01/2025", "01/31/2025");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse("Date mode Search depends on the dates only (A3 §2.6b)");
        cut.Markup.Should().NotContain(TermLengthHint);

        // Keyword mode with a 201-character term: disabled + V1 hint.
        SelectKeywordMode(cut);
        SearchInput(cut).Input(new string('a', 201));
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("a term over 200 characters fails V1 in Keyword mode");
        cut.Find(".cachet-input-hint").TextContent.Trim().Should().Be(TermLengthHint);
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 A2 — V10 (date-only range ≤ 31 days inclusive)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V10_DateOnlyRangeOver31DaysDisablesSearchWithHint()
    {
        var cut = RenderPage();
        SelectDateMode(cut);

        // 01/01/2025–01/31/2025 = 31 days inclusive: allowed.
        EnterDates(cut, "01/01/2025", "01/31/2025");
        cut.FindAll(".cachet-ks-date-hint-range").Should().BeEmpty("31 days inclusive is within the limit (V10)");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse();

        // 01/01/2025–02/01/2025 = 32 days inclusive: hint + disabled.
        cut.Find("#ks-to-date").Input("02/01/2025");
        var hint = cut.Find("span.cachet-ks-date-hint.cachet-ks-date-hint-range");
        hint.TextContent.Trim().Should().Be(DateOnlyRangeHint);
        DateHints(cut).Should().ContainSingle("V10 is the only failing rule").Which.Should().Be(DateOnlyRangeHint);
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("a date-only range over 31 days is not allowed (V10)");

        // CAS-039 A3: the former "with a keyword, no limit" step is unreachable — Date mode has no
        // term input, so V10 always applies there (§2.6b).
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 A2 — SC-9
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_A2_SC9_DateOnlySearchPostsEmptyTermWithDates()
    {
        var cut = RenderPage();
        var posted = CapturePostedSearch();

        SearchInput(cut).GetAttribute("placeholder").Should().Be(KeywordModePlaceholder, "Keyword mode keeps the existing placeholder");

        // SC-9 amended by A3: Date mode has no term input, so the "Keyword (optional)" placeholder
        // assertion no longer applies.
        SelectDateMode(cut);

        EnterDates(cut, "01/01/2025", "01/31/2025");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse();
        SearchButton(cut).Click();
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));

        posted.Request!.Term.Should().Be(string.Empty, "an empty term is posted as Term = \"\" (A2 §2.6a)");
        posted.Request.Bucket.Should().Be(ConfiguredBucket);
        posted.Request.FromDate.Should().Be(new DateOnly(2025, 1, 1));
        posted.Request.ToDate.Should().Be(new DateOnly(2025, 1, 31));
        posted.RawJson.Should().Contain("\"term\":\"\"")
            .And.Contain("\"fromDate\":\"2025-01-01\"")
            .And.Contain("\"toDate\":\"2025-01-31\"");

        SelectKeywordMode(cut);
        SearchInput(cut).GetAttribute("placeholder").Should().Be(KeywordModePlaceholder);
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 A3 — SC-12 (separate, exclusive modes)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_A3_SC12_ModesAreExclusiveAndClearEachOther()
    {
        var cut = RenderPage();
        // Every search completes at once with zero rows, so Search is re-enabled between submits.
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(1, 0)));
        _apiHandler.OnGetSearchBrowse = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, FileExplorer.BrowseFixtures.Listing(string.Empty)));
        var posted = CapturePostedSearch();

        // Keyword mode: type a term, then select Date → the term input is gone.
        SearchInput(cut).Input("quarterly-report");
        SelectDateMode(cut);
        cut.FindAll("#ks-term").Should().BeEmpty("selecting Date Search hides #ks-term (A3 §2.6b)");
        cut.FindAll("#ks-from-date").Should().ContainSingle();
        cut.FindAll("#ks-to-date").Should().ContainSingle();

        // Date mode Search posts Term = "" with both dates (the typed keyword was cleared).
        EnterDates(cut, "01/01/2025", "01/31/2025");
        SearchButton(cut).Click();
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));
        posted.Request!.Term.Should().Be(string.Empty, "selecting Date Search clears the keyword (A3 §2.6b)");
        posted.Request.FromDate.Should().Be(new DateOnly(2025, 1, 1));
        posted.Request.ToDate.Should().Be(new DateOnly(2025, 1, 31));
        posted.RawJson.Should().Contain("\"term\":\"\"");
        cut.WaitForState(() => cut.FindAll("#fe-root .fe-empty").Count > 0 && cut.Find("#fe-root .fe-empty").TextContent == DateZeroRowsCopy, PollWaitTimeout);

        // Select Keyword → #ks-term rendered empty, date inputs gone.
        SelectKeywordMode(cut);
        (SearchInput(cut).GetAttribute("value") ?? string.Empty).Should().BeEmpty("Keyword mode shows the term input empty (A3 §2.6b)");
        cut.FindAll("#ks-from-date").Should().BeEmpty();
        cut.FindAll("#ks-to-date").Should().BeEmpty();

        // Keyword mode Search posts the typed term with null dates.
        posted.Request = null;
        posted.RawJson = null;
        SubmitSearch(cut, "invoice");
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));
        posted.Request!.Term.Should().Be("invoice");
        posted.Request.FromDate.Should().BeNull();
        posted.Request.ToDate.Should().BeNull();
        cut.WaitForState(() => cut.FindAll("#fe-root .fe-empty").Count > 0 && cut.Find("#fe-root .fe-empty").TextContent == KeywordZeroRowsCopy, PollWaitTimeout);

        // Keyword → Date → Keyword never restores the old term or dates.
        SelectDateMode(cut);
        cut.FindAll("#ks-term").Should().BeEmpty();
        (cut.Find("#ks-from-date").GetAttribute("value") ?? string.Empty).Should().BeEmpty("the earlier dates are not restored");
        (cut.Find("#ks-to-date").GetAttribute("value") ?? string.Empty).Should().BeEmpty();
        SelectKeywordMode(cut);
        (SearchInput(cut).GetAttribute("value") ?? string.Empty).Should().BeEmpty("the earlier term is not restored");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("Keyword mode with an empty term cannot search");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-039 A2 — SC-10
    // -----------------------------------------------------------------------------------------

    // -----------------------------------------------------------------------------------------
    // CAS-039 A4 — SC-14 (filter bar resets on every open; reloaded results and header kept)
    // -----------------------------------------------------------------------------------------

    // CAS-048 §5a: rewritten — results appear in the explorer, and a deep link now selects the task's mode
    // (CAS-044 SC-3 amended for ?taskId=); a plain open keeps Files, so the user picks the task's mode.
    [Theory]
    [InlineData("date-only", false)]
    [InlineData("keyword", false)]
    [InlineData("keyword+date", false)]
    [InlineData("date-only", true)]   // ?taskId= deep link names the task instead of the latest one
    public void Test_A4_SC14_OpenAlwaysResetsFilterBarButKeepsReloadedResultsAndHeader(string shape, bool deepLink)
    {
        // Arrange — a Completed legacy KeywordSearch task of the given shape, opened as the latest task
        // (no query) or through ?taskId=.
        const int taskId = 740;
        var parameters = shape switch
        {
            "date-only" => new TaskSearchParameters
            {
                Term = string.Empty,
                Extension = "PDF",
                FromDate = new DateOnly(2025, 1, 1),
                ToDate = new DateOnly(2025, 1, 31),
            },
            "keyword" => new TaskSearchParameters { Term = "invoice", Extension = "PDF" },
            "keyword+date" => new TaskSearchParameters
            {
                Term = "invoice",
                Extension = "PDF",
                FromDate = new DateOnly(2025, 1, 1),
                ToDate = new DateOnly(2025, 1, 31),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var expectedHeader = shape switch
        {
            "date-only" => "Showing results for all files (PDF), created 01/01/2025 – 01/31/2025, submitted",
            "keyword" => "Showing results for \"invoice\" (PDF), submitted",
            _ => "Showing results for \"invoice\" (PDF), created 01/01/2025 – 01/31/2025, submitted",
        };
        // Legacy tasks (no mode): date-only opens as Date Search, every other shape as Keyword Search.
        var taskMode = shape == "date-only" ? "date" : "keyword";
        var task = TaskDetail(taskId) with { SearchParameters = parameters };

        _apiHandler.OnGetFilesBrowse = FileExplorer.BrowseFixtures.Serve;
        if (deepLink)
        {
            StubTasks(latest: TaskDetail(600, term: "latest-term", extension: "All"), task);
        }
        else
        {
            StubTasks(task);
        }

        _apiHandler.OnGetResults = (request, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            ResultsCompleted(request.RequestUri!.AbsolutePath.Contains($"/{taskId}/") ? taskId : 600, 3)));

        // Act — a deep link opens in the task's mode; a plain open stays in Files, so select the task's mode.
        var cut = RenderPageAt(deepLink ? taskId.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, selectKeywordMode: false);
        if (!deepLink)
        {
            cut.Find("#ks-mode-files").HasAttribute("checked").Should().BeTrue("a plain open starts in Files (CAS-044 SC-3)");
            cut.Find($"#ks-mode-{taskMode}").Change(taskMode);
        }

        cut.WaitForState(() => ExplorerHasRows(cut) && cut.FindAll(".cachet-ks-context-header").Count > 0, TimeSpan.FromSeconds(3));

        // Assert — filter bar defaults (A4 §2.6c), with the mode taken from the task on a deep link.
        AssertFilterBarDefaults(cut, taskMode);

        // Assert — the explorer shows that task's rows, and the header describes that task.
        ResultsPaths().Should().Equal($"/api/search/{taskId}/results");
        BrowsePaths().Should().OnlyContain(p => p == $"/api/search/{taskId}/browse");
        var header = cut.Find(".cachet-ks-context-header").TextContent;
        header.Should().Contain(expectedHeader);
        if (shape == "keyword")
        {
            header.Should().NotContain("created");
        }

        // Opening never resubmits.
        AnyRequest(HttpMethod.Post, "/search").Should().BeFalse();

        // Act 2 — the reset filter bar is usable: a Keyword search posts the typed term, null dates.
        var posted = CapturePostedSearch();
        cut.Find("#ks-mode-keyword").Change("keyword");
        SubmitSearch(cut, "contract");
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));

        posted.Request!.Term.Should().Be("contract");
        posted.Request.Extension.Should().Be("All");
        posted.Request.FromDate.Should().BeNull();
        posted.Request.ToDate.Should().BeNull();
        posted.RawJson.Should().Contain("\"fromDate\":null").And.Contain("\"toDate\":null");
    }

    /// <summary>CAS-039 A4 §2.6c: the filter bar's defaults — the term box and date fields empty and
    /// Extension "All". CAS-048 §2.4: a deep link selects the task's mode (<paramref name="mode"/>:
    /// "keyword", "filename" or "date"); a plain open keeps Keyword selected (the tests select it).</summary>
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
            (SearchInput(cut).GetAttribute("value") ?? string.Empty).Should().Be(string.Empty, "the term box is empty on every open (CAS-039 A4)");
            cut.FindAll("#ks-from-date").Should().BeEmpty();
            cut.FindAll("#ks-to-date").Should().BeEmpty();
        }

        cut.Find("#ks-extension").GetAttribute("value").Should().Be("All");
    }

    // =========================================================================================
    // CAS-044 — Filename / Content Matches panels page through all results (paging only).
    // The fake Api serves a full SearchResultsResponse per request: no query → page 1 (the
    // existing initial loads), ?page={p}&pageSize=50 → page p of BOTH lists; the screen must use
    // only the paged panel's list.
    // =========================================================================================

    // -----------------------------------------------------------------------------------------
    // CAS-044 SC-1
    // -----------------------------------------------------------------------------------------

    // -----------------------------------------------------------------------------------------
    // CAS-044 SC-2
    // -----------------------------------------------------------------------------------------

    // -----------------------------------------------------------------------------------------
    // CAS-044 SC-3
    // -----------------------------------------------------------------------------------------

    // -----------------------------------------------------------------------------------------
    // CAS-044 SC-4
    // -----------------------------------------------------------------------------------------

    // -----------------------------------------------------------------------------------------
    // CAS-044 SC-5
    // -----------------------------------------------------------------------------------------

    // -----------------------------------------------------------------------------------------
    // CAS-044 V11
    // -----------------------------------------------------------------------------------------

    // -----------------------------------------------------------------------------------------
    // Shared scenario helpers
    // -----------------------------------------------------------------------------------------

    // CAS-048: results are shown in the explorer (not panels). Renders the page, runs a search in the given
    // mode ("keyword" | "filename" | "date") that completes at once, and returns once the explorer root is
    // listed (folders Alpha and Beta from BrowseFixtures.ServeResults).
    private IRenderedComponent<Web.Components.Pages.KeywordSearch> CompleteASearch(string mode = "keyword", int taskId = 1, int total = 3)
    {
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse(taskId)));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(taskId, total)));

        StartSearch(cut, mode);
        cut.WaitForState(() => ExplorerHasRows(cut), PollWaitTimeout);
        // Let the poll cycle finish rendering before the test interacts (a late render would reset a checked row).
        cut.WaitForState(() => !cut.Markup.Contains("Search in progress"), PollWaitTimeout);

        return cut;
    }

    private static void StartSearch(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string mode, string term = "quarterly-report")
    {
        switch (mode)
        {
            case "date":
                cut.Find("#ks-mode-date").Change("date");
                EnterDates(cut, "01/01/2025", "01/31/2025");
                SearchButton(cut).Click();
                break;
            case "filename":
                cut.Find("#ks-mode-filename").Change("filename");
                SubmitSearch(cut, term);
                break;
            default:
                cut.Find("#ks-mode-keyword").Change("keyword");
                SubmitSearch(cut, term);
                break;
        }
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
    private static bool ExplorerHasRows(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.FindAll("#ks-send-to-quarantine").Count > 0 && cut.FindAll("#fe-root tr.fe-row").Count > 0;

    private static IReadOnlyList<string> ExplorerRowNames(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.FindAll("#fe-root tr.fe-row .fe-name").Select(n => n.TextContent).ToList();

    private static IElement ResultFolderRow(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string name) =>
        cut.FindAll("#fe-root tr.fe-row-folder").Single(r => r.QuerySelector(".fe-name")!.TextContent == name);

    private static IElement ResultFileRow(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string name) =>
        cut.FindAll("#fe-root tr.fe-row-file").Single(r => r.QuerySelector(".fe-name")!.TextContent == name);

    private static void WaitForCrumb(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string name) =>
        cut.WaitForAssertion(() => cut.Find("#fe-root .fe-crumb-current").TextContent.Should().Be(name), TimeSpan.FromSeconds(5));

    private static void OpenResultFolder(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string name)
    {
        ResultFolderRow(cut, name).Click();
        WaitForCrumb(cut, name);
    }

    private static void CheckResultFile(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string name, bool isChecked = true) =>
        ResultFileRow(cut, name).QuerySelector("input.fe-check")!.Change(isChecked);

    private static IElement SendButton(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.Find("#ks-send-to-quarantine");

    private IReadOnlyList<string> BrowsePaths() =>
        _apiHandler.Requests.ToList()
            .Where(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.StartsWith("/api/search/", StringComparison.Ordinal) && r.RequestUri.AbsolutePath.EndsWith("/browse", StringComparison.Ordinal))
            .Select(r => r.RequestUri!.AbsolutePath)
            .ToList();

    // =========================================================================================
    // CAS-044 (Files landing mode) — SC-1..SC-3 and V1..V3. Files is the default mode on every
    // open; Keyword and Date Search restore the existing controls and result panels.
    // =========================================================================================

    private IRenderedComponent<Web.Components.Pages.KeywordSearch> RenderFilesModePage()
    {
        _apiHandler.OnGetFilesBrowse = FileExplorer.BrowseFixtures.Serve;
        _apiHandler.OnPostSearch ??= _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse()));
        var cut = RenderComponent<Web.Components.Pages.KeywordSearch>();
        cut.WaitForState(() => cut.FindAll("tr.fe-row").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    [Fact]
    public void Test_SC1_DefaultsToFilesModeWithExplorerVisible()
    {
        var cut = RenderFilesModePage();

        cut.FindAll("label.cachet-ks-mode-option span").Select(s => s.TextContent.Trim())
            .Should().Equal("Files", "Filename Search", "Keyword Search", "Date Search");
        cut.Find("#ks-mode-files").HasAttribute("checked").Should().BeTrue("Files is the default mode");
        cut.Find("#ks-mode-keyword").HasAttribute("checked").Should().BeFalse();
        cut.Find("#ks-mode-date").HasAttribute("checked").Should().BeFalse();
        cut.Find("#fe-root").Should().NotBeNull("the explorer is shown instead of the result panels");
        cut.FindAll(".cachet-panels").Should().BeEmpty();
        cut.FindAll("#ks-from-date").Should().BeEmpty();
        cut.FindAll("tr.fe-row").Should().NotBeEmpty();
    }

    [Fact]
    public void Test_SC2_SearchModesRestoreControlsAndResultPanels()
    {
        var cut = RenderFilesModePage();

        cut.Find("#ks-mode-keyword").Change("keyword");
        cut.FindAll("#fe-root").Should().BeEmpty("the explorer is hidden outside Files mode");
        cut.FindAll(".cachet-panels").Should().BeEmpty("the Quarantine panel shows only alongside displayed results");
        cut.Find("#ks-term").Should().NotBeNull();
        cut.Find(".cachet-toolbar").ClassList.Should().NotContain("is-files-mode");

        cut.Find("#ks-mode-date").Change("date");
        cut.FindAll("#fe-root").Should().BeEmpty();
        cut.FindAll(".cachet-panels").Should().BeEmpty();
        cut.Find("#ks-from-date").Should().NotBeNull();
        cut.Find("#ks-to-date").Should().NotBeNull();

        cut.Find("#ks-mode-files").Change("files");
        cut.Find("#fe-root").Should().NotBeNull("switching back to Files shows the explorer again");
        cut.FindAll(".cachet-panels").Should().BeEmpty();
    }

    [Fact]
    public void Test_SC3_EveryOpenStartsInFilesMode()
    {
        // A first open, then a second fresh open after the first page switched to Keyword Search.
        var first = RenderFilesModePage();
        first.Find("#ks-mode-files").HasAttribute("checked").Should().BeTrue();
        first.Find("#ks-mode-keyword").Change("keyword");
        first.Find("#ks-mode-keyword").HasAttribute("checked").Should().BeTrue();

        var second = RenderFilesModePage();

        second.Find("#ks-mode-files").HasAttribute("checked").Should().BeTrue("nothing is remembered between opens");
        second.FindAll(".cachet-panels").Should().BeEmpty();
        second.Find(".cachet-toolbar").ClassList.Should().Contain("is-files-mode");
    }

    [Fact]
    public void Test_V1_FilesModeDisablesAllSearchControls()
    {
        var cut = RenderFilesModePage();

        cut.Find(".cachet-toolbar").ClassList.Should().Contain("is-files-mode");
        cut.Find("#ks-term").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#ks-extension").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#ks-bucket").HasAttribute("disabled").Should().BeTrue();
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("Search is not available in Files mode");

        // A forced click on the disabled Search button submits nothing.
        SearchButton(cut).Click();
        _apiHandler.Requests.ToList().Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public void Test_V2_SwitchingToSearchModeEnablesControlsUnderExistingRules()
    {
        var cut = RenderFilesModePage();

        cut.Find("#ks-mode-keyword").Change("keyword");
        cut.Find("#ks-term").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#ks-extension").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#ks-bucket").HasAttribute("disabled").Should().BeTrue("the bucket selector is read-only in every mode");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("Keyword mode still needs a valid term (CAS-039 V1)");

        SearchInput(cut).Input("invoice");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse();

        // Date mode: no term, valid date range required.
        cut.Find("#ks-mode-date").Change("date");
        cut.Find("#ks-extension").HasAttribute("disabled").Should().BeFalse();
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("Date mode needs a valid date range");
        EnterDates(cut, "01/01/2025", "01/31/2025");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Test_V3_SwitchingToFilesKeepsNoStaleDatePosted()
    {
        var cut = RenderFilesModePage();
        var posted = CapturePostedSearch();

        cut.Find("#ks-mode-date").Change("date");
        EnterDates(cut, "01/01/2025", "01/31/2025");

        cut.Find("#ks-mode-files").Change("files");
        cut.FindAll("#ks-from-date").Should().BeEmpty();
        cut.FindAll(".cachet-ks-date-hint").Should().BeEmpty();

        cut.Find("#ks-mode-keyword").Change("keyword");
        cut.FindAll("#ks-from-date").Should().BeEmpty();
        SubmitSearch(cut, "quarterly-report");
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));

        posted.Request!.Term.Should().Be("quarterly-report");
        posted.Request.FromDate.Should().BeNull("a date typed before switching to Files is never sent");
        posted.Request.ToDate.Should().BeNull();

        // Date mode re-entered after Files shows empty date fields.
        cut.Find("#ks-mode-files").Change("files");
        cut.Find("#ks-mode-date").Change("date");
        (cut.Find("#ks-from-date").GetAttribute("value") ?? string.Empty).Should().BeEmpty();
        (cut.Find("#ks-to-date").GetAttribute("value") ?? string.Empty).Should().BeEmpty();
    }

    // =========================================================================================
    // CAS-048 — Filename / Keyword / Date Search with explorer-style results (Web).
    // =========================================================================================

    private static IElement FolderCheckbox(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string folderName) =>
        ResultFolderRow(cut, folderName).QuerySelector("input.fe-check")!;

    private const string SelectionTooLargeText = "Too many files selected (limit 1,000); narrow the selection.";

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC1_FourModesFilesDefault()
    {
        var cut = RenderFilesModePage();

        cut.FindAll("label.cachet-ks-mode-option span").Select(s => s.TextContent.Trim())
            .Should().Equal("Files", "Filename Search", "Keyword Search", "Date Search");
        cut.FindAll("input[name=ks-mode]").Select(i => i.Id)
            .Should().Equal("ks-mode-files", "ks-mode-filename", "ks-mode-keyword", "ks-mode-date");
        cut.Find("#ks-mode-files").HasAttribute("checked").Should().BeTrue("Files is checked on a plain open");
        cut.Find("#ks-mode-filename").HasAttribute("checked").Should().BeFalse();
        cut.Find("#ks-mode-keyword").HasAttribute("checked").Should().BeFalse();
        cut.Find("#ks-mode-date").HasAttribute("checked").Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-2
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("filename", "Filename")]
    [InlineData("keyword", "Keyword")]
    public void Test_SC2_FilenameAndKeywordMinThreeCharacters(string radio, string expectedMode)
    {
        var cut = RenderPage();
        var posted = CapturePostedSearch();
        cut.Find($"#ks-mode-{radio}").Change(radio);

        SearchInput(cut).Input("ab");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("2 characters keep Search disabled");
        cut.Find(".cachet-input-hint").TextContent.Trim().Should().Be("Enter at least 3 characters");

        SearchInput(cut).Input("  ab  ");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("the term is trimmed before it is counted");

        SearchInput(cut).Input("abc");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse("3 characters enable Search");
        cut.FindAll(".cachet-input-hint").Should().BeEmpty();

        SearchButton(cut).Click();
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));

        posted.Request!.Mode.Should().Be(expectedMode);
        posted.Request.Term.Should().Be("abc");
        posted.RawJson.Should().Contain($"\"mode\":\"{expectedMode}\"");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC3_KeywordAndDateModesPost()
    {
        var cut = RenderPage();
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(1, 3)));
        var posted = CapturePostedSearch();

        SubmitSearch(cut, "quarterly-report");
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));
        posted.Request!.Mode.Should().Be("Keyword");
        posted.Request.Term.Should().Be("quarterly-report");
        posted.RawJson.Should().Contain("\"mode\":\"Keyword\"");

        // Search is re-enabled once the first task completes.
        cut.WaitForState(() => ExplorerHasRows(cut) && !cut.Markup.Contains("Search in progress"), PollWaitTimeout);
        posted.Request = null;
        posted.RawJson = null;

        SelectDateMode(cut);
        cut.WaitForState(() => cut.FindAll("#ks-from-date").Count > 0, TimeSpan.FromSeconds(3));
        EnterDates(cut, "01/01/2025", "01/31/2025");
        SearchButton(cut).Click();
        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));

        posted.Request!.Mode.Should().Be("Date");
        posted.Request.FromDate.Should().Be(new DateOnly(2025, 1, 1));
        posted.Request.ToDate.Should().Be(new DateOnly(2025, 1, 31));
        posted.RawJson.Should().Contain("\"mode\":\"Date\"");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-4
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("filename")]
    [InlineData("keyword")]
    [InlineData("date")]
    public void Test_SC4_ResultsShownInExplorerNoPanels(string mode)
    {
        var cut = CompleteASearch(mode);

        // The explorer's top level lists exactly the folders the browse response returned.
        ExplorerRowNames(cut).Should().Equal("Alpha", "Beta");
        cut.FindAll("#fe-root tr.fe-row-folder").Should().HaveCount(2);
        cut.FindAll("#fe-root tr.fe-row-file").Should().BeEmpty();

        // The two match panels no longer exist in any mode; only the Quarantine panel remains.
        cut.Markup.Should().NotContain("Filename Matches").And.NotContain("Content Matches");
        cut.FindAll(".cachet-ks-panel").Should().ContainSingle().Which.ClassList.Should().Contain("cachet-ks-quarantine-panel");

        // The root was read once through GET search/{taskId}/browse with no folder.
        _apiHandler.Requests.ToList()
            .Where(r => r.RequestUri!.AbsolutePath == "/api/search/1/browse")
            .Select(r => r.RequestUri!.Query)
            .Should().ContainSingle().Which.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC6_SelectionPersistsAcrossFoldersAndSendsToQuarantine()
    {
        var cut = CompleteASearch();

        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");
        cut.Find("#fe-up").Click();
        WaitForCrumb(cut, "Archive");

        OpenResultFolder(cut, "Beta");
        ResultFileRow(cut, "b1.pdf").QuerySelector("input.fe-check")!.HasAttribute("checked").Should().BeFalse();
        CheckResultFile(cut, "b1.pdf");
        SendButton(cut).TextContent.Should().Contain("Send to Quarantine (2)");

        // Navigating back keeps the first folder's selection.
        cut.Find("#fe-up").Click();
        WaitForCrumb(cut, "Archive");
        OpenResultFolder(cut, "Alpha");
        ResultFileRow(cut, "a1.pdf").QuerySelector("input.fe-check")!.HasAttribute("checked").Should().BeTrue("selection persists across navigation");
        SendButton(cut).TextContent.Should().Contain("Send to Quarantine (2)");

        QuarantineAddRequest? captured = null;
        _apiHandler.OnPostQuarantineAdd = async request =>
        {
            captured = await request.Content!.ReadFromJsonAsync<QuarantineAddRequest>();
            return JsonResponse(HttpStatusCode.OK, new QuarantineAddResponse
            {
                Items = new[]
                {
                    new QuarantineAddResponseItem { FileId = 901, Name = "a1.pdf", Path = "Alpha/a1.pdf", QuarantinedOn = DateTimeOffset.UtcNow, Status = "Flagged" },
                    new QuarantineAddResponseItem { FileId = 902, Name = "b1.pdf", Path = "Beta/b1.pdf", QuarantinedOn = DateTimeOffset.UtcNow, Status = "Flagged" },
                },
            });
        };
        var refreshCalls = 0;
        _apiHandler.OnGetQuarantine = _ =>
        {
            refreshCalls++;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
            {
                TotalCount = 0,
                Page = 1,
                PageSize = 25,
                Items = new[] { SampleQuarantineItem(901, "a1.pdf"), SampleQuarantineItem(902, "b1.pdf") },
            }));
        };

        SendButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Quarantined On"), TimeSpan.FromSeconds(3));

        captured.Should().NotBeNull();
        captured!.TaskId.Should().Be(1);
        captured.FileIds.Should().BeEquivalentTo(new[] { 9001, 9003 });
        refreshCalls.Should().Be(1, "the Quarantine panel is refreshed after the send");
        SendButton(cut).TextContent.Should().Contain("Send to Quarantine (0)", "the selection is cleared after a successful send");
        SendButton(cut).HasAttribute("disabled").Should().BeTrue();
        ResultFileRow(cut, "a1.pdf").QuerySelector("input.fe-check")!.HasAttribute("checked").Should().BeFalse();
        cut.Find(".cachet-ks-toast").TextContent.Should().Contain("2 file(s) sent to Quarantine.");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC7_QuarantinePanelUnchanged()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 2,
            Page = 1,
            PageSize = 25,
            Items = new[] { SampleQuarantineItem(31, "q-one.pdf"), SampleQuarantineItem(32, "q-two.pdf", "Needs Review") },
        }));

        var cut = RenderPageWithResults();

        var panel = cut.Find(".cachet-ks-quarantine-panel");
        panel.QuerySelector("h2")!.TextContent.Should().StartWith("Quarantine");
        panel.QuerySelector(".cachet-panel-count")!.TextContent.Should().Be("2");
        panel.QuerySelectorAll("thead th").Select(h => h.TextContent.Trim())
            .Should().Equal("Name / Path", "Source Tab", "Quarantined On", "Status");
        panel.QuerySelectorAll("tbody tr").Should().HaveCount(2);
        panel.QuerySelectorAll("tbody tr").Select(r => r.QuerySelectorAll("td")[1].TextContent).Should().AllBe("Keyword Search");

        // Still read-only: no row checkboxes, no bulk action.
        panel.QuerySelectorAll("input[type=checkbox]").Should().BeEmpty();
        panel.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().NotContain(t => t.Contains("Send to Quarantine") || t.Contains("Delete"));

        // A name click still opens the viewer in the Quarantine context.
        panel.QuerySelector("button.cachet-file-link")!.Click();
        var viewer = cut.FindComponent<Web.Components.Shared.FileViewerModal>();
        viewer.Instance.FileIndexId.Should().Be(500_031);
        viewer.Instance.Context.Should().Be("Quarantine");

        // The collapse toggle still works.
        cut.Find(".cachet-ks-quarantine-panel .cachet-panel-toggle").Click();
        cut.Find(".cachet-ks-quarantine-panel").ClassList.Should().Contain("is-collapsed");

        // CAS-052 §2.6: empty copy now also names the Files browser.
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse { TotalCount = 0, Page = 1, PageSize = 25, Items = Array.Empty<QuarantineListItem>() }));
        var empty = RenderPageWithResults();
        System.Text.RegularExpressions.Regex.Replace(empty.Markup, @"\s+", " ")
            .Should().Contain("Quarantine is empty. Files sent to quarantine from Keyword Search or the Files browser appear here for final");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC8_ResultsAndSelectionBoundToMode()
    {
        var cut = CompleteASearch("keyword");
        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");
        SendButton(cut).TextContent.Should().Contain("(1)");

        // Switching mode hides the old results (no stale rows) and clears the selection.
        cut.Find("#ks-mode-filename").Change("filename");
        cut.FindAll("#fe-root").Should().BeEmpty();
        cut.FindAll("#ks-send-to-quarantine").Should().BeEmpty();
        cut.Find(".cachet-ks-results-prompt").TextContent.Should().Be("Enter a search…");

        // Switching back shows that mode's own results again, with nothing selected.
        cut.Find("#ks-mode-keyword").Change("keyword");
        cut.WaitForState(() => ExplorerHasRows(cut), TimeSpan.FromSeconds(3));
        SendButton(cut).TextContent.Should().Contain("(0)");

        // Starting a new search hides the old results while it runs and clears the selection.
        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");
        SendButton(cut).TextContent.Should().Contain("(1)");
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse(2)));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        SubmitSearch(cut, "second-term");
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(3));
        cut.FindAll("#fe-root").Should().BeEmpty("the old results are hidden while the new search runs");
        cut.FindAll("#ks-send-to-quarantine").Should().BeEmpty();

        // The new task's explorer starts with an empty selection.
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(2, 3)));
        cut.WaitForState(() => ExplorerHasRows(cut) && BrowsePaths().Contains("/api/search/2/browse"), PollWaitTimeout);
        SendButton(cut).TextContent.Should().Contain("(0)");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-13 (page level: folder checkbox, distinct count, send, over-cap)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC13_FolderCheckboxSelectsDescendantMatches()
    {
        var cut = CompleteASearch();

        // Over-cap: the Api answers 400 selection-too-large — the message shows and nothing is selected.
        _apiHandler.OnGetSearchResultIds = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.BadRequest, new
        {
            type = "https://cachet.tracecounts.internal/errors/selection-too-large",
            title = "Selection too large.",
            status = 400,
            limit = 1000,
        }));
        FolderCheckbox(cut, "Alpha").Change(true);
        cut.Find("#fe-root .fe-selection-message").TextContent.Should().Be(SelectionTooLargeText);
        SendButton(cut).TextContent.Should().Contain("(0)");
        FolderCheckbox(cut, "Alpha").HasAttribute("checked").Should().BeFalse("an over-cap folder selects nothing");

        // The Api recovers; a file already ticked is counted once, not twice.
        _apiHandler.OnGetSearchResultIds = FileExplorer.BrowseFixtures.ServeResultIds;
        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");
        cut.Find("#fe-up").Click();
        WaitForCrumb(cut, "Archive");

        FolderCheckbox(cut, "Alpha").Change(true);

        // Every matched file beneath Alpha (all depths: a1.pdf and Alpha/Sub/a2.pdf) is selected; N counts distinct files.
        SendButton(cut).TextContent.Should().Contain("Send to Quarantine (2)");
        FolderCheckbox(cut, "Alpha").GetAttribute("data-state").Should().Be("checked");
        FolderCheckbox(cut, "Beta").GetAttribute("data-state").Should().Be("unchecked");
        cut.FindAll("#fe-root .fe-selection-message").Should().BeEmpty();
        _apiHandler.Requests.ToList()
            .Where(r => r.RequestUri!.AbsolutePath == "/api/search/1/browse/result-ids")
            .Select(r => r.RequestUri!.Query)
            .Should().Contain("?folder=Alpha");

        // The header checkbox covers the current folder (files and folders): Beta is still unticked, so it is mixed.
        cut.Find("#fe-root input.fe-check-all").GetAttribute("data-state").Should().Be("mixed");
        FolderCheckbox(cut, "Beta").Change(true);
        SendButton(cut).TextContent.Should().Contain("(3)");
        cut.Find("#fe-root input.fe-check-all").GetAttribute("data-state").Should().Be("checked");

        // Unticking one file inside Alpha leaves the (resolved) Alpha folder indeterminate.
        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf", isChecked: false);
        SendButton(cut).TextContent.Should().Contain("(2)");
        cut.Find("#fe-up").Click();
        WaitForCrumb(cut, "Archive");
        FolderCheckbox(cut, "Alpha").GetAttribute("data-state").Should().Be("mixed");

        // Send posts the distinct searchResultIds of the files that are still selected.
        QuarantineAddRequest? captured = null;
        _apiHandler.OnPostQuarantineAdd = async request =>
        {
            captured = await request.Content!.ReadFromJsonAsync<QuarantineAddRequest>();
            return JsonResponse(HttpStatusCode.OK, new QuarantineAddResponse { Items = Array.Empty<QuarantineAddResponseItem>() });
        };
        SendButton(cut).Click();
        cut.WaitForState(() => captured is not null, TimeSpan.FromSeconds(3));
        captured!.TaskId.Should().Be(1);
        captured.FileIds.Should().BeEquivalentTo(new[] { 9002, 9003 });
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 V1..V3, V6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V1_SearchEnabledRulesPerMode()
    {
        var cut = RenderPage();

        foreach (var radio in new[] { "filename", "keyword" })
        {
            cut.Find($"#ks-mode-{radio}").Change(radio);

            SearchInput(cut).Input(string.Empty);
            SearchButton(cut).HasAttribute("disabled").Should().BeTrue($"{radio}: empty term");
            cut.FindAll(".cachet-input-hint").Should().BeEmpty();

            SearchInput(cut).Input("   ");
            SearchButton(cut).HasAttribute("disabled").Should().BeTrue($"{radio}: blank term");

            SearchInput(cut).Input("ab");
            SearchButton(cut).HasAttribute("disabled").Should().BeTrue($"{radio}: 2 characters");
            cut.Find(".cachet-input-hint").TextContent.Trim().Should().Be("Enter at least 3 characters");

            SearchInput(cut).Input("  ab  ");
            SearchButton(cut).HasAttribute("disabled").Should().BeTrue($"{radio}: trimmed to 2 characters");

            SearchInput(cut).Input("abc");
            SearchButton(cut).HasAttribute("disabled").Should().BeFalse($"{radio}: 3 characters");
            cut.FindAll(".cachet-input-hint").Should().BeEmpty();

            SearchInput(cut).Input("  abc  ");
            SearchButton(cut).HasAttribute("disabled").Should().BeFalse($"{radio}: trimmed to 3 characters");

            SearchInput(cut).Input(new string('x', 200));
            SearchButton(cut).HasAttribute("disabled").Should().BeFalse($"{radio}: 200 characters");

            SearchInput(cut).Input(new string('x', 201));
            SearchButton(cut).HasAttribute("disabled").Should().BeTrue($"{radio}: 201 characters");
            cut.Find(".cachet-input-hint").TextContent.Trim().Should().Be("Search term must be at most 200 characters.");
        }

        // Date mode has no term rule: only a valid date range enables Search.
        SelectDateMode(cut);
        cut.FindAll("#ks-term").Should().BeEmpty();
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("no dates entered");
        EnterDates(cut, "01/01/2025", "01/31/2025");
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse("a valid range enables Date Search");
    }

    [Fact]
    public void Test_V2_FilesStillDisablesControls()
    {
        var cut = RenderFilesModePage();

        cut.Find("#ks-term").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#ks-extension").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#ks-bucket").HasAttribute("disabled").Should().BeTrue();
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue();
        SearchButton(cut).Click();
        _apiHandler.Requests.ToList().Should().NotContain(r => r.Method == HttpMethod.Post, "a forced click submits nothing in Files mode");

        // Leaving Files enables the controls; returning disables them again.
        cut.Find("#ks-mode-filename").Change("filename");
        cut.Find("#ks-term").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#ks-extension").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#ks-mode-files").Change("files");
        cut.Find("#ks-term").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#ks-extension").HasAttribute("disabled").Should().BeTrue();
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Theory]
    [InlineData("filename", "Filename")]
    [InlineData("keyword", "Keyword")]
    [InlineData("date", "Date")]
    public void Test_V3_PostedModeMatchesSelectedRadio(string radio, string expectedMode)
    {
        var cut = RenderPage();
        var posted = CapturePostedSearch();

        // Dates typed in Date mode first, so a stale date would be visible if it leaked into another mode.
        SelectDateMode(cut);
        EnterDates(cut, "01/01/2025", "01/31/2025");
        if (radio == "date")
        {
            SearchButton(cut).Click();
        }
        else
        {
            cut.Find($"#ks-mode-{radio}").Change(radio);
            SubmitSearch(cut, "quarterly-report");
        }

        cut.WaitForState(() => posted.Request is not null, TimeSpan.FromSeconds(3));

        posted.Request!.Mode.Should().Be(expectedMode, "the posted mode always matches the selected radio");
        posted.RawJson.Should().Contain($"\"mode\":\"{expectedMode}\"");
        if (radio == "date")
        {
            posted.Request.FromDate.Should().Be(new DateOnly(2025, 1, 1));
            posted.Request.ToDate.Should().Be(new DateOnly(2025, 1, 31));
        }
        else
        {
            posted.Request.FromDate.Should().BeNull($"{radio} never posts dates");
            posted.Request.ToDate.Should().BeNull();
            posted.RawJson.Should().Contain("\"fromDate\":null").And.Contain("\"toDate\":null");
        }
    }

    [Fact]
    public void Test_V6_QuarantineSendsBrowseIdsOnly()
    {
        // Alpha holds one file with a searchResultId and one without (it cannot be selected).
        _apiHandler.OnGetSearchBrowse = (request, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            FileExplorer.BrowseFixtures.Query(request).folder == "Alpha"
                ? FileExplorer.BrowseFixtures.Listing(
                    "Alpha",
                    null,
                    new[] { FileExplorer.BrowseFixtures.ResultFile(201, 9001, "a1.pdf"), FileExplorer.BrowseFixtures.File(202, "no-result-id.pdf") })
                : FileExplorer.BrowseFixtures.ResultsRoot()));
        var cut = CompleteASearch(taskId: 7);
        OpenResultFolder(cut, "Alpha");

        ResultFileRow(cut, "no-result-id.pdf").QuerySelector("input.fe-check").Should().BeNull("a row without a searchResultId has nothing to send");
        CheckResultFile(cut, "a1.pdf");

        QuarantineAddRequest? captured = null;
        string? rawBody = null;
        _apiHandler.OnPostQuarantineAdd = async request =>
        {
            rawBody = await request.Content!.ReadAsStringAsync();
            captured = System.Text.Json.JsonSerializer.Deserialize<QuarantineAddRequest>(rawBody, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            return JsonResponse(HttpStatusCode.OK, new QuarantineAddResponse { Items = Array.Empty<QuarantineAddResponseItem>() });
        };

        SendButton(cut).Click();
        cut.WaitForState(() => captured is not null, TimeSpan.FromSeconds(3));

        captured!.TaskId.Should().Be(7, "the current task id is sent");
        captured.FileIds.Should().Equal(9001);
        rawBody.Should().NotContain("201").And.NotContain("202", "file index ids and unlisted ids are never sent");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-048 SC-14 (page hosting): the badge hosts the modal and the button is enabled in Files mode.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC14_TaskManagerButtonHostedByBadgeOnKeywordSearchPage()
    {
        var cut = RenderFilesModePage();

        var badge = cut.FindComponent<Web.Components.Layout.TaskManagerBadge>();
        var button = badge.Find("button");
        button.TextContent.Should().Contain("Task Manager");
        button.HasAttribute("disabled").Should().BeFalse("the button is enabled in Files mode");
        button.HasAttribute("title").Should().BeFalse("the 'Coming soon' title is gone");
        badge.FindComponents<Web.Components.Shared.TaskManagerModal>().Should().ContainSingle("the badge hosts the modal itself");

        // CAS-031 / CAS-032 / CAS-024 toolbar elements are still in place beside it.
        cut.Find(".cachet-al-trigger-btn").TextContent.Trim().Should().Be("Audit Log");
        cut.Find(".cachet-ks-quarantine-trigger-btn").TextContent.Should().Contain("Quarantine Queue");

        button.Click();
        cut.WaitForState(() => cut.FindAll(".cachet-tm-modal").Count > 0, TimeSpan.FromSeconds(3));
        _apiHandler.Requests.ToList().Should().Contain(r => r.Method == HttpMethod.Get
            && r.RequestUri!.AbsolutePath == "/api/tasks" && r.RequestUri.Query == "?pageSize=100");
    }

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

    // =========================================================================================
    // CAS-051 — results-mode permission gate (SC-9) and panel refresh from Files mode (SC-10).
    // =========================================================================================

    [Fact]
    public void Test_SC9_ResultsMode_ReadOnly_NoSend()
    {
        _authContext.SetClaims(new System.Security.Claims.Claim("b2_capability", "Read Only"));
        var cut = RenderPage();
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse(1)));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ResultsCompleted(1, 3)));

        StartSearch(cut, "keyword");
        cut.WaitForState(() => cut.FindAll("#fe-root tr.fe-row").Count > 0, PollWaitTimeout);
        cut.WaitForState(() => !cut.Markup.Contains("Search in progress"), PollWaitTimeout);

        ExplorerRowNames(cut).Should().Equal("Alpha", "Beta");
        cut.FindAll("#ks-send-to-quarantine").Should().BeEmpty("Send to Quarantine is hidden for Read Only");
        cut.FindAll("#fe-root input[type=checkbox]").Should().BeEmpty("results selection is disabled for Read Only");
        cut.FindAll("#fe-root input.fe-check-quarantine").Should().BeEmpty();
    }

    [Fact]
    public void Test_SC10_PanelRefreshed()
    {
        var quarantineReads = 0;
        var quarantined = false;
        _apiHandler.OnGetQuarantine = _ =>
        {
            quarantineReads++;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
            {
                TotalCount = quarantined ? 1 : 0,
                Page = 1,
                PageSize = 25,
                Items = quarantined ? new[] { SampleQuarantineItem(901, "readme.txt") } : Array.Empty<QuarantineListItem>(),
            }));
        };
        _apiHandler.OnGetQuarantineBrowseState = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineBrowseStateResponse
        {
            FileIndexIds = quarantined ? new[] { 101 } : Array.Empty<int>(),
            Folders = Array.Empty<QuarantineBrowseStateFolder>(),
        }));
        _apiHandler.OnPostQuarantineBrowseAdd = _ =>
        {
            quarantined = true;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineBrowseAddResponse { Added = 1, AlreadyQuarantined = 0 }));
        };

        var cut = RenderFilesModePage();
        cut.WaitForState(() => cut.FindAll("tr.fe-row-file input.fe-check-quarantine").Count > 0, TimeSpan.FromSeconds(5));
        cut.Find(".cachet-ks-quarantine-trigger-btn .cachet-btn-count").TextContent.Trim().Should().Be("0");
        var readsBefore = quarantineReads;

        cut.Find("tr.fe-row-file input.fe-check-quarantine").Change(true);

        cut.WaitForAssertion(
            () => cut.Find(".cachet-ks-quarantine-trigger-btn .cachet-btn-count").TextContent.Trim().Should().Be("1"),
            TimeSpan.FromSeconds(5));
        quarantineReads.Should().Be(readsBefore + 1, "the Quarantine list/badge is re-read after the Files-mode action");

        // The modal opened from the trigger shows the newly quarantined file.
        cut.Find(".cachet-ks-quarantine-trigger-btn").Click();
        cut.WaitForState(() => cut.Markup.Contains("readme.txt") && cut.Markup.Contains("cachet-qq-modal"), TimeSpan.FromSeconds(5));
    }

    // =========================================================================================
    // CAS-057 — "Cancel search" in the Keyword Search progress bar (SC-1..SC-6, V1..V8; V9/V10 are in
    // KeywordSearchDeepLinkTests). The fake Api answers GET search/{id}/results with 409 until a test
    // publishes a terminal result via _terminalResult, so every state transition is test-driven (no
    // timing guesses); the page's own PeriodicTimer only supplies the tick.
    // =========================================================================================

    private const string CancellingText = "Cancelling search — stopping and gathering the matches found so far…";
    private const string CancelBtn = ".cachet-ks-cancel-btn";
    private const string CancelStop = ".cachet-ks-cancel-stop";
    private const string CancelKeep = ".cachet-ks-cancel-keep";
    private const string CancelConfirm = ".cachet-ks-cancel-confirm";

    private int _nextTaskId;
    private volatile SearchResultsResponse? _terminalResult;
    private HttpStatusCode _cancelStatus = HttpStatusCode.Accepted;

    private static SearchResultsResponse ResultsCancelled(int taskId, int matches, int done = 40, int total = 120) => new()
    {
        TaskId = taskId,
        Status = "Cancelled",
        IsPartial = true,
        ItemsProcessed = done,
        ItemsTotal = total,
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

    /// <summary>POST /search returns the next task id; GET results is 409 until <c>_terminalResult</c> is set;
    /// POST cancel answers <c>_cancelStatus</c> (200 / 202 with the CAS-056 body, anything else bare).</summary>
    private void ArrangeCancellableSearch()
    {
        _apiHandler.OnPostSearch = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, SubmittedResponse(++_nextTaskId)));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(_terminalResult is { } terminal
            ? JsonResponse(HttpStatusCode.OK, terminal)
            : new HttpResponseMessage(HttpStatusCode.Conflict));
        _apiHandler.OnPostCancel = (_, id) => Task.FromResult(_cancelStatus switch
        {
            HttpStatusCode.OK => JsonResponse(HttpStatusCode.OK, new { taskId = id, status = "Cancelled" }),
            HttpStatusCode.Accepted => JsonResponse(HttpStatusCode.Accepted, new { taskId = id, status = "Cancelling" }),
            var other => new HttpResponseMessage(other),
        });
    }

    private IRenderedComponent<Web.Components.Pages.KeywordSearch> RenderRunningSearch(string mode = "keyword")
    {
        var cut = RenderPage();
        ArrangeCancellableSearch();
        StartSearch(cut, mode);
        cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));
        return cut;
    }

    // A mode change dispatched while the page is still rendering the previous search's results can be dropped by
    // bUnit's synchronous event dispatch; retry until the radio reports the mode (the Change is idempotent).
    private static void EnsureMode(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, string radio) =>
        cut.WaitForAssertion(() =>
        {
            cut.Find($"#ks-mode-{radio}").Change(radio);
            cut.Find($"#ks-mode-{radio}").HasAttribute("checked").Should().BeTrue();
        }, TimeSpan.FromSeconds(5));

    private static void ClickCancelThenStop(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut)
    {
        cut.Find(CancelBtn).Click();
        cut.Find(CancelStop).Click();
    }

    private void WaitForCancelPosts(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut, int count) =>
        cut.WaitForAssertion(() => _apiHandler.CancelledTaskIds.ToList().Should().HaveCount(count), TimeSpan.FromSeconds(5));

    private static void WaitForCancelling(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.WaitForState(() => cut.Markup.Contains(CancellingText), TimeSpan.FromSeconds(5));

    private static string Notice(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut) =>
        cut.Find(".cachet-ks-notice").TextContent;

    private const string PartialNotice =
        "Search cancelled — 40 of 120 items were checked. Showing the 3 matches found so far; these results are incomplete.";

    private const string NoMatchNotice = "Search cancelled — 40 of 120 items were checked; no matches were found.";

    [Fact]
    public void Test_SC1_ButtonVisible()
    {
        var cut = RenderRunningSearch();

        var button = cut.Find(".cachet-ks-progress-banner " + CancelBtn);
        button.TextContent.Trim().Should().Be("Cancel search");
        button.GetAttribute("aria-label").Should().Be("Cancel search", "the control has an accessible name (§2.10)");
        button.GetAttribute("type").Should().Be("button");
        cut.Find(".cachet-ks-progress-banner").TextContent.Should().Contain("Search in progress — running for", "the bar text itself is unchanged");
        cut.FindAll(CancelConfirm).Should().BeEmpty("the confirm is closed until the button is clicked");
    }

    [Fact]
    public async Task Test_SC2_ConfirmThenCancelling()
    {
        // A 30 s base interval makes the 2 s cancelling cadence observable: with the base interval a second
        // GET results could not arrive within the wait below.
        _pollingOptions.TaskManagerPollIntervalSeconds = 30;
        var cut = RenderRunningSearch();

        ClickCancelThenStop(cut);
        WaitForCancelling(cut);

        _apiHandler.CancelledTaskIds.ToList().Should().Equal(1);
        cut.FindAll(CancelBtn).Should().BeEmpty("the button is gone while cancelling");
        cut.Find(".cachet-ks-progress-banner").TextContent.Should().Contain(CancellingText);

        var callsAtCancel = _apiHandler.ResultsCallCount;
        await WaitUntilAsync(() => _apiHandler.ResultsCallCount >= callsAtCancel + 2, TimeSpan.FromSeconds(10));
        cut.Markup.Should().Contain(CancellingText, "the 409 ticks keep the Cancelling state");
        _apiHandler.CancelledTaskIds.ToList().Should().Equal(1);
    }

    [Fact]
    public void Test_SC3_PartialResultsAndQuarantine()
    {
        var cut = RenderRunningSearch();
        ClickCancelThenStop(cut);
        WaitForCancelling(cut);

        _terminalResult = ResultsCancelled(1, matches: 3);
        cut.WaitForState(() => ExplorerHasRows(cut) && cut.FindAll(".cachet-ks-notice").Count > 0, PollWaitTimeout);

        Notice(cut).Should().Be(PartialNotice);
        ExplorerRowNames(cut).Should().Equal("Alpha", "Beta");
        cut.Markup.Should().NotContain("Search in progress").And.NotContain(CancellingText);

        // Quarantine selection works exactly as for a completed search.
        QuarantineAddRequest? captured = null;
        _apiHandler.OnPostQuarantineAdd = async request =>
        {
            captured = await request.Content!.ReadFromJsonAsync<QuarantineAddRequest>();
            return JsonResponse(HttpStatusCode.OK, new QuarantineAddResponse
            {
                Items = new[]
                {
                    new QuarantineAddResponseItem
                    {
                        FileId = 900,
                        Name = "a1.pdf",
                        Path = "/o-drive/Alpha/a1.pdf",
                        QuarantinedOn = DateTimeOffset.UtcNow,
                        Status = "Flagged",
                    },
                },
            });
        };

        OpenResultFolder(cut, "Alpha");
        CheckResultFile(cut, "a1.pdf");
        SendButton(cut).TextContent.Should().Contain("Send to Quarantine (1)");
        SendButton(cut).Click();

        cut.WaitForState(() => captured is not null, TimeSpan.FromSeconds(5));
        captured!.TaskId.Should().Be(1);
        captured.FileIds.Should().Equal(9001);
    }

    [Fact]
    public void Test_SC4_CancelledNoMatches()
    {
        var cut = RenderRunningSearch();
        ClickCancelThenStop(cut);
        WaitForCancelling(cut);

        _terminalResult = ResultsCancelled(1, matches: 0);
        cut.WaitForState(() => cut.FindAll(".cachet-ks-notice").Count > 0, PollWaitTimeout);

        Notice(cut).Should().Be(NoMatchNotice);
        cut.FindAll("#fe-root").Should().BeEmpty("no results panel for a cancelled search without matches");
        cut.FindAll("#ks-send-to-quarantine").Should().BeEmpty();
        cut.FindAll(".cachet-ks-context-header").Should().BeEmpty();
        cut.Markup.Should().NotContain("Search in progress").And.NotContain("did not complete successfully");
    }

    [Fact]
    public void Test_SC5_SearchAvailableAgain()
    {
        var cut = RenderRunningSearch();
        ClickCancelThenStop(cut);
        WaitForCancelling(cut);
        _terminalResult = ResultsCancelled(1, matches: 3);
        cut.WaitForState(() => ExplorerHasRows(cut) && cut.FindAll(".cachet-ks-notice").Count > 0, PollWaitTimeout);

        SubmitFollowUpSearchesAndAssert(cut);
    }

    [Fact]
    public void Test_SC6_LateCancelAndConflict()
    {
        // Part A — 409 task-not-cancellable: the search just finished; the next tick renders Completed.
        _cancelStatus = HttpStatusCode.Conflict;
        var cut = RenderRunningSearch();
        ClickCancelThenStop(cut);
        WaitForCancelPosts(cut, 1);
        cut.WaitForState(() => cut.FindAll(".cachet-ks-toast").Count > 0, TimeSpan.FromSeconds(5));
        cut.Find(".cachet-ks-toast").TextContent.Should().Be("The search had already finished.");
        cut.FindAll(".cachet-ks-error-banner").Should().BeEmpty("a 409 is not an error");

        _terminalResult = ResultsCompleted(1, 3);
        cut.WaitForState(() => ExplorerHasRows(cut), PollWaitTimeout);
        cut.WaitForState(() => !cut.Markup.Contains("Search in progress"), PollWaitTimeout);
        ExplorerRowNames(cut).Should().Equal("Alpha", "Beta");
        cut.FindAll(".cachet-ks-notice").Should().BeEmpty("a completed search shows no cancelled notice");

        // Part B — the cancel is accepted (202) but the poll then returns Completed (late cancel).
        _terminalResult = null;
        _cancelStatus = HttpStatusCode.Accepted;
        StartSearch(cut, "keyword");
        cut.WaitForState(() => cut.FindAll(CancelBtn).Count > 0, TimeSpan.FromSeconds(5));
        ClickCancelThenStop(cut);
        WaitForCancelling(cut);
        _apiHandler.CancelledTaskIds.ToList().Should().Equal(1, 2);

        _terminalResult = ResultsCompleted(2, 3);
        cut.WaitForState(() => ExplorerHasRows(cut) && !cut.Markup.Contains(CancellingText), PollWaitTimeout);
        cut.Find(".cachet-ks-toast").TextContent.Should().Be("The search finished before it could be cancelled.");
        cut.FindAll(".cachet-ks-notice").Should().BeEmpty();
        cut.Markup.Should().NotContain("did not complete successfully");
    }

    [Fact]
    public void Test_V1_CancelButtonOnlyForRunningKeywordSearch()
    {
        var cut = RenderPage();
        ArrangeCancellableSearch();
        cut.FindAll(CancelBtn).Should().BeEmpty("idle: no search in flight");

        foreach (var mode in new[] { "filename", "date" })
        {
            _terminalResult = null;
            EnsureMode(cut, mode);
            StartSearch(cut, mode);
            cut.WaitForState(() => cut.Markup.Contains("Search in progress"), TimeSpan.FromSeconds(2));
            cut.FindAll(".cachet-ks-progress-banner").Should().ContainSingle();
            cut.FindAll(CancelBtn).Should().BeEmpty($"{mode} searches are seconds-long and not cancellable from the bar");
            _terminalResult = ResultsCompleted(_nextTaskId, 3);
            cut.WaitForState(() => ExplorerHasRows(cut) && !cut.Markup.Contains("Search in progress"), PollWaitTimeout);
        }

        _terminalResult = null;
        StartSearch(cut, "keyword");
        cut.WaitForState(() => cut.FindAll(CancelBtn).Count > 0, TimeSpan.FromSeconds(5));

        // Files mode: the bar stays, the button goes; back on Keyword it returns.
        cut.Find("#ks-mode-files").Change("files");
        cut.FindAll(CancelBtn).Should().BeEmpty("never in Files mode");
        cut.FindAll(".cachet-ks-progress-banner").Should().ContainSingle();
        cut.Find("#ks-mode-keyword").Change("keyword");
        cut.FindAll(CancelBtn).Should().ContainSingle();

        _terminalResult = ResultsCompleted(_nextTaskId, 3);
        cut.WaitForState(() => !cut.Markup.Contains("Search in progress"), PollWaitTimeout);
        cut.FindAll(CancelBtn).Should().BeEmpty("a finished search has no button");
    }

    [Fact]
    public void Test_V2_ConfirmBeforeCancel()
    {
        var cut = RenderRunningSearch();

        cut.Find(CancelBtn).Click();
        _apiHandler.CancelledTaskIds.ToList().Should().BeEmpty("the first click never calls the API");
        cut.Find(CancelConfirm).TextContent.Should().Contain("Stop this search? Matches found so far are kept.");
        cut.Find(CancelStop).TextContent.Trim().Should().Be("Stop search");
        cut.Find(CancelKeep).TextContent.Trim().Should().Be("Keep running");
        cut.FindAll(CancelBtn).Should().BeEmpty("the confirm replaces the button");

        cut.Find(CancelKeep).Click();
        cut.FindAll(CancelConfirm).Should().BeEmpty();
        cut.FindAll(CancelBtn).Should().ContainSingle("[Keep running] restores the button");
        _apiHandler.CancelledTaskIds.ToList().Should().BeEmpty();

        cut.Find(CancelBtn).Click();
        cut.Find(CancelStop).Click();
        WaitForCancelPosts(cut, 1);
        _apiHandler.CancelledTaskIds.ToList().Should().Equal(1);
    }

    [Fact]
    public async Task Test_V3_CancellingIsSingleShotAndFilterStaysDisabled()
    {
        var cut = RenderRunningSearch();
        ClickCancelThenStop(cut);
        WaitForCancelling(cut);

        cut.FindAll(CancelBtn).Should().BeEmpty();
        cut.FindAll(CancelStop).Should().BeEmpty();
        cut.FindAll(CancelKeep).Should().BeEmpty();
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue("the filter bar stays disabled while cancelling");

        // Defensive guard: even a stray second invocation (stale event) must not POST again.
        var method = typeof(Web.Components.Pages.KeywordSearch)
            .GetMethod("CancelSearchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, null)!);

        _apiHandler.CancelledTaskIds.ToList().Should().BeEquivalentTo(new[] { 1 }, "one POST only");
        SearchButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Test_V4_CancelledIsNotFailed()
    {
        var cut = RenderPage();
        ArrangeCancellableSearch();
        // The task was cancelled elsewhere (e.g. Task Manager): the poll sees Cancelled without a local request.
        _terminalResult = ResultsCancelled(1, matches: 3);

        StartSearch(cut, "keyword");
        cut.WaitForState(() => cut.FindAll(".cachet-ks-notice").Count > 0, PollWaitTimeout);

        Notice(cut).Should().Be(PartialNotice);
        cut.Markup.Should().NotContain("did not complete successfully");
        cut.FindAll(".cachet-ks-error-dismiss").Should().BeEmpty("the Failed banner is not shown");
        cut.FindAll(".cachet-ks-error-banner").Should().BeEmpty();
        _apiHandler.CancelledTaskIds.ToList().Should().BeEmpty();
    }

    [Fact]
    public void Test_V5_PartialResultsShownOnlyWhenMatchesExist()
    {
        var cut = RenderPage();
        ArrangeCancellableSearch();

        _terminalResult = ResultsCancelled(1, matches: 3);
        StartSearch(cut, "keyword");
        cut.WaitForState(() => ExplorerHasRows(cut) && cut.FindAll(".cachet-ks-notice").Count > 0, PollWaitTimeout);
        ExplorerRowNames(cut).Should().Equal("Alpha", "Beta");

        _terminalResult = ResultsCancelled(2, matches: 0);
        StartSearch(cut, "keyword", "another-term");
        cut.WaitForState(() => cut.FindAll(".cachet-ks-notice").Count > 0 && Notice(cut).Contains("no matches"), PollWaitTimeout);

        Notice(cut).Should().Be(NoMatchNotice);
        cut.FindAll("#fe-root").Should().BeEmpty("zero matches: no results panel");
        cut.FindAll("#ks-send-to-quarantine").Should().BeEmpty();
    }

    [Fact]
    public void Test_V6_NewSearchAvailableAfterCancel()
    {
        var cut = RenderRunningSearch();
        ClickCancelThenStop(cut);
        WaitForCancelling(cut);
        _terminalResult = ResultsCancelled(1, matches: 0);
        cut.WaitForState(() => cut.FindAll(".cachet-ks-notice").Count > 0, PollWaitTimeout);
        SearchButton(cut).HasAttribute("disabled").Should().BeFalse("the filter bar is re-enabled on Cancelled");

        SubmitFollowUpSearchesAndAssert(cut);
    }

    // After a cancelled search: a Filename, a Date and a Keyword search can each be submitted without a
    // reload. The first two end Cancelled (no matches), the last Completed; each starts a fresh task.
    private void SubmitFollowUpSearchesAndAssert(IRenderedComponent<Web.Components.Pages.KeywordSearch> cut)
    {
        var posted = new List<SearchRequest>();
        _apiHandler.OnPostSearch = async request =>
        {
            posted.Add((await request.Content!.ReadFromJsonAsync<SearchRequest>())!);
            return JsonResponse(HttpStatusCode.Accepted, SubmittedResponse(++_nextTaskId));
        };

        void Submit(string mode)
        {
            switch (mode)
            {
                case "date":
                    EnsureMode(cut, "date");
                    EnterDates(cut, "01/01/2025", "01/31/2025");
                    break;
                case "filename":
                    EnsureMode(cut, "filename");
                    SearchInput(cut).Input("invoice");
                    break;
                default:
                    EnsureMode(cut, "keyword");
                    SearchInput(cut).Input("quarterly-report");
                    break;
            }

            SearchButton(cut).HasAttribute("disabled").Should().BeFalse($"a new {mode} search must be submit-able after a cancel");
            SearchButton(cut).Click();
        }

        _terminalResult = ResultsCancelled(_nextTaskId + 1, matches: 0);
        Submit("filename");
        cut.WaitForState(() => cut.Markup.Contains("no matches were found") && !cut.Markup.Contains("Search in progress"), PollWaitTimeout);
        cut.FindAll("#fe-root").Should().BeEmpty();

        _terminalResult = ResultsCancelled(_nextTaskId + 1, matches: 0);
        Submit("date");
        cut.WaitForState(() => cut.Markup.Contains("no matches were found") && !cut.Markup.Contains("Search in progress"), PollWaitTimeout);

        _terminalResult = ResultsCompleted(_nextTaskId + 1, 3);
        Submit("keyword");
        cut.WaitForState(() => ExplorerHasRows(cut) && !cut.Markup.Contains("Search in progress"), PollWaitTimeout);
        cut.FindAll(".cachet-ks-notice").Should().BeEmpty("a new search replaces the cancelled state");

        posted.Select(p => p.Mode).Should().Equal("Filename", "Date", "Keyword");
    }

    [Fact]
    public void Test_V7_CancelConflictIsGraceful()
    {
        _cancelStatus = HttpStatusCode.Conflict;
        var cut = RenderRunningSearch();

        ClickCancelThenStop(cut);
        cut.WaitForState(() => cut.FindAll(".cachet-ks-toast").Count > 0, TimeSpan.FromSeconds(5));

        cut.Find(".cachet-ks-toast").TextContent.Should().Be("The search had already finished.");
        cut.FindAll(".cachet-ks-error-banner").Should().BeEmpty("no error banner on 409");
        cut.Markup.Should().NotContain(CancellingText);
        cut.Markup.Should().Contain("Search in progress", "polling continues until the real outcome arrives");
        _apiHandler.CancelledTaskIds.ToList().Should().Equal(1);

        // The next tick renders the real outcome as usual.
        _terminalResult = ResultsCompleted(1, 3);
        cut.WaitForState(() => ExplorerHasRows(cut), PollWaitTimeout);
    }

    [Theory]
    [InlineData("401")]
    [InlineData("404")]
    [InlineData("network")]
    public void Test_V8_CancelErrorsHandled(string failure)
    {
        var cut = RenderRunningSearch();
        switch (failure)
        {
            case "401":
                _cancelStatus = HttpStatusCode.Unauthorized;
                break;
            case "404":
                _cancelStatus = HttpStatusCode.NotFound;
                break;
            default:
                _apiHandler.OnPostCancel = (_, _) => throw new HttpRequestException("simulated: Api unreachable");
                break;
        }

        ClickCancelThenStop(cut);

        if (failure == "401")
        {
            cut.WaitForState(() => cut.Markup.Contains("Your session has expired — sign in again to see results."), TimeSpan.FromSeconds(5));
            cut.FindAll(".cachet-ks-progress-banner").Should().BeEmpty("the search is no longer shown as running");
            cut.Markup.Should().NotContain(CancellingText);
            return;
        }

        // 404 and a network error: the generic banner, and the button is back (the search is still running).
        cut.WaitForState(() => cut.FindAll(".cachet-ks-error-banner").Count > 0, TimeSpan.FromSeconds(5));
        cut.Find(".cachet-ks-error-banner").TextContent.Should().Contain("Something went wrong. Please try again.");
        cut.WaitForState(() => cut.FindAll(CancelBtn).Count > 0, TimeSpan.FromSeconds(5));
        cut.Markup.Should().NotContain(CancellingText);
        cut.Markup.Should().Contain("Search in progress");
    }

    /// <summary>Single-client <see cref="IHttpClientFactory"/> stand-in — this component only ever
    /// requests the "CachetApi" named client, so no name-based routing is needed.</summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
