---
id: CAS-006
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
priority: P1
related_specs: [CAS-001, CAS-002, CAS-003, CAS-004, CAS-005]
touches:
  database: true
  api: true
  ui: false
  worker: true
  b2: true
  config: true
  security: true
migrations:
  - 20260922_170000__CAS-006-keyword-search-api.sql
  - 20260922_173000__CAS-006-keyword-search-api-addendum.sql
---

# CAS-006 — Keyword Search API (Filename + Content Search, Local-First, B2 Cross-Check)

WBS Task 16 (confirmed by graphify — see §17). Implements FR-04–FR-09, FR-18. Architecture basis:
BRD §3(c)/§10.1 (dual local cache, API-confirmed search), `CACHET_API_Design_Specification_v1.md`
§4 (Keyword Search Module) and §1.6/§1.7/§2, `CACHET_DataFlow_Analysis_v1.md` Steps 3/5/7,
`CACHET_Database_Schema_Design_v1.md` §7/§9 (provisional `search_result`/`quarantine_entry`
design, adapted below to CAS-003's merged conventions).

---

## §0 Provenance Note (read this first)

`CAS-003` (merged) deliberately deferred `dbo.search_result`, `dbo.sync_result`, and
`dbo.quarantine_entry` DDL to "whichever spec first needs them" (CAS-003 §4 Out-of-Scope). This
is that spec for `dbo.search_result` (owned here in full) and for `dbo.quarantine_entry`'s
**add-only** path (FR-09) — the table's full, fixed-vocabulary `status` column
(`Active`/`Released`/`Deleted`) is created now since it is a closed BRD-defined set (consistent
with CAS-003's own convention of reserving `CHECK` for closed enumerations), but the
`Released`/`Deleted` transitions themselves, and the `GET /quarantine`, `POST /quarantine/release`,
`POST /quarantine/delete` endpoints (API spec §6.1/§6.3/§6.4), are **out of scope** — a future
Quarantine module spec owns those, reusing this table without a schema change.

`B2:CrossCheckMaxConcurrency` was named but explicitly deferred by CAS-001 §9 to "whichever spec
implements §6.3's cross-check code" — that is this spec (§9).

This spec was requested and scoped explicitly as "Keyword Search API": `POST /search`,
`GET /search/{taskId}/results`, `POST /search/results/quarantine`. It is placed under `specs/api/`
(not `Database/`) per CLAUDE.md's precedence (`ui > api > database > misc`) — its primary
deliverable is the three endpoints; §5 documents the supporting tables because no other spec
currently owns them, not because table ownership is this spec's primary subject.

---

## §1 Summary

Implements FR-04, FR-05, FR-06, FR-07, FR-08, FR-09, and FR-18: `POST /search` submits an async
keyword-search task (bucket + extension filter + term) rather than running synchronously (FR-18);
a Worker-side task processor executes the search **local-first** against `dbo.file_index`
(filename match + SQL Server full-text content match, FR-05), then cross-checks every local
candidate against live Backblaze B2 before it is allowed to become an actionable result (FR-08,
per CAS-001 §6.3's per-match cross-check sequence); `GET /search/{taskId}/results` returns the
already-B2-confirmed Filename Matches and Content Matches panels (FR-06/FR-07); and
`POST /search/results/quarantine` lets the user send one or more matched files to the Quarantine
Queue (FR-09). This spec also introduces the `dbo.search_result` table (owned here) and the
add-only path of `dbo.quarantine_entry` (owned here; full lifecycle deferred — see §0).

---

## §2 User Story

As a **TraceCounts reviewer** signed into Cachet Archive Sync, I want to submit a keyword search
scoped to a bucket and file-extension filter and see, once the search completes, a Filename
Matches panel and a Content Matches panel containing only files **confirmed to still exist on
live Backblaze B2** — never a stale local-mirror entry shown as if it were real — so that I can
review and quarantine genuine matches without chasing false positives.

---

## §3 In-Scope

- `POST /search` **[queued, audited]** — validate request, insert a `dbo.task_queue` row
  (`task_type = 'KeywordSearch'`), write an audit row **before** returning `202 Accepted` with the
  new `taskId` (FR-04, FR-05, FR-18; CLAUDE.md's explicit "search/scan submission must audit
  before success response" rule — see §12 note on the API design doc's own `[audited]` gap).
- Worker-side `KeywordSearch` task processor (new, `Cachet.ArchiveSync.Worker`): claims the queued
  task (CAS-004 Appendix B claim pattern), runs the local-first query against `dbo.file_index`
  (filename `LIKE` for Filename Matches, `CONTAINS`/`FREETEXT` full-text query on `content_text`
  for Content Matches, both scoped to `bucket_name` + `file_extension`), then cross-checks each
  local candidate against B2 per CAS-001 §6.3 using the system-level `IB2Client` (bounded
  concurrency via `B2:CrossCheckMaxConcurrency`), inserts confirmed rows into `dbo.search_result`,
  self-heals and audits (`CrossCheckExcluded`) any candidate B2 no longer has, and transitions the
  task `Queued → Running → Completed`/`Failed`.
- `GET /search/{taskId}/results` — paginated `filenameMatches`/`contentMatches` per API spec §4.2's
  response shape; `404`/`409` per §4.2's error table.
- `POST /search/results/quarantine` **[audited]** — resolves the given task-scoped `fileId`s
  (`search_result_id`s) to their file's stable identity (`file_index_id`), writes
  `dbo.quarantine_entry` rows (`status = 'Active'`), audits (`QuarantineAdd`) before responding
  (FR-09, FR-23).
- New table `dbo.search_result` (full lifecycle — this spec's own).
- New table `dbo.quarantine_entry` (schema owned here; only the add path is implemented — §0, §4).
- `AuditAction` enum additions: `KeywordSearchSubmitted`, `QuarantineAdd` (alongside the already-
  existing `CrossCheckExcluded`, reused verbatim from CAS-001's design).
- Typed DTOs for `task_queue.request_parameters`/`result_detail` for `task_type = 'KeywordSearch'`
  (`KeywordSearchRequestDto`, `KeywordSearchResultSummaryDto`), per CAS-004 §5's resolved
  action/result-catalog convention.
- `B2:CrossCheckMaxConcurrency` configuration key (named by CAS-001 §9, added here).

---

## §4 Out-of-Scope

- **Full Quarantine module**: `GET /quarantine`, `POST /quarantine/release`,
  `POST /quarantine/delete` (API spec §6.1/§6.3/§6.4; FR-14–FR-17) — a future spec, reusing
  `dbo.quarantine_entry` as created here without a schema change.
- **O-Drive Sync and Content Scan** (API spec §5; FR-10–FR-13d) — separate module, separate spec.
- **Generic Task Manager endpoints**: `GET /tasks`, `GET /tasks/{taskId}`,
  `POST /tasks/{taskId}/cancel` (API spec §7; FR-19–FR-22a) — this spec only inserts/updates
  `dbo.task_queue` rows for its own `KeywordSearch` task type; it does not build the Task Manager
  screen or its generic read/cancel API.
- **Blazor Server UI** (`touches.ui: false`) — no `.razor` components; Web project untouched.
- **FR-21/FR-22 auto-population/notification** — a Web/client behavior layered on top of this API,
  per API spec §11's own note that no dedicated push endpoint is specified there either.
- **B2 mirror population / initial `dbo.file_index` indexing** (CAS-001 §6.2, WBS Task 34) —
  assumed already populated; this spec only reads `dbo.file_index`, never populates it.
- **Drive encryption-at-rest** (BRD §12.2 open item) — a physical O-Drive/B2-mirror-population
  concern, not this API surface. Not blocking this spec.
- **Retention/archival** of `search_result` or `quarantine_entry` rows — CAS-004 §15 already
  defers a retention policy generally; this spec does not revisit it.
- **`dbo.sync_result`** — designed alongside `search_result`/`quarantine_entry` in the schema
  design doc, but not touched here; owned by a future O-Drive Sync spec.

---

## §5 Database Objects

### Tables / Columns

#### `dbo.search_result` (new — owned by this spec)

Per-task Keyword Search match rows (FR-06–FR-09) — exists so a search never mutates
`dbo.file_index`'s own row (CAS-003's separation-of-concerns convention, reused from the schema
design doc §7, adapted to CAS-003's exact audit-column/identity style).

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `search_result_id` | `INT IDENTITY(100000,1)` | No | PK, clustered. Exposed to the API as `fileId` (§1.7 semantics) — **not** the same value as `file_index_id`, since the same physical file can appear as a distinct row across different search tasks. |
| `created_by` | `NVARCHAR(50)` | No | Audit column — the Worker's process identity (`worker-svc`). |
| `created_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()`. |
| `updated_by` | `NVARCHAR(50)` | No | Audit column. |
| `updated_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()`. |
| `task_queue_id` | `INT` | No | FK → `dbo.task_queue(task_queue_id)`. The `KeywordSearch` task that produced this row. |
| `file_index_id` | `INT` | No | FK → `dbo.file_index(file_index_id)`. |
| `match_type` | `NVARCHAR(10)` | No | `CHECK (match_type IN ('Filename','Content'))` — which panel (FR-06 vs. FR-07). |
| `match_snippet` | `NVARCHAR(1000)` | Yes | Content-match snippet (FR-07); populated only when `match_type = 'Content'`. |
| `match_status` | `NVARCHAR(20)` | No | `CHECK (match_status IN ('Flagged','Needs Review','Informational'))`. |

**Constraints:** `PK_search_result`, `UNIQUE (task_queue_id, file_index_id, match_type)`,
`CK_search_result_match_type`, `CK_search_result_match_status`.

**Foreign keys:** `task_queue_id → dbo.task_queue(task_queue_id)`,
`file_index_id → dbo.file_index(file_index_id)`.

**Design note (inherited from schema design doc §7, reconfirmed):** a B2-cross-check candidate
that turns out not to exist on B2 (FR-08; Data-Flow Analysis §Step 5.3) is **never inserted
here** — the self-healing correction happens in the Worker before a row would have been created
(§6, §12). This satisfies API spec §4.2's confirmation that no fourth status chip/row exists for
that case.

**Planned stored procedures:** `dbo.search_result_ins`, `dbo.search_result_get`,
`dbo.search_result_lst` (`@task_queue_id` required, `@match_type` optional filter, `@page`/
`@pageSize` per §1.4). No `_upd`/`_del` — a search result, once produced, is not independently
editable; it is either quarantined (§6.3) or superseded by the next search.

#### `dbo.quarantine_entry` (new — schema owned by this spec; add-path only implemented — §0)

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `quarantine_entry_id` | `INT IDENTITY(100000,1)` | No | PK, clustered. |
| `created_by` | `NVARCHAR(50)` | No | Audit column — the user who added this entry; doubles as the business "quarantined by" actor (no separate business field, per schema design doc §9's rationale). |
| `created_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()` — doubles as the future `GET /quarantine`'s `quarantinedOn`. |
| `updated_by` | `NVARCHAR(50)` | No | Audit column — reserved for the future release/delete transitions (§0); unused by this spec beyond mirroring `created_by` at insert. |
| `updated_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()`; same as above. |
| `file_index_id` | `INT` | No | FK → `dbo.file_index(file_index_id)`. |
| `search_result_id` | `INT` | Yes | FK → `dbo.search_result(search_result_id)`. The originating search match — always populated by this spec's own insert path (FR-09 always originates from a search result), nullable only because a hypothetical future entry point might not have one. |
| `match_status_snapshot` | `NVARCHAR(20)` | No | `CHECK (match_status_snapshot IN ('Flagged','Needs Review','Informational'))` — copied from `search_result.match_status` at insert time (churns independently thereafter). |
| `status` | `NVARCHAR(20)` | No | `CHECK (status IN ('Active','Released','Deleted'))`, default `'Active'`. Full enum created now (closed BRD-defined set) even though this spec only ever writes `'Active'` — see §0. |

**Constraints:** `PK_quarantine_entry`,
`UNIQUE (file_index_id) WHERE status = 'Active'` (filtered unique index — a file can only be
actively quarantined once at a time), `CK_quarantine_entry_status`,
`CK_quarantine_entry_match_status_snapshot`.

**Foreign keys:** `file_index_id → dbo.file_index(file_index_id)`,
`search_result_id → dbo.search_result(search_result_id)`.

**Planned stored procedures (this spec):** `dbo.quarantine_entry_ins` only. `_get`/`_lst`/`_upd`
(release/delete transitions) are out of scope (§0, §4) — deferred to the future Quarantine module
spec, which reuses this table as-is.

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `dbo.search_result_ins` | dbo | ins | One row per confirmed match (Filename or Content). |
| `dbo.search_result_get` | dbo | get | By `search_result_id`; joins `file_index` for display fields. |
| `dbo.search_result_lst` | dbo | lst | `@task_queue_id`, optional `@match_type`, `@page`/`@pageSize` — backs §6.2. |
| `dbo.quarantine_entry_ins` | dbo | ins | Add-only (§0); resolves `search_result_id → file_index_id` server-side. |
| `dbo.task_queue_ins` | dbo | ins | Reused from CAS-004 Appendix B — `task_type = 'KeywordSearch'`. |
| `dbo.task_queue_claim` (proposed name, not yet authored anywhere) | dbo | upd | Reused pattern from CAS-004 Appendix B step 2 (`UPDATE TOP(1) ... WITH (READPAST, UPDLOCK) ... OUTPUT`) — this spec is the first to actually need it built. |
| `dbo.task_queue_upd` | dbo | upd | Reused from CAS-004 — status/`result_detail` transitions. |
| `dbo.task_queue_get` (addendum — see note below) | dbo | get | By `task_queue_id`; used by `GET /search/{taskId}/results` (§6, V4) and `POST /search/results/quarantine`'s `taskId` lookup. |
| `dbo.audit_log_ins` | dbo | ins | Reused from CAS-005. |
| `dbo.file_index_search` (addendum — see note below) | dbo | get/lst | Filename `LIKE` + full-text `CONTAINS`/`FREETEXT` query against `dbo.file_index`, scoped to `@bucket_name`/`@file_extension`, one call per `match_type` (Filename/Content) — backs the Worker's local-first step (§3, §6). |
| `dbo.file_index_soft_delete_upd` (addendum — see note below) | dbo | upd | Sets `is_active = 0` on one `file_index_id` — backs the Worker's self-heal step when a candidate fails B2 cross-check (§7 V9, §12). |

**Addendum (implementation-time correction, 2026-09-22):** the original draft of this table
omitted `dbo.task_queue_get` and both `dbo.file_index_*` procedures — a real gap, not a deliberate
scope decision. `dbo.file_index` was merged by CAS-003 as a table only; CAS-003 §5's own text notes
`_get`/`_lst`/`_ins`/`_upd`/`_del` as "planned" but none were actually authored there, and no later
spec added them before this one needed them. This spec is the first to read/mutate `file_index`
after CAS-003, so it is the correct place to add these three procedures — consistent with how this
spec already owns `dbo.task_queue_claim` for the same reason (first-consumer-authors-it). Added
during `/implement` (P2: spec is silent → flag and correct, don't invent silently) rather than
left for the implementer to work around with ad-hoc inline SQL.

11 stored procedures total (7 new to this spec: `search_result_*` (3), `quarantine_entry_ins`,
`task_queue_claim`, `task_queue_get`, `file_index_search`, `file_index_soft_delete_upd` — 3 reused
verbatim: `task_queue_ins`, `task_queue_upd`, `audit_log_ins`) — over the §2.3 nominal threshold of
8, but not treated as a split trigger: every added procedure is infrastructure this spec's own
three endpoints directly require (no new business-logic surface, no new endpoint, no new module),
and only one other §2.3 signal (estimated changed files) was already noted as borderline in §17 —
still short of the "2 or more signals" bar the decomposition guideline sets.

### Migration Strategy

`db-author` writes, at `/implement` time:
- `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-006-keyword-search-api.sql` — idempotent, creates
  `dbo.search_result` and `dbo.quarantine_entry` plus the stored procedures above.
- `Database/CachetArchiveSync/Entities/dbo.search_result.sql`,
  `Database/CachetArchiveSync/Entities/dbo.quarantine_entry.sql` — declarative mirrors.
- `Database/CachetArchiveSync/Stored-Procedures/dbo.search_result_{ins,get,lst}.sql`,
  `dbo.quarantine_entry_ins.sql`, `dbo.task_queue_claim.sql` (if not already introduced by a
  concurrently-implemented spec — check before authoring a duplicate).

No data migration — both tables are new, empty at deploy time.

---

## §6 API Endpoints

**Auth (all three endpoints):** `[Authorize(Policy = "CachetAuthenticated")]` (CAS-002 §6.4) — a
plain authenticated session, no B2-capability-specific policy. Per CAS-001 §6.1: cross-check reads
use the system-level `IB2Client` (read-only capability is sufficient for `b2_get_file_info`/
`b2_list_file_names`), so a "Read Only" user still gets fully B2-confirmed search results; there is
no BRD basis for gating Keyword Search itself by capability level.

### Endpoint: `POST /search` **[queued, audited]**

**Async:** Yes — returns immediately with a task reference (FR-18); never executes the search
inline within the request.

**Request:**
```json
{
  "term": "confidential settlement",
  "bucket": "cachet-archive-prod",
  "extension": "All"
}
```
- `term` — required, non-empty after trim, max 200 characters (confirmed 2026-09-22, R. Devarajan
  — see §15).
- `bucket` — required, must equal the configured `B2:BucketName` (CAS-001 §9) exactly (confirmed
  2026-09-22, R. Devarajan — see §15; CAS-001's architecture assumes a single target archive
  bucket, so this field is validated rather than treated as a free multi-bucket choice).
- `extension` — one of `All | PDF | DOC | DOCX | XLS | XLSX | TXT` (FR-04's expanded list).

**Response `202 Accepted`:**
```json
{
  "taskId": 100234,
  "type": "KeywordSearch",
  "status": "Queued",
  "submittedAt": "2026-09-09T14:05:00Z"
}
```

**Sequence:** validate → insert `dbo.task_queue` (`task_type = 'KeywordSearch'`,
`request_parameters` = serialized `KeywordSearchRequestDto`) → write audit row
(`action: KeywordSearchSubmitted`, `tab_module: 'Keyword Search'`, `task_queue_id` populated) →
return `202`. Audit write happens **before** the response (§12; CLAUDE.md's binding rule — see
the note in §12 on why this is audited despite the API design doc's §4.1 not carrying an explicit
`[audited]` tag).

**Status codes:**
| Code | Condition |
|------|-----------|
| 202 | Accepted — task queued |
| 422 | `validation-failed` — empty/oversized `term`, `bucket` not the configured bucket, or `extension` not in the allowed list |
| 401 | No valid session |

### Endpoint: `GET /search/{taskId}/results`

**Async:** No — synchronous read of already-computed results; the B2 cross-check itself already
happened once, inside the Worker's task processing (§6.3 note below), not on every call to this
endpoint.

**Request:** path param `taskId` (int); optional query params `page`, `pageSize` (default `1`/`50`,
§1.4).

**Response `200 OK`** (task `Completed`):
```json
{
  "taskId": 100234,
  "status": "Completed",
  "filenameMatches": {
    "items": [
      { "fileId": 100501, "name": "agreement-004.pdf", "path": "contracts/2019/agreement-004.pdf",
        "size": 482113, "modifiedAt": "2019-06-12T00:00:00Z", "status": "Flagged" }
    ],
    "page": 1, "pageSize": 50, "totalCount": 1
  },
  "contentMatches": {
    "items": [
      { "fileId": 100502, "name": "meeting-notes-q3.docx", "path": "internal/notes/meeting-notes-q3.docx",
        "snippet": "...regarding the confidential settlement reached in...",
        "modifiedAt": "2021-02-01T00:00:00Z", "status": "Needs Review" }
    ],
    "page": 1, "pageSize": 50, "totalCount": 1
  }
}
```

**Response `200 OK`** (task `Failed`): same envelope with `"status": "Failed"`,
`filenameMatches`/`contentMatches` omitted, plus a `failureReason` string field (proposed — not
BRD-specified; needed so the client can render *something* for a failed task rather than an
ambiguous empty success).

**Sequence:** look up `dbo.task_queue` by `taskId` + `task_type = 'KeywordSearch'`; if `Queued`/
`Running`, return `409`; if `Completed`, call `dbo.search_result_lst` twice (once per
`match_type`) and project into the two panels; if `Failed`, return the `200`/`failureReason` shape
above. No cross-check, no write, no audit — pure read.

**Ownership scope (confirmed 2026-09-22, R. Devarajan — see §15):** not restricted to the
submitting user — any authenticated user may view any completed `KeywordSearch` task's results,
consistent with FR-19's Task Manager listing tasks across all users on this small (max 4 account)
internal team.

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | `Completed` (with matches) or `Failed` (with `failureReason`) |
| 404 | `task-not-found` — unknown `taskId`, or `taskId` belongs to a different `task_type` |
| 409 | `task-not-complete` — task exists but is `Queued`/`Running` (API spec §4.2) |
| 401 | No valid session |

### Endpoint: `POST /search/results/quarantine` **[audited]**

**Async:** No — synchronous, cataloging-only write (FR-15's framing applies here even though FR-15
literally describes `POST /quarantine`; §0/§4 note this endpoint writes the same table via the
same insert path).

**Request:**
```json
{
  "taskId": 100234,
  "fileIds": [100501, 100502]
}
```
`fileIds` are `search_result_id` values scoped to `taskId` (§1.7). The server resolves each to its
`file_index_id` before writing `dbo.quarantine_entry`.

**Response `200 OK`** — **(design decision, per API spec §6.2's own recommended alternative):**
returns just the newly added row(s), not the full Quarantine list (which this spec does not
implement a `GET` for — §0/§4):
```json
{
  "items": [
    { "fileId": 100801, "name": "agreement-004.pdf", "path": "contracts/2019/agreement-004.pdf",
      "quarantinedOn": "2026-09-09T14:10:00Z", "status": "Flagged" }
  ]
}
```
`fileId` here (`100801`) is the file's **stable identity** (`quarantine_entry_id`), a different
value than the `search_result_id` (`100501`) supplied in the request — §1.7 semantics, reused
verbatim.

**Sequence:** for each `fileId`, resolve `search_result` row scoped to `taskId` → `file_index_id`
+ `match_status`; insert `dbo.quarantine_entry` (`status = 'Active'`, `match_status_snapshot` =
the resolved `match_status`); write one audit row per batch (`action: QuarantineAdd`,
`tab_module: 'Keyword Search'`) **before** returning `200` (§12). Each file in the batch is
attempted independently, consistent with the batch-processing precedent set by API spec §6.4 —
one file's `409` (already quarantined) does not abort the others; the response's `items` array
reflects only the files actually added, and a partial-failure case returns `200` with a shorter
`items` array plus a `warnings` array naming the skipped `fileId`s and why (confirmed 2026-09-22,
R. Devarajan — see §15; same partial-success pattern as Permanent Delete, API spec §6.4).

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | At least one file added (see partial-failure note above) |
| 404 | `task-not-found` — unknown `taskId` |
| 404 | `file-not-found` — a `fileId` doesn't correspond to a match in that task |
| 409 | `file-already-quarantined` — every requested `fileId` already has an `Active` quarantine entry (new error `type`, introduced by this spec since it owns the filtered-unique-index constraint that produces it) |
| 422 | `validation-failed` — empty `fileIds` |
| 401 | No valid session |

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `POST /search` `term` must be non-empty after trim and ≤ 200 characters | 422 `validation-failed` | `Test_V1_SearchTermRequiredAndBounded` |
| V2 | `POST /search` `bucket` must equal the configured `B2:BucketName` exactly (confirmed 2026-09-22, R. Devarajan — §15) | 422 `validation-failed` | `Test_V2_SearchBucketMustMatchConfiguredBucket` |
| V3 | `POST /search` `extension` must be one of `All\|PDF\|DOC\|DOCX\|XLS\|XLSX\|TXT` | 422 `validation-failed` | `Test_V3_SearchExtensionMustBeAllowedValue` |
| V4 | `GET .../results` `taskId` must exist and have `task_type = 'KeywordSearch'` | 404 `task-not-found` | `Test_V4_ResultsUnknownOrWrongTypeTaskIdReturns404` |
| V5 | `GET .../results` on a `Queued`/`Running` task returns `409`, not partial/empty `200` | 409 `task-not-complete` | `Test_V5_ResultsBeforeCompletionReturns409` |
| V6 | `POST .../quarantine` `fileIds` must be non-empty | 422 `validation-failed` | `Test_V6_QuarantineEmptyFileIdsRejected` |
| V7 | `POST .../quarantine` every `fileId` must resolve to a `search_result` row scoped to the given `taskId` | 404 `file-not-found` | `Test_V7_QuarantineFileIdNotInTaskReturns404` |
| V8 | `POST .../quarantine` a `fileId` whose file already has an `Active` `quarantine_entry` is rejected for that file only, not the whole batch (confirmed 2026-09-22, R. Devarajan — §15; same partial-success pattern as Permanent Delete, API spec §6.4); enforced at the application layer before the insert to produce a clean error rather than a raw constraint-violation exception | 409 `file-already-quarantined` for that file (whole-request `409` only if every requested file fails — §6) | `Test_V8_QuarantineAlreadyActiveFileRejected` |
| V9 | Worker: a `search_result` row is never inserted for a candidate that fails the B2 cross-check (CAS-001 §6.3) | N/A — enforced by not inserting; proven by absence | `Test_V9_CrossCheckExcludedCandidateNeverPersisted` |

---

## §8 UI / UX

N/A — `touches.ui: false`. No `.razor` components; the Web project is untouched by this spec. BRD
UI/UX spec §4 (Keyword Search screen) exists for reference but its implementation is a separate,
future Web-kind spec that consumes these three endpoints.

---

## §9 Configuration Changes

| Key | Default | Description | Env Override |
|-----|---------|-------------|---------------|
| `B2:CrossCheckMaxConcurrency` | `8` (tune empirically against the account/bucket, per CAS-001 §6.3 — no documented B2 rate limit to derive a number from) | Bounded concurrency for the Worker's per-match `b2_get_file_info` cross-check calls (CAS-001 §6.3's `SemaphoreSlim`-style batching) | Yes |
| `Search:MaxTermLength` | `200` | Server-side cap on `POST /search`'s `term` field (V1) — confirmed 2026-09-22, R. Devarajan (§15) | Yes |

No new B2 credential keys — this spec reuses `B2:SystemKeyId`/`B2:SystemApplicationKey`/
`B2:BucketName`, already introduced by CAS-001 and never touched here.

---

## §10 Success Criteria

**12 SCs, at the P9 size-threshold boundary (>12 triggers a split check) — deliberately kept as-is,
not trimmed:** each covers a distinct, independently-testable behavior (submission, per-field
validation, cross-check, self-heal, completion, results retrieval in each state, quarantine-add,
duplicate-quarantine, log hygiene, claim concurrency). Combined with §17's decomposition check
(module fan-out = 1 real signal, not 2+), this alone does not warrant splitting the spec; removing
any one SC would leave a real behavior unverified rather than reduce genuine spec size.

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `POST /search` with a valid body returns `202` + `taskId`; a `dbo.task_queue` row exists with `task_type = 'KeywordSearch'`, `status = 'Queued'`; the `KeywordSearchSubmitted` audit row exists with `created_at` at or before the response was sent | `Test_SC1_SearchSubmissionQueuesTaskAndAuditsBeforeResponse` |
| SC-2 | `POST /search` with an empty `term` returns `422` and no `task_queue` row is inserted | `Test_SC2_InvalidSearchRequestInsertsNoTask` |
| SC-3 | Given a claimed `KeywordSearch` task, the Worker processor queries `dbo.file_index` scoped to the task's `bucket`/`extension`, cross-checks every local candidate against B2 (mocked `IB2Client` in the unit test), and inserts one `dbo.search_result` row per confirmed match with the correct `match_type` | `Test_SC3_WorkerProcessorProducesConfirmedSearchResults` |
| SC-4 | A candidate whose B2 cross-check returns `404`/hidden is excluded from `dbo.search_result`, the corresponding `dbo.file_index` row is soft-deleted (`is_active = 0`), and a `CrossCheckExcluded` audit row is written for it | `Test_SC4_StaleCandidateSelfHealsAndExcludes` |
| SC-5 | A `KeywordSearch` task reaches `status = 'Completed'` with `completed_at` populated once the Worker finishes processing, even when zero matches are found | `Test_SC5_TaskCompletesEvenWithZeroMatches` |
| SC-6 | `GET /search/{taskId}/results` on a `Completed` task returns `200` with `filenameMatches`/`contentMatches` correctly paginated per §1.4's envelope | `Test_SC6_CompletedTaskReturnsPaginatedPanels` |
| SC-7 | `GET /search/{taskId}/results` on a `Queued`/`Running` task returns `409 task-not-complete` | `Test_SC7_IncompleteTaskReturns409` |
| SC-8 | `GET /search/{taskId}/results` with an unknown `taskId` returns `404 task-not-found` | `Test_SC8_UnknownTaskIdReturns404` |
| SC-9 | `POST /search/results/quarantine` with valid `fileIds` inserts `Active` `dbo.quarantine_entry` rows, writes a `QuarantineAdd` audit row before the response, and returns the newly added rows | `Test_SC9_QuarantineAddInsertsAndAuditsBeforeResponse` |
| SC-10 | `POST /search/results/quarantine` on a file already actively quarantined returns `409 file-already-quarantined` and inserts no duplicate row (proves the filtered unique index is respected, not merely relied upon to throw) | `Test_SC10_DuplicateQuarantineRejectedNotDoubleInserted` |
| SC-11 | No B2 application key, session token/cookie value, or raw file content ever appears in a log line across `POST /search`, `GET .../results`, `POST .../quarantine`, or the Worker processor | `Test_SC11_NoCredentialOrContentLeakageInLogs` |
| SC-12 | Two concurrent Worker instances claiming `KeywordSearch` tasks never both claim the same `task_queue` row (reuses CAS-003 SC-3's `READPAST`/`UPDLOCK` claim pattern, now exercised end-to-end for a real task type) | `Test_SC12_ConcurrentWorkersNeverDoubleClaimSameTask` |

---

## §11 Telemetry

```csharp
_logger.LogInformation("KeywordSearch task {TaskId} submitted by user {UserId}", taskId, userId);
_logger.LogInformation(
    "KeywordSearch task {TaskId} completed: {FilenameMatchCount} filename / {ContentMatchCount} content matches, {ExcludedCount} excluded",
    taskId, filenameMatchCount, contentMatchCount, excludedCount);
_logger.LogWarning("KeywordSearch task {TaskId} failed: {ErrorSummary}", taskId, errorSummary);
```

- Never log: the B2 application key or any part of it, a session token/cookie value, full file
  content, the raw `content_text` full-text field, or the user-entered `term` itself (a search
  term can be sensitive/privileged text — log only counts and IDs, never the query string).
- File paths logged, if any, are truncated the same way the audit log's own `file_path` column
  already captures them — no additional exposure beyond what FR-23 already records.

---

## §12 Security Notes

### B2 Credential Handling
The Worker's/Api's cross-check calls (§6, CAS-001 §6.3) use the **system-level** `IB2Client`
(CAS-001 §6.1) — the server-held system key, never an individual user's login-validation key
(`IUserB2KeyAuthorizer` is login-only, per CAS-002; it is never invoked from this spec's code
paths). No B2 key or key ID is ever present in any request/response DTO defined in §6, and none is
logged (§11).

### Audit Trail
Two audited writes, both before their response (FR-23):
- `POST /search` — `action: KeywordSearchSubmitted`, `tab_module: 'Keyword Search'`,
  `task_queue_id` populated, `result: Success`, before `202`.
- `POST /search/results/quarantine` — `action: QuarantineAdd`, `tab_module: 'Keyword Search'`,
  `file_path` populated per added file (one audit row per batch, or one per file — implementer's
  choice, either satisfies FR-23's field list), before `200`.

Plus one **system-corrective** audit write inside the Worker (not tied to any HTTP response, but
still before the task is marked `Completed`, per CAS-001 §6.4's analogous pattern):
`action: CrossCheckExcluded`, `tab_module: 'Keyword Search'`, attributed to the user who submitted
the task, for every candidate the B2 cross-check excludes (§6, §7 V9).

**Note on the API design doc's own gap:** `CACHET_API_Design_Specification_v1.md` §4.1 does not
carry an explicit `**[audited]**` tag on `POST /search` the way §3.1 (Login) and §6.2 (Quarantine
add) do. This spec treats `POST /search` as audited anyway, because CLAUDE.md's own binding rule
is explicit: *"Any state-changing endpoint (quarantine add, permanent delete, search/scan
submission) must write to the audit log (FR-23) before returning success."* Per this project's
stated skill precedence (Spec > Security rules > Karpathy principles > local conventions),
CLAUDE.md's security rule overrides the API design doc's incomplete tagging — flagged here rather
than silently resolved, per the "never invent requirements, flag what you don't know" rule (this
is not inventing a requirement; it's reconciling two source documents in favor of the higher-
precedence one).

### Session / Auth Requirements
`[Authorize(Policy = "CachetAuthenticated")]` on all three endpoints (CAS-002 §6.4) — a valid
session only, no B2-capability-specific policy (§6's rationale). Not blocked by BRD §12.2 — the
login method underlying `CachetAuthenticated` is already resolved (CAS-002), and drive
encryption-at-rest (the other §12.2 open item) has no bearing on this API surface.

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `SearchRequestValidatorTests` | `Test_V1_SearchTermRequiredAndBounded`, `Test_V2_SearchBucketMustMatchConfiguredBucket`, `Test_V3_SearchExtensionMustBeAllowedValue` | V1–V3 |
| `KeywordSearchTaskProcessorTests` (NSubstitute `IB2Client`, `IFileIndexRepository`, `ISearchResultRepository`, `IAuditLogService`) | `Test_SC3_WorkerProcessorProducesConfirmedSearchResults`, `Test_SC4_StaleCandidateSelfHealsAndExcludes`, `Test_SC5_TaskCompletesEvenWithZeroMatches`, `Test_V9_CrossCheckExcludedCandidateNeverPersisted` | SC-3, SC-4, SC-5, V9 |
| `QuarantineAddServiceTests` (NSubstitute `IQuarantineEntryRepository`, `ISearchResultRepository`, `IAuditLogService`) | `Test_V6_QuarantineEmptyFileIdsRejected`, `Test_V7_QuarantineFileIdNotInTaskReturns404`, `Test_V8_QuarantineAlreadyActiveFileRejected` | V6–V8 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `SearchControllerTests` (`WebApplicationFactory`) | `Test_SC1_SearchSubmissionQueuesTaskAndAuditsBeforeResponse`, `Test_SC2_InvalidSearchRequestInsertsNoTask`, `Test_SC6_CompletedTaskReturnsPaginatedPanels`, `Test_SC7_IncompleteTaskReturns409`, `Test_SC8_UnknownTaskIdReturns404`, `Test_V4_ResultsUnknownOrWrongTypeTaskIdReturns404`, `Test_V5_ResultsBeforeCompletionReturns409` | SC-1, SC-2, SC-6, SC-7, SC-8, V4, V5 |
| `SearchResultRepositoryTests` (real SQL Server) | Insert/list round-trip against `dbo.search_result`, unique constraint on `(task_queue_id, file_index_id, match_type)` | Supports SC-3 |
| `QuarantineEntryRepositoryTests` (real SQL Server) | `Test_SC9_QuarantineAddInsertsAndAuditsBeforeResponse`, `Test_SC10_DuplicateQuarantineRejectedNotDoubleInserted` | SC-9, SC-10 |
| `TaskQueueClaimConcurrencyTests` (real SQL Server, parallel claim attempts) | `Test_SC12_ConcurrentWorkersNeverDoubleClaimSameTask` | SC-12 |
| Log-output assertion (test log sink) across all of the above | `Test_SC11_NoCredentialOrContentLeakageInLogs` | SC-11 |

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. Deploy the database migration first (`dbo.search_result`, `dbo.quarantine_entry`, and the
   stored procedures in §5) — additive-only, no existing table altered.
2. Deploy `Core` (new DTOs/enum members), `Api`, and `Worker` **together** — `POST /search` will
   queue tasks that never complete if the Worker's `KeywordSearch` processor isn't live yet
   (FR-18's whole point is asynchronous execution, but "asynchronous" still requires *something*
   eventually processing the queue).
3. Rollback: both new tables are additive and empty until this feature is used — dropping them is
   safe pre-traffic. Post-traffic, follow the same "never hard-delete audit-adjacent data" posture
   as CAS-003/CAS-004 (do not drop `search_result`/`quarantine_entry` once rows exist; roll back
   code only, leave schema in place).

---

## §15 Open Questions

- [x] **Bucket validation (V2) — resolved 2026-09-22 (R. Devarajan, TraceCounts):**
      `POST /search`'s `bucket` field must equal the single configured `B2:BucketName` exactly
      (422 `validation-failed` otherwise) — consistent with CAS-001's single-target-bucket
      architecture. The API design doc's own example (`"bucket": "cachet-archive-prod"`) is read
      as illustrative, not evidence of an intended multi-bucket selector; nothing else in this
      project's confirmed design (CAS-001) supports more than one bucket today. If multi-bucket
      support becomes a real near-term requirement, loosening this validation is a small, isolated
      change — not a reason to leave it unvalidated now.
- [x] **`GET /search/{taskId}/results` ownership scope — resolved 2026-09-22 (R. Devarajan,
      TraceCounts):** not restricted to the submitting user — any authenticated user may view any
      completed task's results, consistent with FR-19's Task Manager already listing tasks across
      all users on this small (max 4 account) internal team. Restricting visibility here would
      introduce a permissions concept the BRD never describes, and would sit awkwardly next to a
      Task Manager screen that already shows the task to everyone.
- [x] **Partial-failure response shape for `POST /search/results/quarantine` — resolved
      2026-09-22 (R. Devarajan, TraceCounts):** files that succeed are added; a file that's
      already actively quarantined is skipped, not treated as a reason to fail the whole batch.
      The response returns `200` with the successfully added rows plus a `warnings` array naming
      any skipped `fileId`s and why — the same partial-success pattern already established for
      Permanent Delete (API spec §6.4), kept consistent across the app rather than introducing an
      all-or-nothing rule unique to this endpoint.
- [x] **`Search:MaxTermLength` = 200 — resolved 2026-09-22 (R. Devarajan, TraceCounts):** accepted
      as the default. This is a technical guardrail (bounding query cost/size), not a business
      rule, and 200 characters comfortably covers any realistic filename, phrase, or short snippet
      a reviewer would search for. Can be revisited via a simple config change if it proves too
      restrictive in practice.
- Not blocked by either open BRD §12.2 item (login method already resolved; drive
  encryption-at-rest is unrelated to this API surface) — confirmed not applicable, not carried
  forward as a blocker.

All open items resolved as of 2026-09-22 — no remaining blockers to `status → ready`.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 | |
| Reviewer | | | Pending |

---

## §17 Graph Impact

Graph exists for this repo (`graphify-out/`, built 2026-09-10, refreshed since) and indexes the
BRD/WBS/API-spec/decision-log documentation corpus (no application code nodes exist yet for this
feature — Api/Worker code for Keyword Search has not been written). Queried live:

```
python -m graphify query "Keyword Search API POST /search local-first B2 cross-check FR-08" --budget 1500
```

### God Nodes in Blast Radius
None of the graph's top-10 numeric god nodes (all `graphify` tool internals — `main()`,
`_make_id()`, `B2Client`, etc. — see `GRAPH_REPORT.md`) are BRD/spec nodes this feature touches.
Within the documentation graph, `FR-08: B2 API Cross-Check of Matches` (Community 21) is the
highest-degree FR-node this spec's blast radius passes through, bridging to `FR-05: Local-First
Search`, `Task 10: B2 Integration Design`, `Task 32: B2 Cross-Check Confirmation Logic`, and `BRD
Section 3(c): Dual Local Cache with API-Confirmed Search` — confirming CAS-001 §6.3 is the correct
cross-check design to build against (§6, §12), not a re-derivation.

### Communities / Modules Affected
- **Community 21** ("Keyword Search API surface") — `POST /search`, `GET /search/{taskId}/
  results`, `FR-04`–`FR-08`, `WBS Task 16: Keyword Search API` (confirming this spec is the API
  implementation of WBS Task 16), `Task 32: B2 Cross-Check Confirmation Logic`.
- **Community 25** ("Quarantine") — `POST /search/results/quarantine`, `FR-09`, `Quarantine
  Workflow (Keyword-Search-Only)`, `Step 7: Quarantine Flagging` (Data-Flow Analysis) — confirms
  FR-09's quarantine-add path is this spec's concern, while the rest of Community 25 (`GET
  /quarantine`, `POST /quarantine/release`, `POST /quarantine/delete`, FR-14–FR-17) is correctly
  out of scope (§4).
- **Community 46** — `API spec §4: Keyword Search Module (FR-04-FR-09)`,
  `Karpathy Principle P9: Split before you bloat`, `§2.2 Module fan-out (2+ of 7 modules signal)`,
  `WBS Task 16: Keyword Search API` — the graph itself co-locates this feature with the P9
  decomposition-check nodes, which is exactly the check performed below.
- Projects touched: `Api` (controller/service), `Worker` (new `KeywordSearch` task processor —
  first task type actually implemented; `Worker.cs` is currently still the scaffold template),
  `Core` (new DTOs/enum members), `Database` (two new tables). **Not** `Web`.

### Surprising Connections
- The graph's live query surfaces `Self-Healing Local Mirror Correction` and `Step 5: B2 API
  Cross-Check of Candidate Match` (both from `CACHET_DataFlow_Analysis_v1.md`) directly alongside
  `FR-08`, independently corroborating CAS-001 §6.3's design (no new information, but confirms no
  drift between CAS-001 and the Data-Flow Analysis document it was drafted from).
- The already-known AMBIGUOUS edge `FR-18: Search/Scan Submitted to Task Queue` →
  `POST /tasks/{taskId}/cancel` (GRAPH_REPORT.md) falls just outside this spec's actual scope
  (Task Manager's cancel endpoint, §4) — noted so a future Task Manager spec resolves it, not
  silently absorbed here.
- **Decomposition check (P9) result:** module fan-out is 2 (Keyword Search + the Quarantine-add
  sliver of FR-09) — not 2+ *materially different* validation/security postures, since FR-09's add
  path is explicitly documented (API spec §4.3/§6.2) as functionally identical to, and recommended
  to share code with, Quarantine's own add endpoint; keeping it in this spec avoids the "two
  parallel add-to-quarantine code paths" the API design doc itself warns against. No size
  threshold in §2.3 is crossed (3 endpoints, 9 validations, 12 SCs at the boundary but not over,
  2 new tables, ~8 stored procedures). **Decision: single spec, not split** — the one borderline
  signal (estimated changed files, given this is also the first Worker task-processor and the
  first real `task_queue_claim` procedure) is one signal alone, not the 2-or-more the guideline
  requires to force a split.

### Graphify Queries Run
```
python -m graphify query "Keyword Search API POST /search local-first B2 cross-check FR-08" --budget 1500
```
(`--budget` truncated full output; the God Nodes/Communities/Surprising Connections sections above
also draw on the static `graphify-out/GRAPH_REPORT.md`, build 2026-09-10.)

---

## §18 Verification Plan

### Reviewer Checklist
The reviewer will independently verify each of the following. Mark N/A only if the section itself
is N/A.

- [ ] All §10 Success Criteria (SC-1–SC-12) have a passing test.
- [ ] All §7 Validations (V1–V9) are implemented and tested.
- [ ] All three §6 API endpoints exist with the correct verb/route/`[Authorize(Policy =
      "CachetAuthenticated")]`.
- [ ] `POST /search` writes the `KeywordSearchSubmitted` audit row **before** returning `202`
      (§12's explicit note that this is audited despite the API design doc's own tagging gap).
- [ ] `POST /search/results/quarantine` writes the `QuarantineAdd` audit row **before** returning
      `200`.
- [ ] The Worker's cross-check never persists a `dbo.search_result` row for a candidate B2 no
      longer has, and writes `CrossCheckExcluded` for each exclusion (§7 V9, §12).
- [ ] Cross-check calls use the system-level `IB2Client` only — never `IUserB2KeyAuthorizer`, never
      a per-request/browser-supplied key (§12 B2 Credential Handling).
- [ ] §5 `dbo.search_result` and `dbo.quarantine_entry` exist in schema exactly as specified,
      including the filtered unique index on `quarantine_entry(file_index_id) WHERE status =
      'Active'`.
- [ ] §9 config keys (`B2:CrossCheckMaxConcurrency`, `Search:MaxTermLength`) are present in
      `appsettings.json`/`appsettings.Development.json` with placeholder, non-secret values.
- [ ] §11 Telemetry events are emitted, and none logs the B2 key, a session token, raw file
      content, or the user-entered search term.
- [ ] §17 Graph Impact matches actual code changes — no unintended nodes (e.g., no accidental
      `Web`/UI changes, no `GET /quarantine`/`/release`/`/delete` endpoints added).
- [ ] No out-of-scope changes from §4 were made (no Task Manager generic endpoints, no O-Drive
      Sync code, no Quarantine release/delete).

### Verification Report Format
Per template: `CAS-006.review.md` with overall verdict, per-SC verdict table, security gap list,
scope violations.

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage SearchController 100%, KeywordSearchService ~97-100%, QuarantineService ~97-100%, Worker.cs ~94-100% (repositories 0% — no live SQL Server in sandbox, disclosed precedent per CAS-005) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20260922_170000__CAS-006-keyword-search-api.sql` | New — dbo.search_result, dbo.quarantine_entry, search_result/quarantine_entry/task_queue/audit_log procs |
| `Database/migrations/20260922_173000__CAS-006-keyword-search-api-addendum.sql` | New — dbo.task_queue_get, dbo.file_index_search, dbo.file_index_soft_delete_upd |
| `Database/CachetArchiveSync/Entities/dbo.search_result.sql` | New entity mirror |
| `Database/CachetArchiveSync/Entities/dbo.quarantine_entry.sql` | New entity mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.search_result_ins.sql` | New |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.search_result_get.sql` | New |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.search_result_lst.sql` | New |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_ins.sql` | New |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_ins.sql` | New — first author |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_claim.sql` | New — READPAST/UPDLOCK claim pattern |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_upd.sql` | New |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_get.sql` | New — addendum |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql` | New — addendum, local-first query |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_soft_delete_upd.sql` | New — addendum, self-heal |
| `src/Cachet.ArchiveSync.Core/Dtos/SearchRequest.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/SearchSubmittedResponse.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/FilenameMatchItem.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/ContentMatchItem.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/PagedResult.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/SearchResultsResponse.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineAddRequest.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineAddResponseItem.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineAddResponse.cs` | New |
| `src/Cachet.ArchiveSync.Core/Dtos/KeywordSearchRequestDto.cs` | New — task_queue.request_parameters payload |
| `src/Cachet.ArchiveSync.Core/Dtos/KeywordSearchResultSummaryDto.cs` | New — task_queue.result_detail payload |
| `src/Cachet.ArchiveSync.Core/Dtos/SearchResult.cs` | New entity |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineEntry.cs` | New entity |
| `src/Cachet.ArchiveSync.Core/Dtos/SearchResultRow.cs` | New repository projection |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskQueueRow.cs` | New repository projection |
| `src/Cachet.ArchiveSync.Core/Dtos/ClaimedTaskRow.cs` | New repository projection |
| `src/Cachet.ArchiveSync.Core/Dtos/FileIndexCandidateRow.cs` | New repository projection |
| `src/Cachet.ArchiveSync.Core/Interfaces/ISearchResultRepository.cs` | New |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineEntryRepository.cs` | New |
| `src/Cachet.ArchiveSync.Core/Interfaces/ITaskQueueRepository.cs` | New |
| `src/Cachet.ArchiveSync.Core/Interfaces/IFileIndexRepository.cs` | New |
| `src/Cachet.ArchiveSync.Core/Interfaces/IKeywordSearchService.cs` | New |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineService.cs` | New |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | Modified — appended KeywordSearchSubmitted, QuarantineAdd |
| `src/Cachet.ArchiveSync.Api/Repositories/SearchResultRepository.cs` | New |
| `src/Cachet.ArchiveSync.Api/Repositories/QuarantineEntryRepository.cs` | New |
| `src/Cachet.ArchiveSync.Api/Repositories/TaskQueueRepository.cs` | New |
| `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs` | New |
| `src/Cachet.ArchiveSync.Api/Repositories/SqlDateTimeMapping.cs` | New — shared DATETIME2 mapping helper |
| `src/Cachet.ArchiveSync.Api/Services/KeywordSearchService.cs` | New |
| `src/Cachet.ArchiveSync.Api/Services/QuarantineService.cs` | New |
| `src/Cachet.ArchiveSync.Api/Controllers/SearchController.cs` | New — POST /search, GET /search/{taskId}/results, POST /search/results/quarantine |
| `src/Cachet.ArchiveSync.Worker/IWorkerTaskProcessor.cs` | New — extensibility seam for task-type dispatch |
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs` | New — claim, local-first query, B2 cross-check, self-heal, insert search_result |
| `src/Cachet.ArchiveSync.Worker/Worker.cs` | Rewritten — general claim-loop skeleton dispatching by task_type |
| `src/Cachet.ArchiveSync.Api/Program.cs` | Modified — DI registrations |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | Modified — DI registrations |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | Modified — B2:CrossCheckMaxConcurrency, Search:MaxTermLength |
| `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | Modified — B2:CrossCheckMaxConcurrency, Search:MaxTermLength |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `KeywordSearchServiceTests` | `Test_V1_SearchTermRequiredAndBounded` | V1 |
| `KeywordSearchServiceTests` | `Test_V2_SearchBucketMustMatchConfiguredBucket` | V2 |
| `KeywordSearchServiceTests` | `Test_V3_SearchExtensionMustBeAllowedValue` | V3 |
| `KeywordSearchServiceTests` | `Test_SC1_SearchSubmissionQueuesTaskAndAuditsBeforeResponse` | SC-1 |
| `KeywordSearchServiceTests` | `Test_SC2_InvalidSearchRequestInsertsNoTask` | SC-2 |
| `QuarantineServiceTests` | `Test_V6_QuarantineEmptyFileIdsRejected` | V6 |
| `QuarantineServiceTests` | `Test_V7_QuarantineFileIdNotInTaskReturns404` | V7 |
| `QuarantineServiceTests` | `Test_V8_QuarantineAlreadyActiveFileRejected` | V8 |
| `QuarantineServiceTests` | `Test_SC9_QuarantineAddInsertsAndAuditsBeforeResponse` | SC-9 |
| `QuarantineServiceTests` | `Test_SC10_DuplicateQuarantineRejectedNotDoubleInserted` | SC-10 |
| `LogHygieneTests` | `Test_SC11_NoCredentialOrContentLeakageInLogs` | SC-11 (Api) |
| `KeywordSearchTaskProcessorTests` | `Test_SC3_WorkerProcessorProducesConfirmedSearchResults` | SC-3 |
| `KeywordSearchTaskProcessorTests` | `Test_SC4_StaleCandidateSelfHealsAndExcludes` | SC-4 |
| `KeywordSearchTaskProcessorTests` | `Test_SC5_TaskCompletesEvenWithZeroMatches` | SC-5 |
| `KeywordSearchTaskProcessorTests` | `Test_V9_CrossCheckExcludedCandidateNeverPersisted` | V9 |
| `KeywordSearchTaskProcessorTests` | `Test_SC11_NoCredentialOrContentLeakageInLogs` | SC-11 (Worker) |
| `SearchControllerTests (IntegrationTests)` | `Test_SC6_CompletedTaskReturnsPaginatedPanels` | SC-6 |
| `SearchControllerTests (IntegrationTests)` | `Test_SC7_IncompleteTaskReturns409` | SC-7 |
| `SearchControllerTests (IntegrationTests)` | `Test_SC8_UnknownTaskIdReturns404` | SC-8 |
| `SearchControllerTests (IntegrationTests)` | `Test_V4_ResultsUnknownOrWrongTypeTaskIdReturns404` | V4 |
| `SearchControllerTests (IntegrationTests)` | `Test_V5_ResultsBeforeCompletionReturns409` | V5 |
| `SearchControllerTests (Api.Tests, coverage top-up)` | `12 controller-level tests (202/200/404/409/422/401)` | Controller branch coverage |
| `SearchResultRepositoryTests (IntegrationTests)` | `Insert/list/unique-constraint (Skip — no live SQL Server)` | SC-3 support |
| `QuarantineEntryRepositoryTests (IntegrationTests)` | `Test_SC9/Test_SC10 (Skip — no live SQL Server)` | SC-9, SC-10 DB-level |
| `TaskQueueClaimConcurrencyTests (IntegrationTests)` | `Test_SC12_ConcurrentWorkersNeverDoubleClaimSameTask (Skip — no live SQL Server)` | SC-12 |
| `WorkerTests` | `Test_ExecuteAsync_ClaimedTask_DispatchesToMatchingProcessor` | Claim-loop dispatch |
| `WorkerTests` | `Test_ExecuteAsync_NoTaskClaimed_WaitsWithoutThrowing` | Claim-loop idle path |
| `WorkerTests` | `Test_ExecuteAsync_NoProcessorForTaskType_LogsWarningAndContinues` | Claim-loop unmatched type |
| `WorkerTests` | `Test_ExecuteAsync_ProcessorThrows_CatchesLogsAndKeepsLoopAlive` | Claim-loop failure isolation |
| `WorkerTests` | `Test_ExecuteAsync_StoppingTokenCancelled_StopsLoopCleanly` | Claim-loop cancellation |

### Migration
`20260922_170000__CAS-006-keyword-search-api.sql, 20260922_173000__CAS-006-keyword-search-api-addendum.sql`
