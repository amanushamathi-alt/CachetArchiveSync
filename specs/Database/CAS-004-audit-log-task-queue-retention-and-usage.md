---
id: CAS-004
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
priority: P2
related_specs: [CAS-003]             # CAS-003 owns the DDL this spec documents usage/retention
                                      # against; this spec introduces no new columns, constraints,
                                      # or indexes — see §0 and §4.
touches:
  database: false                    # no schema change — documentation/usage addendum only
  api: false
  ui: false
  worker: false
  b2: false
  config: false
  security: true                     # audit-log immutability and retention are security-relevant
                                      # (FR-23/FR-24, BRD §9)
migrations: []
---

# CAS-004 — Audit Log and Task Queue: Indexing Rationale, Retention, and Lifecycle Usage

WBS Tasks 6/13 (carried forward), Task 19 (Task Manager), Task 24 (Audit Log). Documents FR-19,
FR-23, FR-24 against the schema `CAS-003` already merged. Architecture basis: BRD Sections 7.5,
7.6, 8, 9.

---

## §0 Provenance Note (read this first)

**`dbo.audit_log` and `dbo.task_queue` are already designed, implemented, and merged** in
[`CAS-003`](CAS-003-core-schema-foundation.md) §5 — migration
`Database/migrations/20260910_153624__CAS-003-core-schema-foundation.sql` and entity mirrors
`Database/CachetArchiveSync/Entities/dbo.{task_queue,audit_log}.sql` already exist on disk with
status `merged`. This spec was requested as a fresh "implementation spec" for these two tables;
per Karpathy P3/P4 (smallest diff, no re-deriving what already exists) and this project's
single-global-sequence spec numbering, it does **not** re-invent that DDL. Instead it:

1. Restates CAS-003's exact column list and indexes for these two tables (verbatim from the
   merged mirror files, not re-derived) so this document is self-contained for review.
2. Confirms — does not redesign — that the existing indexes already satisfy FR-24 (tab + date
   filtering) and FR-19 (status filtering for Task Manager).
3. Adds what CAS-003 does **not** contain: a retention-considerations note (deferred — see §15
   item 1 — the client confirmed 2026-09-22 that a retention policy is out of scope for the
   current implementation) and worked example `INSERT` statements for a typical audit event and a
   full task lifecycle.

If a future spec needs to *change* these tables' schema (new column, new index, retention-driven
partitioning, etc.), that is a change spec against `CAS-003`, not this one — see §4.

---

## §1 Summary

No schema change. This spec documents, for `dbo.audit_log` and `dbo.task_queue` as merged under
CAS-003: the exact column list, why the existing indexes satisfy FR-24/FR-19's filtering needs,
what is and isn't decided about retention (BRD §12.2), and reference `INSERT` statements a
repository implementation should follow for a typical audit event and a task's full lifecycle.
Both tables sit on this application's critical path — every module writes to `audit_log` (FR-23)
and every async action flows through `task_queue` (FR-18–FR-22a) — so getting usage conventions
right early, before repository code is written against them, reduces drift across modules.

---

## §2 User Story

As a **backend developer implementing any module's repository/service layer** (Keyword Search,
O-Drive Sync, Quarantine, Task Manager), I want an authoritative reference for exactly how to
insert into `dbo.audit_log` and how to drive `dbo.task_queue` through its status lifecycle, so
that every module's audit trail and task tracking stay consistent instead of each module
inventing its own convention.

---

## §3 In-Scope

- Restating `dbo.audit_log` and `dbo.task_queue`'s column lists, constraints, and indexes exactly
  as merged in CAS-003 (no changes).
- Confirming index-to-requirement mapping: FR-24 (audit log tab + date filtering), FR-19 (Task
  Manager status filtering).
- Retention considerations for both tables, explicitly flagged open per BRD §12.2.
- Worked example `INSERT` statements: one audit-log event, one full task lifecycle
  (Queued → Running → Completed, and the Failed variant).

