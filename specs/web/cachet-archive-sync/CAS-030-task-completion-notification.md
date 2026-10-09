---
id: CAS-030
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-24
priority: P1
related_specs: [CAS-006, CAS-007, CAS-016, CAS-022, CAS-023, CAS-025, CAS-028]
touches:
  database: true
  api: true
  ui: true
  worker: false
  b2: false
  config: true
  security: true
migrations: [20260924_130000__CAS-030-task-completion-notification.sql]
---

# CAS-030 — Task Completion Notification and Results Auto-Population

**WBS traceability:** `1.WBS/CACHET_WBS_v1.md` Task 30, "Task completion notification and results
auto-population", category Asynchronous Processing Engine.
**Implements:** FR-21 (Must), FR-22 (Should). Supports FR-19 and FR-20.
**Design sources (cited, not re-decided):**
- `0.Specifications/CACHET_AsyncProcessingEngine_Design_v1.md` §7 (results written before `Completed`), §8
  (polling, 10 s), §13 (config key), Decision Log #4, #6, #7.
- `0.Specifications/CACHET_UIUX_Design_Specification_v1.md` §1.1 (Task Manager badge definition), §3.3,
  §5.2, §7.2, §10.
- `0.Specifications/CACHET_API_Design_Specification_v1.md` §7.1–§7.2 (`GET /tasks`, `GET /tasks/{taskId}`;
  own-tasks-only scoping).

## §0 Reading Guide: Current State and What This Spec Changes

Verified by manual code read on 2026-09-24.

| Area | Current state (verified) | Gap vs. FR-21 / FR-22 | This spec |
|---|---|---|---|
| Completion detection | `KeywordSearch.razor` and `ODriveSyncContentScan.razor` each poll their own results endpoint every **5 s** (hard-coded; CAS-022 §8.2, CAS-023 §8.2). | The design fixes the interval at **10 s**, config-bound (design §8, §13). | Both screens use one config-bound interval (§8.4). |
| Returning to a screen | The in-flight `taskId` is held only in component memory. After navigating away, a reload, or a lost circuit, the screen opens empty. CAS-022 §15 accepted this "until a future Task Manager screen … gives them a way to reattach". | FR-21 and UI/UX §10.2: returning to the screen must show the completed results. | Each screen reloads the user's latest task of its own type from the server when it opens (§8.2, §8.3). |
| `GET /tasks`, `GET /tasks/{taskId}` | **Not built.** `TaskManagerController` hosts only `POST /tasks/{taskId}/restart` (CAS-016). | Nothing lets a screen find the user's tasks. | Built here (§6). |
| Task Manager badge | **Not built.** CAS-025 deferred the Task Manager header button. | FR-22. | Header badge component (§8.1). |
| "Viewed" state for the badge | UI/UX §1.1 counts completed or failed tasks "the current user has not yet viewed". `dbo.task_queue` has no column to record this. | Must survive a browser close (FR-20). | New nullable column plus an acknowledge endpoint (§5, §6.4). |
| Report auto-population | Reports are generated **synchronously** on demand (API design §2; CAS-015). They never become a task. | None. The screen already shows the report in the same response. | Out of scope (§4). |

**Precedence.** This spec changes behavior in two merged specs, CAS-022 and CAS-023. It does so only
within the gap CAS-022 §15 explicitly deferred to "a future Task Manager" spec. It does not reopen any
other decision in those specs.

---

## §1 Summary

Once a search, content scan, or sync action finishes, the user sees the result without re-running
anything, even if they left the screen or closed the browser in the meantime (FR-21). A header badge
shows that a previously submitted task has finished (FR-22).

**Chosen mechanism: server-side polling from the Blazor Server circuit, every 10 seconds, against
durable task state in SQL Server.** No push channel is added. This design was decided on 2026-09-09
(Rakesh Devarajan, TraceCounts), and this spec implements it. The full reasoning is in §8.0.

Three parts:
1. **Detection.** A `PeriodicTimer` in each mounted component calls the Api every
   `Worker:TaskManagerPollIntervalSeconds` (default 10). It runs only while the component is mounted.
2. **Notification (FR-22).** A header `TaskManagerBadge` component polls `GET /tasks/summary`. It shows
   the count defined in UI/UX §1.1 and changes to an attention state when a completed or failed task is
   still unacknowledged.
3. **Auto-population (FR-21).** When Keyword Search or O-Drive Sync opens, it finds the user's latest
   task of its type (or the task named in `?taskId=`) and loads its stored results from the existing
   results endpoints. Viewing a finished task records an acknowledgement, which clears it from the badge.

---

## §2 User Story

As a **TraceCounts reviewer or Cachet business owner**, I want to submit a search or scan, leave the
screen or close my browser, and later see a header indicator that it has finished. When I open that
screen again, I want the results already there. That way I never re-run a task that can take up to a
day (FR-22a) just to see its output.

---

## §3 In-Scope

- **Database:** column `dbo.task_queue.result_acknowledged_at`, three new stored procedures, and one
  additive change to `dbo.task_queue_get` (§5).
- **Api:** `GET /tasks`, `GET /tasks/{taskId}`, `GET /tasks/summary`, and
  `POST /tasks/{taskId}/acknowledge`, all on the existing `TaskManagerController` (§6).
- **Web:**
  - New `TaskManagerBadge` component inside `MainLayout.razor`'s authenticated header (§8.1).
  - Keyword Search: reload the latest task on open, plus the `?taskId=` deep link (§8.2).
  - O-Drive Sync and Content Scan: reload the latest Content Scan on open, plus the `?taskId=` deep
    link. If any sync action is still `Pending` on that scan's rows, its polling resumes (§8.3).
  - Both screens acknowledge a finished task that the current user owns (§8.5).
  - One config-bound poll interval, replacing both 5-second constants (§8.4, §9).
  - Session-expiry (401) handling for every poll loop in this spec's scope (§7 V9).

---

## §4 Out-of-Scope

- **Task Manager screen/list UI (WBS Task 25).** This spec builds the API that screen will use
  (`GET /tasks`, `GET /tasks/{taskId}`, acknowledge) and defines the `?taskId=` deep-link contract its
  **View Results** action will navigate to. It does not build the screen. Until WBS Task 25 ships, the
  header Task Manager button is rendered disabled with its live badge (§8.1; §15 Q3).
- **Cancellation** (`POST /tasks/{taskId}/cancel`, `Cancelled` results contract). Design §10 and
  CAS-028 §4 defer it. `Cancelled` tasks are excluded from the badge (§5), and the screens treat them
  as having no results to load (§8.2).
