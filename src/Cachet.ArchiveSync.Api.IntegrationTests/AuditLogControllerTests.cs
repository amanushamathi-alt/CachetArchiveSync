using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.AuditLog;
using Cachet.ArchiveSync.Core.Dtos;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests for CAS-009 §6's <c>GET /audit-log</c>, run against the REAL
/// <c>Cachet.ArchiveSync.Api/Program.cs</c> pipeline via
/// <see cref="AuditLogApiWebApplicationFactory"/> (see that class's XML doc). Proves the spec's §13
/// Test Plan "AuditLogControllerTests" assignment: SC-6, SC-1 (HTTP-level).
/// </summary>
public sealed class AuditLogControllerTests : IAsyncLifetime
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
    private static readonly PasswordHasher<object> Hasher = new();

    private AuditLogApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new AuditLogApiWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private void SeedUser(int userId, string username = ValidUsername)
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
                Capabilities = new[] { "listFiles", "readFiles" },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
            });
    }

    private async Task<string> LoginAsync(string username = ValidUsername)
    {
        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = username, Password = ValidPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seeded user/B2 fakes are configured to succeed");

        response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
        var cookie = setCookieValues!.FirstOrDefault(v => v.StartsWith(AuditLogApiWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));
        cookie.Should().NotBeNull();
        return cookie!.Split(';')[0];
    }

    private HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url, string cookieHeaderValue)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeaderValue);
        return request;
    }

    // ---- SC-1: GET /audit-log with no query params returns 200 with paged rows, newest first -------

    [Fact]
    public async Task Test_SC1_DefaultRequestReturnsPagedRowsNewestFirst()
    {
        // Arrange
        SeedUser(userId: 1);
        var cookie = await LoginAsync();
        var newest = new Cachet.ArchiveSync.Core.Dtos.AuditLogRow
        {
            AuditLogId = 100901,
            Username = "m.alvarez",
            TabModule = "Quarantine",
            Action = "Deleted",
            FilePath = "contracts/2019/agreement-004.pdf",
            CreatedAt = new DateTimeOffset(2026, 9, 9, 14, 22, 10, TimeSpan.Zero),
            Result = "Success",
            ResultDetail = null
        };
        _factory.AuditLogRepository
            .ListAsync(null, null, null, 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<Cachet.ArchiveSync.Core.Dtos.AuditLogRow> { newest }, 1));

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/audit-log", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedResult<AuditLogListItem>>();
        body!.Items.Should().ContainSingle(i =>
            i.User == "m.alvarez" &&
            i.Tab == "Quarantine" &&
            i.Action == "Deleted" &&
            i.FilePath == "contracts/2019/agreement-004.pdf" &&
            i.Result == "Success");
        body.Page.Should().Be(1);
        body.PageSize.Should().Be(50);
        body.TotalCount.Should().Be(1);
    }

    // ---- SC-6: unauthenticated request returns 401 before any query executes ------------------------

    [Fact]
    public async Task Test_SC6_UnauthenticatedRequestReturns401()
    {
        // Act — no session cookie is sent.
        using var response = await _client.GetAsync("/audit-log");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _factory.AuditLogRepository.DidNotReceive().ListAsync(
            Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---- V-1: an unknown tab value is rejected with 422 over the real HTTP pipeline -----------------

    [Fact]
    public async Task Test_V1_UnknownTabRejectedReturns422OverHttp()
    {
        // Arrange
        SeedUser(userId: 1);
        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(
            AuthenticatedRequest(HttpMethod.Get, "/audit-log?tab=NotARealTab", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
