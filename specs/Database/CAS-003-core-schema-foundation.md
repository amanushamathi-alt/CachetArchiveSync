---
id: CAS-003
target_app: cachet-archive-sync
status: merged
                                      # design/implementation documents (WBS Tasks 6 and 13) — no
                                      # new invention here, only reformatting into this project's
                                      # CAS-### spec standard; see §15 for the two non-blocking
                                      # open items carried forward
owner: devarajan@tracecounts.org
created: 2026-09-10
priority: P1
related_specs: [CAS-001, CAS-002]    # CAS-001 §5's provisional mirror-index/task-queue fields
                                      # must be reconciled against this spec's actual DDL; CAS-002
                                      # §5/§14 explicitly depends on dbo.users existing (created here)
touches:
  database: true
  api: false
  ui: false
  worker: false
  b2: false
  config: false
  security: true                     # dbo.users stores encrypted B2 credentials + password hashes;
                                      # dbo.audit_log is this project's audit trail (FR-23)
migrations:
  - 20260910_153624__CAS-003-core-schema-foundation.sql
---

# CAS-003 — Core Database Schema (Users, File Index, Task Queue, Audit Log)

WBS Tasks 6 (Design) and 13 (Database Development). Implements FR-01–FR-03, FR-05, FR-18–FR-24.
Architecture basis: BRD Sections 3(c)/3(d), 7.1, 7.2, 7.5, 7.6, 8, 9, 10.

---

## §0 Provenance Note (read this first)

This spec did not originate as a fresh design exercise. It is a direct reformatting of two
already-completed, already-reviewed documents into this project's `CAS-###` template standard,
requested explicitly by the solution owner after noticing those two documents predated (and didn't
follow) the format `CAS-001`/`CAS-002` use:

- **`0.Specifications/CACHET_Database_Schema_Design_v1.md`** (WBS Task 6) — the full 9-table
  design, including the schema-wide conventions (`dbo`-only, `IDENTITY(100000,1)` except
  `dbo.configuration`, mandatory audit columns, stored-procedure naming) confirmed with the
  solution owner on 2026-09-09.
- **`0.Specifications/CACHET_Database_Schema_Implementation_v1.md`** (WBS Task 13) — the
  implementation-ready T-SQL for 4 of those 9 tables (plus their hard FK prerequisite), full-text
  index configuration, and migration-tooling conventions, confirmed 2026-09-10.

**No column, type, constraint, or index below was re-derived or changed from those two documents.**
This spec's job is packaging (frontmatter, `§`-numbered sections, testable Validations/Success
Criteria) so the content is `/implement`-consumable — not a second design pass.

---

## §1 Summary

Creates the four foundational tables every other module in this application depends on:
`dbo.users` (FR-01–FR-03, the account/B2-credential model), `dbo.file_index` (FR-05, the
metadata/content index for both mirrors, with full-text search), `dbo.task_queue` (FR-18–FR-22a,
the durable async task record), and `dbo.audit_log` (FR-23/FR-24, the append-only audit trail).
`dbo.users` is included even though it wasn't named in the original WBS Task 6/13 prompts, because
`task_queue`/`audit_log` both carry `NOT NULL` foreign keys into it — it is a hard prerequisite,
not a scope choice.

---

## §2 User Story

As the **background Worker** and the **Api's request handlers**, I want a durable, referentially
sound account/file-index/task-queue/audit-log schema so that user authentication, B2 credential
resolution, search/scan/sync task tracking, and the audit trail all have a consistent data layer
to build against from the very first endpoint.

---

## §3 In-Scope

- `dbo.users` — account model (FR-01–FR-03).
- `dbo.file_index` — metadata/content index for the O-Drive and B2 mirrors (FR-05), including a
  SQL Server full-text index on `content_text` (FR-05/FR-07).
- `dbo.task_queue` — async task record (FR-18–FR-22a).
- `dbo.audit_log` — append-only audit trail (FR-23/FR-24).
- All primary keys, foreign keys, `CHECK` constraints, defaults, and indexes for these four tables.
- The migration script and per-object entity mirror files for these four tables, per this
  project's hybrid migration/mirror model (`db-author.md`).

---

## §4 Out-of-Scope

- **`dbo.configuration`, `dbo.error_log`** — designed in `CACHET_Database_Schema_Design_v1.md` §2–§3
  but deliberately excluded from the Task 13 implementation doc this spec packages (Karpathy P3 —
  neither table is a dependency of the four above). Recommend a follow-on spec (e.g. `CAS-004`) if
  and when they're needed, rather than folding them into this one.
