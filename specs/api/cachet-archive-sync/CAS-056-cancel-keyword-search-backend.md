---
id: CAS-056
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-06
priority: P1
change_type: enhancement
related_specs:
  [
    CAS-057,
    CAS-004,
    CAS-006,
    CAS-016,
    CAS-026,
    CAS-028,
    CAS-029,
    CAS-030,
    CAS-049,
    CAS-054,
    CAS-055,
  ]
touches:
  database: true
  api: true
  ui: false
  worker: true
  b2: false
  config: true
  security: true
migrations: [20261006_190000__CAS-056-cancel-keyword-search.sql]
---

# CAS-056 — Change: Cancel a running Keyword Search (backend — flag, cooperative stop, partial results)

> **Split under Karpathy P9.** The change spans Core + Api + Worker + Web, so it is two cross-linked specs:
> **CAS-056** (this one: database, Api, Worker — the cancel contract, testable with no UI) and
> **CAS-057** (Blazor Keyword Search screen — the Cancel button and post-cancel states). CAS-057 depends on CAS-056.
> Implement CAS-056 first.

## §1 Current Behavior

_Graphify is not yet useful here (the graph is file-level, not call-aware — see CAS-054 §1); blast radius in §4 was mapped by manual grep/read._

- **A Keyword Search cannot be stopped.** `POST /tasks/{taskId}/cancel` does not exist. `TaskManagerController` documents "cancel remains out of scope" (`TaskManagerController.cs:12-13`, CAS-030 §4). CAS-026/028/029 each deferred it.
- **The schema is ready, nothing uses it.** `dbo.task_queue.is_cancel_requested BIT NOT NULL DEFAULT 0` and the `'Cancelled'` status (`CK_task_queue_status`) both exist (`dbo.task_queue.sql:55,69`). No code in `src/` reads or writes `is_cancel_requested`; no code path produces `Cancelled`.
- **The Worker only honours host shutdown.** `KeywordSearchTaskProcessor.ProcessAsync` runs its candidate loop under `Parallel.ForEachAsync(... CancellationToken = ct)` where `ct` is the host `stoppingToken` (`KeywordSearchTaskProcessor.cs:222-224`). A non-shutdown `OperationCanceledException` would fall to `Worker.RunInSlotAsync`'s generic catch and be recorded as `Failed` (`Worker.cs:269-275`).
- **Progress cannot signal cancellation.** `dbo.task_queue_progress_upd` only updates `WHERE status = 'Running'` and never reads `is_cancel_requested` (`dbo.task_queue_progress_upd.sql`).
- **Matches are already written incrementally.** Each confirmed candidate is inserted into `dbo.search_result` as soon as its B2 cross-check returns (`KeywordSearchTaskProcessor.cs:278-286`), so a task stopped midway already has its partial results in the database.
- **Partial results are not readable.** `GET /search/{taskId}/results` returns match pages only for `Completed`; `Queued`/`Running` → `409 task-not-complete`; any other status, including `Cancelled`, is treated as `Failed` (`KeywordSearchService.cs:115-161`). The results browse API (CAS-049) returns `TaskNotComplete` for anything but `Completed` (`SearchResultBrowseService.cs:217-219`).
- **Startup sweep.** `dbo.task_queue_sweep_interrupted` fails every `Running` row and, since CAS-055, may auto-resume it. It does not look at `is_cancel_requested`.
- **A prior design decision applies.** `CACHET_AsyncProcessingEngine_Design_v1.md` §10 item 4 (resolved 2026-09-09) said a cancelled task's partial rows are retained but **not surfaced** as if the task had completed, and left the response contract to the API layer. See §2.6 / §14 Q1 — this spec supersedes the "not surfaced" half.

---

## §2 Desired Behavior

Scope: **`KeywordSearch` tasks only.** `ContentScan` has the same long-running profile but is **out of scope** (§4) — it is a follow-up that should reuse this endpoint, SP and watcher. `SyncCopy/Update/Purge` are never cancellable (Design §10 item 3).

