---
id: CAS-016
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
change_type: enhancement
related_specs: [CAS-004]
touches:
  database: true
  api: true
  ui: true
  worker: true
  b2: false
  config: false
  security: false
migrations: [20260922_240000__CAS-016-task-queue-restart.sql]
---

# CAS-016 — Change: Task Manager — Restart a Crashed/Failed Task

## §1 Current Behavior

Today, `Failed` is a genuine terminal state for a `dbo.task_queue` row with **no restart path of
any kind**:

- **State machine (CAS-004 §1, §7 V1):** a row transitions forward only, `Queued → Running →
  {Completed, Failed}`, or to `Cancelled` from `Queued`/`Running`. V1 is explicit: "never backward,
  and never re-entering `Queued` after leaving it." This is enforced in the application layer (the
  repository's update method), not by a DB `CHECK` constraint — the schema itself permits any
  `CK_task_queue_status` value in any column, but the convention is binding.
- **How a row reaches `Failed` today:** `dbo.task_queue_upd`
  (`Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_upd.sql`) is the only write path
  that can set `status = 'Failed'`. It is called by `ITaskQueueRepository.UpdateStatusAsync`
  (`src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs`,
  `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs`) from a Worker task processor
  (`src/Cachet.ArchiveSync.Worker/*TaskProcessor.cs`) when execution throws, a downstream call (e.g.
  B2) fails unrecoverably, or a startup sweep orphans a row still marked `Running` after a
  server/worker restart (`CACHET_AsyncProcessingEngine_Design_v1.md` §11; NFR Analysis target #17).
  `dbo.task_queue_upd` sets `completed_at = SYSUTCDATETIME()` and stamps whatever `result_detail` it
  is given (via `COALESCE`, so it never nulls out an existing detail) — it has **no branch that
  re-queues** the row.
- **What happens once a row is `Failed`:** nothing. There is no `dbo.task_queue_restart`-style
  procedure, no `POST /tasks/{taskId}/restart`-style endpoint, and no `ITaskQueueRepository` method
  to reset or clone a row. A user's only option today (once the Task Manager UI exists — see below)
  would be to view the row's `result_detail` and manually resubmit the original request through
  whichever module's own submission screen (Keyword Search, O-Drive Sync, etc.), re-typing/re-
  selecting the same parameters by hand.
- **What data about the original request IS retained:** `task_queue.request_parameters`
  (`NVARCHAR(MAX)`, JSON, one typed DTO per `task_type` per CAS-004 §5's resolved convention) is
  written once at `dbo.task_queue_ins` time and is **never cleared or overwritten** by
  `dbo.task_queue_claim` or `dbo.task_queue_upd` — both procedures' `UPDATE` statements omit that
  column entirely (`task_queue_claim.sql` sets only `status`/`started_at`/`updated_by`/`updated_at`;
  `task_queue_upd.sql` sets only `status`/`completed_at`/`result_detail`/`updated_by`/`updated_at`).
  So **a `Failed` row's original request payload is already durably retained today** — the gap is
  entirely the *absence of a re-submission path*, not missing data.
- **What is not retained/available today:** a single-row lookup by id. `ITaskQueueRepository.
  GetAsync` is implemented against `dbo.task_queue_get`, which **does not exist yet** — flagged as
  BLOCKED in `TaskQueueRow.cs`'s and `TaskQueueRepository.cs`'s XML docs (CAS-006 delivered
  `task_queue_ins`/`_claim`/`_upd` only). Any restart flow that needs to read a specific row by
  `task_queue_id` before acting on it inherits this existing gap unless it queries the row itself
  inline (see §7).
- **UI:** no Task Manager screen exists. `src/Cachet.ArchiveSync.Web/Components/Pages/` contains
  only the default Blazor scaffold (`Home.razor`, `Error.razor`) plus layout/routing scaffolding —
  WBS Task 25 ("Task Manager screen — queued/running/completed/failed tasks", Web UI Development,
  BRD WBS table row 25) is still `Not Started`. There is also no Task Manager **API** surface yet
  (no list/get/cancel endpoints) — the only controllers that exist are `AuditLogController`,
  `AuthController`, `QuarantineController`, `ReportsController`, `SearchController`,
  `SyncController` (`src/Cachet.ArchiveSync.Api/Controllers/`). This spec is therefore, in practice,
  the **first** spec to introduce any Task Manager API surface at all, not merely an addition to an
  existing one.
- **Cancellation, for contrast (already-built adjacent behavior):** `CACHET_AsyncProcessingEngine_
  Design_v1.md` §1 confirms "cancellation applies only to `KeywordSearch` and `ContentScan` tasks" —
  `SyncCopy`/`SyncUpdate`/`SyncPurge` are not cancellable once claimed. This spec's restart
  capability is a **different** action from cancellation and is not constrained the same way (see
  §2) — restart only ever acts on an already-`Failed` row, never a `Running` one, so the
  cancel-eligibility carve-out for sync actions does not apply to it.

---

## §2 Desired Behavior

A user viewing the (not-yet-built) Task Manager screen can select a task row in status `Failed` and
trigger **Restart**, which re-submits the same task (same `task_type`, same original
`request_parameters`) for a worker to claim and execute again.

### Recommended model: new row, not same-row reset

Two models were considered, per the BRD's framing of this as re-submission, not "resume/checkpoint
where it left off" (NFR Analysis target #17's rejected scope, §3):

| Model | Description | Verdict |
|---|---|---|
| A — Reset same row | `UPDATE dbo.task_queue SET status = 'Queued', started_at = NULL, completed_at = NULL, ... WHERE task_queue_id = @id AND status = 'Failed'` | **Rejected.** Directly violates CAS-004 §7 V1's binding, already-tested invariant: "never re-entering `Queued` after leaving it." `dbo.audit_log.task_queue_id` also has audit rows pointing at this `task_queue_id` from its original `Failed` outcome (and possibly its submission); silently rewriting the same row's status/timestamps after those audit rows were written would make the audit trail describe a task lifecycle that no longer matches what actually happened at each timestamped audit event — inconsistent with FR-23's audit-before-response and CAS-004's "audit history cannot be silently altered" guarantee (which is about the `audit_log` table itself, but the *spirit* — an immutable record of what happened when — extends to not mutating the very row those audit rows describe). |
| **B — New row, lineage-linked (recommended)** | `INSERT` a brand-new `task_queue` row with the same `task_type` and a copy of the original's `request_parameters`, in status `Queued`, carrying a new `restarted_from_task_queue_id` column pointing back at the original `Failed` row. The original row is left untouched, permanently `Failed`, forever. | **Recommended.** Fully consistent with V1 (a *new* row enters `Queued` for the first time; the old row never re-enters anything). Preserves the existing row's `Failed` status/timestamps/`result_detail` as an immutable historical record, matching the "never hard-delete/never rewrite" convention CAS-003/CAS-004 already established for `audit_log` and extending the same posture to `task_queue`. Gives the Task Manager UI a natural "restarted as task #N" / "restarted from task #N" lineage link to display, and gives the audit log a clean new `task_queue_id` to attach the restart's own audit row to (distinct from the original failure's audit row). |

This spec's DB/API/Worker design (§7–§8) implements **Model B**.

### Precise scope of "restartable"

- Only tasks currently in status `Failed` are restartable. `Queued` tasks are already re-runnable by
  definition (they haven't run yet); `Completed`/`Cancelled` tasks are deliberately excluded —
  restart is scoped to *crashed/errored* tasks per the BRD wording ("a crashed/failed task"), not a
  general "run this again" button for any terminal state.
- **`Running` tasks are never restartable.** A `Running` row is, by definition, currently claimed by
  a worker (`dbo.task_queue_claim`'s `READPAST`/`UPDLOCK` pattern, CAS-003 SC-3). Allowing a restart
  action against a `Running` row would risk a second, concurrent execution of logically the same
  work — the exact double-claim risk CAS-003 SC-3 and CAS-004 §5's Indexing Rationale were designed
  to prevent, just triggered from a different code path (a restart request) instead of two workers
  racing the claim query. §7's new stored procedure enforces this with a `WHERE status = 'Failed'`
  guard inside its own transaction, not merely a pre-check in the API layer, so a race between "user
  clicks restart" and "worker somehow still holds this row" cannot produce two live executions.
- Worker handling: a restarted task is claimed by `dbo.task_queue_claim` exactly like any other
  `Queued` row — no Worker code change is required (§4 Cross-Project Risk), since the claim query is
  already generic across all `task_type` values and does not distinguish a fresh submission from a
  restart-originated one.

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** BRD `0.Specifications\CACHET_Archive_Files_Search_Sync_BRD_v2.md` §12.3, item 12
  (2026-09-22 Product Manager review), quoted exactly: *"Crashed Task Restart — New requirement: a
  user shall be able to select a crashed/failed task from the Task Manager (Task Watcher) view and
  restart it, rather than only viewing its failure detail. This supersedes the prior NFR position
  that no task resume/checkpointing would be built."*
- **Superseded prior position:** `0.Specifications\CACHET_NFR_Analysis_v1.md`, target **#17**
  (Resolved 2026-09-09 at the time it was written): a `Running` task orphaned by a server/worker
  restart is "failed cleanly, not resumed and not silently restarted," and "**true resume/
  checkpointing was deliberately not chosen** — it adds engineering complexity (safely resuming
  partial index writes without duplicate audit-log rows) that this project's confirmed scale (3–4
  users, not a real-time/high-availability system) does not currently justify." BRD §12.3 item 12
  explicitly supersedes this position — but note precisely **what** it supersedes: item 12 asks for
  **user-initiated re-submission of a `Failed` task as a new task**, not the "resume a partially-
  completed `Running` task from a checkpoint" capability NFR target #17 rejected. This spec's Model
  B (§2) is deliberately *not* checkpoint/resume — it starts the restarted task from scratch with the
  original parameters, which is consistent with NFR target #17's own risk framing ("safely resuming
  partial index writes without duplicate audit-log rows" — Model B avoids exactly this problem by
  never resuming partial work, only re-running the whole task as a new row/new audit trail).
- **Risk if not changed:** a user whose task fails (e.g., a transient B2 timeout, per
  `dbo.task_queue_upd`'s worked `result_detail` example "B2 API timeout after 3 retries") has no way
  to retry short of manually re-entering the same search/scan/sync parameters from scratch in the
  originating screen, which is exactly the friction BRD §12.3 item 12 was raised to eliminate.

---

## §4 Blast Radius (Graph Impact)

> graphify not yet vendored for this repo in the sense of indexing application source — the
> existing `graphify-out/` build (2026-09-10, referenced in CAS-004 §17) covers only the BRD/WBS
> documentation corpus, not the C# source tree that now exists under `src/`. This section is a
> manual grep/read of the codebase, stated explicitly per CLAUDE.md's fallback rule.

### Primary Change Target
Entity: `dbo.task_queue` (table) — adding one nullable lineage column.
File: `Database/CachetArchiveSync/Entities/dbo.task_queue.sql`.
Current edges (manual read): referenced by `dbo.audit_log.task_queue_id` (FK, nullable, no
cascade — CAS-003 SC-5), `dbo.task_queue_ins/_claim/_upd` (existing procedures), and every Worker
task processor via `ITaskQueueRepository`.

### Direct Dependents (nodes with edges INTO the changed entity)
- `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs` — needs a new method
  (`RestartAsync` or similar) and its DTO surface extended.
- `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs` — Dapper implementation of the
  new method, calling the new stored procedure (§7).
- `src/Cachet.ArchiveSync.Core/Dtos/TaskQueueRow.cs` — read projection should surface the new
  `restarted_from_task_queue_id`/lineage fields once `dbo.task_queue_get` exists (that procedure
  remains BLOCKED per CAS-006 — see §14).
- `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_ins.sql`,
  `dbo.task_queue_claim.sql`, `dbo.task_queue_upd.sql` — **not modified** by this spec (§6); a new
  sibling procedure is added instead (§7).

### Downstream Modules
- **Task Manager** (this spec's primary subject — the module that doesn't exist as UI/API yet).
- **Audit Log** — the restart action is state-changing (FR-23) and must write an audit row (§9,
  §11) before returning success, tagged `tab_module = 'Task Manager'` per `CK_audit_log_tab_module`.
- **Worker** — no code change required (§2), but the claim loop is a direct dependent in the sense
  that it is what actually executes a restarted row; flagged here for completeness/reviewer
  awareness, not because a file changes.
- **Keyword Search / O-Drive Sync / Quarantine (indirectly)** — any `task_type` can be restarted;
  the restart procedure is generic across `task_type`, so a Keyword Search or Sync task's original
  `request_parameters` DTO shape must remain forward-compatible for a later worker claim to
  deserialize correctly — no DTO shape change is proposed here, just noting the coupling.

### Cross-Project Risk
**Flagged: yes, this change is cross-project.** It touches all four projects in one coordinated
change:
- **Core** (`Cachet.ArchiveSync.Core`) — `ITaskQueueRepository` contract change, shared by both Api
  and (indirectly, via the same NuGet-referenced project) Worker.
- **Api** — new `POST /tasks/{taskId}/restart` endpoint (§8), new repository method, new stored
  procedure call.
- **Worker** — no code change, but behaviorally in scope: a restarted row must be claimable and
  processable exactly like a fresh submission, which this spec asserts is already true by
  construction (§2) rather than modifies.
- **Web** — the eventual Task Manager UI (WBS Task 25, not yet built) is this endpoint's consumer;
  this spec defines the contract the UI will call once built (§14).

### God Node Involvement
`dbo.task_queue` and `ITaskQueueRepository` are high-fan-in nodes (every async module writes
through them — Keyword Search, O-Drive Sync, Quarantine's async paths, Task Manager). This spec adds
a new column and a new repository method rather than modifying any existing method signature, so
existing callers (`InsertAsync`, `ClaimNextAsync`, `UpdateStatusAsync`, `GetAsync`) are unaffected —
the god-node risk is mitigated by additive-only change, not signature change.

### Graphify Queries Run
None — "graphify not yet vendored for application source in this repo; analysis done via manual
grep/read of `src/`, `Database/`, and the CAS-004/CAS-006 specs," per CLAUDE.md's stated fallback.

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `Database/migrations/20260922_240000__CAS-016-task-queue-restart.sql` | add | New migration: adds `restarted_from_task_queue_id` column + FK to `dbo.task_queue`; creates `dbo.task_queue_restart_ins` stored procedure. |
| `Database/CachetArchiveSync/Entities/dbo.task_queue.sql` | modify | Mirror update: add `restarted_from_task_queue_id INT NULL` column + self-referencing FK constraint. |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_restart_ins.sql` | add | New mirror file for the restart procedure (§7). |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs` | modify | Add `RestartAsync(int taskQueueId, int requestingUserId, string createdBy, string updatedBy, CancellationToken ct)` (or equivalent) returning the new `task_queue_id`, plus a restart-specific outcome type distinguishing "not found," "not in `Failed` state," and "not the submitting user" (§14 item 3, resolved). |
| `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs` | modify | Dapper implementation calling `dbo.task_queue_restart_ins`, passing `@requesting_user_id` and mapping the new `@result_code` OUTPUT value to the outcome type. |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskQueueRow.cs` | modify | Add `RestartedFromTaskQueueId` (nullable int) to the read projection (populated once `dbo.task_queue_get` exists — see §14 BLOCKED note; the field is added to the DTO now so it compiles against the new column, per Karpathy P5 build-incrementally). |
| `src/Cachet.ArchiveSync.Api/Controllers/TaskManagerController.cs` | add | New controller hosting only `POST /tasks/{taskId}/restart` (§8) — no list/get/cancel actions, which remain out of scope for a future Task Manager API spec (§6). |
| `src/Cachet.ArchiveSync.Core/Dtos/` (new DTO file, e.g. `RestartTaskResult.cs`) | add | Outcome DTO for the service/controller layer (`Restarted`, `NotFound`, `NotRestartable`, `NotOwner`). |
| A new `Services`-layer type, e.g. `src/Cachet.ArchiveSync.Api/Services/TaskManagerService.cs` | add | Thin service wrapping `ITaskQueueRepository.RestartAsync` + the audit-log write (FR-23), following this codebase's existing `*Service` pattern (`KeywordSearchService`, `QuarantineService`, `SyncActionService`, `SyncScanService`). Passes the authenticated caller's user id (`GetUserId()` pattern) through as `requestingUserId` for the ownership check. |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_ins.sql` | Fresh submission path is unaffected — restart uses its own new procedure (§7), not this one. |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_claim.sql` | Worker claim logic needs no change (§2) — a restarted row is claimed identically to any other `Queued` row. |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_upd.sql` | Status transition logic for `Running → {Completed, Failed, Cancelled}` is unaffected; restart never calls this procedure. |
| `src/Cachet.ArchiveSync.Worker/*TaskProcessor.cs` (all five) | No behavior change — a restarted task is indistinguishable from a fresh one at claim/execution time by design (§2). |
| `src/Cachet.ArchiveSync.Api/Controllers/SearchController.cs`, `SyncController.cs`, `QuarantineController.cs` | Restart is a Task Manager concern, not a per-module concern; these controllers' existing submission/cancel actions are untouched. |
| `dbo.audit_log` schema (`Database/CachetArchiveSync/Entities/dbo.audit_log.sql`) | No schema change needed — the restart action's audit row uses the existing `tab_module = 'Task Manager'`, `task_queue_id` (pointing at the **new** row), `action`/`result` enum convention already established by CAS-004 §5; no new column required. |

---

## §7 Database Changes

### Changed Objects
| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| `dbo.task_queue` | dbo | Add column `restarted_from_task_queue_id INT NULL` + `CONSTRAINT FK_task_queue_restarted_from FOREIGN KEY (restarted_from_task_queue_id) REFERENCES dbo.task_queue(task_queue_id)` | Self-referencing, nullable — `NULL` for every normally-submitted row; populated only on a row created by a restart (Model B, §2). No cascade (consistent with this project's "no FK cascades that could silently remove history" posture, CAS-003 SC-5's spirit, though this FK is same-table rather than into `audit_log`). |
| `dbo.task_queue_restart_ins` (new) | dbo | New stored procedure | See body below. |

### Why a schema change is needed (and why it's the *only* one)
`request_parameters` is already durably retained (§1) — **no column is needed to retain the
original request payload**; Model B's `INSERT` simply copies the existing value. The **one** thing
genuinely missing from the current schema is a way to record that a new row is a restart of an
older one — hence exactly one new nullable column, no other schema change.

### New Stored Procedure: `dbo.task_queue_restart_ins`

Naming: this project's `schema.entity_action` convention (`task_queue_ins`, `_claim`, `_upd`) would
suggest `_upd` since it *updates* the queue's contents in a broad sense, but the operative SQL
statement here is an `INSERT` of a brand-new row (Model B, §2) — so `_restart_ins` more accurately
describes the operation than `_restart_upd`, and is the name this spec proposes. Flag for reviewer
sign-off if the naming convention is read differently (§14).

```sql
CREATE OR ALTER PROCEDURE [dbo].[task_queue_restart_ins]
    @task_queue_id          INT             -- the Failed row being restarted
   ,@requesting_user_id     INT             -- the authenticated caller (§14 item 3: submitter-only)
   ,@created_by             NVARCHAR(50)    -- user triggering the restart
   ,@updated_by             NVARCHAR(50)
   ,@new_task_queue_id      INT             OUTPUT
   ,@result_code            NVARCHAR(20)    OUTPUT  -- 'RESTARTED' | 'NOT_FOUND' | 'NOT_FAILED' | 'NOT_OWNER'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @task_queue_id IS NULL
        BEGIN RAISERROR(N'@task_queue_id is required.', 16, 1); RETURN; END
    IF @requesting_user_id IS NULL
        BEGIN RAISERROR(N'@requesting_user_id is required.', 16, 1); RETURN; END
    IF @created_by IS NULL OR LTRIM(RTRIM(@created_by)) = N''
        BEGIN RAISERROR(N'@created_by is required.', 16, 1); RETURN; END
    IF @updated_by IS NULL OR LTRIM(RTRIM(@updated_by)) = N''
        BEGIN RAISERROR(N'@updated_by is required.', 16, 1); RETURN; END

    BEGIN TRY
        BEGIN TRANSACTION;

            DECLARE @task_type NVARCHAR(20), @request_parameters NVARCHAR(MAX),
                    @found_status NVARCHAR(20), @found_submitted_by_user_id INT;

            -- Row-lock the source row for the duration of this transaction so a concurrent
            -- restart-of-the-same-row (double-click) cannot both succeed.
            SELECT
                @task_type = task_type,
                @request_parameters = request_parameters,
                @found_status = status,
                @found_submitted_by_user_id = submitted_by_user_id
            FROM [dbo].[task_queue] WITH (UPDLOCK, ROWLOCK)
            WHERE task_queue_id = @task_queue_id;

            IF @found_status IS NULL
                BEGIN SET @result_code = N'NOT_FOUND'; COMMIT TRANSACTION; RETURN; END

            IF @found_submitted_by_user_id <> @requesting_user_id
                BEGIN SET @result_code = N'NOT_OWNER'; COMMIT TRANSACTION; RETURN; END

            IF @found_status <> N'Failed'
                BEGIN SET @result_code = N'NOT_FAILED'; COMMIT TRANSACTION; RETURN; END

            INSERT INTO [dbo].[task_queue]
            (
                created_by, updated_by, task_type, submitted_by_user_id,
                status, request_parameters, restarted_from_task_queue_id
            )
            VALUES
            (
                @created_by, @updated_by, @task_type, @found_submitted_by_user_id,
                N'Queued', @request_parameters, @task_queue_id
            );

            SET @new_task_queue_id = SCOPE_IDENTITY();
            SET @result_code = N'RESTARTED';

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO
```

Notes:
- The `Failed`-state check and the ownership check both happen **inside the same transaction** as
  the row-lock read, closing the race window §2 flags (a task cannot be claimed while `Failed`,
  since `dbo.task_queue_claim` only ever selects `status = 'Queued'` — but the explicit re-check
  here is defense in depth and gives a precise, race-safe 404/403/409 at the API layer, §8).
- **Ownership check precedes the state check** (`NOT_OWNER` before `NOT_FAILED`): a non-owner gets
  the same `403` regardless of the row's current status, so the response never leaks the row's
  status to a caller who isn't entitled to act on it.
- `@result_code` OUTPUT replaces bare `RAISERROR` for the three *expected* business-rule branches
  (not found / not owner / not failed) — `RAISERROR`/`THROW` is reserved for genuine exceptions
  (missing required parameter, unexpected DB error) per the `BEGIN CATCH` block. This gives the
  repository/service layer a clean, string-typed outcome to map to `RestartTaskResult` (§5) instead
  of parsing exception messages.
- The new row's `submitted_by_user_id` is copied from `@found_submitted_by_user_id` (the original
  row's own value), not `@requesting_user_id` — since ownership is enforced to always be the same
  user, these are equal in practice, but sourcing it from the already-locked row's own data avoids
  ever trusting a second copy of an identity value that should be single-sourced.
- The original row is **never** `UPDATE`d by this procedure — it remains `Failed` with its original
  `completed_at`/`result_detail` untouched, satisfying Model B's immutability goal (§2).
- `dbo.task_queue_get` (needed to look up the original row's `task_type` for display before a user
  decides to restart) remains a pre-existing BLOCKED gap from CAS-006 (§1, §14) — this procedure
  does not depend on it, since it reads what it needs (`task_type`, `request_parameters`, `status`,
  `submitted_by_user_id`) directly in its own `SELECT`.

### Migration Required
**Yes.** `Database/migrations/20260922_240000__CAS-016-task-queue-restart.sql`, containing:
1. `ALTER TABLE dbo.task_queue ADD restarted_from_task_queue_id INT NULL;`
2. `ALTER TABLE dbo.task_queue ADD CONSTRAINT FK_task_queue_restarted_from FOREIGN KEY
   (restarted_from_task_queue_id) REFERENCES dbo.task_queue(task_queue_id);`
3. The `CREATE OR ALTER PROCEDURE dbo.task_queue_restart_ins` body above.

Mirror updates: `Database/CachetArchiveSync/Entities/dbo.task_queue.sql` (add the column + FK) and
new file `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_restart_ins.sql`.

---

## §8 API Contract Changes

New endpoint — no existing endpoint's shape changes (non-breaking addition).

### `POST /tasks/{taskId}/restart`

| Aspect | Detail |
|---|---|
| Authorization | `[Authorize(Policy = "CachetAuthenticated")]`, matching every other state-changing endpoint in this codebase (`SearchController`, `SyncController`, `QuarantineController`). |
| Path parameter | `taskId` (int) — the `task_queue_id` of the `Failed` row to restart. |
| Request body | None (empty `POST`) — all needed data (`task_type`, `request_parameters`) is read server-side from the existing row; the restarting user's identity comes from the authenticated session claim (`GetUserId()` pattern, `SearchController`) and is passed through as `@requesting_user_id` for the ownership check below. |
| Success response | `202 Accepted`, body `{ "taskId": <new_task_queue_id>, "restartedFromTaskId": <original taskId> }` — mirrors `SearchController.Submit`'s `Accepted(result.Response)` shape/status code, since a restart is, mechanically, a new asynchronous submission. |
| `403 Forbidden` | `taskId` exists but its `submitted_by_user_id` does not match the authenticated caller (§14 item 3, resolved: **submitter-only** — a user may restart only a task they originally submitted). Returned in preference to `404`/`409` regardless of the row's actual status, so a non-owner cannot infer the row's state from the response (§7 proc notes). |
| `404 Not Found` | `taskId` does not exist in `dbo.task_queue`. |
| `409 Conflict` | `taskId` exists, belongs to the caller, but is not currently in status `Failed` (e.g., still `Queued`/`Running`, or already `Completed`/`Cancelled`). `409` chosen over `422` because the request is well-formed but conflicts with the resource's current state — consistent with this codebase's use of `422` for *input validation* failures (`SearchController.Submit`'s `ValidationFailed` branch) versus a state conflict, which is a `409` concern. |
| Ownership scoping | **Resolved 2026-09-22 (owner confirmation):** only the original submitter (`submitted_by_user_id`) may restart a task — not any authenticated user. Enforced inside `dbo.task_queue_restart_ins`'s transaction (§7), not merely a controller-level check, for the same race-safety reason the `Failed`-state check is enforced there. |

| Endpoint | Field | Before | After | Breaking? |
|----------|-------|--------|-------|-----------|
| `POST /tasks/{taskId}/restart` | (new) | N/A | New endpoint, `202`/`403`/`404`/`409` | No |

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 (new, restart-specific) | N/A — no restart action exists | Only a `task_queue` row currently in status `Failed` may be restarted; `Queued`/`Running`/`Completed`/`Cancelled` are rejected with `409` | `Test_V1_RestartRejectedWhenNotFailed` |
| V2 (new) | N/A | A restart targeting a non-existent `taskId` returns `404`, never a `500` or silent no-op | `Test_V2_RestartReturns404ForUnknownTask` |
| V3 (new) | N/A | The restart action writes exactly one `dbo.audit_log` row (`tab_module = 'Task Manager'`, `task_queue_id` = the **new** row's id) before the `202` response is returned (FR-23) | `Test_V3_RestartWritesAuditLogBeforeResponding` |
| V4 (new) | N/A | The new row's `request_parameters` is byte-for-byte identical to the original `Failed` row's `request_parameters` | `Test_V4_RestartCopiesOriginalRequestParametersExactly` |
| V5 (new) | N/A — no restart action exists | Only the task's original submitter (`submitted_by_user_id`) may restart it; any other authenticated user is rejected with `403`, regardless of the row's status | `Test_V5_RestartRejectedForNonSubmitter` |
| Reaffirmed (CAS-004 §7 V1, unchanged) | A `task_queue` row's `status` transitions only forward, never re-entering `Queued` after leaving it | Unchanged — Model B (§2) satisfies this by construction: the restarted row is a *new* row entering `Queued` for the first time; the original row's status is never rewritten | `Test_V1_TaskQueueStatusTransitionIsForwardOnly` (CAS-004, unmodified) |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Restarting a `Failed` task inserts exactly one new `dbo.task_queue` row in status `Queued`, with `restarted_from_task_queue_id` pointing at the original, `task_type` and `request_parameters` copied exactly, and the original row's `status`/`completed_at`/`result_detail` unchanged | `Test_SC1_RestartCreatesLinkedQueuedRowWithoutMutatingOriginal` |
| SC-2 | A restarted row is claimable by `dbo.task_queue_claim` and executes through a Worker task processor exactly like a freshly-submitted row of the same `task_type`, with no Worker code change required | `Test_SC2_RestartedRowClaimableAndProcessableLikeFreshSubmission` |
| SC-3 | Attempting to restart a task in `Queued`, `Running`, `Completed`, or `Cancelled` status returns `409` and creates no new row | `Test_SC3_RestartRejectedForNonFailedStatuses` |
| SC-4 | The restart action writes an audit-log row (FR-23) before the `202` response is returned, and the audit row's `task_queue_id` references the new (restarted) row, not the original | `Test_SC4_RestartAuditRowPrecedesResponseAndReferencesNewRow` |
| SC-5 | A restart request from a user who is not the task's original `submitted_by_user_id` returns `403` and creates no new row, no matter the task's actual status | `Test_SC5_RestartRejectedForNonOwningUser` |

---

## §11 Security Notes

- **Audit logging (binding rule, CLAUDE.md):** `POST /tasks/{taskId}/restart` is a state-changing
  action (it creates a new queued unit of work that will itself perform a search/scan/sync
  operation) and **must** write to `dbo.audit_log` before returning success, not after — following
  the exact pattern already established by `KeywordSearchService`/`QuarantineService`/
  `SyncActionService` for their own state-changing actions. `tab_module = 'Task Manager'` (an
  already-`CHECK`-permitted value per `CK_audit_log_tab_module`); `action` should be a new
  `AuditAction` enum member (e.g. `TaskRestart`) added to `Cachet.ArchiveSync.Core`'s shared catalog
  per CAS-004 §5's resolved convention, not a free-typed string.
- **No B2 credential exposure:** this change touches no B2 client code and introduces no new
  B2-adjacent surface (`touches.b2: false`) — restart only re-queues a task record; the eventual
  Worker execution of a restarted `SyncCopy`/`SyncUpdate`/`SyncPurge` task uses the same
  server-side-brokered B2 credential path (FR-03) that already exists for a fresh submission, unchanged
  by this spec.
- **No new authentication/session surface** (`touches.security: false`) — the new endpoint reuses
  the existing `CachetAuthenticated` policy; no new claim type or session behavior is introduced.
- **Ownership enforcement (resolved, §14 item 3):** restart is scoped to the task's original
  submitter only. This is enforced authoritatively inside `dbo.task_queue_restart_ins`'s transaction
  (§7) — not merely as a controller-level `if` check — so a TOCTOU race between an authorization
  check and the restart itself cannot let a non-owner slip through. The `403` branch is evaluated
  before the `409` (not-`Failed`) branch so a non-owner's response never reveals the task's current
  status.
- **Data minimization:** no new sensitive data is stored — the new column is an `INT` self-reference,
  and the copied `request_parameters` is the same JSON already retained for the original row (§1);
  no additional PII or credential material is introduced.

---

## §12 Rollback Plan

1. **Application rollback:** revert the new controller/service/repository-method commits; the
   endpoint disappears, but no existing endpoint's behavior is affected (this is a pure addition —
   §8's table shows no existing row).
2. **Database rollback:** if the migration must be reverted after deployment,
   `DROP PROCEDURE dbo.task_queue_restart_ins;` followed by
   `ALTER TABLE dbo.task_queue DROP CONSTRAINT FK_task_queue_restarted_from;` then
   `ALTER TABLE dbo.task_queue DROP COLUMN restarted_from_task_queue_id;` — safe only if no row has
   yet been inserted with a non-`NULL` value in that column (if rows exist, dropping the column
   destroys the restart-lineage history for those rows; assess before dropping in a populated
   environment). This project's migrations are append-only (CLAUDE.md), so a genuine rollback in a
   already-migrated environment is a new forward migration that reverses the change, not an edit to
   the original file.
3. **Config rollback:** N/A — no configuration keys are introduced.
4. **Task-queue rollback (in-flight rows):** this change does not alter the shape or meaning of any
   existing `Queued`/`Running` row created under the old contract — `restarted_from_task_queue_id`
   is nullable and defaults to `NULL` for every row not created via the new procedure, so no
   existing or in-flight row is affected by adding the column. A restarted row that is `Queued` at
   the moment of a rollback is treated identically to any other `Queued` row by the existing
   orphan-sweep behavior (NFR target #17) — no special-casing needed.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `TaskQueueRestartRepositoryTests` (integration, real/test SQL Server) | `Test_SC1_RestartCreatesLinkedQueuedRowWithoutMutatingOriginal` | SC-1 |
| `TaskQueueRestartRepositoryTests` | `Test_SC3_RestartRejectedForNonFailedStatuses` | SC-3, V1 |
| `TaskQueueRestartRepositoryTests` | `Test_V4_RestartCopiesOriginalRequestParametersExactly` | V4 |
| `TaskQueueRestartRepositoryTests` | `Test_SC5_RestartRejectedForNonOwningUser` | SC-5, V5 |
| `TaskQueueRestartRepositoryTests` | `Test_NotOwnerPrecedesNotFailed_ResponseHidesTaskStatusFromNonOwner` | §11 (ownership check ordering) |
| `TaskManagerControllerTests` (or `TaskManagerApiWebApplicationFactory`-based integration test, following `SearchApiWebApplicationFactory`'s pattern) | `Test_V2_RestartReturns404ForUnknownTask` | SC-3 (404 branch), V2 |
| `TaskManagerControllerTests` | `Test_SC4_RestartAuditRowPrecedesResponseAndReferencesNewRow` | SC-4, V3 |
| `TaskManagerControllerTests` | `Test_SC5_RestartReturns403ForNonOwningUser` | SC-5, V5 |
| `TaskQueueClaimConcurrencyTests`-style test (extend or sibling of the existing `TaskQueueClaimConcurrencyTests.cs`) | `Test_SC2_RestartedRowClaimableAndProcessableLikeFreshSubmission` | SC-2 |
| `TaskManagerServiceTests` (unit, NSubstitute-mocked repository) | `Test_V1_RestartRejectedWhenNotFailed` | V1 |
| `TaskManagerServiceTests` | `Test_V5_RestartRejectedWhenNotOwner` | V5 |

Coverage target: ≥ 85% on changed files (`TaskManagerController.cs`, `TaskManagerService.cs`,
`TaskQueueRepository.cs`'s new method, `ITaskQueueRepository.cs`'s new member).

---

## §14 Open Questions — Resolved 2026-09-22 (owner: devarajan@tracecounts.org)

- [x] **The Task Manager UI does not exist yet** (WBS Task 25, `Not Started`) — no change from the
      draft: this spec defines only the API/DB/Worker contract (`POST /tasks/{taskId}/restart`, the
      `dbo.task_queue_restart_ins` procedure, the lineage column) that the Blazor Task Manager
      screen will call once it is built. No Blazor component is added by this spec; `touches.ui:
      true` reflects that the eventual UI is this contract's intended consumer, not that a `.razor`
      file is written here. Not blocking — carried forward as a known dependency for WBS Task 25.
- [x] **Design choice: Model B confirmed.** §2's "new row, lineage-linked" model is adopted as
      designed — CAS-004 §7 V1's forward-only state machine and the audit-immutability posture both
      rule out Model A (same-row reset). No change to §5/§7 from the draft.
- [x] **Stored procedure naming: `dbo.task_queue_restart_ins` confirmed** — the operative statement
      is an `INSERT` of a new row (Model B), so `_ins` correctly describes the mechanism; `_upd`
      would mislead a reader into expecting the original row to be updated in place, which this
      design deliberately never does.
- [x] **Ownership scoping: submitter-only, confirmed.** Only the task's original submitter
      (`submitted_by_user_id`) may restart it — not any authenticated user. Implemented as an
      in-transaction check inside `dbo.task_queue_restart_ins` (§7), surfaced as `403 Forbidden`
      at the API layer (§8), and enforced ahead of the `Failed`-state check so a non-owner's
      response never leaks the task's actual status (§11). See §5 (repository/service signature
      changes), §7 (`@requesting_user_id`/`@result_code` OUTPUT), §9 V5, §10 SC-5, §13 (new tests)
      for the resulting design changes from the original draft.
- [x] **`dbo.task_queue_get` remains BLOCKED** (pre-existing gap from CAS-006, §1) — confirmed out
      of scope for this spec. This spec's own procedure does not depend on it (§7); the eventual
      Task Manager UI's "view task detail" screen will need it, tracked against CAS-006, not here.
- [x] **Restart of a task whose `task_type` DTO shape has since changed** — confirmed accepted as a
      documented latent risk, no action taken. No versioning scheme exists for `request_parameters`
      JSON today, and building one speculatively is not justified at this project's confirmed scale
      (BRD §12.3 item 13). Revisit only if this manifests as an actual incident.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 | Draft — BRD §12.3 item 12 |
| Owner | R. Devarajan (TraceCounts) | 2026-09-22 | §14 open questions resolved: Model B, `_ins` naming, and **submitter-only** ownership scoping (§7/§8/§9/§10/§11/§13 updated accordingly) — `status: ready` |
| Reviewer | | | Pending |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] §1 Current Behavior matches the actual merged code (`dbo.task_queue_upd.sql`,
      `TaskQueueRepository.cs`, `ITaskQueueRepository.cs`) exactly as of this spec's writing —
      specifically that `request_parameters` is genuinely retained and never cleared today.
- [ ] §2's Model B is fully implemented — the original `Failed` row is never mutated by any code
      path this spec introduces.
- [ ] Only files in §5 were modified (§6 files are untouched).
- [ ] All §10 Success Criteria have passing tests.
- [ ] All §9 Validations are implemented and tested, including the reaffirmed CAS-004 V1 test still
      passes unmodified.
- [ ] §8 API contract is correct — `404`/`409` branches both verified, no undeclared breaking change
      to any existing endpoint.
- [ ] §11 Security Notes implemented as described — audit row written before the `202` response,
      not after (FR-23 fail-closed ordering), using an enum `AuditAction` value, not a free string.
- [ ] Ownership check (§14 item 3) is enforced inside `dbo.task_queue_restart_ins`'s transaction,
      not only in the controller/service layer, and its `NOT_OWNER` branch is checked before
      `NOT_FAILED` so a non-owner's `403` never reveals the task's actual status.
- [ ] §4 Blast Radius is accurate — no unintended changes bled into `dbo.task_queue_ins/_claim/_upd`
      or any Worker task processor file.
- [ ] §12 in-flight task-queue rows are unaffected by the new nullable column (spot-check: existing
      rows read back with `restarted_from_task_queue_id = NULL`).
- [ ] §14's open questions were either resolved by the owner before `/implement`, or the
      implementation explicitly documents which default assumption it proceeded under.

### Verification Report Format
Per template: `CAS-016.review.md` alongside this spec — overall verdict, per-SC verdict table,
security gap list, scope violations.

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 66.4% overall on new/changed files -- TaskManagerService.cs 100%, TaskManagerController.cs 93.9%, RestartTaskResult.cs 66.7%, TaskQueueRow.cs 63.6%, TaskQueueRepository.cs 0% (below-threshold files covered only by Skip-marked real-DB tests; no live SQL Server in this sandbox, same pre-existing pattern as this codebase's other repository tests; accepted per owner decision 2026-09-22) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs` | Added RestartAsync method |
| `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs` | Dapper impl calling dbo.task_queue_restart_ins |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskQueueRow.cs` | Added RestartedFromTaskQueueId |
| `src/Cachet.ArchiveSync.Core/Dtos/RestartTaskResult.cs` | New outcome DTO/enum (Restarted/NotFound/NotRestartable/NotOwner) |
| `src/Cachet.ArchiveSync.Api/Controllers/TaskManagerController.cs` | New controller, POST /tasks/{taskId}/restart |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskManagerService.cs` | New service interface |
| `src/Cachet.ArchiveSync.Api/Services/TaskManagerService.cs` | New service, audits before 202 response |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | Added TaskRestart member |
| `src/Cachet.ArchiveSync.Api/Program.cs` | DI registration for ITaskManagerService |
| `Database/migrations/20260922_240000__CAS-016-task-queue-restart.sql` | New migration: column+FK+task_queue_restart_ins proc |
| `Database/CachetArchiveSync/Entities/dbo.task_queue.sql` | Mirror: added restarted_from_task_queue_id + FK |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_restart_ins.sql` | New mirror file |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `TaskManagerServiceTests` | `Test_V1_RestartRejectedWhenNotFailed` | V1 |
| `TaskManagerServiceTests` | `Test_V5_RestartRejectedWhenNotOwner` | V5 |
| `TaskManagerServiceTests` | `Test_V3_RestartWritesAuditLogBeforeResponding` | V3, SC-4 |
| `TaskManagerServiceTests` | `Test_RestartNotFound_PassedThroughWithoutAuditWrite` | NotFound branch |
| `TaskQueueRestartRepositoryTests` | `Test_SC1_RestartCreatesLinkedQueuedRowWithoutMutatingOriginal` | SC-1 |
| `TaskQueueRestartRepositoryTests` | `Test_SC3_RestartRejectedForNonFailedStatuses` | SC-3, V1 |
| `TaskQueueRestartRepositoryTests` | `Test_V4_RestartCopiesOriginalRequestParametersExactly` | V4 |
| `TaskQueueRestartRepositoryTests` | `Test_SC5_RestartRejectedForNonOwningUser` | SC-5, V5 |
| `TaskQueueRestartRepositoryTests` | `Test_NotOwnerPrecedesNotFailed_ResponseHidesTaskStatusFromNonOwner` | §11 ordering |
| `TaskQueueRestartClaimTests` | `Test_SC2_RestartedRowClaimableAndProcessableLikeFreshSubmission` | SC-2 |
| `TaskManagerControllerTests` | `Test_V2_RestartReturns404ForUnknownTask` | V2 |
| `TaskManagerControllerTests` | `Test_SC4_RestartAuditRowPrecedesResponseAndReferencesNewRow` | SC-4, V3 |
| `TaskManagerControllerTests` | `Test_SC5_RestartReturns403ForNonOwningUser` | SC-5, V5 |
| `TaskManagerControllerTests` | `Test_Restart_NotFailedStatus_Returns409` | SC-3 (409 branch) |
| `TaskManagerControllerTests` | `Test_PostRestart_NoSession_Returns401` | 401 baseline |

### Migration
`20260922_240000__CAS-016-task-queue-restart.sql`
