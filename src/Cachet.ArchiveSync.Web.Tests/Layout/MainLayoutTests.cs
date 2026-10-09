using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using Cachet.ArchiveSync.Core.Dtos;
using Cachet.ArchiveSync.Web.Tests.Auth;
using Cachet.ArchiveSync.Web.Tests.TaskManager;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cachet.ArchiveSync.Web.Tests.Layout;

/// <summary>
/// CAS-025 §13 — proves the §10/§7 Success Criteria and Validations owned by
/// <c>MainLayout.razor</c> (SC-1, SC-2, SC-5, SC-6, SC-8, V2, V3) via bUnit, matching
/// <c>KeywordSearchPageTests</c>' (CAS-022) and <c>ODriveSyncPageTests</c>' (CAS-023) own
/// conventions for this test project: <see cref="TestContext"/> + <c>Bunit.TestDoubles</c>'
/// <see cref="TestAuthorizationContext"/> for the claims-derived identity/B2 badge, and
/// <see cref="CapturingLoggerProvider"/> for the SC-8 log-leakage assertion.
///
/// <para>
/// SC-3 (Sign Out invalidates the session and redirects to <c>/login</c>), SC-4 (the Web-hosted
/// <c>/logout</c> handler requires antiforgery), and V1 (same rule as SC-4) are proved by the
/// existing CAS-021 integration tests against the REAL <c>Program.cs</c> pipeline —
/// <c>WebAuthFlowIntegrationTests.Test_SC6_SignOutInvalidatesSessionAndRedirects</c> and
/// <c>WebAuthFlowIntegrationTests.Test_SC8_LoginLogoutRequireAntiforgeryToken</c> (its
/// bare-<c>/logout</c>-without-antiforgery assertion) / <c>Test_V3_LoginPostRequiresAntiforgeryToken</c>
/// — and are intentionally NOT duplicated here, per this spec's own §13 test plan.
/// </para>
///
/// <para>
/// <b>Antiforgery in bUnit:</b> <c>MainLayout.razor</c>'s Sign Out form renders
/// <c>&lt;AntiforgeryToken /&gt;</c>, which throws unless an <see cref="AntiforgeryStateProvider"/>
/// is resolvable from DI (unlike <c>WebAuthFlowIntegrationTests</c>' real
/// <c>WebApplicationFactory&lt;Program&gt;</c>, bUnit's container has no real antiforgery
/// pipeline) — <see cref="StubAntiforgeryStateProvider"/> below supplies a fixed, non-credential
/// token value so every test in this class can render the authenticated header.
/// </para>
///
/// <para>
/// <b>CAS-027 addition:</b> CAS-027 §8.7 put the Task Manager header badge, its summary poll and
/// <c>TaskManagerModal</c> in <c>MainLayout.razor</c>. The badge has since moved to the per-page
/// <c>TaskManagerBadge.razor</c> (CAS-030 §8.1), so CAS-027's SC-7/V-4 tests now live in
/// <c>TaskManagerBadgeTests</c>. CAS-048 §2.3 retargeted the former BLOCKED CAS-027 SC-8 test to
/// <c>TaskManagerBadgeTests</c> (the badge now hosts the modal), so it no longer lives here.
/// </para>
/// </summary>
public sealed class MainLayoutTests : TestContext
{
    private readonly CapturingLoggerProvider _loggerProvider = new();
    private readonly TestAuthorizationContext _authContext;
    private readonly FakeTaskManagerApiHandler _taskApiHandler = new();

    public MainLayoutTests()
    {
        Services.AddLogging(builder => builder.AddProvider(_loggerProvider));
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

        _authContext = this.AddTestAuthorization();
        SetAuthenticatedUser();

        // CAS-027 §8.7: harmless defaults so every pre-existing test in this class (none of which
        // concern the Task Manager badge) keeps rendering — a zero-count summary renders no badge
        // at all, and a long poll interval means the background loop never ticks during an
        // unrelated test's short lifetime.
        _taskApiHandler.OnGetSummary = _ => Task.FromResult(JsonResponse(HttpStatusCode.OK, new TaskSummaryResponse
        {
            ActiveCount = 0,
            UnacknowledgedTerminalCount = 0,
        }));
        _taskApiHandler.OnGetTasks = (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new PagedResult<TaskListItem>
        {
            Items = Array.Empty<TaskListItem>(),
            Page = 1,
            PageSize = 100,
            TotalCount = 0,
        }));

