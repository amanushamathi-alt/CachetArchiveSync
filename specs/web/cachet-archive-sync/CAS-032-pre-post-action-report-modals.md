---
id: CAS-032
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-25
priority: P1
related_specs: [CAS-010, CAS-015, CAS-024, CAS-025, CAS-021]
touches:
  database: false
  api: false # consumes CAS-015's merged POST /reports/{pre,post}-action as-is; preview GETs are a prerequisite Api spec (§0.2), not built here
  ui: true
  worker: false # report generation is synchronous (CAS-010 §0.1, CAS-015 §1) — no task-queue involvement
  b2: false # no report data path touches B2 (CAS-015 §11)
  config: false
  security: true # report content includes archive file paths; file bytes transit Web server memory to the browser
migrations: []
---

# CAS-032 — Pre-Action Impact Report and Post-Action Outcome Report Modals

WBS Task 27 (Web UI Development — Pre-Action / Post-Action report screens). Implements the UI half of
**FR-25, FR-26, FR-27** (BRD §7.7). UI contract: `CACHET_UIUX_Design_Specification_v1.md` §8 (Pre-Action,
wireframe-confirmed) and §9 (Post-Action, derived by analogy), as amended by BRD §12.3 item 9
(2026-09-22 — reports are generated on demand and never persisted server-side). API contract:
`CAS-015` (`merged`) for file generation; a **prerequisite preview API spec** for the on-screen table
(§0.2).

---

## §0 Scope Notes (read first)

### §0.1 What "download link" means after BRD §12.3 item 9

The WBS task text says "download links for the generated PDF/XLSX". Under the binding BRD §12.3 item 9
decision there is no stored file to link to. The **Download PDF** and **Download XLSX** buttons each
trigger a fresh on-demand generation call (`POST /reports/{kind}` with `format`), and the returned bytes
are handed straight to the browser as a file download. There is no `href` to a persisted resource and no
report-history screen (UI/UX §8.4, unchanged).

### §0.2 Prerequisite: preview endpoints do not exist yet (verified 2026-09-25)

`CAS-015` §14 (resolved 2026-09-22, owner) chose **option (a)** for the modal's on-screen table:
separate, read-only preview endpoints (`GET /reports/pre-action/preview`,
`GET /reports/post-action/preview`) that re-run the same live aggregation and return `rows[]` as JSON.
`CAS-015` marked them **out of scope** and left them to "the eventual `touches.ui: true` Blazor Reports
screen spec".

**Verified against the current tree:** `src/Cachet.ArchiveSync.Api/Controllers/ReportsController.cs`
exposes only the two `POST` routes, and a grep for `preview` under `src/` finds nothing. The table view
this spec builds therefore has **no data source today**.

**Decomposition decision (Karpathy P9):** the preview endpoints go in a **separate Api-kind spec**, not
in this one. The reasons:
- They need a different test harness (xUnit + `WebApplicationFactory` against the Api) from this spec's
  bUnit component tests.
- They carry their own audit-trail decision (§15 item 1), which is a backend security question.
- WBS Task 27 is scoped to "Web UI Development".

This spec fixes the **consumed contract** in §6.2 so that both specs build to the same shape. The
prerequisite spec is referred to below as **"the preview API spec"**. It has **not been minted yet**; mint
it with `cachet_agent_tools spec next-id` once this file exists, then add its ID to `related_specs`.

~~This spec cannot move to `ready` until the preview API spec is `ready` or later.~~ **Superseded
2026-09-25 by owner direction** (§15 item 1). The Web side is implemented now against the §6.2 contract.
The preview API spec (not yet minted; CAS-033 is taken by task-manager-modal-reachability) follows separately, and the deploy is sequenced per §14.

### §0.3 Placement

The Pre-Action Report and Post-Action Report buttons already exist as **disabled placeholders** in the
per-page `.cachet-actionbar` row of both `KeywordSearch.razor` (lines 79–83) and
`ODriveSyncContentScan.razor` (lines 55–58), added by `CAS-025` §0. `MainLayout.razor` is statically
rendered and cannot host interactive handlers (its top-of-file comment explains why). This spec therefore
**enables those existing per-page buttons in place**. It does not move them into the layout. This is the
same per-page pattern `CAS-024` used for the Quarantine Queue trigger.

---

## §1 Summary

Replaces the two disabled report placeholders on both screens with working triggers. Each trigger opens a
single shared `ReportModal.razor` component in either Pre-Action or Post-Action mode. On open, the modal
shows a generation-in-progress state. It calls the matching preview endpoint (§6.2) and renders:
- the title
- the active-tab subtitle
- a "Generated `<date/time>` by `<user>`" line taken from the server
- a count summary
- a virtualized results table (UI/UX §8.2 / §9.2 column sets)

**Download PDF** and **Download XLSX** each call `CAS-015`'s `POST /reports/pre-action|post-action` with
the chosen `format`. The response bytes stream to the browser through a small JS interop helper, so the
file is saved by the user's browser and nothing is stored on either server (FR-27, BRD §12.3 item 9).
Nothing is emailed (FR-27). Both buttons show their own in-progress state and a scoped error message on
failure.

---

## §2 User Story

As a **TraceCounts reviewer or Cachet business owner** signed into Cachet Archive Sync, I want to open a
Pre-Action Impact Report from any screen, before I delete anything, to see every file currently flagged,
needing review, or informational across search and quarantine. I also want to open a Post-Action Outcome
Report to see what was actually done and when, and to save either one to my machine as a PDF or XLSX.
This gives me a defensible record for review and audit without anything being stored on the server or
sent by email.

