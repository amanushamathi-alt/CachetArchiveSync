---
id: CAS-037
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-28
priority: P1
related_specs: [CAS-001, CAS-003, CAS-007, CAS-011, CAS-012, CAS-028, CAS-029, CAS-034, CAS-035]
touches:
  database: true
  api: true
  ui: false
  worker: true
  b2: true
  config: true
  security: true
migrations:
  - 20260928_140000__CAS-037-b2-mirror-population.sql
---

# CAS-037 — B2 Mirror Download / Local Population Process

WBS Task 34 (B2 Integration phase). Implements BRD Section 3(c) ("The B2 mirror is populated
separately (downloaded from Backblaze into its own directory on the same server)") and feeds
`dbo.file_index` for `source = 'B2'` (FR-05), which Keyword Search (CAS-006/CAS-012), O-Drive Sync
and Content Scan (CAS-007), and the FR-11 SHA-1-changed indicator all depend on already having B2
mirror data present. Concretizes CAS-001 §6.2's design against the **as-built** schema (CAS-003,
corrected by CAS-011) rather than CAS-001 §5's provisional, never-built `B2FileId`/`LocalMirrorPath`
mirror-index fields — see §0.

---

## §0 Provenance / Scope Note

Analysis for this spec was done via a manual read of the BRD, WBS Task 34's own drafting prompt, the
`b2-integration`/`async-task-queue`/`clean-architecture` skills, and the actual current codebase —
**graphify has not been vendored/run against this repo's application code yet** (CLAUDE.md: "Status:
no initial graph built yet"). §17 below is a manual blast-radius read, not a graphify query result.

**Why this spec exists even though CAS-001 §6.2 already describes "the design":** CAS-001 (merged,
2026-09-22) is a design document, not an implementation spec, and its §5 mirror-index field list
(`BucketName`/`ObjectKey`/`B2FileId`/`ContentSha1`/`UploadTimestamp`/`Size`/`LocalMirrorPath`/
`LastVerifiedAt`) was explicitly marked **provisional**, pending the real DB schema spec. That real
schema (CAS-003, corrected by CAS-011) built `dbo.file_index` with `source`/`bucket_name`/
`relative_path`/`file_name`/`file_extension`/`file_size_bytes`/`modified_at`/`sha1_hash`/
`last_seen_at`/`is_active` — **no `B2FileId` column and no `LocalMirrorPath` column exist**. The
local mirror root is instead resolved per-bucket from CAS-011's `dbo.odrive_bucket_mapping` table
(`b2_mirror_root_path`), and the full local file path is `Path.Combine(b2_mirror_root_path,
relative_path)` (confirmed by direct read of `MirrorFileReader.cs` and `ODriveFileReader.cs`'s
identical approach). Grep confirms **no Worker task processor for B2 mirror population exists
today** (`ContentScanTaskProcessor`, `KeywordSearchTaskProcessor`, `SyncCopyTaskProcessor`,
`SyncUpdateTaskProcessor`, `SyncPurgeTaskProcessor` exist; no `MirrorSync`/`MirrorPopulation`
processor does), and `FileIndexRow.Sha1Hash`'s own XML doc says explicitly: "Populated by the
(out-of-scope, §4) mirror-population process — not computed fresh by the `ContentScan` comparison"
— i.e. CAS-007 itself already forward-references this exact spec as the thing that must exist before
`sha1_hash` is ever non-null for a `B2`-source row. This spec is that missing piece, written against
the real, as-built schema and Worker conventions rather than CAS-001's provisional field list.

---

## §1 Summary

Adds a new queued Worker task type, `B2MirrorSync`, that enumerates the configured Backblaze B2
bucket (`b2_list_file_names`), downloads any object that is new or whose `contentSha1` has changed
since the last sync into the server's local B2-mirror directory (resolved per-bucket from
`dbo.odrive_bucket_mapping.b2_mirror_root_path`, CAS-011), skips re-downloading unchanged objects,
and upserts one `dbo.file_index` row (`source = 'B2'`) per object — giving Keyword Search, Content
Scan, and O-Drive Sync a populated local B2-side index to compare and search against (BRD §3(c)).
Implements WBS Task 34; feeds FR-05 (file index), FR-11 (SHA-1-changed indicator).

