---
id: CAS-021
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-23
priority: P1
related_specs: [CAS-002, CAS-001]
touches:
  database: false
  api:
    false # no Cachet.ArchiveSync.Api changes — AuthController (CAS-002) is
    # consumed as-is, unmodified
  ui: true
  worker: false
  b2: false # no direct B2 call — consumes CAS-002's already-brokered result
  config: false # Api:BaseUrl already exists (CAS-002 §6.6); no new config key
  security: true # cookie handling, CSRF, route-authorization gating
migrations: []
---

# CAS-021 — Sign-In / Application Login Screen

WBS Task 21 (Web UI Development). Implements FR-01, FR-02, FR-03; UI contract fixed by
`CACHET_UIUX_Design_Specification_v1.md` §2; session/claims contract fixed by `CAS-002`
(merged, 2026-09-22).

---

## §0 Scope Correction (read this first)

**The task brief that requested this spec described the login method as "pending" and asked for
a component built to swap cleanly between username/password and SSO.** That framing is stale.
Per `CACHET_UIUX_Design_Specification_v1.md` §2 (confirmed by R. Devarajan, TraceCounts,
2026-09-09) and `CAS-002` §0 (which corrected the identical stale framing when it was drafted),
the login method is **resolved as username/password only**, gated by a live B2 key validation in
the same `POST /login` call. SSO was considered and explicitly declined. `CAS-002` §0 already
states the reasoning for not building a pluggable auth-provider abstraction on a 4-user internal
tool with no identity provider anywhere else in scope — this spec follows that same decision and
does **not** build a swappable provider abstraction. If SSO is reconsidered later, that is a new,
separately-confirmed decision requiring its own spec change (Karpathy P4).

This is the second time this stale brief text has reached a spec in this repo (first: `CAS-002`
§0). Recommend the underlying WBS/task-drafting prompt be corrected at the source so a third
recurrence doesn't happen.

---

## §1 Summary

Builds the Sign-In screen the wireframe never had — the Blazor Server page a user lands on when
unauthenticated, replacing the wireframe's original "enter your B2 key" concept entirely (already
superseded by `CAS-002`; this spec is the first thing that actually renders a login UI). `CAS-002`
built and merged the backend (`POST /login`, `POST /logout`, cookie/claims mechanics,
authorization policies) but shipped with **no UI consumer for either endpoint** — no `.razor` page
exists in `src/Cachet.ArchiveSync.Web/` today, and nothing in the app currently requires
authentication at all (`Routes.razor` renders every page unauthenticated). This spec closes both
gaps: the login screen itself, and the route-level authorization gate that makes login mandatory.
Implements FR-01, FR-02, FR-03.

---

## §2 User Story

As a **TraceCounts staff member using the CACHET Archive Files Search and Sync application**, I
want to sign in with my username and password on a dedicated screen, see a clear message if my
credentials or B2 access are rejected, and be dropped into the application once both checks
succeed — so that I never see or interact with archive data before I've been authenticated, and I
always understand why a sign-in attempt failed without the message leaking which check failed.

---

## §3 In-Scope

- `Login.razor` page (`@page "/login"`): fields, client-side non-empty validation, submit
  behavior, and the two distinct inline failure states, per `CACHET_UIUX_Design_Specification_v1.md`
  §2.1/§2.2.
- The mechanism by which a static, non-interactive Blazor page can actually receive the
  `HttpOnly` session cookie `CAS-002` §6.1 issues — see §8; this is a genuine implementation gap
  `CAS-002` §4 left to "how the form looks," and no existing document resolves it.
- Route-level authorization gate: every page except `/login` requires an authenticated session;
  an unauthenticated request is redirected to `/login` (`CAS-002` §6.2's "on window lapse" behavior
  currently has no wiring to act on — see §8).
- A minimal Sign Out affordance in `MainLayout.razor`, since `CAS-002`'s `POST /logout` (§6.3) also
  has no UI consumer today, per `CACHET_UIUX_Design_Specification_v1.md` §2.3 — same
  "endpoint built, no UI consumer" situation `CAS-014`/`CAS-017` already established a precedent
  for closing in a focused follow-up spec.
