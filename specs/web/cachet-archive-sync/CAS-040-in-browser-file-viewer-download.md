---
id: CAS-040
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-30
priority: P2
related_specs: [CAS-002, CAS-005, CAS-006, CAS-007, CAS-008, CAS-011, CAS-012, CAS-013, CAS-022, CAS-023, CAS-024, CAS-032]
touches:
  database: true   # one new read SP (dbo.file_index_get) + one additive column in dbo.quarantine_entry_lst's result set; no DDL
  api: true        # GET /files/{fileIndexId}, /view, /download; additive fileIndexId on six list DTOs
  ui: true         # FileViewerModal + clickable file names on three screens; Web pass-through endpoints
  worker: false    # synchronous read path; no task-queue involvement
  b2: false        # deliberately no B2 API call, credential, or presigned URL on this path (§12)
  config: true     # Files:Preview:* caps (§9)
  security: true   # file content leaves the server; path resolution; audit-before-response
migrations: [20261001_090000__CAS-040-file-viewer-download.sql]
---

# CAS-040 — In-Browser File Viewer and Download

WBS Task 54 (Web UI Development). Proposes **FR-28 / FR-28a / FR-28b** and an **FR-23 amendment**
through change request **CR-001** (`0.Specifications/Change-Requests/CR-001-in-browser-file-viewer-download.md`,
WBS Task 52). Builds on BRD §3(c)/§3(d), §4.1, FR-03, FR-04–FR-09, FR-11, FR-14, FR-23, §9, and
§12.3 items 1–2.

---

## §0 Gate (read first)

> **Build authorized at risk, 2026-09-30. Deployment is BLOCKED until CR-001 is signed.**
> FR-28/FR-28a/FR-28b do not exist in BRD v2.3. On 2026-09-30, ratheesh@scientificvision.net
> (delivery team) authorized implementation **before** client sign-off, using CR-001's recommended
> defaults (D2 = option (a), D5 = O-Drive only). This moves the spec to `ready`, with these
> conditions:
> - **No deployment to any client-facing or production environment until CR-001 §6 is signed** and
>   the BRD is revised to v2.4. §14 step 1 remains a hard precondition for deployment.
> - If the client rejects CR-001, or changes D2 or D5, the implementation is reverted or amended by
>   a change spec before it merges to a release branch.
>
> CR-001's client decisions also determine parts of this spec:
> - **D2** (download authorization) sets the `download` endpoint's policy (§6.3). The spec assumes
>   option (a), `CachetAuthenticated`. If the client picks (b), change one attribute to
>   `CachetFullAccess`, hide the Download button for users without Full Access (§8.3), and add SC-19.
> - **D5** (Modified rows) sets which `fileIndexId` a Modified row exposes (§6.5). The spec assumes
>   O-Drive only.

### §0.1 Decomposition decision (Karpathy P9)

These split signals fired:
- **Project boundary:** the feature touches Api, Web and Database.
- **Test-domain split:** it needs both Api xUnit/WebApplicationFactory tests and Web bUnit tests.
- **Size:** it has more than 12 success criteria and more than 15 changed files.

The spec is **kept as one document on purpose**, for three reasons:
1. CR-001 is a single sign-off gate for the whole feature.
2. The Api response headers (§6) and the Web pass-through (§8.5) together form one security
   boundary, so they are best reviewed as a unit.
3. The WBS task text asks for one spec.

To keep it splittable, the spec is laid out as **Part A: Database + Api** (§5–§7, SC-1–SC-12) and
**Part B: Web** (§8, SC-13–SC-18). §14 deploys A before B. If the reviewer or the owner prefers,
Part B can be moved to CAS-041 without editing Part A.

---

## §1 Summary

A reviewer can click a file name in the Keyword Search Filename and Content Matches panels, the
Keyword Search quarantine panel, the three O-Drive Sync and Content Scan panels (Removed / New /
Modified), and the Quarantine Queue modal. Clicking opens a **File Viewer** pop-up that shows a
read-only rendering of the file, plus a **Download** button that saves the unaltered original.

The file is found by its `dbo.file_index.file_index_id`, never by a path from the client. It is read
from the local O-Drive or B2 mirror directory, resolved through `dbo.odrive_bucket_mapping` (BRD
§12.3 item 2). It is streamed from Api, through a thin Web pass-through, to the browser. No B2 API
call, B2 credential or B2 download URL is involved (FR-03). Every view and download writes an
audit row **before** any content bytes are sent (FR-23, as amended by CR-001).

---

## §2 User Story

As a **TraceCounts staff reviewer**, I want **to open a matched, synced or quarantined file inside
the application and, when needed, download the original**, so that **I can confirm a match before
I quarantine it or permanently delete it, without going around the audited system**.

---

## §3 In-Scope

**Part A: Database + Api**
- New SP `dbo.file_index_get` (by `file_index_id`, including inactive rows).
- `dbo.quarantine_entry_lst` result set gains `fi.file_index_id`.
- Additive `fileIndexId` field on `FilenameMatchItem`, `ContentMatchItem`, `NewOnODriveItem`,
  `RemovedFromODriveItem`, `ModifiedOnODriveItem`, `QuarantineListItem` (§6.5).
- New `FilesController` with `GET /files/{fileIndexId}` (metadata and view mode),
  `GET /files/{fileIndexId}/view` and `GET /files/{fileIndexId}/download`.
- Server-side preview rendering, following the per-type matrix in §8.1:
  - DOCX/XLS/XLSX rendered to encoded HTML via NPOI;
  - DOC rendered as extracted text via the existing b2xtranslator path;
  - TXT decoded with encoding detection and capped.
- A mirror-root containment check (§12) on the new streaming read method.
- `AuditAction.FileViewed` and `AuditAction.FileDownloaded` (appended to the enum).

**Part B: Web**
- `FileViewerModal.razor` (+ `.razor.css`).
- File names become clickable in the seven panels listed in §8.2.
- Web pass-through endpoints `GET /files/{fileIndexId}/view` and `/download` in the Web
  `Program.cs`. These are needed because the browser's only direct relationship is with Web
  (CAS-002 §6.6).

---

## §4 Out-of-Scope

- **B2 fallback.** A file missing from the local mirror is reported as missing (§6.4). It is never
  fetched live from B2, and no B2 presigned URL or `b2_get_download_authorization` is used.
- **Other file types.** Images and any other extension outside {pdf, txt, doc, docx, xls, xlsx} are
  not supported (BRD §4.2 / §12.1 item 7).
- **High-fidelity office rendering.** No LibreOffice, no commercial converter, no images,
  headers/footers, comments, tracked changes or embedded objects in previews (CR-001 risk 2, §15 Q1).
- **Range requests.** No HTTP `Range`/partial content, `ETag` or `Last-Modified`
  (`enableRangeProcessing: false`). This keeps exactly one audit row per view.
- **Viewing both versions of a Modified row.** Deferred pending CR-001 D5.
- **Other modules.** No change to Quarantine, Permanent Delete, Purge, Copy/Update or cross-check
  behavior (FR-13–FR-17, FR-08).
- **Existing read paths.** No change to `IMirrorFileReader.ReadAllBytesAsync` or `ODriveFileReader`.
  Adding the containment check there too is a follow-up (§15 Q4), not a drive-by edit.
- **Web caching (BRD §12.4 item 2).** This spec does not depend on the open caching decision. File
  responses are `no-store` by design and are never cached.
- **Audit Log UI.** No changes. The new action values appear there automatically (CAS-031).

---

## §5 Database Objects

### Tables / Columns

