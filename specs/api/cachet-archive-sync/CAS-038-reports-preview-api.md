---
id: CAS-038
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-30
priority: P1
related_specs: [CAS-010, CAS-015, CAS-032]
touches:
  database: false
  api: true
  ui: false
  worker: false
  b2: false
  config: false
  security: true
migrations: []
---

# CAS-038 — Pre-/Post-Action Report Preview API

## §1 Summary

Adds the two read-only preview endpoints — `GET /reports/pre-action/preview` and
`GET /reports/post-action/preview` — that back the on-screen modal table for the Pre-Action Impact
Report (FR-25) and Post-Action Outcome Report (FR-26). These endpoints were approved in `CAS-015`
§14 (owner-resolved 2026-09-22) but explicitly marked out of scope there, deferred to "the eventual
`touches.ui: true` Blazor Reports screen spec" — that spec, `CAS-032`, was implemented and deployed
against this exact contract (§6.2) but the prerequisite endpoints were never built, which is a
confirmed, currently-reproducing defect: opening either report modal in the deployed Web app 404s
against `GET /reports/{pre,post}-action/preview` and shows the "Something went wrong generating this
report" banner on every open, for every user, unconditionally (verified 2026-09-30 — no such route
exists in `ReportsController` or anywhere else in the Api project; `endpoints snapshot` confirms only
the two `POST` routes exist).

This spec closes that gap: same live-aggregation rules as the matching `POST` endpoint (`CAS-015`),
returned as JSON `rows[]` instead of a rendered file, so `CAS-032`'s `ReportModal.razor` can render
its table without any Web-side change.

---

## §2 User Story

As a **Cachet user with report access**, I want **the Pre-Action/Post-Action report modal to show me
the current rows on-screen before I download a file**, so that **I can review what a report contains
without generating and downloading a PDF/XLSX just to see it**.

---

## §3 In-Scope

- `GET /reports/pre-action/preview` — reuses `PreActionReportService`'s exact row-aggregation logic
  (active `dbo.search_result` rows + `Active` `dbo.quarantine_entry` rows, panel mapping, no dedup).
- `GET /reports/post-action/preview` — reuses `PostActionReportService`'s exact row-aggregation logic
  (`IAuditLogRepository.ListAsync` read, §6.2/CAS-015 V3 allow-list filter, V6 100,000-row cap
  fail-loud).
- New Core DTOs: `PreActionReportPreview` / `PostActionReportPreview` response shapes, matching
  `CAS-032` §6.2 exactly (`generatedAt`, `generatedBy`, `rows[]`).
