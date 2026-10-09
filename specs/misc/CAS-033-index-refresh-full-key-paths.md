---
id: CAS-033
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-30
priority: P0
change_type: enhancement               # bug-fix | enhancement | refactor | security | deprecation
related_specs: [CAS-011, CAS-031, CAS-032]
touches:
  database: false
  api: false
  ui: false
  worker: false
  b2: true
  config: true
  security: true
migrations: []
---

# CAS-033 — Change: Index Refresh Scripts Store the Full B2 Key in `relative_path`

> **Hand-off note.** Replaces CAS-032 (app-side prefix handling), which was dropped on 2026-09-30 in favor
> of this script-only approach. Line numbers are from the codebase as of 2026-09-30; locate by function
> name if they have moved. **No application code changes** (Api, Worker, Web, Core) and no database
> migration. The change is in `scripts/index_refresh/` plus a one-time folder layout and mapping-row
> change.

## §1 Current Behavior

The B2 sync root is a folder inside the bucket (`b2_path_prefix`, production `D/Shares/Operations`). Both
index-refresh scripts store `dbo.file_index.relative_path` **relative to that sync root**, with the prefix
removed:

- `refresh_b2_index.py` lists only objects under `prefix + '/'` and strips that folder from each name
  (`list_remote`, `refresh_b2_index.py:50-62`). B2 object `D/Shares/Operations/Adobe/Adobe/symbol.txt`
  is stored as `Adobe/Adobe/symbol.txt`.
- `refresh_odrive_index.py` walks `odrive_root_path` and stores each file's path relative to that root
  (`walk`, `refresh_odrive_index.py:31-48`).
- Local files are `root + relative_path`: the B2 working copy root is `...\D\Shares\Operations`
  (`refresh_b2_index.py:136,171,224` via `common.local_path`), and the O-Drive root is
  `D:\O-Drive\Operations`.
- The shared rule is documented in `common.py:6-9` and `README.md:99-103`.

**The problem.** The application uses `relative_path` as the B2 object name with nothing added:
- Sync Copy: `UploadFileAsync(uploadUrl, row.OdriveRelativePath, ...)` (`SyncCopyTaskProcessor.cs:103`)
- Sync Update: `row.B2RelativePath ?? row.OdriveRelativePath` (`SyncUpdateTaskProcessor.cs:80`)
- Sync Purge: `row.B2RelativePath` (`SyncPurgeTaskProcessor.cs:80`)
- Quarantine Permanent Delete: `entry.Path` (`QuarantinePermanentDeleteService.cs:140`)
- Keyword Search cross-check: `candidate.RelativePath` (`KeywordSearchTaskProcessor.cs:248`)

The app never reads `b2_path_prefix`. With the prefix stripped from the catalog, every one of these calls
addresses `Adobe/Adobe/symbol.txt` at the bucket root, not `D/Shares/Operations/Adobe/Adobe/symbol.txt`:
Copy and Update write to the wrong place, Purge and Permanent Delete target the wrong key, and the
cross-check reports real files as missing.

---

## §2 Desired Behavior

`relative_path` in `dbo.file_index` holds the **full B2 object key**, for both O-Drive and B2 rows:

```
relative_path = b2_path_prefix + "/" + path_under_the_sync_folder      (prefix non-empty)
relative_path = path_under_the_sync_folder                             (prefix NULL or empty)
```

Example: `D/Shares/Operations/Adobe/Adobe/symbol.txt` on both the O-Drive row and the B2 row.

The unmodified application then works, because it already uses `relative_path` as the B2 object name and
joins it with the mapping roots to find local files. This requires the roots and physical layout to change:

| | Today (CAS-031) | After this spec |
|---|---|---|
| `odrive_root_path` | `D:\O-Drive\Operations` | `D:\O-Drive` |
| O-Drive file location | `D:\O-Drive\Operations\Adobe\Adobe\symbol.txt` | `D:\O-Drive\D\Shares\Operations\Adobe\Adobe\symbol.txt` |
| `b2_mirror_root_path` | `D:\Cachet-Server-C\D\Shares\Operations` | `D:\Cachet-Server-C` |
| B2 working copy location | same | **same physical location**, `D:\Cachet-Server-C\D\Shares\Operations\...` |
| `b2_path_prefix` | `D/Shares/Operations` | `D/Shares/Operations` (still used by the loader to list and scan only that folder) |

