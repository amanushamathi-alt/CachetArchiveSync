using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Web.Http;

/// <summary>Outcome of one <c>GET files/browse</c> call (CAS-044 V4-V6).</summary>
public enum FilesBrowseOutcome
{
    Success,
    Unauthorized,
    NotFound,
    Failed
}

/// <summary>
/// Result of <see cref="FilesBrowseClient.BrowseAsync"/>. No Api detail, ProblemDetails body or
/// exception text is carried (CAS-044 V6/§12).
/// </summary>
public sealed record FilesBrowseResult(FilesBrowseOutcome Outcome, FolderBrowseResponse? Response)
{
    public static FilesBrowseResult Ok(FolderBrowseResponse response) => new(FilesBrowseOutcome.Success, response);

    public static FilesBrowseResult Of(FilesBrowseOutcome outcome) => new(outcome, null);
}

/// <summary>
/// CAS-044 §5: typed wrapper over the cookie-forwarding <c>"CachetApi"</c> client for
/// <c>GET files/browse</c> (CAS-043 §6). The <c>folder</c> value is passed through exactly as given
/// by the caller (the explorer only ever supplies paths taken from earlier responses, V8). Maps
/// 401/404/other failures to <see cref="FilesBrowseOutcome"/>; never logs the folder path or any
/// response body (§11). A caller-requested cancellation propagates as
/// <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class FilesBrowseClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FilesBrowseClient> _logger;

    public FilesBrowseClient(IHttpClientFactory httpClientFactory, ILogger<FilesBrowseClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FilesBrowseResult> BrowseAsync(string? folder, bool foldersOnly, CancellationToken ct)
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

        var uri = query.Count == 0 ? "files/browse" : $"files/browse?{string.Join('&', query)}";
        var client = _httpClientFactory.CreateClient("CachetApi");

        try
        {
            using var response = await client.GetAsync(uri, ct);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<FolderBrowseResponse>(cancellationToken: ct);
                if (body is not null)
                {
                    return FilesBrowseResult.Ok(body);
                }

                _logger.LogWarning("Files browse failed with status {StatusCode}", (int)response.StatusCode);
                return FilesBrowseResult.Of(FilesBrowseOutcome.Failed);
            }

            _logger.LogWarning("Files browse failed with status {StatusCode}", (int)response.StatusCode);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => FilesBrowseResult.Of(FilesBrowseOutcome.Unauthorized),
                HttpStatusCode.NotFound => FilesBrowseResult.Of(FilesBrowseOutcome.NotFound),
                _ => FilesBrowseResult.Of(FilesBrowseOutcome.Failed)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Status 0 = no HTTP status (unreachable, timeout or malformed body); the exception text
            // is not logged because it can carry the request URI (and so the folder path).
            _logger.LogWarning("Files browse failed with status {StatusCode}", 0);
            return FilesBrowseResult.Of(FilesBrowseOutcome.Failed);
        }
    }
}