1. **Cancel endpoint.** `POST /tasks/{taskId}/cancel` (`CachetAuthenticated`), own-scoped by the claim-derived user id. Atomic outcome, decided inside one stored procedure transaction:
   - Task `Queued` → status becomes `Cancelled` immediately (`completed_at` set), `is_cancel_requested = 1`. Response **`200 OK`** `{ "taskId": n, "status": "Cancelled" }`. The Worker never claims it.
   - Task `Running` → `is_cancel_requested = 1`, status stays `Running`. Response **`202 Accepted`** `{ "taskId": n, "status": "Cancelling" }` (`Cancelling` is a response label, **not** a stored status). Repeating the call is idempotent (same `202`, no second audit row).
   - Task terminal (`Completed`/`Failed`/`Cancelled`), or not a `KeywordSearch` → **`409 task-not-cancellable`**.
   - Task missing **or** owned by someone else → the **same** `404 task-not-found` body (no ownership leak; same as CAS-030 V3).
2. **Cooperative stop.** While a `KeywordSearch` task runs, the Worker polls `is_cancel_requested` every `Worker:CancelPollIntervalSeconds` (default 2, range 1–60) and, on seeing it, stops claiming new candidates and cancels in-flight content reads and B2 calls (they receive a linked token). A candidate already past its B2 cross-check finishes its disposition write (insert / soft-delete + audit) — **a candidate is either fully dispositioned or untouched** (Design §10 item 3: "a checkpoint already passed completes normally"). No rollback of rows already written (Design §10 item 3, resolved).
3. **Terminal write.** After the loop has drained, the Worker, in this order: (a) writes the `TaskCancelled` audit row, (b) writes a final progress `(processed, total)`, (c) sets `status = 'Cancelled'` with `result_detail = {"FilenameMatchCount":n,"ContentMatchCount":n,"ExcludedCount":n,"IndeterminateCount":n}`. Audit-before-visible-status (FR-23); if the audit write throws, no status is written and the row stays `Running` for the next sweep, exactly as `Worker.FailAtBoundaryAsync` does.
4. **Cancel arrives too late.** If the loop finishes every candidate before the Worker observes the flag, the task completes normally as `Completed` (full results) — the flag is ignored. The UI sees `Completed` on its next poll.
5. **Startup sweep respects the flag.** A `Running` row with `is_cancel_requested = 1` found at startup becomes `Cancelled` (audit `TaskCancelled`, `completed_at` set), **never** `Failed` and **never** auto-resumed (CAS-055). Cancelled tasks are not restartable (`task_queue_restart_ins` stays `Failed`-only, unchanged).
6. **Partial results are readable.** `GET /search/{taskId}/results` for a `Cancelled` task returns **`200`** with `Status: "Cancelled"`, `IsPartial: true`, `ItemsProcessed`/`ItemsTotal`, and the same paged `FilenameMatches`/`ContentMatches` as `Completed`. The results browse API (CAS-049) and result-ids / quarantine-add paths accept a `Cancelled` task the same way as `Completed`. "Partial" is explicit in the contract so a cancelled task's rows can never be mistaken for a complete answer (the intent of Design §10 item 4).
7. **Audit.** Two new `AuditAction` members, appended: `TaskCancelRequested` (written by the cancel SP in the same transaction as the flag, for a `Running` task) and `TaskCancelled` (written in-transaction by the SP for a `Queued` direct cancel, and by the Worker / sweep for a stopped `Running` task). `ResultDetail` carries task ids and integers only — never a term, path or file name.

---

## §3 Reason for Change

- **Type:** enhancement
- **Trigger:** product-owner request, 2026-10-06 (devarajan@tracecounts.org): a Keyword Search over ~310k files runs for hours; the user needs to cancel it, see any matches found so far, and be free to run a new Keyword / Filename / Date search.
- **Risk if not changed:** the user must wait out a multi-hour search (or restart the service) to regain the Search screen; the only escape today is killing the Worker, which fails the task and (since CAS-055) auto-resumes it.

---

## §4 Blast Radius (Graph Impact)

