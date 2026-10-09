---
id: CAS-045
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-04
priority: P1
change_type: enhancement
related_specs: [CAS-007, CAS-023, CAS-030, CAS-044]
touches:
  database: true
  api: true
  ui: true
  worker: false
  b2: false
  config: true
  security: false
migrations: [20261004_120000__CAS-045-sync-result-lst-exclude-synced.sql]
---

# CAS-045 — Change: O-Drive Sync results — per-panel paging, true totals, scan summary

> **Implements:** BRD §7 FR-13, FR-13a–FR-13d (O-Drive Sync and Content Scan results, Copy/Purge/Update
> actions) and FR-17 (irreversible-delete confirmation — behavior preserved, see §11). The screen contract
> being changed is CAS-023; the API it consumes is CAS-007 §6.2 (extended with one optional query flag and
> input validation — see §8; resolves §14 Q1 and Q4).

---

## §1 Current Behavior

Reproduced on 2026-10-04 against Content Scan task `100003` (Completed). `dbo.sync_result` holds
**New = 77,627, Removed = 3,106, Modified = 26** rows; `task_queue.result_detail` reports the same counts
(`{"NewCount":77627,"RemovedCount":3106,"ModifiedCount":26}`). The screen shows **50 / 50 / 26** rows.

Root cause — the Web page never asks for any page but the first, and never reads the API's `totalCount`:

- `ODriveSyncContentScan.razor:590` (`LoadCompletedScanAsync`) and `:871` (`PollScanAsync`) call
  `GET sync/{taskId}/results` with **no query parameters**. `SyncController.cs:54-59` defaults every panel to
  `page=1, pageSize=50`, so each panel receives at most 50 rows. The API already supports independent
  per-panel paging (`newOnODrivePage/PageSize`, `removedFromODrivePage/PageSize`,
  `modifiedOnODrivePage/PageSize` — CAS-007 §6.2) and returns `TotalCount` per panel
  (`SyncScanService.cs:106-126`); the page ignores both.
- Panel count badges render `@removedItems.Count` / `@newItems.Count` / `@modifiedItems.Count`
  (`ODriveSyncContentScan.razor:113, 191, 271`) — the number of **loaded** rows, not the total.
- There is no pager and no "Showing a–b of N" text on this screen.
- Header select-all (`SetAll`, `:970`) selects only the loaded rows. Copy/Update/Purge act only on
  `selected*Ids` (`:988-1016`).
- `PollActionAsync` (`:1098-1188`) watches an action's progress by re-querying `sync/{scanTaskId}/results`
  (page 1 only) and matching `PendingFileIds` against those 50 rows (`ApplyProgress`, `:1195-1237`). A
  selected file that is not on page 1 can never be matched.
- A `Synced` row is removed from the panel **locally** by `ApplyProgress` (`:1224-1227`); the server
  (`dbo.sync_result_lst`) has no status filter and keeps returning `Synced` rows.
- API ceiling on actions: `SyncActionService.cs:76-80` rejects a Copy/Update/Purge request with more than
  `Sync:MaxBatchSize` file ids (default `100`; confirmed 2026-09-22, R. Devarajan — CAS-007 §10/§15).

---

## §2 Desired Behavior

All of the following apply to `ODriveSyncContentScan.razor` for a `Completed` scan. Wireframe approved
2026-10-04 (R. Devarajan): scan summary strip **and** header badges, both kept.

**2.1 True counts (change a).**
- A **scan summary strip** above the panels shows `Removed from O-Drive`, `New on O-Drive`,
  `Modified on O-Drive` totals, plus "Completed <local time> · Task #<id>".
- Each panel header badge shows that panel's `TotalCount` (not `Items.Count`), formatted with thousands
  separators (`N0`). Summary strip and badges read the same value.
- Each panel footer shows "Showing a–b of N" (`a = (page-1)*pageSize+1`, `b = min(page*pageSize, N)`).