---

## §4 Out-of-Scope

- **Any DDL change** to `dbo.audit_log` or `dbo.task_queue` (new column, new index, altered
  constraint) — that is a change spec against `CAS-003`, not this spec.
- **Stored procedure bodies** (`dbo.task_queue_ins/_upd/_get/_lst`, `dbo.audit_log_ins/_get/_lst`)
  — named in CAS-003 §5 but not authored there or here; written alongside the API endpoints that
  call them (WBS Tasks 15–20) or by `db-author` at `/implement` time.
- **A confirmed retention policy** — BRD §12.2 has not resolved this, and the client confirmed
  2026-09-22 that defining one is out of scope for the current implementation; see §15. This spec
  documents the *absence* of a policy and the schema-level implications, it does not invent one.
- **Actual archival/purge tooling** (e.g., a scheduled job to age out old `audit_log` rows) — would
  itself require a retention policy decision first; out of scope until a future spec revisits §15.
- **`dbo.users` and `dbo.file_index`** — covered by CAS-003; not this spec's concern.

---

## §5 Database Objects

### Tables / Columns

No new tables or columns. Restated verbatim from the merged mirrors for reference:

#### `dbo.task_queue` (unchanged from CAS-003)

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `task_queue_id` | `INT IDENTITY(100000,1)` | No | PK, clustered. |
| `created_by` | `NVARCHAR(50)` | No | Audit column. |
| `created_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()`. |
| `updated_by` | `NVARCHAR(50)` | No | Audit column. |
| `updated_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()`. |
| `task_type` | `NVARCHAR(20)` | No | `CHECK IN ('KeywordSearch','ContentScan','SyncCopy','SyncUpdate','SyncPurge')`. |
| `submitted_by_user_id` | `INT` | No | FK → `dbo.users(user_id)`. |
| `submitted_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()`. |
| `status` | `NVARCHAR(20)` | No | `CHECK IN ('Queued','Running','Completed','Failed','Cancelled')`, default `'Queued'`. |
| `started_at` | `DATETIME2(3)` | Yes | Set when a worker claims the row. |
| `completed_at` | `DATETIME2(3)` | Yes | Set on terminal status. |
| `request_parameters` | `NVARCHAR(MAX)` | Yes | JSON, task-type-specific input. |
| `is_cancel_requested` | `BIT` | No | Default `0`; worker polls this to cooperatively cancel. |
| `result_detail` | `NVARCHAR(MAX)` | Yes | JSON or free text summary of the outcome. |

Constraints: `PK_task_queue`, `FK_task_queue_users`, `CK_task_queue_task_type`,
`CK_task_queue_status`.

Indexes (both already merged, both satisfy FR-19 — see §5 Indexing Rationale below):
- `IX_task_queue_status_submitted_at (status, submitted_at)`
- `IX_task_queue_submitted_by_status (submitted_by_user_id, status)`

#### `dbo.audit_log` (unchanged from CAS-003)

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `audit_log_id` | `INT IDENTITY(100000,1)` | No | PK, clustered. |
| `created_by` | `NVARCHAR(50)` | No | Audit column — never mutated after insert. |
| `created_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()` — the event timestamp used for FR-24 date filtering. |
| `updated_by` | `NVARCHAR(50)` | No | Audit column — present for schema consistency; no `_upd` procedure is planned (§0). |
| `updated_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()`. |
| `user_id` | `INT` | No | FK → `dbo.users(user_id)` — the acting user. |
| `tab_module` | `NVARCHAR(30)` | No | `CHECK IN ('Auth','Keyword Search','O-Drive Sync','Quarantine','Task Manager','Audit Log','Reports')` — the FR-24 tab filter value. |
| `action` | `NVARCHAR(50)` | No | Free text (not a `CHECK` enum — action catalog still growing per CAS-003). |
| `file_path` | `NVARCHAR(1000)` | Yes | Populated when the action is file-scoped. |
| `task_queue_id` | `INT` | Yes | FK → `dbo.task_queue(task_queue_id)` — populated when the action is async. |
| `result` | `NVARCHAR(30)` | No | E.g. `'Success'`, `'Failure'` — action-catalog-defined, not `CHECK`-constrained. |
| `result_detail` | `NVARCHAR(MAX)` | Yes | Free text / JSON detail. |

