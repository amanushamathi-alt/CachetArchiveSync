---
id: CAS-028
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-24
priority: P0
related_specs: [CAS-003, CAS-004, CAS-005, CAS-006, CAS-007, CAS-016]
touches:
  database: true
  api: false
  ui: false
  worker: true
  b2: false
  config: true
  security: true
migrations: [20260924_090000__CAS-028-task-queue-infrastructure.sql]
---

# CAS-028 — Task Queue Infrastructure (Durable Queue, Background Worker Service)

**WBS traceability:** `1.WBS/CACHET_WBS_v1.md` Task 28, "Task queue infrastructure (durable queue,
background worker service)", category Asynchronous Processing Engine.
**Design source:** `0.Specifications/CACHET_AsyncProcessingEngine_Design_v1.md` (WBS Task 9) §1–§5,
§9, §11, §13. This spec implements that design's harness. It does not re-decide anything the design
resolves; the design's own citations are carried forward rather than repeated.

## §0 Reading Guide: What Already Exists vs. What This Spec Adds

Most of the queue already exists. CAS-003, CAS-006, CAS-007, and CAS-016 built the table, the enqueue
path, a claim procedure, and a serial claim loop. Each earlier spec built only what it needed. This
spec **consolidates the queue as one piece of infrastructure** and closes the gaps between the current
code and the Asynchronous Processing Engine design. The table below records what was verified on
2026-09-24 (manual code read, plus a LocalDB compile check where noted).

