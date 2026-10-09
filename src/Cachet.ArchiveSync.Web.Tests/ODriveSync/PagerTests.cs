using AngleSharp.Dom;
using Bunit;
using Cachet.ArchiveSync.Web.Components.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace Cachet.ArchiveSync.Web.Tests.ODriveSync;

/// <summary>
/// CAS-045 §13 — bUnit tests for the reusable <see cref="Pager"/> (SC-4 window rule, SC-11 accessibility,
/// V2 "Go to page", V3 boundary/loading disabling, V4 hidden for a single page).
/// </summary>
public sealed class PagerTests : TestContext
{
    private readonly List<int> _pageChanges = new();

    private IRenderedComponent<Pager> RenderPager(int page, int pageSize, int totalCount, bool isLoading = false) =>
        RenderComponent<Pager>(parameters => parameters
            .Add(p => p.Page, page)
            .Add(p => p.PageSize, pageSize)
            .Add(p => p.TotalCount, totalCount)
            .Add(p => p.IsLoading, isLoading)
            .Add(p => p.OnPageChanged, EventCallback.Factory.Create<int>(this, _pageChanges.Add)));

    /// <summary>The rendered window, left to right: page numbers and "…" gaps (nav controls excluded).</summary>
    private static string Window(IRenderedComponent<Pager> cut) =>
        string.Join(" ", cut.Find("nav").Children
            .Where(e => e.ClassList.Contains("cachet-pager-page") || e.ClassList.Contains("cachet-pager-gap"))
            .Select(e => e.TextContent.Trim()));

    private static IElement Control(IRenderedComponent<Pager> cut, string ariaLabel) =>
        cut.Find($"nav button[aria-label='{ariaLabel}']");

