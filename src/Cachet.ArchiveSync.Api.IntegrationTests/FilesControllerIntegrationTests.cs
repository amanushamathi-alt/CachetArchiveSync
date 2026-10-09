using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cachet.ArchiveSync.Api.IntegrationTests.Files;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Core.Enums;
using Cachet.ArchiveSync.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cachet.ArchiveSync.Api.IntegrationTests;

/// <summary>
/// CAS-040 §13 — end-to-end HTTP tests for <c>GET /files/{fileIndexId}</c>, <c>/view</c> and
/// <c>/download</c> against the REAL <c>Cachet.ArchiveSync.Api/Program.cs</c> pipeline via
/// <see cref="FilesApiWebApplicationFactory"/> (substituted repositories and audit service, real
/// <c>MirrorFileReader</c> over a temp-dir mirror). Proves verb/route/constraint resolution (V1),
/// context validation (V2), the §6/§7 status codes, 401 without a session, original-byte
/// streaming (SC-2, SC-8), security headers (SC-10) and the response-scan half of SC-12.
/// </summary>
public sealed class FilesControllerIntegrationTests : IAsyncLifetime
{
    private const string ValidUsername = "jdoe";
    private const string ValidPassword = "correct horse battery staple";
    private const int UserId = 41;
    private const string HtmlPreviewCsp =
        "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'; sandbox";

    // B2 values the login fakes hand out — SC-12 scans for them in every file response.
    private const string B2Token = "4_secretB2token_neverLog";
    private const string B2ApiHost = "api-fake.backblazeb2.com";
    private const string B2KeyId = "plain-key-id";
    private const string B2AppKey = "plain-app-key";

    private static readonly PasswordHasher<object> Hasher = new();

    private FilesApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new FilesApiWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<string> LoginAsync()
    {
        var sessionTokenVersion = _factory.RegisterSessionTokenVersion(UserId);
        _factory.UserRepository.GetByUsernameAsync(ValidUsername, Arg.Any<CancellationToken>()).Returns(new UserAccount
        {
            UserId = UserId,
            Username = ValidUsername,
            PasswordHash = Hasher.HashPassword(new object(), ValidPassword),
            B2ApplicationKeyId = "ct:keyid",
            B2ApplicationKey = "ct:key",
            IsActive = true,
            SessionTokenVersion = sessionTokenVersion
        });
        _factory.CredentialProtector.Unprotect("ct:keyid").Returns(B2KeyId);
        _factory.CredentialProtector.Unprotect("ct:key").Returns(B2AppKey);
        _factory.B2KeyAuthorizer
            .AuthorizeAsync(B2KeyId, B2AppKey, Arg.Any<CancellationToken>())
            .Returns(new B2AuthorizationInfo
            {
                AccountId = "acct-1",
                AuthorizationToken = B2Token,
                ApiUrl = "https://" + B2ApiHost,
                DownloadUrl = "https://download-fake.backblazeb2.com",
                Capabilities = new[] { "listFiles", "readFiles" },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
            });

        var response = await _client.PostAsJsonAsync("/login", new LoginRequest { Username = ValidUsername, Password = ValidPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Set-Cookie", out var setCookieValues).Should().BeTrue();
        var cookie = setCookieValues!.First(v => v.StartsWith(FilesApiWebApplicationFactory.CookieName + "=", StringComparison.Ordinal));

        // Only the file endpoints' own audit rows count from here on.
        _factory.AuditLogService.ClearReceivedCalls();
        return cookie.Split(';')[0];
    }

    private Task<HttpResponseMessage> GetAsync(string url, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return _client.SendAsync(request);
    }

    /// <summary>Seeds a <c>dbo.file_index</c> row and its physical mirror file.</summary>
    private byte[] SeedFile(int fileIndexId, string extension, bool isActive = true, byte[]? bytes = null, bool writeFile = true)
    {
        var record = FileViewerHttpFixtures.Record(fileIndexId, extension, isActive);
        bytes ??= FileViewerHttpFixtures.ForExtension(extension);
        _factory.FileIndexRepository.GetAsync(fileIndexId, Arg.Any<CancellationToken>()).Returns(record);
        if (writeFile)
        {
            var full = Path.Combine(_factory.MirrorRoot, record.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }

        return bytes;
    }

    private List<AuditLogEntry> AuditEntries() =>
        _factory.AuditLogService.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IAuditLogService.WriteAsync))
            .Select(c => (AuditLogEntry)c.GetArguments()[0]!)
            .ToList();

    private static async Task<string> ProblemTypeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("type").GetString()!;
    }

