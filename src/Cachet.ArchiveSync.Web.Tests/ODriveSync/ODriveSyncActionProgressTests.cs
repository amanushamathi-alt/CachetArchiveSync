using System.Net;
using Bunit;
using FluentAssertions;

namespace Cachet.ArchiveSync.Web.Tests.ODriveSync;

/// <summary>
/// CAS-045 §13 — action progress with paging on <c>ODriveSyncContentScan.razor</c>: SC-7 (progress is
/// tracked through the action's own task, <c>GET tasks/{id}</c>; on Completed the affected panel's current
/// page reloads and its TotalCount refreshes) and SC-12 (the page always sends <c>excludeSynced=true</c>;
/// after an action the badge/summary/range shrink to the remaining rows and an out-of-range current page
/// clamps to the new last page).
/// </summary>
public sealed class ODriveSyncActionProgressTests : ODriveSyncPagedTestBase
{
    private static void Act(IRenderedFragment cut, Action action) =>
        cut.InvokeAsync(action).GetAwaiter().GetResult();

    private static string ButtonText(IRenderedFragment cut, string heading, string label) =>
        Normalize(ActionButton(cut, heading, label).TextContent);

    private void WaitForFirstRow(IRenderedFragment cut, string heading, string name) =>
        cut.WaitForState(() => RowNames(cut, heading).FirstOrDefault() == name && Panel(cut, heading).QuerySelector(".is-loading") is null, TimeSpan.FromSeconds(5));

    private void GoTo(IRenderedFragment cut, string heading, int page, string firstRowName)
    {
        GoToPage(cut, heading, page);
        WaitForFirstRow(cut, heading, firstRowName);
    }

