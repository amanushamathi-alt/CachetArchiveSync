using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;

namespace Cachet.ArchiveSync.Api.B2;

/// <summary>
/// <see cref="IUserScopedB2DeleteClient"/> implementation (CAS-018 §2/§5) — executes
/// <c>b2_list_file_versions</c>/<c>b2_delete_file_version</c> under the acting user's own, live
/// <see cref="B2AuthorizationInfo"/> (never the system key), for Permanent Delete only. Lives in
/// this namespace (not <c>Cachet.ArchiveSync.Api.Services</c>) specifically to reuse
/// <see cref="B2WireModels"/>'s internal wire-format types rather than duplicating them — same
/// reuse rationale <see cref="UserB2KeyAuthorizer"/>'s doc comment already gives for living here.
///
/// Bucket identity does not vary by acting user (CAS-018 §14 — this project targets exactly one
/// B2 bucket), so <see cref="GetBucketIdAsync"/> does its own one-time <c>b2_list_buckets</c>
/// resolution using the server-held SYSTEM key (config keys <c>B2:SystemKeyId</c> /
/// <c>B2:SystemApplicationKey</c> / <c>B2:BucketName</c> — the same configuration
/// <c>B2Client</c>'s constructor reads), cached for the lifetime of this instance exactly like
/// <c>B2Client._cachedBucketId</c>. This class never calls <c>b2_list_buckets</c> with a per-user
/// key. The per-user <see cref="B2AuthorizationInfo"/> passed into
/// <see cref="ListFileVersionsAsync"/>/<see cref="DeleteFileVersionAsync"/> is used ONLY for those
/// calls' Authorization header — never for bucket resolution (CAS-018 §11).
///
/// Transient-failure retry (CAS-036 §2): all four HTTP calls this class makes
/// (<c>b2_authorize_account</c> in <see cref="AuthorizeSystemKeyAsync"/>, <c>b2_list_buckets</c> in
/// <see cref="GetBucketIdAsync"/>, <c>b2_list_file_versions</c> in <see cref="ListFileVersionsAsync"/>,
/// and <c>b2_delete_file_version</c> in <see cref="DeleteFileVersionAsync"/>) are wrapped in a
/// bounded, exponential-backoff retry (<see cref="SendWithRetryAsync"/>) for HTTP 429/500/502/503/504
/// and transient network/timeout failures — the algorithm is deliberately mirrored from
/// <c>B2Client.SendWithRetryAsync</c> (CAS-034 §2) rather than shared, consistent with this class's
/// own established idiom (above) of mirroring <c>B2Client</c> rather than depending on it. This class
/// has no 401-reauthenticate-once behavior (unlike <c>B2Client</c>) — out of scope, unchanged by
/// CAS-036.
/// </summary>
public sealed class UserScopedB2DeleteClient : IUserScopedB2DeleteClient
{
    private const string ApiVersion = "v2";
    private const string AuthorizeAccountUrl = "https://api.backblazeb2.com/b2api/" + ApiVersion + "/b2_authorize_account";

    private readonly HttpClient _httpClient;
    private readonly ILogger<UserScopedB2DeleteClient> _logger;
    private readonly string _systemKeyId;
    private readonly string _systemApplicationKey;
    private readonly string _bucketName;

    // Guards the cached system-key-derived bucket id — resolved at most once per process, same
    // discipline as B2Client's own _authLock/_cachedBucketId (CAS-018 §14).
    private readonly SemaphoreSlim _bucketLock = new(1, 1);
    private string? _cachedBucketId;

    // CAS-036 §2/§9: bounded retry/backoff config for transient B2 failures — reuses the same
    // existing keys/defaults B2Client reads (CAS-034 §2), no new config keys.
    private readonly int _maxRetryAttempts;
    private readonly int _retryBaseDelayMs;

    public UserScopedB2DeleteClient(HttpClient httpClient, IConfiguration configuration, ILogger<UserScopedB2DeleteClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _systemKeyId = RequireConfig(configuration, "B2:SystemKeyId");
        _systemApplicationKey = RequireConfig(configuration, "B2:SystemApplicationKey");
        _bucketName = RequireConfig(configuration, "B2:BucketName");

        _maxRetryAttempts = configuration.GetValue<int?>("B2:MaxRetryAttempts") ?? 3;
        _retryBaseDelayMs = configuration.GetValue<int?>("B2:RetryBaseDelayMs") ?? 500;
    }