    // -----------------------------------------------------------------------------------------
    // V1
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/files/0?context=KeywordSearch")]
    [InlineData("/files/-1?context=KeywordSearch")]
    [InlineData("/files/abc?context=KeywordSearch")]
    [InlineData("/files/0/view?context=KeywordSearch")]
    [InlineData("/files/-7/view?context=KeywordSearch")]
    [InlineData("/files/1.5/view?context=KeywordSearch")]
    [InlineData("/files/0/download?context=KeywordSearch")]
    [InlineData("/files/abc/download?context=KeywordSearch")]
    [InlineData("/files/99999999999/download?context=KeywordSearch")]
    public async Task Test_V1_NonPositiveOrNonIntegerIdIsNotRouted(string url)
    {
        var cookie = await LoginAsync();
        SeedFile(1, "pdf");

        using var response = await GetAsync(url, cookie);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the {fileIndexId:int:min(1)} constraint does not match");
        await _factory.FileIndexRepository.DidNotReceiveWithAnyArgs().GetAsync(default, default);
        AuditEntries().Should().BeEmpty("V1 is not audited");

        // Control: a valid id on the same verb routes (GET, 200) and returns the §6.1 body with no path.
        using var control = await GetAsync("/files/1?context=KeywordSearch", cookie);
        control.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await control.Content.ReadAsStringAsync();
        var metadata = JsonSerializer.Deserialize<FileMetadataResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        metadata.FileIndexId.Should().Be(1);
        metadata.ViewMode.Should().Be(FileViewModes.InlinePdf);
        metadata.Source.Should().Be("ODrive");
        body.Should().NotContain("contracts").And.NotContain(_factory.MirrorRoot.Replace("\\", "\\\\"));
        AuditEntries().Should().BeEmpty("the metadata endpoint is not audited (§6.1)");

        // No session -> 401 on all three endpoints (never a redirect).
        foreach (var endpoint in new[] { "/files/1?context=KeywordSearch", "/files/1/view?context=KeywordSearch", "/files/1/download?context=KeywordSearch" })
        {
            using var unauthenticated = await GetAsync(endpoint, cookie: null);
            unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized, endpoint);
        }