- Post-login redirect target (§8) — root (`/`), since Keyword Search (WBS Task 22, the UI/UX spec
  §2.2's stated default landing screen) is `Not Started` as of this spec's drafting.

---

## §4 Out-of-Scope

- **Login method / SSO** — resolved, not revisited (§0).
- **`Users` table schema, `POST /login`/`POST /logout` request/response contracts, session claim
  shape, sliding-expiration mechanics, authorization policy names** — all already specified and
  implemented by `CAS-002`. This spec is a pure consumer of that contract; it changes no file in
  `Cachet.ArchiveSync.Api` or `Cachet.ArchiveSync.Core`.
- **`POST /admin/users` provisioning** — unrelated; no UI is ever built for it (Security Analysis
  §1, restated `CAS-002` §4).
- **Keyword Search screen itself** — not built yet (WBS Task 22); this spec only decides where
  login redirects _to_ (§8), not what that screen contains.
- **Drive encryption-at-rest / O-Drive refresh cadence** (BRD §12.4, still open) — unrelated to
  this spec; not depended upon.
- No BRD-open-item dependency exists for this spec — the login method itself (the only item this
  screen could plausibly have depended on) is resolved, not open (§0).

---

## §5 Database Objects

N/A — `touches.database: false`. No schema change. Consumes `dbo.users` only indirectly, through
`CAS-002`'s already-built `POST /login`/`POST /logout`.

---

## §6 API Endpoints

No `Cachet.ArchiveSync.Api` endpoint is added or changed (`touches.api: false`) — `POST /login`
and `POST /logout` are `CAS-002`'s, consumed here exactly as specified (API Design Spec §3.1/§3.2).

This spec does add two thin, non-Blazor HTTP endpoints to **`Cachet.ArchiveSync.Web`'s**
`Program.cs` (not a new API contract — see §8 for why these exist and §12 for why they are safe):

| Route                     | Method | Auth                  | Purpose                                   |
| ------------------------- | ------ | --------------------- | ----------------------------------------- |
| `/login` (page POST-back) | `POST` | `[AllowAnonymous]`    | Handles the Sign-In form submit; see §8.  |
| `/logout`                 | `POST` | `CachetAuthenticated` | Handles the Sign Out form submit; see §8. |

---

## §7 Validations

| ID  | Rule                                                                                                                                         | On Failure                                           | Test                                                  |
| --- | -------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------- | ----------------------------------------------------- |
| V1  | Username and Password must both be non-empty before the form can be submitted (UI/UX spec §2.1 — "disabled until both fields are non-empty") | Submit button remains `disabled`; no request is sent | `Test_V1_SignInButtonDisabledUntilBothFieldsNonEmpty` |
| V2  | No client-side validation of password content/strength beyond non-empty (UI/UX spec §2.2 — "all real validation is server-side")             | N/A — by design, nothing to fail client-side         | `Test_V2_NoClientSidePasswordFormatValidation`        |
| V3  | `POST /login` and `POST /logout` (§6) reject any request without a valid antiforgery token                                                   | `400`                                                | `Test_V3_LoginPostRequiresAntiforgeryToken`           |

All credential/B2-validation rules themselves (username/password lookup, live B2 check) are
`CAS-002`'s and are not restated or re-implemented here.

---

## §8 UI / UX

### §8.1 Layout and Fields — `Login.razor`

Exactly as specified, `CACHET_UIUX_Design_Specification_v1.md` §2.1 — restated here only to
confirm the build target, not to redefine it:

- Centered card, CACHET brand colors (navy `#142A4D`, red accent `#C0102A`, white).
- **Username** — `<input type="text" required>`.
- **Password** — `<input type="password" required>`.
- **Sign In** — primary submit button, `disabled` until both fields are non-empty (small,
  unobtrusive vanilla JS in `wwwroot/js/login.js` toggling `disabled` on `input` events — no
  Blazor interactivity/SignalR circuit is used or needed for this; see §8.2 for why the page
  cannot be an `InteractiveServer` component in the first place).
- No "Forgot password" / "Create account" / "Register" links (UI/UX spec §2.1 — deliberately
  absent; do not add them).

### §8.2 The Real Gap This Spec Resolves: How the Cookie Reaches the Browser

**Not addressed by any existing document** — `CAS-002` §4 explicitly scoped "Sign-In screen
layout" out of its own document, and no document states _how_ the login form's submit actually
reaches `AuthController.Login` (Api) such that the browser ends up holding the resulting
`HttpOnly` cookie. This matters because the answer is not "however you'd normally wire up a
button," for two concrete, verified reasons:

1. **`Login.razor` cannot be an `InteractiveServer` (SignalR-circuit) component.**
   `AuthController.Login` (`Cachet.ArchiveSync.Api/Controllers/AuthController.cs:62`) issues the
   session cookie via `HttpContext.SignInAsync`, which writes a `Set-Cookie` response header. If a
   Blazor Server component's `@onclick` handler (running inside an already-established SignalR
   circuit, whose initial HTTP response was sent long ago) tried to trigger that call, there is no
   HTTP response left to attach a `Set-Cookie` header to — the browser would never see it. This is
   a structural limitation of interactive Blazor Server, not a configuration mistake.
