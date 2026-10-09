---
id: CAS-047
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-04
priority: P2
change_type: enhancement
related_specs:
  [
    CAS-048,
    CAS-049,
    CAS-006,
    CAS-012,
    CAS-035,
    CAS-038,
    CAS-030,
    CAS-043,
    CAS-044,
  ]
touches:
  database: true
  api: true
  ui: false # UI half is CAS-048
  worker: true
  b2: false # no new B2 call type; Filename mode makes no B2 call at all
  config: false
  security: true # user-supplied text reaches a LIKE predicate; term must stay out of logs
migrations: [20261004_220000__CAS-047-search-modes.sql]
---

# CAS-047 — Change: Search Modes — Filename (B2 index, LIKE) / Keyword (file content) / Date (backend)

Part 1 of 3. **CAS-049** (results browse endpoint) and **CAS-048** (Web) depend on this spec.

**Requirement status:** BRD v2.3 FR-05/FR-06/FR-07 describe one combined "Keyword Search". The split is a
client-facing change. **The requester confirmed the client approved it (2026-10-04).** The BRD itself has
not been revised; add an FR-05/06/07 revision note when it next is.

---

## §0 Decomposition decision (Karpathy P9)

Split signals fired: project boundary (Database, Api, Core, Worker, Web) and changed-file count (> 15
overall). Split into **CAS-047** (this: mode, Filename SP, Worker, DTOs), **CAS-049** (Api + Database:
browse a task's results as folders and files) and **CAS-048** (Web). This spec alone: 0 new endpoints,
2 database objects, 7 validations, 10 SCs, about 13 files.

---

## §1 Current Behavior

Read from code on 2026-10-04 (manual read; the graph cache predates these files):

- One path for every mode: `POST /search` → `KeywordSearchService.SubmitAsync`
  (`Api/Services/KeywordSearchService.cs:45`) enqueues `task_type = 'KeywordSearch'` with a
  `KeywordSearchRequestDto` (`Term, Bucket, Extension, FromDate, ToDate`). **No mode field**: Date mode is
  inferred as blank term + both dates (CAS-038 A2).
- `KeywordSearchTaskProcessor.ProcessAsync` (`Worker/KeywordSearchTaskProcessor.cs:86`) calls
  `SearchCandidatesAsync` → `dbo.file_index_search`, which returns both branches:
  _Filename_ rows (`file_name LIKE '%term%' OR relative_path LIKE '%term%'`, **all sources**, no wildcard
  escaping) and _Content_ candidates (every active in-scope row; the Worker reads the mirror file and keeps
  only term matches, CAS-012).
- Each kept row is inserted one at a time (`ISearchResultRepository.InsertAsync`) after a **B2
  cross-check** per row (`CrossCheckAsync`). Statuses: Filename `Flagged`, Content `Needs Review`,
  date-only `Informational`.
- `GET /tasks` and `GET /tasks/{id}` expose no mode.

## §2 Desired Behavior

A search request carries an explicit **mode**, stored in the task payload and returned on reads.

| Mode       | Matching                                                                                                                             | Source                                                                                 | B2 cross-check                                                  |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------- | --------------------------------------------------------------- |
| `Filename` | `file_name LIKE '%<input>%'` **on the file name only** (not the path), trimmed input **3–200 characters**; `\ % _ [` match literally | `dbo.file_index` rows with `source = 'B2'` only (database content; no file or B2 read) | **None** (requester 2026-10-04: "against the database content") |
| `Keyword`  | Term found **inside file content** (CAS-012 mirror read + extractor), trimmed input **3–200 characters**; **no** filename-only rows  | **B2 source only, strictly** (requester 2026-10-04)                                    | Unchanged (CAS-006/035)                                         |
| `Date`     | Unchanged: **created date** (`created_at`) range, blank term (CAS-038 A2); confirmed sufficient by the requester                     | **B2 source only, strictly**                                                           | Unchanged                                                       |

- **Why B2 only for all three:** results are displayed in the B2-archive explorer (CAS-048/049). A match
  from the O-Drive source would be stored but never shown.
- **Filename storage:** set-based. One SP inserts every match into `dbo.search_result` for the task
  (`match_type = 'Filename'`, status `Flagged`) and returns the inserted count. No per-row round trip and
  no B2 call, so a broad match is one SQL statement, not thousands of calls.
- Common to all modes: bucket and extension filters, `search_result` storage (Quarantine keeps working off
  `search_result_id`), audit before the 202, Task Manager visibility, progress and completion
  (CAS-029).
- **Mode is optional on the wire for one release.** A request or stored task without `mode` behaves
  exactly as today (legacy), so an older Web or an in-flight task does not break.
- `GET /tasks` items gain `searchMode`; `GET /tasks/{id}` `searchParameters` gains `mode`. Both `null` for
  legacy and non-search tasks.

## §3 Reason for Change

- **Type:** enhancement.
- **Trigger:** requester instruction 2026-10-04: split Keyword Search into a B2-index filename search
  (`LIKE`, minimum 3 characters) and a content search, keep Date Search, and show all results in the file
  viewer format.
- **Risk if not changed:** one ambiguous search returns filename, content and O-Drive rows together, and
  the new explorer-style results (CAS-049) have no per-mode, B2-scoped result set to browse.

## §4 Blast Radius (Graph Impact)

Manual read; graphify not run for this draft.

### Primary Change Target

`KeywordSearchService` (submit), `KeywordSearchTaskProcessor` (execute), `dbo.file_index_search`
(new optional `@source`), task read projections.

### Direct Dependents

`SearchController` (binding); `Core/Dtos`: `SearchRequest`, `KeywordSearchRequestDto`, `TaskListItem`, the
DTO holding `TaskSearchParameters` (located at implementation); `IFileIndexRepository` /
`FileIndexRepository` (`SearchCandidatesAsync`, used by the Worker); `TaskManagerService.cs:134,170`;
tests `KeywordSearchServiceTests`, `KeywordSearchTaskProcessorTests`, `TaskManagerServiceTests`,
`FileIndexRepositoryTests`, `SyncTaskProcessorLogHygieneTests` (calls `SearchCandidatesAsync`).

### Downstream Modules

Keyword Search, Task Manager, Audit Log (detail text only). Quarantine reads `search_result` unchanged.
Reporting reads `search_result` by `match_type` and needs no change (§14 Q4). Not affected: Auth, O-Drive Sync and Content Scan.

### Cross-Project Risk

**CROSS-PROJECT RISK:** Core DTOs are shared by Api, Worker and Web; every new field is optional/nullable.
Deploy order in §12.

### God Node Involvement

`KeywordSearchTaskProcessor` (375 lines, central to FR-05/07/08): **HIGH RISK by centrality**. Changes
are additive branches on `request.Mode`; the legacy path must remain behavior-equivalent (SC-6). The
Date and Keyword paths change only by the B2-source filter (requester decision 2026-10-04).

---

## §5 Files to Change

| File                                                                                                         | Change Type | Description                                                                                                                                      |
| ------------------------------------------------------------------------------------------------------------ | ----------- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Database/migrations/20261004_220000__CAS-047-search-modes.sql`                                              | add         | Creates `dbo.search_result_filename_ins`; alters `dbo.file_index_search` to add optional `@source` (`CREATE OR ALTER`, idempotent, append-only). |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.search_result_filename_ins.sql`                            | add         | Mirror.                                                                                                                                          |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql`                                     | modify      | Mirror of the new optional `@source NVARCHAR(10) = NULL` (NULL = all sources, so legacy calls are unchanged).                                    |
| `src/Cachet.ArchiveSync.Core/Dtos/SearchRequest.cs`                                                          | modify      | `string? Mode`.                                                                                                                                  |
| `src/Cachet.ArchiveSync.Core/Dtos/KeywordSearchRequestDto.cs`                                                | modify      | `string? Mode`.                                                                                                                                  |
| `src/Cachet.ArchiveSync.Core/Dtos/TaskListItem.cs`                                                           | modify      | `string? SearchMode` (field only; populated by the follow-up spec, see Amendment 2026-10-04).                                                    |
| DTO holding `TaskSearchParameters` (Core/Dtos)                                                               | modify      | `string? Mode`.                                                                                                                                  |
| `src/Cachet.ArchiveSync.Core/Interfaces/ISearchResultRepository.cs` + Api implementation                     | modify      | `InsertFilenameMatchesAsync(taskId, bucket, extension, term, actor, ct)` returning the count.                                                    |
| `src/Cachet.ArchiveSync.Core/Interfaces/IFileIndexRepository.cs` + `Api/Repositories/FileIndexRepository.cs` | modify      | `SearchCandidatesAsync` gains an optional `source` argument.                                                                                     |
| `src/Cachet.ArchiveSync.Api/Services/KeywordSearchService.cs`                                                | modify      | Mode validation, persist mode, audit detail.                                                                                                     |
| `src/Cachet.ArchiveSync.Api/Services/TaskManagerService.cs`                                                  | modify      | Project `searchMode` / `mode`.                                                                                                                   |
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs`                                                | modify      | Branch on `request.Mode` (§2).                                                                                                                   |
| Tests (§13)                                                                                                  | add/modify  |                                                                                                                                                  |
| `FileIndexRepositoryTests`, `FilesBrowseRepositoryTests`, `SearchResultRepositoryTests`, `QuarantineEntryRepositoryTests` (Api.IntegrationTests) | modify | Test infrastructure only: `[Collection("CAS-047 file_index real-DB")]` serializes real-DB classes that write `file_index` (avoids SQL deadlocks). `FileIndexRepositoryTests` also re-applies the CAS-047 migration after CAS-038. |

## §6 Files NOT to Change

| File                                                              | Why left untouched                                                                                             |
| ----------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------- |
| `FileContentMatcher`, `MirrorFileReader`, `IB2Client`, `B2Client` | Content matching and cross-check reused as is.                                                                 |
| `SearchController` routes, `GET /search/{id}/results`             | Shape unchanged. The Web stops rendering its two panels (CAS-048) but still uses it for completion and counts. |
| `dbo.task_queue`, `CK_task_queue_task_type`                       | No new task type (§14 Q2).                                                                                     |
| O-Drive Sync / Content Scan services, Quarantine service          | Out of scope.                                                                                                  |

---

## §7 Database Changes

| Object                       | Schema | Change                                                                                                                   | Notes                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| ---------------------------- | ------ | ------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `search_result_filename_ins` | dbo    | **new** SP: `@task_queue_id`, `@bucket_name`, `@file_extension = NULL`, `@term`, `@actor`, output `@inserted INT`        | `INSERT INTO dbo.search_result (...same columns as dbo.search_result_ins...) SELECT ... FROM dbo.file_index fi WHERE fi.is_active = 1 AND fi.source = N'B2' AND fi.bucket_name = @bucket_name AND (@file_extension IS NULL OR @file_extension = N'All' OR fi.file_extension = @file_extension) AND fi.file_name LIKE N'%' + @escaped + N'%' ESCAPE N'\'`. `@escaped` escapes `\`, `%`, `_`, `[` (same method as `dbo.file_index_browse_lst`). Raises on blank bucket, blank term, or trimmed term shorter than 3. `match_type = 'Filename'`, status `Flagged`, snippet NULL. Idempotent per task (no duplicate row for the same `file_index_id` and task). |
| `file_index_search`          | dbo    | add `@source NVARCHAR(10) = NULL`; when set, both branches add `AND fi.source = @source`; value must be `ODrive` or `B2` | Defaults keep every existing caller unchanged.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |

Migration required: **Yes**, both objects in one `CREATE OR ALTER` script plus mirrors. The leading-wildcard
`LIKE` cannot seek on `file_name`; it scans the B2 slice (876,554 rows per CAS-044). **Elapsed time is not
measured here**; record it on the seeded database before review (§14 Q3).

## §8 API Contract Changes

| Endpoint              | Field                   | Before | After                                  | Breaking? |
| --------------------- | ----------------------- | ------ | -------------------------------------- | --------- |
| `POST /search`        | `mode`                  | absent | optional `Filename \| Keyword \| Date` | No        |
| `GET /tasks`          | `items[].searchMode`    | absent | field added, **always null in this spec** (populated by the follow-up spec, see Amendment) | No        |
| `GET /tasks/{taskId}` | `searchParameters.mode` | absent | string or null                         | No        |

---

## §9 Validations Changed

| ID  | Rule (Before)                                     | Rule (After)                                                                                                                                                                                                                                                             | Test                                                    |
| --- | ------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------- |
| V1  | n/a                                               | `mode`, when present, is exactly `Filename`, `Keyword` or `Date` (case-sensitive), else `422 validation-failed`.                                                                                                                                                         | `Test_V1_UnknownModeRejected`                           |
| V2  | Term 1–200 chars unless date-only                 | `Filename`: trimmed term **3–200 characters**, else 422 (message states the 3-character minimum). Counted after trim; wildcard characters count as characters. `Keyword`: trimmed **3–200** as well (requester 2026-10-04). Both require `fromDate`/`toDate` to be null. | `Test_V2_FilenameAndKeywordMinThreeCharsAndForbidDates` |
| V3  | Date-only rules V11–V14 (CAS-038)                 | `Date`: term blank; both dates required; V11–V14 unchanged.                                                                                                                                                                                                              | `Test_V3_DateModeRequiresBlankTermAndRange`             |
| V4  | `LIKE '%' + @term + '%'` unescaped                | `Filename` escapes `\ % _ [` so they match literally.                                                                                                                                                                                                                    | `Test_V4_FilenameWildcardsMatchLiterally`               |
| V5  | Filename rows from all sources, plus path matches | `Filename` mode: `source = 'B2'` and `file_name` only. `Keyword` and `Date`: `source = 'B2'` only. `Keyword` stores no Filename-type rows.                                                                                                                               | `Test_V5_ModesScopedToB2AndTheirMatchType`              |
| V6  | n/a                                               | Absent `mode` (request or stored payload) = legacy behavior, unchanged (all sources, both branches).                                                                                                                                                                     | `Test_V6_AbsentModeIsLegacy`                            |
| V7  | n/a                                               | The SP rejects a blank or under-3-character Filename term even if the Api is bypassed (defence in depth).                                                                                                                                                                | `Test_V7_SpRejectsShortFilenameTerm`                    |

---

## §10 Success Criteria

| ID    | Criterion                                                                                                                                                                  | Test                                                       |
| ----- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- |
| SC-1  | `POST /search` with `mode=Filename` returns 202, stores `Mode` in `request_parameters`, and writes the audit row (detail names the mode, never the term) before returning. | `Test_SC1_SubmitFilenamePersistsModeAndAuditsBeforeAccept` |
| SC-2  | `Filename`: a name containing the input in a B2 row is stored; an O-Drive row with the same name and a row matching only through a folder name are **not**.                | `Test_SC2_FilenameMatchesB2FileNameOnly`                   |
| SC-3  | Input `100%_[final]` matches only names containing that literal text.                                                                                                      | `Test_SC3_FilenameInputWildcardsAreLiteral`                |
| SC-4  | `Filename` makes **no** B2 call and no mirror read; matches are stored `Flagged` in one set-based insert and the task completes with the count.                            | `Test_SC4_FilenameModeNoB2NoMirrorSetBasedInsert`          |
| SC-5  | `Keyword` stores only content matches (B2 rows), zero Filename-type rows; `Date` results match the pre-change fixture restricted to B2.                                    | `Test_SC5_KeywordContentOnlyDateUnchanged`                 |
| SC-6  | A task stored without `mode` runs both branches as today and reads back with `mode = null`.                                                                                | `Test_SC6_LegacyTaskUnchanged`                             |
| SC-7  | `GET /tasks/{id}` returns `searchParameters.mode`; null for legacy and non-search types. (`GET /tasks` `searchMode` moved to the follow-up spec, see Amendment 2026-10-04.) | `Test_SC7_TaskReadsExposeMode`                             |
| SC-8  | `Keyword` and `Date` keep the CAS-006/CAS-035 cross-check outcomes (confirmed, excluded, indeterminate).                                                                   | `Test_SC8_KeywordDateCrossCheckUnchanged`                  |
| SC-9  | No log line or audit detail contains the search term.                                                                                                                      | `Test_SC9_TermNeverLogged`                                 |
| SC-10 | The Pre-Action Report lists a Filename-mode result under "Filename Matches" and a Keyword-mode result under "Content Matches".                                             | `Test_SC10_ReportsLabelNewModeResultsByMatchType`          |

---

## §11 Security Notes

- **LIKE input:** the term stays a bound parameter (no SQL injection). Escaping (V4) stops a `%` or `_`
  input from matching everything. The 3-character minimum (V2, V7) limits near-total result sets.
- **FR-23:** `KeywordSearchSubmitted` is written before the 202; the mode is in the detail text only,
  never the term (CAS-006 §11).
- **FR-03:** no key surface change. Filename mode never touches B2.
- **Data minimization:** Filename mode reads index metadata only, not file content.

## §12 Rollback Plan

1. Deploy order: migration → Api → Worker → CAS-049 → CAS-048 (Web). Rollback in reverse.
2. Revert Api/Worker: queued tasks that already carry `Mode` deserialize with it ignored and run as legacy
   (both branches, all sources). Correct but broader. Let `Queued` rows finish first.
3. Database: both objects can stay (`@source` defaults to NULL; the new SP is uncalled). No table change.
4. Task queue: `task_type` and constraints unchanged, so no in-flight row is dropped or corrupted.

## §13 Test Plan

| Class                                               | Method                                                                                                | Proves                                         |
| --------------------------------------------------- | ----------------------------------------------------------------------------------------------------- | ---------------------------------------------- |
| `KeywordSearchServiceTests`                         | `Test_V1_..`–`Test_V3_..`, `Test_SC1_..`                                                              | Validation, persistence, audit order           |
| `KeywordSearchTaskProcessorTests`                   | `Test_SC4_..`, `Test_SC5_..`, `Test_SC6_..`, `Test_SC8_..`, `Test_SC9_..`, `Test_V5_..`, `Test_V6_..` | Mode branches, legacy, cross-check             |
| `FileIndexRepositoryTests` (integration, seeded DB) | `Test_V4_..`, `Test_V7_..`, `Test_SC2_..`, `Test_SC3_..`                                              | Escaping, B2-only, name-only, set-based insert |
| `TaskManagerServiceTests`                           | `Test_SC7_..`                                                                                         | Mode projection                                |
| `PreActionReportServiceTests`                       | `Test_SC10_..`                                                                                        | Reports unchanged for new modes                |

Coverage target: ≥ 85% on changed files.

## §14 Open Questions

All resolved; none open.

Resolved 2026-10-04 (requester): minimum term is 3 characters for Filename and Keyword; B2-only is strict
for all three modes; Date stays on created date; Filename search is against the `dbo.file_index` table,
`source = 'B2'`, `LIKE`, no B2 cross-check; client approval of the split confirmed.

- **Q2 — Task shape (resolved).** Keep `task_type = 'KeywordSearch'` plus a `Mode` field; no new task types
  and no `CK_task_queue_task_type` change. Reason: a new type would need a migration and edits to every
  task-type switch, and nothing downstream needs the types to differ. Revisit only if tasks must be
  filtered by mode in SQL.
- **Q3 — Performance (resolved as an acceptance step).** No index or computed column now. Before review,
  run Filename search on the seeded database (876,554 rows) with one common and one rare 3-character
  term and record the elapsed times in the Implementation Log. If a common term takes more than about 30
  seconds, raise a follow-up spec (for example trigram or full-text). Not measured at drafting time.
- **Q3 result (measured 2026-10-04, seeded DB, 876,554 active B2 rows, `dbo.search_result_filename_ins`, run in a rolled-back transaction):** common term `pdf` inserted 221,028 rows in 11,191 ms; rare term `zzq` inserted 0 rows in 798 ms. Both under the 30 s threshold, so no follow-up index spec is raised.
- **Q4 — Reports (resolved: no change).** `PreActionReportService` reads every active `search_result` row
  and labels it "Filename Matches" / "Content Matches" from `match_type` alone. The new modes keep those
  match types (Filename and Date store `Filename`; Keyword stores `Content`), so no report change is
  needed. Add the test `Test_SC10_ReportsLabelNewModeResultsByMatchType`. Report wording is left as is.
  `PostActionReportService` reads audit actions, not `search_result`; its query was not read in detail.

## Amendment 2026-10-04 (review CAS-047 CHANGES REQUIRED, resolved)

- **SC-7 list half moved to a follow-up spec.** `dbo.task_queue_lst` deliberately omits `request_parameters` (CAS-030 §12); populating `GET /tasks` `items[].searchMode` needs SP, `TaskQueueListRow` and `TaskQueueRepository` changes outside §5. This spec ships the `SearchMode` DTO field (null) and the detail half only. The follow-up spec must be drafted (`/spec-change`) and signed off before CAS-048 relies on list-level mode. **Sign-off of this deferral is pending the requester.**
- Validation failures return 422 (matches code and existing tests); §9 corrected from 400.
- §5 now lists the integration-test `[Collection]` changes.
- Q3 timings recorded in §14.

## §15 Sign-off

| Role                         | Name                                                | Date       | Notes                                           |
| ---------------------------- | --------------------------------------------------- | ---------- | ----------------------------------------------- |
| Author                       | Claude Code (drafted for devarajan@tracecounts.org) | 2026-10-04 | Draft; revised same day after requester answers |
| Client approval of the split | via devarajan@tracecounts.org                       | 2026-10-04 | Confirmed by the requester                      |
| Reviewer                     |                                                     |            |                                                 |

## §16 Verification Plan

- [ ] §1 matches the code before the change
- [ ] Filename: `file_name` only, B2 only, ≥ 3 characters, wildcards literal, no B2 call (V2, V4, V5, SC-4)
- [ ] Legacy path unchanged (SC-6); Keyword/Date change only by the B2 filter
- [ ] Audit precedes the 202; no term in logs or audit detail (SC-1, SC-9)
- [ ] Only §5 files changed; nothing in §6 touched
- [ ] Migration idempotent and mirrored; §12 deploy order documented

Reviewer writes `CAS-047.review.md`.

## Implementation Log

**Completed:** 2026-10-04
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 86.4% (warn accepted by user: FileIndexRepository 44.2%, SearchResultRepository 24.7%, IKeywordSearchService 71.4%) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Core/Dtos/SearchRequest.cs` | Mode field |
| `Core/Dtos/KeywordSearchRequestDto.cs` | Mode field |
| `Core/Dtos/TaskListItem.cs` | SearchMode field (unpopulated; BLOCKED, follow-up needed) |
| `Core/Dtos/TaskDetailResponse.cs` | searchParameters.mode |
| `Core/Interfaces/ISearchResultRepository.cs` | InsertFilenameMatchesAsync |
| `Core/Interfaces/IFileIndexRepository.cs` | optional source param |
| `Api/Repositories/SearchResultRepository.cs` | calls dbo.search_result_filename_ins |
| `Api/Repositories/FileIndexRepository.cs` | passes @source |
| `Api/Services/KeywordSearchService.cs` | mode validation, persistence, audit |
| `Api/Services/TaskManagerService.cs` | projects searchParameters.mode |
| `Worker/KeywordSearchTaskProcessor.cs` | Filename/Keyword/Date mode branches |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `KeywordSearchServiceTests` | `Test_SC1_SubmitFilenamePersistsModeAndAuditsBeforeAccept` | SC-1 |
| `SearchResultFilenameRepositoryTests` | `Test_SC2_FilenameMatchesB2FileNameOnly` | SC-2 |
| `SearchResultFilenameRepositoryTests` | `Test_SC3_FilenameInputWildcardsAreLiteral` | SC-3 |
| `SearchResultFilenameRepositoryTests` | `Test_SC3_FilenameInputBackslashIsLiteral` | SC-3 |
| `KeywordSearchTaskProcessorTests` | `Test_SC4_FilenameModeNoB2NoMirrorSetBasedInsert` | SC-4 |
| `KeywordSearchTaskProcessorTests` | `Test_SC5_KeywordContentOnlyDateUnchanged` | SC-5 |
| `KeywordSearchTaskProcessorTests` | `Test_SC6_LegacyTaskUnchanged` | SC-6 |
| `TaskManagerServiceTests` | `Test_SC7_TaskReadsExposeMode` | SC-7 (detail half only) |
| `KeywordSearchTaskProcessorTests` | `Test_SC8_KeywordDateCrossCheckUnchanged` | SC-8 |
| `KeywordSearchTaskProcessorTests` | `Test_SC9_TermNeverLogged` | SC-9 |
| `PreActionReportServiceTests` | `Test_SC10_ReportsLabelNewModeResultsByMatchType` | SC-10 |

### Migration
`20261004_220000__CAS-047-search-modes.sql`
