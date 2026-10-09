---
id: CAS-031
target_app: cachet-archive-sync
status: in-progress
owner: devarajan@tracecounts.org
created: 2026-09-24
priority: P0
related_specs: [CAS-003, CAS-007, CAS-011, CAS-012, CAS-013]
touches:
  database: true
  api: false
  ui: false
  worker: false
  b2: true
  config: true
  security: true
migrations:
  - Database/migrations/20260924_140000__CAS-031-index-refresh-seed.sql
---

# CAS-031 — Index Refresh Seed Scripts (B2 and O-Drive)

## §1 Summary

`dbo.file_index` is the file catalog behind Keyword Search (FR-04–FR-09), Content Scan (FR-10–FR-13)
and the Copy/Update/Purge sync actions (FR-13a–FR-13d), but no code fills it: only a successful Sync
Copy inserts a row. This spec adds two on-demand Python programs, run outside the web application,
that hydrate and refresh the catalog. One covers the physical O-Drive copy; the other covers Backblaze
B2 and its local working copy. It also adds two nullable columns:
- `dbo.file_index.file_created_at`, the file's own creation time, which enables a later search by date
  created.
- `dbo.odrive_bucket_mapping.b2_path_prefix`, the folder inside the bucket that is the sync root (prod
  `D/Shares/Operations`, dev `Operations`). This follows BRD §12.3 item 2.

File content is never stored (BRD §12.3 item 1).

Decisions confirmed by R. Devarajan on 2026-09-24:
- Keep the `file_index` catalog design; production is about 17,000 files and 250 GB.
- The scripts run on demand, not in the web app.
- `relative_path` is relative to the prefix.
- A B2 row whose file is gone from B2 is marked inactive and its working-copy file is deleted. An
  O-Drive row whose file is gone is marked inactive.
- Both migrations are included now.

## §2 User Story

As a **TraceCounts operator**, I want to load and refresh the O-Drive and B2 file catalogs on demand
(day 1, after a new O-Drive copy arrives, or when B2 and the working copy drift apart), so that search,
Content Scan and the sync actions work against the current files.

## §3 In-Scope

- `scripts/index_refresh/refresh_odrive_index.py`, `refresh_b2_index.py` and `common.py`, plus a
  README and `requirements.txt`.
- The migration: two nullable columns, an optional `@file_created_at` on `file_index_ins`/`_upd`, the
  new `file_index_lst_by_bucket`, and additive `b2_path_prefix` support on
  `odrive_bucket_mapping_get`/`_lst`/`_ins`/`_upd`.
- One FR-23 audit entry per `--apply` run.

## §4 Out-of-Scope

- **App-side use of `b2_path_prefix`.** `B2Client`'s cross-check, and Copy/Update/Purge building the
  B2 key as prefix + relative path, belong to the separate "config: prefix + encrypted system key" spec.
  **Until then, the app's B2 cross-check and sync actions address the wrong B2 key whenever a prefix is
  set.**
- Moving the B2 system key into the database.
- Date-range filters on Keyword Search, and the supporting index.
- Limiting content-search candidates by type and size.
- Running the refresh from the UI or the task queue.
- A graphify graph: none exists for this repo; this spec was written from a manual read of the BRD,
  the schema and the Worker code.

## §5 Database Objects

| Object | Change |
|---|---|
| `dbo.file_index` | Add `file_created_at DATETIME2(3) NULL` |
| `dbo.odrive_bucket_mapping` | Add `b2_path_prefix NVARCHAR(1000) NULL` |
| `dbo.file_index_ins` / `dbo.file_index_upd` | Optional `@file_created_at` (additive) |
| `dbo.file_index_lst_by_bucket` | New: unpaged active rows for one `(source, bucket_name)` |
| `dbo.odrive_bucket_mapping_get` / `_lst` | Project `b2_path_prefix` |
| `dbo.odrive_bucket_mapping_ins` / `_upd` | Optional `@b2_path_prefix` |

The migration sets `QUOTED_IDENTIFIER ON` because the tables carry filtered unique indexes.

## §6 API Endpoints

None.

## §7 Validations

- **V1:** `relative_path` uses `/`, is relative to the sync root, and keeps the source's case.
  Unsafe segments (`..`), Windows-invalid characters, and names over the column limits are skipped and
  reported.
- **V2:** Two paths that differ only by case are skipped and reported, since they cannot coexist on
  Windows or under the case-insensitive unique index.
- **V3:** A downloaded file must match B2's SHA-1 (when B2 has one) before it replaces the local file.
- **V4:** Mass-delete guard. If a run would deactivate or delete more than 10% of the rows (minimum 50),
  or the source lists 0 files while the index is not empty, it stops before any change (exit 2) unless
  `--allow-mass-delete` is given.
- **V5:** `--apply` requires `--run-as-user-id`, which must be an active `dbo.users` row, checked
  before any change.

## §8 UI / UX

None.

## §9 Configuration Changes

- **New config:** one `dbo.odrive_bucket_mapping` row per bucket, now including `b2_path_prefix`.
- **Existing config reused:** the scripts use the app's existing environment variables (connection
  string, `B2__BucketName`, `B2__SystemKeyId`, `B2__SystemApplicationKey`), falling back to
  registry-level values.

## §10 Success Criteria

- **O-Drive:** a first run inserts one row per file, with SHA-1 and `file_created_at` set. A second
  run reports everything unchanged and hashes nothing. A row whose file is gone is deactivated.
- **B2:** a first run reuses matching working-copy files, downloads only missing or different ones,
  and inserts rows with SHA-1 set. A second run downloads nothing. A file removed from B2 is
  deactivated and its local copy deleted. Untracked local files are only deleted with
  `--remove-untracked`.
- **Dry runs:** a dry run changes nothing.

## §11 Telemetry

Console progress, a CSV report per run (outside the repository), and an audit log entry.

## §12 Security Notes

- **Secrets:** the connection string and the B2 key are never printed, reported or audited.
  B2 errors are reduced to their exception type.
- **Data minimisation:** reports contain file names, so they are kept outside the repository. The
  audit detail holds counts only.
- **O-Drive:** the script only reads the O-Drive folder.
- **Irreversible deletes:** they happen only in the B2 working copy, which is a re-downloadable mirror.
  The V4 guard protects against accidental mass deletion.

## §13 Test Plan

Verified 2026-09-24 against the local dev database, with every test rolled back:
- **Procedures:** a smoke test of each procedure the scripts call.
- **O-Drive script, real folder:** 137 inserts, then 137 unchanged, then 1 planted row deactivated.
- **B2 script, simulated bucket:** reuse vs download, SHA-1 on every row, repeat run with no downloads,
  B2 deletion removing the local copy, and `--remove-untracked`.
- **Guard and bad user id:** both abort before any change.
- **Real bucket not yet exercised:** the `tc-test-Server` credentials are not configured yet.

## §14 Rollout

1. Apply the migration.
2. Insert the mapping row.
3. Dry-run both scripts and review the reports.
4. Run both with `--apply`.
5. Run a Content Scan in the app.

## §15 Open Questions

- **Creation dates:** does the O-Drive transfer copy keep the original creation dates? Windows resets
  them on a plain copy.

## §16 Sign-off

Pending.

## §17 Graph Impact

No graphify graph exists for this repo yet. The change touches no C# code: the procedure parameters
are additive and default to NULL, and Dapper ignores the extra projected column.

## §18 Verification Plan

Run each script as a dry run, then with `--apply`, against `tc-test-Server` once its key is configured.
Then confirm that Content Scan shows no differences for identical copies.
