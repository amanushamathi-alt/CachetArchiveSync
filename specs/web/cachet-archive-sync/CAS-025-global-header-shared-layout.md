---
id: CAS-025
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-23
priority: P2
related_specs: [CAS-002, CAS-021, CAS-022, CAS-023, CAS-024]
touches:
  database: false
  api: false            # consumes CAS-002's already-issued session/claims and CAS-002's /logout as-is
  ui: true
  worker: false
  b2: false
  config: false
  security: true         # Sign Out completes an existing CAS-021 gap; no new credential surface
migrations: []
---

# CAS-025 — Global Header / Shared Layout Shell (Phase 1)

Cross-cutting UI infrastructure supporting WBS Tasks 21–24 (Web UI Development), not owned by a single
WBS task itself. UI contract fixed by `CACHET_UIUX_Design_Specification_v1.md` §1 (Global Layout and
Shared Components) and the client-reviewed wireframe screenshot. Implements the header-bar portion of
FR-02 (capability badge) and closes a gap `CAS-021` §3/§8.4 already scoped (Sign Out) but never built.

---

## §0 Scope Correction — Phase 1 Only (read this first)

**This is deliberately a partial build, not the full wireframe header.** The wireframe (and UI/UX spec
§1.1) shows a header with: wordmark, tab navigation, user identity + B2 badge, Sign Out, **Task Manager**
button+badge, **Quarantine Queue** button+badge, **Audit Log** button+badge, and **Pre-/Post-Action
Report** buttons. Of these:

- **Task Manager**: deliberately deferred project-wide — `CAS-022` §0/§15 already resolved (2026-09-23,
  R. Devarajan) not to build a generic Task Manager API/screen yet. Stays deferred here too; no button
  is added for it (adding a button with nothing behind it would be worse than omitting it).
- **Quarantine Queue**: `CAS-024` (Quarantine Queue Modal) exists as a spec but is `status: draft`, not
  yet implemented as of this spec's drafting. This spec adds the button, styled per the wireframe, but
  **disabled** (no click handler, no live count) until `CAS-024` ships — at which point `CAS-024` (or a
  small follow-up) wires the button to open its modal and show a real count, the same "endpoint built
  now, UI consumer wired once its own spec ships" pattern already used between `CAS-014`→`CAS-017`.
- **Audit Log**: only the API exists (`CAS-009`, merged) — no Audit Log screen/UI has ever been
  specified. Button added, styled, **disabled**, no count. A future spec owns the actual Audit Log
  screen and would then wire this button.
- **Pre-/Post-Action Report**: only the on-demand generation API exists (`CAS-015`, merged) — no report
  modal UI has ever been specified. Both buttons added, styled, **disabled**. A future spec owns the
  report modals and would then wire these buttons.

Building disabled-but-correctly-styled placeholders for these three, rather than omitting them entirely
or silently building their full modals/screens here, keeps this spec's own scope contained while still
visually matching the wireframe the user asked to match — a real, load-bearing distinction confirmed
with the user (2026-09-23) before drafting.

---

## §1 Summary

Builds the shared `MainLayout.razor` header bar every authenticated screen renders inside: the CACHET
wordmark/title, tab navigation between Keyword Search (`/`) and O-Drive Sync (`/odrive-sync`) — both of
which already exist (`CAS-022`, `CAS-023`) — the signed-in user's identity and B2 capability badge
(already partially built by `CAS-002`/`CAS-021`), and a working **Sign Out** button (spec'd by `CAS-021`
§3/§8.4, never actually implemented — confirmed by reading the current `MainLayout.razor`, which has
only the B2 badge fragment). Also adds the Quarantine Queue / Audit Log / Pre-Action Report /
Post-Action Report buttons in their wireframe-styled, disabled placeholder form (§0). Restyles
`KeywordSearch.razor`'s existing filter bar and three panels (brand colors, table borders, colored
status chips) to match the wireframe's visual language, since this spec's header work and that restyle
share the same CSS foundation (brand color tokens, typography) and were requested together.

---

## §2 User Story

As a **TraceCounts reviewer** signed into Cachet Archive Sync, I want every screen to show the same
consistent header — my identity, my B2 access level, a way to switch between Keyword Search and O-Drive
Sync, and a way to sign out — so that the application feels like one coherent tool rather than a set of
disconnected pages, and I can tell at a glance what's available versus not yet built.