    // -----------------------------------------------------------------------------------------
    // SC-7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC7_ActionCompletionTrackedByActionTaskAndPageReloaded()
    {
        var cut = RenderCompletedScan(removedTotal: 0, newTotal: 150, modifiedTotal: 0);
        const int ActionTaskId = 7000;

        // The action task is Queued, then Running, then Completed; on Completed one New row is gone.
        var baseOnGetTask = ApiHandler.OnGetTask!;
        var actionPolls = 0;
        ApiHandler.OnGetTask = (request, id) =>
        {
            if (id != ActionTaskId)
            {
                return baseOnGetTask(request, id);
            }

            var status = ++actionPolls switch { 1 => "Queued", 2 => "Running", _ => "Completed" };
            if (status == "Completed")
            {
                ApiHandler.NewTotal = 149;
            }

            return Task.FromResult(Json(HttpStatusCode.OK, TaskDetail(ActionTaskId, "SyncCopy", status)));
        };
        ApiHandler.OnPostCopy = _ => Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncCopy", ActionTaskId)));

        // The selected row lives on page 2 — never on page 1, so page-1 matching could not have seen it.
        GoTo(cut, NewHeading, 2, FakeODriveSyncApiHandler.NewName(51));
        Act(cut, () => RowCheckboxes(cut, NewHeading)[0].Change(true));
        var queriesBeforeSubmit = ApiHandler.ResultsQueries.Count;

        Act(cut, () => ActionButton(cut, NewHeading, "Copy to B2").Click());

        // Completed -> the badge refreshes to the remaining total.
        cut.WaitForState(() => PanelHeading.Count(cut, NewHeading) == "149", PollWaitTimeout);
        cut.WaitForState(() => Panel(cut, NewHeading).QuerySelector(".is-loading") is null, TimeSpan.FromSeconds(5));

        // Progress was tracked through the ACTION's own task, polled until terminal.
        actionPolls.Should().BeGreaterThanOrEqualTo(3);
        ApiHandler.Requests.ToList().Count(r => r.Method == HttpMethod.Get
            && r.RequestUri!.AbsolutePath.EndsWith($"/tasks/{ActionTaskId}", StringComparison.Ordinal)).Should().BeGreaterThanOrEqualTo(3);

        // Exactly one reload after the submit, of the New panel's CURRENT page (2) only — the scan results
        // were not re-read during the Queued/Running ticks.
        var reloads = ApiHandler.ResultsQueries.Skip(queriesBeforeSubmit).ToList();
        reloads.Should().ContainSingle();
        reloads[0].Should().Contain("newOnODrivePage=2&newOnODrivePageSize=50").And.Contain("excludeSynced=true")
            .And.NotContain("removedFromODrive").And.NotContain("modifiedOnODrive");

        // TotalCount refreshed everywhere it is shown.
        Normalize(SummaryText(cut)).Should().Contain("New on O-Drive 149");
        RangeText(cut, NewHeading).Should().Be("Showing 51–100 of 149");

        // The buttons re-enable: no permanent in-flight state although the selection was on a non-first page.
        Act(cut, () => RowCheckboxes(cut, NewHeading)[1].Change(true));
        cut.WaitForState(() => !ActionButton(cut, NewHeading, "Copy to B2").HasAttribute("disabled"), TimeSpan.FromSeconds(5));
        ButtonText(cut, NewHeading, "Copy to B2").Should().Contain("Copy to B2 (1)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-12
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC12_SyncedRowsExcludedAndTotalsShrinkAfterAction()
    {
        var cut = RenderCompletedScan(removedTotal: 3106, newTotal: 0, modifiedTotal: 0);
        var baseOnGetTask = ApiHandler.OnGetTask!;
        var purgeTaskIds = new Dictionary<int, int>(); // action task id -> Removed total once it has completed
        var submittedPurges = 0;
        ApiHandler.OnGetTask = (request, id) =>
        {
            if (!purgeTaskIds.TryGetValue(id, out var totalAfter))
            {
                return baseOnGetTask(request, id);
            }

            ApiHandler.RemovedTotal = totalAfter; // the purged rows are Synced now -> excluded server-side
            return Task.FromResult(Json(HttpStatusCode.OK, TaskDetail(id, "SyncPurge", "Completed")));
        };
        ApiHandler.OnPostPurge = _ =>
        {
            var id = 7100 + ++submittedPurges;
            purgeTaskIds[id] = submittedPurges == 1 ? 3006 : 3000;
            return Task.FromResult(Json(HttpStatusCode.Accepted, Submitted("SyncPurge", id)));
        };

        ChangePageSize(cut, RemovedHeading, "100");
        cut.WaitForState(() => RowNames(cut, RemovedHeading).Count == 100, TimeSpan.FromSeconds(5));

        // --- Purge the 100 rows of page 1: 3,106 - 100 = 3,006 remaining. ---
        Act(cut, () => HeaderCheckbox(cut, RemovedHeading).Change(true));
        Act(cut, () => ActionButton(cut, RemovedHeading, "Purge from B2").Click());
        Act(cut, () => cut.FindAll(".cachet-confirm button").First(b => b.TextContent.Trim() == "Confirm").Click());

        cut.WaitForState(() => PanelHeading.Count(cut, RemovedHeading) == "3,006", PollWaitTimeout);
        cut.WaitForState(() => Panel(cut, RemovedHeading).QuerySelector(".is-loading") is null, TimeSpan.FromSeconds(5));
        Normalize(SummaryText(cut)).Should().Contain("Removed from O-Drive 3,006");
        RangeText(cut, RemovedHeading).Should().Be("Showing 1–100 of 3,006");

        // --- Purge the last page (31: rows 3,001-3,006) so the current page falls out of range. ---
        GoTo(cut, RemovedHeading, 31, FakeODriveSyncApiHandler.RemovedName(3001));
        RowNames(cut, RemovedHeading).Should().HaveCount(6);
        Act(cut, () => HeaderCheckbox(cut, RemovedHeading).Change(true));
        var queriesBeforeSecondPurge = ApiHandler.ResultsQueries.Count;
        Act(cut, () => ActionButton(cut, RemovedHeading, "Purge from B2").Click());
        Act(cut, () => cut.FindAll(".cachet-confirm button").First(b => b.TextContent.Trim() == "Confirm").Click());

        // 3,000 remaining = 30 pages, so page 31 clamps to page 30 and re-fetches once.
        cut.WaitForState(() => PanelHeading.Count(cut, RemovedHeading) == "3,000"
            && RangeText(cut, RemovedHeading) == "Showing 2,901–3,000 of 3,000", PollWaitTimeout);
        cut.WaitForState(() => Panel(cut, RemovedHeading).QuerySelector(".is-loading") is null, TimeSpan.FromSeconds(5));

        var reloads = ApiHandler.ResultsQueries.Skip(queriesBeforeSecondPurge).ToList();
        reloads.Should().HaveCount(2, "the out-of-range current page is clamped and re-fetched exactly once");
        reloads[0].Should().Contain("removedFromODrivePage=31&removedFromODrivePageSize=100");
        reloads[1].Should().Contain("removedFromODrivePage=30&removedFromODrivePageSize=100");
        RowNames(cut, RemovedHeading).Should().HaveCount(100);
        RowNames(cut, RemovedHeading)[0].Should().Be(FakeODriveSyncApiHandler.RemovedName(2901));
        Normalize(SummaryText(cut)).Should().Contain("Removed from O-Drive 3,000");

        // The page sent excludeSynced=true on EVERY results call it made (initial load, paging, reloads).
        ApiHandler.ResultsQueries.Should().NotBeEmpty().And.OnlyContain(q => q.Contains("excludeSynced=true", StringComparison.Ordinal));
    }
}
