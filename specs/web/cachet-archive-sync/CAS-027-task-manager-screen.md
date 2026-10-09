---
id: CAS-027
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-24
priority: P1
related_specs: [CAS-006, CAS-007, CAS-016, CAS-022, CAS-023, CAS-025, CAS-026]
touches:
  database: false
  api: false          # consumes CAS-026's new endpoints and CAS-016's existing restart endpoint as-is
  ui: true
  worker: false
  b2: false
  config: true        # new Web-side poll-interval app setting (§9)
  security: false
migrations: []
---

# CAS-027 — Task Manager Screen (Queued/Running/Completed/Failed Task List)

WBS Task 25 (Web UI Development — Task Manager screen). Implements FR-18–FR-22a. UI contract fixed by
`CACHET_UIUX_Design_Specification_v1.md` §5 (Task Manager) and §10 (cross-cutting completion/
notification behavior). API contract fixed by `CAS-026` (list/detail/badge, status `draft` — see §0)
and `CAS-016` (restart, merged).

---

## §0 Scope Correction and Load-Bearing Gaps (read this first)

This screen has never existed in this codebase (`CAS-022` §0/§4 and `CAS-025` §0 both explicitly
deferred it — "no button is added for it... adding a button with nothing behind it would be worse than
omitting it"). Building it for real surfaces three gaps a manual read of the current code confirmed,
each resolved below rather than silently worked around or silently expanded past the task brief:

1. **No backing list/detail/badge API exists yet.** `TaskManagerController.cs` hosts only `CAS-016`'s
   `POST /tasks/{taskId}/restart`. This spec's screen depends on `CAS-026` (companion spec, drafted
   alongside this one) for `GET /tasks`, `GET /tasks/{taskId}`, `GET /tasks/summary`, and
   `POST /tasks/mark-viewed`. **This spec cannot reach `status: ready` before `CAS-026` does** — recorded
   in §15, not silently assumed away.