---

## §3 In-Scope

- `MainLayout.razor` header bar, replacing the current bare B2-badge-only fragment:
  - Left: "CACHET" wordmark + "Archive Files Search & Sync" title (static).
  - Center: tab navigation — **Keyword Search** (`/`) | **O-Drive Sync and content Scan**
    (`/odrive-sync`) — active tab underlined in the brand red, using `NavLink`'s built-in active-route
    matching.
  - Right, row 1: signed-in user identity (claims-derived), **Sign Out** button (completes `CAS-021`
    §8.4's gap — a `<form method="post" action="/logout">` with antiforgery, per `CAS-021` §12), B2
    Access badge (already exists, restyled to match the wireframe's pill shape/colors).
  - Right, row 1 (disabled placeholders, §0): **Quarantine Queue** button (no count), **Audit Log**
    button (no count).
  - Right, row 2 (disabled placeholders, §0): **Pre-Action Report**, **Post-Action Report** buttons.
- Footer status bar (`CACHET_UIUX_Design_Specification_v1.md` §1.2) — the "not yet populated" static
  copy only; the populated-state variants require O-Drive/B2 mirror last-updated timestamps that no
  existing endpoint currently exposes, so only the wireframe-confirmed empty-state string is built here
  (flagged, not silently invented — see §15).
- Shared brand CSS (colors `#142A4D` navy, `#C0102A` red, white; typography) as a small shared
  stylesheet/CSS variables block, consumed by `MainLayout.razor` and referenced by `KeywordSearch.razor`
  for the restyle below.
- `KeywordSearch.razor` visual restyle only (no behavior change): filter bar layout/spacing, panel
  headers, table borders/spacing, and the three status chip colors (Flagged red, Needs Review
  amber/orange, Informational blue) per `CACHET_UIUX_Design_Specification_v1.md` §1.3.

---

## §4 Out-of-Scope

- **Task Manager button/screen** — deferred project-wide (§0; `CAS-022` §0/§15).
- **Quarantine Queue modal itself** — `CAS-024`'s scope; this spec only adds the (disabled) button.
- **Audit Log screen** — no spec exists yet; this spec only adds the (disabled) button.
- **Pre-/Post-Action Report modals** — no spec exists yet; this spec only adds the (disabled) buttons.
- **Footer's populated-state copy** (O-Drive/B2 mirror last-updated timestamps) — no endpoint exposes
  this data yet; flagged in §15, not built here.
- **`ODriveSyncContentScan.razor`'s own visual restyle** — out of this spec's scope; it already has its
  own merged spec (`CAS-023`) and its own styling. Only `KeywordSearch.razor` is restyled here, since
  that is the screen the user's screenshot compared against the wireframe.
- **Any `Cachet.ArchiveSync.Api`, `Core`, or `Worker` change** — this spec consumes `CAS-002`'s existing
  `/logout` endpoint and claims exactly as they exist today; no backend change.

---

## §5 Database Objects

N/A — `touches.database: false`.

---

## §6 API Endpoints

No new endpoint. Consumes `CAS-002`'s already-merged `POST /logout` (Api) via the identical
cookie-forwarding pattern `CAS-021` §8.4 already established (a Web-hosted, non-Blazor-page-attached
`/logout` handler, `[Authorize(Policy = "CachetAuthenticated")]`, antiforgery-protected) — **confirmed
by reading `MainLayout.razor` and `Program.cs`: this handler was never actually added**, only the
Api-side `POST /logout` exists. This spec adds the Web-hosted `/logout` handler `CAS-021` §6/§8.4
already specified but left unbuilt.

| Route | Method | Auth | Purpose |
|---|---|---|---|
| `/logout` (Web-hosted, new — closes a `CAS-021` gap) | `POST` | `[Authorize(Policy = "CachetAuthenticated")]`, antiforgery-protected | Relays to Api's `POST /logout` via the existing `"CachetApi"` cookie-forwarding client, forwards the expired `Set-Cookie` back to the browser, redirects to `/login`. |

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `/logout` (Web-hosted) rejects any request without a valid antiforgery token | `400` | `Test_V1_LogoutRequiresAntiforgeryToken` |
| V2 | Quarantine Queue / Audit Log / Pre-Action Report / Post-Action Report buttons render `disabled` and have no click handler wired | N/A — enforced by markup; proven by absence of any registered click behavior | `Test_V2_PlaceholderHeaderButtonsAreDisabledWithNoHandler` |
| V3 | Tab navigation's active-tab underline reflects the current route (`/` vs `/odrive-sync`) via `NavLink`'s built-in matching, not manual string comparison | Wrong tab underlined | `Test_V3_ActiveTabUnderlineMatchesCurrentRoute` |

---

## §8 UI / UX

### §8.1 Header Bar — `MainLayout.razor`

Exactly `CACHET_UIUX_Design_Specification_v1.md` §1.1, scoped per §0:

| Region | Contents | Notes |
|---|---|---|
| Left | "CACHET" wordmark + "Archive Files Search & Sync" title | Static; matches wireframe copy verbatim. |
| Center | `NavLink` tabs: **Keyword Search** (`/`, `Match=NavLinkMatch.All`) \| **O-Drive Sync and content Scan** (`/odrive-sync`) | `NavLink`'s `active` CSS class (Blazor built-in) drives the red underline — no manual route-string comparison (V3). |
| Right, row 1 | User identity (claims-derived display, per `MainLayout.razor`'s existing `AuthenticationStateTask` pattern), **Sign Out** button | Sign Out per §8.4 below. Display name uses the same `username`-as-stopgap value `CAS-021` §15 already flagged as an open, not-yet-resolved gap (`LoginResponse.DisplayName`) — not re-litigated here. |
| Right, row 1 | **B2 Access badge** | Already exists (`b2_capability` claim read); restyled only — pill shape, brand colors, no behavior change. |
| Right, row 1 (disabled) | **Quarantine Queue** button, no count badge | §0 — `disabled`, `title="Coming soon"` tooltip, no `@onclick`. |
| Right, row 1 (disabled) | **Audit Log** button, no count badge | §0 — same disabled treatment. |
| Right, row 2 (disabled) | **Pre-Action Report**, **Post-Action Report** buttons | §0 — same disabled treatment. |

**(design decision, not BRD-specified):** disabled buttons show a `title` tooltip ("Coming soon") on
hover rather than being hidden entirely — this was the specific alternative the user confirmed over
omitting them (§0), since the goal is visual fidelity to the wireframe now, with functionality following
per-button as each one's own spec ships.

### §8.2 Footer Status Bar

Wireframe-confirmed empty-state copy only: `Local Cache: not yet populated.` The two populated-state
variants (`CACHET_UIUX_Design_Specification_v1.md` §1.2) are **not** built here — no existing endpoint
exposes either mirror's last-updated timestamp (flagged, §15). Showing only the empty-state string
regardless of actual mirror population is a known, temporary inaccuracy, not a defect introduced by this
spec — the alternative (fabricating a timestamp) would be worse.

### §8.3 Sign Out

`<form method="post" action="/logout">` with a hidden antiforgery token, placed next to the B2 Access
badge, matching `CAS-021` §8.4's original design exactly (this spec is simply the implementation of that
already-approved design, not a new decision). On success, Web's `/logout` handler redirects to `/login`.

### §8.4 Tab Navigation Target Pages

Both tabs route to pages that already exist and are already `[Authorize]`-protected in their own right
(`KeywordSearch.razor`, `ODriveSyncContentScan.razor` — both fixed with explicit `[Authorize]` attributes
during this session's earlier bug investigation). This spec adds no new authorization surface for either
target page.

### §8.5 `KeywordSearch.razor` Visual Restyle

No behavior change — purely CSS/markup polish to match the wireframe screenshot:
- Filter bar: input/dropdown sizing, spacing, and the primary "Search" button's navy/red brand styling.
- **Three-column panel layout** — Filename Matches, Content Matches, and Quarantine side by side
  (wireframe-confirmed, §1.3), not stacked vertically. Added 2026-09-23 after the initial restyle
  shipped without a wrapping row container, defaulting to a vertical block stack — corrected per
  direct user feedback comparing the live screen against the wireframe. Wraps to a vertical stack
  only below a narrow viewport width (a fixed 3-column layout would not fit a phone-width screen;
  not itself wireframe-specified, a reasonable responsive default).
- Panel headers: bold, with the wireframe's exact count-badge placement (e.g., "Filename Matches 5").
- Table borders/row spacing to match the wireframe's grid look.
- Status chips: **Flagged** = red pill, **Needs Review** = amber/orange pill, **Informational** = blue
  pill (`CACHET_UIUX_Design_Specification_v1.md` §1.3) — replacing whatever default/unstyled rendering
  exists today.
- The Quarantine panel's already-decided-to-omit checkbox column (`CAS-022` §8.4/§15) is unaffected by
  this restyle — still omitted, not reintroduced.

---

## §9 Configuration Changes

None.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `MainLayout.razor` renders the wordmark, tab navigation, user identity, B2 badge, and Sign Out button on every authenticated page | `Test_SC1_HeaderRendersAllCoreElements` |
| SC-2 | The active tab (Keyword Search vs. O-Drive Sync) is underlined correctly based on the current route | `Test_SC2_ActiveTabReflectsCurrentRoute` |
| SC-3 | Clicking Sign Out invalidates the session (a subsequent request with the pre-logout cookie is unauthenticated) and redirects to `/login` | `Test_SC3_SignOutInvalidatesSessionAndRedirects` |
| SC-4 | The Web-hosted `/logout` handler rejects a request with a missing/invalid antiforgery token | `Test_SC4_LogoutRequiresAntiforgeryToken` |
| SC-5 | Quarantine Queue, Audit Log, Pre-Action Report, and Post-Action Report buttons render visibly styled but `disabled`, with no click behavior | `Test_SC5_PlaceholderButtonsDisabledAndInert` |
| SC-6 | The footer shows exactly the wireframe's "Local Cache: not yet populated." copy | `Test_SC6_FooterShowsEmptyStateCopy` |
| SC-7 | `KeywordSearch.razor`'s three status chip types render with the correct distinct colors (Flagged red, Needs Review amber, Informational blue) | `Test_SC7_StatusChipsRenderCorrectColors` |
| SC-8 | No B2 application key, session cookie value, or any other credential ever appears in `MainLayout.razor`'s rendered markup or Web's log output | `Test_SC8_NoCredentialLeakageInHeaderMarkupOrLogs` |

---

## §11 Telemetry

```csharp
// _logger.LogInformation("Logout form submitted for {UserId}", userId); // user id only
```

- Never log: the password, the B2 application key/key ID, the session/cookie value, the antiforgery
  token value.

---

## §12 Security Notes

### B2 Credential Handling
No new B2 interaction. The B2 badge continues reading only the `b2_capability` claim, as `CAS-002`
already established.

### CSRF
The new Web-hosted `/logout` endpoint requires antiforgery validation, identical to `CAS-021` §12's
existing treatment of this exact (previously unbuilt) endpoint — this spec is simply building what that
section already specified.

### Audit Trail
No change — `CAS-002` §6.3 already writes the `Logout` audit row on the Api side, before its own
response returns. This spec adds no new audited action.

### Session / Auth Requirements
`/logout` requires `CachetAuthenticated`, matching `CAS-021` §12. The header itself renders inside
`MainLayout.razor`, which every `[Authorize]`-protected page already uses — no new gate is introduced or
required beyond what `CAS-022`/`CAS-023`'s own per-page `[Authorize]` attributes already enforce.

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `MainLayoutTests` (bUnit) | `Test_SC1_HeaderRendersAllCoreElements`, `Test_SC2_ActiveTabReflectsCurrentRoute`, `Test_SC5_PlaceholderButtonsDisabledAndInert`, `Test_SC6_FooterShowsEmptyStateCopy`, `Test_V2_PlaceholderHeaderButtonsAreDisabledWithNoHandler`, `Test_V3_ActiveTabUnderlineMatchesCurrentRoute` | SC-1, SC-2, SC-5, SC-6, V2, V3 |
| `KeywordSearchPageTests` (bUnit, extending the existing `CAS-022` test class) | `Test_SC7_StatusChipsRenderCorrectColors` | SC-7 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `WebAuthFlowIntegrationTests` (extending the existing `CAS-021` test class, `WebApplicationFactory`) | `Test_SC3_SignOutInvalidatesSessionAndRedirects`, `Test_SC4_LogoutRequiresAntiforgeryToken` | SC-3, SC-4, V1 |
| Log-output assertion (test log sink) | `Test_SC8_NoCredentialLeakageInHeaderMarkupOrLogs` | SC-8 |

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. No database migration (§5).
2. Deploy `Cachet.ArchiveSync.Web` only — no `Api`/`Core`/`Worker` change (§6, §4).
3. No sequencing dependency — `CAS-022`/`CAS-023` (the tab targets) and `CAS-021` (the login/logout
   session mechanics) are all already merged.
4. Rollback: revert `MainLayout.razor`/its stylesheet and the new `/logout` handler; both are additive
   UI changes with no schema/Api impact.

---

## §15 Open Questions

Both items originally raised while drafting this spec were resolved directly with R. Devarajan,
TraceCounts (Solution Owner), 2026-09-23 — logged below for traceability. No blocking open item
remains against this spec.

- [x] **Footer populated-state copy — resolved 2026-09-23 (R. Devarajan, TraceCounts): ship the static
      empty-state copy now.** No existing endpoint exposes O-Drive/B2 mirror last-updated timestamps.
      The footer shows the wireframe's unconditional "Local Cache: not yet populated." text in the
      meantime — a known, disclosed inaccuracy once a mirror actually populates, to be corrected by
      whichever future spec adds real mirror-population monitoring. Not a blocker for this spec.
- [x] **Quarantine Queue / Audit Log / Report button wiring sequencing — resolved 2026-09-23
      (R. Devarajan, TraceCounts): each spec wires its own button.** `CAS-024` flips the Quarantine
      Queue button from disabled to active when it ships; a future Audit Log screen spec flips that
      button; a future Report modal spec flips both Report buttons. No separate "wire the remaining
      header buttons" follow-up spec is needed.
- Not blocked by BRD §12.4 (web caching layer, O-Drive refresh cadence) — unrelated to this spec's
  scope.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-23 | Drafted per user request to restyle Keyword Search to match the wireframe; scoped to Phase 1 (header shell with existing targets only) per user confirmation |
| Reviewer | | | Pending |

---

## §17 Graph Impact

**No graphify graph covers application code yet** (contrary to a stale CLAUDE.md note — `graphify-out/`
exists but indexes only the BRD/WBS/spec documentation corpus, matching the same disclosed limitation
`CAS-021`/`CAS-022`/`CAS-023` already recorded). Analysis via manual read of
`src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor` (confirming it currently has only the
B2-badge fragment — no wordmark, no tab nav, no Sign Out), `Components/Routes.razor`, `Program.cs`
(confirming `POST /logout`'s Web-hosted handler was never added, only Api's own endpoint exists), and
the three existing page components (`Login.razor`, `KeywordSearch.razor`, `ODriveSyncContentScan.razor`).

### God Nodes in Blast Radius
`MainLayout.razor` — every authenticated page in the application renders inside it (`Components/App.razor`
→ `Routes.razor` → `AuthorizeRouteView` → `MainLayout`). This spec's changes are visually small per page
but touch the one file every current and future screen depends on.

### Communities / Modules Affected
Cross-cutting — Auth (Sign Out, completing a `CAS-021` gap), Keyword Search (restyle only, `CAS-022`),
O-Drive Sync (tab target only, no own-screen change, `CAS-023`). Project: `Web` only.

### Surprising Connections
- `CAS-021` §3 explicitly listed "a minimal Sign Out affordance in `MainLayout.razor`" as in-scope, and
  §8.4 fully designed it — but a direct read of the current `MainLayout.razor` confirms it was never
  actually built. This is the same class of "spec described it, implementation shipped without it" gap
  `CAS-014`→`CAS-017` and `CAS-006`→(embedded Quarantine panel) already encountered elsewhere in this
  project; not a regression introduced by this spec, a pre-existing gap this spec closes.
- This session's own earlier bug investigation (this same conversation) already added explicit
  `[Authorize]` attributes to `KeywordSearch.razor`/`ODriveSyncContentScan.razor` after removing
  `Program.cs`'s `FallbackPolicy` — confirmed those attributes are still in place and this spec does not
  need to touch page-level authorization again.

### Graphify Queries Run
None — not applicable to `src/` yet (documentation-only graph). Manual code read only, per the
established fallback convention.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test.
- [ ] All §7 Validations are implemented and tested.
- [ ] `MainLayout.razor` matches `CACHET_UIUX_Design_Specification_v1.md` §1.1 for the elements this
      spec builds, with the four documented disabled-placeholder deviations (§0) and no others.
- [ ] The new Web-hosted `/logout` handler requires antiforgery and `CachetAuthenticated`, and
      correctly forwards Api's expired `Set-Cookie` (same pattern as `CAS-021` §8.2's login handler,
      reversed).
- [ ] Quarantine Queue / Audit Log / Pre-Action Report / Post-Action Report buttons are genuinely
      inert — no click handler, no hidden navigation.
- [ ] `KeywordSearch.razor`'s restyle changes no behavior — all `CAS-022` tests still pass unmodified
      (beyond the new SC-7 chip-color test).
- [ ] No `Cachet.ArchiveSync.Api`, `Core`, or `Worker` file was modified.
- [ ] §17 Graph Impact matches actual code changes.

### Verification Report Format
Per template: `CAS-025.review.md` with overall verdict, per-SC verdict table, security gap list, scope
violations.

## Implementation Log

**Completed:** 2026-09-23
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage MainLayout.razor 92.0%, KeywordSearch.razor 98.3% | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor` | Rewritten — wordmark, NavLink tab nav, user identity, restyled B2 badge, existing Sign Out form restyled, four inert disabled placeholder buttons, static footer |
| `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor.css` | New — header/footer/tab/badge/disabled-button styles |
| `src/Cachet.ArchiveSync.Web/wwwroot/app.css` | Modified — extended :root brand-color tokens (white + three chip colors) |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | Modified — markup-only: status chip spans use new ChipClass(status) helper; added a `cachet-ks-results-row` wrapper around the three result panels (follow-up, §8.5) — no behavior change either time |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor.css` | Modified — filter bar/table/panel restyle, three distinct chip color classes; added `.cachet-ks-results-row` flex layout (follow-up, §8.5) so the three panels render side by side, wrapping to vertical only on narrow viewports |
| `src/Cachet.ArchiveSync.Web.Tests/Layout/MainLayoutTests.cs` | New — bUnit tests for SC-1, SC-2, SC-5, SC-6, SC-8, V-2, V-3 |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchPageTests.cs` | Modified — added Test_SC7_StatusChipsRenderCorrectColors |
| `src/Cachet.ArchiveSync.Web/Program.cs` | Modified — added the Web-hosted `POST /logout` handler §6 describes (antiforgery validation, `CachetAuthenticated` policy, cookie-forwarding to Api's `/logout`, `Set-Cookie` relay, redirect to `/login`). Omitted from this table in the original implementation log — added 2026-09-23 per `CAS-025.review.md` §5/§10/§11's traceability finding. |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `MainLayoutTests` | `Test_SC1_HeaderRendersAllCoreElements` | SC-1 |
| `MainLayoutTests` | `Test_SC2_ActiveTabReflectsCurrentRoute` | SC-2 |
| `MainLayoutTests` | `Test_V3_ActiveTabUnderlineMatchesCurrentRoute` | V-3 |
| `MainLayoutTests` | `Test_SC5_PlaceholderButtonsDisabledAndInert` | SC-5 |
| `MainLayoutTests` | `Test_V2_PlaceholderHeaderButtonsAreDisabledWithNoHandler` | V-2 |
| `MainLayoutTests` | `Test_SC6_FooterShowsEmptyStateCopy` | SC-6 |
| `MainLayoutTests` | `Test_SC8_NoCredentialLeakageInHeaderMarkupOrLogs` | SC-8 |
| `KeywordSearchPageTests` | `Test_SC7_StatusChipsRenderCorrectColors` | SC-7 |
| `WebAuthFlowIntegrationTests (CAS-021, pre-existing)` | `Test_SC6_SignOutInvalidatesSessionAndRedirects` | SC-3 (already covered, no duplicate written) |
| `WebAuthFlowIntegrationTests (CAS-021, pre-existing)` | `Test_SC8_LoginLogoutRequireAntiforgeryToken / Test_V3_LoginPostRequiresAntiforgeryToken` | SC-4, V-1 (already covered, no duplicate written) |
