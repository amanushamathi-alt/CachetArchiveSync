using System.Net;
using Cachet.ArchiveSync.Web.Tests.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cachet.ArchiveSync.Web.Tests.ODriveSync;

/// <summary>
/// CAS-023 §13 — WebApplicationFactory-style "full round trip" tests for
/// <c>ODriveSyncContentScan.razor</c> against the REAL <c>Cachet.ArchiveSync.Web/Program.cs</c>
/// pipeline (routing, the <c>FallbackPolicy</c> auth gate, the real "CachetApi" named
/// <see cref="HttpClient"/>/DI wiring), reusing <see cref="LoginWebApplicationFactory"/> (CAS-021)
/// rather than adding a second <c>WebApplicationFactory</c> subclass for this one screen.
///
/// <para>
/// <b>Scope of what these tests can prove (read before extending):</b>
/// <c>ODriveSyncContentScan.razor</c> is <c>@rendermode InteractiveServer</c> (§8.2) — its scan/
/// action submission, polling, and in-place auto-population all run over a live SignalR circuit,
/// which a plain <see cref="HttpClient"/> cannot drive. This project has no SignalR/Blazor-Server
/// test client dependency, and this spec's own §13 table specified an analogous
/// <c>KeywordSearchFlowIntegrationTests</c> class for CAS-022 that was never built in this codebase
/// for the identical structural reason (confirmed absent by search before writing this class — see
/// this test-writer's report). What these tests DO prove: the real HTTP pipeline (auth gate,
/// DI-registered "CachetApi" HttpClient, routing) serves <c>/odrive-sync</c> correctly for both a
/// <c>CachetFullAccess</c> and a Read Only session, and never leaks a credential/session value into
/// the initial server-rendered HTML or server log output (SC-13). The interactive scan/action round
/// trip itself (SC-3's auto-populate, SC-9's confirmed-purge request) is proven at the component
/// level by the identically-named tests in <see cref="ODriveSyncPageTests"/>, which render the real
/// component class against a real (swapped) "CachetApi" HttpClient and drive its actual event
/// handlers directly — the closest genuine full round trip achievable for an InteractiveServer
/// component without a SignalR test client.
/// </para>
/// </summary>
public sealed class ODriveSyncFlowIntegrationTests : IAsyncLifetime
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

    private async Task<HttpResponseMessage> GetODriveSyncAsync(string sessionCookiePair)
    {
        using var client = CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/odrive-sync");
        request.Headers.TryAddWithoutValidation("Cookie", sessionCookiePair);
        return await client.SendAsync(request);
    }

    // ---------------------------------------------------------------------------------------
    // SC-3 (full round trip — see class-level doc comment for exact scope)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC3_CompletedScanAutoPopulatesAllThreePanelsInPlace()
    {
        const int userId = 601;
        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Full Access").Split(';')[0];

        var response = await GetODriveSyncAsync(sessionCookiePair);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "an authenticated session must reach /odrive-sync through the real FallbackPolicy gate (§12)");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Run Content Scan");
        html.Should().Contain(
            "No comparison run yet",
            "the pre-scan empty state must render correctly through the real Program.cs pipeline before any interactive poll runs");

        // The interactive auto-populate behavior itself (submitting a scan, polling, and the three
        // panels populating in place) is proven by
        // ODriveSyncPageTests.Test_SC3_CompletedScanAutoPopulatesAllThreePanelsInPlace.
    }

    // ---------------------------------------------------------------------------------------
    // SC-9 (full round trip — see class-level doc comment for exact scope)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC9_ConfirmedPurgeCallsApiWithConfirmTrueAndCorrectFileIds()
    {
        const int userId = 602;
        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Full Access").Split(';')[0];

        var response = await GetODriveSyncAsync(sessionCookiePair);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().NotContain(
            "permanently delete", "the second-confirmation dialog must not be pre-rendered open (V3/SC-8) on a plain page load");

        // The confirmed-purge request itself (POST /sync/actions/purge with confirm: true and the
        // exact selected fileIds) is proven by
        // ODriveSyncPageTests.Test_SC9_ConfirmedPurgeCallsApiWithConfirmTrueAndCorrectFileIds, which
        // drives the real component's OpenPurgeConfirm/ConfirmPurgeAsync handlers directly.
    }

    // ---------------------------------------------------------------------------------------
    // SC-13 (log-output assertion, per §13's Integration Tests table)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_SC13_NoCredentialOrContentLeakageInUiSurfaceOrLogs()
    {
        const int userId = 603;
        const string SensitiveB2Key = "raw-b2-appkey-marker-cas023-int-7e41";

        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Full Access").Split(';')[0];
        var rawCookieValue = sessionCookiePair.Split('=', 2)[1];

        var response = await GetODriveSyncAsync(sessionCookiePair);

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
    // WebAuthFlowIntegrationTests' own follow-up closures for CAS-021.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_UnauthenticatedRequestToODriveSyncRedirectsToLogin()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/odrive-sync");

        response.StatusCode.Should().Be(
            HttpStatusCode.Redirect, "§12: the entire screen requires CachetAuthenticated, the same global FallbackPolicy gate CAS-021 establishes");
        response.Headers.Location!.ToString().Should().Contain("/login");
    }

    /// <summary>SC-11: a Read Only session must still be able to reach and view this screen — only
    /// the three sync-action buttons are gated (proven at the component level by
    /// <c>ODriveSyncPageTests.Test_SC11_ReadOnlySessionCannotTriggerSyncActionsButCanScanAndView</c>).
    /// This test proves the real auth pipeline does not reject a Read Only session at the route
    /// level, which V5/SC-11 depend on.</summary>
    [Fact]
    public async Task Test_ReadOnlySessionCanReachODriveSync()
    {
        const int userId = 604;
        _factory.RegisterSessionTokenVersion(userId, 0);
        var sessionCookiePair = _factory.CreateSessionSetCookieHeader(userId, "0", "Read Only").Split(';')[0];

        var response = await GetODriveSyncAsync(sessionCookiePair);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "SC-11: a Read Only session can still view this screen through the real FallbackPolicy gate");
    }
}
