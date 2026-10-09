---
id: CAS-022
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-23
priority: P1
related_specs: [CAS-006, CAS-008, CAS-021]
touches:
  database: false
  api: false # consumes CAS-006/CAS-008's already-merged/ready contracts as-is; no Api/Core change
  ui: true
  worker: false
  b2: false # no direct B2 call — all B2 interaction already happens server-side inside CAS-006/CAS-008
  config: true # amended 2026-09-23 during /implement — Web reads B2:BucketName directly; see §9, §14
  security: true # session-gated screen; no new credential surface, but §12 confirms this explicitly
migrations: []
---

# CAS-022 — Keyword Search Screen (Filters, Filename/Content Match Panels, Embedded Quarantine Panel)

WBS Task 22 (Web UI Development — Keyword Search screen). Implements FR-04–FR-09, FR-21. UI contract
fixed by `CACHET_UIUX_Design_Specification_v1.md` §1.3, §1.4, §3, §10; API contract fixed by `CAS-006`
(merged, 2026-09-22) and `CAS-008` (status `ready`, controller already in code).

---

## §0 Scope Correction and Load-Bearing Gap (read this first)

**The task brief asked for auto-population "when the Task Manager reports the task complete" (FR-21).**
A manual read of `src/Cachet.ArchiveSync.Api/Controllers/TaskManagerController.cs` shows this is not
yet buildable as literally worded: that controller hosts only `POST /tasks/{taskId}/restart` (`CAS-016`,
merged). **No `GET /tasks` or `GET /tasks/{taskId}` status endpoint exists anywhere in this codebase or
in any spec** — `CAS-006` §4 and `CAS-016` §6 both explicitly deferred the generic Task Manager
read/list API to "a future Task Manager API spec," which has not been drafted (WBS Task 25 —
`Not Started`). There is also no Task Manager screen, no global header (Task Manager button, Quarantine
Queue button, Audit Log button, Pre-/Post-Action Report buttons — `CACHET_UIUX_Design_Specification_v1.md`
§1.1) built anywhere in `src/Cachet.ArchiveSync.Web/` yet (only `MainLayout.razor`'s B2-badge fragment
exists, from `CAS-002`/`CAS-021`).

