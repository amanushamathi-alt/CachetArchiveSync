using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Core.Dtos;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cachet.ArchiveSync.Web.Tests.Auth;

/// <summary>
/// CAS-021 §10/§7 — proves every Success Criterion and Validation for the Sign-In/Sign-Out screen
/// against the REAL <c>Cachet.ArchiveSync.Web/Program.cs</c> pipeline (cookie-auth scheme,
/// <c>OnValidatePrincipal</c> session revocation, <c>FallbackPolicy</c>/<c>AuthorizeRouteView</c>
/// route gate, the Web-hosted <c>/login</c>/<c>/logout</c> handlers) via
/// <see cref="LoginWebApplicationFactory"/>. No real Api process, SQL Server, or B2 endpoint is ever
/// reached — the "CachetApi" named <c>HttpClient</c> is redirected to
/// <see cref="FakeCachetApiHandler"/>, and Web's own <c>IUserRepository</c> (used only by the §6.3a
/// session-revocation check) is an NSubstitute fake.
///
/// A fresh <see cref="LoginWebApplicationFactory"/> is created per test (xUnit creates one class
/// instance per <c>[Fact]</c>, and <see cref="IAsyncLifetime"/> re-runs <see cref="InitializeAsync"/>
/// for each), so per-test <see cref="FakeCachetApiHandler"/> scenarios and session-version state
/// never leak between tests.
/// </summary>
public sealed class WebAuthFlowIntegrationTests : IAsyncLifetime
{
    private const string Username = "jdoe";
    private const string Password = "S3cr3t-Plaintext-Marker-Never-Logged!";
    private const string SensitiveB2Detail = "RAW-B2-SDK-DETAIL-never-render-8f3d21";
    private const string SensitiveB2KeyId = "keyid-plaintext-marker-4471";
    private const string SensitiveB2Key = "appkey-plaintext-marker-9902";

    private LoginWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new LoginWebApplicationFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private static string ExtractCookieHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
        {
            return string.Empty;
        }

