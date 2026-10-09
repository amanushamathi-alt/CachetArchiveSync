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
/// Unit tests for <see cref="B2Client"/>. Covers two spec generations against the same class:
///
/// CAS-001 §6.1 (Client Authentication and Credential Brokering):
/// - V-1 (§7): the operational client can only ever be constructed from server-held
///   <see cref="IConfiguration"/> values (B2:SystemKeyId / B2:SystemApplicationKey /
///   B2:BucketName) — there is no constructor path for a per-request/browser-supplied key.
/// - SC-5 (§10): a live B2 credential never leaks — neither via a DTO's <c>ToString()</c>
///   (which a logger or exception message could invoke) nor via an explicit <c>ILogger</c> call
///   in <see cref="B2Client"/> itself.
///
/// CAS-034 (bounded retry/backoff for transient B2 API failures, §9/§10): the tests below the
/// CAS-001 section prove V1-V4 and SC-1 through SC-5 against the real
/// <see cref="B2Client"/>-private <c>SendWithRetryAsync</c> helper and its call sites, using
/// <see cref="B2Client.AuthorizeAsync"/> as the representative retry-eligible operation for the
/// generic retry/backoff/cancellation/logging behavior (it is the only one of the six that needs
/// no separate bucket-id/auth priming call, keeping each test's scripted response sequence exactly
/// aligned with the attempts <c>SendWithRetryAsync</c> actually makes), plus
/// <see cref="B2Client.DeleteFileVersionAsync"/> for SC-5's "existing downstream failure contract"
/// proof and <see cref="B2Client.UploadFileAsync"/> for V3's explicit-exclusion proof. SC-6 is the
/// three pre-existing CAS-001 tests directly above continuing to pass unmodified — see the
/// coverage note at the bottom of this file for what remains out of scope.
/// </summary>
public sealed class B2ClientTests
{
    private const string ValidKeyId = "0123456789ab0000000000001";
    private const string ValidAppKey = "K001SuperSecretApplicationKeyValue";
    private const string ValidBucketName = "cachet-archive-mirror";

    // ---- test doubles -----------------------------------------------------------------------

