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
/// In-house <see cref="HttpClient"/>-based <see cref="IB2Client"/> implementation (CAS-001 §6.1)
/// — deliberately not a third-party NuGet package (Backblaze.Client/DQD.Backblaze.Client,
/// Backblaze.Agent, B2Net were surveyed and rejected in the spec: none is an official Backblaze
/// SDK, and the operation set this integration needs is small and fixed enough that an auditable
/// wrapper is lower-risk in a credential-handling path). Shared by Api and Worker via a direct
/// project reference (clean-architecture skill; Worker already references this project).
///
/// Construction (§7 V1): the constructor reads the system key exclusively from
/// <see cref="IConfiguration"/> (B2:SystemKeyId / B2:SystemApplicationKey / B2:BucketName) and
/// throws <see cref="InvalidOperationException"/> if any is missing — there is no constructor
/// parameter through which a per-request/browser-supplied key could ever reach this class, so a
/// non-server key cannot be used to construct an operational client by construction, not by a
/// runtime check.
///
/// Token lifecycle (§6.1): the <c>authorizationToken</c> from <c>b2_authorize_account</c> is
/// cached in memory for the lifetime of this instance (registered as a singleton, per-process —
/// not per-session, not persisted to disk). Every other call reuses the cached token and only
/// re-authenticates when a call comes back <c>401</c>, per the confirmed token-lifecycle design
/// — it does not proactively re-authenticate on a timer.
///
/// Transient-failure retry (CAS-034 §2): <c>AuthorizeAsync</c>, <c>ListFileNamesAsync</c>,
/// <c>ListFileVersionsAsync</c>, <c>GetFileInfoAsync</c>, <c>DownloadFileByIdAsync</c>,
/// <c>GetUploadUrlAsync</c>, and <c>DeleteFileVersionAsync</c> are wrapped in a bounded,
/// exponential-backoff retry (<see cref="SendWithRetryAsync"/>) for HTTP 429/500/502/503/504 and
/// transient network/timeout failures — composes with, and is independent of, the 401-reauth-once
/// behavior above. <c>UploadFileAsync</c> is deliberately excluded (see its own remarks).
/// </summary>
public sealed class B2Client : IB2Client
{
    private const string ApiVersion = "v2";
    private const string AuthorizeAccountUrl = "https://api.backblazeb2.com/b2api/" + ApiVersion + "/b2_authorize_account";

    private readonly HttpClient _httpClient;
    private readonly ILogger<B2Client> _logger;
    private readonly string _keyId;
    private readonly string _applicationKey;
    private readonly string _bucketName;

    // Guards both the cached authorization and the cached bucket id — both are refreshed together
    // on the same re-authenticate path, and there is at most one B2Client instance per process.
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private B2AuthorizationInfo? _cachedAuth;
    private string? _cachedBucketId;

    // CAS-034 §2/§9: bounded retry/backoff config for transient B2 failures — read once at
    // construction with safe in-code defaults, same non-required-config pattern already used for
    // B2:CrossCheckMaxConcurrency (not RequireConfig-style required-or-throw, unlike the system key).
    private readonly int _maxRetryAttempts;
    private readonly int _retryBaseDelayMs;

