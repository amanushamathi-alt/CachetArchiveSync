---
id: CAS-043
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-04
priority: P2
related_specs: [CAS-044, CAS-040, CAS-011, CAS-032, CAS-033, CAS-005, CAS-002]
touches:
  database: true   # one new read SP (dbo.file_index_browse_lst); no DDL
  api: true        # GET /files/browse; FileBrowser context accepted by CAS-040's /view and /download
  ui: false        # owned by CAS-044
  worker: false    # synchronous read path
  b2: false        # metadata only; no B2 call, credential or URL (FR-03)
  config: false    # reuses B2:BucketName; no new keys
  security: true   # exposes the folder tree; extends the audited file-open context list
migrations:
  - 20261004_120000__CAS-043-files-browse-api.sql
---

# CAS-043 — Files Browse API (Folder Tree and Folder Listing from the File Index)

Part 1 of 2 for the Files landing-screen request (2026-10-04). **Part 2 is `CAS-044`** (Blazor
Files mode). Deploy order: this spec (DB → Api) before `CAS-044` (Web).

**Requirement status:** the BRD (v2.3) has no requirement for browsing the archive as a folder
tree. This spec implements a new capability, referred to here as **FR-29**. It is **client-approved
by email** (sent by Nasrollah on 2026-10-02, confirmed by the requester on 2026-10-04; the email is
held by the requester; see §16). The BRD text has not yet been revised to include it; that
update is a documentation follow-up (§0). It builds on BRD §3(c)/§3(d), §12.3 items 1–2, FR-03,
FR-05 and FR-23.

---

## §0 Gate and decomposition decision (read first)

> **Client approval: received (email from Nasrollah, 2026-10-02; confirmed by the requester
> 2026-10-04).** No deployment gate applies. FR-29 does not appear in BRD v2.3, so the BRD should be
> revised (v2.4) to add it. That revision is a documentation task for the requester and does not
> block implementation. The sender and date are recorded in §16 so an auditor can trace the
> requirement.
>
> The earlier "change request" wording meant exactly this: a written client sign-off to add a
> requirement that is not in the BRD. `CAS-040` needed a formal document (CR-001) for the same
> purpose.
>
> Status stays `draft` until the requester sets it to `ready` after this review.

**Decomposition check (Karpathy P9).** Two signals fired:
- **Project boundary:** the feature touches Database, Api, Core and Web.
- **Test-domain split:** it needs Api xUnit/WebApplicationFactory tests and Web bUnit tests.

Size thresholds were not crossed (§6 has 1 new endpoint, §7 has 8 validations, §5 has 1 SP). The
spec is therefore split at the project boundary: **CAS-043 (Database + Api, this file)** and
**CAS-044 (Web)**. This differs from `CAS-040`, which stayed in one document, because here the two
halves have separate test harnesses and CAS-044 can only be reviewed once this contract is fixed.
The two specs are cross-linked through `related_specs`.

---

## §1 Summary

The Files landing screen (`CAS-044`) needs to show the archive as folders and files, the way
Windows File Explorer does. No folder table exists. Folders are implied by the `/`-separated
`dbo.file_index.relative_path` values (which hold the full B2 object key, `CAS-033`). This spec adds
a read-only endpoint, `GET /files/browse`, rooted at the bucket's configured folder prefix, that returns the direct child folders and files of one
folder, built entirely from `dbo.file_index` metadata. It never touches the file system, B2, or file
content (BRD §12.3 item 1).

**Data basis (index rows supplied by the requester, 2026-10-04).** Production `source = B2` rows
look like this, for bucket `Cachet-Server-C`:

| `relative_path` | `file_name` | `file_extension` | `file_size_bytes` | `modified_at` |
|---|---|---|---|---|
| `D/Shares/Operations/1099 Filing.xlsx` | `1099 Filing.xlsx` | `XLSX` | 16634 | 2022-02-07 17:35:35 |
| `D/Shares/Operations/175 S. Lake/EmergencyAssessment.pdf` | `EmergencyAssessment.pdf` | `PDF` | 905358 | 2024-10-29 19:36:06 |

`relative_path` holds the full B2 key (`CAS-033`), and every key begins with the bucket's folder
prefix `D/Shares/Operations` (`dbo.odrive_bucket_mapping.b2_path_prefix`, `CAS-032`). The browser
tree is therefore **rooted at that prefix**: the root is "Operations", its children are
`1099 Filing.xlsx`, `175 S. Lake`, and so on, and the `D/Shares` part is never shown. This matches
the Windows Explorer view the requester supplied. `file_extension` is stored upper-case (`XLSX`,
`PDF`); `CAS-040` lower-cases it before checking its allowlist.