---

## §3 In-Scope

- **`Components/Shared/ReportModal.razor`** (+ `.razor.css`): one shared, non-routed component. It is
  instantiated once per host page with `@ref` and opened with `OpenAsync(ReportKind kind)`, following the
  `QuarantineQueueModal.razor` pattern.
- **`ReportKind`** enum (`PreAction`, `PostAction`) in the Web project, used only by this component and
  its hosts.
- **Trigger wiring** on `KeywordSearch.razor` and `ODriveSyncContentScan.razor`: replace the two disabled
  placeholder buttons with enabled buttons that call `OpenAsync`. Each host passes its active-tab label
  (§8.1).
- **Generation-in-progress state** while the preview loads (§8.3).
- **Result view**: header, generated-by line, count summary, and a virtualized table per kind (§8.2).
- **Empty state** per kind (§8.5).
- **Download PDF / Download XLSX**: on-demand generation through `CAS-015`'s POST endpoints, with a
  per-button in-progress state, delivered to the browser via `wwwroot/js/download.js` (§8.4).
- **Error handling**: preview failure shows a banner with Retry. Download failure shows a banner scoped
  to the failed format. `401` shows the existing session-expired pattern (§8.5).
- **Footer disclosure copy**: reports are generated on demand, are not saved on the server, and each
  download reflects data at the moment it is generated (§8.2, §15 item 3).
- Updates to the `CAS-025` placeholder tests that currently assert the report buttons are disabled
  (§13.3).

---

## §4 Out-of-Scope

- **Preview endpoints** `GET /reports/pre-action/preview` and `GET /reports/post-action/preview`. These
  belong to the prerequisite preview API spec (§0.2). This spec consumes them only.
- **Any change to `CAS-015`'s POST contract, aggregation logic, 100,000-row cap, or audit writes.**
  They are consumed exactly as merged.
- **Report filtering** (date range, tab filter, search term). FR-25/FR-26 do not ask for it, and the POST
  endpoints do not accept it. `ActiveTabLabel` is a label, not a filter (UI/UX §8.1).
- **Report history or archive screen**. It is excluded by UI/UX §8.4 and BRD §12.3 item 9.
- **Email delivery of any kind** (FR-27, BRD §4.2).
- **Moving the triggers into a global header component**. Same deferral as `CAS-024` §14 point 4.
- **Extracting the duplicated `ChipClass` status-chip helper** into a shared utility. It would be the
  third copy (§8.2). Extracting it would touch `KeywordSearch.razor` and `QuarantineQueueModal.razor`
  beyond this spec's need, so it is recorded as a non-blocking follow-up (§15 item 5).
- **Web caching layer (BRD §12.4)**. It is not relied on. Every open and every download is a live call.
  The still-open caching decision does not affect this spec.
- **Audit Log screen** (FR-24). It is a separate WBS task; its placeholder button stays disabled.

---

## §5 Database Objects

N/A (`touches.database: false`). Data is read indirectly through the Api only.

---

## §6 API Endpoints (consumed; none added by this spec)

All calls use the existing `"CachetApi"` named `HttpClient` (`CAS-002` §6.6, `Web/Program.cs`), which
forwards the circuit's session cookie. No new client registration is needed (see §15 item 4 for the
timeout note).

### §6.1 `POST /reports/pre-action`, `POST /reports/post-action` — `CAS-015` §8, merged

**Auth:** `CachetAuthenticated`. Available to Read Only sessions as well as Full Access.
**Async:** No. Synchronous generation with no task queue.

**Request** (`Core.Dtos.ReportGenerationRequest`):
```json
{ "activeTabLabel": "Keyword Search", "format": "pdf" }
```

| Code | Body / meaning | UI handling |
|------|----------------|-------------|
| 200 | Binary file. `Content-Type` `application/pdf` or `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`. `Content-Disposition: attachment; filename="{pre\|post}-action-report-{yyyyMMddHHmmss}.{ext}"` | Stream to browser (§8.4) |
| 400 | `validation-failed` ProblemDetails. Only reachable if the client sends a bad `format`, which V2 prevents | Generic download error (§8.5) |
| 401 | No or expired session | Session-expired banner (§8.5) |
| 500 | `report-generation-failed` ProblemDetails. Includes the Post-Action 100,000-row cap case (`CAS-015` V6) | "Report generation failed" download error (§8.5) |

Audit: the Api writes `PreActionReportGenerated` / `PostActionReportGenerated` **before** returning `200`
(`CAS-015` SC-5). **Each Download click therefore creates one audit row.**

### §6.2 `GET /reports/pre-action/preview`, `GET /reports/post-action/preview` — prerequisite preview API spec (NOT YET BUILT)

**Required contract (proposed by this spec, for the preview API spec to adopt or amend):**

**Auth:** `CachetAuthenticated`. **Async:** No.
**Query:** `activeTabLabel` (optional, cosmetic; echoed only).
**Aggregation:** must reuse exactly the same row-building logic as the matching POST. The rules that
apply to Pre-Action search + quarantine union (V4 soft-delete exclusion, no dedup) and to Post-Action
(V3 allow-list, V5 failures included, V6 cap fail-loud) are also in force here. This keeps the table and
the file from being built by two different code paths.

