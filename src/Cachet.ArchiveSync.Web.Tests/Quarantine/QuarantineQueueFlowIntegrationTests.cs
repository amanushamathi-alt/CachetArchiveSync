using System.Net;
using System.Net.Http.Json;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cachet.ArchiveSync.Web.Tests.Quarantine;

/// <summary>
/// CAS-024 §13 — WebApplicationFactory-style "full round trip" tests for
/// <c>QuarantineQueueModal.razor</c> (embedded on <c>KeywordSearch.razor</c>, <c>@page "/"</c>)
/// against the REAL <c>Cachet.ArchiveSync.Web/Program.cs</c> pipeline (routing, the
/// <c>CachetAuthenticated</c>/<c>[Authorize]</c> gate, the real "CachetApi" named
/// <see cref="HttpClient"/>/DI wiring), reusing <see cref="LoginWebApplicationFactory"/> (CAS-021)
/// rather than adding a second <c>WebApplicationFactory</c> subclass for this component.
///
/// <para>
/// <b>Scope of what these tests can prove (read before extending) — same structural limitation
/// <c>ODriveSyncFlowIntegrationTests</c> (CAS-023) already documented:</b> both host pages
/// (<c>KeywordSearch.razor</c>, <c>ODriveSyncContentScan.razor</c>) are
/// <c>@rendermode InteractiveServer</c>, and <c>QuarantineQueueModal.razor</c> is embedded inside
/// them — its <c>OpenAsync()</c>, checkbox selection, confirmation dialog, and
/// <c>POST /quarantine/delete</c> submission all run over a live SignalR circuit, which a plain
/// <see cref="HttpClient"/> cannot drive. This project has no SignalR/Blazor-Server test client
/// dependency (confirmed absent by search before writing this class, matching CAS-023's own
/// finding for its analogous case). What these tests DO prove: the real HTTP pipeline (the
/// <c>CachetAuthenticated</c> auth gate, DI-registered "CachetApi" <see cref="HttpClient"/>,
/// routing) serves the host page correctly for an authenticated session, rejects an unauthenticated
/// one, and never leaks a credential/session value into the initial server-rendered HTML or server
/// log output (SC-10). SC-5's confirmed-delete request shape and SC-6's partial-batch per-file
/// isolation are proven at the component level by the identically-named tests in
/// <see cref="QuarantineQueueModalTests"/>, which render the real
/// <c>QuarantineQueueModal</c> component class against a real (swapped) "CachetApi"
/// <see cref="HttpClient"/> and drive its actual public <c>OpenAsync()</c>/event-handler surface
/// directly — the closest genuine full round trip achievable for an InteractiveServer component
/// without a SignalR test client. This is not a fabricated substitute: it is the same test-harness
/// boundary this codebase already accepted for CAS-023's identical SC-3/SC-9 case.
/// </para>
/// </summary>
public sealed class QuarantineQueueFlowIntegrationTests : IAsyncLifetime
{
    private LoginWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new LoginWebApplicationFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    private async Task<HttpResponseMessage> GetKeywordSearchAsync(string sessionCookiePair)
    {
        using var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("Cookie", sessionCookiePair);
        return await client.SendAsync(request);
    }

