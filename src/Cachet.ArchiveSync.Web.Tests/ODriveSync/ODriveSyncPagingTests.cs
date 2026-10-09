using System.Net;
using Bunit;
using FluentAssertions;

namespace Cachet.ArchiveSync.Web.Tests.ODriveSync;

/// <summary>
/// CAS-045 §13 — per-panel paging and true totals on <c>ODriveSyncContentScan.razor</c>: SC-1, SC-2, SC-3,
/// SC-5, SC-8, SC-9 and V1, using the CAS-045 §1 reproduction (scan task 100003: Removed 3,106 /
/// New 77,627 / Modified 26). The fake Api serves paged slices honouring each panel's own
/// page/pageSize query parameters and records every results query string.
/// </summary>
public sealed class ODriveSyncPagingTests : ODriveSyncPagedTestBase
{
    private const int Removed = 3106;
    private const int New = 77_627;
    private const int Modified = 26;

    private static string Query(string newPage, string newSize) => $"newOnODrivePage={newPage}&newOnODrivePageSize={newSize}";

    // -----------------------------------------------------------------------------------------
    // SC-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC1_BadgesAndSummaryShowTotalCountNotLoadedCount()
    {
        var cut = RenderCompletedScan(Removed, New, Modified);

        // Header badges show TotalCount (thousands separators), not the 50/50/26 rows actually loaded.
        PanelHeading.Count(cut, RemovedHeading).Should().Be("3,106");
        PanelHeading.Count(cut, NewHeading).Should().Be("77,627");
        PanelHeading.Count(cut, ModifiedHeading).Should().Be("26");
        RowNames(cut, NewHeading).Should().HaveCount(50);
        RowNames(cut, RemovedHeading).Should().HaveCount(50);
        RowNames(cut, ModifiedHeading).Should().HaveCount(26);

        // The summary strip reads the same values and names the task.
        var summary = Normalize(SummaryText(cut));
        summary.Should().Contain("Removed from O-Drive 3,106");
        summary.Should().Contain("New on O-Drive 77,627");
        summary.Should().Contain("Modified on O-Drive 26");
        summary.Should().Contain("Remaining to review");
        summary.Should().Contain($"Task #{ScanTaskId}");

        // SC-12 (request side): the initial load asks for excludeSynced=true and each panel's own page/size.
        ApiHandler.ResultsQueries.Should().ContainSingle();
        ApiHandler.ResultsQueries[0].Should().Contain("excludeSynced=true")
            .And.Contain("newOnODrivePage=1&newOnODrivePageSize=50")
            .And.Contain("removedFromODrivePage=1&removedFromODrivePageSize=50")
            .And.Contain("modifiedOnODrivePage=1&modifiedOnODrivePageSize=50");
    }

    // -----------------------------------------------------------------------------------------
    // SC-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC2_ShowingRangeText()
    {
        var cut = RenderCompletedScan(Removed, New, Modified);

        RangeText(cut, NewHeading).Should().Be("Showing 1–50 of 77,627");
        RangeText(cut, RemovedHeading).Should().Be("Showing 1–50 of 3,106");

        // Last page of "New": 77,627 / 50 = 1,553 pages; the last holds rows 77,601–77,627.
        Panel(cut, NewHeading).QuerySelector(".cachet-pager-goto-input")!.Input("1553");
        Panel(cut, NewHeading).QuerySelectorAll(".cachet-pager-goto button").Single(b => b.TextContent.Trim() == "Go").Click();
        cut.WaitForState(() => RowNames(cut, NewHeading).Count == 27, TimeSpan.FromSeconds(5));
        RangeText(cut, NewHeading).Should().Be("Showing 77,601–77,627 of 77,627");

        RowNames(cut, NewHeading).Should().HaveCount(27);
        RowNames(cut, NewHeading)[0].Should().Be(FakeODriveSyncApiHandler.NewName(77_601));
        RowNames(cut, NewHeading)[^1].Should().Be(FakeODriveSyncApiHandler.NewName(77_627));
    }

