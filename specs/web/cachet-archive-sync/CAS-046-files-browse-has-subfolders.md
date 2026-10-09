---
id: CAS-046
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-04
priority: P3
change_type: enhancement
related_specs: [CAS-043, CAS-044]
touches:
  database: true   # dbo.file_index_browse_lst: one extra column in result set 1 (no signature change)
  api: true        # GET /files/browse: folders[].hasSubfolders (additive); Core DTO + Api mapping
  ui: true         # Files explorer tree shows a chevron only on folders that have subfolders
  worker: false
  b2: false
  config: false
  security: false  # no new data exposed beyond folder names the user can already browse
migrations: [Database/migrations/20261004_210000__CAS-046-files-browse-has-subfolders.sql]
---

# CAS-046 — Change: Files Explorer Tree Shows a Chevron Only on Folders That Have Subfolders

Follow-up to `CAS-043` (Files Browse API) and `CAS-044` (Files Explorer UI), both in `review`. Found
when the implemented tree was compared with the approved wireframe (2026-10-04).

**Placement.** Filed under `specs/web/` because the visible deliverable is the tree behaviour
(CLAUDE.md: ui > api > database when a spec fits more than one kind). Most of the changed lines are in
the SP and the Api mapping.

---

## §1 Current Behavior

- `GET /files/browse` returns each child folder as `{ name, path, latestModifiedAt }`
  (`Core/Dtos/BrowseFolderItem.cs`). It does not say whether the folder has subfolders.
- `FileExplorer.razor` (`HasChevron`) shows the expand chevron (▸) on every tree node whose children
  have not been loaded yet: `node.Children is null || node.Children.Count > 0`. A new node always has
  `Children == null`, so **every** child folder shows a chevron.
- Clicking the chevron of a leaf folder issues a `foldersOnly=true` call, gets an empty list, and the
  chevron then disappears. The user cannot tell a leaf from a branch before clicking.
- The approved wireframe shows a chevron only on folders that have subfolders (Windows File Explorer
  behaviour). On the production-size index the root has 250 child folders; the wireframe shows one
  with a chevron.
- `dbo.file_index_browse_lst` (`Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_browse_lst.sql`)
  groups the rows under the prefix by first path segment and returns `folder_name,
  latest_modified_at`. The per-row remainder is already computed in that scan.

---

## §2 Desired Behavior

1. `GET /files/browse` returns `hasSubfolders` (boolean) on every item of `folders[]`. It is `true`
   when at least one active row beneath that folder sits in a deeper folder, that is, the folder
   contains a subfolder with a non-empty name; `false` when the folder holds only files directly.
2. The explorer tree shows a chevron on a not-yet-loaded node only when `hasSubfolders` is `true`.
   A node whose children are loaded keeps today's rule (chevron only if it has at least one child).
3. The list pane is unchanged (it never shows chevrons).
4. The value comes from the same single `GROUP BY` pass as `latest_modified_at`, with no extra query
   and no new temp table. Browse timing on production-size data does not get materially worse
   (§10 SC-7).
5. The change is additive and tolerant: a Web build that receives a response without the field (older
   Api) behaves as today (chevron shown), so Api and Web can deploy in either order.

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** requester review of the implemented Files screen against the approved wireframe
  (2026-10-04): "draft the change spec for 'has subfolders'".
- **Risk if not changed:** the tree shows hundreds of chevrons that do nothing for leaf folders, and
  each click on one costs a database call (5-7 s at the root level on production-size data) to learn
  that there is nothing to expand.

---

## §4 Blast Radius (Graph Impact)

> graphify not used: the graph (built 2026-10-01) predates `CAS-043`/`CAS-044` and has no nodes for
> these types. Blast radius mapped via manual grep and read of every reference to
> `BrowseFolderItem`, `FileBrowseFolderRow`, `latest_modified_at` and `HasChevron`.

### Primary Change Target
File: `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_browse_lst.sql` (first),
carried through `Core/Dtos/BrowseFolderItem.cs` and `Web/Components/Shared/FileExplorer.razor`.
Entity: the child-folder row of the browse result.
Current edges (manual read): SP → `FileBrowseRepository` (Dapper `FolderRow`) → `FileBrowseFolderRow`
→ `FileBrowseService` (maps to `BrowseFolderItem`) → `FilesController` (serialises) → Web
`FilesBrowseClient` (`ReadFromJsonAsync<FolderBrowseResponse>`) → `FileExplorer` (`TreeNode`).