Per Karpathy P2 ("spec is silent → flag, don't invent"), this spec does **not** fabricate a generic Task
Manager API or a global header to satisfy the letter of "reports via Task Manager." Instead, it builds
the behavior FR-21 actually requires — the Keyword Search screen detects its own submitted task's
completion and auto-populates — using the **one status-bearing endpoint that already exists for this
exact task type**: `CAS-006`'s `GET /search/{taskId}/results`, which already returns `409
task-not-complete` while `Queued`/`Running` and `200` with results (or `failureReason`) once
`Completed`/`Failed` (`CAS-006` §6.2). This satisfies the UI/UX spec §10 behavior ("panel(s) update in
place... no manual refresh or navigation required") without depending on unbuilt infrastructure. The
global header, Task Manager screen, and Task Manager badge/notification (FR-22) are out of scope here
(§4) and are a known, tracked gap for a future Task Manager spec to close — not silently absorbed into
this one.

---

## §1 Summary

Builds `KeywordSearch.razor` — the default landing screen (per `CACHET_UIUX_Design_Specification_v1.md`
§2.2) a signed-in user sees: a filter bar (bucket, extension, free-text term, Search button), two async
result panels (Filename Matches, Content Matches), and a read-only embedded Quarantine panel, wired to
`CAS-006`'s three endpoints and `CAS-008`'s `GET /quarantine`. Search submission is asynchronous
(FR-18) — the screen shows a "queued/processing" banner immediately, then polls its own task's status
and auto-populates the two match panels in place once the task completes, without a page reload.
Checkbox-select-to-quarantine (FR-09) lets a user send one or more matched rows from either panel to the
Quarantine Queue, which refreshes the embedded Quarantine panel in place. Implements FR-04, FR-05,
FR-06, FR-07, FR-08 (indirectly — cross-check already happened server-side in `CAS-006`'s Worker), FR-09,
FR-21 (scoped per §0).

---

## §2 User Story

As a **TraceCounts reviewer** signed into Cachet Archive Sync, I want to filter and submit a keyword
search, see a clear "processing" indicator while it runs, have the Filename Matches and Content Matches
panels populate automatically the moment results are ready, and be able to select matched rows and send
them to Quarantine — all without leaving the screen or manually refreshing — so that I can review search
results and act on them in one continuous workflow.

---

## §3 In-Scope

- `KeywordSearch.razor` (`@page "/"`, `InteractiveServer` render mode — required for the in-place
  polling/update behavior; see §8.2 for why this differs from `CAS-021`'s static-SSR `Login.razor`):
  - Filter bar: free-text term input, bucket dropdown, extension dropdown (`All | PDF | DOC | DOCX |
    XLS | XLSX | TXT` — FR-04's expanded list), Search button (§8.1).
  - **Filename Matches** panel: checkbox, Name/Path, Size, Modified, Status chip (Flagged/Informational).
  - **Content Matches** panel: checkbox, Name/Path, Match Snippet, Modified, Status chip (Needs Review).
  - **Quarantine** panel (read-only, embedded): Name/Path, Quarantined On, Status chip — no checkbox
    column (resolved 2026-09-23, R. Devarajan — §15) — a live view of `GET /quarantine`'s `Active` rows
    (§8.4).
  - Per-panel **Send to Quarantine** button (Filename/Content panels only), disabled until ≥1 row
    checked, enabled with a count label (FR-09; UI/UX spec §1.4).
  - Async submission UX: toast on submit, in-progress banner while polling, auto-populate on
    completion, failure banner on `Failed` (§8.2, §8.3 — FR-18, FR-21, FR-22a).
  - Empty/error/zero-match states exactly per UI/UX spec §3.4 (§8.5).
- Client-side non-empty/format validations that mirror `CAS-006` §7 (V1–V3) so the user gets immediate
  feedback before a round trip, without duplicating server-side enforcement (§7).
- Polling mechanism (server-side `PeriodicTimer` inside the component, per Blazor Server idiom) against
  `GET /search/{taskId}/results` while the submitted task is not yet complete (§0, §8.2).

---

## §4 Out-of-Scope

- **Generic Task Manager API and screen** (`GET /tasks`, `GET /tasks/{taskId}`,
  `POST /tasks/{taskId}/cancel`; FR-19–FR-22) — genuinely unbuilt (§0); this spec's own polling of its
  own task via `CAS-006`'s existing endpoint is a scoped substitute, not a Task Manager implementation.
- **Global header bar** (`CACHET_UIUX_Design_Specification_v1.md` §1.1 — wordmark, tab navigation
  between Keyword Search / O-Drive Sync, Task Manager button+badge, Quarantine Queue button+badge, Audit
  Log button+badge, Pre-/Post-Action Report buttons, Sign Out) and **footer status bar** (§1.2) — none
  exist in `src/Cachet.ArchiveSync.Web/` yet; building the full header/footer/tab-navigation shell is a
  separate, cross-cutting layout spec this screen would otherwise duplicate piecemeal. `KeywordSearch.razor`
  renders inside the existing bare `MainLayout.razor` as-is.
- **Quarantine Queue modal** (`CACHET_UIUX_Design_Specification_v1.md` §4 — the full modal with Delete
  Permanently, second-confirmation dialog) — this screen's Quarantine panel is the read-only embedded
  view only (§1.4, §3.2 of the UI/UX spec); the modal itself is `CAS-017`'s and a future Delete
  Permanently UI spec's scope, not this one's.
- **O-Drive Sync and Content Scan screen** (FR-10–FR-13d) — separate tab, separate spec.
- **Pre-/Post-Action Report modals** (FR-25–FR-27) — separate, header-triggered feature.
- **Any `Cachet.ArchiveSync.Api`, `Core`, or `Worker` change** — `CAS-006`/`CAS-008`'s contracts are
  consumed exactly as merged/ready; no field, status code, or route is added or altered.
- **Drive encryption-at-rest / O-Drive refresh cadence / web caching layer mechanism** (BRD §12.4, still
  open) — not depended on; this screen always calls the live API, never assumes a caching layer exists
  (§10 note on "served from cache" is deferred to whichever spec resolves §12.4, consistent with the
  UI/UX spec's own framing of that item as open).

---

## §5 Database Objects

N/A — `touches.database: false`. No schema change. Consumes `dbo.search_result`/`dbo.quarantine_entry`
only indirectly, through `CAS-006`'s and `CAS-008`'s already-built endpoints.

---

## §6 API Endpoints

No `Cachet.ArchiveSync.Api` endpoint is added or changed (`touches.api: false`). This screen is a pure
consumer of three already-merged/ready contracts, exactly as specified — restated here only to fix the
build target, not to redefine it:

| Endpoint | Source spec | Used for |
|---|---|---|
| `POST /search` | `CAS-006` §6 | Search button submit (§8.1, §8.2) |
| `GET /search/{taskId}/results?page=&pageSize=` | `CAS-006` §6 | Poll for completion; populate Filename/Content Matches panels (§8.2) |
| `POST /search/results/quarantine` | `CAS-006` §6 | Send to Quarantine from either match panel (§8.4) |
| `GET /quarantine` | `CAS-008` §6 | Populate/refresh the embedded Quarantine panel (§8.4) |

All calls go through the existing `"CachetApi"` named `HttpClient` (`CAS-002` §6.6,
`Cachet.ArchiveSync.Web/Program.cs:122`), which already forwards the circuit's session cookie — no new
DI registration or client is needed.

---

## §7 Validations

Client-side mirrors of `CAS-006`'s own server-side rules (§7 V1–V3), so the user sees immediate feedback
without a round trip — the server remains the source of truth in every case; a client-side pass never
substitutes for the server's own rejection.

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | Search term must be non-empty after trim, ≤ 200 characters (mirrors `CAS-006` V1/`Search:MaxTermLength`) | Search button remains disabled; inline hint shown | `Test_V1_SearchButtonDisabledUntilTermNonEmptyAndWithinLength` |
| V2 | Bucket dropdown offers only the configured bucket value (mirrors `CAS-006` V2 — no free-text bucket entry exists in this UI at all, so this is enforced by construction, not a separate check) | N/A — no invalid state reachable from the UI | `Test_V2_BucketDropdownOffersOnlyConfiguredBucket` |
| V3 | Extension dropdown offers only `All\|PDF\|DOC\|DOCX\|XLS\|XLSX\|TXT` (FR-04; mirrors `CAS-006` V3) | N/A — enforced by construction (fixed `<select>` options) | `Test_V3_ExtensionDropdownOffersExactlyTheAllowedSet` |
| V4 | Send to Quarantine button (either panel) is disabled until ≥1 row in that panel is checked (UI/UX spec §1.4) | Button remains disabled; no request sent | `Test_V4_SendToQuarantineDisabledUntilRowChecked` |
| V5 | A server-side `422`/`404`/`409` from any of the four §6 endpoints is surfaced as a screen-level error state, never an unhandled exception/blank screen | Inline error banner per §8.5 | `Test_V5_ServerErrorResponsesRenderInlineErrorNotCrash` |

---

## §8 UI / UX

### §8.1 Filter Bar — Exactly `CACHET_UIUX_Design_Specification_v1.md` §3.1

- Global search input, placeholder "Global search - filename or path...".
- Bucket dropdown — single configured bucket value (`B2:BucketName`, surfaced read-only from the
  server; the dropdown control remains per §3.1's "keep it configurable per Section 8 Reusability"
  framing, even though exactly one option exists today).
- Extension dropdown — `All | PDF | DOC | DOCX | XLS | XLSX | TXT`, default `All` (FR-04).
- **Search** button — primary. Disabled while the term is empty/over-length (V1) or while a search
  submitted by this component is still in flight (§8.2) — a second submission is not queued on top of
  an unresolved one, since the screen only tracks one active task at a time (§8.2 design note).

### §8.2 Search Submission, Polling, and Auto-Population (FR-18, FR-21, FR-22a)

**Render mode:** `InteractiveServer` — unlike `CAS-021`'s `Login.razor`, this screen requires a live
SignalR circuit to poll and push updates without a page reload; the static-SSR/form-POST pattern
`CAS-021` §8.2 used for the cookie-issuing login request does not apply here (no cookie is being issued;
this is an already-authenticated circuit making ordinary API calls via the existing `"CachetApi"`
`HttpClient`).

1. **Search clicked:** `POST /search` (`CAS-006` §6) with `{ term, bucket, extension }`. On `202`,
   store `taskId`; show a toast: *"Search submitted — this may take anywhere from a few minutes to a
   full day depending on data volume."* (UI/UX spec §3.3 point 2 — the "Check Task Manager for
   progress" clause is dropped from the copy since no Task Manager screen exists yet, §0/§4; nothing
   else in the copy changes).
2. **In-progress banner:** replaces the Search button's row with: *"Search in progress — submitted
   `<relative time>` ago. Results will appear here automatically when ready."* (UI/UX spec §3.3 point 3
   verbatim, minus the Task Manager link, same reason as above).
3. **Polling:** the component starts a server-side `PeriodicTimer` (5-second interval — **(design
   decision, not BRD-specified)**: the BRD gives no polling cadence; 5 seconds balances responsiveness
   against Api load on a max-4-user internal tool, and is cheap to retune later since it is a single
   constant, not a distributed contract) calling `GET /search/{taskId}/results`. A `409
   task-not-complete` response means keep polling; a `200` response (in either `Completed` or `Failed`
   shape, `CAS-006` §6.2) stops the timer and moves to step 4/5.
4. **Task completes (`200`, `Completed`):** replace the in-progress banner with the populated Filename
   Matches / Content Matches panels (`filenameMatches`/`contentMatches`, `CAS-006` §6.2's response
   shape), calling `StateHasChanged()` on the circuit — this is the FR-21 "auto-populate in place, no
   manual refresh" behavior, achieved via the circuit's own live state rather than a page navigation.
5. **Task fails (`200`, `Failed`):** replace the in-progress banner with the failure banner (§8.5).
6. **User navigates away and back within the same session (same circuit):** the component's own
   in-memory `taskId`/poll-loop is scoped to the component instance and does not survive a full circuit
   reconnect. **(known limitation, flagged not silently accepted — see §15):** the UI/UX spec §10 point
   2 describes returning-later behavior in terms of the Task Manager badge/View Results affordance,
   which is out of scope here (§0/§4); this spec does not attempt to replicate that cross-session
   recovery without the infrastructure it depends on.

### §8.3 Results Regions — Exactly `CACHET_UIUX_Design_Specification_v1.md` §3.2

| Panel | Columns | Status chips |
|---|---|---|
| Filename Matches (count badge) | checkbox, Name/Path, Size, Modified, Status | Flagged, Informational |
| Content Matches (count badge) | checkbox, Name/Path, Match Snippet, Modified, Status | Needs Review |

Row data maps directly from `CAS-006`'s `filenameMatches.items[]`/`contentMatches.items[]` — no
client-side reshaping beyond binding.

### §8.4 Checkbox-Select-to-Quarantine (FR-09) and the Embedded Quarantine Panel

- Each row in Filename Matches / Content Matches has a checkbox bound to a per-row selection set.
- **Send to Quarantine (`<n>`)** button per panel — disabled until ≥1 row selected (V4), label shows
  the live count.
- On click: `POST /search/results/quarantine` (`CAS-006` §6.3) with `{ taskId, fileIds }` (the
  selected rows' `fileId`s — `search_result_id` values, per `CAS-006` §1.7/§6.3's own semantics; this
  screen never re-derives or relabels that identity).
- On `200`: clear the selection for the affected rows, show a toast confirming the count added (and any
  `warnings` for files already actively quarantined — `CAS-006` §6.3's partial-success shape), and
  **refresh the embedded Quarantine panel** by re-calling `GET /quarantine` (`CAS-008` §6) — this panel
  does not remove the source rows from Filename/Content Matches (the UI/UX spec does not specify that
  behavior, and a match staying visible after being quarantined is not incorrect — it reflects the
  search result as it was found, while the Quarantine panel is the independent source of truth for
  current quarantine state).
- **Quarantine panel** itself: the UI/UX spec's column list (§3.2) includes a checkbox column, but this
  screen implements **no action for it** (§4 — Delete Permanently lives in the modal, out of scope
  here). **Resolved 2026-09-23 (R. Devarajan, TraceCounts):** the checkbox column is **dropped from
  this screen's rendering** rather than shown disabled/inert — a dead control with no action invites
  confused clicks on a screen with no way to explain why it doesn't do anything. Columns rendered here:
  Name/Path, Quarantined On, Status only. The future Quarantine modal spec (§15) owns reintroducing the
  checkbox once Delete Permanently (or another action) actually exists behind it; this is a documented,
  deliberate deviation from the UI/UX spec §3.2 column list, not an oversight — `CACHET_UIUX_Design_
  Specification_v1.md` §3.2 should receive a matching note if/when that document is next revised.
- Loaded once on initial page render and refreshed after every successful Send to Quarantine action;
  not polled continuously (no BRD requirement for live cross-user updates to this panel).

### §8.5 Empty / Error States — Exactly `CACHET_UIUX_Design_Specification_v1.md` §3.4

| State | Copy/behavior |
|---|---|
| No search yet run this session | Panels show "No filename matches yet — run a search to get started." / equivalent per panel — not a loading indicator. |
| Search completed, zero matches | "No filename matches found for '`<term>`'." (per panel). Not an error. |
| Task failed | Inline banner: *"This search did not complete successfully."* (Task Manager reference dropped, §0/§4 — the failure detail is not independently surfaced by this screen since it has no Task Manager/Audit Log view to link to; a future Task Manager spec restores the link.) |
| Quarantine panel empty | "Quarantine is empty. Files sent to quarantine from Keyword Search appear here for final review before permanent deletion." (UI/UX spec §4.2, reused verbatim — same copy applies whether reached from the modal or this embedded view). |
| Any §6 endpoint returns `422`/`404`/`409` unexpectedly (V5) | Non-field-specific banner: *"Something went wrong. Please try again."* — logged server-side (§11) with the endpoint/status code, never shown raw to the user. |
| `GET /quarantine` or the polling call fails on a transient network/server error | Same generic retry banner as above, scoped to the affected panel only — a failed Quarantine-panel refresh does not blank out the Filename/Content Matches panels, and vice versa. |

---

## §9 Configuration Changes

**Amended 2026-09-23 (R. Devarajan, TraceCounts) — during `/implement`, a real gap surfaced:** §8.1
requires the bucket dropdown's value to be "surfaced read-only from the server," but no `CAS-006`/
`CAS-008` endpoint exposes the configured bucket name, and this spec's original `touches.config: false`
left no key for Web to read it from either. Resolved as: `Cachet.ArchiveSync.Web` reads the existing
`B2:BucketName` key (already defined and consumed by `Cachet.ArchiveSync.Api` per `CAS-001` §9) directly
from its own `IConfiguration` — no new key *name* is introduced, but the key must now also be supplied
to the **Web** process's environment/configuration at deploy time, not just Api's (§14). If the key is
absent for Web, the screen fails closed: the bucket dropdown shows "(bucket not configured)" and the
Search button stays disabled (§8.1) — never silently submits with an empty/wrong bucket value.

| Key | Default | Description | Env Override |
|-----|---------|-------------|---------------|
| `B2:BucketName` | *(none — must be set)* | The single configured B2 bucket name, displayed read-only in the bucket dropdown (§8.1). Reused verbatim from `CAS-001` §9's existing Api-side key — **this spec adds the requirement that Web's process also receive this same value**, not a new key. | Yes |

Reuses `Api:BaseUrl` (`CAS-002` §6.6) via the existing `"CachetApi"` named `HttpClient`. The 5-second
poll interval (§8.2) is a code constant, not a configuration key — **(design decision, not
BRD-specified)**: no BRD/NFR text calls for this to be tunable without a redeploy, and introducing a
config key for it would be speculative given this project's small (max 4 user) scale.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Submitting a valid search shows the in-progress banner immediately and disables the Search button while the task is outstanding | `Test_SC1_SearchSubmissionShowsInProgressBannerAndDisablesButton` |
| SC-2 | While `GET /search/{taskId}/results` returns `409`, the screen continues polling and shows no panel content beyond the in-progress banner | `Test_SC2_PollingContinuesWhileTaskIncomplete` |
| SC-3 | Once `GET /search/{taskId}/results` returns `200` `Completed`, the Filename Matches and Content Matches panels populate in place, without a page navigation/reload, and the in-progress banner is removed | `Test_SC3_CompletedTaskAutoPopulatesPanelsInPlace` |
| SC-4 | Once the task returns `200` `Failed`, the failure banner replaces the in-progress banner and no stale/partial panel content is shown | `Test_SC4_FailedTaskShowsFailureBanner` |
| SC-5 | Send to Quarantine is disabled with 0 rows checked in a panel, and shows the correct count once ≥1 row is checked | `Test_SC5_SendToQuarantineButtonReflectsSelectionCount` |
| SC-6 | Clicking Send to Quarantine with a valid selection calls `POST /search/results/quarantine` with exactly the selected `fileId`s and, on success, refreshes the embedded Quarantine panel via `GET /quarantine` | `Test_SC6_SendToQuarantineCallsApiAndRefreshesQuarantinePanel` |
| SC-7 | A `409 file-already-quarantined`/partial-warning response is surfaced to the user (toast naming the skipped file(s)) without discarding the files that did succeed | `Test_SC7_PartialQuarantineFailureSurfacedWithoutLosingSuccesses` |
| SC-8 | The extension dropdown renders exactly `All, PDF, DOC, DOCX, XLS, XLSX, TXT` — the FR-04-required superset of the original wireframe's `All/PDF/TXT/XLSX` | `Test_SC8_ExtensionDropdownMatchesFR04RequiredList` |
| SC-9 | Zero-match completion renders the "No filename/content matches found for '`<term>`'" copy, not an empty table with no explanation | `Test_SC9_ZeroMatchCompletionShowsExplicitEmptyStateCopy` |
| SC-10 | No B2 application key, session cookie value, or raw file content ever appears in `KeywordSearch.razor`'s rendered markup or in Web's log output | `Test_SC10_NoCredentialOrContentLeakageInUiSurfaceOrLogs` |

---

## §11 Telemetry

```csharp
// _logger.LogInformation("KeywordSearch screen: search submitted, TaskId={TaskId}", taskId);
// _logger.LogInformation("KeywordSearch screen: task {TaskId} completed after {PollCount} polls", taskId, pollCount);
// _logger.LogWarning("KeywordSearch screen: unexpected {StatusCode} from {Endpoint}", statusCode, endpoint);
```

- Never log: the B2 application key, the session/cookie value, the user-entered search `term` (same
  restriction `CAS-006` §11 already applies server-side — a search term can be sensitive text), full
  file content, or match snippets.
- Poll-loop logging is count/status only, never per-poll response bodies.

---

## §12 Security Notes

### B2 Credential Handling
This screen never touches the B2 application key at any point. It consumes only the already-sanitized
response shapes `CAS-006`/`CAS-008` define (`CAS-006` §12 already confirms none of those DTOs carry a
key or key ID). No new B2 client, credential, or capability check is introduced here.

### Audit Trail
No change — `CAS-006` §12 and `CAS-008` §12 already write `KeywordSearchSubmitted`/`QuarantineAdd`
audit rows on the Api side, before each endpoint's own response returns. This spec adds no new audited
action; it is a pure UI/transport layer in front of already-audited backend calls.

### Session / Auth Requirements
The entire screen requires an authenticated session. **Gap noted, not fixed here:** `CAS-021` (the spec
that adds the `FallbackPolicy`/`<AuthorizeRouteView>` gate making this enforceable app-wide) is
`status: in-progress`, not yet `merged` as of this spec's drafting. This spec's own page carries no
`[Authorize]`-equivalent Razor-page gate of its own beyond whatever `CAS-021` establishes globally —
consistent with `CAS-021` §8.5's design (a page-level attribute is the exception, not the default; the
`FallbackPolicy` is the default). **This spec is therefore sequenced after `CAS-021`** (§14) — deploying
`KeywordSearch.razor` before `CAS-021`'s authorization gate ships would leave it reachable
unauthenticated, which this spec does not independently guard against by design (avoiding a second,
redundant authorization mechanism).

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `KeywordSearchPageTests` (bUnit) | `Test_V1_SearchButtonDisabledUntilTermNonEmptyAndWithinLength`, `Test_V2_BucketDropdownOffersOnlyConfiguredBucket`, `Test_V3_ExtensionDropdownOffersExactlyTheAllowedSet`, `Test_V4_SendToQuarantineDisabledUntilRowChecked`, `Test_SC8_ExtensionDropdownMatchesFR04RequiredList` | V1–V4, SC-8 |
| `KeywordSearchPageTests` (bUnit, mocked `HttpClient`) | `Test_SC1_SearchSubmissionShowsInProgressBannerAndDisablesButton`, `Test_SC2_PollingContinuesWhileTaskIncomplete`, `Test_SC3_CompletedTaskAutoPopulatesPanelsInPlace`, `Test_SC4_FailedTaskShowsFailureBanner`, `Test_SC9_ZeroMatchCompletionShowsExplicitEmptyStateCopy` | SC-1–SC-4, SC-9 |
| `KeywordSearchPageTests` (bUnit, mocked `HttpClient`) | `Test_SC5_SendToQuarantineButtonReflectsSelectionCount`, `Test_SC6_SendToQuarantineCallsApiAndRefreshesQuarantinePanel`, `Test_SC7_PartialQuarantineFailureSurfacedWithoutLosingSuccesses` | SC-5–SC-7 |
| `KeywordSearchPageTests` (bUnit) | `Test_V5_ServerErrorResponsesRenderInlineErrorNotCrash` | V5 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `KeywordSearchFlowIntegrationTests` (`WebApplicationFactory`, Web, against a stubbed/in-memory Api double or the real `CAS-006`/`CAS-008` controllers with a test double repository layer) | `Test_SC3_CompletedTaskAutoPopulatesPanelsInPlace` (full round trip), `Test_SC6_SendToQuarantineCallsApiAndRefreshesQuarantinePanel` (full round trip) | SC-3, SC-6 (end-to-end confirmation) |
| Log-output assertion (test log sink) | `Test_SC10_NoCredentialOrContentLeakageInUiSurfaceOrLogs` | SC-10 |

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. No database migration (§5).
2. **Deploy prerequisite (§9, amended during `/implement`):** the `B2:BucketName` environment
   variable/config value — already supplied to Api per `CAS-001` §9 — must also be supplied to the
   **Web** process before this deploy. Without it, the screen fails closed (bucket dropdown shows
   "(bucket not configured)", Search button stays disabled) rather than failing to build or start; this
   is a silent-degradation risk if ops misses the step, so confirm it explicitly as part of this
   rollout's checklist, not assumed from Api's existing configuration.
3. Deploy `Cachet.ArchiveSync.Web` only — no `Api`/`Worker`/`Core` change (§6, §4). `CAS-006` and
   `CAS-008`'s Api endpoints are already live/ready.
4. **Deploy after `CAS-021` merges** (§12) — this screen depends on `CAS-021`'s global authorization
   gate to actually be unreachable without a session; deploying it first would leave `/` (or wherever
   this page is routed) open unauthenticated, same class of gap `CAS-021` §17 itself flagged for the
   pre-`CAS-021` state.
5. Rollback: revert the new `KeywordSearch.razor` route/component; no schema or Api change to unwind.

---

## §15 Open Questions

All three items originally raised while drafting this spec were resolved directly with R. Devarajan,
TraceCounts (Solution Owner), 2026-09-23 — logged below for traceability (Karpathy P6), matching
`CAS-001`/`CAS-002`/`CAS-021`'s own resolution-log convention. No blocking open item remains against
this spec.

- [x] **Generic Task Manager API/screen and global header — resolved 2026-09-23 (R. Devarajan,
      TraceCounts): defer.** CAS-022's self-polling substitute (§0, §8.2) ships as the real
      implementation, not a stopgap awaiting an imminent replacement. A dedicated Task Manager spec
      (WBS Task 25) is **not** prioritized ahead of or alongside this one — it is opened as a separate,
      later spec once more async screens (O-Drive Sync/Content Scan) exist to justify the shared
      infrastructure, rather than speculatively built now against a single consumer. This spec's scope
      and §14 rollout are unaffected.
- [x] **Cross-session/circuit-loss recovery — resolved 2026-09-23 (R. Devarajan, TraceCounts): accept
      as a known, documented limitation.** No client-side persistence of the in-flight `taskId` is
      added. If a user's circuit drops while a search is running, the task still completes server-side
      and its results remain queryable via `CAS-006`'s `GET /search/{taskId}/results` by anyone (that
      spec's ownership-scope resolution already allows this) — the user simply doesn't see automatic
      in-place population after a reload until a future Task Manager screen (deferred above) gives them
      a way to reattach. Not revisited as part of this spec.
- [x] **Quarantine panel's checkbox column — resolved 2026-09-23 (R. Devarajan, TraceCounts): hide it.**
      The embedded Quarantine panel renders Name/Path, Quarantined On, and Status only — no checkbox
      column — until a future spec gives it a real action (§8.4). A visible-but-disabled control was
      rejected as inviting confused clicks with no on-screen explanation.
- Not blocked by BRD §12.4 (web caching layer mechanism, O-Drive refresh cadence) — this screen always
  calls the live API and makes no caching-layer assumption; confirmed not load-bearing here.
- Not blocked by BRD §12.2/§12.1 items — login method is resolved (`CAS-002`), and this spec depends
  only on `CAS-021`'s deployment sequencing (§14), not on any still-open decision.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-23 | Drafted per WBS Task 22 |
| Reviewer | | | Pending |

---

## §17 Graph Impact

Graph exists for this repo (`graphify-out/`, built 2026-09-10, refreshed since) and indexes the
BRD/WBS/API-spec/decision-log documentation corpus — it has no `src/` application-code nodes yet
(confirmed by this same gap already disclosed in `CAS-021` §17). Queried live:

```
python -m graphify query "Keyword Search screen UI FR-04 FR-05 FR-06 FR-07 FR-08 FR-09 FR-21 auto-population Blazor" --budget 1500
```

Supplemented by a manual read of `src/Cachet.ArchiveSync.Web/` (confirming no Keyword Search `.razor`,
no global header, and a still-bare `MainLayout.razor`), `src/Cachet.ArchiveSync.Api/Controllers/
TaskManagerController.cs` (confirming the §0 Task Manager API gap), and
`src/Cachet.ArchiveSync.Api/Controllers/SearchController.cs`/`QuarantineController.cs` (confirming the
exact route/response shapes this spec consumes).

### God Nodes in Blast Radius
None of the graph's top-10 numeric god nodes (all `graphify` tool internals) are BRD/spec nodes this
feature touches. Within the documentation graph, `API spec §4: Keyword Search Module (FR-04-FR-09)` is
the highest-degree node this spec's blast radius passes through, bridging to `POST /search`,
`GET /search/{taskId}/results`, `POST /search/results/quarantine`, `FR-18`, and the P9 decomposition
nodes — consistent with `CAS-006` §17's own findings, now viewed from the UI-consumer side.

### Communities / Modules Affected
- **Community 22** ("Keyword Search API surface") — this screen is the first UI consumer of every node
  in this community; confirms `CAS-006`'s three endpoints are exactly what this spec should build
  against, with no drift.
- **Community 69/101** (Quarantine, B2 cross-check) — touched only via the embedded read-only panel and
  the Send to Quarantine action; the B2 cross-check itself (`FR-08`) is confirmed to have already
  happened server-side in `CAS-006`'s Worker — this screen creates no new cross-check code path.
- **Community 37** ("UI/UX design corpus — Task Manager, Sign-In, O-Drive Sync") — surfaces the
  documentation-only Task Manager/global-header nodes this spec deliberately does not build (§0/§4);
  confirms those are tracked elsewhere in the graph, not silently missing from consideration.
- Projects touched: `Web` only (new `.razor` component + supporting client-side code). **Not** `Api`,
  `Core`, or `Worker`.

### Surprising Connections
- The graph's live query surfaces `Karpathy Principle P9: Split before you bloat` and the `§2.2 Module
  fan-out` node directly alongside the Keyword Search API community — same self-check `CAS-006` §17 ran
  for its own scope; re-run below for this spec's own boundary.
- **Confirmed by manual code read, not by the graph (graph has no `src/` nodes):** `TaskManagerController.cs`
  exists but hosts only `POST /tasks/{taskId}/restart` — a real, load-bearing gap for this spec's FR-21
  requirement, not something graphify's documentation-only index could have surfaced on its own. This is
  the single most consequential finding of this spec's analysis (§0).
- **Decomposition check (P9) result:** module fan-out is 2 (Keyword Search + the read-only Quarantine-
  panel sliver) — the same "not materially different posture" reasoning `CAS-006` §17 already applied to
  its own Quarantine-add sliver holds here too (this screen adds no new Quarantine business logic, only
  a read + an existing add call). Size thresholds: 4 endpoints *consumed* (not owned) so §2.3's
  "§6 API Endpoints > 6" signal doesn't apply the same way to a UI-only spec; 5 validations, 10 SCs, 0 DB
  objects, estimated changed files well under 15. **Decision: single spec, not split.**

### Graphify Queries Run
```
python -m graphify query "Keyword Search screen UI FR-04 FR-05 FR-06 FR-07 FR-08 FR-09 FR-21 auto-population Blazor" --budget 1500
```
(`--budget` truncated full output; God Nodes/Communities/Surprising-Connections sections above also
draw on `graphify-out/GRAPH_REPORT.md` and the manual code read noted above.)

---

## §18 Verification Plan

### Reviewer Checklist
The reviewer will independently verify each of the following. Mark N/A only if the section itself is
N/A.

- [ ] All §10 Success Criteria (SC-1–SC-10) have a passing test.
- [ ] All §7 Validations (V1–V5) are implemented and tested.
- [ ] `KeywordSearch.razor` matches `CACHET_UIUX_Design_Specification_v1.md` §3.1–§3.4 exactly for the
      controls/panels/columns this spec builds (filter bar, three panels, status chips), with the three
      documented deviations (Task Manager references dropped from copy, §0/§8.2/§8.5; Quarantine panel
      checkbox column omitted, §8.4/§15) applied consistently and nowhere else.
- [ ] The extension dropdown offers exactly `All, PDF, DOC, DOCX, XLS, XLSX, TXT` (FR-04, SC-8) — not
      the original wireframe's shorter list.
- [ ] Search submission never blocks synchronously on the search itself completing — `POST /search`
      returns immediately and polling/auto-population is the only mechanism that reveals results
      (FR-18, SC-1–SC-3).
- [ ] Send to Quarantine sends exactly the checked rows' `fileId`s and refreshes the embedded
      Quarantine panel via a fresh `GET /quarantine` call, not a locally-mutated stale list (SC-6).
- [ ] No `Cachet.ArchiveSync.Api`, `Cachet.ArchiveSync.Core`, or `Cachet.ArchiveSync.Worker` file was
      modified (§4, §6 — this spec is Web-only).
- [ ] No generic Task Manager endpoint, Task Manager screen, or global header component was added
      (§0/§4 — confirm the self-polling substitute was used instead, not a workaround that silently
      built the out-of-scope infrastructure).
- [ ] §12 B2/session: no B2 key, key ID, or session cookie value appears in rendered markup or logs
      (SC-10).
- [ ] §17 Graph Impact matches actual code changes — no unintended nodes (no Api/Core/Worker files, no
      O-Drive Sync or Quarantine-modal code).

### Verification Report Format
Per template: `CAS-022.review.md` with overall verdict, per-SC verdict table, security gap list, scope
violations.

## Implementation Log

**Completed:** 2026-09-23
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 98.6% | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | New — @page "/", InteractiveServer filter bar, poll loop against GET /search/{taskId}/results, Filename/Content Matches panels, Send to Quarantine, embedded read-only Quarantine panel |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor.css` | New — scoped styling |
| `src/Cachet.ArchiveSync.Web/Components/Pages/Home.razor` | Removed — route conflict with KeywordSearch.razor at /; unused scaffold placeholder |
| `src/Cachet.ArchiveSync.Web/appsettings.Development.json` | Modified — B2:BucketName env-var-only note added (§9 amendment) |
| `docs/environment-variables.md` | Modified — B2__BucketName now also required by Web (§9 amendment) |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/FakeKeywordSearchApiHandler.cs` | New — HttpMessageHandler stub for the CachetApi named client |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchPageTests.cs` | New — bUnit tests, all 15 SC/V IDs plus coverage-closing tests |
| `src/Cachet.ArchiveSync.Web.Tests/Cachet.ArchiveSync.Web.Tests.csproj` | Modified — added bunit 1.28.9 package reference |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncPageTests.cs` | Modified — unrelated pre-existing whitespace-assertion fix (CAS-023 test, out of CAS-022 scope, fixed to unblock the shared Gate 2 run) |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `KeywordSearchPageTests` | `Test_SC1_SearchSubmissionShowsInProgressBannerAndDisablesButton` | SC-1 |
| `KeywordSearchPageTests` | `Test_SC2_PollingContinuesWhileTaskIncomplete` | SC-2 |
| `KeywordSearchPageTests` | `Test_SC3_CompletedTaskAutoPopulatesPanelsInPlace` | SC-3 |
| `KeywordSearchPageTests` | `Test_SC4_FailedTaskShowsFailureBanner` | SC-4 |
| `KeywordSearchPageTests` | `Test_SC5_SendToQuarantineButtonReflectsSelectionCount` | SC-5 |
| `KeywordSearchPageTests` | `Test_SC6_SendToQuarantineCallsApiAndRefreshesQuarantinePanel` | SC-6 |
| `KeywordSearchPageTests` | `Test_SC7_PartialQuarantineFailureSurfacedWithoutLosingSuccesses` | SC-7 |
| `KeywordSearchPageTests` | `Test_SC8_ExtensionDropdownMatchesFR04RequiredList` | SC-8 |
| `KeywordSearchPageTests` | `Test_SC9_ZeroMatchCompletionShowsExplicitEmptyStateCopy` | SC-9 |
| `KeywordSearchPageTests` | `Test_SC10_NoCredentialOrContentLeakageInUiSurfaceOrLogs` | SC-10 |
| `KeywordSearchPageTests` | `Test_V1_SearchButtonDisabledUntilTermNonEmptyAndWithinLength` | V-1 |
| `KeywordSearchPageTests` | `Test_V2_BucketDropdownOffersOnlyConfiguredBucket` | V-2 |
| `KeywordSearchPageTests` | `Test_V3_ExtensionDropdownOffersExactlyTheAllowedSet` | V-3 |
| `KeywordSearchPageTests` | `Test_V4_SendToQuarantineDisabledUntilRowChecked` | V-4 |
| `KeywordSearchPageTests` | `Test_V5_ServerErrorResponsesRenderInlineErrorNotCrash` | V-5 |
