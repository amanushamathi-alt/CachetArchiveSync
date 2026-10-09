---
id: CAS-055
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-06
priority: P1
change_type: enhancement
related_specs: [CAS-054, CAS-016, CAS-028, CAS-029, CAS-030]
touches:
  database: true
  api: false
  ui: false
  worker: true
  b2: false
  config: true
  security: true
migrations: [20261006_170000__CAS-055-keyword-search-auto-resume.sql]
---

# CAS-055 — Change: Automatically resume an interrupted Keyword Search when the service comes back online

> **Depends on CAS-054** (durable resume watermark + carry-over of results). Implement CAS-054 first; this spec adds only the *unattended trigger* and its safety limits. Split from CAS-054 under Karpathy P9 (file and SC thresholds) and because it carries a distinct risk (crash-resume loops).

## §1 Current Behavior

Manual read — graphify is not call-aware for these objects (see CAS-054 §4); blast radius mapped by grep/read of `Worker.cs`, `dbo.task_queue_sweep_interrupted.sql`, `SweptTaskRow.cs`, `AuditAction.cs`, `dbo.task_queue_restart_ins.sql`.

- On Worker start (after the single-instance lock, CAS-028 §3.7) `Worker.RunStartupSweepAsync` (`Worker.cs`) calls `ITaskQueueRepository.SweepInterruptedAsync(ActingProcessIdentity, InterruptedByRestartReason, ct)` → `dbo.task_queue_sweep_interrupted`.
- That procedure, in **one transaction**, flips every `Running` row to `Failed` (`completed_at`, `result_detail = {"FailureReason": "Interrupted by a service restart - please restart or resubmit this task."}`), inserts one `audit_log` row per task (`TaskInterruptedByRestart`, result `Failure`, `user_id` = submitter, `task_queue_id` = the swept task), then returns `(task_queue_id, task_type, submitted_by_user_id)` rows. The Worker logs a warning with the swept ids.
- Nothing restarts the task. A user must open Task Manager and click Restart (`dbo.task_queue_restart_ins`, CAS-016, Model B: inserts a new `Queued` row; after CAS-054 it also carries the watermark and matches over).
- `result_acknowledged_at` is NULL for the swept row, so it counts toward the Task Manager "unviewed result" badge (CAS-030).

---

## §2 Desired Behavior

**Owner decision (2026-10-06):** "no user intervention required — as soon as the service comes back online it should continue from where it left off."