N/A. There is no DDL change. `dbo.audit_log.tab_module`'s CHECK constraint already allows
`Keyword Search`, `O-Drive Sync` and `Quarantine`. `action` is free `NVARCHAR(50)`, so the new
enum values need no constraint change.

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `file_index_get` | dbo | get | **New.** `@file_index_id INT`. Returns the row's `file_index_id, source, bucket_name, relative_path, file_name, file_extension, file_size_bytes, modified_at, is_active`. Returns inactive rows too: the caller maps `is_active = 0` to 410 (§6.4). Zero rows = not found. Standard `SET NOCOUNT ON; SET XACT_ABORT ON; TRY/CATCH THROW` shape. |
| `quarantine_entry_lst` | dbo | lst | **Modified (additive).** Add `,fi.file_index_id` to the SELECT list. There are no other changes, and existing consumers ignore the extra column. |

`search_result_lst` and `sync_result_lst` already select `file_index_id` /
`odrive_file_index_id` / `b2_file_index_id`, so no change is needed there.

### Migration Strategy

Hybrid model (db-author):
- Idempotent migration `Database/migrations/20261001_090000__CAS-040-file-viewer-download.sql`, using
  `CREATE OR ALTER PROCEDURE` for both SPs.
- Mirror updates: new `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_get.sql` and an
  updated `dbo.quarantine_entry_lst.sql`.
- Rollback: re-run the prior `quarantine_entry_lst` definition from the CAS-008 migration (`20260922_183000__CAS-008-quarantine-permanent-delete-api.sql`), then
  `DROP PROCEDURE IF EXISTS dbo.file_index_get`.

---

## §6 API Endpoints (Part A)

**Common to all three endpoints:**
- Route: `[Route("")]` with explicit route strings, the same style as `QuarantineController` and
  `ReportsController`.
- Constraint: `{fileIndexId:int:min(1)}`.
- Required query parameter `context ∈ {KeywordSearch, ODriveSync, Quarantine}`. It maps to
  `dbo.audit_log.tab_module` values `Keyword Search`, `O-Drive Sync` and `Quarantine`.
- Errors are RFC 7807 `ProblemDetails`, with types under
  `https://cachet.tracecounts.internal/errors/`.
- **Async:** No. These are synchronous reads, and FR-18 does not apply.

**Resolution pipeline, shared by all three endpoints** (implemented once in a new
`FileContentService`):
1. `dbo.file_index_get(fileIndexId)`. No row → V3. `is_active = 0` → V4.
2. Check `file_extension` (lower-cased, without the leading dot) against the allowlist in §8.1.
   Not in the list → V5.
3. Resolve the root with `IBucketMirrorRootRepository.GetRootPathAsync(bucket_name, source)`. The
   repository is already source-aware: `ODrive` → `odrive_root_path`, `B2` → `b2_mirror_root_path`.
   No mapping → V6.
4. Build `full = Path.GetFullPath(Path.Combine(root, relative_path))`. Then require
   `full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + '\\', OrdinalIgnoreCase)`, and require
   that the file is not a reparse point. Either failure → V7.
5. `File.Exists(full)` false → V8.
6. Open with `FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete`, so that a
   concurrent Permanent Delete or Purge is never blocked (§12).

This pipeline is exposed through a new method,
`IMirrorFileReader.OpenReadAsync(FileIndexRecord, CancellationToken) → MirrorFileHandle`. The
existing `ReadAllBytesAsync` is not changed (§4).

### Endpoint 6.1: `GET /files/{fileIndexId}?context=`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`
**Audited:** No. It returns only metadata that the calling list already shows, plus the computed
view mode, and never returns file content. §12 records the rationale.

Runs pipeline steps 1–5, plus a signature sniff (the first 8 bytes, V9) and the size-cap checks
(§9). It does **not** render a preview.

**Response 200:**
```json
{
  "fileIndexId": 100245,
  "name": "Q3-ledger.xlsx",
  "extension": "xlsx",
  "sizeBytes": 482113,
  "modifiedAt": "2026-08-14T10:22:05Z",
  "source": "ODrive",
  "viewMode": "HtmlPreview",
  "downloadOnlyReason": null,
  "previewTruncated": false
}
```
- `viewMode` is one of `InlinePdf | InlineText | ExtractedText | HtmlPreview | DownloadOnly`.
- `downloadOnlyReason` is one of `TooLarge | SignatureMismatch`, or `null`.
- No path is returned, relative or absolute. The relative path is already on screen.

| Code | Condition |
|------|-----------|
| 200 | Resolved; `viewMode` computed |
| 400 | `context` missing or invalid (V2) |
| 401 | No or invalid session |
| 404 | `file-not-found` (V3), `file-missing-from-mirror` (V8) |
| 410 | `file-no-longer-available` (V4) |
| 415 | `file-type-not-supported` (V5) |
| 500 | `mirror-mapping-missing` (V6), `file-path-rejected` (V7) |

### Endpoint 6.2: `GET /files/{fileIndexId}/view?context=`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`
**Audited:** Yes. It writes `FileViewed` (Success or Failure) **before** the first response byte
(V10).

Runs pipeline steps 1–6. It then produces the body for the file's view mode (§8.1), writes the
audit row, and returns the body.

**Response headers on 200:**

| Header | Value |
|---|---|
| `Content-Type` | `application/pdf` (InlinePdf), `text/plain; charset=utf-8` (InlineText/ExtractedText), or `text/html; charset=utf-8` (HtmlPreview) |
| `Content-Disposition` | `inline; filename="<ascii-safe>"; filename*=UTF-8''<pct-encoded>` |
| `X-Content-Type-Options` | `nosniff` |
| `Cache-Control` | `no-store, no-cache, max-age=0` |
| `Pragma` | `no-cache` |
| `Referrer-Policy` | `no-referrer` |
| `X-Frame-Options` | `SAMEORIGIN` |
| `Content-Security-Policy` | HtmlPreview: `default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'; sandbox`. Other modes: `frame-ancestors 'self'` |
| `X-Cachet-Preview-Truncated` | `true` or `false` (InlineText/ExtractedText/HtmlPreview) |
| `X-Cachet-Source-Encoding` | `utf-8`, `utf-16le`, `utf-16be` or `windows-1252` (InlineText only) |

| Code | Condition |
|------|-----------|
| 200 | Rendered and audited |
| 400 | V2 (not audited, because the tab module is unknown) |
| 401 | No or invalid session (not audited, matching every other endpoint) |
| 404 / 410 / 415 / 500 | V3–V8, audited as `FileViewed` / `Failure` |
| 422 | `preview-not-available` (V9: too large, signature mismatch, conversion failure, timeout, archive-bomb guard), audited as Failure |
| 500 | `audit-write-failed` (V10). No content is sent |

### Endpoint 6.3: `GET /files/{fileIndexId}/download?context=`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`. This is CR-001 D2 option (a); under option
(b) it becomes `CachetFullAccess`.
**Audited:** Yes. It writes `FileDownloaded` before the first response byte (V10).

Runs pipeline steps 1–6. It writes the audit row, then streams the **original bytes unchanged**.
There is no size cap, no signature check and no transcoding. The response is a
`FileStreamResult(stream, contentType) { EnableRangeProcessing = false }`.

**Response headers on 200:** the same security headers as §6.2, with these differences:
- `Content-Disposition: attachment; filename=…; filename*=…`
- `Content-Type` comes from the §8.1 matrix's download column, chosen by extension and never
  sniffed.
- `Content-Length` is the file size.
- There is no CSP and no `X-Cachet-*` header.

Status codes are the same as §6.2, except that 422 never occurs.

### §6.4 Missing, deleted and inactive files

