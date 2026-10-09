using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.B2;

/// <summary>
/// <see cref="IUserB2KeyAuthorizer"/> implementation — calls <c>b2_authorize_account</c> with a
/// caller-supplied key pair (CAS-002 §6.1 step 2), never the server-held system key. See
/// <see cref="IUserB2KeyAuthorizer"/>'s XML doc for why this is a separate class from
/// <see cref="B2Client"/> rather than an extension of it. Lives in this namespace (not
/// <c>Cachet.ArchiveSync.Api.Services</c>) specifically to reuse <see cref="B2WireModels"/>'s
/// internal wire-format types rather than duplicating them.
/// </summary>
public sealed class UserB2KeyAuthorizer : IUserB2KeyAuthorizer
{
    private const string ApiVersion = "v2";
    private const string AuthorizeAccountUrl = "https://api.backblazeb2.com/b2api/" + ApiVersion + "/b2_authorize_account";

    private readonly HttpClient _httpClient;
    private readonly ILogger<UserB2KeyAuthorizer> _logger;

    public UserB2KeyAuthorizer(IHttpClientFactory httpClientFactory, ILogger<UserB2KeyAuthorizer> logger)
    {
        // Reuses the same named client B2Client uses (Program.cs) — no per-request key ever
        // reaches B2Client itself; this class only shares the pooled HttpMessageHandler.
        _httpClient = httpClientFactory.CreateClient("B2Client");
        _logger = logger;
    }

    public async Task<B2AuthorizationInfo> AuthorizeAsync(string keyId, string applicationKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthorizeAccountUrl);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{keyId}:{applicationKey}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        using var response = await _httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Never include keyId/applicationKey or the raw B2 error body — CAS-002 §6.1 error
            // table requires the b2-access-unavailable failure to never reveal which check failed
            // or the B2 error detail.
            _logger.LogWarning(
                "Login-time B2 key authorization failed with HTTP {StatusCode}", (int)response.StatusCode);
            throw new B2ApiException((int)response.StatusCode, null, "b2_authorize_account failed for the supplied user key.");
        }

        var payload = await response.Content.ReadFromJsonAsync<B2WireAuthorizeAccountResponse>(cancellationToken: ct)
            ?? throw new B2ApiException((int)response.StatusCode, null, "b2_authorize_account returned an empty body.");

        var capabilities = payload.Allowed?.Capabilities ?? new List<string>();

        return new B2AuthorizationInfo
        {
            AccountId = payload.AccountId,
            AuthorizationToken = payload.AuthorizationToken,
            ApiUrl = payload.ApiUrl,
            DownloadUrl = payload.DownloadUrl,
            Capabilities = capabilities,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
        };
    }
}