    // -----------------------------------------------------------------------------------------
    // SC-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC3_PagingOnePanelDoesNotReloadOthers()
    {
        var cut = RenderCompletedScan(Removed, New, Modified);

        // Selections in the other two panels, made before paging "New".
        RowCheckboxes(cut, RemovedHeading)[0].Change(true);
        RowCheckboxes(cut, ModifiedHeading)[0].Change(true);
        var removedBefore = RowNames(cut, RemovedHeading);
        var modifiedBefore = RowNames(cut, ModifiedHeading);
        var queriesBefore = ApiHandler.ResultsQueries.Count;

        GoToPage(cut, NewHeading, 2);
        cut.WaitForState(() => RowNames(cut, NewHeading)[0] == FakeODriveSyncApiHandler.NewName(51), TimeSpan.FromSeconds(5));

        // Exactly one extra request, for the New panel only (page 2, size 50) — with excludeSynced=true.
        var newQueries = ApiHandler.ResultsQueries.Skip(queriesBefore).ToList();
        newQueries.Should().ContainSingle();
        newQueries[0].Should().Contain("newOnODrivePage=2&newOnODrivePageSize=50").And.Contain("excludeSynced=true")
            .And.NotContain("removedFromODrive").And.NotContain("modifiedOnODrive");

        // Only New's rows were replaced; Removed/Modified rows and their selections are untouched.
        RowNames(cut, NewHeading)[0].Should().Be(FakeODriveSyncApiHandler.NewName(51));
        RowNames(cut, RemovedHeading).Should().Equal(removedBefore);
        RowNames(cut, ModifiedHeading).Should().Equal(modifiedBefore);
        ActionButton(cut, RemovedHeading, "Purge from B2").TextContent.Should().Contain("Purge from B2 (1)");
        ActionButton(cut, ModifiedHeading, "Update in B2").TextContent.Should().Contain("Update in B2 (1)");
        PanelHeading.Count(cut, RemovedHeading).Should().Be("3,106");
    }

    // -----------------------------------------------------------------------------------------
    // SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC5_PageSizeChangeResetsToPage1()
    {
        var cut = RenderCompletedScan(Removed, New, Modified);
        GoToPage(cut, NewHeading, 3);
        cut.WaitForState(() => RowNames(cut, NewHeading)[0] == FakeODriveSyncApiHandler.NewName(101), TimeSpan.FromSeconds(5));
        var queriesBefore = ApiHandler.ResultsQueries.Count;

        ChangePageSize(cut, NewHeading, "25");
        cut.WaitForState(() => RowNames(cut, NewHeading).Count == 25, TimeSpan.FromSeconds(5));

        var queries = ApiHandler.ResultsQueries.Skip(queriesBefore).ToList();
        queries.Should().ContainSingle();
        queries[0].Should().Contain(Query("1", "25")).And.NotContain("removedFromODrive").And.NotContain("modifiedOnODrive");
        RowNames(cut, NewHeading).Should().HaveCount(25);
        Panel(cut, NewHeading).QuerySelector("select[aria-label='Rows per page']")!.GetAttribute("aria-label").Should().Be("Rows per page");
        Panel(cut, NewHeading).QuerySelectorAll("select option").Single(o => o.HasAttribute("selected")).TextContent.Should().Be("25");

        // The other panels keep their own page size.
        RangeText(cut, RemovedHeading).Should().Be("Showing 1–50 of 3,106");
    }

    // -----------------------------------------------------------------------------------------
    // V1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V1_PageSizeSelectorOffersOnly25_50_100()
    {
        var cut = RenderCompletedScan(Removed, New, Modified);

        foreach (var heading in new[] { RemovedHeading, NewHeading, ModifiedHeading })
        {
            var options = Panel(cut, heading).QuerySelectorAll("select[aria-label='Rows per page'] option");
            options.Select(o => o.TextContent.Trim()).Should().Equal("25", "50", "100");
            options.Single(o => o.HasAttribute("selected")).TextContent.Trim().Should().Be("50", "the default page size is 50");
        }

        // Any value other than 25/50/100 is never sent: no request, page size unchanged.
        var queriesBefore = ApiHandler.ResultsQueries.Count;
        foreach (var invalid in new[] { "75", "0", "-1", "1000", "abc", "" })
        {
            ChangePageSize(cut, NewHeading, invalid);
        }

        ApiHandler.ResultsQueries.Count.Should().Be(queriesBefore);
        RangeText(cut, NewHeading).Should().Be("Showing 1–50 of 77,627");

        // And every size the page does send is one of the three.
        ChangePageSize(cut, NewHeading, "100");
        cut.WaitForState(() => RowNames(cut, NewHeading).Count == 100, TimeSpan.FromSeconds(5));
        ApiHandler.ResultsQueries.SelectMany(q => System.Text.RegularExpressions.Regex.Matches(q, @"PageSize=(\d+)").Select(m => m.Groups[1].Value))
            .Distinct().Should().BeSubsetOf(new[] { "25", "50", "100" });
    }

