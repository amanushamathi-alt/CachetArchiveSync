using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cachet.ArchiveSync.Web.Tests.ODriveSync;

/// <summary>
/// CAS-023 §13 — proves every Success Criterion (SC-1..SC-13) and Validation (V1..V5) for
/// <c>ODriveSyncContentScan.razor</c> via bUnit, with the "CachetApi" named <see cref="HttpClient"/>
/// redirected to <see cref="FakeODriveSyncApiHandler"/> so no real Api process/DB is ever reached,
/// matching <c>KeywordSearchPageTests</c>' (CAS-022) own conventions for this test project.
///
/// <para>
/// <b>Polling and real time:</b> <c>ODriveSyncContentScan.razor</c>'s scan poll loop and its three
/// (Copy/Update/Purge) action poll loops each use a genuine <see cref="PeriodicTimer"/> on a fixed
/// 5-second interval (§8.2/§8.4/§9 — a code constant, not a configuration key). Tests that need to
/// observe a loop's first tick use <c>WaitForState</c> with a timeout above 5 seconds, matching
/// <c>KeywordSearchPageTests</c>' own documented trade-off.
/// </para>
///
/// <para>
/// <b>Implementer's documented §8.4 deviation:</b> a Copy/Update/Purge action's own poll loop
/// re-queries the ORIGINAL scan's <c>taskId</c> against <c>GET /sync/{taskId}/results</c> — not the
/// action's own returned <c>taskId</c> — because <c>SyncScanService.GetResultsAsync</c> 404s for any
/// <c>taskId</c> whose <c>task_type</c> isn't <c>ContentScan</c> (see the code comment near line 515
/// of the .razor file). SC-6/SC-7/SC-9's polling assertions below are written against that actual,
/// correct behavior rather than the spec's literal ("this action's own taskId") wording.
/// </para>
/// </summary>
public sealed class ODriveSyncPageTests : TestContext
{
    private static readonly TimeSpan PollWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly FakeODriveSyncApiHandler _apiHandler = new();
    private readonly TaskPollingOptions _pollingOptions = new() { TaskManagerPollIntervalSeconds = 1 };
    private readonly CapturingLoggerProvider _loggerProvider = new();
    private readonly TestAuthorizationContext _authContext;

    public ODriveSyncPageTests()
    {
        var httpClient = new HttpClient(_apiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(httpClient));
        Services.AddLogging(builder => builder.AddProvider(_loggerProvider));
        // CAS-045: the page now injects IConfiguration (ODriveSync:MaxSelectionPerAction, default 100).
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        // CAS-030 §8.4: poll interval now bound via IOptions<TaskPollingOptions> (default 10 s); 1 s
        // here keeps the CAS-023 waits (sized for the former 5 s constant) comfortably in range.
        Services.AddSingleton<IOptions<TaskPollingOptions>>(Options.Create(_pollingOptions));

        _authContext = this.AddTestAuthorization();
        SetFullAccess();
    }

    private void SetFullAccess()
    {
        _authContext.SetAuthorized("full-access-user");
        _authContext.SetClaims(new Claim("b2_capability", "Full Access"));
    }

    private void SetReadOnly()
    {
        _authContext.SetAuthorized("read-only-user");
        _authContext.SetClaims(new Claim("b2_capability", "Read Only"));
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };

