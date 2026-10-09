---
id: CAS-050
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-05
priority: P1
change_type: enhancement
related_specs: [CAS-006, CAS-008, CAS-014, CAS-043, CAS-051, CAS-052]
touches:
  database: true
  api: true
  ui: false
  worker: false
  b2: false
  config: false
  security: true
migrations: [20261005_120000__CAS-050-files-browse-quarantine.sql]
---

# CAS-050 — Change: Quarantine files from the Files browser, one row per file (API + database)

> Product Owner change (Rakesh Devarajan, 2026-10-05, usability) — recorded in BRD v2.4 §12.5 and
> **FR-09a** (new), **FR-14** (amended). Decomposed per Karpathy P9 into CAS-050 (this spec: DB + API),
> CAS-051 (Files explorer UI) and CAS-052 (Quarantine modal/panel pagination).
> BRD traceability: FR-09a, FR-14, FR-15 (catalog only, unchanged), FR-23 (audit before success),
> FR-16/FR-17 (permanent delete — unchanged), BRD §12.3 item 3 (hard delete), §12.5 items 1–7.
>
> **Revision note (2026-10-05):** an earlier draft modelled a folder as a first-class Quarantine entry
> (schema change, hashed folder index, overlap/absorb rules). The Product Owner replaced that with the
> existing design: **Quarantine stays one row per file**; a folder action is a server-side expansion to
> files. This removes all schema change to `dbo.quarantine_entry`.

## §1 Current Behavior

- Quarantine can only be written from a Keyword Search task result: `POST /quarantine` and
  `POST /search/results/quarantine` take `taskId` + `fileIds` that must resolve to `search_result`
  rows of that task (`QuarantineService.cs`). Policy `CachetAuthenticated`
  (`QuarantineController.cs:58-59`). Audit `TabModule = "Keyword Search"` (`QuarantineService.cs:16`).
- `dbo.quarantine_entry`: `file_index_id INT NOT NULL` (FK `dbo.file_index`), `search_result_id INT NULL`,
  `match_status_snapshot NOT NULL` CHECK `('Flagged','Needs Review','Informational')`, filtered unique
  index `UQ_quarantine_entry_file_index_id_active` (`status='Active'`). `quarantine_entry_ins` takes a
  `search_result_id` only. `quarantine_entry_del` hard-deletes one Active row by id.
- `dbo.file_index` holds one row per file (`relative_path` = full key path, bucket prefix included);
  folders exist only as path prefixes. `UQ_file_index_source_bucket_path (source, bucket_name,
relative_path) WHERE is_active=1` supports prefix range seeks. `dbo.file_index_browse_lst` shows the
  prefix/LIKE-escape convention reused here.
- `GET /quarantine` (`QuarantineController.cs:38`) returns every Active row, unpaginated
  (`dbo.quarantine_entry_lst`, header comment "Not paginated").
- `POST /quarantine/remove` (CAS-014) hard-deletes one Active entry by `quarantine_entry_id`
  (`CachetAuthenticated`, `TabModule = "Quarantine"`).
- The Files browse endpoint (`GET files/browse`, CAS-043) knows nothing about quarantine state.

## §2 Desired Behavior

1. A **Full Access** user can quarantine **a single file** or **all active files in a folder and its
   sub-folders** from the Files browser, without a search task. Read Only users cannot (HTTP 403).
2. **One row per file, always.** A folder is never stored. The folder action expands on the server, in
   one set-based statement, to the active `file_index` rows under the folder prefix (all depths) and
   inserts one `quarantine_entry` row per file not already Active-quarantined. No client-supplied id
   lists, no queued task, **no size limit** (BRD §12.5 item 5).
3. **Snapshot semantics.** Only files indexed at the moment of the action are quarantined; files indexed
   later are not (BRD §12.5 item 3). Already-quarantined files are skipped, not duplicated; the response
   reports `added` and `alreadyQuarantined`.
4. **Deselect = hard delete.** Single-file deselect deletes that file's Active row. Folder deselect
   deletes **every** Active row for active files under the prefix, **including rows that originated from
   Keyword Search** (Product Owner decision, §12.5 item 4). Nothing is moved or deleted in B2/O-Drive
   (FR-15).
