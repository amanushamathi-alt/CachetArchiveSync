using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using Bunit;
using Cachet.ArchiveSync.Core.Dtos;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cachet.ArchiveSync.Web.Tests.ODriveSync;

/// <summary>
/// CAS-045 §13 — cumulative, cross-page selection on <c>ODriveSyncContentScan.razor</c>: V5 (header
/// checkbox = current page only), V6 (cap at <c>ODriveSync:MaxSelectionPerAction</c>, default 100 — the
/// §14 Q3 guard), V7 (selection survives page and page-size changes; Clear selection), SC-6 (actions send
/// all selected ids) and V8 (Purge keeps its explicit confirmation and shows the cross-page count).
/// </summary>
public sealed class ODriveSyncSelectionTests : ODriveSyncPagedTestBase
{
    private static void Act(IRenderedFragment cut, Action action) =>
        cut.InvokeAsync(action).GetAwaiter().GetResult();

    private static void CheckRow(IRenderedFragment cut, string heading, int rowIndex, bool isChecked = true) =>
        Act(cut, () => RowCheckboxes(cut, heading)[rowIndex].Change(isChecked));

    private static void SetHeader(IRenderedFragment cut, string heading, bool isChecked) =>
        Act(cut, () => HeaderCheckbox(cut, heading).Change(isChecked));

    private static string ButtonText(IRenderedFragment cut, string heading, string label) =>
        Normalize(ActionButton(cut, heading, label).TextContent);

    private static void WaitForFirstRow(IRenderedFragment cut, string heading, string name) =>
        cut.WaitForState(() => RowNames(cut, heading).FirstOrDefault() == name && Panel(cut, heading).QuerySelector(".is-loading") is null, TimeSpan.FromSeconds(5));

    private void NextPage(IRenderedFragment cut, string heading, int page, string firstRowName)
    {
        GoToPage(cut, heading, page);
        WaitForFirstRow(cut, heading, firstRowName);
    }

    private static bool IsChecked(IElement checkbox) => checkbox.HasAttribute("checked");

    // -----------------------------------------------------------------------------------------
    // V5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V5_HeaderCheckboxSelectsCurrentPageOnly()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 120, modifiedTotal: 0);

        // Page 1: header selects exactly the 50 loaded rows, not all 120.
        SetHeader(cut, NewHeading, true);
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (50)");
        IsChecked(HeaderCheckbox(cut, NewHeading)).Should().BeTrue();

        // Page 2: its rows are unselected, the header reflects THIS page, page 1's selection is kept.
        NextPage(cut, NewHeading, 2, FakeODriveSyncApiHandler.NewName(51));
        IsChecked(HeaderCheckbox(cut, NewHeading)).Should().BeFalse();
        RowCheckboxes(cut, NewHeading).Should().OnlyContain(c => !IsChecked(c));
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (50)");

        SetHeader(cut, NewHeading, true);
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (100)");

        // Deselecting on page 2 leaves page 1's 50 selections untouched.
        SetHeader(cut, NewHeading, false);
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (50)");