Constraints: `PK_audit_log`, `FK_audit_log_users`, `FK_audit_log_task_queue`,
`CK_audit_log_tab_module`. No FK cascades into this table from either `dbo.users` or
`dbo.task_queue` (CAS-003 SC-5) — deactivating a user or a task can never silently remove audit
history.

Indexes (all three already merged, satisfy FR-24 — see Indexing Rationale below):
- `IX_audit_log_tab_module_created_at (tab_module, created_at)`
- `IX_audit_log_created_at (created_at)`
- `IX_audit_log_user_id (user_id)`

### Indexing Rationale (confirmation, not new design)

**FR-24 — Audit Log tab + date filtering:** The Audit Log screen's primary query shape is "rows
for tab X within date range [start, end]", optionally further filtered by user. `IX_audit_log_
tab_module_created_at (tab_module, created_at)` is a composite index with `tab_module` as the
leading key, so an equality seek on tab plus a range seek on `created_at` is fully covered without
a scan. The standalone `IX_audit_log_created_at (created_at)` supports the "all tabs, date range
only" view (no tab filter selected). `IX_audit_log_user_id` supports a per-user audit trail view
if the UI adds a "my actions" filter later — not currently a named FR-24 requirement, but already
present in CAS-003 at negligible cost given `audit_log`'s append-only, low-cardinality-per-row
write pattern.

**FR-19 — Task Manager status filtering:** The Task Manager screen's primary query shape is "my
tasks in status Y" and the worker's claim query is "the oldest Queued task overall". `IX_task_
queue_status_submitted_at (status, submitted_at)` covers the worker's claim pattern (equality seek
on `status = 'Queued'`, ordered by `submitted_at` — see CAS-003 SC-3, the no-double-claim test).
`IX_task_queue_submitted_by_status (submitted_by_user_id, status)` covers the per-user Task
Manager view (equality seek on the current user, then status). No additional index is needed for
either FR without first observing a query plan that shows otherwise — adding one preemptively
would be Karpathy P4 territory (no new abstraction/object until the pattern is observed twice).

Both requirements are already satisfied by CAS-003's merged indexes. **No new index is introduced
by this spec.**

### Action/Result Catalog & Payload Convention (resolved 2026-09-22)

CAS-003 deliberately left `audit_log.action`, `audit_log.result`, `task_queue.request_parameters`,
and `task_queue.result_detail` as free text/unstructured JSON rather than `CHECK`-constrained
enums, since the action catalog grows as each module (Keyword Search, O-Drive Sync, Quarantine,
Task Manager) is implemented. This is an engineering convention, not a client business decision,
and is resolved here (client agreed 2026-09-22) rather than left open:

- **`action` and `result` values** are defined as C# enums in `Cachet.ArchiveSync.Core` (e.g.
  `AuditAction`, `AuditResult`), one shared catalog every module's repository code references —
  not free-typed strings at each call site. The enum's string name (`.ToString()`) is what's
  written to the `NVARCHAR` column. New actions are added by extending the enum, not by a DB
  migration.
- **`request_parameters` and `result_detail` JSON shapes** are defined as one typed DTO per
  `task_type` in `Cachet.ArchiveSync.Core` (e.g. `KeywordSearchRequestDto`,
  `KeywordSearchResultDto`), serialized/deserialized with `System.Text.Json` at the repository
  boundary. The column stays `NVARCHAR(MAX)` — no DB-side JSON schema validation — the DTO is
  what keeps each task type's payload shape consistent across the codebase.
- Deliberately **not** enforced via SQL `CHECK` constraints: the catalog is still growing, and a
  `CHECK IN (...)` list would need a migration on every new action/result, which this project's
  own indexing/constraint conventions (CAS-003) reserve for genuinely closed, stable enumerations
  (e.g. `task_type`, `status`, `tab_module` — each a small, BRD-fixed set) — `action`/`result` are
  not that.

This convention is binding for whichever spec first implements a module's repository (e.g.
Keyword Search) — the enum/DTO types themselves are authored there, not here (§4).

### Stored Procedures

N/A — no change. See CAS-003 §5 for the named-but-unauthored procedure list; this spec does not
add or modify any.

### Migration Strategy

**None.** This spec is documentation/usage-convention only (`touches.database: false`,
`migrations: []`). No `db-author` run is required to implement it — there is no DDL to write.

---

## §6 API Endpoints

N/A — this is a database-usage-documentation spec. No endpoints are defined or implied here.

---

## §7 Validations

No new validations — `dbo.task_queue`'s and `dbo.audit_log`'s `CHECK` constraints (`CK_task_
queue_task_type`, `CK_task_queue_status`, `CK_audit_log_tab_module`) and foreign keys are already
covered by CAS-003 §7 (V4–V8). This spec adds one usage-level rule that is not itself a schema
constraint (SQL Server cannot enforce cross-column state-machine order via `CHECK`), so it is
listed here as a convention repository code must enforce in the application layer:

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | A `task_queue` row's `status` transitions only forward through `Queued → Running → {Completed, Failed}`, or `Queued/Running → Cancelled` when `is_cancel_requested = 1` — never backward, and never re-entering `Queued` after leaving it | Application-layer rejection in the repository's update method (not a DB constraint — the schema permits any value from `CK_task_queue_status`) | `Test_V1_TaskQueueStatusTransitionIsForwardOnly` |

---

## §8 UI / UX

N/A — no UI surface; this spec documents backend usage conventions only.

---

## §9 Configuration Changes

N/A — no new configuration keys.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A typical audit-log `INSERT` (§5's worked example) succeeds and is retrievable via `IX_audit_log_tab_module_created_at` with a query plan showing an index seek, not a scan | `Test_SC1_AuditLogInsertSeeksOnTabAndDate` |
| SC-2 | A full task lifecycle (`Queued → Running → Completed`, and separately `Queued → Running → Failed`) can be driven end-to-end using only the column set in §5, with `started_at`/`completed_at` populated at the correct transitions | `Test_SC2_TaskQueueLifecycleTransitionsPopulateTimestamps` |
| SC-3 | A worker claim query filtered on `status = 'Queued'` ordered by `submitted_at` shows an index seek on `IX_task_queue_status_submitted_at`, not a scan | `Test_SC3_TaskQueueClaimQuerySeeksOnStatusAndSubmittedAt` |
| SC-4 | No retention/archival job exists or is scheduled against either table as of this spec — confirms §15's open item has not been silently worked around elsewhere in the codebase | `Test_SC4_NoUnauthorizedRetentionJobExists` (manual/code-search check, not a runtime test) |

---

## §11 Telemetry

N/A beyond CAS-003's standing rule: no log line may include `b2_application_key`,
`b2_application_key_id`, or `password_hash`. This spec introduces no new logging.

---

## §12 Security Notes

### B2 Credential Handling
N/A — neither table stores B2 credential material.

### Audit Trail
`dbo.audit_log` is itself this project's audit trail (FR-23) — this spec's subject, not a
consumer of it. Reaffirms CAS-003's guarantee: no `_upd`/`_del` procedure is planned, and no FK
cascade exists into this table (SC-5 in CAS-003), so audit history cannot be silently altered or
deleted by a delete elsewhere in the schema. The **audit-before-response invariant (FR-23)** —
writing the audit row before returning success to the caller — is an API-layer concern (each
module's endpoint), not enforceable at the schema level; this spec does not change that division
of responsibility, only documents the table it writes to.

### Retention (deferred — BRD §12.2-adjacent, out of scope for current implementation)
No retention policy has been confirmed by the client for either table, and the client confirmed
2026-09-22 that defining one is deliberately out of scope for the current implementation.
Practical implication as currently designed: **both tables grow unbounded** —
`IDENTITY(100000,1)` has ample headroom, and neither table has partitioning, table-level
compression, or an archival/purge process. This is consistent with the "never hard-delete from
`audit_log`/`task_queue`" rule (CAS-003 §14) but is distinct from retention — retention is about
*how long* data must be kept and *whether* it may ever be archived to cold storage or purged
after that period. One schema-level constraint worth carrying forward to whichever spec revisits
this: `audit_log` has a `NOT NULL`-free (nullable) FK to `task_queue`, and `audit_log` rows are
never updated or deleted (§12 Audit Trail above) — so a `task_queue` row referenced by any
`audit_log` row cannot be purged without either violating that FK or mutating an audit row, which
means **`task_queue`'s effective retention floor is at least as long as `audit_log`'s** for any
task that has an audit entry. Any future retention/archival design needs to account for that
coupling, not treat the two tables independently. See §15.

### Session / Auth Requirements
N/A directly — this spec introduces no endpoint or session logic.

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `TaskQueueLifecycleTests` | `Test_V1_TaskQueueStatusTransitionIsForwardOnly` | V1 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `AuditLogRepositoryTests` (against a real/test SQL Server instance) | `Test_SC1_AuditLogInsertSeeksOnTabAndDate` | SC-1 |
| `TaskQueueRepositoryTests` | `Test_SC2_TaskQueueLifecycleTransitionsPopulateTimestamps`, `Test_SC3_TaskQueueClaimQuerySeeksOnStatusAndSubmittedAt` | SC-2, SC-3 |

### Coverage Target
N/A in the usual line-coverage sense — this spec introduces no application code (`touches.database
= false`, no repository classes authored here). Coverage applies once the repository classes
referenced above are written against `CAS-003`'s schema (a later spec's concern), at which point
§7's V1 (state-machine enforcement) must be covered.

---

## §14 Rollout

No deployment steps — this spec introduces no schema or code change. Its content becomes binding
convention for any repository code written against `dbo.audit_log`/`dbo.task_queue` from this
point forward (WBS Tasks 15–24).

---

## §15 Open Questions

- [ ] **Deferred (client-confirmed 2026-09-22) per BRD §12.2 — retention policy for
      `dbo.audit_log` and `dbo.task_queue`, out of scope for the current implementation.** No
      retention period, archival destination, or purge mechanism has been confirmed by the client,
      and the client has explicitly decided not to define one now. Both tables are therefore
      designed to grow unbounded and neither table's index strategy accounts for pruning old rows
      (e.g. no filtered index excluding aged-out data, no partition scheme) — this spec does not
      propose a default retention period, since that remains a client business decision. When this
      is revisited, bring the client two separate questions, not one: (1) how long must the audit
      trail be kept, and (2) is it acceptable for `task_queue` rows to be retained at least as long
      as any `audit_log` row that references them (see §12 Retention's FK/immutability note),
      since the current schema cannot purge a referenced `task_queue` row without either breaking
      that FK or mutating an immutable audit row.

- [x] **`dbo.audit_log.result`/`.action` and `dbo.task_queue.request_parameters`/`result_detail`
      value catalogs — resolved 2026-09-22.** Client agreed with the recommendation in §5's
      "Action/Result Catalog & Payload Convention" subsection: action/result values become shared
      C# enums in `Core`, and `request_parameters`/`result_detail` payloads become one typed DTO
      per `task_type`, both enforced at the application layer rather than via SQL `CHECK`
      constraints (the catalog is expected to keep growing across modules). No longer open; see §5.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 | Addendum to CAS-003 (merged) — no schema change |
| Reviewer | | | Pending |

---

## §17 Graph Impact

`graphify-out/` exists for this repo (built 2026-09-10) but indexes only the BRD/WBS/tooling
documentation corpus — no application code exists yet, so a `graphify query`/`explain` run against
source code is not yet meaningful. Analysis for this spec was a manual read of `CAS-003`'s merged
spec, migration file, and entity mirror files (§0), cross-referenced against the graph's Community
2 (Auth/Audit Log API surface, node "API spec §8: Audit Log Module (FR-23-FR-24)") and Community 17
(Task Manager API surface, node "API spec §7: Task Manager Module (FR-18-FR-22a)") for FR
traceability.

### God Nodes in Blast Radius
`FR-03: B2 Key Held Server-Side Only` (12 edges, the graph's highest-degree FR node) is not
touched by this spec — confirmed by inspection, since neither table stores B2 key material. No
other god node is in this spec's blast radius; it changes no code.

### Communities / Modules Affected
Task Manager (`task_queue`) and Audit Log (`audit_log`) directly; indirectly, every module that
will eventually write to either table (Auth, Keyword Search, O-Drive Sync, Quarantine, Reports) —
consistent with CAS-003's own framing of these two tables as shared foundation. No `Web` or `Api`
project code changes; this spec is documentation only.

### Surprising Connections
None new. Reconfirms the graph's own extracted hyperedge **"Audit-Before-Response Fail-Closed
Pattern"** (`brd_fr23`, `brd_permanent_delete`, `brd_quarantine_workflow`, `brd_sync_actions`,
`dataflow_store_audit_log`) — every module that performs a state-changing action is expected to
write to `dbo.audit_log` before responding, which is exactly the shared-dependency reasoning this
spec's §2 relies on.

### Graphify Queries Run
Graph exists but has no application-code nodes to query yet (`graphify-out/GRAPH_REPORT.md`,
2026-09-10 build, is documentation-only). No `graphify query`/`explain`/`path` commands were run;
analysis was a manual read of `CAS-003` and the merged migration/mirror files.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] §5's column/constraint/index restatement matches the actual merged mirror files
      (`Database/CachetArchiveSync/Entities/dbo.task_queue.sql`,
      `dbo.audit_log.sql`) exactly — no drift introduced while restating.
- [ ] §5 Indexing Rationale correctly maps each existing index to FR-24/FR-19 — no new index was
      silently introduced.
- [ ] §7 V1 (forward-only status transition) is a real gap the schema does not enforce (confirm no
      `CHECK`/trigger already does this) and is correctly scoped as application-layer, not DB-layer.
- [ ] §12 Retention section accurately reflects "deferred, no policy confirmed" and does not
      assert a retention period the client has not approved.
- [ ] §15's deferred retention item is carried forward as deferred, not silently treated as a real
      policy decision. §15's resolved action/result-catalog item matches §5's convention exactly.
- [ ] §4 Out-of-Scope respected — no DDL, no stored procedure body, no migration file was added
      under this spec.

### Verification Report Format
Per template: `CAS-004.review.md` with overall verdict, per-SC verdict table, security gap list,
scope violations.

---

## Appendix A — Example: Typical Audit-Log Event

Illustrative only — the actual `INSERT` will run through `dbo.audit_log_ins` once that procedure
is authored (CAS-003 §5, not yet written). `action`/`result` values shown are representative of
the resolved enum convention (§5, "Action/Result Catalog & Payload Convention") — the concrete
`AuditAction`/`AuditResult` enum members are authored in `Core` by whichever spec implements the
first module's repository.

```sql
INSERT INTO dbo.audit_log
    (created_by, updated_by, user_id, tab_module, action, file_path, task_queue_id, result, result_detail)