**Response 200 — Pre-Action:**
```json
{
  "generatedAt": "2026-09-25T14:03:11Z",
  "generatedBy": "malvarez",
  "rows": [
    { "fileName": "Q3-ledger.xlsx", "filePath": "/archive/finance/2024/Q3-ledger.xlsx",
      "panel": "Filename Matches", "status": "Flagged" }
  ]
}
```
Rows use `Core.Dtos.PreActionReportRow` (the existing type).

**Response 200 — Post-Action:**
```json
{
  "generatedAt": "2026-09-25T14:03:11Z",
  "generatedBy": "malvarez",
  "rows": [
    { "fileName": "Q3-ledger.xlsx", "filePath": "/archive/finance/2024/Q3-ledger.xlsx",
      "actionTaken": "Quarantined", "sourceTab": "Keyword Search",
      "timestamp": "2026-09-24T09:12:40Z", "result": "Success" }
  ]
}
```
Rows use `Core.Dtos.PostActionReportRow` (the existing type). `fileName` and `filePath` may be `null`
for task-level actions.

| Code | UI handling |
|------|-------------|
| 200 | Render the result view (§8.2) |
| 401 | Session-expired banner (§8.5) |
| 500 | Preview error banner with Retry (§8.5). Includes the Post-Action cap case |

Response DTO names such as `PreActionReportPreview` and `PostActionReportPreview` in
`Cachet.ArchiveSync.Core/Dtos/` are for the preview API spec to decide. This spec binds to whatever it
defines, as long as the fields above are present.

---

## §7 Validations

These are client-side gates only. The Api (`CAS-015` V2–V7) remains the source of truth.

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | Download PDF and Download XLSX are disabled until the preview has loaded **successfully**, including a zero-row success. They stay disabled while the preview is loading and after a preview error. | Buttons disabled; no POST sent | `Test_V1_DownloadButtonsDisabledUntilPreviewSucceeds` |
| V2 | The POST `format` field is only ever the literal `"pdf"` or `"xlsx"`, set from the button clicked and never from user text | No other value can be sent | `Test_V2_DownloadSendsExactFormatLiteral` |
| V3 | At most one download is in flight per modal. While one runs, **both** Download buttons are disabled, which prevents duplicate generation and duplicate audit rows from double-clicks | Additional clicks ignored; no second POST | `Test_V3_SecondDownloadClickIgnoredWhileInFlight` |
| V4 | A non-2xx response or a transport failure from either endpoint shows an inline banner and never an unhandled exception or blank modal (§8.5) | Inline banner | `Test_V4_ServerErrorsRenderInlineBannerNotCrash` |
| V5 | A `200` download response with a missing or unexpected `Content-Type` (anything other than the two types in §6.1 for the requested format) is treated as a failure and is **not** passed to the browser | Download error banner; no JS call | `Test_V5_UnexpectedContentTypeNotDeliveredToBrowser` |
| V6 | A `401` from either endpoint shows the session-expired banner with a Sign-in link (the existing `SessionExpiredMessage` pattern) and sends no automatic retry | Session-expired banner | `Test_V6_UnauthorizedShowsSessionExpiredBanner` |

---

## §8 UI / UX

### §8.1 Triggers

| Host page | Button | Action | `activeTabLabel` passed |
|-----------|--------|--------|-------------------------|
| `KeywordSearch.razor` | Pre-Action Report | `reportModal.OpenAsync(ReportKind.PreAction)` | `"Keyword Search"` |
| `KeywordSearch.razor` | Post-Action Report | `reportModal.OpenAsync(ReportKind.PostAction)` | `"Keyword Search"` |
| `ODriveSyncContentScan.razor` | Pre-Action Report | same | `"O-Drive Sync and content Scan"` (exact tab text in `MainLayout.razor`) |
| `ODriveSyncContentScan.razor` | Post-Action Report | same | same |

- The buttons keep their existing position in `.cachet-actionbar` and change from
  `cachet-header-btn-disabled` to the standard `cachet-btn` style. The `disabled` attribute and
  `title="Coming soon"` are removed.
- The buttons are enabled for every authenticated session. There is no Full Access gating, because
  generation is read and aggregate only (`CAS-010` §12, `CAS-015` §8).
- Each host declares `<ReportModal @ref="reportModal" ActiveTabLabel="..." />` next to its existing
  `<QuarantineQueueModal>`.
- The label is a component parameter, not an argument to `OpenAsync`. It is fixed per host.

### §8.2 Result view layout (UI/UX §8.2 / §9.2)

Structure follows `QuarantineQueueModal.razor`: `.cachet-modal-backdrop` > `.cachet-modal`
(`role="dialog" aria-modal="true" aria-labelledby`), with header, body and footer.

**Header**
- Title: **"PRE-ACTION IMPACT REPORT"** or **"POST-ACTION OUTCOME REPORT"** (UI/UX literal).
- Subtitle: `ActiveTabLabel`.
- The × close button uses `aria-label="Close report"`.

**Meta line**
- Text: "Generated `<generatedAt>` by `<generatedBy>`." Both values come from the preview response, never
  from the client clock or claims.
- Timestamps are shown in **UTC**, formatted `yyyy-MM-dd HH:mm:ss 'UTC'`. This matches the server's `u`
  format in the rendered file (`PostActionReportService`), so the screen and the file agree.

**Summary strip**
- Counts are computed client-side from `rows` only. No additional data is fetched.
- Pre-Action: total rows, then the count per `panel` (Filename Matches / Content Matches / Quarantine)
  and the count per `status`.
