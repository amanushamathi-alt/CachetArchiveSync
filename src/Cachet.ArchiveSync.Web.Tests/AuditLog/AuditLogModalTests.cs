using System.Net;
using AngleSharp.Dom;
using Bunit;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cachet.ArchiveSync.Web.Tests.AuditLog;

/// <summary>
/// CAS-031 §13 — proves V1..V6 and SC-3..SC-9 for <c>AuditLogModal.razor</c> via bUnit, with the
/// "CachetApi" named <see cref="HttpClient"/> redirected to <see cref="FakeAuditLogApiHandler"/> so no
/// real Api process/DB is ever reached — same conventions as <c>QuarantineQueueModalTests</c>
/// (CAS-024). SC-1/SC-10 (Keyword Search host) and SC-2 (O-Drive Sync host) live in
/// <c>KeywordSearchPageTests</c>/<c>ODriveSyncPageTests</c>, per §13's test-class mapping.
/// </summary>
public sealed class AuditLogModalTests : TestContext
{
    private const string LoadErrorMessage = "Unable to load the audit log. Please try again.";
    private const string EmptyStateCopy = "No audit log entries match the selected filters.";
    private const string DefaultQuery = "?page=1&pageSize=50";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(3);

    private readonly FakeAuditLogApiHandler _apiHandler = new();
    private readonly CapturingLoggerProvider _loggerProvider = new();

    public AuditLogModalTests()
    {
        var httpClient = new HttpClient(_apiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(httpClient));
        Services.AddLogging(builder => builder.AddProvider(_loggerProvider));
    }

    // -----------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------

    private static AuditLogListItem Item(
        string user = "reviewer1",
        string tab = "Keyword Search",
        string action = "Search",
        string? filePath = "/o-drive/2026/invoice.pdf",
        DateTimeOffset? timestamp = null,
        string result = "Success",
        string? resultDetail = null) => new()
        {
            User = user,
            Tab = tab,
            Action = action,
            FilePath = filePath,
            Timestamp = timestamp ?? new DateTimeOffset(2026, 9, 9, 14, 5, 7, TimeSpan.Zero),
            Result = result,
            ResultDetail = resultDetail,
        };

    private static IReadOnlyList<AuditLogListItem> Items(int count, string prefix = "user") =>
        Enumerable.Range(1, count).Select(i => Item(user: $"{prefix}-{i}")).ToArray();

    /// <summary>120 total rows: pages 1-2 return 50 rows, page 3 returns 20 (SC-6/V3/V4).</summary>
    private void Return120RowsPaged()
    {
        _apiHandler.OnGetAuditLog = (request, _) =>
        {
            var page = PageOf(request.RequestUri!);
            var count = page < 3 ? 50 : 20;
            return Task.FromResult(FakeAuditLogApiHandler.PageResponse(Items(count, $"p{page}"), 120, page));
        };
    }

