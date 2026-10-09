using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.Sync;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// CAS-045 §13 — HTTP-level proof of <c>GET /sync/{taskId}/results</c>'s §8 changes through the REAL
/// <c>Program.cs</c> pipeline (<see cref="SyncApiWebApplicationFactory"/>, repositories substituted so no
/// SQL Server is needed): V9 (<c>400</c> invalid paging, before any DB call) and V10 (<c>excludeSynced</c>
/// reaches the repository; default <c>false</c> is unchanged). The stored procedure's own
/// <c>@exclude_synced</c> predicate needs a live SQL Server and is not exercised here.
/// </summary>
public sealed class SyncResultsEndpointTests : IAsyncLifetime
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
    private const int TaskId = 100301;
    private static readonly PasswordHasher<object> Hasher = new();

    private SyncApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new SyncApiWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<string> LoginAsync(int userId)
    {
        var sessionTokenVersion = _factory.RegisterSessionTokenVersion(userId);
        _factory.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(new UserAccount
        {
            UserId = userId,
            Username = ValidUsername,
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

        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = ValidUsername, Password = ValidPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
        return setCookieValues!.First(v => v.StartsWith(SyncApiWebApplicationFactory.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
    }

    private static HttpRequestMessage Get(string url, string cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private void SeedCompletedScan()
    {
        _factory.TaskQueueRepository.GetAsync(TaskId, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = TaskId,
            TaskType = "ContentScan",
            Status = "Completed",
            SubmittedByUserId = 1,
            SubmittedAt = DateTimeOffset.UtcNow
        });
    }

    private static SyncResultRow Row(int id, string comparisonType, string status) => new()
    {
        SyncResultId = id,
        TaskQueueId = TaskId,
        ComparisonType = comparisonType,
        SyncStatus = status,
        CreatedAt = DateTimeOffset.UtcNow,
        OdriveFileName = $"file-{id}.pdf",
        OdriveRelativePath = $"dir/file-{id}.pdf",
        OdriveModifiedAt = DateTimeOffset.UtcNow
    };

    [Theory]
    [InlineData("newOnODrivePage=0")]
    [InlineData("removedFromODrivePage=-3")]
    [InlineData("modifiedOnODrivePage=0")]
    [InlineData("newOnODrivePageSize=0")]
    [InlineData("removedFromODrivePageSize=101")]
    [InlineData("modifiedOnODrivePageSize=100000")]
    public async Task Test_V9_ResultsEndpointRejectsInvalidPaging(string invalidQuery)
    {
        // Arrange
        var cookie = await LoginAsync(userId: 1);
        SeedCompletedScan();

        // Act
        using var response = await _client.SendAsync(Get($"/sync/{TaskId}/results?{invalidQuery}", cookie));

        // Assert — 400 problem+json naming the offending parameter; no DB read of any kind.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("https://cachet.tracecounts.internal/errors/invalid-paging");
        body.Should().Contain(invalidQuery.Split('=')[0]);
        await _factory.TaskQueueRepository.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _factory.SyncResultRepository.DidNotReceive().ListAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V9_ResultsEndpointAcceptsBoundaryPaging()
    {
        // Arrange — page 1 and page size 1..100 are the inclusive valid range.
        var cookie = await LoginAsync(userId: 2);
        SeedCompletedScan();
        _factory.SyncResultRepository
            .ListAsync(TaskId, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((new List<SyncResultRow>(), 0));

        // Act
        using var response = await _client.SendAsync(Get(
            $"/sync/{TaskId}/results?newOnODrivePage=1&newOnODrivePageSize=1&removedFromODrivePageSize=100&modifiedOnODrivePage=7", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Test_V10_ExcludeSyncedFiltersRowsAndTotal()
    {
        // Arrange — the substituted repository plays the stored procedure: excludeSynced=true yields only
        // Pending/Failed rows and a TotalCount counting only those.
        var cookie = await LoginAsync(userId: 3);
        SeedCompletedScan();
        _factory.SyncResultRepository
            .ListAsync(TaskId, "Removed", 1, 50, true, Arg.Any<CancellationToken>())
            .Returns((new List<SyncResultRow> { Row(1, "Removed", "Pending"), Row(2, "Removed", "Failed") }, 2));
        _factory.SyncResultRepository
            .ListAsync(TaskId, "New", 1, 50, true, Arg.Any<CancellationToken>())
            .Returns((new List<SyncResultRow>(), 0));
        _factory.SyncResultRepository
            .ListAsync(TaskId, "Modified", 1, 50, true, Arg.Any<CancellationToken>())
            .Returns((new List<SyncResultRow>(), 0));

        // Act
        using var response = await _client.SendAsync(Get($"/sync/{TaskId}/results?excludeSynced=true", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SyncScanResultsResponse>();
        body!.RemovedFromODrive!.TotalCount.Should().Be(2);
        body.RemovedFromODrive.Items.Select(i => i.SyncStatus).Should().BeEquivalentTo(new[] { "Pending", "Failed" });
        await _factory.SyncResultRepository.Received(1).ListAsync(TaskId, "Removed", 1, 50, true, Arg.Any<CancellationToken>());
        await _factory.SyncResultRepository.DidNotReceive().ListAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V10_ExcludeSyncedDefaultFalseUnchanged()
    {
        // Arrange — no excludeSynced parameter: today's behavior, Synced rows and their count included.
        var cookie = await LoginAsync(userId: 4);
        SeedCompletedScan();
        _factory.SyncResultRepository
            .ListAsync(TaskId, "Removed", 1, 50, false, Arg.Any<CancellationToken>())
            .Returns((new List<SyncResultRow> { Row(1, "Removed", "Pending"), Row(2, "Removed", "Synced") }, 2));
        _factory.SyncResultRepository
            .ListAsync(TaskId, Arg.Is<string?>(t => t != "Removed"), 1, 50, false, Arg.Any<CancellationToken>())
            .Returns((new List<SyncResultRow>(), 0));

        // Act
        using var response = await _client.SendAsync(Get($"/sync/{TaskId}/results", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SyncScanResultsResponse>();
        body!.RemovedFromODrive!.Items.Select(i => i.SyncStatus).Should().Contain("Synced");
        body.RemovedFromODrive.TotalCount.Should().Be(2);
        await _factory.SyncResultRepository.DidNotReceive().ListAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), true, Arg.Any<CancellationToken>());
    }
}