| Situation | Detection | Result |
|---|---|---|
| Unknown ID | `file_index_get` returns 0 rows | 404 `file-not-found` |
| Permanently deleted (FR-16), purged (FR-13), or soft-deleted by the cross-check (FR-08) | `is_active = 0` | 410 `file-no-longer-available`. UI text: "This file is no longer in the archive." The three causes are not distinguished to the user; the audit log already distinguishes them. |
| Index row active but physical file absent (for example, mid-refresh) | `File.Exists` false | 404 `file-missing-from-mirror`. There is no self-healing soft-delete and no B2 fallback (§4). A Warning log is written with `fileIndexId` only. |
| Deleted **while streaming** | Handle opened with `FileShare.Delete` | The stream completes with the bytes that were opened. The audit row (Success) predates the delete, so the timeline stays consistent. |

### §6.5 Additive `fileIndexId` on list DTOs

| DTO | `FileIndexId` source | Mapping site |
|---|---|---|
| `FilenameMatchItem`, `ContentMatchItem` | `SearchResultRow.FileIndexId` (already selected) | `KeywordSearchService` |
| `NewOnODriveItem` | `SyncResultRow.OdriveFileIndexId` | `SyncScanService` |
| `RemovedFromODriveItem` | `SyncResultRow.B2FileIndexId` | `SyncScanService` |
| `ModifiedOnODriveItem` | `SyncResultRow.OdriveFileIndexId` (CR-001 D5: O-Drive is authoritative) | `SyncScanService` |
| `QuarantineListItem` | New `QuarantineEntryListRow.FileIndexId` (from the §5 SP change) | `QuarantineListService` |

Each new field is `required int FileIndexId`. It is purely additive: existing `FileId` semantics,
which are task- or entry-scoped, are unchanged, and every existing action endpoint still takes
`FileId`.

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `fileIndexId` route value must be an int ≥ 1 | 404 (route constraint mismatch); not audited | `Test_V1_NonPositiveOrNonIntegerIdIsNotRouted` |
| V2 | `context` is required and must be one of `KeywordSearch`, `ODriveSync`, `Quarantine` (case-sensitive) | 400 `validation-failed`; not audited; Warning log | `Test_V2_MissingOrUnknownContextRejected` |
| V3 | `fileIndexId` must resolve to a `dbo.file_index` row | 404 `file-not-found`; audited Failure `FileIndexNotFound` (`FilePath` null) on `/view` and `/download` | `Test_V3_UnknownFileIndexIdReturns404AndAuditsFailure` |
| V4 | The row must have `is_active = 1` | 410 `file-no-longer-available`; audited Failure `NoLongerAvailable` | `Test_V4_InactiveRowReturns410AndAuditsFailure` |
| V5 | `file_extension` must be in {pdf, txt, doc, docx, xls, xlsx} | 415 `file-type-not-supported`; audited Failure `UnsupportedType` | `Test_V5_OutOfScopeExtensionReturns415` |
| V6 | An active `dbo.odrive_bucket_mapping` row must exist for `(bucket_name, source)` | 500 `mirror-mapping-missing` (the message contains no path); audited Failure `MappingMissing` | `Test_V6_MissingBucketMappingReturns500WithoutPath` |
| V7 | The resolved full path must lie strictly beneath the resolved mirror root and must not be a reparse point | 500 `file-path-rejected`; audited Failure `PathRejected`; Warning security log (`fileIndexId` only) | `Test_V7_PathEscapingMirrorRootIsRejected` |
| V8 | The physical file must exist | 404 `file-missing-from-mirror`; audited Failure `MissingFromMirror` | `Test_V8_FileAbsentFromMirrorReturns404AndAuditsFailure` |
| V9 | `/view` only: the file must be under the per-type cap (§9) and match its signature. Checks: PDF starts with `%PDF-`; DOCX/XLSX start with `PK\x03\x04`; DOC/XLS start with `D0 CF 11 E0 A1 B1 1A E1`; TXT has no signature check. Conversion must finish within `TimeoutSeconds` and pass the OOXML archive guard (§12). | 422 `preview-not-available` with `reason` ∈ {`TooLarge`, `SignatureMismatch`, `ConversionFailed`, `Timeout`, `ArchiveGuard`}; audited Failure; `/download` is unaffected | `Test_V9_PreviewRefusedForOversizeMismatchOrFailedConversion` |
| V10 | The audit row must be written successfully before any response byte on `/view` and `/download` | 500 `audit-write-failed`; no content bytes; file handle disposed | `Test_V10_AuditWriteFailureServesNoContent` |

---

## §8 UI / UX (Part B)

### §8.1 Per-file-type viewer matrix

| Ext | View mode (default) | `/view` body | Download `Content-Type` | Download-only when |
|---|---|---|---|---|
| **pdf** | `InlinePdf`: the browser's native viewer in an iframe **without** a `sandbox` attribute, because Chromium refuses to render PDFs in sandboxed frames | Original bytes, `application/pdf` | `application/pdf` | Size > `PdfMaxBytes`, or no `%PDF-` signature |
| **txt** | `InlineText`: iframe with `sandbox=""` | Encoding detected, then re-encoded as UTF-8, capped at `TextMaxBytes` (truncated, not refused) | `text/plain` (original bytes, no transcoding) | Never. Oversize files are truncated, and the banner says so |
| **docx** | `HtmlPreview`: iframe with `sandbox=""` | Server-built HTML: paragraphs (Heading 1–6 → `h1`–`h6`; bold/italic runs → `strong`/`em`) and tables, in document order | `application/vnd.openxmlformats-officedocument.wordprocessingml.document` | Size > `OfficeMaxBytes`, signature mismatch, or conversion failure/timeout/archive guard |
| **xlsx** | `HtmlPreview` | One `<section>` per sheet (hidden sheets included, labelled "(hidden)"), a `<table>` with column letters and row numbers, cell values from `DataFormatter` using the **cached** formula result (formulas are never evaluated), capped at `MaxSheets` × `MaxRowsPerSheet` × `MaxColumns` | `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` | As for docx |
| **xls** | `HtmlPreview` (NPOI HSSF, same renderer as xlsx) | As for xlsx | `application/vnd.ms-excel` | As for docx (OLE2 signature) |
| **doc** | `ExtractedText`: iframe with `sandbox=""`, labelled "Text-only preview" | Text from the existing `WordTextExtractor` legacy path (b2xtranslator), as UTF-8 `text/plain`, capped at `TextMaxBytes` | `application/msword` | As for docx (OLE2 signature) |
| other | none: metadata returns 415 | n/a | n/a | Always unavailable (V5) |

**TXT encoding detection**, in this order:
1. BOM check: UTF-8 `EF BB BF`, UTF-16LE `FF FE`, UTF-16BE `FE FF`.
2. Strict UTF-8 decode (`throwOnInvalidBytes: true`) of the capped bytes, after trimming up to 3
   trailing bytes of an incomplete sequence at the cap boundary.
3. Fall back to Windows-1252, using `CodePagesEncodingProvider`, registered once at Api startup.

The output never contains U+FFFD from a failed strict decode. That case always falls back to
Windows-1252.

**Office preview library decision (recommended option A):**