### Direct Dependents (references to the changed types)
| File | Reference |
|---|---|
| `Api/Repositories/FileBrowseRepository.cs` | maps `FolderRow` to `FileBrowseFolderRow` |
| `Api/Services/FileBrowseService.cs` | maps `FileBrowseFolderRow` to `BrowseFolderItem` |
| `Core/Dtos/FileBrowseRows.cs`, `Core/Dtos/BrowseFolderItem.cs` | the types changed |
| `Core/Dtos/FolderBrowseResponse.cs` | holds `IReadOnlyList<BrowseFolderItem>`; shape unchanged |
| `Web/Components/Shared/FileExplorer.razor` | builds `TreeNode`s from `BrowseFolderItem` |
| Tests: `Api.Tests/FileBrowseServiceTests.cs`, `Api.IntegrationTests/{FilesBrowseRepositoryTests, FilesBrowseControllerTests, Files/BrowseHttpHarness}.cs`, `Web.Tests/FileExplorer/{BrowseFixtures, FileExplorerTests}.cs` | construct or assert these types |

Four construction sites build `BrowseFolderItem`/`FileBrowseFolderRow` today. Both new members are
**not** `required` and default to `true`, so existing call sites compile and behave as before.

### Downstream Modules
Keyword Search (the screen the explorer lives on). Not affected: Auth, O-Drive Sync, Quarantine, Task
Manager, Audit Log, Reporting. `/files/{id}/view|download` and the audit trail are untouched.

### Cross-Project Risk
**CROSS-PROJECT RISK (Api <-> Web, via Core).** `BrowseFolderItem` lives in `Core` and is used by
both Api (serialise) and Web (deserialise). Mitigation: the new property is optional with default
`true`, so a response without it still produces today's behaviour (§2.5). No Worker involvement.

### God Node Involvement
None identified by manual read. `FileExplorer.razor` is a large component but is not shared. The SP
is called by one repository method only.

### Graphify Queries Run
graphify not yet covering this code. Analysis by manual grep/read.

### Decomposition check (P9)
One signal fires: **test-domain split** (Api xUnit plus real-SQL integration tests, and Web bUnit
tests). No size threshold is crossed (0 endpoints, §9 validations 0, §10 SCs 7, 1 SP changed, about 12
files). **Project boundary:** Database + Api + Core + Web are touched, but each piece is one field or
one method, the SP column serves this one endpoint and has no other consumer, and `CAS-043`/`CAS-044`
are already split along these seams. Guideline §2.1 allows a thin slice like this to stay combined.
**Decision: single spec, no split.** Splitting would force a contract spec and a one-method UI spec
that could not be reviewed separately.

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-046-files-browse-has-subfolders.sql` | add | New migration: `CREATE OR ALTER PROCEDURE dbo.file_index_browse_lst` with the extra column. The `CAS-043` migration stays as is (append-only). |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_browse_lst.sql` | modify | Mirror updated to the new definition. |
| `src/Cachet.ArchiveSync.Core/Dtos/FileBrowseRows.cs` | modify | `FileBrowseFolderRow` gains `HasSubfolders` (default `true`). |
| `src/Cachet.ArchiveSync.Core/Dtos/BrowseFolderItem.cs` | modify | Add `bool HasSubfolders { get; init; } = true` (JSON `hasSubfolders`). |
| `src/Cachet.ArchiveSync.Api/Repositories/FileBrowseRepository.cs` | modify | Read `has_subfolders` into the row. |
| `src/Cachet.ArchiveSync.Api/Services/FileBrowseService.cs` | modify | Map `HasSubfolders` to the response item. |
| `src/Cachet.ArchiveSync.Web/Components/Shared/FileExplorer.razor` | modify | `TreeNode.HasSubfolders`; `HasChevron` uses it for unloaded nodes. |
| `src/Cachet.ArchiveSync.Api.Tests/FileBrowseServiceTests.cs` | modify | Mapping test. |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/FilesBrowseRepositoryTests.cs` | modify | Real-SQL tests for the new column. |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/FilesBrowseControllerTests.cs` | modify | JSON shape includes `hasSubfolders`. |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/Files/BrowseHttpHarness.cs` | modify (only if the mock needs the new member) | |
| `src/Cachet.ArchiveSync.Web.Tests/FileExplorer/BrowseFixtures.cs` | modify | `Folder(path, hasSubfolders = true)` helper parameter. |
| `src/Cachet.ArchiveSync.Web.Tests/FileExplorer/FileExplorerTests.cs` | modify | Chevron tests. |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `Database/migrations/20261004_120000__CAS-043-files-browse-api.sql` | Already applied on dev; migrations are append-only. |
| `src/Cachet.ArchiveSync.Api/Controllers/FilesController.cs` | Serialises the DTO as is; no route, auth or status change. |
| `src/Cachet.ArchiveSync.Api/Services/FileContentService.cs`, `Program.cs`, `Repositories/B2PathPrefixProvider.cs` | Not involved. |
| `src/Cachet.ArchiveSync.Core/Dtos/FolderBrowseResponse.cs`, `BrowseFileItem.cs`, `BreadcrumbItem.cs`, and the `IFileBrowse*` interfaces | Shape and signatures unchanged. |
| `src/Cachet.ArchiveSync.Web/Http/FilesBrowseClient.cs` | Already deserialises the whole response record. |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor`, `FileExplorer.razor.css`, `wwwroot/icons/*` | No markup or style change. |
| `specs/api/.../CAS-043-files-browse-api.md`, `specs/web/.../CAS-044-...md` | Not edited by this spec. See §14 Q3. |