- Post-Action: total rows, then the count per `actionTaken` and the count per `result`
  (Success / Failure).

**Table**
- Rows are rendered through `<Virtualize Items="rows">`. `Virtualize` is already imported in
  `_Imports.razor`. Rows can approach the 100,000 cap, and non-virtualized rendering at that size over
  SignalR is not viable.

| Pre-Action columns (UI/UX §8.2) | Binding |
|---|---|
| File | Two-line cell: `fileName` over `filePath` (`cachet-cell-name` / `cachet-cell-path`, with `title` attributes as in the Quarantine modal) |
| Panel | `panel` |
| Status | `status`, shown as a chip (the `ChipClass` mapping duplicated locally; §4) |

| Post-Action columns (UI/UX §9.2) | Binding |
|---|---|
| File | Two-line `fileName` / `filePath`. Shows **"—"** when both are null (task-level action) |
| Action Taken | `actionTaken` |
| Source Tab | `sourceTab` |
| Timestamp | `timestamp`, UTC, same format as the meta line |
| Result | `result`. Failure rows get a visual chip. Failures are always shown, never hidden (`CAS-015` V5) |

**Footer**
- Disclosure note (§15 item 3 for wording): *"Generated on demand as PDF and XLSX — not saved on the
  server. Each download is generated fresh and reflects the data at the moment it is generated."*
- Buttons: **Download PDF**, **Download XLSX**, **Close**.

### §8.3 Generation-in-progress state (preview)

1. `OpenAsync(kind)` does the following:
   - sets `isOpen = true`
   - clears prior rows, errors and download state
   - sets `isLoading = true`
   - calls `StateHasChanged()` so the modal appears **immediately**
   - then issues the preview GET
2. While loading:
   - the body shows *"Generating report… This may take a moment for large reports."*
   - the body region has `aria-busy="true"` and the status line has `aria-live="polite"`
   - Download buttons are disabled (V1); Close stays enabled
   - no fixed client-side timeout is imposed beyond the HttpClient's own (`CAS-015` §14 performance
     resolution; §15 item 4)
3. On `200`, render §8.2. On failure, show §8.5.
4. Closing the modal during a preview load abandons the result: the component ignores a response that
   arrives after `Close`, or after a newer `OpenAsync`, using a per-open generation counter.
5. Reopening always starts a fresh load. Nothing is cached between opens. This is required by BRD §12.3
   item 9 and the live-read model, and stays in place while BRD §12.4 is open.

### §8.4 Download flow (FR-27)

1. Click **Download PDF** or **Download XLSX**. V1 and V3 must pass.
2. That button's label changes to *"Generating PDF…"* or *"Generating XLSX…"* with a spinner, and both
   Download buttons are disabled (V3). Close stays enabled.
3. `POST reports/pre-action` or `reports/post-action`, with body
   `{ activeTabLabel: ActiveTabLabel, format: "pdf"|"xlsx" }` and
   `HttpCompletionOption.ResponseHeadersRead`.
4. On `200` with the expected content type (V5):
   - read the filename from `Content-Disposition` (`FileNameStar ?? FileName`, trimmed of quotes)
   - if the filename is absent, fall back to `{pre|post}-action-report.{ext}`
   - wrap the response stream in a `DotNetStreamReference`
   - call `JS.InvokeVoidAsync("cachetDownload.fromStream", fileName, streamRef)`
5. **`wwwroot/js/download.js`** (new, about 15 lines, included in `App.razor` next to `panels.js`)
   defines `window.cachetDownload.fromStream(fileName, streamRef)`. It:
   - calls `streamRef.arrayBuffer()`
   - builds a `Blob`, calls `URL.createObjectURL`, creates a temporary `<a download>`, clicks it and
     removes it
   - calls `URL.revokeObjectURL` immediately afterwards

   This is Microsoft's documented Blazor Server file-download pattern. No file is written on the Web
   server. The bytes exist only in the response stream and the browser Blob.
6. The button returns to its normal state. No success banner is shown; the browser's own download UI is
   the confirmation. The on-screen table is **not** refreshed by a download.
7. **Close during an in-flight download:** the modal closes and the request continues. If it succeeds,
   the file is still delivered, because the Api has already written the audit row, and delivering the
   file keeps the audit record and the user's copy consistent. The request is cancelled only when the
   host component is disposed (navigation or circuit end), through a component-scoped
   `CancellationTokenSource`. If that happens after the Api wrote its audit row, the row stands and no
   file is delivered. This is accepted, because the row records that generation occurred, not that the
   file was received (§12).

**Why JS stream interop instead of a Web-hosted download endpoint with a plain `<a href>`:**
- A native link cannot report completion back to the component, so the required per-button
  in-progress state (§8.4 step 2) would be impossible.
- The target POST writes an audit row, so exposing it as a GET link would make a state-changing GET.
- The trade-off: the file bytes pass through the SignalR circuit, sent in chunks by
  `DotNetStreamReference`. This is acceptable for the expected infrequent, low-concurrency usage
  (`CAS-015` §14).

### §8.5 Empty, error and session states