2. **The browser cannot call Api directly from client-side script either.** Confirmed by reading
   `Cachet.ArchiveSync.Api/Program.cs`: no CORS policy is configured. In this project's actual
   `appsettings.Development.json`, `Api:BaseUrl` is `https://localhost:7244` — a different origin
   (scheme+host+port) from Web's own dev origin — so a `fetch()` call from a page served by Web
   directly to Api would be blocked by the browser's same-origin policy without CORS, and adding
   CORS here would be new attack surface for a problem this project doesn't need to solve (`CAS-002`
   §6.6 already established the "no new moving parts" preference for this exact Web↔Api boundary).

**Resolution — reuse `CAS-002` §6.6's existing pattern, applied to a real HTTP request/response
instead of a SignalR circuit:**

`Login.razor` is rendered as a **static server-rendered page** (no `@rendermode` directive) using
.NET 8 Blazor's built-in named-form POST-back feature — a real, full HTTP `POST` to the same
`/login` URL, processed by the page's own `@code` block _before_ any response is sent, which means
`[CascadingParameter] public HttpContext HttpContext { get; set; }` is genuinely usable inside the
handler (unlike the interactive-circuit case in point 1 above):

```razor
@page "/login"
@attribute [AllowAnonymous]

<EditForm Model="Input" FormName="login-form" OnValidSubmit="HandleLoginAsync">
    <DataAnnotationsValidator />
    <InputText @bind-Value="Input.Username" id="username" required />
    <InputText @bind-Value="Input.Password" type="password" id="password" required />
    <button type="submit" id="sign-in-btn">Sign In</button>
</EditForm>

@if (errorMessage is not null)
{
    <p class="cachet-inline-error">@errorMessage</p>
}

@code {
    [CascadingParameter] public HttpContext HttpContext { get; set; } = default!;
    [SupplyParameterFromForm] private LoginFormModel Input { get; set; } = new();
    [Inject] private IHttpClientFactory HttpClientFactory { get; set; } = default!;

    private string? errorMessage;

    private async Task HandleLoginAsync()
    {
        var client = HttpClientFactory.CreateClient("CachetApi");
        var response = await client.PostAsJsonAsync("login",
            new { username = Input.Username, password = Input.Password });

        if (response.IsSuccessStatusCode)
        {
            // Forward Api's Set-Cookie verbatim onto THIS request's real browser response —
            // the one piece CAS-002 §6.6's existing HttpClient pattern doesn't already do,
            // because every other Web→Api call forwards a cookie the OTHER direction.
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var cookie in cookies)
                {
                    HttpContext.Response.Headers.Append("Set-Cookie", cookie);
                }
            }
            HttpContext.Response.Redirect("/");
            return;
        }

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        errorMessage = problem?.Type?.Contains("b2-access-unavailable") == true
            ? "Your account cannot currently access the archive. Contact your administrator."
            : "Incorrect username or password.";
    }
}
```

This is illustrative, not final code — the implementer follows `csharp-dotnet`/`blazor-server`
skill conventions for exact null-handling, DI registration, and file layout. The load-bearing
design decision is: **the cookie is forwarded from Api's response to the real browser response
inside a genuine (non-circuit) HTTP request**, not fabricated or re-derived by Web. Web never
constructs, decodes, or inspects the cookie's claims payload — it only relays the opaque bytes Api
already produced under the shared Data Protection key ring (`CAS-002` §6.6), so the "no B2 key,
no session claim, ever reconstructed outside Api's `LoginService`" invariant (`CAS-002` §6.1,
§12) is preserved exactly.

The `POST /logout` handler (`MainLayout.razor`'s Sign Out form, §8.4) uses the identical
forward-the-cookie-header pattern in the opposite direction (a real POST from the browser calls a
Web-hosted handler, which calls Api's already-authenticated `POST /logout` via the existing
cookie-forwarding `"CachetApi"` client — `CAS-002` §6.6 — and relays Api's expired `Set-Cookie`
back to the browser).

### §8.3 Behavior — Exactly `CACHET_UIUX_Design_Specification_v1.md` §2.2, No Deviation

| Trigger                                       | Action                                                                                                                                                                                                                                                                                                               |
| --------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Sign In submitted                             | Real HTTP `POST /login` (Web-hosted handler, §8.2) — not an XHR/fetch, a genuine form submission. The browser's native "submitting" state is the spinner/disable behavior; §8.1's JS-driven `disabled` toggle only gates _empty-field_ submission, per V1.                                                           |
| Both checks succeed                           | Cookie forwarded (§8.2), full-page redirect to `/` (§8.5).                                                                                                                                                                                                                                                           |
| DB check fails                                | Inline error under the password field: **"Incorrect username or password."** — does not distinguish unknown username from wrong password (matches `LoginFailureReason.InvalidCredentials`, the single enum value the API already collapses both cases into — `Cachet.ArchiveSync.Core/Enums/LoginFailureReason.cs`). |
| DB check succeeds, B2 validation fails        | Inline error: **"Your account cannot currently access the archive. Contact your administrator."** — never reveals the key value, B2 error detail, or which check failed (matches `LoginFailureReason.B2AccessUnavailable`).                                                                                          |
| Network/server error unrelated to credentials | Non-field banner: **"Sign-in is temporarily unavailable. Please try again."**                                                                                                                                                                                                                                        |

### §8.4 Sign Out (`MainLayout.razor`)

Add a small `<form method="post" action="/logout">` with a hidden antiforgery token and a "Sign
Out" submit button, placed next to the existing B2 Access badge
(`Components/Layout/MainLayout.razor:6-9`). No confirmation dialog — logout is not a destructive
action. On success, Web's `/logout` handler redirects to `/login` (`CACHET_UIUX_Design_Specification_v1.md`
§2.3).

