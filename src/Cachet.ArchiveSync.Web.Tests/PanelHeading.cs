using System.Globalization;
using Bunit;

namespace Cachet.ArchiveSync.Web.Tests;

/// <summary>
/// Reads a results panel's heading count. Per CAS-025 §8.3 (wireframe count-badge placement, e.g.
/// "Filename Matches 5"), <c>KeywordSearch.razor</c> and <c>ODriveSyncContentScan.razor</c> render
/// each panel heading as <c>&lt;h2&gt;Title &lt;span class="cachet-panel-count"&gt;N&lt;/span&gt;&lt;/h2&gt;</c>,
/// and render the count span only once the panel has results to count.
/// </summary>
internal static class PanelHeading
{
    /// <summary>The count shown in the panel heading that starts with <paramref name="title"/>,
    /// or <c>null</c> when that heading shows no count (or is not rendered).</summary>
    public static string? Count(IRenderedFragment cut, string title) =>
        cut.FindAll("h2")
            .FirstOrDefault(h => h.TextContent.TrimStart().StartsWith(title, StringComparison.Ordinal))
            ?.QuerySelector(".cachet-panel-count")
            ?.TextContent.Trim();

    public static bool HasCount(IRenderedFragment cut, string title, int count) =>
        Count(cut, title) == count.ToString(CultureInfo.InvariantCulture);
}
