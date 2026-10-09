---
id: CAS-012
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
priority: P1
change_type: refactor
related_specs: [CAS-006, CAS-011, CAS-013]
touches:
  database: true
  api: true
  ui: false
  worker: true
  b2: true
  config: true
  security: false
migrations:
  - 20260922_220000__CAS-012-keyword-search-filesystem-content-search.sql
---

# CAS-012 — Change: Keyword Search Content Matches Move from SQL Full-Text to Filesystem Search

**Reconciliation note (2026-09-22, superseded — see §14 for resolution):** CAS-011 was originally
reviewed against its own draft, where its config table appeared to carry only one `local_root_path`
per `bucket_name`, with no `source` dimension. That draft has since progressed: **CAS-011 is now
`status: review` with a completed Implementation Log.** Its as-built `dbo.odrive_bucket_mapping` table
carries **two** root-path columns per `bucket_name` row — `odrive_root_path` and
`b2_mirror_root_path` — resolved by the owner directly in CAS-011's own §7/§14, precisely to cover
both mirrors without a `source`-keyed row shape. This spec's `IBucketMirrorRootRepository`/
`IMirrorFileReader` design (§5) is reconciled against that actual shape below (§14 item 2, resolved).

## §1 Current Behavior

Today, Keyword Search's Content Matches (FR-07) are produced entirely inside SQL Server, against a
`content_text NVARCHAR(MAX)` column on `dbo.file_index` that a SQL Server full-text index (with
`CHANGE_TRACKING AUTO`) is built over:

- `Database/CachetArchiveSync/Entities/dbo.file_index.sql:28` declares `content_text NVARCHAR(MAX) NULL`;
  `dbo.file_index.sql:44-53` creates `cachet_ft_catalog` and a `CREATE FULLTEXT INDEX ON
  dbo.file_index (content_text LANGUAGE 1033) ... WITH CHANGE_TRACKING AUTO`.
- `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql` (CAS-006 §5 addendum) is
  the single procedure that answers both Filename and Content match queries in one `UNION ALL`:
  - Filename branch (lines 50-63): `fi.file_name LIKE '%'+@term+'%' OR fi.relative_path LIKE
    '%'+@term+'%'` — unaffected by this spec, stays as-is.
  - Content branch (lines 66-93): `WHERE fi.content_text IS NOT NULL AND CONTAINS(fi.content_text,
    @term_phrase)`, with the match snippet computed **in T-SQL** via
    `CHARINDEX(@term, fi.content_text)` / `SUBSTRING(...)` (lines 73-86) — a ±100-character window
    around the first hit, or the first 200 characters if `CHARINDEX` finds nothing (defensive branch
    that should be unreachable given the outer `CONTAINS` filter already required a hit).
- `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs:27-53`
  (`SearchCandidatesAsync`) is a thin Dapper wrapper that calls `dbo.file_index_search` and maps
  every returned row — Filename or Content, indistinguishable except by `match_type` — into a
  `FileIndexCandidateRow` (`Core/Dtos/FileIndexCandidateRow.cs`). It does not read a single byte
  from disk; every row it returns already carries whatever `match_snippet` SQL computed.
- `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs:62-146` (`ProcessAsync`) treats every
  candidate from `SearchCandidatesAsync` identically regardless of `MatchType` — it never
  distinguishes Filename from Content candidates before B2 cross-check; it cross-checks all of them
  against B2 (`IsConfirmedOnB2Async`, lines 167-181) and inserts a `dbo.search_result` row for every
  one that is confirmed (lines 85-107), carrying the SQL-computed `candidate.MatchSnippet` through
  unchanged (line 95: `candidate.MatchType == "Content" ? candidate.MatchSnippet : null`).
- `Core/Dtos/FileIndexCandidateRow.cs:1-37`'s own XML doc (lines 4-6) explicitly documents this as
  "`Content` (`CONTAINS`/`FREETEXT` against `content_text`)" — the assumption this spec corrects.
- `Core/Dtos/ContentMatchItem.cs:16`'s doc comment ("Never the full `content_text` value") assumes
  a `content_text` column exists to be careful about — that constraint becomes moot once the column
  is removed entirely (CAS-011), not merely tightened.
- `src/Cachet.ArchiveSync.Worker.Tests/KeywordSearchTaskProcessorTests.cs` mocks
  `IFileIndexRepository.SearchCandidatesAsync` to return `FileIndexCandidateRow`s that already have
  a `MatchSnippet` populated for `Content` candidates (e.g. `ContentCandidate()`, lines 65-74) —
  i.e., the tests assume SQL already did the content match and snippet extraction before the Worker
  ever sees the candidate.
- No file content is ever read from the local O-Drive/B2 mirror filesystem anywhere in the Keyword
  Search code path today — `Core/Interfaces/IODriveFileReader.cs` /
  `Api/Storage/ODriveFileReader.cs` exist, but only for CAS-007's O-Drive Sync `SyncCopy`/
  `SyncUpdate` processors (uploading O-Drive-mirror bytes to B2), not for search.

## §2 Desired Behavior

Content Matches (FR-07) execute as a filesystem-level scan against the physical O-Drive mirror and
B2 mirror directories on the application server, per BRD §12.3 item 1. File content is never copied
into or stored in any database column. Filename matching (FR-06) is **unaffected** — it remains a
SQL `LIKE` query against `dbo.file_index` metadata.

**Revised flow, replacing only the Content half of §1:**

1. `dbo.file_index_search`'s Content branch stops term-filtering in SQL entirely. Instead of
   `CONTAINS(fi.content_text, @term_phrase)`, it returns **every active, in-scope-extension
   `file_index` row** for the requested `@bucket_name`/`@file_extension` as a content-scan
   *candidate* — regardless of whether the term is actually present — with `match_snippet` always
   `NULL` at the SQL layer (term matching and snippet generation both move to the Worker). The
   branch also now projects `fi.source` and `fi.bucket_name` (needed in step 2) — the only column
   additions to this procedure's result set.
