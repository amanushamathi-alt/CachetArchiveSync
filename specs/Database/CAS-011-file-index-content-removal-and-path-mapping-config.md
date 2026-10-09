---
id: CAS-011
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
change_type: refactor
related_specs: [CAS-003, CAS-006, CAS-007, CAS-012, CAS-013]
touches:
  database: true
  api: false
  ui: false
  worker: false
  b2: false
  config: true
  security: false
migrations:
  - 20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-config.sql
---

# CAS-011 — Change: Remove `file_index.content_text` (Full-Text Search) and Add O-Drive/B2 Path-Mapping Configuration Table

Implements BRD v2.2 Section 12.3, items 1 and 2 (Product Manager Review, 2026-09-22). Corrects two
design assumptions made in `CAS-003-core-schema-foundation.md` before those decisions were confirmed.
This is a **database-only, documentation-scoped spec** — no code is written or migrations run as part
of authoring this spec; `/implement CAS-011` is a separate, later step once `status` moves to `ready`.

---

## §0 Provenance / Scope Note

Analysis for this spec was done via a manual read of the BRD and existing schema/code — **graphify
has not been vendored/run against this repo's application code yet** (per `CLAUDE.md`'s graphify
section: "Status: no initial graph built yet"). §4 Blast Radius below is a grep-based inventory, not
a graphify query result.

---

## §1 Current Behavior

### 1a. `dbo.file_index.content_text` + full-text search

- `Database/CachetArchiveSync/Entities/dbo.file_index.sql` defines `dbo.file_index` with a
  `content_text NVARCHAR(MAX) NULL` column (line 28), described in its header comment as "a SQL
  Server full-text index on `content_text` (FR-05/FR-07)".
