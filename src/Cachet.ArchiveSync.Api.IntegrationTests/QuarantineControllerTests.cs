using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.Quarantine;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Exceptions;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests for CAS-008 §6's three endpoints, run against the REAL
/// <c>Cachet.ArchiveSync.Api/Program.cs</c> pipeline via
/// <see cref="QuarantineApiWebApplicationFactory"/> (see that class's XML doc). Proves the spec's
/// §13 Test Plan "QuarantineControllerTests" assignment: SC-1, SC-6, SC-2 (HTTP-level).
/// </summary>
public sealed class QuarantineControllerTests : IAsyncLifetime
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
    private static readonly PasswordHasher<object> Hasher = new();

    private QuarantineApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new QuarantineApiWebApplicationFactory();
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

        var userAccount = new UserAccount
        {
            UserId = userId,
            Username = username,
            PasswordHash = Hasher.HashPassword(new object(), ValidPassword),
            B2ApplicationKeyId = "ct:keyid",
            B2ApplicationKey = "ct:key",
            IsActive = true,
            SessionTokenVersion = sessionTokenVersion
        };
        _factory.UserRepository.GetByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(userAccount);
        // CAS-018 §2 step 1: Permanent Delete's live pre-batch check re-fetches the acting user by
        // id (not username) — seeded here too so any delete-flow test using this same helper gets a
        // consistent, live-re-checkable identity without duplicating the login-time seed.
        _factory.UserRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(userAccount);
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
        var cookie = setCookieValues!.FirstOrDefault(v => v.StartsWith(QuarantineApiWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));
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

    // ---- SC-1: GET /quarantine returns 200 with every Active row, correctly shaped ------------------

    [Fact]
    public async Task Test_SC1_ListReturnsOnlyActiveEntries()
    {
        // Arrange
        SeedReadOnlyUser(userId: 1);
        var cookie = await LoginAsync();
        _factory.QuarantineEntryRepository.ListActivePagedAsync(1, 25, Arg.Any<CancellationToken>()).Returns(new QuarantineEntryListPage
        {
            TotalCount = 1,
            Rows = new List<QuarantineEntryListRow>
        {
            new()
            {
                QuarantineEntryId = 100801,
                FileIndexId = 600801,
                Name = "agreement-004.pdf",
                Path = "contracts/2019/agreement-004.pdf",
                QuarantinedOn = new DateTimeOffset(2026, 9, 9, 14, 10, 0, TimeSpan.Zero),
                Status = "Flagged",
                SourceTab = "Keyword Search"
            }
            }
        });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/quarantine", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QuarantineListResponse>();
        body!.Items.Should().ContainSingle(i =>
            i.FileId == 100801 &&
            i.Name == "agreement-004.pdf" &&
            i.Path == "contracts/2019/agreement-004.pdf" &&
            i.Status == "Flagged");
    }

    [Fact]
    public async Task Test_GetQuarantine_NoSession_Returns401()
    {
        // Act
        using var response = await _client.GetAsync("/quarantine");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- SC-2: POST /quarantine (HTTP-level) — real end-to-end route alias ------------------------

    [Fact]
    public async Task Test_SC2_PostQuarantineAliasProducesSameResultAsExistingEndpoint()
    {
        // Arrange — the same task/search-result/insert wiring QuarantineServiceTests uses to prove
        // a successful add via IQuarantineService.AddAsync, now driven through the literal
        // POST /quarantine route (§0) rather than a mock, at the HTTP layer.
        SeedFullAccessUser(userId: 2); // CAS-050 §2.8/§11: every Quarantine write now requires Full Access (SC-11: behaviour otherwise unchanged).
        var cookie = await LoginAsync();
        _factory.TaskQueueRepository.GetAsync(100234, Arg.Any<CancellationToken>()).Returns(new TaskQueueRow
        {
            TaskQueueId = 100234,
            TaskType = "KeywordSearch",
            Status = "Completed",
            SubmittedByUserId = 2,
            SubmittedAt = DateTimeOffset.UtcNow
        });
        _factory.SearchResultRepository.GetAsync(100501, Arg.Any<CancellationToken>()).Returns(new SearchResultRow
        {
            SearchResultId = 100501,
            TaskQueueId = 100234,
            FileIndexId = 200501,
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
            HttpMethod.Post, "/quarantine", cookie, new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } }));

        // Assert — same 200 shape/audit write CAS-006's POST /search/results/quarantine produces
        // for the identical input (§0, §6, §7 V8).
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QuarantineAddResponse>();
        body!.Items.Should().ContainSingle(i => i.FileId == 100801 && i.Name == "agreement-004.pdf");

        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.QuarantineAdd && e.TaskQueueId == 100234 && e.UserId == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_PostQuarantine_NoSession_Returns401()
    {
        // Act
        using var response = await _client.PostAsJsonAsync(
            "/quarantine", new QuarantineAddRequest { TaskId = 1, FileIds = new[] { 1 } });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- SC-6: a Read Only session is blocked from POST /quarantine/delete with 403 ------------------

    [Fact]
    public async Task Test_SC6_ReadOnlySessionBlockedFromPermanentDelete()
    {
        // Arrange
        SeedReadOnlyUser(userId: 3);
        var cookie = await LoginAsync();

        // Act — no per-file resolution/B2/local mutation is even reachable; the policy rejects
        // the request before the controller method runs (§6, §12).
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/delete", cookie, new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _factory.QuarantineEntryRepository.DidNotReceive().GetActiveForDeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _factory.UserScopedB2DeleteClient.DidNotReceive().ListFileVersionsAsync(
            Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_FullAccessSession_PermanentDeleteSucceeds()
    {
        // Arrange — the converse of SC-6: a Full Access session IS allowed through the policy gate,
        // and CAS-018's live pre-batch re-check (using the SAME B2KeyAuthorizer/CredentialProtector
        // fakes SeedFullAccessUser already wires for login) also succeeds.
        SeedFullAccessUser(userId: 4);
        var cookie = await LoginAsync();
        _factory.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(new QuarantineEntryForDeleteRow
        {
            QuarantineEntryId = 100801,
            FileIndexId = 200801,
            Name = "agreement-004.pdf",
            Path = "contracts/2019/agreement-004.pdf"
        });
        _factory.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), "contracts/2019/agreement-004.pdf", "contracts/2019/agreement-004.pdf", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new B2ListFileVersionsResult
            {
                Files = new List<B2FileInfo>
                {
                    new() { FileId = "4_z_v1", FileName = "contracts/2019/agreement-004.pdf", ContentSha1 = "sha1", ContentLength = 1, Action = "upload", UploadTimestamp = 1 }
                }
            });
        _factory.QuarantineEntryRepository.TryTransitionToDeletedAsync(100801, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/delete", cookie, new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QuarantineDeleteResponse>();
        body!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Success");
        await _factory.UserScopedB2DeleteClient.Received(1).DeleteFileVersionAsync(
            Arg.Any<B2AuthorizationInfo>(), "contracts/2019/agreement-004.pdf", "4_z_v1", Arg.Any<CancellationToken>());
    }

    // ---- CAS-018 SC-2/SC-3: a live authorization failure at delete time (even for a Full Access ----
    // ---- session that passed login) returns 403, touching zero files -------------------------------

    [Fact]
    public async Task Test_PermanentDelete_LiveAuthorizationDenied_Returns403()
    {
        // Arrange — the session logged in successfully as Full Access (its B2 key had deleteFiles
        // AT LOGIN TIME), but the live re-check CAS-018 §2 performs at delete time now fails — the
        // key was revoked in between. NSubstitute's sequential-Returns lets the same
        // AuthorizeAsync(keyId, key, ct) call succeed once (during login) and then fail (during the
        // delete request), modeling the exact CAS-018 §10 SC-4 scenario: the stale claim from login
        // does not bypass this live check.
        const int userId = 6;
        var sessionTokenVersion = _factory.RegisterSessionTokenVersion(userId);
        var userAccount = new UserAccount
        {
            UserId = userId,
            Username = ValidUsername,
            PasswordHash = Hasher.HashPassword(new object(), ValidPassword),
            B2ApplicationKeyId = "ct:keyid",
            B2ApplicationKey = "ct:key",
            IsActive = true,
            SessionTokenVersion = sessionTokenVersion
        };
        _factory.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(userAccount);
        _factory.UserRepository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(userAccount);
        _factory.CredentialProtector.Unprotect("ct:keyid").Returns("plain-key-id");
        _factory.CredentialProtector.Unprotect("ct:key").Returns("plain-app-key");
        var loginTimeAuthorization = new B2AuthorizationInfo
        {
            AccountId = "acct-1",
            AuthorizationToken = "4_secretB2token_neverLog",
            ApiUrl = "https://api-fake.backblazeb2.com",
            DownloadUrl = "https://download-fake.backblazeb2.com",
            Capabilities = new[] { "listFiles", "readFiles", "writeFiles", "deleteFiles" },
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
        };
        _factory.B2KeyAuthorizer.AuthorizeAsync("plain-key-id", "plain-app-key", Arg.Any<CancellationToken>())
            .Returns(
                _ => loginTimeAuthorization,
                _ => throw new B2ApiException(401, "bad_auth_token", "revoked after login"));

        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/delete", cookie, new QuarantineDeleteRequest { FileIds = new[] { 100801 }, Confirm = true }));

        // Assert — 403, zero files touched, one DeleteAuthorizationDenied audit entry (CAS-018 §10
        // SC-2/SC-4).
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _factory.QuarantineEntryRepository.DidNotReceive().GetActiveForDeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _factory.UserScopedB2DeleteClient.DidNotReceive().ListFileVersionsAsync(
            Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.DeleteAuthorizationDenied && e.UserId == userId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_PostQuarantineDelete_NoSession_Returns401()
    {
        // Act
        using var response = await _client.PostAsJsonAsync(
            "/quarantine/delete", new QuarantineDeleteRequest { FileIds = new[] { 1 }, Confirm = true });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- CAS-014 SC-1: POST /quarantine/remove (HTTP-level) — hard-deletes via the real pipeline ----

    [Fact]
    public async Task Test_SC1_RemoveEndpointHardDeletesViaHttp()
    {
        // Arrange — CAS-050 §2.8/§11: Full Access is now required (a Read Only session gets 403 — see
        // Test_Remove_ReadOnly_Returns403). SC-11: Full Access behaviour is unchanged.
        SeedFullAccessUser(userId: 5);
        var cookie = await LoginAsync();
        var entry = new QuarantineEntryForDeleteRow
        {
            QuarantineEntryId = 100801,
            FileIndexId = 200801,
            Name = "agreement-004.pdf",
            Path = "contracts/2019/agreement-004.pdf"
        };
        _factory.QuarantineEntryRepository.GetActiveForDeleteAsync(100801, Arg.Any<CancellationToken>()).Returns(entry);
        _factory.QuarantineEntryRepository.HardDeleteActiveAsync(100801, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/remove", cookie, new QuarantineRemoveRequest { FileIds = new[] { 100801 } }));

        // Assert — 200 with a per-file Success result, and a QuarantineRemoved audit row written
        // (no B2 call is even reachable, since QuarantineRemoveService never depends on IB2Client).
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QuarantineRemoveResponse>();
        body!.Results.Should().ContainSingle(r => r.FileId == 100801 && r.Result == "Success");

        await _factory.QuarantineEntryRepository.Received(1).HardDeleteActiveAsync(100801, Arg.Any<CancellationToken>());
        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.QuarantineRemoved &&
                e.TabModule == "Quarantine" &&
                e.FilePath == entry.Path &&
                e.UserId == 5 &&
                e.Result == AuditResult.Success),
            Arg.Any<CancellationToken>());
        await _factory.B2Client.DidNotReceive().ListFileVersionsAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _factory.FileIndexRepository.DidNotReceive().UpdateAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_PostQuarantineRemove_NoSession_Returns401()
    {
        // Act
        using var response = await _client.PostAsJsonAsync(
            "/quarantine/remove", new QuarantineRemoveRequest { FileIds = new[] { 1 } });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // =====================================================================================================
    // CAS-050 - Full Access on every Quarantine write; Files-browser add/remove/state; paged list
    // =====================================================================================================

    private static readonly (HttpMethod Method, string Url, object Body)[] FiveWriteRoutes =
    {
        (HttpMethod.Post, "/quarantine", new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } }),
        (HttpMethod.Post, "/search/results/quarantine", new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } }),
        (HttpMethod.Post, "/quarantine/remove", new QuarantineRemoveRequest { FileIds = new[] { 100801 } }),
        (HttpMethod.Post, "/quarantine/browse", new QuarantineBrowseRequest { Scope = "File", FileIndexId = 600801 }),
        (HttpMethod.Post, "/quarantine/browse/remove", new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "b", FolderPath = "Ops" })
    };

    private async Task AssertNoQuarantineWriteReachedServicesAsync()
    {
        await _factory.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default!, default, default);
        await _factory.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().InsertBrowseFileAsync(default!, default!, default, default);
        await _factory.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().InsertBrowseFolderAsync(default!, default!, default!, default!, default!, default);
        await _factory.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().HardDeleteActiveAsync(default, default);
        await _factory.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().HardDeleteBrowseFileAsync(default, default);
        await _factory.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().HardDeleteBrowseFolderAsync(default!, default!, default!, default);
        await _factory.TaskQueueRepository.DidNotReceiveWithAnyArgs().GetAsync(default, default);
        await _factory.FileIndexRepository.DidNotReceiveWithAnyArgs().GetAsync(default, default);
        await _factory.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.QuarantineAdd || e.Action == AuditAction.QuarantineRemoved),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_PostQuarantine_ReadOnly_Returns403()
    {
        // Arrange - CAS-050 section 11: the former "Read Only succeeds on POST /quarantine" expectation is changed to 403.
        SeedReadOnlyUser(userId: 12);
        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine", cookie, new QuarantineAddRequest { TaskId = 100234, FileIds = new[] { 100501 } }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertNoQuarantineWriteReachedServicesAsync();
    }

    [Fact]
    public async Task Test_Remove_ReadOnly_Returns403()
    {
        // Arrange - CAS-050 section 11: the former "Read Only succeeds on POST /quarantine/remove" expectation is changed to 403.
        SeedReadOnlyUser(userId: 13);
        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/remove", cookie, new QuarantineRemoveRequest { FileIds = new[] { 100801 } }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertNoQuarantineWriteReachedServicesAsync();
    }

    // ---- V11: Read Only -> 403 and no service reached on all five write routes -----------------------

    [Fact]
    public async Task Test_V11_AllQuarantineWrites_ReadOnly_403()
    {
        // Arrange
        SeedReadOnlyUser(userId: 14);
        var cookie = await LoginAsync();

        foreach (var (method, url, body) in FiveWriteRoutes)
        {
            // Act
            using var response = await _client.SendAsync(AuthenticatedRequest(method, url, cookie, body));

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {url} must reject Read Only");
        }

        await AssertNoQuarantineWriteReachedServicesAsync();
    }

    [Fact]
    public async Task Test_V11_AllQuarantineWrites_NoSession_401()
    {
        foreach (var (method, url, body) in FiveWriteRoutes)
        {
            using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
            using var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {url}");
        }
    }

    // ---- SC-12 (= V11): Read Only is 403 on all five writes yet still reads list, state, search results

    [Fact]
    public async Task Test_SC12_ReadOnly_AllWrites403_ReadsOk()
    {
        // Arrange
        SeedReadOnlyUser(userId: 15);
        var cookie = await LoginAsync();
        _factory.QuarantineEntryRepository.ListActivePagedAsync(1, 25, Arg.Any<CancellationToken>())
            .Returns(new QuarantineEntryListPage { Rows = new List<QuarantineEntryListRow>(), TotalCount = 0 });
        _factory.QuarantineEntryRepository.GetBrowseStateAsync("B2", "bkt", "D/Shares/Operations/Ops", Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseStateResponse
            {
                FileIndexIds = new[] { 7 },
                Folders = Array.Empty<QuarantineBrowseStateFolder>()
            });

        // Act + Assert - writes
        foreach (var (method, url, body) in FiveWriteRoutes)
        {
            using var write = await _client.SendAsync(AuthenticatedRequest(method, url, cookie, body));
            write.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {url}");
        }

        await AssertNoQuarantineWriteReachedServicesAsync();

        // Act + Assert - reads stay open for Read Only
        using var list = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/quarantine", cookie));
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        using var state = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Get, "/quarantine/browse/state?source=B2&bucketName=bkt&folderPath=Ops", cookie));
        state.StatusCode.Should().Be(HttpStatusCode.OK);

        // Search results read: authorized (an unknown task is a 404 from the service, not a 403 from the policy).
        using var results = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/search/999999/results", cookie));
        results.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- V10: a Files-added row is an ordinary file row - the existing Permanent Delete path accepts it

    [Fact]
    public async Task Test_V10_FilesAddedRow_WorksWithExistingDeletePath()
    {
        // Arrange - a Files-added row resolves through dbo.quarantine_entry_get exactly like a Keyword
        // Search row (file_index_id set; search_result_id is never consulted by the delete path).
        SeedFullAccessUser(userId: 16);
        var cookie = await LoginAsync();
        _factory.QuarantineEntryRepository.GetActiveForDeleteAsync(100900, Arg.Any<CancellationToken>()).Returns(new QuarantineEntryForDeleteRow
        {
            QuarantineEntryId = 100900,
            FileIndexId = 200900,
            Name = "files-added.pdf",
            Path = "Ops/Audit/files-added.pdf"
        });
        _factory.UserScopedB2DeleteClient.ListFileVersionsAsync(
                Arg.Any<B2AuthorizationInfo>(), Arg.Any<string>(), "Ops/Audit/files-added.pdf", "Ops/Audit/files-added.pdf", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new B2ListFileVersionsResult
            {
                Files = new List<B2FileInfo>
                {
                    new() { FileId = "4_z_v9", FileName = "Ops/Audit/files-added.pdf", ContentSha1 = "sha1", ContentLength = 1, Action = "upload", UploadTimestamp = 1 }
                }
            });
        _factory.QuarantineEntryRepository.TryTransitionToDeletedAsync(100900, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/delete", cookie, new QuarantineDeleteRequest { FileIds = new[] { 100900 }, Confirm = true }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QuarantineDeleteResponse>();
        body!.Results.Should().ContainSingle(r => r.FileId == 100900 && r.Result == "Success");
        await _factory.QuarantineEntryRepository.Received(1).TryTransitionToDeletedAsync(100900, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- HTTP supplements for SC-1/SC-2/SC-5/SC-7: verb+route resolve, Full Access 200, audit before response

    [Fact]
    public async Task Test_BrowseAdd_FullAccess_FolderScope_Returns200WithCountsAndAuditsBeforeResponse()
    {
        // Arrange
        SeedFullAccessUser(userId: 17);
        var cookie = await LoginAsync();
        _factory.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", "D/Shares/Operations/Ops/Audit", Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 3, AlreadyQuarantined = 2 });
        var callOrder = new List<string>();
        _factory.AuditLogService.WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(1).AndDoes(ci =>
            {
                if (ci.Arg<AuditLogEntry>().Action == AuditAction.QuarantineAdd)
                {
                    callOrder.Add("audit");
                }
            });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/browse", cookie,
            new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "bkt", FolderPath = "Ops/Audit" }));
        callOrder.Add("response-received");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QuarantineBrowseAddResponse>();
        body!.Added.Should().Be(3);
        body.AlreadyQuarantined.Should().Be(2);
        callOrder.Should().Equal("audit", "response-received");
        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.Action == AuditAction.QuarantineAdd && e.TabModule == "Quarantine" && e.UserId == 17 &&
                e.FilePath == "D/Shares/Operations/Ops/Audit" && e.ResultDetail == "scope=Folder; added=3; alreadyQuarantined=2"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_BrowseAdd_FullAccess_FileScope_Returns200()
    {
        // Arrange
        SeedFullAccessUser(userId: 18);
        var cookie = await LoginAsync();
        _factory.FileIndexRepository.GetAsync(600801, Arg.Any<CancellationToken>()).Returns(new FileIndexRecord
        {
            FileIndexId = 600801,
            Source = "B2",
            BucketName = "bkt",
            RelativePath = "Ops/a.pdf",
            FileName = "a.pdf",
            FileSizeBytes = 1,
            ModifiedAt = DateTimeOffset.UtcNow,
            IsActive = true
        });
        _factory.QuarantineEntryRepository
            .InsertBrowseFileAsync(Arg.Any<string>(), Arg.Any<string>(), 600801, Arg.Any<CancellationToken>())
            .Returns(new QuarantineInsertResult { Outcome = QuarantineInsertOutcome.Added, QuarantineEntryId = 100950 });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/browse", cookie, new QuarantineBrowseRequest { Scope = "File", FileIndexId = 600801 }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QuarantineBrowseAddResponse>();
        body!.Added.Should().Be(1);
    }

    [Fact]
    public async Task Test_BrowseAdd_AuditFailure_DoesNotReturn200()
    {
        // Arrange - FR-23: if the audit write fails the endpoint must not report success.
        SeedFullAccessUser(userId: 19);
        var cookie = await LoginAsync();
        _factory.QuarantineEntryRepository
            .InsertBrowseFolderAsync(Arg.Any<string>(), Arg.Any<string>(), "B2", "bkt", "D/Shares/Operations/Ops", Arg.Any<CancellationToken>())
            .Returns(new QuarantineBrowseFolderAddRow { Added = 1, AlreadyQuarantined = 0 });
        _factory.AuditLogService
            .When(a => a.WriteAsync(Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.QuarantineAdd), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("audit store down"));

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/browse", cookie,
            new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "bkt", FolderPath = "Ops" }));

        // Assert
        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(500);
    }

    [Fact]
    public async Task Test_BrowseRemove_FullAccess_FolderScope_Returns200WithRemoved()
    {
        // Arrange
        SeedFullAccessUser(userId: 20);
        var cookie = await LoginAsync();
        _factory.QuarantineEntryRepository
            .HardDeleteBrowseFolderAsync("B2", "bkt", "D/Shares/Operations/Ops", Arg.Any<CancellationToken>())
            .Returns(4);

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/browse/remove", cookie,
            new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "bkt", FolderPath = "Ops" }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QuarantineBrowseRemoveResponse>();
        body!.Removed.Should().Be(4);
        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.QuarantineRemoved && e.ResultDetail == "scope=Folder; removed=4"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_BrowseAdd_InvalidShape_Returns422()
    {
        // Arrange
        SeedFullAccessUser(userId: 21);
        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/quarantine/browse", cookie,
            new QuarantineBrowseRequest { Scope = "Folder", Source = "B2", BucketName = "bkt", FolderPath = "/Ops/" }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await _factory.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().InsertBrowseFolderAsync(default!, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Test_BrowseRoutes_NoSession_Returns401()
    {
        using var add = await _client.PostAsJsonAsync("/quarantine/browse", new QuarantineBrowseRequest { Scope = "File", FileIndexId = 1 });
        using var remove = await _client.PostAsJsonAsync("/quarantine/browse/remove", new QuarantineBrowseRequest { Scope = "File", FileIndexId = 1 });
        using var state = await _client.GetAsync("/quarantine/browse/state?source=B2&bucketName=b&folderPath=");

        add.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        remove.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        state.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Test_BrowseState_InvalidSource_Returns422()
    {
        // Arrange
        SeedReadOnlyUser(userId: 22);
        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Get, "/quarantine/browse/state?source=Nope&bucketName=b&folderPath=", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ---- HTTP supplements for SC-8 / V7 ---------------------------------------------------------------

    [Fact]
    public async Task Test_GetQuarantine_Page2Size25_PassesPagingAndReturnsEnvelope()
    {
        // Arrange
        SeedReadOnlyUser(userId: 23);
        var cookie = await LoginAsync();
        _factory.QuarantineEntryRepository.ListActivePagedAsync(2, 25, Arg.Any<CancellationToken>()).Returns(new QuarantineEntryListPage
        {
            TotalCount = 60,
            Rows = new List<QuarantineEntryListRow>
            {
                new()
                {
                    QuarantineEntryId = 100026, FileIndexId = 1, Name = "f.pdf", Path = "Ops/f.pdf",
                    QuarantinedOn = DateTimeOffset.UtcNow, Status = "Informational", SourceTab = "Files"
                }
            }
        });

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/quarantine?page=2&pageSize=25", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<QuarantineListResponse>();
        body!.TotalCount.Should().Be(60);
        body.Page.Should().Be(2);
        body.PageSize.Should().Be(25);
        body.Items.Should().ContainSingle(i => i.SourceTab == "Files");
    }

    [Theory]
    [InlineData("page=0&pageSize=25")]
    [InlineData("page=-1&pageSize=25")]
    [InlineData("page=1&pageSize=10")]
    [InlineData("page=1&pageSize=100")]
    public async Task Test_GetQuarantine_InvalidPaging_Returns422(string query)
    {
        // Arrange
        SeedReadOnlyUser(userId: 24);
        var cookie = await LoginAsync();

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/quarantine?" + query, cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await _factory.QuarantineEntryRepository.DidNotReceiveWithAnyArgs().ListActivePagedAsync(default, default, default);
    }
}
