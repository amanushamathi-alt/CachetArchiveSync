---
id: CAS-049
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-04
priority: P2
change_type: enhancement
related_specs: [CAS-047, CAS-048, CAS-043, CAS-046, CAS-006, CAS-030]
touches:
  database: true
  api: true
  ui: false
  worker: false
  b2: false
  config: false
  security: true   # task-ownership check; folder path input
migrations: [20261004_230000__CAS-049-search-result-browse.sql]
---

# CAS-049 — Add: Browse a Completed Search's Results as Folders and Files (Api + Database)

Part 2 of 3. Needs **CAS-047** (results are B2-source, mode-scoped). **CAS-048** (Web) consumes it.
Implements the requester's 2026-10-04 instruction: Filename, Keyword and Date results appear in the file
explorer format, with the root folders that contain matches at the top, and clicking a folder opens its
subfolders and any matched files.

---

## §0 Decomposition decision (Karpathy P9)

Two endpoints (browse, plus `result-ids` added 2026-10-04), two stored procedures, 9 validations, 10 SCs,
about 10 files. Split from CAS-047 because it is
a distinct deliverable (a new endpoint) and CAS-047 alone is already near the file-count threshold.

---

## §1 Current Behavior

- `GET /search/{taskId}/results` returns two paged lists (`filenameMatches`, `contentMatches`) for a
  Completed task (`KeywordSearchService.GetResultsAsync`, `Api/Services/KeywordSearchService.cs:96`).
  There is no folder view of a task's results.
- `GET /files/browse` (CAS-043, `FileBrowseService`) lists folders and files of the B2 index through
  `dbo.file_index_browse_lst` (CAS-046 adds `has_subfolders`). It knows nothing about tasks or matches.
- Not verified here: whether `GET /search/{taskId}/results` checks that the task belongs to the caller.
  The new endpoint enforces ownership (V1) regardless.

## §2 Desired Behavior

**Amended 2026-10-04 (CAS-048 Q3/Q8):** a second endpoint, `GET /search/{taskId}/browse/result-ids`, resolves
a folder to the `searchResultId`s of every matched file beneath it, so the Web can offer folder checkboxes
without walking the tree (§8).

`GET /search/{taskId}/browse` returns the same folder/file shape as `GET /files/browse`, **limited to the
files that matched this task**, and to the folders on the way to them:

- Root (`folder` empty): the first-level folders (under the `Operations` root) that contain at least one
  match, plus matched files that sit directly in the root.
- A folder: its child folders that contain at least one match (with `hasSubfolders` computed over matches
  only), and the matched files directly in it. Unmatched siblings are not shown.
- Each file item also carries `searchResultId` (the id Quarantine needs) and `matchStatus`.
- Folder `latestModifiedAt` is the newest modified date among matched files beneath it.
- Works for every mode (Filename, Keyword, Date, legacy), because all of them are `search_result` rows.
- Same path rules as `/files/browse`: the Api composes the full key path from `B2:BucketName` and the
  mapped prefix; the client never sends or sees the prefix.

## §3 Reason for Change

- **Type:** enhancement (new endpoint).
- **Trigger:** requester 2026-10-04: results for all three modes should use the file-viewer format and
  replace the Filename Matches and Content Matches panels.
- **Risk if not changed:** the Web cannot render results as folders without loading and grouping every
  match in the browser.

## §4 Blast Radius (Graph Impact)

Manual read.

- **Primary:** new route on `SearchController`, new `ISearchResultBrowseService` (or an added method on
  `FileBrowseService`, decide at implementation by reading it first, P1/P4), new SP.
- **Direct dependents:** `FileBrowseService` path composition (reused, not changed), `ITaskQueueRepository`
  (task + owner lookup), `FolderBrowseResponse` / `BrowseFileItem` DTOs (additive fields).
- **Downstream modules:** Keyword Search results, Quarantine (consumes `searchResultId`).
- **Cross-project:** **CROSS-PROJECT RISK:** Core DTO change shared by Api and Web; fields are additive.
- **God nodes:** none touched.

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|-------------|
| `Database/migrations/20261004_230000__CAS-049-search-result-browse.sql` | add | Creates `dbo.search_result_browse_lst`. |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.search_result_browse_lst.sql` | add | Mirror. |
| `src/Cachet.ArchiveSync.Core/Dtos/BrowseFileItem.cs` | modify | `int? SearchResultId`, `string? MatchStatus` (null for `/files/browse`). |
| `src/Cachet.ArchiveSync.Core/Interfaces/` (browse service interface) | add/modify | `BrowseSearchResultsAsync(taskId, userId, folder, foldersOnly, ct)`. |
| `src/Cachet.ArchiveSync.Api/Services/FileBrowseService.cs` or a new sibling | modify/add | Compose path, call the SP, map rows (read the existing file first). |
| `src/Cachet.ArchiveSync.Api/Repositories/` (browse repository) | modify | Dapper call to the SP. |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.search_result_ids_by_folder_lst.sql` | add | Mirror of the amendment SP (same migration). |
| `src/Cachet.ArchiveSync.Api/Controllers/SearchController.cs` | modify | `GET search/{taskId:int}/browse` and `GET search/{taskId:int}/browse/result-ids`. |
| `Api.Tests`, `Api.IntegrationTests` (§13) | add | |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `dbo.file_index_browse_lst`, `GET /files/browse` | Files mode keeps working as is. |
| `GET /search/{taskId}/results` | Still used for completion and counts. |
| `TaskManagerController`, Quarantine endpoints | Unchanged. |