It also extends the file-open context list that `CAS-040` introduced, with a fifth value,
`FileBrowser`. That lets a file opened from the Files screen be viewed and downloaded through the
existing `GET /files/{id}/view` and `/download` endpoints, with the same audit-before-response
guarantee (FR-23).

---

## §2 User Story

As a **TraceCounts staff reviewer**, I want **to browse the archive folder by folder, using the same
index the searches use**, so that **I can find and open a file when I know where it lives, without
having to guess a search term**.

---

## §3 In-Scope

- New SP `dbo.file_index_browse_lst`: direct child folders and files of one folder, for one
  `(source, bucket_name)`, active rows only. **No paging and no item limit**: every direct child is returned
  (decision 2026-10-04, matching Windows File Explorer).
- New endpoint `GET /files/browse?folder=&foldersOnly=` on the existing
  `FilesController`.
- Core DTOs `FolderBrowseResponse`, `BrowseFolderItem`, `BrowseFileItem`, `BreadcrumbItem`.
- New `IFileBrowseService` / `FileBrowseService`: folder-path validation, SP call, mapping, and
  prefix handling. It reads the prefix from the existing `IB2PathPrefixProvider` (`CAS-032`) and
  - prepends it to the client's root-relative `folder` before calling the SP,
  - removes it from every path in the response, so all paths the client sees are relative to the
    prefix root,
  - returns `rootName`, the last segment of the prefix (`Operations`), or the bucket name when the
    prefix is empty.
  Rows whose `relative_path` does not start with the prefix are never listed.
- `FileBrowser` added to the `context` allowlist in `FileContentService` and to the 400 message in
  `FilesController.ProblemFor` (an amendment to `CAS-040` V2; see §7 V8).
- For `context=FileBrowser` only, `FileContentService` appends `; context=FileBrowser` to the audit
  row's `result_detail` on `/view` and `/download` (Success and Failure rows). Rows for the other
  three contexts are unchanged, so existing tests that assert the current detail text keep passing.
  The `result_detail` text is already shown in the Audit Log screen (`AuditLogModal.razor`).
- The Api passes the request's `CancellationToken` to the database call, so a user who navigates
  away stops the query.
- Source and bucket are fixed server-side: `source = B2` (the B2 archive, per the requester's
  decision of 2026-10-04) and `bucket_name` from `B2:BucketName`. Neither is a client parameter.
  Opening a file resolves it through the B2 mirror root (`b2_mirror_root_path`) as `CAS-040`'s
  source-aware pipeline already does.

## §4 Out-of-Scope

- **Any file-system access.** Browsing reads `dbo.file_index` only. Reading file bytes stays with
  `CAS-040`'s `/view` and `/download`.
- **B2 listing or cross-check (FR-08).** Folder contents are index data, not live B2 data. A file
  that was deleted from B2 after the last index refresh can still appear until the index refreshes.
  `/view` and `/download` then return 404/410 per `CAS-040` §6.4.
- **A second source.** Browsing the O-Drive rows (`source = ODrive`) is not offered; there is no
  source toggle.
- **Live B2 access.** "B2 archive" means the `source = B2` rows of `dbo.file_index`. File content is
  read from the server's local B2 mirror directory (`CAS-037`), never from B2 itself. A file in the
  index but not yet in the mirror returns 404 `file-missing-from-mirror` on open (`CAS-040` §6.4).
- **Multiple buckets.** One bucket by design.
- **Write operations.** No rename, move, delete, upload or quarantine from the browser.
- **Folder rows or a folder table.** No DDL. Folders stay implied by paths.
- **Auditing folder clicks.** Opening a folder only lists its contents; nothing is written to the
  audit log (decision 2026-10-04).
- **Caching or pre-computing the folder tree.** Each browse reads SQL Server (§5, §15 Q8). A
  covering index or a cached folder summary is a possible later change, not part of this spec.
- **Search.** Keyword and Date Search are unchanged (`CAS-006`, `CAS-012`, `CAS-038`).
- **Index freshness.** Refresh cadence stays open (BRD §12.4 item 1) and is not changed here.

---

## §5 Database Objects

### Tables / Columns

N/A. No DDL. The existing filtered unique index `UQ_file_index_source_bucket_path`
`(source, bucket_name, relative_path) WHERE is_active = 1` supports the prefix seek.
`dbo.audit_log.tab_module` is unchanged (§12 uses the existing `Keyword Search` value).

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `file_index_browse_lst` | dbo | lst | **New.** Inputs: `@source NVARCHAR(10)`, `@bucket_name NVARCHAR(255)`, `@folder_path NVARCHAR(1000)` (the **full key path including the bucket prefix**, composed by `FileBrowseService`; `''` = bucket root, no trailing `/`), `@folders_only BIT = 0`. Outputs: `@totalFolders INT OUTPUT`, `@totalFiles INT OUTPUT` (the counts of the rows returned). See the behavior below. |