---

## §2 User Story

As the **background Worker**, I want to periodically refresh a local, on-disk copy of the B2 archive
and its metadata index, downloading only what has changed since the last run, so that Keyword
Search's filename/content search and O-Drive Sync's comparison always have an up-to-date local B2
mirror to work against without re-downloading the entire ~500GB archive on every pass.

---

## §3 In-Scope

- A new `dbo.task_queue.task_type` value, `B2MirrorSync`, and the schema change required to allow it
  (`CK_task_queue_task_type`, §5).
- `B2MirrorSyncTaskProcessor` (Worker): enumerates the single B2 bucket the system-key `IB2Client` is
  configured against (`B2:BucketName`), via paginated `b2_list_file_names` (§6.1).
- Skip-unchanged logic: compare each B2 object's `contentSha1` against the corresponding
  `dbo.file_index.sha1_hash` row (matched by `bucket_name` + `relative_path`) before deciding whether
  to download (§6.2).
- Downloading new/changed objects via `b2_download_file_by_id` into the local B2-mirror directory,
  preserving the bucket's path structure (§6.3).
- Upserting `dbo.file_index` rows (`source = 'B2'`) for every enumerated, non-hidden object — insert
  for a new object, update for a changed one, `last_seen_at`-only touch for an unchanged one (§6.4).
- Progress reporting (CAS-029) over a run that can take 10 minutes to a full day (FR-22a).
- One new submission endpoint, `POST /sync/mirror-sync`, so the task can actually be enqueued
  (§6, §9's authorization decision is flagged in §15 for owner confirmation).
- A concurrency guard preventing two `B2MirrorSync` tasks from running at once against the same local
  directory and `dbo.file_index` rows (§7 V1 — an added robustness measure, not BRD-stated; see §15).

---

## §4 Out-of-Scope

- **Multi-bucket mirror sync.** `IB2Client` (CAS-001 §6.1, as built in `B2Client.cs`) is constructed
  once per process against a single `B2:SystemKeyId`/`B2:SystemApplicationKey`/`B2:BucketName` and
  resolves exactly one `bucketId`, cached for the process lifetime — it has no notion of "the current
  bucket" as a per-call parameter. `dbo.odrive_bucket_mapping` (CAS-011) is schematically ready for
  multiple buckets, but this spec syncs only the single bucket `IB2Client` is configured against.
  Making `IB2Client` multi-bucket-aware is a larger, separate change, not invented here (Karpathy P4).
- **"Removed from B2" detection / soft-delete.** CAS-001 §6.2 step 6 explicitly says mirror
  population "must not silently delete the local mirror copy of an object it no longer sees listed."
  This spec follows that: a `dbo.file_index` row for an object no longer returned by enumeration is
  left untouched by this process. The existing self-healing mechanism
  (`IFileIndexRepository.SoftDeleteAsync`, CAS-001 §6.3/CAS-035) already handles this at cross-check
  time, per-candidate, when a stale local row is actually surfaced to a user. This spec does not
  duplicate that.
- **Copy / Update / Purge sync actions** (FR-13/FR-13a/FR-13b, CAS-007) and **Permanent Delete**
  (FR-16/FR-17, CAS-001 §6.4) — both reuse `IB2Client` primitives but are separately-scoped, already
  built (CAS-007, CAS-014, CAS-018) task types.
- **O-Drive physical ingestion.** BRD §3(c)/(d): the O-Drive-side local cache is populated by a
  TraceCounts staff member physically copying Cachet's on-site O-Drive via an external drive — an
  entirely separate, manual process this spec does not touch. The still-open **O-Drive
  physical-refresh cadence** item (BRD §12.4 item 1) is about *that* process's schedule, not this
  one — B2 mirror download is triggered independently (§6, §15), so this spec does **not** depend on
  that open BRD item.
- **Scheduled/automatic triggering.** No cron or timer-based scheduling infrastructure exists in this
  repo (the Worker is a pull-based claim loop over `dbo.task_queue`, per CAS-028) and the BRD/WBS do
  not ask for one here. This spec adds an on-demand submission endpoint only; whether B2 mirror sync
  should eventually run on a fixed schedule is flagged as an open question (§15), not decided here.
- **Web UI.** No wireframe screen owns a "B2 Mirror" tab (the seven documented screens are Sign-In,
  Keyword Search, O-Drive Sync and Content Scan, Quarantine Queue, Task Manager, Audit Log, Reports).
  This spec's one new endpoint is invoked directly (e.g. by TraceCounts staff or a future admin
  screen), not from a new UI surface; it does appear generically in the existing Task Manager list
  (no Task Manager code change needed — confirmed by reading `TaskManagerService.cs`, which
  special-cases only `KeywordSearch` and is otherwise task-type-agnostic).