---

## §7 Database Changes

### Changed Objects
| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| `file_index_browse_lst` | dbo | one column added to result set 1: `has_subfolders BIT` | **Parameter list unchanged.** Result set 2 and both OUTPUT parameters unchanged. |

**Proposed expression** (inside the existing single `GROUP BY`, no extra pass):
```sql
CAST(MAX(CASE WHEN CHARINDEX(N'/', SUBSTRING(r.rem, p.slash_pos + 1, 1000)) > 1
              THEN 1 ELSE 0 END) AS BIT) AS has_subfolders
```
`r.rem` is the remainder after the prefix and `p.slash_pos` the first `/` in it. The text after that
first `/` contains another `/` at position 2 or later exactly when a non-empty subfolder name exists.
`> 1` (not `> 0`) keeps this consistent with the existing rule that empty folder names (`a//b`) are
not listed. Rows are already filtered to active, `source`, bucket and prefix.

### Migration Required
Yes. TWO consistent artifacts (hybrid model, db-author):
- Migration `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-046-files-browse-has-subfolders.sql`, using
  `CREATE OR ALTER PROCEDURE` with the full new body.
- Mirror `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_browse_lst.sql`, identical body.
- Rollback: re-apply the `CAS-043` migration's `CREATE OR ALTER` body, but only after the Api has been
  reverted, because the new repository reads the column (§12).

---

## §8 API Contract Changes

| Endpoint | Field | Before | After | Breaking? |
|----------|-------|--------|-------|-----------|
| `GET /files/browse` | `folders[].hasSubfolders` | absent | boolean, always present | **No** (additive; unknown-field-tolerant consumers) |

```json
"folders": [
  { "name": "Q3", "path": "Accounts Payable - Cachet/2026/Q3",
    "latestModifiedAt": "2026-09-14T15:02:11Z", "hasSubfolders": false }
]
```
No API version bump (additive). The only consumer is the Web project in this repository.

---

## §9 Validations Changed

