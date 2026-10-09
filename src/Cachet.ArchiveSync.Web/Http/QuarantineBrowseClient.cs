using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;

namespace Cachet.ArchiveSync.Web.Http;

/// <summary>Outcome of one Files-browser quarantine call (CAS-051 V4).</summary>
public enum QuarantineBrowseOutcome
{
    Success,
    Unauthorized,

    /// <summary>403 - the caller is not Full Access (CAS-050 §11).</summary>
    Forbidden,

    /// <summary>404 (file/folder not found, or not quarantined) or 409 (already quarantined).</summary>
    Rejected,

    Failed
}

/// <summary>
/// Result of a <see cref="QuarantineBrowseClient"/> call. No Api detail, ProblemDetails body or
/// exception text is carried (CAS-051 V4/§11).
/// </summary>
public sealed record QuarantineBrowseResult<T>(QuarantineBrowseOutcome Outcome, T? Body)
    where T : class
{
    public static QuarantineBrowseResult<T> Ok(T body) => new(QuarantineBrowseOutcome.Success, body);

    public static QuarantineBrowseResult<T> Of(QuarantineBrowseOutcome outcome) => new(outcome, null);
}

/// <summary>
/// CAS-051 §5: typed wrapper over the cookie-forwarding <c>"CachetApi"</c> client for
/// <c>GET quarantine/browse/state</c>, <c>POST quarantine/browse</c> and
/// <c>POST quarantine/browse/remove</c> (CAS-050 §8). Never logs a folder path, file id or response
/// body (§11). A caller-requested cancellation propagates as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class QuarantineBrowseClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QuarantineBrowseClient> _logger;

    public QuarantineBrowseClient(IHttpClientFactory httpClientFactory, ILogger<QuarantineBrowseClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<QuarantineBrowseResult<QuarantineBrowseStateResponse>> GetStateAsync(
        string source, string bucketName, string folderPath, CancellationToken ct)
    {
        var uri = "quarantine/browse/state"
            + $"?source={Uri.EscapeDataString(source)}"
            + $"&bucketName={Uri.EscapeDataString(bucketName)}"
            + $"&folderPath={Uri.EscapeDataString(folderPath)}";

        return SendAsync<QuarantineBrowseStateResponse>(c => c.GetAsync(uri, ct), ct);
    }

    public Task<QuarantineBrowseResult<QuarantineBrowseAddResponse>> AddAsync(
        QuarantineBrowseRequest request, CancellationToken ct) =>
        SendAsync<QuarantineBrowseAddResponse>(c => c.PostAsJsonAsync("quarantine/browse", request, ct), ct);

    public Task<QuarantineBrowseResult<QuarantineBrowseRemoveResponse>> RemoveAsync(
        QuarantineBrowseRequest request, CancellationToken ct) =>
        SendAsync<QuarantineBrowseRemoveResponse>(c => c.PostAsJsonAsync("quarantine/browse/remove", request, ct), ct);

    private async Task<QuarantineBrowseResult<T>> SendAsync<T>(
        Func<HttpClient, Task<HttpResponseMessage>> send, CancellationToken ct)
        where T : class
    {
        var client = _httpClientFactory.CreateClient("CachetApi");

        try
        {
            using var response = await send(client);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
                if (body is not null)
                {
                    return QuarantineBrowseResult<T>.Ok(body);
                }

                _logger.LogWarning("Quarantine browse call failed with status {StatusCode}", (int)response.StatusCode);
                return QuarantineBrowseResult<T>.Of(QuarantineBrowseOutcome.Failed);
            }

            _logger.LogWarning("Quarantine browse call failed with status {StatusCode}", (int)response.StatusCode);
            return QuarantineBrowseResult<T>.Of(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => QuarantineBrowseOutcome.Unauthorized,
                HttpStatusCode.Forbidden => QuarantineBrowseOutcome.Forbidden,
                HttpStatusCode.NotFound or HttpStatusCode.Conflict => QuarantineBrowseOutcome.Rejected,
                _ => QuarantineBrowseOutcome.Failed
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Status 0 = no HTTP status; the exception text is not logged because it can carry the
            // request URI (and so the folder path).
            _logger.LogWarning("Quarantine browse call failed with status {StatusCode}", 0);
            return QuarantineBrowseResult<T>.Of(QuarantineBrowseOutcome.Failed);
        }
    }
}