- **`dbo.search_result`, `dbo.sync_result`, `dbo.quarantine_entry`, `dbo.report`** — designed in
  `CACHET_Database_Schema_Design_v1.md` §7–§11, explicitly assigned to their own follow-on specs
  (CAS-B/C/D/E in that document's §15 recommended split) once each owning module (Keyword Search,
  O-Drive Sync, Quarantine, Reporting) is implemented. Not this spec's concern.
- **Stored procedures** — `dbo.users_get/_lst/_ins/_upd/_del` and equivalents for the other three
  tables are *named* in `CACHET_Database_Schema_Design_v1.md` §4–§10, but their T-SQL bodies are not
  authored here (the original WBS Task 13 prompt scoped that task to tables/indexes/constraints
  only). Written alongside the API endpoints that call them (WBS Tasks 15–20) or by `db-author` at
  `/implement` time.
- **B2 client code, credential-brokering call sequences** — `CAS-001`.
- **Session/cookie mechanics, login flow** — `CAS-002`. This spec only creates the `dbo.users` row
  CAS-002's session design reads from.
- **Drive encryption-at-rest (BRD §12.2, remaining open item)** — unrelated to this spec; not
  depended upon here.

---

## §5 Database Objects

### Tables / Columns

#### `dbo.users`

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `user_id` | `INT IDENTITY(100000,1)` | No | PK, clustered. |
| `created_by` | `NVARCHAR(50)` | No | Audit column — the admin token identity that provisioned this row. |
| `created_at` | `DATETIME2(3)` | No | Audit column, default `SYSUTCDATETIME()`. |
| `updated_by` | `NVARCHAR(50)` | No | Audit column. |
| `updated_at` | `DATETIME2(3)` | No | Audit column, default `SYSUTCDATETIME()`. |
| `username` | `NVARCHAR(50)` | No | Unique; login identifier (FR-01). |
| `password_hash` | `NVARCHAR(255)` | No | Salted hash; never plaintext. |
| `b2_application_key_id` | `NVARCHAR(500)` | No | Ciphertext — application-layer encrypted, decryption key held outside the database. |
| `b2_application_key` | `NVARCHAR(500)` | No | Ciphertext — same as above. |
| `is_active` | `BIT` | No | Default `1`. |

Constraints: `PK_users (user_id)`, `UQ_users_username (username)`.

#### `dbo.file_index`

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `file_index_id` | `INT IDENTITY(100000,1)` | No | PK, clustered; also serves as the full-text index's required key index. |
| `created_by` / `created_at` / `updated_by` / `updated_at` | audit columns | No | Standard. |
| `source` | `NVARCHAR(10)` | No | `CHECK IN ('ODrive','B2')`. |
| `bucket_name` | `NVARCHAR(255)` | Yes | O-Drive-to-bucket mapping convention open — see §15. |
| `relative_path` | `NVARCHAR(1000)` | No | |
| `file_name` | `NVARCHAR(500)` | No | |
| `file_extension` | `NVARCHAR(10)` | Yes | |
| `file_size_bytes` | `BIGINT` | No | |
| `modified_at` | `DATETIME2(3)` | No | |
| `sha1_hash` | `CHAR(40)` | Yes | |
| `content_text` | `NVARCHAR(MAX)` | Yes | Full-text indexed (FR-05/FR-07). |
| `last_seen_at` | `DATETIME2(3)` | No | |
| `is_active` | `BIT` | No | Default `1`; soft-delete. |

Constraints: `PK_file_index (file_index_id)`, `CK_file_index_source`,
`UQ_file_index_source_bucket_path` (filtered unique index, `WHERE is_active = 1`, on
`source, bucket_name, relative_path`). Full-text catalog `cachet_ft_catalog` + full-text index on
`content_text`, keyed to `PK_file_index`, `WITH CHANGE_TRACKING AUTO`.

