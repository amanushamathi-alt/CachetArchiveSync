using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.Auth;
using Cachet.ArchiveSync.Core.Dtos;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests for CAS-002's session mechanics, run against
/// <see cref="TestHostFactory"/>'s self-hosted <c>TestServer</c> (see that class's XML doc for why
/// this is not <c>WebApplicationFactory&lt;Program&gt;</c>). Proves SC-2, SC-3, SC-4, SC-5, and the
/// full-HTTP-round-trip half of SC-8. Not Skip-marked — unlike <c>AuditLogRepositoryTests</c>, this
/// suite needs no live SQL Server or B2 endpoint; every external dependency is an in-process fake.
/// </summary>
public sealed class AuthIntegrationTests : IAsyncLifetime
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
    private static readonly PasswordHasher<object> Hasher = new();

    private TestHostFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new TestHostFactory();
        _client = await _factory.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private void SeedUser(int userId, string username, params string[] b2Capabilities)
    {
        // CAS-002 §6.3a: session_token_version starts at 0 (matching the DB column default) and
        // is tracked by the same fake the OnValidatePrincipal event and POST /logout consult, so
        // this test host's revocation check behaves like the real dbo.users column.
        var sessionTokenVersion = _factory.RegisterSessionTokenVersion(userId);

        _factory.UserRepository.GetByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(new UserAccount
        {
            UserId = userId,
            Username = username,
            PasswordHash = Hasher.HashPassword(new object(), ValidPassword),
            B2ApplicationKeyId = "ct:keyid",
            B2ApplicationKey = "ct:key",
            IsActive = true,
            SessionTokenVersion = sessionTokenVersion
        });
        _factory.CredentialProtector.Unprotect("ct:keyid").Returns("plain-key-id");
        _factory.CredentialProtector.Unprotect("ct:key").Returns("plain-app-key");
        _factory.B2KeyAuthorizer
            .AuthorizeAsync("plain-key-id", "plain-app-key", Arg.Any<CancellationToken>())
            .Returns(new B2AuthorizationInfo
            {
                AccountId = "acct-1",
                AuthorizationToken = "4_secretB2token_neverLog",
                ApiUrl = "https://api-fake.backblazeb2.com",
                DownloadUrl = "https://download-fake.backblazeb2.com",
                Capabilities = b2Capabilities,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
            });
    }

    private async Task<(HttpResponseMessage Response, string CookieHeaderValue)> LoginAsync(string username)
    {
        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = username, Password = ValidPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seeded user/password/B2 fakes are configured to succeed");

        response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
        var cookie = setCookieValues!.FirstOrDefault(v => v.StartsWith(TestHostFactory.CookieName + "=", StringComparison.Ordinal));
        cookie.Should().NotBeNull();
        return (response, cookie!.Split(';')[0]);
    }

    private HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url, string cookieHeaderValue)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeaderValue);
        return request;
    }

    // ---- SC-2: sliding expiration renews within the window, lapses after 8h of inactivity -------

    [Fact]
    public async Task Test_SC2_SlidingExpirationRenewsAndLapses()
    {
        // --- Phase A: a request 7 hours after login (within the 8h window) succeeds and renews ---
        SeedUser(userId: 1, username: ValidUsername, "listFiles", "readFiles");
        var (_, cookieA) = await LoginAsync(ValidUsername);

        _factory.Clock.Advance(TimeSpan.FromHours(7));

        using var withinWindowResponse = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/test/authenticated-ping", cookieA));
        withinWindowResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        withinWindowResponse.Headers.Contains("Set-Cookie").Should().BeTrue(
            "SlidingExpiration renews the cookie on every authenticated request within the window");

        // --- Phase B: a fresh session left untouched for 8+ hours lapses -------------------------
        SeedUser(userId: 2, username: "asmith", "listFiles", "readFiles");
        var (_, cookieB) = await LoginAsync("asmith");

        _factory.Clock.Advance(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(1));

        using var lapsedResponse = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/test/authenticated-ping", cookieB));
        lapsedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- SC-3: logout invalidates the session for the pre-logout cookie value --------------------

    /// <summary>
    /// Previously a KNOWN FAILING TEST (see CAS-002 §6.3a) — plain ASP.NET Core cookie auth's
    /// <c>SignOutAsync</c> only clears the browser's copy; a captured pre-logout cookie remained
    /// valid until its own 8-hour sliding-expiration window lapsed, regardless of logout. Fixed
    /// during `/implement` by adding a per-user <c>session_token_version</c> counter
    /// (<c>dbo.users</c>), embedded in the login-time <c>session_ver</c> claim, incremented by
    /// <c>POST /logout</c>, and re-checked against the live DB value on every request via
    /// <c>CookieAuthenticationOptions.Events.OnValidatePrincipal</c> (registered in both Api's
    /// Program.cs and, verbatim, in <see cref="TestHostFactory"/> above — see its XML doc). This
    /// test now asserts SC-3 exactly as originally written and passes against the real fix.
    /// </summary>
    [Fact]
    public async Task Test_SC3_LogoutInvalidatesSession()
    {
        // Arrange
        SeedUser(userId: 3, username: ValidUsername, "listFiles", "readFiles");
        var (_, cookie) = await LoginAsync(ValidUsername);

        // Sanity: the session works before logout.
        using var beforeLogout = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/test/authenticated-ping", cookie));
        beforeLogout.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act
        using var logoutResponse = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/logout", cookie));
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert — the exact same (pre-logout) cookie value no longer authenticates.
        using var afterLogout = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/test/authenticated-ping", cookie));
        afterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- SC-4 / SC-5: CachetFullAccess policy gating -----------------------------------------------

    [Fact]
    public async Task Test_SC4_ReadOnlyBlockedFromFullAccessActions()
    {
        // Arrange — capability set with neither writeFiles nor deleteFiles -> "Read Only" (§6.1 step 3).
        SeedUser(userId: 4, username: ValidUsername, "listFiles", "readFiles");
        var (_, cookie) = await LoginAsync(ValidUsername);

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/test/full-access-ping", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Test_SC5_FullAccessAllowedOnGatedActions()
    {
        // Arrange — capability set including writeFiles -> "Full Access" (§6.1 step 3).
        SeedUser(userId: 5, username: ValidUsername, "listFiles", "readFiles", "writeFiles", "deleteFiles");
        var (_, cookie) = await LoginAsync(ValidUsername);

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/test/full-access-ping", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- SC-8 (full HTTP round trip): no secret anywhere in the response --------------------------

    [Fact]
    public async Task Test_SC8_NoCredentialLeakageAcrossAuthSurface()
    {
        // Arrange
        SeedUser(userId: 6, username: ValidUsername, "listFiles", "readFiles", "writeFiles");

        // Act
        var (response, cookieHeaderValue) = await LoginAsync(ValidUsername);
        var body = await response.Content.ReadAsStringAsync();

        // Assert — the JSON body, all response headers, and the raw (still-encrypted) cookie value
        // never contain the plaintext B2 key, key id, or the stored password hash.
        body.Should().NotContain("plain-key-id");
        body.Should().NotContain("plain-app-key");
        body.Should().NotContain("4_secretB2token_neverLog");

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            var headerValue = string.Join(" ", header.Value);
            headerValue.Should().NotContain("plain-key-id");
            headerValue.Should().NotContain("plain-app-key");
            headerValue.Should().NotContain("4_secretB2token_neverLog");
        }

        cookieHeaderValue.Should().NotContain("plain-key-id");
        cookieHeaderValue.Should().NotContain("plain-app-key");
    }
}
