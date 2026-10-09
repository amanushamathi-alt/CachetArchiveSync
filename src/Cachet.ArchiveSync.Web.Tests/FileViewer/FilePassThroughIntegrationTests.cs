using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Cachet.ArchiveSync.Core.Interfaces;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Cachet.ArchiveSync.Web.Tests.FileViewer;

/// <summary>
/// CAS-040 §13 — the Web <c>Program.cs</c> §8.5 pass-through endpoints
/// (<c>GET /files/{fileIndexId}/view</c> and <c>/download</c>) against the REAL Web pipeline via
/// <see cref="LoginWebApplicationFactory"/>, with <see cref="FakeCachetApiHandler"/> standing in for
/// Api. Proves SC-17 (streamed body, header allowlist, forced security headers, fixed error bodies),
/// SC-18 (no session → redirect to /login, Api never called) and the Web half of SC-12.
/// </summary>
public sealed class FilePassThroughIntegrationTests : IAsyncLifetime
{
    private const int UserId = 801;
    private const string HtmlPreviewCsp =
        "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'; sandbox";

    private LoginWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new LoginWebApplicationFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private string SessionCookie()
    {
        _factory.RegisterSessionTokenVersion(UserId, 0);
        return _factory.CreateSessionSetCookieHeader(UserId, "0", "Read Only").Split(';')[0];
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string? cookie)
    {
        using var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return await client.SendAsync(request);
    }