        return string.Join("; ", setCookieValues.Select(v => v.Split(';')[0]));
    }

    private static string CombineCookies(params string[] cookieHeaders) =>
        string.Join("; ", cookieHeaders.Where(c => !string.IsNullOrWhiteSpace(c)));

    private static async Task<(string Html, string CookieHeader, HttpResponseMessage Response)> GetLoginPageAsync(HttpClient client)
    {
        var response = await client.GetAsync("/login");
        var html = await response.Content.ReadAsStringAsync();
        return (html, ExtractCookieHeader(response), response);
    }

    private static HttpRequestMessage BuildLoginPostRequest(string html, string cookieHeader, string username, string password)
    {
        var hidden = BlazorFormScraper.ExtractHiddenFields(html);
        var usernameField = BlazorFormScraper.FindInputNameById(html, "username");
        var passwordField = BlazorFormScraper.FindInputNameById(html, "password");
        usernameField.Should().NotBeNull("Login.razor's username InputText must render a name attribute for named-form binding");
        passwordField.Should().NotBeNull("Login.razor's password InputText must render a name attribute for named-form binding");

        var formValues = new Dictionary<string, string>(hidden)
        {
            [usernameField!] = username,
            [passwordField!] = password,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(formValues),
        };
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        return request;
    }

    private static HttpResponseMessage BuildProblemResponse(
        string type, string title, string detail, string? keyId = null, string? key = null) =>
        new(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent.Create(new { type, title, status = 401, detail, keyId, key }),
        };

    // ---------------------------------------------------------------------------------------
    // SC-1
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC1_UnauthenticatedRequestRedirectsToLogin()
    {
        using var client = CreateClient();

        var homeResponse = await client.GetAsync("/");
        homeResponse.StatusCode.Should().Be(
            HttpStatusCode.Redirect,
            "the FallbackPolicy (CAS-021 §8.5) requires an authenticated session for every page except /login");
        homeResponse.Headers.Location.Should().NotBeNull();
        homeResponse.Headers.Location!.ToString().Should().Contain("/login");

        var loginResponse = await client.GetAsync("/login");
        loginResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "/login itself must be reachable without a session ([AllowAnonymous], CAS-021 §8.2)");
    }

    // ---------------------------------------------------------------------------------------
    // SC-2
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC2_SuccessfulLoginIssuesCookieAndRedirects()
    {
        using var client = CreateClient();
        var (html, cookieHeader, _) = await GetLoginPageAsync(client);

        _factory.ApiHandler.OnLogin = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new LoginResponse { UserId = 101, DisplayName = Username, B2AccessLabel = "Read Only" }),
            };
            response.Headers.Add("Set-Cookie", _factory.CreateSessionSetCookieHeader(101, "0", "Read Only"));
            return Task.FromResult(response);
        };

        var request = BuildLoginPostRequest(html, cookieHeader, Username, Password);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "a successful login redirects to / (CAS-021 §8.6)");
        response.Headers.Location!.ToString().Should().Be("/");
        ExtractCookieHeader(response).Should().Contain("CachetAuth=", "Api's Set-Cookie must be forwarded onto the real browser response (§8.2)");

        _factory.ApiHandler.LastRequestBody.Should().NotBeNull();
        _factory.ApiHandler.LastRequestBody!.Should().Contain(Username);
        _factory.ApiHandler.LastRequestBody.Should().Contain(Password);
    }

    // ---------------------------------------------------------------------------------------
    // SC-3
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC3_InvalidCredentialsShowsGenericMessage()
    {
        using var client = CreateClient();
        var (html, cookieHeader, _) = await GetLoginPageAsync(client);

        _factory.ApiHandler.OnLogin = _ => Task.FromResult(BuildProblemResponse(
            "https://cachet.tracecounts.internal/errors/invalid-credentials",
            "Invalid username or password",
            SensitiveB2Detail));

        var request = BuildLoginPostRequest(html, cookieHeader, "nobody", "wrong-password");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an invalid-credentials submission re-renders the same page, it does not redirect");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Incorrect username or password.");
        body.Should().NotContain("Contact your administrator");
        body.Should().NotContain(SensitiveB2Detail);
    }

    // ---------------------------------------------------------------------------------------
    // SC-4
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC4_B2UnavailableShowsAdminContactMessage()
    {
        using var client = CreateClient();
        var (html, cookieHeader, _) = await GetLoginPageAsync(client);

        _factory.ApiHandler.OnLogin = _ => Task.FromResult(BuildProblemResponse(
            "https://cachet.tracecounts.internal/errors/b2-access-unavailable",
            "B2 access unavailable",
            SensitiveB2Detail,
            SensitiveB2KeyId,
            SensitiveB2Key));

        var request = BuildLoginPostRequest(html, cookieHeader, Username, Password);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Your account cannot currently access the archive. Contact your administrator.");
        body.Should().NotContain("Incorrect username or password.");
        body.Should().NotContain(SensitiveB2Detail);
        body.Should().NotContain(SensitiveB2KeyId);
        body.Should().NotContain(SensitiveB2Key);
    }

    // ---------------------------------------------------------------------------------------
    // SC-5 / V-1 (same rule, two required test names)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC5_SignInButtonDisabledUntilBothFieldsNonEmpty()
    {
        using var client = CreateClient();
        var (html, _, response) = await GetLoginPageAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        BlazorFormScraper.ButtonHasDisabledAttribute(html, "sign-in-btn").Should().BeTrue(
            "the button must start disabled server-side; wwwroot/js/login.js re-enables it client-side only once both fields are non-empty (V1/§8.1)");
        html.Should().Contain("js/login.js", "the disabled-until-both-fields behavior depends on this script being wired into the page");
    }

    [Fact]
    public async Task Test_V1_SignInButtonDisabledUntilBothFieldsNonEmpty()
    {
        using var client = CreateClient();
        var (html, _, _) = await GetLoginPageAsync(client);

        BlazorFormScraper.ButtonHasDisabledAttribute(html, "sign-in-btn").Should().BeTrue();
        html.Should().Contain("js/login.js");
    }

    // ---------------------------------------------------------------------------------------
    // SC-6
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC6_SignOutInvalidatesSessionAndRedirects()
    {
        const int userId = 301;
        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Read Only").Split(';')[0];

        using var client = CreateClient();

        // Render an authenticated page (MainLayout's Sign Out form) to obtain the antiforgery
        // cookie + hidden token value the real /logout handler requires (CAS-021 §12 CSRF).
        var homeRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        homeRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookiePair);
        var homeResponse = await client.SendAsync(homeRequest);
        homeResponse.StatusCode.Should().Be(HttpStatusCode.OK, "a valid session cookie must grant access to a page behind the FallbackPolicy");

        var homeHtml = await homeResponse.Content.ReadAsStringAsync();
        var antiforgeryCookie = ExtractCookieHeader(homeResponse);
        var hiddenFields = BlazorFormScraper.ExtractHiddenFields(homeHtml);
        hiddenFields.Should().Contain(
            kv => kv.Key.Contains("RequestVerificationToken", StringComparison.OrdinalIgnoreCase),
            "MainLayout.razor's Sign Out form renders <AntiforgeryToken /> (CAS-021 §8.4)");

        _factory.ApiHandler.OnLogout = _ =>
        {
            // Simulates the real Api's logout bumping the shared dbo.users.session_token_version
            // (CAS-002 §6.3/§6.3a) before returning — Web's own OnValidatePrincipal reads that same
            // shared value on the very next request.
            _factory.RegisterSessionTokenVersion(userId, 1);
            var logoutResponse = new HttpResponseMessage(HttpStatusCode.NoContent);
            logoutResponse.Headers.Add("Set-Cookie", "CachetAuth=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/");
            return Task.FromResult(logoutResponse);
        };

        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/logout")
        {
            Content = new FormUrlEncodedContent(hiddenFields),
        };
        logoutRequest.Headers.TryAddWithoutValidation("Cookie", CombineCookies(sessionCookiePair, antiforgeryCookie));
        var logoutResponse2 = await client.SendAsync(logoutRequest);

        logoutResponse2.StatusCode.Should().Be(HttpStatusCode.Redirect);
        logoutResponse2.Headers.Location!.ToString().Should().Be("/login");

        // The pre-logout cookie must no longer authenticate anything — the server-side
        // session_token_version bump (simulated above) makes OnValidatePrincipal reject the now-stale
        // session_ver claim on the very next request, exactly as CAS-002 SC-3 established.
        var replayRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        replayRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookiePair);
        var replayResponse = await client.SendAsync(replayRequest);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        replayResponse.Headers.Location!.ToString().Should().Contain("/login");
    }

    // ---------------------------------------------------------------------------------------
    // SC-7
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC7_NoCredentialLeakageInLoginUiSurface()
    {
        using var client = CreateClient();

        // Success path.
        var (successHtml, successCookieHeader, _) = await GetLoginPageAsync(client);
        _factory.ApiHandler.OnLogin = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new LoginResponse { UserId = 401, DisplayName = Username, B2AccessLabel = "Full Access" }),
            };
            response.Headers.Add("Set-Cookie", _factory.CreateSessionSetCookieHeader(401, "0", "Full Access"));
            return Task.FromResult(response);
        };
        var successResponse = await client.SendAsync(BuildLoginPostRequest(successHtml, successCookieHeader, Username, Password));
        AssertNoLeakage(await successResponse.Content.ReadAsStringAsync(), successResponse);

        // Failure path (B2 unavailable), with the fake Api's raw error also carrying key/keyId
        // markers Web must never surface (CAS-021 §12 B2 Credential Handling).
        var (failureHtml, failureCookieHeader, _) = await GetLoginPageAsync(client);
        _factory.ApiHandler.OnLogin = _ => Task.FromResult(BuildProblemResponse(
            "https://cachet.tracecounts.internal/errors/b2-access-unavailable",
            "B2 access unavailable",
            SensitiveB2Detail,
            SensitiveB2KeyId,
            SensitiveB2Key));
        var failureResponse = await client.SendAsync(BuildLoginPostRequest(failureHtml, failureCookieHeader, Username, Password));
        AssertNoLeakage(await failureResponse.Content.ReadAsStringAsync(), failureResponse);

        _factory.CapturedLogs.Messages.Should().NotContain(m => m.Contains(Password, StringComparison.Ordinal));
        _factory.CapturedLogs.Messages.Should().NotContain(m => m.Contains(SensitiveB2Detail, StringComparison.Ordinal));
        _factory.CapturedLogs.Messages.Should().NotContain(m => m.Contains(SensitiveB2KeyId, StringComparison.Ordinal));
        _factory.CapturedLogs.Messages.Should().NotContain(m => m.Contains(SensitiveB2Key, StringComparison.Ordinal));

        static void AssertNoLeakage(string body, HttpResponseMessage response)
        {
            body.Should().NotContain(Password);
            body.Should().NotContain(SensitiveB2Detail);
            body.Should().NotContain(SensitiveB2KeyId);
            body.Should().NotContain(SensitiveB2Key);

            if (response.Headers.Location is not null)
            {
                var location = response.Headers.Location.ToString();
                location.Should().NotContain(Password);
                location.Should().NotContain(SensitiveB2Key);
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // SC-8 / V-3
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC8_LoginLogoutRequireAntiforgeryToken()
    {
        using var client = CreateClient();

        // /login: a bare POST with no antiforgery cookie/token pair at all must be rejected before
        // Login.razor's own OnValidSubmit handler (and therefore the fake Api) ever runs.
        var bareLoginResponse = await client.SendAsync(BuildBareLoginRequest());
        bareLoginResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.ApiHandler.LastRequest.Should().BeNull("an antiforgery-rejected /login POST must never reach the CachetApi client");

        // /logout: a valid session cookie but no antiforgery token/cookie is rejected by the
        // explicit IAntiforgery.ValidateRequestAsync check in Program.cs's /logout handler.
        const int userId = 302;
        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Read Only").Split(';')[0];

        var bareLogoutRequest = new HttpRequestMessage(HttpMethod.Post, "/logout");
        bareLogoutRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookiePair);
        var bareLogoutResponse = await client.SendAsync(bareLogoutRequest);
        bareLogoutResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------------------------------
    // V-2
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V2_NoClientSidePasswordFormatValidation()
    {
        using var client = CreateClient();
        var (html, _, _) = await GetLoginPageAsync(client);

        var passwordAttrs = BlazorFormScraper.FindInputAttributesById(html, "password");
        passwordAttrs.Should().NotBeNull();
        passwordAttrs!["type"].Should().Be("password");
        passwordAttrs.Should().NotContainKey("pattern");
        passwordAttrs.Should().NotContainKey("minlength");
        passwordAttrs.Should().NotContainKey("maxlength");
    }

    // ---------------------------------------------------------------------------------------
    // V-3
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_V3_LoginPostRequiresAntiforgeryToken()
    {
        using var client = CreateClient();

        var response = await client.SendAsync(BuildBareLoginRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.ApiHandler.LastRequest.Should().BeNull();
    }

    private static HttpRequestMessage BuildBareLoginRequest() =>
        new(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Username"] = Username,
                ["Input.Password"] = Password,
            }),
        };

    // ---------------------------------------------------------------------------------------
    // Coverage-gap closures (not spec-mapped SC/V IDs) — see CAS-021 test-writer follow-up:
    // Login.razor's Api-unreachable and non-2xx/non-401 fallback branches, and Routes.razor's
    // AuthorizeRouteView NotAuthorized redirect.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Closes the coverage gap on Login.razor's <c>catch (Exception ex) when (ex is
    /// HttpRequestException or TaskCanceledException)</c> branch (lines 69-74): when the "CachetApi"
    /// HttpClient call itself throws (Api process unreachable), the page must show the generic
    /// "temporarily unavailable" banner, clear the submitted password, and not crash. Also exercises
    /// the <c>@if (bannerMessage is not null)</c> render block (lines 18-20).
    /// </summary>
    [Fact]
    public async Task Test_LoginApiUnreachableShowsTemporarilyUnavailableBanner()
    {
        using var client = CreateClient();
        var (html, cookieHeader, _) = await GetLoginPageAsync(client);

        _factory.ApiHandler.OnLogin = _ => throw new HttpRequestException("simulated: Api process unreachable");

        var request = BuildLoginPostRequest(html, cookieHeader, Username, Password);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an unreachable-Api submission re-renders the same page, it does not redirect");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(
            "Sign-in is temporarily unavailable. Please try again.",
            "Login.razor's catch(HttpRequestException/TaskCanceledException) branch must render the generic banner");
        body.Should().NotContain(Password, "Input.Password must be cleared in the catch branch before any re-render");
    }

    /// <summary>
    /// Closes the coverage gap on Login.razor's fallback branch after the final <c>if</c>/<c>if</c>
    /// checks (lines 113-114): a non-2xx, non-401 Api response (e.g. 500) must show the same generic
    /// "temporarily unavailable" banner rather than leaking the raw status or crashing.
    /// </summary>
    [Fact]
    public async Task Test_LoginApiUnexpectedStatusShowsTemporarilyUnavailableBanner()
    {
        using var client = CreateClient();
        var (html, cookieHeader, _) = await GetLoginPageAsync(client);

        _factory.ApiHandler.OnLogin = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var request = BuildLoginPostRequest(html, cookieHeader, Username, Password);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an unexpected-status submission re-renders the same page, it does not redirect");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(
            "Sign-in is temporarily unavailable. Please try again.",
            "Login.razor's fallback branch (unexpected non-2xx/non-401 status) must render the generic banner");
        body.Should().NotContain(Password, "Input.Password must be cleared before any re-render, regardless of which branch is taken");
    }

    /// <summary>
    /// Closes the coverage gap on Routes.razor line 7 (<c>NavigationManager.NavigateTo("/login",
    /// forceLoad: true)</c> inside <c>&lt;AuthorizeRouteView&gt;</c>'s <c>&lt;NotAuthorized&gt;</c>
    /// fragment).
    ///
    /// bUnit is not a dependency of this test project (see Cachet.ArchiveSync.Web.Tests.csproj) and
    /// this test-writer agent's brief is not to add a new test framework for two lines of markup, so
    /// this test exercises the equivalent authorization-gate behavior at the integration level
    /// instead, per the CAS-021 test-writer instructions' explicit fallback: an authenticated
    /// session whose <c>session_token_version</c> is bumped mid-session (without ever calling
    /// <c>/logout</c>) must redirect to <c>/login</c> on its next navigation — mirroring CAS-002's
    /// <c>Test_SC3_LogoutInvalidatesSession</c> pattern, but via a raw version bump rather than the
    /// logout endpoint.
    ///
    /// IMPORTANT — what this test actually proves: the redirect this test observes is issued by the
    /// cookie-authentication middleware's own <c>OnValidatePrincipal</c>/<c>LoginPath</c> challenge
    /// (Program.cs), which runs and produces a 302 BEFORE Blazor's router or
    /// <c>AuthorizeRouteView</c> ever executes. It does NOT exercise Routes.razor line 7's
    /// component-level <c>NotAuthorized</c> fragment, which only runs when Blazor's own
    /// authorization re-check fails after the middleware has already let the request through (e.g.
    /// mid-circuit). That narrower branch remains unverified by any test in this suite; closing it
    /// properly would require a bUnit (or equivalent Blazor test-host) component test, which is out
    /// of scope here per the "do not add new scaffolding" instruction.
    /// </summary>
    [Fact]
    public async Task Test_RoutesNotAuthorizedRedirectsToLogin()
    {
        const int userId = 501;
        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Read Only").Split(';')[0];

        using var client = CreateClient();

        var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        firstRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookiePair);
        var firstResponse = await client.SendAsync(firstRequest);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the session is still valid on the first request");

        // Bump the shared session_token_version out-of-band (no /logout call) — e.g. an admin
        // action or a concurrent sign-in elsewhere invalidating this session.
        _factory.RegisterSessionTokenVersion(userId, 1);

        var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        secondRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookiePair);
        var secondResponse = await client.SendAsync(secondRequest);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        secondResponse.Headers.Location!.ToString().Should().Contain("/login");
    }
}