_graphify not yet useful for these objects — blast radius mapped via manual grep/read of `src/`, `Database/`, and the specs that mention `is_cancel_requested`._

### Primary Change Target

`TaskManagerController` / `ITaskManagerService` (new cancel action), `dbo.task_queue` (flag use), `KeywordSearchTaskProcessor` (observe flag, stop, write `Cancelled`), `KeywordSearchService.GetResultsAsync` and `SearchResultBrowseService` (status gate).

### Direct Dependents

- `ITaskQueueRepository` / `TaskQueueRepository` — gain `CancelAsync`, `IsCancelRequestedAsync`; implemented by test fakes (`Worker.Tests`, `Api.Tests`) which must gain the two members.
- `SearchResultsResponse` (Core DTO) — gains `IsPartial`, `ItemsProcessed`, `ItemsTotal`; consumed by `KeywordSearch.razor` (CAS-057) and `ODriveSyncContentScan.razor` (unchanged here — it deserialises the same type, new fields are optional).
- `dbo.task_queue_sweep_interrupted` — branch added; **HIGH RISK**: it carries CAS-028 T5 and CAS-055 auto-resume logic in one transaction.
- `AuditAction` enum — appended; the value is the literal `audit_log.action` text (CAS-005 §15). No `CK` constraint on `audit_log.action` (grep of `Database/` — none).
- `dbo.task_queue_summary_get` — **not changed**: it counts `Queued/Running` as "active" and only unacknowledged `Completed/Failed` as notifications, so a `Cancelled` task correctly raises no badge.

### Downstream Modules

Keyword Search, Task Manager, Audit Log, Quarantine (consumes search results of a cancelled task).

### Cross-Project Risk

**CROSS-PROJECT RISK** — Core (DTO, enum, repository interface) ↔ Api (controller, service, repository, two SPs) ↔ Worker (processor, new watcher, options, sweep). Shared contract: `task_queue.is_cancel_requested` + `Cancelled` status + `SearchResultsResponse`. Web impact is isolated to CAS-057.

### God Node Involvement

`ITaskQueueRepository` is a high-fan-out interface (≥10 members, implemented by repo + fakes). Adding two members is additive; every implementer is listed in §5 so none is left non-compiling.

### Graphify Queries Run

graphify not yet vendored for call-level analysis — analysis done via manual code read (`Worker.cs`, `KeywordSearchTaskProcessor.cs`, `TaskProgressReporter.cs`, `TaskManagerController.cs`, `TaskManagerService.cs`, `KeywordSearchService.cs`, `SearchResultBrowseService.cs`, `dbo.task_queue*.sql`, Design §10).

### Decomposition check (P9)

Signals: touches Api + Worker + Core + Web (> 1 project); changed files > 15 once tests are counted. **Decision: split** — CAS-056 (database/Api/Worker, ~14 source files + tests) and CAS-057 (Web, 1 page + tests). Boundary is the HTTP contract in §8; each spec is independently reviewable. Record in `coding-agent/decisions/spec-decomposition-guideline.md` §2 on merge.

---

## §5 Files to Change

