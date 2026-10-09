---
id: CAS-015
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
change_type: refactor
related_specs: [CAS-010]
touches:
  database: true
  api: true
  ui: false
  worker: true
  b2: false
  config: true
  security: false
migrations:
  - 20260922_231500__CAS-015-reporting-on-demand-generation.sql
---

# CAS-015 — Change: Reporting Moves to On-Demand Generation (No Server-Side Persistence)

> **Spec-ID note — resolved 2026-09-22 (R. Devarajan, owner):** `cachet_agent_tools spec next-id`
> reported `CAS-011` as the next unused ID at drafting time, not `CAS-015`. Owner has confirmed
> `CAS-015` is correct as authored (CAS-011–CAS-014 are reserved for other in-flight work not yet
> visible in this checkout). No renumbering required.

---

## §1 Current Behavior

CAS-010 (`specs/api/cachet-archive-sync/CAS-010-reporting-api.md`, `status: review`, implemented
2026-09-22) already resolved the original WBS/skill-file assumption of an **async task-queue** report
flow (submit → `Worker` job → poll `GET /reports/{taskId}`) in favor of a **synchronous** flow, per its
own §0.1. However, the synchronous flow it built still **persists** the rendered report files and their
metadata **server-side**, and requires a **separate GET request** to retrieve them. That is the specific
behavior this spec changes — there is no async task-queue/Worker involvement in reporting today to
remove; today's persisted-and-separately-retrieved shape is what moves to fully on-demand,
stream-in-the-same-response generation.

Today's exact shape:

- **`POST /reports/pre-action`** (`src/Cachet.ArchiveSync.Api/Controllers/ReportsController.cs:45-66`)
  and **`POST /reports/post-action`** (`ReportsController.cs:68-90`) are synchronous
  (`[Authorize(Policy = "CachetAuthenticated")]`). Each calls its service
  (`PreActionReportService.GenerateAsync`, `src/Cachet.ArchiveSync.Api/Services/PreActionReportService.cs:61-166`;
  `PostActionReportService.GenerateAsync`, `src/Cachet.ArchiveSync.Api/Services/PostActionReportService.cs:62-165`),
  which:
  1. Aggregates report rows (search+quarantine, or filtered audit-log rows respectively).
  2. Renders both a PDF (`PdfSharpReportWriter`, `src/Cachet.ArchiveSync.Api/Reporting/PdfSharpReportWriter.cs`)
     and an XLSX (`ClosedXmlReportWriter`, `src/Cachet.ArchiveSync.Api/Reporting/ClosedXmlReportWriter.cs`),
     via the shared `IReportDocumentWriter` (`ReportDocumentWriter.cs:12-28`), to disk at
     `Path.Combine(_rootPath, ReportType, "{baseFileName}.pdf|xlsx")`
     (`PreActionReportService.cs:115-124`, `PostActionReportService.cs:114-123`), where `_rootPath`
     comes from config key **`Reports:LocalStorageRootPath`**
     (`PreActionReportService.cs:56-58`, `PostActionReportService.cs:57-59`,
     `ReportsController.cs:39-41`; set to `../.reports-local` in
     `src/Cachet.ArchiveSync.Api/appsettings.Development.json:33-35`).
  3. Inserts one row into **`dbo.report`** via `IReportRepository.InsertAsync`
     (`src/Cachet.ArchiveSync.Core/Interfaces/IReportRepository.cs:14-19`, backed by stored procedure
     `dbo.report_ins`), recording `report_type`, `generated_by_user_id`, `pdf_file_path`,
     `xlsx_file_path` (relative paths, `Database/CachetArchiveSync/Entities/dbo.report.sql:24-38`).
  4. Writes an audit-log row (`PreActionReportGenerated` / `PostActionReportGenerated`) before
     returning.
  5. Returns `200 OK` with `reportId`, `generatedAt`, `generatedBy`, `rows[]`, and a `files` object
     containing **download URLs** (not file bytes) — `files.pdf.downloadUrl = "/reports/{reportId}/pdf"`,
     `files.xlsx.downloadUrl = "/reports/{reportId}/xlsx"`
     (`PreActionReportService.cs:152-163`, `PostActionReportService.cs:151-162`;
     shapes: `src/Cachet.ArchiveSync.Core/Dtos/PreActionReportResponse.cs`,
     `PostActionReportResponse.cs`, `ReportFileLinks.cs`).
- **`GET /reports/{reportId}/pdf`** and **`GET /reports/{reportId}/xlsx`**
  (`ReportsController.cs:92-126`) are the only retrieval path: each looks up the `dbo.report` row via
  `IReportRepository.GetAsync` (backed by `dbo.report_get`), resolves the recorded relative path under
  `Reports:LocalStorageRootPath`, and streams the file from disk if it exists (`404 report-not-found`
  otherwise — `ReportsController.cs:104-119`, `128-131`).