| Option | Library / licence | Fidelity | Operational and security cost | Verdict |
|---|---|---|---|---|
| **A** | **NPOI 2.6.2** (Apache-2.0, already referenced) for DOCX/XLS/XLSX. **GHSoftware.WordDocTextExtractor 1.0.4** (b2xtranslator, already referenced) for DOC text | Structural (text and tables) | No new dependency or licensing change. Every text node is HTML-encoded by our renderer, so **no document-originated markup, script, link or image ever reaches the browser** | **Recommended** |
| B | LibreOffice headless `--convert-to pdf` (MPL-2.0) | High | Server install (~300 MB+), an external process per conversion, a concurrency and timeout wrapper, and a large parser attack surface for untrusted documents | Deferred (§15 Q1) |
| C | Aspose.Words/Cells, Syncfusion DocIO/XlsIO, GemBox (commercial) | High | Paid per-developer/deployment licences (quote required; Syncfusion's community licence has revenue eligibility limits); vendor lock-in | Not recommended without client budget |
| D | Mammoth .NET (BSD-2), DOCX → HTML | Medium (DOCX only) | Its HTML output must be sanitized; it does not cover XLS/XLSX/DOC | Not needed given A |

**Licensing to verify at implementation (not verified by this spec):**
- NPOI 2.6.2 is Apache-2.0. The implementer confirms this against the package's `LICENSE` file.
- GHSoftware.WordDocTextExtractor 1.0.4's licence is **unverified**. It is already a shipped
  dependency (CAS-012), but the implementer must record its licence in the implementation log. If
  the licence is incompatible, DOC falls back to `DownloadOnly`, with no other change to this spec.

**Unsupported and oversized files.** `DownloadOnly` shows a message in the modal body:
- `TooLarge`: "This file is too large to preview (limit N MB). Download it to view."
- `SignatureMismatch`: "This file's content does not match its .ext type, so it can't be previewed
  safely. You can still download it."

The Download button stays available.

### §8.2 Clickable file names

In each panel below, the existing `<div class="cachet-cell-name" title="@item.Name">@item.Name</div>`
becomes `<button type="button" class="cachet-cell-name cachet-file-link" title="Open @item.Name">`.
Clicking the name opens the viewer. It **does not** toggle the row checkbox, and the path span is
unchanged.

| Screen / panel | File | `fileIndexId` | `context` |
|---|---|---|---|
| Keyword Search: Filename Matches | `Pages/KeywordSearch.razor` (~L331) | `FilenameMatchItem.FileIndexId` | `KeywordSearch` |
| Keyword Search: Content Matches | `Pages/KeywordSearch.razor` (~L418) | `ContentMatchItem.FileIndexId` | `KeywordSearch` |
| Keyword Search: quarantine panel | `Pages/KeywordSearch.razor` (~L474) | `QuarantineListItem.FileIndexId` | `Quarantine` |
| O-Drive Sync: Removed from O-Drive | `Pages/ODriveSyncContentScan.razor` (~L170) | `RemovedFromODriveItem.FileIndexId` (B2 mirror) | `ODriveSync` |
| O-Drive Sync: New on O-Drive | `Pages/ODriveSyncContentScan.razor` (~L249) | `NewOnODriveItem.FileIndexId` (O-Drive mirror) | `ODriveSync` |
| O-Drive Sync: Modified files | `Pages/ODriveSyncContentScan.razor` (~L329) | `ModifiedOnODriveItem.FileIndexId` (O-Drive mirror) | `ODriveSync` |
| Quarantine Queue modal | `Shared/QuarantineQueueModal.razor` (~L61) | `QuarantineListItem.FileIndexId` | `Quarantine` |

`.cachet-file-link` styling:
- Inherits the cell-name typography, adds an underline on hover/focus, and shows a visible focus
  ring.
- Uses no background, and is keyboard-operable (Enter/Space) through native `<button>` behavior.

### §8.3 `FileViewerModal.razor` (Components/Shared)

**Parameters:** `int? FileIndexId`, `string Context`, `EventCallback OnClose`. The modal is open
while `FileIndexId` is not null.

**Visual conventions** come from `QuarantineQueueModal` / `ReportModal` (CAS-024/CAS-032):
`cachet-modal` overlay, header, body and footer classes. It is sized large (≈90vw × 85vh) and
stacks above an open Quarantine Queue modal (higher z-index).

**Flow:**
1. On open, call `GET files/{id}?context=` through the `CachetApi` client, the existing
   server-side, cookie-forwarding pattern. Show a loading state.
2. **Header:** file name (Razor-encoded), then size, modified date and a source label ("O-Drive
   mirror" or "B2 mirror").
3. **Body by `viewMode`:**
   - `InlinePdf`: `<iframe src="files/{id}/view?context=…" title="…">` without a sandbox attribute.
   - `InlineText` / `ExtractedText` / `HtmlPreview`: the same iframe with `sandbox=""`.
   - `DownloadOnly`: the reason message from §8.1. No iframe.
4. **Banners:**
   - `previewTruncated` → "Showing the first N MB / rows. Download the file for the full content."
   - `HtmlPreview` → "Read-only preview. Images, headers/footers, comments and tracked changes are
     not shown."
   - `ExtractedText` → "Text-only preview of a legacy Word (.doc) file."
5. **Footer:**
   - **Download** is an `<a class="cachet-btn" href="files/{id}/download?context=…" download>`.
     It is a plain browser navigation to the Web pass-through, so no file bytes cross the SignalR
     circuit. This deliberately differs from CAS-032's `download.js` blob pattern, because
     archive files can be large and the circuit has message-size limits.
   - **Close** closes the modal, as does Esc.
6. **Metadata errors** show a message and **no** Download button:
   - 404 `file-not-found` / `file-missing-from-mirror`: "This file could not be found in the local
     mirror."
   - 410: "This file is no longer in the archive."
   - 415: "This file type can't be opened here."
   - 500: "The file could not be opened. See the Audit Log or contact support."
   - 401: the existing session-expired banner pattern.
7. Closing the modal clears the iframe `src`, which ends any in-flight stream. Reopening issues new
   requests and therefore writes new audit rows. This is expected, and SC-9 relies on it.

### §8.4 Accessibility

- The dialog uses `role="dialog"`, `aria-modal="true"`, and `aria-labelledby` pointing at the file
  name heading.
- Focus moves to Close on open and returns to the clicked name button on close.
- The iframe has a `title`.

### §8.5 Web pass-through endpoints (Web `Program.cs`)

The endpoints are `app.MapGet("/files/{fileIndexId:int:min(1)}/view", …)` and `/download`, each
with `.RequireAuthorization("CachetAuthenticated")`. They follow the existing `/logout` minimal-API
precedent.

Behavior:
- Forward `context` unchanged to Api `files/{id}/view|download?context=…` through the `CachetApi`
  client, using `HttpCompletionOption.ResponseHeadersRead`.
- Stream the body with `CopyToAsync(HttpContext.Response.Body, RequestAborted)`. The whole file is
  never buffered.
- Forward **only** these headers: `Content-Type`, `Content-Disposition`, `Content-Length`,
  `Content-Security-Policy`, `X-Cachet-Preview-Truncated`, `X-Cachet-Source-Encoding`. **Never**
  forward `Set-Cookie`.
- Always set `X-Content-Type-Options: nosniff`, `Cache-Control: no-store, no-cache, max-age=0`,
  `Referrer-Policy: no-referrer` and `X-Frame-Options: SAMEORIGIN`, whatever Api sent.
- Non-2xx from Api:
  - `/view` returns the same status with a fixed, static HTML error page (no interpolated
    content), served with the HtmlPreview CSP.
  - `/download` returns the same status with a short `text/plain` message.
  - Api's ProblemDetails body is never forwarded.

These endpoints are GETs with no business-state change. The session cookie is `SameSite=Strict`
(CAS-002), so no cross-site request carries it. For that reason there is no antiforgery token,
unlike `/logout`'s POST.

---

## §9 Configuration Changes

Api `appsettings.json`, section `Files:Preview`, bound to a `FilePreviewOptions` class with
`ValidateDataAnnotations().ValidateOnStart()` (the same pattern as `TaskPollingOptions`). The
defaults are engineering proposals and can be tuned without a spec change.

| Key | Default | Description | Env Override |
|-----|---------|-------------|--------------|
| `Files:Preview:PdfMaxBytes` | `52428800` (50 MiB) | Above this, a PDF is `DownloadOnly` | `Files__Preview__PdfMaxBytes` |
| `Files:Preview:TextMaxBytes` | `2097152` (2 MiB) | TXT/DOC-extract preview is truncated here | `Files__Preview__TextMaxBytes` |
| `Files:Preview:OfficeMaxBytes` | `20971520` (20 MiB) | Above this, DOC/DOCX/XLS/XLSX are `DownloadOnly` | `Files__Preview__OfficeMaxBytes` |
| `Files:Preview:MaxSheets` | `20` | Spreadsheet sheet cap (truncation banner) | `Files__Preview__MaxSheets` |
| `Files:Preview:MaxRowsPerSheet` | `1000` | Row cap per sheet | `Files__Preview__MaxRowsPerSheet` |
| `Files:Preview:MaxColumns` | `50` | Column cap per sheet | `Files__Preview__MaxColumns` |
| `Files:Preview:MaxHtmlBytes` | `5242880` (5 MiB) | Rendered HTML cap for DOCX (truncation banner) | `Files__Preview__MaxHtmlBytes` |
| `Files:Preview:MaxUncompressedBytes` | `209715200` (200 MiB) | OOXML archive guard: total uncompressed size | `Files__Preview__MaxUncompressedBytes` |
| `Files:Preview:MaxCompressionRatio` | `100` | OOXML archive guard: per-entry uncompressed/compressed ratio | `Files__Preview__MaxCompressionRatio` |
| `Files:Preview:TimeoutSeconds` | `30` | Conversion timeout (V9 `Timeout`) | `Files__Preview__TimeoutSeconds` |
| `Files:Preview:MaxConcurrentConversions` | `2` | `SemaphoreSlim` bound on concurrent conversions (3–4 users, BRD §8) | `Files__Preview__MaxConcurrentConversions` |

No secret and no B2 key setting is involved. There are no Web config changes; Web reuses
`Api:BaseUrl`.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `GET /files/{id}` returns the correct `viewMode` for each of the six extensions under cap: pdf→`InlinePdf`, txt→`InlineText`, doc→`ExtractedText`, docx/xls/xlsx→`HtmlPreview`. Over cap → `DownloadOnly`/`TooLarge`. Signature mismatch → `DownloadOnly`/`SignatureMismatch`. The response contains no path | `Test_SC1_MetadataReturnsViewModePerFileType` |
| SC-2 | PDF `/view` returns the original bytes with `application/pdf` and `Content-Disposition: inline` | `Test_SC2_PdfViewStreamsInlineOriginalBytes` |
| SC-3 | TXT `/view` returns correct UTF-8 text with `X-Cachet-Source-Encoding` set, for UTF-8 with BOM, UTF-8 without BOM, UTF-16LE BOM, UTF-16BE BOM and Windows-1252 inputs | `Test_SC3_TextViewDetectsEncodingAndReturnsUtf8` |
| SC-4 | TXT above `TextMaxBytes` returns exactly the capped prefix (no split multi-byte character) with `X-Cachet-Preview-Truncated: true` | `Test_SC4_TextViewTruncatesAtCapOnCharacterBoundary` |
| SC-5 | DOCX `/view` returns `text/html` containing the document's paragraph and table text in order. A run containing `<script>alert(1)</script>` appears only HTML-encoded, and the output contains no `<script`, `<img`, `<a `, `href=` or `src=` | `Test_SC5_DocxPreviewIsEncodedStructuralHtml` |
| SC-6 | XLSX and XLS `/view` render every sheet (hidden sheets labelled) with cached formula values, not formula text, and truncate at the row/column/sheet caps with a banner | `Test_SC6_SpreadsheetPreviewRendersCachedValuesWithCaps` |
| SC-7 | DOC `/view` returns the `WordTextExtractor` text for `TestAssets/legacy-word-sample.doc` as `text/plain; charset=utf-8` | `Test_SC7_LegacyDocViewReturnsExtractedText` |
| SC-8 | `/download` returns byte-identical original content for each of the six types, with `attachment` disposition, the §8.1 download `Content-Type`, `Content-Length`, and no `Accept-Ranges` | `Test_SC8_DownloadReturnsUnalteredOriginalPerType` |
| SC-9 | Each successful `/view` and `/download` writes exactly one audit row with the correct user, `tab_module` mapped from `context`, `FileViewed`/`FileDownloaded`, `file_path = relative_path` (never absolute), and `Success`. The audit call completes before the response body is written (verified by call ordering on a substitute) | `Test_SC9_ViewAndDownloadAuditBeforeResponse` |
| SC-10 | Every 200 response on `/view` and `/download` carries `nosniff`, `no-store`, `Referrer-Policy: no-referrer` and `X-Frame-Options: SAMEORIGIN`. HtmlPreview additionally carries the exact §6.2 CSP including `sandbox` | `Test_SC10_SecurityHeadersPresentOnEveryFileResponse` |
| SC-11 | The file is resolved from the correct mirror root by `source` (ODrive → `odrive_root_path`, B2 → `b2_mirror_root_path`). A `relative_path` of `..\..\Windows\win.ini` or an absolute path is rejected (V7) without touching the file | `Test_SC11_ResolvesBySourceAndRejectsTraversal` |
| SC-12 | Neither `FilesController`, `FileContentService`, nor any §8.5 Web endpoint takes an `IB2Client`, `IUserScopedB2DeleteClient` or `ICredentialProtector` dependency, and no response body or header contains a B2 host, `Authorization` token or key ID (reflection and response scan) | `Test_SC12_NoB2DependencyOrCredentialOnFilePath` |
| SC-13 | Clicking a file name in each of the seven §8.2 panels opens `FileViewerModal` with that row's `FileIndexId` and the correct `context`, and does not change checkbox selection | `Test_SC13_FileNameClickOpensViewerWithoutTogglingSelection` |
| SC-14 | The modal renders an iframe to `files/{id}/view?context=…` for each non-DownloadOnly mode, with `sandbox=""` for text/HTML modes and no sandbox for `InlinePdf`, plus the correct banners (truncated, preview-fidelity, text-only) | `Test_SC14_ModalRendersViewerPerViewMode` |
| SC-15 | For `DownloadOnly`, the modal shows the reason message and a Download link, with no iframe. For 404/410/415/500 metadata errors it shows the §8.3 message and **no** Download link | `Test_SC15_ModalDownloadOnlyAndErrorStates` |
| SC-16 | The Download link targets `files/{id}/download?context=…` with the `download` attribute, and no `IJSRuntime` download/stream call is made | `Test_SC16_DownloadIsPlainNavigationNotCircuitStream` |
| SC-17 | The Web pass-through streams Api's body through, forwards only the allowlisted headers (never `Set-Cookie`), always sets the four security headers, and replaces an Api non-2xx body with the fixed static page or message | `Test_SC17_WebPassThroughStreamsAndFiltersHeaders` |
| SC-18 | An unauthenticated request to the Web `/files/{id}/view` or `/download` never calls Api (the fake handler records zero calls) and is redirected to `/login` | `Test_SC18_WebPassThroughRequiresSession` |

---

## §11 Telemetry

```csharp
_logger.LogInformation(
    "File {Operation} served for {FileIndexId} ({Extension}, {SizeBytes} bytes, {ViewMode}) in {ElapsedMs} ms",
    operation, fileIndexId, extension, sizeBytes, viewMode, elapsedMs);
_logger.LogWarning("File {Operation} refused for {FileIndexId}: {Reason}", operation, fileIndexId, reason);
_logger.LogWarning("Mirror path containment check failed for {FileIndexId}", fileIndexId); // V7 security event
_logger.LogInformation("File stream aborted by client for {FileIndexId} after {BytesSent} bytes", fileIndexId, bytesSent);
```

- **Never log** any of the following: the absolute mirror path or mirror root, file content or any
  preview text, the B2 key or key ID, or a session cookie value. `relative_path` goes to the audit
  row only (FR-23), never to application logs.

---

## §12 Security Notes

### Threat model and controls

| Threat | Control |
|---|---|
| Path traversal or arbitrary file read | The client supplies only an int `fileIndexId`. The path is built from DB values only: the root from `odrive_bucket_mapping`, plus `relative_path`. It then goes through `GetFullPath`, a strict prefix containment check against the root, and a reparse-point rejection (V7). This is defense in depth, because DB values are written by trusted processes but come from external archive names. |
| Stored XSS through a malicious document | Office previews are **generated** HTML. Every string is `HtmlEncoder`-encoded, and no attribute, URL, style, image or script is taken from the document. The response CSP is `default-src 'none'` + `sandbox`, and the iframe has `sandbox=""`. TXT/DOC are `text/plain` + `nosniff`. |
| MIME confusion (a renamed HTML file served as PDF) | `Content-Type` comes from the extension allowlist, never from sniffing. The signature must match for inline view (V9). `nosniff` is set on every response. |
| Parser DoS (zip bomb, huge sheet, slow parse) | Size caps; an OOXML pre-scan of the ZIP central directory with `System.IO.Compression` (total uncompressed size and per-entry ratio caps) **before** NPOI opens the file; row/column/sheet/HTML caps; a timeout; and a concurrency semaphore (§9). NPOI work cannot be cancelled mid-parse. After a timeout the request returns 422 while the background parse runs to completion, bounded by the semaphore. |
| Formula or macro execution | Formulas are never evaluated (only cached values are read). Macros are never loaded (NPOI does not execute VBA). Hyperlinks are rendered as text only. |
| Clickjacking / framing by other origins | `X-Frame-Options: SAMEORIGIN` + `frame-ancestors 'self'` |
| Caching on shared machines or proxies | `Cache-Control: no-store` + `Pragma: no-cache`; no ETag or Last-Modified |
| CSRF | GET-only with no business-state change; `SameSite=Strict` session cookie (CAS-002). The audit row is a log, not a state change of archive data. |
| Blocking irreversible workflows | Handles are opened with `FileShare.Read \| FileShare.Delete`, so Permanent Delete (FR-16) and Purge (FR-13) are never blocked by an open view. A concurrent **SyncUpdate** write to the same local file can fail while a view is streaming. It then records the existing `SyncActionFailedLocal` result and is retryable. This visible, audited failure was chosen over a silently torn read (§15 Q3). |
| PDF-embedded script | This is handled by the browser's native viewer (PDFium or pdf.js), whose scripting sandbox is outside the application's control. Accepted residual risk, disclosed in CR-001 risk 3. |
| Enumerating IDs to view files never surfaced in results | Every signed-in user can already search the whole shared archive (BRD §5, a single shared cache), and every view/download is audited, including failures. Accepted, pending CR-001 D2. Tightening (for example, requiring the ID to appear in a result, quarantine or sync row) is §15 Q2. |

### B2 Credential Handling

This path never uses B2:
- No `IB2Client`, `IUserScopedB2DeleteClient` or `ICredentialProtector` dependency (SC-12).
- No `b2_download_file_*` call and no `b2_get_download_authorization` call.
- No presigned or tokenized URL is ever generated or sent to the browser.

"B2" in this spec means only the **local B2 mirror directory** (`b2_mirror_root_path`). FR-03 is
satisfied because no credential is ever in scope.

### Audit Trail

`/view` and `/download` call `IAuditLogService.WriteAsync` and **await it** after resolution and
rendering, but **before** returning the `IActionResult`, so before any header or byte is written
(V10, SC-9). The row contains:
- `CreatedBy`/`UpdatedBy = "api-svc"`;
- `UserId` from the `NameIdentifier` claim;
- `TabModule` mapped from `context`;
- `Action = FileViewed | FileDownloaded`;
- `FilePath = relative_path` (the same convention as CAS-008's `entry.Path`);
- `TaskQueueId = null`;
- `Result = Success | Failure`;
- `ResultDetail`, a compact key/value string, for example
  `fileIndexId=100245; source=ODrive; viewMode=HtmlPreview; bytes=482113` on success, or
  `reason=MissingFromMirror` on failure. It never contains content or an absolute path.

Failures V3–V9 are audited as `Failure` so that attempts are traceable. V1, V2 and 401 are not
audited, because the tab module or the user is unknown, and this matches existing endpoints.

**Known limitation:** `Success` means "authorized, resolved and began streaming". A client that
aborts mid-download still has a `Success` row. The abort is logged (§11) but not re-audited,
because the audit log is append-only (CAS-003) and has no update path.

The metadata endpoint (§6.1) is **not** audited. It returns no file content, only metadata already
displayed in the originating list. Auditing it would double the rows per open without adding
traceability. This is flagged for owner confirmation in §15 Q5.

### Session / Auth Requirements

- `[Authorize]` is applied at **action level** on all three Api endpoints (P7). Both Web endpoints
  use `.RequireAuthorization("CachetAuthenticated")`.
- The download policy follows CR-001 D2.
- There is no dependency on an open BRD item: the login method is resolved (§12.1 item 8), and
  drive encryption is resolved (§12.3 item 6).

---

## §13 Test Plan

**Fixtures:** generated at test time, not committed as binaries:
- PDF via PdfSharp;
- DOCX via NPOI XWPF (including a `<script>` run and a table);
- XLSX/XLS via NPOI XSSF/HSSF (including a formula with a cached value, a hidden sheet, and
  over-cap rows);
- TXT as byte arrays in each encoding;
- an OOXML zip-bomb archive built with `System.IO.Compression`;
- a renamed-HTML ".pdf".

The only committed binary is the existing `TestAssets/legacy-word-sample.doc`. Each test uses its
own temp mirror root, deleted on dispose.

### Unit Tests: `Cachet.ArchiveSync.Api.Tests`

| Class | Method | Proves |
|-------|--------|--------|
| `FileContentServiceTests` | `Test_SC1_MetadataReturnsViewModePerFileType` | SC-1 |
| `FileContentServiceTests` | `Test_V3_UnknownFileIndexIdReturns404AndAuditsFailure`, `Test_V4_InactiveRowReturns410AndAuditsFailure`, `Test_V5_OutOfScopeExtensionReturns415`, `Test_V6_MissingBucketMappingReturns500WithoutPath`, `Test_V8_FileAbsentFromMirrorReturns404AndAuditsFailure` | V3–V6, V8 outcomes and failure audit rows |
| `FileContentServiceTests` | `Test_V10_AuditWriteFailureServesNoContent`, `Test_SC9_ViewAndDownloadAuditBeforeResponse` | V10 and SC-9 ordering (`Received.InOrder`) |
| `MirrorFileReaderOpenReadTests` | `Test_SC11_ResolvesBySourceAndRejectsTraversal`, `Test_V7_PathEscapingMirrorRootIsRejected` | SC-11, V7 (`..`, absolute path, sibling-prefix root such as `D:\mirror` vs `D:\mirror2`) |
| `TextPreviewDecoderTests` | `Test_SC3_TextViewDetectsEncodingAndReturnsUtf8`, `Test_SC4_TextViewTruncatesAtCapOnCharacterBoundary` | SC-3, SC-4 |
| `OfficePreviewRendererTests` | `Test_SC5_DocxPreviewIsEncodedStructuralHtml`, `Test_SC6_SpreadsheetPreviewRendersCachedValuesWithCaps`, `Test_SC7_LegacyDocViewReturnsExtractedText` | SC-5–SC-7 |
| `OfficePreviewRendererTests` | `Test_V9_PreviewRefusedForOversizeMismatchOrFailedConversion` | V9 (each `reason`, including `ArchiveGuard` and `Timeout` using a fake slow renderer) |
| `FileSecurityArchitectureTests` | `Test_SC12_NoB2DependencyOrCredentialOnFilePath` | SC-12 (constructor reflection) |

### Integration Tests: `Cachet.ArchiveSync.Api.IntegrationTests` (WebApplicationFactory, substituted repositories, temp-dir mirror)

| Class | Method | Proves |
|-------|--------|--------|
| `FilesControllerIntegrationTests` | `Test_V1_NonPositiveOrNonIntegerIdIsNotRouted`, `Test_V2_MissingOrUnknownContextRejected` | V1, V2 |
| `FilesControllerIntegrationTests` | `Test_SC2_PdfViewStreamsInlineOriginalBytes`, `Test_SC8_DownloadReturnsUnalteredOriginalPerType` | SC-2, SC-8 (theory over six types) |
| `FilesControllerIntegrationTests` | `Test_SC10_SecurityHeadersPresentOnEveryFileResponse` | SC-10 (theory over view modes and download) |
| `FileIndexRepositoryTests` (existing, Skip-marked real-DB) | `Test_FileIndexGet_ReturnsActiveAndInactiveRows` | `dbo.file_index_get` contract (§5) |
| `QuarantineEntryRepositoryTests` (Skip-marked real-DB) | `Test_QuarantineList_IncludesFileIndexId` | §5 SP change and §6.5 mapping |

### Web Tests: `Cachet.ArchiveSync.Web.Tests`

| Class | Method | Proves |
|-------|--------|--------|
| `FileViewer/FileNameClickTests` (bUnit, one theory case per §8.2 panel) | `Test_SC13_FileNameClickOpensViewerWithoutTogglingSelection` | SC-13 |
| `FileViewer/FileViewerModalTests` (bUnit) | `Test_SC14_ModalRendersViewerPerViewMode`, `Test_SC15_ModalDownloadOnlyAndErrorStates`, `Test_SC16_DownloadIsPlainNavigationNotCircuitStream` | SC-14–SC-16 |
| `FileViewer/FilePassThroughIntegrationTests` (WebApplicationFactory + existing `FakeCachetApiHandler` pattern) | `Test_SC17_WebPassThroughStreamsAndFiltersHeaders`, `Test_SC18_WebPassThroughRequiresSession` | SC-17, SC-18 |

Existing `KeywordSearchPageTests`, `ODriveSyncPageTests` and `QuarantineQueueModalTests` must still
pass unchanged. The markup change swaps a `div` for a `button` with the same class, so any selector
relying on `div.cachet-cell-name` must be updated in the same PR, not weakened.

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. **Precondition:** CR-001 signed, BRD v2.4 published, and this spec moved to `ready` (§0).
2. **Part A:** deploy the migration (`dbo.file_index_get`, `dbo.quarantine_entry_lst`), then Api.
   The DTO fields are additive, so the currently deployed Web keeps working.
3. **Part B:** deploy Web. It depends on Part A being live, because it reads the new `fileIndexId`
   and calls `/files/*`.
4. **Smoke test** on the server, one file of each type from each mirror. Confirm one audit row per
   view and download in the Audit Log screen, and confirm the response headers in browser dev tools.
5. **Rollback:** redeploy the previous Web first; the names stop being clickable. Then redeploy the
   previous Api; the `/files/*` routes disappear. The SP changes are additive and may be left in
   place, or reverted per §5.

Audit rows already written stay. The audit log is append-only.

---

## §15 Open Questions

- [ ] **Q0 (deployment gate):** Client sign-off on CR-001 (D1–D5). Build is authorized at risk
      (§0), but deployment is blocked until this is resolved.
- [ ] **Q1:** Is option A's structural preview fidelity acceptable, or does the client want option
      B (LibreOffice) or C (commercial)? This follows CR-001 D4. Choosing B or C changes §8.1,
      §12 and the effort estimate.
- [ ] **Q2:** Should view and download be limited to IDs currently present in a search result,
      sync result or active quarantine row, to prevent ID enumeration? The recommendation is no
      for this phase (§12).
- [ ] **Q3:** Confirm the `FileShare.Read | FileShare.Delete` trade-off. A concurrent SyncUpdate on
      a file being viewed fails locally and can be retried, rather than the view serving a torn
      read.
- [ ] **Q4:** Follow-up spec to add the V7 containment check to the existing
      `MirrorFileReader.ReadAllBytesAsync` and `ODriveFileReader` (out of scope here per P3).
- [ ] **Q5:** Confirm that the metadata endpoint (§6.1) stays unaudited.
- [ ] **Q6:** GHSoftware.WordDocTextExtractor licence is to be verified (§8.1). If it is
      incompatible, DOC becomes `DownloadOnly`.
- This spec does **not** depend on BRD §12.4's open items (web caching; O-Drive refresh cadence).

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code (for R. Devarajan) | 2026-09-30 | Drafted |
| Build authorization (at risk) | ratheesh@scientificvision.net | 2026-09-30 | Implementation before client sign-off, with CR-001 recommended defaults; deployment held until CR-001 §6 is signed (§0) |
| Reviewer | | | |

---

## §17 Graph Impact

### God Nodes in Blast Radius
- `KeywordSearchPageTests` (101 edges) and `ODriveSyncPageTests` (86 edges). Both page test suites
  are affected by the §8.2 markup change (`div` → `button` on `.cachet-cell-name`).
- No production god node is modified. `string`/`int` are type nodes and are not relevant.

### Communities / Modules Affected
- **Keyword Search, O-Drive Sync, Quarantine:** additive `fileIndexId` on DTOs, and clickable names
  in three screens.
- **Audit Log:** two appended `AuditAction` members, through the existing CAS-005 write path.
- **Projects:**
  - **Core:** DTOs, `AuditAction`, `IMirrorFileReader`, new `FileIndexRecord` and `MirrorFileHandle`.
  - **Api:** `FilesController`, `FileContentService`, preview renderers, `FilePreviewOptions`,
    `MirrorFileReader.OpenReadAsync`, `FileIndexRepository.GetAsync`, three mapping services.
  - **Web:** `FileViewerModal`, three razor files, pass-through endpoints in `Program.cs`.
  - **Worker:** not touched.
- **Database:** one new SP and one altered SP.

### Surprising Connections
- **Result IDs are not file identities.** The list DTOs' `FileId` is task-scoped
  (`search_result_id` / `sync_result_id` / `quarantine_entry_id`), not `file_index_id`. This is
  why §6.5's additive field is needed. Keying the viewer on `FileId` would open the wrong file.
- **The browser never talks to Api** (CAS-002 §6.6). This forces the §8.5 Web pass-through, and
  rules out both an Api-hosted iframe URL and CAS-032's blob-over-SignalR download pattern for
  large files.
- **Two mirror-root resolvers already exist.** `IBucketMirrorRootRepository` (source-aware, CAS-012)
  and `IBucketRootMappingRepository` (O-Drive-only, CAS-013). This spec reuses the source-aware
  one, because a file can come from either mirror.
- **No containment check today.** `MirrorFileReader.ReadAllBytesAsync` uses `Path.Combine` with no
  containment check (§15 Q4).

### Graphify Queries Run
```
py -3 -m graphify query "file content streaming from O-Drive or B2 mirror directory by file index id, path mapping, audit log write" --budget 2000
  → CAS-011 migration (odrive_bucket_mapping) and scripts/index_refresh/refresh_odrive_index.py (CAS-031); 7 nodes
graphify-out/GRAPH_REPORT.md → god nodes and surprising connections reviewed
```
The graph returned sparse results for this domain, which is new scope with no existing file-view
node. The analysis was completed by manually reading `IMirrorFileReader`/`MirrorFileReader`,
`IBucketMirrorRootRepository`, `IFileIndexRepository`, `IAuditLogService`/`AuditAction`, the six
list DTOs, `dbo.file_index`/`odrive_bucket_mapping`/`search_result`/`sync_result`/`quarantine_entry`/`audit_log`,
the three list SPs, Api and Web `Program.cs`, `CookieForwardingHandler`, `ReportsController`,
`download.js`, the `Content/*TextExtractor` classes, and the three razor screens.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] §0 gate honored: the at-risk build authorization is recorded, and nothing deploys before
      CR-001 is signed
- [ ] All §10 Success Criteria have a passing test
- [ ] All §7 Validations are implemented and tested
- [ ] All §6 API endpoints exist with correct verb, route, constraint and action-level `[Authorize]`
- [ ] §8.5 Web endpoints exist, require a session, and filter headers
- [ ] §12 implemented:
  - no B2 dependency on the file path;
  - audit awaited before the result;
  - containment check present;
  - CSP, sandbox and nosniff present;
  - no document-originated markup in previews;
  - `FileShare.Delete` used
- [ ] §5 SPs exist in the migration and in the mirror files
- [ ] §9 `Files:Preview` keys are present in `appsettings.json` with `ValidateOnStart`
- [ ] §11 logs contain no absolute paths, content or credentials
- [ ] §17 matches the actual changes (Worker untouched)
- [ ] No §4 out-of-scope changes, in particular:
  - `ReadAllBytesAsync` unchanged;
  - no B2 fallback;
  - no Range support;
  - no soft-delete or undo affordance

### Verification Report Format
The reviewer writes `CAS-040.review.md` with:
- Overall verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Security gap list (if any)
- Scope violations (if any)

## Implementation Log

**Completed:** 2026-09-30
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 84.5% (warn) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20261001_090000__CAS-040-file-viewer-download.sql` | New: dbo.file_index_get; quarantine_entry_lst + fi.file_index_id |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.file_index_get.sql` | New SP mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_lst.sql` | Mirror updated (additive column) |
| `Core/Dtos/{FilenameMatchItem,ContentMatchItem,NewOnODriveItem,RemovedFromODriveItem,ModifiedOnODriveItem,QuarantineListItem}.cs` | + required FileIndexId (§6.5) |
| `Core/Dtos/QuarantineEntryListRow.cs` | + FileIndexId |
| `Core/Dtos/{FileIndexRecord,MirrorFileHandle,FileMetadataResponse}.cs` | New DTOs |
| `Core/Enums/AuditAction.cs` | Appended FileViewed, FileDownloaded |
| `Core/Interfaces/{IMirrorFileReader,IFileIndexRepository}.cs` | + OpenReadAsync, + GetAsync |
| `Core/Interfaces/IFileContentService.cs` | New service contract + outcomes |
| `Api/Controllers/FilesController.cs` | New: GET files/{id}, /view, /download |
| `Api/Services/FileContentService.cs` | New: resolution pipeline, V2-V10, audit-before-response |
| `Api/Content/{FilePreviewOptions,TextPreviewDecoder,OoxmlArchiveGuard,OfficePreviewRenderer,PreviewConversionLimiter}.cs` | New preview pipeline |
| `Api/Storage/MirrorFileReader.cs` | + OpenReadAsync with containment/reparse checks |
| `Api/Repositories/{FileIndexRepository,QuarantineEntryRepository}.cs` | GetAsync; map file_index_id |
| `Api/Services/{KeywordSearchService,SyncScanService,QuarantineListService}.cs` | Map FileIndexId |
| `Api/Program.cs, Api/appsettings.json` | DI, options, CodePages; Files:Preview defaults |
| `Web/Components/Shared/FileViewerModal.razor (+.css)` | New viewer modal |
| `Web/Components/Pages/{KeywordSearch,ODriveSyncContentScan}.razor, Shared/QuarantineQueueModal.razor, wwwroot/app.css` | Clickable names in 7 panels |
| `Web/Program.cs` | Pass-through /files/{id}/view|download |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `FileContentServiceTests` | `Test_SC1, Test_SC9, Test_V3-V6, Test_V8, Test_V10` | SC-1, SC-9, V3-V6, V8, V10 |
| `MirrorFileReaderOpenReadTests` | `Test_SC11, Test_V7` | SC-11, V7 |
| `TextPreviewDecoderTests` | `Test_SC3, Test_SC4` | SC-3, SC-4 |
| `OfficePreviewRendererTests` | `Test_SC5-SC7, Test_V9` | SC-5-SC-7, V9 |
| `FileSecurityArchitectureTests` | `Test_SC12` | SC-12 |
| `FilesControllerIntegrationTests` | `Test_V1-V10, Test_SC2, Test_SC8, Test_SC10, Test_SC12` | V1-V10, SC-2, SC-8, SC-10, SC-12 |
| `FileNameClickTests` | `Test_SC13` | SC-13 |
| `FileViewerModalTests` | `Test_SC14-SC16` | SC-14-SC-16 |
| `FilePassThroughIntegrationTests` | `Test_SC17, Test_SC18, Test_SC12` | SC-17, SC-18, SC-12 |
| `FileIndexRepositoryTests / QuarantineEntryRepositoryTests` | `Test_FileIndexGet_ReturnsActiveAndInactiveRows, Test_QuarantineList_IncludesFileIndexId` | §5 SPs (Skip-marked real-DB; passed on LocalDB) |

### Migration
`20261001_090000__CAS-040-file-viewer-download.sql`

### Gate Notes (2026-09-30)
- **Tests:** "pass" means zero CAS-040 regressions. The full run had these **pre-existing, unrelated** failures:
  - Api.IntegrationTests `FileIndexRepositoryTests` ×4 (Test_SC3/SC4/SC5/A2_SC9). They reference the migration `20260930_150000__CAS-038-A2-date-only-search.sql`, which is not in the repo.
  - Web.Tests `ODriveSyncPageTests.Test_SC10_ODriveSync_ReloadsScanAndResumesPendingSyncActions` and `Test_V9_PollReceives401_StopsPollingAndShowsSessionExpired(actionPoll)`. The cause is a mismatch between the page's `GET /tasks?type=Sync*` calls and the test's `StubTasks`. They still fail with the CAS-040 markup reverted, and ODriveSyncPageTests failures were already noted in the CAS-037 review.
  - `ODriveSyncPageTests.Test_ActionPollContinuesLoopingWhileSomeFileIdsRemainUnmatched` flaked once under full-suite load and passed 2 of 2 runs in isolation.
- **Format:** whitespace auto-fixed in 4 new CAS-040 files (FileContentService, MirrorFileReaderOpenReadTests, OfficePreviewRendererTests, FileNameClickTests).
- **Coverage warning:**
  - `PreviewConversionLimiter` is at 75.5%; the uncovered lines are defensive shutdown and cancellation paths.
  - `FileIndexRepository` is at 0% in the automated run. By project convention it is covered by the Skip-marked real-DB tests, which passed on LocalDB.
- **Implementer items for review:**
  - (1) The cap numbers ("N MB") in `FileViewerModal` messages are hard-coded to the §9 defaults, because the §6.1 metadata doesn't carry the limits.
  - (2) Metadata `previewTruncated` is exact only for TXT. For Office and DOC files, truncation is signalled by the banner inside the preview and the `X-Cachet-Preview-Truncated` header.
  - (3) The Web pass-through returns 502 when Api is unreachable; the spec doesn't cover this case.
  - (4) The reparse-point check also covers directories between the root and the file, which is stricter than §6.
  - (5) Failure `ResultDetail` is `fileIndexId=N; reason=X`.
  - (6) The GHSoftware.WordDocTextExtractor licence is BSD-3-Clause per its nuspec, which resolves §15 Q6.
- **Minor (orchestrator P7 read), corrected by review:** I originally noted that `Uri.EscapeDataString` leaves `'()*` unescaped. That was wrong: since .NET 4.5 it escapes them. The CAS-040 review confirmed `filename*` conforms to RFC 5987.
- **Deployment remains BLOCKED until CR-001 is signed (§0).**
