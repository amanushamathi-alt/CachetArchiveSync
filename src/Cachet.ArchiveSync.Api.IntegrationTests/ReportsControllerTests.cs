using System.Linq;
using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.Reporting;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// End-to-end HTTP tests for CAS-010 §6 / CAS-015 §2/§8's Reporting endpoints, run against the REAL
/// <c>Cachet.ArchiveSync.Api/Program.cs</c> pipeline via <see cref="ReportsApiWebApplicationFactory"/>
/// (see that class's XML doc). Proves correct verb/route resolution, auth gating (§12 — both POST
/// routes require only <c>CachetAuthenticated</c>), streamed binary response shape (SC-1/SC-2), and
/// that the two former <c>GET /reports/{reportId}/pdf|xlsx</c> retrieval routes are gone outright
/// (SC-4) — the aggregation/mapping logic itself is proven by
/// <c>Cachet.ArchiveSync.Api.Tests.PreActionReportServiceTests</c>/<c>PostActionReportServiceTests</c>.
/// </summary>
public sealed class ReportsControllerTests : IAsyncLifetime
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
    private static readonly PasswordHasher<object> Hasher = new();

    private ReportsApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new ReportsApiWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private void SeedReadOnlyUser(int userId, string username = ValidUsername)
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
        var cookie = setCookieValues!.FirstOrDefault(v => v.StartsWith(ReportsApiWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));
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

    // ---- SC-1/SC-2: POST /reports/pre-action, both formats, streamed directly in the response ------

    [Fact]
    public async Task Test_SC1_PreActionPdf_ReturnsParseablePdfInSameResponse()
    {
        // Arrange
        SeedReadOnlyUser(userId: 1); // report generation is CachetAuthenticated only (§12).
        var cookie = await LoginAsync();
        _factory.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>());
        _factory.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/reports/pre-action", cookie, new ReportGenerationRequest { Format = "pdf" }));

        // Assert — the rendered file comes back directly, in this one response: no reportId, no
        // JSON envelope, no separate GET call (CAS-015 §2/§8).
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Content.Headers.ContentDisposition!.FileName.Should().MatchRegex(@"^""?pre-action-report-\d{14}\.pdf""?$");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(0x25, 0x50, 0x44, 0x46);

        await _factory.AuditLogService.Received(1).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.PreActionReportGenerated && e.UserId == 1 && e.TabModule == "Reports"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC2_PreActionXlsx_ReturnsParseableXlsx()
    {
        // Arrange
        SeedReadOnlyUser(userId: 2);
        var cookie = await LoginAsync();
        _factory.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>());
        _factory.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/reports/pre-action", cookie, new ReportGenerationRequest { Format = "xlsx" }));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        response.Content.Headers.ContentDisposition!.FileName.Should().MatchRegex(@"^""?pre-action-report-\d{14}\.xlsx""?$");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(0x50, 0x4B, 0x03, 0x04);
    }

    [Fact]
    public async Task Test_SC2_PostActionPdfAndXlsx_ReturnParseableBodies()
    {
        // Arrange
        SeedReadOnlyUser(userId: 3);
        var cookie = await LoginAsync();
        _factory.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow>
            {
                new()
                {
                    AuditLogId = 1,
                    Username = ValidUsername,
                    TabModule = "Quarantine",
                    Action = nameof(AuditAction.Deleted),
                    FilePath = "contracts/2019/agreement-004.pdf",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = nameof(AuditResult.Success)
                }
            }, 1));

        // Act — PDF
        using var pdfResponse = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/reports/post-action", cookie, new ReportGenerationRequest { Format = "pdf" }));

        // Act — XLSX
        using var xlsxResponse = await _client.SendAsync(AuthenticatedRequest(
            HttpMethod.Post, "/reports/post-action", cookie, new ReportGenerationRequest { Format = "xlsx" }));

        // Assert
        pdfResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        pdfResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await pdfResponse.Content.ReadAsByteArrayAsync()).Should().Equal(0x25, 0x50, 0x44, 0x46);

        xlsxResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        xlsxResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        (await xlsxResponse.Content.ReadAsByteArrayAsync()).Should().Equal(0x50, 0x4B, 0x03, 0x04);

        await _factory.AuditLogService.Received(2).WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.PostActionReportGenerated && e.UserId == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_PreAction_NoSession_Returns401()
    {
        using var response = await _client.PostAsJsonAsync("/reports/pre-action", new ReportGenerationRequest { Format = "pdf" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Test_PostAction_NoSession_Returns401()
    {
        using var response = await _client.PostAsJsonAsync("/reports/post-action", new ReportGenerationRequest { Format = "pdf" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- CAS-038 SC-1/SC-2/V-5: GET /reports/{pre,post}-action/preview end-to-end, real route + ----
    // auth + response shape, including the Post-Action cap-hit fail-loud path. ------------------------

    [Fact]
    public async Task Test_SC1_PreActionPreview_ReturnsExpectedShape()
    {
        // Arrange
        SeedReadOnlyUser(userId: 5);
        var cookie = await LoginAsync();
        _factory.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>
        {
            new()
            {
                SearchResultId = 1,
                TaskQueueId = 100,
                FileIndexId = 200,
                MatchType = "Filename",
                MatchStatus = "Flagged",
                CreatedAt = DateTimeOffset.UtcNow,
                FileName = "agreement-004.pdf",
                RelativePath = "contracts/2019/agreement-004.pdf",
                FileSizeBytes = 100,
                ModifiedAt = DateTimeOffset.UtcNow
            }
        });
        _factory.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/reports/pre-action/preview", cookie));

        // Assert — CAS-038 §6 response shape: not a downloaded file, no reportId/download URL.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PreActionReportPreview>();
        body.Should().NotBeNull();
        body!.GeneratedBy.Should().Be(ValidUsername);
        body.Rows.Should().ContainSingle();
        body.Rows[0].FileName.Should().Be("agreement-004.pdf");
        body.Rows[0].FilePath.Should().Be("contracts/2019/agreement-004.pdf");
        body.Rows[0].Panel.Should().Be("Filename Matches");
        body.Rows[0].Status.Should().Be("Flagged");

        // §12: preview writes no report-generated audit row (unlike the POST download endpoints
        // above) — scoped to the report actions, not a blanket assertion, since LoginAsync() above
        // legitimately writes its own AuditAction.Login row as part of establishing the session.
        await _factory.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.PreActionReportGenerated || e.Action == AuditAction.PostActionReportGenerated),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SC2_PostActionPreview_ReturnsExpectedShape()
    {
        // Arrange — one file-level row and one task-level row (no associated file).
        SeedReadOnlyUser(userId: 6);
        var cookie = await LoginAsync();
        _factory.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow>
            {
                new()
                {
                    AuditLogId = 1,
                    Username = ValidUsername,
                    TabModule = "Quarantine",
                    Action = nameof(AuditAction.Deleted),
                    FilePath = "contracts/2019/agreement-004.pdf",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = nameof(AuditResult.Success)
                },
                new()
                {
                    AuditLogId = 2,
                    Username = ValidUsername,
                    TabModule = "Keyword Search",
                    Action = nameof(AuditAction.KeywordSearchSubmitted),
                    FilePath = null,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = nameof(AuditResult.Success)
                }
            }, 2));

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/reports/post-action/preview", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PostActionReportPreview>();
        body.Should().NotBeNull();
        body!.GeneratedBy.Should().Be(ValidUsername);
        body.Rows.Should().HaveCount(2);
        body.Rows[0].FileName.Should().Be("agreement-004.pdf");
        body.Rows[0].ActionTaken.Should().Be("Deleted Permanently");
        body.Rows[1].FileName.Should().BeNull("task-level actions have no single associated file");
        body.Rows[1].FilePath.Should().BeNull();
        body.Rows[1].ActionTaken.Should().Be("Searched");

        // §12: scoped to the report actions, not a blanket assertion — LoginAsync() above
        // legitimately writes its own AuditAction.Login row as part of establishing the session.
        await _factory.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.PreActionReportGenerated || e.Action == AuditAction.PostActionReportGenerated),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_V5_PostActionPreviewCapReachedFailsLoud()
    {
        // Arrange — exactly the 100,000-row cap is ambiguous (CAS-038 §7 V5 / CAS-015 V6) and must
        // fail loud rather than silently truncate.
        SeedReadOnlyUser(userId: 7);
        var cookie = await LoginAsync();
        var cappedRows = Enumerable.Range(1, 100_000)
            .Select(i => new AuditLogRow
            {
                AuditLogId = i,
                Username = ValidUsername,
                TabModule = "Quarantine",
                Action = nameof(AuditAction.Deleted),
                FilePath = "contracts/2019/agreement-004.pdf",
                CreatedAt = DateTimeOffset.UtcNow,
                Result = nameof(AuditResult.Success)
            })
            .ToList();
        _factory.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((cappedRows, 100_000));

        // Act
        using var response = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/reports/post-action/preview", cookie));

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://cachet.tracecounts.internal/errors/report-generation-failed");

        // §12: scoped to the report actions, not a blanket assertion — LoginAsync() above
        // legitimately writes its own AuditAction.Login row as part of establishing the session.
        await _factory.AuditLogService.DidNotReceive().WriteAsync(
            Arg.Is<AuditLogEntry>(e => e.Action == AuditAction.PreActionReportGenerated || e.Action == AuditAction.PostActionReportGenerated),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_PreActionPreview_NoSession_Returns401()
    {
        using var response = await _client.GetAsync("/reports/pre-action/preview");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Test_PostActionPreview_NoSession_Returns401()
    {
        using var response = await _client.GetAsync("/reports/post-action/preview");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- CAS-038 SC-5: the JSON these live routes emit deserializes cleanly into the exact shape ---
    // CAS-032's ReportModal.razor (already deployed, unmodified) expects. ReportModalTests.cs +
    // FakeReportsApiHandler already fully prove ReportModal's own rendering against this exact
    // contract shape (written to this spec's proposed contract before the Api existed) — what was
    // missing until this spec landed is proof the real Api emits JSON that shape can actually
    // deserialize into. ReportModalPreActionPreviewShape/ReportModalPostActionPreviewShape below
    // mirror ReportModal.razor's private PreActionPreview/PostActionPreview records field-for-field
    // (Components/Shared/ReportModal.razor) rather than referencing them directly, since this Api-only
    // test project takes no project reference on Cachet.ArchiveSync.Web (a separate Blazor Server
    // project) — see this test-writer's report for the reasoning.
    // -----------------------------------------------------------------------------------------------

    private sealed record ReportModalPreActionPreviewShape
    {
        public DateTimeOffset GeneratedAt { get; init; }

        public string GeneratedBy { get; init; } = string.Empty;

        public IReadOnlyList<PreActionReportRow> Rows { get; init; } = Array.Empty<PreActionReportRow>();
    }

    private sealed record ReportModalPostActionPreviewShape
    {
        public DateTimeOffset GeneratedAt { get; init; }

        public string GeneratedBy { get; init; } = string.Empty;

        public IReadOnlyList<PostActionReportRow> Rows { get; init; } = Array.Empty<PostActionReportRow>();
    }

    [Fact]
    public async Task Test_SC5_ReportModalRendersAgainstLivePreviewEndpoints()
    {
        // Arrange
        SeedReadOnlyUser(userId: 8);
        var cookie = await LoginAsync();
        _factory.SearchResultRepository.ListAllActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<SearchResultRow>
        {
            new()
            {
                SearchResultId = 1,
                TaskQueueId = 100,
                FileIndexId = 200,
                MatchType = "Filename",
                MatchStatus = "Flagged",
                CreatedAt = DateTimeOffset.UtcNow,
                FileName = "agreement-004.pdf",
                RelativePath = "contracts/2019/agreement-004.pdf",
                FileSizeBytes = 100,
                ModifiedAt = DateTimeOffset.UtcNow
            }
        });
        _factory.QuarantineEntryRepository.ListActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<QuarantineEntryListRow>());
        _factory.AuditLogRepository
            .ListAsync(null, null, null, 1, 100_000, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogRow>
            {
                new()
                {
                    AuditLogId = 1,
                    Username = ValidUsername,
                    TabModule = "Quarantine",
                    Action = nameof(AuditAction.Deleted),
                    FilePath = "contracts/2019/agreement-004.pdf",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Result = nameof(AuditResult.Success)
                }
            }, 1));

        // Act
        using var preResponse = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/reports/pre-action/preview", cookie));
        using var postResponse = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/reports/post-action/preview", cookie));

        // Assert — both deserialize cleanly (non-null, populated) into ReportModal's exact response
        // shape, proving the live Api contract matches what the deployed Web component expects.
        preResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preBody = await preResponse.Content.ReadFromJsonAsync<ReportModalPreActionPreviewShape>();
        preBody.Should().NotBeNull();
        preBody!.GeneratedBy.Should().Be(ValidUsername);
        preBody.Rows.Should().ContainSingle();
        preBody.Rows[0].FileName.Should().Be("agreement-004.pdf");
        preBody.Rows[0].Panel.Should().Be("Filename Matches");

        postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var postBody = await postResponse.Content.ReadFromJsonAsync<ReportModalPostActionPreviewShape>();
        postBody.Should().NotBeNull();
        postBody!.GeneratedBy.Should().Be(ValidUsername);
        postBody.Rows.Should().ContainSingle();
        postBody.Rows[0].ActionTaken.Should().Be("Deleted Permanently");
    }

    // ---- SC-4: GET /reports/{reportId}/pdf and /xlsx are removed outright ---------------------------

    [Fact]
    public async Task Test_SC4_RemovedGetRoutesReturnNotFound()
    {
        // Arrange
        SeedReadOnlyUser(userId: 4);
        var cookie = await LoginAsync();

        // Act
        using var pdfResponse = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/reports/1/pdf", cookie));
        using var xlsxResponse = await _client.SendAsync(AuthenticatedRequest(HttpMethod.Get, "/reports/1/xlsx", cookie));

        // Assert — the framework's default "no endpoint matched" 404, not the application's
        // report-not-found ProblemDetails (which no longer exists as a reachable response at all,
        // since no route resolves to a controller action for either of these paths).
        pdfResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        xlsxResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        pdfResponse.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json",
            "a removed route must 404 via ASP.NET Core's default routing fallback, not the application's report-not-found ProblemDetails");
        xlsxResponse.Content.Headers.ContentType?.MediaType.Should().NotBe("application/problem+json",
            "a removed route must 404 via ASP.NET Core's default routing fallback, not the application's report-not-found ProblemDetails");
    }

    [Fact]
    public async Task Test_SC4_RemovedGetRoutes_AlsoNotFoundWithoutSession()
    {
        // A removed route resolves to 404 before any auth middleware would even apply, since there
        // is no endpoint left to attach [Authorize] to.
        using var response = await _client.GetAsync("/reports/1/pdf");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
