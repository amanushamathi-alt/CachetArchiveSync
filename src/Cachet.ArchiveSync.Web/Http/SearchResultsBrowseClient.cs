using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Web.Http;

/// <summary>Outcome of one search-results browse call (CAS-048 V7, V9).</summary>
public enum SearchResultsBrowseOutcome
{
    Success,
    Unauthorized,

    /// <summary>404 (task not found/not the caller's, or folder not found).</summary>
    NotFound,

    /// <summary>409 <c>task-not-complete</c>.</summary>
    Conflict,

    /// <summary>400 <c>selection-too-large</c> (result-ids only).</summary>
    SelectionTooLarge,

    Failed
}

/// <summary>
/// Result of <see cref="SearchResultsBrowseClient.BrowseAsync"/>. No Api detail, ProblemDetails body or
/// exception text is carried (CAS-048 V7/§11).
/// </summary>
public sealed record SearchResultsBrowseResult(SearchResultsBrowseOutcome Outcome, FolderBrowseResponse? Response)
{
    public static SearchResultsBrowseResult Ok(FolderBrowseResponse response) => new(SearchResultsBrowseOutcome.Success, response);

    public static SearchResultsBrowseResult Of(SearchResultsBrowseOutcome outcome) => new(outcome, null);
}

/// <summary>Result of <see cref="SearchResultsBrowseClient.GetResultIdsAsync"/> (CAS-048 V9).</summary>
public sealed record SearchResultIdsClientResult(SearchResultsBrowseOutcome Outcome, IReadOnlyList<int>? Ids)
{
    public static SearchResultIdsClientResult Ok(IReadOnlyList<int> ids) => new(SearchResultsBrowseOutcome.Success, ids);

    public static SearchResultIdsClientResult Of(SearchResultsBrowseOutcome outcome) => new(outcome, null);
}

/// <summary>
/// CAS-048 §5/§8: typed wrapper over the cookie-forwarding <c>"CachetApi"</c> client for
/// <c>GET search/{taskId}/browse</c> and <c>GET search/{taskId}/browse/result-ids</c> (CAS-049).
/// The <c>folder</c> value is passed through exactly as given (the explorer only supplies paths from
/// earlier responses, V8). Never logs the folder path or any response body (§11). A caller-requested
/// cancellation propagates as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class SearchResultsBrowseClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SearchResultsBrowseClient> _logger;

    public SearchResultsBrowseClient(IHttpClientFactory httpClientFactory, ILogger<SearchResultsBrowseClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SearchResultsBrowseResult> BrowseAsync(int taskId, string? folder, bool foldersOnly, CancellationToken ct)
    {
        var query = new List<string>(2);
        if (!string.IsNullOrEmpty(folder))
        {
            query.Add($"folder={Uri.EscapeDataString(folder)}");
        }

        if (foldersOnly)
        {
            query.Add("foldersOnly=true");
        }

        var uri = BuildUri(taskId, "browse", query);
        var (outcome, body) = await GetAsync<FolderBrowseResponse>(uri, ct);
        return body is not null && outcome == SearchResultsBrowseOutcome.Success
            ? SearchResultsBrowseResult.Ok(body)
            : SearchResultsBrowseResult.Of(outcome == SearchResultsBrowseOutcome.Success ? SearchResultsBrowseOutcome.Failed : outcome);
    }

    public async Task<SearchResultIdsClientResult> GetResultIdsAsync(int taskId, string? folder, CancellationToken ct)
    {
        var query = new List<string>(1);
        if (!string.IsNullOrEmpty(folder))
        {
            query.Add($"folder={Uri.EscapeDataString(folder)}");
        }

        var uri = BuildUri(taskId, "browse/result-ids", query);
        var (outcome, body) = await GetAsync<SearchResultIdsResponse>(uri, ct);
        return body is not null && outcome == SearchResultsBrowseOutcome.Success
            ? SearchResultIdsClientResult.Ok(body.SearchResultIds)
            : SearchResultIdsClientResult.Of(outcome == SearchResultsBrowseOutcome.Success ? SearchResultsBrowseOutcome.Failed : outcome);
    }

    private static string BuildUri(int taskId, string suffix, List<string> query)
    {
        var path = $"search/{taskId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{suffix}";
        return query.Count == 0 ? path : $"{path}?{string.Join('&', query)}";
    }

    private async Task<(SearchResultsBrowseOutcome Outcome, T? Body)> GetAsync<T>(string uri, CancellationToken ct)
        where T : class
    {
        var client = _httpClientFactory.CreateClient("CachetApi");

        try
        {
            using var response = await client.GetAsync(uri, ct);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
                if (body is null)
                {
                    _logger.LogWarning("Search results browse failed with status {StatusCode}", (int)response.StatusCode);
                    return (SearchResultsBrowseOutcome.Failed, null);
                }

                return (SearchResultsBrowseOutcome.Success, body);
            }

            _logger.LogWarning("Search results browse failed with status {StatusCode}", (int)response.StatusCode);
            return (response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => SearchResultsBrowseOutcome.Unauthorized,
                HttpStatusCode.NotFound => SearchResultsBrowseOutcome.NotFound,
                HttpStatusCode.Conflict => SearchResultsBrowseOutcome.Conflict,
                HttpStatusCode.BadRequest when await IsSelectionTooLargeAsync(response, ct) => SearchResultsBrowseOutcome.SelectionTooLarge,
                _ => SearchResultsBrowseOutcome.Failed
            }, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Status 0 = no HTTP status; the exception text is not logged because it can carry the
            // request URI (and so the folder path).
            _logger.LogWarning("Search results browse failed with status {StatusCode}", 0);
            return (SearchResultsBrowseOutcome.Failed, null);
        }
    }

    // The 400 body is read only to recognise the Api's "selection-too-large" problem type; its text is
    // never surfaced or logged.
    private static async Task<bool> IsSelectionTooLargeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && (type.GetString()?.EndsWith("/selection-too-large", StringComparison.Ordinal) ?? false);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
