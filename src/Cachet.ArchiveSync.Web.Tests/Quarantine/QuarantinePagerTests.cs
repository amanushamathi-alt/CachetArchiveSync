using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace Cachet.ArchiveSync.Web.Tests.Quarantine;

/// <summary>
/// CAS-052 §13 — unit behaviour of the shared <c>QuarantinePager.razor</c>: V1 (page and page-size
/// bounds) and SC-13 (accessible names).
/// </summary>
public sealed class QuarantinePagerTests : TestContext
{
    private IRenderedComponent<Web.Components.Shared.QuarantinePager> Render(
        int page, int pageSize, int totalCount, bool isLoading = false,
        Action<int>? onPage = null, Action<int>? onSize = null) =>
        RenderComponent<Web.Components.Shared.QuarantinePager>(p => p
            .Add(c => c.Page, page)
            .Add(c => c.PageSize, pageSize)
            .Add(c => c.TotalCount, totalCount)
            .Add(c => c.IsLoading, isLoading)
            .Add(c => c.TestIdPrefix, "qq")
            .Add(c => c.OnPageChanged, EventCallback.Factory.Create<int>(this, v => onPage?.Invoke(v)))
            .Add(c => c.OnPageSizeChanged, EventCallback.Factory.Create<int>(this, v => onSize?.Invoke(v))));

    private static string Text(IRenderedComponent<Web.Components.Shared.QuarantinePager> cut, string testId) =>
        cut.Find($"[data-testid=qq-{testId}]").TextContent.Trim();

    [Fact]
    public void Test_V1_PageBounds()
    {
        // First page: Previous disabled, Next enabled; range and page-of reflect the total.
        var first = Render(page: 1, pageSize: 25, totalCount: 60);
        first.Find("[data-testid=qq-prev]").HasAttribute("disabled").Should().BeTrue();
        first.Find("[data-testid=qq-next]").HasAttribute("disabled").Should().BeFalse();
        Text(first, "range").Should().Be("1–25 of 60");
        Text(first, "page-of").Should().Be("Page 1 of 3");

        // Last page: Next disabled, Previous enabled; short final range.
        var last = Render(page: 3, pageSize: 25, totalCount: 60);
        last.Find("[data-testid=qq-next]").HasAttribute("disabled").Should().BeTrue();
        last.Find("[data-testid=qq-prev]").HasAttribute("disabled").Should().BeFalse();
        Text(last, "range").Should().Be("51–60 of 60");

        // An out-of-range Page is displayed clamped, never "Page 9 of 3".
        Text(Render(page: 9, pageSize: 25, totalCount: 60), "page-of").Should().Be("Page 3 of 3");

        // Empty queue is always "Page 1 of 1" / "0 of 0", never "Page 1 of 0".
        var empty = Render(page: 1, pageSize: 25, totalCount: 0);
        Text(empty, "page-of").Should().Be("Page 1 of 1");
        Text(empty, "range").Should().Be("0 of 0");
        empty.Find("[data-testid=qq-prev]").HasAttribute("disabled").Should().BeTrue();
        empty.Find("[data-testid=qq-next]").HasAttribute("disabled").Should().BeTrue();

        // Navigation raises the callback with the in-range target page.
        var pages = new List<int>();
        var middle = Render(page: 2, pageSize: 25, totalCount: 60, onPage: pages.Add);
        middle.Find("[data-testid=qq-next]").Click();
        middle.Find("[data-testid=qq-prev]").Click();
        pages.Should().Equal(3, 1);

        // Page size offers exactly 25 and 50; anything else (or the current size) is ignored.
        middle.FindAll("[data-testid=qq-page-size] option").Select(o => o.GetAttribute("value"))
            .Should().Equal("25", "50");
        var sizes = new List<int>();
        var sizeCut = Render(page: 1, pageSize: 25, totalCount: 60, onSize: sizes.Add);
        sizeCut.Find("[data-testid=qq-page-size]").Change("10");
        sizeCut.Find("[data-testid=qq-page-size]").Change("abc");
        sizeCut.Find("[data-testid=qq-page-size]").Change("25");
        sizes.Should().BeEmpty();
        sizeCut.Find("[data-testid=qq-page-size]").Change("50");
        sizes.Should().Equal(50);

        // While loading, every control is disabled.
        var loading = Render(page: 2, pageSize: 25, totalCount: 60, isLoading: true);
        loading.Find("[data-testid=qq-prev]").HasAttribute("disabled").Should().BeTrue();
        loading.Find("[data-testid=qq-next]").HasAttribute("disabled").Should().BeTrue();
        loading.Find("[data-testid=qq-page-size]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Test_SC13_Pager_Accessibility()
    {
        var cut = Render(page: 2, pageSize: 25, totalCount: 60);

        cut.Find("nav.cachet-qpager").GetAttribute("aria-label").Should().Be("Quarantine pagination");
        cut.Find("[data-testid=qq-prev]").GetAttribute("aria-label").Should().Be("Previous page");
        cut.Find("[data-testid=qq-next]").GetAttribute("aria-label").Should().Be("Next page");
        cut.Find("[data-testid=qq-page-size]").GetAttribute("aria-label").Should().Be("Rows per page");
    }
}