- The same file creates a default full-text catalog and full-text index:
  ```sql
  CREATE FULLTEXT CATALOG cachet_ft_catalog AS DEFAULT;
  CREATE FULLTEXT INDEX ON dbo.file_index (content_text LANGUAGE 1033)
      KEY INDEX PK_file_index
      ON cachet_ft_catalog
      WITH CHANGE_TRACKING AUTO;
  ```
  These statements originate in
  `Database/migrations/20260910_153624__CAS-003-core-schema-foundation.sql` (CAS-003's migration).
- `dbo.file_index_search` (`Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql`,
  introduced by `Database/migrations/20260922_173000__CAS-006-keyword-search-api-addendum.sql`) runs
  a `UNION ALL` of a filename-`LIKE` branch and a content branch that executes
  `CONTAINS(fi.content_text, @term_phrase)` (line 92) and computes a snippet with
  `CHARINDEX`/`SUBSTRING` over `fi.content_text` (lines 75–85).
- `dbo.file_index_ins` (`Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_ins.sql`) takes
  an optional `@content_text NVARCHAR(MAX) = NULL` parameter (line 30) and inserts it as-is.
- `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs` line 148 — confirmed by direct
  read — **always** passes `parameters.Add("@content_text", (string?)null);` when calling
  `dbo.file_index_ins`. No code path in this repository currently populates `content_text` with a
  non-null value anywhere in the codebase (grep for `ContentText` in `src/` returns zero matches —
  no C# DTO or repository method exposes a settable content property at all; only the raw SQL
  parameter name `@content_text` exists, always bound to `NULL`).
- `dbo.file_index_upd`, `dbo.file_index_lst`, and `dbo.file_index_soft_delete_upd` do not reference
  `content_text` directly (confirmed by read) but `dbo.file_index_lst`'s `SELECT` list and the
  `FileIndexRow`/`ListRow` DTOs never surface it either — it is dead in every path except
  `_ins`/`_search`.
- `CAS-003-core-schema-foundation.md` §5 documents `content_text` as "Full-text indexed
  (FR-05/FR-07)" and §10 SC-2 asserts "A full-text query (`CONTAINS`/`FREETEXT`) against
  `file_index.content_text` returns matching rows, confirming the full-text index ... actually
  serves the query" — this success criterion is now superseded by BRD §12.3 item 1 and must be
  retired (§10 below), not carried forward.

### 1b. O-Drive-to-bucket root-folder mapping (today: absent)

- `dbo.file_index.bucket_name` (`NVARCHAR(255) NULL`) exists as a column, but no table, stored
  procedure, or configuration value anywhere in this repo records what local filesystem root a given
  `bucket_name` corresponds to.
- `CAS-003-core-schema-foundation.md` §15 explicitly flags this as an open item: "the BRD doesn't
  specify how an O-Drive-sourced row's directory structure maps to a B2 bucket name for the FR-11
  'same bucket/path' comparison. Flagged for WBS Task 34 (B2 mirror population), not invented here."
- `src/Cachet.ArchiveSync.Worker/ContentScanTaskProcessor.cs` (CAS-007) compares O-Drive and B2 rows
  purely in-database, keyed by `(bucket_name, relative_path)` (line 133's `Key` helper) — it never
  resolves either side to an absolute filesystem path, so today's code has no path-mapping gap of its
  own to fix; the gap is upstream, in whatever process populates `file_index` rows and in any future
  code that must open a file on disk (Keyword Search content search, CAS-012; O-Drive Sync file
  operations, CAS-013).
- A generic `dbo.configuration` table (six generic `configuration_valueN NVARCHAR(255)` columns
  keyed by `configuration_name`) was designed in `0.Specifications/CACHET_Database_Schema_Design_v1.md`
  §2 but was explicitly ruled out-of-scope for CAS-003 (§4: "neither table is a dependency of the
  four above") and has never been built — it does not exist in `Database/CachetArchiveSync/Entities/`
  or any migration. It is not reused here (see §2b's rationale).

---

## §2 Desired Behavior

### 2a. Remove `content_text` and the full-text catalog/index

- `dbo.file_index` no longer has a `content_text` column.
- `cachet_ft_catalog` (the full-text catalog) and its full-text index on `dbo.file_index` no longer
  exist.
- `dbo.file_index` holds metadata only: `source`, `bucket_name`, `relative_path`, `file_name`,
  `file_extension`, `file_size_bytes`, `modified_at`, `sha1_hash`, `last_seen_at`, `is_active`, plus
  the standard audit columns and PK — i.e., every column it has today except `content_text`.
- `dbo.file_index_ins` no longer accepts a `@content_text` parameter and no longer inserts into that
  column.
- `dbo.file_index_search`'s content-match branch (the `CONTAINS`/`CHARINDEX`/`SUBSTRING` logic against
  `content_text`) is removed from this stored procedure. The filename-`LIKE` branch (lines 40–62 of
  the current procedure) is unchanged and remains the procedure's sole result set — matching the
  task instructions' note that "the filename-match `LIKE` path can stay."
  - **BLOCKED for this spec, tracked in CAS-012:** content search itself (matching keyword search
    terms against actual file bytes on the filesystem, per BRD §12.3 item 1 — "Filename and content
    search execute against the physical file system") is an API/Worker-side capability change, not a
    database change. This spec only removes the now-incorrect database-side content search path; it
    does not design the filesystem-based replacement. `dbo.file_index_search`'s `@match_type`
    parameter, `match_type`/`match_snippet` result columns, and the `FileIndexCandidateRow.MatchType`/
    `MatchSnippet` DTO fields are left as-is by this spec (still valid for the `Filename` match type)
    but CAS-012 must decide how a filesystem-based `Content` match type re-populates them, or whether
    the shape changes.

### 2b. New configuration table for O-Drive/B2 root-folder mapping

- A new table, `dbo.odrive_bucket_mapping`, stores the per-bucket root-folder mapping described in
  BRD §12.3 item 2: one row per B2 bucket, recording **two** local filesystem roots that bucket is
  rooted at on the application server — one for the O-Drive mirror, one for the B2-downloaded mirror
  (e.g. bucket `CachetServer-C` → O-Drive root `D:\cachetServer-C\D-drive`, B2 mirror root
  `D:\b2-mirror\CachetServer-C`). A consumer resolves an absolute local path on either side by
  concatenating the relevant root with a `file_index.relative_path` value for the same bucket — since
  "the B2 cloud copy and the local O-Drive mirror follow the same relative directory structure beneath
  a per-bucket root folder; only the root differs" (BRD §12.3 item 2, verbatim).
  - **Resolved 2026-09-22 (owner, this conversation), superseding this spec's original single-root
    design:** the BRD (lines 868–870, 966–969) and prior specs (CAS-001 §9's deferred
    `B2:MirrorRootPath`, CAS-007's `Sync:ODriveMirrorRootPath`, CAS-013 draft §"open questions") all
    describe the O-Drive mirror and the B2 mirror as **two separate directories** on the app server,
    each already tracked (or deferred) as its own config key. A single `local_root_path` column would
    have forced a redesign the first time CAS-012/CAS-013 needed to resolve the B2-mirror-side path.
    The table therefore carries two root-path columns, not one (§7).
- **Why a new dedicated table instead of reusing `dbo.configuration`:** the generic `configuration`
  table (§1b) was designed as an untyped six-slot key/value row per setting name, was never built, and
  is not a natural fit for a table that needs its own uniqueness constraint on a specific business key
  (`bucket_name`) and a single, clearly-named value column (`local_root_path`) rather than up to six
  generically-named ones. A dedicated table also matches this project's established pattern of one
  table per concern (`dbo.users`, `dbo.file_index`, `dbo.task_queue`) rather than a shared
  miscellaneous-settings table. This is a design choice for the owner to confirm at review (§14).
- The new table follows the same audit-column and `IDENTITY(100000,1)` conventions as `dbo.users` and
  `dbo.file_index` (§7 below has the exact DDL-level column list).
- Both Keyword Search (CAS-012, filesystem content search) and O-Drive Sync (CAS-013, file
  copy/update/purge operations against the physical O-Drive mirror) are expected to read this table
  to resolve a `bucket_name` to an absolute local path prefix — recorded here as a forward
  dependency; neither of those specs exists yet as of this spec's authoring (see `related_specs`).

---

## §3 Reason for Change

- **Type:** `refactor`
- **Trigger:** BRD v2.2, Section 12.3 ("Additional Decisions (2026-09-22, Product Manager Review)"),
  items 1 and 2, confirmed directly by Rakesh Devarajan (TraceCounts Solution Owner/Product Manager),
  correcting design assumptions the delivery team had made in the absence of an explicit BRD
  requirement:
  - Item 1 (verbatim): "File content shall **not** be copied into or stored in any database table.
    Filename and content search execute against the physical file system ... not against a database
    full-text index. The local index table(s) hold search-result metadata only (file identity, path,
    size, hash, timestamps), never extracted file content. This corrects the working assumption
    recorded in the Data Flow Analysis document (item D) that a SQL Server full-text index over a
    stored `content_text` column would serve this requirement."
  - Item 2 (verbatim): "The B2 cloud copy and the local O-Drive mirror follow the same relative
    directory structure beneath a per-bucket root folder; only the root differs. ... The per-bucket
    root-folder mapping is stored in a configuration table, not hardcoded or inferred. This resolves
    the previously open 'O-Drive-to-bucket mapping convention' item (flagged for WBS Task 34)."
- **Risk if not changed:**
  - Item 1: storing extracted file content in SQL Server is a data-minimization and compliance
    concern (BRD Section 9) for a system whose archive may contain sensitive content, and a full-text
    index that is never populated (confirmed — always `NULL`, §1a) provides no actual search
    capability while still carrying schema/storage overhead and a misleading `SC-2` success criterion
    in CAS-003 that can never truly pass.
  - Item 2: without a stored, explicit mapping, any future O-Drive Sync or filesystem content-search
    code would have to hardcode or infer the per-bucket root, which is exactly what BRD §12.3 item 2
    forbids and would silently break if a bucket's local mount point changes.

---

## §4 Blast Radius (Graph Impact)

> Graphify not yet vendored for this repo's application code — analysis below is a manual grep/read
> of the whole repository (see §0), not a graphify query result.

### Primary Change Target
File: `Database/CachetArchiveSync/Entities/dbo.file_index.sql`
Entity: `dbo.file_index` (column `content_text`, full-text catalog `cachet_ft_catalog`, full-text
index)
Current edges (manual read): referenced by `dbo.file_index_ins`, `dbo.file_index_search`;
column-level reference only in the `_ins` insert list; no C# DTO exposes it.

### Direct Dependents (nodes with edges INTO the changed entity)
- `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_ins.sql` — `@content_text` parameter,
  insert column list (lines 30, 76, 91).
- `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql` — `CONTAINS`/`CHARINDEX`/
  `SUBSTRING` content-match branch (lines 73–92 in the file as read).
- `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs` line 148 — `@content_text` Dapper
  parameter, always `NULL`. **In scope for this spec** as a thin mechanical follow-on (removing a
  parameter binding that no longer exists on the sproc side), not a logic change.
- `specs/Database/CAS-003-core-schema-foundation.md` — §5 column table, §10 SC-2, §15 open item —
  needs a superseding note (not an edit to CAS-003 itself; this spec supersedes those specific claims,
  per this project's append-only-spec convention. CAS-003 stays as the historical record of what was
  built at the time; this spec is the correction).
- `specs/api/cachet-archive-sync/CAS-006-keyword-search-api.md` — designed `dbo.file_index_search`'s
  content-match branch; needs a corresponding note/update when CAS-012 is authored (out of scope
  here, flagged for that spec).

### Downstream Modules
- **Keyword Search** — most directly affected; `dbo.file_index_search`'s content-match capability is
  removed here and must be replaced by filesystem-based search in CAS-012 (Api/Worker layer).
- **O-Drive Sync** — indirectly affected only insofar as `dbo.file_index` shape changes (one column
  removed); CAS-013 (not yet authored) is the spec that will consume the new
  `dbo.odrive_bucket_mapping` table for path resolution.
- **Quarantine, Task Manager, Audit Log, Reporting, Auth** — no impact; none of these modules
  reference `content_text` or `bucket_name` mapping (confirmed by the `bucket_name` grep — the only
  `src/` hits outside `FileIndexRepository.cs`/`ContentScanTaskProcessor.cs` are
  `SearchResultRepository.cs` and two integration test files, all pre-existing and unaffected by this
  change).

### Cross-Project Risk
- `Cachet.ArchiveSync.Core` — `FileIndexRow.cs`, `FileIndexCandidateRow.cs`: neither DTO currently
  has a `ContentText` property (confirmed — zero `ContentText` grep hits repo-wide), so **no C# DTO
  change is required** by item 1 alone. The DTOs are already "ahead of" the schema in this respect.
- `Cachet.ArchiveSync.Api` — `FileIndexRepository.cs` line 148's `@content_text` parameter binding
  must be removed once `dbo.file_index_ins`'s signature drops that parameter, or the call will fail
  at runtime with an unknown-parameter error. This is the one mechanical, in-scope C# edit for this
  spec (§5).
- `Cachet.ArchiveSync.Worker` — no change; `ContentScanTaskProcessor.cs` never touches
  `content_text`.
- No new table (`dbo.odrive_bucket_mapping`) has any existing consumer yet — it is additive, so no
  cross-project risk from item 2 in this spec's scope.

### God Node Involvement
`dbo.file_index` is this project's most heavily depended-on table (CAS-003 §17: "every module in this
application depends on at least one of these four [foundational] tables"), so it qualifies as a
high-fan-in node. However, the specific column removed (`content_text`) has exactly two direct
dependents (§ above) and zero C# DTO exposure — the blast radius of *this* column's removal is narrow
even though the table itself is a god node. Flagged as high-risk-table / low-risk-column.

### Cross-Check: Other Migrations Referencing `content_text`
`Database/migrations/20260922_170000__CAS-006-keyword-search-api.sql` and
`Database/migrations/20260922_173000__CAS-006-keyword-search-api-addendum.sql` were both checked
(per the task instructions) — only the addendum (`173000`) references `content_text`, in the
`dbo.file_index_search` procedure body it creates (confirmed above); the base `170000` migration has
no `content_text` reference (it creates `search_result`/`quarantine_entry`/other CAS-006 objects, not
`file_index_search`).

### Full grep inventory: `content_text` (case-sensitive)
| File | Nature of reference |
|---|---|
| `Database/CachetArchiveSync/Entities/dbo.file_index.sql` | Column definition + full-text catalog/index DDL — **removed by this spec** |
| `Database/migrations/20260910_153624__CAS-003-core-schema-foundation.sql` | Original `CREATE TABLE`/full-text DDL — **left as historical record** (append-only migrations; new migration adds the `DROP`, does not edit this file) |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_ins.sql` | `@content_text` parameter + insert column — **removed by this spec** |
| `Database/migrations/20260922_173000__CAS-006-keyword-search-api-addendum.sql` | Creates `dbo.file_index_search` with the `CONTAINS` content branch — **left as historical record**; superseded by this spec's new migration's `CREATE OR ALTER` of the same procedure |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql` | `CONTAINS`/`CHARINDEX`/`SUBSTRING` content-match branch — **removed by this spec** |
| `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs` (line 148) | `@content_text` Dapper parameter, always bound `NULL` — **removed by this spec** (mechanical) |
| `src/Cachet.ArchiveSync.Core/Dtos/FileIndexCandidateRow.cs` | XML-doc prose reference only (describes the `Content` match type's origin) — **out of scope for this DB spec; flagged for CAS-012** to update the doc comment once the filesystem-search replacement is designed |
| `specs/Database/CAS-003-core-schema-foundation.md` | §5/§10/§15 prose — **not edited by this spec** (historical record); this spec is the documented correction |
| `specs/Database/CAS-003.review.md` | Review-report prose referencing the same — **not edited**, historical record |
| `specs/api/cachet-archive-sync/CAS-006-keyword-search-api.md` | Design prose for the content-match branch — **out of scope for this DB spec; flagged for CAS-012** |
| `0.Specifications/CACHET_Database_Schema_Design_v1.md` | Original design doc — **not edited**, source document, pre-dates the BRD §12.3 correction |
| `0.Specifications/CACHET_Database_Schema_Implementation_v1.md` | Same — **not edited**, source document |
| `CLAUDE.md` (project root) | Matched only because it names `content_text` while describing this very task in its background section — not a code reference |
| `0.Specifications/CACHET_Archive_Files_Search_Sync_BRD_v2.md` | BRD §12.3 item 1 itself — the authority for this change, not a file to edit |

### Full grep inventory: `ContentText` (case-sensitive)
Zero matches repository-wide. No C# property named `ContentText` exists in any DTO, repository, or
test file — confirming §1a's finding that the C# layer never exposed this column as a settable/
readable property.

### Full grep inventory: `bucket_name` (relevant subset, informing the new table's design)
- `dbo.file_index.sql` / `dbo.file_index_ins.sql` / `dbo.file_index_lst.sql` / `dbo.file_index_search.sql` —
  existing `bucket_name NVARCHAR(255) NULL` column and its stored-procedure parameters; this spec's
  new table's `bucket_name` column matches this type/length exactly (`NVARCHAR(255)`) so a future join
  or lookup between the two tables never hits a collation/length mismatch.
- `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs`,
  `src/Cachet.ArchiveSync.Api/Repositories/SearchResultRepository.cs`,
  `src/Cachet.ArchiveSync.Worker/ContentScanTaskProcessor.cs` — existing consumers of `bucket_name`,
  all read/write it as a plain string key; none of them currently resolve it to a filesystem path.
- No existing table or procedure named anything like `bucket_mapping`, `odrive_mapping`, or
  `path_mapping` — confirmed this is a genuinely new object, not a rename of something existing.

### Graphify Queries Run
None — "graphify not yet vendored — analysis done via manual grep/read of the codebase" (per §0).

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `Database/CachetArchiveSync/Entities/dbo.file_index.sql` | modify | Remove `content_text` column, remove full-text catalog/index DDL; header comment updated to reflect metadata-only purpose and this spec's supersession of CAS-003's full-text design |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_ins.sql` | modify | Remove `@content_text` parameter and its position in the `INSERT` column/value list |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql` | modify | Remove the content-match (`CONTAINS`) branch from the `UNION ALL`; keep the filename-`LIKE` branch and the `match_type`/`match_snippet` result shape unchanged so CAS-012 can extend it later without another shape break |
| `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs` | modify | Remove line 148's `parameters.Add("@content_text", (string?)null);` — mechanical only, no logic change; the sproc no longer accepts this parameter |
| `Database/CachetArchiveSync/Entities/dbo.odrive_bucket_mapping.sql` | add | New entity mirror for the O-Drive/B2 path-mapping configuration table |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.odrive_bucket_mapping_get.sql` | add | New — get by `bucket_name` |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.odrive_bucket_mapping_lst.sql` | add | New — list all mappings (paginated per this project's convention) |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.odrive_bucket_mapping_ins.sql` | add | New — insert a mapping row |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.odrive_bucket_mapping_upd.sql` | add | New — update `local_root_path`/`is_active` |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.odrive_bucket_mapping_del.sql` | add | New — soft-delete (`is_active = 0`), consistent with `dbo.file_index`'s soft-delete pattern (not the hard-delete pattern BRD §12.3 item 3 introduced for Quarantine specifically, which is scoped narrowly to that one module) |
| `Database/migrations/20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-config.sql` | add | New, append-only migration implementing all of the above (this spec does not author the SQL itself — that is `/implement`'s job once `status: ready`) |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `Database/migrations/20260910_153624__CAS-003-core-schema-foundation.sql` | Append-only migration history — this project's convention never edits a past migration; the correction is a new migration |
| `Database/migrations/20260922_173000__CAS-006-keyword-search-api-addendum.sql` | Same — historical record of the now-superseded `dbo.file_index_search` version |
| `specs/Database/CAS-003-core-schema-foundation.md` and `CAS-003.review.md` | Historical spec/review record of what was built at the time; this spec (CAS-011) is the documented correction, referenced via `related_specs`, not a rewrite of CAS-003 |
| `src/Cachet.ArchiveSync.Core/Dtos/FileIndexRow.cs`, `FileIndexCandidateRow.cs` | Neither DTO has a `ContentText` property to remove (confirmed, §4); `FileIndexCandidateRow`'s XML-doc prose mentioning `content_text` is flagged for CAS-012, not edited here, since that spec owns the filesystem-search replacement design |
| `src/Cachet.ArchiveSync.Worker/ContentScanTaskProcessor.cs` | Never references `content_text` or a path-mapping table; unaffected by either item 1 or item 2 |
| `src/Cachet.ArchiveSync.Api/Repositories/SearchResultRepository.cs` | References `bucket_name` but not `content_text`; not part of this schema change |
| `specs/api/cachet-archive-sync/CAS-006-keyword-search-api.md`, `CAS-007-o-drive-sync-content-scan-api.md` | Design prose for modules whose *application-layer* behavior changes belong to CAS-012/CAS-013, not this database-only spec |
| `0.Specifications/CACHET_Database_Schema_Design_v1.md`, `CACHET_Database_Schema_Implementation_v1.md` | Original source design documents predating the BRD correction; left as historical record, same treatment as CAS-003 |

---

## §7 Database Changes

### Changed Objects
| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| `dbo.file_index` | dbo | `ALTER TABLE ... DROP COLUMN content_text` | After first dropping the full-text index (a column cannot be dropped while full-text indexed) |
| Full-text index on `dbo.file_index` | dbo | `DROP FULLTEXT INDEX ON dbo.file_index` | Must precede the column drop |
| `cachet_ft_catalog` | dbo | `DROP FULLTEXT CATALOG cachet_ft_catalog` | Must follow the index drop; catalog cannot be dropped while an index still uses it |
| `dbo.file_index_ins` | dbo | `CREATE OR ALTER PROCEDURE` — drop `@content_text` parameter and its `INSERT` column | Signature change — any caller passing `@content_text` (only `FileIndexRepository.cs`, §5) must be updated in the same deploy |
| `dbo.file_index_search` | dbo | `CREATE OR ALTER PROCEDURE` — drop the `CONTAINS` content-match branch from the `UNION ALL` | `@term`/`@bucket_name`/`@file_extension`/`@match_type` parameters and the `match_type`/`match_snippet` result columns are unchanged; passing `@match_type = 'Content'` now returns zero rows rather than an error (documented in V1 below) until CAS-012 redesigns the content-match path |

### New Objects
| Object | Schema | Notes |
|--------|--------|-------|
| `dbo.odrive_bucket_mapping` | dbo | New table — see column list below |
| `dbo.odrive_bucket_mapping_get` | dbo | `@bucket_name` → single row |
| `dbo.odrive_bucket_mapping_lst` | dbo | Paginated list, `@page`/`@pageSize`/`@totalCount OUTPUT`, matching `dbo.file_index_lst`'s convention |
| `dbo.odrive_bucket_mapping_ins` | dbo | Insert; enforces `UQ_odrive_bucket_mapping_bucket_name` |
| `dbo.odrive_bucket_mapping_upd` | dbo | Update `local_root_path` and/or `is_active`, `COALESCE`-style like `dbo.file_index_upd` |
| `dbo.odrive_bucket_mapping_del` | dbo | Soft-delete (`is_active = 0`) — see §5 rationale |

### `dbo.odrive_bucket_mapping` — Proposed Column List

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `odrive_bucket_mapping_id` | `INT IDENTITY(100000,1)` | No | PK, clustered — matches `dbo.users`/`dbo.file_index`'s `IDENTITY(100000,1)` convention, not the exceptional `IDENTITY(1,1)` used only by the never-built `dbo.configuration` design |
| `created_by` | `NVARCHAR(50)` | No | Audit column, matches `dbo.users`/`dbo.file_index` |
| `created_at` | `DATETIME2(3)` | No | Audit column, default `SYSUTCDATETIME()` |
| `updated_by` | `NVARCHAR(50)` | No | Audit column |
| `updated_at` | `DATETIME2(3)` | No | Audit column, default `SYSUTCDATETIME()` |
| `bucket_name` | `NVARCHAR(255)` | No | The B2 bucket name (e.g. `CachetServer-C`); type/length matches `dbo.file_index.bucket_name` exactly (§4) |
| `odrive_root_path` | `NVARCHAR(1000)` | No | The local O-Drive mirror's root folder for this bucket (e.g. `D:\cachetServer-C\D-drive`); length matches `dbo.file_index.relative_path`'s `NVARCHAR(1000)` since a consumer concatenates the two. Corresponds to CAS-007's `Sync:ODriveMirrorRootPath` config key |
| `b2_mirror_root_path` | `NVARCHAR(1000)` | No | The local B2-downloaded mirror's root folder for this bucket, tracked as a separate physical directory per BRD lines 868–870/966–969. Corresponds to CAS-001 §9's deferred `B2:MirrorRootPath` config key |
| `is_active` | `BIT` | No | Default `1`; soft-delete flag, consistent with `dbo.file_index`'s pattern |

**Resolved 2026-09-22 (owner, this conversation) — replaces the original single `local_root_path`
column** with the two columns above, per §2b's note: the BRD and CAS-001/CAS-007/CAS-013 already treat
the O-Drive mirror and B2 mirror as two distinct directories on the app server, each with its own
(partially deferred) config key. One root column would have forced a breaking redesign the first time
CAS-012/CAS-013 needed the B2-mirror-side path.

Constraints: `PK_odrive_bucket_mapping (odrive_bucket_mapping_id)`,
`UQ_odrive_bucket_mapping_bucket_name UNIQUE (bucket_name) WHERE is_active = 1` (§9 V3 —
**resolved 2026-09-22, superseding the original plain-uniqueness proposal**: `dbo.file_index` already
establishes this project's precedent for exactly this situation —
`UQ_file_index_source_bucket_path` (`Database/CachetArchiveSync/Entities/dbo.file_index.sql:37-41`) is
a filtered unique index `WHERE is_active = 1` specifically so a soft-deleted business key can be
re-created later without violating uniqueness. `odrive_bucket_mapping` follows the same pattern for
consistency, rather than the plain unique constraint this spec originally proposed).

### Migration Required
**Yes.** Per this project's hybrid migration/mirror model:
- Migration: `Database/migrations/20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-config.sql`
  (append-only; new file, does not edit CAS-003's or CAS-006's migrations)
- Mirror updates: `Database/CachetArchiveSync/Entities/dbo.file_index.sql` (modified),
  `Database/CachetArchiveSync/Entities/dbo.odrive_bucket_mapping.sql` (new), plus the five new
  stored-procedure mirror files under `Database/CachetArchiveSync/Stored-Procedures/`

Neither the migration SQL nor the mirror files are authored by this spec document itself — per the
task scope, this is a documentation-only deliverable; `db-author` writes the actual DDL at
`/implement` time, once `status` is `ready`.

---

## §8 API Contract Changes

N/A — no endpoint changes in this DB-only spec. `dbo.odrive_bucket_mapping`'s consumers (CAS-012,
CAS-013) will define their own API contracts, if any, in their own specs.

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | `dbo.file_index_search` with `@match_type = 'Content'` returns full-text `CONTAINS` matches | `dbo.file_index_search` with `@match_type = 'Content'` returns zero rows (branch removed) — not an error, since the parameter itself is still validated as `Filename`/`Content`/`NULL` | `Test_V1_ContentMatchTypeReturnsEmpty` |
| V2 | `dbo.file_index.content_text` accepts any `NVARCHAR(MAX)` value | Column no longer exists — an `INSERT`/`UPDATE` naming it fails to compile/parse | `Test_V2_ContentTextColumnDoesNotExist` |
| V3 (new) | No uniqueness rule exists on any bucket-to-path mapping | `dbo.odrive_bucket_mapping.bucket_name` must be unique among active (`is_active = 1`) rows (filtered unique index, resolved §7) | `Test_V3_OdriveBucketMappingUniqueBucketName` |
| V4 (new) | N/A | `dbo.odrive_bucket_mapping.bucket_name`, `.odrive_root_path`, and `.b2_mirror_root_path` are all required (`NOT NULL`, non-empty per sproc-level guard, matching `dbo.file_index_ins`'s empty-string check pattern) | `Test_V4_OdriveBucketMappingRequiredFields` |

---

## §10 Success Criteria

> **Confirmed 2026-09-22 by owner (devarajan@tracecounts.org):** SC-1/SC-2/SC-3 below are approved
> as-written, no revision requested — reflected in this spec's `status: ready`. This confirmation
> covers the criteria's wording only; it does not resolve the separate, still-open live-environment
> `content_text` check tracked in §12 step 2 and §14, which remains outstanding and must be closed out
> before `/implement CAS-011` actually runs the migration.

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | After the migration runs, `dbo.file_index` has no `content_text` column and `cachet_ft_catalog` no longer exists (`SELECT * FROM sys.fulltext_catalogs WHERE name = 'cachet_ft_catalog'` returns zero rows) | `Test_SC1_ContentTextAndFullTextCatalogRemoved` |
| SC-2 | `dbo.odrive_bucket_mapping_ins` followed by `dbo.odrive_bucket_mapping_get` round-trips `bucket_name`/`odrive_root_path`/`b2_mirror_root_path` correctly, and a second insert of an active row with the same `bucket_name` fails on `UQ_odrive_bucket_mapping_bucket_name` | `Test_SC2_OdriveBucketMappingRoundTripAndUniqueness` |
| SC-3 | Re-running the new migration is a no-op (idempotent), consistent with CAS-003's `SC-1` idempotency guarantee for the original schema | `Test_SC3_MigrationIsIdempotent` |

**Explicitly retired by this spec:** CAS-003 §10 SC-2 ("A full-text query ... against
`file_index.content_text` returns matching rows") no longer applies once this migration runs — CAS-003
itself is not edited (§6), but this spec's own SC-1 supersedes that criterion going forward.

---

## §11 Security Notes

- **Data minimization improvement (BRD Section 9):** removing `content_text` means no extracted file
  content is ever copied into SQL Server — consistent with the BRD's data-minimization principle and
  this organization's standing PCI DSS/HIPAA/SOC 2-aligned posture for regulated data at rest. This
  reduces the sensitivity classification of `dbo.file_index` itself (it now holds only file
  *metadata* — path, name, size, hash, timestamps — never file *contents*), which correspondingly
  reduces the blast radius of a database compromise or an over-broad backup/restore.
- **No new secrets introduced:** `dbo.odrive_bucket_mapping.local_root_path` is a filesystem path on
  the application server's local disk (or an attached/mapped O-Drive), not a credential — no B2 key,
  password, or other secret is stored in this new table. It does not require the ciphertext handling
  `dbo.users.b2_application_key`/`b2_application_key_id` use.
- **No audit-log interaction:** this spec does not touch `dbo.audit_log`; it does not add or remove
  any audit-log write path. (Any future endpoint that lets a user add/edit a bucket mapping — not
  designed here — would need its own FR-23 audit-log-before-response treatment, per this project's
  standing rule; flagged as an open question, §14, for whichever spec eventually adds that endpoint.)
- No `[Authorize]`/session/B2-credential-brokering concerns apply — `touches.security: false` in this
  spec's frontmatter is accurate.

---

## §12 Rollback Plan

1. **Schema rollback:** the new migration's commented rollback section (authored by `db-author` at
   `/implement` time, per this project's migration-file convention) would, in order:
   - Re-add `dbo.file_index.content_text NVARCHAR(MAX) NULL`.
   - Re-create `cachet_ft_catalog` and the full-text index on `dbo.file_index (content_text)`.
   - Restore `dbo.file_index_ins`'s `@content_text` parameter and `dbo.file_index_search`'s
     `CONTAINS` branch (`CREATE OR ALTER PROCEDURE` back to the prior body — both prior bodies are
     preserved verbatim in the CAS-003/CAS-006 migration history, §6, so a rollback script can copy
     them exactly rather than reconstructing them).
   - Drop `dbo.odrive_bucket_mapping` (including both `odrive_root_path` and `b2_mirror_root_path`
     columns) and its five stored procedures.
2. **Data-loss assessment for the `content_text` column drop:** confirmed by direct code read
   (`FileIndexRepository.cs:148`) that the only code path that ever writes to this column
   (`dbo.file_index_ins`) always passes `NULL`. No other insert or update path in this repository
   writes to `content_text` at all. **Resolved 2026-09-22 (owner, this conversation):** the only
   database that exists at this time is Development, and the owner confirmed it holds no data —
   `dbo.file_index` is empty. Combined with the code-read finding above, dropping the column is a
   zero-data-loss operation with no open verification gap. No production/staging environment exists
   yet to separately check.
3. **Config rollback:** N/A beyond the schema rollback in step 1 — `dbo.odrive_bucket_mapping` is the
   only config-touching object this spec introduces, and it is additive (no existing configuration
   value it replaces).
4. **Task-queue rollback:** N/A — this spec does not alter `dbo.task_queue`'s shape or job contract;
   no in-flight `Queued`/`Running` row is affected by either change in this spec.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `FileIndexRepositoryTests` (extend existing, if present, else new) | `Test_SC1_ContentTextAndFullTextCatalogRemoved` | SC-1 |
| `FileIndexRepositoryTests` | `Test_V1_ContentMatchTypeReturnsEmpty` | V1 |
| `FileIndexRepositoryTests` | `Test_V2_ContentTextColumnDoesNotExist` (schema-introspection test, e.g. querying `sys.columns`) | V2 |
| `OdriveBucketMappingRepositoryTests` (new) | `Test_SC2_OdriveBucketMappingRoundTripAndUniqueness` | SC-2 |
| `OdriveBucketMappingRepositoryTests` | `Test_V3_OdriveBucketMappingUniqueBucketName`, `Test_V4_OdriveBucketMappingRequiredFields` | V3, V4 |
| `SchemaMigrationTests` (existing class per CAS-003 §13, extend) | `Test_SC3_MigrationIsIdempotent` | SC-3 |

Coverage target: ≥ 85% on changed files, per project standard — applies once repository/DTO code for
`dbo.odrive_bucket_mapping` exists (a later `/implement` concern; no repository class exists for this
new table yet, mirroring CAS-003's own "coverage applies once repository classes are written" note).

**Resolved 2026-09-22:** the `IOdriveBucketMappingRepository`/`OdriveBucketMappingRepository` pair is
**deferred to CAS-012/CAS-013**, not authored as part of `/implement CAS-011`. This matches this
project's own precedent: CAS-003 added `dbo.file_index` (a database-only spec, like CAS-011) but did
not author `FileIndexRepository.cs` — that repository was added later by CAS-006, the first
API-layer spec to actually consume the table. §5 correctly lists no repository file here, consistent
with `touches.api`/`touches.worker` both being `false` in this spec's frontmatter.

---

## §14 Open Questions

- [x] **Owner confirmation on `dbo.odrive_bucket_mapping` naming and shape — RESOLVED 2026-09-22.**
      Table name `odrive_bucket_mapping` confirmed as-is. The B2-mirror-on-server root **is** a
      separate value from the O-Drive root — confirmed against the BRD (lines 868–870, 966–969: "the
      O-Drive mirror directory and the B2 mirror directory" as two separate app-server directories)
      and this project's own prior specs (CAS-001 §9's `B2:MirrorRootPath`, CAS-007's
      `Sync:ODriveMirrorRootPath`, CAS-013 draft's identical open question). The table now carries
      `odrive_root_path` and `b2_mirror_root_path` as two distinct columns (§2b, §7), not one.
- [x] **Confirm zero non-NULL `content_text` rows in any live environment — RESOLVED 2026-09-22
      (owner, this conversation).** The only database that exists at this time is the Development
      environment, and the owner confirmed it holds no data at all — `dbo.file_index` is empty, so the
      zero-non-NULL-`content_text` condition holds trivially (no rows means no non-NULL values in any
      column). This supersedes the code-only finding in §12 step 2 with a direct owner confirmation of
      actual database state. No production/staging environment exists yet to separately check.
- [x] **Repository/DTO scope for `dbo.odrive_bucket_mapping` — RESOLVED 2026-09-22.** Deferred to
      CAS-012/CAS-013, per the CAS-003→CAS-006 precedent (§13).
- [x] **`UQ_odrive_bucket_mapping_bucket_name` as a plain unique constraint vs. a filtered unique
      index — RESOLVED 2026-09-22.** Filtered unique index `WHERE is_active = 1`, matching
      `dbo.file_index`'s own `UQ_file_index_source_bucket_path` precedent exactly (§7).
- [x] **`dbo.file_index_search`'s `@match_type = 'Content'` returning zero rows vs. an explicit error
      — RESOLVED 2026-09-22.** Confirmed safe: the sole caller
      (`FileIndexRepository.SearchCandidatesAsync`) never passes an explicit `@match_type = 'Content'`
      literal, and the Content branch already returns zero rows in every current execution today
      (guarded by `content_text IS NOT NULL`, always false per §1a) — so removing the branch changes
      no observable behavior for any current caller. Silent-empty-result stands as designed.

**All §14 items resolved as of 2026-09-22.** No remaining blocker before `/implement CAS-011` proceeds.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code, via devarajan@tracecounts.org | 2026-09-22 | Drafted per BRD §12.3 items 1–2; status intentionally left `draft`, not `ready` |
| Owner | devarajan@tracecounts.org | 2026-09-22 | Confirmed all 5 §14 open questions in conversation: dual root-path columns (not one), filtered unique index (not plain), repository deferred to CAS-012/CAS-013, silent-empty-result for `Content` match type accepted, and the live-environment `content_text` NULL check resolved (only Development exists and it holds no data) |
| Reviewer | Independent reviewer agent | 2026-09-23 | APPROVED WITH NOTES — see `CAS-011.review.md` |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches what is actually in the code BEFORE the change — specifically,
      independently re-confirm `FileIndexRepository.cs:148` always passes `NULL` for `@content_text`
      and that zero `ContentText` C# references exist repo-wide.
- [ ] Desired behavior (§2) is fully implemented: `content_text` column, full-text catalog, and
      full-text index are gone; `dbo.odrive_bucket_mapping` exists with the exact column list in §7.
- [ ] Only files in §5 were modified (§6 files are untouched) — in particular, confirm CAS-003's and
      CAS-006's migration files were NOT edited (append-only convention).
- [ ] All §10 Success Criteria have passing tests.
- [ ] All §9 Validations are implemented and tested.
- [ ] §8 API contract is correct (N/A — confirm no endpoint was touched).
- [ ] §11 Security Notes implemented as described — no secret introduced in the new table.
- [ ] §4 Blast Radius is accurate — no unintended side effects in connected nodes (especially: confirm
      `ContentScanTaskProcessor.cs` and `SearchResultRepository.cs` remain untouched).
- [ ] §12 in-flight task-queue rows — N/A, confirm no `dbo.task_queue` shape change occurred.
- [ ] §14 Open Questions were resolved (or explicitly re-deferred with owner sign-off) before
      `status` moved to `ready`.

### Verification Report Format
Reviewer writes `CAS-011.review.md`, alongside this spec (`specs/Database/`):
- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5 file list)
- Unintended blast radius (manual read; graphify not yet available)
- Security gaps

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 0% (Skip-marked, no live SQL Server in sandbox; consistent with existing project-wide DB test convention) | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-config.sql` | New migration: drop content_text/full-text, remove content-match branch, add odrive_bucket_mapping + 5 sprocs |
| `Database/CachetArchiveSync/Entities/dbo.file_index.sql` | Removed content_text column + full-text catalog/index DDL |
| `Database/CachetArchiveSync/Entities/dbo.odrive_bucket_mapping.sql` | New entity mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_ins.sql` | Removed @content_text parameter |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql` | Removed CONTAINS content-match branch |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.odrive_bucket_mapping_get.sql` | New sproc mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.odrive_bucket_mapping_lst.sql` | New sproc mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.odrive_bucket_mapping_ins.sql` | New sproc mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.odrive_bucket_mapping_upd.sql` | New sproc mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.odrive_bucket_mapping_del.sql` | New sproc mirror (soft-delete) |
| `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs` | Removed @content_text parameter binding (mechanical) |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `FileIndexRepositoryTests` | `Test_SC1_ContentTextAndFullTextCatalogRemoved` | SC-1 |
| `FileIndexRepositoryTests` | `Test_V1_ContentMatchTypeReturnsEmpty` | V1 |
| `FileIndexRepositoryTests` | `Test_V2_ContentTextColumnDoesNotExist` | V2 |
| `OdriveBucketMappingRepositoryTests` | `Test_SC2_OdriveBucketMappingRoundTripAndUniqueness` | SC-2 |
| `OdriveBucketMappingRepositoryTests` | `Test_V3_OdriveBucketMappingUniqueBucketName` | V3 |
| `OdriveBucketMappingRepositoryTests` | `Test_V4_OdriveBucketMappingRequiredFields` | V4 |
| `SchemaMigrationTests` | `Test_SC3_MigrationIsIdempotent` | SC-3 |

### Migration
`20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-config.sql`
