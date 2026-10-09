namespace Cachet.ArchiveSync.Api.Content;

/// <summary>
/// Incremental case-insensitive term search over text delivered in pieces. Keeps only a small sliding
/// window (term length + the snippet's lead-in), so memory stays flat however large the source is, and
/// reports completion as soon as the first match has its trailing context. Produces the same ±100
/// character snippet as a whole-text match (CAS-012 §7 V12).
/// </summary>
internal sealed class StreamingSnippetFinder
{
    private const int SnippetWindowChars = 100;

    private readonly string _term;
    private readonly int _keep;
    private string _window = string.Empty;
    private int _matchIndex = -1;

    public StreamingSnippetFinder(string term)
    {
        _term = term;
        // Enough of the previous text that a match straddling two pieces is still found and still has
        // a full lead-in for the snippet.
        _keep = term.Length - 1 + SnippetWindowChars;
    }

    /// <summary>Feeds the next piece of text. Returns true once a match and its trailing context are in hand.</summary>
    public bool Append(string text)
    {
        if (_term.Length == 0 || string.IsNullOrEmpty(text))
        {
            return false;
        }

        _window += text;

        if (_matchIndex < 0)
        {
            _matchIndex = _window.IndexOf(_term, StringComparison.OrdinalIgnoreCase);
            if (_matchIndex < 0)
            {
                if (_window.Length > _keep)
                {
                    _window = _window[^_keep..];
                }

                return false;
            }
        }

        return _window.Length >= _matchIndex + _term.Length + SnippetWindowChars;
    }

    /// <summary>The snippet for the first match, or null if there was none. Call after the last piece.</summary>
    public string? Snippet()
    {
        if (_matchIndex < 0)
        {
            return null;
        }

        var start = Math.Max(0, _matchIndex - SnippetWindowChars);
        var end = Math.Min(_window.Length, _matchIndex + _term.Length + SnippetWindowChars);
        return _window[start..end];
    }
}