2. The Worker's `KeywordSearchTaskProcessor` receives, per candidate, whether it is a `Filename`
   match (term already confirmed by SQL, unchanged from today) or a `Content` *candidate* (term not
   yet confirmed). For each `Content` candidate:
   a. Resolve the candidate's `(source, bucketName, relativePath)` to a physical file path using
      CAS-011's `dbo.odrive_bucket_mapping` table: one row per `bucket_name`, carrying both an
      `odrive_root_path` and a `b2_mirror_root_path` column (CAS-011 §7, as-built). The repository
      selects whichever column matches the candidate's `source` (`ODrive` → `odrive_root_path`, `B2`
      → `b2_mirror_root_path`), then joins the selected root with `relativePath`.
   b. Read the file's bytes from that resolved local path.
   c. Extract the file's text content (per file type — PDF/DOC/DOCX/XLS/XLSX/TXT, the BRD §4.1
      in-scope types) and check whether the requested `term` is present. **Resolved (§14):**
      per-file text extraction in the Worker process, using a file-type-specific extraction library
      per in-scope extension (PDF text extraction; Open XML SDK or equivalent for DOC/DOCX/XLS/XLSX;
      plain read for TXT), followed by a simple substring/word match — no pre-built external index.
   d. If the term is **not** found, the candidate is dropped silently — no B2 cross-check call is
      made for it (this also avoids spending B2 API cost, per BRD §10.1's cost-containment intent,
      on candidates that turn out not to be real content matches).
   e. If the term **is** found, a bounded snippet (same ±100-character window convention as today's
      SQL, computed in the Worker instead) is attached, and the candidate proceeds through the
      **same, unmodified** B2 cross-check → insert `dbo.search_result` path that Filename matches
      already use (FR-08) — local-first, still followed by B2 cross-check before becoming
      actionable.
3. A file that cannot be read (deleted from the mirror since last indexed, permission error,
   corrupt/unsupported content for its declared extension) is skipped for content matching — logged
   as a warning, not a task failure (§7 V10) — since a single unreadable mirror file must not abort
   an entire Keyword Search task.
4. No extracted file text, and no more than the existing bounded snippet, is ever persisted to
   `dbo.search_result.match_snippet` or logged (§11) — the full extracted text lives only in Worker
   process memory for the duration of matching one file, then is discarded.