#### `dbo.task_queue`

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `task_queue_id` | `INT IDENTITY(100000,1)` | No | PK, clustered; exposed as `taskId`. |
| `created_by` / `created_at` / `updated_by` / `updated_at` | audit columns | No | Standard. |
| `task_type` | `NVARCHAR(20)` | No | `CHECK IN ('KeywordSearch','ContentScan','SyncCopy','SyncUpdate','SyncPurge')`. |
| `submitted_by_user_id` | `INT` | No | FK → `dbo.users(user_id)`. |
| `submitted_at` | `DATETIME2(3)` | No | Default `SYSUTCDATETIME()`. |
| `status` | `NVARCHAR(20)` | No | `CHECK IN ('Queued','Running','Completed','Failed','Cancelled')`, default `'Queued'`. |
| `started_at` / `completed_at` | `DATETIME2(3)` | Yes | |
| `request_parameters` | `NVARCHAR(MAX)` | Yes | JSON — task-type-specific input. |
| `is_cancel_requested` | `BIT` | No | Default `0`. |
| `result_detail` | `NVARCHAR(MAX)` | Yes | |

Constraints: `PK_task_queue`, `FK_task_queue_users`, `CK_task_queue_task_type`, `CK_task_queue_status`.
Indexes: `(status, submitted_at)` (worker claim pattern), `(submitted_by_user_id, status)` (own-tasks
scoping).

#### `dbo.audit_log`

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `audit_log_id` | `INT IDENTITY(100000,1)` | No | PK, clustered. |
| `created_by` / `created_at` / `updated_by` / `updated_at` | audit columns | No | Never mutated after insert — see security note in the source design doc §0.3. |
| `user_id` | `INT` | No | FK → `dbo.users(user_id)`. |
| `tab_module` | `NVARCHAR(30)` | No | `CHECK IN ('Auth','Keyword Search','O-Drive Sync','Quarantine','Task Manager','Audit Log','Reports')`. |
| `action` | `NVARCHAR(50)` | No | Free text — not a `CHECK`-constrained enum (action catalog still growing). |
| `file_path` | `NVARCHAR(1000)` | Yes | |
| `task_queue_id` | `INT` | Yes | FK → `dbo.task_queue(task_queue_id)`. |
| `result` | `NVARCHAR(30)` | No | |
| `result_detail` | `NVARCHAR(MAX)` | Yes | |

Constraints: `PK_audit_log`, `FK_audit_log_users`, `FK_audit_log_task_queue`,
`CK_audit_log_tab_module`. Indexes: `(tab_module, created_at)`, `(created_at)`, `(user_id)`.

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `dbo.users_get` / `_lst` / `_ins` / `_upd` / `_del` | dbo | get/lst/ins/upd/del | Named only — bodies not authored here (§4). `_del` is soft, via `is_active`. |
| `dbo.file_index_get` / `_lst` / `_ins` / `_upd` / `_del` | dbo | get/lst/ins/upd/del | Named only. `_del` is soft. |
| `dbo.task_queue_get` / `_lst` / `_ins` / `_upd` | dbo | get/lst/ins/upd | Named only. No `_del` — never hard-deleted from. |
| `dbo.audit_log_get` / `_lst` / `_ins` | dbo | get/lst/ins | Named only. No `_upd`/`_del` — immutable once written. |

### Migration Strategy

The canonical, ready-to-run T-SQL for all four tables (idempotent `CREATE TABLE` guards, full-text
catalog/index setup, and the commented rollback section) already exists at
`0.Specifications/CACHET_Database_Schema_Implementation_v1.md` §4 — this spec does not duplicate
that ~150-line script inline. Per this project's hybrid migration/mirror model (`db-author.md`),
implementing this spec means: `db-author` takes that script as its starting point, re-headers it
under this spec's ID (`CAS-003`) and a fresh timestamp, writes it to
`Database/migrations/<YYYYMMDD_HHMMSS>__CAS-003-core-schema-foundation.sql`, and writes the four
corresponding per-object mirror files under `Database/CachetArchiveSync/Entities/dbo.<table>.sql`.
No migration has been run yet — `Database/` is still empty as of this spec's authoring.

---

## §6 API Endpoints