    public B2Client(HttpClient httpClient, IConfiguration configuration, ILogger<B2Client> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _keyId = RequireConfig(configuration, "B2:SystemKeyId");
        _applicationKey = RequireConfig(configuration, "B2:SystemApplicationKey");
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
                $"Configuration key '{key}' must be set (CAS-001 §9/§7 V1). The operational B2 " +
                "client is constructed only from server-held configuration — never from a " +
                "per-request or browser-supplied value.");
        }

        return value;
    }

    // ---- b2_authorize_account -------------------------------------------------------------

    public async Task<B2AuthorizationInfo> AuthorizeAsync(CancellationToken ct)
    {
        await _authLock.WaitAsync(ct);
        try
        {
            var info = await AuthorizeCoreAsync(ct);
            _cachedAuth = info;
            _cachedBucketId = null; // force bucket-id re-resolution against the fresh authorization
            return info;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private async Task<B2AuthorizationInfo> AuthorizeCoreAsync(CancellationToken ct)
    {
        using var response = await SendWithRetryAsync("b2_authorize_account", async retryCt =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AuthorizeAccountUrl);
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_keyId}:{_applicationKey}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            return await _httpClient.SendAsync(request, retryCt);
        }, ct);

        var payload = await response.Content.ReadFromJsonAsync<B2WireAuthorizeAccountResponse>(cancellationToken: ct)
            ?? throw new B2ApiException((int)response.StatusCode, null, "b2_authorize_account returned an empty body.");

        var capabilities = payload.Allowed?.Capabilities ?? new List<string>();

        // Log only non-sensitive fields — never payload.AuthorizationToken (§10 SC-5).
        _logger.LogInformation(
            "B2 system-key authorization refreshed for account {AccountId}, capabilities [{Capabilities}]",
            payload.AccountId, string.Join(",", capabilities));

        return new B2AuthorizationInfo
        {
            AccountId = payload.AccountId,
            AuthorizationToken = payload.AuthorizationToken,
            ApiUrl = payload.ApiUrl,
            DownloadUrl = payload.DownloadUrl,
            Capabilities = capabilities,
            // §6.1: token is valid at most 24h. This is informational only — re-authentication is
            // triggered reactively on a 401, not proactively from this timestamp.
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
        };
    }

    private async Task<B2AuthorizationInfo> GetAuthorizationAsync(CancellationToken ct) =>
        _cachedAuth ?? await AuthorizeAsync(ct);

    /// <summary>
    /// B2's file operations key on <c>bucketId</c>, not the human-readable <c>bucketName</c> in
    /// configuration (§9). Resolving the name to an id requires <c>b2_list_buckets</c> — not one
    /// of §6.1's seven named operations, but a mechanical necessity to call those seven operations
    /// correctly against the real B2 API; resolved once per authorization and cached alongside it.
    /// </summary>
    private async Task<string> GetBucketIdAsync(CancellationToken ct)
    {
        if (_cachedBucketId is not null)
        {
            return _cachedBucketId;
        }

        var response = await SendWithReauthAsync(auth =>
        {
            var body = JsonSerializer.Serialize(new { accountId = auth.AccountId, bucketName = _bucketName });
            var req = new HttpRequestMessage(HttpMethod.Post, $"{auth.ApiUrl}/b2api/{ApiVersion}/b2_list_buckets")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", auth.AuthorizationToken);
            return req;
        }, ct);

        using (response)
        {
            await EnsureSuccessAsync(response, "b2_list_buckets", ct);
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
            return bucket.BucketId;
        }
    }

    // ---- Shared call plumbing --------------------------------------------------------------

    /// <summary>
    /// Sends a request built from the current cached authorization; on a 401, re-authenticates
    /// once (§6.1 token lifecycle) and retries exactly once with a freshly built request (a
    /// disposed <see cref="HttpRequestMessage"/> cannot be resent).
    /// </summary>
    private async Task<HttpResponseMessage> SendWithReauthAsync(
        Func<B2AuthorizationInfo, HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var auth = await GetAuthorizationAsync(ct);
        using (var firstRequest = requestFactory(auth))
        {
            var response = await _httpClient.SendAsync(firstRequest, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            response.Dispose();
        }

        _logger.LogWarning("B2 call returned 401 (expired/invalid token) — re-authenticating and retrying once.");
        var freshAuth = await AuthorizeAsync(ct);
        using var retryRequest = requestFactory(freshAuth);
        return await _httpClient.SendAsync(retryRequest, ct);
    }

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
    /// CAS-034 §2: wraps a single wire call with bounded, exponential-backoff retry for transient
    /// failures — HTTP 429/500/502/503/504, <see cref="HttpRequestException"/>, and an internal
    /// (non-caller-requested) timeout/cancellation. Non-retryable failures (400/403/404, or a
    /// caller-requested cancellation) surface on the first attempt, exactly as before this spec.
    /// <paramref name="sendAsync"/> is invoked fresh on every attempt (it may itself be, or wrap,
    /// <see cref="SendWithReauthAsync"/>, which keeps its own independent 401-reauthenticate-once
    /// behavior — the two compose rather than interfere). On success or on a non-retryable/
    /// exhausted failure, delegates to <see cref="EnsureSuccessAsync"/> for the final outcome, so
    /// callers no longer call it themselves for any of the seven wrapped operations.
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
    /// 8000ms ceiling is applied last so a Worker loop can never stall indefinitely on one call,
    /// even from an unusually large <c>Retry-After</c> value.
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

    // Never logs the B2 key or an authorization/upload token — only the operation name, attempt
    // count, computed delay, and status code/exception type (§10 SC-4, extends CAS-001 SC-5).
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

    // ---- b2_list_file_names -----------------------------------------------------------------

    public async Task<B2ListFileNamesResult> ListFileNamesAsync(
        string? startFileName, int maxFileCount, CancellationToken ct)
    {
        if (maxFileCount is < 1 or > 1000)
        {
            // §6.2 step 2: B2 caps this at 1000 files per transaction — a higher value just
            // triggers additional billed transactions internally rather than returning more.
            throw new ArgumentOutOfRangeException(
                nameof(maxFileCount), maxFileCount, "b2_list_file_names allows at most 1000 files per call.");
        }

        var bucketId = await GetBucketIdAsync(ct);

        using var response = await SendWithRetryAsync("b2_list_file_names", retryCt => SendWithReauthAsync(auth =>
        {
            var body = JsonSerializer.Serialize(new { bucketId, startFileName, maxFileCount });
            var req = new HttpRequestMessage(HttpMethod.Post, $"{auth.ApiUrl}/b2api/{ApiVersion}/b2_list_file_names")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", auth.AuthorizationToken);
            return req;
        }, retryCt), ct);

        var payload = await response.Content.ReadFromJsonAsync<B2WireListFileNamesResponse>(cancellationToken: ct)
            ?? throw new B2ApiException((int)response.StatusCode, null, "b2_list_file_names returned an empty body.");

        return new B2ListFileNamesResult
        {
            Files = payload.Files.Select(MapFileInfo).ToList(),
            NextFileName = payload.NextFileName
        };
    }

    // ---- b2_list_file_versions ---------------------------------------------------------------

    public async Task<B2ListFileVersionsResult> ListFileVersionsAsync(
        string? startFileName, string? prefix, int maxFileCount, CancellationToken ct)
    {
        if (maxFileCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxFileCount), maxFileCount, "b2_list_file_versions allows at most 1000 files per call.");
        }

        var bucketId = await GetBucketIdAsync(ct);

        using var response = await SendWithRetryAsync("b2_list_file_versions", retryCt => SendWithReauthAsync(auth =>
        {
            var body = JsonSerializer.Serialize(new { bucketId, startFileName, prefix, maxFileCount });
            var req = new HttpRequestMessage(HttpMethod.Post, $"{auth.ApiUrl}/b2api/{ApiVersion}/b2_list_file_versions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", auth.AuthorizationToken);
            return req;
        }, retryCt), ct);

        var payload = await response.Content.ReadFromJsonAsync<B2WireListFileVersionsResponse>(cancellationToken: ct)
            ?? throw new B2ApiException((int)response.StatusCode, null, "b2_list_file_versions returned an empty body.");

        return new B2ListFileVersionsResult
        {
            Files = payload.Files.Select(MapFileInfo).ToList(),
            NextFileName = payload.NextFileName,
            NextFileId = payload.NextFileId
        };
    }

    // ---- b2_get_file_info ---------------------------------------------------------------------

    public async Task<B2FileInfo> GetFileInfoAsync(string fileId, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync("b2_get_file_info", retryCt => SendWithReauthAsync(auth =>
        {
            var body = JsonSerializer.Serialize(new { fileId });
            var req = new HttpRequestMessage(HttpMethod.Post, $"{auth.ApiUrl}/b2api/{ApiVersion}/b2_get_file_info")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", auth.AuthorizationToken);
            return req;
        }, retryCt), ct);

        var payload = await response.Content.ReadFromJsonAsync<B2WireFile>(cancellationToken: ct)
            ?? throw new B2ApiException((int)response.StatusCode, null, "b2_get_file_info returned an empty body.");

        return MapFileInfo(payload);
    }

    // ---- b2_download_file_by_id ----------------------------------------------------------------

    public async Task<B2DownloadedFile> DownloadFileByIdAsync(string fileId, CancellationToken ct)
    {
        // Not `using` — the HttpResponseMessage must outlive this method, since the caller reads
        // the response's live network stream via the returned B2DownloadedFile. Ownership passes
        // to ResponseOwningStream, whose Dispose() disposes the response (and therefore the
        // stream) together; EnsureSuccessAsync/failure paths below dispose it explicitly instead.
        var response = await SendWithRetryAsync("b2_download_file_by_id", retryCt => SendWithReauthAsync(auth =>
        {
            var req = new HttpRequestMessage(
                HttpMethod.Get, $"{auth.DownloadUrl}/b2api/{ApiVersion}/b2_download_file_by_id?fileId={Uri.EscapeDataString(fileId)}");
            req.Headers.TryAddWithoutValidation("Authorization", auth.AuthorizationToken);
            return req;
        }, retryCt), ct);

        try
        {
            // CAS-034 §2: retry already happened (and is exhausted/inapplicable) inside
            // SendWithRetryAsync above — response here is a validated success. Anything from this
            // point on (header parsing, stream construction) is a single attempt, never retried;
            // once B2DownloadedFile is returned to the caller, a mid-stream read failure is the
            // caller's problem, exactly like UploadFileAsync's stream (§6).
            var fileName = response.Headers.TryGetValues("X-Bz-File-Name", out var names)
                ? Uri.UnescapeDataString(names.First())
                : fileId;
            var contentSha1 = response.Headers.TryGetValues("X-Bz-Content-Sha1", out var shas) ? shas.First() : string.Empty;
            var contentLength = response.Content.Headers.ContentLength ?? 0;

            var innerStream = await response.Content.ReadAsStreamAsync(ct);
            var ownedStream = new ResponseOwningStream(innerStream, response);

            return new B2DownloadedFile
            {
                FileName = fileName,
                ContentSha1 = contentSha1,
                ContentLength = contentLength,
                Content = ownedStream
            };
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Wraps the live network stream from a <c>b2_download_file_by_id</c> response so that
    /// disposing the stream (as <see cref="B2DownloadedFile.Dispose"/> does) also disposes the
    /// owning <see cref="HttpResponseMessage"/>, rather than leaking it.
    /// </summary>
    private sealed class ResponseOwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;

        public ResponseOwningStream(Stream inner, HttpResponseMessage response)
        {
            _inner = inner;
            _response = response;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // ---- b2_get_upload_url ---------------------------------------------------------------------

    public async Task<B2UploadUrlInfo> GetUploadUrlAsync(CancellationToken ct)
    {
        var bucketId = await GetBucketIdAsync(ct);

        using var response = await SendWithRetryAsync("b2_get_upload_url", retryCt => SendWithReauthAsync(auth =>
        {
            var body = JsonSerializer.Serialize(new { bucketId });
            var req = new HttpRequestMessage(HttpMethod.Post, $"{auth.ApiUrl}/b2api/{ApiVersion}/b2_get_upload_url")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", auth.AuthorizationToken);
            return req;
        }, retryCt), ct);

        var payload = await response.Content.ReadFromJsonAsync<B2WireGetUploadUrlResponse>(cancellationToken: ct)
            ?? throw new B2ApiException((int)response.StatusCode, null, "b2_get_upload_url returned an empty body.");

        return new B2UploadUrlInfo
        {
            BucketId = payload.BucketId,
            UploadUrl = payload.UploadUrl,
            AuthorizationToken = payload.AuthorizationToken
        };
    }

    // ---- b2_upload_file ------------------------------------------------------------------------

    public async Task<B2FileInfo> UploadFileAsync(
        B2UploadUrlInfo uploadUrl, string fileName, Stream content, string contentSha1, long contentLength, CancellationToken ct)
    {
        // Uses the upload-specific token from GetUploadUrlAsync directly against uploadUrl.UploadUrl
        // — not the system-key authorization/SendWithReauthAsync path. Per B2's own guidance, a
        // failed upload call should be retried against a freshly obtained upload URL, not
        // transparently retried here; that retry policy belongs to the (out-of-scope) mirror
        // population caller, not this client method.
        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl.UploadUrl);
        request.Headers.TryAddWithoutValidation("Authorization", uploadUrl.AuthorizationToken);
        request.Headers.TryAddWithoutValidation("X-Bz-File-Name", Uri.EscapeDataString(fileName));
        request.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", contentSha1);

        using var streamContent = new StreamContent(content);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("b2/x-auto");
        streamContent.Headers.ContentLength = contentLength;
        request.Content = streamContent;

        using var response = await _httpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "b2_upload_file", ct);

        var payload = await response.Content.ReadFromJsonAsync<B2WireFile>(cancellationToken: ct)
            ?? throw new B2ApiException((int)response.StatusCode, null, "b2_upload_file returned an empty body.");

        return MapFileInfo(payload);
    }

    // ---- b2_delete_file_version -----------------------------------------------------------------

    public async Task DeleteFileVersionAsync(string fileName, string fileId, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync("b2_delete_file_version", retryCt => SendWithReauthAsync(auth =>
        {
            var body = JsonSerializer.Serialize(new { fileName, fileId });
            var req = new HttpRequestMessage(HttpMethod.Post, $"{auth.ApiUrl}/b2api/{ApiVersion}/b2_delete_file_version")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", auth.AuthorizationToken);
            return req;
        }, retryCt), ct);

        _logger.LogInformation("B2 delete_file_version succeeded for {FileName} ({FileId})", fileName, fileId);
    }
}