**2.2 Paging (change b).**
- Each panel pages **independently** (own page number and own page size).
- Page-size selector per panel: **25 / 50 / 100**, default **50**. Changing it resets that panel to page 1.
- Pager: `« ‹ [windowed page numbers] › »` + "Go to page [__] [Go]". Window rule: always show first and
  last page; show the current page ±2; collapse gaps to `…`; at the edges show 5 consecutive pages
  (`1 2 3 4 5 … N`, `1 … 6 7 [8] 9 10 … N`, `1 … N-4 N-3 N-2 N-1 [N]`). Current page highlighted with
  `aria-current="page"`; the control is a `<nav aria-label="Pagination">`. `« ‹` disabled on page 1,
  `› »` disabled on the last page.
- When `TotalCount <= pageSize` the pager (and "Go to page") is hidden; only "Showing a–b of N" remains.
- Changing page re-fetches only the affected panel's slice
  (`GET sync/{taskId}/results?{panel}Page=..&{panel}PageSize=..`) and replaces only that panel's rows. While
  loading, the panel keeps its height, dims its rows and shows an inline spinner; on error it shows an inline
  retry (not an empty panel). "Go to page" clamps to `1..lastPage`.
- Row order is stable (`sync_result_id ASC`, already the stored procedure's order).

**2.3 Selection.**
- Header checkbox selects/deselects **the rows on the current page only**.
- Selection **persists** across page changes and page-size changes within the same scan (`HashSet<int>` per
  panel, as today). The action button shows the running total, e.g. `Copy to B2 (125)`; a **Clear selection**
  link resets that panel.
- Selection is capped at the action batch size (§9 V6). At the cap, unchecked boxes are disabled and an inline
  notice reads "Up to N files can be submitted at once."
- **No "select all N matching" in this spec** — resolved §14 Q2 (deferred to a follow-up spec; the wireframe
  affordance is not rendered).

**2.4 Action progress with paging.**
- After a successful `202` submit, the page tracks the action by polling the **action's own task**
  (`GET tasks/{submittedTaskId}` — `SyncCopy`/`SyncUpdate`/`SyncPurge` are allowed task types,
  `TaskManagerController.cs:20-23`) every `TaskPollingOptions.PollInterval`, instead of re-reading scan results
  page 1. On `Completed` or `Failed`, reload the affected panel's **current page** (and refresh its
  `TotalCount`). This replaces the page-1 matching in `PollActionAsync`/`ApplyProgress`.
- **`Synced` rows are excluded server-side** (§14 Q1 → option B). The page always requests
  `excludeSynced=true`, so `TotalCount`, "Showing a–b of N", page count, badges and the summary strip all
  describe **remaining actionable rows** (`Pending` + `Failed`) and shrink as actions complete. Local
  removal of `Synced` rows in `ApplyProgress` is deleted; the reload after the action task finishes is the
  single source of truth. If a reload leaves the current page beyond the new last page, the panel clamps to
  the new last page and re-fetches once.
- The summary strip's tooltip/caption reads "Remaining to review"; the scan's original totals remain in
  `task_queue.result_detail` (unchanged) and are not shown on this screen.

**2.5 Unchanged:** panel layout/columns, collapse toggles, status chips, file-name click → File Viewer,
Purge confirm dialog and wording, Full Access gating, scan polling, deep-link/acknowledge behavior (CAS-030).

---

## §3 Reason for Change

- **Type:** `enhancement` (defect in coverage: results are unreachable beyond row 50 per panel)
- **Trigger:** user-reported 2026-10-04 — scan `100003` shows 50 / 50 / 26 against real totals 77,627 /
  3,106 / 26.
- **Risk if not changed:** 99.9% of "New" and 98% of "Removed" results cannot be reviewed or acted on;
  the "50" badge reads as the full count; the reviewer may purge or copy on a misleading picture of the data.

---

## §4 Blast Radius (Graph Impact)

> graphify: `graphify-out/` exists (built 2026-10-01) but `.razor` files are not indexed —
> `explain "ODriveSyncContentScan"` and `query "what depends on SyncScanResultsResponse"` returned only
> test-file stubs and unrelated nodes. **Blast radius mapped via manual grep/read** (below).