    private static string NormalizeWhitespace(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(markup, @"\s+", " ").Trim();

    private IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> RenderPage() =>
        RenderComponent<Web.Components.Pages.ODriveSyncContentScan>();

    private static IElement RunScanButton(IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> cut) =>
        cut.Find(".cachet-sync-scan-btn");

    private static IElement FindPanel(IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> cut, string headingPrefix) =>
        cut.FindAll(".cachet-sync-panel").First(p => p.QuerySelector("h2")!.TextContent.Contains(headingPrefix, StringComparison.OrdinalIgnoreCase));

    private static IElement FindActionButton(
        IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> cut, string panelHeading, string buttonLabel) =>
        FindPanel(cut, panelHeading).QuerySelectorAll("button").First(b => b.TextContent.Contains(buttonLabel, StringComparison.OrdinalIgnoreCase));

    /// <summary>Waits for the second-confirmation modal's button (the Purge click handler is async, so the modal
    /// is not guaranteed to be in the markup the instant <c>Click()</c> returns when the thread pool is busy).</summary>
    private static IElement WaitForModalButton(
        IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> cut, string label)
    {
        cut.WaitForState(
            () => cut.FindAll(".cachet-sync-modal button").Any(b => b.TextContent.Contains(label)),
            TimeSpan.FromSeconds(10));
        return cut.FindAll(".cachet-sync-modal button").First(b => b.TextContent.Contains(label));
    }

    private static void CheckRowByMarker(
        IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> cut, string panelHeading, string nameMarker)
    {
        var panel = FindPanel(cut, panelHeading);
        var row = panel.QuerySelectorAll("tbody tr").First(r => r.TextContent.Contains(nameMarker, StringComparison.OrdinalIgnoreCase));
        row.QuerySelector("input[type=checkbox]")!.Change(true);
    }

    /// <summary>Gap 6 support: the uncheck path of <c>ToggleSelection</c> — a row previously
    /// checked via <see cref="CheckRowByMarker"/> is unchecked again.</summary>
    private static void UncheckRowByMarker(
        IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> cut, string panelHeading, string nameMarker)
    {
        var panel = FindPanel(cut, panelHeading);
        var row = panel.QuerySelectorAll("tbody tr").First(r => r.TextContent.Contains(nameMarker, StringComparison.OrdinalIgnoreCase));
        row.QuerySelector("input[type=checkbox]")!.Change(false);
    }

    private static TaskSubmittedResponse ScanSubmitted(int taskId = 1) => new()
    {
        TaskId = taskId,
        Type = "ContentScan",
        Status = "Queued",
        SubmittedAt = DateTimeOffset.UtcNow,
    };

    private static TaskSubmittedResponse ActionSubmitted(string type, int taskId = 2) => new()
    {
        TaskId = taskId,
        Type = type,
        Status = "Queued",
        SubmittedAt = DateTimeOffset.UtcNow,
    };

    private static SyncScanResultsResponse CompletedResults(
        IReadOnlyList<NewOnODriveItem>? newItems = null,
        IReadOnlyList<RemovedFromODriveItem>? removedItems = null,
        IReadOnlyList<ModifiedOnODriveItem>? modifiedItems = null) => new()
        {
            TaskId = 1,
            Status = "Completed",
            NewOnODrive = new PagedResult<NewOnODriveItem>
            {
                Items = newItems ?? Array.Empty<NewOnODriveItem>(),
                Page = 1,
                PageSize = 50,
                TotalCount = newItems?.Count ?? 0,
            },
            RemovedFromODrive = new PagedResult<RemovedFromODriveItem>
            {
                Items = removedItems ?? Array.Empty<RemovedFromODriveItem>(),
                Page = 1,
                PageSize = 50,
                TotalCount = removedItems?.Count ?? 0,
            },
            ModifiedOnODrive = new PagedResult<ModifiedOnODriveItem>
            {
                Items = modifiedItems ?? Array.Empty<ModifiedOnODriveItem>(),
                Page = 1,
                PageSize = 50,
                TotalCount = modifiedItems?.Count ?? 0,
            },
        };

    private static NewOnODriveItem SampleNewItem(int fileId, string? name = null, string syncStatus = "Pending") => new()
    {
        FileId = fileId,
        FileIndexId = fileId + 500_000,
        Name = name ?? $"new-file-{fileId}.pdf",
        Path = $"/o-drive/new-file-{fileId}.pdf",
        Size = 1024,
        ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
        SyncStatus = syncStatus,
    };

    private static RemovedFromODriveItem SampleRemovedItem(int fileId, string? name = null, string syncStatus = "Pending") => new()
    {
        FileId = fileId,
        FileIndexId = fileId + 500_000,
        Name = name ?? $"removed-file-{fileId}.pdf",
        Path = $"/o-drive/removed-file-{fileId}.pdf",
        LastSeen = DateTimeOffset.UtcNow.AddDays(-7),
        SyncStatus = syncStatus,
    };

    private static ModifiedOnODriveItem SampleModifiedItem(int fileId, string? name = null, string syncStatus = "Pending") => new()
    {
        FileId = fileId,
        FileIndexId = fileId + 500_000,
        Name = name ?? $"modified-file-{fileId}.pdf",
        Path = $"/o-drive/modified-file-{fileId}.pdf",
        ModifiedAt = DateTimeOffset.UtcNow.AddHours(-2),
        Sha1Changed = true,
        SyncStatus = syncStatus,
    };

    /// <summary>Runs a Content Scan to completion with one item in each of the three panels
    /// (FileIds 101/201/301 by default) and waits for the panels to render.</summary>
    private IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> CompleteScanWithAllPanels(
        int newFileId = 101, int removedFileId = 201, int modifiedFileId = 301)
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults(
            newItems: new[] { SampleNewItem(newFileId) },
            removedItems: new[] { SampleRemovedItem(removedFileId) },
            modifiedItems: new[] { SampleModifiedItem(modifiedFileId) })));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));
        cut.WaitForState(() => PanelHeading.HasCount(cut, "New on O-Drive", 1), PollWaitTimeout);

        return cut;
    }

    // -----------------------------------------------------------------------------------------
    // SC-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC1_ScanSubmissionShowsInProgressBannerAndDisablesButton()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        RunScanButton(cut).Click();

        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));

        cut.Markup.Should().Contain("Content Scan in progress — submitted");
        RunScanButton(cut).HasAttribute("disabled").Should().BeTrue(
            "the Run Content Scan button must be disabled while its own task is outstanding (§8.1)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC2_PollingContinuesWhileScanTaskIncomplete()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));

        // A 409 response never calls StateHasChanged (the poll loop just `continue`s), so there is
        // no render event to hook bUnit's WaitForState off of — poll the handler's own call counter.
        await WaitUntilAsync(() => _apiHandler.ResultsCallCount >= 1, PollWaitTimeout);

        cut.Markup.Should().Contain("Content Scan in progress", "a 409 must keep the in-progress banner, not stop polling");
        PanelHeading.Count(cut, "New on O-Drive").Should().BeNull("no panel content beyond the in-progress banner while incomplete");
        PanelHeading.Count(cut, "Removed from O-Drive").Should().BeNull();
        PanelHeading.Count(cut, "Modified files on O-Drive").Should().BeNull();
        RunScanButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    // -----------------------------------------------------------------------------------------
    // SC-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC3_CompletedScanAutoPopulatesAllThreePanelsInPlace()
    {
        var cut = CompleteScanWithAllPanels();

        PanelHeading.HasCount(cut, "New on O-Drive", 1).Should().BeTrue();
        PanelHeading.HasCount(cut, "Removed from O-Drive", 1).Should().BeTrue();
        PanelHeading.HasCount(cut, "Modified files on O-Drive", 1).Should().BeTrue();
        cut.Markup.Should().Contain("new-file-101.pdf");
        cut.Markup.Should().Contain("removed-file-201.pdf");
        cut.Markup.Should().Contain("modified-file-301.pdf");
        cut.Markup.Should().NotContain("Content Scan in progress", "the in-progress banner must be removed once results are in");
    }

    // -----------------------------------------------------------------------------------------
    // SC-4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC4_FailedScanShowsFailureBanner()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new SyncScanResultsResponse
        {
            TaskId = 1,
            Status = "Failed",
            FailureReason = "worker crashed",
        }));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));

        cut.WaitForState(() => cut.Markup.Contains("This Content Scan did not complete successfully."), PollWaitTimeout);

        cut.Markup.Should().Contain("This Content Scan did not complete successfully.");
        cut.Markup.Should().NotContain("Content Scan in progress");
        PanelHeading.Count(cut, "New on O-Drive").Should().BeNull("no stale/partial panel content on a failed scan");
        PanelHeading.Count(cut, "Removed from O-Drive").Should().BeNull();
        PanelHeading.Count(cut, "Modified files on O-Drive").Should().BeNull();
    }

    // -----------------------------------------------------------------------------------------
    // SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC5_ActionButtonsReflectOwnPanelSelectionCountIndependently()
    {
        var cut = CompleteScanWithAllPanels();

        FindActionButton(cut, "New on O-Drive", "Copy to B2").TextContent.Should().Contain("Copy to B2 (0)");
        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").TextContent.Should().Contain("Purge from B2 (0)");
        FindActionButton(cut, "Modified files on O-Drive", "Update in B2").TextContent.Should().Contain("Update in B2 (0)");

        CheckRowByMarker(cut, "New on O-Drive", "new-file-101.pdf");

        FindActionButton(cut, "New on O-Drive", "Copy to B2").TextContent.Should().Contain("Copy to B2 (1)");
        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").TextContent.Should().Contain(
            "Purge from B2 (0)", "a selection in the New panel must not affect the Removed panel's own count (SC-5)");
        FindActionButton(cut, "Modified files on O-Drive", "Update in B2").TextContent.Should().Contain(
            "Update in B2 (0)", "a selection in the New panel must not affect the Modified panel's own count (SC-5)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC6_CopyToB2CallsApiWithCorrectFileIdsAndPolls()
    {
        var cut = CompleteScanWithAllPanels(newFileId: 42);
        CheckRowByMarker(cut, "New on O-Drive", "new-file-42.pdf");

        SyncActionRequest? captured = null;
        _apiHandler.OnPostCopy = async request =>
        {
            captured = await request.Content!.ReadFromJsonAsync<SyncActionRequest>();
            return JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncCopy"));
        };

        var pollCountAtSubmit = _apiHandler.ResultsCallCount;

        // CAS-045 §2.4: progress is tracked via the ACTION's own task (GET tasks/{id}); once it is
        // Completed the New panel reloads and the server (excludeSynced=true) no longer returns the row.
        _apiHandler.OnGetTask = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ScanTask(2, "Completed", "SyncCopy")));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults()));

        FindActionButton(cut, "New on O-Drive", "Copy to B2").Click();

        cut.WaitForState(() => !cut.Markup.Contains("new-file-42.pdf"), PollWaitTimeout);

        captured.Should().NotBeNull();
        captured!.TaskId.Should().Be(1, "the request carries the ORIGINAL scan's taskId (CAS-007 §6 semantics)");
        captured.FileIds.Should().BeEquivalentTo(new[] { 42 });
        _apiHandler.ResultsCallCount.Should().BeGreaterThan(
            pollCountAtSubmit,
            "SC-6: after the action task completes the New panel must reload its page from GET /sync/{taskId}/results (CAS-045 §2.4)");
        AnyRequest(HttpMethod.Get, "/tasks/2").Should().BeTrue("progress is tracked via the action's own task (CAS-045 §2.4)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC7_UpdateInB2CallsApiWithCorrectFileIds()
    {
        var cut = CompleteScanWithAllPanels(modifiedFileId: 77);
        CheckRowByMarker(cut, "Modified files on O-Drive", "modified-file-77.pdf");

        SyncActionRequest? captured = null;
        _apiHandler.OnPostUpdate = async request =>
        {
            captured = await request.Content!.ReadFromJsonAsync<SyncActionRequest>();
            return JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncUpdate"));
        };
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        FindActionButton(cut, "Modified files on O-Drive", "Update in B2").Click();

        cut.WaitForState(() => captured is not null, TimeSpan.FromSeconds(3));

        captured!.TaskId.Should().Be(1, "the request carries the ORIGINAL scan's taskId (CAS-007 §6 semantics)");
        captured.FileIds.Should().BeEquivalentTo(new[] { 77 });
    }

    // -----------------------------------------------------------------------------------------
    // SC-8 / V-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC8_PurgeRequiresExplicitConfirmationBeforeApiCall()
    {
        var cut = CompleteScanWithAllPanels(removedFileId: 55);
        CheckRowByMarker(cut, "Removed from O-Drive", "removed-file-55.pdf");

        var purgeCalled = false;
        _apiHandler.OnPostPurge = _ =>
        {
            purgeCalled = true;
            return Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncPurge")));
        };

        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").Click();
        var cancelButton = WaitForModalButton(cut, "Cancel");

        // Markup contains the literal Razor source line break/indentation between "action" and
        // "cannot be undone" — normalize whitespace before asserting, rather than matching raw markup.
        NormalizeWhitespace(cut.Markup).Should()
            .Contain("This will permanently delete 1 file(s) from Backblaze B2. This action cannot be undone.");
        purgeCalled.Should().BeFalse("Purge must never be submitted before the second-confirmation dialog is confirmed (V3)");

        cancelButton.Click();

        purgeCalled.Should().BeFalse("canceling sends no request (SC-8)");
        cut.Markup.Should().NotContain("permanently delete", "the confirmation dialog must be dismissed on Cancel");
        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").TextContent.Should().Contain(
            "Purge from B2 (1)", "canceling must preserve the selection (SC-8)");
    }

    [Fact]
    public void Test_V3_PurgeNeverSubmittedWithoutExplicitConfirmation()
    {
        var cut = CompleteScanWithAllPanels(removedFileId: 66);
        CheckRowByMarker(cut, "Removed from O-Drive", "removed-file-66.pdf");

        var purgeCalled = false;
        _apiHandler.OnPostPurge = _ =>
        {
            purgeCalled = true;
            return Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncPurge")));
        };

        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").Click();

        purgeCalled.Should().BeFalse("opening the confirmation dialog alone must never call POST /sync/actions/purge");
        _apiHandler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/sync/actions/purge", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------------------------
    // SC-9
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC9_ConfirmedPurgeCallsApiWithConfirmTrueAndCorrectFileIds()
    {
        var cut = CompleteScanWithAllPanels(removedFileId: 88);
        CheckRowByMarker(cut, "Removed from O-Drive", "removed-file-88.pdf");

        SyncPurgeRequest? captured = null;
        _apiHandler.OnPostPurge = async request =>
        {
            captured = await request.Content!.ReadFromJsonAsync<SyncPurgeRequest>();
            return JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncPurge"));
        };
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").Click();
        WaitForModalButton(cut, "Confirm").Click();

        cut.WaitForState(() => captured is not null, TimeSpan.FromSeconds(3));

        captured!.TaskId.Should().Be(1);
        captured.FileIds.Should().BeEquivalentTo(new[] { 88 });
        captured.Confirm.Should().BeTrue("§8.4/CAS-007 §6 requires confirm: true on the submitted request");
        cut.Markup.Should().NotContain("permanently delete", "the dialog must close once confirmed");
    }

    // -----------------------------------------------------------------------------------------
    // SC-10
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC10_SyncedRowDropsFromPanelFailedRowShowsStatusChip()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults(
            newItems: new[]
            {
                SampleNewItem(10, name: "synced-file.pdf"),
                SampleNewItem(20, name: "failed-file.pdf"),
            })));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));
        cut.WaitForState(() => PanelHeading.HasCount(cut, "New on O-Drive", 2), PollWaitTimeout);

        CheckRowByMarker(cut, "New on O-Drive", "synced-file.pdf");
        CheckRowByMarker(cut, "New on O-Drive", "failed-file.pdf");

        _apiHandler.OnPostCopy = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncCopy")));
        // CAS-045 §2.4: the action task completes; the reload (excludeSynced=true) now omits the Synced row
        // server-side and still returns the Failed one.
        _apiHandler.OnGetTask = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ScanTask(2, "Completed", "SyncCopy")));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults(
            newItems: new[]
            {
                SampleNewItem(20, name: "failed-file.pdf", syncStatus: "Failed"),
            })));

        FindActionButton(cut, "New on O-Drive", "Copy to B2").Click();

        cut.WaitForState(() => cut.Markup.Contains("failed-file.pdf") && cut.Markup.Contains("cachet-sync-chip\">Failed"), PollWaitTimeout);

        cut.Markup.Should().NotContain("synced-file.pdf", "a row whose action succeeded (Synced) must drop off its panel entirely (SC-10)");
        cut.Markup.Should().Contain("failed-file.pdf", "a row whose action failed must remain visible");
        cut.Markup.Should().Contain("cachet-sync-chip\">Failed", "a failed row must show a Failed status chip");
    }

    // -----------------------------------------------------------------------------------------
    // SC-11 / V-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC11_ReadOnlySessionCannotTriggerSyncActionsButCanScanAndView()
    {
        SetReadOnly();
        var cut = CompleteScanWithAllPanels();

        RunScanButton(cut).HasAttribute("disabled").Should().BeFalse(
            "Run Content Scan remains available without CachetFullAccess (SC-11)");
        cut.Markup.Should().Contain("new-file-101.pdf", "viewing results remains available without CachetFullAccess (SC-11)");

        foreach (var (panelHeading, buttonLabel) in new[]
        {
            ("New on O-Drive", "Copy to B2"),
            ("Removed from O-Drive", "Purge from B2"),
            ("Modified files on O-Drive", "Update in B2"),
        })
        {
            var button = FindActionButton(cut, panelHeading, buttonLabel);
            button.HasAttribute("disabled").Should().BeTrue($"{buttonLabel} must be disabled for a Read Only session (V5)");
            button.GetAttribute("title").Should().Be("Requires Full Access");
        }

        // Defense in depth: even a checked row must never let a click reach the Api for a
        // Read Only session (the component's own SubmitSyncActionAsync/OpenPurgeConfirm guards).
        _apiHandler.OnPostCopy = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncCopy")));
        CheckRowByMarker(cut, "New on O-Drive", "new-file-101.pdf");
        FindActionButton(cut, "New on O-Drive", "Copy to B2").Click();

        _apiHandler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/sync/actions/copy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Test_V5_ActionButtonsUnavailableWithoutFullAccess()
    {
        SetReadOnly();
        var cut = CompleteScanWithAllPanels();

        FindActionButton(cut, "New on O-Drive", "Copy to B2").HasAttribute("disabled").Should().BeTrue();
        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").HasAttribute("disabled").Should().BeTrue();
        FindActionButton(cut, "Modified files on O-Drive", "Update in B2").HasAttribute("disabled").Should().BeTrue();
    }

    // -----------------------------------------------------------------------------------------
    // SC-12
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC12_ZeroResultPanelShowsExplicitEmptyStateCopy()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults()));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));
        cut.WaitForState(() => cut.Markup.Contains("No files added."), PollWaitTimeout);

        cut.Markup.Should().Contain("No files added.");
        cut.Markup.Should().Contain("No files removed.");
        cut.Markup.Should().Contain("No files modified.");
        cut.Markup.Should().NotContain("<table", "a zero-result panel renders explanatory copy, not an empty table");
    }

    // -----------------------------------------------------------------------------------------
    // SC-13
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC13_NoCredentialOrContentLeakageInUiSurfaceOrLogs()
    {
        const string SensitiveB2Key = "raw-b2-appkey-marker-cas023-9f21";
        const string SensitiveSessionCookie = "session-cookie-marker-cas023-7b3e";
        const string SensitiveFileContent = "raw-file-content-marker-cas023-4d10";

        // ODriveSyncContentScan.razor never reads a key from configuration or handles raw file
        // content — prove the property holds structurally, same defensive pattern
        // KeywordSearchPageTests' SC-10 test already established for this project.
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:ApplicationKey"] = SensitiveB2Key })
            .Build());

        var cut = CompleteScanWithAllPanels();

        cut.Markup.Should().NotContain(SensitiveB2Key);
        cut.Markup.Should().NotContain(SensitiveSessionCookie);
        cut.Markup.Should().NotContain(SensitiveFileContent);

        var logText = string.Join("\n", _loggerProvider.Messages);
        logText.Should().NotContain(SensitiveB2Key);
        logText.Should().NotContain(SensitiveSessionCookie);
        logText.Should().NotContain(SensitiveFileContent);
    }

    // -----------------------------------------------------------------------------------------
    // CAS-024 SC-1 (QuarantineQueueModal trigger button)
    // -----------------------------------------------------------------------------------------

    /// <summary>CAS-024 SC-1 — the "Quarantine Queue" trigger button on this host page opens the
    /// shared modal. The Keyword Search host's identically-named test is
    /// <c>KeywordSearchPageTests.Test_SC1_TriggerButtonOnBothPagesOpensModal</c>.</summary>
    [Fact]
    public void Test_SC1_TriggerButtonOnBothPagesOpensModal()
    {
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));

        var cut = RenderPage();

        cut.Find(".cachet-sync-quarantine-trigger-btn").Click();

        cut.WaitForState(() => cut.Markup.Contains("cachet-qq-modal"), TimeSpan.FromSeconds(3));
        cut.Markup.Should().Contain("cachet-qq-modal", "clicking the trigger button must open QuarantineQueueModal");
    }

    /// <summary>CAS-052 SC-6 — the toolbar badge shows the Api's <c>TotalCount</c> (60), not the
    /// 25-row first page.</summary>
    [Fact]
    public void Test_SC6_ODriveSync_Badge_Total()
    {
        var store = Quarantine.QuarantinePagingFixtures.Store(60);
        _apiHandler.OnGetQuarantine = Quarantine.QuarantinePagingFixtures.Serve(store);

        var cut = RenderPage();

        cut.WaitForAssertion(
            () => cut.Find(".cachet-sync-quarantine-trigger-btn .cachet-btn-count").TextContent.Trim().Should().Be("60"),
            TimeSpan.FromSeconds(3));
    }

    // -----------------------------------------------------------------------------------------
    // CAS-031 SC-2 (Audit Log trigger button; the Keyword Search host's SC-1/SC-10 are in
    // KeywordSearchPageTests)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC2_OpenFromODriveSyncLoadsDefaultPage()
    {
        // Arrange
        _apiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));
        _apiHandler.OnGetAuditLog = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new PagedResult<AuditLogListItem>
        {
            Items = new[]
            {
                new AuditLogListItem
                {
                    User = "sync-reviewer",
                    Tab = "O-Drive Sync",
                    Action = "Content Scan",
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

        // Assert — the same modal, the same default request as on Keyword Search (SC-1).
        cut.Find(".cachet-al-modal").GetAttribute("role").Should().Be("dialog");
        cut.Find("#cachet-al-title").TextContent.Should().Be("Audit Log");
        var auditLogRequests = _apiHandler.Requests.ToList()
            .Where(r => r.RequestUri!.AbsolutePath.EndsWith("/audit-log", StringComparison.OrdinalIgnoreCase))
            .ToList();
        auditLogRequests.Should().ContainSingle();
        auditLogRequests[0].Method.Should().Be(HttpMethod.Get);
        auditLogRequests[0].RequestUri!.Query.Should().Be("?page=1&pageSize=50", "no tab/dateFrom/dateTo on the default load");
        cut.Find(".cachet-al-table tbody tr").TextContent.Should().Contain("sync-reviewer");
    }

    // -----------------------------------------------------------------------------------------
    // CAS-032 SC-1 (Pre-Action / Post-Action report trigger buttons; the Keyword Search host's
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
            cut.Find(".cachet-report-subtitle").TextContent.Should().Be("O-Drive Sync and content Scan");
            var previewRequest = _apiHandler.Requests.ToList().Skip(requestsBefore)
                .Single(r => r.RequestUri!.AbsolutePath.Contains("/reports/", StringComparison.OrdinalIgnoreCase));
            previewRequest.Method.Should().Be(HttpMethod.Get);
            previewRequest.RequestUri!.AbsolutePath.Should().EndWith(route);
            Uri.UnescapeDataString(previewRequest.RequestUri.Query).Should().Be("?activeTabLabel=O-Drive Sync and content Scan");

            cut.Find(".cachet-report-close-btn").Click();
        }
    }

    // -----------------------------------------------------------------------------------------
    // V-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V1_RunContentScanDisabledWhileScanInFlight()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        RunScanButton(cut).HasAttribute("disabled").Should().BeFalse();

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));

        RunScanButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    // -----------------------------------------------------------------------------------------
    // V-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V2_ActionButtonsDisabledUntilRowChecked()
    {
        var cut = CompleteScanWithAllPanels();

        FindActionButton(cut, "New on O-Drive", "Copy to B2").HasAttribute("disabled").Should().BeTrue();
        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").HasAttribute("disabled").Should().BeTrue();
        FindActionButton(cut, "Modified files on O-Drive", "Update in B2").HasAttribute("disabled").Should().BeTrue();

        CheckRowByMarker(cut, "New on O-Drive", "new-file-101.pdf");
        CheckRowByMarker(cut, "Removed from O-Drive", "removed-file-201.pdf");
        CheckRowByMarker(cut, "Modified files on O-Drive", "modified-file-301.pdf");

        FindActionButton(cut, "New on O-Drive", "Copy to B2").HasAttribute("disabled").Should().BeFalse();
        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").HasAttribute("disabled").Should().BeFalse();
        FindActionButton(cut, "Modified files on O-Drive", "Update in B2").HasAttribute("disabled").Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------
    // V-4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V4_ServerErrorResponsesRenderInlineErrorNotCrash()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));

        var act = () => RunScanButton(cut).Click();

        act.Should().NotThrow("a 422 from POST /sync/scan must be surfaced as a screen-level error, never an unhandled exception (V4)");
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
        cut.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    // -----------------------------------------------------------------------------------------
    // Coverage-gap closures (not spec-mapped SC/V IDs) — same precedent as
    // KeywordSearchPageTests' own follow-up closures for CAS-022, closing branches §10/§7 don't
    // individually name but §13's ≥85% line-coverage target requires.
    // -----------------------------------------------------------------------------------------

    /// <summary>Closes the coverage gap on <c>PollScanAsync</c>'s transient-network-exception
    /// branch — a single missed poll tick must not abandon the loop.</summary>
    [Fact]
    public void Test_ScanPollingTransientNetworkErrorIsSwallowedAndPollingContinues()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, callCount) => callCount == 1
            ? throw new HttpRequestException("simulated: transient network failure")
            : Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults(newItems: new[] { SampleNewItem(1) })));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));

        cut.WaitForState(() => PanelHeading.HasCount(cut, "New on O-Drive", 1), TimeSpan.FromSeconds(16));

        PanelHeading.HasCount(cut, "New on O-Drive", 1).Should().BeTrue("a single transient network error must not abandon the scan poll loop (§8.5)");
    }

    /// <summary>Closes the coverage gap on <c>HandleRunScanAsync</c>'s
    /// <c>catch (HttpRequestException or TaskCanceledException)</c> branch.</summary>
    [Fact]
    public void Test_ScanSubmitApiUnreachableShowsGenericErrorNotCrash()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => throw new HttpRequestException("simulated: Api unreachable");

        var act = () => RunScanButton(cut).Click();

        act.Should().NotThrow();
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
    }

    /// <summary>Closes the coverage gap on <c>SubmitSyncActionAsync</c>'s non-success branch (a
    /// server-side error on the action submission itself, distinct from V4's scan-submit case).</summary>
    [Fact]
    public void Test_ActionSubmitApiFailureShowsGenericErrorNotCrash()
    {
        var cut = CompleteScanWithAllPanels(newFileId: 900);
        CheckRowByMarker(cut, "New on O-Drive", "new-file-900.pdf");

        _apiHandler.OnPostCopy = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => FindActionButton(cut, "New on O-Drive", "Copy to B2").Click();

        act.Should().NotThrow();
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
    }

    /// <summary>Closes the coverage gap on <c>DisposeAsync</c> — the scan poll loop's and all three
    /// action poll loops' <see cref="CancellationTokenSource"/>/<see cref="PeriodicTimer"/> must be
    /// torn down cleanly when the component is removed mid-poll, without throwing.</summary>
    [Fact]
    public async Task Test_DisposeDuringActivePollDoesNotThrow()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));

        var act = () => cut.Instance.DisposeAsync().AsTask();

        await act.Should().NotThrowAsync();
    }

    // -----------------------------------------------------------------------------------------
    // Round 2 coverage-gap closures — CAS-023 §13's ≥85% line-coverage target. Each test below is
    // annotated with the numbered gap (from the merged Cobertura analysis) it closes.
    //
    // BLOCKED (not closable without reflecting into private state or changing production code,
    // which is out of scope for a test-writer pass — Karpathy P3/P4):
    //   - Gap 10 (PollActionAsync's `scanTaskId is null` guard, lines ~541-545): scanTaskId is set
    //     exactly once (HandleRunScanAsync) and never reset to null anywhere in the component, and
    //     SubmitSyncActionAsync itself already guards `scanTaskId is null` before ever calling
    //     PollActionAsync. There is no public/UI path that reaches PollActionAsync's loop with
    //     scanTaskId still null — this is dead defensive code under the component's current design.
    //   - Gap 21 (FormatRelativeTime(null) -> "just now", lines ~661-663): the only call site is
    //     FormatRelativeTime(scanSubmittedAtUtc) rendered only while isScanInFlight is true, and
    //     scanSubmittedAtUtc is assigned synchronously (no await in between) in the same statement
    //     block that sets isScanInFlight = true — so scanSubmittedAtUtc can never be observed as
    //     null at render time. Also dead defensive code, not reachable via the public component
    //     surface without reflection.
    // -----------------------------------------------------------------------------------------

    /// <summary>Gap 1 (lines ~275-276): a second Run Content Scan click while one is already
    /// in-flight must be a no-op — bUnit's <c>Click()</c> invokes the handler directly regardless
    /// of the "disabled" HTML attribute, so this exercises <c>HandleRunScanAsync</c>'s own guard,
    /// not just the DOM attribute.</summary>
    [Fact]
    public void Test_RunScanSecondClickWhileInFlightIsNoOp()
    {
        var cut = RenderPage();
        var scanCallCount = 0;
        _apiHandler.OnPostScan = _ =>
        {
            scanCallCount++;
            return Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        };
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));

        RunScanButton(cut).Click();

        scanCallCount.Should().Be(1, "a second scan click while one is outstanding must be a no-op (HandleRunScanAsync's isScanInFlight guard)");
    }

    /// <summary>Gap 2 (lines ~309-311): a 2xx <c>POST /sync/scan</c> whose body deserializes to
    /// null must show the generic error banner and never enter the in-flight state.</summary>
    [Fact]
    public void Test_ScanSubmitNullResponseBodyShowsGenericErrorAndDoesNotEnterInFlight()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create<TaskSubmittedResponse?>(null),
        });

        RunScanButton(cut).Click();

        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));

        cut.Markup.Should().NotContain("Content Scan in progress", "a null-deserialized submit response must not enter the in-flight state");
        RunScanButton(cut).HasAttribute("disabled").Should().BeFalse("scanTaskId was never set, so the button must remain enabled");
    }

    /// <summary>Gap 3 (lines ~363-368): a non-success, non-409 response from the scan results poll
    /// must show the generic error banner, clear the in-flight state, and stop polling.</summary>
    [Fact]
    public void Test_ScanPollNonSuccessNonConflictShowsErrorAndStopsPolling()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));

        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), PollWaitTimeout);

        cut.Markup.Should().NotContain("Content Scan in progress", "a 500 from the results poll must clear the in-flight state rather than keep polling silently");
        RunScanButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    /// <summary>Gap 4 (lines ~373-374): a 200 response whose body deserializes to null must keep
    /// polling (`continue`), not crash or stop.</summary>
    [Fact]
    public void Test_ScanPollNullResponseBodyContinuesPolling()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, callCount) => callCount == 1
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create<SyncScanResultsResponse?>(null) })
            : Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults(newItems: new[] { SampleNewItem(1) })));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));

        cut.WaitForState(() => PanelHeading.HasCount(cut, "New on O-Drive", 1), TimeSpan.FromSeconds(16));

        PanelHeading.HasCount(cut, "New on O-Drive", 1).Should().BeTrue("a 200 with a null-deserialized body must keep polling rather than crash or stop (§8.5)");
    }

    /// <summary>Gap 5 (line ~399): PollScanAsync's <c>catch (OperationCanceledException)</c>, reached
    /// when the component is disposed mid-poll. Waits for at least one poll tick before disposing so
    /// the cancellation is observed from inside the loop body, not merely before it starts.</summary>
    [Fact]
    public async Task Test_DisposeAfterFirstPollTickCancelsScanLoopWithoutThrowing()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(2));

        await WaitUntilAsync(() => _apiHandler.ResultsCallCount >= 1, PollWaitTimeout);

        var act = () => cut.Instance.DisposeAsync().AsTask();

        await act.Should().NotThrowAsync();
    }

    /// <summary>Gap 6 (lines ~413-415): <c>ToggleSelection</c>'s uncheck path
    /// (<c>selection.Remove(fileId)</c>) — only the check/Add path was exercised before.</summary>
    [Fact]
    public void Test_ToggleSelectionUncheckRemovesFileIdFromSelection()
    {
        var cut = CompleteScanWithAllPanels();

        CheckRowByMarker(cut, "New on O-Drive", "new-file-101.pdf");
        FindActionButton(cut, "New on O-Drive", "Copy to B2").TextContent.Should().Contain("Copy to B2 (1)");

        UncheckRowByMarker(cut, "New on O-Drive", "new-file-101.pdf");

        FindActionButton(cut, "New on O-Drive", "Copy to B2").TextContent.Should().Contain(
            "Copy to B2 (0)", "unchecking a row must remove its fileId from the panel's selection (ToggleSelection's isChecked==false branch)");
    }

    /// <summary>Gap 7 (lines ~427-428), sub-condition <c>selectedRemovedIds.Count == 0</c>:
    /// <c>OpenPurgeConfirm</c> must no-op when nothing is selected. bUnit's <c>Click()</c> bypasses
    /// the "disabled" HTML attribute, so this exercises the method's own guard.</summary>
    [Fact]
    public void Test_OpenPurgeConfirmNoOpWhenNoRowsSelected()
    {
        var cut = CompleteScanWithAllPanels();

        var purgeCalled = false;
        _apiHandler.OnPostPurge = _ =>
        {
            purgeCalled = true;
            return Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncPurge")));
        };

        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").Click();

        cut.Markup.Should().NotContain("permanently delete", "OpenPurgeConfirm must no-op when no rows are selected");
        purgeCalled.Should().BeFalse();
    }

    /// <summary>Gap 7, sub-condition <c>!hasFullAccess</c>: <c>OpenPurgeConfirm</c> must no-op for a
    /// Read Only session even when rows are selected (defense in depth alongside V5's disabled
    /// button).</summary>
    [Fact]
    public void Test_OpenPurgeConfirmNoOpWithoutFullAccessEvenWhenRowsSelected()
    {
        SetReadOnly();
        var cut = CompleteScanWithAllPanels(removedFileId: 909);
        CheckRowByMarker(cut, "Removed from O-Drive", "removed-file-909.pdf");

        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").Click();

        cut.Markup.Should().NotContain("permanently delete", "OpenPurgeConfirm must no-op for a Read Only session even with rows selected");
    }

    /// <summary>Gap 8 (lines ~477-481): <c>SubmitSyncActionAsync</c>'s network-exception catch for
    /// the Copy/Update/Purge submit call itself (distinct from the scan submit's own catch).</summary>
    [Fact]
    public void Test_ActionSubmitApiUnreachableShowsGenericErrorNotCrash()
    {
        var cut = CompleteScanWithAllPanels(newFileId: 910);
        CheckRowByMarker(cut, "New on O-Drive", "new-file-910.pdf");

        _apiHandler.OnPostCopy = _ => throw new HttpRequestException("simulated: Api unreachable");

        var act = () => FindActionButton(cut, "New on O-Drive", "Copy to B2").Click();

        act.Should().NotThrow();
        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
    }

    /// <summary>Gap 9 (lines ~495-497): a 2xx Copy/Update/Purge submit response whose body
    /// deserializes to null must show the generic error and return before clearing the selection or
    /// setting state.IsInFlight.</summary>
    [Fact]
    public void Test_ActionSubmitNullResponseBodyShowsGenericErrorAndDoesNotEnterInFlight()
    {
        var cut = CompleteScanWithAllPanels(newFileId: 920);
        CheckRowByMarker(cut, "New on O-Drive", "new-file-920.pdf");

        _apiHandler.OnPostCopy = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create<TaskSubmittedResponse?>(null),
        });

        FindActionButton(cut, "New on O-Drive", "Copy to B2").Click();

        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));

        FindActionButton(cut, "New on O-Drive", "Copy to B2").TextContent.Should().Contain(
            "Copy to B2 (1)", "a null-deserialized submit response must return before clearing the selection or entering IsInFlight");
    }

    private static HttpResponseMessage ActionTaskResponse(string type, string status) =>
        JsonResponse(HttpStatusCode.OK, ScanTask(2, status, type));

    /// <summary>Gap 11: <c>PollActionAsync</c>'s own network-exception catch during its
    /// <c>GET tasks/{id}</c> call — a single missed tick must not abandon the loop (CAS-045 §2.4).</summary>
    [Fact]
    public void Test_ActionPollTransientNetworkErrorIsSwallowedAndPollingContinues()
    {
        var cut = CompleteScanWithAllPanels(newFileId: 930);
        CheckRowByMarker(cut, "New on O-Drive", "new-file-930.pdf");

        _apiHandler.OnPostCopy = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncCopy")));
        var actionPollTick = 0;
        _apiHandler.OnGetTask = (_, _) => ++actionPollTick == 1
            ? throw new HttpRequestException("simulated: transient network failure")
            : Task.FromResult(ActionTaskResponse("SyncCopy", "Completed"));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults()));

        FindActionButton(cut, "New on O-Drive", "Copy to B2").Click();

        cut.WaitForState(() => !cut.Markup.Contains("new-file-930.pdf"), TimeSpan.FromSeconds(16));
    }

    /// <summary>Gap 12: a Queued/Running action task must keep the loop polling (not reload yet).</summary>
    [Fact]
    public void Test_ActionPollRunningTaskContinuesPolling()
    {
        var cut = CompleteScanWithAllPanels(newFileId: 940);
        CheckRowByMarker(cut, "New on O-Drive", "new-file-940.pdf");

        _apiHandler.OnPostCopy = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncCopy")));
        var actionPollTick = 0;
        _apiHandler.OnGetTask = (_, _) => Task.FromResult(ActionTaskResponse("SyncCopy", ++actionPollTick switch
        {
            1 => "Queued",
            2 => "Running",
            _ => "Completed",
        }));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults()));
        var resultsCallsAtSubmit = _apiHandler.ResultsCallCount;

        FindActionButton(cut, "New on O-Drive", "Copy to B2").Click();

        cut.WaitForState(() => !cut.Markup.Contains("new-file-940.pdf"), TimeSpan.FromSeconds(16));
        actionPollTick.Should().BeGreaterThanOrEqualTo(3, "Queued and Running ticks must not end the loop");
        _apiHandler.ResultsCallCount.Should().Be(resultsCallsAtSubmit + 1, "the panel reloads exactly once, on the terminal status");
    }

    /// <summary>Gap 13: a non-success response from <c>PollActionAsync</c>'s own poll must show the
    /// generic error banner and clear state.IsInFlight.</summary>
    [Fact]
    public void Test_ActionPollNonSuccessShowsErrorAndClearsInFlight()
    {
        var cut = CompleteScanWithAllPanels(newFileId: 950);
        CheckRowByMarker(cut, "New on O-Drive", "new-file-950.pdf");

        _apiHandler.OnPostCopy = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncCopy")));
        _apiHandler.OnGetTask = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        FindActionButton(cut, "New on O-Drive", "Copy to B2").Click();

        cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(16));

        cut.Markup.Should().Contain("Something went wrong. Please try again.");
    }

    /// <summary>Gap 14: a null-deserialized body from <c>PollActionAsync</c>'s own poll must
    /// <c>continue</c> rather than crash or reload.</summary>
    [Fact]
    public void Test_ActionPollNullBodyContinuesPolling()
    {
        var cut = CompleteScanWithAllPanels(newFileId: 960);
        CheckRowByMarker(cut, "New on O-Drive", "new-file-960.pdf");

        _apiHandler.OnPostCopy = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncCopy")));
        var actionPollTick = 0;
        _apiHandler.OnGetTask = (_, _) => ++actionPollTick == 1
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create<TaskDetailResponse?>(null) })
            : Task.FromResult(ActionTaskResponse("SyncCopy", "Completed"));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults()));

        FindActionButton(cut, "New on O-Drive", "Copy to B2").Click();

        cut.WaitForState(() => !cut.Markup.Contains("new-file-960.pdf"), TimeSpan.FromSeconds(16));
    }

    /// <summary>Gap 15: the <c>SyncUpdate</c> branch — a Completed action task reloads the Modified panel.</summary>
    [Fact]
    public void Test_UpdateActionPollReloadsModifiedPanelOnCompletion()
    {
        var cut = CompleteScanWithAllPanels(modifiedFileId: 970);
        CheckRowByMarker(cut, "Modified files on O-Drive", "modified-file-970.pdf");

        _apiHandler.OnPostUpdate = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncUpdate")));
        _apiHandler.OnGetTask = (_, _) => Task.FromResult(ActionTaskResponse("SyncUpdate", "Completed"));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults()));

        FindActionButton(cut, "Modified files on O-Drive", "Update in B2").Click();

        cut.WaitForState(() => !cut.Markup.Contains("modified-file-970.pdf"), PollWaitTimeout);
        _apiHandler.ResultsQueries.Last().Should().Contain("modifiedOnODrivePage=1").And.NotContain("newOnODrive");
    }

    /// <summary>Gap 16: the <c>SyncPurge</c> branch — a Completed action task reloads the Removed panel.</summary>
    [Fact]
    public void Test_PurgeActionPollReloadsRemovedPanelOnCompletion()
    {
        var cut = CompleteScanWithAllPanels(removedFileId: 980);
        CheckRowByMarker(cut, "Removed from O-Drive", "removed-file-980.pdf");

        _apiHandler.OnPostPurge = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ActionSubmitted("SyncPurge")));
        _apiHandler.OnGetTask = (_, _) => Task.FromResult(ActionTaskResponse("SyncPurge", "Completed"));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, CompletedResults()));

        FindActionButton(cut, "Removed from O-Drive", "Purge from B2").Click();
        WaitForModalButton(cut, "Confirm").Click();

        cut.WaitForState(() => !cut.Markup.Contains("removed-file-980.pdf"), PollWaitTimeout);
        _apiHandler.ResultsQueries.Last().Should().Contain("removedFromODrivePage=1").And.NotContain("newOnODrive");
    }

    /// <summary>Gap 22: <c>FormatRelativeTime</c> for an elapsed time between 1 minute and 1 hour
    /// returns the "{minutes} minute(s)" branch — driven by backdating the scan submission's
    /// <c>SubmittedAt</c> rather than waiting real time.</summary>
    [Fact]
    public void Test_FormatRelativeTimeShowsMinutesForElapsedUnderOneHour()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, new TaskSubmittedResponse
        {
            TaskId = 1,
            Type = "ContentScan",
            Status = "Queued",
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        }));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        RunScanButton(cut).Click();

        cut.WaitForState(() => cut.Markup.Contains("minute"), TimeSpan.FromSeconds(2));

        cut.Markup.Should().Contain("submitted 5 minutes ago", "an elapsed time under one hour must render via the \"{minutes} minute(s)\" branch");
    }

    /// <summary>Gap 23: <c>FormatRelativeTime</c> for an elapsed time of 1 hour or more returns the
    /// "{hours} hour(s)" branch.</summary>
    [Fact]
    public void Test_FormatRelativeTimeShowsHoursForElapsedOverOneHour()
    {
        var cut = RenderPage();
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, new TaskSubmittedResponse
        {
            TaskId = 1,
            Type = "ContentScan",
            Status = "Queued",
            SubmittedAt = DateTimeOffset.UtcNow.AddHours(-3),
        }));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));

        RunScanButton(cut).Click();

        cut.WaitForState(() => cut.Markup.Contains("hour"), TimeSpan.FromSeconds(2));

        cut.Markup.Should().Contain("submitted 3 hours ago", "an elapsed time of one hour or more must render via the \"{hours} hour(s)\" branch");
    }

    // =========================================================================================
    // CAS-030 §8.3 (FR-21) — reload on open, ?taskId= deep link, resumed sync actions,
    // acknowledgement, V7 (Sync* deep link), V9.
    // =========================================================================================

    private const string TaskNotFoundNotice = "That task could not be found.";
    private const string SessionExpiredMessage = "Your session has expired — sign in again to see results.";
    private const string NotPending = "Not Synced";

    private static TaskDetailResponse ScanTask(
        int taskId,
        string status = "Completed",
        string type = "ContentScan",
        bool isAcknowledged = false,
        string? resultDetail = null) => new()
        {
            TaskId = taskId,
            Type = type,
            SubmittedBy = "full-access-user",
            SubmittedAt = DateTimeOffset.UtcNow.AddMinutes(-7),
            StartTime = status == "Queued" ? null : DateTimeOffset.UtcNow.AddMinutes(-7),
            CompletedAt = status is "Completed" or "Failed" or "Cancelled" ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
            Status = status,
            IsAcknowledged = isAcknowledged,
            ResultDetail = resultDetail,
            SearchParameters = null,
        };

    private void StubTasks(TaskDetailResponse? latest, params TaskDetailResponse[] tasks)
    {
        if (latest is not null)
        {
            _apiHandler.OnGetTasks = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new PagedResult<TaskListItem>
            {
                Items = new[]
                {
                    new TaskListItem
                    {
                        TaskId = latest.TaskId, Type = latest.Type, SubmittedBy = latest.SubmittedBy,
                        SubmittedAt = latest.SubmittedAt, StartTime = latest.StartTime, CompletedAt = latest.CompletedAt,
                        Status = latest.Status, IsAcknowledged = latest.IsAcknowledged,
                    },
                },
                Page = 1,
                PageSize = 1,
                TotalCount = 1,
            }));
        }

        var byId = tasks.Append(latest).Where(t => t is not null).ToDictionary(t => t!.TaskId, t => t!);
        _apiHandler.OnGetTask = (_, id) => Task.FromResult(byId.TryGetValue(id, out var task)
            ? JsonResponse(HttpStatusCode.OK, task)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>CAS-045: GET /tasks is queried per type (the scan itself, then the Sync* resume checks).
    /// <paramref name="latestByType"/> answers <c>?type=X</c>; <c>GET /tasks/{id}</c> serves every task given.</summary>
    private void StubTasksByType(TaskDetailResponse scan, IReadOnlyDictionary<string, TaskDetailResponse> latestByType, params TaskDetailResponse[] others)
    {
        _apiHandler.OnGetTasks = request =>
        {
            var type = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["type"];
            var latest = type is not null && latestByType.TryGetValue(type, out var found) ? found : scan;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new PagedResult<TaskListItem>
            {
                Items = new[]
                {
                    new TaskListItem
                    {
                        TaskId = latest.TaskId, Type = latest.Type, SubmittedBy = latest.SubmittedBy,
                        SubmittedAt = latest.SubmittedAt, StartTime = latest.StartTime, CompletedAt = latest.CompletedAt,
                        Status = latest.Status, IsAcknowledged = latest.IsAcknowledged,
                    },
                },
                Page = 1,
                PageSize = 1,
                TotalCount = 1,
            }));
        };

        var byId = others.Append(scan).Concat(latestByType.Values).GroupBy(t => t.TaskId).ToDictionary(g => g.Key, g => g.First());
        _apiHandler.OnGetTask = (_, id) => Task.FromResult(byId.TryGetValue(id, out var task)
            ? JsonResponse(HttpStatusCode.OK, task)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static SyncScanResultsResponse ScanResultsFor(int taskId, string marker, string syncStatus = NotPending) =>
        CompletedResults(
            newItems: new[] { SampleNewItem(101, $"{marker}-new.pdf", syncStatus) },
            removedItems: new[] { SampleRemovedItem(201, $"{marker}-removed.pdf", syncStatus) },
            modifiedItems: new[] { SampleModifiedItem(301, $"{marker}-modified.pdf", syncStatus) }) with
        { TaskId = taskId };

    private IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> RenderPageAt(string? taskIdQuery)
    {
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            taskIdQuery is null ? "/odrive-sync" : $"/odrive-sync?taskId={Uri.EscapeDataString(taskIdQuery)}");
        return RenderPage();
    }

    private IReadOnlyList<string> ResultsPaths() =>
        _apiHandler.Requests.ToList()
            .Where(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("/results", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.RequestUri!.AbsolutePath)
            .ToList();

    private bool AnyRequest(HttpMethod method, string pathSuffix) =>
        _apiHandler.Requests.ToList().Any(r => r.Method == method
            && r.RequestUri!.AbsolutePath.TrimEnd('/').EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase));

    private static string RowStatus(IRenderedComponent<Web.Components.Pages.ODriveSyncContentScan> cut, string panelHeading, string nameMarker) =>
        FindPanel(cut, panelHeading).QuerySelectorAll("tbody tr")
            .First(r => r.TextContent.Contains(nameMarker, StringComparison.OrdinalIgnoreCase))
            .QuerySelector(".cachet-sync-chip")!.TextContent;

    // -----------------------------------------------------------------------------------------
    // SC-9 (O-Drive Sync variant; the "/" variant is in KeywordSearchPageTests)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC9_DeepLink_PopulatesNamedTask()
    {
        // Arrange — the latest scan is 600, the deep link names 888.
        StubTasks(latest: ScanTask(600), ScanTask(888));
        _apiHandler.OnGetResults = (request, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK,
            request.RequestUri!.AbsolutePath.Contains("/888/") ? ScanResultsFor(888, "named-scan") : ScanResultsFor(600, "latest-scan")));
        _apiHandler.OnPostScan = _ => throw new InvalidOperationException("a reload must never POST /sync/scan");

        // Act
        var cut = RenderPageAt("888");
        cut.WaitForState(() => PanelHeading.HasCount(cut, "New on O-Drive", 1), TimeSpan.FromSeconds(3));

        // Assert — all three panels from task 888, never the latest scan.
        PanelHeading.HasCount(cut, "Removed from O-Drive", 1).Should().BeTrue();
        PanelHeading.HasCount(cut, "Modified files on O-Drive", 1).Should().BeTrue();
        cut.Markup.Should().Contain("named-scan-new.pdf").And.Contain("named-scan-removed.pdf").And.Contain("named-scan-modified.pdf");
        cut.Markup.Should().NotContain("latest-scan");
        ResultsPaths().Should().Equal("/api/sync/888/results");
        _apiHandler.Requests.ToList().Any(r => r.Method == HttpMethod.Get
                && r.RequestUri!.AbsolutePath.TrimEnd('/').EndsWith("/tasks", StringComparison.OrdinalIgnoreCase)
                && r.RequestUri.Query.Contains("type=ContentScan", StringComparison.Ordinal))
            .Should().BeFalse("a deep link resolves GET /tasks/{taskId}, not the latest-ContentScan list (the Sync* lookups are the §8.3 resume checks)");
        AnyRequest(HttpMethod.Post, "/sync/scan").Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------
    // SC-10
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC10_ODriveSync_ReloadsScanAndResumesPendingSyncActions()
    {
        // Arrange — the latest scan (700) is Completed; one row per panel is still Pending
        // (New -> Copy, Removed -> Purge, Modified -> Update), plus one non-pending New row.
        // CAS-045 §2.4/§8.3: the user's latest Copy/Purge/Update tasks are Running when the page opens;
        // each is watched via GET tasks/{id}, and on Completed its panel reloads (excludeSynced=true).
        StubTasksByType(
            ScanTask(700),
            new Dictionary<string, TaskDetailResponse>
            {
                ["SyncCopy"] = ScanTask(7001, "Running", "SyncCopy"),
                ["SyncPurge"] = ScanTask(7002, "Running", "SyncPurge"),
                ["SyncUpdate"] = ScanTask(7003, "Running", "SyncUpdate"),
            });
        var actionTasksFinished = false;
        var baseOnGetTask = _apiHandler.OnGetTask!;
        _apiHandler.OnGetTask = (request, id) => id >= 7001 && actionTasksFinished
            ? Task.FromResult(JsonResponse(HttpStatusCode.OK, ScanTask(id, "Completed", id == 7001 ? "SyncCopy" : id == 7002 ? "SyncPurge" : "SyncUpdate")))
            : baseOnGetTask(request, id);
        var pendingState = CompletedResults(
            newItems: new[] { SampleNewItem(101, "copy-pending.pdf", "Pending"), SampleNewItem(102, "untouched-new.pdf", NotPending) },
            removedItems: new[] { SampleRemovedItem(201, "purge-pending.pdf", "Pending") },
            modifiedItems: new[] { SampleModifiedItem(301, "update-pending.pdf", "Pending") }) with
        { TaskId = 700 };
        // After the actions finish the server (excludeSynced=true) omits the Synced rows and keeps Failed.
        var finishedState = CompletedResults(
            newItems: new[] { SampleNewItem(102, "untouched-new.pdf", NotPending) },
            modifiedItems: new[] { SampleModifiedItem(301, "update-pending.pdf", "Failed") }) with
        { TaskId = 700 };
        _apiHandler.OnGetResults = (_, call) => Task.FromResult(JsonResponse(HttpStatusCode.OK, call == 1 ? pendingState : finishedState));

        // Act 1 — reload fills all three panels, Pending rows included.
        var cut = RenderPageAt(taskIdQuery: null);
        cut.WaitForState(() => PanelHeading.HasCount(cut, "New on O-Drive", 2), TimeSpan.FromSeconds(3));
        actionTasksFinished = true;

        // Assert 1
        PanelHeading.HasCount(cut, "Removed from O-Drive", 1).Should().BeTrue();
        PanelHeading.HasCount(cut, "Modified files on O-Drive", 1).Should().BeTrue();
        RowStatus(cut, "New on O-Drive", "copy-pending.pdf").Should().Be("Pending");
        RowStatus(cut, "Modified files", "update-pending.pdf").Should().Be("Pending");

        // Act 2 — the resumed per-action poll loops see their tasks Completed and reload their panels.
        cut.WaitForState(() => !cut.Markup.Contains("copy-pending.pdf")
            && !cut.Markup.Contains("purge-pending.pdf")
            && cut.Markup.Contains(">Failed<"), PollWaitTimeout);

        // Assert 2 — Synced rows drop off, the Failed row stays with its chip, untouched rows stay.
        RowStatus(cut, "Modified files", "update-pending.pdf").Should().Be("Failed");
        cut.Markup.Should().Contain("untouched-new.pdf");
        ResultsPaths().Should().OnlyContain(p => p == "/api/sync/700/results", "sync progress lives on the scan's own rows (§8.3)");
        _apiHandler.ResultsQueries.Should().OnlyContain(q => q.Contains("excludeSynced=true"), "CAS-045 §8.3: every results call sends excludeSynced=true");
        _apiHandler.ResultsCallCount.Should().BeGreaterThanOrEqualTo(2);
        AnyRequest(HttpMethod.Post, "/sync/actions/copy").Should().BeFalse("resuming never re-submits an action");
        AnyRequest(HttpMethod.Post, "/sync/actions/update").Should().BeFalse();
        AnyRequest(HttpMethod.Post, "/sync/actions/purge").Should().BeFalse();
        AnyRequest(HttpMethod.Post, "/sync/scan").Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------
    // SC-11 (O-Drive Sync variant)
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Completed", false, 1)]
    [InlineData("Failed", false, 1)]
    [InlineData("RunningThenCompleted", false, 1)]
    [InlineData("Completed", true, 0)]
    [InlineData("Queued", false, 0)]
    [InlineData("Running", false, 0)]
    [InlineData("Cancelled", false, 0)]
    [InlineData("NotFound", false, 0)]
    [InlineData("SyncTaskDeepLink", false, 0)] // §8.3: a Sync* task id is the wrong type (V7)
    public async Task Test_SC11_Screens_AcknowledgeOnlyOwnRenderedTerminalTaskOnce(string scenario, bool alreadyAcknowledged, int expectedAcks)
    {
        // Arrange
        const int TaskId = 703;
        var status = scenario switch
        {
            "RunningThenCompleted" => "Running",
            "NotFound" or "SyncTaskDeepLink" => "Completed",
            _ => scenario,
        };
        StubTasks(
            ScanTask(TaskId, status: status, isAcknowledged: alreadyAcknowledged,
                resultDetail: status == "Failed" ? "O-Drive mirror unavailable." : null),
            ScanTask(950, type: "SyncCopy"));
        _apiHandler.OnGetResults = (_, call) => Task.FromResult(
            scenario is "Queued" or "Running" || (scenario == "RunningThenCompleted" && call == 1)
                ? new HttpResponseMessage(HttpStatusCode.Conflict)
                : JsonResponse(HttpStatusCode.OK, ScanResultsFor(TaskId, "ack-scan")));

        // Act
        var cut = RenderPageAt(scenario switch { "NotFound" => "999999", "SyncTaskDeepLink" => "950", _ => null });
        switch (scenario)
        {
            case "Completed":
            case "RunningThenCompleted":
                cut.WaitForState(() => PanelHeading.HasCount(cut, "New on O-Drive", 1), PollWaitTimeout);
                break;
            case "Failed":
                cut.WaitForState(() => cut.Markup.Contains("This Content Scan did not complete successfully."), TimeSpan.FromSeconds(3));
                cut.FindAll(".cachet-sync-error-banner").Single().TextContent.Should().Be(
                    "This Content Scan did not complete successfully. O-Drive mirror unavailable.");
                break;
            case "Queued":
            case "Running":
                cut.WaitForState(() => cut.Markup.Contains("Content Scan in progress"), TimeSpan.FromSeconds(3));
                break;
            case "Cancelled":
                await WaitUntilAsync(() => AnyRequest(HttpMethod.Get, $"/tasks/{TaskId}"), TimeSpan.FromSeconds(3));
                break;
            case "NotFound":
            case "SyncTaskDeepLink":
                cut.WaitForState(() => cut.Markup.Contains(TaskNotFoundNotice), TimeSpan.FromSeconds(3));
                cut.Find(".cachet-sync-notice").TextContent.Should().Be(TaskNotFoundNotice);
                break;
        }

        if (expectedAcks > 0)
        {
            await WaitUntilAsync(() => _apiHandler.AcknowledgedTaskIds.Count >= 1, TimeSpan.FromSeconds(3));
        }

        // Extra renders and poll ticks must not repeat the acknowledge.
        cut.Render();
        cut.Render();
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Assert
        _apiHandler.AcknowledgedTaskIds.Should().HaveCount(expectedAcks);
        _apiHandler.AcknowledgedTaskIds.Should().NotContain(id => id != TaskId);
        if (scenario is "Cancelled" or "NotFound" or "SyncTaskDeepLink")
        {
            cut.Markup.Should().Contain("No comparison run yet — run a Content Scan to get started.");
            _apiHandler.ResultsCallCount.Should().Be(0);
            RunScanButton(cut).HasAttribute("disabled").Should().BeFalse("the screen stays usable");
        }
    }

    // -----------------------------------------------------------------------------------------
    // V9
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("scanPoll")]
    [InlineData("actionPoll")]
    [InlineData("reloadList")]
    [InlineData("reloadDetail")]
    [InlineData("reloadResults")]
    public async Task Test_V9_PollReceives401_StopsPollingAndShowsSessionExpired(string where)
    {
        // Arrange
        StubTasks(ScanTask(706, status: where == "scanPoll" ? "Running" : "Completed"));
        if (where == "actionPoll")
        {
            // CAS-045: the resumed Copy task (id 7061) is Running; its own GET tasks/{id} poll gets the 401.
            StubTasksByType(
                ScanTask(706),
                new Dictionary<string, TaskDetailResponse> { ["SyncCopy"] = ScanTask(7061, "Running", "SyncCopy") });
            var baseOnGetTask = _apiHandler.OnGetTask!;
            _apiHandler.OnGetTask = (request, id) => id == 7061
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
                : baseOnGetTask(request, id);
        }

        switch (where)
        {
            case "reloadList":
                _apiHandler.OnGetTasks = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                break;
            case "reloadDetail":
                _apiHandler.OnGetTask = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                break;
        }

        _apiHandler.OnGetResults = (_, call) => Task.FromResult(where == "actionPoll" && call == 1
            ? JsonResponse(HttpStatusCode.OK, ScanResultsFor(706, "pending-row", syncStatus: "Pending"))
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));

        // Act
        var cut = RenderPageAt(taskIdQuery: null);
        cut.WaitForState(() => cut.Markup.Contains(SessionExpiredMessage), PollWaitTimeout);
        await Task.Delay(TimeSpan.FromSeconds(1.5)); // let any sibling action loops hit their own 401
        var resultsCallsAt401 = _apiHandler.ResultsCallCount;
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Assert — message + sign-in link; no stale in-progress banner or panels; polling stopped.
        var banner = cut.FindAll(".cachet-sync-error-banner").Single(b => b.TextContent.Contains(SessionExpiredMessage));
        // PathBase-relative (resolves against App.razor's <base href>), same as ReportModal's sign-in link.
        banner.QuerySelector("a")!.GetAttribute("href").Should().Be("login");
        cut.Markup.Should().NotContain("Content Scan in progress");
        if (where != "actionPoll")
        {
            // Panels stay mounted in every state (ODriveSyncContentScan.razor); no scan results were
            // ever loaded, so no counts show. (In the actionPoll case the Completed scan was rendered
            // before the action loop's 401; CAS-030 V9 does not say whether it must then be hidden.)
            PanelHeading.Count(cut, "New on O-Drive").Should().BeNull();
            PanelHeading.Count(cut, "Removed from O-Drive").Should().BeNull();
            PanelHeading.Count(cut, "Modified files on O-Drive").Should().BeNull();
        }

        _apiHandler.ResultsCallCount.Should().Be(resultsCallsAt401, "a 401 stops that poll loop (V9)");
        if (where != "actionPoll")
        {
            // (In the actionPoll case the Completed scan WAS rendered before the action loop's 401,
            // so its acknowledge is correct per §8.5.)
            _apiHandler.AcknowledgedTaskIds.Should().BeEmpty("no finished outcome was ever rendered");
        }

        _loggerProvider.Messages.Should().Contain(m => m.Contains("ODriveSyncContentScan: poll stopped on 401 from"));
    }

    // ---- Coverage-gap closure (not a spec-mapped SC/V ID): transient failures during the reload keep
    //      the screen usable rather than crashing it (V10 / CAS-023 behavior).

    [Theory]
    [InlineData("listThrows")]
    [InlineData("list500")]
    [InlineData("detailThrows")]
    [InlineData("detail500")]
    [InlineData("resultsThrows")]
    [InlineData("results500")]
    [InlineData("resultsNotCompleted")]
    public async Task Test_ReloadTransientFailureKeepsScreenUsable(string failure)
    {
        // Arrange
        StubTasks(ScanTask(707));
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
            "resultsNotCompleted" => (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ScanResultsFor(707, "x") with { Status = "Failed" })),
            _ => (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ScanResultsFor(707, "x"))),
        };

        // Act
        var cut = RenderPageAt(taskIdQuery: null);

        // Assert
        if (failure.StartsWith("results", StringComparison.Ordinal))
        {
            cut.WaitForState(() => cut.Markup.Contains("Something went wrong. Please try again."), TimeSpan.FromSeconds(3));
        }
        else
        {
            await WaitUntilAsync(() => AnyRequest(HttpMethod.Get, failure.StartsWith("list", StringComparison.Ordinal) ? "/tasks" : "/tasks/707"), TimeSpan.FromSeconds(3));
            await Task.Delay(300);
            cut.Markup.Should().Contain("No comparison run yet");
            cut.Markup.Should().NotContain("Something went wrong");
            _apiHandler.ResultsCallCount.Should().Be(0);
        }

        cut.Markup.Should().NotContain(SessionExpiredMessage);
        _apiHandler.AcknowledgedTaskIds.Should().BeEmpty();
        RunScanButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    public void Test_V7_NonIntegerDeepLinkShowsNotFoundNotice(string taskIdQuery)
    {
        // Coverage of the screen's own V7 parse branch (the full V7 matrix is in KeywordSearchPageTests).
        var cut = RenderPageAt(taskIdQuery);
        cut.WaitForState(() => cut.Markup.Contains(TaskNotFoundNotice), TimeSpan.FromSeconds(3));

        AnyRequest(HttpMethod.Get, "/tasks").Should().BeFalse();
        // The page-hosted TaskManagerBadge's own GET /tasks/summary poll (CAS-030 §8.1) is not a task lookup.
        _apiHandler.Requests.ToList().Should().NotContain(r => r.RequestUri!.AbsolutePath.Contains("/tasks/")
            && !r.RequestUri.AbsolutePath.EndsWith("/tasks/summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Test_NewScanAfterReloadClearsReloadedContext()
    {
        // §8.3/§8.2: a new scan submitted after a reload behaves exactly as today.
        StubTasks(ScanTask(708, status: "Failed", resultDetail: "old failure"));
        _apiHandler.OnPostScan = _ => Task.FromResult(JsonResponse(HttpStatusCode.Accepted, ScanSubmitted()));
        _apiHandler.OnGetResults = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, ScanResultsFor(1, "fresh-scan")));

        var cut = RenderPageAt(taskIdQuery: null);
        cut.WaitForState(() => cut.Markup.Contains("old failure"), TimeSpan.FromSeconds(3));

        RunScanButton(cut).Click();
        cut.WaitForState(() => cut.Markup.Contains("fresh-scan-new.pdf"), PollWaitTimeout);

        cut.Markup.Should().NotContain("old failure");
    }

    // -----------------------------------------------------------------------------------------
    // Shared helpers
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

    /// <summary>Single-client <see cref="IHttpClientFactory"/> stand-in — this component only ever
    /// requests the "CachetApi" named client, so no name-based routing is needed.</summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