---

## §7 Database Changes

| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| `search_result_browse_lst` | dbo | **new** SP: `@task_queue_id`, `@bucket_name`, `@folder_path`, `@folders_only = 0`, outputs `@totalFolders`, `@totalFiles` | Same logic and escaping as `dbo.file_index_browse_lst`, but from `dbo.search_result sr JOIN dbo.file_index fi ON fi.file_index_id = sr.file_index_id WHERE sr.task_queue_id = @task_queue_id AND fi.is_active = 1 AND fi.source = N'B2'`. Result set 1: child folders (`folder_name`, `latest_modified_at`, `has_subfolders`). Result set 2 (not when `@folders_only = 1`): matched files directly in the folder (`file_index_id`, `search_result_id`, `file_name`, `file_extension`, `file_size_bytes`, `modified_at`, `match_status`), ordered by name then id. A file matched twice (Filename and Content, legacy) appears once. No limit on rows. |

Migration required: **Yes**, idempotent `CREATE OR ALTER` + mirror. `dbo.search_result` already has
`IX_search_result_task_queue_id_match_type` (leading column `task_queue_id`, read from the entity file
on 2026-10-04), which covers the task lookup; no new index. It also has
`UQ_search_result_task_file_matchtype`, so a file appears once per task and match type.

### Added SP (amendment): `dbo.search_result_ids_by_folder_lst`
`@task_queue_id`, `@bucket_name`, `@folder_path` (empty = whole task), `@max_ids` (1000 + 1, to detect
overflow). Same join, filters, escaping and de-duplication as `search_result_browse_lst`, but matches
**every depth** (`file_path LIKE @escaped_prefix + N'/%'` plus files directly in the folder). Returns
`search_result_id` ordered ascending, one row per file. Same migration file; `CREATE OR ALTER`.

## §8 API Contract Changes

### `GET /search/{taskId}/browse/result-ids` (amendment)
**Auth:** `CachetAuthenticated`. Synchronous read. Params: `folder` (string, default `""`, same rules as
browse; each value must have come from a browse response). **200:** `{ "searchResultIds": [5521, 5522], "count": 2 }`.
**Cap: 1,000 files.** More than 1,000 → **400 `selection-too-large`** with `{ "limit": 1000 }` and no ids.
Other codes as browse (401, 404 `task-not-found`/`folder-not-found`, 409 `task-not-complete`, 500 generic).
Not audited (read of own results). The quarantine write stays audited by the existing endpoint (FR-23).
The cap is also the Web's cap on the **total** selection per send; `POST /search/results/quarantine` itself
is not changed (it has no size limit today and inserts per id; 1,000 per request is within what it already
handles, to be confirmed by `Test_SC10_`).

