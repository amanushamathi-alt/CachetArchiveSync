---
id: CAS-029
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-24
priority: P1
related_specs: [CAS-001, CAS-006, CAS-007, CAS-012, CAS-013, CAS-016, CAS-028]
depends_on: [CAS-028]
touches:
  database: true
  api: false
  ui: false
  worker: true
  b2: true
  config: true
  security: true
migrations: [20260924_120000__CAS-029-long-running-job-execution-progress.sql]
---

# CAS-029 — Long-Running Search/Scan Job Execution and Progress Tracking

**WBS traceability:** `1.WBS/CACHET_WBS_v1.md` Task 29, "Long-running search/scan job execution and
progress tracking", category Asynchronous Processing Engine.
**Design sources:** `0.Specifications/CACHET_AsyncProcessingEngine_Design_v1.md` §2, §5.2, §6, §7, §9;
`specs/api/cachet-archive-sync/CAS-006-keyword-search-api.md` and `CAS-012` (Keyword Search job);
`specs/api/cachet-archive-sync/CAS-007-o-drive-sync-content-scan-api.md` and `CAS-013` (Content Scan
and sync-action jobs); `specs/misc/CAS-028-task-queue-infrastructure.md` (the harness these jobs run
inside).

## §0 Reading Guide: What Already Exists vs. What This Spec Adds

WBS Task 29 asks for four things. Three of them already exist in code, built by CAS-006/007/012/013,
which each implemented the job logic their endpoints needed. This spec **records those three as the
binding job-execution contract** without changing their behavior. It **builds the fourth, progress
tracking**, which does not exist anywhere today. The table below reflects a manual code read on
2026-09-24.

| WBS Task 29 ask | Current state (verified) | This spec |
|---|---|---|
| How the local index is searched | `KeywordSearchTaskProcessor` → `dbo.file_index_search`. Filename candidates are pre-filtered by SQL `LIKE`. Content candidates are every active in-scope file, matched in Worker memory against the **physical mirror file** (BRD §12.3 item 1, CAS-012). `ContentScanTaskProcessor` compares O-Drive vs. B2 `file_index` metadata in memory. | Documented as the contract (§3.1, §3.2). No behavior change, except that ContentScan's loops are restructured without changing their output (§3.2). |
| How each candidate is cross-checked against B2 | `IB2Client.ListFileNamesAsync(relativePath, 1)`: exact name match and `action = "upload"`, system key, single configured bucket (CAS-001, CAS-006 deviation note, CAS-013 §4). Excluded candidates are soft-deleted and audited **before** any result row is written. | Documented as the contract (§3.1). No change. |
| Progress periodically written back to the task record | **None.** No column, no SP, no Worker write. A task shows bare `Running` for up to a day. | **New:** 3 nullable columns, 1 guarded SP, a throttled `TaskProgressReporter`, and wiring into all 5 processors (§3.3–§3.5, §5). |
| Final result set attached on completion | Per-item `search_result`/`sync_result` rows are written as the job runs, then the summary JSON goes into `result_detail` with `Completed`. The ordering is correct but is not stated or tested as an invariant. | Stated as an invariant (§3.6), with ordering tests (SC-5). |

### §0.1 Design reversal this spec depends on (why `status` stays `draft`)

The Async Engine design §2/§6 (Decision Log #1) and UI/UX spec §5.2 both reject a progress column:
*"no percentage/ETA is specified by the BRD; do not fabricate one."* The design itself names the way
back: *"If per-item progress is wanted in the future, it is an additive, backward-compatible change (a
new nullable column plus a UI decision reversal)."*

On 2026-09-24 the requester (Ratheesh, TraceCounts delivery) chose **measured counts only**:

- items processed and items total, both **counted** by the Worker, never estimated;
- **no ETA** and **no stored percentage**; a percentage, if any UI shows one, is derived from the
  two counts at display time;
- this narrows, rather than contradicts, the "do not fabricate" rule, because nothing here is
  extrapolated.

This still reverses a recorded, client-confirmed design decision, so **§15 OQ-1 (client/design-owner
sign-off) blocks `status → ready`**. If sign-off is refused, §3.3–§3.5 and §5 are removed, and the
spec shrinks to §3.1/§3.2/§3.6 as a documentation-plus-ordering-tests spec.

---

## §1 Summary

This spec defines the execution logic that runs inside a Worker slot for each queued task type
(`KeywordSearch`, `ContentScan`, `SyncCopy`, `SyncUpdate`, `SyncPurge`), a job that can take from
10 minutes to a full day (FR-22a). It fixes four contracts: how the local index and mirror files are
searched, how candidates are cross-checked against B2, how measured progress (`items_processed` of
`items_total`) is periodically written back to `dbo.task_queue`, and how the final result set is
attached before `Completed` becomes visible. The first two and the fourth are as-built behavior,
recorded here as the binding contract. Progress tracking is new. It gives the Task Manager (FR-19)
something truthful to show besides `Running`, and gives operators a last-activity timestamp for a
long job. Implements the Worker side of **FR-05–FR-08** (local-first search, B2 cross-check),
**FR-10/FR-11** (Content Scan), **FR-13c** (queued sync actions), **FR-19** (task status detail), and
**FR-21** (a `Completed` signal that is safe to fetch results on). Supports **FR-22a** and the BRD §8
Performance/Availability NFRs.

---

## §2 User Story

