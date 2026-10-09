using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.Search;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests for CAS-006 §6's three endpoints, run against the REAL
/// <c>Cachet.ArchiveSync.Api/Program.cs</c> pipeline via <see cref="SearchApiWebApplicationFactory"/>
/// (see that class's XML doc). Proves the spec's §13 Test Plan "SearchControllerTests" assignment:
/// SC-1, SC-2, SC-6, SC-7, SC-8, V4, V5 — plus the verb/route/401 coverage
/// <c>.claude/agents/test-writer.md</c> asks every controller integration suite to include.
/// </summary>
public sealed class SearchControllerTests : IAsyncLifetime
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
    private const string ConfiguredBucket = "cachet-archive-prod";
    private static readonly PasswordHasher<object> Hasher = new();

    private SearchApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new SearchApiWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private void SeedUser(int userId, string username = ValidUsername, bool fullAccess = false)
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
                Capabilities = fullAccess
                    ? new[] { "listFiles", "readFiles", "writeFiles", "deleteFiles" }
                    : new[] { "listFiles", "readFiles" },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
            });
    }

    private async Task<string> LoginAsync(string username = ValidUsername)
    {
        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = username, Password = ValidPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the seeded user/B2 fakes are configured to succeed");

        response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
        var cookie = setCookieValues!.FirstOrDefault(v => v.StartsWith(SearchApiWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));
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

    // ---- SC-1: POST /search, valid body -> 202 + taskId, task_queue row, audit before response -----

    [Fact]
    public async Task Test_SC1_SearchSubmissionQueuesTaskAndAuditsBeforeResponse()
    {
        // Arrange
        SeedUser(userId: 1);
        var cookie = await LoginAsync();
        var callOrder = new List<string>();
        _factory.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "KeywordSearch", 1, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100234)
            .AndDoes(_ => callOrder.Add("insert-task"));
        _factory.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/search", cookie,
            new SearchRequest { Term = "confidential settlement", Bucket = ConfiguredBucket, Extension = "All" }));
        callOrder.Add("response-received");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<SearchSubmittedResponse>();
        body!.TaskId.Should().Be(100234);
        body.Status.Should().Be("Queued");

        callOrder.Should().Equal("insert-task", "audit", "response-received");
    }

    // ---- SC-2: POST /search, empty term -> 422, no task inserted ------------------------------------

    [Fact]
    public async Task Test_SC2_InvalidSearchRequestInsertsNoTask()
    {
        // Arrange
        SeedUser(userId: 2);
        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/search", cookie,
            new SearchRequest { Term = "", Bucket = ConfiguredBucket, Extension = "All" }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await _factory.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ---- CAS-038 V11: POST /search with exactly one date -> 422 validation-failed, no task, no audit --

    [Theory]
    [InlineData("fromDate")]
    [InlineData("toDate")]
    public async Task Test_V11_OnlyOneDateSuppliedReturns422AndInsertsNoTask(string suppliedDateField)
    {
        // Arrange — raw wire shape (§8: camelCase, ISO yyyy-MM-dd), one date field only.
        SeedUser(userId: 10);
        var cookie = await LoginAsync();
        var body = new Dictionary<string, object?>
        {
            ["term"] = "confidential settlement",
            ["bucket"] = ConfiguredBucket,
            ["extension"] = "All",
            [suppliedDateField] = "2025-01-10"
        };

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/search", cookie, body));

        // Assert — 422 with the validation-failed problem type and the V11 message.
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Type.Should().Be("https://cachet.tracecounts.internal/errors/validation-failed");
        problem.Title.Should().Be("'fromDate' and 'toDate' must be supplied together.");

        await _factory.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _factory.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == Cachet.ArchiveSync.Core.Enums.AuditAction.KeywordSearchSubmitted), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_Search_NoSession_Returns401()
    {
        // Act — no Cookie header at all.
        using var response = await _client.PostAsJsonAsync("/search", new SearchRequest { Term = "x", Bucket = ConfiguredBucket, Extension = "All" });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- SC-6: GET .../results on a Completed task -> 200 with paginated panels ---------------------

    [Fact]
    public async Task Test_SC6_CompletedTaskReturnsPaginatedPanels()
    {
        // Arrange
        SeedUser(userId: 3);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = "Completed",
            SubmittedByUserId = 3,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        _factory.SearchResultRepository.ListAsync(100234, "Filename", 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<SearchResultRow>
            {
                new()
                {
                    SearchResultId = 100501, TaskQueueId = 100234, FileIndexId = 1, MatchType = "Filename",
                    MatchStatus = "Flagged", CreatedAt = DateTimeOffset.UtcNow, FileName = "agreement-004.pdf",
                    RelativePath = "contracts/2019/agreement-004.pdf", FileSizeBytes = 482113, ModifiedAt = DateTimeOffset.UtcNow
                }
            }, 1));
        _factory.SearchResultRepository.ListAsync(100234, "Content", 1, 50, Arg.Any<CancellationToken>())
            .Returns((new List<SearchResultRow>(), 0));

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/search/100234/results", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SearchResultsResponse>();
        body!.Status.Should().Be("Completed");
        body.FilenameMatches!.Items.Should().ContainSingle(i => i.FileId == 100501);
        body.ContentMatches!.TotalCount.Should().Be(0);
    }

    // ---- SC-7 / V5: GET .../results on Queued/Running -> 409 -----------------------------------------

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    public async Task Test_SC7_IncompleteTaskReturns409(string status)
    {
        // Arrange
        SeedUser(userId: 4);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = status,
            SubmittedByUserId = 4,
            SubmittedAt = DateTimeOffset.UtcNow
        });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/search/100234/results", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Test_V5_ResultsBeforeCompletionReturns409()
    {
        // Arrange — same scenario as SC-7, kept as its own named test per the pre-extracted map.
        SeedUser(userId: 5);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = "Running",
            SubmittedByUserId = 5,
            SubmittedAt = DateTimeOffset.UtcNow
        });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/search/100234/results", cookie));

        // Assert — not a partial/empty 200 (V5's explicit requirement).
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- SC-8 / V4: GET .../results, unknown or wrong-task-type taskId -> 404 -----------------------

    [Fact]
    public async Task Test_SC8_UnknownTaskIdReturns404()
    {
        // Arrange
        SeedUser(userId: 6);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(999999, Arg.Any<CancellationToken>()).Returns((TaskQueueRow?)null);

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/search/999999/results", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Test_V4_ResultsUnknownOrWrongTypeTaskIdReturns404()
    {
        // Arrange — taskId exists but belongs to a different task_type.
        SeedUser(userId: 7);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "ContentScan",
            Status = "Completed",
            SubmittedByUserId = 7,
            SubmittedAt = DateTimeOffset.UtcNow
        });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/search/100234/results", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Test_GetResults_NoSession_Returns401()
    {
        // Act
        using var response = await _client.GetAsync("/search/100234/results");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- POST /search/results/quarantine: route/verb/auth coverage (V6-V10's own scenarios are ------
    // ---- covered at the service/repository layer — see QuarantineServiceTests/QuarantineEntryRepositoryTests)

    [Fact]
    public async Task Test_QuarantineEndpoint_ValidRequest_Returns200()
    {
        // Arrange — CAS-050 §2.8/V11: Full Access is required (was Read Only-sufficient before).
        SeedUser(userId: 8, fullAccess: true);
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = "Completed",
            SubmittedByUserId = 8,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        _factory.SearchResultRepository.GetAsync(100501, Arg.Any<CancellationToken>()).Returns(new SearchResultRow
        {
            SearchResultId = 100501,
            TaskQueueId = 100234,
            FileIndexId = 1,
            MatchType = "Filename",
            MatchStatus = "Flagged",
            CreatedAt = DateTimeOffset.UtcNow,
            FileName = "agreement-004.pdf",
            RelativePath = "contracts/2019/agreement-004.pdf",
            FileSizeBytes = 482113,
            ModifiedAt = DateTimeOffset.UtcNow
        });
        _factory.QuarantineEntryRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), 100501, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.Added, QuarantineEntryId = 100801 });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/search/results/quarantine", cookie,
            new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QuarantineAddResponse>();
        body!.Items.Should().ContainSingle(i => i.FileId == 100801);
    }

    [Fact]
    public async Task Test_QuarantineEndpoint_ReadOnly_Returns403()
    {
        // Arrange — CAS-050 §11: the Read Only-succeeds expectation is changed to 403; service never reached.
        SeedUser(userId: 10);
        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/search/results/quarantine", cookie,
            new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _factory.QuarantineEntryRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_QuarantineEndpoint_EmptyFileIds_Returns422()
    {
        // Arrange
        SeedUser(userId: 9, fullAccess: true);
        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/search/results/quarantine", cookie,
            new QuarantineAddRequest { TaskId = 100234, FileIds = Array.Empty<int>() }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Test_Quarantine_NoSession_Returns401()
    {
        // Act
        using var response = await _client.PostAsJsonAsync(
            "/search/results/quarantine", new QuarantineAddRequest { TaskId = 1, FileIds = new[] { 1 } });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- CAS-047 SC-1 (HTTP): POST /search with mode=Filename -> 202, Mode persisted, audit before response ----

    [Fact]
    public async Task Test_SC1_SubmitFilenamePersistsModeAndAuditsBeforeAccept()
    {
        // Arrange
        SeedUser(userId: 47);
        var cookie = await LoginAsync();
        var callOrder = new List<string>();
        string? persisted = null;
        _factory.TaskQueueRepository
            .InsertAsync(Arg.Any<string>(), Arg.Any<string>(), "KeywordSearch", 47, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(100234)
            .AndDoes(ci => { persisted = ci.ArgAt<string?>(4); callOrder.Add("insert-task"); });
        _factory.AuditLogService
            .WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1)
            .AndDoes(_ => callOrder.Add("audit"));

        // Act — raw wire shape: camelCase "mode".
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/search", cookie,
            new Dictionary<string, object?> { ["term"] = "agreement", ["bucket"] = ConfiguredBucket, ["extension"] = "All", ["mode"] = "Filename" }));
        callOrder.Add("response-received");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        System.Text.Json.JsonSerializer.Deserialize<KeywordSearchRequestDto>(persisted!)!.Mode.Should().Be("Filename");
        callOrder.Should().Equal("insert-task", "audit", "response-received");
        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.ResultDetail != null && e.ResultDetail.Contains("Filename") && !e.ResultDetail.Contains("agreement")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC1_SubmitFilenamePersistsModeAndAuditsBeforeAccept_UnknownModeReturns422AndNoSession401()
    {
        // Arrange
        SeedUser(userId: 48);
        var cookie = await LoginAsync();
        var body = new Dictionary<string, object?> { ["term"] = "agreement", ["bucket"] = ConfiguredBucket, ["extension"] = "All", ["mode"] = "Bogus" };

        // Act
        using var invalid = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Post, "/search", cookie, body));
        using var anonymous = await _client.PostAsJsonAsync("/search", body);

        // Assert
        invalid.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _factory.TaskQueueRepository.DidNotReceive().InsertAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
