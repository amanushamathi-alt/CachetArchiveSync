---
id: CAS-054
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-06
priority: P1
change_type: enhancement
related_specs: [CAS-006, CAS-012, CAS-016, CAS-028, CAS-029, CAS-047, CAS-055]
touches:
  database: true
  api: true
  ui: false
  worker: true
  b2: false
  config: true
  security: true
migrations: [20261006_150000__CAS-054-keyword-search-resume-watermark.sql]
---

# CAS-054 — Change: Resume an interrupted Keyword Search from a durable watermark

## §1 Current Behavior

*Verified by manual read — graphify not yet useful for this change: `graphify explain "KeywordSearchTaskProcessor"` returns a single file-level node (degree 1); the graph is not call-aware for this class, so the blast radius in §4 was mapped via manual grep/read.*

- **Progress is a pair of counters only.** `dbo.task_queue` stores `items_processed` / `items_total` (CAS-029) written by `dbo.task_queue_progress_upd`. Nothing records *which* candidates were completed. A candidate that does not match leaves no trace in any table.
- **Files finish out of order.** `KeywordSearchTaskProcessor` processes candidates through a bounded parallel loop (default one per core). `items_processed = N` therefore does not mean "the first N candidates".
- **Interrupted tasks are failed at startup.** `Worker.RunStartupSweepAsync` calls `dbo.task_queue_sweep_interrupted`, which flips every `Running` row to `Failed` ("Interrupted by a service restart - please restart or resubmit this task.") and writes a `TaskInterruptedByRestart` audit row per task (CAS-028 §3.4).
- **Restart re-runs everything and creates a new task.** `dbo.task_queue_restart_ins` (CAS-016, Model B) inserts a **new** `Queued` row copying `task_type` / `request_parameters`, linked by `restarted_from_task_queue_id`; the original row is never updated. The new task's search therefore starts at candidate 1. `TaskManagerService.RestartAsync` audits `TaskRestart` against the new row.
- **Matches are already durable.** Each confirmed match is inserted into `dbo.search_result` (keyed `task_queue_id`, `file_index_id`, `match_type`; `UQ_search_result_task_file_matchtype`) *before* the candidate is counted as complete. Match results of the original task remain under the original `task_queue_id` and are not visible from the restarted task.
- **Candidate order is stable.** `dbo.file_index_search` returns `ORDER BY file_index_id ASC`; `file_index_id` is an identity column.
- **Observed impact:** task 100031 (`"Term":"pioneer"`, B2 `Cachet-Server-C`, Keyword mode) was interrupted at `items_processed = 31149` of `310854`; all of that work is discarded on restart.

---

## §2 Desired Behavior

When a `Failed` Keyword Search task is restarted from Task Manager, the new task **continues from the point the original reached** instead of starting at candidate 1:

1. While a Keyword Search runs, the Worker persists a **resume watermark**: the highest `file_index_id` W such that *every* candidate with `file_index_id <= W` has been fully dispositioned (match inserted / dropped / excluded / indeterminate). The watermark is contiguous, so out-of-order completion can never cause a skipped, unchecked file.
2. The watermark is saved on the running task row at most once per throttle interval (default 10 s), monotonically non-decreasing, only while the row is `Running`. After a crash/sweep the row is `Failed` and its watermark is frozen.
3. `dbo.task_queue_restart_ins`, for a `KeywordSearch` row with a non-null watermark, in the **same transaction** as the new-row insert: (a) stores the original's watermark on the new row, and (b) copies the original's `dbo.search_result` rows with `file_index_id <= W` to the new `task_queue_id`. Rows above W are **not** copied — those files are re-checked, so no duplicate-key conflict can arise.
4. The Worker, on claiming a task whose row has a non-null watermark W, re-runs the candidate query, drops candidates with `file_index_id <= W` before processing, and reports progress as `items_total = full candidate count`, `items_processed` starting at the number of skipped candidates, so Task Manager shows continuity (e.g. 31,149/310,854 → continues upward).
5. The result summary of a resumed task includes `ResumedFromTaskQueueId`, and match counts include the carried-over rows. `ExcludedCount` / `IndeterminateCount` cover only the post-resume portion (see §14 Q2).
6. **24-hour limit (owner decision, 2026-10-06).** Carry-over/resume happens only if the original's last progress write (`progress_updated_at`) is within `Worker:ResumeMaxAgeHours` (default **24**) of the restart. Older, or with no watermark, the restart is a full restart exactly as today (new task starts at candidate 1, nothing copied).
7. The restart audit row (`TaskRestart`) states the resume: `ResultDetail` = `"Resumed from file_index_id <W>; <M> match result(s) carried over from task <original>."`. A restart that does not resume (no watermark, or older than the limit) behaves exactly as today and its audit `ResultDetail` says "Full restart (no checkpoint)" or "Full restart (checkpoint older than 24 h)".