N/A — this is a database-only spec. No endpoints are defined or implied here.

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `dbo.users.username` must be unique | `INSERT`/`UPDATE` fails with a unique-constraint violation | `Test_V1_UsersUsernameUnique` |
| V2 | `dbo.file_index.source` must be `'ODrive'` or `'B2'` | `CHECK` constraint violation | `Test_V2_FileIndexSourceCheck` |
| V3 | `dbo.file_index` allows at most one *active* row per `(source, bucket_name, relative_path)` | Filtered unique index violation on a second `is_active = 1` insert for the same key | `Test_V3_FileIndexFilteredUniqueIndex` |
| V4 | `dbo.task_queue.task_type` must be one of the five defined values | `CHECK` constraint violation | `Test_V4_TaskQueueTaskTypeCheck` |
| V5 | `dbo.task_queue.status` must be one of the five defined values | `CHECK` constraint violation | `Test_V5_TaskQueueStatusCheck` |
| V6 | `dbo.task_queue.submitted_by_user_id` must reference an existing `dbo.users` row | FK violation | `Test_V6_TaskQueueUserForeignKey` |
| V7 | `dbo.audit_log.user_id` must reference an existing `dbo.users` row | FK violation | `Test_V7_AuditLogUserForeignKey` |
| V8 | `dbo.audit_log.tab_module` must be one of the seven defined values | `CHECK` constraint violation | `Test_V8_AuditLogTabModuleCheck` |

---

## §8 UI / UX

N/A — no UI surface; this is a database-only spec.

---

## §9 Configuration Changes

N/A — this spec introduces no `appsettings.json` keys. `dbo.users.b2_application_key`/
`b2_application_key_id` hold ciphertext produced by application-layer encryption whose key material
is `CAS-001`/`CAS-002`'s configuration concern (e.g. a Data Protection key ring), not this spec's.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | All four tables (`users`, `file_index`, `task_queue`, `audit_log`) and the full-text catalog/index exist in `CachetArchiveSync` after the migration runs; re-running the same script is a no-op | `Test_SC1_MigrationIsIdempotent` |
| SC-2 | A full-text query (`CONTAINS`/`FREETEXT`) against `file_index.content_text` returns matching rows, confirming the full-text index (not a table scan) actually serves the query | `Test_SC2_FullTextSearchReturnsMatches` |
| SC-3 | Two concurrent claim attempts (`UPDATE ... WITH (READPAST, UPDLOCK) OUTPUT ...`) against `task_queue` for the oldest `Queued` row never both succeed in claiming the same row | `Test_SC3_TaskQueueClaimNoDoubleClaim` |
| SC-4 | Inserting an `audit_log` row with a `user_id` that does not exist in `dbo.users` fails | `Test_SC4_AuditLogRequiresValidUser` |
| SC-5 | The schema defines no cascading delete from `dbo.users` or `dbo.task_queue` into `dbo.audit_log` — deleting (deactivating) a user or a task row can never silently remove audit history | `Test_SC5_NoCascadeDeleteIntoAuditLog` |
| SC-6 | A row can be soft-deleted from `file_index` (`is_active = 0`) and immediately re-inserted as active for the same `(source, bucket_name, relative_path)` without violating the filtered unique index | `Test_SC6_FileIndexSoftDeleteAllowsReindex` |

---

## §11 Telemetry

N/A beyond the standing project-wide rule: no log line, anywhere, may include `b2_application_key`,
`b2_application_key_id`, or `password_hash` values — this spec introduces the columns that rule
protects, but defines no logging code itself.

---

## §12 Security Notes

### B2 Credential Handling
`dbo.users.b2_application_key` / `b2_application_key_id` store **only** application-layer-encrypted
ciphertext (`NVARCHAR(500)`); the database alone is never sufficient to recover a usable B2 key
(Security Analysis §2.1). Column sizing is an assumption pending confirmation against the actual
encryption provider's ciphertext length — see §15.

