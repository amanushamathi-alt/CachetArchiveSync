---
id: CAS-023
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-23
priority: P1
related_specs: [CAS-007, CAS-013, CAS-022]
touches:
  database: false
  api: false # consumes CAS-007's already-merged five endpoints as-is; no Api/Core change
  ui: true
  worker: false
  b2: false # no direct B2 call — all B2 interaction already happens server-side inside CAS-007's Worker processors
  config: false
  security: true # session-gated screen; CachetFullAccess required for the three action buttons — §12 confirms
migrations: []
---

# CAS-023 — O-Drive Sync and Content Scan Screen (Removed/New/Modified Panels, Copy/Update/Purge Actions)

WBS Task 23 (Web UI Development — O-Drive Sync and Content Scan screen). Implements FR-10, FR-11, FR-12,
FR-13, FR-13a, FR-13b, FR-13c, FR-21. UI contract fixed by `CACHET_UIUX_Design_Specification_v1.md` §7
(Layout §7.1, Behavior §7.2, Empty/Error States §7.3) and §10 (cross-cutting task completion/
auto-population); API contract fixed by `CAS-007` (status `merged`).

---

## §0 Scope Correction (read this first)

**The task brief's checkbox-select-to-quarantine framing does not apply to this screen.** BRD v2.2
(confirmed 2026-09-09, Section 12.1 item 9) made Quarantine a **Keyword Search-only** workflow; O-Drive
Sync has no Quarantine integration of any kind. `CAS-007` §0 already corrected this exact misreading at
the API layer (rejecting a `POST /sync/results/quarantine` endpoint that does not exist in any
authoritative source) and its §4 explicitly defers "the O-Drive Sync screen (WBS Task 23)" to this spec.
Per this project's skill precedence (spec/BRD > local task text — `CLAUDE.md`), this spec builds what
FR-13/FR-13a/FR-13b/the UI/UX spec §7.1 actually define: **checkbox-select-to-_sync-action_**, not
checkbox-select-to-quarantine —

- **Removed from O-Drive** panel → checkbox-select → **Purge from B2** (FR-13, irreversible,
  double-confirmed).
- **New on O-Drive** panel → checkbox-select → **Copy to B2** (FR-13a).
- **Modified files on O-Drive** panel → checkbox-select → **Update in B2** (FR-13b).

The task brief's "content-match indicator" on the Removed panel is also a superseded reading — BRD FR-11
explicitly records that this comparison (and by extension every panel on this screen) is **independent
of the configured keyword search term**, so there is no content-match indicator anywhere on this screen;
the SHA-1-changed indicator belongs to the **Modified** panel only (FR-11, UI/UX spec §7.1's column
table). This spec builds exactly the UI/UX spec §7.1 column set — it does not invent a content-match
column BRD v2.2 already retired.

---

## §1 Summary