2. **"View Results" (FR-21) cannot deep-link every task type the same way.** `KeywordSearch.razor`
   (`CAS-022`) and `ODriveSyncContentScan.razor` (`CAS-023`) each poll a task's results only for the
   task they themselves just submitted in the current circuit — neither currently accepts an external
   `taskId` to load an *already-completed* task's results on page load. Closing this gap is unavoidable
   for FR-21 to mean anything from Task Manager (the task brief explicitly asks for "how a user
   navigates from a completed task to its populated results screen") — so, unlike `CAS-022`'s narrower
   in-scope/out-of-scope call, this spec makes the small, targeted addition itself (§3, §8.3) rather than
   flagging it as a future gap.
3. **Sync actions (`SyncCopy`/`SyncUpdate`/`SyncPurge`) cannot be deep-linked by their own `taskId` at
   all.** `ODriveSyncContentScan.razor`'s own code comment (§8.4, lines ~578–587) documents that
   `GET /sync/{taskId}/results` (`CAS-007` §6/§7 V1) only accepts `task_type = 'ContentScan'` — a sync
   action's own `taskId` 404s against that endpoint. The existing screen works around this by re-polling
   the *original scan's* `taskId` instead. This spec's "View Results" therefore does **not** attempt a
   `?taskId=` deep link for `SyncCopy`/`SyncUpdate`/`SyncPurge` rows — it navigates to `/odrive-sync`
   with no `taskId`, landing the user on the current comparison state (which already reflects the
   completed sync action, since the Worker updates `sync_result` rows in place). This is the only
   workable behavior given `CAS-007`'s actual merged contract, not a design preference (§8.3).

---

## §1 Summary

Builds the Task Manager screen: a header button + badge (`MainLayout.razor`, closing the gap `CAS-025`
§0 deliberately left open) that opens a modal listing every task the signed-in user has submitted —
newest-first, with **Type**, **Submitted By**, **Start Time**, and **Status** (FR-19) — backed by
`CAS-026`'s `GET /tasks`. A **Completed** row offers **View Results**, navigating into the Keyword
Search or O-Drive Sync screen with that task's results already populated (FR-21, §0 point 2/3). A
**Failed** row offers **View Details** (the task's `resultDetail`, inline — no separate error-message
store, per UI/UX §5.2) and **Restart** (reusing `CAS-016`'s already-built
`POST /tasks/{taskId}/restart`). The header badge (FR-22) shows the count of in-progress tasks plus
tasks completed/failed since the user last opened Task Manager, backed by `CAS-026`'s
`GET /tasks/summary`/`POST /tasks/mark-viewed`. The screen also carries the FR-22a processing-time
message as a static, always-visible note, since a user may open Task Manager without having just
submitted anything (unlike the existing per-screen toasts, which only show right after a fresh
submission).

---

## §2 User Story

As a **TraceCounts reviewer**, I want a single place to see every search/scan/sync task I've submitted —
what it is, when it started, and whether it's still running, finished, or failed — so that I can close my
browser after submitting a long-running task, come back later, and immediately tell (via a badge, without
hunting through each screen) whether anything needs my attention, then jump straight to a completed
task's results or retry a failed one (FR-18, FR-19, FR-20, FR-21, FR-22, FR-22a).

---

## §3 In-Scope

- **`MainLayout.razor` header change:** replace `CAS-025`'s deliberately-omitted Task Manager slot with a
  real button + live count badge, placed immediately left of the (still-disabled) Quarantine Queue
  button, matching that button's existing visual pattern (UI/UX §1.1). Polls `GET /tasks/summary` on a
  configurable interval (§9) while any authenticated page is open, so the badge reflects state that
  changed while the user was elsewhere (FR-22) without requiring the modal to be open.
- **New `TaskManagerModal.razor`** (non-routed, `@ref`-opened component — same convention as
  `QuarantineQueueModal.razor`, `CAS-024`):
  - Table: **Type** (mapped to display labels — see §8.2), **Submitted By**, **Start Time**, **Status**
    (chip, colored per UI/UX §5.2's table), newest-first, backed by `GET /tasks` (§8.1).
  - **View Results** button on `Completed` rows → navigates per §0 point 2/3 and §8.3.
  - **View Details** button on `Failed` rows → expands the row inline to show `resultDetail` from
    `GET /tasks/{taskId}` (§8.4).
  - **Restart** button on `Failed` rows → calls `POST /tasks/{taskId}/restart` (`CAS-016`, unchanged),
    then re-fetches the list (§8.5).
  - Static FR-22a processing-time note, always visible at the top of the modal (§8.6).
  - Calls `POST /tasks/mark-viewed` when the modal opens, clearing the badge for tasks shown (§8.7).
  - Empty/loading/error states matching this codebase's established pattern (`QuarantineQueueModal.razor`,
    §8.8).
- **`KeywordSearch.razor` targeted addition:** a `[SupplyParameterFromQuery] int? TaskId` parameter; if
  present on load, immediately begin polling that `taskId` via the existing, unmodified `PollTaskAsync`
  method instead of waiting for a fresh submission (§8.3, §0 point 2).
- **`ODriveSyncContentScan.razor`` targeted addition:** the same `[SupplyParameterFromQuery] int? TaskId`
  pattern, reusing the existing, unmodified `PollScanAsync` method — meaningful only for `ContentScan`
  deep links (§0 point 3).
- New Web app setting `Worker:TaskManagerPollIntervalSeconds` (§9) — reuses the exact key name and value
  (10s) `CACHET_AsyncProcessingEngine_Design_v1.md` §8/§9 already resolved with the client (2026-09-09)
  for this exact purpose, added to `src/Cachet.ArchiveSync.Web/appsettings.json` (a process-local copy,
  same pattern `KeywordSearch.razor` already uses for `B2:BucketName`).

---

## §4 Out-of-Scope

- **`CAS-026`'s API/database work** — this spec only consumes it.
- **Cancel action** — not requested by UI/UX §5.2's row-affordance table (no `Queued`/`Running` row
  action is drawn); `CAS-026` §0/§4 documents why the backing endpoint isn't built either.
- **Filtering the Task Manager list** — UI/UX §5.2 explicitly contrasts this screen with the Audit Log
  (FR-24 requires filters there) and says to "keep this screen to the required columns rather than
  adding filtering not asked for." No filter UI is built here.
- **Pagination controls** — `CAS-026`'s `GET /tasks` supports `page`/`pageSize`, but at this project's
  confirmed 3–4 user scale and no BRD-stated page-size requirement for this screen, the modal requests a
  single generously-sized page (§8.1) rather than building pager UI not asked for. Flagged in §15 as a
  latent limitation if task volume ever grows large enough to matter.
- **Percent-complete/ETA on `Running` rows** — UI/UX §5.2 explicitly instructs against fabricating one.
- **Worker-restart-orphan sweep UI** — UI/UX §5.3 confirms the existing `Failed` status + this spec's own
  **View Details**/**Restart** affordances already cover that case; no new UI element is needed.
- **Quarantine Queue / Audit Log / Report header buttons** — untouched, remain `CAS-025`'s disabled
  placeholders.
- **Any `Cachet.ArchiveSync.Api`, `Core`, `Worker`, or database change** beyond consuming `CAS-026`'s and
  `CAS-016`'s already-specified/-built contracts exactly as they exist.

---

## §5 Database Objects

N/A — `touches.database: false`. All schema/procedure work is `CAS-026`'s.

---

## §6 API Endpoints

No new endpoint. Consumes, unmodified:

| Route | Method | Source | Purpose here |
|---|---|---|---|
| `/tasks` | GET | `CAS-026` §6.1 | Task list (§8.1) |
| `/tasks/{taskId}` | GET | `CAS-026` §6.2 | View Details' `resultDetail` (§8.4) |
| `/tasks/summary` | GET | `CAS-026` §6.3 | Header badge (§8.7) |
| `/tasks/mark-viewed` | POST | `CAS-026` §6.4 | Clears badge on modal open (§8.7) |
| `/tasks/{taskId}/restart` | POST | `CAS-016` §8 | Restart button (§8.5) |
| `/search/{taskId}/results` | GET | `CAS-006` §6.2 | `KeywordSearch.razor` deep-link poll (§8.3, reused as-is) |
| `/sync/{taskId}/results` | GET | `CAS-007` §6 | `ODriveSyncContentScan.razor` deep-link poll (§8.3, reused as-is, `ContentScan` only) |

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | The modal's task table renders exactly the four FR-19 columns (Type, Submitted By, Start Time, Status) — no filter controls, no extra columns | Visual/markup only | `Test_V1_TaskTableHasExactlyFourRequiredColumns` |
| V2 | **View Results** renders only on `Completed` rows; **View Details**/**Restart** render only on `Failed` rows; no row action renders for `Queued`/`Running` | Wrong affordance shown | `Test_V2_RowAffordancesMatchStatus` |
| V3 | A `KeywordSearch.razor` or `ODriveSyncContentScan.razor` load with a `taskId` that 404s or belongs to another user shows the existing generic error banner, never an unhandled exception or blank page | Error banner shown | `Test_V3_InvalidDeepLinkTaskIdShowsErrorBanner` |
| V4 | The header badge count equals `inProgressCount + unviewedCompletedCount` from `GET /tasks/summary`, refreshed at least once per configured poll interval | Stale/wrong count | `Test_V4_BadgeCountMatchesSummaryResponse` |
| V5 | Opening the Task Manager modal calls `POST /tasks/mark-viewed` exactly once per open, before or immediately after the list loads | Badge never clears | `Test_V5_OpenCallsMarkViewedExactlyOnce` |
| V6 | Restart is disabled while a restart request for that row is in flight (no double-submit) | Duplicate `POST` | `Test_V6_RestartDisabledWhileInFlight` |

---

## §8 UI / UX

### §8.1 Task Manager Modal — Layout

Per UI/UX §5.2, a modal/panel (not a routed page), newest-first, exactly these columns:

| Column | Source | Notes |
|---|---|---|
| Type | `GET /tasks` `type` | Display label mapping (§8.2) — never the raw enum string. |
| Submitted By | `GET /tasks` `submittedBy` | Username as returned (matches this project's existing display-name-as-stopgap convention, `CAS-021` §15/`CAS-025` §8.1 — not re-litigated here). |
| Start Time | `GET /tasks` `startTime` | Rendered in the browser's local time zone (matches `KeywordSearch.razor`'s existing `FormatRelativeTime`-adjacent convention for time display). |
| Status | `GET /tasks` `status` | Chip, colored per §8.2's table. |

Requests a single page (`pageSize` large enough to cover this project's confirmed task volume at 3–4
users — e.g. 200, `CAS-026`'s validated max) rather than building pager UI (§4).

### §8.2 Status Chips and Type Labels

| Status | Chip color | Row affordance |
|---|---|---|
| Queued | gray | none |
| Running | blue, subtle progress indicator | none — no percent/ETA (§4) |
| Completed | green | **View Results** |
| Failed | red | **View Details**, **Restart** |
| Cancelled | gray, struck-through label | none — no code path produces this today (`CAS-026` §6.1), rendered defensively so an unrecognized-but-valid enum value never breaks the table |

`type` display labels: `KeywordSearch` → "Keyword Search", `ContentScan` → "Content Scan", `SyncCopy` →
"Copy to B2", `SyncUpdate` → "Update in B2", `SyncPurge` → "Purge from B2" — matches UI/UX §5.2's exact
type vocabulary.

### §8.3 View Results Navigation (FR-21)

On a `Completed` row's **View Results** click, per §0 points 2–3:

| `type` | Navigation target |
|---|---|
| `KeywordSearch` | `NavigationManager.NavigateTo($"/?taskId={taskId}")` |
| `ContentScan` | `NavigationManager.NavigateTo($"/odrive-sync?taskId={taskId}")` |
| `SyncCopy` \| `SyncUpdate` \| `SyncPurge` | `NavigationManager.NavigateTo("/odrive-sync")` — **no** `taskId` param (§0 point 3; `CAS-007`'s contract cannot resolve a sync action's own `taskId` independently of its originating scan) |

**`KeywordSearch.razor` change (targeted, reuses existing code):**
```csharp
[SupplyParameterFromQuery] public int? TaskId { get; set; }

protected override async Task OnInitializedAsync()
{
    bucketName = Configuration["B2:BucketName"];
    await LoadQuarantineAsync();

    if (TaskId is not null)
    {
        isSearchInFlight = true;
        taskState = "InProgress";
        submittedAtUtc = null; // unknown for a deep link — banner copy adjusts (below)
        _ = PollTaskAsync(TaskId.Value);
    }
}
```
The in-progress banner's copy already reads `Search in progress — submitted @FormatRelativeTime(...)
ago.` (line 79 of the current file); for a deep-linked task with no known `submittedAtUtc`, this spec
adds a null-guarded fallback: `Search in progress. Results will appear here automatically when ready.`
(omitting the "submitted X ago" clause rather than showing a broken/blank relative-time string).
`PollTaskAsync` itself is reused byte-for-byte — it already generically polls any `taskId` and already
handles the `404`/`409`/`200` cases this deep-link path needs (§0 point 2).

**`ODriveSyncContentScan.razor` change:** the same `[SupplyParameterFromQuery] int? TaskId` parameter,
calling that screen's existing scan-poll entry point (`PollScanAsync(TaskId.Value)`) on init when
present — mirrors the `KeywordSearch.razor` change exactly; the implementer should read that screen's
current `OnInitializedAsync` before writing this to match its own existing null-guards/banner
conventions rather than copy `KeywordSearch.razor`'s verbatim.

### §8.4 View Details (FR-19, Failed rows)

Inline expansion within the modal's table row (no navigation, no new screen) showing
`GET /tasks/{taskId}`'s `resultDetail` verbatim — matches UI/UX §5.2's "do not duplicate a separate
error-message store." A `GET` failure while expanding (e.g., a transient network error) shows this
codebase's standard generic error text inline rather than leaving the row silently unresponsive.

### §8.5 Restart (Failed rows)

Calls the existing `POST /tasks/{taskId}/restart` (`CAS-016`, unmodified). On `202`, shows a brief
confirmation ("Restarted as a new task.") and re-fetches the list so the new `Queued` row appears. On
`403`/`404`/`409` (all already-defined by `CAS-016` §8), shows the generic error banner — this screen
does not need to distinguish those three cases in its own copy, since `CAS-016`'s own design already
made `403` intentionally indistinguishable from the others for a non-owner (§11 of that spec) and the
other two are rare edge cases (row disappeared, or was already restarted from another tab) that a simple
"couldn't restart — please refresh and try again" message covers adequately at this project's scale.

### §8.6 FR-22a Processing-Time Messaging

A static note, always visible at the top of the modal (not tied to any specific task):

> *"Search, scan, and sync tasks run in the background and can take anywhere from about 10 minutes to a
> full day to complete, depending on data volume. You can close this window — or your browser — and come
> back later; your task will keep running and appear here when it's done."*

This is the same "10 minutes to a full day" figure FR-22a confirms and the existing per-screen toasts
already use ("a few minutes to a full day" — `KeywordSearch.razor` line 385,
`ODriveSyncContentScan.razor`'s equivalent); this spec's copy additionally states the closeable-browser
guarantee (FR-20) explicitly, since a user opening Task Manager *without* having just submitted anything
never sees either screen's submission-time toast.

### §8.7 Header Badge (FR-22)

`MainLayout.razor` polls `GET /tasks/summary` every `Worker:TaskManagerPollIntervalSeconds` (10s, §9)
while an authenticated page is rendered (the header block already only renders when `isAuthenticated`,
§8.1 pattern already in the file). Badge text: the sum of `inProgressCount + unviewedCompletedCount`; no
badge/count shown at all when the sum is zero (matches the existing Quarantine Queue/Audit Log button's
"no count" convention when empty, `CAS-025` §8.1). Opening `TaskManagerModal` calls
`POST /tasks/mark-viewed` (§8.1's load sequence), so the badge's `unviewedCompletedCount` component
drops to 0 on the next poll tick after close — `inProgressCount` is unaffected by mark-viewed and
continues reflecting live state.

### §8.8 Empty / Error States

Matches `QuarantineQueueModal.razor`'s established pattern exactly:

| State | Copy |
|---|---|
| No tasks ever submitted | "No tasks yet. Search, scan, and sync actions you submit will appear here." |
| Load failure (`GET /tasks`) | "Something went wrong. Please try again." (same `GenericErrorMessage` constant convention as every other modal in this codebase) |
| Restart/View Details row-level failure | Same generic message, shown inline at the row, not as a modal-wide banner (matches `QuarantineQueueModal.razor`'s per-row error convention). |

---

## §9 Configuration Changes

| Key | Default | Description | Env Override |
|-----|---------|-------------|---------------|
| `Worker:TaskManagerPollIntervalSeconds` | `10` | Client-side (Blazor circuit) interval for the header badge poll (§8.7). Value and key name already resolved by `CACHET_AsyncProcessingEngine_Design_v1.md` §8/§9 (2026-09-09) — this spec adds the same key to `src/Cachet.ArchiveSync.Web/appsettings.json` (a process-local copy; Web reads its own config independently of the Worker process that originally motivated the key's name, matching `KeywordSearch.razor`'s existing `B2:BucketName` precedent for a key read independently by two processes). | Yes |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | The Task Manager modal lists every task the signed-in user submitted, newest-first, with exactly the four FR-19 columns correctly populated | `Test_SC1_ModalListsOwnTasksWithRequiredColumns` |
| SC-2 | Clicking **View Results** on a Completed `KeywordSearch` row navigates to `/?taskId={id}`, and that page auto-populates with the task's results without requiring a fresh search submission | `Test_SC2_ViewResultsDeepLinksKeywordSearchAndAutoPopulates` |
| SC-3 | Clicking **View Results** on a Completed `ContentScan` row navigates to `/odrive-sync?taskId={id}` and auto-populates | `Test_SC3_ViewResultsDeepLinksContentScanAndAutoPopulates` |
| SC-4 | Clicking **View Results** on a Completed `SyncCopy`/`SyncUpdate`/`SyncPurge` row navigates to `/odrive-sync` with no `taskId` param | `Test_SC4_ViewResultsForSyncActionsNavigatesWithoutTaskId` |
| SC-5 | Clicking **View Details** on a Failed row displays that task's `resultDetail` inline, with no navigation | `Test_SC5_ViewDetailsShowsResultDetailInline` |
| SC-6 | Clicking **Restart** on a Failed row calls `POST /tasks/{taskId}/restart` and the modal shows the newly-created `Queued` row after refresh | `Test_SC6_RestartCreatesNewQueuedRowVisibleAfterRefresh` |
| SC-7 | The header badge shows a nonzero count whenever `GET /tasks/summary`'s `inProgressCount + unviewedCompletedCount > 0`, and no badge when it's zero | `Test_SC7_BadgeReflectsSummaryCounts` |
| SC-8 | Opening the modal clears the badge's unviewed-completed component (via `POST /tasks/mark-viewed`) without affecting `inProgressCount` | `Test_SC8_OpeningModalClearsUnviewedCountOnly` |
| SC-9 | A task submitted, browser closed, reopened in a new session — Task Manager still shows its current state (FR-20) | `Test_SC9_TaskStateVisibleAfterNewSession` |
| SC-10 | The FR-22a processing-time note is visible in the modal regardless of whether any task is currently in flight | `Test_SC10_ProcessingTimeNoteAlwaysVisible` |

---

## §11 Telemetry

```csharp
_logger.LogInformation("TaskManagerModal: opened, TaskCount={TaskCount}", items.Count);
_logger.LogInformation("TaskManagerModal: restart requested, TaskId={TaskId}", taskId);
_logger.LogInformation("MainLayout: task manager badge poll, InProgressCount={InProgressCount}, UnviewedCompletedCount={UnviewedCompletedCount}", inProgressCount, unviewedCompletedCount);
```
- Never log: the B2 application key, a session token/cookie value, or a task's `resultDetail`/
  `requestParameters` contents beyond what the audit log already records — telemetry here carries only
  ids and counts.

---

## §12 Security Notes

### B2 Credential Handling
This screen never touches the B2 application key — every endpoint it calls (`CAS-026`'s four,
`CAS-016`'s restart, `CAS-006`'s/`CAS-007`'s results lookups) already brokers B2 access entirely
server-side, unchanged by this spec (FR-03).

### Audit Trail
No new audit-writing action is introduced by this screen. Restart's audit write already happens inside
`CAS-016`'s existing `TaskManagerService.RestartAsync` before its `202` response — this spec's UI simply
calls that already-audited endpoint. Viewing the task list, viewing details, and navigating to results
are reads, matching `CAS-026` §12's "not one of FR-23's four audited categories" determination.

### Session / Auth Requirements
Every page/component this spec touches requires `[Authorize(Policy = "CachetAuthenticated")]` — the
modal and header badge poll only run inside an authenticated circuit (the header block already only
renders `isAuthenticated`, per the existing `MainLayout.razor` pattern this spec extends, not replaces).
No `CachetFullAccess`-gated action is added (Restart/View Results/View Details all require only base
authentication, matching `CAS-026`'s endpoint policies).

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `TaskManagerModalTests` (bUnit) | `Test_V1_TaskTableHasExactlyFourRequiredColumns` | V1 |
| `TaskManagerModalTests` | `Test_V2_RowAffordancesMatchStatus` | V2 |
| `TaskManagerModalTests` | `Test_V6_RestartDisabledWhileInFlight` | V6 |
| `TaskManagerModalTests` | `Test_SC4_ViewResultsForSyncActionsNavigatesWithoutTaskId` | SC-4 |
| `TaskManagerModalTests` | `Test_SC5_ViewDetailsShowsResultDetailInline` | SC-5 |
| `TaskManagerModalTests` | `Test_SC10_ProcessingTimeNoteAlwaysVisible` | SC-10 |
| `MainLayoutTests` (bUnit) | `Test_SC7_BadgeReflectsSummaryCounts` | SC-7 |
| `MainLayoutTests` | `Test_SC8_OpeningModalClearsUnviewedCountOnly` | SC-8 |
| `KeywordSearchDeepLinkTests` (bUnit) | `Test_SC2_ViewResultsDeepLinksKeywordSearchAndAutoPopulates` | SC-2 |
| `KeywordSearchDeepLinkTests` | `Test_V3_InvalidDeepLinkTaskIdShowsErrorBanner` | V3 |
| `ODriveSyncContentScanDeepLinkTests` (bUnit) | `Test_SC3_ViewResultsDeepLinksContentScanAndAutoPopulates` | SC-3 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `TaskManagerEndToEndTests` (WebApplicationFactory-style, exercising `CAS-026`'s real endpoints) | `Test_SC1_ModalListsOwnTasksWithRequiredColumns` | SC-1 |
| `TaskManagerEndToEndTests` | `Test_SC6_RestartCreatesNewQueuedRowVisibleAfterRefresh` | SC-6 |
| `TaskManagerEndToEndTests` | `Test_SC9_TaskStateVisibleAfterNewSession` | SC-9 |
| `TaskManagerEndToEndTests` | `Test_V5_OpenCallsMarkViewedExactlyOnce` | V5 |

### Coverage Target
Changed files must reach ≥ 85% line coverage, per this project's standard.

---

## §14 Rollout

1. `CAS-026` (database migration + Api) must be deployed first — this spec's screen is non-functional
   without it.
2. Deploy Web (header change, new `TaskManagerModal.razor`, the two deep-link parameter additions).
3. Rollback: revert the Web commits (pure UI addition to `MainLayout.razor`'s existing conditional
   block; `KeywordSearch.razor`/`ODriveSyncContentScan.razor`'s new `TaskId` parameter is optional and
   inert when absent, so reverting is safe even if a `CAS-026` deploy already shipped independently).

---

## §15 Open Questions

- [ ] **Blocking dependency on `CAS-026`, currently `status: draft`.** This spec cannot move to `ready`
      until `CAS-026` does (§0 point 1). Not a design gap — a sequencing note for whoever schedules
      `/implement`.
- [ ] **Sync-action "View Results" lands on the live comparison state, not a historical snapshot
      (§0 point 3, §8.3).** If a user runs a second scan between when a Copy/Update/Purge action
      completed and when they click View Results from Task Manager, the O-Drive Sync screen they land on
      reflects the *current* comparison, not necessarily the exact state at that action's completion.
      Accepted as consistent with `CAS-023`'s own existing behavior (not a regression this spec
      introduces) — flag for the client if a snapshot-at-completion view is actually wanted, since that
      would require a `CAS-007` contract change, out of this spec's scope.
- [ ] **Single-page task list, no pagination UI (§4).** Acceptable now; revisit if task volume at this
      project's scale ever exceeds one page in practice.
- [ ] This spec does not depend on either open BRD item (§12.2 login method — resolved; §12.4 Web
      caching layer / O-Drive refresh cadence — not applicable to a live task list, per `CAS-026` §4).

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code, on behalf of R. Devarajan (TraceCounts) | 2026-09-24 | Draft |
| Reviewer | | | Pending |

---

## §17 Graph Impact

> `graphify update .` has not been run against the application source tree in this repo. Per
> `CLAUDE.md`'s fallback rule, this section is a manual read of `src/Cachet.ArchiveSync.Web/` and the
> related specs listed in this spec's frontmatter.

### God Nodes in Blast Radius
- `MainLayout.razor` — renders on every authenticated page; already a high-fan-in file (`CAS-025`
  touched it for the header shell). This spec extends its existing conditional header block rather than
  restructuring it.
- `KeywordSearch.razor` — the default landing page (`CAS-022`); this spec's change is additive (one new
  optional parameter, reusing an existing method) and does not alter its existing submit-and-poll flow
  for a user who arrives without a `taskId`.

### Communities / Modules Affected
- **Task Manager** (this spec's primary subject — first UI for the module).
- **Keyword Search**, **O-Drive Sync and Content Scan** — both receive a small, additive deep-link
  parameter (§8.3), not a restyle or behavior change to their existing submission flows.
- **Web project only.** No Api/Core/Worker change (all consumed contracts are `CAS-026`'s or `CAS-016`'s,
  unmodified).

### Surprising Connections
- FR-21's "navigate to the populated results screen" turned out to have a real, pre-existing API
  limitation for three of five task types (§0 point 3) — not obvious from the BRD/UI-UX spec text alone,
  only surfaced by reading `ODriveSyncContentScan.razor`'s own code comments.
- The badge (FR-22) has no natural per-task "viewed" state anywhere in the current schema — closing it
  required `CAS-026` to touch `dbo.users` (an Auth-module table), a cross-module dependency this Web
  spec doesn't itself introduce but does rely on.

### Graphify Queries Run
```
None — graphify not yet vendored for application source in this repo; analysis done via manual
grep/read of src/Cachet.ArchiveSync.Web/ and CAS-016/CAS-022/CAS-023/CAS-025/CAS-026's specs, per
CLAUDE.md's stated fallback.
```

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test.
- [ ] All §7 Validations are implemented and tested.
- [ ] §6 confirms no new Api/Core/Worker endpoint was added — only existing `CAS-026`/`CAS-016`
      contracts consumed.
- [ ] §12 Security Notes hold — no B2 key exposure, `CachetAuthenticated` enforced on every new UI path,
      no new audit-write introduced by this spec's own code (restart's audit write remains `CAS-016`'s).
- [ ] §9 Config key (`Worker:TaskManagerPollIntervalSeconds`) is present in
      `src/Cachet.ArchiveSync.Web/appsettings.json`.
- [ ] §11 Telemetry events are emitted with no B2 key / session token / task detail content in logs.
- [ ] §17 Graph Impact matches actual code changes — confirm `KeywordSearch.razor`'s and
      `ODriveSyncContentScan.razor`'s existing submit-and-poll behavior (for a user who did NOT arrive
      via a deep link) is unchanged.
- [ ] No out-of-scope changes from §4 were made — specifically: no Cancel UI, no filter UI, no pager UI,
      no percent/ETA indicator on Running rows.
- [ ] §0 point 3's sync-action navigation behavior matches exactly (no `taskId` param for
      `SyncCopy`/`SyncUpdate`/`SyncPurge` rows) — this is easy to get wrong by "fixing" what looks like a
      missing parameter; confirm the implementer did not silently attempt to add one.

### Verification Report Format
Per template: `CAS-027.review.md` alongside this spec — overall verdict, per-SC verdict table, security
gap list, scope violations.

## Implementation Log

**Completed:** 2026-09-24
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 95.6-99.6% (implementer-touched files) | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Shared/TaskManagerModal.razor` | New: non-routed Task Manager modal |
| `src/Cachet.ArchiveSync.Web/Components/Shared/TaskManagerModal.razor.css` | New: scoped styles for modal |
| `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor` | Header button + badge, summary poll loop |
| `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor.css` | Badge pill styling |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | TaskId deep-link query param + auto-populate |
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | TaskId deep-link query param + auto-populate |
| `src/Cachet.ArchiveSync.Web/appsettings.json` | Worker:TaskManagerPollIntervalSeconds setting |
| `src/Cachet.ArchiveSync.Web.Tests/TaskManager/FakeTaskManagerApiHandler.cs` | New: fake HttpMessageHandler for Task Manager endpoints |
| `src/Cachet.ArchiveSync.Web.Tests/TaskManager/TaskManagerModalTests.cs` | New: TaskManagerModal component tests |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchDeepLinkTests.cs` | New: deep-link tests |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncContentScanDeepLinkTests.cs` | New: deep-link tests |
| `src/Cachet.ArchiveSync.Web.Tests/Layout/MainLayoutTests.cs` | Extended: badge/summary tests |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `TaskManagerModalTests` | `Test_SC1_ModalListsOwnTasksWithRequiredColumns` | SC-1 |
| `KeywordSearchDeepLinkTests` | `Test_SC2_ViewResultsDeepLinksKeywordSearchAndAutoPopulates` | SC-2 |
| `ODriveSyncContentScanDeepLinkTests` | `Test_SC3_ViewResultsDeepLinksContentScanAndAutoPopulates` | SC-3 |
| `TaskManagerModalTests` | `Test_SC4_ViewResultsForSyncActionsNavigatesWithoutTaskId` | SC-4 |
| `TaskManagerModalTests` | `Test_SC5_ViewDetailsShowsResultDetailInline` | SC-5 |
| `TaskManagerModalTests` | `Test_SC6_RestartCreatesNewQueuedRowVisibleAfterRefresh` | SC-6 |
| `MainLayoutTests` | `Test_SC7_BadgeReflectsSummaryCounts` | SC-7 |
| `MainLayoutTests` | `Test_SC8_OpeningModalClearsUnviewedCountOnly` | SC-8 |
| `TaskManagerModalTests` | `Test_SC9_TaskStateVisibleAfterNewSession` | SC-9 |
| `TaskManagerModalTests` | `Test_SC10_ProcessingTimeNoteAlwaysVisible` | SC-10 |
| `TaskManagerModalTests` | `Test_V1_TaskTableHasExactlyFourRequiredColumns` | V-1 |
| `TaskManagerModalTests` | `Test_V2_RowAffordancesMatchStatus` | V-2 |
| `KeywordSearchDeepLinkTests` | `Test_V3_InvalidDeepLinkTaskIdShowsErrorBanner` | V-3 |
| `MainLayoutTests` | `Test_V4_BadgeCountMatchesSummaryResponse` | V-4 |
| `TaskManagerModalTests` | `Test_V5_OpenCallsMarkViewedExactlyOnce` | V-5 |
| `TaskManagerModalTests` | `Test_V6_RestartDisabledWhileInFlight` | V-6 |
