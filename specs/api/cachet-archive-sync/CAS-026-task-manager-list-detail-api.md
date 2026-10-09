---
id: CAS-026
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-24
priority: P1
related_specs: [CAS-003, CAS-004, CAS-006, CAS-009, CAS-016, CAS-027]
touches:
  database: true
  api: true
  ui: false
  worker: false
  b2: false
  config: false
  security: false
migrations: [20260924_150000__CAS-026-task-manager-list-detail-api.sql]
---

# CAS-026 — Task Manager List, Detail, and Badge-Count API

Implements the read-side API surface FR-19 (Task Manager listing), FR-20 (durable, re-fetchable task
state), and FR-22 (completion badge/indicator) require. Companion to `CAS-027` (Task Manager screen),
which is this endpoint set's sole consumer. WBS coverage: `CACHET_API_Design_Specification_v1.md` §7
flagged that the WBS has no dedicated "Task Manager API" row (rows 15–20 cover Auth/Keyword
Search/O-Drive Sync/Quarantine/Audit Log/Reporting only) and recommended adding one — this spec is that
work, tracked against WBS Task 25's screen via `CAS-027`.

---

## §0 Scope Correction (read this first)

**`dbo.task_queue_get` is not actually BLOCKED — the code's own comments are stale.** `CAS-016` §1/§14,
`TaskQueueRow.cs`'s XML doc, and `TaskQueueRepository.cs`'s `GetAsync` XML doc all say `dbo.task_queue_get`
"does not exist yet" / is "BLOCKED." A direct read of
`Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_get.sql` and its introducing migration
(`Database/migrations/20260922_173000__CAS-006-keyword-search-api-addendum.sql`) shows the procedure
**was already added** by a `CAS-006` addendum, and `TaskQueueRepository.GetAsync` is already wired to
call it. The BLOCKED comments were simply never updated after that addendum shipped. This spec's
`GET /tasks/{taskId}` (§6.2) reuses `ITaskQueueRepository.GetAsync` exactly as it exists today — no
database change is needed for that one lookup. Flagged here as a documentation-accuracy note for the
implementer to clean up the three stale comments while touching these files (small, in-scope
housekeeping — not a scope expansion), not because it changes this spec's design.

**`POST /tasks/{taskId}/cancel` is deliberately excluded from this spec (§4).**
`CACHET_API_Design_Specification_v1.md` §7.3 already designs this endpoint in detail and confirms it
client-side (2026-09-09), but two things keep it out of this pair:
1. `CACHET_UIUX_Design_Specification_v1.md` §5.2's Task Manager row-affordance table — the authoritative
   description of what this screen's rows actually do — lists only **View Results** (Completed) and
   **View Details** (Failed). No Cancel affordance is drawn for `Queued`/`Running` rows. `CAS-027`'s own
   task brief (front-end spec scope) likewise asks only for the list, View Results navigation, the
   badge, and FR-22a messaging — not cancellation.
2. The cancellation model itself is cooperative (§7.3: "the worker checks a cancellation flag at safe
   checkpoints") and depends on `is_cancel_requested` (already a column on `dbo.task_queue`, added by
   `CAS-003`) being read somewhere in the Worker's execution loop. A grep of
   `src/Cachet.ArchiveSync.Worker/*.cs` (`KeywordSearchTaskProcessor.cs`, `ContentScanTaskProcessor.cs`,
   and the three sync processors) confirms **zero references to `is_cancel_requested`** anywhere —
   nothing currently checks it. Shipping a `POST /tasks/{taskId}/cancel` endpoint that flips the column
   with no Worker code that ever reads it back would be a dead affordance for `Running` tasks (a
   `Queued` task can still be meaningfully cancelled by simply deleting/never-claiming it, but that's a
   narrower capability than the full endpoint implies). Building it correctly requires touching the
   Worker's five task processors, which is out of this API+UI pair's project-boundary scope (Karpathy
   P9) and not requested by the task brief. Tracked as a known, deliberate gap — see §15.

---

## §1 Summary

Adds three read endpoints and one write endpoint to the existing `TaskManagerController`
(`src/Cachet.ArchiveSync.Api/Controllers/TaskManagerController.cs`, currently hosting only `CAS-016`'s
`POST /tasks/{taskId}/restart`):

- `GET /tasks` — the authenticated user's own tasks, newest-first, optionally filtered by status,
  paginated (FR-19).
- `GET /tasks/{taskId}` — single-task detail including `resultDetail` for a Failed row's **View
  Details** affordance (FR-19, UI-UX §5.2).
- `GET /tasks/summary` — two counts (`inProgressCount`, `unviewedCompletedCount`) backing the Task
  Manager header badge (FR-22). No existing endpoint or column tracks "has this user seen this
  completed/failed task yet," so this spec adds one nullable timestamp column
  (`dbo.users.task_manager_last_viewed_at`) and computes "unviewed" as "completed/failed after that
  timestamp" (§5, §15 — a deliberate account-level approximation, not per-task read receipts).
- `POST /tasks/mark-viewed` — sets that timestamp to now; called when the Task Manager screen opens, so
  the badge clears the tasks just shown without needing per-row state.

All four endpoints are scoped to the authenticated user's own tasks only, matching the existing,
client-confirmed scoping decision for this module (`CACHET_API_Design_Specification_v1.md` §7.1,
confirmed 2026-09-09: "each user sees only their own submitted tasks").