### `GET /search/{taskId}/browse`
**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`. **Async:** no, a synchronous read.

| Param | Type | Default | Description |
|---|---|---|---|
| `folder` | string | `""` | Root-relative folder path (as `/files/browse`). |
| `foldersOnly` | bool | `false` | Folders only (tree pane). |

**Response 200:** the `FolderBrowseResponse` of CAS-043 §6 with these additions on each file:
`"searchResultId": 5521, "matchStatus": "Flagged"`. Example file:
`{ "fileIndexId": 100245, "searchResultId": 5521, "name": "HRButler 141104.01", "extension": "01", "sizeBytes": 1946, "modifiedAt": "2014-11-04T21:49:00Z", "matchStatus": "Flagged" }`.

| Code | Condition |
|------|-----------|
| 200 | Listed (an empty result set at the root is 200 with empty lists) |
| 400 | Validation failure (V2, V3) |
| 401 | No session |
| 404 | `task-not-found` (missing, not the caller's, or not a search task, same body for all three) or `folder-not-found` (V4) |
| 409 | `task-not-complete` (Queued/Running; Failed/Cancelled also return it) |
| 500 | Bucket or mapping not configured; database failure (generic body) |

No absolute path, root path, B2 key or search term appears in the response.

---

## §9 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | The task must belong to the caller (claim-derived user id) and be a `KeywordSearch` task. | 404 `task-not-found`, identical body for missing / not-owned / wrong type | `Test_V1_ForeignOrMissingTaskReturns404` |
| V2 | `folder` rules as CAS-043 V1/V2 (length, no leading/trailing `/`, no `//`, no `\`, no control chars, no `.`/`..`). | 400 | `Test_V2_MalformedOrTraversalFolderRejected` |
| V3 | `foldersOnly` is a valid boolean. | 400 | `Test_V3_NonBooleanFoldersOnlyRejected` |
| V4 | A non-empty `folder` must contain at least one match of this task. | 404 `folder-not-found` | `Test_V4_FolderWithNoMatchesReturns404` |
| V5 | `%`, `_`, `[` in `folder` match literally. | n/a | `Test_V5_WildcardsInFolderAreLiteral` |
| V6 | A database failure is not surfaced as SQL text; Warning log without the folder value. | 500 generic | `Test_V6_DatabaseFailureReturnsGeneric500` |
| V7 | `result-ids`: V1, V2, V4, V5 apply as for browse (ownership, folder rules, literal wildcards, folder must hold a match). | as browse | `Test_V7_ResultIdsSharesBrowseGuards` |
| V8 | `result-ids`: more than 1,000 matches beneath the folder returns no ids. | 400 `selection-too-large` | `Test_V8_OverCapReturns400WithoutIds` |
| V9 | `result-ids` returns only ids belonging to the requested task. | n/a | `Test_V9_IdsScopedToTask` |

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A Completed Filename task with matches in `A/B/x.xlsx` and `C/y.pdf`: root lists folders `A` and `C` only; folder `A` lists `B`; folder `A/B` lists `x.xlsx`. | `Test_SC1_ResultsAppearAsFolderHierarchy` |
| SC-2 | Unmatched siblings and unmatched files in the same folders are not returned. | `Test_SC2_OnlyMatchesAndTheirAncestors` |
| SC-3 | File items carry `searchResultId` and `matchStatus`; `searchResultId` can be sent to `POST /search/results/quarantine` for the same task. | `Test_SC3_FileItemsCarrySearchResultId` |
| SC-4 | Another user's task and a Queued task return 404 and 409 respectively. | `Test_SC4_OwnershipAndCompletionEnforced` |
| SC-5 | A task with 100,000 matches returns a folder in one response without paging; record elapsed time. | `Test_SC5_LargeResultSetBrowsesInOneCall` |
| SC-6 | A legacy task with the same file matched twice lists it once. | `Test_SC6_LegacyDuplicateMatchListedOnce` |
| SC-7 | The response contains no absolute path, B2 key, prefix or term. | `Test_SC7_NoServerPathOrTermInResponse` |
| SC-8 | `result-ids` for folder `A` returns the ids of matches at every depth beneath `A` (e.g. `A/x`, `A/B/y`) and not those in `C`. | `Test_SC8_ResultIdsIncludeAllDepths` |
| SC-9 | A folder with 1,001 matches returns 400 `selection-too-large`; exactly 1,000 returns 200. | `Test_SC9_ResultIdsCapBoundary` |
| SC-10 | The ids from `result-ids` (1,000 of them) are accepted by `POST /search/results/quarantine` for the same task; record elapsed time. | `Test_SC10_ResultIdsFeedQuarantineAtCap` |

## §11 Security Notes

Ownership is enforced server-side from the claim, never from a client value (V1). Same path-input rules
as CAS-043 (traversal rejected, wildcards literal). Not audited: a read of the caller's own results
(consistent with CAS-030 §12); file opens and downloads stay audited by the existing `/files` endpoints
before the first byte (FR-23). No logging of folder values or search terms.

## §12 Rollback Plan

Remove the route and the SP call; the SP can stay. No table change, no task-queue change. Web (CAS-048)
must be rolled back first, because it depends on this endpoint.

## §13 Test Plan

| Class | Method | Proves |
|-------|--------|--------|
| `SearchResultBrowseServiceTests` (unit) | `Test_V1_..`, `Test_V2_..`, `Test_V3_..`, `Test_V6_..` | Validation, ownership, error mapping |
| `SearchResultBrowseIntegrationTests` (seeded DB) | `Test_SC1_..`–`Test_SC10_..`, `Test_V4_..`, `Test_V5_..`, `Test_V7_..`–`Test_V9_..` | Hierarchy, scope, scale, no leakage, folder-to-ids, cap |

Coverage target: ≥ 85% on changed files.

## §14 Open Questions

Resolved 2026-10-04 (requester): the view shows only folders that lead to matches and only the matched
files, for drill-down (Filename and Keyword alike); Keyword snippets are not shown in the list (still
stored); results are B2-only, strictly (CAS-047 §2).