N/A. No request parameter, validation rule or status code changes (`CAS-043` V1-V9 unchanged).

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | For a child folder whose active descendants include a file two or more levels below it, the SP returns `has_subfolders = 1`; for a folder holding only direct files it returns `0`. | `Test_SC1_HasSubfoldersTrueOnlyWhenADeeperFolderExists` |
| SC-2 | Inactive rows, other-source rows and other-bucket rows never make `has_subfolders` true. An empty segment (`Top//x.txt`) does not make it true. | `Test_SC2_HasSubfoldersIgnoresInactiveOtherSourceAndEmptySegments` |
| SC-3 | A folder reached via case-insensitive merge (`Adrian` and `ADRIAN`) is `true` if either spelling has a subfolder. | `Test_SC3_HasSubfoldersMergesAcrossCaseVariants` |
| SC-4 | `FileBrowseService` maps the SP value to `BrowseFolderItem.HasSubfolders`, and the `GET /files/browse` JSON contains `hasSubfolders` on every folder item. | `Test_SC4_ServiceMapsHasSubfolders`, `Test_SC4_BrowseJsonContainsHasSubfoldersOnEveryFolder` |
| SC-5 | In the explorer, after the root loads, a child node with `hasSubfolders = false` shows no chevron and one with `true` shows a chevron; a node whose children were loaded and are empty shows none. | `Test_SC5_TreeChevronOnlyOnFoldersWithSubfolders` |
| SC-6 | A response without `hasSubfolders` (older Api) deserialises to `true` and the chevron is shown (today's behaviour). | `Test_SC6_MissingHasSubfoldersFieldKeepsChevron` |
| SC-7 | **Manual performance record.** The four `CAS-043` SC-13 cases (root, first-level, deep, most-files folder) re-measured on the production-size copy (median of three warm-cache runs per case) and recorded in the Implementation Log; no case is more than 40% slower than the `CAS-043` record (ceiling raised from 25% on 2026-10-04 after measurement, see §14 Q4). | manual (§16) |

---

## §11 Security Notes

No security impact. `hasSubfolders` is derived from `relative_path` values the user can already browse
folder by folder; it adds one bit of structure per folder name that is already returned. No new
parameter, no file-system or B2 access, no change to authorisation or to the audit trail (browsing is
still not audited, `CAS-043` §12). No B2 key, server path or credential is involved (FR-03).

---

## §12 Rollback Plan

1. Revert Web (`FileExplorer.razor`) first, then Api (`FileBrowseRepository`, `FileBrowseService`,
   Core DTOs). Reverting Web alone restores the old tree behaviour.
2. Database rollback: re-apply the `CAS-043` `CREATE OR ALTER PROCEDURE` body. **Only after the Api
   revert**, because the new repository reads the `has_subfolders` column and would fail with a 500
   `browse-failed` against the old SP.
3. Config rollback: none.
4. Task-queue rollback: not applicable (no queue or job contract change).

Deploy order: migration, then Api, then Web (Web tolerates an Api without the field, §2.5, so Web
before Api is also safe).

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `FilesBrowseRepositoryTests` (real SQL) | `Test_SC1_...`, `Test_SC2_...`, `Test_SC3_...` | SC-1, SC-2, SC-3 |
| `FileBrowseServiceTests` | `Test_SC4_ServiceMapsHasSubfolders` | SC-4 |
| `FilesBrowseControllerTests` | `Test_SC4_BrowseJsonContainsHasSubfoldersOnEveryFolder` | SC-4 |
| `FileExplorerTests` (bUnit) | `Test_SC5_...`, `Test_SC6_...` | SC-5, SC-6 |

Existing `CAS-043`/`CAS-044` tests must keep passing (the new members default to `true`).
Coverage target: ≥ 85% on changed files.

---

## §14 Open Questions

Resolved 2026-10-04 with the author's recommended defaults. Reviewer: veto any of these before `status: ready`.

- [x] **Q1 — Default when the field is missing: `true`.** A response without `hasSubfolders` keeps the
      chevron (today's behaviour). A `false` default would make a Web-before-Api deploy silently lose
      every expand arrow, which is a worse failure than showing an extra chevron. Already reflected in
      §2.5, §5 and SC-6; no text change needed.
- [x] **Q2 — "Subfolder" meaning: derived from active rows only.** A folder exists only because an
      active file path implies it, so a folder whose deeper content is all inactive shows no chevron.
      This is consistent with what the list pane shows when the folder is opened (it also filters to
      active rows), so the tree never promises a folder the list cannot show. Already reflected in
      §7 and SC-2.
- [x] **Q3 — Spec housekeeping: leave `CAS-043` and `CAS-044` as written.** They are still in `review`;
      editing them now would blur what the reviewer is checking. `CAS-046` lists both in
      `related_specs` and is the authoritative amendment for the `hasSubfolders` field and the chevron
      rule. Revisit only if a later consolidation of the Files specs is done. §6 stays as is.
- [x] **Q4 — Performance gate: ceiling raised to 40% (requester, 2026-10-04), measurement rule kept.**
      The first measurement showed the extra column costs about 25-34% on the root and first-level
      folders (about +1 to 1.5 s on a 4.5-5 s scan; deep and leaf folders are unaffected), so the
      original 25% ceiling was breached. A cheaper `CHARINDEX`-with-start-position form gave identical
      rows but no speed-up, so the cost is inherent in one more per-row expression over about 870k rows.
      The requester accepted the cost, because the screen already shows a spinner for this call (`CAS-044`
      §8.4). SC-7 timings remain the median of three runs per case, warm cache, same production-size copy
      as the `CAS-043` record; the reviewer should also confirm the plan shows no new spool or extra scan (§16).
---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code (drafted for devarajan@tracecounts.org) | 2026-10-04 | Draft from `/spec-change` |
| Reviewer | | | |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches the code before the change (`HasChevron` at `FileExplorer.razor`, `BrowseFolderItem` fields)
- [ ] Desired behavior (§2) is fully implemented, including the tolerant default
- [ ] Only files in §5 were modified (§6 files are untouched), in particular the `CAS-043` migration file
- [ ] All §10 Success Criteria have passing tests; SC-7 timings are in the Implementation Log
- [ ] §8: `hasSubfolders` is present on every folder item and the change is additive
- [ ] §7: the new migration and the mirror SP bodies are identical; parameter list unchanged
- [ ] No extra database pass or temp table was introduced
- [ ] §11: no new data, path or credential exposed
- [ ] §4 Blast Radius is accurate: the list pane, `/view`, `/download` and the audit trail are unchanged

### Verification Report Format
Reviewer writes `CAS-046.review.md`:
- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5 file list)
- Unintended blast radius (manual read found changes in §6 files)
- Security gaps

---

## SC-7 Manual Performance Record (2026-10-04)

Same method and data as the `CAS-043` SC-13 record: `dbo.file_index_browse_lst` run with `sqlcmd` on the
development copy of production (876,554 active `B2` rows, bucket `Cachet-Server-C`, prefix
`D/Shares/Operations`), wall-clock for the whole call, median of three warm runs per case, rows streamed
to the client. The machine was busy with builds and test runs during the measurement, so absolute times
are noisy; the comparison column uses the interleaved A/B below.

| Case | `CAS-043` record (first / repeat) | CAS-046 median of 3 | Rows (folders / files) |
|---|---|---|---|
| Root | 5,749 / 5,887 ms | 7,554 ms | 250 / 13 |
| First-level (`MYPayrollHR`) | 6,663 / 6,082 ms | 5,257 ms | 25 / 34 |
| Deep (depth 11) | 731 / 894 ms | 887 ms | 1 / 2 |
| Most direct files | 2,440 / 2,573 ms | 2,493 ms | 0 / 24,634 |

Root is about 30% above the `CAS-043` average; no case exceeds the 40% ceiling (§10 SC-7).

**Interleaved A/B (same session, old `CAS-043` SP body vs new body, `foldersOnly=1`, run 0 discarded as
warm-up).** This isolates the cost of the new column from machine load:

| Case | Old SP (median) | New SP (median) | Change |
|---|---|---|---|
| Root, session 1 | 4,699 ms | 6,007 ms | +28% |
| First-level, session 1 | 4,172 ms | 5,579 ms | +34% |
| Root, session 2 | 4,622 ms | 5,903 ms | +28% |
| First-level, session 2 | 4,901 ms | 6,109 ms | +25% |

Both forms returned identical rows. A `CHARINDEX(N'/', r.rem, p.slash_pos + 2) > 0` variant (no `SUBSTRING`)
was also tried and was not faster, so the migration keeps the form written in §7. The test procedures were
session-temporary; nothing was left in the database.