**Behavior of `dbo.file_index_browse_lst`:**
1. Validate `@source IN ('ODrive','B2')` and a non-empty `@bucket_name`, using the same
   `RAISERROR` style as `dbo.file_index_lst`.
2. Let `@prefix = ''` for root, otherwise `@folder_path + '/'`. Escape `%`, `_` and `[` in `@prefix`
   and match with `LIKE @prefix_escaped + N'%' ESCAPE N'\'`.
3. For each active row under the prefix, let `remainder` be `relative_path` after the prefix.
   - If `remainder` contains `/`, the row belongs to the child folder named by the text before the
     first `/`.
   - Otherwise it is a file directly in this folder.
4. Result set 1, child folders: `folder_name`, `latest_modified_at` (`MAX(modified_at)` of the
   folder's descendants), ordered by `folder_name`. Result set 2, files (skipped when
   `@folders_only = 1`): `file_index_id, file_name, file_extension, file_size_bytes, modified_at`,
   ordered by `file_name`.
5. Set `@totalFolders` and `@totalFiles` (the latter `0` when `@folders_only = 1`).
6. Folders sort before files. No `OFFSET`/`FETCH`: all rows are returned.

**Data volume and shape.** The production `source = B2` set has **876,554 rows**
(figure supplied by the requester on 2026-10-04; the split between active and inactive rows was not
stated). Listing the root groups the rows beneath the
prefix, which is most of them, so:
- The SP must not copy all descendant rows into a temp table. Run the folder `GROUP BY` as one
  statement over the prefix range and the direct-files query as a second statement that returns only
  rows whose remainder has no `/`. (The reference query in the review discussion used a temp table
  for readability only.)
- The Api must pass a `CancellationToken`. The repository's command timeout must be set high enough
  for the slowest browse measured in SC-13, so long-running listings finish instead of failing; a
  timeout or SQL failure still returns 500 `browse-failed` with no SQL text (V9).
- **Risk accepted (requester, 2026-10-04):** with no limit, a folder with very many direct files
  returns a large response and a long query. The screen shows a spinner for the whole call
  (`CAS-044` §8.4) and lists through a virtualized list (`CAS-044` §8.2). SC-13 includes the largest
  folder.
- The browse timing on production-size data is measured and recorded (SC-13). No response-time
  target is set; the screen shows a spinner while the call is in flight (`CAS-044` §8.4).

**Known limit (confirmed).** The database collation is `SQL_Latin1_General_CP1_CI_AS`
(case-insensitive, 2026-10-04). Two sibling folders that differ only by letter case, for example
`Adrian` and `ADRIAN`, merge into one folder, which shows the contents of both, under whichever
spelling SQL Server returns. B2 keys are case-sensitive, so this is a display difference only.
Opening a file uses its `file_index_id`, so it is unaffected. Accepted by the requester; run the
check in §15 Q5 to see whether any such folders exist today.

### Migration Strategy

Hybrid model (db-author) writes both:
- Idempotent migration `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-043-files-browse-api.sql`, using
  `CREATE OR ALTER PROCEDURE`.
- Mirror `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_browse_lst.sql`, with the
  header block used by the neighboring mirrors.
- Rollback: `DROP PROCEDURE IF EXISTS dbo.file_index_browse_lst`.

---

## §6 API Endpoints

### Endpoint: `GET /files/browse`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`, the same policy as the other `/files`
endpoints.
**Async:** No. A synchronous read; FR-18 does not apply.
**Route note:** `FilesController` already has `GET files/{fileIndexId:int:min(1)}`. The literal
segment `browse` does not match the `int` constraint, so the routes do not collide. Add a routing
test (SC-9).

**Request (query string):**

| Param | Type | Default | Description |
|---|---|---|---|
| `folder` | string | `""` | Folder path relative to the prefix root (`Operations`), `/`-separated, no leading or trailing `/`. Empty means the root. The server adds the `b2_path_prefix`; the client never sends or sees it. |
| `foldersOnly` | bool | `false` | `true` returns only child folders (used by the tree pane). |

There are no paging parameters. A folder's full contents come back in one response.