VALUES
    (N'system', N'system', 100001, N'Quarantine', N'QuarantineAdd',
     N'\\ODrive\Contracts\2024\vendor-agreement.pdf', NULL, N'Success', NULL);
```

Notes:
- `created_by`/`updated_by` hold the identity performing the write (here, the API's service
  identity — CAS-003 does not define whether this should instead be the acting `username`; not
  covered by §15's resolved action/result convention, flag separately if it needs deciding).
- `task_queue_id` is `NULL` because a Quarantine add is synchronous, not queued (FR-14 is
  Keyword-Search-only cataloging, not an async task).
- `created_at`/`updated_at` are omitted — both default to `SYSUTCDATETIME()`.

## Appendix B — Example: Typical Task Lifecycle

> **Erratum (2026-09-24, CAS-028):** the claim example in step 2 below is **invalid T-SQL**. `UPDATE TOP (n)`
> does not accept `ORDER BY` (SQL Server Msg 156, verified on LocalDB). Use the CTE form in
> `specs/misc/CAS-028-task-queue-infrastructure.md` §5 (`WITH next_task AS (SELECT TOP (1) … ORDER BY
> submitted_at, task_queue_id) UPDATE next_task …`). The rest of this appendix is unaffected.

Illustrative only — actual statements will run through `dbo.task_queue_ins`/`_upd` once those
procedures are authored (CAS-003 §5, not yet written).

**1. Submission (Api, synchronous, FR-18):**
```sql
INSERT INTO dbo.task_queue
    (created_by, updated_by, task_type, submitted_by_user_id, status, request_parameters)