        // A non-GET verb on the same route does not resolve to these endpoints.
        using var post = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/files/1/view?context=KeywordSearch")
        {
            Headers = { { "Cookie", cookie } }
        });
        post.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    // -----------------------------------------------------------------------------------------
    // V2
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", null)]
    [InlineData("/view", null)]
    [InlineData("/download", null)]
    [InlineData("", "keywordsearch")]
    [InlineData("/view", "KEYWORDSEARCH")]
    [InlineData("/download", "Reports")]
    [InlineData("/view", "")]
    public async Task Test_V2_MissingOrUnknownContextRejected(string suffix, string? context)
    {
        var cookie = await LoginAsync();
        SeedFile(2, "pdf");
        var url = $"/files/2{suffix}" + (context is null ? string.Empty : $"?context={Uri.EscapeDataString(context)}");

        using var response = await GetAsync(url, cookie);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemTypeAsync(response)).Should().Be("https://cachet.tracecounts.internal/errors/validation-failed");
        AuditEntries().Should().BeEmpty("V2 is not audited — the tab module is unknown");
        await _factory.FileIndexRepository.DidNotReceiveWithAnyArgs().GetAsync(default, default);
    }

    // -----------------------------------------------------------------------------------------
    // V3–V10 — HTTP status / ProblemDetails mapping (the service-level audit rows are proven in
    // Cachet.ArchiveSync.Api.Tests.FileContentServiceTests / OfficePreviewRendererTests)
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("/view")]
    [InlineData("/download")]
    public async Task Test_V3_UnknownFileIndexIdReturns404AndAuditsFailure(string suffix)
    {
        var cookie = await LoginAsync();
        _factory.FileIndexRepository.GetAsync(3, Arg.Any<CancellationToken>()).Returns((FileIndexRecord?)null);

        using var response = await GetAsync($"/files/3{suffix}?context=KeywordSearch", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemTypeAsync(response)).Should().EndWith("/file-not-found");
        if (suffix == string.Empty)
        {
            AuditEntries().Should().BeEmpty("the metadata endpoint is not audited (§6.1)");
            return;
        }

        AuditEntries().Should().ContainSingle(e => e.Result == AuditResult.Failure && e.UserId == UserId && e.FilePath == null);
    }

    [Fact]
    public async Task Test_V4_InactiveRowReturns410AndAuditsFailure()
    {
        var cookie = await LoginAsync();
        SeedFile(4, "pdf", isActive: false);

        using var response = await GetAsync("/files/4/download?context=Quarantine", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ProblemTypeAsync(response)).Should().EndWith("/file-no-longer-available");
        AuditEntries().Should().ContainSingle(e => e.Action == AuditAction.FileDownloaded && e.Result == AuditResult.Failure && e.TabModule == "Quarantine");
    }

    [Fact]
    public async Task Test_V5_OutOfScopeExtensionReturns415()
    {
        var cookie = await LoginAsync();
        SeedFile(5, "png");

        using var response = await GetAsync("/files/5?context=KeywordSearch", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await ProblemTypeAsync(response)).Should().EndWith("/file-type-not-supported");
    }

    [Fact]
    public async Task Test_V6_MissingBucketMappingReturns500WithoutPath()
    {
        var cookie = await LoginAsync();
        SeedFile(6, "pdf");
        _factory.BucketMirrorRootRepository.GetRootPathAsync(FilesApiWebApplicationFactory.Bucket, "ODrive", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("No active dbo.odrive_bucket_mapping row."));

        using var response = await GetAsync("/files/6/view?context=KeywordSearch", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadAsStringAsync();
        (await ProblemTypeAsync(response)).Should().EndWith("/mirror-mapping-missing");
        body.Should().NotContain("contracts").And.NotContain(":\\\\", "the message contains no path");
        AuditEntries().Should().ContainSingle(e => e.ResultDetail == "fileIndexId=6; reason=MappingMissing");
    }

    [Fact]
    public async Task Test_V7_PathEscapingMirrorRootIsRejected()
    {
        var cookie = await LoginAsync();
        var record = FileViewerHttpFixtures.Record(7, "txt") with { RelativePath = @"..\..\Windows\win.ini" };
        _factory.FileIndexRepository.GetAsync(7, Arg.Any<CancellationToken>()).Returns(record);

        using var response = await GetAsync("/files/7/download?context=KeywordSearch", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ProblemTypeAsync(response)).Should().EndWith("/file-path-rejected");
        (await response.Content.ReadAsStringAsync()).Should().NotContainEquivalentOf("win.ini");
        AuditEntries().Should().ContainSingle(e => e.ResultDetail == "fileIndexId=7; reason=PathRejected");
    }

    [Fact]
    public async Task Test_V8_FileAbsentFromMirrorReturns404AndAuditsFailure()
    {
        var cookie = await LoginAsync();
        SeedFile(8, "pdf", writeFile: false);

        using var response = await GetAsync("/files/8/view?context=ODriveSync", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemTypeAsync(response)).Should().EndWith("/file-missing-from-mirror");
        AuditEntries().Should().ContainSingle(e => e.ResultDetail == "fileIndexId=8; reason=MissingFromMirror" && e.TabModule == "O-Drive Sync");
    }

    [Fact]
    public async Task Test_V9_PreviewRefusedForOversizeMismatchOrFailedConversion()
    {
        var cookie = await LoginAsync();
        var html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        SeedFile(9, "pdf", bytes: html);

        using var response = await GetAsync("/files/9/view?context=KeywordSearch", cookie);

        response.StatusCode.Should().Be((HttpStatusCode)422);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("type").GetString().Should().EndWith("/preview-not-available");
        json.RootElement.GetProperty("reason").GetString().Should().Be("SignatureMismatch");

        // /download is unaffected: the original bytes are served.
        using var download = await GetAsync("/files/9/download?context=KeywordSearch", cookie);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(html);
    }

    [Theory]
    [InlineData("/view")]
    [InlineData("/download")]
    public async Task Test_V10_AuditWriteFailureServesNoContent(string suffix)
    {
        var cookie = await LoginAsync();
        var bytes = SeedFile(10, "pdf");
        _factory.AuditLogService.WriteAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("audit store down"));

        using var response = await GetAsync($"/files/10{suffix}?context=KeywordSearch", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ProblemTypeAsync(response)).Should().EndWith("/audit-write-failed");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Content.Headers.ContentDisposition.Should().BeNull("no file content is sent");
        Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync()).Should().NotContain(Encoding.Latin1.GetString(bytes, 0, 5));
    }

    // -----------------------------------------------------------------------------------------
    // SC-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC2_PdfViewStreamsInlineOriginalBytes()
    {
        var cookie = await LoginAsync();
        var bytes = SeedFile(20, "pdf");

        using var response = await GetAsync("/files/20/view?context=KeywordSearch", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().Should().Be("application/pdf");
        var disposition = response.Content.Headers.ContentDisposition!;
        disposition.DispositionType.Should().Be("inline");
        disposition.FileName.Should().Be("\"file-20.pdf\"");
        response.Content.Headers.GetValues("Content-Disposition").Single().Should().Contain("filename*=UTF-8''file-20.pdf");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes, "InlinePdf streams the original bytes");
        AuditEntries().Should().ContainSingle(e => e.Action == AuditAction.FileViewed && e.Result == AuditResult.Success);

        using var unauthenticated = await GetAsync("/files/20/view?context=KeywordSearch", cookie: null);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------------------------
    // SC-8
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pdf")]
    [InlineData("txt")]
    [InlineData("doc")]
    [InlineData("docx")]
    [InlineData("xls")]
    [InlineData("xlsx")]
    public async Task Test_SC8_DownloadReturnsUnalteredOriginalPerType(string extension)
    {
        var cookie = await LoginAsync();
        var bytes = SeedFile(30, extension);

        using var response = await GetAsync("/files/30/download?context=ODriveSync", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes, "download returns the original bytes, unchanged");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName.Should().Be($"\"file-30.{extension}\"");
        response.Content.Headers.ContentType!.ToString().Should().Be(FileViewerHttpFixtures.DownloadContentType(extension));
        response.Content.Headers.ContentLength.Should().Be(bytes.Length);
        response.Headers.Contains("Accept-Ranges").Should().BeFalse("range processing is disabled (§4)");
        response.Headers.ETag.Should().BeNull();
        response.Content.Headers.LastModified.Should().BeNull();
        AuditEntries().Should().ContainSingle(e => e.Action == AuditAction.FileDownloaded && e.Result == AuditResult.Success
            && e.TabModule == "O-Drive Sync" && e.FilePath == $"contracts/2026/file-30.{extension}");

        using var unauthenticated = await GetAsync("/files/30/download?context=ODriveSync", cookie: null);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------------------------------
    // SC-10
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("pdf", "view", FileViewModes.InlinePdf)]
    [InlineData("txt", "view", FileViewModes.InlineText)]
    [InlineData("doc", "view", FileViewModes.ExtractedText)]
    [InlineData("docx", "view", FileViewModes.HtmlPreview)]
    [InlineData("xls", "view", FileViewModes.HtmlPreview)]
    [InlineData("xlsx", "view", FileViewModes.HtmlPreview)]
    [InlineData("pdf", "download", null)]
    [InlineData("xlsx", "download", null)]
    public async Task Test_SC10_SecurityHeadersPresentOnEveryFileResponse(string extension, string operation, string? viewMode)
    {
        var cookie = await LoginAsync();
        SeedFile(40, extension);

        using var response = await GetAsync($"/files/40/{operation}?context=KeywordSearch", cookie);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.CacheControl.NoCache.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.Zero);
        response.Headers.GetValues("Pragma").Single().Should().Be("no-cache");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("SAMEORIGIN");

        response.Headers.TryGetValues("Content-Security-Policy", out var csp);
        response.Headers.TryGetValues("X-Cachet-Preview-Truncated", out var truncated);
        response.Headers.TryGetValues("X-Cachet-Source-Encoding", out var sourceEncoding);

        switch (viewMode)
        {
            case FileViewModes.HtmlPreview:
                csp!.Single().Should().Be(HtmlPreviewCsp, "the exact §6.2 CSP, including sandbox");
                response.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
                truncated!.Single().Should().Be("false");
                sourceEncoding.Should().BeNull();
                break;
            case FileViewModes.InlinePdf:
                csp!.Single().Should().Be("frame-ancestors 'self'");
                truncated.Should().BeNull();
                break;
            case FileViewModes.InlineText:
                csp!.Single().Should().Be("frame-ancestors 'self'");
                response.Content.Headers.ContentType!.ToString().Should().Be("text/plain; charset=utf-8");
                truncated!.Single().Should().Be("false");
                sourceEncoding!.Single().Should().Be("utf-8");
                break;
            case FileViewModes.ExtractedText:
                csp!.Single().Should().Be("frame-ancestors 'self'");
                response.Content.Headers.ContentType!.ToString().Should().Be("text/plain; charset=utf-8");
                truncated!.Single().Should().Be("false");
                sourceEncoding.Should().BeNull();
                break;
            default: // download
                csp.Should().BeNull("download carries no CSP (§6.3)");
                truncated.Should().BeNull();
                sourceEncoding.Should().BeNull();
                break;
        }
    }

    // -----------------------------------------------------------------------------------------
    // SC-12 (response scan — the constructor-reflection half is in
    // Cachet.ArchiveSync.Api.Tests.FileSecurityArchitectureTests)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC12_NoB2DependencyOrCredentialOnFilePath()
    {
        var cookie = await LoginAsync();
        SeedFile(50, "docx");

        var forbidden = new[] { B2Token, B2ApiHost, "backblazeb2", B2KeyId, B2AppKey, "ct:keyid", "ct:key" };
        foreach (var url in new[] { "/files/50?context=KeywordSearch", "/files/50/view?context=KeywordSearch", "/files/50/download?context=KeywordSearch" })
        {
            using var response = await GetAsync(url, cookie);
            response.StatusCode.Should().Be(HttpStatusCode.OK, url);

            var headers = string.Join("\n", response.Headers.Concat(response.Content.Headers)
                .Select(h => h.Key + ": " + string.Join(",", h.Value)));
            var body = Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync());

            response.Headers.Contains("Authorization").Should().BeFalse(url);
            foreach (var value in forbidden)
            {
                headers.Should().NotContain(value, $"{url} headers must carry no B2 host, token or key id");
                body.Should().NotContain(value, $"{url} body must carry no B2 host, token or key id");
            }
        }

        await _factory.B2KeyAuthorizer.Received(1).AuthorizeAsync(B2KeyId, B2AppKey, Arg.Any<CancellationToken>());
    }
}