5. A **state** lookup for one folder listing lets the explorer render checkboxes: quarantined direct-child
   file ids, plus per direct-child folder `totalFiles` / `quarantinedFiles` (all depths) — enough for
   checked / unchecked / partial display **and** for the confirmation count (added = total − quarantined;
   removed = quarantined) without a separate count endpoint.
6. `GET /quarantine` becomes **paginated** (25 or 50 per page, default 25) and each item reports its
   `sourceTab` (`Files` when `search_result_id IS NULL`, else `Keyword Search`), so the modal and the
   Keyword Search panel show Files-added items.
7. **One audit row per action** (not per file), carrying the file count, written **before** the success
   response (FR-23).
8. **All Quarantine writes require Full Access** (Product Owner decision, BRD §12.5 item 8). The two
   existing add routes (`POST /quarantine`, `POST /search/results/quarantine`) and `POST /quarantine/remove`
   move from `CachetAuthenticated` to `CachetFullAccess`. Read Only users keep read access (list, state,
   search results) but cannot add or remove Quarantine items from anywhere (HTTP 403).

## §3 Reason for Change

- **Type:** enhancement
- **Trigger:** Product Owner usability change (2026-10-05) — BRD v2.4 §12.5.
- **Risk if not changed:** users must run a search task just to quarantine a known file or folder; an
  unpaged queue degrades as bulk folder actions grow it.

## §4 Blast Radius (Graph Impact)

_graphify:_ `graphify-out/GRAPH_REPORT.md` exists (2026-10-04) with community hubs only; no node-level
data was queried. Blast radius mapped by manual grep/read. Re-run `graphify update .` after implementation.

### Primary Change Target

`QuarantineController`, `QuarantineListService`, `IQuarantineEntryRepository`/`QuarantineEntryRepository`,
`dbo.quarantine_entry_lst`; new `QuarantineBrowseService`.

### Direct Dependents

- `QuarantineService` (add), `QuarantineRemoveService`, `QuarantinePermanentDeleteService` — **service logic
  unchanged** (add/remove endpoints only gain the Full Access policy, §2.8):
  Files-added rows are ordinary file rows (`file_index_id` set), so `quarantine_entry_get`,
  `GetActiveForDelete`, Permanent Delete and the Pre-Action Report (`PreActionReportService`) treat them
  exactly like Keyword Search rows. V10 proves it.
- Web: `KeywordSearch.razor` (panel + badge via `GET /quarantine`), `QuarantineQueueModal.razor`.
- Tests: `QuarantineControllerTests`, `QuarantineEntryRepositoryTests`, `QuarantineAddRouteTests`,
  Web fakes (`FakeKeywordSearchApiHandler`, `FakeCachetApiHandler`).

### Downstream Modules

Quarantine, Keyword Search (Files mode), Audit Log.

### Cross-Project Risk

**CROSS-PROJECT RISK:** `Cachet.ArchiveSync.Core` DTOs `QuarantineListItem` / `QuarantineListResponse`
are consumed by Api and Web; `GET /quarantine` becomes paged (additive fields, truncated item list).
Web consumers are updated in CAS-052 — **deploy Api + Web together**.

### God Node Involvement

`dbo.quarantine_entry` is read by three services and the Permanent Delete path → **HIGH RISK**, mitigated
by **no change to the table**, to `quarantine_entry_get`, `quarantine_entry_ins`, `quarantine_entry_del` or
the delete path. Only `quarantine_entry_lst` changes (paging + `source_tab`).

### Graphify Queries Run

graphify not queried at node level — analysis done via manual code read.

## §5 Files to Change