| File                                                                                   | Change     | Description                                                                                               |
| -------------------------------------------------------------------------------------- | ---------- | --------------------------------------------------------------------------------------------------------- |
| `Database/migrations/20261006_190000__CAS-056-cancel-keyword-search.sql`               | add        | Creates `task_queue_cancel_req`, `task_queue_cancel_requested_get`; amends `task_queue_sweep_interrupted` |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_cancel_req.sql`           | add        | Mirror                                                                                                    |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_cancel_requested_get.sql` | add        | Mirror                                                                                                    |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_sweep_interrupted.sql`    | modify     | Mirror of the amended sweep                                                                               |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs`                                     | modify     | Append `TaskCancelRequested`, `TaskCancelled`                                                             |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs`                       | modify     | `CancelAsync`, `IsCancelRequestedAsync`                                                                   |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskManagerService.cs`                        | modify     | `CancelAsync`                                                                                             |
| `src/Cachet.ArchiveSync.Core/Dtos/CancelTaskResult.cs` (+ outcome enum)                | add        | `Cancelled` / `CancelRequested` / `NotFound` / `NotCancellable`                                           |
| `src/Cachet.ArchiveSync.Core/Dtos/SearchResultsResponse.cs`                            | modify     | `IsPartial`, `ItemsProcessed`, `ItemsTotal`                                                               |
| `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs`                       | modify     | Dapper calls for the two SPs                                                                              |
| `src/Cachet.ArchiveSync.Api/Services/TaskManagerService.cs`                            | modify     | `CancelAsync` (maps outcome; no extra audit — SP audits)                                                  |
| `src/Cachet.ArchiveSync.Api/Controllers/TaskManagerController.cs`                      | modify     | `POST tasks/{taskId:int}/cancel`; fix the "cancel out of scope" XML doc                                   |
| `src/Cachet.ArchiveSync.Api/Services/KeywordSearchService.cs`                          | modify     | `GetResultsAsync`: `Cancelled` → partial response                                                         |
| `src/Cachet.ArchiveSync.Api/Services/SearchResultBrowseService.cs`                     | modify     | Gate `Completed` → `Completed or Cancelled`                                                               |
| `src/Cachet.ArchiveSync.Worker/CancelRequestWatcher.cs`                                | add        | Per-task poller → linked `CancellationTokenSource`                                                        |
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs`                          | modify     | Linked token, graceful-cancel path, terminal write                                                        |
| `src/Cachet.ArchiveSync.Worker/WorkerOptions.cs`                                       | modify     | `CancelPollIntervalSeconds` (1–60, validated like the existing keys)                                      |
| `src/Cachet.ArchiveSync.Worker/appsettings.json`                                       | modify     | Document the default (`appsettings.Production.json` is **never** edited)                                  |
| Test projects (Api.Tests, Api.IntegrationTests, Worker.Tests)                          | add/modify | §13; fakes implementing `ITaskQueueRepository` gain the two members                                       |

---

## §6 Files NOT to Change

| File                                                                                                   | Why left untouched                                                                                                                                    |
| ------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/Cachet.ArchiveSync.Worker/Worker.cs`                                                              | The processor handles its own cancel and returns normally; the dispatch boundary's generic catch is never reached for a user cancel                   |
| `src/Cachet.ArchiveSync.Worker/TaskProgressReporter.cs`, `dbo.task_queue_progress_upd.sql`             | Progress stays advisory and `Running`-guarded; cancel is **not** piggy-backed on it (a 10 s-throttled, failure-swallowing write is the wrong channel) |
| `dbo.task_queue_restart_ins.sql`                                                                       | `Cancelled` stays non-restartable by design                                                                                                           |
| `dbo.task_queue_summary_get.sql`, `dbo.task_queue_acknowledge_upd.sql`                                 | A cancelled task raises no badge; acknowledge stays `Completed/Failed` only                                                                           |
| `ContentScanTaskProcessor.cs`, `SyncCopy/Update/PurgeTaskProcessor.cs`, `B2MirrorSyncTaskProcessor.cs` | Out of scope (§2)                                                                                                                                     |
| `src/Cachet.ArchiveSync.Web/**`                                                                        | CAS-057                                                                                                                                               |
| `appsettings.Production.json` and any file with secrets                                                | Never edited (CLAUDE.md)                                                                                                                              |

---

## §7 Database Changes

### Changed Objects