Script behavior:
1. **Key construction.** Both scripts build the key from the **configured prefix text**, not from disk
   folder casing. B2 keys are case-sensitive and Windows is not; building from config guarantees the
   O-Drive key equals the B2 key.
2. **O-Drive script** scans only `odrive_root_path / prefix` and records `prefix + "/" + path under that
   folder`. If the scan folder does not exist, it stops before touching the database.
3. **B2 script** keeps the full object name as `relative_path`, downloads to `root + key` (the same
   physical folder as today), and restricts its "untracked local file" scan to the prefix folder.
4. **Stale-catalog guard.** If any active `file_index` row for the bucket and source does not start with
   `prefix + "/"` (case-sensitive), the script stops before any change and says the catalog needs a reset
   (§7). This prevents old short-address rows from being deactivated in bulk or mixed with new ones.
5. **Empty prefix:** behavior is unchanged from today (keys equal paths under the root).
6. **Length rules** apply to the full key: `relative_path` ≤ 1,000 characters (column size) and ≤ 1,024
   bytes UTF-8 (B2 object-name limit). Violations are skipped and reported, as other invalid paths are.

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** production layout confirmed 2026-09-30; decision 2026-09-30 to fix the prefix issue in the
  loader scripts and the folder layout instead of the application (CAS-032).
- **Risk if not changed:** the first production Sync Copy or Update writes to the wrong place in the
  archive bucket, Keyword Search cross-checks are wrong, and the two irreversible actions (Purge,
  Permanent Delete — FR-13d, FR-16/FR-17) target the wrong keys. Blocks go-live.

---

## §4 Blast Radius