    // -----------------------------------------------------------------------------------------
    // SC-8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC8_LoadingAndErrorStates()
    {
        var cut = RenderCompletedScan(Removed, New, Modified);
        var inner = ApiHandler.OnGetResults!;
        var gate = new TaskCompletionSource();
        var holdPage2 = true;
        var failPage3 = true;
        ApiHandler.OnGetResults = async (request, call) =>
        {
            var query = request.RequestUri!.Query;
            if (holdPage2 && query.Contains("newOnODrivePage=2", StringComparison.Ordinal))
            {
                await gate.Task;
            }

            if (failPage3 && query.Contains("newOnODrivePage=3", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return await inner(request, call);
        };
        var page1Rows = RowNames(cut, NewHeading);

        // Loading: rows dimmed (is-loading / aria-busy) with an inline spinner, pager disabled, rows kept.
        GoToPage(cut, NewHeading, 2);
        cut.WaitForState(() => Panel(cut, NewHeading).QuerySelector(".cachet-sync-panel-body-wrap")!.ClassList.Contains("is-loading"), TimeSpan.FromSeconds(5));
        var body = Panel(cut, NewHeading).QuerySelector(".cachet-sync-panel-body-wrap")!;
        body.GetAttribute("aria-busy").Should().Be("true");
        body.QuerySelector(".cachet-sync-spinner").Should().NotBeNull();
        PagerNav(cut, NewHeading)!.QuerySelectorAll("button").Should().OnlyContain(b => b.HasAttribute("disabled"));
        RowNames(cut, NewHeading).Should().Equal(page1Rows, "the panel keeps its previous rows while loading");
        // Other panels are not dimmed.
        Panel(cut, RemovedHeading).QuerySelector(".cachet-sync-panel-body-wrap")!.ClassList.Should().NotContain("is-loading");

        gate.SetResult();
        cut.WaitForState(() => RowNames(cut, NewHeading)[0] == FakeODriveSyncApiHandler.NewName(51)
            && !Panel(cut, NewHeading).QuerySelector(".cachet-sync-panel-body-wrap")!.ClassList.Contains("is-loading"), TimeSpan.FromSeconds(5));
        RangeText(cut, NewHeading).Should().Be("Showing 51–100 of 77,627");
        holdPage2 = false;
        var page2Rows = RowNames(cut, NewHeading);

        // Error: an inline retry appears, and the panel keeps its previous rows (not an empty panel).
        GoToPage(cut, NewHeading, 3);
        cut.WaitForState(() => Panel(cut, NewHeading).QuerySelector(".cachet-sync-panel-error") is not null, TimeSpan.FromSeconds(5));
        var error = Panel(cut, NewHeading).QuerySelector(".cachet-sync-panel-error")!;
        error.GetAttribute("role").Should().Be("alert");
        error.TextContent.Should().Contain("Could not load this page");
        RowNames(cut, NewHeading).Should().Equal(page2Rows);
        Panel(cut, NewHeading).QuerySelector(".cachet-sync-panel-body-wrap")!.ClassList.Should().NotContain("is-loading");
        Panel(cut, RemovedHeading).QuerySelector(".cachet-sync-panel-error").Should().BeNull();

        // Retry re-requests the same page and succeeds.
        failPage3 = false;
        Panel(cut, NewHeading).QuerySelectorAll(".cachet-sync-panel-error button").Single(b => b.TextContent.Trim() == "Retry").Click();
        cut.WaitForState(() => RowNames(cut, NewHeading)[0] == FakeODriveSyncApiHandler.NewName(101)
            && Panel(cut, NewHeading).QuerySelector(".cachet-sync-panel-error") is null, TimeSpan.FromSeconds(5));
        RangeText(cut, NewHeading).Should().Be("Showing 101–150 of 77,627");
        ApiHandler.ResultsQueries.Count(q => q.Contains("newOnODrivePage=3", StringComparison.Ordinal)).Should().Be(2);
    }

    // -----------------------------------------------------------------------------------------
    // SC-9
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC9_SinglePagePanelHasNoPager()
    {
        var cut = RenderCompletedScan(Removed, New, Modified);

        RangeText(cut, ModifiedHeading).Should().Be("Showing 1–26 of 26");
        PagerNav(cut, ModifiedHeading).Should().BeNull("TotalCount <= pageSize hides the pager");
        Panel(cut, ModifiedHeading).QuerySelector(".cachet-pager-goto").Should().BeNull();

        // The two large panels do show theirs.
        PagerNav(cut, NewHeading).Should().NotBeNull();
        PagerNav(cut, RemovedHeading).Should().NotBeNull();
    }
}