### Audit Trail
`dbo.audit_log` is this project's audit trail (FR-23). Schema-level guarantees: no `_upd`/`_del`
stored procedures are planned for it (§5); no FK relationship cascades a delete into it (SC-5); its
`user_id`/`task_queue_id` foreign keys are what let the `_get`/`_lst` master-table-join convention
(this project's stored-procedure rule (g)) actually work.

### Session / Auth Requirements
N/A directly — this spec creates the `dbo.users` row `CAS-002`'s session design authenticates
against, but defines no session/auth logic itself.

---

## §13 Test Plan

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `SchemaMigrationTests` (against a real/test SQL Server instance) | `Test_SC1_MigrationIsIdempotent` | SC-1 |
| `FileIndexRepositoryTests` | `Test_SC2_FullTextSearchReturnsMatches` | SC-2 |
| `FileIndexRepositoryTests` | `Test_V2_FileIndexSourceCheck`, `Test_V3_FileIndexFilteredUniqueIndex`, `Test_SC6_FileIndexSoftDeleteAllowsReindex` | V2, V3, SC-6 |
| `TaskQueueRepositoryTests` | `Test_SC3_TaskQueueClaimNoDoubleClaim` (concurrent claim simulation) | SC-3 |
| `TaskQueueRepositoryTests` | `Test_V4_TaskQueueTaskTypeCheck`, `Test_V5_TaskQueueStatusCheck`, `Test_V6_TaskQueueUserForeignKey` | V4, V5, V6 |
| `AuditLogRepositoryTests` | `Test_SC4_AuditLogRequiresValidUser`, `Test_V7_AuditLogUserForeignKey`, `Test_V8_AuditLogTabModuleCheck`, `Test_SC5_NoCascadeDeleteIntoAuditLog` | SC-4, V7, V8, SC-5 |
| `UsersRepositoryTests` | `Test_V1_UsersUsernameUnique` | V1 |

### Coverage Target
N/A in the usual line-coverage sense — this spec has no application code, only schema. Coverage
applies once repository classes are written against these tables (a later spec's concern).

---

## §14 Rollout

1. Confirm the target SQL Server instance has the *Full Text and Semantic Extractions for Search*
   feature installed (environment prerequisite — not scripted; see
   `CACHET_Database_Schema_Implementation_v1.md` §2) before running the migration, or the full-text
   catalog/index statements will fail.
2. `db-author` authors the official migration from the canonical script (§5's Migration Strategy)
   and writes the four entity mirror files. This is the first schema migration for this project —
   there is nothing to sequence against.
3. Deploy order: database migration first, then any Api/Web/Worker code that depends on these four
   tables (WBS Tasks 15+). `CAS-002`'s implementation specifically waits on `dbo.users` existing
   (its own §14).
4. Rollback: commented `DROP` statements exist at the bottom of the canonical migration script —
   appropriate only for a failed initial deployment, never a routine operation, per this project's
   binding "never hard-delete from `audit_log`/`task_queue`" rule.

---

## §15 Open Questions

Both items below are carried forward from `CACHET_Database_Schema_Design_v1.md`'s own Open Items
(§14) — neither blocks implementing this spec's DDL as written; both need an answer before the
columns they touch are relied upon downstream.

- [ ] **`dbo.file_index.bucket_name`'s O-Drive-to-bucket mapping convention** — the BRD doesn't
      specify how an O-Drive-sourced row's directory structure maps to a B2 bucket name for the
      FR-11 "same bucket/path" comparison. Flagged for WBS Task 34 (B2 mirror population), not
      invented here.
- [ ] **`dbo.users.b2_application_key_id`/`b2_application_key` column sizing** (`NVARCHAR(500)`) —
      confirm against the actual encryption provider's ciphertext length before/at implementation
      (WBS Task 13/15).

Not depending on either open BRD §12.2 item (O-Drive refresh cadence; drive encryption-at-rest).

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-10 | Reformatted from `CACHET_Database_Schema_Design_v1.md` (WBS Task 6) and `CACHET_Database_Schema_Implementation_v1.md` (WBS Task 13) into CAS-### standard, at the solution owner's explicit request |
| Reviewer | | | Pending |

---

## §17 Graph Impact

No graphify graph currently covers application code (none exists in this repo yet —
`graphify-out/` indexes only the BRD/WBS/tooling documentation corpus, per
`graphify-out/GRAPH_REPORT.md`). Same fallback `CAS-001`/`CAS-002` used: filled from a manual read
of `CACHET_Database_Schema_Design_v1.md` and `CACHET_Database_Schema_Implementation_v1.md` in full.

### God Nodes in Blast Radius
N/A — no source code exists yet.

### Communities / Modules Affected
Auth (`users`), Keyword Search / O-Drive Sync / Quarantine (all consume `file_index`), Task Manager
(`task_queue`), Audit Log (`audit_log`) — i.e., every module in this application depends on at least
one of these four tables; this is this project's shared foundation, not a single-module change.
Projects: `Core` (entity/DTO definitions), `Api` (repositories calling the stored procedures named
in §5), `Worker` (the `task_queue` claim pattern). No `Web` impact directly.

### Surprising Connections
None new beyond what `CACHET_Database_Schema_Design_v1.md` §0.2 already surfaced: the WBS itself
never assigns any task to design `dbo.users`', `dbo.quarantine_entry`'s, or `dbo.report`'s physical
schema, which is why the source design document expanded its own scope beyond its literal prompt.

### Graphify Queries Run
Graphify not applicable to application code yet — analysis done via manual read of the two source
documents named in §0, in full.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria are each testable against the described schema (no test code exists
      yet — the reviewer verifies the *design* is implementable and internally consistent, not that
      code passes, until `/implement` has run).
- [ ] Every column, type, and constraint in §5 matches `CACHET_Database_Schema_Design_v1.md` and
      `CACHET_Database_Schema_Implementation_v1.md` exactly — this spec must not have silently
      drifted from its own stated source during reformatting.
- [ ] §7 Validations are each traceable to a concrete constraint in §5.
- [ ] §12 Security Notes: confirm no path stores a B2 key or password in plaintext, and no FK
      cascade exists into `audit_log`.
- [ ] §4 Out-of-Scope is respected — no DDL for `configuration`, `error_log`, `search_result`,
      `sync_result`, `quarantine_entry`, or `report` was introduced under this spec's migration.
- [ ] §17 Graph Impact matches actual code/schema changes once `/implement` has run.

### Verification Report Format
Per template: `CAS-003.review.md` with overall verdict, per-SC verdict table, security gap list,
scope violations.

---

## Implementation Log

**Completed:** 2026-09-10
**Gates:** Build — (skipped) | Tests — (skipped) | Format — (skipped) | Coverage n/a | Endpoints — (skip, touches.api=false)

**Scope note:** This spec is database-only (`touches.database=true`; `api`/`ui`/`worker`/`config`
all `false`). `src/` and `Database/` were both empty before this run — no `.sln`, no C# projects,
no test projects exist yet anywhere in this repo. Per user direction (asked explicitly given this
gap), this `/implement` run executed **Phase A (db-author) only**:
- Phase B (implementer) was not run — no api/ui/worker/config touches to implement.
- Phase C (test-writer) was not run — §13's integration test classes (`FileIndexRepositoryTests`,
  `TaskQueueRepositoryTests`, `AuditLogRepositoryTests`, `UsersRepositoryTests`,
  `SchemaMigrationTests`) require a test project and repository classes that don't exist yet; per
  §13's own Coverage Target note, that's "a later spec's concern."
- Gates 1–3 (`dotnet build`/`test`/`format`) were not run — no `solution_path` exists to target.
- Gate 4 (coverage) is N/A per §13 (no application code).
- Gate 5 (endpoint drift) is N/A — `touches.api=false`.

None of V1–V8 or SC-1–SC-6 have been machine-verified against a real SQL Server instance. The
migration and mirrors below are believed correct against §5 by inspection (db-author's self-check
+ a manual re-read of the migration file), not by test execution.

### Files Changed
| File | Change |
|---|---|
| `Database/migrations/20260910_153624__CAS-003-core-schema-foundation.sql` | New — idempotent migration creating `dbo.users`, `dbo.file_index` (+ full-text catalog/index), `dbo.task_queue`, `dbo.audit_log` |
| `Database/CachetArchiveSync/Entities/dbo.users.sql` | New — declarative mirror |
| `Database/CachetArchiveSync/Entities/dbo.file_index.sql` | New — declarative mirror, incl. full-text index DDL |
| `Database/CachetArchiveSync/Entities/dbo.task_queue.sql` | New — declarative mirror |
| `Database/CachetArchiveSync/Entities/dbo.audit_log.sql` | New — declarative mirror |
| `specs/Database/CAS-003-core-schema-foundation.md` | Frontmatter `migrations:` array updated |

### Tests Added
_None — no test project exists yet (see Scope note above)._

### Migration
`20260910_153624__CAS-003-core-schema-foundation.sql`

### Follow-ups Before This Spec Can Be Fully Closed
- Run the migration against a real/test SQL Server instance (confirm the *Full Text and Semantic
  Extractions for Search* feature is installed first — §14 item 1) and verify SC-1 (idempotent
  re-run).
- Once a solution/test-project scaffold exists (a separate, unscoped decision — see this run's
  scope note), write the `SchemaMigrationTests`/`FileIndexRepositoryTests`/`TaskQueueRepositoryTests`/
  `AuditLogRepositoryTests`/`UsersRepositoryTests` classes from §13 and execute V1–V8/SC-1–SC-6 for
  real.
- `/review CAS-003` should independently re-check the migration/mirror files against §5, §7, §10,
  and confirm this log's "not machine-verified" caveat is carried into the review report.