OUTPUT INSERTED.task_queue_id
VALUES
    (N'jsmith', N'jsmith', N'KeywordSearch', 100001, N'Queued',
     N'{"keyword":"invoice","fileTypes":["pdf","docx"]}');
```

**2. Worker claim (FR-18/§5 Indexing Rationale — uses `IX_task_queue_status_submitted_at`):**
```sql
UPDATE TOP (1) dbo.task_queue WITH (READPAST, UPDLOCK)
SET status = N'Running',
    started_at = SYSUTCDATETIME(),
    updated_by = N'worker-svc',
    updated_at = SYSUTCDATETIME()
OUTPUT INSERTED.task_queue_id, INSERTED.task_type, INSERTED.request_parameters
WHERE status = N'Queued'
ORDER BY submitted_at ASC;
```

**3a. Successful completion (FR-21):**
```sql
UPDATE dbo.task_queue
SET status = N'Completed',
    completed_at = SYSUTCDATETIME(),
    result_detail = N'{"matchCount":37}',
    updated_by = N'worker-svc',
    updated_at = SYSUTCDATETIME()
WHERE task_queue_id = @taskQueueId;
```

**3b. Failure variant (worker-caught exception, still resolves the task record — never left
`Running` indefinitely):**
```sql
UPDATE dbo.task_queue
SET status = N'Failed',
    completed_at = SYSUTCDATETIME(),
    result_detail = N'{"error":"B2 API timeout after 3 retries"}',
    updated_by = N'worker-svc',
    updated_at = SYSUTCDATETIME()
WHERE task_queue_id = @taskQueueId;
```

Notes:
- Step 2's `UPDATE ... WITH (READPAST, UPDLOCK) ... OUTPUT` is the exact pattern CAS-003 SC-3
  tests for (no double-claim under concurrency) — reproduced here for reference, not redefined.
- `is_cancel_requested` is set to `1` by a separate `UPDATE` (triggered by `POST /tasks/{taskId}/
  cancel`) and is expected to be polled by the worker between processing steps, not enforced by
  any schema-level trigger — this is an application-layer cooperative-cancellation convention,
  consistent with §7 V1's framing that state-machine order is enforced in code, not DDL.