1. In the **same transaction** as the sweep's `Running → Failed` flip, for each swept row that is eligible, the sweep also creates the **resumed task**: a new `Queued` row (`task_type`, `request_parameters`, `submitted_by_user_id` copied; `restarted_from_task_queue_id` = the swept row), carrying the swept row's `resume_watermark_file_index_id` and its `search_result` rows with `file_index_id <= W` (CAS-054 mechanics). The worker's normal claim loop then picks it up and CAS-054 resumes it. No user action.
2. **Eligible** = `task_type = 'KeywordSearch'`, `resume_watermark_file_index_id IS NOT NULL`, last progress (`progress_updated_at`) within `Worker:ResumeMaxAgeHours` (default 24, CAS-054), and the lineage's `auto_resume_count < Worker:AutoResumeMaxAttempts` (default **3**) — the **loop guard** (§2.4).
3. The swept (original) row still ends `Failed` (it was interrupted) so history is truthful, but its `FailureReason` reads "Interrupted by a service restart - automatically resumed as task <N>." instead of "…please restart or resubmit…", and — because the failure is superseded by the resumed task — `result_acknowledged_at` is set so it does not raise a failure badge. The resumed task appears in Task Manager under the same user.
4. **Loop guard.** New `task_queue.auto_resume_count INT NOT NULL DEFAULT 0`. An auto-resumed row gets `original.auto_resume_count + 1`; a **manual** restart (CAS-016/054) resets the count to 0 (a human intervened). If the swept row's count is already `>= @auto_resume_max_attempts`, it is **not** auto-resumed: it stays `Failed` with the original "please restart" reason (a human must decide), and a distinct audit row `TaskAutoResumeSuppressed` records why. This stops a file that crashes the service every time from cycling forever.
5. Audit (FR-23), in the same transaction as the flip: the existing `TaskInterruptedByRestart` row (unchanged), plus one `TaskAutoResumed` row per resumed task (`user_id` = submitter, `tab_module` = the task's module, `task_queue_id` = the **new** task, `result` Success, `ResultDetail` = `"Automatically resumed from task <original> at file_index_id <W>; <M> match result(s) carried over (auto-resume <k> of <max>)."`) — no file names, paths, terms or content.
6. Non-eligible swept rows (other task types, no watermark, older than the limit, over the attempt cap) behave **exactly as today**.
7. The Worker logs one warning per auto-resumed task id (and per suppressed one); no other log volume added.

Out of scope: auto-resume of `ContentScan` / `Sync*` tasks; resuming anything other than via the sweep (no mid-run recovery); changes to Web UI (the existing task list shows the new task).

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** Owner request 2026-10-06 (after the 2026-10-06 crash of task 100031): interrupted searches must continue automatically when the service returns. This replaces the user-initiated-restart model for Keyword Search interruptions and is a change to the CAS-016 / CAS-028 / BRD §12.3 item 12 behavior — see §14 Q1.
- **Risk if not changed:** an overnight/unattended interruption leaves the search stalled until someone notices and clicks Restart.

---

## §4 Blast Radius (Graph Impact)

### Primary Change Target
File: `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_sweep_interrupted.sql`
Entity: Startup sweep (CAS-028 §3.4, transition T5) — now also creates the resumed task.
Current edges: manual read — called only by `TaskQueueRepository.SweepInterruptedAsync` ← `Worker.RunStartupSweepAsync`.

### Direct Dependents
- `Worker.cs` (`RunStartupSweepAsync`, `InterruptedByRestartReason`) — passes the new parameters; logs resumed/suppressed tasks.
- `ITaskQueueRepository.SweepInterruptedAsync` / `TaskQueueRepository` — new parameters; `SweptTaskRow` gains `ResumedAsTaskQueueId`, `AutoResumeSuppressed`, `CarriedOverCount`.
- `dbo.task_queue_restart_ins` (CAS-054) — resets `auto_resume_count` to 0; carry-over logic is extracted into a shared helper `dbo.task_queue_resume_carry_over_ins` used by both (second use → extraction justified, P4).
- Task Manager list / badge (reads `task_queue`, `result_acknowledged_at`) — behavior changes only through data (new task row; acknowledged original).
- Tests: `TaskQueueSweepTests`, `WorkerTests`, `TaskQueueRestartRepositoryTests`.

### Downstream Modules
Task Manager, Keyword Search, Audit Log.

### Cross-Project Risk
**CROSS-PROJECT RISK:** `Cachet.ArchiveSync.Core` contract change (`ITaskQueueRepository.SweepInterruptedAsync`, `SweptTaskRow`, `AuditAction`) shared by Api and Worker. Api's `TaskQueueRepository` implements the interface; Web is unaffected.

### God Node Involvement
`dbo.task_queue` — central table (**HIGH RISK**): one additive `NOT NULL DEFAULT 0` column only; no change to existing columns or constraints. `dbo.task_queue_sweep_interrupted` is a single-purpose SP (low fan-in) but **safety-critical**: it runs on every start and must remain atomic and must never prevent the service starting — see §2.1 and V5.

### Decomposition Check (P9)
Single spec after the CAS-054 split: files ≈ 11 (< 15), SCs = 9 (< 12), DB objects = 1 column + 1 new SP + 2 modified SPs. No further split.

### Graphify Queries Run
"graphify not yet call-aware for these objects — analysis done via manual code read" (see CAS-054 §4).

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `Database/migrations/20261006_170000__CAS-055-keyword-search-auto-resume.sql` | add | Column + helper SP + modified sweep and restart SPs (idempotent) |
| `Database/CachetArchiveSync/Entities/dbo.task_queue.sql` | modify | Mirror: `auto_resume_count INT NOT NULL DEFAULT 0` |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_resume_carry_over_ins.sql` | add | Shared helper (copy `search_result` ≤ W to the new task; returns count) |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_sweep_interrupted.sql` | modify | Auto-resume + loop guard + audit, in the existing transaction |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_restart_ins.sql` | modify | Use the helper; set `auto_resume_count = 0` |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs` | modify | `SweepInterruptedAsync` gains `resumeMaxAgeHours`, `autoResumeMaxAttempts` |
| `src/Cachet.ArchiveSync.Core/Dtos/SweptTaskRow.cs` | modify | Add resume outcome fields |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | modify | Append `TaskAutoResumed`, `TaskAutoResumeSuppressed` (existing members not reordered) |
| `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs` | modify | Pass new sweep parameters; map new result columns |
| `src/Cachet.ArchiveSync.Worker/Worker.cs` | modify | Pass config values; log resumed/suppressed |
| `src/Cachet.ArchiveSync.Worker/WorkerOptions.cs` | modify | `ResumeMaxAgeHours` (1–720, default 24), `AutoResumeMaxAttempts` (0–10, default 3; 0 disables auto-resume) |
| `src/Cachet.ArchiveSync.Worker/appsettings.json` | modify | Add the two settings (non-secret) |
| Tests (§13) | add/modify | |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `KeywordSearchTaskProcessor.cs`, `ResumeWatermarkTracker.cs` | Resume itself is CAS-054; this spec only creates the resumed row |
| `dbo.search_result*.sql` | Unchanged (carry-over copies only rows ≤ W) |
| `dbo.task_queue_claim.sql`, `dbo.task_queue_progress_upd.sql` | Claim already returns the watermark after CAS-054 |
| `TaskManagerService.cs`, `TaskManagerController.cs`, Web | Manual restart path unchanged apart from the SP-level count reset |
| `Extraction/**`, B2 client, other processors | Unrelated |

---

## §7 Database Changes

### Changed Objects
| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| `task_queue` | dbo | column added | `auto_resume_count INT NOT NULL CONSTRAINT DF_task_queue_auto_resume_count DEFAULT (0)` |
| `task_queue_resume_carry_over_ins` | dbo | new SP | `@source_task_queue_id, @new_task_queue_id, @watermark, @created_by, @updated_by, @carried_over_count OUTPUT` — `INSERT … SELECT` of the source's `search_result` rows with `file_index_id <= @watermark` under the new task id (shared by sweep and manual restart) |
| `task_queue_sweep_interrupted` | dbo | modified | New optional params `@resume_max_age_hours INT = NULL`, `@auto_resume_max_attempts INT = NULL` (**NULL = no auto-resume — today's behavior, so old callers/tests are unaffected**). After the flip, for each eligible swept row: insert the resumed `Queued` row (`auto_resume_count = old + 1`), call the helper, write `TaskAutoResumed`; set the original's `FailureReason` text and `result_acknowledged_at`. For rows at/over the attempt cap with a watermark: write `TaskAutoResumeSuppressed`. Whole thing stays inside the existing `TRY/TRANSACTION`. Result set adds `resumed_as_task_queue_id INT NULL`, `auto_resume_suppressed BIT`, `carried_over_count INT NULL`. |
| `task_queue_restart_ins` | dbo | modified | Calls the helper (replacing CAS-054's inline copy); new row `auto_resume_count = 0` |

### Migration Required
**Yes** — `Database/migrations/20261006_170000__CAS-055-keyword-search-auto-resume.sql` (idempotent: `COL_LENGTH` guard + `CREATE OR ALTER`; commented manual rollback) **plus** the four mirror files in §5. Requires the CAS-054 migration (`resume_watermark_file_index_id`) to have run first.

---

## §8 API Contract Changes

N/A — no endpoint or DTO exposed to the Web changes. (Task Manager's existing `GET /tasks` lists the new row.)

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | Sweep takes `@updated_by`, `@failure_reason` | New params optional; `@resume_max_age_hours` 1–720 and `@auto_resume_max_attempts` 0–10 when supplied, else RAISERROR before the transaction | `Test_V1_SweepValidatesNewParameters` |
| V2 | Sweep only flips + audits | Resumed row is created **only** for `KeywordSearch` rows with a watermark, within the age limit, under the attempt cap | `Test_V2_OnlyEligibleRowsAreAutoResumed` |
| V3 | n/a | `auto_resume_count` increments on auto-resume, resets to 0 on manual restart | `Test_V3_AutoResumeCountIncrementsAndManualRestartResets` |
| V4 | n/a | Row at/over the cap is not resumed; stays Failed with original reason; `TaskAutoResumeSuppressed` audited | `Test_V4_AttemptCapSuppressesAutoResume` |
| V5 | n/a | Any failure inside the auto-resume part rolls back the **whole** sweep transaction and the Worker's existing retry loop (§3.4 V3) retries it; the service never claims tasks until the sweep succeeds (unchanged) | `Test_V5_AutoResumeFailureRollsBackSweepAndIsRetried` |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Kill the Worker mid-search; on restart the sweep marks the task `Failed` **and** a new `Queued` task exists for the same user, with the same request, the original's watermark and its matches ≤ W | `Test_SC1_SweepCreatesResumedTaskWithWatermarkAndMatches` |
| SC-2 | The claim loop then runs the resumed task to `Completed` with no user action; progress continues from the skipped count (CAS-054) | `Test_SC2_ResumedTaskIsClaimedAndCompletedUnattended` |
| SC-3 | The resumed task's final results equal an uninterrupted run (static index) | `Test_SC3_AutoResumedRunMatchesUninterruptedRun` |
| SC-4 | After the third consecutive interruption of the same lineage, the next sweep does **not** create a task, keeps the "please restart" reason, and audits `TaskAutoResumeSuppressed` | `Test_SC4_ThirdConsecutiveInterruptionIsSuppressed` |
| SC-5 | A manual restart of that suppressed task works (resumes per CAS-054) and resets the count so auto-resume is available again | `Test_SC5_ManualRestartAfterSuppressionResetsCount` |
| SC-6 | Swept rows that are not Keyword, have no watermark, or whose progress is older than the limit are failed exactly as before (no new row, original reason, no extra audit) | `Test_SC6_IneligibleRowsBehaveAsBefore` |
| SC-7 | Audit: one `TaskInterruptedByRestart` + one `TaskAutoResumed` (referencing the new task, with watermark/count/attempt detail, no names or terms) per resumed task, committed with the status change | `Test_SC7_AutoResumeAuditCompleteAndPrivacySafe` |
| SC-8 | The original's `result_detail` names the resumed task and `result_acknowledged_at` is set (no failure badge for a superseded failure); the resumed task appears in `GET /tasks` | `Test_SC8_OriginalMarkedSupersededAndResumedTaskListed` |
| SC-9 | `Worker:AutoResumeMaxAttempts = 0` disables auto-resume entirely (today's behavior) | `Test_SC9_ZeroAttemptsDisablesAutoResume` |

---

## §11 Security Notes

- **FR-23:** the status change and *both* audit rows commit atomically; the new automatic action is audited under the **submitting user's** id with a distinct action (`TaskAutoResumed`), so an auditor can tell system-initiated resumption from a user restart (`TaskRestart`). No file names, paths, terms or content in any audit detail.
- **Authority:** the system acts on a task the user already submitted, with the user's own request parameters and only their own results; no cross-user data movement, no privilege change. The single-instance lock still gates the sweep (only the lock holder sweeps/resumes, CAS-028 V7).
- **Availability/DoS guard:** the attempt cap (default 3) bounds automatic retries so a poisonous input cannot create an unbounded crash→resume loop or flood the queue/audit log.
- **Integrity:** resumed results mix pre- and post-interruption work; the audit rows and summary label (`ResumedFromTaskQueueId`, CAS-054) record that. Files are static (owner, 2026-10-06) and the 24 h age limit bounds staleness.
- **Compliance note:** this changes the documented restart model (user-initiated) — see §14 Q1; the BRD must be amended by the client before this is described to auditors as the system's behavior.

---

## §12 Rollback Plan

1. Set `Worker:AutoResumeMaxAttempts = 0` (no redeploy of SQL needed) → auto-resume off, sweep behaves as today. (Config only — immediate.)
2. Revert Worker/Api builds; the sweep SP's new parameters are optional (NULL = no auto-resume), so an old Worker works against the new SP.
3. Database: the column and helper SP may stay (additive/uncalled). Full revert = re-run the prior `task_queue_sweep_interrupted` (CAS-028) and `task_queue_restart_ins` (CAS-054) bodies; optional `DROP PROCEDURE dbo.task_queue_resume_carry_over_ins` and column drop (commented in the migration).
4. **Task-queue rollback / in-flight rows:** the migration touches no existing row's status or data (new column defaults to 0). Already-created resumed tasks are ordinary `Queued`/`Running`/`Completed` rows and remain valid after a rollback; they are not dropped or altered.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `TaskQueueSweepTests` (IntegrationTests, extend) | `Test_V1_…`, `Test_V2_…`, `Test_V3_…`, `Test_V4_…`, `Test_V5_…`, `Test_SC1_…`, `Test_SC4_…`, `Test_SC6_…`, `Test_SC7_…`, `Test_SC8_…`, `Test_SC9_…` | V1–V5, SC-1/4/6/7/8/9 |
| `TaskQueueRestartRepositoryTests` (extend) | `Test_SC5_…` | SC-5, V3 |
| `WorkerTests` (extend) | passes config to sweep; logs resumed/suppressed | §2.7 |
| `KeywordSearchResumeTests` (CAS-054, extend) | `Test_SC2_…`, `Test_SC3_…` | SC-2, SC-3 |

Coverage target: ≥ 85% on changed files.

---

## §14 Open Questions

Resolution status (2026-10-06): Q2–Q4 are **resolved as design defaults** (author's recommendation, adopted so implementation is unblocked; the owner can override any of them by editing this section — each is a config value or a one-line SP change). Q1 is **not** resolvable inside this repo and stays open as a documentation/compliance gate, not an implementation blocker.

- [ ] **Q1 — BRD / client sign-off (OPEN — external dependency, does not block implementation).** Automatic resume replaces the user-initiated restart model recorded in CAS-016 and BRD §12.3 item 12 ("user-initiated restart of a crashed/failed task from Task Manager"). The owner has decided it; the BRD is the client's document and lives outside this repo, so client acceptance is **unverified**.
  - **Decision for implementation:** ship with auto-resume enabled by default but behind `Worker:AutoResumeMaxAttempts` (0 = off, §12 step 1), so it can be disabled instantly if the client declines.
  - **Required follow-up (owner action):** raise a BRD amendment against §12.3 item 12 with the client. Until it is accepted, do not describe auto-resume to auditors as the system's approved behavior; describe it as "implemented, pending client acceptance". Audit rows (`TaskAutoResumed`) already distinguish system-initiated from user-initiated restarts, so the behavior is fully reconstructable either way.
- [x] **Q2 — Attempt cap. RESOLVED: 3.** Default `Worker:AutoResumeMaxAttempts = 3` consecutive automatic resumes per lineage, then human intervention (§2.4). Rationale: tolerates transient causes (deploy, host reboot, one bad restart) while bounding a crash loop to ≤ 3 cycles. Configurable 0–10 without redeploying SQL.
- [x] **Q3 — Superseded failure badge. RESOLVED: set `result_acknowledged_at` on the swept original (as written in §2.3).** Rationale: the failure is superseded by a live resumed task, so a failure badge would be noise and would train users to ignore the badge. Nothing is lost for audit purposes — the original row stays `Failed` with the `FailureReason` naming the resumed task, and `TaskInterruptedByRestart` + `TaskAutoResumed` are both in the audit log. The suppressed (cap-reached) case is deliberately **not** acknowledged, so a human-needed failure still raises the badge (§2.4).
- [x] **Q4 — Which interruptions auto-resume. RESOLVED: service-interruption sweeps only.** A task that ends `Failed` through its own error (e.g. B2 failure, validation, unhandled processor exception) is **not** auto-restarted; those need a human to look at the cause. Only rows swept from `Running` by the startup sweep are eligible. This is already the scope of §2/§6 and the "Out of scope" paragraph; no spec change beyond this confirmation.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude (draft) | 2026-10-06 | Q2–Q4 resolved as design defaults (§14); Q1 (client BRD amendment) open as a documentation gate, not an implementation blocker |
| Reviewer | | | |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches the code BEFORE the change
- [ ] Desired behavior (§2) fully implemented, including the loop guard (§2.4)
- [ ] Only files in §5 changed (§6 files untouched)
- [ ] All §10 SCs and §9 validations have passing tests
- [ ] Sweep remains atomic: a failure anywhere rolls back the whole transaction and the existing retry loop applies; the service never claims before the sweep succeeds
- [ ] `TaskAutoResumed` audit is atomic with the status change and carries no file names/paths/terms
- [ ] Old Worker + new SP and new Worker + old SP both work (optional parameters; NULL = today's behavior)
- [ ] §12 in-flight rows unaffected

### Verification Report Format
Reviewer writes `CAS-055.review.md`: Verdict, per-SC table, scope violations, unintended blast radius, security gaps.

## Implementation Log

**Completed:** 2026-10-06
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 88.1% | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20261006_170000__CAS-055-keyword-search-auto-resume.sql` | add |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_resume_carry_over_ins.sql` | add |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_sweep_interrupted.sql` | modify |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_restart_ins.sql` | modify |
| `Database/CachetArchiveSync/Entities/dbo.task_queue.sql` | modify |
| `Core/ITaskQueueRepository.cs, SweptTaskRow.cs, AuditAction.cs` | modify |
| `Api/TaskQueueRepository.cs` | modify |
| `Worker/Worker.cs, WorkerOptions.cs, appsettings.json` | modify |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `TaskQueueSweepTests` | `V1-V5, SC1/4/6/7/8/9` | skipped - need SQL Server |
| `TaskQueueRestartRepositoryTests` | `Test_SC5_*` | SC-5 (skipped - need SQL Server) |
| `KeywordSearchResumeTests` | `Test_SC2_*, Test_SC3_*` | SC-2, SC-3 |
| `WorkerTests` | `Test_CAS055_* x2` | 2.7 |

### Migration
`20261006_170000__CAS-055-keyword-search-auto-resume.sql`