- **Worker changes.** The Worker already writes all result rows before setting `Completed` (design §7).
  Moving orphaned `Running` rows to `Failed` belongs to **CAS-028** (draft). Without it, a crashed task
  stays counted as active in the badge (§15 Q6).
- **Report auto-population.** Reports are synchronous and never queued (§0).
- **Web caching layer (BRD §12.4, still open).** This spec reads stored results through the existing
  results endpoints. It adds no cache and assumes none. If §12.4 is resolved in favour of a cache, the
  cache sits behind those endpoints (CAS-006 / CAS-010) and this spec is unaffected. **Not blocked.**
- **Push notification** over SignalR, a Redis backplane, or SQL Server Query Notifications. Rejected in
  §8.0.
- Changing who may read results. `GET /search/{taskId}/results` and `GET /sync/{taskId}/results` stay
  readable by any authenticated user, as CAS-006 §15 resolved on 2026-09-22. This spec's **UI** only
  loads tasks the current user owns (§12).
- Toast or pop-up notifications, email, or browser notifications. FR-22 asks only for a
  "badge/indicator".
- Writing the §8 resolution back into `CACHET_UIUX_Design_Specification_v1.md` §10 (design §17 item 2).
  That is a different document (§15 Q8).

---

## §5 Database Objects

### Tables / Columns

`dbo.task_queue`: add one column.

| Column | Type | Null | Default | Purpose |
|---|---|---|---|---|
| `result_acknowledged_at` | `DATETIME2(3)` | NULL | none | UTC time the **submitting user** first viewed this task's finished outcome (`Completed` or `Failed`). `NULL` means not yet viewed, so the task still counts toward the badge. Written once and never cleared. |

- Existing rows stay `NULL`, which means every already-finished task starts out unacknowledged. See
  the §14 rollout note on backfill.
- A restart (CAS-016) inserts a new row, so the new task starts `NULL`. The original row keeps its own
  value.
- **No new index.** At the confirmed scale (3–4 users), `IX_task_queue_submitted_by_status`
  (`submitted_by_user_id, status`) already supports both the summary and the list.

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `task_queue_lst` | dbo | lst | **New.** `@submitted_by_user_id INT` (required), `@task_type NVARCHAR(20) = NULL`, `@status NVARCHAR(20) = NULL`, `@page INT`, `@page_size INT`. Returns rows newest first (`submitted_at DESC, task_queue_id DESC`) joined to `dbo.users` for `username`, plus a total-count result set, following the `audit_log_lst` / `search_result_lst` paging convention. Always filters by `@submitted_by_user_id`. |
| `task_queue_summary_get` | dbo | get | **New.** `@submitted_by_user_id INT`. Returns one row: `active_count` = own rows in `Queued` or `Running`; `unacknowledged_terminal_count` = own rows in `Completed` or `Failed` with `result_acknowledged_at IS NULL`. `Cancelled` is never counted. |
| `task_queue_acknowledge_upd` | dbo | upd | **New.** `@task_queue_id INT`, `@submitted_by_user_id INT`, `@updated_by NVARCHAR(50)`. In one statement: `UPDATE … SET result_acknowledged_at = SYSUTCDATETIME(), updated_by, updated_at WHERE task_queue_id = @id AND submitted_by_user_id = @user AND status IN ('Completed','Failed') AND result_acknowledged_at IS NULL`. Returns an outcome code: `0` = acknowledged or already acknowledged; `1` = not found **or** not owned (deliberately the same code); `2` = not in a finished state. The ownership check happens inside the procedure, as in CAS-016's `task_queue_restart_ins`. |
| `task_queue_get` | dbo | get | **Changed, additive only.** Add `result_acknowledged_at` to the SELECT list. Parameters and existing columns are unchanged, so current callers (`KeywordSearchService`, `SyncScanService`, the Worker) are unaffected. |

### Migration Strategy

Hybrid model, as in every prior spec. db-author writes:
- An idempotent migration, `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-030-task-completion-notification.sql`:
  guarded `ALTER TABLE … ADD`, and `CREATE OR ALTER` for the four procedures. The commented rollback
  section drops the three new procedures, restores `task_queue_get`'s previous SELECT list, and drops
  the column.
- Updated mirrors: `Entities/dbo.task_queue.sql`, `Stored-Procedures/dbo.task_queue_get.sql`, and three
  new `Stored-Procedures/dbo.task_queue_{lst,summary_get,acknowledge_upd}.sql`.

---

## §6 API Endpoints

All four endpoints go on the existing `TaskManagerController`, use `[Authorize(Policy =
"CachetAuthenticated")]`, and are synchronous. The user id always comes from the
`ClaimTypes.NameIdentifier` claim (the existing `GetUserId()` pattern), never from the request.
Errors are `ProblemDetails` with the existing `https://cachet.tracecounts.internal/errors/*` type
URIs.

### Endpoint: `GET /tasks`

Lists the caller's own tasks, newest first (FR-19; API design §7.1). The screens use it to find the
latest task of their type.

**Query:** `type` (optional: `KeywordSearch|ContentScan|SyncCopy|SyncUpdate|SyncPurge`), `status`
(optional: `Queued|Running|Completed|Failed|Cancelled`), `page` (default 1), `pageSize` (default 50).
`type` is an **addition** to API design §7.1 **(design decision, not BRD-specified)**. The screens need
it to find the latest task of one type without scanning pages on the client.

**Response 200:**
```json
{
  "items": [
    {
      "taskId": 100234,
      "type": "KeywordSearch",
      "submittedBy": "m.alvarez",
      "submittedAt": "2026-09-24T14:05:01Z",
      "startTime": "2026-09-24T14:05:03Z",
      "completedAt": null,
      "status": "Running",
      "isAcknowledged": false
    }
  ],
  "page": 1, "pageSize": 50, "totalCount": 1
}
```
`submittedAt`, `completedAt`, and `isAcknowledged` are additive to the API design §7.1 shape.
`startTime` is `null` while the task is `Queued`.

| Code | Condition |
|------|-----------|
| 200 | Success. An empty `items` list is valid. |
| 400 | V1, V2 (`validation-failed`) |
| 401 | No or invalid session |

### Endpoint: `GET /tasks/{taskId:int}`

Returns one of the caller's tasks (API design §7.2). Used to resolve a `?taskId=` deep link and to
re-label the filter bar with the task's own parameters.