| Area | Current state (verified) | Gap vs. design | This spec |
|---|---|---|---|
| `dbo.task_queue` table | Exists (CAS-003, amended by CAS-016). 5 statuses and 5 task types are enforced by `CHECK`. Index `IX_task_queue_status_submitted_at` exists. | None | No change |
| Enqueue | `ITaskQueueRepository.InsertAsync` → `dbo.task_queue_ins`, called by `KeywordSearchService`/`SyncScanService`/`SyncActionService`. Order: insert → audit → `202`. | None | Documented only (§3.1); no change |
| Claim SP `dbo.task_queue_claim` | **Mirror and migration disagree.** The mirror used `UPDATE TOP (1) … ORDER BY submitted_at`, which **does not compile** (LocalDB 2026-09-24: `Msg 156: Incorrect syntax near the keyword 'ORDER'`; the same pattern is in CAS-004 Appendix B). The CAS-006 migration on disk has that line commented out (`--ORDER BY t.submitted_at ASC;`, line 421; file last modified 2026-09-24 12:11 IST, outside this spec's work and contrary to the append-only migration rule). So a database built from the migrations **compiles and claims, but in arbitrary order, not FIFO**. The real-DB claim tests (`TaskQueueClaimConcurrencyTests`, `SyncTaskQueueClaimConcurrencyTests`) are all `[Fact(Skip = …)]`. *Corrected 2026-09-24 after the db-author found the commented line; an earlier draft said both files had the invalid form.* | Claim order is not FIFO (design §4 Decision #2); the mirror doesn't match the deployed object | Replace with the verified FIFO form (§5); mirror and migration consistent again |
| Worker execution | `Worker.cs` awaits `processor.ProcessAsync` inline, so **one task runs at a time**. | Design §5.3 requires bounded parallelism, default 4 | Bounded-parallel slot model (§3.3) |
| Poll interval | Hard-coded `TimeSpan.FromSeconds(5)` | Design §13 requires the config key `Worker:PollingIntervalSeconds` | Config-bound (§9) |
| Restart / crash recovery | **None.** A row left `Running` by a crash stays `Running` forever. CAS-016 restart only accepts `Failed` rows, so such a task can never be restarted. | Design §11 startup sweep | New SP plus a startup sweep (§3.4, §5) |
| Unregistered `task_type` | Logs a warning and **leaves the task `Running` forever** (`Worker.cs:53-59`) | Design §9: never leave a task stuck | Mark `Failed` + audit (§3.5) |
| Exception escaping a processor | Caught by the loop and logged; **the task is left `Running`** | Design §9.1–9.3 | Dispatch-boundary safety net (§3.5) |
| Service hosting | Console host only; no Windows Service integration | Design §5.1: independently deployable process, e.g. a Windows Service | `AddWindowsService()` (§3.6) |

---

## §1 Summary

This spec makes the SQL Server–backed task queue and the `Cachet.ArchiveSync.Worker`
`BackgroundService` durable, concurrency-safe, and restart-safe. Tasks are enqueued by the existing
API submission endpoints. A worker claims them atomically in FIFO order with a `READPAST`/`UPDLOCK`
claim that is safe under concurrent claimers. Up to a configurable number of tasks (default 4) run in
parallel inside the Worker, independent of any browser session. Because task state lives only in
`dbo.task_queue`, it survives both browser close and server restart. On Worker startup, any task
orphaned in `Running` by a previous process lifetime is swept to `Failed`, atomically with its audit
row, so the user sees an explicit outcome and can restart it via CAS-016.

It also fixes the claim procedure. Its mirror is invalid T-SQL, and the deployed migration version
compiles only because its `ORDER BY` was commented out, so it claims in arbitrary rather than FIFO
order. Implements **FR-18** and **FR-20**, and supports **FR-19** (accurate
statuses), **FR-21** (a trustworthy `Completed` signal), and **FR-23** (audit of Worker-originated
terminal transitions). Also supports the BRD §8 NFRs for **Availability** ("task state must be durable
(e.g., persisted in SQL Server), not held only in browser or server memory") and **Concurrency** ("the
task queue … must be safe for this scale of concurrent access", 3–4 users).

---

## §2 User Story

As a **TraceCounts reviewer or Cachet business owner**, I want a search, content scan, or sync action
I submit to keep running after I close my browser, and to end in a definite outcome
(`Completed`/`Failed`) even if the server restarts. That way I never have to watch a tab for up to a
day (FR-22a), and a task never silently hangs in `Running`.

As the **background worker**, I want to claim each queued task exactly once, even with several
execution slots or instances polling at the same moment, and to recover cleanly from my own
restarts. That way no task runs twice and none is lost.

---

## §3 In-Scope

### §3.1 Enqueue (existing; documented as the contract, no code change)

Every queuing endpoint does the following, in order, within the HTTP request:

1. Validate the request. Validation is owned by the endpoint's own spec (CAS-006, CAS-007).
2. `ITaskQueueRepository.InsertAsync` → `dbo.task_queue_ins`: one row with `status = 'Queued'`,
   `submitted_by_user_id` = the authenticated user, and `request_parameters` = JSON from the request
   body.
3. Write the `*Submitted` audit row (FR-23, audit before response).
4. Return `202 Accepted` with the `taskId` (FR-18). The request never waits for a claim.

The Worker has no involvement until it claims the row. **Invariant, carried forward from CAS-004
§7 V1:** once a row leaves `Queued` it never re-enters `Queued`. A restart creates a *new* row
(CAS-016 Model B).

### §3.2 Claim (fix the SP; the contract is unchanged)

- Replace `dbo.task_queue_claim`'s body with the compile-verified FIFO form in §5. The contract
  stays identical: same parameter (`@updated_by`) and same `OUTPUT` columns. `TaskQueueRepository`
  and `ClaimedTaskRow` are therefore unchanged.
- Claim order: oldest `submitted_at` first, with ties broken by `task_queue_id` ascending. The design
  decision is FIFO (design §4, Decision Log #2); the tie-break is added so the order is deterministic.
- **Multi-claimer safety:** `READPAST` skips rows another claimer has locked, and `UPDLOCK`/`ROWLOCK`
  on the `TOP (1)` CTE make "select next + flip to Running" a single atomic statement. Two concurrent
  claimers (execution slots in one process, or two Worker processes) can never receive the same row.

### §3.3 Bounded-parallel execution (replaces the serial loop)

`Worker.ExecuteAsync` is restructured as below. This is pseudocode for the required behavior, not
code to copy verbatim.

```
await AcquireInstanceLockAsync(stoppingToken)        // §3.7 — standby until this instance holds the lock
await RunStartupSweepAsync(stoppingToken)            // §3.4 — must succeed before any claim
slots = new SemaphoreSlim(MaxDegreeOfParallelism)
inFlight = concurrent set of Tasks
while not stoppingToken.IsCancellationRequested:
    await slots.WaitAsync(stoppingToken)             // wait for a free slot
    if not await instanceLock.IsHeldAsync():         // §3.7 — lock lost → StopApplication, stop claiming
        slots.Release(); lifetime.StopApplication(); break
    claimed = ClaimNextAsync("worker-svc")           // own DI scope for the claim
    if claimed is null:
        slots.Release(); await Task.Delay(PollingInterval, stoppingToken); continue
    t = RunInSlotAsync(claimed, stoppingToken)       // own DI scope per task; do NOT await here
    inFlight.Add(t); t.ContinueWith(_ => { inFlight.Remove(t); slots.Release(); })
on stop: await Task.WhenAll(inFlight)                // let in-flight tasks observe cancellation
```

Rules:
- One `AsyncServiceScope` per claimed task, disposed when that task finishes. Scoped repositories
  are never shared across slots.
- As soon as a slot frees up, the loop claims again with no poll delay. The delay applies only when
  nothing was claimed. This lets a backlog drain at full parallelism.
- A slot is **always** released, whatever the outcome: success, failure, missing processor, or
  cancellation.
- Parallelism is per Worker process. With N processes the ceiling would be N × the limit, but
  production runs exactly one process (§3.7).

### §3.4 Durability across server restart: startup sweep

- **Durable state:** every lifecycle fact (`status`, `started_at`, `completed_at`, `result_detail`,
  `request_parameters`, `submitted_by_user_id`) lives only in `dbo.task_queue`. No task state is held
  in Worker memory beyond the currently executing call. This is why a browser close (FR-20) and a
  Web or Api restart have no effect on queued or running tasks.
- **What survives a restart, by state:**
  - `Queued`: untouched. It is claimed normally after the Worker starts.
  - `Completed`/`Failed`/`Cancelled`: terminal and untouched.
  - `Running`: orphaned. No process is executing it any more.
- **Sweep:** on `ExecuteAsync` start, **after acquiring the single-instance lock (§3.7) and before
  the first claim**, the Worker calls the new
  `dbo.task_queue_sweep_interrupted` (§5). In a single transaction, that procedure:
  1. flips every `Running` row to `Failed` with `completed_at` set and
     `result_detail = {"FailureReason":"Interrupted by a service restart - please restart or resubmit this task."}`;
  2. inserts one `dbo.audit_log` row per swept task (`action = 'TaskInterruptedByRestart'`,
     `result = 'Failure'`, `tab_module` = the task's own module, `user_id` = the submitter,
     `task_queue_id` populated).

  Because both writes commit together, no poller can ever observe a swept `Failed` status without
  its audit row (FR-23; design §12 "audit before visible").
- **No resume, no auto-resubmit.** The design keeps this position (§11 item 3). The user restarts the
  swept `Failed` task explicitly through CAS-016, which already accepts `Failed` rows. Without this
  sweep, CAS-016's restart feature cannot reach a crashed task at all.
- **Sweep failure:** if the sweep throws (e.g., SQL Server is not yet reachable at boot), the Worker
  logs an error, waits `PollingInterval`, and retries. **It never enters the claim loop until a sweep
  has succeeded.** Otherwise it could claim a task and then sweep its own newly claimed rows.
- **Graceful shutdown:** when the host stops, `stoppingToken` is cancelled. Processors already exclude
  `OperationCanceledException` from their failure handlers, so an interrupted task is deliberately
  **left `Running`** and is resolved by the next startup's sweep. The Worker awaits in-flight tasks
  (§3.3) so the process does not exit mid-write. This spec writes no status on shutdown, because the
  database may be the reason for the shutdown. The sweep is the single, tested recovery path.

### §3.5 Dispatch-boundary safety net (never leave a task `Running`)

In `RunInSlotAsync`:
- **No processor registered for `task_type`:** write an audit row (`TaskFailed`, `Failure`, the
  task's own module), then `UpdateStatusAsync(…, "Failed", {"FailureReason":"This task type is not
  supported by the worker."})`. Replaces the current warn-and-leave-`Running` behavior.
- **Non-cancellation exception escapes `ProcessAsync`:** catch it at the boundary and write the audit
  row (`TaskFailed`, `Failure`), then `Failed` with the fixed message
  `{"FailureReason":"Unexpected error while processing this task."}`. Log the exception server-side
  only. **Never** copy `ex.Message` or a stack trace into `result_detail` or the audit row (design §9.2).
- **Audit-first, fail-closed:** if the audit write itself throws, do **not** write `Failed`. Log at
  `Error` and leave the row `Running`, so the next startup sweep resolves it (it audits and fails it
  atomically). This preserves the FR-23 rule that a terminal status is never visible without its
  audit record.
- `result_detail` is always the JSON object `{"FailureReason": "<fixed text>"}`. It is shape-compatible
  with all three existing summary DTOs (`KeywordSearchResultSummaryDto`, `ContentScanResultSummaryDto`,
  `SyncActionResultSummaryDto`), each of which exposes `FailureReason`. The existing
  `KeywordSearchService`/`SyncScanService` `Deserialize<…>(resultDetail)?.FailureReason` reads keep
  working. A plain-text value would not.
- The task-type → `tab_module` mapping is `KeywordSearch` → `Keyword Search`; `ContentScan`,
  `SyncCopy`, `SyncUpdate`, `SyncPurge` → `O-Drive Sync`. It never uses `Task Manager` (design §9.3,
  API spec §8.1). The Worker's C# mapping and the sweep SP's `CASE` must agree.

### §3.6 Service hosting

- `Program.cs`: add `builder.Services.AddWindowsService(o => o.ServiceName = "Cachet Archive Sync Worker")`
  using the package `Microsoft.Extensions.Hosting.WindowsServices` 8.0.x. This is a no-op when the
  process runs as a console app or under a test host. It is what lets the Worker start automatically
  after a server reboot, which the "survives a server restart" requirement depends on end-to-end.
- Bind and validate the `Worker` options (§9) with `ValidateOnStart()`. An invalid value stops the
  service at startup rather than running with an unsafe setting.

### §3.7 Multi-worker model (the explicit answer to the WBS prompt)

| Layer | Concurrency mechanism | Safe? |
|---|---|---|
| Multiple execution slots in one Worker process (default 4, which covers 3–4 concurrent users) | Each slot claims via `dbo.task_queue_claim` (`READPAST`/`UPDLOCK`) | **Yes.** Proven by SC-3 |
| Multiple Worker *processes*: claim path | Same SP. Row locks work across connections, whatever the process. | **Yes.** Same test, two independent repositories/connections |
| Multiple Worker processes: startup sweep | The sweep fails **every** `Running` row, so it is only safe when no other instance is executing | **Yes, via the single-instance lock below.** A second process stands by and never sweeps or claims. |

**Single-instance lock (OQ-1, resolved 2026-09-24).** Enforces "exactly one active Worker per
database" in code instead of relying only on deployment discipline:

- `IWorkerInstanceLock` (Worker project) with `Task<bool> TryAcquireAsync(CancellationToken)`,
  `Task<bool> IsHeldAsync(CancellationToken)`, and `IAsyncDisposable`. The implementation,
  `SqlWorkerInstanceLock`, is registered as a singleton. It owns one **dedicated, non-pooled**
  `SqlConnection` (`Pooling=false`, so disposing it really ends the session and releases the lock),
  opened on the first acquire attempt and kept open for the process lifetime.
- Acquire calls `dbo.worker_instance_lock_acquire` (§5), a session-owned exclusive `sp_getapplock` on
  resource `CachetArchiveSync.Worker` with `@LockTimeout = 0`. Held calls
  `dbo.worker_instance_lock_held` (`APPLOCK_MODE … = 'Exclusive'`) on the same connection. Any
  exception from the held check counts as **not held**, which fails safe.
- **Startup:** if the lock isn't acquired, log a Warning ("another Worker instance is active;
  standing by") and retry every `PollingInterval`. The standby instance **never sweeps and never
  claims**. When the active instance's session ends (process exit or crash), the standby acquires
  the lock, sweeps that instance's orphaned `Running` rows, and takes over. This also covers
  blue/green overlap during a release.
- **Lock lost while running** (e.g., the lock connection dropped): stop claiming immediately, log
  Critical, and call `IHostApplicationLifetime.StopApplication()`. In-flight tasks are cancelled and
  left `Running` (the §3.4 shutdown semantics). The next instance to acquire the lock sweeps them.
  Windows Service recovery (§14) restarts the process.
- The lock is released only by closing the connection (dispose on host shutdown). It is never
  released explicitly while tasks are in flight.
- Verified on LocalDB 2026-09-24 with a draft of both procedures: session A acquired the lock and it
  stayed held after the procedure returned; session B was refused; B acquired it after A closed.

### §3.8 State Machine (diagram description)

```
                  POST /search | /sync/scan | /sync/actions/*        (Api; §3.1)
                                   │ dbo.task_queue_ins
                                   ▼
┌──────────────────────────── Queued ─────────────────────────────┐
│  durable row; survives browser close, Api/Web/Worker restart    │
└───────┬─────────────────────────────────────────────┬───────────┘
        │ dbo.task_queue_claim (Worker slot; §3.2)    │ cancel before claim
        │ READPAST/UPDLOCK, FIFO                      │ [future spec — §4]
        ▼                                             ▼
┌──────────────────────────── Running ────────────┐   Cancelled (terminal)
│  started_at set; executing in exactly one slot  │        ▲
└──┬──────────────────┬──────────────────┬────────┘        │ cooperative cancel
   │ processor         │ processor fails  │ Worker process   │ [future spec — §4]
   │ succeeds          │ / no processor / │ dies or stops    │
   │ (results written  │ exception at     │ (row orphaned)   │
   │  first, design §7)│ boundary (§3.5)  │                  │
   ▼                   ▼                  ▼                  │
Completed          Failed  ◄────── startup sweep (§3.4) ─────┘ (from Running only)
(terminal)         (terminal)       dbo.task_queue_sweep_interrupted
                       │
                       │ user restart (CAS-016) → inserts a NEW Queued row
                       ▼ with restarted_from_task_queue_id → original row unchanged
                   (new) Queued
```

**Transition table** (authoritative; the diagram illustrates it):

| # | From → To | Actor | Mechanism | Columns written | Audit (FR-23) |
|---|---|---|---|---|---|
| T1 | ∅ → `Queued` | Api | `dbo.task_queue_ins` | all insert columns, `status='Queued'` | `*Submitted`, before `202` |
| T2 | `Queued` → `Running` | Worker slot | `dbo.task_queue_claim` | `status`, `started_at`, `updated_*` | none (not an FR-23 event, design §12) |
| T3 | `Running` → `Completed` | Processor | `dbo.task_queue_upd` | `status`, `completed_at`, `result_detail` (summary JSON) | per-item audits by the processor (CAS-006/007) |
| T4a | `Running` → `Failed` | Processor | `dbo.task_queue_upd` | `status`, `completed_at`, `result_detail` | processor-owned (§15 OQ-3: follow-on) |
| T4b | `Running` → `Failed` | Worker dispatch boundary | `dbo.task_queue_upd` | same as T4a | `TaskFailed`, **before** the status write (§3.5) |
| T5 | `Running` → `Failed` | Worker startup | `dbo.task_queue_sweep_interrupted` | same as T4a | `TaskInterruptedByRestart`, **same transaction** |
| T6 | `Failed` → (new row) `Queued` | Api (CAS-016) | `dbo.task_queue_restart_ins` | new row; original untouched | `TaskRestart` (CAS-016) |
| T7 | `Queued`/`Running` → `Cancelled` | — | out of scope (§4) | — | — |

Terminal states (`Completed`, `Failed`, `Cancelled`) are never exited. No transition moves a row back
to `Queued`.

---

## §4 Out-of-Scope

- **Cancellation** (`POST /tasks/{taskId}/cancel`, `is_cancel_requested` polling, the `Cancelled`
  transition from `Running`). No cancel endpoint exists (`TaskManagerController` exposes only
  `restart`), and no code reads `is_cancel_requested`. `Cancelled` appears in the §3.8 state machine
  for completeness because the table allows it, but no code path in this spec writes it. Owned by a
  future Task Manager/cancel spec (design §10).
- **Job logic inside each processor** (search, content matching, B2 cross-check, sync actions) and any
  progress reporting: WBS Task 29. Per design §6 and UI/UX spec §5.2, **no progress/ETA column is
  added**. The `async-task-queue` skill's "periodically write progress" bullet is superseded by that
  later, client-confirmed decision.
- **The processors' own internal failure paths.** `KeywordSearchTaskProcessor` and the Sync processors
  currently write `Failed` without first writing a task-level audit row. That conflicts with design
  §9.3 but belongs to CAS-006 and CAS-007. It is flagged as a follow-on (§15 OQ-3: follow-on) rather than changed
  here (P3, smallest diff).
- **`dbo.error_log`.** Design §9.4/§11.2 reference it, but the table does not exist in this schema
  (no entity mirror, no migration). This spec uses structured `ILogger` output as the server-side
  exception sink. Creating `error_log` is a separate Database spec (§15 OQ-2: follow-on).
- Completion notification and Task Manager polling (WBS Task 30); the Task Manager UI (WBS Task 25);
  the list and get endpoints `GET /tasks`, `GET /tasks/{id}`.
- CAS-016 restart logic: consumed as-is, not changed.
- Checkpointed resume of partially executed work (rejected by the client, design §11.3).
- A heartbeat or lease column for detecting a stuck `Running` task while the Worker is still alive.
  Not in the design and not proposed here. The residual risk is recorded in §12.
- Windows Service installation and recovery settings (`sc.exe` failure actions, service account):
  deployment/runbook (WBS deployment task), not code.
- Editing merged specs CAS-004 and CAS-006, whose Appendix B/§5 examples contain the invalid claim
  pattern. This spec supersedes them in behavior. The document corrections are follow-on actions
  (§15 OQ-4: done).
- Depends on neither former BRD §12.2 item (login method, encryption-at-rest; both resolved in
  BRD v2.3), nor on open BRD §12.4 (web caching layer).

---

## §5 Database Objects

### Tables / Columns

N/A. No DDL. `dbo.task_queue` and `dbo.audit_log` are used as defined by CAS-003 and CAS-016.

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `task_queue_claim` | dbo | upd (claim) | **Body replaced; contract unchanged.** Fixes the invalid `UPDATE TOP … ORDER BY`. |
| `task_queue_sweep_interrupted` | dbo | upd (sweep) + audit ins | **New.** Atomic `Running`→`Failed` plus one audit row per task. Returns the swept rows. |
| `worker_instance_lock_acquire` | dbo | lock | **New.** `@acquired BIT OUTPUT`. Session-owned exclusive `sp_getapplock`, resource `CachetArchiveSync.Worker`, `@LockTimeout = 0`; `@acquired = 1` when the return code is ≥ 0. |
| `worker_instance_lock_held` | dbo | get | **New.** `@held BIT OUTPUT`: `APPLOCK_MODE(N'public', N'CachetArchiveSync.Worker', N'Session') = N'Exclusive'`. |

**`dbo.task_queue_claim`, required body.** Compile- and behavior-verified on LocalDB, 2026-09-24:
claimed the older of two `Queued` rows and ignored a `Running` row. db-author keeps the file's
existing header, validation, and `TRY/CATCH` conventions.

```sql
WITH next_task AS (
    SELECT TOP (1) *
    FROM   [dbo].[task_queue] WITH (READPAST, UPDLOCK, ROWLOCK)
    WHERE  status = N'Queued'
    ORDER BY submitted_at ASC, task_queue_id ASC
)
UPDATE next_task
SET    status     = N'Running'
      ,started_at = SYSUTCDATETIME()
      ,updated_by = @updated_by
      ,updated_at = SYSUTCDATETIME()
OUTPUT INSERTED.task_queue_id, INSERTED.task_type, INSERTED.submitted_by_user_id,
       INSERTED.request_parameters, INSERTED.submitted_at;
```

**`dbo.task_queue_sweep_interrupted`, required semantics.** A draft of this body was
compile-/behavior-verified on LocalDB, 2026-09-24. Two `Running` rows were swept with the correct
`tab_module` per type, `Queued`/`Completed` rows were untouched, and a second run swept 0 rows.

- Parameters: `@updated_by NVARCHAR(50)` (required), `@failure_reason NVARCHAR(400)` (required; the
  Worker passes the fixed text from §3.4).
- `result_detail` is built as `(SELECT @failure_reason AS FailureReason FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)`.
- Inside one transaction:
  1. `UPDATE dbo.task_queue … WHERE status = N'Running'` with `OUTPUT … INTO @swept`.
  2. `INSERT dbo.audit_log (…) SELECT … FROM @swept` with `action = N'TaskInterruptedByRestart'`,
     `result = N'Failure'`, `result_detail = @failure_reason`,
     `tab_module = CASE task_type WHEN N'KeywordSearch' THEN N'Keyword Search' ELSE N'O-Drive Sync' END`.
- After commit: `SELECT task_queue_id, task_type, submitted_by_user_id FROM @swept`. The Worker uses
  this for logging.
- Idempotent: running it with no `Running` rows is a no-op and returns an empty result set.

The two lock procedures must **not** wrap `sp_getapplock` in `BEGIN TRANSACTION`. A session-owned
lock must outlive the procedure call, and a transaction-owned lock would not.

### Migration Strategy

db-author writes both of the following:
- `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-028-task-queue-infrastructure.sql`: idempotent
  `CREATE OR ALTER` for all four procedures. It is append-only; the CAS-006 migration is **not** edited.
  `CREATE OR ALTER` works whether or not the broken CAS-006 batch ever created a `task_queue_claim`
  on a given database. It is **unknown** whether any shared environment currently has that procedure;
  the migration makes that question moot.
- Mirror updates: `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_claim.sql` (replaced
  body; add `Amended by` to the header) and new `dbo.task_queue_sweep_interrupted.sql`, `dbo.worker_instance_lock_acquire.sql`,
  `dbo.worker_instance_lock_held.sql`.
- Rollback section: restore neither broken body. Rollback drops `task_queue_sweep_interrupted` and both lock procedures.
  `task_queue_claim` stays at the fixed version, because no correct prior version exists (the mirror's form never compiled; the migration's form is not FIFO).

---

## §6 API Endpoints

N/A. No endpoint is added or changed. The existing queuing endpoints' contract is restated in §3.1
for completeness only. Endpoint drift (`endpoints snapshot/diff`) is expected to show zero changes.

---

## §7 Validations

> Accepted 2026-09-24 (§15). This spec has no HTTP surface, so "On Failure"
> describes Worker/SP behavior instead of an HTTP status.

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `Worker:PollingIntervalSeconds` is an integer in 1–60 | Host fails at startup (`OptionsValidationException`) and the Worker does not run | `Test_V1_WorkerOptions_PollingIntervalOutOfRange_FailsValidation` |
| V2 | `Worker:MaxDegreeOfParallelism` is an integer in 1–16 | Same as V1 | `Test_V2_WorkerOptions_ParallelismOutOfRange_FailsValidation` |
| V3 | The startup sweep must succeed before any claim | Error logged; retry after `PollingInterval`; `ClaimNextAsync` is never called until the sweep succeeds | `Test_V3_ExecuteAsync_SweepThrows_DoesNotClaimUntilSweepSucceeds` |
| V4 | `dbo.task_queue_sweep_interrupted`: `@updated_by`/`@failure_reason` must be non-blank | `RAISERROR` severity 16; no rows changed | `Test_V4_SweepProc_BlankParameters_Raises` |
| V5 | `result_detail` written by the Worker boundary or the sweep is always `{"FailureReason":…}` JSON with fixed text, never exception text | N/A (construction rule); verified by test | `Test_V5_DispatchBoundary_ProcessorThrows_ResultDetailIsFixedJsonWithoutExceptionText` |
| V6 | A claimed task whose `task_type` has no registered processor is failed, not left `Running` | Audit `TaskFailed`, then `Failed` | `Test_V6_ExecuteAsync_NoProcessorForTaskType_AuditsThenMarksFailed` |
| V7 | A Worker that has not acquired the instance lock never sweeps and never claims | Warning logged; lock acquisition retried every `PollingInterval` | `Test_V7_ExecuteAsync_LockNotAcquired_NeverSweepsOrClaims` |
| V8 | If `IsHeldAsync` returns false (or throws) while running, no further claim is made | Critical logged; `StopApplication()` called | `Test_V8_ExecuteAsync_LockLost_StopsClaimingAndStopsHost` |

---

## §8 UI / UX

N/A. Visible effect only: a task interrupted by a restart now shows as `Failed` with the reason
"Interrupted by a service restart - please restart or resubmit this task." Previously it stayed
`Running` indefinitely. CAS-016's Restart action then applies to it.

---

## §9 Configuration Changes

Worker `appsettings.json` only. These are tuning values, never credentials (FR-03).

| Key | Default | Description | Env Override |
|-----|---------|-------------|--------------|
| `Worker:PollingIntervalSeconds` | `5` | Delay between claim attempts when nothing is queued; also the sweep retry delay (design §5.1) | Yes (`Worker__PollingIntervalSeconds`) |
| `Worker:MaxDegreeOfParallelism` | `4` | Maximum tasks executing concurrently per Worker process (design §5.3). **Not load-validated.** Validate in WBS Task 36. | Yes (`Worker__MaxDegreeOfParallelism`) |

`Worker:TaskManagerPollIntervalSeconds` (design §13) is a Web-side key owned by WBS Task 30 and is not
added here.

---

## §10 Success Criteria

> Accepted 2026-09-24 (§15). Tests tagged *(real DB)* follow this project's
> `Skip`-marked real-SQL convention. **Because the last real-DB-only defect shipped undetected, this
> spec requires run evidence:** the implementer runs those tests un-skipped against LocalDB
> (`(localdb)\MSSQLLocalDB`, available on the dev host) with the full migration chain applied, and
> pastes the results into the §19 log before `/review`.

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | The CAS-028 migration applies without error (`sqlcmd -b`) on top of the existing chain, is idempotent on re-apply, and afterwards all four procedures exist. (The full chain cannot run error-free on LocalDB because CAS-003 needs full-text search, which LocalDB does not support; that pre-existing limitation is outside this spec.) | `Test_SC1_Cas026Migration_AppliesIdempotently_AllProcsExist` *(real DB)* |
| SC-2 | Claim returns the oldest `Queued` row by `submitted_at`, then `task_queue_id`, and sets `Running` + `started_at` | `Test_SC2_Claim_ReturnsOldestQueuedFirst_AndMarksRunning` *(real DB)* |
| SC-3 | 20 seeded tasks claimed by two concurrent claimers on separate connections: every task is claimed exactly once, with no duplicates and none missed | existing `TaskQueueClaimConcurrencyTests.Test_SC12_ConcurrentWorkersNeverDoubleClaimSameTask` + `SyncTaskQueueClaimConcurrencyTests`, **run un-skipped** *(real DB)* |
| SC-4 | With `MaxDegreeOfParallelism = 4` and 5 queued tasks whose processors block, exactly 4 run concurrently; the 5th is claimed only after one completes | `Test_SC4_ExecuteAsync_RespectsMaxDegreeOfParallelism` |
| SC-5 | A slot is released after each outcome (success, processor throw, no processor), so throughput is not permanently reduced | `Test_SC5_ExecuteAsync_SlotReleasedAfterEveryOutcome` |
| SC-6 | Sweep runs exactly once, before the first `ClaimNextAsync` call | `Test_SC6_ExecuteAsync_SweepRunsBeforeFirstClaim` |
| SC-7 | Sweep flips only `Running` rows to `Failed`, with `{"FailureReason":…}` and `completed_at`; `Queued`/`Completed`/`Failed`/`Cancelled` rows are unchanged | `Test_SC7_SweepProc_FailsOnlyRunningRows` *(real DB)* |
| SC-8 | Sweep writes exactly one `audit_log` row per swept task with the correct `user_id`, `tab_module`, `action`, `task_queue_id`, in the same transaction (a forced audit-insert failure rolls back the status change) | `Test_SC8_SweepProc_AuditRowPerTask_Atomic` *(real DB)* |
| SC-9 | End-to-end durability: enqueue → claim → abandon (simulated crash) → sweep → the task is `Failed` → CAS-016 restart returns `RESTARTED` and a new `Queued` row is claimable | `Test_SC9_CrashedTask_SweptToFailed_ThenRestartable` *(real DB)* |
| SC-10 | A processor exception at the dispatch boundary writes an audit row **then** `Failed`, in that order; `result_detail` contains no exception text | `Test_SC10_DispatchBoundary_ProcessorThrows_AuditThenFailed` |
| SC-11 | If the boundary audit write throws, no `Failed` status is written (fail-closed) and the slot is released | `Test_SC11_DispatchBoundary_AuditFails_StatusNotWritten` |
| SC-12 | On host stop, in-flight tasks are awaited, and no status write is attempted for tasks cancelled by shutdown | `Test_SC12_StopAsync_AwaitsInFlight_NoStatusWriteOnShutdown` |
| SC-13 | Instance lock is exclusive across sessions: while instance A holds it, B's acquire returns false and B's held check is false; after A is disposed, B acquires it | `Test_SC13_InstanceLock_ExclusiveAcrossSessions_ReleasedOnDispose` *(real DB)* |
| SC-14 | Standby takeover: B waits while A holds the lock, then after A releases it B acquires, sweeps, and begins claiming, in that order | `Test_SC14_ExecuteAsync_StandbyAcquiresThenSweepsThenClaims` |

---

## §11 Telemetry

```csharp
_logger.LogInformation("Worker started: MaxDegreeOfParallelism={Parallelism}, PollingIntervalSeconds={Poll}", …);
_logger.LogWarning("Startup sweep failed {Count} interrupted task(s): {TaskIds}", swept.Count, ids);   // only when Count > 0
_logger.LogError(ex, "Startup sweep failed; retrying in {Delay}s — no tasks will be claimed until it succeeds", …);
_logger.LogInformation("Claimed task {TaskId} ({TaskType}); {InFlight}/{Parallelism} slots busy", …);
_logger.LogError(ex, "Task {TaskId} ({TaskType}) failed at the dispatch boundary", …);
_logger.LogError(ex, "Audit write failed for task {TaskId}; leaving Running for the next startup sweep", …);
```

- Log only `task_queue_id`, `task_type`, counts, and durations. **Never log `request_parameters`**.
  It carries search terms and file IDs, and the existing log-hygiene tests already forbid raw terms.
  Also never log B2 key material, session values, or file content.
- The exception object goes only to `ILogger`, which stays server-side. It never goes to
  `result_detail` or `audit_log`.

---

## §12 Security Notes

> Threat model accepted 2026-09-24 (§15).

### B2 Credential Handling
No change. The Worker's queue harness never touches B2 credentials. Processors resolve the submitter's
server-side B2 capability per task, as today (CAS-001/CAS-018). `request_parameters`, `result_detail`,
audit rows, and log lines written by this spec contain no key material (FR-03). The fixed failure
strings in §3.4/§3.5 are constants by construction.

### Audit Trail
- Every Worker-originated terminal transition added by this spec is audited: T4b `TaskFailed` (audit
  written first, fail-closed) and T5 `TaskInterruptedByRestart` (same transaction).
- New `AuditAction` members `TaskFailed` and `TaskInterruptedByRestart` are **appended** to
  `Cachet.ArchiveSync.Core/Enums/AuditAction.cs` without reordering existing members, because
  `Enum.ToString()` is the stored `audit_log.action` literal (CAS-005 §15). The sweep SP's literal
  must match `nameof(AuditAction.TaskInterruptedByRestart)` exactly; a test asserts this.
- `audit_log` stays append-only. Nothing here updates or deletes audit rows.

### Session / Auth Requirements
The Worker has no HTTP surface and no user session. It acts under the fixed service identity
`worker-svc` (the existing `updated_by` value) and attributes audit rows to `submitted_by_user_id`.
The Windows Service account should be least-privilege: `EXECUTE` on the required procedures, and
read access to the mirror directories. That is a runbook item (§14).

### Residual Risks (accepted or flagged, not solved here)
| Risk | Impact | Mitigation / owner |
|---|---|---|
| A second Worker instance is started against the same DB | Without a guard, its sweep would fail the first instance's live tasks while they keep executing | **Mitigated:** the single-instance lock (§3.7). The second instance stands by. |
| The lock connection drops while the Worker process stays up | Another instance could acquire the lock while this one still executes tasks | The held check runs before every claim, and loss triggers `StopApplication()`. Residual window: tasks already executing continue until they observe cancellation (at most the host shutdown timeout). Accepted at this scale. |
| A terminal status write fails while the Worker stays up (DB outage mid-task) | The task shows `Running` until the next Worker restart | Accepted per design (no heartbeat). Operational: restart the Worker service after a DB outage. |
| An interrupted `SyncCopy`/`SyncUpdate`/`SyncPurge` is restarted via CAS-016 | The restarted task re-evaluates files already processed before the crash | Processor-level idempotency is WBS Task 29/CAS-007's concern. Flagged for that spec's owner. |
| Parallelism default 4 not load-tested | Disk/B2 contention under the real ~500 GB mirror | Config-tunable; validate in WBS Task 36 (design §5.3). |

---

## §13 Test Plan

### Unit Tests (`Cachet.ArchiveSync.Worker.Tests`, xUnit + NSubstitute + FluentAssertions)
| Class | Method | Proves |
|-------|--------|--------|
| `WorkerTests` (extend) | `Test_SC4_…`, `Test_SC5_…`, `Test_SC6_…`, `Test_SC10_…`, `Test_SC11_…`, `Test_SC12_…`, `Test_V3_…`, `Test_V5_…`, `Test_V6_…` | Slot model, sweep ordering, boundary safety net, shutdown |
| `WorkerTests` (existing) | `Test_ExecuteAsync_ClaimedTask_DispatchesToMatchingProcessor`, `Test_ExecuteAsync_NoTaskClaimed_WaitsWithoutThrowing` | Must still pass. Update fixtures to stub the sweep call and inject options. |
| `WorkerOptionsTests` (new) | `Test_V1_…`, `Test_V2_…`, `Test_WorkerOptions_Defaults_Are5And4` | Binding, validation, and defaults |
| `WorkerTests` (extend) | `Test_V7_…`, `Test_V8_…`, `Test_SC14_…` | Single-instance lock behavior (mocked `IWorkerInstanceLock`) |
| `WorkerTaskModuleMapTests` (new) | `Test_TaskTypeToTabModule_MatchesSweepProcCase`, `Test_TaskInterruptedByRestart_EnumNameMatchesSqlLiteral` | C# ↔ SQL literal agreement (§3.5, §12) |

The sweep and claim are called through `ITaskQueueRepository` (add `SweepInterruptedAsync(string
updatedBy, string failureReason, CancellationToken ct)` returning the swept rows), so the unit tests
mock them.

### Integration Tests (`Cachet.ArchiveSync.Api.IntegrationTests`, real SQL, existing `Skip` convention)
| Class | Method | Proves |
|-------|--------|--------|
| `TaskQueueClaimTests` (new) | `Test_SC1_…`, `Test_SC2_…` | Claim SP compiles and is FIFO |
| `TaskQueueClaimConcurrencyTests`, `SyncTaskQueueClaimConcurrencyTests` (existing) | as-is | SC-3: no double claim |
| `TaskQueueSweepTests` (new) | `Test_SC7_…`, `Test_SC8_…`, `Test_V4_…` | Sweep semantics and atomicity |
| `TaskQueueDurabilityTests` (new) | `Test_SC9_…` | Crash → sweep → CAS-016 restart, end to end |
| `WorkerInstanceLockTests` (new, `Cachet.ArchiveSync.Worker.Tests`, real DB, `Skip` convention) | `Test_SC13_…` | Real `sp_getapplock` exclusivity and release |

### Coverage Target
Changed files ≥ 85% line coverage (`Worker.cs`, `WorkerOptions.cs`, `TaskQueueRepository.cs` delta).

---

## §14 Rollout

1. Apply the CAS-028 migration first. It is backward-compatible: the claim contract is unchanged,
   and the sweep SP is only called by the new Worker.
2. Deploy the Worker. Api and Web need no redeploy, since there is no contract change.
3. Install and run the Worker as a Windows Service. Plan for **one active instance per environment**;
   a second instance is harmless (it stands by on the §3.7 lock). Configure it set
   to automatic start with service-recovery restart on failure (runbook, WBS deployment task).
4. After the first start, verify that the log line "Worker started…" appears and that any
   pre-existing `Running` rows were swept, each with an audit row.
5. **Rollback:** redeploy the previous Worker build. The fixed claim SP stays in place; the old Worker
   calls it with the same contract, and it now claims in FIFO order. Drop
   `task_queue_sweep_interrupted` only if required. No data rollback is needed, because swept rows are
   legitimately `Failed` and audited.

---

## §15 Open Questions

All items were resolved on 2026-09-24 by the requester (Ratheesh), accepting the author's
recommendations ("go as per recommendation").

- [x] **OQ-1: Enforce single-instance?** **Yes.** A session-scoped `sp_getapplock` guard is added to
  scope (§3.7, §5, V7, V8, SC-13, SC-14).
- [x] **OQ-2: `dbo.error_log`.** **Structured `ILogger` output is the server-side exception sink** for
  now. Creating `dbo.error_log` is a separate future Database spec (follow-on, not raised here).
- [x] **OQ-3: Processor-level failure audit.** **Out of scope for CAS-028.** Raise a CAS-006/CAS-007
  change spec so processors audit before writing `Failed` (follow-on).
- [x] **OQ-4: Document corrections.** **Approved.** Dated erratum notes were added to CAS-004
  Appendix B and to `CACHET_AsyncProcessingEngine_Design_v1.md` §4, pointing to CAS-028 §5.
- [x] **OQ-5: Validation bounds.** **Confirmed:** `PollingIntervalSeconds` 1–60,
  `MaxDegreeOfParallelism` 1–16.
- [x] Poll interval 5 s, parallelism 4, FIFO, sweep-to-`Failed` with no resume: resolved in design
  §15/§16 (2026-09-09, Rakesh Devarajan) and carried forward.
- Not dependent on BRD §12.4 (web caching layer, O-Drive refresh cadence).

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code (for Ratheesh, ratheesh@scientificvision.net) | 2026-09-24 | §15 resolved per author recommendations; §7/§10/§12 accepted as proposed |
| Reviewer | | | |

---

## §17 Graph Impact

### God Nodes in Blast Radius
None of the top-10 graph god nodes (`GRAPH_REPORT.md`) is modified. `ILogger` (27 edges) is used, not
changed. `KeywordSearchTaskProcessorTests` (29 edges) is not touched; the processors are out of scope.

### Communities / Modules Affected
- **Projects:** Worker (`Worker.cs`, `Program.cs`, new `WorkerOptions.cs`, `appsettings.json`,
  csproj package ref), Core (`AuditAction.cs` +2 members, `ITaskQueueRepository` +1 method), Api
  (`TaskQueueRepository.cs` +1 method; Api is the host of the shared Dapper repositories, and its HTTP
  behavior is unchanged), Database (1 migration, 2 SP mirrors). **Web: none.**
- **Modules:** Task Manager (restartability of crashed tasks), Audit Log (2 new actions), with an
  indirect effect on Keyword Search and O-Drive Sync (their tasks now reach a terminal state reliably).
- Estimated changed files: about 15 (the lock adds `IWorkerInstanceLock.cs` and
  `SqlWorkerInstanceLock.cs`), at the P9 threshold of 15. §6 has 0 endpoints, §7 has 8 validations,
  §10 has 14 criteria, and §5 has 4 SPs. Only one size threshold (§10 > 12) is crossed, and P9 needs
  two. **Decomposition check: no split signal fired.
  Single spec.**

### Surprising Connections
1. **`dbo.task_queue_claim` is not FIFO, and its mirror never compiled.** The invalid
   `UPDATE TOP … ORDER BY` pattern came from CAS-004 Appendix B. Someone later commented out the
   `ORDER BY` in the merged CAS-006 migration (edited 2026-09-24 12:11 IST) instead of fixing it, so
   the deployed proc silently lost FIFO ordering. Every real-DB claim test is `Skip`-marked, so neither
   problem was caught.
2. **CAS-016 restart is unreachable for crashed tasks.** It requires `Failed`, but nothing ever moves
   an orphaned `Running` row to `Failed`.
3. **`result_detail` is a typed JSON contract, not free text.** API services deserialize it into
   per-type summary DTOs, so the sweep has to emit JSON.

### Graphify Queries Run
```
py -3 -m graphify query "task queue claim background worker restart sweep concurrency" --budget 1500
   → BFS from Worker / TaskQueueRestartClaimTests / TaskQueueClaimConcurrencyTests, 118 nodes (mostly test classes)
py -3 -m graphify explain "Worker"
   → resolved to a graphify-internal node (extract.py); not useful — the name collides with the tool's own code
```
The graph adds little beyond locating the relevant files. The findings above come from a manual read
of `Worker.cs`, `Program.cs`, `IWorkerTaskProcessor.cs`, `TaskQueueRepository.cs`,
`ITaskQueueRepository.cs`, the task-queue SP mirrors and CAS-006 migration, the processors' failure
paths, and `WorkerTests.cs`, plus two LocalDB compile checks.
Tooling note: `python -m cachet_agent_tools` fails to start on the dev host's Python 3.14. A help
string at `cachet_agent_tools/__main__.py:64` contains an unescaped `%`, which 3.14's argparse rejects.
The spec ID was minted by calling `cachet_agent_tools.spec._cmd_next_id` directly (the same logic), not
by hand. **Fixed 2026-09-24** (at the requester's go-ahead): `%` escaped to `%%` in that help string,
and the CLI now starts on 3.14.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test, and *(real DB)* tests have pasted LocalDB run
      evidence in §19
- [ ] All §7 Validations are implemented and tested
- [ ] §6 N/A: `endpoints diff CAS-028` shows no change
- [ ] §12: no exception text or `request_parameters` in `result_detail`, audit rows, or logs; audit
      before or with every Worker terminal write; enum literal matches the SQL literal
- [ ] §5: both SPs exist; the claim SP contract is unchanged; the CAS-006 migration file is not edited
- [ ] §9: both config keys are present with the stated defaults and validated on start
- [ ] §11 telemetry is emitted with no forbidden fields
- [ ] §17 matches the actual change set (no Web changes; no processor changes)
- [ ] Single-instance lock: a standby instance never sweeps or claims; losing the lock stops the host
- [ ] No §4 out-of-scope item was implemented (especially cancellation, progress columns, `error_log`)

### Verification Report Format
The reviewer writes `specs/misc/CAS-028.review.md` with: overall verdict (APPROVED | APPROVED WITH
NOTES | CHANGES REQUIRED | BLOCKED), a per-SC verdict table, a security gap list, and scope violations.

## Implementation Log

**Completed:** 2026-09-24
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage Worker.cs 97.5%, WorkerOptions.cs 100%, SqlWorkerInstanceLock.cs 88.6% (LocalDB run); TaskQueueRepository.cs real-DB-only (below 85% whole-file) | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20260924_090000__CAS-028-task-queue-infrastructure.sql` | New: CREATE OR ALTER task_queue_claim (FIFO), task_queue_sweep_interrupted, worker_instance_lock_acquire/_held |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_claim.sql` | Mirror: FIFO READPAST/UPDLOCK CTE claim; Amended-by header |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_sweep_interrupted.sql` | Mirror (new): atomic Running->Failed + audit row per task |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.worker_instance_lock_acquire.sql` | Mirror (new): session-owned sp_getapplock |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.worker_instance_lock_held.sql` | Mirror (new): APPLOCK_MODE held check |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | Appended TaskFailed, TaskInterruptedByRestart |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs` | Added SweepInterruptedAsync |
| `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs` | Added SweepInterruptedAsync (dbo.task_queue_sweep_interrupted) |
| `src/Cachet.ArchiveSync.Worker/Worker.cs` | Instance lock, startup sweep, bounded-parallel slots, dispatch-boundary safety net |
| `src/Cachet.ArchiveSync.Worker/WorkerOptions.cs` | New: PollingIntervalSeconds (1-60), MaxDegreeOfParallelism (1-16) |
| `src/Cachet.ArchiveSync.Worker/IWorkerInstanceLock.cs` | New: single-instance lock abstraction |
| `src/Cachet.ArchiveSync.Worker/SqlWorkerInstanceLock.cs` | New: non-pooled dedicated-connection sp_getapplock lock |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | AddWindowsService, options ValidateOnStart, lock registration |
| `src/Cachet.ArchiveSync.Worker/Cachet.ArchiveSync.Worker.csproj` | Microsoft.Extensions.Hosting.WindowsServices 8.0.x |
| `src/Cachet.ArchiveSync.Worker/appsettings.json` | Worker:PollingIntervalSeconds=5, Worker:MaxDegreeOfParallelism=4 |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `WorkerTests` | `Test_SC4_ExecuteAsync_RespectsMaxDegreeOfParallelism` | SC-4 |
| `WorkerTests` | `Test_SC5_ExecuteAsync_SlotReleasedAfterEveryOutcome` | SC-5 |
| `WorkerTests` | `Test_SC6_ExecuteAsync_SweepRunsBeforeFirstClaim` | SC-6 |
| `WorkerTests` | `Test_SC10_DispatchBoundary_ProcessorThrows_AuditThenFailed` | SC-10 |
| `WorkerTests` | `Test_SC11_DispatchBoundary_AuditFails_StatusNotWritten` | SC-11 |
| `WorkerTests` | `Test_SC12_StopAsync_AwaitsInFlight_NoStatusWriteOnShutdown` | SC-12 |
| `WorkerTests` | `Test_SC14_ExecuteAsync_StandbyAcquiresThenSweepsThenClaims` | SC-14 |
| `WorkerTests` | `Test_V3_ExecuteAsync_SweepThrows_DoesNotClaimUntilSweepSucceeds` | V-3 |
| `WorkerTests` | `Test_V5_DispatchBoundary_ProcessorThrows_ResultDetailIsFixedJsonWithoutExceptionText` | V-5 |
| `WorkerTests` | `Test_V6_ExecuteAsync_NoProcessorForTaskType_AuditsThenMarksFailed` | V-6 |
| `WorkerTests` | `Test_V7_ExecuteAsync_LockNotAcquired_NeverSweepsOrClaims` | V-7 |
| `WorkerTests` | `Test_V8_ExecuteAsync_LockLost_StopsClaimingAndStopsHost` | V-8 |
| `WorkerTests` | `Test_ExecuteAsync_LockAcquireThrows_RetriesWithoutSweepingOrClaiming` | coverage (sec 3.7) |
| `WorkerTests` | `Test_ExecuteAsync_SweepReturnsInterruptedTasks_LogsWarningAndProceedsToClaim` | coverage (sec 11) |
| `WorkerTests` | `Test_ExecuteAsync_ClaimThrows_ReleasesSlotLogsAndRetries` | coverage (sec 3.3) |
| `WorkerTests` | `Test_ExecuteAsync_SlotErrorOutsideProcessorBoundary_LogsReleasesSlotNoFailedStatus` | coverage (sec 3.5) |
| `WorkerTests` | `Test_DispatchBoundary_StatusWriteFailsAfterAudit_LogsErrorAndReleasesSlot` | coverage (sec 3.5) |
| `WorkerOptionsTests` | `Test_V1_WorkerOptions_PollingIntervalOutOfRange_FailsValidation` | V-1 |
| `WorkerOptionsTests` | `Test_V2_WorkerOptions_ParallelismOutOfRange_FailsValidation` | V-2 |
| `WorkerTaskModuleMapTests` | `Test_TaskTypeToTabModule_MatchesSweepProcCase` | sec 3.5 |
| `WorkerTaskModuleMapTests` | `Test_TaskInterruptedByRestart_EnumNameMatchesSqlLiteral` | sec 12 |
| `TaskQueueClaimTests` | `Test_SC1_Cas026Migration_AppliesIdempotently_AllProcsExist` | SC-1 (real DB, LocalDB pass) |
| `TaskQueueClaimTests` | `Test_SC2_Claim_ReturnsOldestQueuedFirst_AndMarksRunning` | SC-2 (real DB, LocalDB pass) |
| `TaskQueueClaimConcurrencyTests` | `Test_SC12_ConcurrentWorkersNeverDoubleClaimSameTask` | SC-3 (real DB, LocalDB pass) |
| `SyncTaskQueueClaimConcurrencyTests` | `Test_SC13_ConcurrentWorkersNeverDoubleClaimSyncTasks` | SC-3 (real DB, LocalDB pass) |
| `TaskQueueSweepTests` | `Test_SC7_SweepProc_FailsOnlyRunningRows` | SC-7 (real DB, LocalDB pass) |
| `TaskQueueSweepTests` | `Test_SC8_SweepProc_AuditRowPerTask_Atomic` | SC-8 (real DB, LocalDB pass) |
| `TaskQueueSweepTests` | `Test_V4_SweepProc_BlankParameters_Raises` | V-4 (real DB, 6 cases, LocalDB pass) |
| `TaskQueueDurabilityTests` | `Test_SC9_CrashedTask_SweptToFailed_ThenRestartable` | SC-9 (real DB, LocalDB pass) |
| `WorkerInstanceLockTests` | `Test_SC13_InstanceLock_ExclusiveAcrossSessions_ReleasedOnDispose` | SC-13 (real DB, LocalDB pass) |

### Migration
`20260924_090000__CAS-028-task-queue-infrastructure.sql`

### Real-DB Run Evidence (§10 requirement)

- **Target:** throwaway LocalDB database `CasTest028` on `(localdb)\MSSQLLocalDB`. The full `Database/migrations`
  chain was applied with `sqlcmd -I` (no `-b`). Only the known pre-existing errors occurred: CAS-003 Msg 9982/7609 (no
  full-text on LocalDB), CAS-002 Msg 207, and CAS-006 addendum Msg 7601. There was also one Msg 911 per file, from
  `USE [CachetArchiveSync]` on a differently named DB, and each batch still ran in `CasTest028`. The seed user was a
  placeholder row with dummy values (`user_id` 100000).
- **SC-1 (shell level):** the CAS-028 migration was re-targeted with `USE [CasTest028]` and applied twice with
  `sqlcmd -I -b`. Both passes returned exit 0 with no errors. All four procedures existed afterwards.
- **Un-skipped run, 2026-09-24 17:39 IST**, against the code as of that time. `Skip` was removed temporarily, then
  restored, and each file was verified byte-identical to its pre-run backup.
  ```
  Passed TaskQueueClaimConcurrencyTests.Test_SC12_ConcurrentWorkersNeverDoubleClaimSameTask
  Passed TaskQueueClaimTests.Test_SC2_Claim_ReturnsOldestQueuedFirst_AndMarksRunning
  Passed TaskQueueClaimTests.Test_SC1_Cas026Migration_AppliesIdempotently_AllProcsExist
  Passed TaskQueueDurabilityTests.Test_SC9_CrashedTask_SweptToFailed_ThenRestartable
  Passed TaskQueueSweepTests.Test_V4_SweepProc_BlankParameters_Raises   (x6 cases)
  Passed TaskQueueSweepTests.Test_SC8_SweepProc_AuditRowPerTask_Atomic
  Passed TaskQueueSweepTests.Test_SC7_SweepProc_FailsOnlyRunningRows
  Passed SyncTaskQueueClaimConcurrencyTests.Test_SC13_ConcurrentWorkersNeverDoubleClaimSyncTasks
  Passed WorkerInstanceLockTests.Test_SC13_InstanceLock_ExclusiveAcrossSessions_ReleasedOnDispose
  Api.IntegrationTests: Passed 13 / Failed 0;  Worker.Tests: Passed 1 / Failed 0
  ```
  An identical earlier run at 17:11 also passed 14/14. It was repeated because another session edited
  `TaskQueueRepository.cs` at 17:24.

### Gate Notes for the Reviewer

- **Concurrent work in the same tree.** While these gates ran, two other sessions were implementing CAS-029 and CAS-030
  in the same working copy. Both specs were `in-progress`. They touched files this spec also owns:
  `WorkerOptions.cs`, `Program.cs`, `ITaskQueueRepository.cs`, and `TaskQueueRepository.cs`. They also touched the
  processors and `appsettings.json`. The repo is not under git, so the gates certify the **combined tree as of
  17:33–17:39 IST**, not a CAS-028-only diff. The §18 checklist item "§17 matches the actual change set" must be
  judged by reading each file for CAS-028 content only.
- **Gate 2:** 611 passed, 0 failed (Api.Tests 286, Api.IntegrationTests 76 + 59 skipped, Worker.Tests 109 + 1 skipped,
  Web.Tests 140). Tests ran on the `-c Release` build because Visual Studio locks the Debug outputs (Api and Web
  running). In an earlier full run, the Web test `KeywordSearchPageTests.Test_V4_SendToQuarantineDisabledUntilRowChecked`
  failed once. It then passed 3/3 when run alone and in two full re-runs. CAS-028 has no Web changes, so this looks
  like a timing flake under host load, not a regression.
- **Gate 3:** `dotnet format --verify-no-changes` was run per project (Core, Api, Worker, Worker.Tests,
  Api.IntegrationTests), because the whole-solution run crashes on this host. All exited 0.
- **Gate 4 (soft):** Worker.cs 97.5% and WorkerOptions.cs 100% (unit runs). SqlWorkerInstanceLock.cs was 88.6%, but only
  in the LocalDB run, because its tests are Skip-marked. TaskQueueRepository.cs is **below 85% whole-file**: its methods
  are exercised only by Skip-marked real-DB suites, and it now also holds CAS-029/030 methods. Five unit tests were added
  after the first Gate 4 result to cover Worker.cs catch branches.
- **Gate 5:** N/A (`touches.api: false`). No endpoint snapshot was taken.
- **Naming nit:** the SC-1 test name in §10 and in code says `Cas026Migration`, not `Cas028Migration`. The code matches
  the spec as written, and it was left unchanged.