    // -----------------------------------------------------------------------------------------
    // SC-4
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, "1 2 3 4 5 … 20")]
    [InlineData(2, "1 2 3 4 5 … 20")]
    [InlineData(3, "1 2 3 4 5 … 20")]
    [InlineData(4, "1 2 3 4 5 6 … 20")]
    [InlineData(8, "1 … 6 7 8 9 10 … 20")]
    [InlineData(17, "1 … 15 16 17 18 19 20")]
    [InlineData(18, "1 … 16 17 18 19 20")]
    [InlineData(20, "1 … 16 17 18 19 20")]
    public void Test_SC4_PagerWindowRule(int currentPage, string expectedWindow)
    {
        // 1,000 rows / 50 per page = 20 pages.
        var cut = RenderPager(currentPage, pageSize: 50, totalCount: 1000);

        Window(cut).Should().Be(expectedWindow);
    }

    [Fact]
    public void Test_SC4_PagerWindowRuleSmallPageCountHasNoGap()
    {
        var cut = RenderPager(page: 1, pageSize: 50, totalCount: 250); // 5 pages

        Window(cut).Should().Be("1 2 3 4 5");
    }

    [Fact]
    public void Test_SC4_PagerWindowRuleLastPageWhenTotalCountIsNotAMultiple()
    {
        // 77,627 rows / 50 = 1,553 pages (CAS-045 §1's scan 100003 "New" panel).
        var cut = RenderPager(page: 1, pageSize: 50, totalCount: 77_627);

        Window(cut).Should().Be("1 2 3 4 5 … 1,553");
    }

    // -----------------------------------------------------------------------------------------
    // V3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V3_PagerButtonsDisabledAtBoundsAndWhileLoading()
    {
        // Page 1: « ‹ disabled, › » enabled.
        var cut = RenderPager(page: 1, pageSize: 50, totalCount: 1000);
        Control(cut, "First page").HasAttribute("disabled").Should().BeTrue();
        Control(cut, "Previous page").HasAttribute("disabled").Should().BeTrue();
        Control(cut, "Next page").HasAttribute("disabled").Should().BeFalse();
        Control(cut, "Last page").HasAttribute("disabled").Should().BeFalse();

        // Last page: › » disabled, « ‹ enabled.
        cut.SetParametersAndRender(parameters => parameters.Add(p => p.Page, 20));
        Control(cut, "First page").HasAttribute("disabled").Should().BeFalse();
        Control(cut, "Previous page").HasAttribute("disabled").Should().BeFalse();
        Control(cut, "Next page").HasAttribute("disabled").Should().BeTrue();
        Control(cut, "Last page").HasAttribute("disabled").Should().BeTrue();

        // Middle page while loading: every control (nav buttons, page numbers, Go) is disabled.
        cut.SetParametersAndRender(parameters => parameters.Add(p => p.Page, 10).Add(p => p.IsLoading, true));
        cut.FindAll("nav button").Should().OnlyContain(b => b.HasAttribute("disabled"));
        cut.Find("nav input").HasAttribute("disabled").Should().BeTrue();

        // Clicking a disabled control raises nothing.
        _pageChanges.Should().BeEmpty();
    }

    [Fact]
    public void Test_V3_PagerNavigationButtonsRaiseTheExpectedPage()
    {
        var cut = RenderPager(page: 10, pageSize: 50, totalCount: 1000);

        Control(cut, "First page").Click();
        Control(cut, "Previous page").Click();
        Control(cut, "Next page").Click();
        Control(cut, "Last page").Click();
        Control(cut, "Page 8").Click();

        _pageChanges.Should().Equal(1, 9, 11, 20, 8);
    }

    // -----------------------------------------------------------------------------------------
    // V2
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("5", 5)]
    [InlineData(" 7 ", 7)]
    [InlineData("999", 20)] // clamped to the last page
    [InlineData("0", 1)]    // clamped to the first page
    public void Test_V2_GoToPageClampsAndRejectsNonNumeric_ValidIntegersClamp(string typed, int expectedPage)
    {
        var cut = RenderPager(page: 10, pageSize: 50, totalCount: 1000);

        cut.Find(".cachet-pager-goto-input").Input(typed);
        cut.FindAll(".cachet-pager-goto button").Single(b => b.TextContent.Trim() == "Go").Click();

        _pageChanges.Should().Equal(expectedPage);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-3")]
    [InlineData("7.5")]
    [InlineData("1e2")]
    [InlineData("12abc")]
    public void Test_V2_GoToPageClampsAndRejectsNonNumeric(string typed)
    {
        var cut = RenderPager(page: 10, pageSize: 50, totalCount: 1000);

        cut.Find(".cachet-pager-goto-input").Input(typed);
        cut.FindAll(".cachet-pager-goto button").Single(b => b.TextContent.Trim() == "Go").Click();

        _pageChanges.Should().BeEmpty("non-numeric (or non-integer) input is ignored — no page change is requested");
    }

    // -----------------------------------------------------------------------------------------
    // V4
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 50)]
    [InlineData(26, 50)]
    [InlineData(50, 50)] // TotalCount <= PageSize
    public void Test_V4_PagerHiddenForSinglePage(int totalCount, int pageSize)
    {
        var cut = RenderPager(page: 1, pageSize, totalCount);

        cut.FindAll("nav").Should().BeEmpty();
        cut.FindAll(".cachet-pager-goto").Should().BeEmpty("'Go to page' is hidden together with the pager");
    }

    [Fact]
    public void Test_V4_PagerShownOnceTotalCountExceedsPageSize()
    {
        var cut = RenderPager(page: 1, pageSize: 50, totalCount: 51);

        cut.FindAll("nav").Should().ContainSingle();
        Window(cut).Should().Be("1 2");
    }

    // -----------------------------------------------------------------------------------------
    // SC-11
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC11_PagerAccessibility()
    {
        var cut = RenderPager(page: 8, pageSize: 50, totalCount: 1000);

        // A labelled navigation landmark.
        var nav = cut.Find("nav");
        nav.GetAttribute("aria-label").Should().Be("Pagination");

        // Exactly one page carries aria-current="page" — the current one.
        var current = cut.FindAll("[aria-current]");
        current.Should().ContainSingle();
        current[0].GetAttribute("aria-current").Should().Be("page");
        current[0].TextContent.Trim().Should().Be("8");

        // Every control is a native, labelled, Tab-reachable element (no tabindex="-1", no div/span buttons).
        var buttons = cut.FindAll("nav button");
        buttons.Should().NotBeEmpty();
        buttons.Should().OnlyContain(b => b.GetAttribute("type") == "button" && !string.IsNullOrWhiteSpace(b.GetAttribute("aria-label") ?? b.TextContent));
        cut.FindAll("nav [tabindex='-1']").Should().BeEmpty();
        cut.Find("nav input").GetAttribute("aria-label").Should().Be("Go to page");

        // Decorative gap ellipses are hidden from assistive technology.
        cut.FindAll(".cachet-pager-gap").Should().OnlyContain(g => g.GetAttribute("aria-hidden") == "true");
    }
}