**Response 200:**
```json
{
  "taskId": 100234,
  "type": "KeywordSearch",
  "submittedBy": "m.alvarez",
  "submittedAt": "2026-09-24T14:05:01Z",
  "startTime": "2026-09-24T14:05:03Z",
  "completedAt": "2026-09-24T15:12:40Z",
  "status": "Completed",
  "isAcknowledged": false,
  "resultDetail": null,
  "searchParameters": { "term": "invoice", "extension": "PDF" }
}
```
- `resultDetail` is filled only for `Failed`. It carries `task_queue.result_detail` as is, and that
  value is already non-sensitive (design §9.2).
- `searchParameters` is filled only for `type = KeywordSearch`. It is a **fixed projection** of
  `request_parameters` containing only `term` and `extension`. The raw JSON is never passed through
  (data minimization, §12). It is `null` for every other type.

| Code | Condition |
|------|-----------|
| 200 | Found, and owned by the caller |
| 401 | No or invalid session |
| 404 | V3: task does not exist **or** is not owned by the caller (same `task-not-found` body either way) |

### Endpoint: `GET /tasks/summary`

Returns the numbers the badge needs (FR-22). It is cheap enough to call every 10 seconds.

**Response 200:**
```json
{ "activeCount": 1, "unacknowledgedTerminalCount": 2 }
```
The badge number is `activeCount + unacknowledgedTerminalCount`, which is the UI/UX §1.1 definition.
Both values count only the caller's own tasks.

| Code | Condition |
|------|-----------|
| 200 | Success |
| 401 | No or invalid session |

### Endpoint: `POST /tasks/{taskId:int}/acknowledge`

Records that the owner has viewed a finished task, which clears it from the badge. **Async:** No.
**Body:** none.

| Code | Condition |
|------|-----------|
| 204 | Acknowledged now, or already acknowledged (idempotent, V5) |
| 401 | No or invalid session |
| 404 | V3: not found or not owned (same body as `GET /tasks/{taskId}`) |
| 409 | V4: task is `Queued`, `Running`, or `Cancelled` (`task-not-terminal`) |

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `GET /tasks`: `page` ≥ 1; `pageSize` 1–100 | 400 `validation-failed` | `Test_V1_ListTasks_InvalidPaging_Returns400` |
| V2 | `GET /tasks`: `type` and `status`, when present, must be one of the values listed in §6 (exact, case-sensitive match, as enforced by `CK_task_queue_*`) | 400 `validation-failed` | `Test_V2_ListTasks_UnknownTypeOrStatus_Returns400` |
| V3 | `GET /tasks/{taskId}` and `POST …/acknowledge`: a task that does not exist and a task owned by another user return the **same** response | 404 `task-not-found` (same body in both cases) | `Test_V3_OtherUsersTask_Returns404IdenticalToMissing` |
| V4 | `POST …/acknowledge`: status must be `Completed` or `Failed` | 409 `task-not-terminal` | `Test_V4_AcknowledgeNonTerminalTask_Returns409` |
| V5 | `POST …/acknowledge` is idempotent: a second call returns 204 and leaves the first `result_acknowledged_at` value unchanged | n/a (success) | `Test_V5_AcknowledgeTwice_TimestampUnchanged` |
| V6 | All four endpoints require an authenticated session | 401 | `Test_V6_TaskEndpoints_Unauthenticated_Return401` |
| V7 | Web: a `?taskId=` that is not a positive integer, returns 404 from `GET /tasks/{taskId}`, or belongs to a different task type than the screen shows no results, sends no acknowledge, and shows "That task could not be found." | Screen shows the notice; filter bar stays usable | `Test_V7_DeepLinkUnknownOrWrongTypeTask_ShowsNotFoundNotice` |
| V8 | Web: `Worker:TaskManagerPollIntervalSeconds` must be an integer from 5 to 60 | Web host fails at startup (`OptionsValidationException`) | `Test_V8_PollIntervalOutOfRange_FailsValidation` |
| V9 | Web: a 401 from any poll call (badge, screen status, or results) stops **that** poll loop | The screen shows "Your session has expired — sign in again to see results." with a link to `/login`. The badge hides its number rather than showing a stale one | `Test_V9_PollReceives401_StopsPollingAndShowsSessionExpired` |
| V10 | Web: a transient poll failure (network error or 5xx) keeps the last rendered state and retries on the next tick. The badge never shows an error | Warning logged; no banner for the badge; screen behaves as CAS-022 §8.5 | `Test_V10_TransientPollFailure_KeepsLastStateAndRetries` |

---

## §8 UI / UX

### §8.0 Detection Mechanism: Polling, and Why