| File                                                                                                  | Change Type | Description                                                                                                                                                                                                                  |
| ----------------------------------------------------------------------------------------------------- | ----------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Database/migrations/20261005_120000__CAS-050-files-browse-quarantine.sql`                            | add         | New SPs + `quarantine_entry_lst` change (no table change)                                                                                                                                                                    |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_lst.sql`                           | modify      | Paged list + `source_tab`, `@total_count OUTPUT`                                                                                                                                                                             |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_ins_browse_file.sql`               | add         | Add one file                                                                                                                                                                                                                 |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_ins_browse_folder.sql`             | add         | Set-based add of all files under a prefix                                                                                                                                                                                    |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_del_browse_file.sql`               | add         | Remove one file's Active row                                                                                                                                                                                                 |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_del_browse_folder.sql`             | add         | Remove all Active rows under a prefix                                                                                                                                                                                        |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_browse_state_lst.sql`              | add         | State for one folder listing                                                                                                                                                                                                 |
| `src/Cachet.ArchiveSync.Core/Dtos/*`                                                                  | add/modify  | `QuarantineBrowseRequest`, `QuarantineBrowseAddResponse`, `QuarantineBrowseRemoveResponse`, `QuarantineBrowseStateResponse`; `QuarantineListItem` (+`SourceTab`); `QuarantineListResponse` (+`TotalCount`,`Page`,`PageSize`) |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineBrowseService.cs`, `IQuarantineEntryRepository.cs` | add/modify  | New service; repository methods                                                                                                                                                                                              |
| `src/Cachet.ArchiveSync.Api/Services/QuarantineBrowseService.cs`                                      | add         | Add/remove/state, audit                                                                                                                                                                                                      |
| `src/Cachet.ArchiveSync.Api/Services/QuarantineListService.cs`                                        | modify      | Paging                                                                                                                                                                                                                       |
| `src/Cachet.ArchiveSync.Api/Repositories/QuarantineEntryRepository.cs`                                | modify      | New SP calls                                                                                                                                                                                                                 |
| `src/Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs`                                      | modify      | New endpoints, paged GET; `[Authorize]` policy on `POST /quarantine` and `POST /quarantine/remove` → `CachetFullAccess`; update XML-doc comments that say "requires only an authenticated session"                           |
| `src/Cachet.ArchiveSync.Api/Controllers/SearchController.cs`                                          | modify      | `[Authorize]` on `POST /search/results/quarantine` only (line ~179) → `CachetFullAccess`; update its class summary. Other actions stay `CachetAuthenticated`                                                                 |
| `src/Cachet.ArchiveSync.Api/Program.cs`                                                               | modify      | DI registration only                                                                                                                                                                                                         |
| `src/Cachet.ArchiveSync.Api.Tests/*`, `Api.IntegrationTests/*`                                        | add/modify  | Tests per §13                                                                                                                                                                                                                |

## §6 Files NOT to Change

| File                                                                     | Why left untouched                                                                                                                                      |
| ------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Database/CachetArchiveSync/Entities/dbo.quarantine_entry.sql`           | **No table change** — Files-added rows fit the existing columns (`file_index_id` set, `search_result_id` NULL, `match_status_snapshot='Informational'`) |
| `dbo.quarantine_entry_ins/_get/_del/_delete_upd`, `QuarantineService.cs` | Service logic and SPs stay as-is; only the **controller-level policy** on the add routes changes (§5)                                                   |
| `QuarantinePermanentDeleteService.cs`, `QuarantineRemoveService.cs`      | Service logic unchanged; only the endpoint policy of `POST /quarantine/remove` changes (§5)                                                             |
| `dbo.file_index*`, `FilesController.cs`, `file_index_browse_lst`         | Browse contract (CAS-043/046) untouched; state comes from a separate endpoint                                                                           |
| `dbo.audit_log`                                                          | `tab_module` already allows `'Quarantine'` — no CHECK change                                                                                            |
| `appsettings.Production.json`, B2 clients                                | No B2 call, no secrets                                                                                                                                  |

## §7 Database Changes

### New / Changed Objects

| Object                               | Schema | Change                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   | Notes                                                                                                                               |
| ------------------------------------ | ------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| `quarantine_entry_ins_browse_file`   | dbo    | New. `@file_index_id`, `@created_by`, `@updated_by`, `@quarantine_entry_id OUTPUT`. Validates an **active** `file_index` row exists; rejects if already Active-quarantined; inserts `search_result_id=NULL`, `match_status_snapshot=N'Informational'`, `status=N'Active'`                                                                                                                                                                                                                | Mirrors `quarantine_entry_ins` error style                                                                                          |
| `quarantine_entry_ins_browse_folder` | dbo    | New. `@source`, `@bucket_name`, `@folder_path`, `@created_by`, `@updated_by`, `@added OUTPUT`, `@already_quarantined OUTPUT`. One `INSERT … SELECT` from `file_index` (`is_active=1`, same source/bucket, `relative_path LIKE <escaped prefix + '/'> + '%'`), skipping files with an Active entry (`NOT EXISTS`). Runs in one transaction with `UPDLOCK, HOLDLOCK` on the existence check; the filtered unique index is the backstop. Raises when the prefix holds **zero** active files | Prefix and LIKE escaping copied from `file_index_browse_lst` (escape `\ % _ [`). `@already_quarantined` = matching files − `@added` |
| `quarantine_entry_del_browse_file`   | dbo    | New. Hard-deletes the Active row for `@file_index_id`; `@rows_affected OUTPUT`                                                                                                                                                                                                                                                                                                                                                                                                           |                                                                                                                                     |
| `quarantine_entry_del_browse_folder` | dbo    | New. Hard-deletes **all** Active rows whose `file_index` row is active and under the prefix, regardless of `search_result_id`; `@rows_affected OUTPUT`. Raises when the prefix holds zero active files                                                                                                                                                                                                                                                                                   |                                                                                                                                     |
| `quarantine_entry_browse_state_lst`  | dbo    | New. Input `(source, bucket_name, folder_path)`. Result set 1: `file_index_id` of direct-child files with an Active entry. Result set 2: per direct child folder (same case-insensitive grouping and case-sensitive `MIN` display name as `file_index_browse_lst`): `folder_name`, `total_files`, `quarantined_files` (all depths). `@folder_path=''` = bucket root                                                                                                                      | One grouped scan per navigation — same cost class as the browse call                                                                |
| `quarantine_entry_lst`               | dbo    | Add `@page`, `@page_size`, `@total_count OUTPUT`; `OFFSET/FETCH`; add `source_tab` (`CASE WHEN qe.search_result_id IS NULL THEN 'Files' ELSE 'Keyword Search' END`); order `created_at ASC, quarantine_entry_id ASC` (id tie-break: bulk adds share near-identical timestamps)                                                                                                                                                                                                           | Still INNER JOIN `file_index`. Consumed only by `GET /quarantine`                                                                   |

### Index check

`UQ_file_index_source_bucket_path (source, bucket_name, relative_path) WHERE is_active=1` already supports
the prefix seek; **no new index** unless SC-10 timing shows otherwise. Add `IX_quarantine_entry_file_index_id`
only if the integration test plan shows the `NOT EXISTS`/state join scanning (the filtered unique index
`UQ_quarantine_entry_file_index_id_active` should serve both).

### Migration Required

Yes — `20261005_120000__CAS-050-files-browse-quarantine.sql` (append-only, `CREATE OR ALTER`), plus the
mirror files in §5. **No DDL on existing tables.**

## §8 API Contract Changes

All routes under the existing API prefix; error bodies are RFC 7807 `ProblemDetails` like
`QuarantineController`.

| Endpoint                                                                                 | Field                                                                            | Before                                                   | After                                                                                                                                                                                                                         | Breaking?                                                                                                                                      |
| ---------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------- | -------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------- |
| `POST /quarantine/browse` (new, `CachetFullAccess`)                                      | body `{scope:"File"\|"Folder", fileIndexId?, source?, bucketName?, folderPath?}` | n/a                                                      | 200 `{added, alreadyQuarantined}`; 403 Read Only; 404 `file-not-found` / `folder-not-found`; 409 `already-quarantined` (**File scope only**); 422 validation. Folder scope with all files already quarantined → 200 `added=0` | No (new)                                                                                                                                       |
| `POST /quarantine/browse/remove` (new, `CachetFullAccess`)                               | same body                                                                        | n/a                                                      | 200 `{removed}`; 404 `not-quarantined` (File scope) / `folder-not-found`; Folder scope with nothing quarantined → 200 `removed=0`                                                                                             | No (new)                                                                                                                                       |
| `GET /quarantine/browse/state?source&bucketName&folderPath` (new, `CachetAuthenticated`) | n/a                                                                              | n/a                                                      | 200 `{fileIndexIds:int[], folders:[{folderName, totalFiles, quarantinedFiles}]}`                                                                                                                                              | No (new)                                                                                                                                       |
| `GET /quarantine?page&pageSize`                                                          | response                                                                         | `{items:[…]}`, all rows                                  | `{items:[…], totalCount, page, pageSize}`; `page` ∈ 1..1,000,000, default 1; `pageSize` ∈ {25, 50}, default 25                                                                                                                                | **Yes (items truncated to a page)** — only consumer is Web, updated in CAS-052; deploy together                                                |
| `POST /quarantine`, `POST /search/results/quarantine`, `POST /quarantine/remove`         | authorization policy                                                             | `CachetAuthenticated`                                    | `CachetFullAccess` — Read Only sessions get 403                                                                                                                                                                               | **Yes (permission tightening)** — Read Only users lose add/remove; request/response bodies unchanged. Web gates the matching buttons (CAS-051) |
| `GET /quarantine` item                                                                   | fields                                                                           | `fileId, fileIndexId, name, path, quarantinedOn, status` | + `sourceTab` (`"Files"`\|`"Keyword Search"`); other fields unchanged (`fileIndexId` stays non-null)                                                                                                                          | No (additive)                                                                                                                                  |

`fileId` in list items remains the `quarantine_entry_id` (unchanged semantics, CAS-006 §1.7).

## §9 Validations Changed

| ID  | Rule (Before)                                    | Rule (After)                                                                                                                                                                                                                                                                                     | Test                                                  |
| --- | ------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------- |
| V1  | add required `taskId`                            | browse add/remove: `scope` ∈ {File, Folder}; File needs `fileIndexId`; Folder needs `source` ∈ {ODrive, B2}, `bucketName`, **non-empty** `folderPath` with no leading/trailing `/` (bucket root is not selectable)                                                                               | `Test_V1_Browse_InvalidShape_422`                     |
| V2  | n/a                                              | Read Only session → 403 on both write routes; service never called                                                                                                                                                                                                                               | `Test_V2_BrowseWrites_ReadOnly_403`                   |
| V3  | n/a                                              | File must be an active `file_index` row; folder must contain ≥ 1 active file                                                                                                                                                                                                                     | `Test_V3_Browse_UnknownTarget_404`                    |
| V4  | unique per file                                  | File scope already Active ⇒ 409 `already-quarantined`; Folder scope skips them and counts `alreadyQuarantined`                                                                                                                                                                                   | `Test_V4_AlreadyQuarantined_FileConflict_FolderSkips` |
| V5  | n/a                                              | Folder add includes all depths (sub-folders), only `is_active=1` rows, only the given `source` + `bucket_name`; a sibling folder with the same name prefix (`Ops` vs `Ops2`) is **not** included (prefix + `/`)                                                                                  | `Test_V5_FolderPrefix_ExactBoundary`                  |
| V6  | n/a                                              | Folder remove deletes Active rows under the prefix regardless of origin (Files **and** Keyword Search); rows outside the prefix untouched                                                                                                                                                        | `Test_V6_FolderRemove_AllOrigins_PrefixScoped`        |
| V7  | unpaged                                          | `pageSize` ∉ {25,50} or `page` < 1 **or `page` > 1,000,000** ⇒ 422                                                                                                                                                                                                                                                         | `Test_V7_List_InvalidPaging_422`                      |
| V8  | n/a                                              | `folderPath` LIKE metacharacters (`% _ [ \`) matched literally                                                                                                                                                                                                                                   | `Test_V8_FolderPath_LikeEscaping`                     |
| V9  | n/a                                              | Folder identity is case-insensitive (`Ops/Audit` = `ops/audit`), as in browse                                                                                                                                                                                                                    | `Test_V9_FolderPath_CaseInsensitive`                  |
| V11 | add/remove routes open to any authenticated user | Read Only → 403 and service never called on **all five** write routes: `POST /quarantine`, `POST /search/results/quarantine`, `POST /quarantine/remove`, `POST /quarantine/browse`, `POST /quarantine/browse/remove`; Full Access unchanged; list/state/search reads still allowed for Read Only | `Test_V11_AllQuarantineWrites_ReadOnly_403`           |
| V10 | n/a                                              | A Files-added row is deletable via the existing Permanent Delete path and listed in the Pre-Action Report exactly like a Keyword Search row                                                                                                                                                      | `Test_V10_FilesAddedRow_WorksWithExistingDeletePath`  |

## §10 Success Criteria

_Proposed — for review/sign-off._

| ID    | Criterion                                                                                                                                                                                                  | Test                                                                           |
| ----- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------ |
| SC-1  | Full Access user adds a file from Files; one Active row, `sourceTab='Files'`, status `Informational`                                                                                                       | `Test_SC1_AddFile`                                                             |
| SC-2  | Folder add inserts exactly one row per active file at all depths; `added` = file count; **no row is created for the folder itself**                                                                        | `Test_SC2_AddFolder_OneRowPerFile`                                             |
| SC-3  | A file indexed into the folder **after** the add is not quarantined (snapshot); re-running the folder add adds only the new file                                                                           | `Test_SC3_Snapshot_RerunAddsOnlyNew`                                           |
| SC-4  | Folder add where some files are already quarantined (incl. from Keyword Search): no duplicates, `alreadyQuarantined` correct                                                                               | `Test_SC4_FolderAdd_SkipsExisting`                                             |
| SC-5  | File deselect removes the row; folder deselect removes all rows under the prefix incl. Keyword Search-origin rows; state/list no longer show them                                                          | `Test_SC5_Deselect`                                                            |
| SC-6  | Read Only receives 403 on both write routes and reads state/list normally                                                                                                                                  | `Test_SC6_ReadOnly`                                                            |
| SC-7  | Exactly one audit row per add/remove action (not per file), `TabModule="Quarantine"`, file count in `ResultDetail`, written before 200; audit failure ⇒ no success response                                | `Test_SC7_Audit_OnePerAction_BeforeResponse`                                   |
| SC-8  | `GET /quarantine?page=2&pageSize=25` on a 60-row set returns rows 26–50, `totalCount=60`, stable order across calls                                                                                        | `Test_SC8_Paging`                                                              |
| SC-9  | State endpoint returns direct-child quarantined file ids and per-child-folder `totalFiles`/`quarantinedFiles` (all depths)                                                                                 | `Test_SC9_State`                                                               |
| SC-10 | Folder add and folder remove on a 50,000-file folder each complete as a single set-based statement (no per-file round trips); timing recorded in the review report (no hard threshold — BRD §12.3 item 14) | `Test_SC10_Folder_SetBased_50k`                                                |
| SC-11 | For a Full Access user, Keyword Search quarantine add (`POST /quarantine`, `POST /search/results/quarantine`) and `POST /quarantine/remove` behave exactly as before                                       | existing `QuarantineAddRouteTests` / `QuarantineRemoveServiceTests` stay green |
| SC-12 | Read Only is rejected (403) on all five write routes and still reads list, state and search results                                                                                                        | `Test_SC12_ReadOnly_AllWrites403_ReadsOk` (= V11)                              |

## §11 Security Notes

- `touches.security: true`. **Every** Quarantine write (five routes, §9 V11) requires policy
  `CachetFullAccess` at **action level** (P7); a Read Only session never reaches service code. UI hiding
  (CAS-051) is convenience, not enforcement. This tightens the earlier CAS-006/CAS-014 position
  ("add/remove are not B2-write actions") by Product Owner decision (BRD §12.5 item 8); existing tests that
  assert Read Only success on add/remove (`QuarantineControllerTests.cs` ~lines 157 and 346, integration;
  matching unit tests) must be **changed to expect 403**, not deleted.
- FR-23: one audit row per action, written before returning success. `TabModule = "Quarantine"` (already a
  permitted `tab_module`; same value as `QuarantineRemoveService`), `Action` = `QuarantineAdd` /
  `QuarantineRemoved` (follow the exact action strings used by the existing services),
  `FilePath` = file path or folder path, `ResultDetail` = `scope`, `added`/`alreadyQuarantined` or
  `removed` count. No file content. **Implementation note:** follow the existing ordering/failure handling
  in `QuarantineService` — if the audit write fails the endpoint must not return 200; reviewer to confirm
  the database mutation/audit ordering is no weaker than the existing add path.
- **Bulk-action safety:** quarantine only earmarks (FR-15); the irreversible step remains Permanent Delete
  (FR-16/FR-17) with its own confirmation, unchanged. The folder deselect also removes Keyword
  Search-origin rows by Product Owner decision (§12.5 item 4) — the UI confirmation (CAS-051) must state
  the count, and the audit row records it.
- Data minimisation: metadata only (paths, ids, counts); no B2 key, no file content in any request,
  response or log line.
- Folder path inputs are parameterised (Dapper SP parameters); LIKE metacharacters escaped (V8).
- No soft-delete/undo introduced. Permanent Delete semantics unchanged.

## §12 Rollback Plan

1. Redeploy previous Api + Web builds together (the list contract is paged in the new build only).
2. Database: forward-fix preferred. If rollback is required: drop the five new SPs and restore the previous
   `quarantine_entry_lst` from the CAS-040 mirror. Rows created from Files are valid ordinary
   `quarantine_entry` rows and **can stay** — no schema reversal is needed.
3. Config rollback: none.
4. Task-queue rollback: no task-queue shape change; N/A.

## §13 Test Plan

| Class                                               | Method                                                                                                                                                               | Proves SC / V                |
| --------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------- |
| `QuarantineBrowseServiceTests` (unit, NSubstitute)  | audit-before-response ordering, count mapping, 404/409 mapping                                                                                                       | V3, V4, SC-7                 |
| `QuarantineBrowseControllerTests`                   | policy 403/200, status mapping, request validation                                                                                                                   | V1, V2, SC-6                 |
| `QuarantineEntryRepositoryTests` (integration)      | SP behaviour, prefix boundary, LIKE escape, case-insensitivity, snapshot, all-origin remove, paging, state, 50k set-based                                            | V4–V9, SC-1–SC-5, SC-8–SC-10 |
| `QuarantineControllerTests` (integration)           | Files-added row through existing delete/remove paths; legacy add unchanged for Full Access; Read Only 403 on all write routes (updates the Read Only-succeeds tests) | V10, V11, SC-11, SC-12       |
| `SearchControllerTests` / `QuarantineAddRouteTests` | `POST /search/results/quarantine` Read Only 403, Full Access 200                                                                                                     | V11, SC-11                   |

Coverage target: ≥ 85% on changed files.

## §14 Open Questions

Resolved by the Product Owner (2026-10-05) and removed: folder-as-entry, overlap/absorb, folder delete,
folder in reports, FR-ID/BRD log (BRD v2.4 §12.5, FR-09a/FR-14), audit `tab_module` (`'Quarantine'`),
snapshot semantics, deselect scope, size limit, and the permission model (all Quarantine writes Full
Access — §2.8, BRD §12.5 item 8).

None open. Status can move to `ready` after review of this revision.

## §14a Review Follow-ups (CAS-050 review, 2026-10-05)

Fixed in this spec: **D1** — `page` upper bound 1,000,000 (V7; keeps the SP's `(@page-1)*@page_size` inside INT);
tests `Test_V7_List_InvalidPaging_422` (cases 1,000,001 and `int.MaxValue`) and the 1,000,000 boundary case.

Accepted / logged for a later change (not blocking, none contradict the spec):
- **D2** — if the audit write fails after a folder deselect the hard delete is already committed with no audit
  row. Same mutation-then-audit ordering as the existing add path; revisit with the audit design (FR-23) if the
  Product Owner wants atomic mutation + audit.
- **D3** — a file whose `file_index` row later becomes inactive keeps its Active quarantine row (still listed in
  the queue) but is skipped by folder remove and the state counts.
- **D4** — concurrent adds that hit the unique-index backstop / a deadlock are not mapped and return 500 (not 409).
- **D5** — single-file remove reads the audit path after the delete.
- **D6** — a missing request body returns 400, not 422.
- **D7** — no length validation on `folderPath` / `bucketName` (SP columns are 1000 / 255 chars).
- **SC-10 timing** — to be recorded in the review report (about 3 s for 50,000 files add + remove on a scratch DB).

## §15 Sign-off

| Role          | Name                   | Date       | Notes                                       |
| ------------- | ---------------------- | ---------- | ------------------------------------------- |
| Author        | Senior Systems Analyst | 2026-10-05 | draft, revised for per-file design          |
| Product Owner | Rakesh Devarajan       | 2026-10-05 | design decisions recorded in BRD v2.4 §12.5 |
| Reviewer      |                        |            |                                             |

## §16 Verification Plan

### What the Reviewer Will Check

- [ ] Current behavior (§1) matches the code before the change
- [ ] Desired behavior (§2) fully implemented; **no folder row is ever written**
- [ ] Only §5 files modified; §6 files untouched (esp. `dbo.quarantine_entry` DDL and delete path)
- [ ] All §10 SCs and §9 validations have passing tests
- [ ] §8 contract correct; paged `GET /quarantine` break declared and Web updated in the same release
- [ ] `[Authorize(Policy = "CachetFullAccess")]` on **all five** write actions (V11); one audit row per action, before 200
- [ ] Tests that asserted Read Only success on add/remove were updated to 403, not removed
- [ ] Folder prefix match uses `prefix + '/'` (no sibling bleed), escapes LIKE metacharacters
- [ ] Folder add/remove are single set-based statements (no per-file loops in C# or T-SQL cursors)

### Verification Report Format

Reviewer writes `CAS-050.review.md` (Verdict, per-SC table, scope violations, unintended blast radius,
security gaps).

## Implementation Log

**Completed:** 2026-10-05
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 88.2% | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20261005_120000__CAS-050-files-browse-quarantine.sql` | 5 new SPs + paged quarantine_entry_lst; SET QUOTED_IDENTIFIER/ANSI_NULLS ON |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_lst.sql` | paged list, source_tab |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_ins_browse_file.sql` | new |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_ins_browse_folder.sql` | new, set-based |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_del_browse_file.sql` | new |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_del_browse_folder.sql` | new, set-based |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_browse_state_lst.sql` | new |
| `Core/Dtos/QuarantineBrowse*.cs, QuarantineEntryListPage.cs, QuarantineBrowseFolderAddRow.cs` | new DTOs |
| `Core/Interfaces/IQuarantineBrowseService.cs` | new |
| `Core/Interfaces/IQuarantineEntryRepository.cs, IQuarantineListService.cs` | new methods / paged signature |
| `Core/Dtos/QuarantineListItem.cs, QuarantineEntryListRow.cs, QuarantineListResponse.cs` | SourceTab, TotalCount, Page, PageSize |
| `Api/Services/QuarantineBrowseService.cs` | new: add/remove/state + audit |
| `Api/Services/QuarantineListService.cs` | paging |
| `Api/Repositories/QuarantineEntryRepository.cs` | new SP calls; ListActiveAsync via paged SP |
| `Api/Controllers/QuarantineController.cs` | 3 new endpoints, paged GET, Full Access on add/remove |
| `Api/Controllers/SearchController.cs` | POST /search/results/quarantine -> CachetFullAccess |
| `Api/Program.cs` | DI registration |
| `Web.Tests (8 files) + KeywordSearchPageTests + ODriveSyncPageTests + AssemblyInfo.cs` | compile fixes for new DTO fields; stale banner assertion; modal waits; serial test execution |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `QuarantineBrowseServiceTests` | `Test_SC7, V3, V4 + service-level SC1/2/4/5/8/9` | SC-1..SC-9 (service), V3, V4 |
| `QuarantineBrowseControllerTests` | `Test_V1, Test_V2, Test_SC6` | V1, V2, SC-6 |
| `QuarantineBrowseRepositoryTests (real SQL, 16)` | `Test_SC1,2,3,4,5,8,9,10; Test_V5,V6,V8,V9; *_Sql` | SC-1..5, SC-8..10, V4..V10 |
| `QuarantineControllerTests (Api.Tests)` | `Test_V7_List_InvalidPaging_422` | V7 |
| `QuarantineControllerTests (IntegrationTests)` | `Test_V10, Test_V11, Test_SC12, ReadOnly_Returns403` | V10, V11, SC-11, SC-12 |

### Migration
`20261005_120000__CAS-050-files-browse-quarantine.sql`