- **Drive encryption-at-rest** — confirmed **not required** (BRD §12.3 item 6); not addressed here.

---

## §5 Database Objects

### Tables / Columns

No new tables. One existing constraint must change:

`dbo.task_queue.CK_task_queue_task_type` currently allows only
`('KeywordSearch','ContentScan','SyncCopy','SyncUpdate','SyncPurge')` (confirmed by direct read of
`Database/CachetArchiveSync/Entities/dbo.task_queue.sql:38`). It must be widened to include
`'B2MirrorSync'`:

```sql
ALTER TABLE dbo.task_queue DROP CONSTRAINT CK_task_queue_task_type;
ALTER TABLE dbo.task_queue ADD CONSTRAINT CK_task_queue_task_type
    CHECK (task_type IN ('KeywordSearch','ContentScan','SyncCopy','SyncUpdate','SyncPurge','B2MirrorSync'));
```

No change to `dbo.file_index` or `dbo.odrive_bucket_mapping` — both are already shaped correctly for
this process (CAS-003, CAS-011).

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `dbo.task_queue_active_by_type_get` | dbo | get | **New.** `@task_type NVARCHAR(20)` → single `BIT` column `is_active_task`, true if any row with that `task_type` is currently `Queued` or `Running`. Backs the §7 V1 concurrency guard. Deliberately not a reuse of `dbo.task_queue_lst` (which is scoped to `submitted_by_user_id` — this check must be global, across all users). |

