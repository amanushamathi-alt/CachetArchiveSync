using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.Sync;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests for CAS-007 §6's five endpoints, run against the REAL
/// <c>Cachet.ArchiveSync.Api/Program.cs</c> pipeline via <see cref="SyncApiWebApplicationFactory"/>
/// (see that class's XML doc). Proves the spec's §13 Test Plan "SyncControllerTests" assignment:
/// SC-1, SC-4, SC-5, SC-11, V1, V2, V8.
/// </summary>
public sealed class SyncControllerTests : IAsyncLifetime
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
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

    private void SeedUser(int userId, IReadOnlyList<string> b2Capabilities, string username = ValidUsername)
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

    private void SeedFullAccessUser(int userId, string username = ValidUsername) =>
        SeedUser(userId, new[] { "listFiles", "readFiles", "writeFiles", "deleteFiles" }, username);

    private void SeedReadOnlyUser(int userId, string username = ValidUsername) =>
        SeedUser(userId, new[] { "listFiles", "readFiles" }, username);

    private async Task<string> LoginAsync(string username = ValidUsername)
    {
        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = username, Password = ValidPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seeded user/B2 fakes are configured to succeed");

        response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
        var cookie = setCookieValues!.FirstOrDefault(v => v.StartsWith(SyncApiWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));
        cookie.Should().NotBeNull();
        return cookie!.Split(';')[0];
    }

    private HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url, string cookieHeaderValue, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeaderValue);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }

    private static TaskQueueRow CompletedContentScanTask(int taskId, int userId) => new()
    {
        TaskQueueId = taskId,
        TaskType = "ContentScan",
        Status = "Completed",
        SubmittedByUserId = userId,
        SubmittedAt = DateTimeOffset.UtcNow
    };

    // ---- SC-1: POST /sync/scan -> 202 + taskId, task_queue row, audit before response ----------------

    [Fact]
    public async Task Test_SC1_ScanSubmissionQueuesTaskAndAuditsBeforeResponse()
    {
        // Arrange
        SeedFullAccessUser(userId: 1);
        var cookie = await LoginAsync();
        var callOrder = new List<string>();
        _factory.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "ContentScan", 1, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100301)
            .AndDoes(_ => callOrder.Add("insert-task"));
        _factory.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/sync/scan", cookie));
        callOrder.Add("response-received");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<TaskSubmittedResponse>();
        body!.TaskId.Should().Be(100301);
        body.Type.Should().Be("ContentScan");
        body.Status.Should().Be("Queued");

        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.ContentScanSubmitted && e.TaskQueueId == 100301),
            Arg.Any<CancellationToken>());
        callOrder.Should().Equal("insert-task", "audit", "response-received");
    }

    [Fact]
    public async Task Test_Scan_NoSession_Returns401()
    {
        // Act — no Cookie header at all.
        using var response = await _client.PostAsync("/sync/scan", content: null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- SC-4: GET /sync/{taskId}/results on a Completed task -> 200 with three paginated panels ----

    [Fact]
    public async Task Test_SC4_CompletedScanReturnsThreePaginatedPanels()
    {
        // Arrange
        SeedFullAccessUser(userId: 2);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100301, Arg.Any<CancellationToken>()).Returns(CompletedContentScanTask(100301, 2));
        _factory.SyncResultRepository.ListAsync(100301, "New", 1, 50, false, Arg.Any<CancellationToken>())
            .Returns((new List<SyncResultRow>
            {
                new()
                {
                    SyncResultId = 100601, TaskQueueId = 100301, ComparisonType = "New", SyncStatus = "Pending",
                    CreatedAt = DateTimeOffset.UtcNow, OdriveFileName = "new-invoice.xlsx",
                    OdriveRelativePath = "finance/2026/new-invoice.xlsx", OdriveFileSizeBytes = 20481,
                    OdriveModifiedAt = DateTimeOffset.UtcNow
                }
            }, 1));
        _factory.SyncResultRepository.ListAsync(100301, "Removed", 1, 50, false, Arg.Any<CancellationToken>())
            .Returns((new List<SyncResultRow>(), 0));
        _factory.SyncResultRepository.ListAsync(100301, "Modified", 1, 50, false, Arg.Any<CancellationToken>())
            .Returns((new List<SyncResultRow>
            {
                new()
                {
                    SyncResultId = 100603, TaskQueueId = 100301, ComparisonType = "Modified", SyncStatus = "Pending",
                    CreatedAt = DateTimeOffset.UtcNow, Sha1Changed = true, OdriveFileName = "policy.docx",
                    OdriveRelativePath = "hr/policy.docx", OdriveModifiedAt = DateTimeOffset.UtcNow
                }
            }, 1));

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/sync/100301/results", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SyncScanResultsResponse>();
        body!.Status.Should().Be("Completed");
        body.NewOnODrive!.Items.Should().ContainSingle(i => i.FileId == 100601);
        body.RemovedFromODrive!.TotalCount.Should().Be(0);
        body.ModifiedOnODrive!.Items.Should().ContainSingle(i => i.FileId == 100603 && i.Sha1Changed);
    }

    // ---- SC-5 / V1 / V2: incomplete or unknown/wrong-type taskId handled correctly -------------------

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    public async Task Test_SC5_IncompleteOrUnknownTaskIdHandledCorrectly(string status)
    {
        // Arrange — incomplete task -> 409.
        SeedFullAccessUser(userId: 3);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100301, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100301,
            TaskType = "ContentScan",
            Status = status,
            SubmittedByUserId = 3,
            SubmittedAt = DateTimeOffset.UtcNow
        });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/sync/100301/results", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Test_SC5_UnknownTaskId_Returns404()
    {
        // Arrange — unknown taskId -> 404.
        SeedFullAccessUser(userId: 4);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(999999, Arg.Any<CancellationToken>()).Returns((TaskQueueRow?)null);

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/sync/999999/results", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Test_V1_ResultsUnknownOrWrongTypeTaskIdReturns404()
    {
        // Arrange — taskId exists but belongs to a different task_type.
        SeedFullAccessUser(userId: 5);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100301, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100301,
            TaskType = "KeywordSearch",
            Status = "Completed",
            SubmittedByUserId = 5,
            SubmittedAt = DateTimeOffset.UtcNow
        });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/sync/100301/results", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Test_V2_ResultsBeforeCompletionReturns409()
    {
        // Arrange — same scenario as SC-5's Running case, kept as its own named test per the map.
        SeedFullAccessUser(userId: 6);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100301, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100301,
            TaskType = "ContentScan",
            Status = "Running",
            SubmittedByUserId = 6,
            SubmittedAt = DateTimeOffset.UtcNow
        });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/sync/100301/results", cookie));

        // Assert — not a partial/empty 200.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Test_GetResults_NoSession_Returns401()
    {
        // Act
        using var response = await _client.GetAsync("/sync/100301/results");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- SC-11 / V8: Read Only session -> 403 on all three actions, 200/202 on scan/results ----------

    [Fact]
    public async Task Test_SC11_ReadOnlySessionCannotSubmitActionsButCanScanAndRead()
    {
        // Arrange
        SeedReadOnlyUser(userId: 7);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "ContentScan", 7, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100301);
        _factory.TaskQueueRepository.GetAsync(100301, Arg.Any<CancellationToken>()).Returns(CompletedContentScanTask(100301, 7));
        _factory.SyncResultRepository.ListAsync(100301, Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((new List<SyncResultRow>(), 0));

        // Act
        using var scanResponse = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/sync/scan", cookie));
        using var resultsResponse = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/sync/100301/results", cookie));
        using var copyResponse = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/sync/actions/copy", cookie, new SyncActionRequest { TaskId = 100301, FileIds = new[] { 100601 } }));
        using var updateResponse = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/sync/actions/update", cookie, new SyncActionRequest { TaskId = 100301, FileIds = new[] { 100603 } }));
        using var purgeResponse = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/sync/actions/purge", cookie, new SyncPurgeRequest { TaskId = 100301, FileIds = new[] { 100602 }, Confirm = true }));

        // Assert
        scanResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        resultsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        copyResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        purgeResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Test_V8_SyncActionsRequireFullAccessScanAndResultsDoNotEscalate()
    {
        // Arrange — a Full Access session, to confirm the three actions succeed when the
        // capability IS present (the converse of SC-11 — scan/results never require MORE than
        // CachetAuthenticated, i.e. a plain authenticated session is never itself rejected by the
        // action endpoints' policy check being silently downgraded).
        SeedFullAccessUser(userId: 8);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100301, Arg.Any<CancellationToken>()).Returns(CompletedContentScanTask(100301, 8));
        _factory.SyncResultRepository.GetAsync(100601, Arg.Any<CancellationToken>()).Returns(new SyncResultRow
        {
            SyncResultId = 100601,
            TaskQueueId = 100301,
            ComparisonType = "New",
            SyncStatus = "Pending",
            CreatedAt = DateTimeOffset.UtcNow,
            OdriveFileName = "new-invoice.xlsx",
            OdriveRelativePath = "finance/2026/new-invoice.xlsx"
        });
        _factory.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "SyncCopy", 8, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100401);

        // Act
        using var copyResponse = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/sync/actions/copy", cookie, new SyncActionRequest { TaskId = 100301, FileIds = new[] { 100601 } }));

        // Assert
        copyResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Test_CopyAction_NoSession_Returns401()
    {
        // Act
        using var response = await _client.PostAsJsonAsync(
            "/sync/actions/copy", new SyncActionRequest { TaskId = 1, FileIds = new[] { 1 } });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ==== CAS-037 §6 — POST /sync/mirror-sync ==========================================================

    [Fact]
    public async Task Test_PostMirrorSync_ReturnsAcceptedAndEnqueuesTask()
    {
        // Arrange — CachetAuthenticated is sufficient (§6, not CachetFullAccess), proven here with a
        // Read-Only session — mirror sync only reads from B2.
        SeedReadOnlyUser(userId: 20);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.HasActiveTaskOfTypeAsync("B2MirrorSync", Arg.Any<CancellationToken>()).Returns(false);
        _factory.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "B2MirrorSync", 20, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100801);

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/sync/mirror-sync", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<TaskSubmittedResponse>();
        body!.TaskId.Should().Be(100801);
        body.Type.Should().Be("B2MirrorSync");
        body.Status.Should().Be("Queued");

        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.B2MirrorSyncSubmitted && e.TaskQueueId == 100801),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_MirrorSync_NoSession_Returns401()
    {
        // Act
        using var response = await _client.PostAsync("/sync/mirror-sync", content: null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Test_MirrorSync_AlreadyRunning_Returns409NoTaskInserted()
    {
        // Arrange — a B2MirrorSync task is already Queued/Running (§7 V1).
        SeedFullAccessUser(userId: 21);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.HasActiveTaskOfTypeAsync("B2MirrorSync", Arg.Any<CancellationToken>()).Returns(true);

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/sync/mirror-sync", cookie));

        // Assert — SC-6: 409, no task_queue row inserted.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _factory.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), "B2MirrorSync", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