- **`dbo.report`** table (`Database/CachetArchiveSync/Entities/dbo.report.sql`,
  `Database/migrations/20260922_200000__CAS-010-reporting-api.sql`) is write-once, no update/delete
  path, accumulates one row per report generated, indefinitely (CAS-010 §4: "files accumulate on local
  disk indefinitely, same posture as `dbo.audit_log`").
- **No Worker/task-queue involvement exists in reporting today** — confirmed by grep: no
  `ReportGenerationTaskProcessor` (or similarly named class) exists anywhere in
  `src/Cachet.ArchiveSync.Worker/`, and no `Cachet.ArchiveSync.Web` file references reporting at all
  (grep for "report" under `src/Cachet.ArchiveSync.Web/` returns only build-output binaries). CAS-010's
  own §0.1 already reversed the WBS's original async-task assumption before any Worker code was
  written — this spec is not removing task-queue code because none exists; it is removing the
  **persisted-file + separate-retrieval-endpoint** pattern CAS-010 built instead.
- ClosedXML/PdfSharp are confined to `src/Cachet.ArchiveSync.Api/Reporting/` (`ClosedXmlReportWriter.cs`,
  `PdfSharpReportWriter.cs`) — confirmed by grep; no other project references either library.

---

## §2 Desired Behavior

Per BRD §12.3 item 9 (quoted verbatim in §3): report generation becomes fully on-demand — the rendered
file is produced and returned **in the same HTTP response** as the generation request, with **no**
server-side persistence of the file or of report metadata after the response completes.

- `POST /reports/pre-action` and `POST /reports/post-action` remain the two entry points (their
  aggregation/mapping logic — search+quarantine union, audit-log allow-list filtering, the 100,000-row
  cap fail-loud check — is **unchanged**, §9). Each now takes an additional required request parameter
  selecting the desired output format (`format: "pdf" | "xlsx"`) and returns that single rendered file
  **directly as the response body** — `200 OK`, `Content-Type: application/pdf` or
  `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`,
  `Content-Disposition: attachment; filename="pre-action-report-{timestamp}.pdf|xlsx"` — instead of a
  JSON envelope containing `reportId` + download URLs.
  - **Rationale for a `format` request parameter over always generating both files:** the current UI/UX
    design (§8.2/§8.4) shows two separate buttons, "Download PDF" and "Download XLSX" — each a
    distinct user action requesting one file. An on-demand, no-persistence design that still rendered
    _both_ formats on every request would render (and immediately discard) a file the user never asked
    for, on every click. One request → one rendered format → one response body is the shape that
    avoids wasted rendering work under the new no-persistence model. **This is a specific design choice
    this spec is making, not something BRD §12.3 item 9 mandates outright — flagged for owner
    confirmation, §14.**
  - The `rows[]` data (for the on-screen modal table, per the wireframe §8.2) is no longer returned by
    these two endpoints under this design, since the endpoint now returns a binary file body, not JSON.
    **This is a UI-consuming behavior change with no `touches.ui: true` Web spec yet built to consume
    it** — flagged in §14, since no Blazor screen exists yet to be broken or to inform this trade-off.
- `GET /reports/{reportId}/pdf` and `GET /reports/{reportId}/xlsx` are **removed** — there is no
  `reportId` to retrieve by, because nothing is persisted after the response completes.
- **`dbo.report`** table, `dbo.report_ins`, `dbo.report_get` stored procedures, `IReportRepository`,
  and the `Reports:LocalStorageRootPath` config key are all **removed** — no report file or row
  survives past the response.
- `PreActionReportGenerated` / `PostActionReportGenerated` audit-log rows continue to be written
  **before** the response returns (§9, §11) — the audit trail requirement (FR-23) is unaffected; only
  the report **artifact's** persistence is removed, not the fact-of-generation record.
- ClosedXML/PdfSharp rendering logic (`PdfSharpReportWriter`, `ClosedXmlReportWriter`,
  `IReportDocumentWriter`) is **unaffected in its rendering approach** — it changes only in its output
  target: writing to an in-memory `Stream`/`byte[]` returned to the controller, instead of writing to a
  file path on disk.

---

## §3 Reason for Change

- **Type:** `refactor`
- **Trigger:** BRD §12.3 item 9 (confirmed 2026-09-22, R. Devarajan, TraceCounts — Solution Owner /
  Product Manager), quoted verbatim from
  `0.Specifications/CACHET_Archive_Files_Search_Sync_BRD_v2.md`:

  > **9 Report Storage** — Pre-Action Impact Reports and Post-Action Outcome Reports are **not**
  > persisted on the server. Each report is generated on demand when requested and downloaded directly
  > to the user's browser. This supersedes Section 11.4's "saved locally as PDF and XLSX" wireframe
  > caption and any prior design describing a server-side report store with a separate download-link
  > retrieval step.

  This is a **binding decision, not an open item** (BRD §12.3 preamble: "These are binding decisions,
  not open items").

- **Superseded UI/UX wireframe wording:** the UI/UX Design Specification's Pre-Action Impact Report
  section (`0.Specifications/CACHET_UIUX_Design_Specification_v1.md` §8.2/§8.4 — the BRD's own
  cross-reference to "Section 11.4" does not match this document's current section numbering, where
  the equivalent content is §8; the wording BRD §12.3 item 9 supersedes is confirmed present there
  verbatim) reads: _"Saved as PDF and XLSX (ClosedXML / PdfSharp)"_ (§8.2 footer note) and, at §8.4,
  frames "saved locally" as meaning **server-side**, resolving the retrieval gap with a
  generate-then-separately-download pattern (`GET /reports/{reportId}/pdf|xlsx`) — exactly the pattern
  BRD §12.3 item 9 now reverses. Per the BRD, only the "saved locally"/server-side-store framing is
  superseded; the wireframe's dropped "no email is sent" clause (§8.2, already removed project-wide per
  §8.2's own note) is unrelated and stands unchanged — this spec makes no email-related change.
- **Risk if not changed:** the shipped CAS-010 implementation would retain every generated report's PDF
  and XLSX indefinitely on server-side disk (CAS-010 §4: "files accumulate on local disk indefinitely,
  no retention/archival/deletion policy"), plus one `dbo.report` row per generation, forever — directly
  contradicting the Product Manager's now-binding BRD decision, and running counter to data-minimization
  posture for a regulated-environment deliverable (§11).

---

## §4 Blast Radius (Graph Impact)

> `graphify-out/` exists for this repo (per CLAUDE.md, generated during CAS-010's work), but this
> analysis was done by direct grep/read against the current tree, not a graphify query, since the
> question ("what touches Reports:LocalStorageRootPath / dbo.report / the four report routes") is
> answerable precisely by grep and the result needs file:line precision the graph would not add here.

### Primary Change Target

- Files: `ReportsController.cs`, `PreActionReportService.cs`, `PostActionReportService.cs`,
  `IReportRepository.cs`, `ReportRepository.cs` (Dapper impl, not yet read in full here but confirmed
  present at `src/Cachet.ArchiveSync.Api/Repositories/ReportRepository.cs` per CAS-010's Files Changed
  log), `dbo.report` table + `report_ins`/`report_get` stored procedures.
- Entity: the Reporting module's persistence/retrieval layer end-to-end.

### Direct Dependents (nodes with edges INTO the changed entity)

- `Program.cs` — DI registrations for `IReportRepository`/`ReportRepository`,
  `IReportDocumentWriter`/`ReportDocumentWriter`, `PdfSharpReportWriter`, `ClosedXmlReportWriter`,
  `IPreActionReportService`/`PreActionReportService`, `IPostActionReportService`/`PostActionReportService`
  (per CAS-010's Files Changed log) — registrations for the removed repository/config key must be
  removed; registrations for the (unchanged-in-nature) writers/services stay, with constructor
  signature changes flowing through.
- Test doubles/fixtures constructing these services with an in-memory
  `Reports:LocalStorageRootPath` config value (`PreActionReportServiceTests.cs:38-46`,
  presumably mirrored in `PostActionReportServiceTests.cs`) — the config dependency is removed from the
  constructor, so every such fixture changes.

### Downstream Modules

- **Reporting** — primary target, full rewrite of the persistence/retrieval half of the module.
- **Keyword Search** — read-only dependency unchanged (`ISearchResultRepository.ListAllActiveAsync`,
  CAS-006, still called for Pre-Action aggregation).
- **Quarantine** — read-only dependency unchanged (`IQuarantineEntryRepository.ListActiveAsync`,
  CAS-008).
- **Audit Log** — two touch points, both unchanged in behavior: (1) `IAuditLogRepository.ListAsync`
  (CAS-009) still sources Post-Action rows; (2) `IAuditLogService.WriteAsync` (CAS-005) still writes
  `PreActionReportGenerated`/`PostActionReportGenerated` rows before the response returns.
- **Worker / Task Manager** — **not touched**, confirmed no reporting code exists there today (§1).
  `touches.worker: true` in this spec's frontmatter reflects the CAS-015 task description's premise
  that a Worker-side processor might need removal; since none exists, this box is set `true` only to
  make the "verified none exists, nothing to remove" finding explicit and auditable, not because a
  Worker file changes.
- **Web** — not touched; no Blazor screen consumes any report endpoint yet (confirmed by grep, §1). The
  eventual UI/UX-spec implementation of the report modal (CAS-010 §8, not yet written as a `touches.ui:
true` spec) will need to consume this spec's new streamed-file contract, not CAS-010's JSON-envelope
  one — flagged in §14 as a sequencing note for whoever writes that future Web spec.

### Cross-Project Risk

- `Cachet.ArchiveSync.Core` DTOs consumed only by `Api` today
  (`PreActionReportResponse`, `PostActionReportResponse`, `ReportFileLinks`, `ReportDownloadLink`,
  `ReportRow`) — no `Worker` or `Web` project references them (confirmed by grep), so removing/replacing
  them carries no cross-project contract risk beyond the Api project itself.

### God Node Involvement

- None. `ReportsController`/the two report services are each single-consumer, low-edge-count nodes —
  same conclusion CAS-010 §17 reached ("no god nodes; only doc nodes carry high edge-count in this
  module").

### Graphify Queries Run

None this pass — manual grep/read only (rationale above). If a future revision of this spec runs
`graphify query "Reports:LocalStorageRootPath dbo.report on-demand"`, that result should be appended
here rather than replacing this note.

---

## §5 Files to Change

| File                                                                                                                                                                 | Change Type             | Description                                                                                                                                                                                                                                                                                                                                                  |
| -------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `src/Cachet.ArchiveSync.Api/Controllers/ReportsController.cs`                                                                                                        | modify                  | Remove `GetPdf`/`GetXlsx` actions and `StreamReportFileAsync`/`ReportNotFound` helpers; remove `IReportRepository`/`_rootPath` constructor dependency; change `PreAction`/`PostAction` actions to accept a `format` parameter and return `File(bytes, contentType, downloadName)` directly from the service result instead of `Ok(result.Response)`.         |
| `src/Cachet.ArchiveSync.Api/Services/PreActionReportService.cs`                                                                                                      | modify                  | Remove `IReportRepository`/`_rootPath`/`Reports:LocalStorageRootPath` dependency; render to `Stream`/`byte[]` via `IReportDocumentWriter` instead of a file path; return the rendered bytes + content-type + filename instead of a `PreActionReportResponse` with download URLs; keep aggregation logic (search+quarantine union, dedup decision) unchanged. |
| `src/Cachet.ArchiveSync.Api/Services/PostActionReportService.cs`                                                                                                     | modify                  | Same shape of change as `PreActionReportService.cs`; keep the audit-log allow-list filtering and the 100,000-row cap fail-loud check (§7 V6, unchanged) as-is.                                                                                                                                                                                               |
| `src/Cachet.ArchiveSync.Api/Reporting/IReportDocumentWriter.cs` (`Cachet.ArchiveSync.Core/Interfaces/`)                                                              | modify                  | Change `WritePdfAsync`/`WriteXlsxAsync` signatures from `(document, filePath, ct)` to `(document, ct) -> Task<byte[]>` (or `Task<Stream>`) — no file path parameter.                                                                                                                                                                                         |
| `src/Cachet.ArchiveSync.Api/Reporting/PdfSharpReportWriter.cs`                                                                                                       | modify                  | Render to a `MemoryStream`/`byte[]` return value instead of `PdfDocument.Save(filePath)`.                                                                                                                                                                                                                                                                    |
| `src/Cachet.ArchiveSync.Api/Reporting/ClosedXmlReportWriter.cs`                                                                                                      | modify                  | Render to a `MemoryStream`/`byte[]` return value instead of `XLWorkbook.SaveAs(filePath)`.                                                                                                                                                                                                                                                                   |
| `src/Cachet.ArchiveSync.Api/Reporting/ReportDocumentWriter.cs`                                                                                                       | modify                  | Update delegation signatures to match.                                                                                                                                                                                                                                                                                                                       |
| `src/Cachet.ArchiveSync.Core/Interfaces/IPreActionReportService.cs`, `IPostActionReportService.cs`                                                                   | modify                  | Change `GenerateAsync` return shape from a `Response`-with-download-URLs result to a rendered-file result (bytes/stream + content-type + filename + outcome).                                                                                                                                                                                                |
| `src/Cachet.ArchiveSync.Core/Interfaces/IReportRepository.cs`                                                                                                        | delete                  | No longer needed — nothing is persisted.                                                                                                                                                                                                                                                                                                                     |
| `src/Cachet.ArchiveSync.Api/Repositories/ReportRepository.cs`                                                                                                        | delete                  | Implementation of the deleted interface.                                                                                                                                                                                                                                                                                                                     |
| `src/Cachet.ArchiveSync.Core/Dtos/PreActionReportResponse.cs`, `PostActionReportResponse.cs`, `ReportFileLinks.cs`, `ReportRow.cs`                                   | delete                  | JSON-envelope/download-link/row-lookup shapes no longer returned.                                                                                                                                                                                                                                                                                            |
| `src/Cachet.ArchiveSync.Core/Dtos/ReportGenerationRequest.cs`                                                                                                        | modify                  | Add the new `format` field (`"pdf" \| "xlsx"`); keep `activeTabLabel`.                                                                                                                                                                                                                                                                                       |
| `src/Cachet.ArchiveSync.Core/Dtos/PreActionReportRow.cs`, `PostActionReportRow.cs`                                                                                   | keep, modify usage only | Row-shaping logic used internally to build the rendered document is unchanged; only the _response envelope_ that exposed `rows[]` externally is removed (§2).                                                                                                                                                                                                |
| `src/Cachet.ArchiveSync.Api/Program.cs`                                                                                                                              | modify                  | Remove `IReportRepository`/`ReportRepository` DI registration; remove any config-validation/fail-fast registration tied to `Reports:LocalStorageRootPath`.                                                                                                                                                                                                   |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json`                                                                                                            | modify                  | Remove the `Reports:LocalStorageRootPath` key.                                                                                                                                                                                                                                                                                                               |
| `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-015-reporting-on-demand-generation.sql`                                                                                  | add                     | `DROP PROCEDURE dbo.report_get`, `DROP PROCEDURE dbo.report_ins`, `DROP TABLE dbo.report` (append-only migration file recording the drop, per the hybrid model — see §7).                                                                                                                                                                                    |
| `Database/CachetArchiveSync/Entities/dbo.report.sql`                                                                                                                 | delete                  | Mirror of the dropped table.                                                                                                                                                                                                                                                                                                                                 |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.report_ins.sql`, `dbo.report_get.sql`                                                                              | delete                  | Mirrors of the dropped procedures.                                                                                                                                                                                                                                                                                                                           |
| `src/Cachet.ArchiveSync.Api.Tests/PreActionReportServiceTests.cs`, `PostActionReportServiceTests.cs`, `ReportsControllerTests.cs`, `ReportGenerationFailureTests.cs` | modify                  | Update fixtures to drop `IReportRepository`/config mocking; assert on returned bytes/content-type instead of `reportId`/download URLs; remove `GetPdf`/`GetXlsx` test cases.                                                                                                                                                                                 |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/ReportRepositoryTests.cs`                                                                                               | delete                  | Tests the deleted repository/stored procedures.                                                                                                                                                                                                                                                                                                              |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/ReportsControllerTests.cs`, `Reporting/ReportsApiWebApplicationFactory.cs`                                              | modify                  | Update end-to-end assertions to the new streamed-file contract; remove `GetPdf`/`GetXlsx` end-to-end cases.                                                                                                                                                                                                                                                  |
| `.claude/skills/reporting-pdf-xlsx/SKILL.md`                                                                                                                         | modify                  | CAS-010 §0.1 already flagged this file as carrying stale, pre-synchronous WBS wording as a non-blocking follow-up; this spec's implementation should fold in that correction (drop _both_ the original async-task wording and CAS-010's persisted-file/download-URL wording) rather than leaving two generations of staleness in the same file.              |

---

## §6 Files NOT to Change

| File                                                                                                                               | Why left untouched                                                                                                                                                                                        |
| ---------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/Cachet.ArchiveSync.Core/Interfaces/ISearchResultRepository.cs`, `SearchResultRepository.cs` (incl. `ListAllActiveAsync`)      | Pre-Action aggregation source query is unchanged (§9) — only the response/persistence layer changes.                                                                                                      |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineEntryRepository.cs` and its `ListActiveAsync` implementation                    | Same reasoning — Quarantine-scope read is unaffected.                                                                                                                                                     |
| `src/Cachet.ArchiveSync.Core/Interfaces/IAuditLogRepository.cs`, its `ListAsync` implementation, and `dbo.audit_log_lst` (CAS-009) | Post-Action's audit-log source query, including the 100,000-row cap and fail-loud behavior, is explicitly preserved (§2, §9).                                                                             |
| `src/Cachet.ArchiveSync.Core/Interfaces/IAuditLogService.cs`/its `WriteAsync` implementation (CAS-005)                             | The audit-trail write for `PreActionReportGenerated`/`PostActionReportGenerated` continues unchanged — only the report _artifact's_ persistence is removed, not the audit record of generation (§2, §11). |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs`                                                                                 | No new/removed enum members — the two report-generation audit actions already exist and are reused as-is.                                                                                                 |
| `Database/CachetArchiveSync/Entities/dbo.search_result.sql`, `dbo.quarantine_entry.sql`, `dbo.audit_log.sql`, `dbo.file_index.sql` | No schema change to any table this module reads from — only `dbo.report` (a table this module owns outright) is dropped.                                                                                  |
| `src/Cachet.ArchiveSync.Web/**`                                                                                                    | Confirmed empty of any report references (§1, §4) — no UI exists yet to break or update.                                                                                                                  |
| `src/Cachet.ArchiveSync.Worker/**`                                                                                                 | Confirmed empty of any report-processing code (§1, §4) — nothing to remove.                                                                                                                               |
| B2 client / `IB2Client` and any B2 configuration                                                                                   | This module has never called `IB2Client` (CAS-010 §12, reconfirmed unaffected — no report data path touches B2, before or after this change).                                                             |

---

## §7 Database Changes

### Changed Objects

| Object                      | Schema | Change        | Notes                                                                                                                               |
| --------------------------- | ------ | ------------- | ----------------------------------------------------------------------------------------------------------------------------------- | ---------------- |
| `dbo.report`                | dbo    | **Dropped**   | Write-once report-metadata table introduced by CAS-010; no longer needed because nothing is persisted after the response completes. |
| `dbo.report_ins`            | dbo    | **Dropped**   | Inserted `dbo.report` rows; no longer called.                                                                                       |
| `dbo.report_get`            | dbo    | **Dropped**   | Resolved a `reportId` to its row for the now-removed `GET /reports/{reportId}/pdf                                                   | xlsx` endpoints. |
| `dbo.search_result_lst_all` | dbo    | **Unchanged** | Still backs Pre-Action aggregation (§6); not part of this spec's diff.                                                              |

A `reports` table tracking generated-report metadata/file paths does exist (`dbo.report`, above) and
must be dropped — this is exactly the case the task brief anticipated, not the "never tracked, files

- task_queue rows only" alternative. There is **no** `task_queue` involvement for reports to clean up
  (§1) — the only schema object touching reporting is `dbo.report` and its two stored procedures.

### Migration Required

**Yes.** Hybrid model, two consistent artifacts:

- Migration: `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-015-reporting-on-demand-generation.sql` —
  append-only, contains `DROP PROCEDURE IF EXISTS dbo.report_get`,
  `DROP PROCEDURE IF EXISTS dbo.report_ins`, `DROP TABLE IF EXISTS dbo.report`, in that dependency
  order (procedures before the table, though SQL Server does not enforce a dependency ordering here —
  kept for readability and to match this project's drop-order convention).
- Mirror update: **delete** `Database/CachetArchiveSync/Entities/dbo.report.sql`,
  `Database/CachetArchiveSync/Stored-Procedures/dbo.report_ins.sql`,
  `Database/CachetArchiveSync/Stored-Procedures/dbo.report_get.sql` (the mirror reflects
  current-state schema; a dropped object has no current-state file — see CAS-010's own note that the
  mirror files are "declarative, current-state definitions, not re-run").
- **No data migration/backfill** — any existing `dbo.report` rows and their referenced files on disk
  are abandoned by this drop (no `SELECT INTO` archive step). This is a deliberate, low-risk choice
  given `dbo.report` starts empty in every environment that hasn't yet run CAS-010's own migration in
  production (per CAS-010 §14 Rollout, only a Development deploy is confirmed as of this spec's
  drafting) — flagged for owner confirmation in §14 if any environment has accumulated real reports
  worth preserving before this migration runs.

---

## §8 API Contract Changes

**Breaking: Yes.** Both POST endpoints' response shape changes entirely (JSON envelope → binary file
body); both GET endpoints are removed outright.

| Endpoint                       | Field            | Before                                                                                                                       | After                                                                                                                                                                                                                           | Breaking?                                                                     |
| ------------------------------ | ---------------- | ---------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| `POST /reports/pre-action`     | Request body     | `{ "activeTabLabel"?: string }`                                                                                              | `{ "activeTabLabel"?: string, "format": "pdf" \| "xlsx" }` — `format` is a new required field                                                                                                                                   | Yes                                                                           |
| `POST /reports/pre-action`     | Response (`200`) | `application/json`: `{ reportId, generatedAt, generatedBy, rows[], files: { pdf: { downloadUrl }, xlsx: { downloadUrl } } }` | Binary body: `Content-Type: application/pdf` or `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` (per requested `format`), `Content-Disposition: attachment; filename="pre-action-report-{timestamp}.{ext}"` | Yes                                                                           |
| `POST /reports/post-action`    | Request body     | `{ "activeTabLabel"?: string }`                                                                                              | `{ "activeTabLabel"?: string, "format": "pdf" \| "xlsx" }`                                                                                                                                                                      | Yes                                                                           |
| `POST /reports/post-action`    | Response (`200`) | Same JSON shape as Pre-Action, `report_type = 'PostAction'`                                                                  | Same binary-body shape as Pre-Action, filename prefix `post-action-report-{timestamp}`                                                                                                                                          | Yes                                                                           |
| `GET /reports/{reportId}/pdf`  | Route            | Exists                                                                                                                       | **Removed** (404 for any caller after deploy — no `reportId` exists to look up)                                                                                                                                                 | Yes                                                                           |
| `GET /reports/{reportId}/xlsx` | Route            | Exists                                                                                                                       | **Removed**                                                                                                                                                                                                                     | Yes                                                                           |
| Both POST endpoints            | Status codes     | `200`, `401`, `500 report-generation-failed`                                                                                 | Same three codes; `report-not-found` (`404`) is no longer a possible response from _any_ reporting endpoint, since it was only ever returned by the two removed GET routes                                                      | No new codes, but the `report-not-found` `ProblemDetails` type becomes unused |

No API version bump exists in this project's convention today (CAS-010 introduced routes at the root
`""` prefix with no `/v1`-style versioning) — this spec follows that precedent and does not introduce
one; flagged as a general project gap, not specific to this spec, in §14.

---

## §9 Validations Changed

| ID           | Rule (Before)                                                                                                                                                                                                                 | Rule (After)                                                                                                  | Test                                                                                                                              |
| ------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| V1 (CAS-010) | `reportId` (GET routes) must resolve to an existing `dbo.report` row and the recorded file must exist on disk, else `404 report-not-found`                                                                                    | **Removed** — no `reportId`/GET routes exist to validate                                                      | N/A — proven by the routes' absence, `Test_V1_RoutesRemoved_ReturnsRouteNotFound` (or equivalent)                                 |
| V2 (CAS-010) | Any `IReportDocumentWriter` exception during either POST endpoint is caught and mapped to `500 report-generation-failed`, never an unhandled 500                                                                              | **Unchanged** — same catch/map behavior, now wrapping a render-to-bytes call instead of a render-to-file call | `Test_V2_PdfWriteFailureReturnsReportGenerationFailed`, `Test_V2_XlsxWriteFailureReturnsReportGenerationFailed` (carried forward) |
| V3 (CAS-010) | Post-Action rows restricted to the state-changing `AuditAction` allow-list                                                                                                                                                    | **Unchanged**                                                                                                 | `Test_V3_ReadOnlyAndAuthActionsExcludedFromPostActionReport` (carried forward)                                                    |
| V4 (CAS-010) | Pre-Action panels exclude soft-deleted (`is_active = 0`) files                                                                                                                                                                | **Unchanged**                                                                                                 | `Test_V4_SoftDeletedFilesExcludedFromPreActionReport` (carried forward)                                                           |
| V5 (CAS-010) | Failed audit rows (`result = 'Failure'`) included in Post-Action report, never dropped                                                                                                                                        | **Unchanged**                                                                                                 | `Test_V5_FailedActionsAppearInPostActionReport` (carried forward)                                                                 |
| V6 (CAS-010) | If the audit-log read hits the 100,000-row cap exactly, fail loud (`500`) rather than silently truncate                                                                                                                       | **Unchanged**                                                                                                 | `Test_V6_AuditLogCapReachedFailsLoudRatherThanTruncating` (carried forward)                                                       |
| V7 (new)     | `format` request field must be exactly `"pdf"` or `"xlsx"` (case-sensitive, matching this project's existing enum/string-field convention)                                                                                    | Any other value (missing, empty, unrecognized) → `400` validation error, not a silent default to one format   | `Test_V7_InvalidFormatReturnsValidationError`, `Test_V7_MissingFormatReturnsValidationError`                                      |
| V8 (new)     | After a `200` response completes, no report file exists anywhere under the filesystem location the old `Reports:LocalStorageRootPath` config key pointed to (proven by absence — the key/writer no longer target disk at all) | New — a data-minimization/no-persistence guarantee, not a request-input rule                                  | `Test_V8_NoFileWrittenToDiskAfterResponse` (asserts the rendering path returns bytes and touches no `System.IO.File.*` write API) |

---

## §10 Success Criteria

| ID   | Criterion                                                                                                                                                                                                                                                                                | Test                                                                                                                                                             |
| ---- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| SC-1 | `POST /reports/pre-action` with `format: "pdf"` returns `200` with `Content-Type: application/pdf` and a byte body that PdfSharp can re-open/parse, in the same response — no `reportId`, no separate GET call needed                                                                    | `Test_SC1_PreActionPdf_ReturnsParseablePdfInSameResponse`                                                                                                        |
| SC-2 | `POST /reports/pre-action` with `format: "xlsx"` and `POST /reports/post-action` with either `format` return the analogous correct `Content-Type`/parseable-body result                                                                                                                  | `Test_SC2_PreActionXlsx_ReturnsParseableXlsx`, `Test_SC2_PostActionPdfAndXlsx_ReturnParseableBodies`                                                             |
| SC-3 | Neither POST endpoint creates a `dbo.report` row or leaves a file on server-side disk after the response completes — verified because `dbo.report`/`IReportRepository`/`Reports:LocalStorageRootPath` no longer exist in the codebase (a compile-time guarantee, not just a runtime one) | `Test_SC3_NoReportTableOrRepositoryReferencesRemainInReportingModule` (static/reflection-based, mirroring `LogHygieneTests`' pattern per CAS-010 SC-6 precedent) |
| SC-4 | `GET /reports/{reportId}/pdf` and `/xlsx` routes no longer exist (return the framework's default `404`/route-not-found, not the application's `report-not-found` `ProblemDetails`)                                                                                                       | `Test_SC4_RemovedGetRoutesReturnNotFound`                                                                                                                        |
| SC-5 | Both POST endpoints still write their respective `PreActionReportGenerated`/`PostActionReportGenerated` audit-log row **before** the response returns (audit-trail requirement unaffected by this change)                                                                                | `Test_SC5_AuditRowWrittenBeforeResponseReturned` (carried forward from CAS-010 SC-3)                                                                             |

---

## §11 Security Notes

- **Data minimization (aligns with BRD §9 and this org's SOC 2/PCI-DSS-adjacent posture):** removing
  server-side persistence of Pre-Action Impact Report and Post-Action Outcome Report artifacts reduces
  the volume of potentially sensitive file-path/status/action data retained on disk and in SQL Server
  to zero for this artifact class — a direct data-minimization improvement over CAS-010's
  indefinite-retention posture (§3).
- **B2 credential handling unaffected:** confirmed (again, post-change) that neither report service nor
  either report writer references `IB2Client` or the B2 application key, directly or indirectly — this
  module's data sources remain SQL Server (`dbo.search_result`, `dbo.quarantine_entry`, `dbo.audit_log`)
  and, after this change, nothing on the local filesystem at all. SC-3's static check should be extended
  (or a sibling test added) to continue asserting zero `IB2Client`/email references in the Reporting
  module, matching CAS-010 SC-6's precedent.
- **Audit trail unaffected:** FR-23's requirement that state-changing/reportable actions be recorded is
  fully preserved — only the report _artifact_ loses server-side persistence; the _fact that a report
  was generated, by whom, and when_ remains durably recorded via the unchanged `IAuditLogService.WriteAsync`
  call (§6, §9 SC-5).
- **No new attack surface identified:** streaming rendered bytes directly in the response is not a
  materially different trust boundary than streaming from a known-good on-disk path (CAS-010's prior
  design) — both paths render content sourced entirely from already-authenticated, already-validated
  SQL Server queries, not user-supplied file paths.

---

## §12 Rollback Plan

1. **Api rollback:** revert the `Cachet.ArchiveSync.Api` deployment to the CAS-010 build. This restores
   the four original routes, the `IReportRepository`/`ReportRepository`, and the file-writing behavior
   of the two report services — provided the database rollback (below) has not yet dropped `dbo.report`
   and its stored procedures.
2. **Database rollback:** because this migration is a **hard drop** (`DROP TABLE`/`DROP PROCEDURE`), a
   rollback requires re-running CAS-010's original migration
   (`Database/migrations/20260922_200000__CAS-010-reporting-api.sql`) to recreate `dbo.report`,
   `dbo.report_ins`, `dbo.report_get` — this restores the _schema_, not any _data_ dropped in step 4
   below. If this migration's rollback is ever needed, restore the mirror files
   (`Database/CachetArchiveSync/Entities/dbo.report.sql`, `Stored-Procedures/dbo.report_{ins,get}.sql`)
   from source control alongside re-running the CAS-010 migration, rather than hand-authoring
   replacements.
3. **Config rollback:** re-add `Reports:LocalStorageRootPath` to `appsettings.Development.json` (and any
   environment-specific override) if the Api rollback (step 1) is executed, since the rolled-back code
   will fail fast at startup without it (CAS-010's `ODriveFileReader`-pattern fail-fast behavior,
   preserved in the reverted build).
4. **In-flight risk — this spec's real rollback risk is NOT task-queue rows (§1: no task-queue
   involvement exists in reporting), it is `dbo.report` rows and their on-disk files created under
   CAS-010's contract, still present at the moment this spec's migration runs.** Since §7 chooses a
   hard drop with **no data migration/backfill**, any `dbo.report` row and its referenced PDF/XLSX
   files that exist at migration time are **permanently orphaned/inaccessible** the instant the
   migration runs (the table is gone; the files remain on disk but nothing in the application can look
   them up any longer, and CAS-010's own §4 already scoped out any report-history/list endpoint that
   could enumerate them by directory scan). **This is the direct analogue of the template's §12 point 4
   concern, applied to reporting instead of the task queue — flagged explicitly for owner sign-off in
   §14**, since unlike a genuinely empty table, production environments that have used the Reporting
   feature since CAS-010 shipped may have real rows/files at risk of silent, permanent
   inaccessibility if this migration runs without a prior check.

---

## §13 Test Plan

| Class                                                                                                             | Method                                                                                                                                                                                                                                                                                                         | Proves SC / V                |
| ----------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------- | ---------------- |
| `PreActionReportServiceTests` (modified)                                                                          | `Test_SC1_PreActionPdf_ReturnsParseablePdfInSameResponse`                                                                                                                                                                                                                                                      | SC-1                         |
| `PreActionReportServiceTests` (modified)                                                                          | `Test_SC2_PreActionXlsx_ReturnsParseableXlsx`                                                                                                                                                                                                                                                                  | SC-2                         |
| `PostActionReportServiceTests` (modified)                                                                         | `Test_SC2_PostActionPdfAndXlsx_ReturnParseableBodies`                                                                                                                                                                                                                                                          | SC-2                         |
| `PreActionReportServiceTests` / `PostActionReportServiceTests` (modified)                                         | `Test_V4_SoftDeletedFilesExcludedFromPreActionReport`, `Test_V3_ReadOnlyAndAuthActionsExcludedFromPostActionReport`, `Test_V5_FailedActionsAppearInPostActionReport`, `Test_V6_AuditLogCapReachedFailsLoudRatherThanTruncating` (carried forward, fixtures updated to drop `IReportRepository`/config mocking) | V3, V4, V5, V6               |
| `ReportGenerationFailureTests` (modified)                                                                         | `Test_V2_PdfWriteFailureReturnsReportGenerationFailed`, `Test_V2_XlsxWriteFailureReturnsReportGenerationFailed`                                                                                                                                                                                                | V2                           |
| `ReportsControllerTests` (modified)                                                                               | `Test_V7_InvalidFormatReturnsValidationError`, `Test_V7_MissingFormatReturnsValidationError`                                                                                                                                                                                                                   | V7                           |
| `ReportsControllerTests` (modified)                                                                               | `Test_SC4_RemovedGetRoutesReturnNotFound`                                                                                                                                                                                                                                                                      | SC-4, V1                     |
| `ReportsControllerTests` (modified)                                                                               | `Test_SC5_AuditRowWrittenBeforeResponseReturned`                                                                                                                                                                                                                                                               | SC-5                         |
| `LogHygieneTests` (extended)                                                                                      | `Test_SC3_NoReportTableOrRepositoryReferencesRemainInReportingModule`                                                                                                                                                                                                                                          | SC-3                         |
| `LogHygieneTests` (extended, carried forward)                                                                     | `ReportingModule_NoB2OrEmailReferences`                                                                                                                                                                                                                                                                        | §11                          |
| `Test_V8_NoFileWrittenToDiskAfterResponse` (new, in `PreActionReportServiceTests`/`PostActionReportServiceTests`) | Asserts `IReportDocumentWriter`'s new signature is called and no `System.IO.File.WriteAllBytesAsync`/`FileStream` write occurs in either service                                                                                                                                                               | V8                           |
| `ReportRepositoryTests` (deleted — tests a deleted class)                                                         | N/A                                                                                                                                                                                                                                                                                                            | N/A                          |
| `ReportsApiWebApplicationFactory`-based (modified, `Cachet.ArchiveSync.Api.IntegrationTests`)                     | End-to-end: `POST /reports/pre-action` with each `format` returns the correct content-type/parseable body; `GET /reports/{id}/pdf                                                                                                                                                                              | xlsx` return route-not-found | SC-1, SC-2, SC-4 |

Coverage target: ≥ 85% on changed files (matching CAS-010's applied/accepted precedent of the project's
85% threshold, with the same allowance CAS-010 used for 0%-covered Dapper repository classes if any
survive this change — none do, since `ReportRepository` is deleted outright).

---

## §14 Open Questions

- [x] **Spec-ID mismatch — resolved 2026-09-22 (owner):** confirmed `CAS-015` is correct as authored; no
      renumbering. See §1 note.
- [x] **One-format-per-request design choice (§2) — resolved 2026-09-22 (owner):** confirmed. One
      request → one format → one rendered file, matching the wireframe's two separate Download PDF /
      Download XLSX buttons. No multi-part/bundled-both-formats response. §2/§8 stand as written.
- [x] **`rows[]` for the on-screen modal (§2) — resolved 2026-09-22 (owner):** option (a) confirmed — a
      **separate, lightweight, read-only preview endpoint** (e.g. `GET /reports/pre-action/preview`,
      `GET /reports/post-action/preview`), independent of the file-generating POST endpoints. It re-runs the
      same live aggregation query against the underlying tables (`dbo.search_result`, `dbo.quarantine_entry`,
      `dbo.audit_log`) on each call and returns `rows[]` as JSON for the on-screen modal table — there is no
      persisted/saved report object anywhere in this design; every read, preview or download, is drawn fresh
      from the underlying tables at request time. **Out of scope for this spec** (no Web screen exists yet,
      §6) — this decision is locked in for whoever writes the eventual `touches.ui: true` Blazor Reports
      screen spec, so it is not re-litigated there.
- [x] **Synchronous large-dataset risk — resolved 2026-09-22 (owner):** accepted, combining two of the
      options raised: (1) accept the risk as-is — on-demand generation with no background/async escape hatch
      is the explicit trade-off BRD §12.3 item 9 asks for, and report generation is expected to be infrequent
      and low-concurrency; (2) no architecture change, but the eventual Blazor UI must communicate a
      "this may take a moment" expectation (e.g. a spinner with no fixed timeout assumption) rather than
      assuming a sub-second response, for reports approaching the 100,000-row cap (§9 V6). No response-time
      SLA/ceiling is being set by this spec. This UI messaging requirement is out of scope for this
      (API-only, `touches.ui: false`) spec — carried forward as a requirement for the eventual Blazor Reports
      screen spec.
- [x] **Orphaned `dbo.report` rows/files at migration time (§12) — resolved 2026-09-22 (owner):**
      confirmed no production environment exists yet — the platform is still in development, so no real
      `dbo.report` rows/files are at risk of being orphaned by this migration. No pre-migration export/backfill
      needed.
- [x] **API versioning — resolved 2026-09-22 (owner):** leave as-is; no `/v1`-style versioning
      introduced by this spec. Remains a general project-wide gap, not specific to CAS-015.

---

## §15 Sign-off

| Role     | Name         | Date       | Notes                                                                                                                            |
| -------- | ------------ | ---------- | -------------------------------------------------------------------------------------------------------------------------------- |
| Author   | R. Devarajan | 2026-09-22 | All §14 open questions resolved (spec ID, format-per-request, preview endpoint, performance risk, orphaned-rows, API versioning) |
| Reviewer |              |            |                                                                                                                                  |

---

## §16 Verification Plan

### What the Reviewer Will Check

- [ ] Current behavior (§1) matches what is actually in the code BEFORE the change — specifically, that
      CAS-010's synchronous-but-persisted-and-separately-retrieved shape (not an async task-queue shape)
      is the accurate starting point
- [ ] Desired behavior (§2) is fully implemented — POST endpoints stream the rendered file directly;
      GET retrieval routes are gone; nothing is written to disk or `dbo.report` after a response
      completes
- [ ] Only files in §5 were modified/added/deleted (§6 files are untouched)
- [ ] All §10 Success Criteria have passing tests
- [ ] All §9 Validations (carried-forward and new) are implemented and tested
- [ ] §8 API contract is correct and every breaking change is accounted for (no undeclared additional
      breaking changes)
- [ ] §11 Security Notes implemented as described (audit row still written before response; no
      B2/email reference anywhere in the Reporting module)
- [ ] §4 Blast Radius is accurate — confirm no Worker or Web file was touched, since none exists today
- [ ] §7 migration performs a clean `DROP` with no data migration, and both mirror-file deletions are
      present
- [ ] §12's orphaned-row risk was explicitly raised with the owner (§14) before this spec's migration
      ran in any environment with real data
- [ ] §14 Open Questions were resolved (not silently decided by the implementer) before `/implement`
      proceeded, per Karpathy P2 ("emit `BLOCKED: <topic>` if spec is silent") — this spec deliberately
      leaves several design points open rather than resolving them unilaterally

### Verification Report Format

Reviewer writes `CAS-015.review.md` (or the corrected spec ID's equivalent, per the §14 spec-ID flag):

- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5 file list)
- Unintended blast radius (manual read confirms changes stayed within Api/Core/Database — no Worker/Web
  edits)
- Security gaps
- Confirmation that §14's open questions were answered by the owner, not assumed by the implementer

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 98-100% on all implementer-modified service/reporting/controller files; IPreActionReportService.cs 77.8% accepted as boilerplate record-member gap (owner-confirmed) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Core/Interfaces/IReportDocumentWriter.cs` | modify - WritePdfAsync/WriteXlsxAsync now return Task<byte[]>, no file path param |
| `src/Cachet.ArchiveSync.Core/Interfaces/IPreActionReportService.cs` | modify - added ValidationFailed outcome; result carries FileBytes/ContentType/DownloadFileName |
| `src/Cachet.ArchiveSync.Core/Interfaces/IPostActionReportService.cs` | modify - same shape change as Pre-Action |
| `src/Cachet.ArchiveSync.Core/Dtos/ReportGenerationRequest.cs` | modify - added required Format field (pdf|xlsx) |
| `src/Cachet.ArchiveSync.Api/Reporting/PdfSharpReportWriter.cs` | modify - renders to MemoryStream/byte[], no disk write |
| `src/Cachet.ArchiveSync.Api/Reporting/ClosedXmlReportWriter.cs` | modify - renders to MemoryStream/byte[] via SaveAs(stream) |
| `src/Cachet.ArchiveSync.Api/Reporting/ReportDocumentWriter.cs` | modify - delegation signatures updated |
| `src/Cachet.ArchiveSync.Api/Services/PreActionReportService.cs` | modify - dropped IReportRepository/_rootPath; validates format (V7); renders bytes; audit write unchanged |
| `src/Cachet.ArchiveSync.Api/Services/PostActionReportService.cs` | modify - same shape of change; V3/V5/V6 logic untouched |
| `src/Cachet.ArchiveSync.Api/Controllers/ReportsController.cs` | modify - removed GetPdf/GetXlsx; POST actions return File(bytes, contentType, name) |
| `src/Cachet.ArchiveSync.Api/Program.cs` | modify - removed IReportRepository/ReportRepository DI registration |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | modify - removed Reports:LocalStorageRootPath key |
| `src/Cachet.ArchiveSync.Core/Interfaces/IReportRepository.cs` | delete |
| `src/Cachet.ArchiveSync.Api/Repositories/ReportRepository.cs` | delete |
| `src/Cachet.ArchiveSync.Core/Dtos/PreActionReportResponse.cs` | delete |
| `src/Cachet.ArchiveSync.Core/Dtos/PostActionReportResponse.cs` | delete |
| `src/Cachet.ArchiveSync.Core/Dtos/ReportFileLinks.cs` | delete |
| `src/Cachet.ArchiveSync.Core/Dtos/ReportRow.cs` | delete |
| `Database/migrations/20260922_231500__CAS-015-reporting-on-demand-generation.sql` | add - drops dbo.report/report_ins/report_get |
| `Database/CachetArchiveSync/Entities/dbo.report.sql` | delete (mirror) |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.report_ins.sql` | delete (mirror) |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.report_get.sql` | delete (mirror) |
| `src/Cachet.ArchiveSync.Api.Tests/PreActionReportServiceTests.cs` | modify - rewritten for new constructor/result shape |
| `src/Cachet.ArchiveSync.Api.Tests/PostActionReportServiceTests.cs` | modify - rewritten for new constructor/result shape |
| `src/Cachet.ArchiveSync.Api.Tests/ReportGenerationFailureTests.cs` | modify - V2 tests updated |
| `src/Cachet.ArchiveSync.Api.Tests/ReportsControllerTests.cs` | modify - V7/SC-4/SC-5 tests added |
| `src/Cachet.ArchiveSync.Api.Tests/LogHygieneTests.cs` | modify - extended with SC-3 static check |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/ReportsControllerTests.cs` | modify - end-to-end streamed-file assertions |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/Reporting/ReportsApiWebApplicationFactory.cs` | modify - removed IReportRepository/config fakes |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/ReportRepositoryTests.cs` | delete - tested the deleted repository |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `PreActionReportServiceTests` | `Test_SC1_PreActionPdf_ReturnsParseablePdfInSameResponse` | SC-1 |
| `PreActionReportServiceTests` | `Test_SC2_PreActionXlsx_ReturnsParseableXlsx` | SC-2 |
| `PreActionReportServiceTests` | `Test_V4_SoftDeletedFilesExcludedFromPreActionReport` | V4 |
| `PreActionReportServiceTests` | `Test_V8_NoFileWrittenToDiskAfterResponse` | V8 |
| `PostActionReportServiceTests` | `Test_SC2_PostActionPdfAndXlsx_ReturnParseableBodies` | SC-2 |
| `PostActionReportServiceTests` | `Test_V3_ReadOnlyAndAuthActionsExcludedFromPostActionReport` | V3 |
| `PostActionReportServiceTests` | `Test_V5_FailedActionsAppearInPostActionReport` | V5 |
| `PostActionReportServiceTests` | `Test_V6_AuditLogCapReachedFailsLoudRatherThanTruncating` | V6 |
| `ReportGenerationFailureTests` | `Test_V2_PdfWriteFailureReturnsReportGenerationFailed` | V2 |
| `ReportGenerationFailureTests` | `Test_V2_XlsxWriteFailureReturnsReportGenerationFailed` | V2 |
| `ReportsControllerTests` | `Test_V7_InvalidFormatReturnsValidationError` | V7 |
| `ReportsControllerTests` | `Test_V7_MissingFormatReturnsValidationError` | V7 |
| `ReportsControllerTests` | `Test_SC4_RemovedGetRoutesReturnNotFound` | SC-4 |
| `ReportsControllerTests` | `Test_SC5_AuditRowWrittenBeforeResponseReturned` | SC-5 |
| `LogHygieneTests` | `Test_SC3_NoReportTableOrRepositoryReferencesRemainInReportingModule` | SC-3 |
| `Api.IntegrationTests.ReportsControllerTests` | `Test_SC1_PreActionPdf_ReturnsParseablePdfInSameResponse` | SC-1 (e2e) |
| `Api.IntegrationTests.ReportsControllerTests` | `Test_SC4_RemovedGetRoutesReturnNotFound` | SC-4 (e2e) |

### Migration
`20260922_231500__CAS-015-reporting-on-demand-generation.sql`