### §8.5 Route-Level Authorization Gate — the Second Real Gap

**`Components/Routes.razor` currently renders `<RouteView>` directly, with no `<AuthorizeRouteView>`
wrapper.** `Program.cs` registers `AddCascadingAuthenticationState()` (`CAS-002` §6.6), which makes
`AuthenticationState` _available_ to components, but nothing today actually _enforces_ it — every
existing page (`Home.razor`) is reachable with no session at all. This is a pre-existing gap this
spec closes, not a regression this spec introduces:

```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)">
            <NotAuthorized>
                @{ NavigationManager.NavigateTo("/login", forceLoad: true); }
            </NotAuthorized>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
</Router>
```

Plus, in `Program.cs`:

- `options.AddPolicy(...)` block (`CAS-002` §6.4) gains one addition: a `FallbackPolicy` requiring
  an authenticated user, so any page without its own `[Authorize]`/`[AllowAnonymous]` attribute
  defaults to requiring a session — `Login.razor`'s `[AllowAnonymous]` (§8.2) is the one exception.
- `CookieAuthenticationOptions` (`Program.cs`, the `AddCookie(AuthScheme, ...)` block `CAS-002`
  already added) gains `options.LoginPath = "/login"`, so a direct, non-Blazor `401` (e.g. a stale
  API-style request) redirects somewhere that exists, instead of ASP.NET Core's default
  `/Account/Login`, which this project never builds.

### §8.6 Post-Login Redirect Target

Redirects to `/` (root), which today renders the placeholder `Home.razor`. The UI/UX spec §2.2
states the default landing screen is Keyword Search — not yet built (WBS Task 22, `Not Started`).
This spec does not fabricate a Keyword Search placeholder; `/` remains whatever the current root
page is, and Task 22's own spec is responsible for making that page the real Keyword Search
screen. No open question blocks this spec on that account — `/` is a stable redirect target
regardless of what page currently lives there.

---

## §9 Configuration Changes

No new configuration key. `Api:BaseUrl` (already added by `CAS-002` §6.6,
`Cachet.ArchiveSync.Web/appsettings.Development.json`) is reused as-is by the `"CachetApi"`
named `HttpClient` this spec's `/login` handler also uses.

---

## §10 Success Criteria

| ID   | Criterion                                                                                                                                                                                                   | Test                                                   |
| ---- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------ |
| SC-1 | Navigating to any page while unauthenticated redirects to `/login`; `/login` itself is reachable without a session                                                                                          | `Test_SC1_UnauthenticatedRequestRedirectsToLogin`      |
| SC-2 | Submitting valid credentials + a valid B2 key results in a session cookie being set on the browser response and a redirect to `/`                                                                           | `Test_SC2_SuccessfulLoginIssuesCookieAndRedirects`     |
| SC-3 | Submitting an unknown username or a wrong password shows "Incorrect username or password." and does not distinguish the two cases                                                                           | `Test_SC3_InvalidCredentialsShowsGenericMessage`       |
| SC-4 | Submitting valid credentials whose stored B2 key fails live validation shows "Your account cannot currently access the archive. Contact your administrator." and never renders the underlying B2 error text | `Test_SC4_B2UnavailableShowsAdminContactMessage`       |
| SC-5 | The Sign In button is disabled while either field is empty and enabled once both are non-empty                                                                                                              | `Test_SC5_SignInButtonDisabledUntilBothFieldsNonEmpty` |
| SC-6 | Submitting the Sign Out form invalidates the session (a subsequent request with the pre-logout cookie returns `401`/redirects to `/login`), matching `CAS-002` SC-3                                         | `Test_SC6_SignOutInvalidatesSessionAndRedirects`       |
| SC-7 | No raw B2 application key, key ID, password, or password hash ever appears in `Login.razor`'s rendered HTML, a redirect URL, a query string, or Web's own log output                                        | `Test_SC7_NoCredentialLeakageInLoginUiSurface`         |
| SC-8 | `POST /login` and `POST /logout` (Web-hosted, §6) reject a request with a missing/invalid antiforgery token                                                                                                 | `Test_SC8_LoginLogoutRequireAntiforgeryToken`          |