**Response 200:**
```json
{
  "rootName": "Operations",
  "folder": "Accounts Payable - Cachet/2026",
  "parent": "Accounts Payable - Cachet",
  "breadcrumb": [
    { "name": "Accounts Payable - Cachet", "path": "Accounts Payable - Cachet" },
    { "name": "2026", "path": "Accounts Payable - Cachet/2026" }
  ],
  "folders": [
    { "name": "Q3", "path": "Accounts Payable - Cachet/2026/Q3", "latestModifiedAt": "2026-09-14T15:02:11Z" }
  ],
  "files": [
    { "fileIndexId": 100245, "name": "AP Aging Sep.xlsx", "extension": "xlsx",
      "sizeBytes": 90377, "modifiedAt": "2026-09-14T15:02:11Z" }
  ],
  "totalFolders": 1,
  "totalFiles": 2
}
```
- `parent` is `null` at the root. `breadcrumb` is empty at the root.
- Times are UTC ISO-8601. Sizes are bytes.
- The response contains **no absolute path, root path or B2 key** other than the index-relative
  folder paths the user is already navigating.

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Folder listed (an empty root is 200 with empty lists) |
| 400 | Validation failure (V1–V4) |
| 401 | Missing or invalid session |
| 404 | `folder` is non-empty and has no active rows beneath it (V5) |
| 500 | `B2:BucketName` not configured, or no active bucket mapping row for the prefix (V6); database failure or timeout (V9) |