- Refactor: extract the row-building step out of `PreActionReportService.GenerateAsync` /
  `PostActionReportService.GenerateAsync` into a shared private method each service also uses for its
  new preview method, so the table and the downloaded file are provably built from one code path
  (`CAS-032` §6.2's explicit requirement).
- `activeTabLabel` optional query parameter — cosmetic only, echoed nowhere in the response body
  (it only ever affected the rendered document title, which previews don't have) but accepted as a
  no-op for URL compatibility with what `CAS-032`'s Web code already sends.
- `CachetAuthenticated` authorization policy on both routes (same policy as the existing `POST`
  endpoints).
- No audit log write on preview (see §12).

---

## §4 Out-of-Scope

- Any change to `CAS-015`'s `POST /reports/pre-action` / `POST /reports/post-action` contract,
  rendering, or audit-write behavior.
- Any change to `CAS-032`'s `ReportModal.razor` or other Web code — it already targets this exact
  contract and needs no change once these routes exist.
- Report history, persistence, or a `reportId` of any kind — `CAS-015` §2 already established that
  nothing survives past a single response; this spec does not reintroduce persistence.
- Performance/SLA work for the 100,000-row cap case beyond the existing fail-loud behavior (`CAS-015`
  §14 explicitly set no response-time ceiling — carried forward unchanged).
- A response-time ceiling for the preview call itself — `CAS-032` §15 item 3 raised the Web
  `HttpClient`'s 100-second default timeout as a separate, Web-side concern; not addressed here.

---

## §5 Database Objects

N/A — no new tables, columns, or stored procedures. Reuses the same repositories
(`ISearchResultRepository`, `IQuarantineEntryRepository`, `IAuditLogRepository`) the existing `POST`
endpoints already call.

---

## §6 API Endpoints

### Endpoint: `GET /reports/pre-action/preview`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]` (matches `POST /reports/pre-action`).
**Async:** No.

**Query:** `activeTabLabel` (optional string, cosmetic only — accepted and ignored beyond input-length
sanity, never used to filter rows).

**Response 200:**
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

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Success — includes the zero-row case (`rows: []`) |
| 401 | Missing or invalid session |

---

### Endpoint: `GET /reports/post-action/preview`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]` (matches `POST /reports/post-action`).
**Async:** No.

**Query:** `activeTabLabel` (optional string, cosmetic only, same as above).

**Response 200:**
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
`fileName`/`filePath` may be `null` for task-level actions (no single associated file), matching
`PostActionReportRow`'s existing nullability.

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Success — includes the zero-row case |
| 401 | Missing or invalid session |
| 500 | `report-generation-failed` `ProblemDetails` — the audit-log read hit the internal 100,000-row cap exactly (ambiguous truncation, `CAS-015` V6), same fail-loud rule as the `POST` endpoint |

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | Both routes require `CachetAuthenticated`; an unauthenticated/invalid-session caller gets `401`, never rows | 401, empty body | `Test_V1_PreActionPreview_Unauthenticated_Returns401`, `Test_V1_PostActionPreview_Unauthenticated_Returns401` |
| V2 | Pre-Action preview rows come from the exact same aggregation as `PreActionReportService.GenerateAsync` (active search rows + active quarantine rows, panel mapping, no dedup) — proven by both code paths calling one shared private method, not by independent duplicate logic | Row-for-row match on a shared fixture | `Test_V2_PreActionPreviewRowsMatchGenerateAsyncRows` |
| V3 | Post-Action preview rows are restricted to the §6.2 state-changing `AuditAction` allow-list (same as `CAS-015` V3), and failed rows (`Result = "Failure"`) are included, never dropped (`CAS-015` V5) | Rows filtered/included per allow-list | `Test_V3_PostActionPreviewAppliesAllowListAndIncludesFailures` |
| V4 | Pre-Action preview excludes soft-deleted (`is_active = 0`) files, matching `CAS-015` V4 | Excluded from `rows[]` | `Test_V4_PreActionPreviewExcludesSoftDeletedFiles` |
| V5 | If the Post-Action preview's underlying audit-log read hits the 100,000-row cap exactly, return `500 report-generation-failed` rather than silently truncating, matching `CAS-015` V6 | 500 `report-generation-failed` | `Test_V5_PostActionPreviewCapReachedFailsLoud` |
| V6 | `activeTabLabel` is accepted but never filters `rows[]` or appears in the response body — cosmetic only, per `CAS-032` §6.2 | Rows unaffected by the query value | `Test_V6_ActiveTabLabelIsCosmeticOnlyForBothPreviews` |

---

## §8 UI / UX

N/A — `CAS-032`'s `ReportModal.razor` already implements the calling code against this exact
contract (`src/Cachet.ArchiveSync.Web/Components/Shared/ReportModal.razor`, `LoadPreviewAsync`). No
Web changes are made or needed by this spec.

---

## §9 Configuration Changes

N/A — no new configuration keys.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `GET /reports/pre-action/preview` returns `200` with `generatedAt`, `generatedBy`, and `rows[]` matching `CAS-032` §6.2's shape, including the zero-row case | `Test_SC1_PreActionPreview_ReturnsExpectedShape` |
| SC-2 | `GET /reports/post-action/preview` returns `200` with the analogous Post-Action shape, including `null` `fileName`/`filePath` for task-level rows | `Test_SC2_PostActionPreview_ReturnsExpectedShape` |
| SC-3 | Both preview endpoints and their matching `POST` endpoint produce identical `rows[]` content for the same underlying data — proven by a shared row-building method, not parallel logic | `Test_SC3_PreviewAndGenerateAsyncProduceIdenticalRows` (one test per report kind) |
| SC-4 | Neither preview endpoint writes an `AuditLogEntry` row (§12 decision: preview is not an audited action) | `Test_SC4_PreviewEndpointsWriteNoAuditRow` |
| SC-5 | `CAS-032`'s `ReportModal.razor`, unmodified, successfully renders both report tables end-to-end against these endpoints (manual/integration smoke check) | `Test_SC5_ReportModalRendersAgainstLivePreviewEndpoints` (Web integration test using the real Api routes, not the existing `FakeReportsApiHandler`) |

---

## §11 Telemetry

```csharp
_logger.LogInformation("ReportPreview: {ReportType} preview returned {RowCount} rows for {UserId}", reportType, rows.Count, actingUserId);
```

- Never log: the B2 application key, a session token/cookie value, full file content, or response
  row bodies (matches `ReportModal.razor`'s existing "never log response bodies" rule, §11 there).
- On the Post-Action cap-hit failure, log at `Error` with the same shape as `PostActionReportService`'s
  existing cap-hit log line (`CAS-015`), not the row contents.

---

## §12 Security Notes

No B2 credential path — confirmed neither `PreActionReportService` nor `PostActionReportService`
references `IB2Client` or the B2 application key, directly or indirectly (carried forward from
`CAS-015` §11); this spec adds no new dependency.

### B2 Credential Handling
N/A — this endpoint never touches B2. Data sources are exclusively `dbo.search_result`,
`dbo.quarantine_entry`, and `dbo.audit_log` via existing repositories.

### Audit Trail
**Decision (confirmed with the project owner, 2026-09-30): opening a preview does NOT write an audit
log row.** FR-23's audited-action list (search, match, quarantine, delete, generate) does not name
"viewing/previewing a report," and only the `POST` download endpoints continue to write
`PreActionReportGenerated`/`PostActionReportGenerated` rows (`CAS-015` SC-5, unchanged). This keeps
preview lightweight for a call that fires every time a modal opens — materially more often than a
download — and avoids a mismatch between "generated" audit semantics and a read-only preview. SC-4
proves this with a negative assertion (no audit write occurs).

### Session / Auth Requirements
Both routes require the existing `CachetAuthenticated` policy — the same policy already enforced on
`POST /reports/pre-action` and `POST /reports/post-action`. No dependency on the still-open BRD
§12.4 items (web caching layer mechanism, O-Drive physical-refresh cadence) — this spec's preview
always re-runs the live aggregation query per `CAS-015` §14's locked-in decision ("every read,
preview or download, is drawn fresh from the underlying tables at request time"), so it takes no
position on the open caching-layer question.

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `PreActionReportServiceTests` (extended) | `Test_SC1_PreActionPreview_ReturnsExpectedShape` | SC-1 |
| `PreActionReportServiceTests` (extended) | `Test_V4_PreActionPreviewExcludesSoftDeletedFiles` | V4 |
| `PreActionReportServiceTests` (extended) | `Test_SC3_PreviewAndGenerateAsyncProduceIdenticalRows` (Pre-Action variant) | SC-3, V2 |
| `PostActionReportServiceTests` (extended) | `Test_SC2_PostActionPreview_ReturnsExpectedShape` | SC-2 |
| `PostActionReportServiceTests` (extended) | `Test_V3_PostActionPreviewAppliesAllowListAndIncludesFailures` | V3 |
| `PostActionReportServiceTests` (extended) | `Test_V5_PostActionPreviewCapReachedFailsLoud` | V5 |
| `PostActionReportServiceTests` (extended) | `Test_SC3_PreviewAndGenerateAsyncProduceIdenticalRows` (Post-Action variant) | SC-3, V2 |
| `ReportsControllerTests` (extended) | `Test_V1_PreActionPreview_Unauthenticated_Returns401`, `Test_V1_PostActionPreview_Unauthenticated_Returns401` | V1 |
| `ReportsControllerTests` (extended) | `Test_V6_ActiveTabLabelIsCosmeticOnlyForBothPreviews` | V6 |
| `ReportsControllerTests` (extended) | `Test_SC4_PreviewEndpointsWriteNoAuditRow` | SC-4 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `ReportsControllerTests` (`Cachet.ArchiveSync.Api.IntegrationTests`, extended) | End-to-end `GET /reports/{pre,post}-action/preview` against a real test database, both success and cap-hit paths | SC-1, SC-2, V5 |
| `ReportModalTests` (`Cachet.ArchiveSync.Web.Tests`, new/extended) | `Test_SC5_ReportModalRendersAgainstLivePreviewEndpoints` | SC-5 |

### Coverage Target
Changed files must reach ≥ 85% line coverage (project standard, `CAS-015` precedent).

---

## §14 Rollout

1. No database migration — deploy `Cachet.ArchiveSync.Api` alone; the two new `GET` routes are
   additive and cannot break any existing caller (nothing currently calls them successfully — they
   404 today).
2. No Web deployment required — `CAS-032`'s `ReportModal.razor` is already deployed and already
   targets this exact contract; once the Api routes exist, the next preview open succeeds with no
   Web-side redeploy.
3. Rollback: revert the Api deployment. The two new routes disappear and callers return to the
   current (broken) 404/banner state — no data-loss risk, since these are read-only, non-persisting
   endpoints.

---

## §15 Open Questions

- [x] **Audit-on-preview — resolved 2026-09-30 (project owner):** no audit row on preview. See §12.
- [ ] None outstanding beyond the above.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude (assisted) | 2026-09-30 | Drafted per `CAS-032` §15 item 1's explicit instruction to mint this spec; closes the reproducing "Something went wrong generating this report" defect |
| Reviewer | | | |

---

## §17 Graph Impact

### God Nodes in Blast Radius
None. `graphify explain "ReportsController"` shows degree 1 (only its own DI registration edge);
`IPreActionReportService`/`IPostActionReportService` show degree 2 each (`ReportsController` +
their concrete implementation). No node with >10 edges is touched.

### Communities / Modules Affected
Reporting module only (community 9 in the current graph — `ReportsController`,
`IPreActionReportService`, `IPostActionReportService`). Project: `Cachet.ArchiveSync.Api` and
`Cachet.ArchiveSync.Core` (new DTOs) only — no `Web` or `Worker` changes.

### Surprising Connections
None identified — this is a narrowly-scoped, low-connectivity addition confined to the existing
Reporting service pair and controller.

### Graphify Queries Run
```
python -m graphify query "pre-action and post-action report preview endpoints, reusing PreActionReportService and PostActionReportService row aggregation" --budget 2000
python -m graphify explain "ReportsController"
python -m graphify explain "PreActionReportService"
python -m graphify explain "PostActionReportService"
```

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test
- [ ] All §7 Validations are implemented and tested
- [ ] Both §6 API endpoints exist with correct verb/route/auth (`GET /reports/pre-action/preview`,
      `GET /reports/post-action/preview`, both `CachetAuthenticated`)
- [ ] §12 Security Notes are implemented as described (no B2 key exposure; **no** audit log write on
      preview, confirmed by a negative test; correct `CachetAuthenticated` policy enforced)
- [ ] §5 Database objects — N/A, confirm no schema changes were made
- [ ] §9 Config keys — N/A, confirm none were added
- [ ] §11 Telemetry events are emitted (no B2 key / session token / row content in logs)
- [ ] §17 Graph Impact matches actual code changes (no unintended nodes)
- [ ] No out-of-scope changes in §4 were made (in particular: no changes to `CAS-015`'s `POST`
      endpoints' contract or to `CAS-032`'s `ReportModal.razor`)
- [ ] Manually confirm (or via SC-5) that opening either report modal in the running Web app no
      longer shows the "Something went wrong generating this report" banner

### Verification Report Format
The reviewer writes `CAS-038.review.md` with:
- Overall verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Security gap list (if any)
- Scope violations (if any)

## Implementation Log

**Completed:** 2026-09-30
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 83.1% | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Core/Dtos/PreActionReportPreview.cs` | New — GET pre-action preview response DTO |
| `src/Cachet.ArchiveSync.Core/Dtos/PostActionReportPreview.cs` | New — GET post-action preview response DTO |
| `src/Cachet.ArchiveSync.Core/Interfaces/IPreActionReportService.cs` | Added PreviewAsync |
| `src/Cachet.ArchiveSync.Core/Interfaces/IPostActionReportService.cs` | Added PreviewAsync, ReportPreviewOutcome, PostActionReportPreviewResult |
| `src/Cachet.ArchiveSync.Api/Services/PreActionReportService.cs` | Extracted BuildRowsAsync, added PreviewAsync |
| `src/Cachet.ArchiveSync.Api/Services/PostActionReportService.cs` | Extracted BuildRowsAsync (incl. cap-hit), added PreviewAsync |
| `src/Cachet.ArchiveSync.Api/Controllers/ReportsController.cs` | Added GET /reports/pre-action/preview and /post-action/preview actions |
| `src/Cachet.ArchiveSync.Api.Tests/PreActionReportServiceTests.cs` | Added SC-1/V-4/SC-3 preview unit tests |
| `src/Cachet.ArchiveSync.Api.Tests/PostActionReportServiceTests.cs` | Added SC-2/V-3/V-5/SC-3 preview unit tests |
| `src/Cachet.ArchiveSync.Api.Tests/ReportsControllerTests.cs` | Added V-1/V-6/SC-4 controller-level preview tests |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/ReportsControllerTests.cs` | Added end-to-end preview tests + SC-5; orchestrator fixed audit-assertion scoping (Login vs. report actions) |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `PreActionReportServiceTests` | `Test_SC1_PreActionPreview_ReturnsExpectedShape` | SC-1 |
| `PreActionReportServiceTests` | `Test_V4_PreActionPreviewExcludesSoftDeletedFiles` | V-4 |
| `PreActionReportServiceTests` | `Test_SC3_PreviewAndGenerateAsyncProduceIdenticalRows` | SC-3/V-2 |
| `PostActionReportServiceTests` | `Test_SC2_PostActionPreview_ReturnsExpectedShape` | SC-2 |
| `PostActionReportServiceTests` | `Test_V3_PostActionPreviewAppliesAllowListAndIncludesFailures` | V-3 |
| `PostActionReportServiceTests` | `Test_V5_PostActionPreviewCapReachedFailsLoud` | V-5 |
| `PostActionReportServiceTests` | `Test_SC3_PreviewAndGenerateAsyncProduceIdenticalRows` | SC-3/V-2 |
| `ReportsControllerTests (Api.Tests)` | `Test_V1_PreActionPreview_Unauthenticated_Returns401` | V-1 |
| `ReportsControllerTests (Api.Tests)` | `Test_V1_PostActionPreview_Unauthenticated_Returns401` | V-1 |
| `ReportsControllerTests (Api.Tests)` | `Test_V6_ActiveTabLabelIsCosmeticOnlyForBothPreviews` | V-6 |
| `ReportsControllerTests (Api.Tests)` | `Test_SC4_PreviewEndpointsWriteNoAuditRow` | SC-4 |
| `ReportsControllerTests (IntegrationTests)` | `Test_SC1_PreActionPreview_ReturnsExpectedShape` | SC-1 |
| `ReportsControllerTests (IntegrationTests)` | `Test_SC2_PostActionPreview_ReturnsExpectedShape` | SC-2 |
| `ReportsControllerTests (IntegrationTests)` | `Test_V5_PostActionPreviewCapReachedFailsLoud` | V-5 |
| `ReportsControllerTests (IntegrationTests)` | `Test_PreActionPreview_NoSession_Returns401` | V-1 |
| `ReportsControllerTests (IntegrationTests)` | `Test_PostActionPreview_NoSession_Returns401` | V-1 |
| `ReportsControllerTests (IntegrationTests)` | `Test_SC5_ReportModalRendersAgainstLivePreviewEndpoints` | SC-5 |
