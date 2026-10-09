---
id: CAS-007
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
priority: P1
related_specs: [CAS-001, CAS-002, CAS-003, CAS-004, CAS-005, CAS-006]
touches:
  database: true
  api: true
  ui: false
  worker: true
  b2: true
  config: true
  security: true
migrations:
  - 20260922_180000__CAS-007-o-drive-sync-content-scan-api.sql
  - 20260922_181500__CAS-007-o-drive-sync-content-scan-api-addendum.sql
---

# CAS-007 — O-Drive Sync and Content Scan API (Removed/New/Modified Comparison, SHA-1 Check, Copy/Update/Purge Actions)

WBS Task 17 (confirmed by graphify — see §17). Implements FR-10, FR-11, FR-12, FR-13, FR-13a,
FR-13b, FR-13c, FR-13d. Architecture basis: BRD §3(d)/§7.3/§9/§12.1 items 9–10,
`CACHET_API_Design_Specification_v1.md` §5 (O-Drive Sync Module) and §1.6/§1.7/§2,
`CACHET_DataFlow_Analysis_v1.md` §3.3/Step 9, `CACHET_Database_Schema_Design_v1.md` §8
(provisional `sync_result` design, adapted below to CAS-003's merged conventions), CAS-001 §6.1/
§6.4 (`IB2Client`), CAS-002 §6.4/§6.5 (`CachetFullAccess`/`CachetAuthenticated` mapping and
system-key task-execution model).

---

## §0 Provenance Note (read this first)

**Correction to the requested task text:** the drafting prompt supplied for this spec named a
third endpoint, `POST /sync/results/quarantine`. That endpoint does not exist in any authoritative
source. BRD v2.2 (confirmed 2026-09-09, Section 12.1 item 9) made Quarantine a **Keyword
Search-only** workflow — O-Drive Sync has no Quarantine integration of any kind, a point the API
design spec's own §5 preamble and WBS Task 17's row both state explicitly ("this screen has **no**
quarantine integration; that was superseded by BRD v2.2"). The real second-through-fifth endpoints,
per WBS Task 17's own drafting prompt and API design spec §5.3–§5.5, are `POST /sync/actions/copy`
(FR-13a), `POST /sync/actions/update` (FR-13b), and `POST /sync/actions/purge` (FR-13). This spec
is built against those five endpoints (`scan`, `results`, `copy`, `update`, `purge`), not a
quarantine endpoint. Per this project's skill precedence (spec/BRD > local task text), the BRD and
WBS take priority over the stale instruction.

**Decomposition check (P9), resolved with the client (confirmed 2026-09-22, R. Devarajan,
TraceCounts) — single spec, not split:** Scan+Results (read-only, `CachetAuthenticated`, one
`ContentScan` task type) and Copy/Update/Purge (`CachetFullAccess`, three more task types, three
Worker processors, two of which call `IB2Client` write/delete operations) differ enough in
security posture and worker complexity that this project's own P9 rule would ordinarily flag two
signals (module/security-posture fan-out + approaching size thresholds) and warrant a split.
The client chose to keep this as one spec, matching WBS Task 17's own bundling of all five
endpoints into a single task/prompt and the precedent CAS-006 set for following WBS bundling
across a security-posture seam. See §17 for the full signal analysis.

`CAS-003` (merged) deliberately deferred `dbo.sync_result` DDL to "whichever spec first needs it"
(CAS-003 §4 Out-of-Scope; CAS-006 §4 confirms it is not that spec). This is that spec — full
lifecycle of `dbo.sync_result` (insert at scan time, `sync_status` transitions from Copy/Update/
Purge) is owned here.

`dbo.task_queue.task_type` already includes `'ContentScan'`, `'SyncCopy'`, `'SyncUpdate'`,
`'SyncPurge'` (CAS-003 §5, CAS-004 §5 — pre-existing, no schema change needed for that column).

This spec is placed under `specs/api/` (not `Database/`) per CLAUDE.md's precedence
(`ui > api > database > misc`) — its primary deliverable is the five endpoints; §5 documents
`dbo.sync_result` because no other spec currently owns it, not because table ownership is this
spec's primary subject.

**Implementation addendum (2026-09-22) — two gaps this spec's original draft left silent, closed
during `/implement`, not silently invented by the implementer agent:**
1. §5's own Comparison Algorithm (step 1: "load both sides") requires listing `dbo.file_index`
   rows by `source`, but the original §5 Stored Procedures table never named that procedure. Added
   `dbo.file_index_lst` (§5) and had db-author author it in an addendum migration, same pattern
   CAS-006 used for its own post-implementation addendum.
2. §9 never named a configuration key for the local filesystem root the `SyncCopy`/`SyncUpdate`
   Worker processors read O-Drive-side file bytes from before uploading to B2. Added
   `Sync:ODriveMirrorRootPath` (§9) — same category of gap as CAS-001 §9's still-deferred
   `B2:MirrorRootPath` (that one remains deferred to WBS Task 34; this one could not be deferred,
   since Copy/Update cannot function without it).
Both are narrow, spec-design completions directly implied by this spec's own already-approved §3/
§5 scope — not new features, not scope creep (§4 remains unchanged).

---

## §1 Summary

Implements FR-10, FR-11, FR-12, FR-13, FR-13a, FR-13b, FR-13c, FR-13d: `POST /sync/scan` submits
an async, parameterless Content Scan task that compares the O-Drive mirror against the B2 mirror
**purely as a local-directory-vs-local-directory comparison** — it never calls the live Backblaze
B2 API (API design spec §5.1) — and produces three result sets (New on O-Drive, Removed from
O-Drive, Modified on O-Drive with a SHA-1-changed flag); `GET /sync/{taskId}/results` returns those
three paginated panels once the task completes; and `POST /sync/actions/copy`,
`POST /sync/actions/update`, `POST /sync/actions/purge` let the user reconcile B2 to match the
O-Drive backup (the confirmed authoritative dataset), each submitted as its own tracked task
(FR-13c) and audited before response (FR-13d). This spec introduces the `dbo.sync_result` table
(owned here, full lifecycle) and four new Worker task processors (`ContentScan`, `SyncCopy`,
`SyncUpdate`, `SyncPurge`).

---

## §2 User Story

As a **TraceCounts reviewer** signed into Cachet Archive Sync, I want to run a Content Scan that
compares the current O-Drive backup against the B2 mirror and see exactly which files are new,
removed, or modified (with a clear SHA-1-changed signal), and then reconcile B2 to match the
O-Drive backup one action at a time — Copy new files in, Update changed files, or Purge files B2
still has that O-Drive no longer does — so that B2 stays a faithful mirror of the authoritative
O-Drive backup without me manually diffing two file trees.

---

## §3 In-Scope

- `POST /sync/scan` **[queued, audited]** — no request body; insert a `dbo.task_queue` row
  (`task_type = 'ContentScan'`), write an audit row **before** returning `202 Accepted` with the
  new `taskId` (FR-10, FR-18; CLAUDE.md's binding "search/scan submission must audit before
  success response" rule — same reconciliation this project made for CAS-006 §12).
- Worker-side `ContentScan` task processor (new, `Cachet.ArchiveSync.Worker`): claims the queued
  task (CAS-004 Appendix B claim pattern via `dbo.task_queue_claim`, reused from CAS-006), reads
  the current `dbo.file_index` rows for `source = 'ODrive'` and `source = 'B2'`, runs the
  presence-diff + SHA-1 comparison algorithm (§5's "Comparison Algorithm" subsection), inserts one
  `dbo.sync_result` row per disagreement (`comparison_type` = `New`/`Removed`/`Modified`,
  `sync_status = 'Pending'`), and transitions the task `Queued → Running → Completed`/`Failed`.
  **Never calls `IB2Client`** — this comparison is local-mirror-vs-local-mirror only (API design
  spec §5.1).
- `GET /sync/{taskId}/results` — paginated `newOnODrive`/`removedFromODrive`/`modifiedOnODrive`
  panels per API design spec §5.2's response shape; `404`/`409` per its error table.
- `POST /sync/actions/copy` **[queued, audited]** — validates `fileIds` are `Pending`
  `sync_result` rows of `comparison_type = 'New'` scoped to `taskId`; inserts a `dbo.task_queue`
  row (`task_type = 'SyncCopy'`); audits before `202`.
- Worker-side `SyncCopy` task processor: claims the task, for each targeted `sync_result` row
  reads the O-Drive file's content from the local mirror path, computes/reuses its SHA-1, calls
  `IB2Client.GetUploadUrlAsync` + `UploadFileAsync` (system key, CAS-001 §6.1/CAS-002 §6.5), inserts
  a new `dbo.file_index` row for `source = 'B2'` reflecting the uploaded object, updates the
  `sync_result.sync_status` to `'Synced'` or `'Failed'` per file, writes one `SyncCopyToB2` audit
  row per file (or per batch — implementer's choice, §12) before the task is marked complete,
  each file's outcome independent of the others (§7 V-series, consistent with CAS-006's/API design
  spec §6.4's per-file independence pattern).
- `POST /sync/actions/update` **[queued, audited]** — same request/validation shape as Copy but
  against `comparison_type = 'Modified'` rows; inserts `task_type = 'SyncUpdate'`.
- Worker-side `SyncUpdate` task processor: claims the task, for each targeted row uploads the
  O-Drive file's current version to B2 via `IB2Client.GetUploadUrlAsync`/`UploadFileAsync`
  (B2 versioning means this naturally supersedes the old object, per Backblaze B2 semantics — no
  explicit "overwrite" call exists), updates the corresponding `dbo.file_index` (`source = 'B2'`)
  row's `sha1_hash`/`file_size_bytes`/`modified_at`, updates `sync_result.sync_status`, audits
  (`SyncUpdateInB2`) per file, before task completion.
- `POST /sync/actions/purge` **[queued, audited]** — request includes `confirm: true` (required,
  irreversible, same posture as Permanent Delete per API design spec §5.5/BRD §9); validates
  `fileIds` are `Pending` `sync_result` rows of `comparison_type = 'Removed'`; inserts
  `task_type = 'SyncPurge'`; audits before `202`.
- Worker-side `SyncPurge` task processor: claims the task, for each targeted row calls
  `IB2Client.ListFileVersionsAsync` + `DeleteFileVersionAsync` for **every** version (reusing
  CAS-001 §6.4's Permanent Delete version-enumeration pattern verbatim, since both are the same
  underlying B2 operation per WBS Task 33's own "consider a shared delete module" note), soft-
  deletes the corresponding `dbo.file_index` (`source = 'B2'`) row (`is_active = 0`), updates
  `sync_result.sync_status`, audits (`SyncPurgeFromB2`) per file, before task completion.
- New table `dbo.sync_result` (full lifecycle — this spec's own).
- `AuditAction` enum additions: `ContentScanSubmitted`, `SyncCopySubmitted`, `SyncCopyToB2`,
  `SyncUpdateSubmitted`, `SyncUpdateInB2`, `SyncPurgeSubmitted`, `SyncPurgeFromB2`,
  `SyncActionFailedLocal`, `SyncActionFailedB2` (mirroring the existing `Deleted`/
  `DeleteFailedLocal`/`DeleteFailedB2` pattern already in `AuditAction.cs` for Permanent Delete).
- Typed DTOs for `task_queue.result_detail`/`request_parameters` for `task_type` values
  `ContentScan`, `SyncCopy`, `SyncUpdate`, `SyncPurge` (per CAS-004 §5's resolved
  action/result-catalog convention).

---

## §4 Out-of-Scope

- **Quarantine of any kind for O-Drive Sync** — does not exist per BRD v2.2 (§0). No
  `POST /sync/results/quarantine` endpoint, no shared table with `dbo.quarantine_entry`.
- **Keyword Search** (API design spec §4; FR-04–FR-09) — separate module, CAS-006's concern.
- **Generic Task Manager endpoints**: `GET /tasks`, `GET /tasks/{taskId}`,
  `POST /tasks/{taskId}/cancel` (API design spec §7; FR-19–FR-22a) — this spec only
  inserts/updates `dbo.task_queue` rows for its own four task types; it does not build the Task
  Manager screen or its generic read/cancel API.
- **Blazor Server UI** (`touches.ui: false`) — no `.razor` components; the O-Drive Sync screen
  (WBS Task 23) is a separate, future Web-kind spec consuming these five endpoints.
- **B2 mirror population / initial `dbo.file_index` indexing for either source** (CAS-001 §6.2,
  WBS Task 34) — assumed already populated for both `source = 'ODrive'` and `source = 'B2'`; this
  spec only reads and updates `dbo.file_index`, it does not perform the initial O-Drive-directory
  or B2-bucket crawl.
- **`dbo.file_index.bucket_name`'s O-Drive-to-bucket mapping convention** — flagged open by
  CAS-003 §15 for WBS Task 34, not resolved or re-opened here; this spec's comparison algorithm
  (§5) assumes the mapping already exists on every `dbo.file_index` row it reads.
- **A shared Permanent-Delete/Purge delete module** — WBS Task 33 raises the possibility of one
  in-house delete module serving both Permanent Delete (CAS-B, future) and this spec's Purge
  action, since both perform the identical `ListFileVersionsAsync`/`DeleteFileVersionAsync`
  sequence. This spec implements Purge's own call sequence directly (duplicating, not sharing,
  CAS-001 §6.4's logic) since no Permanent Delete spec exists yet to share code with (Karpathy P4
  — no abstraction until the pattern appears twice in actual code, not merely in two specs).
  Flagged in §15 for whichever spec implements Permanent Delete next.
- **Retention/archival** of `sync_result` rows — CAS-004 §15 already defers a retention policy
  generally; not revisited here.
- **Drive encryption-at-rest** (BRD §12.2 open item) — a physical O-Drive/B2-mirror-population
  concern, not this API surface. Not blocking this spec.

---

## §5 Database Objects

### Tables / Columns

#### `dbo.sync_result` (new — owned by this spec)

Per-task Content Scan comparison rows (FR-11) and their own Copy/Update/Purge sync-action status
(FR-12) — exists so a scan never mutates `dbo.file_index`'s own rows directly (CAS-003's
separation-of-concerns convention, same rationale as CAS-006's `dbo.search_result`), and per
schema design doc §8, adapted to CAS-003's exact audit-column/identity style (same adaptation
CAS-006 made for `dbo.search_result`).

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `sync_result_id` | `INT IDENTITY(100000,1)` | No | PK, clustered. Exposed to the API as `fileId` (§1.7 semantics) — scoped to the `ContentScan` task that produced it, not a stable identity across scans. |
| `created_by` | `NVARCHAR(50)` | No | Audit column — the Worker's process identity (`worker-svc`). |
| `created_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()`. |
| `updated_by` | `NVARCHAR(50)` | No | Audit column — updated by the Copy/Update/Purge task processor on a `sync_status` transition. |
| `updated_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()`. |
| `task_queue_id` | `INT` | No | FK → `dbo.task_queue(task_queue_id)`. The `ContentScan` task that produced this row. |
| `comparison_type` | `NVARCHAR(10)` | No | `CHECK (comparison_type IN ('New','Removed','Modified'))` (DB schema design doc §8). |
| `odrive_file_index_id` | `INT` | Yes | FK → `dbo.file_index(file_index_id)`. Populated for `New` and `Modified`. |
| `b2_file_index_id` | `INT` | Yes | FK → `dbo.file_index(file_index_id)`. Populated for `Removed` and `Modified`. |
| `sha1_changed` | `BIT` | Yes | Only meaningful for `Modified` rows (FR-11's SHA-1-changed indicator). |
| `sync_status` | `NVARCHAR(20)` | No | `CHECK (sync_status IN ('Pending','Synced','Failed'))`, default `'Pending'` (FR-12). |
| `sync_action_task_queue_id` | `INT` | Yes | FK → `dbo.task_queue(task_queue_id)`. The `SyncCopy`/`SyncUpdate`/`SyncPurge` task that last transitioned `sync_status`; `NULL` until a sync action is submitted for this row. |

**Constraints:** `PK_sync_result`, `CK_sync_result_comparison_type`, `CK_sync_result_sync_status`,
`CK_sync_result_fk_shape` — a `CHECK` enforcing the schema design doc §8 rule:
```sql
CHECK (
    (comparison_type = 'New'      AND odrive_file_index_id IS NOT NULL AND b2_file_index_id IS NULL)
 OR (comparison_type = 'Removed'  AND b2_file_index_id IS NOT NULL AND odrive_file_index_id IS NULL)
 OR (comparison_type = 'Modified' AND odrive_file_index_id IS NOT NULL AND b2_file_index_id IS NOT NULL)
)
```

**Foreign keys:** `task_queue_id → dbo.task_queue(task_queue_id)`,
`sync_action_task_queue_id → dbo.task_queue(task_queue_id)`,
`odrive_file_index_id → dbo.file_index(file_index_id)`,
`b2_file_index_id → dbo.file_index(file_index_id)`.

**Design note:** unlike `dbo.search_result`'s `(task_queue_id, file_index_id, match_type)` unique
constraint, `dbo.sync_result` has no equivalent uniqueness constraint — the schema design doc does
not specify one, and each scan is expected to fully supersede the prior scan's open comparisons for
review purposes (the UI queries by the current `taskId` only, per API design spec §5.2). Not
adding one here (Karpathy P3 — no invented constraint beyond what the design doc specifies).

**Planned stored procedures:** `dbo.sync_result_ins`, `dbo.sync_result_get`, `dbo.sync_result_lst`
(`@task_queue_id` required, `@comparison_type` optional filter, `@page`/`@pageSize` per §1.4),
`dbo.sync_result_upd` (`sync_status`/`sync_action_task_queue_id` transition only, mirroring
CAS-006's `search_result` "no independent edit beyond status" convention but for status, not
content).

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `dbo.sync_result_ins` | dbo | ins | One row per disagreement found by the Content Scan comparison. |
| `dbo.sync_result_get` | dbo | get | By `sync_result_id`; joins `file_index` (both FK roles) for display fields. |
| `dbo.sync_result_lst` | dbo | lst | `@task_queue_id`, optional `@comparison_type`, `@page`/`@pageSize` — backs §6.2. |
| `dbo.sync_result_upd` | dbo | upd | `sync_status`/`sync_action_task_queue_id` transition only — called by the Copy/Update/Purge task processors. |
| `dbo.file_index_ins` | dbo | ins | Named by CAS-003 §5, first authored here for real use: `SyncCopy`'s new B2-side `file_index` row. |
| `dbo.file_index_upd` | dbo | upd | Named by CAS-003 §5, first authored here for real use: `SyncUpdate`'s B2-side row refresh, `SyncPurge`'s soft-delete (`is_active = 0`). |
| `dbo.file_index_lst` | dbo | lst | **Added post-review (implementer-discovered gap, 2026-09-22) — see §0 addendum.** `@source` (`'ODrive'`/`'B2'`), `@page`/`@pageSize` (§1.4), `@totalCount` OUTPUT; filters `is_active = 1`. Backs §5's Comparison Algorithm step 1 (load both sides) — named by CAS-003 §5 but, like `file_index_ins`/`_upd`, not authored there; this spec is the first to need it for real. |
| `dbo.task_queue_ins` | dbo | ins | Reused from CAS-004 Appendix B — `task_type` = `ContentScan`/`SyncCopy`/`SyncUpdate`/`SyncPurge`. |
| `dbo.task_queue_claim` | dbo | upd | Reused verbatim from CAS-006 (first authored there). |
| `dbo.task_queue_upd` | dbo | upd | Reused from CAS-004 — status/`result_detail` transitions. |
| `dbo.audit_log_ins` | dbo | ins | Reused from CAS-005. |

11 stored procedures total, of which 4 (`sync_result_*`) are new to this spec, 3
(`file_index_ins`/`_upd`/`_lst`) are named-but-unauthored-elsewhere procedures this spec authors
for the first time, and 4 are reused verbatim from CAS-004/CAS-005/CAS-006 — over the §2.3
threshold (>8 triggers a split check) as of the `file_index_lst` addendum; not treated as a new
split signal (§17) since it is one additional procedure discovered mid-implementation to close a
real gap in this spec's own original design, not new scope.

### Comparison Algorithm (FR-10, FR-11 — implementation-level detail)

Runs entirely inside the `ContentScan` Worker task processor, entirely against `dbo.file_index`
(no B2 API call, per API design spec §5.1):

1. **Load both sides.** Query `dbo.file_index` for all `is_active = 1` rows where `source =
   'ODrive'` into an in-memory map keyed by `(bucket_name, relative_path)`, and separately for all
   `is_active = 1` rows where `source = 'B2'` into a second map keyed the same way. (`bucket_name`
   is O-Drive Sync's join key across both sources — CAS-003 §15's open mapping-convention item is
   what makes this key meaningful; not re-litigated here, see §4.)
2. **New on O-Drive:** for every key present in the O-Drive map but absent from the B2 map, insert
   a `dbo.sync_result` row with `comparison_type = 'New'`, `odrive_file_index_id` = that O-Drive
   row's `file_index_id`, `b2_file_index_id = NULL`, `sha1_changed = NULL`, `sync_status =
   'Pending'`.
3. **Removed from O-Drive:** for every key present in the B2 map but absent from the O-Drive map,
   insert a row with `comparison_type = 'Removed'`, `b2_file_index_id` = that B2 row's
   `file_index_id`, `odrive_file_index_id = NULL`, `sha1_changed = NULL`, `sync_status =
   'Pending'`.
4. **Modified on O-Drive:** for every key present in **both** maps, compare `sha1_hash`
   (`dbo.file_index.sha1_hash`, populated by the mirror-population process per CAS-003 §5's
   comment on that column, not computed fresh here — if either side's `sha1_hash` is `NULL`,
   treat as "not yet hashed" and skip the pair, do not insert a row and do not treat a null as a
   mismatch). If the two SHA-1 values differ, insert a row with `comparison_type = 'Modified'`,
   both FK columns populated, `sha1_changed = 1`, `sync_status = 'Pending'`. **If the SHA-1 values
   are equal, no row is inserted at all** — an unchanged file in both mirrors produces no
   comparison output, consistent with API design spec §5.2's response shape (only disagreements
   are listed; there is no "unchanged" panel).
5. **Scale note (NFR — archive scale, thousands of files per NFR Analysis target #20):** both maps
   are built with one paginated `dbo.file_index_lst`-equivalent query per source (not one row at a
   time), and the comparison itself is an in-memory hash-map join (`O(n + m)`, not nested-loop),
   so total comparison cost is linear in the combined row count of both mirrors, not quadratic.
6. **Completion:** once all three passes finish (steps 2–4 can run in any order or be combined
   into one pass over the union of both maps' keys — implementer's choice, this spec does not
   prescribe pass ordering, only the classification rule per key), the Worker updates the
   `ContentScan` task row to `Completed` with a `result_detail` summarizing counts per
   `comparison_type` (§11), or to `Failed` with the error detail if the comparison throws.

### Migration Strategy

`db-author` writes, at `/implement` time:
- `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-007-o-drive-sync-content-scan-api.sql` — idempotent,
  creates `dbo.sync_result` plus the stored procedures above (including first-time bodies for the
  already-named-but-unauthored `dbo.file_index_ins`/`_upd`, scoped narrowly to what this spec's
  Copy/Update/Purge processors need — full CRUD parity with CAS-003 §5's naming is not required
  here, only the operations this spec's processors call).
- `Database/CachetArchiveSync/Entities/dbo.sync_result.sql` — declarative mirror.
- `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_{ins,get,lst,upd}.sql`,
  `dbo.file_index_{ins,upd}.sql` (if not already introduced by a concurrently-implemented spec —
  check before authoring a duplicate).

No data migration — `dbo.sync_result` is new, empty at deploy time.

---

## §6 API Endpoints

### Endpoint: `POST /sync/scan` **[queued, audited]**

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]` (CAS-002 §6.4 — Content Scan is a
read/compare-only operation, no B2 write, so any signed-in session qualifies, same posture as
Keyword Search).

**Async:** Yes — returns immediately with a task reference (FR-10, FR-18); the comparison itself
runs in the Worker, never inline within the request.

**Request:** no body — the comparison scope is always the full mirror pair (API design spec §5.1).

**Response `202 Accepted`:**
```json
{
  "taskId": 100301,
  "type": "ContentScan",
  "status": "Queued",
  "submittedAt": "2026-09-22T14:05:00Z"
}
```

**Sequence:** insert `dbo.task_queue` (`task_type = 'ContentScan'`) → write audit row
(`action: ContentScanSubmitted`, `tab_module: 'O-Drive Sync'`, `task_queue_id` populated) →
return `202`. Audit write happens **before** the response (§12).

**Status codes:**
| Code | Condition |
|------|-----------|
| 202 | Accepted — task queued |
| 401 | No valid session |

### Endpoint: `GET /sync/{taskId}/results`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]` (CAS-002 §6.4 — read-only).

**Async:** No — synchronous read of already-computed results.

**Request:** path param `taskId` (int); optional query params `page`, `pageSize` per panel
(default `1`/`50`, §1.4); the panel each pagination param applies to is disambiguated by prefix
(`newOnODrivePage`, `removedFromODrivePage`, `modifiedOnODrivePage` — **(design decision, not
BRD-specified)** since API design spec §5.2's example does not show three independently paginated
query-string shapes; not needed for a single-page result set, only for archive-scale results).

**Response `200 OK`** (task `Completed`) — per API design spec §5.2's exact shape:
```json
{
  "taskId": 100301,
  "status": "Completed",
  "newOnODrive": {
    "items": [
      { "fileId": 100601, "name": "new-invoice.xlsx", "path": "finance/2026/new-invoice.xlsx",
        "size": 20481, "modifiedAt": "2026-09-01T00:00:00Z", "syncStatus": "Pending" }
    ],
    "page": 1, "pageSize": 50, "totalCount": 1
  },
  "removedFromODrive": {
    "items": [
      { "fileId": 100602, "name": "old-contract.pdf", "path": "contracts/2015/old-contract.pdf",
        "lastSeen": "2026-08-01T00:00:00Z", "syncStatus": "Pending" }
    ],
    "page": 1, "pageSize": 50, "totalCount": 1
  },
  "modifiedOnODrive": {
    "items": [
      { "fileId": 100603, "name": "policy.docx", "path": "hr/policy.docx",
        "modifiedAt": "2026-08-20T00:00:00Z", "sha1Changed": true, "syncStatus": "Pending" }
    ],
    "page": 1, "pageSize": 50, "totalCount": 1
  }
}
```

**Response `200 OK`** (task `Failed`): same envelope with `"status": "Failed"`, the three panel
fields omitted, plus `failureReason` (proposed — not BRD-specified, same pattern CAS-006 §6
already established for `GET /search/{taskId}/results`, kept consistent across modules).

**Sequence:** look up `dbo.task_queue` by `taskId` + `task_type = 'ContentScan'`; if
`Queued`/`Running`, return `409`; if `Completed`, call `dbo.sync_result_lst` three times (once per
`comparison_type`) and project into the three panels; if `Failed`, return the `failureReason`
shape above.

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | `Completed` (with panels) or `Failed` (with `failureReason`) |
| 404 | `task-not-found` — unknown `taskId`, or `taskId` belongs to a different `task_type` |
| 409 | `task-not-complete` — task exists but is `Queued`/`Running` |
| 401 | No valid session |

### Endpoint: `POST /sync/actions/copy` **[queued, audited]**

**Auth:** `[Authorize(Policy = "CachetFullAccess")]` (CAS-002 §6.4 — B2-write action).

**Async:** Yes — returns immediately with a task reference (FR-13c); the upload runs in the
Worker.

**Request:**
```json
{ "taskId": 100301, "fileIds": [100601] }
```
`fileIds` are `sync_result_id` values scoped to `taskId`, each expected to be a `Pending` row with
`comparison_type = 'New'`.

**Response `202 Accepted`:**
```json
{ "taskId": 100401, "type": "SyncCopy", "status": "Queued", "submittedAt": "2026-09-22T14:10:00Z" }
```

**Sequence:** validate → insert `dbo.task_queue` (`task_type = 'SyncCopy'`,
`request_parameters` = `{ "sourceTaskId": 100301, "fileIds": [100601] }`) → write audit row
(`action: SyncCopySubmitted`, `tab_module: 'O-Drive Sync'`) → return `202`. Worker then, per
targeted `sync_result` row: reads the O-Drive file at its local mirror path, uploads via
`IB2Client.GetUploadUrlAsync`/`UploadFileAsync` (system key), inserts a new `dbo.file_index` row
(`source = 'B2'`), sets `sync_result.sync_status = 'Synced'`, writes a `SyncCopyToB2` audit row —
or, on failure for that file, sets `sync_status = 'Failed'` and writes `SyncActionFailedB2` (or
`SyncActionFailedLocal` if the O-Drive-side read itself fails), **before** the task overall is
marked `Completed`. Each file's outcome is independent — one file failing does not stop the others
(§7).

**Status codes:**
| Code | Condition |
|------|-----------|
| 202 | Accepted — task queued |
| 404 | `task-not-found` — unknown `taskId` |
| 404 | `file-not-found` — a `fileId` is not a `Pending`/`New` `sync_result` row scoped to `taskId` |
| 422 | `validation-failed` — empty `fileIds` |
| 403 | `insufficient-capability` — session lacks `CachetFullAccess` |
| 401 | No valid session |

### Endpoint: `POST /sync/actions/update` **[queued, audited]**

**Auth:** `[Authorize(Policy = "CachetFullAccess")]`.

**Async:** Yes.

**Request:**
```json
{ "taskId": 100301, "fileIds": [100603] }
```
`fileIds` scoped to `taskId`, each expected to be a `Pending` row with `comparison_type =
'Modified'`.

**Response `202 Accepted`:** same shape, `"type": "SyncUpdate"`.

**Sequence:** same pattern as Copy (`action: SyncUpdateSubmitted` at submission,
`SyncUpdateInB2`/`SyncActionFailedB2`/`SyncActionFailedLocal` per file at completion), except the
Worker uploads to the **existing** B2 object's `fileName` (B2's own versioning supersedes the
prior content — no separate "overwrite" B2 call exists) and updates that `dbo.file_index` row's
`sha1_hash`/`file_size_bytes`/`modified_at` in place rather than inserting a new row.

**Status codes:** same as Copy, with `file-not-found` meaning "not a `Pending`/`Modified` row".

### Endpoint: `POST /sync/actions/purge` **[queued, audited]**

**Auth:** `[Authorize(Policy = "CachetFullAccess")]`.

**Async:** Yes.

**Request:**
```json
{ "taskId": 100301, "fileIds": [100602], "confirm": true }
```
`confirm` must be `true` — same server-side backstop as Permanent Delete (API design spec §6.4);
absent or `false` is rejected before any task is queued, not merely before deletion is attempted.

**Response `202 Accepted`:** same shape, `"type": "SyncPurge"`.

**Sequence:** validate `confirm = true` and `fileIds` non-empty and each a `Pending`/`Removed` row
→ insert `dbo.task_queue` (`task_type = 'SyncPurge'`) → audit (`action: SyncPurgeSubmitted`) →
return `202`. Worker then, per targeted row: calls `IB2Client.ListFileVersionsAsync` (prefix = the
object's path) then `IB2Client.DeleteFileVersionAsync` for **every** version returned (CAS-001
§6.4 Step B pattern, reused directly per §4's note), soft-deletes the `dbo.file_index` (`source =
'B2'`) row (`is_active = 0`), sets `sync_result.sync_status = 'Synced'` (meaning "purge
completed" — the row's own naming convention carries over from Copy/Update even though "purged"
reads more naturally; not renaming the shared enum value per file, consistent with API design spec
§5.2's note that `Synced` is the terminal success state for any of the three actions) or `'Failed'`
on error, writes `SyncPurgeFromB2`/`SyncActionFailedB2` audit rows per file, before task
completion.

**Status codes:**
| Code | Condition |
|------|-----------|
| 202 | Accepted — task queued |
| 422 | `confirmation-required` — `confirm` missing or `false` |
| 422 | `validation-failed` — empty `fileIds` |
| 404 | `task-not-found` — unknown `taskId` |
| 404 | `file-not-found` — a `fileId` is not a `Pending`/`Removed` row scoped to `taskId` |
| 403 | `insufficient-capability` — session lacks `CachetFullAccess` |
| 401 | No valid session |

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `GET .../results` `taskId` must exist and have `task_type = 'ContentScan'` | 404 `task-not-found` | `Test_V1_ResultsUnknownOrWrongTypeTaskIdReturns404` |
| V2 | `GET .../results` on a `Queued`/`Running` task returns `409`, not partial/empty `200` | 409 `task-not-complete` | `Test_V2_ResultsBeforeCompletionReturns409` |
| V3 | `POST /sync/actions/{copy,update,purge}` `fileIds` must be non-empty | 422 `validation-failed` | `Test_V3_SyncActionEmptyFileIdsRejected` |
| V4 | `POST /sync/actions/copy` every `fileId` must resolve to a `Pending` `sync_result` row with `comparison_type = 'New'` scoped to `taskId` | 404 `file-not-found` | `Test_V4_CopyFileIdNotPendingNewReturns404` |
| V5 | `POST /sync/actions/update` every `fileId` must resolve to a `Pending` `sync_result` row with `comparison_type = 'Modified'` scoped to `taskId` | 404 `file-not-found` | `Test_V5_UpdateFileIdNotPendingModifiedReturns404` |
| V6 | `POST /sync/actions/purge` every `fileId` must resolve to a `Pending` `sync_result` row with `comparison_type = 'Removed'` scoped to `taskId` | 404 `file-not-found` | `Test_V6_PurgeFileIdNotPendingRemovedReturns404` |
| V7 | `POST /sync/actions/purge` `confirm` must be `true`; absent/`false` rejects the whole request before any task is queued | 422 `confirmation-required` | `Test_V7_PurgeWithoutConfirmRejectedBeforeQueueing` |
| V8 | `POST /sync/actions/{copy,update,purge}` require `CachetFullAccess`; `POST /sync/scan` and `GET .../results` require only `CachetAuthenticated` | 403 `insufficient-capability` (actions) | `Test_V8_SyncActionsRequireFullAccessScanAndResultsDoNotEscalate` |
| V9 | Worker: the Content Scan comparison never inserts a `sync_result` row for a `(bucket_name, relative_path)` pair whose SHA-1 values are equal on both sides | N/A — enforced by not inserting; proven by absence | `Test_V9_UnchangedFilePairProducesNoSyncResultRow` |
| V10 | Worker: a `Modified` pair where either side's `sha1_hash` is `NULL` is skipped (no row inserted, not treated as a mismatch) | N/A — enforced by not inserting | `Test_V10_UnhashedFilePairSkippedNotFlaggedModified` |

---

## §8 UI / UX

N/A — `touches.ui: false`. No `.razor` components; the Web project is untouched by this spec. BRD
UI/UX spec §7 (O-Drive Sync and Content Scan screen) exists for reference but its implementation
is WBS Task 23, a separate, future Web-kind spec that consumes these five endpoints.

---

## §9 Configuration Changes

| Key | Default | Description | Env Override |
|-----|---------|-------------|---------------|
| `Sync:MaxBatchSize` | `100` | Server-side cap on `fileIds` length per Copy/Update/Purge request — bounds a single Worker task's per-file B2 call volume (no documented B2 rate limit to derive a tighter number from, same reasoning CAS-001 §9 used for `B2:CrossCheckMaxConcurrency`). **Confirmed 2026-09-22, R. Devarajan (TraceCounts) — see §15.** | Yes |
| `Sync:ODriveMirrorRootPath` | *(none)* | **Added post-review (implementer-discovered gap, 2026-09-22) — see §0 addendum.** Local filesystem root for the O-Drive mirror `SyncCopy`/`SyncUpdate` read file bytes from before uploading to B2 — same category as CAS-001 §9's `B2:MirrorRootPath` (that one stays deferred to WBS Task 34; this one cannot be, since Copy/Update cannot function without it). No BRD/WBS text names this path; value must be supplied per environment before Copy/Update can run against a real O-Drive mount. | Yes |

No new B2 credential keys — this spec reuses `B2:SystemKeyId`/`B2:SystemApplicationKey`/
`B2:BucketName`, already introduced by CAS-001 and never touched here. No change to
`B2:CrossCheckMaxConcurrency` (CAS-006) — this spec's Worker processors never call
`IB2Client.GetFileInfoAsync` in a cross-check loop, so that concurrency knob does not apply here.

---

## §10 Success Criteria

**15 SCs — above the P9 §2.3 boundary (>12 triggers a split check) by design, per §0's
client-confirmed decision to keep this as one spec despite the signal.** Each SC covers a
distinct, independently-testable behavior across the scan side and the three action sides; no SC
below duplicates another action's SC with only the task-type name changed, since each action
processor has a materially different B2 call shape (upload-new vs. upload-supersede vs.
list-and-delete-every-version).

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `POST /sync/scan` returns `202` + `taskId`; a `dbo.task_queue` row exists with `task_type = 'ContentScan'`, `status = 'Queued'`; the `ContentScanSubmitted` audit row exists with `created_at` at or before the response was sent | `Test_SC1_ScanSubmissionQueuesTaskAndAuditsBeforeResponse` |
| SC-2 | Given a claimed `ContentScan` task, the Worker processor correctly classifies a synthetic `dbo.file_index` fixture (files only on O-Drive, only in B2, on both with matching SHA-1, on both with differing SHA-1) into exactly the expected `New`/`Removed`/`Modified`/no-row outcomes | `Test_SC2_ContentScanProcessorClassifiesAllFourCases` |
| SC-3 | A `ContentScan` task reaches `status = 'Completed'` with `completed_at` populated and `result_detail` containing per-`comparison_type` counts, even when zero disagreements are found | `Test_SC3_ScanTaskCompletesEvenWithZeroDisagreements` |
| SC-4 | `GET /sync/{taskId}/results` on a `Completed` task returns `200` with all three panels correctly paginated per §1.4's envelope, `modifiedOnODrive` items carrying `sha1Changed: true` | `Test_SC4_CompletedScanReturnsThreePaginatedPanels` |
| SC-5 | `GET /sync/{taskId}/results` on a `Queued`/`Running` task returns `409 task-not-complete`; on an unknown `taskId` returns `404 task-not-found` | `Test_SC5_IncompleteOrUnknownTaskIdHandledCorrectly` |
| SC-6 | `POST /sync/actions/copy` with valid `fileIds` queues a `SyncCopy` task, and the Worker processor uploads each targeted file via `IB2Client`, inserts a new B2-side `dbo.file_index` row, and sets `sync_result.sync_status = 'Synced'` | `Test_SC6_SyncCopyUploadsAndUpdatesMirrorAndStatus` |
| SC-7 | `POST /sync/actions/update` with valid `fileIds` queues a `SyncUpdate` task, and the Worker processor uploads the O-Drive version to the existing B2 object and updates that `dbo.file_index` row's `sha1_hash`/`file_size_bytes`/`modified_at` in place (no new row inserted) | `Test_SC7_SyncUpdateSupersedesB2ObjectAndUpdatesExistingRow` |
| SC-8 | `POST /sync/actions/purge` with `confirm: true` and valid `fileIds` queues a `SyncPurge` task, and the Worker processor calls `DeleteFileVersionAsync` once per version returned by `ListFileVersionsAsync` for each targeted object, then soft-deletes the corresponding `dbo.file_index` row | `Test_SC8_SyncPurgeDeletesEveryB2VersionAndSoftDeletesFileIndex` |
| SC-9 | A per-file B2 failure during Copy/Update/Purge sets that file's `sync_result.sync_status = 'Failed'` and writes `SyncActionFailedB2`, without aborting the other files in the same batch | `Test_SC9_PerFileB2FailureIsolatedNotBatchAborting` |
| SC-10 | All three action endpoints (`copy`/`update`/`purge`) write their respective `*Submitted` audit row before returning `202`, and their respective per-file completion audit row (`SyncCopyToB2`/`SyncUpdateInB2`/`SyncPurgeFromB2`) before the task is marked `Completed` | `Test_SC10_AllThreeActionsAuditBeforeSubmitAndBeforeCompletion` |
| SC-11 | A session with only `Read Only` B2 capability (no `CachetFullAccess` claim) receives `403` from all three action endpoints but `200`/`202` from `POST /sync/scan` and `GET .../results` | `Test_SC11_ReadOnlySessionCannotSubmitActionsButCanScanAndRead` |
| SC-12 | Copy/Update/Purge task processors never call `IUserB2KeyAuthorizer`, only the system-level `IB2Client` (mocked in the unit test) — confirms the CAS-002 §6.5 "system key, not the submitting user's key" model is followed | `Test_SC12_ActionProcessorsUseSystemKeyNotUserKey` |
| SC-13 | Two concurrent Worker instances claiming any of the four task types this spec introduces never both claim the same `task_queue` row (reuses CAS-003 SC-3/CAS-006 SC-12's claim pattern, now exercised for `ContentScan`/`SyncCopy`/`SyncUpdate`/`SyncPurge`) | `Test_SC13_ConcurrentWorkersNeverDoubleClaimSyncTasks` |
| SC-14 | No B2 application key, session token/cookie value, raw file content, or full local file path beyond what the audit log already records ever appears in a log line across all five endpoints or the four Worker processors | `Test_SC14_NoCredentialOrContentLeakageInLogs` |
| SC-15 | A batch of `fileIds` larger than `Sync:MaxBatchSize` is rejected with `422 validation-failed` before any task is queued | `Test_SC15_OversizedBatchRejectedBeforeQueueing` |

---

## §11 Telemetry

```csharp
_logger.LogInformation("ContentScan task {TaskId} submitted by user {UserId}", taskId, userId);
_logger.LogInformation(
    "ContentScan task {TaskId} completed: {NewCount} new, {RemovedCount} removed, {ModifiedCount} modified",
    taskId, newCount, removedCount, modifiedCount);
_logger.LogInformation(
    "{ActionType} task {TaskId} submitted by user {UserId} for {FileCount} files",
    actionType, taskId, userId, fileCount);
_logger.LogInformation(
    "{ActionType} task {TaskId} completed: {SucceededCount} succeeded, {FailedCount} failed",
    actionType, taskId, succeededCount, failedCount);
_logger.LogWarning("{ActionType} task {TaskId} file {SyncResultId} failed: {ErrorSummary}",
    actionType, taskId, syncResultId, errorSummary);
```

- Never log: the B2 application key or any part of it, a B2 upload/authorization token, a session
  token/cookie value, full file content, or a raw local O-Drive filesystem path beyond what the
  audit log's own `file_path` column already captures (same rule as CAS-006 §11, extended to this
  spec's four Worker processors).

---

## §12 Security Notes

### B2 Credential Handling
Copy/Update/Purge task processors use the **system-level** `IB2Client` only (CAS-001 §6.1,
CAS-002 §6.5's "checked at enqueue, not re-checked by the Worker" model) — never
`IUserB2KeyAuthorizer`, which is login-validation-only (CAS-002). No B2 key, key ID, or B2
upload/authorization token is ever present in any request/response DTO defined in §6, and none is
logged (§11). `CachetFullAccess` is checked at submission time in the Api (while the session is
live), not re-checked by the Worker at execution time — consistent with CAS-002 §6.5's explicit
reconciliation of this exact point for Copy/Update/Purge.

### Audit Trail
Eight audited writes, each before its corresponding response or task-completion transition
(FR-13d):
- `POST /sync/scan` — `action: ContentScanSubmitted`, `tab_module: 'O-Drive Sync'`,
  `task_queue_id` populated, before `202`.
- `POST /sync/actions/copy` — `action: SyncCopySubmitted`, before `202`.
- `POST /sync/actions/update` — `action: SyncUpdateSubmitted`, before `202`.
- `POST /sync/actions/purge` — `action: SyncPurgeSubmitted`, before `202`.
- Worker, per successfully-synced file — `SyncCopyToB2` / `SyncUpdateInB2` / `SyncPurgeFromB2`,
  `file_path` populated, `result: Success`, before the task is marked `Completed`.
- Worker, per failed file — `SyncActionFailedB2` (B2 call itself failed) or
  `SyncActionFailedLocal` (reading the O-Drive source file failed, Copy/Update only), `result:
  Failure`, `result_detail` carrying the error, before the task is marked `Completed`/`Failed`.

**Note on the API design doc's own tagging:** `CACHET_API_Design_Specification_v1.md` §5.1
(`POST /sync/scan`) does not carry an explicit `**[audited]**` tag, the same gap CAS-006 §12
already flagged and resolved for `POST /search`. This spec applies the identical reconciliation:
CLAUDE.md's binding rule ("search/scan submission must audit before success response") overrides
the API design doc's incomplete tagging on this one endpoint. §5.3–§5.5 (the three actions) do
each carry `**[audited]**` explicitly, so no reconciliation is needed there.

### Session / Auth Requirements
`[Authorize(Policy = "CachetAuthenticated")]` on `POST /sync/scan` and `GET .../results`;
`[Authorize(Policy = "CachetFullAccess")]` on all three action endpoints (CAS-002 §6.4's confirmed
mapping — Purge/Copy/Update require `Full Access`, matching Permanent Delete's own policy). Not
blocked by BRD §12.2 — the login method underlying both policies is already resolved (CAS-002),
and drive encryption-at-rest (the other §12.2 open item) has no bearing on this API surface.

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `ContentScanTaskProcessorTests` (NSubstitute `IFileIndexRepository`, `ISyncResultRepository`) | `Test_SC2_ContentScanProcessorClassifiesAllFourCases`, `Test_SC3_ScanTaskCompletesEvenWithZeroDisagreements`, `Test_V9_UnchangedFilePairProducesNoSyncResultRow`, `Test_V10_UnhashedFilePairSkippedNotFlaggedModified` | SC-2, SC-3, V9, V10 |
| `SyncCopyTaskProcessorTests` (NSubstitute `IB2Client`, `IFileIndexRepository`, `ISyncResultRepository`, `IAuditLogService`) | `Test_SC6_SyncCopyUploadsAndUpdatesMirrorAndStatus`, `Test_SC9_PerFileB2FailureIsolatedNotBatchAborting` (Copy case), `Test_SC12_ActionProcessorsUseSystemKeyNotUserKey` (Copy case) | SC-6, SC-9, SC-12 |
| `SyncUpdateTaskProcessorTests` (same substitutes) | `Test_SC7_SyncUpdateSupersedesB2ObjectAndUpdatesExistingRow`, `Test_SC9_PerFileB2FailureIsolatedNotBatchAborting` (Update case) | SC-7, SC-9 |
| `SyncPurgeTaskProcessorTests` (same substitutes) | `Test_SC8_SyncPurgeDeletesEveryB2VersionAndSoftDeletesFileIndex`, `Test_SC9_PerFileB2FailureIsolatedNotBatchAborting` (Purge case) | SC-8, SC-9 |
| `SyncActionRequestValidatorTests` | `Test_V3_SyncActionEmptyFileIdsRejected`, `Test_V4_CopyFileIdNotPendingNewReturns404`, `Test_V5_UpdateFileIdNotPendingModifiedReturns404`, `Test_V6_PurgeFileIdNotPendingRemovedReturns404`, `Test_V7_PurgeWithoutConfirmRejectedBeforeQueueing`, `Test_SC15_OversizedBatchRejectedBeforeQueueing` | V3–V7, SC-15 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `SyncControllerTests` (`WebApplicationFactory`) | `Test_SC1_ScanSubmissionQueuesTaskAndAuditsBeforeResponse`, `Test_SC4_CompletedScanReturnsThreePaginatedPanels`, `Test_SC5_IncompleteOrUnknownTaskIdHandledCorrectly`, `Test_V1_ResultsUnknownOrWrongTypeTaskIdReturns404`, `Test_V2_ResultsBeforeCompletionReturns409`, `Test_SC11_ReadOnlySessionCannotSubmitActionsButCanScanAndRead`, `Test_V8_SyncActionsRequireFullAccessScanAndResultsDoNotEscalate` | SC-1, SC-4, SC-5, SC-11, V1, V2, V8 |
| `SyncResultRepositoryTests` (real SQL Server) | Insert/list round-trip against `dbo.sync_result`, `CK_sync_result_fk_shape` constraint per `comparison_type` | Supports SC-2 |
| `SyncActionAuditIntegrationTests` (real SQL Server, real `IAuditLogService`, mocked `IB2Client`) | `Test_SC10_AllThreeActionsAuditBeforeSubmitAndBeforeCompletion` | SC-10 |
| `SyncTaskQueueClaimConcurrencyTests` (real SQL Server, parallel claim attempts) | `Test_SC13_ConcurrentWorkersNeverDoubleClaimSyncTasks` | SC-13 |
| Log-output assertion (test log sink) across all of the above | `Test_SC14_NoCredentialOrContentLeakageInLogs` | SC-14 |

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. Deploy the database migration first (`dbo.sync_result` and the stored procedures in §5) —
   additive-only, no existing table altered.
2. Deploy `Core` (new DTOs/enum members), `Api`, and `Worker` **together** — `POST /sync/scan` and
   the three action endpoints will queue tasks that never complete if the corresponding Worker
   processors aren't live yet.
3. Rollback: `dbo.sync_result` is additive and empty until this feature is used — dropping it is
   safe pre-traffic. Post-traffic, follow the same "never hard-delete audit-adjacent data" posture
   as CAS-003/CAS-004/CAS-006 (do not drop `sync_result` once rows exist; roll back code only,
   leave schema in place). Because `SyncPurge` performs an irreversible B2 delete, a rollback of
   this spec's code **after** any Purge task has run does not and cannot restore the purged B2
   objects — this is the same accepted, deliberate risk posture BRD §9 already records for
   Permanent Delete.

---

## §15 Open Questions

- [x] **`POST /sync/results/quarantine` does not exist — resolved 2026-09-22 (R. Devarajan,
      TraceCounts, via this spec's §0 correction):** built against the five endpoints the BRD/API
      design spec/WBS Task 17 actually define instead.
- [x] **Decomposition (P9) — resolved 2026-09-22 (R. Devarajan, TraceCounts):** kept as one spec
      despite two near-boundary signals (security-posture fan-out, §10 exceeding the 12-SC
      guideline at 15) — see §0 and §17.
- [x] **`Sync:MaxBatchSize` = 100 — resolved 2026-09-22 (R. Devarajan, TraceCounts).** No BRD or
      WBS text specifies a batch cap for Copy/Update/Purge; this is an engineering guardrail, not
      a business rule, same category as CAS-006's `Search:MaxTermLength`. Accepted at 100 — already
      2x the default results-panel page size (§1.4's 50), so constructing a batch that large
      requires the user to select across multiple pages, a soft backstop ahead of the config cap
      itself. Not yet exercised against real O-Drive/B2 volumes (a 100-file Purge batch can mean
      100+ sequential `ListFileVersionsAsync`/`DeleteFileVersionAsync` calls in one Worker task,
      since B2 version count per object is unbounded) — revisit once WBS Task 34 (mirror
      population) has run against production-scale data; a config change, not a schema or contract
      change, if it needs tuning.
- [ ] **`Sync:ODriveMirrorRootPath` actual value — needs environment-specific confirmation.**
      Added post-review (§0 addendum) to close a real implementation gap; no BRD/WBS text names
      this path, and it cannot be deferred the way CAS-001's analogous `B2:MirrorRootPath` is,
      since `SyncCopy`/`SyncUpdate` cannot read O-Drive source bytes without it. Left blank in
      `appsettings.Development.json`; must be set per environment (dev/staging/prod) before Copy/
      Update can be exercised against a real O-Drive mount.
- [ ] **Shared Purge/Permanent-Delete module (WBS Task 33) — deferred, not this spec's decision.**
      This spec implements Purge's `ListFileVersionsAsync`/`DeleteFileVersionAsync` sequence
      directly rather than factoring out a shared module with the not-yet-implemented Permanent
      Delete spec (§4). Flag for whichever spec implements Permanent Delete next: consider
      extracting a shared `IB2DeleteService` at that point (Karpathy P4 — the pattern will then
      have appeared twice in actual code, not just in two specs). Deliberately left unchecked —
      this is forward-looking guidance for a future spec, not a decision CAS-007 itself needs to
      make; it does not block `status → ready`.
- Not blocked by either open BRD §12.2 item (login method already resolved; drive
  encryption-at-rest is unrelated to this API surface) — confirmed not applicable, not carried
  forward as a blocker.

All items that gate `status → ready` are now resolved — the one remaining unchecked item
(shared Purge/Permanent-Delete module) is intentionally forward-looking, not a blocker.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 | |
| Reviewer | | | Pending |

---

## §17 Graph Impact

Graph exists for this repo (`graphify-out/`, built 2026-09-10, indexes the BRD/WBS/API-spec/
decision-log documentation corpus — no application code nodes exist yet for O-Drive Sync; only
Auth (CAS-002) has real Api/Worker code on disk as of this spec's authoring).

### God Nodes in Blast Radius
None of the graph's top-10 numeric god nodes (`graphify` tool internals) are BRD/spec nodes this
feature touches. Within the documentation graph, `FR-13c: Sync Actions Queued via Task Manager` is
the highest-degree FR-node this spec's blast radius passes through, bridging to `FR-13: Purge`,
`FR-13a: Copy`, `FR-13b: Update`, `FR-13d: Audit Before Completion`, `WBS Task 17: O-Drive Sync
API`, and `WBS Task 33: B2 Permanent-Delete Integration` (the last confirming §4/§15's "shared
delete module" cross-reference is a real, graph-surfaced connection, not a speculative one).

### Communities / Modules Affected
- **O-Drive Sync community** — `POST /sync/scan`, `GET /sync/{taskId}/results`,
  `POST /sync/actions/{copy,update,purge}`, `FR-10`–`FR-13d`, `WBS Task 17` (confirming this spec
  is the API implementation of WBS Task 17).
- **Task Manager community** — `task_queue`'s four new-to-this-spec `task_type` values
  (`ContentScan` already named by CAS-003; `SyncCopy`/`SyncUpdate`/`SyncPurge` likewise) — this
  spec is the first to actually implement processors for any of the four.
- **B2 Integration community** — `IB2Client` (CAS-001), specifically the `GetUploadUrlAsync`/
  `UploadFileAsync` pair (Copy/Update) and `ListFileVersionsAsync`/`DeleteFileVersionAsync` pair
  (Purge, shared graph node with `FR-16`/`FR-17` Permanent Delete — confirms §4/§15's flagged
  future-sharing opportunity is graph-visible, not this spec's invention).
- **Auth/Session community** — `CachetFullAccess`/`CachetAuthenticated` policy nodes (CAS-002
  §6.4), directly connected to `FR-13`/`FR-13a`/`FR-13b`'s "Full Access required" requirement.
- Projects touched: `Api` (new `SyncController`), `Worker` (four new task processors — the first
  Worker task processors to call `IB2Client` for a write/delete operation, versus CAS-006's
  read-only cross-check use), `Core` (new DTOs/enum members), `Database` (one new table). **Not**
  `Web`.

### Surprising Connections
- The graph co-locates `WBS Task 33: B2 permanent-delete integration`'s own text — "consider
  building this as one shared delete module both features call, rather than two parallel
  implementations" — directly with this spec's `FR-13` node, independently corroborating §4/§15's
  decision to flag (not silently resolve) the shared-module question rather than either building
  it now (no Permanent Delete code exists yet to share with) or ignoring the overlap.
- `CAS-002 §6.5`'s task-queue B2 key resolution reconciliation note is graph-adjacent to both this
  spec's `FR-13c` node and CAS-006's `FR-08` node, confirming the same system-key-only model
  applies uniformly across every Worker-executed B2 operation in this codebase — no module-
  specific exception was introduced here.
- **Decomposition check (P9) result — two real signals, resolved by explicit client decision, not
  silently absorbed:** (1) security-posture fan-out (`CachetAuthenticated` for scan/results vs.
  `CachetFullAccess` for the three actions, each action also a distinct Worker/B2 call shape) and
  (2) §10 Success Criteria at 15, above the >12 threshold. Per the guideline, "2 or more [signals]
  = stop and find a fault line" would normally force a split into a scan/results spec and a
  copy/update/purge spec. The client was presented this exact choice on 2026-09-22 and chose to
  keep one spec, matching WBS Task 17's own single-task bundling of all five endpoints and the
  precedent CAS-006 set for following WBS bundling over the guideline's default split
  recommendation. **Decision: single spec, per explicit client override of the default
  recommendation** — recorded here rather than silently overridden, per this project's own
  auditability principle (Karpathy P6).

### Graphify Queries Run
```
python -m graphify query "O-Drive Sync Content Scan Copy Update Purge FR-10 FR-11 FR-13 FR-13c" --budget 1500
```
(The God Nodes/Communities/Surprising Connections sections above also draw on the static
`graphify-out/GRAPH_REPORT.md`, build 2026-09-10, and a manual read of CAS-001/CAS-002/CAS-003/
CAS-004/CAS-006 and the current `src/` tree, since no O-Drive Sync application code exists yet for
the graph to index.)

---

## §18 Verification Plan

### Reviewer Checklist
The reviewer will independently verify each of the following. Mark N/A only if the section itself
is N/A.

- [ ] All §10 Success Criteria (SC-1–SC-15) have a passing test.
- [ ] All §7 Validations (V1–V10) are implemented and tested.
- [ ] All five §6 API endpoints exist with the correct verb/route/policy — `CachetAuthenticated`
      on `POST /sync/scan` and `GET .../results`, `CachetFullAccess` on all three action
      endpoints. Confirm no endpoint named `POST /sync/results/quarantine` was added (§0/§4).
- [ ] `POST /sync/scan` writes the `ContentScanSubmitted` audit row **before** returning `202`
      (§12's explicit note that this is audited despite the API design doc's own tagging gap on
      §5.1 specifically).
- [ ] Each of `POST /sync/actions/{copy,update,purge}` writes its `*Submitted` audit row before
      `202`, and its per-file completion audit row before the task is marked `Completed`.
- [ ] The `ContentScan` Worker processor never calls `IB2Client` (§3, §5 Comparison Algorithm) —
      confirms the local-mirror-vs-local-mirror-only design.
- [ ] Copy/Update/Purge Worker processors call only the system-level `IB2Client`, never
      `IUserB2KeyAuthorizer` (§12 B2 Credential Handling, SC-12).
- [ ] A `Modified` pair with equal SHA-1 values produces no `sync_result` row; a pair with a
      `NULL` SHA-1 on either side is skipped, not flagged `Modified` (§7 V9/V10).
- [ ] `Purge` correctly enumerates and deletes **every** B2 version of a targeted object, not just
      the current version (§6, SC-8, reusing CAS-001 §6.4's pattern).
- [ ] §5 `dbo.sync_result` exists in schema exactly as specified, including
      `CK_sync_result_fk_shape`'s per-`comparison_type` FK-population rule.
- [ ] §9 config key (`Sync:MaxBatchSize`) is present in `appsettings.json`/
      `appsettings.Development.json` with a placeholder, non-secret value.
- [ ] §11 Telemetry events are emitted, and none logs the B2 key, an upload/authorization token, a
      session token, raw file content, or a raw local file path.
- [ ] §17 Graph Impact matches actual code changes — no unintended nodes (e.g., no accidental
      `Web`/UI changes, no Quarantine-table references from this spec's code).
- [ ] No out-of-scope changes from §4 were made (no Task Manager generic endpoints, no Keyword
      Search code, no shared Permanent-Delete module invented ahead of its own spec).

### Verification Report Format
Per template: `CAS-007.review.md` with overall verdict, per-SC verdict table, security gap list,
scope violations.

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 30.3% overall (below 85% threshold; accepted per user decision 2026-09-22 -- see note below) | Endpoints ✓

**Coverage gate note (2026-09-22, accepted by R. Devarajan, TraceCounts):** overall coverage
(30.3%) is below the §13 85% target. Root cause: `SyncResultRepository`, `FileIndexRepository`,
and `ODriveFileReader` require a live SQL Server / real filesystem to exercise meaningfully, and
this sandbox has neither — the same 15 real-DB tests that are skip-marked in CAS-006's own suites
(`TaskQueueClaimConcurrencyTests`, `*RepositoryDatabaseTests`, etc.) are skip-marked here too
(`SyncTaskQueueClaimConcurrencyTests`). Per-file breakdown: repository/reader classes at 0%,
services/controller at 18.5%–59.5% (only DB-independent branches unit-testable), Worker task
processors (the fully unit-testable layer) at 83%–99%. This is structurally identical to CAS-006's
own already-merged coverage profile (also 30.3% overall, 0% on its DB repository classes) — not a
CAS-007-specific test-writing gap. Real coverage of the DB-dependent classes requires a CI/test
environment with a live SQL Server instance, not additional mocked unit tests. Accepted as-is;
flagged for the reviewer to confirm rather than silently passed.

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20260922_180000__CAS-007-o-drive-sync-content-scan-api.sql` | New — idempotent migration creating dbo.sync_result plus 6 stored procedures |
| `Database/migrations/20260922_181500__CAS-007-o-drive-sync-content-scan-api-addendum.sql` | New — addendum migration adding dbo.file_index_lst (spec-design gap closed mid-implementation) |
| `Database/CachetArchiveSync/Entities/dbo.sync_result.sql` | New — declarative mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_ins.sql` | New |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_get.sql` | New |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_lst.sql` | New |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_upd.sql` | New |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_ins.sql` | New — named by CAS-003, authored here for the first time |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_upd.sql` | New — named by CAS-003, authored here for the first time |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_lst.sql` | New — addendum, closes a spec-design gap in the Comparison Algorithm |
| `src/Cachet.ArchiveSync.Core/Dtos/SyncResultRow.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/FileIndexRow.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/NewOnODriveItem.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/RemovedFromODriveItem.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/ModifiedOnODriveItem.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskSubmittedResponse.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/SyncScanResultsResponse.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/SyncActionRequest.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/SyncPurgeRequest.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/SyncActionRequestParametersDto.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/ContentScanResultSummaryDto.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/SyncActionResultSummaryDto.cs` | New |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | Modified — appended 9 CAS-007 audit actions |
| `src/Cachet.ArchiveSync.Core/Interfaces/ISyncResultRepository.cs` | New |
| `src/Cachet.ArchiveSync.Core/Interfaces/ISyncScanService.cs` | New |
| `src/Cachet.ArchiveSync.Core/Interfaces/ISyncActionService.cs` | New |
| `src/Cachet.ArchiveSync.Core/Interfaces/IODriveFileReader.cs` | New |
| `src/Cachet.ArchiveSync.Core/Interfaces/IFileIndexRepository.cs` | Modified — added ListActiveBySourceAsync, InsertAsync, UpdateAsync |
| `src/Cachet.ArchiveSync.Api/Repositories/SyncResultRepository.cs` | New |
| `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs` | Modified — added three new methods |
| `src/Cachet.ArchiveSync.Api/Storage/ODriveFileReader.cs` | New — local O-Drive mirror file reader |
| `src/Cachet.ArchiveSync.Api/Services/SyncScanService.cs` | New |
| `src/Cachet.ArchiveSync.Api/Services/SyncActionService.cs` | New |
| `src/Cachet.ArchiveSync.Api/Controllers/SyncController.cs` | New — all five routes |
| `src/Cachet.ArchiveSync.Api/Program.cs` | Modified — DI registrations |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | Modified — added Sync:MaxBatchSize, Sync:ODriveMirrorRootPath |
| `src/Cachet.ArchiveSync.Worker/ContentScanTaskProcessor.cs` | New |
| `src/Cachet.ArchiveSync.Worker/SyncCopyTaskProcessor.cs` | New |
| `src/Cachet.ArchiveSync.Worker/SyncUpdateTaskProcessor.cs` | New |
| `src/Cachet.ArchiveSync.Worker/SyncPurgeTaskProcessor.cs` | New |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | Modified — registered four new processors |
| `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | Modified — added Sync config block |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `SyncControllerTests` | `Test_SC1_ScanSubmissionQueuesTaskAndAuditsBeforeResponse` | SC-1 |
| `ContentScanTaskProcessorTests` | `Test_SC2_ContentScanProcessorClassifiesAllFourCases` | SC-2 |
| `ContentScanTaskProcessorTests` | `Test_SC3_ScanTaskCompletesEvenWithZeroDisagreements` | SC-3 |
| `SyncControllerTests` | `Test_SC4_CompletedScanReturnsThreePaginatedPanels` | SC-4 |
| `SyncControllerTests` | `Test_SC5_IncompleteOrUnknownTaskIdHandledCorrectly` | SC-5 |
| `SyncCopyTaskProcessorTests` | `Test_SC6_SyncCopyUploadsAndUpdatesMirrorAndStatus` | SC-6 |
| `SyncUpdateTaskProcessorTests` | `Test_SC7_SyncUpdateSupersedesB2ObjectAndUpdatesExistingRow` | SC-7 |
| `SyncPurgeTaskProcessorTests` | `Test_SC8_SyncPurgeDeletesEveryB2VersionAndSoftDeletesFileIndex` | SC-8 |
| `SyncCopyTaskProcessorTests, SyncUpdateTaskProcessorTests, SyncPurgeTaskProcessorTests` | `Test_SC9_PerFileB2FailureIsolatedNotBatchAborting` | SC-9 |
| `SyncActionServiceTests, SyncActionProcessorSecurityTests` | `Test_SC10_AllThreeActionsAuditBeforeSubmitAndBeforeCompletion` | SC-10 |
| `SyncControllerTests` | `Test_SC11_ReadOnlySessionCannotSubmitActionsButCanScanAndRead` | SC-11 |
| `SyncActionProcessorSecurityTests` | `Test_SC12_ActionProcessorsUseSystemKeyNotUserKey` | SC-12 |
| `SyncTaskQueueClaimConcurrencyTests` | `Test_SC13_ConcurrentWorkersNeverDoubleClaimSyncTasks` | SC-13 (skip-marked, no live SQL Server in sandbox) |
| `SyncLogHygieneTests, SyncTaskProcessorLogHygieneTests` | `Test_SC14_NoCredentialOrContentLeakageInLogs` | SC-14 |
| `SyncActionServiceTests` | `Test_SC15_OversizedBatchRejectedBeforeQueueing` | SC-15 |
| `SyncControllerTests` | `Test_V1_ResultsUnknownOrWrongTypeTaskIdReturns404` | V1 |
| `SyncControllerTests` | `Test_V2_ResultsBeforeCompletionReturns409` | V2 |
| `SyncActionServiceTests` | `Test_V3_SyncActionEmptyFileIdsRejected` | V3 |
| `SyncActionServiceTests` | `Test_V4_CopyFileIdNotPendingNewReturns404` | V4 |
| `SyncActionServiceTests` | `Test_V5_UpdateFileIdNotPendingModifiedReturns404` | V5 |
| `SyncActionServiceTests` | `Test_V6_PurgeFileIdNotPendingRemovedReturns404` | V6 |
| `SyncActionServiceTests` | `Test_V7_PurgeWithoutConfirmRejectedBeforeQueueing` | V7 |
| `SyncControllerTests` | `Test_V8_SyncActionsRequireFullAccessScanAndResultsDoNotEscalate` | V8 |
| `ContentScanTaskProcessorTests` | `Test_V9_UnchangedFilePairProducesNoSyncResultRow` | V9 |
| `ContentScanTaskProcessorTests` | `Test_V10_UnhashedFilePairSkippedNotFlaggedModified` | V10 |

### Migration
`20260922_180000__CAS-007-o-drive-sync-content-scan-api.sql, 20260922_181500__CAS-007-o-drive-sync-content-scan-api-addendum.sql`