    private static int PageOf(Uri uri) =>
        int.Parse(uri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("page=", StringComparison.Ordinal))["page=".Length..]);

    private IRenderedComponent<Web.Components.Shared.AuditLogModal> RenderModal() =>
        RenderComponent<Web.Components.Shared.AuditLogModal>();

    private static async Task OpenAsync(IRenderedComponent<Web.Components.Shared.AuditLogModal> cut) =>
        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

    private IReadOnlyList<string> AuditLogQueries() =>
        _apiHandler.Requests
            .Where(r => r.Uri.AbsolutePath.EndsWith("/audit-log", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Uri.Query)
            .ToList();

    private static IElement[] BodyRows(IRenderedComponent<Web.Components.Shared.AuditLogModal> cut) =>
        cut.FindAll(".cachet-al-table tbody tr").ToArray();

    private static string PagerText(IRenderedComponent<Web.Components.Shared.AuditLogModal> cut) =>
        cut.Find(".cachet-al-pager-text").TextContent;

    private static bool IsDisabled(IRenderedComponent<Web.Components.Shared.AuditLogModal> cut, string selector) =>
        cut.Find(selector).HasAttribute("disabled");

    // -----------------------------------------------------------------------------------------
    // SC-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC3_TableRendersSixFr23ColumnsMappedCorrectly()
    {
        // Arrange — server order (newest first); the first timestamp carries a +02:00 offset to prove
        // the UTC rendering (§8.3: timestamp.UtcDateTime).
        _apiHandler.ReturnPage(new[]
        {
            Item(user: "alice", tab: "Quarantine", action: "Permanent Delete", filePath: "/o-drive/a/contract.docx",
                timestamp: new DateTimeOffset(2026, 9, 9, 16, 5, 7, TimeSpan.FromHours(2)), result: "Success"),
            Item(user: "bob", tab: "Auth", action: "Login", filePath: null,
                timestamp: new DateTimeOffset(2026, 9, 8, 8, 0, 1, TimeSpan.Zero), result: "Success"),
        }, totalCount: 2);

        // Act
        var cut = RenderModal();
        await OpenAsync(cut);

        // Assert — exactly six headers in §8.3 order.
        cut.FindAll(".cachet-al-table thead th").Select(th => th.TextContent.Trim()).Should().Equal(
            "Timestamp (UTC)", "User", "Tab/Module", "Action", "File/Path", "Result/Detail");

        var rows = BodyRows(cut);
        rows.Should().HaveCount(2);

        var first = rows[0].QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ToArray();
        first.Should().HaveCount(6);
        first.Should().Equal("2026-09-09 14:05:07", "alice", "Quarantine", "Permanent Delete", "/o-drive/a/contract.docx", "Success");
        rows[0].QuerySelector(".cachet-al-path")!.GetAttribute("title").Should().Be("/o-drive/a/contract.docx");
        rows[0].QuerySelector(".cachet-al-chip")!.ClassList.Should().Contain("cachet-chip-success");

        var second = rows[1].QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ToArray();
        second.Should().Equal("2026-09-08 08:00:01", "bob", "Auth", "Login", "—", "Success");
        rows[1].QuerySelector(".cachet-al-path").Should().BeNull("a null filePath renders the em-dash placeholder, not a path span");
    }

    // -----------------------------------------------------------------------------------------
    // SC-4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC4_TabFilterSendsTabParam()
    {
        var cut = RenderModal();
        await OpenAsync(cut);
        _apiHandler.ReturnPage(new[] { Item(user: "quarantine-row-user", tab: "Quarantine", action: "Remove From Quarantine") }, totalCount: 1);

        cut.Find(".cachet-al-tab-select").Change("Quarantine");
        AuditLogQueries().Should().HaveCount(1, "changing the dropdown alone must not fetch (§8.2)");

        cut.Find(".cachet-al-apply").Click();
        cut.WaitForState(() => cut.FindAll(".cachet-al-table").Count > 0, WaitTimeout);

        AuditLogQueries().Should().HaveCount(2);
        AuditLogQueries()[1].Should().Be("?tab=Quarantine&page=1&pageSize=50");
        var cells = BodyRows(cut).Single().QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ToArray();
        cells[1].Should().Be("quarantine-row-user");
        cells[2].Should().Be("Quarantine");
        cells[3].Should().Be("Remove From Quarantine");
    }

    // -----------------------------------------------------------------------------------------
    // SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC5_DateRangeSendsInclusiveUtcBounds()
    {
        var cut = RenderModal();
        await OpenAsync(cut);

        cut.Find("#cachet-al-date-from").Change("2026-09-01");
        cut.Find("#cachet-al-date-to").Change("2026-09-09");
        cut.Find(".cachet-al-apply").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 2, WaitTimeout);

        AuditLogQueries()[1].Should().Be("?dateFrom=2026-09-01T00:00:00Z&dateTo=2026-09-09T23:59:59.9999999Z&page=1&pageSize=50");
        cut.Markup.Should().Contain("Times and dates are shown in UTC.");
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC6_PagerNavigatesWithAppliedFilters()
    {
        Return120RowsPaged();
        var cut = RenderModal();
        await OpenAsync(cut);

        // Apply a Tab filter, then leave an UNAPPLIED edit in the filter bar.
        cut.Find(".cachet-al-tab-select").Change("Quarantine");
        cut.Find(".cachet-al-apply").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 2 && cut.FindAll(".cachet-al-pager-text").Count > 0, WaitTimeout);
        PagerText(cut).Should().Be("Showing 1–50 of 120");

        cut.Find(".cachet-al-tab-select").Change("Auth");

        // Next → page 2 with the applied (not the edited) filter.
        cut.Find(".cachet-al-next").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 3 && PagerText(cut) == "Showing 51–100 of 120", WaitTimeout);
        AuditLogQueries()[2].Should().Be("?tab=Quarantine&page=2&pageSize=50");

        // Next → page 3: last page, Next disabled.
        cut.Find(".cachet-al-next").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 4 && PagerText(cut) == "Showing 101–120 of 120", WaitTimeout);
        AuditLogQueries()[3].Should().Be("?tab=Quarantine&page=3&pageSize=50");
        BodyRows(cut).Should().HaveCount(20);
        IsDisabled(cut, ".cachet-al-next").Should().BeTrue();
    }

    // -----------------------------------------------------------------------------------------
    // SC-7
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC7_EmptyResultShowsEmptyStateCopy()
    {
        _apiHandler.ReturnPage(Array.Empty<AuditLogListItem>(), totalCount: 0);

        var cut = RenderModal();
        await OpenAsync(cut);

        cut.Find(".cachet-al-empty").TextContent.Trim().Should().Be(EmptyStateCopy);
        cut.FindAll(".cachet-al-table").Should().BeEmpty("an empty result renders explanatory copy, not an empty table");
        cut.FindAll(".cachet-al-pager").Should().BeEmpty("the pager shows only when totalCount > 0 (§8.4)");
        cut.FindAll(".cachet-al-error-banner").Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // SC-8
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("500")]
    [InlineData("422")]
    [InlineData("401")]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task Test_SC8_LoadFailureShowsErrorAndClearsRows(string failure)
    {
        // Arrange — a first successful load puts rows on screen.
        _apiHandler.ReturnPage(new[] { Item(user: "stale-row-user") }, totalCount: 1);
        var cut = RenderModal();
        await OpenAsync(cut);
        BodyRows(cut).Should().HaveCount(1);

        switch (failure)
        {
            case "500": _apiHandler.ReturnStatus(HttpStatusCode.InternalServerError); break;
            case "422": _apiHandler.ReturnStatus(HttpStatusCode.UnprocessableEntity); break;
            case "401": _apiHandler.ReturnStatus(HttpStatusCode.Unauthorized); break;
            case "network": _apiHandler.Throw(); break;
            case "timeout": _apiHandler.OnGetAuditLog = (_, _) => throw new TaskCanceledException("simulated: timeout"); break;
        }

        // Act
        var act = () => cut.Find(".cachet-al-apply").Click();
        act.Should().NotThrow();
        cut.WaitForState(() => cut.FindAll(".cachet-al-error-banner").Count > 0, WaitTimeout);

        // Assert
        cut.Find(".cachet-al-error-banner").TextContent.Trim().Should().Be(LoadErrorMessage);
        cut.FindAll(".cachet-al-table").Should().BeEmpty("stale rows are never shown under a failed load (§8.5)");
        cut.Markup.Should().NotContain("stale-row-user");
        cut.FindAll(".cachet-al-pager").Should().BeEmpty();
        cut.FindAll(".cachet-al-empty").Should().BeEmpty("the failure state shows the banner, not the empty-state copy");
        IsDisabled(cut, ".cachet-al-apply").Should().BeFalse("Apply stays usable as the retry (§8.5)");
    }

    // -----------------------------------------------------------------------------------------
    // SC-9
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC9_ModalIssuesOnlyGetRequests()
    {
        Return120RowsPaged();
        var cut = RenderModal();

        // Every interaction the modal offers.
        await OpenAsync(cut);
        cut.Find(".cachet-al-tab-select").Change("Reports");
        cut.Find("#cachet-al-date-from").Change("2026-09-01");
        cut.Find("#cachet-al-date-to").Change("2026-09-09");
        cut.Find(".cachet-al-apply").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 2, WaitTimeout);
        cut.Find(".cachet-al-next").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 3, WaitTimeout);
        cut.Find(".cachet-al-prev").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 4, WaitTimeout);
        cut.Find(".cachet-al-clear").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 5, WaitTimeout);
        cut.Find(".cachet-al-close").Click();
        cut.Markup.Should().BeEmpty("the header × closes the modal");
        await OpenAsync(cut);
        cut.Find(".cachet-al-close-btn").Click();
        cut.Markup.Should().BeEmpty("the footer Close button closes the modal");

        // Assert
        _apiHandler.Requests.Should().HaveCount(6);
        _apiHandler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "the modal is read-only (UI/UX §6.2)");
        _apiHandler.Requests.Should().OnlyContain(r => r.Uri.AbsolutePath == "/api/audit-log");
    }

    // -----------------------------------------------------------------------------------------
    // V-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V1_ToBeforeFromBlocksApplyAndSendsNoRequest()
    {
        var cut = RenderModal();
        await OpenAsync(cut);
        cut.FindAll(".cachet-al-range-error").Should().BeEmpty();

        cut.Find("#cachet-al-date-from").Change("2026-09-10");
        cut.Find("#cachet-al-date-to").Change("2026-09-01");

        cut.Find(".cachet-al-range-error").TextContent.Trim().Should().Be("‘To’ date must be on or after ‘From’ date.");
        IsDisabled(cut, ".cachet-al-apply").Should().BeTrue();

        cut.Find(".cachet-al-apply").Click();

        AuditLogQueries().Should().Equal(new[] { DefaultQuery }, "an invalid range sends no request");
        cut.FindAll(".cachet-al-error-banner").Should().BeEmpty("V1 is an inline message, not a banner (§8.5)");

        // Same-day range is valid ("on or after").
        cut.Find("#cachet-al-date-to").Change("2026-09-10");
        cut.FindAll(".cachet-al-range-error").Should().BeEmpty();
        IsDisabled(cut, ".cachet-al-apply").Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------
    // V-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V2_TabDropdownOffersExactlyTheAllowedValues()
    {
        var cut = RenderModal();
        await OpenAsync(cut);

        var select = cut.Find(".cachet-al-tab-select");
        var expected = new[] { "All", "Auth", "Keyword Search", "O-Drive Sync", "Quarantine", "Task Manager", "Reports" };
        select.QuerySelectorAll("option").Select(o => o.TextContent).Should().Equal(expected);
        select.QuerySelectorAll("option").Select(o => o.GetAttribute("value")).Should().Equal(expected);
        select.GetAttribute("aria-label").Should().Be("Filter by tab");
        select.GetAttribute("value").Should().Be("All", "All is the default");

        AuditLogQueries().Should().Equal(new[] { DefaultQuery }, "All sends no tab param");

        // Re-applying All explicitly still sends no tab param.
        cut.Find(".cachet-al-apply").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 2, WaitTimeout);
        AuditLogQueries()[1].Should().Be(DefaultQuery);
    }

    // -----------------------------------------------------------------------------------------
    // V-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V3_ApplyAndClearResetToPageOne()
    {
        Return120RowsPaged();
        var cut = RenderModal();
        await OpenAsync(cut);

        // Apply from page 2 → page 1.
        cut.Find(".cachet-al-next").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 2, WaitTimeout);
        AuditLogQueries()[1].Should().Be("?page=2&pageSize=50");

        cut.Find(".cachet-al-tab-select").Change("Task Manager");
        cut.Find(".cachet-al-date-from").Change("2026-09-01");
        cut.Find(".cachet-al-apply").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 3, WaitTimeout);
        AuditLogQueries()[2].Should().Be("?tab=Task%20Manager&dateFrom=2026-09-01T00:00:00Z&page=1&pageSize=50");
        cut.WaitForState(() => PagerText(cut) == "Showing 1–50 of 120", WaitTimeout);

        // Clear from page 2 → page 1, filters back to defaults.
        cut.Find(".cachet-al-next").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 4, WaitTimeout);
        AuditLogQueries()[3].Should().Be("?tab=Task%20Manager&dateFrom=2026-09-01T00:00:00Z&page=2&pageSize=50");

        cut.Find(".cachet-al-clear").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 5, WaitTimeout);
        AuditLogQueries()[4].Should().Be(DefaultQuery);
        cut.WaitForState(() => PagerText(cut) == "Showing 1–50 of 120", WaitTimeout);
        cut.Find(".cachet-al-tab-select").GetAttribute("value").Should().Be("All");
        cut.Find("#cachet-al-date-from").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Find("#cachet-al-date-to").GetAttribute("value").Should().BeNullOrEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // V-4
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V4_PagerBoundsAndInFlightDisable()
    {
        Return120RowsPaged();
        var cut = RenderModal();
        await OpenAsync(cut);

        // Page 1: Previous disabled, Next enabled.
        IsDisabled(cut, ".cachet-al-prev").Should().BeTrue();
        IsDisabled(cut, ".cachet-al-next").Should().BeFalse();

        // Previous on page 1 sends nothing.
        cut.Find(".cachet-al-prev").Click();
        AuditLogQueries().Should().HaveCount(1);

        // Page 3 (3 * 50 >= 120): Next disabled, Previous enabled; Next there sends nothing.
        cut.Find(".cachet-al-next").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 2, WaitTimeout);
        cut.Find(".cachet-al-next").Click();
        cut.WaitForState(() => AuditLogQueries().Count == 3 && PagerText(cut) == "Showing 101–120 of 120", WaitTimeout);
        IsDisabled(cut, ".cachet-al-next").Should().BeTrue();
        IsDisabled(cut, ".cachet-al-prev").Should().BeFalse();
        cut.Find(".cachet-al-next").Click();
        AuditLogQueries().Should().HaveCount(3);

        // In flight: hold the Previous request.
        var held = _apiHandler.Hold();
        cut.Find(".cachet-al-prev").Click();
        cut.WaitForState(() => cut.Markup.Contains("Loading…"), WaitTimeout);

        IsDisabled(cut, ".cachet-al-prev").Should().BeTrue("both pager buttons are disabled while a request is in flight");
        IsDisabled(cut, ".cachet-al-next").Should().BeTrue();
        IsDisabled(cut, ".cachet-al-apply").Should().BeTrue();
        IsDisabled(cut, ".cachet-al-clear").Should().BeTrue();
        IsDisabled(cut, ".cachet-al-tab-select").Should().BeTrue();
        IsDisabled(cut, "#cachet-al-date-from").Should().BeTrue();
        IsDisabled(cut, "#cachet-al-date-to").Should().BeTrue();

        // No duplicate request from any control while in flight.
        cut.Find(".cachet-al-prev").Click();
        cut.Find(".cachet-al-next").Click();
        cut.Find(".cachet-al-apply").Click();
        cut.Find(".cachet-al-clear").Click();
        AuditLogQueries().Should().HaveCount(4, "exactly one request is outstanding");
        AuditLogQueries()[3].Should().Be("?page=2&pageSize=50");

        // Release → page 2, both pager buttons enabled again.
        held.SetResult(FakeAuditLogApiHandler.PageResponse(Items(50, "p2"), 120, 2));
        cut.WaitForState(() => !cut.Markup.Contains("Loading…") && PagerText(cut) == "Showing 51–100 of 120", WaitTimeout);
        IsDisabled(cut, ".cachet-al-prev").Should().BeFalse();
        IsDisabled(cut, ".cachet-al-next").Should().BeFalse();
        AuditLogQueries().Should().HaveCount(4);
    }

    // -----------------------------------------------------------------------------------------
    // V-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V5_CellValuesAreHtmlEncoded()
    {
        const string User = "<script>alert('user')</script>";
        const string Action = "<b>bold-action</b>";
        const string FilePath = "/o-drive/<img src=x onerror=alert(1)>.pdf";
        const string Detail = "<i>b2 error</i><script>alert('detail')</script>";

        _apiHandler.ReturnPage(new[]
        {
            Item(user: User, action: Action, filePath: FilePath, result: "Failure", resultDetail: Detail),
        }, totalCount: 1);

        var cut = RenderModal();
        await OpenAsync(cut);

        var row = BodyRows(cut).Single();
        var cells = row.QuerySelectorAll("td").ToArray();

        // Rendered as text, character for character — never parsed into elements.
        cells[1].TextContent.Trim().Should().Be(User);
        cells[3].TextContent.Trim().Should().Be(Action);
        cells[4].TextContent.Trim().Should().Be(FilePath);
        row.QuerySelector(".cachet-al-detail")!.TextContent.Trim().Should().Be(Detail);

        cut.FindAll("script").Should().BeEmpty();
        cut.FindAll(".cachet-al-table img").Should().BeEmpty();
        cut.FindAll(".cachet-al-table b").Should().BeEmpty();
        cut.FindAll(".cachet-al-table i").Should().BeEmpty();
        cut.Markup.Should().Contain("&lt;script&gt;alert('user')&lt;/script&gt;");
        cut.Markup.Should().Contain("&lt;b&gt;bold-action&lt;/b&gt;");
    }

    // -----------------------------------------------------------------------------------------
    // V-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V6_FailureRowsRenderWithDetail()
    {
        _apiHandler.ReturnPage(new[]
        {
            Item(user: "u1", action: "Permanent Delete", result: "Failure", resultDetail: "B2 delete failed: 503 service unavailable"),
            Item(user: "u2", action: "Search", result: "Success", resultDetail: "12 matches"),
            Item(user: "u3", action: "Purge", result: "Failure", resultDetail: null),
            Item(user: "u4", action: "Other", result: "Partial", resultDetail: null),
        }, totalCount: 4);

        var cut = RenderModal();
        await OpenAsync(cut);

        var rows = BodyRows(cut);
        rows.Should().HaveCount(4, "Failure rows are never filtered or hidden by the client");
        rows.Select(r => r.QuerySelectorAll("td")[1].TextContent.Trim()).Should().Equal("u1", "u2", "u3", "u4");

        // Failure with detail → red chip + error-styled detail.
        var failureChip = rows[0].QuerySelector(".cachet-al-chip")!;
        failureChip.TextContent.Should().Be("Failure");
        failureChip.ClassList.Should().Contain("cachet-chip-flagged");
        var failureDetail = rows[0].QuerySelector(".cachet-al-detail")!;
        failureDetail.TextContent.Should().Be("B2 delete failed: 503 service unavailable");
        failureDetail.ClassList.Should().Contain("cachet-cell-error");

        // Success with detail → green chip + muted detail.
        rows[1].QuerySelector(".cachet-al-chip")!.ClassList.Should().Contain("cachet-chip-success");
        var successDetail = rows[1].QuerySelector(".cachet-al-detail")!;
        successDetail.ClassList.Should().Contain("cachet-al-detail-muted").And.NotContain("cachet-cell-error");

        // Failure without detail still renders, with the failure chip.
        rows[2].QuerySelector(".cachet-al-chip")!.ClassList.Should().Contain("cachet-chip-flagged");
        rows[2].QuerySelector(".cachet-al-detail").Should().BeNull();

        // Any other result value → neutral chip (§8.3 defensive).
        rows[3].QuerySelector(".cachet-al-chip")!.ClassList.Should().Contain("cachet-chip-neutral");
    }

    /// <summary>Single-client <see cref="IHttpClientFactory"/> stand-in — mirrors
    /// QuarantineQueueModalTests' own private helper for this test project.</summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
