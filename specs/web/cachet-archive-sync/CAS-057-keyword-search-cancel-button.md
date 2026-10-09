---
id: CAS-057
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-06
priority: P1
change_type: enhancement
related_specs: [CAS-056, CAS-027, CAS-029, CAS-030, CAS-039, CAS-048]
touches:
  database: false
  api: false
  ui: true
  worker: false
  b2: false
  config: false
  security: false
migrations: []
---

# CAS-057 — Change: "Cancel search" in the Keyword Search progress bar (Blazor UI)

> **Depends on CAS-056** (cancel endpoint, `Cancelled` status, partial results contract). Split from it under Karpathy P9 — see CAS-056 §4. Do not implement before CAS-056 is merged.

## §1 Current Behavior

*Graphify not yet useful for Razor components (file-level nodes only); mapped by manual read of `KeywordSearch.razor`.*

- While a search runs, `isSearchInFlight` is `true` and the page shows one yellow progress banner (`cachet-banner-progress`) with elapsed time, `N of M items processed (P%)` and an estimate (`KeywordSearch.razor:263-271`, `ProgressBannerText` `:1604`). It has **no action**.
- Search is disabled while in flight: `CanSearch` requires `!isSearchInFlight` (`:632-634`). So a multi-hour search locks the screen.
- `PollTaskAsync` (`:1334`) polls `GET search/{id}/results` every `Worker:TaskManagerPollIntervalSeconds` (default 10 s); `409` → refresh progress and keep polling; `Completed` → `taskState = "Completed"`; **anything else → `taskState = "Failed"`** (`:1398-1406`).
- Results are shown only when `taskState == "Completed"` (`ShowResultsExplorer`, `:577-579`; context header `:287`).
- Reload/deep link (`ReloadTaskAsync`, `:882-917`): `Queued/Running` resume polling, `Completed` loads results, `Failed` shows the error banner, **`Cancelled` falls to `default` → empty initial state** (`:914-916`).
- Filename and Date searches also show the banner but finish in seconds.

---

## §2 Desired Behavior

1. **Cancel button in the bar.** For a running **Keyword-mode** search (`resultsMode == KeywordMode`, `activeTaskId` known), the yellow progress banner gains a right-aligned outlined **"Cancel search"** button (same flex layout as the existing Failed banner at `:274`). The bar's text is unchanged. Not shown for Filename/Date searches (seconds-long; CAS-056 §14 Q4) or in Files mode.
2. **Confirm before stopping.** Clicking the button swaps the button for an inline confirmation inside the same bar — *"Stop this search? Matches found so far are kept."* with **[Stop search]** and **[Keep running]**. No modal. [Keep running] restores the button. (Guards against a mis-click discarding hours of work; the cancel itself is irreversible — a cancelled search cannot be resumed.)
3. **Cancelling state.** On [Stop search] the page calls `POST tasks/{id}/cancel`.
   - `200` (task was still Queued) or `202` (Running): the bar text becomes *"Cancelling search — stopping and gathering the matches found so far…"*, the button is gone/disabled, the filter bar **stays disabled** (`isSearchInFlight` stays `true`; this is typically a few seconds), and polling continues at a **2 s** interval (instead of 10 s) until the task leaves `Running`.
   - `409 task-not-cancellable`: the search just finished — drop the confirm, keep polling; the next tick renders `Completed`/`Failed` as usual, with a toast *"The search had already finished."*
   - `404`: treated as the generic error (below). `401`: existing `sessionExpired` handling. Network/other error: the existing `GenericErrorMessage` banner and the button is restored (the search is still running).