### Primary Change Target
File: `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor`
Entity: the O-Drive Sync and Content Scan Blazor page (results loading, panel markup, selection, action polling).

### Direct Dependents (callers / consumers)
- `GET sync/{taskId}/results` — consumed by this page only (3 call sites). The new `excludeSynced` flag is
  optional and defaults to `false`, so any other caller keeps today's behavior. `SyncResultRepository.cs`
  is the only caller of `dbo.sync_result_lst`.
- `Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncPageTests.cs`,
  `ODriveSyncContentScanDeepLinkTests.cs`, `ODriveSyncFlowIntegrationTests.cs`, `FakeODriveSyncApiHandler.cs`
  — exercise this page. The fake handler matches on `AbsolutePath` only, so adding query parameters does
  not break routing; tests asserting `Items.Count` badges and page-1 action polling **will** need updates.
- `TaskPollingOptions` (`Web`) — reused for the new action-task poll; unchanged.

### Downstream Modules
O-Drive Sync only (UI). Keyword Search (`.cachet-ks-pager`, CAS-044) and Audit Log (`.cachet-al-pager`) have
their own pagers — **left untouched** (§6).

### Cross-Project Risk
**CROSS-PROJECT (accepted, §14 Q1 → B):** Web + Api + Database. No change to `Cachet.ArchiveSync.Core`
DTOs (the page reuses `PagedResult<T>.TotalCount/Page/PageSize` already in `SyncScanResultsResponse`) and
none to the Worker. Deployment order: Database migration → Api → Web (an older Web against a newer Api is
safe because the flag is optional; a newer Web against an older Api would ignore the flag and show `Synced`
rows, so Web must not deploy first).

### God Node Involvement
None. `ODriveSyncContentScan.razor` is large (~1,300 lines) but is a leaf UI node; no god-node dependents found.

### Decomposition Check (P9)
One logical unit (the O-Drive Sync results screen and the single read endpoint feeding it); the Api/DB
change is one optional parameter plus input validation (≈4 files), <14 SCs. **No split of this spec.** One
item deliberately split out: "select all N matching" for Copy/Update (needs Api + Worker design; conflicts
with the confirmed `Sync:MaxBatchSize = 100`) — recorded in §14 Q2 as a follow-up spec, ID not minted.