        // Back on page 1 its rows are still checked.
        NextPage(cut, NewHeading, 1, FakeODriveSyncApiHandler.NewName(1));
        RowCheckboxes(cut, NewHeading).Should().OnlyContain(c => IsChecked(c));
        IsChecked(HeaderCheckbox(cut, NewHeading)).Should().BeTrue();
    }

    // -----------------------------------------------------------------------------------------
    // V6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V6_SelectionCappedAtBatchSize()
    {
        // Default cap (100 = Api Sync:MaxBatchSize): 300 New rows at 100 per page.
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 300, modifiedTotal: 0);
        ChangePageSize(cut, NewHeading, "100");
        cut.WaitForState(() => RowNames(cut, NewHeading).Count == 100, TimeSpan.FromSeconds(5));

        SetHeader(cut, NewHeading, true);

        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (100)");
        Panel(cut, NewHeading).QuerySelector(".cachet-sync-selection-notice")!.TextContent.Trim()
            .Should().Be("Up to 100 files can be submitted at once.");

        // Page 2: at the cap every unchecked box (and the header) is disabled — no 101st selection is possible.
        NextPage(cut, NewHeading, 2, FakeODriveSyncApiHandler.NewName(101));
        RowCheckboxes(cut, NewHeading).Should().HaveCount(100).And.OnlyContain(c => c.HasAttribute("disabled"));
        HeaderCheckbox(cut, NewHeading).HasAttribute("disabled").Should().BeTrue();
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (100)");

        // Back on page 1, the selected (checked) rows stay enabled so they can be unchecked.
        NextPage(cut, NewHeading, 1, FakeODriveSyncApiHandler.NewName(1));
        RowCheckboxes(cut, NewHeading).Should().OnlyContain(c => !c.HasAttribute("disabled"));
        CheckRow(cut, NewHeading, 0, isChecked: false);
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (99)");
        Panel(cut, NewHeading).QuerySelector(".cachet-sync-selection-notice").Should().BeNull("below the cap the notice is gone");
    }

    [Fact]
    public void Test_V6_SelectionCapHonoursConfiguredValue()
    {
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ODriveSync:MaxSelectionPerAction"] = "3" }).Build());
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 120, modifiedTotal: 0);

        // The header checkbox stops adding at the cap rather than selecting all 50 rows.
        SetHeader(cut, NewHeading, true);

        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (3)");
        Panel(cut, NewHeading).QuerySelector(".cachet-sync-selection-notice")!.TextContent.Trim()
            .Should().Be("Up to 3 files can be submitted at once.");
        RowCheckboxes(cut, NewHeading).Count(c => c.HasAttribute("disabled")).Should().Be(47);
    }

    /// <summary>CAS-045 §14 Q3 guard: the Web default cap must equal the Api's <c>Sync:MaxBatchSize</c>
    /// default of 100 (Api <c>SyncActionService</c> falls back to 100 when unset).</summary>
    [Fact]
    public void Test_V6_DefaultMaxSelectionPerActionEqualsApiBatchSize100()
    {
        // 1) The shipped Web appsettings.json value.
        var webSettings = ReadJson(FindSource("Cachet.ArchiveSync.Web", "appsettings.json"));
        webSettings.GetProperty("ODriveSync").GetProperty("MaxSelectionPerAction").GetInt32().Should().Be(100);

        // 2) The Api's configured batch size is the same number.
        var apiSettings = ReadJson(FindSource("Cachet.ArchiveSync.Api", "appsettings.Development.json"));
        apiSettings.GetProperty("Sync").GetProperty("MaxBatchSize").GetInt32().Should().Be(100);

        // 3) With NO configuration at all, the page's built-in fallback is also 100 (cap notice at 100).
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 150, modifiedTotal: 0);
        ChangePageSize(cut, NewHeading, "100");
        cut.WaitForState(() => RowNames(cut, NewHeading).Count == 100, TimeSpan.FromSeconds(5));
        SetHeader(cut, NewHeading, true);
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (100)");
    }

    private static System.Text.Json.JsonElement ReadJson(string path) =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(path), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })
            .RootElement.Clone();

    private static string FindSource(string project, string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, project, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"{project}/{file} not found above {AppContext.BaseDirectory}");
    }

    // -----------------------------------------------------------------------------------------
    // V7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V7_SelectionPersistsAcrossPagesAndClearWorks()
    {
        var cut = RenderCompletedScan(removedTotal: 120, newTotal: 120, modifiedTotal: 0);

        // Two rows on page 1 of New and one in Removed.
        CheckRow(cut, NewHeading, 0);
        CheckRow(cut, NewHeading, 1);
        CheckRow(cut, RemovedHeading, 0);
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (2)");

        // Page change: selection persists and grows with a row picked on page 2.
        NextPage(cut, NewHeading, 2, FakeODriveSyncApiHandler.NewName(51));
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (2)");
        CheckRow(cut, NewHeading, 0);
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (3)");

        // Page-size change (resets to page 1): selection persists, page-1 rows still show as checked.
        ChangePageSize(cut, NewHeading, "25");
        cut.WaitForState(() => RowNames(cut, NewHeading).Count == 25 && RowNames(cut, NewHeading)[0] == FakeODriveSyncApiHandler.NewName(1), TimeSpan.FromSeconds(5));
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (3)");
        IsChecked(RowCheckboxes(cut, NewHeading)[0]).Should().BeTrue();
        IsChecked(RowCheckboxes(cut, NewHeading)[1]).Should().BeTrue();
        IsChecked(RowCheckboxes(cut, NewHeading)[2]).Should().BeFalse();

        // Clear selection empties only that panel.
        Act(cut, () => ClickClearSelection(cut, NewHeading));
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (0)");
        ButtonText(cut, RemovedHeading, "Purge from B2").Should().Contain("Purge from B2 (1)");
        RowCheckboxes(cut, NewHeading).Should().OnlyContain(c => !IsChecked(c));
        Panel(cut, NewHeading).QuerySelectorAll("button").Should().NotContain(b => b.TextContent.Trim() == "Clear selection");
        Panel(cut, RemovedHeading).QuerySelectorAll("button").Should().Contain(b => b.TextContent.Trim() == "Clear selection");
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC6_CrossPageSelectionSubmitsAllSelectedIds()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 120, modifiedTotal: 0);
        var baseOnGetTask = ApiHandler.OnGetTask!;
        ApiHandler.OnGetTask = (request, id) => id == 7000
            ? Task.FromResult(Json(HttpStatusCode.OK, TaskDetail(7000, "SyncCopy", "Running")))
            : baseOnGetTask(request, id);
        SyncActionRequest? captured = null;
        ApiHandler.OnPostCopy = async request =>
        {
            captured = await request.Content!.ReadFromJsonAsync<SyncActionRequest>();
            return Json(HttpStatusCode.Accepted, Submitted("SyncCopy", 7000));
        };

        // One row on page 1, then one on page 2: the button shows the cumulative total.
        CheckRow(cut, NewHeading, 0);
        NextPage(cut, NewHeading, 2, FakeODriveSyncApiHandler.NewName(51));
        CheckRow(cut, NewHeading, 0);
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (2)");

        Act(cut, () => ActionButton(cut, NewHeading, "Copy to B2").Click());
        cut.WaitForState(() => captured is not null, TimeSpan.FromSeconds(5));

        captured!.TaskId.Should().Be(ScanTaskId, "the request carries the scan's task id");
        captured.FileIds.Should().BeEquivalentTo(new[] { FakeODriveSyncApiHandler.NewIdBase + 1, FakeODriveSyncApiHandler.NewIdBase + 51 });
        cut.WaitForState(() => ButtonText(cut, NewHeading, "Copy to B2").Contains("Copy to B2 (0)"), TimeSpan.FromSeconds(5));
    }

    // -----------------------------------------------------------------------------------------
    // V8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V8_PurgeStillRequiresExplicitConfirmWithCrossPageCount()
    {
        var cut = RenderCompletedScan(removedTotal: 120, newTotal: 0, modifiedTotal: 0);
        var baseOnGetTask = ApiHandler.OnGetTask!;
        ApiHandler.OnGetTask = (request, id) => id == 7001
            ? Task.FromResult(Json(HttpStatusCode.OK, TaskDetail(7001, "SyncPurge", "Running")))
            : baseOnGetTask(request, id);
        SyncPurgeRequest? captured = null;
        ApiHandler.OnPostPurge = async request =>
        {
            captured = await request.Content!.ReadFromJsonAsync<SyncPurgeRequest>();
            return Json(HttpStatusCode.Accepted, Submitted("SyncPurge", 7001));
        };

        // One Removed row on each of two pages.
        CheckRow(cut, RemovedHeading, 0);
        NextPage(cut, RemovedHeading, 2, FakeODriveSyncApiHandler.RemovedName(51));
        CheckRow(cut, RemovedHeading, 0);
        ButtonText(cut, RemovedHeading, "Purge from B2").Should().Contain("Purge from B2 (2)");

        // Opening the dialog shows the exact cross-page count and submits nothing.
        Act(cut, () => ActionButton(cut, RemovedHeading, "Purge from B2").Click());
        Normalize(cut.Find(".cachet-confirm").TextContent).Should()
            .Contain("This will permanently delete 2 file(s) from Backblaze B2. This action cannot be undone.");
        captured.Should().BeNull();
        ApiHandler.Requests.ToList().Should().NotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/sync/actions/purge", StringComparison.OrdinalIgnoreCase));

        // Cancel: nothing sent, selection (both pages) intact.
        Act(cut, () => cut.FindAll(".cachet-confirm button").First(b => b.TextContent.Trim() == "Cancel").Click());
        cut.FindAll(".cachet-confirm").Should().BeEmpty();
        captured.Should().BeNull();
        ButtonText(cut, RemovedHeading, "Purge from B2").Should().Contain("Purge from B2 (2)");

        // Only the explicit Confirm submits, with confirm=true and both ids.
        Act(cut, () => ActionButton(cut, RemovedHeading, "Purge from B2").Click());
        Act(cut, () => cut.FindAll(".cachet-confirm button").First(b => b.TextContent.Trim() == "Confirm").Click());
        cut.WaitForState(() => captured is not null, TimeSpan.FromSeconds(5));

        captured!.Confirm.Should().BeTrue();
        captured.TaskId.Should().Be(ScanTaskId);
        captured.FileIds.Should().BeEquivalentTo(new[] { FakeODriveSyncApiHandler.RemovedIdBase + 1, FakeODriveSyncApiHandler.RemovedIdBase + 51 });
    }
}