Builds `ODriveSyncContentScan.razor` — the second primary tab (per `CACHET_UIUX_Design_Specification_v1.md`
§1.1's two-tab header: Keyword Search | O-Drive Sync and Content Scan) a signed-in user reaches to
reconcile the B2 mirror against the authoritative O-Drive backup: a single **Run Content Scan** trigger
(no filter bar — the comparison scope is always the full mirror pair, FR-10), three async result panels
(New on O-Drive, Removed from O-Drive, Modified files on O-Drive — each with its own checkbox column and
sync action), and the same queued/processing-to-auto-populated task flow `CAS-022` established for
Keyword Search (FR-21). Wired to `CAS-007`'s five endpoints: `POST /sync/scan`,
`GET /sync/{taskId}/results`, and the three `CachetFullAccess`-gated sync actions
`POST /sync/actions/{copy,update,purge}`. Implements FR-10, FR-11, FR-12, FR-13, FR-13a, FR-13b, FR-13c
(indirectly — task-queue tracking already happens server-side in `CAS-007`), FR-21 (scoped per `CAS-022`
§0's precedent, restated in §0/§8.2 below).

---

## §2 User Story

As a **TraceCounts reviewer** signed into Cachet Archive Sync, I want to run a Content Scan comparing the
current O-Drive backup against the B2 mirror, see clearly which files are new, removed, or modified (with
a plain SHA-1-changed signal on the Modified panel), and then reconcile B2 to match the O-Drive backup one
action at a time — Copy new files in, Update changed files, or Purge files B2 still has that O-Drive no
longer does, with an explicit confirmation before any irreversible Purge — so that B2 stays a faithful
mirror of the authoritative O-Drive backup without me manually diffing two file trees or risking an
accidental permanent deletion.

---

## §3 In-Scope

- `ODriveSyncContentScan.razor` (`@page "/odrive-sync"`, `InteractiveServer` render mode — same
  requirement as `CAS-022` §8.2: in-place polling/update needs a live SignalR circuit):
  - **Run Content Scan** button (primary, no filter bar — UI/UX spec §7.1) (§8.1).
  - **New on O-Drive** panel: checkbox, Name/Path, Size, Modified; persistent **Copy to B2** action
    button, disabled until ≥1 row checked (§8.3, §8.4).
  - **Removed from O-Drive** panel: checkbox, Name/Path, Last Seen; persistent **Purge from B2** action
    button, disabled until ≥1 row checked, gated behind an explicit second-confirmation dialog before
    submission (FR-13, irreversible — same posture as `CAS-017`'s Delete Permanently confirmation)
    (§8.3, §8.4).
  - **Modified files on O-Drive** panel: checkbox, Name/Path, Modified, SHA1 Changed (Yes/No); persistent
    **Update in B2** action button, disabled until ≥1 row checked (§8.3, §8.4).
  - Per-row sync-action status: Pending, Synced, Failed (FR-12 — this screen's own vocabulary, distinct
    from Keyword Search's Flagged/Needs Review/Informational) (§8.3).
  - Async submission UX for both the scan and the three sync actions: toast on submit, in-progress
    banner while polling, auto-populate/row-removal on completion, failure banner on `Failed` (§8.2,
    §8.5 — FR-18, FR-21, FR-22a).
  - Empty/error/zero-result states exactly per UI/UX spec §7.3 (§8.5).
- Client-side non-empty selection validations for the three action buttons (§7).
- Polling mechanism (server-side `PeriodicTimer` inside the component, same pattern `CAS-022` §8.2
  established) against `GET /sync/{taskId}/results` while the submitted scan task is not yet complete
  (§8.2), and against the same endpoint's per-row `syncStatus` field while a submitted Copy/Update/Purge
  task is outstanding (§8.4).

---

## §4 Out-of-Scope

- **Quarantine of any kind** — does not exist for O-Drive Sync per BRD v2.2 and `CAS-007` §0/§4 (§0
  above). No checkbox-select-to-quarantine control anywhere on this screen.
- **Content-match indicator on the Removed panel** — superseded reading corrected in §0; not built.
- **Generic Task Manager API and screen, global header/tab-navigation shell, footer status bar** — same
  genuine gap `CAS-022` §0/§4 already disclosed (confirmed still true as of this spec's drafting, §17):
  `TaskManagerController.cs` hosts only `POST /tasks/{taskId}/restart`; no `GET /tasks`/`GET
/tasks/{taskId}` exists; no header/footer/tab-navigation component exists in
  `src/Cachet.ArchiveSync.Web/` yet. This screen's own polling of its own tasks via `CAS-007`'s existing
  endpoints is a scoped substitute, not a Task Manager implementation — exactly the pattern `CAS-022`
  established, now applied to O-Drive Sync's four task types (`ContentScan`/`SyncCopy`/`SyncUpdate`/
  `SyncPurge`).
- **Keyword Search screen** (FR-04–FR-09) — `CAS-022`'s scope, not this one's.
- **Pre-/Post-Action Report modals** (FR-25–FR-27) — separate, header-triggered feature, still unbuilt
  (same gap `CAS-022` §4 flagged).
- **Any `Cachet.ArchiveSync.Api`, `Core`, or `Worker` change** — `CAS-007`'s contracts are consumed
  exactly as merged; no field, status code, or route is added or altered.
- **A lightweight confirmation step for Copy/Update** — UI/UX spec §7.2 explicitly does not require one
  for these two actions (Copy is purely additive; Update's B2 content is superseded but the O-Drive
  backup remains recoverable), flagging its own text as "design decision — not separately confirmed;
  flag if you'd rather require a lightweight confirm for Update." Not re-opened here — built exactly as
  the UI/UX spec specifies (no confirmation for Copy/Update, second-confirmation only for Purge); see
  §15 if the client wants to revisit.
- **Drive encryption-at-rest / O-Drive refresh cadence / web caching layer mechanism** (BRD §12.4, still
  open) — not depended on; this screen always calls the live API, never assumes a caching layer exists
  (same deferral `CAS-022` §4 already made for the identical open item).

---

## §5 Database Objects

N/A — `touches.database: false`. No schema change. Consumes `dbo.sync_result`/`dbo.file_index` only
indirectly, through `CAS-007`'s already-built endpoints.

---

## §6 API Endpoints

No `Cachet.ArchiveSync.Api` endpoint is added or changed (`touches.api: false`). This screen is a pure
consumer of `CAS-007`'s five already-merged contracts, exactly as specified — restated here only to fix
the build target, not to redefine it:

| Endpoint                                                                                   | Source spec  | Used for                                                                                                                       |
| ------------------------------------------------------------------------------------------ | ------------ | ------------------------------------------------------------------------------------------------------------------------------ |
| `POST /sync/scan`                                                                          | `CAS-007` §6 | Run Content Scan submit (§8.1, §8.2)                                                                                           |
| `GET /sync/{taskId}/results?newOnODrivePage=&removedFromODrivePage=&modifiedOnODrivePage=` | `CAS-007` §6 | Poll for scan completion; populate the three panels; poll per-row `syncStatus` while a sync action is outstanding (§8.2, §8.4) |
| `POST /sync/actions/copy`                                                                  | `CAS-007` §6 | Copy to B2 from the New on O-Drive panel (§8.4)                                                                                |
| `POST /sync/actions/update`                                                                | `CAS-007` §6 | Update in B2 from the Modified files panel (§8.4)                                                                              |
| `POST /sync/actions/purge`                                                                 | `CAS-007` §6 | Purge from B2 from the Removed from O-Drive panel, only after the second-confirmation dialog (§8.4)                            |

All calls go through the existing `"CachetApi"` named `HttpClient` (`CAS-002` §6.6,
`Cachet.ArchiveSync.Web/Program.cs:133`), which already forwards the circuit's session cookie — no new DI
registration or client is needed.

---

## §7 Validations

Client-side gating so the user gets immediate feedback before a round trip — the server (`CAS-007` §7
V3–V8) remains the source of truth in every case; a client-side pass never substitutes for the server's
own rejection.

| ID  | Rule                                                                                                                                                                                                                                                                                                                                                                                                    | On Failure                                                                  | Test                                                     |
| --- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------- | -------------------------------------------------------- |
| V1  | Run Content Scan is disabled while a scan submitted by this component is still in flight (one active scan task at a time, mirrors `CAS-022` V1's "no second submission queued on top of an unresolved one" design)                                                                                                                                                                                      | Button remains disabled                                                     | `Test_V1_RunContentScanDisabledWhileScanInFlight`        |
| V2  | Copy to B2 / Update in B2 / Purge from B2 are each disabled until ≥1 row in their own panel is checked (UI/UX spec §7.1 "disabled-until-selected" pattern)                                                                                                                                                                                                                                              | Button remains disabled; no request sent                                    | `Test_V2_ActionButtonsDisabledUntilRowChecked`           |
| V3  | Purge from B2 never calls `POST /sync/actions/purge` without the user completing the second-confirmation dialog, regardless of how many rows are selected                                                                                                                                                                                                                                               | No request sent until confirmed                                             | `Test_V3_PurgeNeverSubmittedWithoutExplicitConfirmation` |
| V4  | A server-side `403`/`404`/`409`/`422` from any of the five §6 endpoints is surfaced as a screen-level error state, never an unhandled exception/blank screen                                                                                                                                                                                                                                            | Inline error banner per §8.5                                                | `Test_V4_ServerErrorResponsesRenderInlineErrorNotCrash`  |
| V5  | Copy to B2 / Update in B2 / Purge from B2 buttons are disabled (never hidden) for a session lacking `CachetFullAccess` (mirrors `CAS-007` V8/SC-11's server-side `403`, surfaced here as a UI affordance rather than a trap the user falls into after clicking; **resolved 2026-09-23, R. Devarajan — disabled-with-tooltip over hidden, to keep a consistent panel layout across session types, §15**) | Buttons rendered disabled with a tooltip/hint (e.g. "Requires Full Access") | `Test_V5_ActionButtonsUnavailableWithoutFullAccess`      |

---

## §8 UI / UX

### §8.1 Layout — Exactly `CACHET_UIUX_Design_Specification_v1.md` §7.1

- Single primary control: **Run Content Scan** button. No filter bar (unlike Keyword Search) — the
  comparison scope is always the full O-Drive-mirror/B2-mirror pair (FR-10).
- Three result panels, each with its own checkbox column and persistent action button:

| Panel                         | Columns                                              | Action button     | Calls                                                  |
| ----------------------------- | ---------------------------------------------------- | ----------------- | ------------------------------------------------------ |
| **New on O-Drive**            | checkbox, Name/Path, Size, Modified                  | **Copy to B2**    | `POST /sync/actions/copy`                              |
| **Removed from O-Drive**      | checkbox, Name/Path, Last Seen                       | **Purge from B2** | `POST /sync/actions/purge` (after second confirmation) |
| **Modified files on O-Drive** | checkbox, Name/Path, Modified, SHA1 Changed (Yes/No) | **Update in B2**  | `POST /sync/actions/update`                            |

Row data maps directly from `CAS-007`'s `newOnODrive.items[]` / `removedFromODrive.items[]` /
`modifiedOnODrive.items[]` (`GET /sync/{taskId}/results`, §6) — `size`/`modifiedAt` for New, `lastSeen`
for Removed, `modifiedAt`/`sha1Changed` for Modified — no client-side reshaping beyond binding. No
embedded Quarantine panel (§0, UI/UX spec §7.1's explicit note that Quarantine remains Keyword
Search-only).

### §8.2 Content Scan Submission, Polling, and Auto-Population (FR-10, FR-18, FR-21, FR-22a)

**Render mode:** `InteractiveServer` — same rationale as `CAS-022` §8.2: this screen requires a live
SignalR circuit to poll and push updates without a page reload.

1. **Run Content Scan clicked:** `POST /sync/scan` (`CAS-007` §6, no request body) with `V1` guarding
   against a duplicate in-flight submission. On `202`, store `taskId`; show a toast: _"Content Scan
   submitted — this may take anywhere from a few minutes to a full day depending on data volume."_
   (UI/UX spec §7.2 point 1 mirrors §3.3's Keyword Search copy; the "Check Task Manager for progress"
   clause is dropped for the same reason `CAS-022` §8.2 dropped it — no Task Manager screen exists yet,
   §0/§4).
2. **In-progress banner:** replaces the Run Content Scan button's row with: _"Content Scan in progress —
   submitted `<relative time>` ago. Results will appear here automatically when ready."_ (same pattern as
   `CAS-022` §8.2 step 2, Task Manager link dropped for the same reason).
3. **Polling:** the component starts a server-side `PeriodicTimer` (5-second interval — **(design
   decision, not BRD-specified, same reasoning and value `CAS-022` §8.2 already established for this
   project's scale — kept identical across both async screens rather than independently re-derived)**)
   calling `GET /sync/{taskId}/results`. A `409 task-not-complete` response means keep polling; a `200`
   response (`Completed` or `Failed` shape, `CAS-007` §6) stops the timer and moves to step 4/5.
4. **Scan completes (`200`, `Completed`):** replace the in-progress banner with the three populated
   panels (`newOnODrive`/`removedFromODrive`/`modifiedOnODrive`, `CAS-007` §6's response shape), calling
   `StateHasChanged()` on the circuit — the FR-21 "auto-populate in place, no manual refresh" behavior,
   same mechanism `CAS-022` §8.2 step 4 used.
5. **Scan fails (`200`, `Failed`):** replace the in-progress banner with the failure banner (§8.5).
6. **User navigates away and back within the same session (same circuit):** same known, documented
   limitation `CAS-022` §8.2 step 6 / §15 already accepted — the component's in-memory `taskId`/poll-loop
   does not survive a full circuit reconnect; not replicated as a fix here, consistent with that
   precedent.

### §8.3 Results Panels — Exactly `CACHET_UIUX_Design_Specification_v1.md` §7.1

Each panel carries a count badge (per-panel result count) and a status column reflecting the panel's own
row-level sync state — Pending, Synced, Failed (FR-12) — distinct from Keyword Search's Flagged/Needs
Review/Informational vocabulary, since these rows are judged against B2 agreement, not a search term.
A row's status starts `Pending` (server default, `CAS-007` §5) and updates once its Copy/Update/Purge
task completes (§8.4).

### §8.4 Checkbox-Select-to-Sync-Action (FR-13, FR-13a, FR-13b) and Per-Row Status

Each panel's rows have a checkbox bound to that panel's own per-row selection set (selections are not
shared across panels — a New-on-O-Drive row can never be submitted to Purge, and the UI enforces this by
construction: each panel's action button only ever POSTs that panel's own row `fileId`s to its own
endpoint).

- **Copy to B2 (`<n>`)** — disabled until ≥1 row selected in New on O-Drive (V2). On click:
  `POST /sync/actions/copy` (`CAS-007` §6) with `{ taskId, fileIds }` — the selected rows' `fileId`s
  (`sync_result_id` values, `CAS-007` §1.7/§6 semantics; this screen never re-derives or relabels that
  identity). On `202`: clear the selection, show a toast confirming the count submitted, mark the
  submitted rows `Pending` (already their default) and begin polling `GET /sync/{taskId}/results` for
  this action's own `taskId` (a second, independent poll loop from the scan's own — §8.2's mechanism,
  reused per action).
- **Update in B2 (`<n>`)** — same pattern, scoped to Modified files on O-Drive, calling
  `POST /sync/actions/update`.
- **Purge from B2 (`<n>`)** — disabled until ≥1 row selected in Removed from O-Drive (V2). On click:
  opens the second-confirmation dialog (V3) with the exact copy UI/UX spec §7.2 specifies: _"This will
  permanently delete `<N>` file(s) from Backblaze B2. This action cannot be undone."_ Only on confirming
  does the component call `POST /sync/actions/purge` with `{ taskId, fileIds, confirm: true }`
  (`CAS-007` §6's required `confirm` field) — canceling the dialog submits nothing and leaves the
  selection intact (same posture `CAS-017`'s Delete Permanently confirmation already established for this
  project's other irreversible action).
- **On sync-action completion (per row, via the action's own poll loop):** per UI/UX spec §7.2's terminal
  behavior — a row whose action **succeeded** (`syncStatus: "Synced"`) drops off its panel entirely (the
  disagreement is resolved, so it no longer belongs in New/Removed/Modified); a row whose action
  **failed** (`syncStatus: "Failed"`) stays visible with a "Failed" status chip. No link to Task Manager
  detail is shown (§0/§4 — no Task Manager screen exists yet, same scoped deviation `CAS-022` §8.5 made
  for its own failure banner).
- **Concurrent sync actions across panels:** a user may have Copy, Update, and Purge each in flight
  simultaneously (three independent panels, three independent action task IDs) — the component tracks
  up to three concurrent action poll loops plus the one scan poll loop; this is a component-level
  concurrency detail, not a UI affordance the user directly sees beyond each panel's own in-progress
  status.

### §8.5 Empty / Error States — Exactly `CACHET_UIUX_Design_Specification_v1.md` §7.3

| State                                                             | Copy/behavior                                                                                                                                                                                                                                                      |
| ----------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| No scan yet run this session                                      | Panels show "No comparison run yet — run a Content Scan to get started." (per UI/UX spec §7.3's "no comparison run yet" state, applied per panel or once above all three — implementer's choice, not distinguished by the UI/UX spec).                             |
| Scan completed, a panel has zero results                          | Per-panel: "No files removed/added/modified." (UI/UX spec §7.3's "no files removed/added/modified on a completed scan" state, worded per panel). Not an error.                                                                                                     |
| Scan task failed                                                  | Inline banner: _"This Content Scan did not complete successfully."_ (Task Manager reference dropped, §0/§4 — same scoped deviation as `CAS-022` §8.5).                                                                                                             |
| A Copy/Update/Purge action task fails for one or more rows        | Those rows show a "Failed" status chip in place (§8.4); no separate screen-level banner — the failure is scoped to the affected rows, consistent with `CAS-007` §3's per-file-independent-outcome design (one file failing does not imply the whole batch failed). |
| Any §6 endpoint returns `403`/`404`/`409`/`422` unexpectedly (V4) | Non-field-specific banner: _"Something went wrong. Please try again."_ — logged server-side (§11) with the endpoint/status code, never shown raw to the user.                                                                                                      |

---

## §9 Configuration Changes

No new configuration key. Reuses `Api:BaseUrl` (`CAS-002` §6.6) via the existing `"CachetApi"` named
`HttpClient`. The 5-second poll interval (§8.2) is a code constant, not a configuration key — same
reasoning `CAS-022` §9 already gave for this project's small (max 4 user) scale, kept as the identical
constant rather than a second, independently-tunable one.

---

## §10 Success Criteria

| ID    | Criterion                                                                                                                                                                                                                      | Test                                                                 |
| ----- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------- |
| SC-1  | Submitting a Content Scan shows the in-progress banner immediately and disables the Run Content Scan button while the task is outstanding                                                                                      | `Test_SC1_ScanSubmissionShowsInProgressBannerAndDisablesButton`      |
| SC-2  | While `GET /sync/{taskId}/results` returns `409`, the screen continues polling and shows no panel content beyond the in-progress banner                                                                                        | `Test_SC2_PollingContinuesWhileScanTaskIncomplete`                   |
| SC-3  | Once `GET /sync/{taskId}/results` returns `200` `Completed`, all three panels (New/Removed/Modified) populate in place, without a page navigation/reload, and the in-progress banner is removed                                | `Test_SC3_CompletedScanAutoPopulatesAllThreePanelsInPlace`           |
| SC-4  | Once the scan task returns `200` `Failed`, the failure banner replaces the in-progress banner and no stale/partial panel content is shown                                                                                      | `Test_SC4_FailedScanShowsFailureBanner`                              |
| SC-5  | Each of Copy to B2 / Update in B2 / Purge from B2 is disabled with 0 rows checked in its own panel, and shows the correct count once ≥1 row is checked in that panel only (not affected by selections in the other two panels) | `Test_SC5_ActionButtonsReflectOwnPanelSelectionCountIndependently`   |
| SC-6  | Clicking Copy to B2 with a valid selection calls `POST /sync/actions/copy` with exactly the selected New-on-O-Drive `fileId`s and `taskId`, and begins polling that action's own `taskId`                                      | `Test_SC6_CopyToB2CallsApiWithCorrectFileIdsAndPolls`                |
| SC-7  | Clicking Update in B2 with a valid selection calls `POST /sync/actions/update` with exactly the selected Modified-files `fileId`s                                                                                              | `Test_SC7_UpdateInB2CallsApiWithCorrectFileIds`                      |
| SC-8  | Clicking Purge from B2 never calls `POST /sync/actions/purge` before the second-confirmation dialog is explicitly confirmed; canceling the dialog sends no request and preserves the selection                                 | `Test_SC8_PurgeRequiresExplicitConfirmationBeforeApiCall`            |
| SC-9  | Confirming the Purge dialog calls `POST /sync/actions/purge` with `confirm: true` and exactly the selected Removed-from-O-Drive `fileId`s                                                                                      | `Test_SC9_ConfirmedPurgeCallsApiWithConfirmTrueAndCorrectFileIds`    |
| SC-10 | A row whose sync action completes with `syncStatus: "Synced"` is removed from its panel; a row whose action completes with `syncStatus: "Failed"` remains visible with a Failed status chip                                    | `Test_SC10_SyncedRowDropsFromPanelFailedRowShowsStatusChip`          |
| SC-11 | A session without `CachetFullAccess` cannot trigger Copy/Update/Purge from the UI (buttons rendered disabled with a tooltip per V5) while Run Content Scan and viewing results remain available                                | `Test_SC11_ReadOnlySessionCannotTriggerSyncActionsButCanScanAndView` |
| SC-12 | Zero-result completion on any panel renders the "No files removed/added/modified" copy for that panel, not an empty table with no explanation                                                                                  | `Test_SC12_ZeroResultPanelShowsExplicitEmptyStateCopy`               |
| SC-13 | No B2 application key, session cookie value, or raw file content ever appears in `ODriveSyncContentScan.razor`'s rendered markup or in Web's log output                                                                        | `Test_SC13_NoCredentialOrContentLeakageInUiSurfaceOrLogs`            |

---

## §11 Telemetry

```csharp
// _logger.LogInformation("ODriveSyncContentScan screen: scan submitted, TaskId={TaskId}", taskId);
// _logger.LogInformation("ODriveSyncContentScan screen: scan task {TaskId} completed after {PollCount} polls", taskId, pollCount);
// _logger.LogInformation("ODriveSyncContentScan screen: {ActionType} submitted, TaskId={TaskId}, FileCount={FileCount}", actionType, taskId, fileCount);
// _logger.LogWarning("ODriveSyncContentScan screen: unexpected {StatusCode} from {Endpoint}", statusCode, endpoint);
```

- Never log: the B2 application key, the session/cookie value, full file content, or raw local O-Drive/B2
  filesystem paths beyond what the audit log's own `file_path` column already captures (same restriction
  `CAS-022` §11 and `CAS-007` §11 already apply).
- Poll-loop logging (scan and each action) is count/status only, never per-poll response bodies.

---

## §12 Security Notes

### B2 Credential Handling

This screen never touches the B2 application key at any point. It consumes only the already-sanitized
response shapes `CAS-007` §6 defines (`CAS-007` §12 already confirms none of those DTOs carry a key or
key ID). No new B2 client, credential, or capability check is introduced here.

### Audit Trail

No change — `CAS-007` §12 already writes `ContentScanSubmitted`/`SyncCopySubmitted`/
`SyncUpdateSubmitted`/`SyncPurgeSubmitted` and each action's per-file completion audit rows on the Api
side, before each endpoint's own response returns or the task completes (FR-13d). This spec adds no new
audited action; it is a pure UI/transport layer in front of already-audited backend calls.

### Session / Auth Requirements

The entire screen requires an authenticated session (`CachetAuthenticated`, same global gate `CAS-021`
establishes). The three sync-action buttons additionally require `CachetFullAccess` — `CAS-007` §6/§12
already enforces this server-side (`403 insufficient-capability` on a `Read Only` session); V5/SC-11 make
this a UI-level affordance (disabled buttons with a tooltip, §15) rather than a trap the user only discovers after
submitting. **Same sequencing dependency `CAS-022` §12 recorded:** this spec's page carries no
`[Authorize]`-equivalent gate of its own beyond whatever `CAS-021`'s `FallbackPolicy` establishes
app-wide; deploy after `CAS-021` (§14).

---

## §13 Test Plan

### Unit Tests

| Class                                              | Method                                                                                                                                                                                                                                                                                                                                                     | Proves                 |
| -------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------- |
| `ODriveSyncPageTests` (bUnit)                      | `Test_V1_RunContentScanDisabledWhileScanInFlight`, `Test_V2_ActionButtonsDisabledUntilRowChecked`, `Test_V3_PurgeNeverSubmittedWithoutExplicitConfirmation`, `Test_V5_ActionButtonsUnavailableWithoutFullAccess`, `Test_SC5_ActionButtonsReflectOwnPanelSelectionCountIndependently`, `Test_SC11_ReadOnlySessionCannotTriggerSyncActionsButCanScanAndView` | V1–V3, V5, SC-5, SC-11 |
| `ODriveSyncPageTests` (bUnit, mocked `HttpClient`) | `Test_SC1_ScanSubmissionShowsInProgressBannerAndDisablesButton`, `Test_SC2_PollingContinuesWhileScanTaskIncomplete`, `Test_SC3_CompletedScanAutoPopulatesAllThreePanelsInPlace`, `Test_SC4_FailedScanShowsFailureBanner`, `Test_SC12_ZeroResultPanelShowsExplicitEmptyStateCopy`                                                                           | SC-1–SC-4, SC-12       |
| `ODriveSyncPageTests` (bUnit, mocked `HttpClient`) | `Test_SC6_CopyToB2CallsApiWithCorrectFileIdsAndPolls`, `Test_SC7_UpdateInB2CallsApiWithCorrectFileIds`, `Test_SC8_PurgeRequiresExplicitConfirmationBeforeApiCall`, `Test_SC9_ConfirmedPurgeCallsApiWithConfirmTrueAndCorrectFileIds`, `Test_SC10_SyncedRowDropsFromPanelFailedRowShowsStatusChip`                                                          | SC-6–SC-10             |
| `ODriveSyncPageTests` (bUnit)                      | `Test_V4_ServerErrorResponsesRenderInlineErrorNotCrash`                                                                                                                                                                                                                                                                                                    | V4                     |

### Integration Tests

| Class                                                                                                                                                                        | Method                                                                                                                                                            | Proves                               |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------ |
| `ODriveSyncFlowIntegrationTests` (`WebApplicationFactory`, Web, against a stubbed/in-memory Api double or the real `CAS-007` controller with a test double repository layer) | `Test_SC3_CompletedScanAutoPopulatesAllThreePanelsInPlace` (full round trip), `Test_SC9_ConfirmedPurgeCallsApiWithConfirmTrueAndCorrectFileIds` (full round trip) | SC-3, SC-9 (end-to-end confirmation) |
| Log-output assertion (test log sink)                                                                                                                                         | `Test_SC13_NoCredentialOrContentLeakageInUiSurfaceOrLogs`                                                                                                         | SC-13                                |

### Coverage Target

Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. No database migration (§5).
2. Deploy `Cachet.ArchiveSync.Web` only — no `Api`/`Worker`/`Core` change (§6, §4). `CAS-007`'s Api
   endpoints and Worker processors are already merged/live.
3. **Deploy after `CAS-021` merges** (§12) — same sequencing dependency `CAS-022` §14 already established
   for the identical reason: this screen depends on `CAS-021`'s global authorization gate to be
   unreachable without a session.
4. Rollback: revert the new `ODriveSyncContentScan.razor` route/component; no schema or Api change to
   unwind. **Note (same risk class as `CAS-007` §14):** a rollback after any Purge task has already run
   through this screen does not and cannot restore the purged B2 objects — this is `CAS-007`'s own
   accepted, deliberate risk posture (BRD §9), unaffected by this spec being UI-only.

---

## §15 Open Questions

All four items below were raised while drafting this spec and resolved directly with R. Devarajan,
TraceCounts (Solution Owner), 2026-09-23, via this session — logged below for traceability (Karpathy
P6), matching `CAS-001`/`CAS-002`/`CAS-021`/`CAS-022`'s own resolution-log convention. No blocking open
item remains against this spec.

- [x] **Lightweight confirmation for Update in B2 — resolved 2026-09-23 (R. Devarajan, TraceCounts): no
      confirmation dialog, proceed as `CACHET_UIUX_Design_Specification_v1.md` §7.2 specifies.** §7.2
      itself only flagged this as a design decision open to revisiting, since Update does overwrite
      existing B2 content; the client confirmed the original reasoning holds — B2's own versioning means
      the prior object version remains recoverable via the O-Drive backup re-running Update, unlike
      Purge's true delete, so no confirm step is added. §4/§8.4 already reflected this; no spec change
      needed beyond closing this item.
- [x] **Generic Task Manager API/screen and global header — resolved 2026-09-23 (R. Devarajan,
      TraceCounts): defer, same decision as `CAS-022` §15.** This spec's self-polling substitute (§0,
      §8.2, §8.4) ships as the real implementation for O-Drive Sync's four task types, not a stopgap. A
      dedicated Task Manager spec (WBS Task 25) remains a separate, later spec — now with two consumer
      screens (`CAS-022`, this spec) to justify its shared infrastructure once drafted.
- [x] **Cross-session/circuit-loss recovery — resolved 2026-09-23 (R. Devarajan, TraceCounts): accept as
      a known, documented limitation, same decision as `CAS-022` §15.** No client-side persistence of any
      in-flight `taskId` (scan or the three actions) is added; results remain queryable via `CAS-007`'s
      endpoints by anyone once complete, but this screen does not auto-reattach after a circuit drop
      until a future Task Manager screen exists.
- [x] **`CachetFullAccess` UI gating for Copy/Update/Purge (V5) — resolved 2026-09-23 (R. Devarajan,
      TraceCounts): disabled with a tooltip, not hidden.** All three action buttons render visibly but
      disabled for a read-only (non-`CachetFullAccess`) session, with a hint (e.g. "Requires Full
      Access") explaining why — keeps a consistent panel layout across session types rather than shifting
      content when buttons are omitted. §7 V5 and §8 updated accordingly.
- Not blocked by BRD §12.4 (web caching layer mechanism, O-Drive refresh cadence) — this screen always
  calls the live API and makes no caching-layer assumption; confirmed not load-bearing here, same
  deferral `CAS-022` §15 already made for the identical open item.
- Not blocked by BRD §12.2/§12.1 items — login method is resolved (`CAS-002`); this spec depends only on
  `CAS-021`'s deployment sequencing (§14), not on any still-open decision.

---

## §16 Sign-off

| Role     | Name                                        | Date       | Notes                   |
| -------- | ------------------------------------------- | ---------- | ----------------------- |
| Author   | R. Devarajan (TraceCounts), via Claude Code | 2026-09-23 | Drafted per WBS Task 23 |
| Reviewer |                                             |            | Pending                 |

---

## §17 Graph Impact

Graph exists for this repo (`graphify-out/`, built 2026-09-10, refreshed since) and indexes the
BRD/WBS/API-spec/decision-log documentation corpus — it has no `src/` application-code nodes for O-Drive
Sync yet (`CAS-007`'s own §17 already confirmed this; unchanged as of this spec's drafting). Queried
live:

```
python -m graphify query "O-Drive Sync Content Scan screen UI Removed New Modified panels Run Content Scan FR-10 FR-11 FR-13 FR-13a FR-13b FR-21" --budget 1500
```

Result: a 6-node, depth-2 BFS anchored on `Task 23: O-Drive Sync and Content Scan Screen` (WBS), the
CAS-007 migration files, and `Task 17: O-Drive Sync API + Copy/Purge/Update Endpoints` — confirming
`Task 23` is this spec's WBS anchor and `Task 17`/`CAS-007` is its sole upstream dependency in the graph,
with no other spec or module surfaced as load-bearing.

Supplemented by a manual read of `src/Cachet.ArchiveSync.Web/` (confirming no O-Drive Sync `.razor`, no
global header, and a still-bare `MainLayout.razor` — the same state `CAS-022` §17 found), `src/
Cachet.ArchiveSync.Api/Controllers/TaskManagerController.cs` (confirming the same Task Manager API gap
`CAS-022` §0 flagged, unchanged), and `CAS-007` (full read, §6/§7/§10/§12 — the exact route/response/
validation/audit shapes this spec consumes) and `CAS-022` (full read, as the direct structural precedent
for an async, polling, checkbox-select Web screen in this codebase).

### God Nodes in Blast Radius

None of the graph's top-10 numeric god nodes (all `graphify` tool internals) are BRD/spec nodes this
feature touches. Within the documentation graph, `Task 17: O-Drive Sync API + Copy/Purge/Update
Endpoints` is the highest-degree node this spec's blast radius passes through — confirming this spec is
purely a UI consumer of that already-implemented surface, with no independent API-layer node of its own.

### Communities / Modules Affected

- **O-Drive Sync community** (`CAS-007`'s five endpoints, `FR-10`–`FR-13d`, `WBS Task 17`, `WBS Task
23`) — this screen is the first (and, per `CAS-007` §4, only planned) UI consumer of every node in this
  community; confirms `CAS-007`'s five endpoints are exactly what this spec should build against, with no
  drift.
- **Task Manager community** — touched only via this screen's own scoped polling substitute for its four
  task types (`ContentScan`/`SyncCopy`/`SyncUpdate`/`SyncPurge`), same pattern `CAS-022` established for
  Keyword Search's task type — no generic Task Manager code is built here.
- **Auth/Session community** — `CachetFullAccess`/`CachetAuthenticated` policy nodes (`CAS-002` §6.4),
  directly connected to this screen's V5/SC-11 UI-level capability gating, mirroring `CAS-007`'s own
  server-side enforcement.
- **UI/UX design corpus** — `CACHET_UIUX_Design_Specification_v1.md` §7 (this screen's own section) and
  §10 (cross-cutting task completion, shared with `CAS-022`'s §3.3/§10 usage).
- Projects touched: `Web` only (new `.razor` component + supporting client-side code). **Not** `Api`,
  `Core`, or `Worker`.

### Surprising Connections

- The graph's live query surfaces `Task 23`'s own node linked, via the WBS effort re-estimate note
  (Section 14), directly to `Task 17` rather than to any UI/UX-document node — confirming the WBS itself
  treats this screen as a pure downstream consumer of the API task, consistent with this spec's §6
  "no Api/Core/Worker change" scope and giving no reason to expect hidden coupling beyond `CAS-007`.
- **Confirmed by manual code read, not by the graph (graph has no `src/` nodes for this module):** no
  `ODriveSyncContentScan.razor` or equivalent exists yet anywhere in `src/Cachet.ArchiveSync.Web/` — this
  spec is a clean build against `CAS-007`'s contract, not a modification of partial existing work.
- **Decomposition check (P9) result:** module fan-out is 1 (O-Drive Sync only — no embedded
  Quarantine/other-module sliver, unlike `CAS-022`'s embedded read-only Quarantine panel). Size
  thresholds: 5 endpoints _consumed_ (not owned), 5 validations, 13 SCs (one over `CAS-022`'s 10, driven
  by three independent sync actions each needing their own SC pair rather than one shared action — still
  well under any hard project ceiling and structurally comparable to `CAS-022`'s own UI-only precedent),
  0 DB objects, estimated changed files well under 15. **Decision: single spec, not split** — same
  conclusion `CAS-022` §17 reached for its own comparable UI-only scope.

### Graphify Queries Run

```
python -m graphify query "O-Drive Sync Content Scan screen UI Removed New Modified panels Run Content Scan FR-10 FR-11 FR-13 FR-13a FR-13b FR-21" --budget 1500
```

(God Nodes/Communities/Surprising-Connections sections above also draw on `graphify-out/GRAPH_REPORT.md`
and the manual code/spec read noted above.)

---

## §18 Verification Plan

### Reviewer Checklist

The reviewer will independently verify each of the following. Mark N/A only if the section itself is
N/A.

- [ ] All §10 Success Criteria (SC-1–SC-13) have a passing test.
- [ ] All §7 Validations (V1–V5) are implemented and tested.
- [ ] `ODriveSyncContentScan.razor` matches `CACHET_UIUX_Design_Specification_v1.md` §7.1–§7.3 exactly
      for the controls/panels/columns this spec builds (single Run Content Scan trigger, three panels
      with their exact column sets, three action buttons), with no content-match indicator or Quarantine
      checkbox anywhere on this screen (§0).
- [ ] No `POST`/checkbox affordance targets a Quarantine endpoint anywhere on this screen (§0/§4 — confirm
      the scope correction was applied, not the task brief's original quarantine framing).
- [ ] Each of the three action buttons submits only its own panel's row `fileId`s to its own endpoint
      (`copy`→New, `update`→Modified, `purge`→Removed) — never a cross-panel submission (§8.4).
- [ ] Purge from B2 is never submitted without the user explicitly confirming the second-confirmation
      dialog first (V3, SC-8, SC-9) — cancel leaves the selection intact and sends no request.
- [ ] Content Scan submission never blocks synchronously on the scan itself completing — `POST
    /sync/scan` returns immediately and polling/auto-population is the only mechanism that reveals
      results (FR-18, SC-1–SC-3).
- [ ] A row whose sync action succeeds drops off its panel; a row whose action fails shows a Failed
      status chip and remains visible (SC-10).
- [ ] No `Cachet.ArchiveSync.Api`, `Cachet.ArchiveSync.Core`, or `Cachet.ArchiveSync.Worker` file was
      modified (§4, §6 — this spec is Web-only).
- [ ] No generic Task Manager endpoint, Task Manager screen, or global header component was added
      (§0/§4 — confirm the self-polling substitute was used instead).
- [ ] §12 B2/session: no B2 key, key ID, or session cookie value appears in rendered markup or logs
      (SC-13); Copy/Update/Purge buttons are unavailable to a session without `CachetFullAccess` (V5,
      SC-11).
- [ ] §17 Graph Impact matches actual code changes — no unintended nodes (no Api/Core/Worker files, no
      Keyword Search or Quarantine-modal code).

### Verification Report Format

Per template: `CAS-023.review.md` with overall verdict, per-SC verdict table, security gap list, scope
violations.

## Implementation Log

**Completed:** 2026-09-23
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 98.1% | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | New Blazor Server screen (@page "/odrive-sync", InteractiveServer): Run Content Scan trigger, New/Removed/Modified panels with Copy/Purge/Update actions, scan+action poll loops, Purge second-confirmation dialog, CachetFullAccess UI gating. |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `ODriveSyncPageTests` | `Test_SC1_ScanSubmissionShowsInProgressBannerAndDisablesButton` | SC-1 |
| `ODriveSyncPageTests` | `Test_SC2_PollingContinuesWhileScanTaskIncomplete` | SC-2 |
| `ODriveSyncPageTests` | `Test_SC3_CompletedScanAutoPopulatesAllThreePanelsInPlace` | SC-3 |
| `ODriveSyncPageTests` | `Test_SC4_FailedScanShowsFailureBanner` | SC-4 |
| `ODriveSyncPageTests` | `Test_SC5_ActionButtonsReflectOwnPanelSelectionCountIndependently` | SC-5 |
| `ODriveSyncPageTests` | `Test_SC6_CopyToB2CallsApiWithCorrectFileIdsAndPolls` | SC-6 |
| `ODriveSyncPageTests` | `Test_SC7_UpdateInB2CallsApiWithCorrectFileIds` | SC-7 |
| `ODriveSyncPageTests` | `Test_SC8_PurgeRequiresExplicitConfirmationBeforeApiCall` | SC-8 |
| `ODriveSyncPageTests` | `Test_SC9_ConfirmedPurgeCallsApiWithConfirmTrueAndCorrectFileIds` | SC-9 |
| `ODriveSyncPageTests` | `Test_SC10_SyncedRowDropsFromPanelFailedRowShowsStatusChip` | SC-10 |
| `ODriveSyncPageTests` | `Test_SC11_ReadOnlySessionCannotTriggerSyncActionsButCanScanAndView` | SC-11 |
| `ODriveSyncPageTests` | `Test_SC12_ZeroResultPanelShowsExplicitEmptyStateCopy` | SC-12 |
| `ODriveSyncPageTests` | `Test_SC13_NoCredentialOrContentLeakageInUiSurfaceOrLogs` | SC-13 |
| `ODriveSyncPageTests` | `Test_V1_RunContentScanDisabledWhileScanInFlight` | V-1 |
| `ODriveSyncPageTests` | `Test_V2_ActionButtonsDisabledUntilRowChecked` | V-2 |
| `ODriveSyncPageTests` | `Test_V3_PurgeNeverSubmittedWithoutExplicitConfirmation` | V-3 |
| `ODriveSyncPageTests` | `Test_V4_ServerErrorResponsesRenderInlineErrorNotCrash` | V-4 |
| `ODriveSyncPageTests` | `Test_V5_ActionButtonsUnavailableWithoutFullAccess` | V-5 |
| `ODriveSyncPageTests` | `Test_RunScanSecondClickWhileInFlightIsNoOp` | coverage gap: HandleRunScanAsync in-flight guard |
| `ODriveSyncPageTests` | `Test_ScanSubmitNullResponseBodyShowsGenericErrorAndDoesNotEnterInFlight` | coverage gap: null scan submit response |
| `ODriveSyncPageTests` | `Test_ScanPollNonSuccessNonConflictShowsErrorAndStopsPolling` | coverage gap: scan poll non-success status |
| `ODriveSyncPageTests` | `Test_ScanPollNullResponseBodyContinuesPolling` | coverage gap: scan poll null body |
| `ODriveSyncPageTests` | `Test_DisposeAfterFirstPollTickCancelsScanLoopWithoutThrowing` | coverage gap: OperationCanceledException on dispose |
| `ODriveSyncPageTests` | `Test_ToggleSelectionUncheckRemovesFileIdFromSelection` | coverage gap: ToggleSelection uncheck branch |
| `ODriveSyncPageTests` | `Test_OpenPurgeConfirmNoOpWhenNoRowsSelected` | coverage gap: OpenPurgeConfirm guard (no selection) |
| `ODriveSyncPageTests` | `Test_OpenPurgeConfirmNoOpWithoutFullAccessEvenWhenRowsSelected` | coverage gap: OpenPurgeConfirm guard (no FullAccess) |
| `ODriveSyncPageTests` | `Test_ActionSubmitApiUnreachableShowsGenericErrorNotCrash` | coverage gap: action submit network exception |
| `ODriveSyncPageTests` | `Test_ActionSubmitNullResponseBodyShowsGenericErrorAndDoesNotEnterInFlight` | coverage gap: action submit null body |
| `ODriveSyncPageTests` | `Test_ActionPollTransientNetworkErrorIsSwallowedAndPollingContinues` | coverage gap: action poll network exception |
| `ODriveSyncPageTests` | `Test_ActionPollConflictContinuesPolling` | coverage gap: action poll 409 |
| `ODriveSyncPageTests` | `Test_ActionPollNonSuccessNonConflictShowsErrorAndClearsInFlight` | coverage gap: action poll non-success status |
| `ODriveSyncPageTests` | `Test_ActionPollNullOrIncompleteResultContinuesPolling` | coverage gap: action poll null/incomplete result |
| `ODriveSyncPageTests` | `Test_UpdateActionPollAppliesProgressToModifiedItemsOnCompletion` | coverage gap: SyncUpdate switch case |
| `ODriveSyncPageTests` | `Test_PurgeActionPollAppliesProgressToRemovedItemsOnCompletion` | coverage gap: SyncPurge switch case |
| `ODriveSyncPageTests` | `Test_ActionPollContinuesLoopingWhileSomeFileIdsRemainUnmatched` | coverage gap: ApplyProgress unmatched fileId + loop continuation |
| `ODriveSyncPageTests` | `Test_ActionPollContinuesWhileMatchedItemStatusStillPending` | coverage gap: ApplyProgress Pending status branch |
| `ODriveSyncPageTests` | `Test_ActionPollApplyProgressNoOpWhenServerPanelIsNull` | coverage gap: ApplyProgress null serverItems guard |
| `ODriveSyncPageTests` | `Test_FormatRelativeTimeShowsMinutesForElapsedUnderOneHour` | coverage gap: FormatRelativeTime minutes branch |
| `ODriveSyncPageTests` | `Test_FormatRelativeTimeShowsHoursForElapsedOverOneHour` | coverage gap: FormatRelativeTime hours branch |
| `ODriveSyncPageTests` | `Test_ScanSubmitApiUnreachableShowsGenericErrorNotCrash` | coverage gap: scan submit network exception |
| `ODriveSyncPageTests` | `Test_ScanPollingTransientNetworkErrorIsSwallowedAndPollingContinues` | coverage gap: scan poll network exception |
| `ODriveSyncPageTests` | `Test_DisposeDuringActivePollDoesNotThrow` | DisposeAsync during in-flight poll |
| `ODriveSyncPageTests` | `Test_ActionSubmitApiFailureShowsGenericErrorNotCrash` | action submit non-success status |
| `ODriveSyncFlowIntegrationTests` | `Test_UnauthenticatedRequestToODriveSyncRedirectsToLogin` | CAS-021 auth gate enforcement |
| `ODriveSyncFlowIntegrationTests` | `Test_ReadOnlySessionCanReachODriveSync` | SC-11 (integration) |