### Graphify Queries Run
```
python -m graphify explain "ODriveSyncContentScan"            -> test-file stubs only (razor not indexed)
python -m graphify query "what depends on SyncScanResultsResponse" --budget 1200 -> unrelated nodes
```
Blast radius therefore from grep/read of: `SyncController.cs`, `SyncScanService.cs`,
`SyncActionService.cs`, `SyncScanResultsResponse.cs`, `dbo.sync_result_lst.sql`,
`dbo.sync_result.sql`, `ODriveSyncContentScan.razor`, `KeywordSearch.razor` (existing pager pattern),
`FakeODriveSyncApiHandler.cs`.

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | modify | Per-panel page/pageSize/loading state; summary strip; `TotalCount` badges; "Showing a–b of N"; pager + page-size selector; cumulative selection with cap + Clear; action progress via `GET tasks/{id}` + panel reload; remove page-1 matching. |
| `src/Cachet.ArchiveSync.Web/Components/Shared/Pager.razor` (+ `.razor.css`) | add | Reusable windowed pager (parameters: `Page`, `PageSize`, `TotalCount`, `IsLoading`, `OnPageChanged`). Third pager in the codebase (KeywordSearch, Audit Log, this) — meets P4's "appears twice". |
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor.css` (or the stylesheet that already holds `.cachet-sync-*`) | modify | Summary strip, footer, selection notice styles. |
| `src/Cachet.ArchiveSync.Web/appsettings.json` | modify | Add `ODriveSync:MaxSelectionPerAction` (default `100`) — must equal Api `Sync:MaxBatchSize` (§14 Q3). No secrets. |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_lst.sql` + `Database/migrations/20261004_120000__CAS-045-sync-result-lst-exclude-synced.sql` | modify / add | Add `@exclude_synced BIT = 0`; when 1, filter `sr.sync_status <> 'Synced'` in **both** the `COUNT(*)` and the page query. Idempotent `CREATE OR ALTER`; existing callers unaffected. |
| `src/Cachet.ArchiveSync.Api/Repositories/SyncResultRepository.cs`, `Services/SyncScanService.cs`, `Controllers/SyncController.cs` | modify | Thread `excludeSynced` (query → service → repo → `@exclude_synced`); add §8 input validation returning `400` ProblemDetails. |
| `src/Cachet.ArchiveSync.Api.Tests/**`, `Api.IntegrationTests/**` | modify / add | Tests for the flag and the 400 validation (§13). |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/*` and new `Pager` tests | modify / add | See §13. |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `Api/Services/SyncActionService.cs` | Action submit/validation unchanged (`Sync:MaxBatchSize` stays authoritative). |
| `Database/**/dbo.sync_result.sql`, `dbo.file_index_lst.sql`, all other procs | No other DB change; `sync_status` CHECK constraint untouched. |
| `Core/Dtos/SyncScanResultsResponse.cs`, `PagedResult.cs`, item DTOs | Already carry `Page/PageSize/TotalCount`. |
| `Worker/*` | No task-queue contract change. |
| `KeywordSearch.razor`, `KeywordSearch.razor.css`, `AuditLogModal.razor` | Own pagers (CAS-044 / Audit Log); not refactored onto `Pager.razor` here (P3). |
| Purge confirm dialog markup/wording | FR-17 behavior preserved verbatim. |

---

## §7 Database Changes

`dbo.sync_result_lst` (modify, no table/index/constraint change):

- New parameter `@exclude_synced BIT = 0`. When `1`, both the `@totalCount` `COUNT(*)` and the paged `SELECT`
  add `AND sr.sync_status <> N'Synced'` (leaves `Pending` and `Failed`). The same predicate must appear in
  both statements so `TotalCount` always matches the pageable set.
- Default `0` preserves today's result for every existing caller. Order stays `sync_result_id ASC`.
- Migration `Database/migrations/20261004_120000__CAS-045-sync-result-lst-exclude-synced.sql`
  (timestamped, append-only, idempotent `CREATE OR ALTER`); mirror file updated per its header convention.
- Index note: `dbo.sync_result` is filtered by `task_queue_id` (+ `comparison_type`); the added predicate is a
  residual filter on that range. Confirm the plan against task `100003` (~81k rows) during implementation;
  add an index only if the check shows a regression (any such index goes in this migration).

---

## §8 API Contract Changes

`GET /sync/{taskId}/results` (CAS-007 §6.2) — additive and backward compatible:

1. **New optional query parameter** `excludeSynced` (bool, default `false`). When `true`, the response omits
   `Synced` rows and each panel's `TotalCount` counts only non-`Synced` rows. Response shape is unchanged.
2. **Input validation (resolves §14 Q4).** For all six paging parameters: `*Page >= 1` and
   `1 <= *PageSize <= 100`. A violation returns `400` `application/problem+json`
   (`type: https://cachet.tracecounts.internal/errors/invalid-paging`, naming the offending parameter, no
   echo of unrelated data) **before** any DB call. This replaces the stored procedure's silent clamping for
   this endpoint (the procedure's clamping stays as defense in depth). The 100 cap matches `/tasks` and the
   UI's maximum (§14 Q5).
3. The Web page always sends `excludeSynced=true` plus the per-panel page/pageSize it needs.

`GET /sync/{taskId}/results` already accepts `newOnODrivePage`, `newOnODrivePageSize`,
`removedFromODrivePage`, `removedFromODrivePageSize`, `modifiedOnODrivePage`, `modifiedOnODrivePageSize`;
the Web page starts using them.

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | Page size fixed at API default 50, never sent | Page size ∈ {25, 50, 100}; any other value is not sent | `Test_V1_PageSizeSelectorOffersOnly25_50_100` |
| V2 | n/a | "Go to page" accepts integers only and clamps to `1..lastPage`; non-numeric input is ignored | `Test_V2_GoToPageClampsAndRejectsNonNumeric` |
| V3 | n/a | `›`/`»` disabled on last page; `«`/`‹` disabled on page 1; all disabled while that panel is loading | `Test_V3_PagerButtonsDisabledAtBoundsAndWhileLoading` |
| V4 | Pager n/a | Pager hidden when `TotalCount <= pageSize` | `Test_V4_PagerHiddenForSinglePage` |
| V5 | Header checkbox = loaded rows | Header checkbox = current page rows only | `Test_V5_HeaderCheckboxSelectsCurrentPageOnly` |
| V6 | Selection ≤ 50 (implicit) | Selection ≤ `ODriveSync:MaxSelectionPerAction` (default 100); further checks disabled with notice | `Test_V6_SelectionCappedAtBatchSize` |
| V7 | n/a | Selection survives page and page-size change; "Clear selection" empties only that panel | `Test_V7_SelectionPersistsAcrossPagesAndClearWorks` |
| V8 | Purge dialog count = selected | Unchanged (count = `selectedRemovedIds.Count`, cross-page) and explicit Confirm still required | `Test_V8_PurgeStillRequiresExplicitConfirmWithCrossPageCount` |
| V9 | No paging validation (silent clamp in SP) | Api returns `400` for any `*Page < 1`, `*PageSize < 1` or `*PageSize > 100`, before any DB call | `Test_V9_ResultsEndpointRejectsInvalidPaging` (Api) |
| V10 | `Synced` rows always returned | `excludeSynced=true` omits `Synced` rows from both rows and `TotalCount`; default `false` unchanged | `Test_V10_ExcludeSyncedFiltersRowsAndTotal` (Api + SP) |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | With totals New 77,627 / Removed 3,106 / Modified 26, summary strip and badges show `77,627`, `3,106`, `26` (not 50) | `Test_SC1_BadgesAndSummaryShowTotalCountNotLoadedCount` |
| SC-2 | Each panel footer reads "Showing 1–50 of 77,627" etc.; last page reads "Showing 77,601–77,627 of 77,627" | `Test_SC2_ShowingRangeText` |
| SC-3 | Clicking page 2 in "New" requests `newOnODrivePage=2&newOnODrivePageSize=50` and replaces only the New rows; Removed/Modified rows and selections are untouched | `Test_SC3_PagingOnePanelDoesNotReloadOthers` |
| SC-4 | Pager window renders exactly: p1 `1 2 3 4 5 … N`; p8 `1 … 6 7 8 9 10 … N`; last `1 … N-4 … N` | `Test_SC4_PagerWindowRule` |
| SC-5 | Changing page size resets that panel to page 1 and re-fetches with the new size | `Test_SC5_PageSizeChangeResetsToPage1` |
| SC-6 | Selecting rows on page 1 then page 2 shows the cumulative count on the action button; actions send all selected ids | `Test_SC6_CrossPageSelectionSubmitsAllSelectedIds` |
| SC-7 | After a Copy submit, progress is tracked via `GET tasks/{actionTaskId}`; on `Completed` the current page reloads and `TotalCount` refreshes; the buttons re-enable (no permanent in-flight state when selected rows were on non-first pages) | `Test_SC7_ActionCompletionTrackedByActionTaskAndPageReloaded` |
| SC-8 | While a panel page is loading, its rows are dimmed and the pager disabled; on API error an inline retry appears and the panel keeps its previous rows | `Test_SC8_LoadingAndErrorStates` |
| SC-9 | Modified panel with 26 rows shows "Showing 1–26 of 26" and no pager | `Test_SC9_SinglePagePanelHasNoPager` |
| SC-10 | Existing CAS-023 behavior still passes: disabled-until-row-checked, Full Access gating, Purge confirm, deep link (CAS-030). The CAS-023 SC-10 "Synced row disappears" outcome is preserved, now by server exclusion on reload | existing `ODriveSyncPageTests` / `ODriveSyncContentScanDeepLinkTests` (updated where §5 notes) |
| SC-12 | The Web page sends `excludeSynced=true` on every results call; after an action completes, the reloaded panel and its badge/summary exclude the synced rows (e.g. 3,106 Removed − 100 purged → `3,006`), and an out-of-range current page clamps to the new last page | `Test_SC12_SyncedRowsExcludedAndTotalsShrinkAfterAction` |
| SC-13 | Api: `excludeSynced` default `false` returns identical rows/totals to today; invalid paging → `400` | `Test_V9_...`, `Test_V10_...` (Api.Tests / IntegrationTests) |
| SC-11 | Keyboard/AT: pager is `<nav aria-label="Pagination">`, current page has `aria-current="page"`, all controls reachable by Tab | `Test_SC11_PagerAccessibility` |

---

## §11 Security Notes

No change to authentication, authorization or the B2 key path (FR-03). Copy/Update/Purge remain
`CachetFullAccess`-gated server-side and Full-Access-gated in the UI. **Purge keeps its irreversible-action
confirmation (FR-17) unchanged**; because selection now spans pages, the dialog continues to show the exact
cross-page count being purged. No new endpoint and no new audit action (the audit write for the submit stays
in the Api, before the `202` — FR-23). Logging stays count/status only (CAS-023 §9): the new page/pageSize
parameters and counts may be logged; file names/paths must not.

---

## §12 Rollback Plan

1. Revert the §5 Web files first (Razor page, `Pager.razor`, CSS, `appsettings.json` key, tests), then the
   Api changes (flag + validation).
2. Database rollback: re-apply the prior `dbo.sync_result_lst` definition (without `@exclude_synced`) via a new
   append-only migration; no data or schema is altered, so nothing to restore. Roll back in the order
   Web → Api → DB (reverse of deployment).
3. Config rollback: remove `ODriveSync:MaxSelectionPerAction`; the page falls back to its built-in default `100`.
4. Task-queue rollback: no task-queue shape or contract change. In-flight `SyncCopy/SyncUpdate/SyncPurge`
   tasks continue unaffected; after rollback the old page-1 polling simply resumes (rows beyond page 1 remain
   unreachable, as before).

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `PagerTests` (bUnit, new) | `Test_SC4_PagerWindowRule`, `Test_V3_PagerButtonsDisabledAtBoundsAndWhileLoading`, `Test_V2_GoToPageClampsAndRejectsNonNumeric`, `Test_V4_PagerHiddenForSinglePage`, `Test_SC11_PagerAccessibility` | SC-4, SC-11, V2–V4 |
| `ODriveSyncPagingTests` (bUnit, new; extend `FakeODriveSyncApiHandler` to capture query strings and serve `totalCount`) | `Test_SC1_...`, `Test_SC2_...`, `Test_SC3_...`, `Test_SC5_...`, `Test_SC8_...`, `Test_SC9_...`, `Test_V1_...` | SC-1–3, SC-5, SC-8, SC-9, V1 |
| `ODriveSyncSelectionTests` (bUnit, new) | `Test_V5_...`, `Test_V6_...`, `Test_V7_...`, `Test_SC6_...`, `Test_V8_...` | SC-6, V5–V8 |
| `ODriveSyncActionProgressTests` (bUnit, new) | `Test_SC7_ActionCompletionTrackedByActionTaskAndPageReloaded` | SC-7 |
| `ODriveSyncActionProgressTests` (cont.) | `Test_SC12_SyncedRowsExcludedAndTotalsShrinkAfterAction` | SC-12 |
| `SyncControllerTests` / `SyncScanServiceTests` (Api.Tests, extend) and `SyncResultsEndpointTests` (Api.IntegrationTests, extend) | `Test_V9_ResultsEndpointRejectsInvalidPaging`, `Test_V10_ExcludeSyncedFiltersRowsAndTotal` (incl. default-`false` parity) | SC-13, V9, V10 |
| `ODriveSyncPageTests` / `ODriveSyncContentScanDeepLinkTests` (existing) | update badge/poll assertions only | SC-10 |

Coverage target: ≥ 85% on changed files.

---

## §14 Open Questions

> All questions resolved 2026-10-04 (decisions proposed by the author per the recommendations below and
> recorded here for sign-off in §15; any reversal reopens the affected sections noted).

- [x] **Q1 — `Synced` rows → RESOLVED: option B (server-side exclusion).** `GET /sync/{taskId}/results` gains
      optional `excludeSynced` and `dbo.sync_result_lst` gains `@exclude_synced` (§7, §8). Rationale: only B
      keeps `TotalCount`, "Showing a–b of N" and the page count truthful; A leaves short pages and overstated
      totals, C changes the CAS-023 SC-10 outcome. Trade-off accepted: the change becomes cross-project
      (Web + Api + DB) with a fixed deploy order DB → Api → Web (§4). Affects §2.4, §4–§9, §10 SC-12/13, §12, §13.
- [x] **Q2 — "Select all N matching" → RESOLVED: out of scope; follow-up spec.** Cannot be Web-only given
      `Sync:MaxBatchSize = 100` (`SyncActionService.cs:76-80`) and per-id DB validation (`:91-101`). Follow-up
      (Api + Worker): server-expanded, batched job, e.g. "all Pending of type X for scan T". Spec ID to be minted
      with `python -m cachet_agent_tools spec next-id` when drafted (not by hand). Until then selection is capped
      at 100 (V6) and the wireframe's select-all-N affordance is not rendered. Purge never gets select-all-N (FR-17).
- [x] **Q3 — Duplicated cap → RESOLVED: accept the duplication.** `ODriveSync:MaxSelectionPerAction` stays in
      Web config (default `100`). The Api remains authoritative (`400` if exceeded), so a mismatch degrades
      to a clear server error, never to an over-large action. Guard: a note beside both keys in
      `appsettings*.json` and an assertion in the Web tests that the default equals the Api default (`100`).
      Exposing the cap from the Api is deferred to the Q2 follow-up, which touches that surface anyway.
- [x] **Q4 — Server-side hardening → RESOLVED: included in this spec.** `400` validation for
      `page >= 1` and `1 <= pageSize <= 100` on all six paging parameters (§8 item 2, V9). It is the same
      controller action already being modified and removes unbounded `pageSize` as a load risk.
- [x] **Q5 — Page sizes → RESOLVED: 25 / 50 / 100 only**, default 50 (approved 2026-10-04); 100 matches
      `/tasks` and is now enforced server-side (Q4).
- [x] **Q6 — Pager refactor → RESOLVED: deferred.** KeywordSearch (CAS-044) and Audit Log pagers are not
      migrated onto `Pager.razor` here (P3, §6). Revisit as a separate refactor spec once `Pager.razor` is stable.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-10-04 | Wireframe + selection scope + page sizes + summary-and-badges approved 2026-10-04 |
| Author (open-question resolutions) | R. Devarajan (TraceCounts), via Claude Code | 2026-10-04 | §14 Q1–Q6 resolved as proposed (Q1 → B, Q4 included); **pending explicit confirmation by the owner before status → `ready`** |
| Reviewer | | | |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches the code BEFORE the change (page calls with no params; badges = `.Count`)
- [ ] Desired behavior (§2) fully implemented, including action progress via the action's own task
- [ ] Only files in §5 were modified (§6 files untouched — notably no Worker/Core edits, no `SyncActionService` change)
- [ ] All §10 Success Criteria have passing tests; §9 validations implemented and tested
- [ ] §8: only the additive `excludeSynced` flag and the paging `400` validation changed; default behavior of the endpoint is otherwise identical; both `COUNT(*)` and page query carry the same `@exclude_synced` predicate
- [ ] §11: Purge confirmation (FR-17) unchanged and shows the exact cross-page count; no file names in logs
- [ ] §4 blast radius accurate (no side effects in Keyword Search / Audit Log pagers)
- [ ] §14 Q1–Q6 resolved and recorded (done 2026-10-04); sign-off recorded in §15 before status is set to `ready`

### Verification Report Format
Reviewer writes `CAS-045.review.md` (APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED), per-SC verdicts,
scope violations, unintended blast radius, security gaps.