No changes to `dbo.file_index_ins`, `dbo.file_index_upd`, or `dbo.file_index_lst` — this spec calls
all three exactly as they exist today (confirmed against `IFileIndexRepository`'s current signatures).

### Migration Strategy

Hybrid model — db-author writes both at `/implement` time (this document does not author SQL):
- `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-037-b2-mirror-population.sql` (append-only; drops and
  re-adds `CK_task_queue_task_type`, creates `dbo.task_queue_active_by_type_get`).
- Mirror updates: `Database/CachetArchiveSync/Entities/dbo.task_queue.sql` (constraint text updated
  in place — declarative, current-state file per this project's convention), plus new
  `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_active_by_type_get.sql`.

---

## §6 API Endpoints

### Endpoint: `POST /sync/mirror-sync`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]` — **not** `CachetFullAccess`. Rationale
(consistent with CAS-001 §12's own capability-tier mapping, itself flagged there as "this document's
proposal — not stated in the BRD"): `Full Access` is reserved for B2-**write** actions (Copy, Update,
Purge, Permanent Delete). Mirror sync only reads from B2 (`b2_list_file_names`,
`b2_download_file_by_id`) — the same tier `POST /sync/scan` (Content Scan) already uses. This mapping
is this spec's own design decision, not a stated BRD requirement — flagged for owner confirmation in
§15, same as CAS-001's original mapping was.

**Async:** Yes — enqueues a `B2MirrorSync` task and returns immediately (FR-18/FR-22a); a full-bucket
enumerate/download can take 10 minutes to a full day.

**Request:** No body — the sync scope is always "the whole configured B2 bucket" (same
no-request-body pattern as `POST /sync/scan`, per `SyncScanService.SubmitAsync`).

**Response 202:**
```json
{
  "taskId": 100123,
  "type": "B2MirrorSync",
  "status": "Queued",
  "submittedAt": "2026-09-28T14:00:00Z"
}
```
Reuses `TaskSubmittedResponse` (`Cachet.ArchiveSync.Core/Dtos/TaskSubmittedResponse.cs`) — already a
generic `{taskId, type, status, submittedAt}` shape shared by CAS-007's four sync endpoints; adding a
fifth caller here does not violate the "don't reuse across modules" note in that DTO's own XML doc,
since this is the same conceptual family of "queue submission acknowledgement" responses within the
same controller.

**Status codes:**
| Code | Condition |
|------|-----------|
| 202 | Accepted — task queued |
| 401 | Missing or invalid session |
| 409 | A `B2MirrorSync` task is already `Queued` or `Running` (§7 V1) |

Added to `SyncController` (`src/Cachet.ArchiveSync.Api/Controllers/SyncController.cs`) alongside the
existing `/sync/scan`/`/sync/actions/*` routes — no new controller, per Karpathy P4 (this is the same
"submit a queued B2/mirror-touching task" pattern that controller already implements four times).

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | At most one `B2MirrorSync` task may be `Queued` or `Running` at a time (added robustness — concurrent runs would race on the same local directory writes and `dbo.file_index` rows for the same bucket) | 409, no task inserted | `Test_V1_RejectsConcurrentMirrorSyncSubmission` |
| V2 | An enumerated B2 object with `action != "upload"` (i.e. `"hide"`, `"folder"`, `"start"`) is never downloaded or written to `dbo.file_index` — only real, complete files are mirrored | Object silently skipped, counted in neither `NewCount` nor `UpdatedCount` | `Test_V2_NonUploadActionObjectsSkipped` |
| V3 | An object whose `dbo.file_index.sha1_hash` already equals its current B2 `contentSha1` is never re-downloaded — only `last_seen_at` is touched | No `b2_download_file_by_id` call for that object | `Test_V3_UnchangedObjectSkipsDownload` |
| V4 | A single object's download/write/upsert failure does not abort the run — the task continues to the next object and the failure is counted, not thrown | Task still reaches `Completed` (with a non-zero `FailedCount`) unless every object fails | `Test_V4_PerFileFailureDoesNotAbortRun` |
| V5 | A missing/inactive `dbo.odrive_bucket_mapping` row for the configured bucket fails the whole task outright (there is no local root to write to) | Task transitions to `Failed` with a clear, non-path-leaking `FailureReason` | `Test_V5_MissingMirrorRootFailsTask` |

---

## §8 UI / UX

N/A — no new screen. The submitted task appears in the existing Task Manager list/detail views
(CAS-026/CAS-027) with `type = "B2MirrorSync"` and no code change required there (§4).

---

## §9 Configuration Changes

| Key | Default | Description | Env Override |
|-----|---------|-------------|--------------|
| `B2:MirrorSyncPageSize` | `1000` | `maxFileCount` per `b2_list_file_names` call during enumeration (§6.1). Capped at 1000 by the B2 API itself (`IB2Client.ListFileNamesAsync` already throws `ArgumentOutOfRangeException` above that — confirmed in `B2Client.cs`). Optional, same non-required-config pattern as `B2:CrossCheckMaxConcurrency`. | Yes |

No new secret-holding keys — this process reuses the existing `B2:SystemKeyId`/
`B2:SystemApplicationKey`/`B2:BucketName` (CAS-001 §9) and `dbo.odrive_bucket_mapping.b2_mirror_root_path`
(CAS-011 §7) for everything credential- and path-related. The now-superseded `B2:MirrorRootPath` key
CAS-001 §9 originally deferred is **not** revived — CAS-011 already replaced it with the per-bucket
DB-table value.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A full run against a bucket with N new objects inserts exactly N new `dbo.file_index` rows (`source = 'B2'`), each with the correct `bucket_name`/`relative_path`/`file_name`/`file_extension`/`file_size_bytes`/`modified_at`/`sha1_hash`/`last_seen_at` | `Test_SC1_NewObjectsInsertedCorrectly` |
| SC-2 | Re-running the sync with no B2-side changes downloads zero files and updates only `last_seen_at` on every existing row | `Test_SC2_UnchangedRerunSkipsAllDownloads` |
| SC-3 | An object whose B2 `contentSha1` changed since the last sync is re-downloaded and its `dbo.file_index` row's `sha1_hash`/`file_size_bytes`/`modified_at`/`last_seen_at` are all updated | `Test_SC3_ChangedObjectRedownloadedAndUpdated` |
| SC-4 | Downloaded file bytes are written under `b2_mirror_root_path` at exactly `relative_path`, matching `Path.Combine(root, relativePath)` — the same path convention `MirrorFileReader`/`ODriveFileReader` already read from | `Test_SC4_DownloadedFileWrittenAtExpectedPath` |
| SC-5 | The Task Manager shows measurable, monotonically increasing progress (`items_processed`/`items_total`) throughout a run, never a stuck "Running" with no detail (FR-22a) | `Test_SC5_ProgressReportedThroughoutRun` |
| SC-6 | A second `POST /sync/mirror-sync` while one is already `Queued`/`Running` is rejected with 409 and inserts no task row | `Test_SC6_ConcurrentSubmissionRejected` |
| SC-7 | No B2 application key value (system key or its ID) ever appears in a log line, HTTP response, or `dbo.audit_log` row for this flow | `Test_SC7_NoKeyLeakage` |

---

## §11 Telemetry

```csharp
// _logger.LogInformation("Task {TaskId} (B2MirrorSync) started: {ItemsTotal} object(s) enumerated", taskId, itemsTotal);
// _logger.LogWarning(ex, "B2MirrorSync task {TaskId}: object {RelativePath} failed; continuing", taskId, relativePath);
// _logger.LogInformation("B2MirrorSync task {TaskId} completed: {NewCount} new, {UpdatedCount} updated, {SkippedCount} unchanged, {FailedCount} failed", ...);
```

- Never log: the B2 application key or any part of it, a session token/cookie value, full file
  content. Per-object log lines may include the object's `relative_path` (already how
  `SyncCopyTaskProcessor`/`ContentScanTaskProcessor` log today — file paths are not treated as
  secret in this codebase's existing telemetry, only credentials and tokens are).
- Do log: enumeration page counts, per-object skip/download/failure outcome (at `LogDebug`/`LogWarning`
  as appropriate — not one `LogInformation` line per object at full-bucket scale), and the final
  summary counts.

---

## §12 Security Notes

### B2 Credential Handling
This process calls `IB2Client` (the existing singleton, system-key-authenticated client, CAS-001
§6.1) exactly as `SyncCopyTaskProcessor`/`ContentScanTaskProcessor` already do — no new credential
path is introduced. The B2 key is never read, logged, or referenced by this spec's new code; it stays
entirely inside `B2Client.cs`, unchanged by this spec.

### Audit Trail
- **At submission (Api):** one `AuditAction.B2MirrorSyncSubmitted` row (user, `TabModule = "O-Drive
  Sync"` — same tab-module convention `SyncScanService` uses for `ContentScanSubmitted`, since no
  dedicated "B2 Mirror" tab exists) written **before** the 202 response, matching this project's
  audit-before-response rule (FR-23, CLAUDE.md Rules).
- **At completion (Worker):** one `AuditAction.B2MirrorSyncCompleted` row summarizing
  new/updated/skipped/failed counts, written before the task transitions to `Completed` — matching
  the "audit before terminal-state visibility" pattern `SyncCopyTaskProcessor`/`Worker.cs`'s dispatch
  boundary already use.
- **No per-object audit row.** At full-bucket scale (thousands of objects, CAS-001 §6.3), an
  audit-log row per downloaded file would flood `dbo.audit_log` for a background refresh operation
  that (unlike Quarantine/Delete/Copy/Update/Purge) is not itself a user-selected, per-file action.
  This mirrors `ContentScanTaskProcessor`'s own precedent — it writes zero per-item audit rows for its
  (also potentially large) comparison pass, only a submission-time row via `SyncScanService`.
- A total task-level failure (e.g. missing mirror-root mapping, V5) does **not** get its own audit
  row beyond the existing dispatch-boundary/outer-catch handling `Worker.cs`/other processors already
  provide — consistent with `SyncCopyTaskProcessor`'s outer catch, which sets `Failed` without a
  dedicated audit write of its own.

### Session / Auth Requirements
`POST /sync/mirror-sync` requires only `CachetAuthenticated` (§6) — this spec's own proposed mapping,
flagged for owner confirmation (§15), not a BRD-stated requirement.

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `B2MirrorSyncTaskProcessorTests` | `Test_SC1_NewObjectsInsertedCorrectly` | SC-1 |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC2_UnchangedRerunSkipsAllDownloads` | SC-2, V3 |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC3_ChangedObjectRedownloadedAndUpdated` | SC-3 |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC4_DownloadedFileWrittenAtExpectedPath` | SC-4 |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC5_ProgressReportedThroughoutRun` | SC-5 |
| `B2MirrorSyncTaskProcessorTests` | `Test_V2_NonUploadActionObjectsSkipped` | V2 |
| `B2MirrorSyncTaskProcessorTests` | `Test_V4_PerFileFailureDoesNotAbortRun` | V4 |
| `B2MirrorSyncTaskProcessorTests` | `Test_V5_MissingMirrorRootFailsTask` | V5 |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC7_NoKeyLeakage` | SC-7 |
| `B2MirrorSyncServiceTests` | `Test_V1_RejectsConcurrentMirrorSyncSubmission` | V1 |
| `B2MirrorSyncServiceTests` | `Test_SC6_ConcurrentSubmissionRejected` | SC-6 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `SyncControllerIntegrationTests` (extend existing) | `Test_PostMirrorSync_ReturnsAcceptedAndEnqueuesTask` | §6 endpoint contract |
| `TaskQueueRepositoryTests` (extend existing) | `Test_TaskQueueActiveByTypeGet_ReflectsQueuedAndRunning` | §5 new stored procedure |

### Coverage Target
Changed files must reach ≥ 85% line coverage (project standard).

---

## §14 Rollout

1. Database migration first (§5): widen `CK_task_queue_task_type`, add
   `dbo.task_queue_active_by_type_get`.
2. Deploy Api (new endpoint, `IB2MirrorSyncService`) and Worker (new
   `B2MirrorSyncTaskProcessor`, registered as an additional `IWorkerTaskProcessor` in
   `Worker/Program.cs` alongside the existing five) together — a `B2MirrorSync` row claimed by a
   Worker that predates this deploy would hit `Worker.cs`'s existing "no processor registered"
   dispatch-boundary path (`UnsupportedTaskTypeReason`, already-built fail-safe, CAS-028 §3.5) rather
   than crash, but should still be avoided by deploying both together.
3. First-ever run against a freshly created bucket mapping row (CAS-011) — if
   `dbo.odrive_bucket_mapping.b2_mirror_root_path` is not yet configured/active for the target
   bucket, V5 fails the task cleanly (§7); the mapping row must exist before the first mirror sync is
   submitted.
4. Rollback: revert the Api/Worker deploy; the `CK_task_queue_task_type` constraint widening and
   `dbo.task_queue_active_by_type_get` addition are additive and harmless to leave in place even if
   the code is rolled back (no existing row ever has `task_type = 'B2MirrorSync'` before this
   deploys, so a schema-only rollback is unnecessary).

---

## §15 Open Questions

- [x] **Submission authorization tier — RESOLVED 2026-09-28.** `CachetAuthenticated` confirmed (not
      `CachetFullAccess`), mirroring Content Scan's read-only tier — mirror sync only reads from B2,
      consistent with CAS-001 §12's Read-Only-sufficient mapping. §6/§12 stand as written.
- [x] **Scheduling — RESOLVED 2026-09-28.** On-demand only for this spec: `POST /sync/mirror-sync` is
      the sole trigger; no cron/timer infrastructure is added. A fixed schedule (if ever wanted) is
      explicitly deferred to a separate future spec, not built here. This is unrelated to, and does
      not depend on, the separately-open O-Drive physical-refresh cadence item (BRD §12.4 item 1 —
      see §4).
  - Not depending on either open BRD item (Web Caching Layer mechanism, §12.4; O-Drive
        physical-refresh cadence, §12.4 item 1) — noted for completeness per the template's
        instruction, not because this spec needs them resolved.
- [x] **Concurrency guard scope (V1) — RESOLVED 2026-09-28.** Global guard confirmed, as originally
      specced: at most one `B2MirrorSync` task `Queued`/`Running` at a time, regardless of bucket.
      Simplest given there is currently exactly one B2 bucket in play. If multi-bucket support (§4,
      out of scope) is ever added, this guard would need to become per-bucket, not global — flagged
      here rather than silently assumed to still be correct then.

**All §15 items resolved as of 2026-09-28.** No remaining blocker before `/implement CAS-037` proceeds.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code, via harikesh@scientificvision.net | 2026-09-28 | Drafted per WBS Task 34 and BRD §3(c); status intentionally left `draft` pending §15 owner confirmation |
| Owner | harikesh@scientificvision.net | 2026-09-28 | Confirmed all 3 §15 open questions: `CachetAuthenticated` tier, on-demand-only triggering (no scheduling infra), global (not per-bucket) concurrency guard — all three as originally proposed |
| Reviewer | | | Pending |

---

## §17 Graph Impact

No graphify graph exists for this repo's application code yet (`graphify-out/` covers the BRD/WBS/docs
corpus only — confirmed via `graphify-out/GRAPH_REPORT.md`). This section is a manual grep/read of the
current codebase instead, per CLAUDE.md's documented fallback.

### God Nodes in Blast Radius
`dbo.file_index` (CAS-003 §17: "every module in this application depends on at least one of these
four foundational tables") — this spec is a **new writer** into it (alongside `SyncCopyTaskProcessor`
and, indirectly, whatever originally seeded `source = 'ODrive'` rows), not a schema change to it.
`dbo.task_queue` — schema change (§5) is narrow (one CHECK constraint value added), consistent with
five prior additions to the same constraint's value list.

### Communities / Modules Affected
- **B2 Integration** (primary) — new `IB2Client.ListFileNamesAsync`/`DownloadFileByIdAsync` caller.
- **Keyword Search, O-Drive Sync and Content Scan** — downstream consumers of the `dbo.file_index`
  rows this process populates; no code change to either, but both were previously unable to return
  any real `source = 'B2'` results/comparisons without this process having run at least once.
- **Task Manager** — a new `task_type` value flows through the existing generic list/detail/restart
  code paths with no change required (confirmed: `TaskManagerService.cs` special-cases only
  `KeywordSearch`).
- Projects: `Api` (new `IB2MirrorSyncService`, one new `SyncController` action), `Worker` (new
  `B2MirrorSyncTaskProcessor`), `Core` (new `MirrorSyncResultSummaryDto`, two new `AuditAction`
  members). No `Web` (Blazor) change.

### Surprising Connections
- `FileIndexRow.Sha1Hash`'s existing XML doc comment already names this exact gap ("Populated by the
  (out-of-scope, §4) mirror-population process") — CAS-007 was written already anticipating this
  spec, which is a stronger-than-usual confirmation that this is genuinely the missing piece, not a
  speculative addition.
- CAS-001 §5's `B2FileId`/`LocalMirrorPath` mirror-index fields were never actually built into
  `dbo.file_index` (CAS-003/CAS-011 built a narrower, metadata-only shape instead) — any future
  reader of CAS-001 §6.2 in isolation would design against fields that don't exist. This spec is the
  documented correction for that specific subsection, the same way CAS-011 corrected CAS-003's
  `content_text` assumption.
- `dbo.task_queue.CK_task_queue_task_type` is a plain `CHECK` constraint enumerating five literal
  values today — every new task type (this is the sixth) requires a schema migration, not just an
  application-layer enum change (`AuditAction`'s append-only enum pattern does not apply here; this
  is a DB-level allow-list).

### Graphify Queries Run
None — "graphify not yet vendored — analysis done via manual grep/read of the codebase" (§0).

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test.
- [ ] All §7 Validations are implemented and tested.
- [ ] `POST /sync/mirror-sync` exists with the correct verb/route/auth policy (§6).
- [ ] §12 Security Notes implemented: no B2 key ever reaches a log line, response, or audit row;
      submission audit write happens before the 202 response; completion audit write happens before
      the task transitions to `Completed`.
- [ ] §5 database objects exist: `CK_task_queue_task_type` includes `'B2MirrorSync'`;
      `dbo.task_queue_active_by_type_get` exists and behaves as described.
- [ ] §9 config key (`B2:MirrorSyncPageSize`) is present with the documented default.
- [ ] §11 Telemetry: no B2 key/session token/PII in logs; no per-object `LogInformation` flood at
      full-bucket scale.
- [ ] §4 Out-of-Scope: confirm no multi-bucket support, no removed-from-B2 soft-delete logic, and no
      scheduling/cron infrastructure were added beyond what this spec describes.
- [ ] §17 Graph Impact matches actual code changes (no unintended nodes touched, e.g. no change to
      `TaskManagerService.cs`'s `KeywordSearchTaskType` special-case).

### Verification Report Format
Reviewer writes `CAS-037.review.md`, alongside this spec (`specs/misc/`):
- Overall verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Security gap list (if any)
- Scope violations (if any)

## Implementation Log

**Completed:** 2026-09-28
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 83% overall (both logic-bearing files at 100%; shortfall is compiler-generated record code + pre-existing shared-file lines; accepted as-is, owner-confirmed 2026-09-28) | Endpoints ⚠

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20260928_140000__CAS-037-b2-mirror-population.sql` | New migration: widen CK_task_queue_task_type, add dbo.task_queue_active_by_type_get, fix dbo.task_queue_ins allow-list |
| `Database/CachetArchiveSync/Entities/dbo.task_queue.sql` | CK_task_queue_task_type widened to include B2MirrorSync |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_active_by_type_get.sql` | New stored procedure backing the V1 concurrency guard |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_ins.sql` | Fixed hard-coded task_type allow-list to include B2MirrorSync |
| `src/Cachet.ArchiveSync.Core/Dtos/MirrorSyncResultSummaryDto.cs` | New result_detail DTO |
| `src/Cachet.ArchiveSync.Core/Interfaces/IB2MirrorSyncService.cs` | New service interface, submit outcome enum/result |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs` | Appended HasActiveTaskOfTypeAsync |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | Appended B2MirrorSyncSubmitted, B2MirrorSyncCompleted |
| `src/Cachet.ArchiveSync.Api/Services/B2MirrorSyncService.cs` | New submission service: concurrency check, insert, submission audit |
| `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs` | Implemented HasActiveTaskOfTypeAsync |
| `src/Cachet.ArchiveSync.Api/Controllers/SyncController.cs` | Added POST /sync/mirror-sync |
| `src/Cachet.ArchiveSync.Api/Program.cs` | Registered IB2MirrorSyncService |
| `src/Cachet.ArchiveSync.Worker/B2MirrorSyncTaskProcessor.cs` | New IWorkerTaskProcessor: enumerate/download/upsert B2 mirror |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | Registered B2MirrorSyncTaskProcessor |
| `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | Added B2:MirrorSyncPageSize |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC1_NewObjectsInsertedCorrectly` | SC-1 |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC2_UnchangedRerunSkipsAllDownloads` | SC-2, V-3 |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC3_ChangedObjectRedownloadedAndUpdated` | SC-3 |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC4_DownloadedFileWrittenAtExpectedPath` | SC-4 |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC5_ProgressReportedThroughoutRun` | SC-5 |
| `B2MirrorSyncTaskProcessorTests` | `Test_V2_NonUploadActionObjectsSkipped` | V-2 |
| `B2MirrorSyncTaskProcessorTests` | `Test_V4_PerFileFailureDoesNotAbortRun` | V-4 |
| `B2MirrorSyncTaskProcessorTests` | `Test_V5_MissingMirrorRootFailsTask` | V-5 |
| `B2MirrorSyncTaskProcessorTests` | `Test_SC7_NoKeyLeakage` | SC-7 |
| `B2MirrorSyncServiceTests` | `Test_V1_RejectsConcurrentMirrorSyncSubmission` | V-1 |
| `B2MirrorSyncServiceTests` | `Test_SC6_ConcurrentSubmissionRejected` | SC-6 |
| `SyncControllerTests` | `Test_PostMirrorSync_ReturnsAcceptedAndEnqueuesTask` | §6 endpoint contract |
| `SyncControllerTests` | `Test_MirrorSync_NoSession_Returns401` | 401 case |
| `SyncControllerTests` | `Test_MirrorSync_AlreadyRunning_Returns409NoTaskInserted` | SC-6 (HTTP layer) |
| `TaskQueueActiveByTypeGetTests` | `Test_TaskQueueActiveByTypeGet_ReflectsQueuedAndRunning` | §5 stored procedure |

### Migration
`20260928_140000__CAS-037-b2-mirror-population.sql`
