using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cachet.ArchiveSync.Api.B2;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.Tests;

/// <summary>
/// Unit tests for <see cref="UserScopedB2DeleteClient"/> (CAS-018 §2/§5/§14) — construction/call
/// discipline mirroring <c>B2ClientTests</c>' V-1 shape (per the spec's §13 Test Plan): the client
/// can only resolve its bucket id via the server-held system key, while
/// <see cref="UserScopedB2DeleteClient.ListFileVersionsAsync"/>/<see cref="UserScopedB2DeleteClient.DeleteFileVersionAsync"/>
/// use only the caller-supplied, per-request <see cref="B2AuthorizationInfo"/> — never the system
/// key — for their own Authorization header.
/// </summary>
public sealed class UserScopedB2DeleteClientTests
{
    private const string SystemKeyId = "0000000000000000000000001";
    private const string SystemApplicationKey = "K000SystemApplicationKeyValueNeverLeak";
    private const string BucketName = "cachet-archive-mirror";
    private const string SystemAuthToken = "4_systemAuthToken_neverLog0000000000000000000000000000000000000";
    private const string UserAuthToken = "4_userAuthToken_neverLog00000000000000000000000000000000000000";
    private const string BucketId = "bucket-abc123";

    private static IConfiguration CreateConfiguration(string? keyId, string? applicationKey, string? bucketName)
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["B2:SystemKeyId"].Returns(keyId);
        configuration["B2:SystemApplicationKey"].Returns(applicationKey);
        configuration["B2:BucketName"].Returns(bucketName);
        return configuration;
    }

    private static IConfiguration CreateValidConfiguration() =>
        CreateConfiguration(SystemKeyId, SystemApplicationKey, BucketName);

    private static B2AuthorizationInfo UserAuthorization(string apiUrl = "https://api-fake.backblazeb2.com") => new()
    {
        AccountId = "acct-user-1",
        AuthorizationToken = UserAuthToken,
        ApiUrl = apiUrl,
        DownloadUrl = "https://download-fake.backblazeb2.com",
        Capabilities = new[] { "listFiles", "readFiles", "writeFiles", "deleteFiles" },
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
    };

    /// <summary>Routes by request path/method, recording every request it sees — the only seam
    /// available for exercising this class's real request-building/response-mapping logic without
    /// a live B2 endpoint (same rationale as <c>B2ClientTests.StubAuthorizeHandler</c> /
    /// <c>UserB2KeyAuthorizerTests.FakeHandler</c>).</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage AuthorizeAccountResponse() => JsonResponse(HttpStatusCode.OK, new
    {
        accountId = "acct-system-1",
        authorizationToken = SystemAuthToken,
        apiUrl = "https://api-fake.backblazeb2.com",
        downloadUrl = "https://download-fake.backblazeb2.com",
        allowed = new { capabilities = new[] { "listFiles", "readFiles", "writeFiles", "deleteFiles" } }
    });

    private static HttpResponseMessage ListBucketsResponse(string bucketName, string bucketId) => JsonResponse(HttpStatusCode.OK, new
    {
        buckets = new[] { new { bucketId, bucketName } }
    });

    /// <summary>Standard fake transport: b2_authorize_account (system key) then b2_list_buckets
    /// succeed, matching the configured bucket name to <see cref="BucketId"/>.</summary>
    private static FakeHandler CreateHappyPathHandler(string configuredBucketName = BucketName) =>
        new(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                _ when path.EndsWith("b2_authorize_account", StringComparison.Ordinal) => AuthorizeAccountResponse(),
                _ when path.EndsWith("b2_list_buckets", StringComparison.Ordinal) => ListBucketsResponse(configuredBucketName, BucketId),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

    private static UserScopedB2DeleteClient CreateSut(FakeHandler handler, out ILogger<UserScopedB2DeleteClient> logger)
    {
        logger = Substitute.For<ILogger<UserScopedB2DeleteClient>>();
        var httpClient = new HttpClient(handler);
        return new UserScopedB2DeleteClient(httpClient, CreateValidConfiguration(), logger);
    }

    private static List<string> GetFormattedLogMessages(ILogger<UserScopedB2DeleteClient> logger)
    {
        var messages = new List<string>();
        foreach (var call in logger.ReceivedCalls())
        {
            var args = call.GetArguments();
            if (args.Length < 5 || args[2] is null || args[4] is not Delegate formatter)
            {
                continue;
            }

            var message = (string?)formatter.DynamicInvoke(args[2], args[3] as Exception);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    // ---- Construction: only ever from server-held system key config (mirrors B2ClientTests V-1) ----

    [Theory]
    [InlineData(null, SystemApplicationKey, BucketName)]
    [InlineData("", SystemApplicationKey, BucketName)]
    [InlineData("   ", SystemApplicationKey, BucketName)]
    [InlineData(SystemKeyId, null, BucketName)]
    [InlineData(SystemKeyId, "", BucketName)]
    [InlineData(SystemKeyId, SystemApplicationKey, null)]
    [InlineData(SystemKeyId, SystemApplicationKey, "")]
    public void Test_Construct_MissingServerConfig_Throws(string? keyId, string? applicationKey, string? bucketName)
    {
        // Arrange
        var configuration = CreateConfiguration(keyId, applicationKey, bucketName);
        using var httpClient = new HttpClient();
        var logger = Substitute.For<ILogger<UserScopedB2DeleteClient>>();

        // Act
        var act = () => new UserScopedB2DeleteClient(httpClient, configuration, logger);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be set*");
    }

    [Fact]
    public void Test_Construct_AllServerConfigPresent_DoesNotThrow()
    {
        // Arrange
        var configuration = CreateValidConfiguration();
        using var httpClient = new HttpClient();
        var logger = Substitute.For<ILogger<UserScopedB2DeleteClient>>();

        // Act
        var act = () => new UserScopedB2DeleteClient(httpClient, configuration, logger);

        // Assert
        act.Should().NotThrow();
    }

    // ---- GetBucketIdAsync: system key only (CAS-018 §14) ---------------------------------------------

    [Fact]
    public async Task Test_GetBucketIdAsync_ResolvesViaSystemKey_UsesBasicAuthWithSystemKeyPair()
    {
        // Arrange
        var handler = CreateHappyPathHandler();
        var sut = CreateSut(handler, out _);

        // Act
        var bucketId = await sut.GetBucketIdAsync(CancellationToken.None);

        // Assert
        bucketId.Should().Be(BucketId);
        var authorizeRequest = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("b2_authorize_account", StringComparison.Ordinal));
        authorizeRequest.Headers.Authorization.Should().NotBeNull();
        authorizeRequest.Headers.Authorization!.Scheme.Should().Be("Basic");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorizeRequest.Headers.Authorization.Parameter!));
        decoded.Should().Be($"{SystemKeyId}:{SystemApplicationKey}");

        var listBucketsRequest = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("b2_list_buckets", StringComparison.Ordinal));
        listBucketsRequest.Headers.TryGetValues("Authorization", out var authHeaderValues).Should().BeTrue();
        authHeaderValues!.Single().Should().Be(SystemAuthToken);
    }

    [Fact]
    public async Task Test_GetBucketIdAsync_CachesResultAcrossCalls_OnlyOneRoundTrip()
    {
        // Arrange
        var handler = CreateHappyPathHandler();
        var sut = CreateSut(handler, out _);

        // Act
        var first = await sut.GetBucketIdAsync(CancellationToken.None);
        var second = await sut.GetBucketIdAsync(CancellationToken.None);

        // Assert — resolved once, per-process, exactly like B2Client._cachedBucketId (CAS-018 §14).
        first.Should().Be(BucketId);
        second.Should().Be(BucketId);
        handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("b2_authorize_account", StringComparison.Ordinal)).Should().Be(1);
        handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("b2_list_buckets", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Test_GetBucketIdAsync_ConfiguredBucketNameNotFound_Throws()
    {
        // Arrange — b2_list_buckets succeeds but returns no bucket matching B2:BucketName.
        var handler = CreateHappyPathHandler(configuredBucketName: "some-other-bucket");
        var sut = CreateSut(handler, out _);

        // Act
        var act = () => sut.GetBucketIdAsync(CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{BucketName}*");
    }

    // ---- ListFileVersionsAsync: caller-supplied per-user authorization only, never the system key ---

    [Fact]
    public async Task Test_ListFileVersionsAsync_UsesCallerSuppliedAuthorization_NotSystemKey()
    {
        // Arrange
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("b2_list_file_versions", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, new
                {
                    files = new[]
                    {
                        new { fileId = "4_z_v1", fileName = "contracts/2019/agreement-004.pdf", contentSha1 = "sha1", contentLength = 1024L, action = "upload", uploadTimestamp = 1L }
                    },
                    nextFileName = (string?)null,
                    nextFileId = (string?)null
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var sut = CreateSut(handler, out _);
        var authorization = UserAuthorization();

        // Act
        var result = await sut.ListFileVersionsAsync(
            authorization, BucketId, startFileName: "contracts/2019/agreement-004.pdf", prefix: "contracts/2019/agreement-004.pdf", maxFileCount: 1000, CancellationToken.None);

        // Assert — mapped result, and the Authorization header carries the per-user token, never
        // any system-key-derived Basic credential (this call never calls b2_authorize_account at
        // all — no system key round trip happens here).
        result.Files.Should().ContainSingle(f => f.FileId == "4_z_v1" && f.FileName == "contracts/2019/agreement-004.pdf");
        var request = handler.Requests.Single();
        request.Headers.TryGetValues("Authorization", out var authHeaderValues).Should().BeTrue();
        authHeaderValues!.Single().Should().Be(UserAuthToken);
        authHeaderValues.Single().Should().NotContain(SystemKeyId).And.NotContain(SystemApplicationKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Test_ListFileVersionsAsync_MaxFileCountOutOfRange_ThrowsWithoutCallingB2(int maxFileCount)
    {
        // Arrange — no HTTP call should even be attempted for an invalid page size.
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var sut = CreateSut(handler, out _);

        // Act
        var act = () => sut.ListFileVersionsAsync(UserAuthorization(), BucketId, null, null, maxFileCount, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        handler.Requests.Should().BeEmpty();
    }

    // ---- DeleteFileVersionAsync: caller-supplied per-user authorization only, never logs the token --

    [Fact]
    public async Task Test_DeleteFileVersionAsync_UsesCallerSuppliedAuthorization_LogsOnlyNonSensitiveFields()
    {
        // Arrange
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("b2_delete_file_version", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var sut = CreateSut(handler, out var logger);
        var authorization = UserAuthorization();

        // Act
        await sut.DeleteFileVersionAsync(authorization, "contracts/2019/agreement-004.pdf", "4_z_v1", CancellationToken.None);

        // Assert
        var request = handler.Requests.Single();
        request.Headers.TryGetValues("Authorization", out var authHeaderValues).Should().BeTrue();
        authHeaderValues!.Single().Should().Be(UserAuthToken);

        var loggedMessages = GetFormattedLogMessages(logger);
        loggedMessages.Should().NotBeEmpty();
        loggedMessages.Should().OnlyContain(m => !m.Contains(UserAuthToken));
    }

    // ---- Error mapping: non-success responses -----------------------------------------------------

    [Fact]
    public async Task Test_DeleteFileVersionAsync_NotFoundResponse_ThrowsB2NotFoundException()
    {
        // Arrange
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.NotFound, new { status = 404, code = "file_not_present", message = "not found" }));
        var sut = CreateSut(handler, out _);

        // Act
        var act = () => sut.DeleteFileVersionAsync(UserAuthorization(), "contracts/2019/agreement-004.pdf", "4_z_v1", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<B2NotFoundException>();
    }

    [Fact]
    public async Task Test_ListFileVersionsAsync_NonSuccessResponse_ThrowsB2ApiException_NeverLeaksToken()
    {
        // Arrange
        var handler = new FakeHandler(_ => JsonResponse(HttpStatusCode.Unauthorized, new { status = 401, code = "bad_auth_token", message = "Invalid authorization token" }));
        var sut = CreateSut(handler, out _);
        var authorization = UserAuthorization();

        // Act
        var act = () => sut.ListFileVersionsAsync(authorization, BucketId, null, null, 1000, CancellationToken.None);

        // Assert
        var exception = await act.Should().ThrowAsync<B2ApiException>();
        exception.Which.StatusCode.Should().Be(401);
        exception.Which.Message.Should().NotContain(UserAuthToken);
    }

    // =============================================================================================
    // CAS-036 — bounded retry/backoff for UserScopedB2DeleteClient's four HTTP calls
    // =============================================================================================
    //
    // <see cref="ListFileVersionsAsync"/> is used as the representative retry-eligible operation
    // for the generic retry/backoff/cancellation/logging tests (V1-V3, SC-1 through SC-4): it needs
    // no separate bucket-id/system-key priming call, so a queued response sequence lines up exactly
    // with the attempts SendWithRetryAsync makes — the same reason B2ClientTests uses AuthorizeAsync
    // as its representative operation. SC-5 exercises DeleteFileVersionAsync directly, since that is
    // the exact call QuarantinePermanentDeleteService.DeleteAllVersionsAsync's DeleteFailedB2 path
    // depends on.

    private static IConfiguration CreateRetryConfiguration(int maxRetryAttempts, int retryBaseDelayMs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["B2:SystemKeyId"] = SystemKeyId,
                ["B2:SystemApplicationKey"] = SystemApplicationKey,
                ["B2:BucketName"] = BucketName,
                ["B2:MaxRetryAttempts"] = maxRetryAttempts.ToString(),
                ["B2:RetryBaseDelayMs"] = retryBaseDelayMs.ToString()
            })
            .Build();

    private static UserScopedB2DeleteClient CreateSut(FakeHandler handler, IConfiguration configuration, out ILogger<UserScopedB2DeleteClient> logger)
    {
        logger = Substitute.For<ILogger<UserScopedB2DeleteClient>>();
        var httpClient = new HttpClient(handler);
        return new UserScopedB2DeleteClient(httpClient, configuration, logger);
    }

    /// <summary>Wraps <see cref="FakeHandler"/> with an ordered response queue, dequeued
    /// regardless of request path — used for the single-wire-call representative operations
    /// (<c>ListFileVersionsAsync</c>/<c>DeleteFileVersionAsync</c>) where every attempt targets the
    /// same endpoint, so a plain queue is enough to script "fail N times then succeed"/"fail every
    /// allowed attempt".</summary>
    private static FakeHandler CreateQueuedHandler(params Func<HttpResponseMessage>[] responses)
    {
        var queue = new Queue<Func<HttpResponseMessage>>(responses);
        return new FakeHandler(_ =>
        {
            if (queue.Count == 0)
            {
                throw new InvalidOperationException("CreateQueuedHandler ran out of scripted responses.");
            }

            return queue.Dequeue()();
        });
    }

    private static HttpResponseMessage BuildErrorResponse(HttpStatusCode status, string code = "error") =>
        JsonResponse(status, new { status = (int)status, code, message = $"{code} for test" });

    private static HttpResponseMessage BuildRetryAfterResponse(TimeSpan retryAfter)
    {
        var response = BuildErrorResponse(HttpStatusCode.TooManyRequests, "too_many_requests");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private static HttpResponseMessage ListFileVersionsSuccessResponse() => JsonResponse(HttpStatusCode.OK, new
    {
        files = Array.Empty<object>(),
        nextFileName = (string?)null,
        nextFileId = (string?)null
    });

    // ---- V1: retried up to B2:MaxRetryAttempts with exponential backoff -----------------------

    [Fact]
    public async Task Test_V1_TransientFailureRetriedUpToMaxAttempts()
    {
        // Arrange — attempts 1 and 2 fail transiently (503, then 500), attempt 3 (the last one
        // B2:MaxRetryAttempts allows) succeeds — proves retry happens up to, and including, the
        // configured bound.
        var handler = CreateQueuedHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => BuildErrorResponse(HttpStatusCode.InternalServerError),
            () => ListFileVersionsSuccessResponse());
        var sut = CreateSut(handler, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), out _);

        // Act
        var result = await sut.ListFileVersionsAsync(UserAuthorization(), BucketId, null, null, 1000, CancellationToken.None);

        // Assert
        result.Files.Should().BeEmpty();
        handler.Requests.Should().HaveCount(3, "both retryable failures should have been retried up to the third and final allowed attempt");
    }

    // ---- V1 (HttpRequestException branch): a transient network failure is retried the same way
    // a retryable HTTP status is (CAS-036 review §1 SC-1 note — this branch was previously untested).

    [Fact]
    public async Task Test_V1_HttpRequestExceptionTransientFailureRetriedThenSucceeds()
    {
        // Arrange — attempt 1 throws HttpRequestException (simulating a connection-level transient
        // failure, e.g. a reset connection), attempt 2 succeeds.
        var handler = CreateQueuedHandler(
            () => throw new HttpRequestException("Simulated transient network failure"),
            () => ListFileVersionsSuccessResponse());
        var sut = CreateSut(handler, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), out _);

        // Act
        var result = await sut.ListFileVersionsAsync(UserAuthorization(), BucketId, null, null, 1000, CancellationToken.None);

        // Assert
        result.Files.Should().BeEmpty();
        handler.Requests.Should().HaveCount(2, "the HttpRequestException on attempt 1 should have been retried, not surfaced");
    }

    // ---- V2: non-retryable failures (and caller-cancelled requests) surface immediately ---------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(null, true)]
    public async Task Test_V2_NonRetryableFailureSurfacesImmediately(HttpStatusCode? statusCode, bool preCancelled)
    {
        // Arrange
        var handler = CreateQueuedHandler(() => BuildErrorResponse(statusCode ?? HttpStatusCode.BadRequest));
        var sut = CreateSut(handler, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), out _);
        using var cts = new CancellationTokenSource();
        if (preCancelled)
        {
            cts.Cancel();
        }

        // Act
        var act = () => sut.ListFileVersionsAsync(UserAuthorization(), BucketId, null, null, 1000, cts.Token);

        // Assert — never retried, regardless of which non-retryable condition applies.
        if (preCancelled)
        {
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        else if (statusCode == HttpStatusCode.NotFound)
        {
            await act.Should().ThrowAsync<B2NotFoundException>();
            handler.Requests.Should().HaveCount(1, "a non-retryable 404 must surface on the first attempt");
        }
        else
        {
            var thrown = await act.Should().ThrowAsync<B2ApiException>();
            thrown.Which.StatusCode.Should().Be((int)statusCode!.Value);
            handler.Requests.Should().HaveCount(1, "a non-retryable 4xx must surface on the first attempt");
        }
    }

    // ---- V3: a 429 Retry-After header is honored over the computed backoff ----------------------

    [Fact]
    public async Task Test_V3_RetryAfterHeaderHonoredOverComputedBackoff()
    {
        // Arrange — RetryBaseDelayMs is tiny (1ms, so the computed exponential delay is at most a
        // couple of milliseconds plus up to 100ms of jitter), but the 429 response's Retry-After
        // asks for 300ms — comfortably larger than the computed ceiling this configuration can
        // produce, so honoring Retry-After is the only way the observed delay could reach ~300ms.
        var retryAfter = TimeSpan.FromMilliseconds(300);
        var handler = CreateQueuedHandler(
            () => BuildRetryAfterResponse(retryAfter),
            () => ListFileVersionsSuccessResponse());
        var sut = CreateSut(handler, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), out _);
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = await sut.ListFileVersionsAsync(UserAuthorization(), BucketId, null, null, 1000, CancellationToken.None);
        stopwatch.Stop();

        // Assert
        result.Files.Should().BeEmpty();
        handler.Requests.Should().HaveCount(2);
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250), "the Retry-After value (300ms), not the ~1-101ms computed backoff, should have been honored");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "Retry-After should not be inflated all the way to the 8s ceiling for a 300ms request");
    }

    // ---- SC-1: transient failure then success returns the successful result -------------------

    [Fact]
    public async Task Test_SC1_TransientFailureEventuallySucceeds()
    {
        // Arrange — attempt 1 comes back 503 (retryable), attempt 2 succeeds, well within
        // B2:MaxRetryAttempts (3).
        var handler = CreateQueuedHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => ListFileVersionsSuccessResponse());
        var sut = CreateSut(handler, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), out _);

        // Act
        var result = await sut.ListFileVersionsAsync(UserAuthorization(), BucketId, null, null, 1000, CancellationToken.None);

        // Assert — the caller sees only the successful result, never the attempt-1 failure.
        result.Files.Should().BeEmpty();
        handler.Requests.Should().HaveCount(2, "one failed attempt and one successful retry should have occurred");
    }

    // ---- SC-2: exhausted retries surface the original failure ---------------------------------

    [Fact]
    public async Task Test_SC2_ExhaustedRetriesSurfacesOriginalFailure()
    {
        // Arrange — every attempt (both allowed by B2:MaxRetryAttempts = 2) comes back 503.
        var handler = CreateQueuedHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable));
        var sut = CreateSut(handler, CreateRetryConfiguration(maxRetryAttempts: 2, retryBaseDelayMs: 1), out _);

        // Act
        var act = () => sut.ListFileVersionsAsync(UserAuthorization(), BucketId, null, null, 1000, CancellationToken.None);

        // Assert — the same exception type/content attempt 1 would have thrown today (a 503
        // B2ApiException), not a new wrapper type, and no attempt beyond MaxRetryAttempts.
        var thrown = await act.Should().ThrowAsync<B2ApiException>();
        thrown.Which.Should().NotBeOfType<B2NotFoundException>();
        thrown.Which.StatusCode.Should().Be((int)HttpStatusCode.ServiceUnavailable);
        handler.Requests.Should().HaveCount(2, "no attempt beyond B2:MaxRetryAttempts should have been made");
    }

    // ---- SC-3: caller cancellation during a retry delay is honored immediately ------------------

    [Fact]
    public async Task Test_SC3_CallerCancellationDuringRetryDelayHonoredImmediately()
    {
        // Arrange — attempt 1 fails transiently, which schedules a ~5s backoff delay
        // (RetryBaseDelayMs = 5000); the caller cancels 50ms into that delay. MaxRetryAttempts = 3
        // means attempt 1 < maxAttempts, so the code path under test is genuinely "cancelled while
        // waiting to retry", not "cancelled because retries were already exhausted".
        var handler = CreateQueuedHandler(() => BuildErrorResponse(HttpStatusCode.ServiceUnavailable));
        var sut = CreateSut(handler, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 5000), out _);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        // Act
        var act = () => sut.ListFileVersionsAsync(UserAuthorization(), BucketId, null, null, 1000, cts.Token);

        // Assert — OperationCanceledException propagates well before the full ~5s delay would have
        // elapsed, and no second attempt is made.
        await act.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "cancellation during the retry delay must abort it immediately, not wait it out");
        handler.Requests.Should().HaveCount(1, "cancellation during the retry delay must pre-empt any further attempt");
    }

    // ---- SC-4: no key/token leakage in retry log lines -----------------------------------------

    [Fact]
    public async Task Test_SC4_NoKeyOrTokenLeakageInRetryLogLines()
    {
        // Arrange — exercises both the system-key path (GetBucketIdAsync, which calls
        // b2_authorize_account then b2_list_buckets) and the per-user-token path
        // (DeleteFileVersionAsync, with one retried transient failure), so a leak of either
        // credential would be caught by a single test.
        var deleteCallCount = 0;
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("b2_authorize_account", StringComparison.Ordinal))
            {
                return AuthorizeAccountResponse();
            }
            if (path.EndsWith("b2_list_buckets", StringComparison.Ordinal))
            {
                return ListBucketsResponse(BucketName, BucketId);
            }
            if (path.EndsWith("b2_delete_file_version", StringComparison.Ordinal))
            {
                deleteCallCount++;
                return deleteCallCount == 1
                    ? BuildErrorResponse(HttpStatusCode.ServiceUnavailable)
                    : JsonResponse(HttpStatusCode.OK, new { });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var sut = CreateSut(handler, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), out var logger);

        // Act
        await sut.GetBucketIdAsync(CancellationToken.None);
        await sut.DeleteFileVersionAsync(UserAuthorization(), "contracts/2019/agreement-004.pdf", "4_z_v1", CancellationToken.None);

        // Assert
        var loggedMessages = GetFormattedLogMessages(logger);
        loggedMessages.Should().Contain(m => m.Contains("retrying in"), "a retry warning should have been logged for the delete's attempt-1 failure");
        loggedMessages.Should().OnlyContain(m =>
            !m.Contains(SystemKeyId) && !m.Contains(SystemApplicationKey) &&
            !m.Contains(SystemAuthToken) && !m.Contains(UserAuthToken),
            "no retry or informational log line may contain the system key, the system authorization token, or the acting user's authorization token, even partially");
    }

    // ---- SC-5: existing DeleteFailedB2 contract only reached after exhaustion (or a real 404) ----

    [Fact]
    public async Task Test_SC5_InterfaceExceptionContractUnchangedOnExhaustionOrNonRetryable()
    {
        // Proves QuarantinePermanentDeleteService's per-file DeleteFailedB2 path (reached only via
        // a thrown exception from DeleteFileVersionAsync, CAS-008 §7 V5/§10 SC-5) is reached only
        // when retry is exhausted or a non-retryable failure occurs — never by a single transient
        // blip that resolves within the retry budget (mirrors B2ClientTests' equivalent SC-5 test).

        // Scenario A — a single transient blip, resolved by retry: must NOT throw.
        var resolvedHandler = CreateQueuedHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => JsonResponse(HttpStatusCode.OK, new { }));
        var resolvedSut = CreateSut(resolvedHandler, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), out _);

        var resolvedAct = () => resolvedSut.DeleteFileVersionAsync(UserAuthorization(), "contracts/2019/agreement-004.pdf", "4_z_v1", CancellationToken.None);
        await resolvedAct.Should().NotThrowAsync("a single transient blip that a retry resolves must never reach the existing DeleteFailedB2 contract");
        resolvedHandler.Requests.Should().HaveCount(2);

        // Scenario B — a real 404 (not a transient status) surfaces immediately, on the first
        // attempt, exactly as it does today.
        var realMissHandler = CreateQueuedHandler(
            () => JsonResponse(HttpStatusCode.NotFound, new { status = 404, code = "file_not_present", message = "not found" }));
        var realMissSut = CreateSut(realMissHandler, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), out _);

        var realMissAct = () => realMissSut.DeleteFileVersionAsync(UserAuthorization(), "contracts/2019/agreement-004.pdf", "4_z_v2", CancellationToken.None);
        await realMissAct.Should().ThrowAsync<B2NotFoundException>();
        realMissHandler.Requests.Should().HaveCount(1, "a real 404 must surface on the first attempt, never retried");

        // Scenario C — transient failures exhaust every allowed attempt: only THEN does the
        // existing DeleteFailedB2 contract (a thrown B2ApiException) get reached.
        var exhaustedHandler = CreateQueuedHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable));
        var exhaustedSut = CreateSut(exhaustedHandler, CreateRetryConfiguration(maxRetryAttempts: 2, retryBaseDelayMs: 1), out _);

        var exhaustedAct = () => exhaustedSut.DeleteFileVersionAsync(UserAuthorization(), "contracts/2019/agreement-004.pdf", "4_z_v3", CancellationToken.None);
        var exhaustedThrown = await exhaustedAct.Should().ThrowAsync<B2ApiException>();
        exhaustedThrown.Which.Should().NotBeOfType<B2NotFoundException>();
        exhaustedHandler.Requests.Should().HaveCount(2, "the failure contract must be reached only after every allowed attempt was used");
    }
}