4. **Cancelled with matches.** When the poll receives `Status: "Cancelled"` and there is at least one match, `taskState = "Cancelled"`, `isSearchInFlight = false` (filter bar re-enabled — the user can immediately run a new **Keyword, Filename or Date** search), and the existing results explorer is shown for the cancelled task (`ShowResultsExplorer` and the context header accept `Cancelled` as well as `Completed`). Above the results, a notice banner: *"Search cancelled — {done:N0} of {total:N0} items were checked. Showing the {n:N0} matches found so far; these results are incomplete."* The context header reads *"Showing partial results for "term" (ext)…"*. Quarantine selection works exactly as for a completed search.
5. **Cancelled with no matches.** Per the owner's instruction, leave the screen **as it was before the search**: no results panel; a single notice *"Search cancelled — {done:N0} of {total:N0} items were checked; no matches were found."* The status is `Cancelled` (also visible in Task Manager). The filter bar is re-enabled.
6. **Late cancel.** If the poll returns `Completed` while cancelling, render the normal completed state and a toast *"The search finished before it could be cancelled."*
7. **A new search replaces the cancelled state.** Submitting any new search clears the cancelled banner/results exactly as a new search clears a completed one today (`ResetTaskState`); the cancelled task's results stay reachable from Task Manager.
8. **Reload / deep link of a `Cancelled` task** (`ReloadTaskAsync`): with matches → state 4 (partial results + notice); with none → the existing empty initial state with no banner (unchanged from today's `default` branch; a plain page open must not nag about an old cancel). A deep link (`?taskId=`) to a cancelled task with no matches shows the state-5 notice instead.
9. **No acknowledgement.** `Cancelled` is never acknowledged (the API returns `409` for it and raises no badge — CAS-056 §6); the `OnAfterRenderAsync` acknowledge guard (`:809-816`) already only covers `Completed`/`Failed` and stays unchanged.
10. Accessibility: the button has an accessible name ("Cancel search"); the confirm region and the Cancelling/Cancelled notices use the page's existing banner roles; the control is keyboard-reachable.

---

## §3 Reason for Change

- **Type:** enhancement
- **Trigger:** product-owner request, 2026-10-06 — the search runs for hours and the screen is locked meanwhile; the user wants a visible way to cancel, keep any matches already found, and start a new search.
- **Risk if not changed:** the user cannot use the Search screen for the duration of a long search (see CAS-056 §3).

---

## §4 Blast Radius (Graph Impact)

*graphify not yet useful for Razor — blast radius mapped via manual read/grep of `src/Cachet.ArchiveSync.Web`.*

### Primary Change Target
`src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` — `Failed`-vs-other branch in `PollTaskAsync`, `ShowResultsExplorer`, `ReloadTaskAsync`, the banner block, `ProgressBannerText`.

### Direct Dependents
- `FileExplorer.razor` / `SearchResultsBrowseClient` — **unchanged**; the explorer already reads stored rows through browse (CAS-048) and the browse gate is widened server-side in CAS-056.
- `ODriveSyncContentScan.razor` — shares the `SearchResultsResponse` type and a copy of the `Cancelled` `default` branch (`:664`); **unchanged** (ContentScan cancel is out of scope, CAS-056 §14 Q5). The new optional DTO fields do not affect it.
- `TaskManagerModal.razor` — already has a `Cancelled` chip; only a now-false comment changes (§5).
- `TaskManagerBadge.razor` — unchanged (a cancelled task raises no notification).

### Downstream Modules
Keyword Search screen, Task Manager (display only), Quarantine (selection from partial results).

### Cross-Project Risk
**CROSS-PROJECT RISK (contract):** consumes CAS-056's `POST tasks/{id}/cancel` and the `Cancelled`/`IsPartial` shape of `GET search/{id}/results`. Until CAS-056 ships, the page must not be deployed (the button would `404`).

### God Node Involvement
`KeywordSearch.razor` is a large component (~1,650 lines, many states): **HIGH RISK** for regressions in the existing `Completed`/`Failed`/reload paths. Every existing branch is listed in §9 as "unchanged" with a regression test.

### Graphify Queries Run
graphify not yet vendored for Razor call analysis — analysis done via manual code read.

### Decomposition check (P9)
One page + its tests; below every size threshold. Single spec; split from CAS-056 on the project boundary.

---

## §5 Files to Change

| File | Change | Description |
|------|--------|-------------|
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | modify | Cancel button + inline confirm in the progress banner; `isCancelRequested`/`showCancelConfirm` state; `CancelSearchAsync`; `Cancelled` handling in `PollTaskAsync`, `ShowResultsExplorer`, context header, `ReloadTaskAsync`; 2 s poll while cancelling |
| `src/Cachet.ArchiveSync.Web/Components/Shared/TaskManagerModal.razor` | modify | **Comment only** (`:368`: "no code path produces it" is no longer true) |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchPageTests.cs` | modify | §13 |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchDeepLinkTests.cs` | modify | Cancelled deep link |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/FakeKeywordSearchApiHandler.cs` | modify | Fake `POST tasks/{id}/cancel` and `Cancelled` results |

Styling uses existing classes (`cachet-btn`, `cachet-banner-*`) and the inline flex pattern already used at `:274`; **no `app.css` change** unless review requires one.

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `FileExplorer.razor`, `SearchResultsBrowseClient.cs`, `FilesBrowseClient.cs` | Browse is task-status-agnostic client-side; gate widened in CAS-056 |
| `ODriveSyncContentScan.razor` | ContentScan cancel is a separate follow-up |
| `TaskManagerBadge.razor`, `Program.cs`, `TaskPollingOptions.cs` | The 2 s cancelling interval is a local constant, not a new config key (P3) |
| Any Api / Worker / Core / Database file | CAS-056 |

---

## §7 Database Changes
N/A

---

## §8 API Contract Changes
N/A (consumes CAS-056 §8: `POST /tasks/{taskId}/cancel`; `GET /search/{taskId}/results` `Status: "Cancelled"`, `IsPartial`, `ItemsProcessed`, `ItemsTotal`).

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | Banner has no action | "Cancel search" button shown only while a **Keyword-mode** search is in flight with a known task id; absent for Filename/Date/Files modes and when idle | `Test_V1_CancelButtonOnlyForRunningKeywordSearch` |
| V2 | n/a | First click never calls the API; only [Stop search] does; [Keep running] restores the button | `Test_V2_ConfirmBeforeCancel` |
| V3 | n/a | While cancelling the button cannot be clicked twice (one POST) and the filter bar stays disabled | `Test_V3_CancellingIsSingleShotAndFilterStaysDisabled` |
| V4 | Non-`Completed` result → `Failed` | `Status:"Cancelled"` → `Cancelled` state, **never** `Failed`; `Completed` and `Failed` branches unchanged | `Test_V4_CancelledIsNotFailed` + existing Completed/Failed tests |
| V5 | Results only for `Completed` | Results explorer + header also for `Cancelled` with ≥ 1 match; none for 0 matches | `Test_V5_PartialResultsShownOnlyWhenMatchesExist` |
| V6 | Filter bar disabled until `Completed`/`Failed` | Re-enabled on `Cancelled`; a new Keyword, Filename and Date search can each be submitted | `Test_V6_NewSearchAvailableAfterCancel` |
| V7 | n/a | `409` on cancel → no error banner; toast "already finished"; polling continues | `Test_V7_CancelConflictIsGraceful` |
| V8 | n/a | `401` on cancel → session-expired banner; network error → generic banner and button restored | `Test_V8_CancelErrorsHandled` |
| V9 | Reload of `Cancelled` → empty state | With matches → partial results + notice; without → unchanged empty state (plain open) / notice (deep link) | `Test_V9_ReloadCancelledTask` |
| V10 | n/a | No acknowledge call is made for a `Cancelled` task | `Test_V10_NoAcknowledgeForCancelled` |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A "Cancel search" button is visible inside the yellow bar of a running Keyword search | `Test_SC1_ButtonVisible` |
| SC-2 | Confirm → the page POSTs cancel once and shows the Cancelling text; polling speeds up to 2 s | `Test_SC2_ConfirmThenCancelling` |
| SC-3 | On `Cancelled` with matches the explorer lists the matches, the notice states the checked/total counts and "incomplete", and Quarantine selection works | `Test_SC3_PartialResultsAndQuarantine` |
| SC-4 | On `Cancelled` with no matches the page shows only the "Search cancelled … no matches" notice — no results panel | `Test_SC4_CancelledNoMatches` |
| SC-5 | After either outcome, the user can submit a new Keyword, Filename and Date search without reloading | `Test_SC5_SearchAvailableAgain` |
| SC-6 | The late-cancel (`Completed`) and `409` paths render normally with the right toast | `Test_SC6_LateCancelAndConflict` |
| SC-7 | All existing `KeywordSearch*` Web tests pass unchanged; coverage ≥ 85% on changed code | CI |
| SC-8 | Manual smoke on a real long search (≥ 5 min): cancel → bar shows Cancelling → partial results appear within a few seconds | manual, recorded in the review |

---

## §11 Security Notes

No new security surface in the UI: the cancel call goes through the existing `CachetApi` client with the forwarded auth cookie (no B2 key, session token or term in the browser beyond what the page already holds). Authorisation and audit are enforced server-side (CAS-056 §11). No file name, path or term is added to any log line (`Logger` calls log only the task id and status). The button is hidden when idle, but hiding is **not** the control — the API is.

---

## §12 Rollback Plan

1. Revert `KeywordSearch.razor` and the test files; the page returns to a bar with no action. CAS-056 can stay deployed (it is inert without the button).
2. Database / config: none.
3. Task-queue rollback: tasks already `Cancelled` by CAS-056 remain valid; the reverted page's `default` branch shows them as the empty initial state (today's behavior) and the poll branch would show them as `Failed` only if the user were watching one live at the moment of rollback.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `KeywordSearchPageTests` (bUnit) | button visibility per mode/state; confirm flow; cancelling state; cancelled with/without matches; new search after cancel; 409/401/network; late cancel | V1–V8, SC-1–SC-6 |
| `KeywordSearchDeepLinkTests` | Cancelled task via `?taskId=` and via plain reload; no acknowledge call | V9, V10 |
| Existing `KeywordSearch*` suites | regression | V4, SC-7 |

Coverage target: ≥ 85% on changed files.

---

## §14 Open Questions

All resolved — none remain open and nothing in this spec is `BLOCKED`.

- [x] **Q1 — Filter bar while "Cancelling" — RESOLVED 2026-10-06 (owner):** a new search is allowed only once the active search has reached a terminal state (Completed, Failed or Cancelled), so the filter bar stays disabled during the few-second "Cancelling" phase and the cancelled task's partial results are always loaded before a new search replaces them. Implemented in §2.3/§2.4, tested by V3/V6.
- [x] **Q2 — Button wording — RESOLVED (owner accepted drafted default):** the bar button is **"Cancel search"**; the confirm action is **[Stop search]**, with **[Keep running]** to back out (§2.1/§2.2).
- [x] **Q3 — Confirm step — RESOLVED (owner accepted drafted default):** inline two-step confirm, no modal. Rationale: the cancel is irreversible and the search may represent hours of work; the extra click is cheap insurance (§2.2).
- [x] **Q4 — Reload of a cancelled task with matches — RESOLVED (owner accepted drafted default):** a plain page open/reload **shows partial results** plus the notice (consistent with `Completed`); a cancelled task with no matches shows the empty initial state on plain open and the §2.5 notice only via deep link (§2.8).

### Sequencing note (not a spec block)
CAS-056 (cancel endpoint, `Cancelled` status, `IsPartial` contract) is a build-order dependency: implement and merge it before `/implement CAS-057` (§4 Cross-Project Risk). CAS-056's own Q4 (Filename/Date modes) is already consistent with this spec: the endpoint accepts them, this UI hides the button for them (§2.1).

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude (draft) | 2026-10-06 | |
| Reviewer | | | |
| Owner | devarajan@tracecounts.org | 2026-10-06 | Q1 confirmed; Q2–Q4 drafted defaults accepted; all open questions closed 2026-10-06; marked ready |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches the code before the change
- [ ] Desired behavior (§2) is fully implemented, incl. §2.3 error paths and §2.8 reload
- [ ] Only §5 files modified (§6 untouched; `TaskManagerModal.razor` comment-only)
- [ ] All §9 validations / §10 SCs have passing tests; existing Completed/Failed/reload paths unchanged
- [ ] The cancel POST happens only after the confirm; never twice
- [ ] No B2 key, token, term, path or file name in any new log line or toast
- [ ] CAS-056 is merged and deployed first

### Verification Report Format
`CAS-057.review.md` alongside this spec: verdict, per-SC table, scope violations, unintended blast radius, security gaps.

## Implementation Log

**Completed:** 2026-10-06
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 92.9% KeywordSearch.razor | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `KeywordSearch.razor` | cancel button, confirm, cancelling/cancelled states, reload |
| `TaskManagerModal.razor` | comment only |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `KeywordSearchPageTests` | `SC1-SC6, V1-V8` |  |
| `KeywordSearchDeepLinkTests` | `V9, V10` |  |
