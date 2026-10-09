---
id: CAS-010
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-22
priority: P1
related_specs: [CAS-003, CAS-005, CAS-006, CAS-008, CAS-009]
touches:
  database: true
  api: true
  ui: false
  worker: false
  b2: false
  config: true
  security: true
migrations:
  - 20260922_200000__CAS-010-reporting-api.sql
---

# CAS-010 — Reporting API (Pre-Action Impact Report / Post-Action Outcome Report)

WBS Task 20 (Reporting API). Implements FR-25 (Pre-Action Impact Report), FR-26 (Post-Action
Outcome Report), FR-27 (both saved locally as PDF and XLSX, no email). Architecture basis:
`0.Specifications/CACHET_API_Design_Specification_v1.md` §9 (Reporting Module), BRD §5
(Assumptions — ClosedXML/PdfSharp carry forward), BRD §7.7/§9 (defensible-record rationale),
CAS-006 (`dbo.search_result`), CAS-008 (`dbo.quarantine_entry`), and CAS-009
(`dbo.audit_log_lst` / `IAuditLogRepository.ListAsync`, reused by the Post-Action report).

---

## §0 Provenance Note (read this first)

**§0.1 — Synchronous, not async-task-queue (CONFIRMED 2026-09-22, R. Devarajan, TraceCounts).**
Two documents in this repo describe conflicting shapes for this module:

- The original WBS Task 20 prompt (and this project's `.claude/skills/reporting-pdf-xlsx/SKILL.md`,
  which still carries the WBS prompt's text verbatim) describe report generation as an async
  task-queue job: `POST /reports/pre-action` submits a task, `GET /reports/{taskId}` polls for a
  download link once complete.
- `CACHET_API_Design_Specification_v1.md` §9 **explicitly reverses that assumption**, confirmed
  2026-09-09 (R. Devarajan, TraceCounts): the UI/UX wireframe shows the report modal opening
  immediately with populated data and a real generation timestamp — no queued/processing state
  anywhere in the reviewed design. Both `POST` endpoints are synchronous and return the full
  report payload (including download URLs) in their `200 OK` response.

This spec follows the API design spec (§9) — reconfirmed directly with the client during drafting.
**Follow-up (not part of this spec's diff, tracked in §15):** `reporting-pdf-xlsx/SKILL.md` should
be updated to match, so a future spec isn't misled by the stale async description.

**§0.2 — No prior code exists for this module.** `graphify query` returns only doc nodes (BRD/API
design spec/WBS) for "Reporting" — no `Cachet.ArchiveSync.*` C# nodes. This is the first spec to
implement Reporting.

**§0.3 — Reuse boundaries with existing specs (Karpathy P4 — no duplicate logic):**
- Quarantine panel data reuses `IQuarantineEntryRepository.ListActiveAsync` (CAS-008) unchanged —
  no new quarantine query.
- Post-Action report rows reuse `IAuditLogRepository.ListAsync` / `dbo.audit_log_lst` (CAS-009)
  unchanged — no second, parallel action-history record (API design doc §9.2's explicit
  requirement, "this keeps the two views from ever disagreeing about what happened").
- Filename/Content Match panel data has **no existing "all tasks" query** — `ISearchResultRepository`
  (CAS-006) is scoped per `taskQueueId` only. This spec adds one new repository method and stored
  procedure for that gap (§5, §3 bullet 4).

**§0.4 — Hard dependency on CAS-009 (RESOLVED 2026-09-22, R. Devarajan: sequence, don't split).**
CAS-009 (`GET /audit-log`, `dbo.audit_log_lst`, `IAuditLogRepository.ListAsync`) is
`status: in-progress` at the time this spec is drafted. This spec's Post-Action report **cannot be
implemented** until CAS-009 reaches `merged`/implemented — this is a build-order gate on
`/implement CAS-010`, not a design fork. CAS-010 stays a single spec (splitting Pre-/Post-Action
into two specs just to decouple this sequencing constraint would add review overhead for a
dependency already on track to resolve on its own). **`/implement CAS-010` must not run until
CAS-009's status is `merged` (or otherwise fully implemented).**

---

## §1 Summary

Implements FR-25/FR-26/FR-27: two synchronous report-generation endpoints and two file-retrieval
endpoints. `POST /reports/pre-action` generates the Pre-Action Impact Report — every file currently
flagged, needing review, or informational, aggregated across Keyword Search match scope
(`dbo.search_result`) and Quarantine scope (`dbo.quarantine_entry`, `Active` rows only) — prior to
any deletion decision. `POST /reports/post-action` generates the Post-Action Outcome Report —
every state-changing action actually taken (searches submitted, files quarantined/deleted, O-Drive
Sync copy/update/purge actions), sourced from the same underlying audit-log query as `GET
/audit-log` so the two views never disagree. Both reports are rendered as PDF (PdfSharp) and XLSX
(ClosedXML) — carrying forward the existing desktop-client generation approach per BRD §5's
assumption — saved to local disk only; `GET /reports/{reportId}/pdf` and `GET
/reports/{reportId}/xlsx` are the only retrieval path (FR-27: no email is ever sent). Report
generation is itself an audited action (§12).

---

## §2 User Story

As a **TraceCounts reviewer**, I want to generate a Pre-Action Impact Report before I delete
anything, so I can see everything flagged, needing review, or already quarantined in one place;
and I want a Post-Action Outcome Report after I've taken action, so I have a defensible,
timestamped record of exactly what was searched, quarantined, deleted, or synced — both downloadable
as PDF and XLSX with no email step to forget or misconfigure.

---

## §3 In-Scope

- `POST /reports/pre-action` **[audited]** — synchronous. Aggregates, in one response:
  - Every `dbo.search_result` row across **every** `task_queue_id` (not scoped to one task), joined
    to `dbo.file_index` and filtered to `file_index.is_active = 1` (excludes files already
    Permanently Deleted — CAS-008 soft-delete). `match_type` maps to `panel`: `'Filename'` →
    `"Filename Matches"`, `'Content'` → `"Content Matches"`. `match_status` maps to `status`
    verbatim (`Flagged` | `Needs Review` | `Informational`).
  - Every `Active` `dbo.quarantine_entry` row (via `IQuarantineEntryRepository.ListActiveAsync`,
    CAS-008 — unchanged), mapped to `panel = "Quarantine"`, `status = match_status_snapshot`.
  - **Dedup decision (RESOLVED 2026-09-22, R. Devarajan):** a file that is both an active search
    match and actively quarantined appears as **two separate rows**, one per panel (no dedup). FR-25
    reads "across search and quarantine scope" as a union over two scopes, not a single collapsed
    per-file status, and the two rows carry different facts (match snippet/type vs. quarantine
    intake date) that a reviewer preparing a deletion decision needs to see. If this proves visually
    cluttered in the rendered report, that is a report-layout concern for a future UI/UX-spec
    revision, not a query change.
  - Writes `dbo.report` (`report_type = 'PreAction'`), renders PDF + XLSX, writes an audit row,
    returns `reportId` + `rows[]` + download URLs — all before the response returns.
- `POST /reports/post-action` **[audited]** — synchronous. Calls
  `IAuditLogRepository.ListAsync(tab: null, dateFrom: null, dateTo: null, page: 1, pageSize:
  100_000, ct)` (CAS-009's repository method, called directly — not through the `GET /audit-log`
  HTTP validation layer, since this is a server-side reuse, not a proxied request), then filters the
  returned rows in-memory to the state-changing action allow-list (§7 V-table), maps each
  `AuditAction` enum member to its `actionTaken` display string (§6.2), writes `dbo.report`
  (`report_type = 'PostAction'`), renders PDF + XLSX, writes an audit row, returns `reportId` +
  `rows[]` + download URLs. **Cap decision (RESOLVED 2026-09-22, R. Devarajan):** if
  `ListAsync`'s returned row count equals the 100,000 cap exactly, the result is ambiguous (cannot
  tell if it's the true total or a truncation) — treat this as `report-generation-failed` (500)
  rather than silently returning a partial, incomplete defensible record (§7 V6). No dedicated
  unpaginated repository method is added (Karpathy P4 — `dbo.audit_log` is append-only/unretained,
  CAS-004, so a cap-plus-fail-loud check is the pragmatic compromise given no BRD-specified volume
  ceiling exists). Follow-up (non-blocking, post-launch): revisit this cap if `dbo.audit_log`
  approaches ~50,000 rows in production — true pagination inside the report would become worth the
  added complexity at that point.
- `GET /reports/{reportId}/pdf`, `GET /reports/{reportId}/xlsx` — not audited (read-only retrieval
  of an already-generated, already-audited artifact). Binary file stream from the path recorded in
  `dbo.report` at generation time. `404 report-not-found` for an unknown `reportId`.
- New `dbo.report` table (§5) — first table this spec introduces; not owned by any prior spec
  (CAS-003 §0 explicitly deferred it: "designed when [Reporting] is implemented").
- New stored procedures: `dbo.report_ins`, `dbo.report_get`, `dbo.search_result_lst_all` (§5).
- New repository: `IReportRepository` / `ReportRepository` (insert + get-by-id).
- New repository method: `ISearchResultRepository.ListAllActiveAsync` — extends, does not modify,
  CAS-006's existing per-task `ListAsync`.
- New services: `IPreActionReportService` / `PreActionReportService`, `IPostActionReportService` /
  `PostActionReportService` — split per operation (materially different data sources), matching this
  project's existing Quarantine-module precedent (`IQuarantineListService` vs.
  `IQuarantinePermanentDeleteService`).
- New shared document-writer abstraction: `IReportDocumentWriter` with `WritePdfAsync` and
  `WriteXlsxAsync`, taking a generic `(title, columnHeaders, rows, generatedAt, generatedBy)` shape
  — used by both report types immediately (Karpathy P4: abstraction justified because the pattern
  appears twice in this same spec), implemented as `PdfSharpReportWriter` and
  `ClosedXmlReportWriter` in `Cachet.ArchiveSync.Api/Reporting/`.
- New `ReportsController` (`Cachet.ArchiveSync.Api/Controllers/ReportsController.cs`) owning all
  four routes.
- New `AuditAction` enum members: `PreActionReportGenerated`, `PostActionReportGenerated` (appended,
  not reordered — CAS-005 §15 convention).
- New config key `Reports:LocalStorageRootPath` (§9, §15 — flagged the same way CAS-007 flagged
  `Sync:ODriveMirrorRootPath`).

---

## §4 Out-of-Scope

- **Report history / archive screen or list endpoint** — API design doc §9.3 explicitly scopes this
  out ("no separate report-history/archive screen or endpoint exists, since FR-25–FR-27 don't ask
  for one"). Generated files remain on disk server-side regardless.
- **Email or any notification affordance** — explicitly prohibited by FR-27 and BRD §4.2. No SMTP
  client, no notification service, anywhere in this module's diff.
- **Async task-queue generation** — reversed by §0.1; not built.
- **The `?format=` single-endpoint retrieval design** — API design doc's earlier draft, superseded
  by the two-endpoint form (§9.3) before this spec was written.
- **O-Drive Sync / Content Scan as a Pre-Action report scope** — BRD v2.2 §12.1 item 9 confirms
  O-Drive Sync is independent of Keyword Search and Quarantine is Keyword-Search-only; the API
  design doc's `panel` enum has exactly three values (`Filename Matches`, `Content Matches`,
  `Quarantine`), none of them O-Drive Sync. Pre-Action report scope is search + quarantine only.
- **Retention/archival or deletion of old `dbo.report` rows or their PDF/XLSX files** — no policy
  defined in the BRD; files accumulate on local disk indefinitely, same posture as `dbo.audit_log`
  (CAS-004, append-only, no scheduled purge).
- **Drive encryption-at-rest** (BRD §12.2 open item) — not applicable; this spec never writes to the
  O-Drive mirror, only to `Reports:LocalStorageRootPath`.

---

## §5 Database Objects

### Tables / Columns

**New table `dbo.report`:**

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `report_id` | `INT IDENTITY(1,1)` | No | PK. Plain auto-incrementing integer, per API design doc §1.7's ID-format convention. |
| `report_type` | `NVARCHAR(10)` | No | `CHECK (report_type IN ('PreAction','PostAction'))`. |
| `generated_at` | `DATETIME2(3)` | No | UTC. Returned verbatim as `generatedAt`. |
| `generated_by_user_id` | `INT` | No | FK → `dbo.users(user_id)`. Joined for the `generatedBy` (username) response field. |
| `pdf_file_path` | `NVARCHAR(400)` | No | Path relative to `Reports:LocalStorageRootPath` — never the raw absolute path (keeps the config root the single point of change). |
| `xlsx_file_path` | `NVARCHAR(400)` | No | Same convention as `pdf_file_path`. |
| `created_at` | `DATETIME2(3)` | No | `DEFAULT SYSUTCDATETIME()`. Row-insert timestamp; equals `generated_at` in practice but kept separate per this project's existing audit/entity convention (e.g. `quarantine_entry`'s `created_at` vs. `quarantined_on`). |

**Constraints:** `PK_report` (`report_id`), `FK_report_users` (`generated_by_user_id` →
`dbo.users.user_id`), `CK_report_type`.

**Indexes:** none beyond the clustered PK — retrieval is always a single-row `report_id` lookup
(§9.3), and there is no list/history endpoint (§4) to index for.

No changes to `dbo.search_result`, `dbo.quarantine_entry`, `dbo.audit_log`, or `dbo.file_index`
schemas — this spec only adds read queries against them.

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `dbo.report_ins` | dbo | ins | `@report_type`, `@generated_by_user_id`, `@pdf_file_path`, `@xlsx_file_path` → returns new `report_id`. Backs both `POST` endpoints (§6.1, §6.2). |
| `dbo.report_get` | dbo | get | `@report_id` → one row (`report_type`, `generated_at`, `generated_by` username via join, `pdf_file_path`, `xlsx_file_path`), or no rows if unknown. Backs §6.3/§6.4. |
| `dbo.search_result_lst_all` | dbo | lst | No parameters (or an optional `@include_inactive_files BIT = 0`, default excludes soft-deleted files). Joins `dbo.search_result` → `dbo.file_index` (`is_active = 1`), across every `task_queue_id`, ordered `created_at DESC`. Backs the Pre-Action report's Filename/Content panels (§6.1). Not paginated — reports render every matching row. |

### Migration Strategy

`db-author` writes, at `/implement` time (after §15's open items are resolved and status → ready):

- `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-010-reporting-api.sql` — idempotent: `CREATE TABLE
  dbo.report` + its constraints, then `CREATE OR ALTER PROCEDURE` for all three SPs above.
- Per-object mirrors: `Database/CachetArchiveSync/Entities/dbo.report.sql`,
  `Database/CachetArchiveSync/Stored-Procedures/dbo.{report_ins,report_get,search_result_lst_all}.sql`.

No data migration — `dbo.report` starts empty.

---

## §6 API Endpoints

### 6.1 `POST /reports/pre-action` **[audited]**

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]` — report generation is a read/aggregate
action, not a B2-write; matches API design doc §9 (no `403` listed).

**Async:** No — synchronous, per §0.1.

**Request:**
```json
{
  "activeTabLabel": "Keyword Search"
}
```
`activeTabLabel` is optional, carried through unused into the rendered report's subtitle only — it
does not filter `rows[]` (API design doc §9.1: "a label, not a filter").

**Response `200 OK`:**
```json
{
  "reportId": 1,
  "generatedAt": "2026-09-22T14:30:00Z",
  "generatedBy": "m.alvarez",
  "rows": [
    { "fileName": "agreement-004.pdf", "filePath": "contracts/2019/agreement-004.pdf",
      "panel": "Filename Matches", "status": "Flagged" }
  ],
  "files": {
    "pdf": { "downloadUrl": "/reports/1/pdf" },
    "xlsx": { "downloadUrl": "/reports/1/xlsx" }
  }
}
```

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Success (`rows` may be empty) |
| 401 | No valid session |
| 500 | `report-generation-failed` — PDF/XLSX write failure (§7 V2) |

### 6.2 `POST /reports/post-action` **[audited]**

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`.

**Async:** No.

**Request:** same shape as §6.1 (`activeTabLabel`, optional, cosmetic only).

**Response `200 OK`:**
```json
{
  "reportId": 2,
  "generatedAt": "2026-09-22T16:00:00Z",
  "generatedBy": "m.alvarez",
  "rows": [
    { "fileName": "agreement-004.pdf", "filePath": "contracts/2019/agreement-004.pdf",
      "actionTaken": "Deleted Permanently", "sourceTab": "Quarantine",
      "timestamp": "2026-09-22T14:22:10Z", "result": "Success" }
  ],
  "files": {
    "pdf": { "downloadUrl": "/reports/2/pdf" },
    "xlsx": { "downloadUrl": "/reports/2/xlsx" }
  }
}
```

`actionTaken` mapping from `AuditAction` (allow-list — every other `AuditAction` member, e.g.
`Login`, `CrossCheckExcluded`, is excluded from this report):

| `AuditAction` member | `actionTaken` value |
|---|---|
| `KeywordSearchSubmitted`, `ContentScanSubmitted` | `"Searched"` |
| `QuarantineAdd` | `"Quarantined"` |
| `Deleted` | `"Deleted Permanently"` |
| `SyncCopyToB2` | `"Copied to B2"` |
| `SyncUpdateInB2` | `"Updated in B2"` |
| `SyncPurgeFromB2` | `"Purged from B2"` |

`sourceTab` = the audit row's `tab_module` value verbatim. `result`/`timestamp` = the audit row's
`result`/`created_at` verbatim (§7 V-table: failures are never filtered out, matching CAS-009 V5's
precedent for `GET /audit-log`).

**Status codes:** same as §6.1.

### 6.3 `GET /reports/{reportId}/pdf`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]`.

**Async:** No.

**Request:** path param `reportId` (int) only.

**Response `200 OK`:** binary stream, `Content-Type: application/pdf`,
`Content-Disposition: attachment; filename="report-{reportId}.pdf"`.

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | File streamed |
| 401 | No valid session |
| 404 | `report-not-found` — unknown `reportId`, or the row exists but the file is missing from disk (§7 V1) |

### 6.4 `GET /reports/{reportId}/xlsx`

Identical to §6.3 except `Content-Type:
application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` and
`filename="report-{reportId}.xlsx"`.

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `reportId` (§6.3/§6.4) must resolve to an existing `dbo.report` row **and** the recorded file path must exist on disk at request time | 404 `report-not-found` | `Test_V1_UnknownReportIdReturnsNotFound`, `Test_V1_MissingFileOnDiskReturnsNotFound` |
| V2 | Any exception from `IReportDocumentWriter` (PdfSharp/ClosedXML write failure, disk-full, permission denied) during either POST endpoint is caught and mapped, not allowed to bubble as an unhandled 500 with a stack trace | 500 `report-generation-failed` | `Test_V2_PdfWriteFailureReturnsReportGenerationFailed`, `Test_V2_XlsxWriteFailureReturnsReportGenerationFailed` |
| V3 | Post-Action report rows are restricted to the state-changing `AuditAction` allow-list in §6.2's table — read-only/auth actions (`Login`, `LoginFailed`, `Logout`, `CrossCheckExcluded`, `PreActionReportGenerated`, `PostActionReportGenerated`) never appear as rows | N/A — proven by absence | `Test_V3_ReadOnlyAndAuthActionsExcludedFromPostActionReport` |
| V4 | Pre-Action report's Filename/Content panels exclude files where `dbo.file_index.is_active = 0` (Permanently Deleted) | N/A — proven by absence | `Test_V4_SoftDeletedFilesExcludedFromPreActionReport` |
| V5 | Failed audit rows (`result = 'Failure'`) are included in the Post-Action report, never silently dropped | N/A — proven by presence | `Test_V5_FailedActionsAppearInPostActionReport` |
| V6 | If `IAuditLogRepository.ListAsync`'s returned row count equals the 100,000 internal cap exactly, the Post-Action report request fails rather than silently returning a truncated result | 500 `report-generation-failed` | `Test_V6_AuditLogCapReachedFailsLoudRatherThanTruncating` |

---

## §8 UI / UX

N/A — `touches.ui: false`. No `.razor` components; this spec is Api-only. The BRD UI/UX wireframe
(§8, §9) for the report modal exists for reference; its Blazor implementation is a separate, future
Web-kind spec that consumes these four endpoints (same pattern as CAS-009 §8's note for the Audit
Log screen).

---

## §9 Configuration Changes

| Key | Default | Description | Env Override |
|-----|---------|-------------|---------------|
| `Reports:LocalStorageRootPath` | none — must be set, service fails fast at startup if missing (matching `ODriveFileReader`'s `Sync:ODriveMirrorRootPath` precedent, CAS-007) | Filesystem root under which generated PDF/XLSX report files are saved (FR-27). Not a BRD-specified key — flagged for sign-off, §15. | Yes — standard ASP.NET Core config override (`appsettings.Production.json` / environment variable). Never a secret; contains no credentials. |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `POST /reports/pre-action` returns `200` with `rows[]` containing every active (`is_active = 1`) `dbo.search_result` row across all tasks (mapped to `Filename Matches`/`Content Matches`) plus every `Active` `dbo.quarantine_entry` row (mapped to `Quarantine`), and writes a `dbo.report` row with `report_type = 'PreAction'` | `Test_SC1_PreActionReportAggregatesSearchAndQuarantineRows` |
| SC-2 | `POST /reports/post-action` returns `200` with `rows[]` sourced from `IAuditLogRepository.ListAsync` (CAS-009), filtered to the §6.2 allow-list, and writes a `dbo.report` row with `report_type = 'PostAction'` | `Test_SC2_PostActionReportSourcesFromAuditLogQuery` |
| SC-3 | Both POST endpoints write a `dbo.report` row and an audit-log row (`PreActionReportGenerated` / `PostActionReportGenerated`, `tab_module = 'Reports'`, `result = 'Success'`) before the `200` response is returned | `Test_SC3_AuditRowWrittenBeforeResponseReturned` |
| SC-4 | Both POST endpoints write an actual `.pdf` file (parseable by PdfSharp) and `.xlsx` file (parseable by ClosedXML) to `Reports:LocalStorageRootPath`, and the returned `downloadUrl`s resolve to those exact files via §6.3/§6.4 | `Test_SC4_GeneratedFilesExistAndAreParseable`, `Test_SC4_DownloadUrlsServeGeneratedFiles` |
| SC-5 | `GET /reports/{reportId}/pdf` and `/xlsx` return `404 report-not-found` for an unknown `reportId`; `200` with the correct `Content-Type`/`Content-Disposition` for a known one | `Test_SC5_UnknownReportIdReturns404`, `Test_SC5_KnownReportIdReturnsCorrectContentType` |
| SC-6 | No code path in this module ever calls `IB2Client` or sends an email/SMTP call — `Cachet.ArchiveSync.Api/Reporting/` and `ReportsController` have zero references to either | `Test_SC6_NoB2OrEmailReferencesInReportingModule` (static/reflection-based, mirroring `LogHygieneTests`' pattern) |

---

## §11 Telemetry

```csharp
_logger.LogInformation("{Action} completed for {ReportId} ({ReportType})", action, reportId, reportType);
_logger.LogError(ex, "Report generation failed for {ReportType} requested by {UserId}", reportType, userId);
```

- Never log: the B2 application key (N/A here — this module never touches `IB2Client`), a session
  token/cookie value, full file content, or raw archive file paths beyond what the audit log (FR-23)
  already records.
- Do not log the full rendered `rows[]` payload (could be large; the audit row and `dbo.report`
  already provide the durable record).

---

## §12 Security Notes

### B2 Credential Handling
This module never calls `IB2Client` and never references the B2 application key, directly or
indirectly — all report data is sourced from SQL Server (`dbo.search_result`,
`dbo.quarantine_entry`, `dbo.audit_log`) and the local filesystem (`Reports:LocalStorageRootPath`,
a plain, non-secret config path). SC-6 verifies this with a static check.

### Audit Trail
Both `POST /reports/pre-action` and `POST /reports/post-action` write one `dbo.audit_log` row each
(`action`: `PreActionReportGenerated` / `PostActionReportGenerated`, `tab_module: 'Reports'`,
`result: 'Success'`, `user` = the requesting session's user, `file_path`: null — this action has no
single associated file) **before** the `200` response is returned, via `IAuditLogService.WriteAsync`
(CAS-005, unchanged). This extends beyond FR-23's literal action list, matching the precedent
already set and confirmed for exactly this pair of endpoints in the API design doc (§9.1/§9.2,
CONFIRMED 2026-09-09) — report generation itself is part of the defensible record these reports
exist to support.

### Session / Auth Requirements
All four endpoints require `CachetAuthenticated` (a valid session) — none require
`CachetFullAccess`, since report generation and retrieval are read/aggregate operations, not
destructive actions (no `403` is listed for this module in the API design doc's error catalog).
This spec does not depend on the still-open BRD §12.2 login-method decision (that decision — FR-01/
FR-02, live B2 key validation at sign-in — is already resolved per CLAUDE.md's "Source of Truth"
section; this spec only consumes the resulting authenticated-session claim).

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `PreActionReportServiceTests` | `GeneratesReport_AggregatesSearchAndQuarantineRows` | SC-1 |
| `PreActionReportServiceTests` | `ExcludesSoftDeletedFiles` | V4 |
| `PostActionReportServiceTests` | `GeneratesReport_FiltersToStateChangingActions` | SC-2, V3 |
| `PostActionReportServiceTests` | `IncludesFailedActions` | V5 |
| `ReportsControllerTests` | `PreAction_WritesAuditRowBeforeReturning200` | SC-3 |
| `ReportsControllerTests` | `PostAction_WritesAuditRowBeforeReturning200` | SC-3 |
| `ReportsControllerTests` | `GetPdf_UnknownReportId_Returns404` | SC-5, V1 |
| `ReportsControllerTests` | `GetXlsx_UnknownReportId_Returns404` | SC-5, V1 |
| `ReportGenerationFailureTests` | `PdfWriteThrows_MapsTo500ReportGenerationFailed` | V2 |
| `PostActionReportServiceTests` | `AuditLogCapReached_FailsLoudRatherThanTruncating` | V6 |
| `LogHygieneTests` (extended) | `ReportingModule_NoB2OrEmailReferences` | SC-6 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `ReportRepositoryTests` (real SQL Server) | `InsertThenGet_RoundTrips` | §5 SP correctness |
| `SearchResultRepositoryTests` (extended) | `ListAllActiveAsync_ReturnsAcrossAllTasks_ExcludesInactiveFiles` | SC-1, V4 |
| `ReportsApiWebApplicationFactory`-based | `PreAction_EndToEnd_WritesFilesAndAuditRow` | SC-1, SC-3, SC-4 |
| `ReportsApiWebApplicationFactory`-based | `PostAction_EndToEnd_MatchesAuditLogQuery` | SC-2 (depends on CAS-009's `dbo.audit_log_lst` being merged — §15) |
| `ReportsApiWebApplicationFactory`-based | `GetPdf_KnownReportId_ReturnsFileBytes` | SC-4, SC-5 |

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. Deploy the database migration first: `CREATE TABLE dbo.report` + `report_ins`/`report_get`/
   `search_result_lst_all` stored procedures. All additive — no existing table altered.
2. Set `Reports:LocalStorageRootPath` in the target environment's `appsettings.Production.json` (or
   environment variable) before the Api deploys — the service should fail fast at startup if unset
   (matching `ODriveFileReader`'s pattern), not fail lazily on first report request.
3. Deploy `Cachet.ArchiveSync.Api` (new `ReportsController`, services, repositories). Web and Worker
   are untouched by this spec and do not need to deploy together with it.
4. Rollback: revert the Api deployment (removes the four routes). The `dbo.report` table and new
   SPs are additive-only and safe to leave in place — no data migration to reverse, and no other
   spec's code path depends on `dbo.report` existing.

---

## §15 Open Questions

All resolved 2026-09-22 (R. Devarajan, TraceCounts). No unresolved items remain.

- [x] **CAS-009 sequencing (§0.4):** resolved — sequence, don't split. `/implement CAS-010` gated on
  CAS-009 reaching `merged`/implemented (Pre-Action-only partial implementation was considered and
  rejected — see §0.4's full rationale).
- [x] **Dedup question (§3):** resolved — two rows, no dedup (a file appears once per panel). See
  §3's inline rationale.
- [x] **Unpaginated audit-log reuse cap (§3, §6.2):** resolved — reuse `IAuditLogRepository.ListAsync`
  with an internal `pageSize: 100_000` cap; fail loud (`500 report-generation-failed`, §7 V6) if the
  cap is hit exactly, rather than silently truncating. No dedicated unpaginated repository method
  added. Flagged for a non-blocking revisit once `dbo.audit_log` approaches ~50,000 rows.
- [x] **`Reports:LocalStorageRootPath` config key (§9):** resolved — approved as-is, single shared
  root, no per-report-type subfolder (mirrors CAS-007's `Sync:ODriveMirrorRootPath` precedent).
- [x] **Stale skill file (§0.1):** resolved — `.claude/skills/reporting-pdf-xlsx/SKILL.md` updated in
  the same change to match the confirmed synchronous design (tracked outside this spec's own diff,
  since the skill file is not an implementation artifact).

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | | | |
| Reviewer | | | |

---

## §17 Graph Impact

### God Nodes in Blast Radius
None — no C# code nodes exist for this feature yet (§0.2). The only high-edge-count doc node in the
traversal is `API spec §9: Reporting Module (FR-25-FR-27)` itself (4 outbound `references` edges to
the four endpoint nodes), not a "god node" in the code sense.

### Communities / Modules Affected
- **Reporting** (new — this spec creates it).
- **Keyword Search** (read-only: new `dbo.search_result_lst_all` query against CAS-006's table).
- **Quarantine** (read-only: reuses CAS-008's `ListActiveAsync` unchanged).
- **Audit Log** (read-only: reuses CAS-009's `ListAsync`/`dbo.audit_log_lst`, and writes two new
  `AuditAction` members via the existing CAS-005 writer).
- **Project scope:** `Cachet.ArchiveSync.Api` and `Cachet.ArchiveSync.Core` only. `Web` and
  `Worker` are untouched (no UI, no async task-queue involvement per §0.1).

### Surprising Connections
- `ISearchResultRepository` (CAS-006) has no "all tasks" query — every existing consumer scopes by
  `taskQueueId`. This spec is the first to need an aggregate, cross-task view, which is why §5 adds
  a new stored procedure rather than reusing an existing one.
- `dbo.report` was explicitly deferred by CAS-003's schema-foundation spec ("designed when
  [Reporting] is implemented") — this is that implementation, confirming CAS-003's own forward
  note.

### Graphify Queries Run
```
python -m graphify query "Reporting API pre-action post-action report generation PDF XLSX" --budget 2000
```
Result: 11 nodes, all from `0.Specifications/CACHET_API_Design_Specification_v1.md`,
`CACHET_Archive_Files_Search_Sync_BRD_v2.md`, the converted WBS, and `CLAUDE.md` — no
`Cachet.ArchiveSync.*` code nodes, confirming §0.2. Remaining code-level findings (`ISearchResultRepository`'s
per-task scoping, `dbo.report`'s deferred status) came from a manual read of CAS-003, CAS-006,
CAS-008, CAS-009, and the current `src/` tree per Step 3.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test
- [ ] All §7 Validations are implemented and tested
- [ ] All §6 API endpoints exist with correct verb/route/auth (`CachetAuthenticated` on all four —
      confirm none accidentally requires `CachetFullAccess`)
- [ ] §12 Security Notes are implemented as described (no `IB2Client`/email reference anywhere in
      the Reporting module; audit log write happens before both POST responses return)
- [ ] §5 Database objects exist in schema: `dbo.report` table, `report_ins`, `report_get`,
      `search_result_lst_all`
- [ ] §9 Config key `Reports:LocalStorageRootPath` is present in appsettings and the service fails
      fast at startup if it is missing
- [ ] §11 Telemetry events are emitted (no B2 key / session token / PII / full `rows[]` payload in
      logs)
- [ ] §17 Graph Impact matches actual code changes (only Api/Core touched; no Web/Worker changes)
- [ ] No out-of-scope changes in §4 were made (no report-history endpoint, no email code path, no
      async task-queue submission)
- [ ] Confirm CAS-009 (§0.4 gate) was `merged`/implemented before `/implement CAS-010` ran

### Verification Report Format
The reviewer writes `CAS-010.review.md` with:
- Overall verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Security gap list (if any)
- Scope violations (if any)

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 94.7-100% core logic; 0% on 2 Dapper repositories (expected, matches project precedent); 73.6% blended overall vs 85% threshold - accepted by user 2026-09-22 | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Core/Dtos/ReportRow.cs` | New — dbo.report_get projection |
| `src/Cachet.ArchiveSync.Core/Dtos/ReportDocument.cs` | New — generic writer input shape |
| `src/Cachet.ArchiveSync.Core/Dtos/ReportGenerationRequest.cs` | New — shared POST request body |
| `src/Cachet.ArchiveSync.Core/Dtos/ReportFileLinks.cs` | New — download link response shape |
| `src/Cachet.ArchiveSync.Core/Dtos/PreActionReportRow.cs` | New — Pre-Action row shape |
| `src/Cachet.ArchiveSync.Core/Dtos/PostActionReportRow.cs` | New — Post-Action row shape |
| `src/Cachet.ArchiveSync.Core/Dtos/PreActionReportResponse.cs` | New — POST /reports/pre-action 200 body |
| `src/Cachet.ArchiveSync.Core/Dtos/PostActionReportResponse.cs` | New — POST /reports/post-action 200 body |
| `src/Cachet.ArchiveSync.Core/Interfaces/IReportRepository.cs` | New — insert + get-by-id |
| `src/Cachet.ArchiveSync.Core/Interfaces/IReportDocumentWriter.cs` | New — WritePdfAsync/WriteXlsxAsync |
| `src/Cachet.ArchiveSync.Core/Interfaces/IPreActionReportService.cs` | New — includes ReportGenerationOutcome enum |
| `src/Cachet.ArchiveSync.Core/Interfaces/IPostActionReportService.cs` | New |
| `src/Cachet.ArchiveSync.Core/Interfaces/ISearchResultRepository.cs` | Extended — added ListAllActiveAsync |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | Extended — appended PreActionReportGenerated, PostActionReportGenerated |
| `src/Cachet.ArchiveSync.Api/Repositories/ReportRepository.cs` | New — Dapper wrapper over report_ins/report_get |
| `src/Cachet.ArchiveSync.Api/Repositories/SearchResultRepository.cs` | Extended — ListAllActiveAsync wrapping search_result_lst_all |
| `src/Cachet.ArchiveSync.Api/Reporting/PdfSharpReportWriter.cs` | New — PDF renderer |
| `src/Cachet.ArchiveSync.Api/Reporting/ClosedXmlReportWriter.cs` | New — XLSX renderer |
| `src/Cachet.ArchiveSync.Api/Reporting/ReportDocumentWriter.cs` | New — composes the two writers behind IReportDocumentWriter |
| `src/Cachet.ArchiveSync.Api/Services/PreActionReportService.cs` | New |
| `src/Cachet.ArchiveSync.Api/Services/PostActionReportService.cs` | New |
| `src/Cachet.ArchiveSync.Api/Controllers/ReportsController.cs` | New — 4 routes per §6 |
| `src/Cachet.ArchiveSync.Api/Program.cs` | DI registrations for new repos/services/writers |
| `src/Cachet.ArchiveSync.Api/Cachet.ArchiveSync.Api.csproj` | Added PdfSharp 6.1.1, ClosedXML 0.104.1 PackageReferences |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | Added Reports:LocalStorageRootPath |
| `Database/migrations/20260922_200000__CAS-010-reporting-api.sql` | New — dbo.report table + 3 SPs |
| `Database/CachetArchiveSync/Entities/dbo.report.sql` | New mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.report_ins.sql` | New mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.report_get.sql` | New mirror |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.search_result_lst_all.sql` | New mirror |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `PreActionReportServiceTests` | `Test_SC1_PreActionReportAggregatesSearchAndQuarantineRows` | SC-1 |
| `PreActionReportServiceTests` | `Test_V4_SoftDeletedFilesExcludedFromPreActionReport` | V-4 |
| `PostActionReportServiceTests` | `Test_SC2_PostActionReportSourcesFromAuditLogQuery` | SC-2 |
| `PostActionReportServiceTests` | `Test_V3_ReadOnlyAndAuthActionsExcludedFromPostActionReport` | V-3 |
| `PostActionReportServiceTests` | `Test_V5_FailedActionsAppearInPostActionReport` | V-5 |
| `PostActionReportServiceTests` | `Test_V6_AuditLogCapReachedFailsLoudRatherThanTruncating` | V-6 |
| `ReportGenerationFailureTests` | `Test_V2_XlsxWriteFailureReturnsReportGenerationFailed` | V-2 |
| `ReportsControllerTests` | `Test_SC3_AuditRowWrittenBeforeResponseReturned` | SC-3 |
| `ReportsControllerTests` | `Test_SC4_DownloadUrlsServeGeneratedFiles` | SC-4 |
| `ReportsControllerTests` | `Test_SC5_KnownReportIdReturnsCorrectContentType` | SC-5 |
| `ReportsControllerTests` | `Test_V1_MissingFileOnDiskReturnsNotFound` | V-1 |
| `LogHygieneTests` | `Test_SC6_ReportingModule_NoB2OrEmailReferences` | SC-6 |
| `ReportRepositoryTests` | `InsertThenGet_RoundTrips` | §5 SP correctness (skip-marked, real SQL Server) |
| `ReportsApiWebApplicationFactory-based ReportsControllerTests (IntegrationTests)` | `verb/route/401/200/404 coverage for all 4 endpoints` | SC-1..SC-6 |

### Migration
`20260922_200000__CAS-010-reporting-api.sql`
