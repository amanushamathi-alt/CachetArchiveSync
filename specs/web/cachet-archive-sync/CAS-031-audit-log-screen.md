---
id: CAS-031
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-25
priority: P1
related_specs: [CAS-003, CAS-005, CAS-009, CAS-022, CAS-023, CAS-024, CAS-025, CAS-027]
touches:
  database: false
  api: false
  ui: true
  worker: false
  b2: false
  config: false
  security: true
migrations: []
---

# CAS-031 — Audit Log Screen (Filterable by Tab and Date Range)

WBS Task 26 (Web UI Development — Audit Log screen). Implements FR-24 ("The Audit Log shall be
viewable within the application, filterable at minimum by tab and by date") and the display side of
FR-23 (user, tab/module, action, file/path, timestamp, result/detail). UI contract:
`0.Specifications/CACHET_UIUX_Design_Specification_v1.md` §1.1 (header button) and §6 (Audit Log).
API contract: `CAS-009` (`GET /audit-log`, status `merged`) — consumed unmodified.

---

## §0 Reconciliation Notes (read this first)

The task brief, the UI/UX spec, and the merged API disagree in three places. Each is resolved here
by precedence (BRD > merged contract > UI/UX proposal > task brief summary), not silently:

1. **Columns — six, not four.** The WBS brief summarises the table as "time, tab, action, detail."
   FR-23 and UI/UX §6.1 both require **User, Tab/Module, Action, File/Path, Timestamp,
   Result/Detail**, and CAS-009 returns all six. This spec builds the six FR-23 columns; the brief's
   four are a subset, so nothing it asks for is dropped.
2. **Tab dropdown values.** UI/UX §6.1 proposes `All | Keyword Search | O-Drive Sync | Quarantine |
   Reports`. A read of the current writers shows rows are also written with `tab_module = 'Auth'`
   (`LoginService.cs`, `AuthController.cs`, `UserProvisioningService.cs`) and `'Task Manager'`
   (`TaskManagerService.cs` — restarts). With UI/UX's list those rows would be visible only under
   "All" and impossible to filter to, which undercuts FR-24. This spec's dropdown is: **All, Auth,
   Keyword Search, O-Drive Sync, Quarantine, Task Manager, Reports** — every value CAS-009 accepts
   *and* that some code path writes. `Audit Log` (accepted by CAS-009 V1) is omitted because nothing
   writes it (CAS-009 §12: listing is not audited), so it would always return an empty result.
   Confirm in §15 Q1.
3. **Header button count badge.** UI/UX §1.1 draws the Audit Log button with a count badge ("2" in
   the wireframe) but never defines what the count means, and CAS-009 exposes no count-only endpoint.
   Per Karpathy P2 this spec does **not** invent a meaning: the button is enabled with **no count
   badge**. Flagged in §15 Q2.

Placement decision: the screen is a **non-routed modal** (`AuditLogModal.razor`), opened from the
existing per-page **Audit Log** toolbar button on both `KeywordSearch.razor` and
`ODriveSyncContentScan.razor`. This matches UI/UX §1.1 ("every other screen is reached via a header
button, not a tab") and the established sibling pattern (`QuarantineQueueModal.razor` CAS-024,
`TaskManagerModal.razor` CAS-027). No new route is added.

---

## §1 Summary

Replaces the disabled "Audit Log — Coming soon" placeholder (CAS-025 §0) on both tab screens with a
live button that opens `AuditLogModal.razor`: a read-only, paginated table of `dbo.audit_log` entries,
newest first, with the six FR-23 columns, and a filter bar for **Tab/Module** and a **From/To date
range** (FR-24). Data comes only from CAS-009's `GET /audit-log` via the existing `CachetApi`
`HttpClient` (cookie-forwarding, CAS-002 §6.6). Failed actions show with their `Failure` result and
`resultDetail`. They are never hidden (NFR target #11). The screen writes nothing, calls no B2 path,
and adds no Api/Core/Worker/database change.

---

## §2 User Story

As a **TraceCounts reviewer**, I want to open the audit log from either tab, narrow it to one module
and a date range, and page through the results, so that I can confirm who did what, to which file,
when, and whether it succeeded, including failures (FR-23, FR-24).

---

## §3 In-Scope

- **New `Components/Shared/AuditLogModal.razor`** (+ scoped `.razor.css`). This is a non-routed
  component opened through `@ref` and a public `OpenAsync()`, the same convention as
  `QuarantineQueueModal.razor`. It contains:
  - Filter bar: Tab/Module `<select>` (§8.2), From and To `<input type="date">`, **Apply** and
    **Clear** buttons.
  - Results table: the six FR-23 columns (§8.3), newest first, in server order (never re-sorted on
    the client).
  - Pager: **Previous** / **Next** and the text "Showing X–Y of Z" (§8.4). Page size is fixed at 50.
  - Loading, empty, validation, and error states (§8.5).
  - A **Close** button, plus the × in the header.
- **`KeywordSearch.razor` and `ODriveSyncContentScan.razor`:** replace the disabled Audit Log
  placeholder button with an enabled button that calls `auditLogModal.OpenAsync()`. Add
  `<AuditLogModal @ref="auditLogModal" />` next to the existing `<QuarantineQueueModal>`. The button
  keeps its current position and CSS classes, without `cachet-header-btn-disabled`, and has no count
  badge (§0 point 3).
- New Web test fake `FakeAuditLogApiHandler` (same shape as `FakeQuarantineQueueApiHandler`) and the
  bUnit tests in §13.

---

## §4 Out-of-Scope

- **Any change to `GET /audit-log` or its SP/repository** (CAS-009). This includes new filters. The
  `user` and `action` free-text filters stay deferred per CAS-009 §15.
- **User / Action / Result filter controls, column sorting, free-text search, column resizing.**
  FR-24's minimum is tab + date, and UI/UX §6.1 draws nothing else.
- **Export to CSV/XLSX/PDF from this screen.** Export belongs to Reporting (FR-25–FR-27; CAS-010/CAS-015).
- **Auto-refresh or polling.** The view loads on open and when Apply or the pager is used. Nothing
  in the BRD or UI/UX asks for a live-updating log.
- **Count badge on the Audit Log button** (§0 point 3, §15 Q2).
- **Browser-local time zone conversion** (§8.6, §15 Q3). The Web project has no JS-interop
  time-zone mechanism today, and this spec does not add one.
- **Web caching layer** (BRD §12.4 open item). This screen reads live from CAS-009 each time, so it
  does not depend on the open decision (§15).
- **Pre-/Post-Action Report buttons.** They stay CAS-025's disabled placeholders.
- **`MainLayout.razor`.** It is not touched. The toolbar row is per-page (see that file's header
  comment).

---

## §5 Database Objects

N/A — `touches.database: false`. `dbo.audit_log` (CAS-003) and `dbo.audit_log_lst` (CAS-009) are
consumed indirectly through the API only.

---

## §6 API Endpoints

No new endpoint. This spec consumes one endpoint without modifying it:

| Route | Method | Source | Purpose here |
|---|---|---|---|
| `/audit-log` | GET | CAS-009 §6 | Table rows + `totalCount` for the pager |

Request built by the modal (via `HttpClientFactory.CreateClient("CachetApi")`):

| Param | Sent when | Value |
|---|---|---|
| `tab` | Tab ≠ All | The selected value, URL-encoded (`Keyword Search` → `Keyword%20Search`) |
| `dateFrom` | From is set | `{From}T00:00:00Z` (ISO 8601 UTC, start of day) |
| `dateTo` | To is set | `{To}T23:59:59.9999999Z` (ISO 8601 UTC, end of day, so the To date is fully included) |
| `page` | Always | Current page, starting at 1 |
| `pageSize` | Always | `50` (UI/UX §6.1, CAS-009 default) |

Responses handled: `200` → `PagedResult<AuditLogListItem>` (existing Core DTOs, reused);
`401` / `422` / any other non-2xx / `HttpRequestException` / `TaskCanceledException` → load-failure
state (§8.5).

---

## §7 Validations

All client-side. The server-side rules remain CAS-009 V1–V6.

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | If both From and To are set, To must be on or after From (mirrors CAS-009 V2) | Inline message "‘To’ date must be on or after ‘From’ date."; **Apply** disabled; **no** request sent | `Test_V1_ToBeforeFromBlocksApplyAndSendsNoRequest` |
| V2 | The Tab/Module dropdown offers exactly: All, Auth, Keyword Search, O-Drive Sync, Quarantine, Task Manager, Reports (§0 point 2). "All" is the default and sends no `tab` param | Wrong/missing option | `Test_V2_TabDropdownOffersExactlyTheAllowedValues` |
| V3 | Applying a filter or clearing filters resets to page 1 | Stale page index sent | `Test_V3_ApplyAndClearResetToPageOne` |
| V4 | Previous is disabled on page 1. Next is disabled when `page * pageSize >= totalCount`. Both are disabled while a request is in flight | Out-of-range or duplicate request | `Test_V4_PagerBoundsAndInFlightDisable` |
| V5 | Every cell value (`user`, `action`, `filePath`, `resultDetail`) is rendered as encoded text, never as `MarkupString` | Script/markup injection via a crafted filename or detail string | `Test_V5_CellValuesAreHtmlEncoded` |
| V6 | Rows with `result = "Failure"` render with the failure chip and their `resultDetail`, and are never filtered or hidden by the client | Failure omitted | `Test_V6_FailureRowsRenderWithDetail` |

---

## §8 UI / UX

### §8.1 Modal Shell

This follows `QuarantineQueueModal.razor`'s markup and classes: `cachet-modal-backdrop` >
`cachet-modal` (`role="dialog" aria-modal="true" aria-labelledby="cachet-al-title"`), a header with
`<h2 id="cachet-al-title">Audit Log</h2>` and a close button (`aria-label="Close Audit Log"`), a body,
and a footer. Use the prefix `cachet-al-` for the component's own classes. The modal must be wide
enough for six columns. Long values wrap or use ellipsis with a `title` tooltip, following the
existing `cachet-cell-path` pattern. The page itself must not scroll sideways.

Load sequence for `OpenAsync()`: set `isOpen = true`, reset the filters to their defaults (All,
blank From/To), set page 1, then fetch. Reopening always starts from defaults and does not keep the
previous filter state. This keeps each view's scope obvious and avoids per-viewer state.

### §8.2 Filter Bar

| Control | Behavior |
|---|---|
| Tab/Module `<select>` (`aria-label="Filter by tab"`) | The options from §7 V2, in that order. Changing it does **not** fetch until Apply is clicked. |
| From `<input type="date">` (label "From") | Optional. Interpreted as a **UTC calendar date** (§8.6). |
| To `<input type="date">` (label "To") | Optional. Interpreted as a UTC calendar date and fully included (§6). |
| **Apply** | Fetches page 1 with the current filters. Disabled while V1 fails or a request is in flight. |
| **Clear** | Resets to All with blank dates and fetches page 1. |

A small caption under the filter bar reads: "Times and dates are shown in UTC."

### §8.3 Results Table

`cachet-grid-table cachet-al-table`, with one row per `items[]` entry in API order (newest first):

| Column header | Source | Rendering |
|---|---|---|
| Timestamp (UTC) | `timestamp` | `yyyy-MM-dd HH:mm:ss` from the UTC value (`timestamp.UtcDateTime`), culture-invariant |
| User | `user` | As returned (username; CAS-021 §15 stopgap convention) |
| Tab/Module | `tab` | As returned |
| Action | `action` | As returned |
| File/Path | `filePath` | As returned. `null` → "—". Ellipsis plus `title` tooltip when long |
| Result/Detail | `result`, `resultDetail` | Chip: `Success` → green, `Failure` → red, any other value → neutral/gray (defensive). When `resultDetail` is non-null it goes below the chip in the `cachet-cell-error` style for Failure rows and in plain muted text otherwise |

Timestamp is the first column because it is the default sort key. This is a column-order choice
only; it adds or removes no FR-23 field.

### §8.4 Pager

It sits below the table and shows only when `totalCount > 0`. The text reads "Showing
{(page−1)×50+1}–{min(page×50, totalCount)} of {totalCount}", followed by **Previous** and **Next**
(V4). A pager click fetches that page with the **currently applied** filters, meaning the ones last
sent to the API, not unapplied edits in the filter bar.

### §8.5 Loading / Empty / Error States

Copy is taken verbatim from UI/UX §6.3 where it exists:

| State | Rendering |
|---|---|
| Loading | `<p class="cachet-modal-empty">Loading…</p>`. Filter controls and pager disabled |
| `200`, `items` empty | "No audit log entries match the selected filters." (UI/UX §6.3) |
| Any load failure (§6) | Error banner `cachet-banner cachet-banner-error`: "Unable to load the audit log. Please try again." (UI/UX §6.3). The table is cleared so stale rows are never shown under a new filter. Apply stays usable as the retry |
| V1 invalid range | Inline message under the date inputs (§7 V1). No banner, no request |

### §8.6 Time Zone

Displayed timestamps and the date-filter boundaries are both **UTC** and labeled as UTC. Reasons:
(a) `dbo.audit_log.created_at` and CAS-009 are UTC. (b) A Blazor Server circuit does not know the
browser's time zone, and the Web project has no interop for it today. (c) For an audit trail, one
unambiguous time base that matches the stored record is safer than a converted display that could
disagree with a PDF/XLSX report or a SQL query. Local-time display is §15 Q3.

---

## §9 Configuration Changes

N/A. There are no new keys. Page size 50 is a constant in the component (UI/UX §6.1 says
"adjustable during Web UI Development", which means a code constant, not configuration). The
existing `CachetApi` base address is reused.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Clicking **Audit Log** on the Keyword Search screen opens the modal and issues `GET /audit-log?page=1&pageSize=50` with no `tab`/`dateFrom`/`dateTo` | `Test_SC1_OpenFromKeywordSearchLoadsDefaultPage` |
| SC-2 | The same button on the O-Drive Sync screen opens the same modal with the same default request | `Test_SC2_OpenFromODriveSyncLoadsDefaultPage` |
| SC-3 | The table renders exactly the six FR-23 columns, in the order in §8.3, with each row's values mapped per §8.3 (including "—" for a null `filePath`) | `Test_SC3_TableRendersSixFr23ColumnsMappedCorrectly` |
| SC-4 | Selecting Tab = "Quarantine" and clicking Apply sends `tab=Quarantine&page=1` and renders the returned rows | `Test_SC4_TabFilterSendsTabParam` |
| SC-5 | Setting From = 2026-09-01, To = 2026-09-09 and applying sends `dateFrom=2026-09-01T00:00:00Z` and `dateTo=2026-09-09T23:59:59.9999999Z` | `Test_SC5_DateRangeSendsInclusiveUtcBounds` |
| SC-6 | With `totalCount = 120`, the pager reads "Showing 1–50 of 120". Next requests `page=2` with the applied filters unchanged, and on page 3 it reads "Showing 101–120 of 120" with Next disabled | `Test_SC6_PagerNavigatesWithAppliedFilters` |
| SC-7 | An empty `items` response shows "No audit log entries match the selected filters." | `Test_SC7_EmptyResultShowsEmptyStateCopy` |
| SC-8 | A `500`, `422`, `401`, or network exception shows "Unable to load the audit log. Please try again." and no stale rows | `Test_SC8_LoadFailureShowsErrorAndClearsRows` |
| SC-9 | The modal issues only `GET` requests. No `POST`/`PUT`/`DELETE` is sent from any interaction (read-only, UI/UX §6.2) | `Test_SC9_ModalIssuesOnlyGetRequests` |
| SC-10 | The Audit Log button on both screens is enabled, has no `disabled` attribute or "Coming soon" title, and shows no count badge | `Test_SC10_AuditLogButtonEnabledWithoutBadge` |

---

## §11 Telemetry

```csharp
_logger.LogInformation(
    "AuditLogModal: loaded page {Page}, Tab={Tab}, HasDateFrom={HasDateFrom}, HasDateTo={HasDateTo}, Returned={ReturnedCount}, Total={TotalCount}",
    page, tab ?? "All", dateFrom is not null, dateTo is not null, items.Count, totalCount);
_logger.LogWarning("AuditLogModal: load failed, Status={StatusCode}", (int?)statusCode);
```

- Log only counts, the page number, and the tab name. Never log row contents (`user`, `filePath`,
  `resultDetail`), because the audit log is already the system of record for those, and copying them
  into application logs would duplicate personal and path data (data minimisation).
- Never log the B2 application key, a session token or cookie value, or file content.

---

## §12 Security Notes

### B2 Credential Handling
Not applicable by construction. The modal calls only `GET /audit-log`, and CAS-009's SP selects only
`dbo.users.username` and never the `b2_application_key*` columns (CAS-009 §12). No B2 key, key ID,
or capability secret ever enters this component's state or markup (FR-03).

### Audit Trail
The screen is **read-only** (UI/UX §6.2) and introduces no state-changing action, so FR-23's
audit-before-response rule has nothing to apply to here. Viewing the log is itself not audited, which
matches CAS-009 §12. SC-9 enforces that the modal issues only `GET` requests.

### Session / Auth Requirements
The host pages already carry `[Authorize(Policy = "CachetAuthenticated")]`, and the API enforces the
same policy on `GET /audit-log`. The session cookie reaches the API through the existing
`CookieForwardingHandler`. Nothing is sent to or stored in the browser beyond what Blazor Server
renders.

### Output Encoding
`filePath` and `resultDetail` can contain attacker-influenced strings, for example a crafted archive
filename or a B2 error message. All cells must use Razor's default encoding (`@value`), and the
component must not use `MarkupString` (V5).

### Visibility Scope (data minimisation note)
CAS-009 returns **all users'** entries to any authenticated user. There is no per-user scoping, and
this spec does not add any. That matches FR-24's "viewable within the application" and this
project's 3–4-user reviewer population. It is recorded in §15 Q4 so the client can confirm it
explicitly for SOC 2 access-review purposes.

---

## §13 Test Plan

### Unit Tests (bUnit, `Cachet.ArchiveSync.Web.Tests/AuditLog/`)
| Class | Method | Proves |
|-------|--------|--------|
| `AuditLogModalTests` | `Test_V1_ToBeforeFromBlocksApplyAndSendsNoRequest` | V1 |
| `AuditLogModalTests` | `Test_V2_TabDropdownOffersExactlyTheAllowedValues` | V2 |
| `AuditLogModalTests` | `Test_V3_ApplyAndClearResetToPageOne` | V3 |
| `AuditLogModalTests` | `Test_V4_PagerBoundsAndInFlightDisable` | V4 |
| `AuditLogModalTests` | `Test_V5_CellValuesAreHtmlEncoded` | V5 |
| `AuditLogModalTests` | `Test_V6_FailureRowsRenderWithDetail` | V6 |
| `AuditLogModalTests` | `Test_SC3_TableRendersSixFr23ColumnsMappedCorrectly` | SC-3 |
| `AuditLogModalTests` | `Test_SC4_TabFilterSendsTabParam` | SC-4 |
| `AuditLogModalTests` | `Test_SC5_DateRangeSendsInclusiveUtcBounds` | SC-5 |
| `AuditLogModalTests` | `Test_SC6_PagerNavigatesWithAppliedFilters` | SC-6 |
| `AuditLogModalTests` | `Test_SC7_EmptyResultShowsEmptyStateCopy` | SC-7 |
| `AuditLogModalTests` | `Test_SC8_LoadFailureShowsErrorAndClearsRows` | SC-8 |
| `AuditLogModalTests` | `Test_SC9_ModalIssuesOnlyGetRequests` | SC-9 |
| `KeywordSearchPageTests` (extended) | `Test_SC1_OpenFromKeywordSearchLoadsDefaultPage`, `Test_SC10_AuditLogButtonEnabledWithoutBadge` | SC-1, SC-10 |
| `ODriveSyncPageTests` (extended) | `Test_SC2_OpenFromODriveSyncLoadsDefaultPage` | SC-2 |

`FakeAuditLogApiHandler` records every request (method + URI) so V1/V3/SC-4/SC-5/SC-6/SC-9 can assert
exact query strings, and it can be set to return 200 / empty / 401 / 422 / 500 / throw.

### Integration Tests
None new. The HTTP contract is already covered end to end by CAS-009's `AuditLogControllerTests`.
This spec adds no server-side code path.

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. Prerequisite: CAS-009 (`GET /audit-log`, `dbo.audit_log_lst`) is deployed. It is already `merged`.
2. Deploy Web only. There is no database, Api, or Worker change.
3. Rollback: revert the Web change. The two page edits are limited to the button and modal tag, and
   `AuditLogModal.razor` is self-contained.

---

## §15 Open Questions

- [x] **Q1: Tab dropdown values (§0 point 2).** — **resolved 2026-09-25 (Ratheesh, via /implement instruction): accepted as drafted.** This spec adds `Auth` and `Task Manager` to UI/UX
      §6.1's list because rows with those tabs are written today and would otherwise be unfilterable.
      It omits `Audit Log` because nothing writes it. *Recommended: accept as drafted.*
- [x] **Q2: Audit Log button count badge (§0 point 3).** — **resolved 2026-09-25 (Ratheesh, via /implement instruction): accepted as drafted.** UI/UX §1.1 shows a count, but its meaning is
      undefined (unviewed entries? today's failures?), and no API supports it. *Recommended: ship
      without a badge and raise it with the client. If a meaning is confirmed, it needs a CAS-009
      change spec plus a follow-up Web change.*
- [x] **Q3: UTC vs browser-local time (§8.6).** — **resolved 2026-09-25 (Ratheesh, via /implement instruction): accepted as drafted.** Drafted as UTC throughout, labeled. Local-time display
      would need a JS-interop time-zone read and a matching change to the date-boundary conversion.
      *Recommended: keep UTC for audit consistency with the stored record and reports.*
- [x] **Q4: Visibility scope (§12).** — **resolved 2026-09-25 (Ratheesh, via /implement instruction): accepted as drafted.** Every authenticated user sees every user's audit entries (CAS-009
      behavior, unchanged). Confirm this is the intended access model for SOC 2 access-review
      evidence.
- Not blocked by BRD §12.4 (web caching layer, O-Drive refresh cadence). The screen reads live from
  CAS-009 and needs no cache. Not affected by the resolved §12.1/§12.3 items (login method, drive
  encryption-at-rest).

All open items resolved as of 2026-09-25 — Q1–Q4 accepted as drafted. No remaining blockers to `status → ready`.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code, on behalf of R. Devarajan (TraceCounts) | 2026-09-25 | Draft |
| Reviewer | | | Pending |

---

## §17 Graph Impact

> `graphify-out/` exists, but the query below returned only migration nodes (paths cached from a
> different machine) and no `src/Cachet.ArchiveSync.Web` nodes. The graph is stale for the Web
> project. This section therefore comes from a manual read of `src/Cachet.ArchiveSync.Web/`,
> `AuditLogController.cs`/`AuditLogListService.cs`, the `TabModule` writers across Api/Worker, and
> CAS-009/CAS-024/CAS-025/CAS-027. Running `graphify update .` on the Windows host is recommended.

### God Nodes in Blast Radius
- `KeywordSearch.razor` (default landing page) and `ODriveSyncContentScan.razor`: a small additive
  edit to each (one button, one component tag, one `@ref` field, one open handler). Their submit and
  poll flows are untouched.

### Communities / Modules Affected
- **Audit Log**: the first UI for the module.
- **Keyword Search, O-Drive Sync**: host the trigger button only.
- **Web project only.** No Api/Core/Worker/database change. `AuditLogListItem` and `PagedResult<T>`
  (Core) are reused as they are.

### Surprising Connections
- The UI/UX tab list does not match the tabs actually written. `Auth` (login, logout, user
  provisioning) and `Task Manager` (restart) rows exist in `dbo.audit_log` but are not in UI/UX §6.1's
  dropdown (§0 point 2).
- `QuarantineService.cs` writes quarantine-add events under `tab_module = 'Keyword Search'`, not
  `'Quarantine'` (by design: quarantine-add originates on the Keyword Search tab). A reviewer who
  filters by "Quarantine" sees removals and permanent deletes but **not** additions. This is correct
  per the stored data, but the client may find it counter-intuitive. It is noted here so the reviewer
  and tester do not log it as a screen defect.

### Graphify Queries Run
```
python -m graphify query "Audit Log screen Blazor filter by tab and date range GET audit-log" --budget 1500
  → 3 nodes (CAS-005/CAS-009 migration files only); no Web nodes — graph stale for src/.
```

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria (SC-1–SC-10) have a passing test.
- [ ] All §7 Validations (V1–V6) are implemented and tested.
- [ ] No new Api/Core/Worker endpoint, DTO, or migration. Only CAS-009's `GET /audit-log` is consumed.
- [ ] The modal issues only `GET` requests (SC-9). No audit write is introduced.
- [ ] No `MarkupString` in `AuditLogModal.razor`, and every cell is encoded (V5).
- [ ] Timestamps are rendered in UTC and labeled, and date bounds are sent as UTC start/end of day (§6, §8.6).
- [ ] Tab dropdown matches §7 V2 exactly (includes Auth and Task Manager, excludes Audit Log).
- [ ] Telemetry logs counts and tab only, never row contents, B2 key, or session values (§11).
- [ ] No out-of-scope items from §4 were built (no extra filters, sorting, export, polling, count
      badge, or `MainLayout.razor` edit).

### Verification Report Format
Per template: `CAS-031.review.md` alongside this spec, with the overall verdict, a per-SC verdict
table, a security gap list, and scope violations.

## Implementation Log

**Completed:** 2026-09-25
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 93.1% (AuditLogModal.razor 100%, KeywordSearch.razor 95.7%, ODriveSyncContentScan.razor 95.3%) | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Shared/AuditLogModal.razor` | New: non-routed Audit Log modal (filters, six-column table, pager, states) |
| `src/Cachet.ArchiveSync.Web/Components/Shared/AuditLogModal.razor.css` | New: scoped styles |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | Audit Log button enabled, hosts AuditLogModal |
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | Audit Log button enabled, hosts AuditLogModal |
| `src/Cachet.ArchiveSync.Web.Tests/AuditLog/FakeAuditLogApiHandler.cs` | New: recording fake for GET /audit-log |
| `src/Cachet.ArchiveSync.Web.Tests/AuditLog/AuditLogModalTests.cs` | New: bUnit tests V1-V6, SC-3..SC-9 |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchPageTests.cs` | Extended: SC-1, SC-10 |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncPageTests.cs` | Extended: SC-2 |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/FakeKeywordSearchApiHandler.cs` | Extended: optional GET /audit-log branch |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/FakeODriveSyncApiHandler.cs` | Extended: optional GET /audit-log branch |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `KeywordSearchPageTests` | `Test_SC1_OpenFromKeywordSearchLoadsDefaultPage` | SC-1 |
| `ODriveSyncPageTests` | `Test_SC2_OpenFromODriveSyncLoadsDefaultPage` | SC-2 |
| `AuditLogModalTests` | `Test_SC3_TableRendersSixFr23ColumnsMappedCorrectly` | SC-3 |
| `AuditLogModalTests` | `Test_SC4_TabFilterSendsTabParam` | SC-4 |
| `AuditLogModalTests` | `Test_SC5_DateRangeSendsInclusiveUtcBounds` | SC-5 |
| `AuditLogModalTests` | `Test_SC6_PagerNavigatesWithAppliedFilters` | SC-6 |
| `AuditLogModalTests` | `Test_SC7_EmptyResultShowsEmptyStateCopy` | SC-7 |
| `AuditLogModalTests` | `Test_SC8_LoadFailureShowsErrorAndClearsRows (5 cases)` | SC-8 |
| `AuditLogModalTests` | `Test_SC9_ModalIssuesOnlyGetRequests` | SC-9 |
| `KeywordSearchPageTests` | `Test_SC10_AuditLogButtonEnabledWithoutBadge` | SC-10 |
| `AuditLogModalTests` | `Test_V1_ToBeforeFromBlocksApplyAndSendsNoRequest` | V-1 |
| `AuditLogModalTests` | `Test_V2_TabDropdownOffersExactlyTheAllowedValues` | V-2 |
| `AuditLogModalTests` | `Test_V3_ApplyAndClearResetToPageOne` | V-3 |
| `AuditLogModalTests` | `Test_V4_PagerBoundsAndInFlightDisable` | V-4 |
| `AuditLogModalTests` | `Test_V5_CellValuesAreHtmlEncoded` | V-5 |
| `AuditLogModalTests` | `Test_V6_FailureRowsRenderWithDetail` | V-6 |