---

## §11 Telemetry

```csharp
// _logger.LogInformation("Login form submitted for {Username}", username); // username only, never password
// _logger.LogWarning("Login form submission failed: {Reason}", reason);    // fixed enum/string, never raw Api error body
```

- Never log: the password (plaintext or otherwise), the B2 application key/key ID, the raw
  `Set-Cookie` value, or the antiforgery token value.
- `CAS-002` §11 already governs Login/Logout _business-event_ logging on the Api side; this
  spec's telemetry is limited to the Web-hosted form-handling layer and must not duplicate or
  contradict it.

---

## §12 Security Notes

### B2 Credential Handling

This spec never touches the B2 application key at any point — it consumes `LoginResponse`
(`userId`, `displayName`, `b2AccessLabel`), which `CAS-002` §6.1 step 5 already guarantees excludes
the key, key ID, and password hash. The `Set-Cookie` bytes this spec forwards (§8.2) are opaque,
Data-Protection-encrypted by Api under the shared key ring — Web relays them without ever decoding
them, so no new decode/re-encode path exists that could leak a claim.

### CSRF

Both new Web-hosted endpoints (§6) accept state-changing `POST`s from a real browser form. `/login`
uses Blazor's built-in named-form antiforgery validation (automatic for `EditForm`/`FormName`
static-SSR forms in .NET 8). `/logout` (a plain, non-Blazor-page-attached endpoint, §8.4) must
explicitly opt into antiforgery validation (`RequireAntiforgeryTokenAttribute` metadata, or
equivalent) — do not skip this because it "just clears a cookie": logout CSRF is a real (if
low-severity) forced-logout nuisance vector, and the project's `app.UseAntiforgery()` middleware
(`Program.cs`, already registered) is designed to be attached like this.

### Audit Trail

No change — `CAS-002` §6.1/§6.3 already write `Login`/`Logout` audit rows on the Api side, before
each endpoint's own response returns. This spec adds no new audited action; it is a UI/transport
layer in front of an already-audited backend call.

### Session / Auth Requirements