Filename matching (step 1's unchanged `LIKE` branch) and the B2 cross-check (step 2e) are explicitly
**not** touched by this spec beyond the new `source`/`bucket_name` columns needed to resolve a
mirror path.

## §3 Reason for Change

- **Type:** `refactor`
- **Trigger:** BRD v2.3, §12.3 item 1 (Product Manager review, 2026-09-22, R. Devarajan,
  TraceCounts): *"File content shall **not** be copied into or stored in any database table.
  Filename and content search execute against the physical file system — the O-Drive mirror
  directory and the B2 mirror directory on the application server — not against a database
  full-text index. The local index table(s) hold search-result metadata only (file identity, path,
  size, hash, timestamps), never extracted file content. This corrects the working assumption
  recorded in the Data Flow Analysis document (item D) that a SQL Server full-text index over a
  stored `content_text` column would serve this requirement."*
- **Risk if not changed:** The current implementation stores full extracted file content in SQL
  Server (`dbo.file_index.content_text`), which is a confirmed, binding client correction, not an
  option — continuing to store file content in the database directly contradicts BRD §12.3 item 1
  and the data-minimization posture already recorded in BRD §8/§9. This must be corrected before the
  Keyword Search module can be considered compliant with the current BRD.

## §4 Blast Radius (Graph Impact)

Graphify not yet vendored for application code changes of this scope — `graphify-out/` indexes the
BRD/WBS/spec documentation corpus (built 2026-09-10, refreshed since CAS-006/007/008/009/010), but
no live query was run for this spec; analysis below is a manual grep/read of the current codebase
(`content_text|CONTAINS|FREETEXT`, `IODriveFileReader`) rather than a graphify query, and this is
stated explicitly per CLAUDE.md's fallback instruction.

### Primary Change Target
File: `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql` (Content branch) and
`src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs` (content-match handling).
Entity: `dbo.file_index_search`'s Content result set; `KeywordSearchTaskProcessor.ProcessAsync`'s
per-candidate loop.

### Direct Dependents (nodes with edges INTO the changed entity)
- `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs` (`SearchCandidatesAsync`) — the
  only caller of `dbo.file_index_search`; must map the two new `source`/`bucket_name` columns.
- `src/Cachet.ArchiveSync.Core/Dtos/FileIndexCandidateRow.cs` — must carry `Source`/`BucketName` so
  the Worker can resolve a mirror path; its doc comment's `CONTAINS`/`FREETEXT` claim is now wrong
  and must be corrected.
- `src/Cachet.ArchiveSync.Worker.Tests/KeywordSearchTaskProcessorTests.cs` — every existing
  Content-match fixture (`ContentCandidate()`, `SetConfirmedOnB2`/`SetNotConfirmedOnB2` usage in
  `Test_SC3_...`, `Test_SC11_...`) assumes SQL already produced a matched snippet; these must be
  rewritten to model the new match/no-match-then-drop step.

### Downstream Modules
- **Keyword Search** (this spec's primary module — FR-04–FR-09).
- **Database** (Database-kind, but the actual DDL for dropping `content_text`/the full-text
  catalog/index and adding the bucket→mirror-root config table is CAS-011's, not this spec's — see
  §7's explicit dependency note).
- No other BRD module (O-Drive Sync, Quarantine, Task Manager, Audit Log, Reporting) is functionally
  changed by this spec, though O-Drive Sync's own `IODriveFileReader`/`Sync:ODriveMirrorRootPath`
  (CAS-007) is a **related, but explicitly out-of-scope-here**, precedent — see §14.

### Cross-Project Risk
`Cachet.ArchiveSync.Core` (shared by `Api` and `Worker`): `FileIndexCandidateRow` gains two fields
consumed by the Worker but produced by the Api-layer repository — a genuine `Core` ↔ `Api` ↔
`Worker` three-way edge, same shape as CAS-006's original design. No `Cachet.ArchiveSync.Web`
edge — `touches.ui: false`, no Blazor component reads any of this.

### God Node Involvement
`dbo.file_index` is the highest-edge-count table in the schema (referenced by CAS-003, CAS-006,
CAS-007, CAS-008 at minimum) — a change to one of its supporting procedures is inherently
higher-risk than an isolated table change, though this spec touches only `dbo.file_index_search`'s
Content branch and column list, not `dbo.file_index` itself (CAS-011 owns that DDL).

### Graphify Queries Run
None — `graphify-out/` was not queried live for this spec; the analysis above is a manual grep/read
of the codebase, per CLAUDE.md's stated fallback ("say so explicitly in the spec's §17/§4").

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql` | modify | Content branch: remove `CONTAINS(fi.content_text, @term_phrase)` filter and the `CHARINDEX`/`SUBSTRING` snippet computation; return every active in-scope `file_index` row for `@bucket_name`/`@file_extension` as a content-scan candidate with `match_snippet = NULL`; add `fi.source`, `fi.bucket_name` to the projected columns (both branches, for a consistent shape). Filename branch is untouched. **This DDL edit ships inside CAS-011's migration** (§7) — listed here because this spec's C#/Worker code depends on its exact resulting shape, not because this spec authors a new migration file of its own. |
| `src/Cachet.ArchiveSync.Core/Dtos/FileIndexCandidateRow.cs` | modify | Add `Source` (`"ODrive"`/`"B2"`) and `BucketName` fields; rewrite the doc comment to remove the `CONTAINS`/`FREETEXT`/`content_text` claim and describe the new candidate-vs-confirmed-match split for `Content` rows. |
| `src/Cachet.ArchiveSync.Core/Interfaces/IFileIndexRepository.cs` | modify | `SearchCandidatesAsync`'s XML doc updated to describe the Content branch as "unfiltered candidates for content scanning," not "full-text matches." No signature change. |
| `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs` | modify | Map the two new `source`/`bucket_name` columns from `dbo.file_index_search`'s result set onto `FileIndexCandidateRow.Source`/`BucketName`. |
| `src/Cachet.ArchiveSync.Core/Interfaces/IMirrorFileReader.cs` | add | New abstraction: `Task<byte[]> ReadAllBytesAsync(string source, string bucketName, string relativePath, CancellationToken ct)` — reads one file's bytes from whichever local mirror root (O-Drive or B2) CAS-011's config table resolves for the given `source`/`bucketName`. Deliberately **not** a reuse of CAS-007's `IODriveFileReader` — see §14 for why a new interface was chosen over extending that one. |
| `src/Cachet.ArchiveSync.Api/Storage/MirrorFileReader.cs` | add | `IMirrorFileReader` implementation: calls `IBucketMirrorRootRepository.GetRootPathAsync(bucketName, source)` to resolve the root path (selecting `odrive_root_path` or `b2_mirror_root_path` per `source` — CAS-011 §7 as-built), then `Path.Combine(root, relativePath)` + `File.ReadAllBytesAsync`, mirroring `ODriveFileReader`'s existing pattern (CAS-007) but table-driven and source-aware instead of a single flat config key. |
| `src/Cachet.ArchiveSync.Core/Interfaces/IBucketMirrorRootRepository.cs` | add | `Task<string> GetRootPathAsync(string bucketName, string source, CancellationToken ct)` — reads CAS-011's `dbo.odrive_bucket_mapping` table (as-built: one row per `bucket_name` with both `odrive_root_path` and `b2_mirror_root_path` columns) via `dbo.odrive_bucket_mapping_get`, returning whichever column matches `source`. Throws a clear, non-path-leaking exception (caught by the existing §7 V10 skip-not-fail path) if no active mapping row exists for `bucketName`. |
| `src/Cachet.ArchiveSync.Api/Repositories/BucketMirrorRootRepository.cs` | add | Dapper implementation of the above, calling `dbo.odrive_bucket_mapping_get` (CAS-011, confirmed name and shape). |
| `src/Cachet.ArchiveSync.Core/Interfaces/IFileContentMatcher.cs` | add | `Task<string?> TryMatchAsync(byte[] fileBytes, string fileExtension, string term, CancellationToken ct)` — returns a bounded snippet if `term` is found in the file's extracted text, else `null`. |
| `src/Cachet.ArchiveSync.Api/Content/FileContentMatcher.cs` (dispatcher) + one extractor class per in-scope file type | add | **Resolved (§14):** per-file-type text extraction (PDF text-extraction library; Open XML SDK or equivalent for DOC/DOCX/XLS/XLSX; plain read for TXT) dispatched by file extension, followed by a substring/word match against `term` in Worker process memory — no external indexing service or pre-built index. Concrete library selection is an `/implement`-time detail; the dispatcher/extractor-per-type layout is confirmed here so `IFileContentMatcher`'s call site does not change regardless of which specific library is used per type. |
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs` | modify | Split the per-candidate loop: `Filename` candidates proceed exactly as today; `Content` candidates first resolve bytes via `IMirrorFileReader` and call `IFileContentMatcher.TryMatchAsync` — only a non-null result proceeds into the existing B2 cross-check/insert path (with the returned snippet in place of the old SQL-computed one); a `null` result or a file-read failure (§7 V10) drops the candidate without a B2 call, without an insert, and without a `CrossCheckExcluded` audit row (that audit action is reserved for candidates B2 itself rejects, per CAS-006 §12 — a content non-match is a different, non-audited case, since no local match was ever confirmed to begin with). |
| `src/Cachet.ArchiveSync.Core/Dtos/ContentMatchItem.cs` | modify | Update the doc comment — "Never the full `content_text` value" is rewritten since `content_text` no longer exists; restate the same data-minimization intent generically ("never the full extracted file text"). |
| `src/Cachet.ArchiveSync.Worker.Tests/KeywordSearchTaskProcessorTests.cs` | modify | Add `IMirrorFileReader`/`IFileContentMatcher` fakes to the `Fixture`; rewrite `ContentCandidate()`/`Test_SC3_...`/`Test_SC4_...`/`Test_SC11_...` to model the new candidate → read → match-or-drop → cross-check sequence; add new tests per §13. |
| `src/Cachet.ArchiveSync.Api/Program.cs`, `src/Cachet.ArchiveSync.Worker/Program.cs` | modify | DI registration for `IMirrorFileReader`/`MirrorFileReader`, `IBucketMirrorRootRepository`/`BucketMirrorRootRepository`, `IFileContentMatcher`/its concrete implementation. |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `Database/CachetArchiveSync/Entities/dbo.file_index.sql` | Dropping `content_text` and its full-text catalog/index is CAS-011's DDL, not this spec's — this spec only depends on the resulting column set. |
| `Database/migrations/20260910_153624__CAS-003-core-schema-foundation.sql` | Historical, append-only migration — never edited retroactively. |
| `src/Cachet.ArchiveSync.Core/Interfaces/IODriveFileReader.cs`, `src/Cachet.ArchiveSync.Api/Storage/ODriveFileReader.cs` | CAS-007's own O-Drive Sync reader (`Sync:ODriveMirrorRootPath`, a single flat config key). This spec deliberately introduces a separate `IMirrorFileReader` rather than extending this one — see §14 for the reasoning and the follow-on question about eventually consolidating the two. |
| `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs`'s `InsertAsync`/`UpdateAsync` and their `@content_text` parameter | Still called by CAS-007's `SyncCopy`/`SyncUpdate` processors; already passes `content_text = null` today (line 148) so no *behavioral* change results from the column's removal, but removing the now-dead parameter from the C# call site and from `dbo.file_index_ins`/`dbo.file_index_upd`'s signatures is CAS-011's companion cleanup, not this spec's (out of scope — flagged in §14). |
| `src/Cachet.ArchiveSync.Api/Controllers/SearchController.cs` | No request/response contract change (§8) — the controller's three endpoints are untouched. |
| Any `Cachet.ArchiveSync.Web` file | `touches.ui: false` — no Blazor component exists for Keyword Search yet (CAS-006 §8), so none is affected. |
| `dbo.search_result`, `dbo.quarantine_entry` (tables and their procedures) | Unaffected — this spec only changes what feeds the *candidate* stage before a `search_result` row is ever considered; the confirmed-match table and its procedures are untouched. |

## §7 Database Changes

### Changed Objects
| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| `dbo.file_index_search` | dbo | Content branch: drop `CONTAINS(fi.content_text, @term_phrase)` filter and `CHARINDEX`/`SUBSTRING` snippet logic; return all active in-scope-extension rows for `@bucket_name`/`@file_extension` as unfiltered content-scan candidates (`match_snippet = NULL`); add `fi.source`, `fi.bucket_name` to both branches' projected columns | Filename branch (`LIKE`) is unchanged. This is a `CREATE OR ALTER`, same object name — no new procedure. |
| `dbo.file_index` | dbo | Drop `content_text` column and its full-text catalog/index | **Owned by CAS-011, not this spec** — listed here only because it is the reason `dbo.file_index_search` can no longer reference `content_text`. |
| `dbo.odrive_bucket_mapping` | dbo | Consumed, not authored, by this spec | **Owned by CAS-011** (status `review`, implemented). Confirmed as-built shape: one row per `bucket_name`, columns `odrive_root_path NVARCHAR(1000)`, `b2_mirror_root_path NVARCHAR(1000)`, `is_active BIT`, standard audit columns, read via `dbo.odrive_bucket_mapping_get`. This spec's `IBucketMirrorRootRepository` (§5) is reconciled against this exact shape — no further reconciliation needed at `/implement` time (§14, resolved). |

### Migration Required
**Corrected at `/implement` time (2026-09-22) — this spec DOES ship its own migration,
superseding the "No" originally written here.** The original assumption was that this procedure
edit was schema-coupled to CAS-011's `content_text` column drop and would ship inside CAS-011's
migration. On actually reading the deployed state, CAS-011's migration
(`20260922_210000__...`) only *neutralized* the Content branch — it dropped the `CONTAINS`/
`content_text` reference, leaving `dbo.file_index_search` returning zero rows for
`@match_type = 'Content'` (documented, expected, in CAS-011 §7/§9 V1) — and explicitly deferred
the unfiltered-candidate redesign to this spec, per that procedure's own header comment. CAS-012
therefore introduces its own migration:
`Database/migrations/20260922_220000__CAS-012-keyword-search-filesystem-content-search.sql`
(`CREATE OR ALTER PROCEDURE`, same object name, timestamped after CAS-011's migration). Frontmatter
`migrations:` has been updated accordingly.

## §8 API Contract Changes

| Endpoint | Field | Before | After | Breaking? |
|----------|-------|--------|-------|-----------|
| `GET /search/{taskId}/results` | `contentMatches.items[].snippet` | Computed in T-SQL (`CHARINDEX`/`SUBSTRING`, ±100 chars) | Computed in Worker application code (same ±100-char window convention) after reading the file from the local mirror | No — response DTO shape (`ContentMatchItem`, CAS-006 §6) is unchanged; only where the snippet value is computed changes. |
| `POST /search`, `POST /search/results/quarantine` | — | — | — | No — neither request nor response contract touched by this spec. |

No API version bump required — no field added, removed, or retyped on any request/response DTO.

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| — | (CAS-006 V1–V9 all continue to apply unchanged — none of them referenced `content_text`/`CONTAINS`/`FREETEXT` directly) | Unchanged | (existing tests) |
| V10 (new) | N/A — SQL's `CONTAINS` filter meant a candidate was already a content match by construction; no "unreadable file" case existed | A content-scan candidate whose file cannot be read from the resolved local mirror path (missing, permission error) or whose text cannot be extracted for its declared extension is skipped — logged as a warning, not a task failure; the `KeywordSearch` task must still reach `Completed` | `Test_V10_UnreadableMirrorFileSkippedNotTaskFailure` |
| V11 (new) | N/A | A `Content` candidate whose extracted text does not contain `term` is dropped before any B2 cross-check call is made (no B2 API cost spent on non-matches) | `Test_V11_NonMatchingContentCandidateNeverCrossChecked` |
| V12 (new) | N/A | The Worker-computed content match snippet is bounded to the same length CAS-006 §5's `dbo.search_result.match_snippet NVARCHAR(1000)` column allows — never the full extracted file text | `Test_V12_ContentMatchSnippetIsBoundedNotFullText` |

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A Content Match search against a known test file present on the local O-Drive/B2 mirror, containing the search term, returns the expected match with a correctly bounded snippet — with the executed `dbo.file_index_search` SQL containing no `content_text`/`CONTAINS`/`FREETEXT` reference anywhere | `Test_SC1_FilesystemContentSearchReturnsExpectedMatch` |
| SC-2 | A `file_index` row in-scope for the bucket/extension but whose file's extracted text does **not** contain the term is excluded from `dbo.search_result` entirely, and no B2 cross-check call (`IB2Client`) is made for it | `Test_SC2_NonMatchingCandidateExcludedWithNoB2Call` |
| SC-3 | A file that fails to be read from the local mirror (simulated `IMirrorFileReader` I/O exception) does not fail the `KeywordSearch` task — the task still reaches `Completed`, and the failure is logged as a warning for that one file only | `Test_SC3_UnreadableFileDoesNotFailTask` |
| SC-4 | No extracted file text beyond the bounded snippet is ever persisted to `dbo.search_result.match_snippet` or written to a log line (extends CAS-006 SC-11's log-hygiene check to also cover the newly-introduced file-read/extraction code path) | `Test_SC4_NoFullExtractedContentPersistedOrLogged` |

## §11 Security Notes

- **Transient file access only:** the Worker process now reads full file bytes from the local
  mirror filesystem for the duration of one content-match check, then discards them — file content
  is never written to any database column (BRD §12.3 item 1), never cached, and never persisted to
  disk anywhere other than its existing mirror location.
- **No new credential surface:** resolving a mirror root path (via CAS-011's config table) and
  reading a local file involves no B2 key, session token, or other credential — `IMirrorFileReader`
  and `IBucketMirrorRootRepository` never touch `IB2Client`/`IUserB2KeyAuthorizer`.
- **Logging carries forward CAS-006 §11's rule unchanged:** never log the B2 application key, a
  session token/cookie value, full file content, the raw extracted text of any file, or the
  user-entered search `term` itself. This now additionally covers the new file-read/extraction code
  path (§10 SC-4) — a stack trace or exception message surfaced when a file fails to read/extract
  must not include file content, and file paths logged (if any) follow the same truncation
  convention the audit log's `file_path` column already uses (CAS-006 §11).
- **Audit trail unaffected:** this spec introduces no new state-changing endpoint and no new audit
  action — `KeywordSearchSubmitted`/`QuarantineAdd`/`CrossCheckExcluded` (CAS-006 §12) are unchanged.
  A content candidate dropped for not matching the term (§7 V11) is **not** an audit event — it was
  never a confirmed local match to begin with, distinct from `CrossCheckExcluded` (which is reserved
  for a candidate B2 itself rejects, per CAS-006 §12's existing distinction).
- **No B2 credential brokering change:** `frontmatter touches.security: false` because no
  authentication/session/B2-key-handling code path is touched — this is a data-flow/storage change
  within an already-authenticated, already-authorized code path (CAS-006 §12's
  `[Authorize(Policy = "CachetAuthenticated")]` and system-level `IB2Client` usage are both
  unchanged).

## §12 Rollback Plan

1. **Application code rollback:** revert `Api`/`Worker`/`Core` to the pre-CAS-012 commit. This is
   safe only if CAS-011's `content_text` column has **not** yet been dropped in the target
   environment — reverting this spec's code while CAS-011's DDL is already applied would leave the
   old SQL-based content-search code path referencing a column that no longer exists, breaking
   Keyword Search's Content Matches entirely. Rollback order matters: **application code first**, if
   CAS-011 hasn't shipped yet; **CAS-011's DDL rollback first**, if it has (see CAS-011's own
   rollback plan).
2. **Database rollback:** N/A for this spec directly — it introduces no migration of its own (§7).
   Any rollback of `dbo.file_index_search`'s content-branch edit is CAS-011's migration's rollback
   responsibility, since that is where the edit ships.
3. **Config rollback:** N/A — no new secret-bearing config key is introduced; any new non-secret key
   this spec's implementation needs (e.g., a content-extraction library's own settings, once §14 is
   resolved) is additive and safely removable.
4. **Task-queue rollback:** any `KeywordSearch` task already `Queued`/`Running` at deploy time was
   built against the *old* `KeywordSearchRequestDto`/`KeywordSearchResultSummaryDto` shape, which is
   **unchanged** by this spec — no `task_queue.request_parameters`/`result_detail` contract change
   occurs, so in-flight rows are unaffected either way. The only real risk is the SQL/column-drop
   sequencing in point 1, not the task queue's own row shape.

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `KeywordSearchTaskProcessorTests` (rewritten fixtures, new `IMirrorFileReader`/`IFileContentMatcher` fakes) | `Test_SC1_FilesystemContentSearchReturnsExpectedMatch` | SC-1 |
| `KeywordSearchTaskProcessorTests` | `Test_SC2_NonMatchingCandidateExcludedWithNoB2Call` | SC-2, V11 |
| `KeywordSearchTaskProcessorTests` | `Test_SC3_UnreadableFileDoesNotFailTask` | SC-3, V10 |
| `KeywordSearchTaskProcessorTests` | `Test_V12_ContentMatchSnippetIsBoundedNotFullText` | V12 |
| `KeywordSearchTaskProcessorTests` (extends existing `Test_SC11_...`-style log-hygiene test) | `Test_SC4_NoFullExtractedContentPersistedOrLogged` | SC-4 |
| `FileIndexRepositoryTests` (new or extended — real/mocked SQL) | Round-trip of `source`/`bucket_name` columns from `dbo.file_index_search` onto `FileIndexCandidateRow` | Supports SC-1/SC-2 |
| `BucketMirrorRootRepositoryTests` (new) | Root-path resolution for a known `(bucketName, source)` pair; not-found case | Supports SC-1/SC-3 |
| `MirrorFileReaderTests` (new) | Successful read; missing-file/I/O-exception path | Supports SC-3 |
| Concrete `IFileContentMatcher` implementation's own test class — **not named here**, since the class itself is not authored by this spec draft (§5, §14 BLOCKED) | Left to the implementer once §14's technology question is resolved | Supports SC-1, SC-2 |

The existing `Test_SC3_WorkerProcessorProducesConfirmedSearchResults`,
`Test_SC4_StaleCandidateSelfHealsAndExcludes`, and the `SC-11` log-hygiene test from CAS-006's own
test plan continue to apply to the **Filename** path unchanged; only their `Content`-path fixtures
need rewriting to exercise the new read/match/drop sequence rather than assuming SQL already
produced a matched snippet.

Coverage target: ≥ 85% on changed files (matching CAS-006's own precedent).

## §14 Open Questions

- [x] **RESOLVED 2026-09-22 (owner, this conversation) — Content extraction/matching technology.**
      Decision: **per-file extraction in the Worker process, no external index.** A file-type-specific
      extraction library is dispatched per in-scope extension (PDF text extraction; Open XML SDK or
      equivalent for DOC/DOCX/XLS/XLSX; plain read for TXT), followed by a simple substring/word match
      — no Windows Search/Indexing Service or other pre-built index component.
      **Rationale:** at this project's confirmed scale (BRD §12.3 item 13: ~500GB combined mirrors,
      thousands of files, 3–4 concurrent users, no future-growth headroom required), per-search
      scanning is well within the FR-22a completion-time budget (10 minutes–a full day), and avoids
      standing up a second system that would itself hold derived file content outside the
      application's control — which would cut against the same data-minimization intent (BRD §12.3
      item 1) driving this whole spec. `IFileContentMatcher` (§5) does not need to change regardless
      of which specific per-type library is chosen at `/implement` time.
      **Accepted trade-off, not a defect:** every Content Match search re-reads and re-extracts every
      in-scope file every time — no incremental/cached benefit. This is the explicitly accepted cost
      of not storing content in the database (BRD §12.3 items 1 and 13).
- [x] **RESOLVED 2026-09-22 — CAS-011's config table shape (supersedes the original "confirmed gap"
      framing above).** CAS-011 has since shipped (status `review`, Implementation Log complete). Its
      as-built `dbo.odrive_bucket_mapping` table carries **two** root-path columns on one row per
      `bucket_name` — `odrive_root_path` and `b2_mirror_root_path` — rather than a `source`-keyed row
      per bucket. This resolves the gap directly: `IBucketMirrorRootRepository.GetRootPathAsync
      (bucketName, source)` (§5) reads the single mapping row for `bucketName` via
      `dbo.odrive_bucket_mapping_get` and returns whichever column matches `source`. No further
      reconciliation needed at `/implement` time.
      **Note:** this differs from an earlier proposal to skip a mapping table for the B2 side
      entirely (deriving the B2 mirror's local folder from a fixed base path + bucket-name
      convention). That approach is **not** what was built — CAS-011 already stores an explicit,
      owner-configurable `b2_mirror_root_path` per bucket, which this spec now depends on as-is. No
      action needed; noted here only so the two prior conversations don't appear to disagree.
      **Follow-up flagged, not resolved by this edit:** CAS-013's own §14 still asks the equivalent
      question ("does CAS-011 need a separate B2-mirror-side root?") against its now-stale
      understanding of CAS-011 as single-root. That item should be corrected in CAS-013 directly —
      out of scope for this edit since it wasn't requested, but noted here for traceability.
- [ ] **Deferred, not blocking — CAS-007 (`IODriveFileReader`/`Sync:ODriveMirrorRootPath`)
      consolidation.** CAS-007's own local-file reader for O-Drive Sync and this spec's
      `IMirrorFileReader` now solve a near-identical problem (reading a local mirror file given a
      relative path) with two different root-resolution strategies. Per Karpathy P4, the pattern has
      now appeared twice, which justifies consolidating — but doing so here would require editing
      CAS-007's own files, which are explicitly out of scope for this spec (§6). Tracked as a future
      follow-on spec, to be picked up once CAS-011, CAS-012, and CAS-013 have all shipped. Not a
      blocker to this spec's `status: ready`.
- [ ] **Accepted trade-off, not blocking — performance/cost of per-search full-file scanning.**
      Unlike the SQL full-text index (pre-built, incrementally maintained via `CHANGE_TRACKING
      AUTO`), this design reads and extracts text from every in-scope-extension file in the bucket on
      every Content Match search, every time. BRD §12.3 items 1 and 13 explicitly accept this
      trade-off (no file content in the database, full stop; no future-growth headroom required
      beyond current scale). Recorded here as a signed-off NFR acceptance, not an open question.
- [ ] **Accepted scope limitation, not blocking — scanned/image-only PDFs.** BRD §4.2 confirms image
      file types are out of scope, but a scanned (image-only, no embedded text layer) PDF is
      technically an in-scope file type (PDF) whose content extraction would yield no text without
      OCR. Consistent with BRD §4.2's own note that OCR/image-content extraction is a separate,
      not-yet-scoped future requirement — such a file will simply produce zero content matches under
      the current scope, which is expected behavior, not a defect. No design change needed unless the
      client later requests OCR support as a new FR.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 | Initial draft. |
| Owner | devarajan@tracecounts.org | 2026-09-22 | Confirmed both §14 blockers: (1) per-file extraction/matching in the Worker, no external index; (2) CAS-011's as-built `dbo.odrive_bucket_mapping` (dual `odrive_root_path`/`b2_mirror_root_path` columns per bucket) is sufficient — no separate B2 mapping table needed. `status` moved `draft` → `ready`. |
| Reviewer | | | Pending. Note: CAS-011's migration (`20260922_210000__...`) is confirmed merged and deployed, so the deployment-ordering condition originally noted here is already satisfied — no remaining gate on that point. |

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches what is actually in the code BEFORE the change — specifically,
      that `dbo.file_index_search`'s Content branch really does use `CONTAINS`/`content_text` today
      and that `KeywordSearchTaskProcessor` really does treat all candidates uniformly today.
- [ ] Desired behavior (§2) is fully implemented: Content candidates are read from the local mirror,
      matched in application code, and only a positive match proceeds to B2 cross-check.
- [ ] Only files in §5 were modified (§6 files are untouched) — in particular, that
      `Database/CachetArchiveSync/Entities/dbo.file_index.sql` was **not** edited by this spec's
      implementation (that is CAS-011's DDL).
- [ ] All §10 Success Criteria (SC-1–SC-4) have passing tests.
- [ ] All §9 Validations (V10–V12) are implemented and tested; CAS-006's original V1–V9 still pass
      unmodified.
- [ ] §8 API contract confirmed unchanged (no field added/removed on `ContentMatchItem` or any other
      DTO); no API version bump occurred.
- [ ] §11 Security Notes implemented: no file content, extracted text, or search term appears in any
      log line across the new file-read/extraction code path.
- [ ] §4 Blast Radius is accurate — no unintended side effects in CAS-007's `IODriveFileReader`/
      `ODriveFileReader`, and no `dbo.file_index` DDL changes attributed to this spec's own migration
      (there is none — §7).
- [ ] §12 in-flight `KeywordSearch` task-queue rows are unaffected by this change (no
      `request_parameters`/`result_detail` contract change occurred).
- [ ] Confirm the exact `dbo.file_index_search` shape actually deployed by CAS-011 matches what this
      spec's `FileIndexRepository`/`FileIndexCandidateRow` code was written against — reconcile any
      drift (§14).

### Verification Report Format
Reviewer writes `CAS-012.review.md`:
- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5 file list)
- Unintended blast radius (manual read — graphify not queried live for this spec, §4)
- Security gaps

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 100% (Content extractors, FileContentMatcher, MirrorFileReader), 98.1% (KeywordSearchTaskProcessor); FileIndexRepository.cs/BucketMirrorRootRepository.cs below threshold (Dapper/SQL-backed, real-DB-only per established project convention, no live SQL Server in this sandbox); FileIndexCandidateRow.cs 80% (compiler-generated record members, same unaddressed pattern as every other DTO in this codebase) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20260922_220000__CAS-012-keyword-search-filesystem-content-search.sql` | New migration: reintroduce Content branch as unfiltered candidate feed; add source/bucket_name to both branches |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_search.sql` | Mirror updated to match the new migration |
| `src/Cachet.ArchiveSync.Core/Dtos/FileIndexCandidateRow.cs` | Added Source/BucketName; rewrote doc comment |
| `src/Cachet.ArchiveSync.Core/Dtos/ContentMatchItem.cs` | Doc comment updated to drop content_text reference |
| `src/Cachet.ArchiveSync.Core/Interfaces/IFileIndexRepository.cs` | SearchCandidatesAsync doc updated (no signature change) |
| `src/Cachet.ArchiveSync.Core/Interfaces/IMirrorFileReader.cs` | New interface |
| `src/Cachet.ArchiveSync.Core/Interfaces/IBucketMirrorRootRepository.cs` | New interface |
| `src/Cachet.ArchiveSync.Core/Interfaces/IFileContentMatcher.cs` | New interface |
| `src/Cachet.ArchiveSync.Api/Repositories/FileIndexRepository.cs` | Maps source/bucket_name columns onto FileIndexCandidateRow |
| `src/Cachet.ArchiveSync.Api/Storage/MirrorFileReader.cs` | New IMirrorFileReader implementation |
| `src/Cachet.ArchiveSync.Api/Repositories/BucketMirrorRootRepository.cs` | New Dapper repository calling dbo.odrive_bucket_mapping_get |
| `src/Cachet.ArchiveSync.Api/Content/ITextExtractor.cs` | New internal dispatch interface |
| `src/Cachet.ArchiveSync.Api/Content/PdfTextExtractor.cs` | New — PdfOxide-based .pdf extractor (swapped in after UglyToad.PdfPig failed nuget restore verification) |
| `src/Cachet.ArchiveSync.Api/Content/WordTextExtractor.cs` | New — NPOI for .docx, GHSoftware.WordDocTextExtractor for legacy .doc (NPOI never implemented HWPF) |
| `src/Cachet.ArchiveSync.Api/Content/ExcelTextExtractor.cs` | New — NPOI HSSF/.xls and XSSF/.xlsx |
| `src/Cachet.ArchiveSync.Api/Content/PlainTextExtractor.cs` | New — .txt |
| `src/Cachet.ArchiveSync.Api/Content/FileContentMatcher.cs` | New IFileContentMatcher implementation, dispatches by extension, bounded +/-100 char snippet |
| `src/Cachet.ArchiveSync.Api/Program.cs` | DI registration for the three new interfaces |
| `src/Cachet.ArchiveSync.Api/Cachet.ArchiveSync.Api.csproj` | Package refs: PdfOxide, NPOI, GHSoftware.WordDocTextExtractor |
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs` | Content candidates now resolve bytes + match via IMirrorFileReader/IFileContentMatcher before B2 cross-check |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | DI registration for the three new interfaces |
| `src/Cachet.ArchiveSync.Worker.Tests/KeywordSearchTaskProcessorTests.cs` | Rewrote Content-path fixtures; added SC-1/SC-2/SC-3/SC-4/V10/V11/V12 tests |
| `src/Cachet.ArchiveSync.Api.Tests/FileContentMatcherTests.cs` | New — txt/docx/xlsx/xls/pdf/doc happy paths + corrupt-bytes error paths |
| `src/Cachet.ArchiveSync.Api.Tests/MirrorFileReaderTests.cs` | New — real filesystem read, missing-file, unresolvable-mapping |
| `src/Cachet.ArchiveSync.Api.Tests/BucketMirrorRootRepositoryTests.cs` | New — constructor validation (Dapper repo, DB round-trip is integration-only) |
| `src/Cachet.ArchiveSync.Api.Tests/TestAssets/legacy-word-sample.doc` | New — real small legacy .doc fixture from GHSoftware.WordDocTextExtractor's own public test corpus (BSD-3-Clause) |
| `src/Cachet.ArchiveSync.Api.Tests/TestAssets/README.md` | New — fixture provenance |
| `src/Cachet.ArchiveSync.Api.Tests/Cachet.ArchiveSync.Api.Tests.csproj` | Copy legacy-word-sample.doc to output directory |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/BucketMirrorRootRepositoryTests.cs` | New — real-DB round trip + not-found case (skip-marked, no live SQL Server in sandbox) |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/FileIndexRepositoryTests.cs` | Extended — source/bucket_name round-trip (skip-marked) |
| `cachet_agent_tools/coverage.py` | Fixed a merge bug: coverage across multiple test projects sharing an assembly was summed additively instead of unioned per line, deflating shared-class scores |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `KeywordSearchTaskProcessorTests` | `Test_SC1_FilesystemContentSearchReturnsExpectedMatch` | SC-1 |
| `KeywordSearchTaskProcessorTests` | `Test_SC2_NonMatchingCandidateExcludedWithNoB2Call` | SC-2, V11 |
| `KeywordSearchTaskProcessorTests` | `Test_SC3_UnreadableFileDoesNotFailTask` | SC-3, V10 |
| `KeywordSearchTaskProcessorTests` | `Test_SC4_NoFullExtractedContentPersistedOrLogged` | SC-4 |
| `KeywordSearchTaskProcessorTests` | `Test_V10_UnreadableMirrorFileSkippedNotTaskFailure` | V10 |
| `KeywordSearchTaskProcessorTests` | `Test_V11_NonMatchingContentCandidateNeverCrossChecked` | V11 |
| `KeywordSearchTaskProcessorTests` | `Test_V12_ContentMatchSnippetIsBoundedNotFullText` | V12 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_TxtContainingTerm_ReturnsBoundedSnippet` | V12 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_TxtCaseInsensitiveMatch_ReturnsSnippet` | SC-1 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_TxtNotContainingTerm_ReturnsNull` | V11 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_EmptyFile_ReturnsNull` | V11 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_UnknownExtension_ReturnsNullWithoutLogging` | V10 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_DocxContainingTerm_ReturnsSnippet` | SC-1 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_DocxNotContainingTerm_ReturnsNull` | V11 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_XlsxContainingTerm_ReturnsSnippet` | SC-1 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_XlsContainingTerm_ReturnsSnippet` | SC-1 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_PdfContainingTerm_ReturnsSnippet` | SC-1 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_DocContainingTerm_ReturnsSnippet` | SC-1 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_CorruptDocxBytes_ReturnsNullAndLogsFixedWarningOnly` | V10 |
| `FileContentMatcherTests` | `Test_TryMatchAsync_CorruptPdfBytes_ReturnsNullAndLogsFixedWarningOnly` | V10 |
| `MirrorFileReaderTests` | `Test_SC1_ReadAllBytesAsync_ResolvesRootAndReadsFile` | SC-1 |
| `MirrorFileReaderTests` | `Test_SC1_ReadAllBytesAsync_ODriveSource_ResolvesViaSourceParameter` | SC-1 |
| `MirrorFileReaderTests` | `Test_SC3_ReadAllBytesAsync_MissingFile_ThrowsForCallerToSkip` | SC-3 |
| `MirrorFileReaderTests` | `Test_SC3_ReadAllBytesAsync_UnresolvableMapping_PropagatesRepositoryException` | SC-3, V10 |
| `BucketMirrorRootRepositoryTests (integration)` | `Test_SC1_GetRootPathAsync_ResolvesOdriveAndB2RootsForKnownBucket` | SC-1 |
| `BucketMirrorRootRepositoryTests (integration)` | `Test_V10_GetRootPathAsync_NoMappingRowForBucket_ThrowsNonPathLeakingException` | V10 |

### Migration
`Database/migrations/20260922_220000__CAS-012-keyword-search-filesystem-content-search.sql`