| State | Copy and behavior |
|---|---|
| Pre-Action, zero rows | *"No files are currently flagged, needing review, or informational across search and quarantine."* Downloads **enabled**, because an empty report is still a valid FR-25 record |
| Post-Action, zero rows | *"No actions have been recorded yet."* Downloads enabled |
| Preview fails (non-2xx other than 401, or a transport error) | Modal banner *"Something went wrong generating this report. Please try again."* plus a **Retry** button that re-runs §8.3. Downloads disabled (V1) |
| Download returns `500 report-generation-failed` | Banner *"Report generation failed. Please try again."* The table stays visible and the buttons re-enable |
| Download fails in any other way (400, other non-2xx, transport error, V5 content-type mismatch, JS interop exception) | Banner *"The {PDF\|XLSX} download failed. Please try again."* The table stays visible |
| `401` from either call | The existing `SessionExpiredMessage` banner with a `<a href="login">Sign in</a>` link (as in `KeywordSearch.razor`). Downloads disabled |

Raw ProblemDetails text is never shown. The status code and endpoint are logged (§11).

---

## §9 Configuration Changes

None. The existing `"CachetApi"` client and `Api:BaseUrl` are reused. §15 item 4 covers whether a
report-specific timeout key is needed later.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | On both host pages, the Pre-Action and Post-Action buttons are enabled, and clicking each one opens `ReportModal` in the matching mode with the host's tab label as subtitle | `Test_SC1_TriggersOnBothPagesOpenModalInCorrectMode` |
| SC-2 | The modal renders immediately in the in-progress state (copy, `aria-busy`, downloads disabled) **before** the preview response arrives | `Test_SC2_InProgressStateShownBeforePreviewCompletes` |
| SC-3 | Pre-Action success renders the title, the subtitle, "Generated … by …" from the response (UTC format), a correct per-panel and per-status summary, and File/Panel/Status columns for every row | `Test_SC3_PreActionPreviewRendersHeaderSummaryAndColumns` |
| SC-4 | Post-Action success renders File/Action Taken/Source Tab/Timestamp/Result for every row, including `Failure` rows, and shows "—" for null file fields | `Test_SC4_PostActionPreviewRendersColumnsFailuresAndNullFiles` |
| SC-5 | Download PDF sends exactly one `POST reports/{kind}` with `format:"pdf"` and the host's `activeTabLabel`, then invokes `cachetDownload.fromStream` once with the `Content-Disposition` filename; the XLSX path does the same with `format:"xlsx"` | `Test_SC5_DownloadPostsFormatAndDeliversStreamWithServerFilename` |
| SC-6 | While a download is in flight, that button shows "Generating {FORMAT}…" and both download buttons are disabled; they re-enable on completion | `Test_SC6_DownloadInProgressStateAndReenable` |
| SC-7 | A zero-row preview shows the kind-specific empty-state copy, with downloads enabled | `Test_SC7_EmptyReportShowsCopyAndAllowsDownload` |
| SC-8 | A preview failure shows the banner and Retry, and Retry re-issues the preview GET; a download `500` shows the generation-failed banner while keeping the table | `Test_SC8_PreviewRetryAndDownloadFailureStates` |
| SC-9 | A response for a stale open (after Close, or after a newer `OpenAsync`) never overwrites the current modal state | `Test_SC9_StalePreviewResponseIgnored` |
| SC-10 | The Web project writes no report file to disk and adds no server-side cache of report bytes or rows; each open and each download is a fresh Api call | `Test_SC10_NoServerSidePersistenceOrCachingOfReports` |
| SC-11 | No session cookie value, B2 key, or report row content (file names or paths) appears in Web log output from this component; only kind, format, row count, byte length, status code and endpoint are logged | `Test_SC11_NoSensitiveDataInReportModalLogs` |
| SC-12 | A Read Only session can open both reports and download both formats (no Full Access gating) | `Test_SC12_ReadOnlySessionCanViewAndDownloadReports` |

The test for V1–V6 is listed in §7. The count stays within the P9 thresholds (≤ 12 SCs).

---

## §11 Telemetry

```csharp
Logger.LogInformation("ReportModal: opened {ReportKind} from {ActiveTab}", kind, ActiveTabLabel);
Logger.LogInformation("ReportModal: preview loaded {ReportKind}, RowCount={RowCount}, ElapsedMs={ElapsedMs}", kind, rows.Count, ms);
Logger.LogInformation("ReportModal: download delivered {ReportKind} {Format}, Bytes={Bytes}, ElapsedMs={ElapsedMs}", kind, format, bytes, ms);
Logger.LogWarning("ReportModal: unexpected {StatusCode} from {Endpoint}", (int)status, endpoint);
Logger.LogWarning(ex, "ReportModal: unable to reach Api for {Endpoint}", endpoint);
```

- **Never log** any of the following:
  - row contents (file names or paths): they already live in `dbo.audit_log` / `dbo.search_result`, and
    duplicating them into Web logs widens the data footprint
  - the `Content-Disposition` header
  - cookie values
  - the B2 key or key ID
  - ProblemDetails bodies
- `ElapsedMs` gives an evidence base for §15 item 4 (the timeout question).

---

## §12 Security Notes

### B2 Credential Handling
Not applicable by construction. The component calls only the Reporting endpoints, whose DTOs carry no key
material (`CAS-015` §11). No B2 client or claim beyond the existing `b2_capability` label is used, and
this spec does not read that claim at all.

### Audit Trail (FR-23)
- **Downloads:** the Api writes `PreActionReportGenerated` / `PostActionReportGenerated` before returning
  `200` (`CAS-015` SC-5). The Web layer adds no audit write. V3 prevents double-click duplicate rows. An
  audit row can exist for a download the user never received (§8.4 step 7). This is accepted, because the
  row records that generation occurred and who requested it.