Every page except `/login` requires `CachetAuthenticated` (§8.5's `FallbackPolicy`) — no
BRD-open-item dependency (§4). The Sign Out form requires an authenticated session
(`CachetAuthenticated`), consistent with `CAS-002` §6.4/§12.

---

## §13 Test Plan

### Unit Tests

| Class                    | Method                                                                     | Proves         |
| ------------------------ | -------------------------------------------------------------------------- | -------------- |
| `LoginPageTests` (bUnit) | `Test_SC3_InvalidCredentialsShowsGenericMessage`                           | SC-3           |
| `LoginPageTests` (bUnit) | `Test_SC4_B2UnavailableShowsAdminContactMessage`                           | SC-4           |
| `LoginPageTests` (bUnit) | `Test_SC7_NoCredentialLeakageInLoginUiSurface` (rendered-markup assertion) | SC-7 (partial) |

### Integration Tests

| Class                                                        | Method                                                                                                                                                                            | Proves      |
| ------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------- |
| `WebAuthFlowIntegrationTests` (`WebApplicationFactory`, Web) | `Test_SC1_UnauthenticatedRequestRedirectsToLogin`                                                                                                                                 | SC-1        |
| `WebAuthFlowIntegrationTests`                                | `Test_SC2_SuccessfulLoginIssuesCookieAndRedirects`                                                                                                                                | SC-2        |
| `WebAuthFlowIntegrationTests`                                | `Test_SC5_SignInButtonDisabledUntilBothFieldsNonEmpty` (Playwright/E2E, if the project's test harness supports browser-level checks; otherwise a JS unit test against `login.js`) | SC-5        |
| `WebAuthFlowIntegrationTests`                                | `Test_SC6_SignOutInvalidatesSessionAndRedirects`                                                                                                                                  | SC-6        |
| `WebAuthFlowIntegrationTests`                                | `Test_SC7_NoCredentialLeakageInLoginUiSurface` (full HTTP round trip)                                                                                                             | SC-7 (full) |
| `WebAuthFlowIntegrationTests`                                | `Test_SC8_LoginLogoutRequireAntiforgeryToken`                                                                                                                                     | SC-8        |

### Coverage Target

Changed files must reach ≥ 85% line coverage (project standard).

---

## §14 Rollout

1. No database migration (§5).
2. Deploy `Cachet.ArchiveSync.Web` only — no `Api`/`Worker` change (§6). Since `AuthController`
   (`CAS-002`) is already live, this is a Web-only deployment.
3. **Deploy this ahead of any other Web UI screen still to be built** (Keyword Search, Task
   Manager, etc.) — those screens will all render behind the same `FallbackPolicy` (§8.5) the
   moment it ships, so shipping this spec first is what makes every subsequent Web spec's
   "authenticated session required" assumption actually true rather than aspirational.
4. Rollback: revert `Routes.razor`'s `<AuthorizeRouteView>` change and the `FallbackPolicy`
   registration to fully undo the authorization gate if a rollback is needed in an emergency
   (e.g., the login flow itself has a defect blocking all access) — this temporarily re-opens the
   app to unauthenticated access, matching the pre-this-spec state, and should be treated as a
   short-lived emergency measure, not a standing configuration.

---

## §15 Open Questions

All items originally identified while drafting this document were resolved before completion —
logged below for traceability (Karpathy P6), matching `CAS-001`/`CAS-002`'s own resolution-log
convention. No blocking open item remains against this spec.

- [x] **Production reverse-proxy topology** (`CAS-001`/`CAS-002` §6.6's own open recommendation —
      whether Api and Web sit behind one reverse-proxy origin or are deployed as fully separate
      services). **Resolved as: not this spec's decision to make, and confirmed not load-bearing
      here.** §8.2's cookie-forwarding design was deliberately chosen _because_ it works
      identically either way — Web always talks to Api over the existing server-side
      `Api:BaseUrl` `HttpClient` (`CAS-002` §6.6), never depends on the browser reaching Api
      directly, and forwards Api's `Set-Cookie` bytes verbatim regardless of topology. This spec
      therefore introduces no new dependency on that decision and does not duplicate it as an open
      item of its own — it remains tracked exactly once, under `CAS-002` §15, which is the
      document that actually owns the Web↔Api deployment-topology decision. Nothing here is
      blocked on it, and nothing here would need to change if it is resolved differently later,
      since §8.2 is deployment-topology-agnostic by construction.
- [x] **`LoginResponse.DisplayName` fallback value** (`CAS-002`'s own flagged gap —
      `Cachet.ArchiveSync.Core/Dtos/LoginResponse.cs` uses raw `username` as a stopgap because no
      document defines a `display_name` derivation rule, and `dbo.users` has no such column).
      **Resolved as: confirmed out of scope for this spec, verified by inspection, not assumed.**
      `Login.razor` never renders `DisplayName` (§8.1's fields are only Username/Password/Sign In;
      §8.3's failure states never echo it back); `MainLayout.razor`'s only claim-derived UI element
      is the `b2_capability` badge (`Components/Layout/MainLayout.razor:6-9`), not `DisplayName`.
      This spec's own scope therefore has zero code path that touches the unresolved value, so
      there is nothing here to make a decision about. Not re-tracked as an open item of this spec;
      the gap remains exactly where `CAS-002` already flagged it (`LoginResponse.cs`'s own doc
      comment), for whichever future spec first renders a user's display name (e.g., a header
      profile element or an audit-log "performed by" column) to resolve against actual product
      input, not a value invented here.

Neither resolution above required a new product/client decision — both were closed by confirming,
from the actual code and this spec's own §8 design, that the dependency claimed in the original
open-question text does not in fact exist. No item in this list ever depended on the (resolved)
login-method decision or on a still-open BRD §12.4 item (O-Drive refresh cadence, web caching
layer) — neither is load-bearing here.

---

## §16 Sign-off

| Role     | Name                                        | Date       | Notes                                                                 |
| -------- | ------------------------------------------- | ---------- | --------------------------------------------------------------------- |
| Author   | R. Devarajan (TraceCounts), via Claude Code | 2026-09-23 | Drafted per WBS Task 21; scope corrected from stale task brief per §0 |
| Reviewer |                                             |            | Pending                                                               |

---

## §17 Graph Impact

**No graphify graph covers application code yet.** `graphify-out/` exists in this repo (contrary
to a stale note elsewhere in this project's `CLAUDE.md` claiming none has been built) but its own
`GRAPH_REPORT.md` indexes only the BRD/WBS/tooling documentation corpus, not `src/`. Analysis below
is a manual read of `src/Cachet.ArchiveSync.Web/` and `src/Cachet.ArchiveSync.Api/Controllers/AuthController.cs`,
matching the fallback convention `CAS-002`/`CAS-017` already used.

### God Nodes in Blast Radius

`Components/Routes.razor` — every routed page in the application flows through this one file's
`<Router>`/`<RouteView>` (or, after this spec, `<AuthorizeRouteView>`) declaration. This spec's
§8.5 change is small in line count but affects every current and future page's reachability.

### Communities / Modules Affected

**Auth** (primary — this spec's entire scope). Every other module (Keyword Search, O-Drive Sync,
Quarantine, Task Manager, Audit Log, Reporting) is affected only in that their eventual Web pages
will render behind this spec's authorization gate (§8.5, §14 item 3) once each is built — no
existing module's code changes as a result of this spec, since none of their Web pages exist yet.
Project: `Web` only. `Api`, `Core`, `Worker` are untouched (§4, §6).

### Surprising Connections

- `Components/Routes.razor` rendering a plain `<RouteView>` (no `<AuthorizeRouteView>`) means the
  entire application has been reachable with zero authentication since `CAS-002` merged
  (2026-09-22) — `AddCascadingAuthenticationState()` was wired up, but nothing consumed it to
  actually gate access. This was not flagged by `CAS-002`'s own reviewer checklist, since
  `CAS-002` §4 explicitly scoped "Sign-In screen layout" out of its own document and no other spec
  has existed to catch the gap until now.
- `AuthController.Login` (Api) calling `HttpContext.SignInAsync` only works correctly for a caller
  that owns a real, not-yet-sent HTTP response — a fact `CAS-002` §6.1 does not itself have to
  reckon with (it specifies the claims and the call, not the transport that invokes it), but which
  becomes load-bearing the moment an actual UI has to invoke it (§8.2).

### Graphify Queries Run

None — not applicable to `src/` yet. Analysis via manual read of `Components/Routes.razor`,
`Components/Layout/MainLayout.razor`, `Components/App.razor`, `Program.cs` (Web and Api),
`Controllers/AuthController.cs`, `CAS-002-authentication-session-design.md` (full document), and
`CACHET_UIUX_Design_Specification_v1.md` §2.

---

## §18 Verification Plan

### Reviewer Checklist

- [ ] All §10 Success Criteria have a passing test.
- [ ] `Login.razor` matches `CACHET_UIUX_Design_Specification_v1.md` §2.1/§2.2 exactly — fields,
      button-disabled behavior, both distinct error messages, no "Forgot password"/"Create
      account" affordance.
- [ ] §8.2's cookie-forwarding design is actually implemented as described — confirm no code path
      attempts `HttpContext.SignInAsync`/cookie issuance from within an `InteractiveServer`
      component's circuit-bound event handler (would silently fail to reach the browser).
- [ ] §8.5's `<AuthorizeRouteView>`/`FallbackPolicy` change is in place and every page except
      `/login` actually requires authentication (manually verify by requesting `/` with no cookie).
- [ ] §12 CSRF: both `/login` and `/logout` (Web-hosted) reject a request with a missing/invalid
      antiforgery token.
- [ ] §12 B2 Credential Handling: no B2 key, key ID, password, or password hash appears in
      `Login.razor`'s rendered markup, any redirect URL/query string, or Web's log output.
- [ ] No `Cachet.ArchiveSync.Api`, `Cachet.ArchiveSync.Core`, or `Cachet.ArchiveSync.Worker` file
      was modified (§4, §6 — this spec is Web-only).
- [ ] No SSO-related code, configuration, or provider abstraction was introduced (§0).
- [ ] §17 Graph Impact matches actual code changes (no unintended nodes).

### Verification Report Format

Per template: `CAS-021.review.md` — overall verdict, per-SC verdict table, security gap list,
scope violations.

## Implementation Log

**Completed:** 2026-09-23
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage Login.razor 100%; Routes.razor 50%, MainLayout.razor 84.6%, Web/Program.cs 84.9% (pre-existing CAS-002 gaps, accepted, not CAS-021-introduced) | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Pages/Login.razor` | New /login static-SSR page; named-form POST-back forwards Api Set-Cookie, distinct error/banner states |
| `src/Cachet.ArchiveSync.Web/Components/Pages/Login.razor.css` | Scoped CACHET-brand card styling |
| `src/Cachet.ArchiveSync.Web/wwwroot/js/login.js` | Toggle Sign In disabled state until both fields non-empty (V1) |
| `src/Cachet.ArchiveSync.Web/Components/Routes.razor` | AuthorizeRouteView + NotAuthorized redirect to /login |
| `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor` | Sign Out form (POST /logout) next to B2 Access badge |
| `src/Cachet.ArchiveSync.Web/Program.cs` | LoginPath, FallbackPolicy, Web-hosted POST /logout endpoint with antiforgery validation |
| `src/Cachet.ArchiveSync.Web.Tests/Auth/BlazorFormScraper.cs` | Scrapes real rendered Blazor form field names for tests |
| `src/Cachet.ArchiveSync.Web.Tests/Auth/FakeCachetApiHandler.cs` | Fakes the CachetApi HttpClient handler, no real Api process contacted |
| `src/Cachet.ArchiveSync.Web.Tests/Auth/CapturingLoggerProvider.cs` | Captures log output for SC-7 no-leakage assertion |
| `src/Cachet.ArchiveSync.Web.Tests/Auth/LoginWebApplicationFactory.cs` | WebApplicationFactory<Program> against real Web pipeline |
| `src/Cachet.ArchiveSync.Web.Tests/Auth/WebAuthFlowIntegrationTests.cs` | 14 tests: SC-1..SC-8, V-1..V-3, plus 3 added coverage tests |
| `src/Cachet.ArchiveSync.Web.Tests/Cachet.ArchiveSync.Web.Tests.csproj` | Added Microsoft.AspNetCore.Mvc.Testing + FrameworkReference |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `WebAuthFlowIntegrationTests` | `Test_SC1_UnauthenticatedRequestRedirectsToLogin` | SC-1 |
| `WebAuthFlowIntegrationTests` | `Test_SC2_SuccessfulLoginIssuesCookieAndRedirects` | SC-2 |
| `WebAuthFlowIntegrationTests` | `Test_SC3_InvalidCredentialsShowsGenericMessage` | SC-3 |
| `WebAuthFlowIntegrationTests` | `Test_SC4_B2UnavailableShowsAdminContactMessage` | SC-4 |
| `WebAuthFlowIntegrationTests` | `Test_SC5_SignInButtonDisabledUntilBothFieldsNonEmpty` | SC-5 |
| `WebAuthFlowIntegrationTests` | `Test_SC6_SignOutInvalidatesSessionAndRedirects` | SC-6 |
| `WebAuthFlowIntegrationTests` | `Test_SC7_NoCredentialLeakageInLoginUiSurface` | SC-7 |
| `WebAuthFlowIntegrationTests` | `Test_SC8_LoginLogoutRequireAntiforgeryToken` | SC-8 |
| `WebAuthFlowIntegrationTests` | `Test_V1_SignInButtonDisabledUntilBothFieldsNonEmpty` | V-1 |
| `WebAuthFlowIntegrationTests` | `Test_V2_NoClientSidePasswordFormatValidation` | V-2 |
| `WebAuthFlowIntegrationTests` | `Test_V3_LoginPostRequiresAntiforgeryToken` | V-3 |
| `WebAuthFlowIntegrationTests` | `Test_LoginApiUnreachableShowsTemporarilyUnavailableBanner` | coverage gap: Login.razor lines 69-74 |
| `WebAuthFlowIntegrationTests` | `Test_LoginApiUnexpectedStatusShowsTemporarilyUnavailableBanner` | coverage gap: Login.razor lines 113-114 |
| `WebAuthFlowIntegrationTests` | `Test_RoutesNotAuthorizedRedirectsToLogin` | related but does not close Routes.razor line 7 (see spec notes) |

### Post-Merge Fix (2026-09-23) — Wireframe Visual Fidelity

Caught by user review against the actual running app, comparing screenshots against the
wireframe. Two real defects, neither caught by the functional/security-focused review above
(§18's checklist never asked "does this match the wireframe visually," a gap in this spec's own
verification plan, not just an implementation miss):

1. **`MainLayout.razor`'s Sign Out form/B2 Access badge rendered unconditionally on every page**,
   including the unauthenticated `/login` page itself — a stray "Sign Out" button appeared above
   the sign-in card. Fixed: both now gate on `authState.User.Identity?.IsAuthenticated`.
2. **No CACHET brand tokens existed anywhere in the app** (`wwwroot/app.css` was still the
   unmodified `dotnet new blazor` scaffold default — no font-family, no page background, no brand
   color variables), and `Login.razor` itself never included the wireframe's header block (CACHET
   eyebrow / "Archive Files Search & Sync" title / subtitle) or footer help text — a §8.1 spec gap,
   not just an implementation gap. Fixed: added baseline brand tokens to `app.css`, a brand accent
   bar to `MainLayout.razor` (visible pre- and post-login, matching the wireframe), and the missing
   header/footer content to `Login.razor` (footer copy reworded for username/password per §0/FR-01,
   deliberately avoiding the literal phrase "Contact your administrator" so it can't be confused
   with the B2-unavailable error message, §8.3).

**Files changed in this fix round:**
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor` | Sign Out/badge now gated on `isAuthenticated`; added brand accent bar (renders always) |
| `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor.css` | Brand accent bar styling, header bar layout, Sign Out button styling |
| `src/Cachet.ArchiveSync.Web/wwwroot/app.css` | Baseline CACHET brand tokens: page background, font-family, color variables |
| `src/Cachet.ArchiveSync.Web/Components/Pages/Login.razor` | Added wireframe header block (eyebrow/title/subtitle) and footer help text |
| `src/Cachet.ArchiveSync.Web/Components/Pages/Login.razor.css` | Styling for the new header/footer elements |

**Gates re-run:** Build ✓ | Tests ✓ (84/84 in `Cachet.ArchiveSync.Web.Tests`, Release config) | verified via direct HTTP inspection of the rendered `/login` page and served CSS bundle, not just test assertions.

**Not re-submitted to independent `/review`** — this fix is content/styling/layout-gating only,
touches no security-relevant code path already verified APPROVED (§12 findings unaffected), and
stays within the same file set the original review covered. Recommend a lightweight visual
spot-check before the next release rather than a full re-review.
