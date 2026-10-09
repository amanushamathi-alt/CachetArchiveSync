using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.Auth;
using Cachet.ArchiveSync.Core.Dtos;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// Supplements <see cref="AuthIntegrationTests"/> (which runs against
/// <see cref="TestHostFactory"/>'s hand-built <c>TestServer</c> — see that class's XML doc) with the
/// same core CAS-002 session scenarios run against the REAL
/// <c>Cachet.ArchiveSync.Api/Program.cs</c> via <see cref="RealProgramWebApplicationFactory"/>, now
/// that <c>public partial class Program {}</c> makes <c>WebApplicationFactory&lt;Program&gt;</c>
/// usable. This closes the line-coverage gap on <c>Program.cs</c>'s actual DI/middleware wiring
/// (cookie scheme, <c>OnValidatePrincipal</c>, authorization policies) that
/// <see cref="TestHostFactory"/>'s copy of that wiring could never exercise. <see cref="TestHostFactory"/>
/// remains the suite of record for the clock-dependent SC-2 sliding-expiration scenario and the
/// SC-4/SC-5 policy-gating scenarios (both need test-only probe endpoints/a controllable clock that
/// the real <c>Program.cs</c> does not — and per this run's scope, should not — expose).
/// </summary>
public sealed class RealProgramAuthIntegrationTests : IAsyncLifetime
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
    private static readonly PasswordHasher<object> Hasher = new();

    private RealProgramWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new RealProgramWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private void SeedUser(int userId, string username, params string[] b2Capabilities)
    {
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

    private async Task<string> LoginAsync(string username)
    {
        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = username, Password = ValidPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seeded user/password/B2 fakes are configured to succeed");

        response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
        var cookie = setCookieValues!.FirstOrDefault(v => v.StartsWith(RealProgramWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));
        cookie.Should().NotBeNull();
        return cookie!.Split(';')[0];
    }

    private HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url, string cookieHeaderValue)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeaderValue);
        return request;
    }

    [Fact]
    public async Task Test_RealProgram_Login_Success_SetsAuthCookieAndReturns200()
    {
        // Arrange
        SeedUser(userId: 201, username: ValidUsername, "listFiles", "readFiles");

        // Act
        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = ValidUsername, Password = ValidPassword });

        // Assert — the real Program.cs cookie-authentication scheme issues the configured cookie
        // name (appsettings.Development.json's Auth:CookieName) on a successful AuthController.Login.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be(201);
        body.B2AccessLabel.Should().Be("Read Only");

        response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
        setCookieValues!.Should().Contain(v => v.StartsWith(RealProgramWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_RealProgram_Login_InvalidCredentials_Returns401_NoCookieSet()
    {
        // Arrange — no seeded user, so LoginService's real GetByUsernameAsync-null branch runs.

        // Act
        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = "nobody", Password = "irrelevant" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Test_RealProgram_Logout_WithoutSession_Returns401()
    {
        // Act — no Cookie header at all; Program.cs's real CachetAuthenticated policy (verbatim
        // §6.4) and OnRedirectToLogin -> 401 override reject this before AuthController.Logout runs.
        var response = await _client.PostAsync("/logout", content: null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Re-proves SC-3 (CAS-002 §6.3a) through the REAL <c>Program.cs</c> pipeline: the same
    /// (pre-logout) session cookie is rejected on its very next authenticated use, via the real
    /// <c>OnValidatePrincipal</c> registered in <c>Program.cs</c> — not <see cref="TestHostFactory"/>'s
    /// copy of it. <c>POST /logout</c> is itself <c>[Authorize(Policy = "CachetAuthenticated")]</c>,
    /// so calling it a second time with the same cookie is a real end-to-end probe: the first call
    /// succeeds and increments <c>session_token_version</c>; the second call's principal is rejected
    /// by the real <c>OnValidatePrincipal</c> event before <c>AuthController.Logout</c> ever runs
    /// again.
    /// </summary>
    [Fact]
    public async Task Test_RealProgram_SC3_LogoutInvalidatesSession()
    {
        // Arrange
        SeedUser(userId: 202, username: ValidUsername, "listFiles", "readFiles");
        var cookie = await LoginAsync(ValidUsername);

        // Act — first logout succeeds and revokes the session server-side (§6.3a).
        using var firstLogout = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/logout", cookie));
        firstLogout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Assert — the exact same (now-revoked) cookie no longer authenticates against the real
        // OnValidatePrincipal check, even though it has not otherwise expired.
        using var secondLogout = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/logout", cookie));
        secondLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Test_RealProgram_SC8_NoCredentialLeakageAcrossAuthSurface()
    {
        // Arrange
        SeedUser(userId: 203, username: ValidUsername, "listFiles", "readFiles", "writeFiles");

        // Act
        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = ValidUsername, Password = ValidPassword });
        var body = await response.Content.ReadAsStringAsync();

        // Assert — same invariant as AuthIntegrationTests.Test_SC8, now proven through the real
        // Program.cs pipeline rather than TestHostFactory's copy of it.
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
    }
}