- **Preview (opening the modal):** whether it is audited is **undecided** and belongs to the preview API
  spec (§15 item 1). This spec's UI behaves the same either way.

### Session / Auth Requirements
- Both host pages already carry `[Authorize(Policy = "CachetAuthenticated")]`, which `AuthorizeRouteView`
  enforces.
- The modal is not routable.
- The Api enforces `CachetAuthenticated` on every call independently.
- No role gating is applied (SC-12).

### Data Handling (data minimization, SOC 2 / HIPAA-aligned posture)
- Report content (archive file paths, statuses, actions, usernames) is held **only** in component memory
  for the life of the open modal. Rows are cleared on Close.
- File bytes are streamed and never buffered to disk, temp files or `IMemoryCache` on the Web server
  (SC-10).
- The browser-side Blob URL is revoked immediately after the download is triggered.
- The saved file on the user's machine is outside the application's control. Handling of that copy is an
  operational or policy matter for the client and is noted here for audit transparency.
- **Screen vs. file consistency:** the table and each downloaded file are separate live reads. If
  underlying data changes between them, they can differ. This is inherent to the owner-approved model
  ("every read, preview or download, is drawn fresh", `CAS-015` §14). The footer copy discloses it, and
  each file carries its own generated timestamp.

---

## §13 Test Plan

The harness is bUnit + FluentAssertions + NSubstitute, as used in `Cachet.ArchiveSync.Web.Tests`.
`HttpClient` is faked with a new `Reports/FakeReportsApiHandler.cs`, which mirrors
`FakeQuarantineQueueApiHandler` (unconfigured routes fail loudly with 502). JS interop uses bUnit's
`JSInterop` in strict mode, with `cachetDownload.fromStream` set up as a planned void invocation.

### §13.1 Unit tests — `Reports/ReportModalTests.cs` (bUnit)

| Method | Proves |
|--------|--------|
| `Test_V1_DownloadButtonsDisabledUntilPreviewSucceeds` | V1 |
| `Test_V2_DownloadSendsExactFormatLiteral` | V2 |
| `Test_V3_SecondDownloadClickIgnoredWhileInFlight` | V3 |
| `Test_V4_ServerErrorsRenderInlineBannerNotCrash` | V4 |
| `Test_V5_UnexpectedContentTypeNotDeliveredToBrowser` | V5 |
| `Test_V6_UnauthorizedShowsSessionExpiredBanner` | V6 |
| `Test_SC2_InProgressStateShownBeforePreviewCompletes` (fake handler holds on a `TaskCompletionSource`) | SC-2 |
| `Test_SC3_PreActionPreviewRendersHeaderSummaryAndColumns` | SC-3 |
| `Test_SC4_PostActionPreviewRendersColumnsFailuresAndNullFiles` | SC-4 |
| `Test_SC5_DownloadPostsFormatAndDeliversStreamWithServerFilename` | SC-5 |
| `Test_SC6_DownloadInProgressStateAndReenable` | SC-6 |
| `Test_SC7_EmptyReportShowsCopyAndAllowsDownload` | SC-7 |
| `Test_SC8_PreviewRetryAndDownloadFailureStates` | SC-8 |
| `Test_SC9_StalePreviewResponseIgnored` | SC-9 |
| `Test_SC10_NoServerSidePersistenceOrCachingOfReports`: asserts a second open issues a second GET and a second download issues a second POST. A static check asserts that no `System.IO.File`/`FileStream` write and no `IMemoryCache`/`IDistributedCache` reference exist in `ReportModal` | SC-10 |
| `Test_SC11_NoSensitiveDataInReportModalLogs` (test log sink; fixture rows use a distinctive path marker) | SC-11 |
| `Test_SC12_ReadOnlySessionCanViewAndDownloadReports` | SC-12 |

### §13.2 Host-page tests (bUnit, extended)

| Class | Method | Proves |
|-------|--------|--------|
| `KeywordSearch` page tests | `Test_SC1_TriggersOnBothPagesOpenModalInCorrectMode` (KS host) | SC-1 |
| `ODriveSync` page tests | `Test_SC1_TriggersOnBothPagesOpenModalInCorrectMode` (O-Drive host) | SC-1 |

### §13.3 Existing tests to update (not new behavior)

- `CAS-025`'s `Test_V2_PlaceholderHeaderButtonsAreDisabledWithNoHandler` and
  `Test_SC5_PlaceholderButtonsDisabledAndInert` currently assert the report buttons are disabled. Narrow
  them to the **Audit Log** placeholder only. Do not delete them.

### §13.4 JS helper
`download.js` is about 15 lines of browser-API glue with no branching. It has no JS test harness in this
repo, so it is verified by the reviewer's manual check (§18) and exercised through the bUnit invocation
assertion in SC-5.

### Coverage Target
Changed or added `.razor`/`.cs` files must reach at least 85% line coverage.

---

## §14 Rollout

1. **Prerequisite:** the preview API spec is implemented and deployed to Api first (§0.2). Deploying Web
   without it makes every modal open show the §8.5 preview-error state. That fails safely, but the feature
   is unusable.
2. No database migration.
3. Deploy `Cachet.ArchiveSync.Web`. There are no Core changes from this spec, beyond any DTOs the preview
   API spec adds, which ship with that spec.
4. **Rollback:** revert `ReportModal.razor` (+ `.css`), `ReportKind.cs`, `download.js`, the `App.razor`
   script tag, and the two host-page button changes. This restores the `CAS-025` placeholders. No data or
   schema needs to be unwound. Files already downloaded to user machines are unaffected.