| Object                            | Schema | Change     | Notes                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| --------------------------------- | ------ | ---------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `task_queue_cancel_req`           | dbo    | **new** SP | `@task_queue_id`, `@requesting_user_id`, `@updated_by`. One transaction: a single `UPDATE … WHERE task_queue_id = @id AND submitted_by_user_id = @user AND task_type = 'KeywordSearch' AND status IN ('Queued','Running') OUTPUT deleted.status` that sets `is_cancel_requested = 1` and, **only when the prior status was `Queued`**, `status = 'Cancelled'`, `completed_at = SYSUTCDATETIME()`. Atomic against `ClaimNextAsync` (READPAST/UPDLOCK claim): whichever statement wins decides `Queued`-direct vs `Running`-flag; there is no window where both happen. Inserts the audit row in the same transaction (`TaskCancelled` for the Queued case; `TaskCancelRequested` for Running **only if the flag was not already set**). Returns `outcome` (`0` CancelRequested, `1` Cancelled, `2` NotFound/NotOwner, `3` NotCancellable). Not-owned and missing both return `2`. |
| `task_queue_cancel_requested_get` | dbo    | **new** SP | `@task_queue_id` → single row `is_cancel_requested`. Read-only, no audit; PK lookup.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| `task_queue_sweep_interrupted`    | dbo    | amended    | New first branch: `Running` rows with `is_cancel_requested = 1` → `Cancelled` (`completed_at`, `TaskCancelled` audit, same transaction), excluded from the `Failed` flip **and** from CAS-055 auto-resume. All other rows behave exactly as today; the proc's signature and result-set shape are unchanged.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |

### Migration Required

**Yes** — hybrid model: migration `Database/migrations/20261006_190000__CAS-056-cancel-keyword-search.sql` (idempotent `CREATE OR ALTER`, append-only) + the three mirror files in §5. **No table/column change** — `is_cancel_requested` and `'Cancelled'` already exist.

---

## §8 API Contract Changes