Out of scope: `ContentScan`, `SyncCopy`, `SyncUpdate`, `SyncPurge` tasks; Keyword `Filename` mode (a single set-based insert — atomic, no per-file work to resume); a "start over" option (owner chose resume-always); files skipped as encrypted/unreadable stay excluded from search (unchanged); **automatic (unattended) resume at service start — specified separately in CAS-055**, which builds on this spec; any change to the sweep's Running→Failed behavior (CAS-055 extends it).

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** Service crash on 2026-10-06 lost ~31k of 310k files of work (task 100031); owner request to resume interrupted tasks from where they stopped. Owner confirmed the archive files are static (2026-10-06), which makes reuse of pre-interruption results safe.
- **Risk if not changed:** every interruption (crash, deploy, restart) discards hours of search work on a ~310k-file archive; users are pushed toward avoiding restarts/deploys.

---

## §4 Blast Radius (Graph Impact)

### Primary Change Target
Files: `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs`; `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_restart_ins.sql`
Entity: Keyword Search execution loop + task-queue restart path.
Current edges: manual read (graphify gave only a file node). Direct collaborators: `ITaskQueueRepository`, `ITaskProgressReporter`/`TaskProgressReporter`, `ISearchResultRepository`, `IFileIndexRepository`, `ClaimedTaskRow`, `KeywordSearchResultSummaryDto`.

### Direct Dependents (nodes with edges INTO the changed entity)
- `Worker.cs` — claims tasks and calls `ProcessAsync` (unchanged).
- `ITaskQueueRepository` / `TaskQueueRepository` — gains `UpdateResumeWatermarkAsync`; `RestartAsync` mapping gains two output fields.
- `dbo.task_queue_claim` → `ClaimedTaskRow` — must now return the watermark column.
- `TaskManagerService.RestartAsync` / `TaskManagerController` restart route — audit detail only; route and status codes unchanged.
- Test fakes: `KeywordSearchTaskProcessorTests`, `WorkerTests`, `TaskManagerServiceTests`, `FakeTaskManagerApiHandler` (Web.Tests), `TaskQueueRestart*Tests`, `TaskQueueClaimTests`.

### Downstream Modules
Keyword Search, Task Manager, Audit Log. (Quarantine, Sync, Reporting: none. Search-result views read `dbo.search_result` by `task_queue_id` — they automatically show carried-over rows under the new task.)

### Cross-Project Risk
**CROSS-PROJECT RISK:** `Cachet.ArchiveSync.Core` contracts shared by Api and Worker change (`ITaskQueueRepository`, `ClaimedTaskRow`, `RestartTaskResult`, `KeywordSearchResultSummaryDto`). The Web project calls the Api restart route; its request/response shape is unchanged (additive result fields only), so Web is untouched.

### God Node Involvement
`KeywordSearchTaskProcessor` is large but not a graph god node (degree 1 in the AST-only graph). `dbo.task_queue` is a central table (read by claim, sweep, list, progress, restart, summary): treated as **HIGH RISK for schema changes** — hence one additive, nullable column and no constraint changes to existing columns.