---

## §15 Open Questions

- [x] **1. Resolved 2026-09-25 (owner direction: "make it ready and implement") — the Web side proceeds
      ahead of the preview API spec.** The following conditions apply:
      - (a) The implementation binds to the §6.2 contract exactly.
      - (b) Because the Core preview DTOs do not exist yet, the component deserializes the preview
        responses into **component-private records**. Those records reuse the existing
        `Core.Dtos.PreActionReportRow` / `PostActionReportRow` for rows. There is no Core change. The
        component switches to the preview API spec's DTOs when that spec lands.
      - (c) Until the preview API spec is deployed, opening a report shows the §8.5 preview-error state. Downloads stay
        disabled (V1), so the feature is not usable in production until then (§14 step 1).
      - (d) The audit-on-preview question below moves unchanged to the preview API spec, and this UI is unaffected
        either way.

      Original text of the item follows.
      **BLOCKING — preview API spec not yet written (§0.2).** Mint it and draft it as an Api-kind spec
      covering `GET /reports/{pre,post}-action/preview` to the §6.2 contract. Its owner must also decide
      **whether opening a preview writes an audit row**. The trade-off:
      - FR-23's list (search, match, quarantine, delete) does not name report viewing, and the preview is
        read-only, which argues for no row.
      - A preview does expose the same aggregated file-path data as a download, and the download is
        audited, which argues for a row.
      - **Recommendation:** audit it as `PreActionReportViewed` / `PostActionReportViewed` for a complete
        access trail. This needs owner sign-off because it adds `AuditAction` members.
- [x] **2. Download delivery mechanism — decided in this spec (§8.4):** JS stream interop
      (`DotNetStreamReference`) rather than a Web-hosted GET download link. The rationale is recorded in
      §8.4. The owner can override before `ready`.
- [ ] **3. Non-blocking — footer copy wording.** UI/UX §8.2 marks its revised footer as "pending final
      wording". This spec uses: *"Generated on demand as PDF and XLSX — not saved on the server. Each
      download is generated fresh and reflects the data at the moment it is generated."* It drops the
      library names, which are implementation detail and not user-facing. The owner should confirm or
      supply exact copy. The default ships as written.
- [ ] **4. Non-blocking — HttpClient timeout.** The shared `"CachetApi"` client uses .NET's default
      100-second `Timeout`. `CAS-015` §14 set no response-time ceiling, and a report near the 100,000-row
      cap could exceed 100 s. **Default:** keep the existing client unchanged. A timeout surfaces as the
      §8.5 generic download or preview error. The `ElapsedMs` telemetry (§11) provides evidence. If
      measured generation times approach the limit, a follow-up change spec adds a dedicated named client
      with a configurable timeout, rather than raising the timeout for every Api call.
- [ ] **5. Non-blocking follow-up — `ChipClass` duplication.** This would be the third local copy
      (`KeywordSearch.razor`, `QuarantineQueueModal.razor`, `ReportModal.razor`). The Karpathy P4
      threshold is now met, but extraction touches files outside this spec's need (P3). Propose a small
      follow-up refactor spec.
- Not dependent on BRD §12.4 (web caching layer, O-Drive refresh cadence). Every read is live (§8.3
  step 5).

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code for R. Devarajan (TraceCounts) | 2026-09-25 | Drafted per WBS Task 27. §15 item 1 resolved by owner direction 2026-09-25; implemented, gates green |
| Reviewer | Independent reviewer agent | 2026-09-25 | APPROVED WITH NOTES — see `CAS-032.review.md` |

---

## §17 Graph Impact

### God Nodes in Blast Radius
None. `ReportModal` is a new leaf component. The two host pages are the only edges into it. `ReportsController`
and the two report services are single-consumer, low-edge nodes (`CAS-015` §4).

### Communities / Modules Affected
- **Reporting:** consumer only. No Api change from this spec.
- **Web project only:**
  - `Components/Shared/ReportModal.razor` (+ `.css`) and `ReportKind.cs`: new
  - `wwwroot/js/download.js`: new
  - `Components/App.razor`: script tag added
  - `Components/Pages/KeywordSearch.razor` and `ODriveSyncContentScan.razor`: two buttons each, plus the
    modal instance
  - `Web.Tests`: new `Reports/` folder and the §13.3 test updates
- **Api / Core:** only through the prerequisite preview API spec (§0.2). None from this spec.
- **Worker:** none.

The estimated number of changed files is about 10, within the P9 threshold.

### Surprising Connections
- **The preview endpoints the owner approved in `CAS-015` §14 were never built.** The UI table has no data
  source until the preview API spec lands (§0.2, §15 item 1).
- **`CAS-025` tests pin the report buttons as disabled.** They must be narrowed, not deleted (§13.3).
- **The default 100-second `HttpClient` timeout** conflicts with `CAS-015`'s "no fixed timeout
  assumption" (§15 item 4).
- **Screen and file are independent live reads** and can diverge (§12).