- [x] **Q8 — Folder-select endpoint and cap (from CAS-048).** Resolved 2026-10-04 on the author's
      recommendation: `GET /search/{taskId}/browse/result-ids`, cap 1,000 files, 400 `selection-too-large`
      above it, quarantine endpoint unchanged. 1,000 is a starting value chosen to keep one quarantine
      request small; **confirmed by devarajan@tracecounts.org 2026-10-04**, and SC-10 measures it. Change the number here
      and in CAS-048 together. *Re-reviewed 2026-10-04: keep 1,000.* Above the cap the user gets a clear
      400 and can select a narrower sub-folder, so nothing is silently dropped. Raise it only if SC-10
      shows 1,000 ids go through quarantine comfortably within the request timeout.
- [x] **Q1 — Retention.** Resolved 2026-10-04 on the author's recommendation (accept both behaviours;
      **confirmed by devarajan@tracecounts.org 2026-10-04**).
      - *Checked 2026-10-04 (grep of `Database/` and `src/`):* no stored procedure, job or service deletes
        `dbo.search_result` rows; the only `DELETE FROM dbo.search_result` is test cleanup in
        `SearchResultFilenameRepositoryTests`. `FK_search_result_file_index` has no `ON DELETE CASCADE`,
        so a `file_index` row cannot be hard-deleted while a result references it; removals are soft
        deletes (`is_active = 0`, `dbo.file_index_soft_delete_upd`).
      - *Decision:* keep the `fi.is_active = 1` filter. A file that was deleted after the search drops out
        of the reloaded view, and the folder/file counts shrink to match. This is correct, because the
        user could not act on a missing file anyway (open, download and quarantine would fail).
      - *Decision:* no retention rule is added here. Completed searches stay browsable for as long as
        their rows exist. A purge policy for old tasks would be its own spec; if ever added, it must
        return 404 `task-not-found` for a purged task (already covered by V1).
      - *Not verified:* whether DBAs run a manual or external clean-up outside this repository.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code (drafted for devarajan@tracecounts.org) | 2026-10-04 | Draft; not reviewed. Q1 and Q8 answered on author's recommendation and confirmed by devarajan@tracecounts.org 2026-10-04 |
| Reviewer | | | |

## §16 Verification Plan

- [ ] Hierarchy and match-only scope (SC-1, SC-2); `searchResultId` usable for Quarantine (SC-3)
- [ ] Ownership and completion enforced; identical 404 bodies (V1, SC-4)
- [ ] Path rules reused from CAS-043; no leakage (V2, V5, SC-7)
- [ ] Elapsed time for the large-result browse recorded (SC-5); existing `task_queue_id` index used
- [ ] Only §5 files changed; nothing in §6 touched

Reviewer writes `CAS-049.review.md`.

## Implementation Log

**Completed:** 2026-10-05
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 86.9% (ISearchResultBrowseService.cs 66.7%, declarations only) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20261004_230000__CAS-049-search-result-browse.sql` | add |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.search_result_browse_lst.sql` | add |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.search_result_ids_by_folder_lst.sql` | add |
| `src/Cachet.ArchiveSync.Core/Dtos/SearchResultBrowseRows.cs` | add |
| `src/Cachet.ArchiveSync.Core/Dtos/BrowseFileItem.cs` | modify (SearchResultId, MatchStatus; omitted when null) |
| `src/Cachet.ArchiveSync.Core/Interfaces/ISearchResultBrowseRepository.cs` | add |
| `src/Cachet.ArchiveSync.Core/Interfaces/ISearchResultBrowseService.cs` | add |
| `src/Cachet.ArchiveSync.Api/Repositories/SearchResultBrowseRepository.cs` | add |
| `src/Cachet.ArchiveSync.Api/Services/SearchResultBrowseService.cs` | add |
| `src/Cachet.ArchiveSync.Api/Services/FileBrowseService.cs` | modify (folder validation private to internal) |
| `src/Cachet.ArchiveSync.Api/Controllers/SearchController.cs` | modify (2 routes) |
| `src/Cachet.ArchiveSync.Api/Program.cs` | modify (DI) |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `SearchResultBrowseServiceTests` | `Test_V1, V2, V3, V6` | V1,V2,V3,V6 |
| `SearchResultBrowseIntegrationTests` | `Test_SC1..SC10, V4, V5, V7, V8, V9` | SC-1..SC-10, V4, V5, V7-V9 |
| `FilesBrowseControllerTests` | `Test_CAS049_FilesBrowseJsonOmitsSearchResultIdAndMatchStatus` | additive DTO |

### Migration
`20261004_230000__CAS-049-search-result-browse.sql`