As a **TraceCounts reviewer or Cachet business owner**, I want a long-running search, scan, or sync
action to show how much of its work it has finished (for example, *"12,400 of 88,210 candidates
checked"*), so that I can tell a slow job apart from a stuck one without guessing.

As the **background worker**, I want one small, throttled way to report progress that can never
fail or distort the job itself, so that progress stays advisory and the result rows and audit log
remain the only record of truth.

---

## §3 In-Scope

### §3.1 KeywordSearch job (as-built contract; no behavior change except progress hooks)

Sequence inside `KeywordSearchTaskProcessor.ProcessAsync`:

1. **Deserialize** `request_parameters` → `KeywordSearchRequestDto { Term, Bucket, Extension }`. If it
   is empty or malformed, the task becomes `Failed` (existing catch path).
2. **Local index query:** `IFileIndexRepository.SearchCandidatesAsync(bucket, extension, term)` →
   `dbo.file_index_search`. It returns two candidate kinds, active rows only:
   - `Filename`: the term is already confirmed by SQL `LIKE` on `file_name`/`relative_path`.
   - `Content`: **every** active in-scope-extension row for the bucket, unfiltered. Content matching
     is never done in SQL (BRD §12.3 item 1; CAS-012 §2).
   A file can appear once as each kind. **The unit of work is the candidate row, not the file.**
3. **Progress start (new):** `reporter.StartAsync(itemsTotal: candidates.Count)` (§3.4).
4. **Per candidate**, bounded by `B2:CrossCheckMaxConcurrency` (default 8, existing):
   - a. `Content` only: read the physical mirror file (`IMirrorFileReader`, source-aware per
     CAS-011/012), then `IFileContentMatcher.TryMatchAsync`. A `null` result (non-match, unreadable
     file, unsupported type) → **drop silently**: no B2 call, no row, no audit (CAS-012 V10/V11).
   - b. **B2 cross-check (FR-08):** `IB2Client.ListFileNamesAsync(relativePath, 1)`. The candidate
     is confirmed only if the first returned name equals `relativePath` exactly (ordinal) **and**
     `action == "upload"`. `B2NotFoundException` → not confirmed. Any other `B2ApiException`
     propagates and fails the task (design §9).
   - c. Confirmed → `search_result` insert (`Filename`→`Flagged`; `Content`→`Needs Review` +
     snippet).
   - d. Not confirmed → `file_index` soft-delete (self-heal), **then** a `CrossCheckExcluded` audit
     row. No `search_result` row is ever written for it (CAS-006 §12).
   - e. **Progress (new):** `reporter.ItemCompletedAsync()` exactly once per candidate, after its
     disposition (dropped, confirmed, or excluded) is durably written.
5. **Completion:** after every candidate has finished → `reporter.CompleteAsync()` → `task_queue_upd`
   `Completed` with `KeywordSearchResultSummaryDto` JSON (§3.6).

B2 credential posture (unchanged): the Worker uses the server-held **system key** B2 client, scoped
to the single configured bucket (CAS-001 §7 V1; CAS-013 §4). It never uses a per-user key; CAS-018
limits per-user key checks to synchronous Permanent Delete.

### §3.2 ContentScan job (as-built comparison; loop restructured, output unchanged)

1. Load all active `file_index` rows for `source = 'ODrive'` and `source = 'B2'` into two maps keyed by
   `(bucket_name, relative_path)` (existing; `UQ_file_index_source_bucket_path` guarantees unique keys
   per source). **No B2 call and no file read:** this is a metadata comparison between the two
   mirrors (API design §5.1).
2. **Unit of work = one distinct key in the union of both maps.** `itemsTotal = |ODrive keys ∪ B2
   keys|`. Then `reporter.StartAsync(itemsTotal)`. During step 1 `items_total` is still `NULL`,
   which is an honest "not yet counted".
3. **Restructure:** the current three passes (New, Removed, Modified) become two passes that visit
   each key exactly once, so that progress counts each key once:
   - Pass A, over the O-Drive keys: absent in B2 → `New`. Present in B2 with both hashes non-null and
     unequal → `Modified`. Otherwise (equal hash or a null hash) → no row (V9/V10 unchanged).
   - Pass B, over the B2 keys absent from O-Drive → `Removed`.
   - `reporter.ItemCompletedAsync()` once per key, after any `sync_result` insert for that key.
   The **set** of `sync_result` rows produced must be identical to the current algorithm (SC-6).
   Insert order may change, and nothing depends on insert order.
4. Completion: `reporter.CompleteAsync()` → `Completed` with `ContentScanResultSummaryDto`.

### §3.3 SyncCopy / SyncUpdate / SyncPurge jobs (as-built per-file logic; progress hooks only)

- `itemsTotal = request.FileIds.Count`, as submitted; there is no dedup, matching the existing loop.
  `reporter.StartAsync` runs after deserialization and before the first file.
- `reporter.ItemCompletedAsync()` once per `fileId`, whatever the outcome: success, per-file failure,
  or skipped (missing or ineligible `sync_result` row). Each call comes after that file's
  `sync_result` status and audit writes.
- Per-file continue-on-failure and the "all-failed is still `Completed`" rule are unchanged (design §7,
  Decision #8). These task types stay non-cancellable (design §1 note 5).

### §3.4 `TaskProgressReporter` (new, `Cachet.ArchiveSync.Worker`)

One instance per executing task, created by a scoped `TaskProgressReporterFactory`
(`Create(int taskQueueId)`). It is a concrete sealed class with no interface. Tests construct it
directly with a mocked `ITaskQueueRepository` and a `FakeTimeProvider` (P4: five callers, one
implementation).

| Member | Behavior |
|---|---|
| `StartAsync(int itemsTotal, CancellationToken ct)` | Stores the total and **writes immediately**: `(0, itemsTotal)`. Called once. |
| `ItemCompletedAsync(CancellationToken ct)` | `Interlocked.Increment` of the in-memory count. Writes **only if** at least `Worker:ProgressReportIntervalSeconds` have passed since the last write **and** no other write is in flight (`Interlocked.CompareExchange` guard). Otherwise it returns without I/O. Safe to call from KeywordSearch's parallel lambdas. |
| `CompleteAsync(CancellationToken ct)` | Forced final write `(count, itemsTotal)`, awaited, **before** the processor's `Completed` status write. Not called on the failure path. |

Rules:
- **Advisory, never fatal.** A progress write that throws (anything except
  `OperationCanceledException`) is caught, logged at `Warning` with the task ID only, and swallowed.
  The job continues and its outcome is unchanged (V6).
- **Stops when the task is no longer `Running`.** If the SP reports 0 rows affected, the reporter
  logs one `Warning` and makes **no further writes** for that task (V7). It does not abort the job:
  stopping a job is cancellation's job, which is out of scope (§4).
- **Time-triggered only, evaluated on item completion.** There is no background timer, so a single
  very long item (for example, one large PDF being read) produces no writes while it runs.
  `progress_updated_at` then honestly shows the last time an item finished.
- Uses `TimeProvider` (registered as `TimeProvider.System`) so throttling is testable.
- The reporter only ever sends two integers and a task ID to the database. It never sends a term,
  path, or file name.

### §3.5 Progress persistence semantics

- `items_processed`, `items_total`, `progress_updated_at` on `dbo.task_queue` (§5). All three are
  `NULL` until the job's `StartAsync`.
- Written **only while `status = 'Running'`**, by `dbo.task_queue_progress_upd`. That SP never lowers
  `items_processed`, so out-of-order concurrent writes cannot move the count backwards.
- After a terminal transition the values are **retained, not cleared.** `Completed` shows
  `items_processed = items_total`. `Failed` (processor, dispatch boundary, or CAS-028 startup sweep)
  shows where the job stopped, which is useful when diagnosing and deciding whether to restart
  (CAS-016).
- A CAS-016 restart inserts a new row with explicit columns, so its progress starts `NULL`. The
  original row keeps its values.
- **Not an FR-23 audit event.** Progress is operational telemetry about a task, not a user action or
  a state change to archive data. Every job's real effects keep their existing per-item audit rows.
- `TaskQueueRow` gains `ItemsProcessed`, `ItemsTotal`, `ProgressUpdatedAt`, mapped by
  `TaskQueueRepository.GetAsync` from the extended `dbo.task_queue_get`. **No endpoint exposes them in
  this spec** (§4, §6).

### §3.6 Result attachment invariant (as-built, now stated and tested)

For every task type, in this order:
1. All per-item result rows (`search_result` inserts, `sync_result` inserts or status updates) and
   per-item audit rows are awaited and committed. Each is an individually committed SP call.
2. `reporter.CompleteAsync()`.
3. `task_queue_upd(status = 'Completed', result_detail = <summary JSON>)`.

The summary counts are exactly the counts of rows written in step 1 for that `task_queue_id`
(`FilenameMatchCount + ContentMatchCount` = `search_result` rows; `NewCount + RemovedCount +
ModifiedCount` = `sync_result` rows; `SucceededCount + FailedCount` = files evaluated, including
skipped ones counted as failed). This is what makes it safe for `GET /search/{id}/results` and
`GET /sync/{id}/results` to read rows the moment they stop returning `409` (design §7, Decision #5).
Partial rows from a task that later fails stay in the database and are not served as a completed
result (existing API behavior; design §10.4 for the cancel analogue).

---

## §4 Out-of-Scope

- **Any endpoint or UI that displays progress.** No `GET /tasks` endpoint exists
  (`TaskManagerController` only exposes `restart`), and the Task Manager screen (WBS Task 25) and the
  completion notification (WBS Task 30) are future specs. They consume `TaskQueueRow`'s new fields.
  §8 gives proposed display copy for them. The `409` bodies of `GET /search|/sync/{id}/results` are
  **not** changed.
- **ETA, stored percentage, phase labels.** Rejected by the 2026-09-24 decision (§0.1).
- **Cancellation** (`is_cancel_requested` polling, `Running → Cancelled`): same deferral as CAS-028
  §4. The reporter's write point is the natural future checkpoint (§15 OQ-4), but nothing is built
  here.
- **The queue harness:** claim, bounded parallelism, dispatch-boundary safety net, startup sweep, and
  `WorkerOptions`. All are owned by **CAS-028, a prerequisite** (§14).
- **Search/scan algorithm changes:** the `file_index_search` SQL, content extraction
  (`IFileContentMatcher`), whole-file `ReadAllBytesAsync` memory use, the single-bucket B2
  cross-check, and the fact that one `B2ApiException` fails the whole search. All are recorded as-is
  (§3.1). Changes to them belong to CAS-006/012/013 change specs.
- **Processor failure-path audit gap** (design §9.3; CAS-028 OQ-3): unchanged here.
- **A heartbeat/lease for stuck-`Running` detection.** `progress_updated_at` could later serve this
  purpose, but no detection logic is added (CAS-028 §4).
- **O-Drive ingestion / B2 mirror population** (who populates `file_index`): depends on open BRD
  §12.4 item 1 (refresh cadence). This spec does not depend on it.
- Not dependent on BRD §12.4's web caching item, nor on the former §12.2 items (resolved in v2.3).

---

## §5 Database Objects

### Tables / Columns

`ALTER TABLE dbo.task_queue` adds three columns. All are nullable, have no default, and
existing rows are unaffected:

| Column | Type | Meaning |
|---|---|---|
| `items_processed` | `INT NULL` | Units of work finished so far (§3.1–§3.3 define the unit per task type). |
| `items_total` | `INT NULL` | Units of work counted at job start. Fixed for the task's lifetime. |
| `progress_updated_at` | `DATETIME2(3) NULL` | UTC time of the last successful progress write. |

Constraint `CK_task_queue_progress`:
```sql
CHECK (   (items_processed IS NULL OR items_processed >= 0)
      AND (items_total     IS NULL OR items_total     >= 0)
      AND (items_processed IS NULL OR items_total IS NULL OR items_processed <= items_total))
```
No new index. Progress is only ever written and read by the PK.

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `task_queue_progress_upd` | dbo | upd | **New.** Guarded `Running`-only, monotonic progress write. Returns `rows_affected`. |
| `task_queue_get` | dbo | get | **Amended.** Adds the 3 columns to the projection (additive; existing callers unaffected). |

**`dbo.task_queue_progress_upd`, required semantics** (db-author keeps the house header, validation,
and `TRY/CATCH` conventions):
- Parameters: `@task_queue_id INT`, `@updated_by NVARCHAR(50)`, `@items_processed INT`,
  `@items_total INT`. All are required and validated per §7 V2.
- Body:
  ```sql
  UPDATE dbo.task_queue
  SET    items_processed     = CASE WHEN items_processed IS NULL OR @items_processed > items_processed
                                    THEN @items_processed ELSE items_processed END
        ,items_total         = @items_total
        ,progress_updated_at = SYSUTCDATETIME()
        ,updated_by          = @updated_by
        ,updated_at          = SYSUTCDATETIME()
  WHERE  task_queue_id = @task_queue_id
    AND  status = N'Running';
  SELECT @@ROWCOUNT AS rows_affected;
  ```
- It never touches `status`, `result_detail`, `completed_at`, or `is_cancel_requested`.
  `dbo.task_queue_upd` is **not** changed, so terminal writes leave the progress columns as they are.

### Migration Strategy

db-author writes both of the following:
- `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-029-long-running-job-execution-progress.sql`: idempotent
  (`IF COL_LENGTH(...) IS NULL` guards per column, `IF NOT EXISTS` for the constraint, `CREATE OR ALTER`
  for both SPs). It is append-only, with a timestamp later than CAS-028's migration.
- Mirror updates: `Entities/dbo.task_queue.sql` (the 3 columns, the constraint, and an `Amended by`
  line), `Stored-Procedures/dbo.task_queue_get.sql` (projection plus `Amended by`), and the new
  `Stored-Procedures/dbo.task_queue_progress_upd.sql`.
- Rollback section: drop `task_queue_progress_upd`, restore the prior `task_queue_get` projection,
  drop the constraint, then drop the 3 columns. The progress data is advisory, so losing it is
  acceptable.

---

## §6 API Endpoints

N/A. No endpoint is added or changed. `endpoints diff CAS-029` is expected to show zero changes.
`TaskQueueRow` gains three fields, which is an internal DTO change. `KeywordSearchService` and
`SyncScanService` read `TaskQueueRow` but do not project those fields into any response.

---

## §7 Validations

> **PROPOSED:** confirm before `status → ready`. There is no HTTP surface, so "On Failure" describes
> Worker/SP behavior.

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `Worker:ProgressReportIntervalSeconds` is an integer in 5–300 | Host fails at startup (`OptionsValidationException`, via CAS-028's `ValidateOnStart`) | `Test_V1_WorkerOptions_ProgressIntervalOutOfRange_FailsValidation` |
| V2 | `task_queue_progress_upd`: `@task_queue_id` non-null; `@updated_by` non-blank; `@items_processed`/`@items_total` non-null, ≥ 0, and processed ≤ total | `RAISERROR` severity 16; no row changed | `Test_V2_ProgressProc_InvalidParameters_Raises` *(real DB)* |
| V3 | The progress SP updates only a `Running` row | Returns `rows_affected = 0`; a `Queued`/`Completed`/`Failed`/`Cancelled` row is unchanged | `Test_V3_ProgressProc_NonRunningRow_NoChange` *(real DB)* |
| V4 | `items_processed` never decreases | A lower value keeps the stored value; `items_total`/`progress_updated_at` still refresh | `Test_V4_ProgressProc_LowerValue_DoesNotRegress` *(real DB)* |
| V5 | `CK_task_queue_progress` holds at the table level | Direct violating `UPDATE` fails with a constraint error | `Test_V5_TaskQueue_ProgressCheckConstraint_RejectsProcessedAboveTotal` *(real DB)* |
| V6 | A progress write failure never changes the task's outcome | Warning logged; the job continues to its normal terminal status | `Test_V6_Reporter_WriteThrows_SwallowedAndLogged` |
| V7 | After `rows_affected = 0`, the reporter makes no further writes for that task | One Warning; subsequent `ItemCompletedAsync`/`CompleteAsync` perform no I/O | `Test_V7_Reporter_TaskNoLongerRunning_StopsWriting` |

---

## §8 UI / UX

N/A in this spec. There is no screen change and no endpoint to read progress from yet. The following
is a **proposal for the future Task Manager spec (WBS Task 25/30)**. It is not built here, and it
takes effect only if §15 OQ-1 is signed off:

- `Running` with `items_total` NULL: current chip only (*"Running"* plus the indeterminate spinner,
  UI/UX §5.2).
- `Running` with a total: *"Running — 12,400 of 88,210 candidates checked"* (`files compared` for
  ContentScan, `files processed` for sync actions), plus *"last update 40 s ago"* from
  `progress_updated_at`. The chip does not show an ETA.
- `Failed` with progress: the details can say *"Stopped after 12,400 of 88,210"*.

---

## §9 Configuration Changes

Worker `appsettings.json`, added to CAS-028's `WorkerOptions` (`Worker` section). This is a tuning
value, never a credential (FR-03).

| Key | Default | Description | Env Override |
|-----|---------|-------------|--------------|
| `Worker:ProgressReportIntervalSeconds` | `15` | Minimum seconds between intermediate progress writes per task (§3.4). It is kept above the Task Manager's 10 s UI poll (design §8) so a poll rarely sees a value that is already stale, while the DB load stays negligible (≤ 4 slots × 1 write per 15 s). Not BRD-sourced. | Yes (`Worker__ProgressReportIntervalSeconds`) |

---

## §10 Success Criteria

> **PROPOSED:** confirm before `status → ready`. *(real DB)* tests follow the project's
> `Skip`-marked convention. As in CAS-028 §10, the implementer runs them un-skipped against LocalDB
> with the full migration chain and pastes the results into the §19 log before `/review`.

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | The migration applies cleanly after CAS-028's. The 3 columns, `CK_task_queue_progress`, and `task_queue_progress_upd` exist. `TaskQueueRepository.GetAsync` returns `ItemsProcessed`/`ItemsTotal`/`ProgressUpdatedAt`, NULL for a fresh row. A CAS-016 restart row starts with NULL progress | `Test_SC1_Migration_ProgressColumnsAndProc_RoundTripViaGetAsync` *(real DB)* |
| SC-2 | Terminal writes via `task_queue_upd` leave the progress columns unchanged (retained on `Completed` and `Failed`) | `Test_SC2_TaskQueueUpd_TerminalStatus_RetainsProgress` *(real DB)* |
| SC-3 | KeywordSearch: the first progress write is `(0, candidates.Count)`, made after `SearchCandidatesAsync` and before any mirror read or B2 call | `Test_SC3_KeywordSearch_StartWrite_ZeroOfCandidateCount_BeforeFirstB2Call` |
| SC-4 | KeywordSearch: with a fixture mixing all four dispositions (filename-confirmed, content-confirmed, content-dropped, B2-excluded), the final write is `(N, N)`; each candidate is counted exactly once | `Test_SC4_KeywordSearch_EveryDispositionCountedOnce_FinalEqualsTotal` |
| SC-5 | For each of the 5 processors: every result-row write and per-item audit write happens before the final progress write, which happens before the `Completed` status write (`Received.InOrder`); summary counts equal the number of result-row writes | `Test_SC5_KeywordSearch_ResultsThenProgressThenCompleted`, `Test_SC5_ContentScan_…`, `Test_SC5_SyncCopy_…`, `Test_SC5_SyncUpdate_…`, `Test_SC5_SyncPurge_…` |
| SC-6 | ContentScan: `items_total = |ODrive ∪ B2 keys|`; the set of `(comparison_type, odrive_id, b2_id, sha1_changed)` rows is identical to the pre-change algorithm for a fixture covering New/Removed/Modified/equal-hash/null-hash; final write `(total, total)` | `Test_SC6_ContentScan_UnionTotal_OutputSetUnchanged` |
| SC-7 | Sync actions: `items_total = FileIds.Count`; missing-row, per-file-failed, and succeeded files each increment once; final `(total, total)` | `Test_SC7_SyncActions_EveryFileIdCountedOnce` (one per processor via `[Theory]`) |
| SC-8 | Throttling (`FakeTimeProvider`, interval 15 s): 1,000 items finished within one interval → exactly 2 writes (start and final). Advancing time 15 s before an item finishes → exactly one intermediate write carrying the current count | `Test_SC8_Reporter_ThrottlesByInterval` |
| SC-9 | Concurrency: 8 parallel callers × 500 `ItemCompletedAsync` with the repository write artificially delayed → never more than one write in flight, and the final write reports exactly 4,000 | `Test_SC9_Reporter_ParallelIncrements_SingleWriteInFlight_ExactFinal` |
| SC-10 | A progress write that throws does not change the outcome: the processor still writes `Completed` with the correct summary; `result_detail` contains no exception text | `Test_SC10_KeywordSearch_ProgressWriteThrows_TaskStillCompletes` |
| SC-11 | The progress SP is guarded and monotonic end to end: a real `Running` row accepts `(5,10)` then ignores `(3,10)`; after `Completed` a further write returns 0 and changes nothing | `Test_SC11_ProgressProc_GuardedAndMonotonic` *(real DB)* |
| SC-12 | Log hygiene: across a full KeywordSearch and SyncCopy run, no log entry emitted by the reporter or the progress wiring contains the search term, a relative path, or a file name | `Test_SC12_ProgressLogging_NoTermOrPath` (extends `SyncTaskProcessorLogHygieneTests` pattern) |

---

## §11 Telemetry

```csharp
_logger.LogInformation("Task {TaskId} ({TaskType}) started: {ItemsTotal} item(s) to process", …);
_logger.LogDebug("Task {TaskId} progress {ItemsProcessed}/{ItemsTotal}", …);            // on each write
_logger.LogWarning(ex, "Progress write failed for task {TaskId}; continuing without it", …);
_logger.LogWarning("Task {TaskId} is no longer Running; progress reporting stopped", …);
```

- Only the task ID, task type, and integer counts are logged. **Never** the term, `request_parameters`,
  relative path, file name, snippet, or extracted text (existing CAS-006 §11/CAS-012 §11 rules), and
  never B2 key material.
- The existing per-processor completion log lines are unchanged.

---

## §12 Security Notes

> **PROPOSED threat model:** confirm before `status → ready`.

### B2 Credential Handling
No change. Progress reporting never touches B2. The cross-check and sync actions keep using the
server-held system-key `IB2Client` (CAS-001 §7 V1). No key material is logged or stored in
`request_parameters`, `result_detail`, or the progress columns (FR-03).

### Data Minimization (SOC 2 / BRD §9)
The new columns hold **only integers and a timestamp**. They cannot carry a term, path, file name, or
content. `items_total` for a KeywordSearch shows how many in-scope files a bucket/extension has. That
is low-sensitivity metadata, and it is only visible to whoever can already read the task row: the
owner-scoped `GET /tasks` in a future spec (API design §7.1).

### Audit Trail
- No new audit actions. Progress writes are deliberately **not** FR-23 events (§3.5); they record
  neither a user action nor a change to archive data.
- Unchanged and still binding: `CrossCheckExcluded` is audited after the self-heal and before any
  result would exist. Per-file sync audits are written before `Completed`.
- The result-before-`Completed` ordering (§3.6) is what makes a `Completed` status trustworthy for a
  compliance-relevant review.

### Session / Auth Requirements
The Worker has no session. It uses the `worker-svc` identity as `updated_by` and attributes per-item
audits to `submitted_by_user_id` (unchanged).

### Residual Risks
| Risk | Impact | Mitigation / owner |
|---|---|---|
| A single very large item (e.g., a multi-GB PDF) blocks writes | Progress looks frozen for that item's duration | `progress_updated_at` shows the truth; the future UI shows "last update N ago". Accepted. |
| `items_total` is fixed at start, while the mirror may change during a day-long run | The count describes the candidate set at start, not a live view | Correct by definition: the job processes exactly that set. Documented in §3.1/§3.2. |
| Progress is misread as "percent of time remaining" | User expectation mismatch (FR-22a) | No ETA; UI copy uses "N of M items", not a time estimate (§8). |

---

## §13 Test Plan

### Unit Tests (`Cachet.ArchiveSync.Worker.Tests`, xUnit + NSubstitute + FluentAssertions)
| Class | Method | Proves |
|-------|--------|--------|
| `TaskProgressReporterTests` (new) | `Test_SC8_…`, `Test_SC9_…`, `Test_V6_…`, `Test_V7_…`, `Test_Reporter_StartWritesZeroOfTotalImmediately` | Throttle, concurrency, fault tolerance, stop-on-not-running |
| `KeywordSearchTaskProcessorTests` (extend) | `Test_SC3_…`, `Test_SC4_…`, `Test_SC5_KeywordSearch_…`, `Test_SC10_…` | Unit of work, ordering, non-fatal progress |
| `ContentScanTaskProcessorTests` (extend) | `Test_SC5_ContentScan_…`, `Test_SC6_…` | Union total; restructure preserves output |
| `SyncCopy/SyncUpdate/SyncPurgeTaskProcessorTests` (extend) | `Test_SC5_…` per type, `Test_SC7_…` | Per-file counting and ordering |
| `SyncTaskProcessorLogHygieneTests` (extend) | `Test_SC12_…` | No term/path in progress logs |
| `WorkerOptionsTests` (CAS-028's; extend) | `Test_V1_…` | Bounds and default 15 |

All existing processor tests must still pass. Their fixtures gain a `TaskProgressReporterFactory`
built over the same mocked `ITaskQueueRepository` and a `FakeTimeProvider`. Package:
`Microsoft.Extensions.TimeProvider.Testing` 8.x (test project only).

### Integration Tests (`Cachet.ArchiveSync.Api.IntegrationTests`, real SQL, existing `Skip` convention)
| Class | Method | Proves |
|-------|--------|--------|
| `TaskQueueProgressTests` (new) | `Test_SC1_…`, `Test_SC2_…`, `Test_SC11_…`, `Test_V2_…`, `Test_V3_…`, `Test_V4_…`, `Test_V5_…` | Schema, SP guard and monotonicity, retention, `GetAsync` mapping |

### Coverage Target
Changed files ≥ 85% line coverage (`TaskProgressReporter.cs`, `TaskProgressReporterFactory.cs`, the 5
processors' deltas, and `TaskQueueRepository.cs`'s delta).

---

## §14 Rollout

1. **Prerequisite:** CAS-028 is merged and deployed. That provides `WorkerOptions`/`ValidateOnStart`
   and a claim SP that actually compiles; without it no task runs against a real DB, so there is
   nothing to report progress on.
2. Apply the CAS-029 migration. It is backward-compatible: nullable columns, an additive projection,
   and a new SP. Old Worker/Api builds ignore all three.
3. Deploy the Worker. Api only needs a redeploy for the `TaskQueueRow`/repository mapping, which is
   harmless if deployed later because nothing reads the fields yet. Web: none.
4. Verify on the first long task: `items_total` is set shortly after `started_at`,
   `progress_updated_at` advances about every 15 s, and on `Completed` `items_processed = items_total`.
5. **Rollback:** redeploy the previous Worker. The columns simply stop updating. Run the migration's
   rollback section only if the schema must be removed; no data of record is lost.

---

## §15 Open Questions

- [ ] **OQ-1 (blocks `ready`): Sign-off on reversing design Decision #1 / UI/UX §5.2 for measured
  counts only (§0.1).** Needs the TraceCounts design owner (Rakesh Devarajan, who resolved the design's
  decisions) and client confirmation, since UI/UX §5.2 was client-confirmed. Once confirmed, add a
  dated supersession note to design §6/§15 #1 and UI/UX §5.2 pointing to CAS-029. Those are other
  documents, so they are not edited without approval.
  **2026-09-24 — proceeding on requester instruction:** Ratheesh (TraceCounts delivery) instructed
  "make it ready and implement". `status → ready` was set on that instruction. **Design-owner and
  client sign-off are still pending**. This is recorded as a requester override, not as sign-off. If
  sign-off is refused, §0.1's fallback applies: remove §3.3–§3.5 and §5 in a change spec.
  §7/§10/§12 and OQ-2/OQ-3 defaults are implemented as proposed.
- [ ] **OQ-2: Unit-of-work labels.** KeywordSearch counts **candidate rows** (a file matching by both
  name and content counts twice, and every in-scope file is a Content candidate). Is "candidates
  checked" acceptable wording for users, or should the future UI say "files checked" and accept the
  imprecision?
- [ ] **OQ-3: Interval default and bounds** (15 s; 5–300 s) are proposed, not BRD-sourced. Confirm them.
- [ ] **OQ-4 (follow-on, not blocking):** a future cancellation spec could have
  `task_queue_progress_upd` also return `is_cancel_requested`, which would make each progress write a
  cancel checkpoint (design §10) at no extra round trip. It is recorded here and deliberately not
  built (P4).
- [ ] **OQ-5 (follow-on, not blocking):** the CAS-028 §4 "no progress column" bullet and the
  `async-task-queue` skill's progress bullet should be reconciled once OQ-1 is decided.
- Not dependent on BRD §12.4 (web caching layer; O-Drive refresh cadence).

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code (for Ratheesh, ratheesh@scientificvision.net) | 2026-09-24 | Draft; progress scope chosen by requester (measured counts, no ETA). §7/§10/§12 proposed |
| Requester go-ahead | Ratheesh (TraceCounts delivery) | 2026-09-24 | Instructed `ready` + `/implement` before OQ-1 sign-off (see §15 OQ-1) |
| Design-decision sign-off (OQ-1) | | | **Pending**: requester override recorded above |
| Reviewer | | | |

---

## §17 Graph Impact

### God Nodes in Blast Radius
None of `GRAPH_REPORT.md`'s top god nodes is modified. `ILogger` is used, not changed.
`KeywordSearchTaskProcessorTests` (29 edges) is **extended**: new tests plus a fixture change for the
reporter factory.

### Communities / Modules Affected
- **Projects:** Worker (5 processors, new `TaskProgressReporter.cs` + `TaskProgressReporterFactory.cs`,
  `Program.cs` registrations for the factory and `TimeProvider.System`, `WorkerOptions` +1 property,
  `appsettings.json`), Core (`TaskQueueRow` +3 properties, `ITaskQueueRepository` +1 method `Task<bool>
  UpdateProgressAsync(int taskQueueId, string updatedBy, int itemsProcessed, int itemsTotal,
  CancellationToken ct)`), Api (`TaskQueueRepository` +1 method and a `GetAsync` mapping delta),
  Database (1 migration, 3 mirrors). **Web: none.**
- **Modules:** Keyword Search, O-Drive Sync (job execution), Task Manager (data only).

### Decomposition Check (P9)
Signals: §10 has 12 criteria (at the threshold, not over); §7 has 7; §5 has 1 table and 2 SPs; §6 has
0 endpoints. Estimated changed files: about 20 (including 7 test files), which is **over 15**. That is
one threshold crossed, below the stop rule of "2 or more". The file count comes from 5 processors each
needing the same mechanical 3-call hook plus its test file. The candidate fault line (reporter + DB
vs. processor wiring) was considered and rejected: the reporter with no callers would be dead code,
and all tests use one harness (Worker xUnit) plus the same real-DB convention CAS-028 uses. **Decision:
single spec.**

### Surprising Connections
1. **The WBS brief and the design disagree about progress.** Task 29's brief and the
   `async-task-queue` skill ask for it; design §6 and UI/UX §5.2 forbid a fabricated metric. The
   design already anticipated this reversal path (§0.1).
2. **KeywordSearch's unit of work is the candidate row, not the file.** Since CAS-012, every in-scope
   file is a Content candidate, so `items_total` is at least the file count for the bucket/extension.
   This is also why the job can take a day (OQ-2).
3. **ContentScan's three-pass loop visits intersecting keys twice.** This is harmless for output, but
   it has to be restructured to count progress honestly (§3.2).
4. **`dbo.task_queue_upd` has no status guard.** Any write path, including a late one, could overwrite
   a terminal row. That is why progress uses its own guarded SP instead of reusing `task_queue_upd` or
   `result_detail`.

### Graphify Queries Run
```
py -3 -m graphify query "KeywordSearchTaskProcessor ContentScanTaskProcessor progress task_queue result_detail" --budget 1200
   → BFS depth 2 from KeywordSearchTaskProcessor / ContentScanTaskProcessor, 151 nodes (mostly test classes)
```
As with CAS-028, the graph mainly located files. The findings above come from a manual read of all 5
processors, `Worker.cs`, `Program.cs`, `IWorkerTaskProcessor.cs`, `ITaskQueueRepository`,
`IFileIndexRepository`, `ISearchResultRepository`, `ISyncResultRepository`, `IB2Client`,
`TaskQueueRepository.GetAsync`, the `task_queue`/`search_result`/`sync_result` entity mirrors, the
`task_queue_upd`/`task_queue_get`/`task_queue_restart_ins`/`file_index_search` SP mirrors, the result
summary DTOs, BRD §7.2/§7.5, design §2/§6/§7/§9, UI/UX §5.2, and CAS-028.
Tooling note: `python -m cachet_agent_tools` still fails on Python 3.14 (the unescaped `%` at
`cachet_agent_tools/__main__.py:64`, already recorded by CAS-028). The ID was minted by calling
`cachet_agent_tools.spec._cmd_next_id` directly. The tool was not modified.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] OQ-1 sign-off recorded before `/implement` ran (otherwise BLOCKED)
- [ ] All §10 Success Criteria have a passing test; *(real DB)* tests have pasted LocalDB evidence in §19
- [ ] All §7 Validations are implemented and tested
- [ ] §6 N/A: `endpoints diff CAS-029` shows no change
- [ ] §3.1/§3.3 behavior unchanged apart from progress hooks (no drift in the cross-check,
      self-heal/audit order, or per-file sync semantics); §3.2 output set unchanged (SC-6)
- [ ] §3.6 ordering holds in all 5 processors
- [ ] §12: progress columns and logs carry no term/path/file name/key material; no new audit actions
- [ ] §5: the 3 columns + constraint + SP exist; `task_queue_upd` unchanged; migration timestamp after CAS-028's
- [ ] §9 key present with default 15 and validated on start
- [ ] No §4 out-of-scope item implemented (especially endpoints/UI, ETA/percent, cancellation)

### Verification Report Format
The reviewer writes `specs/misc/CAS-029.review.md` with: overall verdict (APPROVED | APPROVED WITH
NOTES | CHANGES REQUIRED | BLOCKED), a per-SC verdict table, a security gap list, and scope violations.

## Implementation Log

**Completed:** 2026-09-24
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 96.9% (Worker files 93.1–100%; TaskQueueRepository.cs 0% in-run — real-DB Skip convention, 21/21 passed on LocalDB) | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20260924_120000__CAS-029-long-running-job-execution-progress.sql` | new: 3 progress columns, CK_task_queue_progress, task_queue_progress_upd, task_queue_get projection |
| `Database/CachetArchiveSync/Entities/dbo.task_queue.sql` | mirror: progress columns + constraint |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_get.sql` | mirror: projection |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_progress_upd.sql` | new mirror |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskQueueRow.cs` | +ItemsProcessed/ItemsTotal/ProgressUpdatedAt |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs` | +UpdateProgressAsync |
| `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs` | +UpdateProgressAsync, GetAsync mapping |
| `src/Cachet.ArchiveSync.Worker/TaskProgressReporter.cs` | new throttled advisory reporter |
| `src/Cachet.ArchiveSync.Worker/TaskProgressReporterFactory.cs` | new scoped factory |
| `src/Cachet.ArchiveSync.Worker/WorkerOptions.cs` | +ProgressReportIntervalSeconds [Range(5,300)] |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | register factory + TimeProvider.System |
| `src/Cachet.ArchiveSync.Worker/appsettings.json` | Worker:ProgressReportIntervalSeconds=15 |
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs` | progress hooks |
| `src/Cachet.ArchiveSync.Worker/ContentScanTaskProcessor.cs` | two-pass restructure + progress hooks |
| `src/Cachet.ArchiveSync.Worker/SyncCopyTaskProcessor.cs` | progress hooks |
| `src/Cachet.ArchiveSync.Worker/SyncUpdateTaskProcessor.cs` | progress hooks |
| `src/Cachet.ArchiveSync.Worker/SyncPurgeTaskProcessor.cs` | progress hooks |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `TaskProgressReporterTests` | `Test_SC8_Reporter_ThrottlesByInterval` | SC-8 |
| `TaskProgressReporterTests` | `Test_SC9_Reporter_ParallelIncrements_SingleWriteInFlight_ExactFinal` | SC-9 |
| `TaskProgressReporterTests` | `Test_V6_Reporter_WriteThrows_SwallowedAndLogged` | V-6 |
| `TaskProgressReporterTests` | `Test_V7_Reporter_TaskNoLongerRunning_StopsWriting` | V-7 |
| `TaskProgressReporterTests` | `Test_Reporter_StartWritesZeroOfTotalImmediately` | §3.4 |
| `KeywordSearchTaskProcessorTests` | `Test_SC3_KeywordSearch_StartWrite_ZeroOfCandidateCount_BeforeFirstB2Call` | SC-3 |
| `KeywordSearchTaskProcessorTests` | `Test_SC4_KeywordSearch_EveryDispositionCountedOnce_FinalEqualsTotal` | SC-4 |
| `KeywordSearchTaskProcessorTests` | `Test_SC5_KeywordSearch_ResultsThenProgressThenCompleted` | SC-5 |
| `KeywordSearchTaskProcessorTests` | `Test_SC10_KeywordSearch_ProgressWriteThrows_TaskStillCompletes` | SC-10 |
| `ContentScanTaskProcessorTests` | `Test_SC5_ContentScan_ResultsThenProgressThenCompleted` | SC-5 |
| `ContentScanTaskProcessorTests` | `Test_SC6_ContentScan_UnionTotal_OutputSetUnchanged` | SC-6 |
| `SyncCopyTaskProcessorTests` | `Test_SC5_SyncCopy_ResultsThenProgressThenCompleted` | SC-5 |
| `SyncCopyTaskProcessorTests` | `Test_SC7_SyncActions_EveryFileIdCountedOnce` | SC-7 |
| `SyncUpdateTaskProcessorTests` | `Test_SC5_SyncUpdate_ResultsThenProgressThenCompleted` | SC-5 |
| `SyncPurgeTaskProcessorTests` | `Test_SC5_SyncPurge_ResultsThenProgressThenCompleted` | SC-5 |
| `SyncTaskProcessorLogHygieneTests` | `Test_SC12_ProgressLogging_NoTermOrPath` | SC-12 |
| `WorkerOptionsTests` | `Test_V1_WorkerOptions_ProgressIntervalOutOfRange_FailsValidation` | V-1 |
| `TaskQueueProgressTests` | `Test_SC1_Migration_ProgressColumnsAndProc_RoundTripViaGetAsync` | SC-1 (real DB) |
| `TaskQueueProgressTests` | `Test_SC2_TaskQueueUpd_TerminalStatus_RetainsProgress` | SC-2 (real DB) |
| `TaskQueueProgressTests` | `Test_SC11_ProgressProc_GuardedAndMonotonic` | SC-11 (real DB) |
| `TaskQueueProgressTests` | `Test_V2_ProgressProc_InvalidParameters_Raises` | V-2 (real DB) |
| `TaskQueueProgressTests` | `Test_V3_ProgressProc_NonRunningRow_NoChange` | V-3 (real DB) |
| `TaskQueueProgressTests` | `Test_V4_ProgressProc_LowerValue_DoesNotRegress` | V-4 (real DB) |
| `TaskQueueProgressTests` | `Test_V5_TaskQueue_ProgressCheckConstraint_RejectsProcessedAboveTotal` | V-5 (real DB) |

### Migration
`20260924_120000__CAS-029-long-running-job-execution-progress.sql`

### Real-DB Evidence (§10) and Gate Notes
- **LocalDB run (2026-09-24):** throwaway `CachetCas029TestDb` on `(localdb)\MSSQLLocalDB`, all migrations through CAS-029 applied (`sqlcmd -I`, no `-b`; CAS-030 excluded). Only the known pre-existing errors appeared (CAS-003 Msg 9982/7609, CAS-002 Msg 207, CAS-006 addendum Msg 7601), and the CAS-029 migration applied with no errors. `TaskQueueProgressTests` were un-skipped with `-- xUnit.ParallelizeTestCollections=false`: `Total tests: 21 / Passed: 21` (7 methods, 21 cases). Skips restored and confirmed identical to the backup, re-run showed `Skipped: 7`, and the database was dropped.
- **Gate 2 deviation:** the solution-level `dotnet test` crashed with test-host `0x80131506` (Internal CLR error, a known host issue on this machine). Re-run per project, all passed: Api.Tests 44/44; Worker.Tests 109 passed, 1 pre-existing skip; Web.Tests 140/140; Api.IntegrationTests 76 passed, 59 Skip-marked real-DB tests.
- **Gate 3:** `dotnet format --verify-no-changes` was run per project (the whole-solution run crashes on this host). All 8 projects clean.
- **Gate 4:** `TaskQueueRepository.cs` shows 0% in-run because its tests follow the real-DB Skip convention. It was exercised by the LocalDB run above.
- **Gate 5:** not applicable (`touches.api: false`, §6 N/A).
- **OQ-1:** design-owner/client sign-off is still pending. Implemented on the requester's override (§15).
- **Concurrency note:** during Phase C, a separate session (CAS-030 work) rewrote and later deleted `TaskProgressReporterTests.cs`. The test-writer recreated it. Its final contents were verified on disk before the gates ran.