        var taskHttpClient = new HttpClient(_taskApiHandler) { BaseAddress = new Uri("http://localhost/api/") };
        Services.AddSingleton<IHttpClientFactory>(new SingleClientHttpClientFactory(taskHttpClient));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worker:TaskManagerPollIntervalSeconds"] = "300" })
            .Build());
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };

    /// <summary>Single-client <see cref="IHttpClientFactory"/> stand-in — this component only ever
    /// requests the "CachetApi" named client, so no name-based routing is needed. Mirrors every
    /// other test class in this test project.</summary>
    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private void SetAuthenticatedUser(string username = "jdoe", string b2Capability = "Full Access")
    {
        _authContext.SetAuthorized(username);
        _authContext.SetClaims(new Claim("b2_capability", b2Capability));
    }

    // A minimal marker fragment standing in for whatever page renders inside @Body — this test
    // class only needs to prove @Body is (or is not) rendered alongside the header/footer chrome,
    // never what a specific page actually contains.
    private static readonly RenderFragment TestBody = builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "id", "test-body-marker");
        builder.AddContent(2, "Test Page Body");
        builder.CloseElement();
    };

    private IRenderedComponent<Web.Components.Layout.MainLayout> RenderLayout() =>
        RenderComponent<Web.Components.Layout.MainLayout>(parameters => parameters.Add(p => p.Body, TestBody));

    private static IElement KeywordSearchTab(IRenderedComponent<Web.Components.Layout.MainLayout> cut) =>
        cut.FindAll("a.cachet-header-tab").First(a => a.TextContent.Contains("Keyword Search"));

    private static IElement ODriveTab(IRenderedComponent<Web.Components.Layout.MainLayout> cut) =>
        cut.FindAll("a.cachet-header-tab").First(a => a.TextContent.Contains("O-Drive Sync"));

    // -----------------------------------------------------------------------------------------
    // SC-1
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC1_HeaderRendersAllCoreElements()
    {
        var cut = RenderLayout();

        cut.Find(".cachet-header-wordmark").TextContent.Should().Be("CACHET");
        cut.Find(".cachet-header-title").TextContent.Should().Be("Archive Files Search & Sync");

        var tabs = cut.FindAll("a.cachet-header-tab");
        tabs.Should().HaveCount(2);
        tabs.Select(t => t.TextContent).Should().Contain(new[] { "Keyword Search", "O-Drive Sync and content Scan" });

        cut.Find(".cachet-header-user").TextContent.Should().Be("jdoe", "the identity display name is claims-derived (§8.1)");

        var signOutForm = cut.Find("form.cachet-sign-out-form");
        signOutForm.GetAttribute("method").Should().Be("post");
        // PathBase-relative: resolves against App.razor's <base href="{PathBase}/"> to the Web-hosted
        // /logout handler, including under an IIS virtual application.
        signOutForm.GetAttribute("action").Should().Be("logout");
        signOutForm.QuerySelector("button[type=submit]")!.TextContent.Should().Be("Sign Out");

        cut.Find(".cachet-badge-pill").TextContent.Should().Be("B2 Access: Full Access");

        cut.Markup.Should().Contain("Test Page Body", "@Body must still render inside the layout alongside the header");
    }

    // -----------------------------------------------------------------------------------------
    // SC-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC2_ActiveTabReflectsCurrentRoute()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();

        navigationManager.NavigateTo("/");
        var homeCut = RenderLayout();
        KeywordSearchTab(homeCut).GetAttribute("class").Should().Contain(
            "active", "the default route (\"/\") is Keyword Search's own route (Match=NavLinkMatch.All)");
        ODriveTab(homeCut).GetAttribute("class").Should().NotContain("active");

        navigationManager.NavigateTo("/odrive-sync");
        var syncCut = RenderLayout();
        ODriveTab(syncCut).GetAttribute("class").Should().Contain("active");
        KeywordSearchTab(syncCut).GetAttribute("class").Should().NotContain("active");
    }

    // -----------------------------------------------------------------------------------------
    // V-3
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V3_ActiveTabUnderlineMatchesCurrentRoute()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        // NavLink's built-in matching compares only the path, ignoring the query string — a manual
        // string-equality comparison against NavigationManager.Uri would NOT match here, since the
        // full absolute Uri (with query string) never equals "http://localhost/odrive-sync". This
        // proves the underline is driven by NavLink itself, not manual route-string comparison (§7 V3).
        navigationManager.NavigateTo("/odrive-sync?ref=email-campaign");

        var cut = RenderLayout();

        ODriveTab(cut).GetAttribute("class").Should().Contain("active");
        KeywordSearchTab(cut).GetAttribute("class").Should().NotContain("active");
    }

    // -----------------------------------------------------------------------------------------
    // SC-5
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC5_PlaceholderButtonsDisabledAndInert()
    {
        var cut = RenderLayout();

        // None of CAS-025's four placeholders is rendered by this layout any more: the toolbar and
        // report rows moved to each page (see MainLayout.razor's header comment), and each button has
        // since shipped live there — Quarantine Queue (CAS-024), Audit Log (CAS-031), Pre-/Post-Action
        // Report (CAS-032 §0.3/§13.3). The static layout therefore hosts no placeholder at all.
        cut.FindAll("button.cachet-header-btn-disabled").Should().BeEmpty(
            "the layout hosts no disabled placeholder buttons; they live (enabled) on the per-page toolbars");
        foreach (var label in new[] { "Quarantine Queue", "Audit Log", "Pre-Action Report", "Post-Action Report" })
        {
            cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == label, $"{label} is a per-page toolbar button, not a layout one");
        }
    }

    // -----------------------------------------------------------------------------------------
    // V-2
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_V2_PlaceholderHeaderButtonsAreDisabledWithNoHandler()
    {
        var cut = RenderLayout();

        // The layout is statically rendered (no interactive handlers can run), and CAS-025's
        // placeholders have left it (see Test_SC5 above). Its only remaining button is the Sign Out
        // form's native submit (CAS-025 §8.4), which must have no Blazor @onclick handler wired.
        var buttons = cut.FindAll("button");
        buttons.Should().ContainSingle("Sign Out is the only button the static layout renders");
        var signOut = buttons.Single();
        signOut.TextContent.Trim().Should().Be("Sign Out");
        var act = () => signOut.Click();
        act.Should().Throw<MissingEventHandlerException>(
            "the layout must wire no @onclick handler at all (V2) — bUnit raises this exception " +
            "when a click is dispatched to an element with no registered Blazor event handler, which is " +
            "exactly the proof that no click behavior exists");
    }

    // -----------------------------------------------------------------------------------------
    // SC-6
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC6_FooterShowsEmptyStateCopy()
    {
        var cut = RenderLayout();

        cut.Find("footer.cachet-footer-bar").TextContent.Should().Be("Local Cache: not yet populated.");
    }

    // -----------------------------------------------------------------------------------------
    // SC-8
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_SC8_NoCredentialLeakageInHeaderMarkupOrLogs()
    {
        const string SensitiveB2Key = "raw-b2-appkey-marker-cas025-6e2f";
        const string SensitiveSessionCookie = "session-cookie-marker-cas025-9a41";

        // MainLayout.razor never reads a raw B2 key or session-cookie value directly — it only
        // reads the b2_capability CLAIM (a label like "Full Access"/"Read Only", never the raw key
        // itself, per CAS-002's existing design) and the identity Name claim. Prove the property
        // holds structurally, the same defensive pattern CAS-022/CAS-023's own SC-8/SC-10/SC-13
        // tests already established for this project: nothing resembling a credential marker
        // reaches rendered markup or the shared log pipeline, regardless of what else is present
        // in the DI container.
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["B2:ApplicationKey"] = SensitiveB2Key })
            .Build());

        var cut = RenderLayout();

        cut.Markup.Should().NotContain(SensitiveB2Key);
        cut.Markup.Should().NotContain(SensitiveSessionCookie);

        var logText = string.Join("\n", _loggerProvider.Messages);
        logText.Should().NotContain(SensitiveB2Key);
        logText.Should().NotContain(SensitiveSessionCookie);
    }

    // -----------------------------------------------------------------------------------------
    // Coverage-gap closure (not a spec-mapped SC/V ID) — same precedent as KeywordSearchPageTests'
    // (CAS-022) and ODriveSyncPageTests' (CAS-023) own follow-up closures: §13's ≥85% line-coverage
    // target requires exercising the `@if (isAuthenticated)` false branch too (the unauthenticated
    // /login page renders through this same layout, per the CAS-021 §8.6 fix documented in the
    // component's own comments), which no spec-mapped SC/V test above reaches.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void Test_UnauthenticatedLayoutRendersNoHeaderOrFooter()
    {
        _authContext.SetNotAuthorized();

        var cut = RenderLayout();

        cut.Markup.Should().NotContain("cachet-header-bar", "the full header chrome must not render for an unauthenticated user (CAS-021 §8.6 fix)");
        cut.Markup.Should().NotContain("cachet-footer-bar");
        cut.Markup.Should().Contain("cachet-brand-bar", "the brand accent bar renders unconditionally, authenticated or not");
        cut.Markup.Should().Contain("Test Page Body", "@Body must still render even when the header/footer chrome is suppressed");
    }

    /// <summary>Minimal stub satisfying &lt;AntiforgeryToken /&gt;'s injected
    /// <see cref="AntiforgeryStateProvider"/> dependency — bUnit's DI container has no real
    /// antiforgery pipeline (unlike <c>WebAuthFlowIntegrationTests</c>' real
    /// <c>WebApplicationFactory&lt;Program&gt;</c>), so without this the Sign Out form's
    /// &lt;AntiforgeryToken /&gt; throws <see cref="InvalidOperationException"/> on render. The
    /// fixed value below is a stand-in CSRF token, not a credential — antiforgery tokens are
    /// expected to render (that is the whole point of the field), unlike the B2 key/session
    /// cookie markers SC-8 asserts must never appear.
    /// </summary>
    private sealed class StubAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() =>
            new("stub-antiforgery-token-value-not-a-credential", "__RequestVerificationToken");
    }
}