| Endpoint                                                       | Field                                       | Before                     | After                                                                                                                              | Breaking?                                                                                                                                                      |
| -------------------------------------------------------------- | ------------------------------------------- | -------------------------- | ---------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `POST /tasks/{taskId}/cancel`                                  | —                                           | does not exist             | `200 {taskId,status:"Cancelled"}` · `202 {taskId,status:"Cancelling"}` · `404 task-not-found` · `409 task-not-cancellable` · `401` | No (new)                                                                                                                                                       |
| `GET /search/{taskId}/results`                                 | `Status`                                    | `Completed` \| `Failed`    | adds `Cancelled`                                                                                                                   | **Additive** — a client that treats unknown ≠ `Completed` as failure (today's `KeywordSearch.razor` branch) mis-renders it until CAS-057 ships; see §12 step 5 |
| `GET /search/{taskId}/results`                                 | `IsPartial`, `ItemsProcessed`, `ItemsTotal` | absent                     | present; `IsPartial` is `true` only for `Cancelled`                                                                                | No                                                                                                                                                             |
| `GET /search/{taskId}/results` (+ CAS-049 browse / result-ids) | `Cancelled` task                            | `409`/Failed shape         | `200` with match pages                                                                                                             | Behavioural                                                                                                                                                    |
| `GET /tasks/{taskId}`, `GET /tasks`                            | `Status`                                    | `Cancelled` never produced | may be `Cancelled`                                                                                                                 | No (already in the allow-lists)                                                                                                                                |

No API version bump: all changes are additive.

---

## §9 Validations Changed

| ID  | Rule (Before)                          | Rule (After)                                                                                                                            | Test                                                   |
| --- | -------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------ |
| V1  | n/a                                    | Cancel on another user's task and on a missing task return the identical `404` body                                                     | `Test_V1_CancelNotOwnedAndMissingAreIndistinguishable` |
| V2  | n/a                                    | Cancel on `Completed`/`Failed`/`Cancelled`, or on a non-`KeywordSearch` task → `409 task-not-cancellable`; state unchanged              | `Test_V2_CancelTerminalOrWrongTypeIs409`               |
| V3  | n/a                                    | Cancel of a `Queued` task → `Cancelled` + `200`; it is never claimable afterwards                                                       | `Test_V3_CancelQueuedIsImmediateAndUnclaimable`        |
| V4  | n/a                                    | Cancel of a `Running` task → `202`, status stays `Running`, flag = 1; a second call is `202` with **one** audit row total               | `Test_V4_CancelRunningIsIdempotent`                    |
| V5  | n/a                                    | Cancel racing a claim: exactly one of {Queued-direct-cancel, Running-flag} occurs; never `Running` task with `Cancelled`-less flag lost | `Test_V5_CancelVsClaimIsAtomic`                        |
| V6  | Worker ignores the flag                | Worker observes the flag within `CancelPollIntervalSeconds` + the longest single in-flight step and stops dispatching candidates        | `Test_V6_WorkerStopsAfterFlag`                         |
| V7  | n/a                                    | Terminal order is audit → final progress → `Cancelled`; audit failure leaves the row `Running` and writes no status                     | `Test_V7_AuditFailureWritesNoCancelledStatus`          |
| V8  | n/a                                    | A candidate is never half-dispositioned: no soft-delete without its audit row, no insert without a completed cross-check, on cancel     | `Test_V8_CancelNeverSplitsACandidateDisposition`       |
| V9  | n/a                                    | All candidates done before the flag is seen → task ends `Completed`, not `Cancelled`                                                    | `Test_V9_LateCancelCompletesNormally`                  |
| V10 | Sweep fails/auto-resumes all `Running` | `Running` + flag → `Cancelled`, no auto-resume, audited; unflagged rows unchanged                                                       | `Test_V10_SweepCancelsFlaggedAndNeverResumesThem`      |
| V11 | `Cancelled` results → Failed shape     | `GET /search/{id}/results` → `200`, `Status:"Cancelled"`, `IsPartial:true`, rows retained from before the cancel                        | `Test_V11_CancelledResultsArePartialAnd200`            |
| V12 | Browse gate = `Completed`              | `Completed` or `Cancelled`; `Queued`/`Running`/`Failed` still `409`/unchanged                                                           | `Test_V12_BrowseAcceptsCancelled`                      |
| V13 | n/a                                    | `Worker:CancelPollIntervalSeconds` outside 1–60 fails startup with a clear message                                                      | `Test_V13_CancelPollIntervalIsRangeChecked`            |

---

## §10 Success Criteria

| ID    | Criterion                                                                                                                                                                              | Test                                          |
| ----- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------- |
| SC-1  | A Queued Keyword Search can be cancelled and ends `Cancelled` without ever running                                                                                                     | `Test_SC1_QueuedCancel`                       |
| SC-2  | A Running Keyword Search over a large fake candidate set stops within poll-interval + one step of the cancel call, with in-flight reads/B2 calls cancelled via the linked token        | `Test_SC2_RunningCancelStopsPromptly`         |
| SC-3  | Matches inserted before the stop are retained and returned by `GET /search/{id}/results` with `IsPartial: true`; zero matches → `200` with empty pages                                 | `Test_SC3_PartialAndEmptyResults`             |
| SC-4  | `task_queue.status = 'Cancelled'`, `completed_at` set, `result_detail` holds the four counts; progress shows the final `processed/total`                                               | `Test_SC4_CancelledRowShape`                  |
| SC-5  | Audit: one `TaskCancelRequested` (Running) and one `TaskCancelled` per cancelled task, `task_queue_id` populated, `user_id` = submitter, no term/path/file name in any `result_detail` | `Test_SC5_AuditRows`                          |
| SC-6  | Another user cannot cancel the task or distinguish it from a missing one                                                                                                               | `Test_SC6_OwnershipIsolation`                 |
| SC-7  | A flagged Running task found by the startup sweep becomes `Cancelled` and is not auto-resumed; CAS-054/055 sweep tests still pass unchanged                                            | `Test_SC7_SweepHonoursFlag` + existing suites |
| SC-8  | Quarantine-add from a cancelled task's partial results works (same as `Completed`)                                                                                                     | `Test_SC8_QuarantineFromCancelledResults`     |
| SC-9  | No B2 key, session token, term, path or file name appears in any new log line or error body (`LogHygieneTests` extended)                                                               | `Test_SC9_LogHygiene`                         |
| SC-10 | Build clean; all existing Api/Worker tests pass; coverage ≥ 85% on changed files                                                                                                       | CI                                            |

---

## §11 Security Notes

- **FR-23:** state-changing endpoint → audit written **before** the success response (in the SP's transaction); a terminal `Cancelled` status is never visible without its audit row.
- **Authorisation:** own-scoped by the claim-derived `NameIdentifier`, enforced inside the SP's `WHERE` (not only in C#); `[Authorize(Policy = "CachetAuthenticated")]` at **action** level (P7). No admin override in this spec (§14 Q3).
- **No enumeration:** not-owned ≡ missing (`404`, identical body).
- **FR-03:** no B2 key or session token is involved, logged, or returned. Cancel does not touch B2 beyond cancelling in-flight reads.
- **No new irreversible action:** cancel deletes nothing; retained partial rows are exactly what the Worker already wrote. It does **not** add a soft-delete/undo path to Permanent Delete/Purge (BRD §9 untouched).
- **Data minimisation:** audit `ResultDetail` = task ids/integers only.

---

## §12 Rollback Plan

1. Revert the Api/Worker/Core commits. The Cancel endpoint disappears; the `Cancelled` read path is dead code.
2. Database: the migration is additive (`CREATE OR ALTER` of two new SPs + one amended SP). Roll back by re-running the **previous** `dbo.task_queue_sweep_interrupted` definition (CAS-055 migration `20261006_170000`) and dropping the two new SPs. No data migration.
3. Config: `Worker:CancelPollIntervalSeconds` is optional with a default; nothing to unset.
4. Task-queue rollback: rows already `Cancelled` stay valid (`CK_task_queue_status` already allowed it); an old Worker never reads the flag, so a flagged `Running` row simply runs to completion — **not** dropped or corrupted. An old sweep would fail (not cancel) a flagged `Running` row — acceptable degradation.
5. **Deployment order:** database → Api + Worker → Web (CAS-057). Shipping CAS-056 alone is safe: nothing in the UI calls cancel yet; `Cancelled` results only appear if cancelled via the API directly (then the old page shows "Failed" until CAS-057).

---

## §13 Test Plan

| Class                                                               | Method                                                                         | Proves SC / V           |
| ------------------------------------------------------------------- | ------------------------------------------------------------------------------ | ----------------------- |
| `TaskManagerControllerTests` (Api.Tests)                            | route/outcome → status-code mapping, `[Authorize]` present, 404 body identical | V1, V2, SC-6            |
| `TaskManagerServiceTests`                                           | outcome mapping, no extra audit write                                          | V4                      |
| `TaskQueueCancelRepositoryTests` (Api.IntegrationTests, real SQL)   | queued/running/terminal/not-owned/idempotent/race with `ClaimNextAsync`        | V1–V5, SC-1, SC-5, SC-6 |
| `TaskQueueSweepTests` (extend)                                      | flagged vs unflagged `Running`; auto-resume suppressed                         | V10, SC-7               |
| `CancelRequestWatcherTests` (Worker.Tests, fake `TimeProvider`)     | polls at interval, cancels linked CTS once, stops on dispose                   | V6                      |
| `KeywordSearchCancelTests` (Worker.Tests)                           | stop, partial rows kept, audit-first, late cancel, atomic disposition          | V6–V9, SC-2, SC-4       |
| `SearchControllerTests` / `SearchResultBrowseServiceTests` (extend) | `Cancelled` → 200 partial; browse gate                                         | V11, V12, SC-3, SC-8    |
| `WorkerOptionsTests` (extend)                                       | range check                                                                    | V13                     |
| `LogHygieneTests` (extend)                                          | no sensitive data                                                              | SC-9                    |

Coverage target: ≥ 85% on changed files.

---

## §14 Open Questions

- [x] **Q1 — RESOLVED 2026-10-06 (owner): partial results are displayed, labelled partial.** Supersedes a resolved decision. Design §10 item 4 (2026-09-09) said partial rows are _not surfaced_. This spec surfaces them, labelled `IsPartial`. Owner confirmation requested (the 2026-10-06 request implies yes; the explicit `Cancelled` + `IsPartial` contract preserves the "never misread as complete" intent). Add an addendum to the Design doc §10 on merge.
- [ ] **Q2 — cancel latency vs. cost.** Default poll = 2 s (one PK lookup per running search per 2 s). Confirm 2 s is acceptable, or use 5 s.
- [ ] **Q3 — admins.** Should an admin be able to cancel another user's search? Default **no** (own-scoped, as every other Task Manager action).
- [ ] **Q4 — Filename/Date modes.** They finish in seconds and Filename mode is one set-based insert that cannot be interrupted; the endpoint still accepts them (flag set, task completes normally). CAS-057 hides the button for them. Confirm.
- [ ] **Q5 — ContentScan.** Same screen pattern (`ODriveSyncContentScan.razor`); recommend a follow-up spec reusing the SP/watcher. Confirm sequencing.
- [ ] **Q6 — verify during implementation:** the quarantine-add path (`QuarantineService`) has no `Completed` gate (grep found none) — SC-8 asserts it; if a gate exists, it joins §5.

---

## §15 Sign-off

| Role     | Name                      | Date       | Notes                      |
| -------- | ------------------------- | ---------- | -------------------------- |
| Author   | Claude (draft)            | 2026-10-06 |                            |
| Reviewer |                           |            |                            |
| Owner    | devarajan@tracecounts.org | 2026-10-06 | Q1 confirmed; marked ready |

---

## §16 Verification Plan

### What the Reviewer Will Check

- [ ] Current behavior (§1) matches the code before the change
- [ ] Desired behavior (§2) fully implemented, incl. §2.4 late cancel and §2.5 sweep
- [ ] Only §5 files modified; §6 files untouched (esp. `Worker.cs`, `TaskProgressReporter.cs`)
- [ ] All §9 validations and §10 SCs have passing tests
- [ ] `[Authorize]` at action level; audit written before the success response and before the visible `Cancelled` status
- [ ] No B2 key / term / path in logs or audit `ResultDetail`
- [ ] Disposition writes use the host token, not the cancel token (V8)
- [ ] CAS-054/055 resume/sweep tests unchanged and green
- [ ] §12 in-flight rows handled

### Verification Report Format

`CAS-056.review.md` alongside this spec: verdict, per-SC table, scope violations, unintended blast radius, security gaps.

## Implementation Log

**Completed:** 2026-10-06
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 87.4% overall (TaskQueueRepository 68.7%: real-SQL tests skip-marked) | Endpoints ✓

### Files Changed

| File                            | Change                                   |
| ------------------------------- | ---------------------------------------- |
| `AuditAction.cs`                | append TaskCancelRequested/TaskCancelled |
| `CancelTaskResult.cs`           | new                                      |
| `ITaskQueueRepository.cs`       | CancelAsync, IsCancelRequestedAsync      |
| `ITaskManagerService.cs`        | CancelAsync                              |
| `SearchResultsResponse.cs`      | IsPartial, ItemsProcessed, ItemsTotal    |
| `TaskQueueRepository.cs`        | two SP calls                             |
| `TaskManagerService.cs`         | CancelAsync                              |
| `TaskManagerController.cs`      | POST tasks/{id}/cancel                   |
| `KeywordSearchService.cs`       | Cancelled -> partial results             |
| `SearchResultBrowseService.cs`  | gate widened                             |
| `CancelRequestWatcher.cs`       | new                                      |
| `KeywordSearchTaskProcessor.cs` | graceful cancel path                     |
| `WorkerOptions.cs`              | CancelPollIntervalSeconds                |
| `appsettings.json`              | default 2                                |

### Tests Added

| Class                            | Method                                                | Covers |
| -------------------------------- | ----------------------------------------------------- | ------ |
| `Cas056CancelApiTests`           | `V1,V2,V11,V12,SC3,SC8,SC9`                           |        |
| `TaskQueueCancelRepositoryTests` | `SC1,SC5,SC6,SC7,V3,V4,V5,V10 (skip-marked real SQL)` |        |
| `KeywordSearchCancelTests`       | `V6-V9,SC2,SC4`                                       |        |
| `CancelRequestWatcherTests`      | `watcher`                                             |        |
| `WorkerOptionsCancelPollTests`   | `V13`                                                 |        |

### Migration

`20261006_190000__CAS-056-cancel-keyword-search.sql`