### Graphify Queries Run
```
python -m graphify query "Pre-Action Post-Action report modal download PDF XLSX Blazor" --budget 1500
```
The result was 6 nodes around `PdfSharpReportWriter` / the Reporting skill doc. Node source paths point
at `C:/Work/Sourcecode/CACHET-archive-file-sync/...`, a different checkout, and no `.razor` nodes were
returned. **The graph is stale for this host** (consistent with `CAS-024` §17's "no `.razor` nodes"
finding). The analysis was therefore completed by a manual read of:
- `ReportsController.cs`
- `PreActionReportService.cs` / `PostActionReportService.cs`
- the report DTOs in `Core/Dtos`
- `Web/Program.cs`, `MainLayout.razor`, `App.razor`, `_Imports.razor`
- `KeywordSearch.razor` / `ODriveSyncContentScan.razor` (the action bar, session-expired pattern and
  modal hosting)
- `QuarantineQueueModal.razor` and `FakeQuarantineQueueApiHandler.cs`
- `CAS-015` (in full), `CAS-024`, `CAS-025` (report-button references)
- UI/UX §1.1, §8, §9
- BRD §7.7 and §12.3 item 9

Refresh the graph on this host with `graphify update .` after implementation.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 SCs and §7 Vs have passing tests
- [ ] The preview API spec (§0.2) is merged and its contract matches §6.2, or §6.2 was amended to match
      it
- [ ] Both host pages' report buttons are enabled (the Audit Log button's state is owned by CAS-031, which has since made it live)
- [ ] `download.js` revokes the object URL and is loaded in `App.razor`; a manual download of each format
      from each report works in a browser, and the file opens
- [ ] No disk write or server-side cache of report data in Web (SC-10); no row content in logs (SC-11)
- [ ] No Api, Core or Worker file was changed by this spec's diff
- [ ] No B2 key or cookie value in any rendered markup
- [ ] No email or report-history affordance was added (§4)

### Verification Report Format
The reviewer writes `CAS-032.review.md` alongside this spec. It contains:
- a verdict
- a per-SC table
- security gaps
- scope violations
- confirmation that §15 item 1 was resolved by the owner and not assumed by the implementer

## Implementation Log

**Completed:** 2026-09-25
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 95.0% ReportModal.razor; 95.7% KeywordSearch.razor; 95.3% ODriveSyncContentScan.razor | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/ReportKind.cs` | new - PreAction/PostAction enum |
| `src/Cachet.ArchiveSync.Web/Components/Shared/ReportModal.razor` | new - shared report modal (preview, in-progress, table/summary, PDF/XLSX download) |
| `src/Cachet.ArchiveSync.Web/Components/Shared/ReportModal.razor.css` | new - scoped styles |
| `src/Cachet.ArchiveSync.Web/wwwroot/js/download.js` | new - cachetDownload.fromStream Blob download helper |
| `src/Cachet.ArchiveSync.Web/Components/App.razor` | modify - download.js script tag |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | modify - report buttons enabled, ReportModal instance |
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | modify - report buttons enabled, ReportModal instance |
| `src/Cachet.ArchiveSync.Web.Tests/Reports/FakeReportsApiHandler.cs` | new - fake Api handler |
| `src/Cachet.ArchiveSync.Web.Tests/Reports/ReportModalTests.cs` | new - bUnit tests |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchPageTests.cs` | modify - SC-1 test |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncPageTests.cs` | modify - SC-1 test |
| `src/Cachet.ArchiveSync.Web.Tests/Layout/MainLayoutTests.cs` | modify - §13.3 report labels removed; OUT-OF-SCOPE owner-approved removal of stale Test_DisposeAsyncDuringActivePollDoesNotThrow (blocked compilation) |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `KeywordSearchPageTests / ODriveSyncPageTests` | `Test_SC1_TriggersOnBothPagesOpenModalInCorrectMode` | SC-1 |
| `ReportModalTests` | `Test_SC2_InProgressStateShownBeforePreviewCompletes` | SC-2 |
| `ReportModalTests` | `Test_SC3_PreActionPreviewRendersHeaderSummaryAndColumns` | SC-3 |
| `ReportModalTests` | `Test_SC4_PostActionPreviewRendersColumnsFailuresAndNullFiles` | SC-4 |
| `ReportModalTests` | `Test_SC5_DownloadPostsFormatAndDeliversStreamWithServerFilename` | SC-5 |
| `ReportModalTests` | `Test_SC6_DownloadInProgressStateAndReenable` | SC-6 |
| `ReportModalTests` | `Test_SC7_EmptyReportShowsCopyAndAllowsDownload` | SC-7 |
| `ReportModalTests` | `Test_SC8_PreviewRetryAndDownloadFailureStates` | SC-8 |
| `ReportModalTests` | `Test_SC9_StalePreviewResponseIgnored` | SC-9 |
| `ReportModalTests` | `Test_SC10_NoServerSidePersistenceOrCachingOfReports` | SC-10 |
| `ReportModalTests` | `Test_SC11_NoSensitiveDataInReportModalLogs` | SC-11 |
| `ReportModalTests` | `Test_SC12_ReadOnlySessionCanViewAndDownloadReports` | SC-12 |
| `ReportModalTests` | `Test_V1_DownloadButtonsDisabledUntilPreviewSucceeds` | V1 |
| `ReportModalTests` | `Test_V2_DownloadSendsExactFormatLiteral` | V2 |
| `ReportModalTests` | `Test_V3_SecondDownloadClickIgnoredWhileInFlight` | V3 |
| `ReportModalTests` | `Test_V4_ServerErrorsRenderInlineBannerNotCrash` | V4 |
| `ReportModalTests` | `Test_V5_UnexpectedContentTypeNotDeliveredToBrowser` | V5 |
| `ReportModalTests` | `Test_V6_UnauthorizedShowsSessionExpiredBanner` | V6 |