**Decision (implemented, not re-decided):** each mounted component polls the Api every 10 seconds from
the server-side Blazor circuit. Decided 2026-09-09, Rakesh Devarajan, TraceCounts (design §8, Decision
Log #4 and #7).

| Option | Verdict | Reason |
|---|---|---|
| **Poll the durable task state (chosen)** | Adopted | Web and Worker are separate processes (design §5.1, Decision #6). Polling reads the same SQL Server rows that FR-20 already requires to be durable, so a notification cannot be lost. A dropped circuit, a Web restart, or a closed browser is repaired by the next poll, or by the reload when the screen opens (§8.2). Load at 10 s: ≤ 4 users × (1 badge + ≤ 1 screen loop) is **under 1 request per second**, each a single indexed query. |
| SignalR push from Worker to Web | Rejected | The Worker would need a channel into the Web process: an internal HTTP callback into `IHubContext`, or a backplane such as Redis. That is new infrastructure, against BRD §10.2's no-broker direction. A push that arrives while the circuit is disconnected is lost, so polling would still be needed for recovery. |
| SQL Server Query Notifications (`SqlDependency`) | Rejected | Needs Service Broker enabled and permissions granted, and fires once per subscription, so it must re-subscribe after every notification. This adds operational fragility for no benefit a user would notice. |
| 5 s interval (the current CAS-022/023 constant) | Replaced | Tasks take 10 minutes to a full day (FR-22a). Getting the result 5 seconds sooner is not noticeable, and it doubles the polling load. The design fixes the value at 10 s. |

"Client-side" in design §8 means **per circuit**. The timer runs on the server inside the Blazor Server
component and calls the Api through the existing `CachetApi` `HttpClient`. No JavaScript timer or
browser `fetch` is used, which keeps session-forwarding and FR-03 unchanged.

**Lifecycle, same for every poll loop:** start in `OnInitializedAsync` or on submission. Poll **only**
while the component is mounted (design §8). Stop on reaching a finished state, on a 401 (V9), or on
`DisposeAsync`. There is one `CancellationTokenSource` per loop, as in the current code.

### §8.1 Task Manager Badge (FR-22)

- **Component:** new `Components/Layout/TaskManagerBadge.razor`, `@rendermode InteractiveServer`,
  rendered in `MainLayout.razor`'s authenticated header row immediately **left of** the Quarantine
  Queue button (UI/UX §1.1). `MainLayout` itself stays static, so the badge must be its own interactive
  island.
- **Data:** calls `GET /tasks/summary` **immediately on init**, so the badge is correct right after each
  navigation, and then on every poll tick.
- **Count:** `activeCount + unacknowledgedTerminalCount` (UI/UX §1.1). When the count is 0, no number
  is shown; this matches the Quarantine Queue and Audit Log button pattern.
- **Attention state (design decision, not BRD-specified; §15 Q1):** under the §1.1 definition, a task
  moving from `Running` to `Completed` **does not change the total**: it leaves `activeCount` and joins
  `unacknowledgedTerminalCount`. A count alone therefore cannot show that "a task finished". So:
  - If `unacknowledgedTerminalCount > 0`, the badge uses a distinct attention style and its
    `title` / `aria-label` reads e.g. "Task Manager — 2 finished tasks not yet viewed, 1 in progress".
  - If `unacknowledgedTerminalCount` **increases** between two polls, the component sets a
    visually hidden `aria-live="polite"` region to "A task has finished." This is for accessibility
    only; there is no toast.
- **Button:** labelled "Task Manager". Until WBS Task 25 ships, it is rendered `disabled` with
  `title="Coming soon"` (CAS-025 §0 placeholder convention), and the badge stays live (§15 Q3).
  WBS Task 25 enables it.

### §8.2 Keyword Search: Reload on Open and Deep Link (FR-21)

Adds `[SupplyParameterFromQuery(Name = "taskId")] public int? TaskId { get; set; }` to
`KeywordSearch.razor` (`@page "/"`). In `OnInitializedAsync`, after the existing quarantine load:

1. **Resolve the task.**
   - If `TaskId` is present: call `GET /tasks/{TaskId}`. On a 404 or a `type` other than
     `KeywordSearch`, show the V7 notice and stop.
   - Otherwise: call `GET /tasks?type=KeywordSearch&page=1&pageSize=1`. If no items come back, keep
     today's empty initial state (`taskState = "None"`) and stop.
2. **Branch on status:**

   | Status | Screen behavior |
   |---|---|
   | `Queued` / `Running` | Set `activeTaskId` and `submittedAtUtc` from the task, show the existing in-progress banner, and start the existing results poll loop (`PollTaskAsync`) using the configured interval. When it finishes, it fills the panels in place, exactly as it does today. |
   | `Completed` | Call `GET /search/{taskId}/results` once and fill the Filename Matches and Content Matches panels. **No `POST /search`, no new task, no B2 call**: the results are the stored rows (design §7). |
   | `Failed` | Show the existing failure banner. When `resultDetail` is present, append it. |
   | `Cancelled` | Treat as no results to load: empty initial state (cancellation UI is out of scope, §4). |

3. **Label the results.** For a reloaded or deep-linked task, set the filter bar's search term and
   extension from `searchParameters`, and show a one-line context header above the panels: *"Showing
   results for "`<term>`" (`<extension>`), submitted `<relative time>` ago."* A user must never have to
   guess which search the results they see belong to. This matters for accurate review.
4. **Acknowledge** as described in §8.5.

A new search submitted after a reload behaves exactly as it does today (CAS-022 §8.2).

### §8.3 O-Drive Sync and Content Scan: Reload on Open and Deep Link (FR-21)

This is the same pattern as §8.2, on `ODriveSyncContentScan.razor` (`@page "/odrive-sync"`), with the
task type `ContentScan`.

- `Queued` / `Running`: set `scanTaskId`, show the existing in-progress banner, and start the existing
  `PollScanAsync` loop.
- `Completed`: call `GET /sync/{taskId}/results` once and fill the three panels.
  - **Resuming sync actions.** Each row's `syncStatus` is stored on the scan's own `sync_result` rows
    (CAS-023 §8.4, verified at `ODriveSyncContentScan.razor:581-584`). Any row whose `syncStatus` is
    `Pending` belongs to a Copy, Update, or Purge action that is still in progress. Pending rows in
    New-on-O-Drive map to Copy, Modified to Update, and Removed to Purge. The component adds those
    `fileId`s to that action's `PendingFileIds` and starts the existing per-action poll loop, so rows
    update in place when the action finishes.
  - A Copy, Update, or Purge task's own `taskId` is **not** used as a deep link, because that task's
    results are the scan's rows. A `?taskId=` that points to a `Sync*` task is treated as the wrong
    type (V7). WBS Task 25's View Results for a sync action should link to the scan instead
    (§15 Q7).
- `Failed` / `Cancelled`: same as §8.2.
- An all-failed sync batch keeps showing as specified by CAS-023. This spec does not change it.

### §8.4 One Poll Interval

- Both 5-second constants are replaced by one value bound from configuration:
  `KeywordSearch.razor:400` (`TimeSpan.FromSeconds(5)`) and `ODriveSyncContentScan.razor:227`
  (`PollInterval`).
- New `TaskPollingOptions` (Web), `ValidateOnStart`, range from V8, default 10 s (§9). It is injected
  into the badge and both pages through `IOptions<TaskPollingOptions>`, so tests can set a short
  interval.

### §8.5 Acknowledgement Rule

A screen calls `POST /tasks/{taskId}/acknowledge` **once per task per component instance**, and only
when **all** of the following hold:
- the task's finished state (`Completed` results, or the `Failed` banner) has **actually been
  rendered**, meaning after the `StateHasChanged` that shows it;
- the task was resolved through `GET /tasks` or `GET /tasks/{taskId}`, and therefore belongs to the
  current user;
- the task's `isAcknowledged` value is `false`.

This covers both routes by which a user first sees an outcome: arriving at a task that is already
finished, and watching an in-progress task finish in place. A task the user watched finish is still
acknowledged; a count for a result the user already saw would be noise.

An acknowledge failure (any non-204) is logged at Warning and never shown to the user. The next time
the screen opens, it retries.

---

## §9 Configuration Changes

| Key | Default | Description | Env Override |
|-----|---------|-------------|--------------|
| `Worker:TaskManagerPollIntervalSeconds` | `10` | **Web** process. Poll interval for the Task Manager badge and every in-progress screen poll loop (design §8, §13). Integer 5–60 (V8). The key name is taken verbatim from design §13 even though it is read by Web (§15 Q5). | Yes (`Worker__TaskManagerPollIntervalSeconds`) |

Added to `src/Cachet.ArchiveSync.Web/appsettings.json`. It holds no credential (FR-03).

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `GET /tasks` returns only the caller's tasks, newest first by `submitted_at`, filtered by `type`/`status` when given, with `submittedBy` resolved to a username | `Test_SC1_ListTasks_ReturnsOnlyCallersTasksNewestFirst` |
| SC-2 | `GET /tasks/summary` returns `activeCount` = own `Queued`+`Running` and `unacknowledgedTerminalCount` = own unacknowledged `Completed`+`Failed`; other users' tasks and `Cancelled` tasks are never counted | `Test_SC2_Summary_CountsOnlyOwnActiveAndUnacknowledgedTerminal` |
| SC-3 | After `POST /tasks/{id}/acknowledge` on an own `Completed` task, `result_acknowledged_at` is set and the next `GET /tasks/summary` has `unacknowledgedTerminalCount` reduced by 1 | `Test_SC3_Acknowledge_ClearsTaskFromSummary` |
| SC-4 | `GET /tasks/{id}` for an own `KeywordSearch` task returns `searchParameters` with only `term` and `extension`; for any other type `searchParameters` is null; the raw `request_parameters` string never appears in the response | `Test_SC4_GetTask_ProjectsSearchParametersOnly` |
| SC-5 | The badge calls `GET /tasks/summary` on first render with no wait for a tick, shows `activeCount + unacknowledgedTerminalCount`, refreshes each configured interval, and stops polling after disposal | `Test_SC5_Badge_RendersImmediatelyPollsAndStopsOnDispose` |
| SC-6 | When `unacknowledgedTerminalCount` goes from 0 to 1 between polls, with the total unchanged, the badge switches to the attention state and the `aria-live` region reads "A task has finished." | `Test_SC6_Badge_TerminalTransitionShowsAttentionState` |
| SC-7 | Opening `/` when the user's latest `KeywordSearch` task is `Completed` fills both panels from `GET /search/{id}/results`, sets the filter bar and context header from `searchParameters`, and makes **no** `POST /search` call | `Test_SC7_KeywordSearch_ReloadsCompletedTaskWithoutResubmitting` |
| SC-8 | Opening `/` when the latest task is `Running` shows the in-progress banner and resumes polling; when the fake Api later returns the finished results, the panels fill in place with no navigation | `Test_SC8_KeywordSearch_ReloadsRunningTaskAndAutoPopulatesOnCompletion` |
| SC-9 | `/?taskId={id}` for an own completed task, and `/odrive-sync?taskId={id}` for an own completed `ContentScan`, each fill their panels from that task rather than the latest one | `Test_SC9_DeepLink_PopulatesNamedTask` |
| SC-10 | Opening `/odrive-sync` when the latest `ContentScan` is `Completed` fills all three panels; rows whose `syncStatus` is `Pending` start the matching action's poll loop and update in place when they leave `Pending` | `Test_SC10_ODriveSync_ReloadsScanAndResumesPendingSyncActions` |
| SC-11 | Each screen calls `POST …/acknowledge` exactly once after rendering an own unacknowledged `Completed` or `Failed` task, and never for a `Queued`/`Running`/`Cancelled` task, an already-acknowledged task, or a V7 not-found task | `Test_SC11_Screens_AcknowledgeOnlyOwnRenderedTerminalTaskOnce` |
| SC-12 | The badge and both screen poll loops use `Worker:TaskManagerPollIntervalSeconds` (default 10); no hard-coded 5-second interval remains in `Cachet.ArchiveSync.Web` | `Test_SC12_PollInterval_BoundFromConfiguration` |

---

## §11 Telemetry

```csharp
_logger.LogInformation("Task list read: user {UserId}, type {TaskType}, page {Page}, count {Count}", userId, type, page, count);
_logger.LogInformation("Task acknowledged: task {TaskId} by user {UserId}", taskId, userId);
_logger.LogWarning("Task acknowledge rejected: task {TaskId}, outcome {Outcome}", taskId, outcome);   // 404/409

// Web
_logger.LogInformation("{Screen}: reloaded task {TaskId} with status {Status}", screen, taskId, status);
_logger.LogWarning("{Component}: poll stopped on 401 from {Endpoint}", component, endpoint);            // V9
_logger.LogWarning("{Component}: transient poll failure on {Endpoint}", component, endpoint);           // V10
```

- **Never log:** the search term or any other part of `request_parameters` (a search term can itself
  be sensitive, for example a patient or client name), results content, file paths, cookie or session
  values, or anything related to B2 keys.
- The badge logs **nothing** on a successful tick, so it does not add log noise every 10 seconds.

---

## §12 Security Notes

### B2 Credential Handling
No B2 interaction. None of the four endpoints or the Web changes calls B2 or touches a key. Reloading
results reads stored `search_result`/`sync_result` rows through the existing endpoints, so FR-03 is
unaffected. `request_parameters` never contains a key (design §12), and in any case §6 exposes only a
fixed two-field projection of it.

### Ownership and Data Exposure
- **Scoped on the server.** `task_queue_lst`, `task_queue_summary_get`, and `task_queue_acknowledge_upd`
  all take `@submitted_by_user_id` from the authenticated claim. Ownership for `GET /tasks/{taskId}`
  is enforced in the service by comparing against `task_queue_get`'s `submitted_by_user_id`. The client
  never supplies a user id (API design §7.1: "do not rely on the client").
- **No task enumeration.** A missing task and another user's task return the same 404 (V3). This
  intentionally differs from CAS-016's `403 task-not-owner` on restart: the Task Manager read surface
  is own-scoped by design (API design §7.1), so confirming that another user's task exists would be
  an information leak. CAS-016 is not changed.
- **The UI only loads the user's own tasks.** The two screens resolve tasks only through the own-scoped
  endpoints. So the Web never loads another user's task by default, even though the results endpoints
  themselves stay readable by any authenticated user (CAS-006 §15, unchanged, §4).
- **Data minimization.** `searchParameters` exposes only `term` and `extension`. Sync-action `fileIds`
  and any future `request_parameters` fields are not passed through.

### Audit Trail
- The three `GET` endpoints are reads. FR-23 covers "search, match, quarantine, and delete" actions,
  so they are not audited.
- **`POST …/acknowledge` is not audited (design decision; §15 Q2).** It sets a notification flag and
  does not change any archive file, result, or business state. An audit row every time someone views
  results would crowd the Audit Log and the Post-Action Report, which is built from audit rows
  (UI/UX §9.3), with non-actions. The `result_acknowledged_at` timestamp and `updated_by`/`updated_at`
  on the row already record it durably.
- No existing audit write is moved, removed, or reordered.

### Session / Auth Requirements
- All endpoints use `CachetAuthenticated`. Read Only users are included, because FR-19–FR-22 apply to
  every signed-in user.
- **CSRF.** `POST …/acknowledge` is called server-to-server from the circuit's `HttpClient` using the
  forwarded `SameSite=Strict` cookie. This is the same trust model as CAS-016's
  `POST /tasks/{taskId}/restart`, and no new browser-facing form is added.
- **Circuit cookie staleness (risk, not verified at runtime; §15 Q4).** `CookieForwardingHandler`
  forwards the `Cookie` header captured when the circuit connected. When the Api renews the sliding
  session, the new cookie goes back to the Web server's `HttpClient`, not to the browser. A circuit
  left open longer than `Auth:SlidingExpirationHours` (8 h) is therefore expected to start getting
  401s. That is exactly the full-day-task case FR-22a describes. V9 makes this fail visibly and
  safely: polling stops and the user is told to sign in again, instead of the screen silently staying
  "in progress". After signing in, the reload on open (§8.2/§8.3) brings the results back. A proper
  fix belongs in CAS-002's session design, not here.
- Login and encryption-at-rest are resolved (BRD §12.3). This spec depends on neither.

---

## §13 Test Plan

**Decomposition check (P9):** four signals were assessed.

| Signal | Result |
|---|---|
| Project boundary | **Fired.** The spec spans DB, Api, and Web. |
| Test-domain split | **Fired.** It needs xUnit Api tests, SQL Server integration tests, and bUnit tests. |
| Size | **Fired.** The estimate is more than 15 changed files. |
| Module fan-out | Did not fire. Task Manager is shared infrastructure for Keyword Search and O-Drive Sync (guideline §2.2). |

**Resolution:** kept as one spec by the requester's decision (2026-09-24). Reason: the backend is small
(4 endpoints, 1 column, 3 new procedures) and exists only to serve this spec's UI. WBS Task 30 is a
single deliverable. §5–§6 can be reviewed on their own as a contract. Record this in the decomposition
guideline's §2.1 and §2.4 precedent slots.

### Unit Tests (`Cachet.ArchiveSync.Api.Tests`, xUnit + NSubstitute + FluentAssertions)
| Class | Method | Proves |
|-------|--------|--------|
| `TaskManagerServiceTests` | `Test_V3_GetTask_NotOwned_ReturnsNotFound` | V3 (service-level ownership check) |
| `TaskManagerServiceTests` | `Test_SC4_GetTask_ProjectsSearchParametersOnly` | SC-4, data minimization |
| `TaskManagerServiceTests` | `Test_V4_Acknowledge_MapsNotTerminalOutcome` | V4 mapping |
| `TaskManagerControllerTests` (unit) | `Test_V1_ListTasks_InvalidPaging_Returns400`, `Test_V2_ListTasks_UnknownTypeOrStatus_Returns400` | V1, V2 |

### Integration Tests (`Cachet.ArchiveSync.Api.IntegrationTests`, existing `TaskManagerApiWebApplicationFactory` + LocalDB)
| Class | Method | Proves |
|-------|--------|--------|
| `TaskManagerControllerTests` | `Test_SC1_ListTasks_ReturnsOnlyCallersTasksNewestFirst` | SC-1 |
| `TaskManagerControllerTests` | `Test_SC2_Summary_CountsOnlyOwnActiveAndUnacknowledgedTerminal` | SC-2 |
| `TaskManagerControllerTests` | `Test_SC3_Acknowledge_ClearsTaskFromSummary` | SC-3 |
| `TaskManagerControllerTests` | `Test_V3_OtherUsersTask_Returns404IdenticalToMissing` | V3 (byte-identical bodies) |
| `TaskManagerControllerTests` | `Test_V4_AcknowledgeNonTerminalTask_Returns409`, `Test_V5_AcknowledgeTwice_TimestampUnchanged`, `Test_V6_TaskEndpoints_Unauthenticated_Return401` | V4–V6 |
| `TaskQueueAcknowledgeRepositoryTests` (new) | `Test_AcknowledgeUpd_OwnershipEnforcedInProcedure` | The procedure itself rejects a non-owner, not only the service |

**Must actually run.** CAS-028 §0 found that the queue's real-database tests were `[Fact(Skip = …)]`,
which let an invalid claim procedure ship. The integration tests above **must run against LocalDB, not
be skipped**. A skipped test counts as a gate failure for this spec.

### Component Tests (`Cachet.ArchiveSync.Web.Tests`, bUnit, existing `Fake*ApiHandler` pattern)
| Class | Method | Proves |
|-------|--------|--------|
| `TaskManagerBadgeTests` (new) | `Test_SC5_Badge_RendersImmediatelyPollsAndStopsOnDispose`, `Test_SC6_Badge_TerminalTransitionShowsAttentionState`, `Test_V9_…` (badge variant), `Test_V10_…` (badge variant) | SC-5, SC-6, V9, V10 |
| `KeywordSearchPageTests` | `Test_SC7_…`, `Test_SC8_…`, `Test_SC9_DeepLink_PopulatesNamedTask` (search), `Test_V7_…`, `Test_SC11_…` (search) | SC-7–SC-9, SC-11, V7 |
| `ODriveSyncPageTests` | `Test_SC9_…` (scan), `Test_SC10_ODriveSync_ReloadsScanAndResumesPendingSyncActions`, `Test_SC11_…` (scan), `Test_V9_PollReceives401_StopsPollingAndShowsSessionExpired` | SC-9–SC-11, V9 |
| `TaskPollingOptionsTests` (new) | `Test_V8_PollIntervalOutOfRange_FailsValidation`, `Test_SC12_PollInterval_BoundFromConfiguration` | V8, SC-12 |

**Existing tests:** `KeywordSearchPageTests` and `ODriveSyncPageTests` build their pages with no
`GET /tasks` stub. Their fake handlers must return an empty `GET /tasks` list by default, so every
existing CAS-022/023 test keeps passing without changes to its assertions. A pre-existing test that
starts failing is a regression, not something to update.

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. **Database migration first.** The column is nullable and the change to `task_queue_get` is
   additive, so the currently deployed Api and Worker keep working unchanged.
   - **Backfill decision (§15 Q9):** without a backfill, every task that finished before this release
     appears as "not yet viewed" in its owner's badge. Recommended: in the migration, set
     `result_acknowledged_at = completed_at` for rows already in `Completed`/`Failed`, so only tasks
     that finish after the release count.
2. **Api.** The four endpoints are additive. No existing route changes; confirm with
   `endpoints diff CAS-030`, which should show exactly 4 additions.
3. **Web.** Deploy after the Api: the badge and the reload need the new endpoints, and a Web build
   running against an older Api would get 404s on `GET /tasks`. Add `Worker__TaskManagerPollIntervalSeconds`
   to the Web environment if a value other than the default is wanted.
4. **Worker:** no redeploy.
5. **Rollback:** revert Web first, then Api, then run the migration's rollback section. The column can
   be left in place, since it is nullable and harmless, if a quick rollback is preferred.

---

## §15 Open Questions

All resolved 2026-09-24 by ratheesh@scientificvision.net ("make it ready and implement"), adopting the
option this spec recommended for each question. Where a question had no stated recommendation, the
smallest-scope option was taken and is marked as such.

- [x] **Q1 — Badge signal (FR-22).** Resolved: attention state plus `aria-live` announcement (§8.1);
      the UI/UX §1.1 count definition is kept unchanged.
- [x] **Q2 — Audit for acknowledge.** Resolved: `POST …/acknowledge` is **not** audited (§12).
- [x] **Q3 — Task Manager button before WBS Task 25.** Resolved: shipped disabled (`title="Coming soon"`)
      with a live badge; WBS Task 25 enables it.
- [x] **Q4 — Stale circuit cookie (risk).** Resolved: V9's safe-failure handling is accepted; a proper
      fix is tracked against CAS-002; runtime confirmation during WBS Task 36.
- [x] **Q5 — Config key name.** Resolved: keep `Worker:TaskManagerPollIntervalSeconds` verbatim from
      design §13.
- [x] **Q6 — Dependency on CAS-028 (draft).** Resolved (no stated recommendation; smallest scope): this
      spec proceeds independently. Until CAS-028's startup sweep ships, an orphaned `Running` task stays
      counted as active in the badge — accepted as a known, documented limitation.
- [x] **Q7 — View Results target for sync actions.** Resolved: WBS Task 25 links Copy/Update/Purge to
      the originating scan (`/odrive-sync?taskId=<scanTaskId>`). No change in this spec (§8.3 already
      treats a `Sync*` deep link as wrong type, V7).
- [x] **Q8 — Documentation write-back.** Resolved (no stated recommendation; smallest scope): tracked
      separately, outside this spec's delivery (§4).
- [x] **Q9 — Backfill.** Resolved: yes — the migration sets `result_acknowledged_at = completed_at` for
      rows already in `Completed`/`Failed` (§14 step 1).
- Not dependent on BRD §12.4. See §4: stored results are read through the existing endpoints, and no
  caching-layer assumption is made.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code, for WBS Task 30 | 2026-09-24 | Draft |
| Approver (ready) | ratheesh@scientificvision.net | 2026-09-24 | §15 Q1–Q9 resolved per recommendations; §7/§10/§12 accepted as written |
| Reviewer | | | |

---

## §17 Graph Impact

### God Nodes in Blast Radius
- `ODriveSyncPageTests` (67 edges) and `KeywordSearchPageTests` (53 edges): both gain tests and a
  default `GET /tasks` stub (§13).
- `ILogger` (27 edges): new log events in the controller, service, badge, and both pages (§11).

### Communities / Modules Affected
- **Modules:** Task Manager (primary), Keyword Search, O-Drive Sync. Auth only through the V9
  session-expiry handling. Quarantine, Audit Log, and Reporting are unaffected.
- **Projects:** Core (DTOs, interfaces), Api (`TaskManagerController`, `TaskManagerService`,
  `TaskQueueRepository`), Web (`MainLayout`, the new `TaskManagerBadge`, `KeywordSearch`,
  `ODriveSyncContentScan`, options, `Program.cs`). **Worker: no change.**

### Surprising Connections
1. **Results readable by anyone, task surface own-only.** `GET /search|sync/{taskId}/results` accepts
   any authenticated user (CAS-006 §15), while `GET /tasks` is own-only (API design §7.1). This spec
   keeps both as they are and only scopes what the UI loads by default (§12).
2. **Sync-action progress lives on the scan's rows,** not on the action task's own id
   (`ODriveSyncContentScan.razor:581-584`). So reloading only the scan also recovers in-progress sync
   actions (§8.3).
3. **Circuit cookie forwarding** (`CookieForwardingHandler`) captures the cookie once per circuit, which
   conflicts with full-day polling (§12, Q4).
4. **The badge total does not change on completion** under the UI/UX §1.1 definition (§8.1, Q1).
5. **Tooling:** `python -m cachet_agent_tools` fails to start on Python 3.14. The help text
   `'e.g. "87.3%"'` in `__main__.py:64` has an unescaped `%`. The helpers were called through their
   module functions for this spec; the CLI itself was not changed.

### Graphify Queries Run
```
py -m graphify query "task completion notification badge auto-populate results Task Manager polling FR-21 FR-22" --budget 1500
  → BFS depth 2 from 'FR-22: Task Completion Notification', 'TaskManagerApiWebApplicationFactory'; 119 nodes
```
Supplemented by a manual read of: `TaskManagerController.cs`, `TaskManagerService.cs`,
`ITaskQueueRepository.cs`, `TaskQueueRow.cs`, `KeywordSearchService.cs` / `SyncScanService.cs`
(`GetResultsAsync`), `SearchController.cs`, `SyncController.cs`, `KeywordSearch.razor`,
`ODriveSyncContentScan.razor`, `MainLayout.razor`, `Routes.razor`, Web `Program.cs`,
`CookieForwardingHandler.cs`, `dbo.task_queue.sql`, `dbo.task_queue_get.sql`, `dbo.task_queue_upd.sql`.
Endpoint baseline: `endpoints snapshot CAS-030 --phase before` recorded 22 endpoints.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test (integration tests ran against LocalDB, not skipped)
- [ ] All §7 Validations are implemented and tested
- [ ] `endpoints diff CAS-030` shows exactly the 4 §6 routes added and nothing else changed
- [ ] Every `/tasks` read and write is scoped by the claim-derived user id on the server (§12); no
      endpoint accepts a user id from the request
- [ ] Missing and other-user tasks return identical 404 bodies (V3)
- [ ] `GET /tasks/{taskId}` never returns the raw `request_parameters` (SC-4)
- [ ] No B2 call, no `POST /search` / `POST /sync/scan`, and no new `task_queue` row results from
      reloading a screen (SC-7)
- [ ] No hard-coded poll interval remains in `Cachet.ArchiveSync.Web` (SC-12)
- [ ] Each poll loop stops on dispose and on 401 (SC-5, V9)
- [ ] §5 column and procedures exist; `task_queue_get`'s change is additive only; mirrors are updated
- [ ] §9 key present in Web `appsettings.json`
- [ ] §11: no search term, `request_parameters`, file path, or cookie value in any new log line
- [ ] Existing CAS-022 / CAS-023 tests pass unchanged (§13)
- [ ] No §4 out-of-scope changes (no Task Manager list screen, no cancel endpoint, no Worker change, no
      change to the results endpoints' ownership scope)

### Verification Report Format
The reviewer writes `CAS-030.review.md` alongside this spec, with: overall verdict, per-SC verdict
table, security gap list, and scope violations.

## Implementation Log

**Completed:** 2026-09-24
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 77.1% overall (warn: TaskQueueRepository.cs 51% file-wide, CAS-030 methods 76/78 lines; TaskSummaryResponse.cs 66.7%, 3 measurable lines) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Core/Dtos/AcknowledgeTaskOutcome.cs` | New: SP outcome enum (0/1/2) |
| `Core/Dtos/TaskListItem.cs` | New: GET /tasks item |
| `Core/Dtos/TaskDetailResponse.cs` | New: GET /tasks/{id} body + fixed term/extension projection |
| `Core/Dtos/TaskSummaryResponse.cs` | New: summary body/row |
| `Core/Dtos/TaskQueueListRow.cs` | New: task_queue_lst row |
| `Core/Dtos/TaskQueueRow.cs` | Add ResultAcknowledgedAt |
| `Core/Interfaces/ITaskQueueRepository.cs` | Add ListAsync/GetSummaryAsync/AcknowledgeAsync |
| `Core/Interfaces/ITaskManagerService.cs` | Add list/get/summary/acknowledge |
| `Api/Repositories/TaskQueueRepository.cs` | Dapper calls for 3 new SPs |
| `Api/Services/TaskManagerService.cs` | Ownership-scoped list/get/summary/acknowledge |
| `Api/Controllers/TaskManagerController.cs` | 4 endpoints per §6 |
| `Web/TaskPollingOptions.cs` | New: Worker:TaskManagerPollIntervalSeconds [Range(5,60)], default 10 |
| `Web/Program.cs` | Bind + ValidateOnStart TaskPollingOptions |
| `Web/appsettings.json` | Add Worker:TaskManagerPollIntervalSeconds=10 |
| `Web/Components/Layout/TaskManagerBadge.razor` | New: §8.1 badge |
| `Web/Components/Layout/TaskManagerBadge.razor.css` | New: badge styles |
| `Web/Components/Layout/MainLayout.razor` | Place badge in header |
| `Web/Components/Pages/KeywordSearch.razor` | §8.2 reload-on-open, deep link, acknowledge, config interval |
| `Web/Components/Pages/ODriveSyncContentScan.razor` | §8.3 same + resume Pending sync actions |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `Api.IntegrationTests/TaskManagerControllerTests` | `Test_SC1_ListTasks_ReturnsOnlyCallersTasksNewestFirst` | SC-1 |
| `Api.IntegrationTests/TaskManagerControllerTests` | `Test_SC2_Summary_CountsOnlyOwnActiveAndUnacknowledgedTerminal` | SC-2 |
| `Api.IntegrationTests/TaskManagerControllerTests` | `Test_SC3_Acknowledge_ClearsTaskFromSummary` | SC-3 |
| `Api.Tests/TaskManagerServiceTests` | `Test_SC4_GetTask_ProjectsSearchParametersOnly` | SC-4 |
| `Web.Tests/TaskManagerBadgeTests` | `Test_SC5_Badge_RendersImmediatelyPollsAndStopsOnDispose` | SC-5 |
| `Web.Tests/TaskManagerBadgeTests` | `Test_SC6_Badge_TerminalTransitionShowsAttentionState` | SC-6 |
| `Web.Tests/KeywordSearchPageTests` | `Test_SC7_KeywordSearch_ReloadsCompletedTaskWithoutResubmitting` | SC-7 |
| `Web.Tests/KeywordSearchPageTests` | `Test_SC8_KeywordSearch_ReloadsRunningTaskAndAutoPopulatesOnCompletion` | SC-8 |
| `Web.Tests/KeywordSearchPageTests + ODriveSyncPageTests` | `Test_SC9_DeepLink_PopulatesNamedTask` | SC-9 |
| `Web.Tests/ODriveSyncPageTests` | `Test_SC10_ODriveSync_ReloadsScanAndResumesPendingSyncActions` | SC-10 |
| `Web.Tests/KeywordSearchPageTests + ODriveSyncPageTests` | `Test_SC11_Screens_AcknowledgeOnlyOwnRenderedTerminalTaskOnce` | SC-11 |
| `Web.Tests/TaskPollingOptionsTests` | `Test_SC12_PollInterval_BoundFromConfiguration` | SC-12 |
| `Api.Tests/TaskManagerControllerTests` | `Test_V1_ListTasks_InvalidPaging_Returns400` | V-1 |
| `Api.Tests/TaskManagerControllerTests` | `Test_V2_ListTasks_UnknownTypeOrStatus_Returns400` | V-2 |
| `Api.IntegrationTests/TaskManagerControllerTests (+ Api.Tests service)` | `Test_V3_OtherUsersTask_Returns404IdenticalToMissing` | V-3 |
| `Api.IntegrationTests/TaskManagerControllerTests (+ Api.Tests service)` | `Test_V4_AcknowledgeNonTerminalTask_Returns409` | V-4 |
| `Api.IntegrationTests/TaskManagerControllerTests` | `Test_V5_AcknowledgeTwice_TimestampUnchanged` | V-5 |
| `Api.IntegrationTests/TaskManagerControllerTests` | `Test_V6_TaskEndpoints_Unauthenticated_Return401` | V-6 |
| `Web.Tests/KeywordSearchPageTests` | `Test_V7_DeepLinkUnknownOrWrongTypeTask_ShowsNotFoundNotice` | V-7 |
| `Web.Tests/TaskPollingOptionsTests` | `Test_V8_PollIntervalOutOfRange_FailsValidation` | V-8 |
| `Web.Tests/TaskManagerBadgeTests + both page tests` | `Test_V9_PollReceives401_StopsPollingAndShowsSessionExpired` | V-9 |
| `Web.Tests/TaskManagerBadgeTests` | `Test_V10_TransientPollFailure_KeepsLastStateAndRetries` | V-10 |

### Migration
`20260924_130000__CAS-030-task-completion-notification.sql`