    private static IConfiguration CreateConfiguration(string? keyId, string? applicationKey, string? bucketName)
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration["B2:SystemKeyId"].Returns(keyId);
        configuration["B2:SystemApplicationKey"].Returns(applicationKey);
        configuration["B2:BucketName"].Returns(bucketName);
        return configuration;
    }

    private static IConfiguration CreateValidConfiguration() =>
        CreateConfiguration(ValidKeyId, ValidAppKey, ValidBucketName);

    /// <summary>
    /// Minimal stub <see cref="HttpMessageHandler"/> that answers every request with a fixed
    /// b2_authorize_account-shaped 200 response, so <see cref="B2Client.AuthorizeAsync"/> can be
    /// exercised without a live B2 endpoint (this project's IB2Client has no other seam for
    /// injecting a fake transport, so this is the smallest double that gets SC-5's log-capture
    /// assertion to a real, non-guessed code path).
    /// </summary>
    private sealed class StubAuthorizeHandler : HttpMessageHandler
    {
        private readonly string _authorizationToken;

        public StubAuthorizeHandler(string authorizationToken) => _authorizationToken = authorizationToken;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                accountId = "acct-123",
                authorizationToken = _authorizationToken,
                apiUrl = "https://api-fake.backblazeb2.com",
                downloadUrl = "https://download-fake.backblazeb2.com",
                allowed = new { capabilities = new[] { "listFiles", "readFiles" } }
            });

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Extracts the formatted message from every call NSubstitute recorded against an
    /// <c>ILogger&lt;T&gt;</c> mock. <c>ILogger.LogInformation</c>/<c>LogWarning</c> are extension
    /// methods that ultimately call the interface's <c>Log&lt;TState&gt;</c> method, whose
    /// arguments are (LogLevel, EventId, TState state, Exception?, Func&lt;TState, Exception?, string&gt; formatter).
    /// </summary>
    private static List<string> GetFormattedLogMessages<T>(ILogger<T> logger)
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

    // ---- V-1: operational client can only be constructed from server config -----------------

    [Theory]
    [InlineData(null, ValidAppKey, ValidBucketName)]
    [InlineData("", ValidAppKey, ValidBucketName)]
    [InlineData("   ", ValidAppKey, ValidBucketName)]
    [InlineData(ValidKeyId, null, ValidBucketName)]
    [InlineData(ValidKeyId, "", ValidBucketName)]
    [InlineData(ValidKeyId, ValidAppKey, null)]
    [InlineData(ValidKeyId, ValidAppKey, "")]
    public void Test_V1_B2ClientRejectsNonServerKey(string? keyId, string? applicationKey, string? bucketName)
    {
        // Arrange — configuration is missing (or blank) one of the three server-held B2 keys.
        // B2Client's constructor is the only entry point into this class; there is no overload or
        // parameter that accepts a per-request/browser-supplied key, so "missing server config"
        // is the only way to exercise the rejection this test proves.
        var configuration = CreateConfiguration(keyId, applicationKey, bucketName);
        using var httpClient = new HttpClient();
        var logger = Substitute.For<ILogger<B2Client>>();

        // Act
        var act = () => new B2Client(httpClient, configuration, logger);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be set*");
    }

    [Fact]
    public void Test_V1_B2ClientConstructsWhenAllServerConfigPresent()
    {
        // Arrange — all three server-held B2 keys are present, as they would be from
        // appsettings/environment/secret store, never from a request or a browser.
        var configuration = CreateValidConfiguration();
        using var httpClient = new HttpClient();
        var logger = Substitute.For<ILogger<B2Client>>();

        // Act
        var act = () => new B2Client(httpClient, configuration, logger);

        // Assert
        act.Should().NotThrow();
    }

    // ---- SC-5: no key/token leakage -----------------------------------------------------------

    [Fact]
    public async Task Test_SC5_NoKeyLeakage()
    {
        // --- Part 1: DTOs holding a live token redact it from ToString() -----------------------
        // A logger or exception message that is ever handed one of these DTOs directly (rather
        // than a specific property) must not be able to print the raw credential.
        const string rawAuthToken = "4_00abcSECRETAUTHTOKENvalue0000000000000000000000000000000000000";
        var authInfo = new B2AuthorizationInfo
        {
            AccountId = "acct-123",
            AuthorizationToken = rawAuthToken,
            ApiUrl = "https://api-fake.backblazeb2.com",
            DownloadUrl = "https://download-fake.backblazeb2.com",
            Capabilities = new[] { "listFiles", "readFiles" },
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
        };
        authInfo.ToString().Should().NotContain(rawAuthToken);
        authInfo.ToString().Should().Contain("redacted");

        const string rawUploadToken = "5_00defSECRETUPLOADTOKENvalue00000000000000000000000000000000000";
        var uploadUrlInfo = new B2UploadUrlInfo
        {
            BucketId = "bucket-123",
            UploadUrl = "https://pod-fake.backblazeb2.com/upload",
            AuthorizationToken = rawUploadToken
        };
        uploadUrlInfo.ToString().Should().NotContain(rawUploadToken);
        uploadUrlInfo.ToString().Should().Contain("redacted");

        // --- Part 2: B2Client itself never logs a raw token, even on a real call path -----------
        // Arrange — a real b2_authorize_account round trip against a stub transport, so this
        // exercises B2Client's own _logger.LogInformation call in AuthorizeCoreAsync rather than
        // asserting against source text.
        var configuration = CreateValidConfiguration();
        using var httpClient = new HttpClient(new StubAuthorizeHandler(rawAuthToken));
        var logger = Substitute.For<ILogger<B2Client>>();
        var client = new B2Client(httpClient, configuration, logger);

        // Act
        var result = await client.AuthorizeAsync(CancellationToken.None);

        // Assert — the call succeeded (proving the log call in AuthorizeCoreAsync actually ran)
        // and the raw token never appears in any formatted log message.
        result.AuthorizationToken.Should().Be(rawAuthToken);
        var loggedMessages = GetFormattedLogMessages(logger);
        loggedMessages.Should().NotBeEmpty("AuthorizeCoreAsync logs an informational message on every successful authorization");
        loggedMessages.Should().OnlyContain(message => !message.Contains(rawAuthToken));
    }

    // =============================================================================================
    // CAS-034 — bounded retry/backoff for transient B2 API failures
    // =============================================================================================

    // ---- test doubles (CAS-034) ---------------------------------------------------------------

    private const string RetryAuthToken = "4_00abcRETRYTESTTOKENvalue0000000000000000000000000000000000000";

    private static IConfiguration CreateRetryConfiguration(int maxRetryAttempts, int retryBaseDelayMs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["B2:SystemKeyId"] = ValidKeyId,
                ["B2:SystemApplicationKey"] = ValidAppKey,
                ["B2:BucketName"] = ValidBucketName,
                ["B2:MaxRetryAttempts"] = maxRetryAttempts.ToString(),
                ["B2:RetryBaseDelayMs"] = retryBaseDelayMs.ToString()
            })
            .Build();

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage BuildAuthorizeSuccessResponse(string token = RetryAuthToken) => JsonResponse(HttpStatusCode.OK, new
    {
        accountId = "acct-123",
        authorizationToken = token,
        apiUrl = "https://api-fake.backblazeb2.com",
        downloadUrl = "https://download-fake.backblazeb2.com",
        allowed = new { capabilities = new[] { "listFiles", "readFiles" } }
    });

    private static HttpResponseMessage BuildErrorResponse(HttpStatusCode status, string code = "error") =>
        JsonResponse(status, new { status = (int)status, code, message = $"{code} for test" });

    private static HttpResponseMessage BuildRetryAfterResponse(TimeSpan retryAfter)
    {
        var response = BuildErrorResponse(HttpStatusCode.TooManyRequests, "too_many_requests");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private static HttpResponseMessage BuildEmptyJsonSuccessResponse() => JsonResponse(HttpStatusCode.OK, new { });

    /// <summary>
    /// Answers every request from an ordered queue of response factories, regardless of URL — used
    /// where the operation under test is itself <c>b2_authorize_account</c>
    /// (<see cref="B2Client.AuthorizeAsync"/>), so there is no separate "always-succeed authorize"
    /// call to special-case: the queue drives every attempt <c>SendWithRetryAsync</c> makes.
    /// </summary>
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;

        public int CallCount { get; private set; }

        public SequencedHandler(params Func<HttpResponseMessage>[] responses) =>
            _responses = new Queue<Func<HttpResponseMessage>>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"SequencedHandler ran out of scripted responses on call #{CallCount}.");
            }

            return Task.FromResult(_responses.Dequeue()());
        }
    }

    /// <summary>
    /// Answers every <c>b2_authorize_account</c> call with a fixed success response (so a wire
    /// operation reached via <c>SendWithReauthAsync</c>, which needs a cached authorization first,
    /// can obtain one transparently) and answers every other call from an ordered queue supplied by
    /// the test — used for SC-5, which exercises <see cref="B2Client.DeleteFileVersionAsync"/>
    /// rather than <c>AuthorizeAsync</c> itself.
    /// </summary>
    private sealed class AuthThenScriptedOperationHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _operationResponses;

        public int OperationCallCount { get; private set; }

        public AuthThenScriptedOperationHandler(params Func<HttpResponseMessage>[] operationResponses) =>
            _operationResponses = new Queue<Func<HttpResponseMessage>>(operationResponses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is not null && request.RequestUri.AbsoluteUri.Contains("b2_authorize_account"))
            {
                return Task.FromResult(BuildAuthorizeSuccessResponse());
            }

            OperationCallCount++;
            if (_operationResponses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"AuthThenScriptedOperationHandler ran out of scripted operation responses on call #{OperationCallCount}.");
            }

            return Task.FromResult(_operationResponses.Dequeue()());
        }
    }

    // ---- SC-1: transient failure then success returns the successful result -------------------

    [Fact]
    public async Task Test_SC1_TransientFailureEventuallySucceeds()
    {
        // Arrange — attempt 1 comes back 503 (retryable), attempt 2 succeeds, well within
        // B2:MaxRetryAttempts (3).
        var handler = new SequencedHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => BuildAuthorizeSuccessResponse());
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.AuthorizeAsync(CancellationToken.None);

        // Assert — the caller sees only the successful result, never the attempt-1 failure.
        result.AuthorizationToken.Should().Be(RetryAuthToken);
        handler.CallCount.Should().Be(2, "one failed attempt and one successful retry should have occurred");
    }

    // ---- SC-2: exhausted retries surface the original failure ---------------------------------

    [Fact]
    public async Task Test_SC2_ExhaustedRetriesSurfacesOriginalFailure()
    {
        // Arrange — every attempt (both allowed by B2:MaxRetryAttempts = 2) comes back 503.
        var handler = new SequencedHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 2, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var act = () => client.AuthorizeAsync(CancellationToken.None);

        // Assert — the same exception type/content attempt 1 would have thrown today (a 503
        // B2ApiException), not a new wrapper type, and no attempt beyond MaxRetryAttempts.
        var thrown = await act.Should().ThrowAsync<B2ApiException>();
        thrown.Which.Should().NotBeOfType<B2NotFoundException>();
        thrown.Which.StatusCode.Should().Be((int)HttpStatusCode.ServiceUnavailable);
        handler.CallCount.Should().Be(2, "no attempt beyond B2:MaxRetryAttempts should have been made");
    }

    // ---- SC-3: caller cancellation during a retry delay is honored immediately ------------------

    [Fact]
    public async Task Test_SC3_CallerCancellationDuringRetryDelayHonoredImmediately()
    {
        // Arrange — attempt 1 fails transiently, which schedules a ~5s backoff delay
        // (RetryBaseDelayMs = 5000); the caller cancels 50ms into that delay. MaxRetryAttempts = 3
        // means attempt 1 < maxAttempts, so the code path under test is genuinely "cancelled while
        // waiting to retry", not "cancelled because retries were already exhausted".
        var handler = new SequencedHandler(() => BuildErrorResponse(HttpStatusCode.ServiceUnavailable));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 5000), Substitute.For<ILogger<B2Client>>());
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        // Act
        var act = () => client.AuthorizeAsync(cts.Token);

        // Assert — OperationCanceledException propagates well before the full ~5s delay would have
        // elapsed, and no second attempt is made.
        await act.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "cancellation should abort the delay immediately, not wait it out");
        handler.CallCount.Should().Be(1, "cancellation during the retry delay must pre-empt any further attempt");
    }

    // ---- SC-4: no key/token leakage in retry log lines -----------------------------------------

    [Fact]
    public async Task Test_SC4_NoKeyOrTokenLeakageInRetryLogLines()
    {
        // Arrange — force one retry warning log line (attempt 1 transient, attempt 2 succeeds and
        // logs the informational "authorization refreshed" message too), so both log call sites
        // exercised by this class are captured.
        var handler = new SequencedHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => BuildAuthorizeSuccessResponse());
        using var httpClient = new HttpClient(handler);
        var logger = Substitute.For<ILogger<B2Client>>();
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), logger);

        // Act
        await client.AuthorizeAsync(CancellationToken.None);

        // Assert
        var loggedMessages = GetFormattedLogMessages(logger);
        loggedMessages.Should().Contain(m => m.Contains("retrying in"), "a retry warning should have been logged for the attempt-1 failure");
        loggedMessages.Should().OnlyContain(m =>
            !m.Contains(RetryAuthToken) && !m.Contains(ValidAppKey) && !m.Contains(ValidKeyId),
            "no retry or authorization log line may contain the B2 key or an authorization token, even partially");
    }

    // ---- SC-5: existing failure contract only reached after exhaustion (or a real 404) ---------

    [Fact]
    public async Task Test_SC5_ExistingFailureContractOnlyReachedAfterExhaustion()
    {
        // Scenario A — a single transient blip on a delete, resolved by retry: the existing
        // "DeleteFailed-B2" failure contract (§6.4, keyed off a thrown exception from
        // DeleteFileVersionAsync) must NOT be reached — the call must complete successfully.
        var resolvedHandler = new AuthThenScriptedOperationHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => BuildEmptyJsonSuccessResponse());
        using var resolvedHttpClient = new HttpClient(resolvedHandler);
        var resolvedClient = new B2Client(resolvedHttpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        var resolvedAct = () => resolvedClient.DeleteFileVersionAsync("file.txt", "file-id-1", CancellationToken.None);
        await resolvedAct.Should().NotThrowAsync("a single transient blip that a retry resolves must never reach the existing failure contract");
        resolvedHandler.OperationCallCount.Should().Be(2);

        // Scenario B — a real 404 (not a transient status) surfaces immediately, on the first
        // attempt, exactly as it does today — the "excluded-on-miss"/DeleteFailed-B2 contract is
        // reached, but only because the file is genuinely gone, not because retries ran out.
        var realMissHandler = new AuthThenScriptedOperationHandler(
            () => BuildErrorResponse(HttpStatusCode.NotFound, "file_not_present"));
        using var realMissHttpClient = new HttpClient(realMissHandler);
        var realMissClient = new B2Client(realMissHttpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        var realMissAct = () => realMissClient.DeleteFileVersionAsync("file.txt", "file-id-2", CancellationToken.None);
        await realMissAct.Should().ThrowAsync<B2NotFoundException>();
        realMissHandler.OperationCallCount.Should().Be(1, "a real 404 must surface on the first attempt, never retried");

        // Scenario C — transient failures exhaust every allowed attempt: only THEN does the
        // existing failure contract (a thrown B2ApiException, which callers such as
        // QuarantinePermanentDeleteService's DeleteFailed-B2 path and the Worker's mirror-sync
        // task-failure path key off) get reached.
        var exhaustedHandler = new AuthThenScriptedOperationHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable));
        using var exhaustedHttpClient = new HttpClient(exhaustedHandler);
        var exhaustedClient = new B2Client(exhaustedHttpClient, CreateRetryConfiguration(maxRetryAttempts: 2, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        var exhaustedAct = () => exhaustedClient.DeleteFileVersionAsync("file.txt", "file-id-3", CancellationToken.None);
        var exhaustedThrown = await exhaustedAct.Should().ThrowAsync<B2ApiException>();
        exhaustedThrown.Which.Should().NotBeOfType<B2NotFoundException>();
        exhaustedHandler.OperationCallCount.Should().Be(2, "the failure contract must be reached only after every allowed attempt was used");
    }

    // ---- V1: retried up to B2:MaxRetryAttempts with exponential backoff ------------------------

    [Fact]
    public async Task Test_V1_TransientFailureRetriedUpToMaxAttempts()
    {
        // Arrange — attempts 1 and 2 fail transiently, attempt 3 (the last one B2:MaxRetryAttempts
        // allows) succeeds — proves retry happens up to, and including, the configured bound.
        var handler = new SequencedHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => BuildErrorResponse(HttpStatusCode.InternalServerError),
            () => BuildAuthorizeSuccessResponse());
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.AuthorizeAsync(CancellationToken.None);

        // Assert
        result.AuthorizationToken.Should().Be(RetryAuthToken);
        handler.CallCount.Should().Be(3, "both retryable failures should have been retried up to the third and final allowed attempt");
    }

    // ---- V1 (HttpRequestException branch): a transient network failure is retried the same way
    // a retryable HTTP status is (CAS-036 review §1 SC-1 note — this branch was previously untested,
    // and is shared/mirrored by UserScopedB2DeleteClient's own retry wrapper).

    [Fact]
    public async Task Test_V1_HttpRequestExceptionTransientFailureRetriedThenSucceeds()
    {
        // Arrange — attempt 1 throws HttpRequestException (simulating a connection-level transient
        // failure, e.g. a reset connection), attempt 2 succeeds.
        var handler = new SequencedHandler(
            () => throw new HttpRequestException("Simulated transient network failure"),
            () => BuildAuthorizeSuccessResponse());
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.AuthorizeAsync(CancellationToken.None);

        // Assert
        result.AuthorizationToken.Should().Be(RetryAuthToken);
        handler.CallCount.Should().Be(2, "the HttpRequestException on attempt 1 should have been retried, not surfaced");
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
        var handler = new SequencedHandler(() => BuildErrorResponse(statusCode ?? HttpStatusCode.BadRequest));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());
        using var cts = new CancellationTokenSource();
        if (preCancelled)
        {
            cts.Cancel();
        }

        // Act
        var act = () => client.AuthorizeAsync(cts.Token);

        // Assert — never retried, regardless of which non-retryable condition applies.
        if (preCancelled)
        {
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        else if (statusCode == HttpStatusCode.NotFound)
        {
            await act.Should().ThrowAsync<B2NotFoundException>();
            handler.CallCount.Should().Be(1, "a non-retryable 404 must surface on the first attempt");
        }
        else
        {
            var thrown = await act.Should().ThrowAsync<B2ApiException>();
            thrown.Which.StatusCode.Should().Be((int)statusCode!.Value);
            handler.CallCount.Should().Be(1, "a non-retryable 4xx must surface on the first attempt");
        }
    }

    // ---- V3: UploadFileAsync is excluded from the retry wrapper --------------------------------

    [Fact]
    public async Task Test_V3_UploadFileAsyncNotWrappedInRetry()
    {
        // Arrange — a transient status that WOULD be retried for any of the six wrapped
        // operations; UploadFileAsync must still fail on the very first attempt.
        var handler = new SequencedHandler(() => BuildErrorResponse(HttpStatusCode.ServiceUnavailable));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());
        var uploadUrlInfo = new B2UploadUrlInfo
        {
            BucketId = "bucket-123",
            UploadUrl = "https://pod-fake.backblazeb2.com/upload",
            AuthorizationToken = "upload-token-for-test"
        };
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("test file content"));

        // Act
        var act = () => client.UploadFileAsync(uploadUrlInfo, "file.txt", content, "fake-sha1", content.Length, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<B2ApiException>();
        handler.CallCount.Should().Be(1, "UploadFileAsync must never be retried inside B2Client — a failed upload is the caller's responsibility to redo against a fresh upload URL");
    }

    // ---- V4: a 429 Retry-After header is honored over the computed backoff ----------------------

    [Fact]
    public async Task Test_V4_RetryAfterHeaderHonoredOverComputedBackoff()
    {
        // Arrange — RetryBaseDelayMs is tiny (1ms, so the computed exponential delay is at most a
        // couple of milliseconds plus up to 100ms of jitter), but the 429 response's Retry-After
        // asks for 300ms — comfortably larger than the computed ceiling this configuration can
        // produce, so honoring Retry-After is the only way the observed delay could reach ~300ms.
        var retryAfter = TimeSpan.FromMilliseconds(300);
        var handler = new SequencedHandler(
            () => BuildRetryAfterResponse(retryAfter),
            () => BuildAuthorizeSuccessResponse());
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());
        var stopwatch = Stopwatch.StartNew();

        // Act
        var result = await client.AuthorizeAsync(CancellationToken.None);
        stopwatch.Stop();

        // Assert
        result.AuthorizationToken.Should().Be(RetryAuthToken);
        handler.CallCount.Should().Be(2);
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250), "the Retry-After value (300ms), not the ~1-101ms computed backoff, should have been honored");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "Retry-After should not be inflated all the way to the 8s ceiling for a 300ms request");
    }

    // =============================================================================================
    // CAS-034 — coverage top-up: the five previously-uncovered IB2Client operations
    // (ListFileNamesAsync, ListFileVersionsAsync, GetFileInfoAsync, DownloadFileByIdAsync,
    // GetUploadUrlAsync). Each of these already routes through the shared SendWithRetryAsync
    // helper proven generically above via AuthorizeAsync/DeleteFileVersionAsync — these tests focus
    // on each operation's OWN branching/mapping logic (per Gate 4 coverage guidance), with a
    // representative retry-path test for three of the five (the other two — ListFileVersionsAsync,
    // DownloadFileByIdAsync — get fuller own-logic coverage instead: NextFileId mapping and
    // header/stream-disposal behavior, respectively).
    // =============================================================================================

    // ---- test doubles (coverage top-up) --------------------------------------------------------

    /// <summary>
    /// Like <see cref="AuthThenScriptedOperationHandler"/>, but also auto-answers
    /// <c>b2_list_buckets</c> with a single bucket matching <see cref="ValidBucketName"/> — used
    /// for the three operations that resolve a bucket id via <c>GetBucketIdAsync</c> before their
    /// own wire call: <see cref="B2Client.ListFileNamesAsync"/>, <see cref="B2Client.ListFileVersionsAsync"/>,
    /// and <see cref="B2Client.GetUploadUrlAsync"/>.
    /// </summary>
    private sealed class AuthAndBucketThenScriptedOperationHandler : HttpMessageHandler
    {
        private const string StubBucketId = "bucket-abc";
        private readonly Queue<Func<HttpResponseMessage>> _operationResponses;

        public int OperationCallCount { get; private set; }

        public AuthAndBucketThenScriptedOperationHandler(params Func<HttpResponseMessage>[] operationResponses) =>
            _operationResponses = new Queue<Func<HttpResponseMessage>>(operationResponses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (url.Contains("b2_authorize_account"))
            {
                return Task.FromResult(BuildAuthorizeSuccessResponse());
            }

            if (url.Contains("b2_list_buckets"))
            {
                return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
                {
                    buckets = new[] { new { bucketId = StubBucketId, bucketName = ValidBucketName } }
                }));
            }

            OperationCallCount++;
            if (_operationResponses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"AuthAndBucketThenScriptedOperationHandler ran out of scripted operation responses on call #{OperationCallCount}.");
            }

            return Task.FromResult(_operationResponses.Dequeue()());
        }
    }

    /// <summary>
    /// A <see cref="ByteArrayContent"/> that records whether it was disposed — used to prove
    /// <see cref="B2Client.ResponseOwningStream"/> (private, exercised only indirectly via
    /// <see cref="B2Client.DownloadFileByIdAsync"/>) disposes its owning <see cref="HttpResponseMessage"/>
    /// (and therefore this content) when the caller disposes the returned stream, not before.
    /// </summary>
    private sealed class DisposeTrackingContent : ByteArrayContent
    {
        public DisposeTrackingContent(byte[] content) : base(content)
        {
        }

        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WasDisposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private static HttpResponseMessage BuildDownloadResponse(string? fileNameHeaderValue, string? contentSha1HeaderValue, byte[] content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        if (fileNameHeaderValue is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Bz-File-Name", fileNameHeaderValue);
        }

        if (contentSha1HeaderValue is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", contentSha1HeaderValue);
        }

        response.Content.Headers.ContentLength = content.Length;
        return response;
    }

    // ---- ListFileNamesAsync ---------------------------------------------------------------------

    [Fact]
    public async Task Test_ListFileNamesAsync_HappyPath_ReturnsFilesAndNextFileName()
    {
        // Arrange
        var handler = new AuthAndBucketThenScriptedOperationHandler(
            () => JsonResponse(HttpStatusCode.OK, new
            {
                files = new[]
                {
                    new { fileId = "file-1", fileName = "docs/a.txt", contentSha1 = "sha-a", contentLength = 100L, action = "upload", uploadTimestamp = 1000L },
                    new { fileId = "file-2", fileName = "docs/b.txt", contentSha1 = "sha-b", contentLength = 200L, action = "upload", uploadTimestamp = 2000L }
                },
                nextFileName = "docs/c.txt"
            }));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.ListFileNamesAsync(startFileName: null, maxFileCount: 100, CancellationToken.None);

        // Assert
        result.Files.Should().HaveCount(2);
        result.Files[0].FileId.Should().Be("file-1");
        result.Files[0].FileName.Should().Be("docs/a.txt");
        result.Files[0].ContentSha1.Should().Be("sha-a");
        result.Files[0].ContentLength.Should().Be(100);
        result.NextFileName.Should().Be("docs/c.txt");
        handler.OperationCallCount.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Test_ListFileNamesAsync_MaxFileCountOutOfRange_ThrowsArgumentOutOfRangeException(int maxFileCount)
    {
        // Arrange — B2 caps b2_list_file_names at 1000 files per call (§6.2 step 2); the guard
        // must fire before any wire call is made, so an empty scripted queue is sufficient.
        using var httpClient = new HttpClient(new AuthAndBucketThenScriptedOperationHandler());
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var act = () => client.ListFileNamesAsync(null, maxFileCount, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Test_ListFileNamesAsync_TransientFailureThenSuccess_Retries()
    {
        // Arrange — the b2_list_file_names call itself (not the bucket lookup) fails transiently
        // once, then succeeds, proving this call site is wrapped in SendWithRetryAsync too.
        var handler = new AuthAndBucketThenScriptedOperationHandler(
            () => BuildErrorResponse(HttpStatusCode.ServiceUnavailable),
            () => JsonResponse(HttpStatusCode.OK, new { files = Array.Empty<object>(), nextFileName = (string?)null }));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.ListFileNamesAsync(null, 100, CancellationToken.None);

        // Assert
        result.Files.Should().BeEmpty();
        handler.OperationCallCount.Should().Be(2, "the first transient 503 on b2_list_file_names should have been retried");
    }

    // ---- ListFileVersionsAsync -------------------------------------------------------------------

    [Fact]
    public async Task Test_ListFileVersionsAsync_HappyPath_ReturnsFilesNextFileNameAndNextFileId()
    {
        // Arrange — includes a "hide" action row to prove Action is mapped through as-is, not just
        // the common "upload" case.
        var handler = new AuthAndBucketThenScriptedOperationHandler(
            () => JsonResponse(HttpStatusCode.OK, new
            {
                files = new[]
                {
                    new { fileId = "file-1", fileName = "docs/a.txt", contentSha1 = (string?)"sha-a", contentLength = 100L, action = "upload", uploadTimestamp = 1000L },
                    new { fileId = "file-1-hide", fileName = "docs/a.txt", contentSha1 = (string?)null, contentLength = 0L, action = "hide", uploadTimestamp = 1500L }
                },
                nextFileName = "docs/a.txt",
                nextFileId = "file-1-next"
            }));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.ListFileVersionsAsync(startFileName: null, prefix: "docs/", maxFileCount: 100, CancellationToken.None);

        // Assert
        result.Files.Should().HaveCount(2);
        result.Files[1].Action.Should().Be("hide");
        result.NextFileName.Should().Be("docs/a.txt");
        result.NextFileId.Should().Be("file-1-next");
        handler.OperationCallCount.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Test_ListFileVersionsAsync_MaxFileCountOutOfRange_ThrowsArgumentOutOfRangeException(int maxFileCount)
    {
        // Arrange
        using var httpClient = new HttpClient(new AuthAndBucketThenScriptedOperationHandler());
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var act = () => client.ListFileVersionsAsync(null, null, maxFileCount, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ---- GetFileInfoAsync ------------------------------------------------------------------------

    [Fact]
    public async Task Test_GetFileInfoAsync_HappyPath_ReturnsMappedFileInfo()
    {
        // Arrange — b2_get_file_info returns "size", not "contentLength" (B2WireFile's comment);
        // this response deliberately omits contentLength to prove MapFileInfo's
        // `ContentLength = file.ContentLength ?? file.Size ?? 0` fallback reaches the Size branch.
        var handler = new AuthThenScriptedOperationHandler(
            () => JsonResponse(HttpStatusCode.OK, new
            {
                fileId = "file-42",
                fileName = "reports/q1.pdf",
                contentSha1 = "sha-42",
                size = 4096L,
                action = "upload",
                uploadTimestamp = 9999L
            }));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.GetFileInfoAsync("file-42", CancellationToken.None);

        // Assert
        result.FileId.Should().Be("file-42");
        result.FileName.Should().Be("reports/q1.pdf");
        result.ContentSha1.Should().Be("sha-42");
        result.ContentLength.Should().Be(4096, "size must be used as the fallback when contentLength is absent");
        handler.OperationCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Test_GetFileInfoAsync_NotFound_ThrowsB2NotFoundException()
    {
        // Arrange
        var handler = new AuthThenScriptedOperationHandler(
            () => BuildErrorResponse(HttpStatusCode.NotFound, "file_not_present"));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var act = () => client.GetFileInfoAsync("missing-file", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<B2NotFoundException>();
        handler.OperationCallCount.Should().Be(1, "a real 404 must surface on the first attempt, never retried");
    }

    [Fact]
    public async Task Test_GetFileInfoAsync_TransientFailureThenSuccess_Retries()
    {
        // Arrange
        var handler = new AuthThenScriptedOperationHandler(
            () => BuildErrorResponse(HttpStatusCode.TooManyRequests),
            () => JsonResponse(HttpStatusCode.OK, new
            {
                fileId = "file-7",
                fileName = "x.txt",
                contentSha1 = "sha-7",
                contentLength = 10L,
                action = "upload",
                uploadTimestamp = 1L
            }));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.GetFileInfoAsync("file-7", CancellationToken.None);

        // Assert
        result.FileId.Should().Be("file-7");
        handler.OperationCallCount.Should().Be(2, "the first transient 429 on b2_get_file_info should have been retried");
    }

    // ---- DownloadFileByIdAsync -------------------------------------------------------------------

    [Fact]
    public async Task Test_DownloadFileByIdAsync_HappyPath_ReturnsMappedFileWithHeaders()
    {
        // Arrange — the file name header is URI-escaped on the wire (matches how a real B2 response
        // encodes a name containing a space); DownloadFileByIdAsync must unescape it.
        var contentBytes = Encoding.UTF8.GetBytes("hello world");
        var handler = new AuthThenScriptedOperationHandler(
            () => BuildDownloadResponse(Uri.EscapeDataString("docs/my file.txt"), "sha-download-1", contentBytes));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        using var result = await client.DownloadFileByIdAsync("file-99", CancellationToken.None);

        // Assert
        result.FileName.Should().Be("docs/my file.txt");
        result.ContentSha1.Should().Be("sha-download-1");
        result.ContentLength.Should().Be(contentBytes.Length);
        using var reader = new StreamReader(result.Content);
        var text = await reader.ReadToEndAsync();
        text.Should().Be("hello world");
        handler.OperationCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Test_DownloadFileByIdAsync_MissingFileNameHeader_FallsBackToFileId()
    {
        // Arrange — when B2 omits X-Bz-File-Name (defensive branch), DownloadFileByIdAsync falls
        // back to the requested fileId; a missing X-Bz-Content-Sha1 falls back to string.Empty.
        var contentBytes = Encoding.UTF8.GetBytes("data");
        var handler = new AuthThenScriptedOperationHandler(
            () => BuildDownloadResponse(fileNameHeaderValue: null, contentSha1HeaderValue: null, contentBytes));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        using var result = await client.DownloadFileByIdAsync("file-fallback-id", CancellationToken.None);

        // Assert
        result.FileName.Should().Be("file-fallback-id", "when B2 omits X-Bz-File-Name, DownloadFileByIdAsync falls back to the requested fileId");
        result.ContentSha1.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_DownloadFileByIdAsync_DisposingContentStream_DisposesUnderlyingResponse()
    {
        // Arrange — proves ResponseOwningStream's Dispose() also disposes the owning
        // HttpResponseMessage/content (§2's special-case comment: the network stream/response must
        // stay open until the caller explicitly disposes what DownloadFileByIdAsync returned).
        var trackingContent = new DisposeTrackingContent(Encoding.UTF8.GetBytes("stream-disposal-test"));
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = trackingContent };
        response.Headers.TryAddWithoutValidation("X-Bz-File-Name", "disposal-test.txt");
        response.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", "sha-disposal");
        response.Content.Headers.ContentLength = 21;

        var handler = new AuthThenScriptedOperationHandler(() => response);
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.DownloadFileByIdAsync("file-disposal", CancellationToken.None);
        trackingContent.WasDisposed.Should().BeFalse("the response must stay open until the caller disposes the returned stream");

        result.Dispose();

        // Assert
        trackingContent.WasDisposed.Should().BeTrue("disposing B2DownloadedFile (ResponseOwningStream) must also dispose the owning HttpResponseMessage/content");
    }

    // ---- GetUploadUrlAsync -----------------------------------------------------------------------

    [Fact]
    public async Task Test_GetUploadUrlAsync_HappyPath_ReturnsMappedUploadUrlInfo()
    {
        // Arrange
        var handler = new AuthAndBucketThenScriptedOperationHandler(
            () => JsonResponse(HttpStatusCode.OK, new
            {
                bucketId = "bucket-abc",
                uploadUrl = "https://pod-fake.backblazeb2.com/upload/abc",
                authorizationToken = "upload-auth-token-xyz"
            }));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.GetUploadUrlAsync(CancellationToken.None);

        // Assert
        result.BucketId.Should().Be("bucket-abc");
        result.UploadUrl.Should().Be("https://pod-fake.backblazeb2.com/upload/abc");
        result.AuthorizationToken.Should().Be("upload-auth-token-xyz");
        handler.OperationCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Test_GetUploadUrlAsync_TransientFailureThenSuccess_Retries()
    {
        // Arrange — the b2_get_upload_url call itself (not the bucket lookup) fails transiently
        // once, then succeeds.
        var handler = new AuthAndBucketThenScriptedOperationHandler(
            () => BuildErrorResponse(HttpStatusCode.BadGateway),
            () => JsonResponse(HttpStatusCode.OK, new
            {
                bucketId = "bucket-abc",
                uploadUrl = "https://pod-fake.backblazeb2.com/upload/retry",
                authorizationToken = "upload-auth-token-retry"
            }));
        using var httpClient = new HttpClient(handler);
        var client = new B2Client(httpClient, CreateRetryConfiguration(maxRetryAttempts: 3, retryBaseDelayMs: 1), Substitute.For<ILogger<B2Client>>());

        // Act
        var result = await client.GetUploadUrlAsync(CancellationToken.None);

        // Assert
        result.AuthorizationToken.Should().Be("upload-auth-token-retry");
        handler.OperationCallCount.Should().Be(2, "the first transient 502 on b2_get_upload_url should have been retried");
    }

    // Coverage note (test-writer scope, updated for Gate 4 coverage top-up):
    // All eight IB2Client operations now have direct unit test coverage in this file:
    // AuthorizeAsync/DeleteFileVersionAsync/UploadFileAsync (prior CAS-034 pass, proving the shared
    // SendWithRetryAsync/ComputeDelay/IsRetryableStatusCode/TryGetRetryAfterMs/LogRetryWarning helper
    // generically, plus V3's exclusion), and ListFileNamesAsync/ListFileVersionsAsync/
    // GetFileInfoAsync/DownloadFileByIdAsync/GetUploadUrlAsync (this pass, focused on each
    // operation's own branching/mapping: the maxFileCount>1000 guard and NextFileName/NextFileId
    // pagination fields, GetFileInfoAsync's 404-to-B2NotFoundException mapping and its
    // contentLength/size fallback, DownloadFileByIdAsync's X-Bz-File-Name/X-Bz-Content-Sha1 header
    // extraction with its fileId/empty-string fallbacks plus ResponseOwningStream's dispose-forwards-
    // to-response behavior, and GetUploadUrlAsync's response mapping) — each backed by a
    // representative retry-path test except ListFileVersionsAsync and DownloadFileByIdAsync, whose
    // retry behavior is already proven identically via the shared helper and whose tests here instead
    // spend that coverage budget on NextFileId mapping and header/stream-disposal logic respectively.
    // Still not covered by any unit test in this file: GetBucketIdAsync/b2_list_buckets's own
    // "configured bucket name not found" branch, and the 401-reauthenticate-once path in
    // SendWithReauthAsync — both pre-date CAS-034 (CAS-001 scope) and are unchanged by this spec.
}