    private static HttpResponseMessage ApiFileResponse(byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileName = "\"report.docx\"" };
        response.Content.Headers.ContentLength = body.Length;
        response.Headers.TryAddWithoutValidation("Content-Security-Policy", HtmlPreviewCsp);
        response.Headers.TryAddWithoutValidation("X-Cachet-Preview-Truncated", "true");
        response.Headers.TryAddWithoutValidation("X-Cachet-Source-Encoding", "utf-16le");
        // Headers that must NOT be forwarded.
        response.Headers.TryAddWithoutValidation("Set-Cookie", "CachetAuth=api-issued-cookie; path=/");
        response.Headers.TryAddWithoutValidation("X-Internal-Debug", "api-internal-value");
        response.Headers.TryAddWithoutValidation("Cache-Control", "public, max-age=3600");
        response.Headers.TryAddWithoutValidation("X-Frame-Options", "ALLOWALL");
        return response;
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.CacheControl.NoCache.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.Zero);
        response.Headers.CacheControl.Public.Should().BeFalse("Api's Cache-Control is never forwarded");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("SAMEORIGIN");
    }

    // -----------------------------------------------------------------------------------------
    // SC-17
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("view", HttpStatusCode.OK)]
    [InlineData("download", HttpStatusCode.OK)]
    // Non-2xx: same status, fixed static body, Api's ProblemDetails never forwarded.
    [InlineData("view", HttpStatusCode.NotFound)]
    [InlineData("view", HttpStatusCode.Gone)]
    [InlineData("view", (HttpStatusCode)422)]
    [InlineData("download", HttpStatusCode.InternalServerError)]
    [InlineData("download", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("view", HttpStatusCode.BadGateway)] // Api unreachable
    [InlineData("download", HttpStatusCode.BadGateway)] // Api unreachable
    public async Task Test_SC17_WebPassThroughStreamsAndFiltersHeaders(string operation, HttpStatusCode apiStatus)
    {
        if (apiStatus != HttpStatusCode.OK)
        {
            await AssertNonSuccessPassThroughAsync(operation, apiStatus);
            return;
        }

        var cookie = SessionCookie();
        var body = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        _factory.ApiHandler.OnGetFile = _ => Task.FromResult(ApiFileResponse(body));

        using var response = await GetAsync($"/files/77/{operation}?context=ODriveSync", cookie);

        // Body streamed through, byte-identical.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(body);

        // context forwarded unchanged to the matching Api route, through the cookie-forwarding client.
        var apiRequest = _factory.ApiHandler.Requests.Should().ContainSingle().Subject;
        apiRequest.Method.Should().Be(HttpMethod.Get);
        apiRequest.RequestUri!.AbsolutePath.Should().EndWith($"/files/77/{operation}");
        apiRequest.RequestUri.Query.Should().Be("?context=ODriveSync");

        // Allowlisted headers forwarded.
        response.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
        response.Content.Headers.ContentDisposition.FileName.Should().Be("\"report.docx\"");
        response.Content.Headers.ContentLength.Should().Be(body.Length);
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Be(HtmlPreviewCsp);
        response.Headers.GetValues("X-Cachet-Preview-Truncated").Single().Should().Be("true");
        response.Headers.GetValues("X-Cachet-Source-Encoding").Single().Should().Be("utf-16le");

        // Never forwarded.
        response.Headers.Contains("Set-Cookie").Should().BeFalse("Api's Set-Cookie is never forwarded (§8.5)");
        response.Headers.Contains("X-Internal-Debug").Should().BeFalse("only the allowlisted headers are forwarded");

        // Always set, whatever Api sent.
        AssertSecurityHeaders(response);
    }

    private async Task AssertNonSuccessPassThroughAsync(string operation, HttpStatusCode apiStatus)
    {
        var cookie = SessionCookie();
        const string ProblemMarker = "problem-detail-must-not-leak <script>";
        _factory.ApiHandler.OnGetFile = _ =>
        {
            if (apiStatus == HttpStatusCode.BadGateway)
            {
                throw new HttpRequestException("connection refused");
            }

            var problem = new HttpResponseMessage(apiStatus)
            {
                Content = new StringContent($"{{\"type\":\"x\",\"title\":\"{ProblemMarker}\"}}", Encoding.UTF8, "application/problem+json")
            };
            problem.Headers.TryAddWithoutValidation("Set-Cookie", "CachetAuth=api-issued-cookie; path=/");
            return Task.FromResult(problem);
        };

        using var response = await GetAsync($"/files/78/{operation}", cookie);

        response.StatusCode.Should().Be(apiStatus, "the Api status is preserved (502 when Api is unreachable)");
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain(ProblemMarker, "Api's ProblemDetails body is never forwarded (§8.5)");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        AssertSecurityHeaders(response);

        if (operation == "view")
        {
            response.Content.Headers.ContentType!.ToString().Should().Be("text/html; charset=utf-8");
            response.Headers.GetValues("Content-Security-Policy").Single().Should().Be(HtmlPreviewCsp);
            text.Should().Contain("This preview could not be displayed.");
        }
        else
        {
            response.Content.Headers.ContentType!.ToString().Should().Be("text/plain; charset=utf-8");
            text.Should().Be("The file could not be downloaded.");
        }

        // No context on the incoming request -> none forwarded.
        _factory.ApiHandler.Requests.Should().ContainSingle().Which.RequestUri!.Query.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // SC-18
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/files/77/view?context=KeywordSearch")]
    [InlineData("/files/77/download?context=Quarantine")]
    public async Task Test_SC18_WebPassThroughRequiresSession(string url)
    {
        _factory.ApiHandler.OnGetFile = _ => Task.FromResult(ApiFileResponse(new byte[] { 1, 2, 3 }));

        using var response = await GetAsync(url, cookie: null);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/login");
        _factory.ApiHandler.Requests.Should().BeEmpty("an unauthenticated request never reaches Api");

        // A revoked session (stale session_ver) is treated the same way.
        _factory.RegisterSessionTokenVersion(UserId, 5);
        var staleCookie = _factory.CreateSessionSetCookieHeader(UserId, "0", "Full Access").Split(';')[0];
        using var stale = await GetAsync(url, staleCookie);
        stale.StatusCode.Should().Be(HttpStatusCode.Redirect);
        stale.Headers.Location!.ToString().Should().Contain("/login");
        _factory.ApiHandler.Requests.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // SC-12 (Web §8.5 endpoints — the Api halves are in Api.Tests.FileSecurityArchitectureTests and
    // Api.IntegrationTests.FilesControllerIntegrationTests)
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC12_NoB2DependencyOrCredentialOnFilePath()
    {
        var forbidden = new[] { typeof(IB2Client), typeof(IUserScopedB2DeleteClient), typeof(ICredentialProtector) };

        _ = _factory.Server;
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/files/", StringComparison.Ordinal) == true)
            .ToList();

        endpoints.Select(e => e.RoutePattern.RawText).Should().BeEquivalentTo(
            "/files/{fileIndexId:int:min(1)}/view", "/files/{fileIndexId:int:min(1)}/download");

        foreach (var endpoint in endpoints)
        {
            var handler = endpoint.Metadata.GetMetadata<MethodInfo>();
            handler.Should().NotBeNull("minimal-API endpoints carry their handler MethodInfo");
            handler!.GetParameters().Select(p => p.ParameterType)
                .Should().NotContain(t => forbidden.Any(f => f.IsAssignableFrom(t)),
                    $"{endpoint.RoutePattern.RawText} must take no B2 client or credential dependency");
        }

        // Response scan: nothing B2-related or credential-like leaves the pass-through.
        var cookie = SessionCookie();
        _factory.ApiHandler.OnGetFile = _ => Task.FromResult(ApiFileResponse(Encoding.UTF8.GetBytes("<p>preview</p>")));
        using var response = await GetAsync("/files/79/view?context=KeywordSearch", cookie);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var headers = string.Join("\n", response.Headers.Concat(response.Content.Headers).Select(h => h.Key + ": " + string.Join(",", h.Value)));
        var body = await response.Content.ReadAsStringAsync();
        foreach (var scan in new[] { headers, body })
        {
            scan.Should().NotContainEquivalentOf("backblaze");
            scan.Should().NotContain("api-issued-cookie");
        }

        response.Headers.Contains("Authorization").Should().BeFalse();
    }
}