---

## §2 User Story

As a **TraceCounts reviewer**, I want the application to remember every task I've submitted — including
ones I submitted before closing my browser — and tell me at a glance how many are still running or have
finished since I last checked, so that I can safely leave the app and come back later without losing
track of my work (FR-19, FR-20, FR-22).

---

## §3 In-Scope

- `GET /tasks` (own tasks, optional `status` filter, `page`/`pageSize`, newest-first by `submitted_at`).
- `GET /tasks/{taskId}` (own task only — see §7 V-series for the not-owner/not-found handling; reuses
  `ITaskQueueRepository.GetAsync` + `IUserRepository.GetByIdAsync` for the `submittedBy` username, no
  new stored procedure needed for this one, §0).
- `GET /tasks/summary` (`inProgressCount`, `unviewedCompletedCount`).
- `POST /tasks/mark-viewed` (resets `task_manager_last_viewed_at` to now for the calling user).
- New stored procedure `dbo.task_queue_lst` (list, paginated, joined to `dbo.users` for username —
  mirrors `dbo.audit_log_lst`'s exact shape/convention).
- New stored procedure `dbo.task_queue_summary_get` (the two badge counts).
- New stored procedure `dbo.users_mark_task_manager_viewed` (single-column timestamp update).
- New column `dbo.users.task_manager_last_viewed_at DATETIME2(3) NULL`.
- Fixing the three stale "BLOCKED" XML-doc comments identified in §0.

---

## §4 Out-of-Scope

- `POST /tasks/{taskId}/cancel` — see §0. A future spec, scoped jointly with the Worker changes needed
  to make cancellation of a `Running` task actually take effect.
- Any Worker code change (`is_cancel_requested` remains unread by any task processor after this spec).
- The Task Manager screen itself, the header button/badge UI, and the `?taskId=` deep-link additions to
  `KeywordSearch.razor`/`ODriveSyncContentScan.razor` — all `CAS-027`'s scope.
- Per-task "viewed" tracking (e.g., a `viewed_at` column on `task_queue` itself, or per-row read
  receipts) — the account-level `task_manager_last_viewed_at` approximation is deliberately simpler and
  proportionate to this project's confirmed 3–4 user scale (§15).
- Any change to `dbo.task_queue_ins`/`_claim`/`_upd`/`_restart_ins` or the `TaskManagerController`'s
  existing `Restart` action (`CAS-016`) — all reused as-is.
- The still-open BRD §12.4 "Web caching layer mechanism" item — this spec's own `GET /tasks*` responses
  are inherently always-fresh, live queries (a task list has no "repeat view from cache" semantics to
  resolve), so §12.4 does not block this spec. `CAS-027`'s consumption of `CAS-006`'s/`CAS-007`'s
  existing results endpoints inherits whatever §12.4 eventually resolves for those, unchanged by this
  spec.

---

## §5 Database Objects

### Tables / Columns

| Object | Change | Notes |
|---|---|---|
| `dbo.users` | Add `task_manager_last_viewed_at DATETIME2(3) NULL` | No default — `NULL` means "never opened Task Manager," treated as "everything completed/failed so far is unviewed" by `dbo.task_queue_summary_get` (§7 V4). Mirrors the existing `session_token_version` precedent (`CAS-002`) of adding a single targeted column to `dbo.users` via its own migration, not a schema rewrite. |

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `task_queue_lst` | dbo | lst | New. Paginated, filtered by `@submitted_by_user_id` (required) and optional `@status`, joined to `dbo.users` for `submitted_by`. Mirrors `dbo.audit_log_lst`'s exact structure (validate → `@total_count` OUTPUT via a `COUNT(*)` SELECT → paged `OFFSET/FETCH` SELECT). |
| `task_queue_summary_get` | dbo | get | New. Returns `in_progress_count` (`Queued`+`Running`) and `unviewed_completed_count` (`Completed`+`Failed` with `completed_at > task_manager_last_viewed_at`, or all of them if that column is `NULL`) for one user. |
| `users_mark_task_manager_viewed` | dbo | upd | New. Sets `task_manager_last_viewed_at = SYSUTCDATETIME()` for one `user_id`. Single-purpose rather than reusing the general `dbo.users_upd` (per `CAS-002`'s own precedent of adding narrow, purpose-specific update procedures — e.g. `users_increment_session_token_version` — instead of routing every single-column change through the general update procedure). |
| `task_queue_get` | dbo | get | **Unchanged** — already exists (§0). `GET /tasks/{taskId}` reuses it via the existing `ITaskQueueRepository.GetAsync`. |

### Migration Strategy

Idempotent timestamped migration:
`Database/migrations/20260924_150000__CAS-026-task-manager-list-detail-api.sql`, containing:
1. `ALTER TABLE dbo.users ADD task_manager_last_viewed_at DATETIME2(3) NULL;`
2. `CREATE OR ALTER PROCEDURE dbo.task_queue_lst` (body below).
3. `CREATE OR ALTER PROCEDURE dbo.task_queue_summary_get` (body below).
4. `CREATE OR ALTER PROCEDURE dbo.users_mark_task_manager_viewed` (body below).

Mirror updates: `Database/CachetArchiveSync/Entities/dbo.users.sql` (add the column) and three new files
under `Database/CachetArchiveSync/Stored-Procedures/`: `dbo.task_queue_lst.sql`,
`dbo.task_queue_summary_get.sql`, `dbo.users_mark_task_manager_viewed.sql`.

**`dbo.task_queue_lst` body:**
```sql
CREATE OR ALTER PROCEDURE [dbo].[task_queue_lst]
    @submitted_by_user_id INT
   ,@status               NVARCHAR(20) = NULL
   ,@page                 INT          = 1
   ,@page_size            INT          = 50
   ,@total_count          INT          OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @submitted_by_user_id IS NULL
        BEGIN RAISERROR(N'@submitted_by_user_id is required.', 16, 1); RETURN; END
    IF @page IS NULL OR @page < 1
        BEGIN RAISERROR(N'@page must be >= 1.', 16, 1); RETURN; END
    IF @page_size IS NULL OR @page_size < 1
        BEGIN RAISERROR(N'@page_size must be >= 1.', 16, 1); RETURN; END
    IF @status IS NOT NULL AND @status NOT IN (N'Queued', N'Running', N'Completed', N'Failed', N'Cancelled')
        BEGIN RAISERROR(N'@status must be a recognized task_queue status value.', 16, 1); RETURN; END

    SELECT @total_count = COUNT(*)
    FROM [dbo].[task_queue] AS t
    WHERE t.submitted_by_user_id = @submitted_by_user_id
      AND (@status IS NULL OR t.status = @status);

    SELECT
        t.task_queue_id
       ,t.task_type
       ,u.username AS submitted_by
       ,t.submitted_at
       ,t.status
    FROM [dbo].[task_queue] AS t
    INNER JOIN [dbo].[users] AS u
        ON t.submitted_by_user_id = u.user_id
    WHERE t.submitted_by_user_id = @submitted_by_user_id
      AND (@status IS NULL OR t.status = @status)
    ORDER BY t.submitted_at DESC
    OFFSET (@page - 1) * @page_size ROWS
    FETCH NEXT @page_size ROWS ONLY;
END
GO
```
**Design note — "start time" maps to `submitted_at`, not `started_at`:** FR-19 requires a "start time"
column across all four statuses, including `Queued` (which has no `started_at` yet — that column stays
`NULL` until a worker claims the row). `CACHET_API_Design_Specification_v1.md` §7.1's own worked example
response uses `submitted_at`'s value for its `startTime` field. This spec follows that precedent:
`startTime` = `submitted_at` (when the user's request entered the queue), not `started_at` (when a
worker began executing it) — the latter would leave every `Queued` row's column blank, which the UI/UX
spec's column list doesn't describe as a valid empty state. Flagged explicitly since the table's own
column names (`submitted_at` vs. `started_at`) make this genuinely ambiguous on a first read.

**`dbo.task_queue_summary_get` body:**
```sql
CREATE OR ALTER PROCEDURE [dbo].[task_queue_summary_get]
    @submitted_by_user_id INT
AS
BEGIN
    SET NOCOUNT ON;

    IF @submitted_by_user_id IS NULL
        BEGIN RAISERROR(N'@submitted_by_user_id is required.', 16, 1); RETURN; END

    DECLARE @last_viewed_at DATETIME2(3);
    SELECT @last_viewed_at = task_manager_last_viewed_at
    FROM [dbo].[users]
    WHERE user_id = @submitted_by_user_id;

    SELECT
        ISNULL(SUM(CASE WHEN status IN (N'Queued', N'Running') THEN 1 ELSE 0 END), 0) AS in_progress_count
       ,ISNULL(SUM(CASE WHEN status IN (N'Completed', N'Failed')
                         AND (@last_viewed_at IS NULL OR completed_at > @last_viewed_at)
                    THEN 1 ELSE 0 END), 0)                                            AS unviewed_completed_count
    FROM [dbo].[task_queue]
    WHERE submitted_by_user_id = @submitted_by_user_id;
END
GO
```
**Post-implementation code-review fix (2026-09-24):** the body above supersedes this spec's original
draft, which computed the two counts as independent `SELECT COUNT(*) FROM task_queue WHERE ...`
subqueries. Under `READ COMMITTED`, those two independent reads could observe a status transition
between them (e.g. a Worker's `dbo.task_queue_upd` flipping `Running` → `Completed` mid-query),
transiently dropping that row from both counts. A single conditional-aggregation `SELECT` reads every
row it touches in one consistent pass, closing that gap; `ISNULL(...,0)` guards against `SUM` returning
`NULL` when a user has zero `task_queue` rows. Applied directly to the not-yet-deployed migration file
rather than a follow-up addendum, since no environment had run the original body yet.

**`dbo.users_mark_task_manager_viewed` body:**
```sql
CREATE OR ALTER PROCEDURE [dbo].[users_mark_task_manager_viewed]
    @user_id     INT
   ,@updated_by  NVARCHAR(50)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @user_id IS NULL
        BEGIN RAISERROR(N'@user_id is required.', 16, 1); RETURN; END
    IF @updated_by IS NULL OR LTRIM(RTRIM(@updated_by)) = N''
        BEGIN RAISERROR(N'@updated_by is required.', 16, 1); RETURN; END

    UPDATE [dbo].[users]
    SET task_manager_last_viewed_at = SYSUTCDATETIME()
       ,updated_by                  = @updated_by
       ,updated_at                  = SYSUTCDATETIME()
    WHERE user_id = @user_id;
END
GO
```

---

## §6 API Endpoints

All four hosted on the existing `TaskManagerController` (`[Route("")]`), alongside `CAS-016`'s
`Restart` action.

### §6.1 `GET /tasks`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`
**Async:** No — this is a synchronous read of already-durable state, not a job submission.

**Request:** query params `status` (optional: `Queued|Running|Completed|Failed|Cancelled`), `page`
(default 1), `pageSize` (default 50).

**Response 200:**
```json
{
  "items": [
    {
      "taskId": 100234,
      "type": "KeywordSearch",
      "submittedBy": "m.alvarez",
      "startTime": "2026-09-24T14:05:03Z",
      "status": "Running"
    }
  ],
  "page": 1, "pageSize": 50, "totalCount": 1
}
```
`type` values: `KeywordSearch | ContentScan | SyncCopy | SyncUpdate | SyncPurge` (matches
`CK_task_queue_task_type`). `status` values: `Queued | Running | Completed | Failed | Cancelled`
(matches `CK_task_queue_status` — `Cancelled` is returned if present even though no code path in this
codebase can currently produce it, §0/§4, so a future restart/cancel change doesn't require a contract
change here).

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Success (including zero rows — an empty `items` array, not a 404) |
| 401 | Missing or invalid session |
| 422 | `status` is not a recognized value, or `page`/`pageSize` out of range (§7 V1–V3) |

### §6.2 `GET /tasks/{taskId}`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`
**Async:** No.

**Response 200:**
```json
{
  "taskId": 100234,
  "type": "KeywordSearch",
  "submittedBy": "m.alvarez",
  "startTime": "2026-09-24T14:05:03Z",
  "status": "Failed",
  "resultDetail": "B2 authorization failed during cross-check (see Audit Log)."
}
```
`resultDetail` reuses `dbo.task_queue.result_detail` (the same value the audit log's `result/detail`
carries for the same event) — no separate error-message store, per UI-UX §5.2.

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Success |
| 401 | Missing or invalid session |
| 403 | `taskId` exists but belongs to a different user (§7 V4 — never `404`, to stay consistent with `CAS-016`'s "don't leak whether the row exists" posture for another user's resource, though here there's no status to hide, just ownership) |
| 404 | `taskId` does not exist |

### §6.3 `GET /tasks/summary`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`
**Async:** No.

**Response 200:**
```json
{ "inProgressCount": 2, "unviewedCompletedCount": 1 }
```

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Success (zero counts is a normal, not-error response) |
| 401 | Missing or invalid session |

### §6.4 `POST /tasks/mark-viewed`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`
**Async:** No.
**Request body:** none.

**Response 200:** `{ "markedViewedAt": "2026-09-24T14:12:00Z" }`

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Success |
| 401 | Missing or invalid session |

| Endpoint | Field | Before | After | Breaking? |
|----------|-------|--------|-------|-----------|
| `GET /tasks` | (new) | N/A | New endpoint | No |
| `GET /tasks/{taskId}` | (new) | N/A | New endpoint | No |
| `GET /tasks/summary` | (new) | N/A | New endpoint | No |
| `POST /tasks/mark-viewed` | (new) | N/A | New endpoint | No |

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `GET /tasks`'s `status`, if supplied, must be one of `Queued\|Running\|Completed\|Failed\|Cancelled` | 422 `{ "error": "..." }` | `Test_V1_ListRejectsUnrecognizedStatus` |
| V2 | `GET /tasks`'s `page` must be `>= 1` | 422 | `Test_V2_ListRejectsPageBelowOne` |
| V3 | `GET /tasks`'s `pageSize` must be between 1 and 200 (matching `CAS-009`'s `MaxPageSize` precedent) | 422 | `Test_V3_ListRejectsPageSizeOutOfRange` |
| V4 | `GET /tasks/{taskId}` for a `taskId` that exists but does not belong to the caller returns `403`, never `200` with another user's data | 403 | `Test_V4_GetReturns403ForNonOwnedTask` |
| V5 | `GET /tasks/{taskId}` for a nonexistent `taskId` returns `404` | 404 | `Test_V5_GetReturns404ForUnknownTask` |
| V6 | `GET /tasks` and `GET /tasks/{taskId}` never return another user's rows, regardless of filter values supplied | — (structural) | `Test_V6_ListNeverReturnsAnotherUsersTasks` |
| V7 | `GET /tasks/summary`'s `unviewedCompletedCount` counts every `Completed`/`Failed` row when `task_manager_last_viewed_at` is `NULL` (never-viewed user), not zero | — | `Test_V7_SummaryTreatsNullLastViewedAsNeverViewed` |
| V8 | `POST /tasks/mark-viewed` updates only the calling user's own `task_manager_last_viewed_at`, never another user's | — | `Test_V8_MarkViewedScopedToCallingUser` |

---

## §8 UI / UX

N/A — `touches.ui: false`. Consumed entirely by `CAS-027`.

---

## §9 Configuration Changes

None. No new `appsettings.json` keys.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `GET /tasks` returns only the authenticated user's own tasks, newest-first by `submitted_at`, correctly paginated | `Test_SC1_ListReturnsOwnTasksNewestFirstPaginated` |
| SC-2 | `GET /tasks?status=Failed` returns only `Failed` rows for that user | `Test_SC2_ListFiltersByStatus` |
| SC-3 | `GET /tasks/{taskId}` returns `resultDetail` for a `Failed` task and omits it (`null`) for a non-`Failed` task | `Test_SC3_GetReturnsResultDetailOnlyWhenPresent` |
| SC-4 | `GET /tasks/summary`'s `inProgressCount` equals the caller's `Queued`+`Running` row count at query time | `Test_SC4_SummaryInProgressCountMatchesQueuedPlusRunning` |
| SC-5 | After `POST /tasks/mark-viewed`, a subsequent `GET /tasks/summary` returns `unviewedCompletedCount: 0` for tasks completed before the mark-viewed call, and correctly counts a task completed *after* it | `Test_SC5_MarkViewedResetsUnviewedCountForPriorCompletions` |
| SC-6 | A task submitted, then the browser session ended, then a fresh session's `GET /tasks`/`GET /tasks/{taskId}` still returns its current state (FR-20 — durability, not session-scoped) | `Test_SC6_TaskStateSurvivesAcrossSessions` |

---

## §11 Telemetry

```csharp
_logger.LogInformation(
    "Task list requested by user {UserId}: status={Status}, page={Page}, pageSize={PageSize}, returned={ReturnedCount}, totalCount={TotalCount}",
    actingUserId, status, page, pageSize, items.Count, totalCount);
_logger.LogInformation("Task manager marked viewed by user {UserId} at {MarkedViewedAt}", actingUserId, markedViewedAt);
```
- Never log: the B2 application key, a session token/cookie value, `request_parameters`'/`resultDetail`
  raw contents beyond what the audit log already records — this spec's log lines carry only ids, enum
  values, and counts.

---

## §12 Security Notes

`touches.security: false` — no new claim type, policy, or session behavior. Documented anyway per this
project's B2/audit-trail rules.

### B2 Credential Handling
None of these four endpoints touches B2 in any way — they read `dbo.task_queue`/`dbo.users` columns
that were already durably written by other modules' existing, server-side-brokered B2 calls. No B2
application key is read, returned, or logged (FR-03 unaffected).

### Audit Trail
**None of these four endpoints write to `dbo.audit_log`.** FR-23 names four audited action categories
("every search execution, quarantine action, and permanent-delete action" — `CACHET_NFR_Analysis_v1.md`
target #9) plus this codebase's own precedent of leaving other pure reads unaudited (`GET /quarantine`,
`GET /audit-log` itself, `CAS-008`/`CAS-009`). `GET /tasks`, `GET /tasks/{taskId}`, and
`GET /tasks/summary` are reads; `POST /tasks/mark-viewed` is a write, but it mutates only a per-user
UI-state bookkeeping timestamp with no file/task/security consequence — it fits none of FR-23's four
categories and is not itself a "action taken against a file or task." Not writing an audit row for it
matches the established precedent rather than inventing a new audited-action category the BRD doesn't
name.

### Session / Auth Requirements
All four endpoints require `[Authorize(Policy = "CachetAuthenticated")]` — the same policy every other
read endpoint in this codebase uses (no `CachetFullAccess` requirement; reading your own task list/badge
is not a B2-write-adjacent action). Ownership is enforced server-side in every stored procedure/query
(`@submitted_by_user_id`/`@user_id` bound to the authenticated caller's own claim, never client-supplied)
— matches `CAS-016`'s "enforced inside the transaction, not merely the controller" posture, applied here
to `SELECT` scoping rather than a state transition.

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `TaskListServiceTests` | `Test_V1_ListRejectsUnrecognizedStatus` | V1 |
| `TaskListServiceTests` | `Test_V2_ListRejectsPageBelowOne` | V2 |
| `TaskListServiceTests` | `Test_V3_ListRejectsPageSizeOutOfRange` | V3 |
| `TaskListServiceTests` | `Test_SC1_ListReturnsOwnTasksNewestFirstPaginated` (repository mocked) | SC-1 |
| `TaskListServiceTests` | `Test_SC2_ListFiltersByStatus` | SC-2 |
| `TaskDetailServiceTests` | `Test_V4_GetReturns403ForNonOwnedTask` | V4 |
| `TaskDetailServiceTests` | `Test_V5_GetReturns404ForUnknownTask` | V5 |
| `TaskDetailServiceTests` | `Test_SC3_GetReturnsResultDetailOnlyWhenPresent` | SC-3 |
| `TaskSummaryServiceTests` | `Test_V7_SummaryTreatsNullLastViewedAsNeverViewed` | V7 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `TaskQueueListRepositoryTests` (real/test SQL Server) | `Test_SC1_ListReturnsOwnTasksNewestFirstPaginated` | SC-1 |
| `TaskQueueListRepositoryTests` | `Test_V6_ListNeverReturnsAnotherUsersTasks` | V6 |
| `TaskManagerControllerTests` | `Test_SC4_SummaryInProgressCountMatchesQueuedPlusRunning` | SC-4 |
| `TaskManagerControllerTests` | `Test_SC5_MarkViewedResetsUnviewedCountForPriorCompletions` | SC-5, V8 |
| `TaskManagerControllerTests` | `Test_SC6_TaskStateSurvivesAcrossSessions` | SC-6 |
| `TaskManagerControllerTests` | `Test_V4_GetReturns403ForNonOwnedTask` | V4 |
| `TaskManagerControllerTests` | `Test_V5_GetReturns404ForUnknownTask` | V5 |

### Coverage Target
Changed files (`TaskManagerController.cs`'s new actions, the new list/summary/mark-viewed
service/repository methods) must reach ≥ 85% line coverage, matching this project's standard.

---

## §14 Rollout

1. Deploy database migration first: `20260924_150000__CAS-026-task-manager-list-detail-api.sql`.
2. Deploy Api (new controller actions, service, repository methods). No Web or Worker deploy is required
   by this spec alone — `CAS-027` is the consumer and deploys separately once ready.
3. Rollback: revert the Api commits (pure additions, no existing endpoint's behavior changes). Database
   rollback (only if genuinely needed post-deploy): drop the three new procedures, then
   `ALTER TABLE dbo.users DROP COLUMN task_manager_last_viewed_at;` — safe only if no row has a
   non-`NULL` value in that column yet (this project's migrations are append-only, so a real rollback in
   an already-migrated environment is a new forward migration, not an edit to this file).

---

## §15 Open Questions — Resolved 2026-09-24 (owner: harikesh@scientificvision.net)

- [x] **`POST /tasks/{taskId}/cancel` remains unbuilt (§0/§4).** Accepted as designed — deliberately out
      of scope for this API+UI pair since the UI/UX spec's row-affordance table doesn't request it and
      the Worker's cooperative-cancellation check is unbuilt. Tracked as a future spec pairing the
      endpoint with the Worker-side `is_cancel_requested` check across all five task processors. No
      change to §0/§4 from the draft.
- [x] **Account-level "last viewed" vs. per-task read receipts (§4).** Accepted as designed — one
      timestamp per user rather than a `viewed_at` per task row, proportionate to this project's
      confirmed 3–4 user scale. Revisit only if a real usability complaint surfaces (e.g., a user's
      Task Manager visit for one task silently clearing the badge for another, unviewed one) — a real
      but, at this scale, low-probability edge case. No change to §5/§6 from the draft.
- [x] **BRD §12.4 Web caching layer mechanism** — confirmed not blocking for this spec (§4); this
      module's endpoints are inherently always-fresh reads with no cache semantics of their own. No
      change from the draft.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code, on behalf of R. Devarajan (TraceCounts) | 2026-09-24 | Draft |
| Owner | harikesh@scientificvision.net | 2026-09-24 | §15 open questions reviewed and accepted as documented design trade-offs, no changes required — `status: ready` |
| Reviewer | | | Pending |

---

## §17 Graph Impact

> `graphify update .` has not been run against the application source tree in this repo (the existing
> `graphify-out/` build, referenced by `CAS-004` §17, covers only the BRD/WBS documentation corpus).
> Per `CLAUDE.md`'s fallback rule, this section is a manual read of `src/`, `Database/`, and the related
> specs listed in this spec's frontmatter.

### God Nodes in Blast Radius
- `dbo.task_queue` — high fan-in (every async module writes through it: Keyword Search, O-Drive Sync
  scan/sync actions, Task Manager). This spec adds one new read procedure (`task_queue_lst`) and reuses
  the existing `task_queue_get`; it does not modify the table's write paths (`_ins`/`_claim`/`_upd`/
  `_restart_ins`).
- `ITaskQueueRepository` — already a high-fan-in interface (`CAS-016` §4 flagged this). This spec adds
  one new method (`ListAsync`) without changing any existing method's signature.
- `dbo.users` — referenced by `TaskQueueRepository` (FK), `AuditLogRepository` (username join), and the
  auth/session stack. This spec adds one nullable column, additive-only.

### Communities / Modules Affected
- **Task Manager** (this spec's primary subject — first read/list API surface for the module; `CAS-016`
  built only the restart write path).
- **Primarily Api, with one minimal, justified Web touch.** No Worker or Core-interface-*breaking*
  change (`ITaskManagerService`'s existing `RestartAsync` member is untouched; new members are
  additive). **Correction (post-implementation, flagged by independent review):** this section
  originally read "Api project only. No Web ... change," which understated the actual blast radius —
  `IUserRepository` (Core) gained `MarkTaskManagerViewedAsync`, and since that interface has two
  implementers (`Api`'s real Dapper repository and `Web`'s session-revocation-only stub,
  `src/Cachet.ArchiveSync.Web/Data/UserRepository.cs`), Web's stub needed a matching
  `NotSupportedException` member added to keep compiling — an unavoidable, one-line, in-pattern
  consequence of one shared interface having two implementers, not a Web behavior change (see §5's
  Files Changed table and the Implementation Log, which already disclosed this).

### Surprising Connections
- `dbo.task_queue_get`'s "BLOCKED" status turned out to be stale documentation, not an actual gap (§0) —
  a genuine surprise worth flagging so the implementer doesn't waste time re-authoring a procedure that
  already exists.
- The badge (FR-22) has no natural home in the existing schema — closing it required a new column on
  `dbo.users`, a table otherwise owned by the Auth module (`CAS-002`/`CAS-003`), not Task Manager. This
  is a small, deliberate cross-module touch (§5), not a sign of a deeper coupling problem.

### Graphify Queries Run
```
None — graphify not yet vendored for application source in this repo; analysis done via manual
grep/read of src/, Database/, and CAS-003/CAS-004/CAS-006/CAS-009/CAS-016's specs, per CLAUDE.md's
stated fallback.
```

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test.
- [ ] All §7 Validations are implemented and tested.
- [ ] All §6 API endpoints exist with correct verb/route/auth, hosted on the existing
      `TaskManagerController`.
- [ ] §12 Security Notes are implemented as described — no B2 key exposure, no audit row written by any
      of these four endpoints (confirm this is a deliberate, documented choice, not an omission),
      `CachetAuthenticated` enforced on all four, ownership scoped server-side via the authenticated
      claim only.
- [ ] §5 Database objects exist exactly as specified: the new `dbo.users` column, the three new stored
      procedures, and confirmation that `dbo.task_queue_get`/`dbo.task_queue_ins`/`_claim`/`_upd`/
      `_restart_ins` are genuinely untouched.
- [ ] §0's three stale "BLOCKED" comments were corrected (or confirm they were left and flag why).
- [ ] §17 Graph Impact matches actual code changes — no unintended edits inside the Worker or Web
      projects.
- [ ] No out-of-scope changes from §4 were made, specifically: no `POST /tasks/{taskId}/cancel`
      endpoint, no Worker code touching `is_cancel_requested`.

### Verification Report Format
Per template: `CAS-026.review.md` alongside this spec — overall verdict, per-SC verdict table, security
gap list, scope violations.

## Implementation Log

**Completed:** 2026-09-24
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 74.8% overall -- TaskListService.cs 100%, TaskDetailService.cs 100%, TaskBadgeService.cs 100%, TaskManagerController.cs 88.9%, ITaskDetailService.cs 66.7%, ITaskListService.cs 75%, TaskQueueRepository.cs 0%, Api UserRepository.cs 3.9%, Web UserRepository.cs (stub) 0% -- below-threshold files are Dapper repositories/interfaces covered only by Skip-marked real-DB tests, no live SQL Server in this sandbox, same pre-existing pattern as CAS-016; accepted per owner decision 2026-09-24 | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20260924_150000__CAS-026-task-manager-list-detail-api.sql` | New migration: users.task_manager_last_viewed_at column + task_queue_lst/task_queue_summary_get/users_mark_task_manager_viewed procs |
| `Database/CachetArchiveSync/Entities/dbo.users.sql` | Mirror: added task_manager_last_viewed_at column |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_lst.sql` | New mirror file |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_summary_get.sql` | New mirror file |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.users_mark_task_manager_viewed.sql` | New mirror file |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskQueueListRow.cs` | New repo row DTO for task_queue_lst |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskQueueSummaryRow.cs` | New repo row DTO for task_queue_summary_get |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskListItem.cs` | New GET /tasks item DTO |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskDetailResponse.cs` | New GET /tasks/{taskId} response DTO |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskSummaryResponse.cs` | New GET /tasks/summary response DTO |
| `src/Cachet.ArchiveSync.Core/Dtos/MarkViewedResponse.cs` | New POST /tasks/mark-viewed response DTO |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskQueueRow.cs` | Corrected stale BLOCKED XML doc (CAS-026 §0) |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskListService.cs` | New service interface + TaskListOutcome/TaskListResult |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskDetailService.cs` | New service interface + TaskDetailOutcome/TaskDetailResult |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskBadgeService.cs` | New service interface (summary + mark-viewed) |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs` | Appended ListAsync/GetSummaryAsync; corrected stale BLOCKED doc on GetAsync |
| `src/Cachet.ArchiveSync.Core/Interfaces/IUserRepository.cs` | Appended MarkTaskManagerViewedAsync |
| `src/Cachet.ArchiveSync.Api/Services/TaskListService.cs` | New: validates + calls ITaskQueueRepository.ListAsync |
| `src/Cachet.ArchiveSync.Api/Services/TaskDetailService.cs` | New: NotFound/NotOwner outcome mapping |
| `src/Cachet.ArchiveSync.Api/Services/TaskBadgeService.cs` | New: summary + mark-viewed |
| `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs` | Appended ListAsync/GetSummaryAsync Dapper impls; corrected stale BLOCKED docs |
| `src/Cachet.ArchiveSync.Api/Repositories/UserRepository.cs` | Appended MarkTaskManagerViewedAsync |
| `src/Cachet.ArchiveSync.Api/Controllers/TaskManagerController.cs` | Added GET /tasks, GET /tasks/{taskId}, GET /tasks/summary, POST /tasks/mark-viewed |
| `src/Cachet.ArchiveSync.Api/Program.cs` | DI registration for ITaskListService/ITaskDetailService/ITaskBadgeService |
| `src/Cachet.ArchiveSync.Web/Data/UserRepository.cs` | Fix: added stubbed MarkTaskManagerViewedAsync (NotSupportedException) to satisfy IUserRepository - Web build was broken without it |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `TaskListServiceTests` | `Test_V1_ListRejectsUnrecognizedStatus` | V1 |
| `TaskListServiceTests` | `Test_V2_ListRejectsPageBelowOne` | V2 |
| `TaskListServiceTests` | `Test_V3_ListRejectsPageSizeOutOfRange` | V3 |
| `TaskListServiceTests` | `Test_SC1_ListReturnsOwnTasksNewestFirstPaginated` | SC-1 |
| `TaskListServiceTests` | `Test_SC2_ListFiltersByStatus` | SC-2 |
| `TaskDetailServiceTests` | `Test_V4_GetReturns403ForNonOwnedTask` | V4 |
| `TaskDetailServiceTests` | `Test_V5_GetReturns404ForUnknownTask` | V5 |
| `TaskDetailServiceTests` | `Test_SC3_GetReturnsResultDetailOnlyWhenPresent` | SC-3 |
| `TaskBadgeServiceTests` | `Test_V7_SummaryTreatsNullLastViewedAsNeverViewed` | V7 |
| `TaskManagerControllerTests` | `Test_V4_GetReturns403ForNonOwnedTask` | V4 (HTTP) |
| `TaskManagerControllerTests` | `Test_V5_GetReturns404ForUnknownTask` | V5 (HTTP) |
| `TaskManagerControllerTests` | `Test_SC4_SummaryInProgressCountMatchesQueuedPlusRunning` | SC-4 |
| `TaskManagerControllerTests` | `Test_SC5_MarkViewedResetsUnviewedCountForPriorCompletions` | SC-5 |
| `TaskManagerControllerTests` | `Test_SC6_TaskStateSurvivesAcrossSessions` | SC-6 |
| `TaskManagerControllerTests` | `Test_V8_MarkViewedScopedToCallingUser` | V8 |
| `TaskQueueListRepositoryTests` | `Test_SC1_ListReturnsOwnTasksNewestFirstPaginated` | SC-1 (Skip: no live SQL Server) |
| `TaskQueueListRepositoryTests` | `Test_V6_ListNeverReturnsAnotherUsersTasks` | V6 (Skip: no live SQL Server) |

### Migration
`20260924_150000__CAS-026-task-manager-list-detail-api.sql`

### Post-Implementation Code-Review Fixes (2026-09-24)

`/code-review` surfaced three findings against this implementation. Two were confirmed and fixed;
one was confirmed but is out of this spec's scope:

| Finding | File | Verdict | Action |
|---|---|---|---|
| Empty-string `status` (`GET /tasks?status=`) passes `Validate()` but isn't normalized to `null` before reaching `ITaskQueueRepository.ListAsync` → `dbo.task_queue_lst`'s own `@status IS NOT NULL AND @status NOT IN (...)` guard, which `RAISERROR`s and surfaces as an unhandled 500 instead of the documented behavior | `TaskListService.cs` | CONFIRMED | Fixed — normalize `""`/`null` to `null` before the repository call (mirrors `AuditLogListService`'s existing `tabFilter` pattern). Regression test added: `Test_ListNormalizesEmptyStringStatusToNull`. |
| `dbo.task_queue_summary_get`'s two badge counts were computed as independent `SELECT COUNT(*)` subqueries with no shared snapshot — a status transition between them (e.g. a Worker's `dbo.task_queue_upd`) could transiently drop that row from both counts for one response | `dbo.task_queue_summary_get.sql` (migration + mirror) | CONFIRMED | Fixed — rewritten as a single conditional-aggregation `SELECT` (one consistent pass, `ISNULL(...,0)` guarding the zero-row case). Applied directly to the not-yet-deployed migration file (no environment had run the original body). Spec §5's SQL body updated to match. |
| `Program.cs` serves Swagger UI/JSON with no auth gate in every environment including Production | `Program.cs` | CONFIRMED, OUT OF SCOPE | Not fixed here — this predates CAS-026 (a separately-dated, explicit 2026-09-23 decision overriding an earlier Development-only gate, unrelated to this spec's endpoints). Flagged to the spec owner for a standalone decision rather than silently changed as a drive-by fix. |

Re-verified after the fix: Build ✓ | Full suite (Api.Tests 301/301, Api.IntegrationTests 89/89 runnable + 48 Skip, Worker.Tests 49/49, Web.Tests 140/140) ✓ | Format ✓.

## Supersession Note (2026-09-25)

CAS-030 (task completion notification) later rebuilt this spec's `/tasks` surface, and the code now
follows CAS-030. During the CAS-031 gate run, the integration tests in
`Api.IntegrationTests/TaskManagerControllerTests.cs` that contradicted the CAS-030 contract were
updated as follows (authorised by Ratheesh, "fix all"):

| CAS-026 test | Change | Now proven by (CAS-030) |
|---|---|---|
| `Test_V4_GetReturns403ForNonOwnedTask` | Removed: another user's task is now 404, identical to a missing task (CAS-030 V3) | `Test_V3_OtherUsersTask_Returns404IdenticalToMissing` |
| `Test_SC5_MarkViewedResetsUnviewedCountForPriorCompletions` | Removed: `POST /tasks/mark-viewed` replaced by per-task acknowledge | `Test_SC2_Summary_CountsOnlyOwnActiveAndUnacknowledgedTerminal`, `Test_SC3_Acknowledge_ClearsTaskFromSummary` |
| `Test_V8_MarkViewedScopedToCallingUser` | Removed (same reason) | `Test_V3_OtherUsersTask_Returns404IdenticalToMissing` |
| `Test_List_UnrecognizedStatus_Returns422OverHttp` | Renamed `…Returns400OverHttp`; expects 400 (CAS-030 V2) | — |
| `Test_TaskManagerReadEndpoints_NoSession_Returns401` | Dropped the `POST /tasks/mark-viewed` case | `Test_V6_TaskEndpoints_Unauthenticated_Return401` |
| `Test_SC6_TaskStateSurvivesAcrossSessions` | `startTime` now asserted as `started_at` (CAS-030 §6) | — |

The shared dev database `SVSWS0010\SQL2025/CachetArchiveSync` also lacked the CAS-028, CAS-029 and
CAS-030 migrations. They were applied in order on 2026-09-25 (all idempotent and additive).