    private static string RequireConfig(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Configuration key '{key}' must be set (CAS-018 §14). Bucket resolution for the " +
                "user-scoped delete client uses the same server-held system key as B2Client, " +
                "never a per-user key.");
        }

        return value;
    }

    // ---- Bucket id resolution (system key only, CAS-018 §14) ------------------------------

    public async Task<string> GetBucketIdAsync(CancellationToken ct)
    {
        if (_cachedBucketId is not null)
        {
            return _cachedBucketId;
        }

        await _bucketLock.WaitAsync(ct);
        try
        {
            if (_cachedBucketId is not null)
            {
                return _cachedBucketId;
            }

            var systemAuth = await AuthorizeSystemKeyAsync(ct);

            using var response = await SendWithRetryAsync("b2_list_buckets", async retryCt =>
            {
                var body = JsonSerializer.Serialize(new { accountId = systemAuth.AccountId, bucketName = _bucketName });
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{systemAuth.ApiUrl}/b2api/{ApiVersion}/b2_list_buckets")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("Authorization", systemAuth.AuthorizationToken);
                return await _httpClient.SendAsync(request, retryCt);
            }, ct);

            var payload = await response.Content.ReadFromJsonAsync<B2WireListBucketsResponse>(cancellationToken: ct)
                ?? throw new B2ApiException((int)response.StatusCode, null, "b2_list_buckets returned an empty body.");

            var bucket = payload.Buckets.FirstOrDefault(b =>
                string.Equals(b.BucketName, _bucketName, StringComparison.Ordinal));

            if (bucket is null)
            {
                throw new InvalidOperationException(
                    $"Configured B2:BucketName '{_bucketName}' was not found among the buckets " +
                    "visible to the configured system key.");
            }

            _cachedBucketId = bucket.BucketId;
            return _cachedBucketId;
        }
        finally
        {
            _bucketLock.Release();
        }
    }

    private async Task<B2AuthorizationInfo> AuthorizeSystemKeyAsync(CancellationToken ct)
    {
        using var response = await SendWithRetryAsync("b2_authorize_account", async retryCt =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AuthorizeAccountUrl);
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_systemKeyId}:{_systemApplicationKey}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            return await _httpClient.SendAsync(request, retryCt);
        }, ct);

        var payload = await response.Content.ReadFromJsonAsync<B2WireAuthorizeAccountResponse>(cancellationToken: ct)
            ?? throw new B2ApiException((int)response.StatusCode, null, "b2_authorize_account returned an empty body.");

        // Log only non-sensitive fields — never payload.AuthorizationToken (CAS-018 §11).
        _logger.LogInformation(
            "User-scoped B2 delete client resolved bucket via system key for account {AccountId}",
            payload.AccountId);

        return new B2AuthorizationInfo
        {
            AccountId = payload.AccountId,
            AuthorizationToken = payload.AuthorizationToken,
            ApiUrl = payload.ApiUrl,
            DownloadUrl = payload.DownloadUrl,
            Capabilities = payload.Allowed?.Capabilities ?? new List<string>(),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
        };
    }

    // ---- b2_list_file_versions (acting user's own live authorization) ----------------------

    public async Task<B2ListFileVersionsResult> ListFileVersionsAsync(
        B2AuthorizationInfo authorization, string bucketId, string? startFileName, string? prefix, int maxFileCount, CancellationToken ct)
    {
        if (maxFileCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFileCount), maxFileCount, "b2_list_file_versions allows at most 1000 files per call.");
        }

        using var response = await SendWithRetryAsync("b2_list_file_versions", async retryCt =>
        {
            var body = JsonSerializer.Serialize(new { bucketId, startFileName, prefix, maxFileCount });
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{authorization.ApiUrl}/b2api/{ApiVersion}/b2_list_file_versions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", authorization.AuthorizationToken);
            return await _httpClient.SendAsync(request, retryCt);
        }, ct);

        var payload = await response.Content.ReadFromJsonAsync<B2WireListFileVersionsResponse>(cancellationToken: ct)
            ?? throw new B2ApiException((int)response.StatusCode, null, "b2_list_file_versions returned an empty body.");

        return new B2ListFileVersionsResult
        {
            Files = payload.Files.Select(MapFileInfo).ToList(),
            NextFileName = payload.NextFileName,
            NextFileId = payload.NextFileId
        };
    }

    // ---- b2_delete_file_version (acting user's own live authorization) ---------------------

    public async Task DeleteFileVersionAsync(B2AuthorizationInfo authorization, string fileName, string fileId, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync("b2_delete_file_version", async retryCt =>
        {
            var body = JsonSerializer.Serialize(new { fileName, fileId });
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{authorization.ApiUrl}/b2api/{ApiVersion}/b2_delete_file_version")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", authorization.AuthorizationToken);
            return await _httpClient.SendAsync(request, retryCt);
        }, ct);

        // Non-sensitive fields only — never authorization.AuthorizationToken (CAS-018 §11).
        _logger.LogInformation("User-scoped B2 delete_file_version succeeded for {FileName} ({FileId})", fileName, fileId);
    }

    // ---- Shared call plumbing ---------------------------------------------------------------

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? code = null;
        string message = $"{operation} failed with HTTP {(int)response.StatusCode}.";
        try
        {
            var error = await response.Content.ReadFromJsonAsync<B2WireErrorResponse>(cancellationToken: ct);
            if (error is not null)
            {
                code = error.Code;
                message = error.Message is null
                    ? message
                    : $"{operation} failed: {error.Code} — {error.Message}";
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body — fall back to the status-code-only message built above.
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new B2NotFoundException(message);
        }

        throw new B2ApiException((int)response.StatusCode, code, message);
    }

    /// <summary>
    /// CAS-036 §2: wraps a single wire call with bounded, exponential-backoff retry for transient
    /// failures — HTTP 429/500/502/503/504, <see cref="HttpRequestException"/>, and an internal
    /// (non-caller-requested) timeout/cancellation. Non-retryable failures (400/403/404, or a
    /// caller-requested cancellation) surface on the first attempt, exactly as before this spec.
    /// Mirrors <c>B2Client.SendWithRetryAsync</c> (CAS-034 §2) — deliberately duplicated rather than
    /// shared (§14). <paramref name="sendAsync"/> is invoked fresh on every attempt so it can build
    /// a new <see cref="HttpRequestMessage"/> each time (a disposed request cannot be resent). On
    /// success or on a non-retryable/exhausted failure, delegates to <see cref="EnsureSuccessAsync"/>
    /// for the final outcome, so callers no longer call it themselves for any of the four wrapped
    /// operations.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string operation, Func<CancellationToken, Task<HttpResponseMessage>> sendAsync, CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _maxRetryAttempts);

        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await sendAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if (!IsRetryableStatusCode(response.StatusCode) || attempt >= maxAttempts)
                {
                    await EnsureSuccessAsync(response, operation, ct);
                    return response; // unreachable — EnsureSuccessAsync always throws for a non-success status
                }

                var delay = ComputeDelay(attempt, response);
                LogRetryWarning(operation, attempt, maxAttempts, delay, response.StatusCode.ToString());
                response.Dispose();
                await Task.Delay(delay, ct);
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                response?.Dispose();
                var delay = ComputeDelay(attempt, null);
                LogRetryWarning(operation, attempt, maxAttempts, delay, "HttpRequestException");
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                // Caller-requested cancellation (ct.IsCancellationRequested) is excluded by the
                // filter above and propagates immediately, unretried — including when it occurs
                // during Task.Delay itself (§2).
                response?.Dispose();
                var delay = ComputeDelay(attempt, null);
                LogRetryWarning(operation, attempt, maxAttempts, delay, "OperationCanceledException (internal timeout)");
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// §2 backoff formula: <c>RetryBaseDelayMs * 2^(attempt-1)</c> plus 0-100ms jitter, capped at a
    /// fixed 8000ms ceiling. A 429's <c>Retry-After</c> header is honored in place of the computed
    /// delay — taking the greater of the two only when <c>Retry-After</c> is implausibly small —
    /// which is equivalent to always taking <c>max(RetryAfter, computed)</c> when present. The
    /// 8000ms ceiling is applied last so this can never stall indefinitely on one call, even from an
    /// unusually large <c>Retry-After</c> value.
    /// </summary>
    private TimeSpan ComputeDelay(int attempt, HttpResponseMessage? response)
    {
        const int CeilingMs = 8000;

        var computedMs = _retryBaseDelayMs * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 101);
        var retryAfterMs = TryGetRetryAfterMs(response);
        var delayMs = retryAfterMs is { } ra ? Math.Max(ra, computedMs) : computedMs;

        return TimeSpan.FromMilliseconds(Math.Min(delayMs, CeilingMs));
    }

    private static double? TryGetRetryAfterMs(HttpResponseMessage? response)
    {
        if (response is null || response.StatusCode != HttpStatusCode.TooManyRequests)
        {
            return null;
        }

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return Math.Max(0, delta.TotalMilliseconds);
        }

        if (retryAfter.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining.TotalMilliseconds : 0;
        }

        return null;
    }

    // Never logs the B2 key, the system authorization token, or the acting user's authorization
    // token — only the operation name, attempt count, computed delay, and status code/exception
    // type (§10 SC-4, extends CAS-001 SC-5 / CAS-018 §11 / CAS-034 SC-4).
    private void LogRetryWarning(string operation, int attempt, int maxAttempts, TimeSpan delay, string reason) =>
        _logger.LogWarning(
            "B2 {Operation} transient failure on attempt {Attempt}/{MaxAttempts} ({Reason}) — retrying in {DelayMs}ms.",
            operation, attempt, maxAttempts, reason, delay.TotalMilliseconds);

    private static B2FileInfo MapFileInfo(B2WireFile file) => new()
    {
        FileId = file.FileId,
        FileName = file.FileName,
        ContentSha1 = file.ContentSha1 ?? string.Empty,
        ContentLength = file.ContentLength ?? file.Size ?? 0,
        Action = file.Action ?? "upload",
        UploadTimestamp = file.UploadTimestamp
    };
}
