using System.Net;
using System.Text.RegularExpressions;

namespace Cachet.ArchiveSync.Web.Tests.Auth;

/// <summary>
/// CAS-021: minimal, regex-based scraper for the rendered <c>Login.razor</c>/<c>MainLayout.razor</c>
/// HTML, used by <see cref="WebAuthFlowIntegrationTests"/> to discover the real
/// <c>&lt;input&gt;</c> <c>name</c> attributes Blazor's named-form binding and automatic antiforgery
/// support actually emit, instead of guessing/hard-coding a naming scheme this test-writer agent
/// cannot verify by running the app. Deliberately does not pull in an HTML parser package for one
/// narrow, well-known shape (self-closing/void <c>&lt;input&gt;</c> and <c>&lt;button&gt;</c> tags).
/// </summary>
internal static class BlazorFormScraper
{
    private static readonly Regex InputTagRegex = new(@"<input\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ButtonTagRegex = new(@"<button\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttrRegex = new(@"([\w-]+)\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>All <c>type="hidden"</c> inputs' name/value pairs (e.g. the named-form <c>_handler</c>
    /// field and the automatic antiforgery <c>__RequestVerificationToken</c> field).</summary>
    public static Dictionary<string, string> ExtractHiddenFields(string html)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match tagMatch in InputTagRegex.Matches(html))
        {
            var attrs = ExtractAttributes(tagMatch.Value);
            if (attrs.TryGetValue("type", out var type)
                && string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase)
                && attrs.TryGetValue("name", out var name))
            {
                result[name] = attrs.TryGetValue("value", out var value) ? value : string.Empty;
            }
        }

        return result;
    }

    /// <summary>The <c>name</c> attribute of the first <c>&lt;input&gt;</c> tag with the given <c>id</c>.</summary>
    public static string? FindInputNameById(string html, string id)
    {
        foreach (Match tagMatch in InputTagRegex.Matches(html))
        {
            var attrs = ExtractAttributes(tagMatch.Value);
            if (attrs.TryGetValue("id", out var idValue)
                && string.Equals(idValue, id, StringComparison.Ordinal)
                && attrs.TryGetValue("name", out var name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Raw attribute map of the first <c>&lt;input&gt;</c> tag with the given <c>id</c> —
    /// used by V2 to prove no format-restricting attribute (<c>pattern</c>/<c>minlength</c>/etc.)
    /// was added to the password field beyond <c>required</c>.</summary>
    public static IReadOnlyDictionary<string, string>? FindInputAttributesById(string html, string id)
    {
        foreach (Match tagMatch in InputTagRegex.Matches(html))
        {
            var attrs = ExtractAttributes(tagMatch.Value);
            if (attrs.TryGetValue("id", out var idValue) && string.Equals(idValue, id, StringComparison.Ordinal))
            {
                return attrs;
            }
        }

        return null;
    }

    /// <summary>True if the first <c>&lt;button&gt;</c> tag with the given <c>id</c> carries a
    /// (boolean or valued) <c>disabled</c> attribute.</summary>
    public static bool ButtonHasDisabledAttribute(string html, string id)
    {
        foreach (Match tagMatch in ButtonTagRegex.Matches(html))
        {
            var attrs = ExtractAttributes(tagMatch.Value);
            if (attrs.TryGetValue("id", out var idValue) && string.Equals(idValue, id, StringComparison.Ordinal))
            {
                // Boolean HTML attributes may render as `disabled` (no value) or `disabled="disabled"`/
                // `disabled=""` depending on the renderer — both must count as "disabled present".
                return Regex.IsMatch(tagMatch.Value, @"(?<=[\s""])disabled\b", RegexOptions.IgnoreCase);
            }
        }

        return false;
    }

    private static Dictionary<string, string> ExtractAttributes(string tag)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRegex.Matches(tag))
        {
            dict[m.Groups[1].Value] = WebUtility.HtmlDecode(m.Groups[2].Value);
        }

        return dict;
    }
}