**Amendment to existing endpoints:** `GET /files/{fileIndexId}`, `/view` and `/download` accept
`context=FileBrowser` (V8). Nothing else about those endpoints changes.

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `folder`, if present, is at most 1,000 characters (the `relative_path` column size). | 400 `validation-failed` | `Test_V1_FolderOverMaxLengthRejected` |
| V2 | `folder` has no leading `/`, no trailing `/`, no `//`, no `\`, no control characters, and no `.` or `..` segment. | 400 `validation-failed` (message names no path) | `Test_V2_MalformedOrTraversalFolderRejected` |
| V3 | **Retired 2026-10-04** (paging removed). The ID is kept so numbering and references stay stable. | n/a | n/a |
| V4 | `foldersOnly` is a valid boolean when present. | 400 `validation-failed` | `Test_V4_NonBooleanFoldersOnlyRejected` |
| V5 | A non-empty `folder` must have at least one active row beneath it. | 404 `folder-not-found` | `Test_V5_UnknownFolderReturns404` |
| V6 | `B2:BucketName` must be configured, and an active `dbo.odrive_bucket_mapping` row must exist for it (so the prefix can be read). | 500 `bucket-not-configured` or `mirror-mapping-missing` (no config value or path in the message) | `Test_V6_MissingBucketConfigOrMappingReturns500` |
| V7 | `LIKE` wildcards (`%`, `_`, `[`) in `folder` are matched literally, never as patterns. | n/a (behavior) | `Test_V7_WildcardCharactersInFolderAreLiteral` |
| V9 | A database failure or command timeout during browse is not surfaced as SQL text. | 500 `browse-failed` (generic message, no SQL, path or parameter values); Warning log without the folder value | `Test_V9_DatabaseFailureReturnsGeneric500` |
| V8 | `context=FileBrowser` is accepted by `/files/{id}`, `/view` and `/download`. Missing or other unknown values still return 400 as in `CAS-040` V2. | 400 `validation-failed` | `Test_V8_FileBrowserContextAcceptedAndUnknownStillRejected` |

---

## §8 UI / UX

N/A. The screen is `CAS-044`.

---

## §9 Configuration Changes

None. The endpoint reuses `B2:BucketName`, which the Api already reads (never a secret).

| Key | Default | Description | Env Override |
|-----|---------|-------------|--------------|
| `B2:BucketName` | existing | Bucket whose `B2` rows are browsed. Unchanged. | existing |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Browsing the root returns each distinct first path segment of active `B2` rows exactly once as a folder, and returns rows with no `/` as files. | `Test_SC1_RootListsDistinctTopLevelFoldersAndRootFiles` |
| SC-2 | Browsing `A/B` returns only the direct children of `A/B`; deeper descendants appear only as their first-level folder. | `Test_SC2_FolderListsOnlyDirectChildren` |
| SC-3 | Rows with `is_active = 0`, rows of another `source`, and rows of another bucket never appear. | `Test_SC3_InactiveOtherSourceAndOtherBucketRowsExcluded` |
| SC-4 | A folder's `latestModifiedAt` equals the maximum `modified_at` of its active descendants. | `Test_SC4_FolderLatestModifiedIsMaxOfDescendants` |
| SC-5 | Folders are listed before files, each list sorted by name. `totalFolders` / `totalFiles` equal the number of items returned, and nothing is truncated: a folder with 2,500 direct files returns all 2,500. | `Test_SC5_ListsAreSortedAndNothingIsTruncated` |
| SC-6 | `foldersOnly=true` returns no files and `totalFiles = 0`. | `Test_SC6_FoldersOnlyOmitsFiles` |
| SC-7 | `breadcrumb` lists each ancestor in order and `parent` is the immediate ancestor, or `null` at the root. | `Test_SC7_BreadcrumbAndParentMatchFolderPath` |
| SC-8 | The response never contains the mirror root path, a drive letter, or any B2 key/credential. | `Test_SC8_ResponseExposesNoServerPathOrCredential` |
| SC-9 | `GET /files/browse` routes to the browse action and `GET /files/123` still routes to `GetMetadata`. | `Test_SC9_BrowseRouteDoesNotCollideWithFileIdRoute` |
| SC-10 | `GET /files/browse` without a session returns 401. | `Test_SC10_BrowseRequiresAuthenticatedSession` |
| SC-12 | With prefix `D/Shares/Operations` and the §1 sample rows, browsing the root returns `1099 Filing.xlsx` as a file and `175 S. Lake` as a folder, `rootName` is `Operations`, and no response field contains `D/Shares`. Rows outside the prefix never appear. With an empty prefix the root is the bucket root and `rootName` is the bucket name. | `Test_SC12_RootIsRelativeToBucketPrefixAndHidesIt` |
| SC-11 | A `/view` or `/download` call with `context=FileBrowser` writes its `FileViewed` / `FileDownloaded` audit row, with `tab_module = 'Keyword Search'` and a `result_detail` that contains `context=FileBrowser`, before the first content byte. Calls with the other three contexts produce the same `result_detail` as before. | `Test_SC11_FileBrowserContextIsAuditedBeforeContent` |
| SC-13 | **Manual performance record.** Browsing the root, a first-level folder and a deep folder against production-size data (876,554 `B2` rows) completes without a timeout, and so does the folder with the most direct files; the elapsed times are recorded in the Implementation Log. No threshold is set. | manual (§18) |
| SC-14 | Aborting the HTTP request cancels the database call (the `CancellationToken` reaches the repository), and no audit row is written for a browse. | `Test_SC14_ClientAbortCancelsBrowseAndNothingIsAudited` |

---

## §11 Telemetry

```csharp
_logger.LogInformation(
    "Files browse for {UserId}: {FolderDepth} deep, {FolderCount} folders, {FileCount} files, {ElapsedMs} ms",
    userId, depth, folders.Count, files.Count, elapsedMs);
_logger.LogWarning("Files browse rejected folder parameter for {UserId}: {Rule}", userId, ruleId);
```
- Never log: the `folder` value (it is user-entered archive path text), file names, the B2
  application key or any part of it, or session values. Log depth and counts only.

---

## §12 Security Notes

**Threat model.** The endpoint exposes the archive's folder and file names to any authenticated
user. Those names are already visible through search results and the audit log. Main risks: (1)
path traversal, (2) SQL pattern injection through `LIKE`, (3) disclosure of server paths, (4)
unaudited access.

- **Traversal.** Browsing never opens a file-system path, so traversal cannot reach disk. V2 still
  rejects `..`, `\` and rooted paths so a bad value cannot reach later code.
- **Injection.** The SP is parameterized through Dapper, and `LIKE` wildcards are escaped (V7).
- **Disclosure.** Only prefix-relative paths are returned: the `D/Shares` part of the key, mirror
  roots and drive letters never leave the server (SC-8, SC-12). Reading content stays with
  `CAS-040`, which has its own root-containment check.
- **Authorization.** Same policy as `CAS-040`'s endpoints. No role split is introduced.

### B2 Credential Handling
No B2 client, key, presigned URL or download authorization is used. The endpoint reads SQL Server
only (FR-03).

### Audit Trail
- **File open and download.** Unchanged `CAS-040` behavior: `FileViewed` / `FileDownloaded`, audited
  before the first content byte (FR-23), now also reachable with `context=FileBrowser`. The
  `FileBrowser` context maps to `tab_module = 'Keyword Search'` (decision 2026-10-04), so no
  `CK_audit_log_tab_module` change is needed. To keep Files-screen opens distinguishable in an
  audit, `result_detail` carries `context=FileBrowser` (§3, SC-11).
- **Folder browsing.** Not audited (decision 2026-10-04): opening a folder only lists names the
  user can already find by searching. A user's file opens and downloads are the audited actions.

### Session / Auth Requirements
An authenticated session (`CachetAuthenticated`). The login method is resolved (BRD §12.1 items
8–10), and drive encryption at rest is not required (§12.3 item 6). **Neither open BRD §12.4 item
affects this spec.**

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `FileBrowseServiceTests` | `Test_V1_...`, `Test_V2_...`, `Test_V4_...` | Folder and parameter validation (V1, V2, V4) |
| `FileBrowseServiceTests` | `Test_SC7_BreadcrumbAndParentMatchFolderPath` | Breadcrumb and parent mapping (SC-7) |
| `FileBrowseServiceTests` | `Test_V6_MissingBucketConfigOrMappingReturns500`, `Test_SC12_RootIsRelativeToBucketPrefixAndHidesIt` | Bucket and mapping guard, prefix handling (V6, SC-12) |

### Integration Tests
Real SQL Server through the existing Api integration-test fixture, seeded with `B2`, `ODrive`,
inactive and other-bucket rows.

| Class | Method | Proves |
|-------|--------|--------|
| `FilesBrowseRepositoryTests` | `Test_SC1_...`–`Test_SC6_...`, `Test_V7_...` | SP behavior (SC-1–SC-6, V7) |
| `FilesBrowseControllerTests` | `Test_SC8_...`, `Test_SC9_...`, `Test_SC10_...`, `Test_V5_...` | Contract, routing, auth (SC-8–SC-10, V5) |
| `FilesControllerContextTests` | `Test_V8_...`, `Test_SC11_...` | New context and audit (V8, SC-11) |
| `FilesBrowseControllerTests` | `Test_V9_DatabaseFailureReturnsGeneric500`, `Test_SC14_ClientAbortCancelsBrowseAndNothingIsAudited` | Failure handling, cancellation, no browse audit (V9, SC-14) |

The integration fixture seeds a small set of rows. Production-size behavior is covered by the
manual record in SC-13, not by seeding 876,554 rows in a test.

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. Deploy the migration (`dbo.file_index_browse_lst`) first. It is additive and harmless on its own.
2. Deploy Api. It can go out before Web, because nothing calls the new route yet.
3. Deploy Web (`CAS-044`) last. The client approval is in hand (§0), so no further gate applies.
4. Rollback: revert Api, then `DROP PROCEDURE IF EXISTS dbo.file_index_browse_lst`. Roll back
   `CAS-044` first if it has shipped.

---

## §15 Open Questions

- [x] **Q1 — Client approval. Resolved 2026-10-04:** the client approved FR-29 by email (held by
      the requester); sender Nasrollah, dated 2026-10-02, recorded in §16. Remaining documentation task:
      add FR-29 to the BRD in the next revision.
- [x] **Q2 — Source. Resolved 2026-10-04 (requester): browse the B2 archive** (`source = B2`), not
      O-Drive. Consequence to confirm: the B2 index and the local B2 mirror are refreshed by
      separate scripts (`CAS-031`, `CAS-037`), so a listed file can be missing from the mirror until
      the next population. That case is handled as a 404 on open (§4).
- [x] **Q3 — Audit module. Resolved 2026-10-04 (requester):** file opens and downloads from the
      Files screen are logged under the existing `Keyword Search` module, with `context=FileBrowser`
      in `result_detail`. No database change.
- [x] **Q4 — Browse auditing. Resolved 2026-10-04 (requester):** folder clicks are not logged; they
      just open the folder and show its contents.
- [x] **Q5 — Case sensitivity. Resolved 2026-10-04:** collation is `SQL_Latin1_General_CP1_CI_AS`
      (case-insensitive). Folders that differ only by case merge in the display (§5). Optional
      check for the requester, listing any such folders at the root (the same pattern works for
      deeper folders):
      ```sql
      SELECT LOWER(LEFT(rem, CHARINDEX('/', rem) - 1)) AS folder_key, COUNT(DISTINCT LEFT(rem, CHARINDEX('/', rem) - 1) COLLATE Latin1_General_CS_AS) AS spellings
      FROM (SELECT SUBSTRING(relative_path, LEN(N'D/Shares/Operations/') + 1, 1000) AS rem
            FROM dbo.file_index
            WHERE is_active = 1 AND source = N'B2' AND relative_path LIKE N'D/Shares/Operations/%') t
      WHERE CHARINDEX('/', rem) > 0
      GROUP BY LOWER(LEFT(rem, CHARINDEX('/', rem) - 1))
      HAVING COUNT(DISTINCT LEFT(rem, CHARINDEX('/', rem) - 1) COLLATE Latin1_General_CS_AS) > 1;
      ```
      No rows means no merged folders exist today.
- [x] **Q6 — Folder "Date modified". Resolved 2026-10-04 (requester):** a folder shows the newest
      `modified_at` among the files beneath it.
- [x] **Q7 — Root label and prefix. Resolved 2026-10-04 from the supplied index rows:** keys start
      with `D/Shares/Operations/`, held in `dbo.odrive_bucket_mapping.b2_path_prefix` (`CAS-032`).
      The root is that folder, labelled by its last segment ("Operations"), and `D/Shares` is never
      shown (§1, §3, SC-12).
- [x] **Q8 — Performance. Resolved 2026-10-04 (requester):** the `B2` source has 876,554 rows. A
      spinner on the screen while the query and the explorer build is acceptable; no response-time
      target is set. The earlier "about 17,000 rows" figure (from `CAS-031`) was an underestimate and
      is not used. Delivery requirements that follow: the SP avoids a temp-table copy of all rows
      (§5), the query is cancellable (SC-14), and timings are recorded on production-size data
      (SC-13). If a browse proves too slow in practice, the follow-up options are a covering index
      or a cached folder summary, each needing its own spec (§4).

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code (drafted for devarajan@tracecounts.org) | 2026-10-04 | Draft from `/spec-new`; open questions closed in review |
| Client approval of FR-29 | Nasrollah | 2026-10-02 | Email confirmation, held by devarajan@tracecounts.org |
| Reviewer | | | |

---

## §17 Graph Impact

### God Nodes in Blast Radius
- `FileContentService` / `FilesController` (CAS-040): the only existing code changed (context
  allowlist). Both are shared by four screens' file-open path.
- `dbo.file_index` / `FileIndexRepository`: read by search, sync and the viewer. This spec adds one
  read SP and does not change any existing one.

### Communities / Modules Affected
- Modules: Keyword Search (the screen it lives on) and Audit Log (new context in existing audit
  rows). Not affected: Auth, O-Drive Sync, Quarantine, Task Manager, Reporting.
- Projects: **Api** (controller, service, repository), **Core** (DTOs, interface), **Database** (one
  SP). Not Worker. Web is `CAS-044`.

### Surprising Connections
- `CAS-039` A4 and `CAS-044` both change the default mode of the same screen (see `CAS-044` §1).
- The `context` allowlist lives in the Api only (`FileContentService`, and the 400 text in
  `FilesController.ProblemFor`). Web's `/files/{id}/view|download` pass-through forwards `context`
  unchanged (`Program.cs`, `ProxyFileAsync`), so Web needs no allowlist change for `FileBrowser`.

### Graphify Queries Run
```
python -m graphify query "file index browse folders files list download B2 object mirror" --budget 2000
python -m graphify explain "FilesController"
python -m graphify explain "FileViewerModal"
```
The graph (built 2026-10-01, 4,295 nodes) returned only containment edges for these C# types and
mostly tool and test nodes for the query. It did not map call relationships, so the blast radius
above comes from a **manual read** of `FilesController.cs`, `FileContentService.cs`, `dbo.file_index*`
SQL and `CAS-040`.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test
- [ ] All §7 Validations are implemented and tested
- [ ] `GET /files/browse` exists with the correct verb, route and `CachetAuthenticated` policy
- [ ] §12: no file-system access, no B2 call, no server path in any response; wildcard escaping works
- [ ] §12: `/view` and `/download` with `FileBrowser` still write the audit row before the first byte
- [ ] §5: `dbo.file_index_browse_lst` exists in the migration and the mirror folder
- [ ] §11: no `folder` value, file name, key or session value in any log line
- [ ] §17 matches the actual changes; nothing in §4 was changed
- [ ] `GET /files/{id}` routing still works (SC-9)
- [ ] The Implementation Log records the three browse timings on production-size data (SC-13)
- [ ] Browsing writes no audit row; `FileBrowser` opens carry `context=FileBrowser` in `result_detail`

### Verification Report Format
The reviewer writes `CAS-043.review.md` with: overall verdict (APPROVED | APPROVED WITH NOTES |
CHANGES REQUIRED | BLOCKED), a per-SC verdict table, a security gap list and scope violations.

---

## Implementation Log

**Completed:** 2026-10-04
**Gates:** Build ✓ | Tests ✗ | Format ✓ | Coverage 84.4% | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20261004_120000__CAS-043-files-browse-api.sql` | new SP dbo.file_index_browse_lst |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_browse_lst.sql` | mirror |
| `src/Cachet.ArchiveSync.Api/Controllers/FilesController.cs` | GET files/browse; FileBrowser context message |
| `src/Cachet.ArchiveSync.Api/Services/FileBrowseService.cs` | new |
| `src/Cachet.ArchiveSync.Api/Repositories/FileBrowseRepository.cs` | new, 15 min timeout |
| `src/Cachet.ArchiveSync.Api/Repositories/B2PathPrefixProvider.cs` | new |
| `src/Cachet.ArchiveSync.Api/Services/FileContentService.cs` | FileBrowser context + audit detail |
| `src/Cachet.ArchiveSync.Api/Program.cs` | DI |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `FileBrowseServiceTests` (Api.Tests) | `Test_V1_FolderOverMaxLengthRejected`, `Test_V2_MalformedOrTraversalFolderRejected`, `Test_V6_MissingBucketConfigOrMappingReturns500`, `Test_SC7_BreadcrumbAndParentMatchFolderPath`, `Test_SC12_RootIsRelativeToBucketPrefixAndHidesIt`, `Test_SC14_AbortSurfacedAsDatabaseExceptionIsCancellationNotFailure` + SC-5 and V5 service-level tests | V1, V2, V5, V6, SC-5, SC-7, SC-12, SC-14 |
| `FilesBrowseRepositoryTests` (real SQL) | `Test_SC1_...`, `Test_SC2_...`, `Test_SC3_...`, `Test_SC4_...`, `Test_SC5_ListsAreSortedAndNothingIsTruncated`, `Test_SC6_...`, `Test_V7_...` + empty-folder-name test | SC-1 to SC-6, V7 |
| `FilesBrowseControllerTests` (HTTP) | `Test_SC8_...`, `Test_SC9_...`, `Test_SC10_...`, `Test_SC14_ClientAbortCancelsBrowseAndNothingIsAudited`, `Test_V4_NonBooleanFoldersOnlyRejected`, `Test_V5_UnknownFolderReturns404`, `Test_V5_FoldersOnlyLeafFolderIsNot404_UnknownFolderIs404`, `Test_V9_DatabaseFailureReturnsGeneric500` + V6 controller mapping | SC-8, SC-9, SC-10, SC-14, V4, V5, V6, V9 |
| `FilesControllerContextTests` (HTTP) | `Test_V8_FileBrowserContextAcceptedAndUnknownStillRejected`, `Test_SC11_FileBrowserContextIsAuditedBeforeContent` (+ other contexts unchanged) | V8, SC-11 |
| `B2PathPrefixProviderTests` (real SQL) | `Test_Provider_NormalizesPrefixAndSignalsMissingMapping` | prefix provider |

SC-13 is manual (below). V3 is retired and has no test.

### Migration
`20261004_120000__CAS-043-files-browse-api.sql`

**Gate 2 note (2026-10-04):** all CAS-043 tests pass (Api.Tests 512/512, real-SQL and HTTP browse tests green on the refreshed database). `tests: fail` reflects 9 failures outside this spec: 4 in `FileIndexRepositoryTests` (missing `Database/migrations/20260930_150000__CAS-038-A2-date-only-search.sql`, CAS-038) and 5 Web bUnit tests (`ODriveSyncPageTests` x4, `KeywordSearchPageTests` x1). No Web file was changed by this spec. The requester approved moving to `review` with these open. Gate 4: only `IFileBrowseService.cs` (enum/record declarations) is below 85%; overall 84.4%.

### SC-13 manual performance record (2026-10-04)

Measured by executing `dbo.file_index_browse_lst` directly with `sqlcmd` against the local development
database refreshed from production (`dbo.file_index`: 876,554 active `B2` rows and 951,075 `ODrive` rows,
bucket `Cachet-Server-C`, prefix `D/Shares/Operations`). Times are wall-clock for the whole `sqlcmd` call
(connect, run, stream all rows to the client), so they slightly overstate the SP time. They do **not**
include the Api, JSON serialisation or the browser. Two passes per case, back to back; the SQL buffer cache
was not cleared, so "first" is not a true cold read. The 15-minute command timeout
(`FileBrowseRepository.CommandTimeoutSeconds`) was not approached. An independent spot check by the
reviewer (root, 5.66 s; root folders-only, 5.18 s) agrees.

| Case | Folder (prefix-relative) | foldersOnly | First | Repeat | Folders / files returned |
|---|---|---|---|---|---|
| Root | *(empty)* | false | 5,749 ms | 5,887 ms | 250 / 13 |
| Root, tree pane | *(empty)* | true | 5,180 ms | 5,455 ms | 250 / 0 |
| First-level folder | `MYPayrollHR` | false | 6,663 ms | 6,082 ms | 25 / 34 |
| Deep folder (depth 11) | `Bank Reconciliation - BofI/.../Spreadsheets/Spreadsheets` | false | 731 ms | 894 ms | 1 / 2 |
| Folder with most direct files | `MYPayrollHR/Email 2013-2014/2013/07-01-2013-07-31-2013-20250717141350.eml` | false | 2,440 ms | 2,573 ms | 0 / 24,634 |

Observations: cost tracks how many rows sit under the prefix, not depth; root and first-level browsing
(about 5-7 s) are the slowest and the first a user hits. If that spinner time is judged too long, the
covering-index / cached-folder-summary options in §4 and §15 Q8 are the follow-up. Root returns 250 folders
against the 265 the Windows view shows; not investigated (case-insensitive merging per §5 or inactive rows
are the likely causes).
