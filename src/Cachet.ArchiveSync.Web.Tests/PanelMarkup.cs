using System.Globalization;
using Bunit;

namespace Cachet.ArchiveSync.Web.Tests;

/// <summary>
/// Reads a result panel's count badge in the wireframe panel header
/// (<c>&lt;h2&gt;Title&lt;span class="cachet-panel-count"&gt;n&lt;/span&gt;&lt;/h2&gt;</c>, CAS-025 §3
/// "Filename Matches 5" placement). Shared by the Keyword Search and O-Drive Sync page tests.
/// </summary>
internal static class PanelMarkup
{
    /// <summary>The panel's count, or <c>null</c> when the panel is not rendered or shows no count.</summary>
    public static int? PanelCount(IRenderedFragment cut, string title)
    {
        var heading = cut.FindAll(".cachet-panel-header h2")
            .FirstOrDefault(h => string.Equals(h.FirstChild?.TextContent.Trim(), title, StringComparison.Ordinal));
        var count = heading?.QuerySelector(".cachet-panel-count");
        return count is null ? null : int.Parse(count.TextContent.Trim(), CultureInfo.InvariantCulture);
    }
}