> Mapped by manual read of the scripts and every consumer of `relative_path` (graphify does not trace
> Python-to-C# data flow).

### Primary Change Target
Files: `scripts/index_refresh/common.py`, `refresh_odrive_index.py`, `refresh_b2_index.py`, `README.md`.
Entity: the value written to `dbo.file_index.relative_path`, and the folders each script scans.

### What consumes `relative_path` (verified unchanged and compatible)
| Consumer | Use | Works with a full-key `relative_path`? |
|---|---|---|
| Sync Copy / Update / Purge, Permanent Delete, Keyword Search cross-check | B2 object name | Yes, this is the purpose of the change |
| `ODriveFileReader`, `MirrorFileReader` | `odrive_root_path` / `b2_mirror_root_path` + `relative_path` | Yes, once the roots are the parents (§2) |
| Content Scan | matches rows on `(bucket_name, relative_path)` | Yes, both sides now carry the same full key |
| `SyncCopyTaskProcessor` | inserts the new B2 row with the O-Drive row's `relative_path` | Yes, consistent with loader rows |
| `file_index_search` | `relative_path LIKE '%term%'` | Works, but see Known Effects |
| Reports, audit `FilePath`, UI results | display `relative_path` | Works, shows the long path |

### Known Effects (accepted)
- Every displayed path starts with `D/Shares/Operations/`.
- Keyword Search matches the term against `relative_path`, so searching "Shares" or "Operations" matches
  every file.
- The 1,000-character limit now includes the prefix.
- The app does not check that a key starts with the prefix. Correctness depends on the loaded data;
  the stale-catalog guard (§2.4) is the compensating control at load time.
- If the prefix ever changes, the catalog and layout must be reset and reloaded.

### Cross-Project Risk
None in code. `b2_path_prefix` is read only by the scripts. **Operational dependency:** the physical
O-Drive copy must keep the `D\Shares\Operations` structure on every refresh (BRD §12.4 item 1, the
O-Drive refresh process, is still open).

### God Node Involvement
None.

### Decomposition Check (P9)
One logical unit: 3 scripts, 1 README, tests. No threshold crossed. **No split.**

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `scripts/index_refresh/common.py` | modify | Add helpers: build the key from prefix + path, resolve the scan folder, stale-catalog guard. Update module docstring (`:6-9`). Add the 1,024-byte UTF-8 check to `validate_relative_path` (`:151-165`). |
| `scripts/index_refresh/refresh_odrive_index.py` | modify | Scan `root / prefix`; record the full key; abort if the scan folder is missing; call the guard after loading the index. Update the module docstring. |
| `scripts/index_refresh/refresh_b2_index.py` | modify | Keep the full object name as `relative_path`; restrict `walk_local` to the prefix folder; call the guard after loading the index. |
| `scripts/index_refresh/README.md` | modify | New path rules (`:99-103`), mapping-row values and layout (`:26-37`), catalog reset step. |
| `scripts/index_refresh/tests/test_paths.py` (new) | add | Unit tests for the pure helpers (§13). |
| `scripts/index_refresh/requirements-dev.txt` (new, optional) | add | `pytest`. |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| Everything under `src/` (Api, Worker, Web, Core and their tests) | The app already uses `relative_path` as the B2 object name and joins it with the roots. A diff under `src/` is a scope violation. |
| `Database/**` (tables, stored procedures, migrations) | No schema change. `odrive_bucket_mapping_get` already returns `b2_path_prefix`. |
| `specs/misc/CAS-031-*.md` | Historical record. This spec supersedes its "relative to the prefix" decision. |
| `docs/environment-variables.md` | No new variables. |

---

## §7 Database Changes

No schema or stored-procedure changes. Data changes only:

1. **Mapping row** (update with `dbo.odrive_bucket_mapping_upd`, or insert with `_ins` if none exists):

   | Column | Test | Production |
   |---|---|---|
   | `bucket_name` | `test-bucket-name` | `Cachet-Server-C` |
   | `b2_path_prefix` | `D/Shares/Operations` | `D/Shares/Operations` |
   | `odrive_root_path` | `D:\O-Drive` | `D:\O-Drive` |
   | `b2_mirror_root_path` | `D:\Cachet-Server-C` | `D:\Cachet-Server-C` |

   `bucket_name` must equal `B2__BucketName` exactly, case included.

2. **Catalog reset (test environment only).** Existing `file_index` rows hold short paths. Production has
   never been loaded, so this does not apply there. In the test database, clear the rows created by
   earlier loads and the rows that reference them (`sync_result`, `search_result`, `quarantine_entry`,
   then `file_index`; confirm the foreign-key order), then run the loaders. This is a deliberate data
   step; do not run it against a database holding data you need.

---

## §8 API Contract Changes

N/A.

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | `relative_path` excludes the prefix | `relative_path` = `prefix/` + path under the sync folder, identical for the same file on both sides | `test_key_is_prefix_plus_path` |
| V2 | n/a | The key uses the configured prefix text, not disk casing; a disk folder cased differently from the prefix still yields the configured casing | `test_key_uses_configured_prefix_casing` |
| V3 | O-Drive: root must exist | O-Drive: `root / prefix` must exist as a folder; otherwise abort with a clear message and no database write | `test_missing_scan_folder_aborts` |
| V4 | n/a | Any active row for the bucket and source not starting with `prefix/` (case-sensitive) aborts the run before any change | `test_stale_rows_abort` |
| V5 | n/a | Empty or NULL prefix: keys equal paths under the root (today's behavior) | `test_empty_prefix_unchanged` |
| V6 | length ≤ 1,000 chars | length ≤ 1,000 chars **and** ≤ 1,024 bytes UTF-8, measured on the full key | `test_key_length_limits` |
| V7 | B2 script reports untracked files under the whole root | Only under the prefix folder | `test_untracked_scan_limited_to_prefix` |
| V8 | Local path = `root + rel` | Unchanged formula; verified to land in the same physical B2 working-copy folder as before | `test_b2_local_path_matches_existing_location` |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | After loading with the test configuration, a file present on both sides has an O-Drive row and a B2 row with the **identical** `relative_path` `D/Shares/Operations/...` and the same `bucket_name` | manual E2E, SQL check |
| SC-2 | Content Scan on identical files reports no New or Removed | manual E2E |
| SC-3 | Sync Copy creates the object at the key equal to `relative_path`; nothing new appears at the bucket root; the new B2 row's `relative_path` equals the O-Drive row's | manual E2E (E1) |
| SC-4 | Sync Update writes a new version at that key | manual E2E (E2) |
| SC-5 | Sync Purge deletes all versions of that exact key only; decoys untouched | manual E2E (E3) |
| SC-6 | Permanent Delete does the same and still audits before reporting success | manual E2E (E4) |
| SC-7 | Keyword Search cross-check confirms files at the key and excludes ones absent from B2 | manual E2E (E5, E6) |
| SC-8 | Running either script against a catalog with short-path rows aborts with no changes | `test_stale_rows_abort`, manual |
| SC-9 | A second run of each script with no changes reports everything unchanged and downloads or hashes nothing | manual |
| SC-10 | `git diff` (or equivalent) shows no change under `src/` or `Database/` | review |

---

## §11 Security Notes

`touches.security: true`: this change decides which object the irreversible actions (Purge, Permanent
Delete) act on, and the B2 script deletes local working-copy files.

- **Stale-catalog guard** (V4) stops mixed short and full paths from reaching the app.
- **Mass-delete guard** (existing `check_mass_delete`) stays in force on both scripts.
- **B2 script deletes** remain inside `root`; with the guard, every candidate row starts with the prefix,
  so deletions stay inside the prefix folder.
- **No app-side prefix check.** The app trusts `relative_path`. Test the delete flows against the decoys
  in §13 before any production use.
- **Logging and audit:** scripts continue to write counts only, no paths or secrets (FR-23 unchanged).
- **Credentials:** unchanged. The B2 key still needs `listBuckets`, `listFiles`, `readFiles`.

---

## §12 Rollback Plan

1. Revert the script changes (restore the previous versions of the four files).
2. Restore the mapping row's `odrive_root_path` and `b2_mirror_root_path` to the CAS-031 values (§2 table).
3. Reset the test catalog and reload with the reverted scripts. Paths change meaning in this spec, so rows
   from one scheme must not be mixed with the other. (The guard in V4 enforces this in the new scheme.)
4. Move the O-Drive folder back to `D:\O-Drive\Operations` if it was restructured.
5. No database or application rollback. No task-queue impact.

---

## §13 Test Plan

### Test configuration
| Setting | Value |
|---|---|
| Bucket | `test-bucket-name` (= `B2__BucketName` for Api, Worker, Web and the scripts) |
| `b2_path_prefix` | `D/Shares/Operations` |
| `odrive_root_path` | `D:\O-Drive` |
| `b2_mirror_root_path` | `D:\Cachet-Server-C` |
| O-Drive files at | `D:\O-Drive\D\Shares\Operations\...` |
| B2 key | bucket-wide: list, read, write, delete |

### 1. Unit tests (`scripts/index_refresh/tests/test_paths.py`)
One test per V1–V8 in §9, using temporary folders and a fake index. Pure helpers only; no database or B2.

### 2. Seed the test bucket
Upload to the bucket: several files under `D/Shares/Operations/` (nested folders, a name with spaces and
symbols, a non-ASCII name, one file with **two versions**). Add **decoys**:
`finance/a.xlsx` at the bucket root, `D/Shares/Operations/finance/a.xlsx.old`, and
`D/Shares/Operations2/finance/a.xlsx`. Place matching files in the O-Drive folder.

### 3. Load and verify
1. Run both scripts as a dry run, review the report, then `--apply`.
2. SQL check: for a sample file, the `ODrive` and `B2` rows have the identical `relative_path`
   (`D/Shares/Operations/...`) and the same `bucket_name`; the B2 working copy sits at
   `D:\Cachet-Server-C\D\Shares\Operations\...`. Decoys other than the one under the prefix are not indexed.
3. Run each script again: everything unchanged (SC-9).
4. Run Content Scan in the app: no New/Removed for identical files (SC-2).

### 4. End-to-end checks (verify in B2 directly: console, B2 CLI or rclone)
| # | Action | Expected result |
|---|---|---|
| E1 | Add a new nested file to the O-Drive folder, reload O-Drive, run Content Scan, then Copy | Object appears at `D/Shares/Operations/<path>`; nothing new at the bucket root; the new B2 row matches the O-Drive row's path |
| E2 | Modify an O-Drive file, reload, scan, Update | New version at the same key, no root-level object |
| E3 | Purge a file that exists only in B2 | All versions of the exact key deleted; the three decoys untouched |
| E4 | Keyword Search, add to Quarantine, Permanent Delete | Exact key's versions deleted; decoys untouched; `file_index` row inactive; quarantine entry Deleted; audit entry present; a user without `deleteFiles` is denied |
| E5 | Keyword Search (filename and content) | Files present in B2 are confirmed |
| E6 | Keyword Search on a file removed from B2 outside the app, and one that exists only as the root-level decoy | Both excluded, with a `CrossCheckExcluded` audit entry |
| E7 | Load a catalog that still has short-path rows (restore a backup row or insert one) | Script aborts with the stale-catalog message and changes nothing (V4) |
| E8 | Rename the O-Drive `Operations` folder temporarily | O-Drive script aborts: scan folder missing (V3); no database change |
| E9 | Rename the O-Drive disk folder to `operations` (different case) | Keys still use `D/Shares/Operations` casing (V2); the Copy/Update E2E still works |

### 5. Final checks
- Top level of the bucket shows only `D/` plus the seeded decoys.
- The report folder and audit log contain no secrets.

Coverage target: all pure helper functions covered by unit tests.

---

## §14 Open Questions

- [x] **Q1 — O-Drive layout.** Confirmed 2026-09-30: the physical O-Drive copy will be arranged as
  `D:\O-Drive\D\Shares\Operations\...` for test and production. Maintaining that structure on every
  refresh remains an operational dependency (BRD §12.4 item 1, still open). If a refresh ever delivers the
  old layout, the O-Drive script aborts (V3) rather than loading wrong paths.
- [x] **Q2 — Search matches the prefix text.** Accepted 2026-09-30 by choosing this approach after the
  trade-off was listed: Keyword Search matches the term against the full path, so "Shares" or
  "Operations" matches every file. Out of scope here; a later fix could match only the path under the
  prefix. The owner will flush the catalog and rerun the scripts to load the complete paths (see Q3, Q4).
- [x] **Q3 — Test catalog reset.** Confirmed 2026-09-30: the test database holds nothing to keep; the
  owner will flush it and rerun both scripts. Production has never been loaded.
- [x] **Q4 — In-place rebase of existing rows.** Decided 2026-09-30: not needed. Reset and reload is
  the approach, so no new stored procedure and no database change.
- [x] **Q5 — Script unit tests.** Confirmed 2026-09-30: add `pytest` tests for the pure helpers
  (`scripts/index_refresh/tests/`).
- [x] **Q6 — App-side guardrail dropped.** Accepted 2026-09-30: no application code checks that a key
  begins with the prefix. The load-time guard (V4) is the compensating control.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | | | |
| Reviewer | | | |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches the scripts before the change
- [ ] Desired behavior (§2) implemented in both scripts, including the empty-prefix case
- [ ] Only files in §5 were modified; nothing under `src/` or `Database/` (§6)
- [ ] All §10 criteria pass; all §9 validations are tested
- [ ] §11: stale-catalog guard, mass-delete guard and local-delete containment intact
- [ ] README reflects the new layout, mapping values and reset step

### Verification Report Format
Reviewer writes `CAS-033.review.md`: verdict (APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED),
per-SC table, scope violations, security gaps.

## Implementation Log

**Completed:** 2026-10-01
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 100% (new helpers) | Endpoints —

> **Gate notes (Python-only change; no edits under `src/` or `Database/`).** Build = `py_compile` of the
> three scripts and the test file, plus a `--help` smoke run of both scripts. Tests = `pytest tests` (10 passed,
> Python 3.14.6). Format: no Python formatter is configured for `scripts/`, so none was run; the tool only
> accepts pass/fail. The .NET build/test/format gates and endpoint drift were not run, because no C# file
> changed. Coverage: the new helpers (`full_key`, `scan_folder`, `require_scan_folder`, `check_catalog_prefix`,
> the UTF-8 byte check) have 100% line coverage. `main()` bodies are I/O-bound and are left to the §13 manual E2E
> (SC-1–SC-7, SC-9 not yet run). README catalog-reset order corrected from §7.2's list to the FK order:
> `quarantine_entry` → `search_result` → `sync_result` → `file_index`.

### Files Changed
| File | Change |
| --- | --- |
| `scripts/index_refresh/common.py` | modify |
| `scripts/index_refresh/refresh_odrive_index.py` | modify |
| `scripts/index_refresh/refresh_b2_index.py` | modify |
| `scripts/index_refresh/README.md` | modify |
| `scripts/index_refresh/tests/test_paths.py` | add |
| `scripts/index_refresh/requirements-dev.txt` | add |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `test_paths` | `test_key_is_prefix_plus_path` | V1 |
| `test_paths` | `test_key_uses_configured_prefix_casing` | V2 |
| `test_paths` | `test_missing_scan_folder_aborts` | V3 |
| `test_paths` | `test_existing_scan_folder_is_returned` | V3 |
| `test_paths` | `test_stale_rows_abort` | V4, SC-8 |
| `test_paths` | `test_empty_prefix_unchanged` | V5 |
| `test_paths` | `test_key_length_limits` | V6 |
| `test_paths` | `test_untracked_scan_limited_to_prefix` | V7 |
| `test_paths` | `test_b2_listing_keeps_full_name_and_skips_excluded` | V1, V7 |
| `test_paths` | `test_b2_local_path_matches_existing_location` | V8 |