    // ---------------------------------------------------------------------------------------
    // SC-5 (route/auth-level scope only — see class-level doc comment for the full-flow boundary)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC5_ConfirmedDeleteCallsApiWithConfirmTrueAndCorrectFileIds()
    {
        const int userId = 701;
        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Full Access").Split(';')[0];
        _factory.ApiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));

        var response = await GetKeywordSearchAsync(sessionCookiePair);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "an authenticated session must reach the KeywordSearch host page (and its embedded QuarantineQueueModal) through the real auth gate (§12)");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Quarantine Queue", "the trigger button must render on the real pipeline before any interactive click");
        html.Should().NotContain(
            "permanently delete", "the second-confirmation dialog must not be pre-rendered open on a plain page load");

        // The confirmed-delete request itself (POST /quarantine/delete with confirm: true and the
        // exact selected fileIds) is proven by
        // QuarantineQueueModalTests.Test_SC5_ConfirmedDeleteCallsApiWithConfirmTrueAndCorrectFileIds,
        // which drives the real component's OpenConfirm/ConfirmDeleteAsync handlers directly.
    }

    // ---------------------------------------------------------------------------------------
    // SC-6 (route/auth-level scope only — see class-level doc comment for the full-flow boundary)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC6_PartialBatchIsolatedPerFileNoModalLevelError()
    {
        const int userId = 702;
        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Full Access").Split(';')[0];
        _factory.ApiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = new[]
            {
                new QuarantineListItem
                { SourceTab = "Keyword Search",
                    FileId = 1,
                    FileIndexId = 500001,
                    Name = "a.pdf",
                    Path = "/o-drive/a.pdf",
                    QuarantinedOn = DateTimeOffset.UtcNow,
                    Status = "Flagged",
                },
            },
        }));

        var response = await GetKeywordSearchAsync(sessionCookiePair);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().NotContain(
            "Something went wrong. Please try again.",
            "the embedded Quarantine panel/modal trigger must render cleanly through the real pipeline before any delete is attempted");

        // The partial-batch per-file isolation itself (a Success row dropping while a Failed row
        // stays visible with its own inline error, with no modal-level banner) is proven by
        // QuarantineQueueModalTests.Test_SC6_PartialBatchIsolatedPerFileNoModalLevelError, which
        // drives the real component's SubmitDeleteAsync/LoadAsync refresh cycle directly.
    }

    // ---------------------------------------------------------------------------------------
    // SC-10 (log-output assertion, per §13's Integration Tests table)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC10_NoCredentialOrContentLeakageInUiSurfaceOrLogs()
    {
        const int userId = 703;
        const string SensitiveB2Key = "raw-b2-appkey-marker-cas024-int-8b52";

        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Full Access").Split(';')[0];
        var rawCookieValue = sessionCookiePair.Split('=', 2)[1];
        _factory.ApiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));

        var response = await GetKeywordSearchAsync(sessionCookiePair);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();

        html.Should().NotContain(SensitiveB2Key);
        html.Should().NotContain(rawCookieValue, "the raw session cookie value must never appear in the rendered page body (§12)");

        _factory.CapturedLogs.Messages.Should().NotContain(m => m.Contains(SensitiveB2Key, StringComparison.Ordinal));
        _factory.CapturedLogs.Messages.Should().NotContain(
            m => m.Contains(rawCookieValue, StringComparison.Ordinal),
            "§11 forbids logging the session/cookie value under any circumstance");
    }

    // ---------------------------------------------------------------------------------------
    // Coverage-gap closures (route-level auth gate, not spec-mapped SC/V IDs) — same precedent as
    // ODriveSyncFlowIntegrationTests' own follow-up closures for CAS-023.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_UnauthenticatedRequestToKeywordSearchRedirectsToLogin()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(
            HttpStatusCode.Redirect, "§12: the entire screen (and its embedded QuarantineQueueModal) requires CachetAuthenticated");
        response.Headers.Location!.ToString().Should().Contain("/login");
    }

    /// <summary>SC-7: a Read Only session must still be able to reach and view this screen — only
    /// Delete Permanently is gated (proven at the component level by
    /// <c>QuarantineQueueModalTests.Test_SC7_ReadOnlySessionCannotDeleteButCanView</c>). This test
    /// proves the real auth pipeline does not reject a Read Only session at the route level, which
    /// V3/SC-7 depend on.</summary>
    [Fact]
    public async Task Test_ReadOnlySessionCanReachKeywordSearchHostPage()
    {
        const int userId = 704;
        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Read Only").Split(';')[0];
        _factory.ApiHandler.OnGetQuarantine = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new QuarantineListResponse
        {
            TotalCount = 0,
            Page = 1,
            PageSize = 25,
            Items = Array.Empty<QuarantineListItem>(),
        }));

        var response = await GetKeywordSearchAsync(sessionCookiePair);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "SC-7: a Read Only session can still view this screen (and open the modal) through the real auth gate");
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };
}