### Decomposition Check (P9)
Spans Database, Core, Api, Worker, but the pieces are one contract (Worker writes the watermark; restart SP and claim SP read it) and cannot be verified separately. Size thresholds: §5 files ≈ 15 (not > 15), SCs = 11 (< 12), DB objects = 1 table column + 3 SPs (< 3 tables / 8 SPs), endpoints = 0 new. **Decision: single spec for checkpoint + resume-on-restart, implemented in order DB → Core → Api → Worker.** The owner's request for *automatic* resume at service start was split out as **CAS-055** because adding it here would cross the file and SC thresholds and it carries a distinct risk (crash-resume loops).

### Graphify Queries Run
```
python -m graphify explain "KeywordSearchTaskProcessor"   → file node only (degree 1); not call-aware
```
Blast radius otherwise mapped via manual grep/read of: `task_queue.sql`, `task_queue_restart_ins.sql`, `task_queue_progress_upd.sql`, `task_queue_sweep_interrupted.sql`, `search_result.sql`, `search_result_ins.sql`, `TaskManagerService.cs`, `KeywordSearchTaskProcessor.cs`, `Worker.cs`.

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `Database/migrations/20261006_150000__CAS-054-keyword-search-resume-watermark.sql` | add | Add column; create/alter 3 SPs (idempotent, append-only) |
| `Database/CachetArchiveSync/Entities/dbo.task_queue.sql` | modify | Mirror: `resume_watermark_file_index_id INT NULL` |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_resume_watermark_upd.sql` | add | New SP (§7) |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_restart_ins.sql` | modify | Copy watermark + carry over results; new OUTPUTs |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_claim.sql` | modify | Return `resume_watermark_file_index_id` |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs` | modify | Add `UpdateResumeWatermarkAsync` |
| `src/Cachet.ArchiveSync.Core/Dtos/ClaimedTaskRow.cs` | modify | Add `ResumeWatermarkFileIndexId` |
| `src/Cachet.ArchiveSync.Core/Dtos/RestartTaskResult.cs` | modify | Add `ResumedFromFileIndexId`, `CarriedOverCount` |
| `src/Cachet.ArchiveSync.Core/Dtos/KeywordSearchResultSummaryDto.cs` | modify | Add `ResumedFromTaskQueueId` |
| `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs` | modify | Implement new method; map new restart OUTPUTs and claim column |
| `src/Cachet.ArchiveSync.Api/Services/TaskManagerService.cs` | modify | Pass `Worker:ResumeMaxAgeHours` to restart; audit `ResultDetail` (resumed / full restart + reason) |
| `src/Cachet.ArchiveSync.Api/appsettings.json` | modify | Add `Worker:ResumeMaxAgeHours` = 24 (non-secret; code default is also 24) |
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs` | modify | Skip ≤ W, track + persist watermark, carry-over counts, summary field |
| `src/Cachet.ArchiveSync.Worker/ResumeWatermarkTracker.cs` | add | Contiguous-watermark tracker (position-based) |
| Tests (§13) | add/modify | New + updated tests |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `Database/.../dbo.search_result.sql`, `dbo.search_result_ins.sql` | Unique key and insert stay as-is; carry-over copies only rows ≤ W so no conflict handling is needed |
| `Database/.../dbo.task_queue_sweep_interrupted.sql` | Running→Failed behavior and audit unchanged; the watermark simply persists on the swept row |
| `Database/.../dbo.task_queue_progress_upd.sql` | Kept as-is so existing 5-argument mocks/tests and CAS-029 behavior are untouched; watermark uses its own SP |
| `Cachet.ArchiveSync.Worker/Worker.cs` | Claim/dispatch/sweep flow unchanged |
| `TaskProgressReporter.cs` | Reused as-is (progress counters only) |
| `ContentScan*`, `Sync*TaskProcessor.cs`, B2 client | Out of scope |
| `Cachet.ArchiveSync.Web/**` | Restart button/route/response shape unchanged |
| `Extraction/**`, `FileContentMatcher*` | Unrelated to resume |

---

## §7 Database Changes

### Changed Objects
| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| `task_queue` | dbo | column added | `resume_watermark_file_index_id INT NULL` — no default, no constraint; NULL for every non-Keyword task and for rows that never wrote one |
| `task_queue_resume_watermark_upd` | dbo | new SP | `@task_queue_id INT, @updated_by NVARCHAR(50), @resume_watermark_file_index_id INT`; validates args (V1); `UPDATE ... SET resume_watermark_file_index_id = @w, updated_by, updated_at WHERE task_queue_id = @id AND status = N'Running' AND task_type = N'KeywordSearch' AND (resume_watermark_file_index_id IS NULL OR resume_watermark_file_index_id < @w)`; returns `rows_affected` (0 = not Running / not newer — not an error) |
| `task_queue_restart_ins` | dbo | modified | After the existing new-row insert: if source `task_type = N'KeywordSearch'` and source watermark `W IS NOT NULL` → set the new row's `resume_watermark_file_index_id = W` and `INSERT INTO dbo.search_result (created_by, updated_by, task_queue_id, file_index_id, match_type, match_snippet, match_status) SELECT @created_by, @updated_by, @new_task_queue_id, file_index_id, match_type, match_snippet, match_status FROM dbo.search_result WHERE task_queue_id = @task_queue_id AND file_index_id <= W`. New parameters (all optional so old callers still work): `@resume_max_age_hours INT = NULL` (NULL = no age limit; the Api always passes the configured value) — resume/carry-over only when `DATEDIFF(MINUTE, source.progress_updated_at, SYSUTCDATETIME()) <= @resume_max_age_hours * 60` and `progress_updated_at IS NOT NULL`; `@resumed_from_file_index_id INT = NULL OUTPUT`, `@carried_over_count INT = NULL OUTPUT`, `@resume_note NVARCHAR(40) = NULL OUTPUT` (`'Resumed'`, `'NoCheckpoint'` or `'CheckpointTooOld'`, for the audit detail). All inside the existing `UPDLOCK` transaction (atomic with the new row). |
| `task_queue_claim` | dbo | modified | Result set adds `resume_watermark_file_index_id` |

### Migration Required
**Yes.** Two consistent artifacts (hybrid model):
- Migration: `Database/migrations/20261006_150000__CAS-054-keyword-search-resume-watermark.sql` — `IF COL_LENGTH('dbo.task_queue','resume_watermark_file_index_id') IS NULL ALTER TABLE ... ADD ...` then `CREATE OR ALTER` of the three procedures. Idempotent; append-only; includes a commented manual rollback.
- Mirrors: `dbo.task_queue.sql` (Entities) and the three SP files (Stored-Procedures).

---

## §8 API Contract Changes

`POST /tasks/{id}/restart` (existing, CAS-016): status codes, request and the existing response fields are **unchanged**. `RestartTaskResult` gains two additive internal fields; if the controller response DTO exposes them it is additive and non-breaking (decision for the implementer — not required for the feature; see §14 Q5).

| Endpoint | Field | Before | After | Breaking? |
|----------|-------|--------|-------|-----------|
| `POST /tasks/{id}/restart` | (none required) | — | — | No |
| Task result summary (`result_detail` JSON) | `ResumedFromTaskQueueId` | absent | present on resumed Keyword tasks | No (additive; readers ignore unknown) |

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | n/a | `task_queue_resume_watermark_upd`: `@task_queue_id`, `@updated_by` required; `@resume_watermark_file_index_id` required and > 0 | `Test_V1_ResumeWatermarkUpd_RejectsMissingArguments` |
| V2 | Restart copies type/params only | Restart of a `KeywordSearch` row with a watermark copies the watermark and carries over `search_result` rows with `file_index_id <= W`, atomically with the new row | `Test_V2_RestartCarriesWatermarkAndResultsAtomically` |
| V3 | n/a | Watermark never decreases (stale/out-of-order write is a no-op) and is written only while `Running` | `Test_V3_WatermarkMonotonicAndOnlyWhileRunning` |
| V4 | n/a | Worker skips exactly the candidates with `file_index_id <= W`; candidates above W (including any that finished out of order before the interruption) are re-checked | `Test_V4_ResumedTaskSkipsOnlyCandidatesAtOrBelowWatermark` |
| V5 | n/a | Tracker's watermark is contiguous: a still-running lower candidate holds the watermark below any completed higher candidate | `Test_V5_WatermarkIsContiguousUnderOutOfOrderCompletion` |
| V6 | n/a | A restart with a NULL watermark, a non-Keyword task, or Keyword `Filename` mode behaves exactly as before CAS-054 | `Test_V6_RestartWithoutWatermarkBehavesAsBefore` |
| V7 | n/a | Resume applies only if `progress_updated_at` is within `Worker:ResumeMaxAgeHours` (default 24, must be 1–720); otherwise full restart, nothing copied | `Test_V7_RestartOlderThanLimitIsFullRestart` |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | While a Keyword Search runs, `resume_watermark_file_index_id` advances monotonically and never exceeds the highest candidate for which all lower candidates are complete | `Test_SC1_WatermarkAdvancesOnlyOverContiguousCompletedCandidates` |
| SC-2 | After the Worker is killed and the sweep marks the task `Failed`, restarting it creates a new task whose watermark equals the original's last persisted watermark | `Test_SC2_RestartAfterSweepInheritsWatermark` |
| SC-3 | The restarted task's `dbo.search_result` contains every original match with `file_index_id <= W`, no match above W, and no duplicates after the task completes | `Test_SC3_RestartedTaskResultsEqualOriginalBelowWatermarkPlusRecheckedAbove` |
| SC-4 | The resumed task processes no candidate with `file_index_id <= W` (zero mirror reads / extractions for them) and does process every candidate above W | `Test_SC4_ResumedTaskDoesNotReadCandidatesAtOrBelowWatermark` |
| SC-5 | Progress for the resumed task starts at the skipped count and ends at `items_total` (`items_processed = items_total` at `Completed`) | `Test_SC5_ResumedProgressStartsAtSkippedCountAndReachesTotal` |
| SC-6 | Final resumed result set equals the result set of an uninterrupted run over an unchanged index (resume gives the same answer as a full run) | `Test_SC6_ResumedRunMatchesUninterruptedRun` |
| SC-7 | Restart of a task with no watermark (NULL), or of any non-Keyword task, produces the same new row and results as before this change | `Test_SC7_RestartWithoutWatermarkUnchanged` |
| SC-8 | The restart audit row records the resume (watermark and carried-over count) and is written before the 202 response | `Test_SC8_RestartAuditDetailNamesResumeAndCarryOver` |
| SC-9 | The summary of a resumed task includes `ResumedFromTaskQueueId`; filename/content counts include carried-over rows | `Test_SC9_ResumedSummaryNamesSourceTaskAndIncludesCarriedCounts` |
| SC-11 | A restart whose original last made progress more than `Worker:ResumeMaxAgeHours` ago starts from candidate 1 with no results copied, and its audit detail says so | `Test_SC11_RestartBeyondMaxAgeIsFullRestart` |
| SC-10 | Application and migration deploy independently: new Worker + old DB, or old Worker + new DB, still completes searches (no watermark ⇒ no resume, no failure) | `Test_SC10_WorkerToleratesMissingWatermarkColumnOrProcedure` |

---

## §11 Security Notes

- **FR-23 / audit completeness:** the restart is audited before the response, now with resume detail (no file names, paths, terms or content — only integers and task ids). The sweep's `TaskInterruptedByRestart` audit is unchanged.
- **Data minimization:** the only new persisted datum is an integer id (`file_index_id`); no file content, snippet or term is stored beyond what `dbo.search_result` already holds.
- **Ownership:** carry-over runs inside `task_queue_restart_ins` *after* its existing `NOT_OWNER` / `NOT_FOUND` / `NOT_FAILED` checks, so only the submitter can cause rows to be copied, and only from their own failed task.
- **Integrity caveat (auditability):** a resumed search is a composite of two runs. If files were added/changed after the interruption, results for files ≤ W reflect the pre-interruption state. The resume is therefore **recorded** (audit `ResultDetail` and summary `ResumedFromTaskQueueId`) so a report can state it. See §14 Q3.
- No B2 credentials, session or auth paths are touched (`touches.security: true` solely for the audit-log path).

---

## §12 Rollback Plan

1. Revert the Worker/Api builds. Old code ignores the new column and the new SP; restart reverts to full restart (no behavior break).
2. Database: the column and new SP may stay (additive, nullable, uncalled). To fully revert, re-run the `task_queue_restart_ins` and `task_queue_claim` bodies from the prior migrations (CAS-016 / CAS-028), then optionally `DROP PROCEDURE dbo.task_queue_resume_watermark_upd` and `ALTER TABLE dbo.task_queue DROP COLUMN resume_watermark_file_index_id` (commented in the migration).
3. Config rollback: remove `Worker:ResumeMaxAgeHours` (code default 24 applies). The watermark write throttle (10 s) is a code constant.
4. **Task-queue rollback / in-flight rows:** rows `Running`/`Queued` created before the migration have `NULL` watermark → restart behaves as a full restart (SC-7). Rows already carried over (copied `search_result` rows) remain valid on the new task. No `Queued`/`Running` row is dropped or altered by the migration.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `ResumeWatermarkTrackerTests` (Worker.Tests) | `Test_V5_…`, `Test_SC1_…` | V5, SC-1 |
| `KeywordSearchTaskProcessorTests` | `Test_V4_…`, `Test_SC4_…`, `Test_SC5_…`, `Test_SC9_…` | V4, SC-4, SC-5, SC-9 |
| `KeywordSearchResumeTests` (new, fake queue/repos) | `Test_SC6_…` | SC-6 |
| `TaskQueueResumeWatermarkRepositoryTests` (IntegrationTests, real DB) | `Test_V1_…`, `Test_V3_…` | V1, V3 |
| `TaskQueueRestartRepositoryTests` (extend) | `Test_V2_…`, `Test_V6_…`, `Test_SC2_…`, `Test_SC3_…`, `Test_SC7_…` | V2, V6, SC-2, SC-3, SC-7 |
| `TaskManagerServiceTests` (extend) | `Test_SC8_…` | SC-8 |
| `TaskQueueRestartRepositoryTests` (extend) | `Test_V7_…`, `Test_SC11_…` | V7, SC-11 |
| `WorkerResumeCompatibilityTests` | `Test_SC10_…` | SC-10 |

Existing tests that assert exact progress writes keep passing unchanged (the progress SP and reporter are untouched). Coverage target: ≥ 85% on changed files.

---

## §14 Open Questions

All questions resolved by the owner (devarajan@tracecounts.org) on 2026-10-06:

- [x] **Q1 — Resume vs start over:** always resume (no start-over option).
- [x] **Q2 — Counts across a resume:** accepted — excluded/indeterminate counts cover the post-resume portion only; summary labelled resumed.
- [x] **Q3 — Point-in-time semantics:** files are static; a **24-hour** maximum resume age is accepted (§2.6, V7, SC-11).
- [x] **Q4 — Restart trigger:** the owner wants resume to happen **automatically with no user intervention as soon as the service is back online**. This changes the CAS-016 / BRD §12.3 item 12 model (user-initiated restart); specified separately as **CAS-055** (depends on this spec). Manual restart via Task Manager remains and also resumes.
- [x] **Q5 — Response surface:** audit log only; the restart API response is unchanged.
- [x] **Q6 — Write interval:** 10 s code constant (recommendation accepted).
- [x] **Q7 — BRD traceability:** proceed; the BRD wording amendment (restart/auto-resume continues Keyword Search from the last checkpoint) is to be raised with the client — BRD lives outside this repo, so this spec cannot edit it.
- [x] **Q8 — Encrypted/unreadable files:** remain excluded from search (no change). A skipped-count in the summary is a separate, optional future spec.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude (draft) | 2026-10-06 | Owner answered all §14 questions 2026-10-06 |
| Reviewer | | | |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches what is actually in the code BEFORE the change
- [ ] Desired behavior (§2) is fully implemented
- [ ] Only files in §5 were modified (§6 files are untouched)
- [ ] All §10 Success Criteria have passing tests
- [ ] All §9 Validations are implemented and tested
- [ ] §8 API contract is correct (no undeclared breaking changes)
- [ ] §11 Security Notes implemented as described (audit before response; no file names/paths/terms in resume audit detail)
- [ ] §4 Blast Radius is accurate — no unintended side effects in connected nodes (claim, sweep, list, progress, summary)
- [ ] §12 in-flight task-queue rows are handled, not silently broken
- [ ] The watermark is contiguous (never skips an unchecked candidate) and the carry-over copies only rows ≤ W

### Verification Report Format
Reviewer writes `CAS-054.review.md`:
- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5 file list)
- Unintended blast radius (manual read found changes in §6 files)
- Security gaps

## Implementation Log

**Completed:** 2026-10-06
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 88.2% | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Core/Interfaces/ITaskQueueRepository.cs` | UpdateResumeWatermarkAsync; RestartAsync resumeMaxAgeHours |
| `Core/Dtos/ClaimedTaskRow.cs` | ResumeWatermarkFileIndexId, RestartedFromTaskQueueId |
| `Core/Dtos/RestartTaskResult.cs` | ResumedFromFileIndexId, CarriedOverCount, ResumeNote |
| `Core/Dtos/KeywordSearchResultSummaryDto.cs` | ResumedFromTaskQueueId |
| `Api/Repositories/TaskQueueRepository.cs` | watermark write; restart OUTPUT + claim mapping |
| `Api/Services/TaskManagerService.cs` | ResumeMaxAgeHours; resume audit detail |
| `Api/appsettings.json` | Worker:ResumeMaxAgeHours=24 |
| `Worker/KeywordSearchTaskProcessor.cs` | skip <= W; track/persist watermark; summary |
| `Worker/ResumeWatermarkTracker.cs` | new contiguous watermark tracker |
| `Database/migrations/20261006_150000__CAS-054-keyword-search-resume-watermark.sql` | column + 3 SPs (claim also returns restarted_from_task_queue_id) |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `ResumeWatermarkTrackerTests` | `Test_V5_*, Test_SC1_*` | V5, SC-1 |
| `KeywordSearchTaskProcessorTests` | `Test_V4_*, Test_SC4_*, Test_SC5_*, Test_SC9_*` | V4, SC-4, SC-5, SC-9 |
| `KeywordSearchResumeTests` | `Test_SC6_*, Test_DefectProbe_*` | SC-6, SC-1 |
| `WorkerResumeCompatibilityTests` | `Test_SC10_*` | SC-10 |
| `TaskManagerServiceTests` | `Test_SC8_*, Test_SC11_*_AuditDetail, Test_V7_*` | SC-8, SC-11, V7 |
| `TaskQueueResumeWatermarkRepositoryTests` | `Test_V1_*, Test_V3_*` | V1, V3 (DB, verified locally) |
| `TaskQueueRestartRepositoryTests` | `Test_V2_*, Test_V6_*, Test_V7_*, Test_SC2_*, Test_SC3_*, Test_SC7_*, Test_SC11_*` | V2, V6, V7, SC-2, SC-3, SC-7, SC-11 (DB, verified locally) |

### Migration
`20261006_150000__CAS-054-keyword-search-resume-watermark.sql`
