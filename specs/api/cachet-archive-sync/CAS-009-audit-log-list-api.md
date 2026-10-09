---
id: CAS-009
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
priority: P1
related_specs: [CAS-003, CAS-004, CAS-005]
touches:
  database: true
  api: true
  ui: false
  worker: false
  b2: false
  config: false
  security: true
migrations:
  - 20260922_193000__CAS-009-audit-log-list-api.sql
---

# CAS-009 — Audit Log List API

WBS Task 24 (Audit Log). Implements FR-23 ("SQL Server Audit Log," read side), FR-24 ("Audit Log
Filterable" — "filterable at minimum by tab and by date"). Architecture basis:
`0.Specifications/CACHET_API_Design_Specification_v1.md` §8.1 (`GET /audit-log`), BRD §7.6
(Audit Log), CAS-003 (owns `dbo.audit_log`'s DDL — this spec adds no columns/constraints), and
CAS-005 (owns the write path, `dbo.audit_log_ins` / `IAuditLogService.WriteAsync` — this spec adds
no new writer and does not touch that code path).

---

## §0 Provenance Note (read this first)

`dbo.audit_log` and its write path (`dbo.audit_log_ins`, `IAuditLogRepository.InsertAsync`,
`IAuditLogService.WriteAsync`) already exist, merged under CAS-003/CAS-005. Every state-changing
endpoint built since (CAS-006 Keyword Search, CAS-007 O-Drive Sync/Content Scan, CAS-008
Quarantine/Permanent Delete) already writes rows into this table before returning its own success
response. **No read/list endpoint exists yet** — this is the first spec to expose `GET /audit-log`,
closing the read side of FR-23/FR-24 so the (separately speced, future) Audit Log Blazor screen has
something to call. This spec adds:

- A new stored procedure (`dbo.audit_log_lst`) — read-only, no change to `dbo.audit_log`'s schema.
- A new repository method (`IAuditLogRepository.ListAsync`) — extends, does not replace, the
  existing `InsertAsync`.
- A new service (`IAuditLogListService` / `AuditLogListService`) and a new
  `AuditLogController` (`GET /audit-log`) — the write side's `IAuditLogService` is untouched.

---

## §1 Summary

Implements the read side of FR-23/FR-24: `GET /audit-log` returns a paginated, filterable list of
`dbo.audit_log` rows for the Audit Log screen, filterable by `tab` and by a `dateFrom`/`dateTo`
range (FR-24's minimum requirement), with each row shaped per FR-23's required field set — user,
tab/module, action, file/path, timestamp, result (and `resultDetail` for failures). This is a
read-only, non-audited endpoint (listing the audit log is not itself an auditable action) and
introduces no new write path — the existing CAS-005 writer is unchanged and unreferenced by this
spec's code.

---

## §2 User Story

As a **TraceCounts reviewer**, I want to see every logged action across the system, filtered by
module tab and by date range, so that I can confirm what happened, by whom, and with what outcome
— including failed actions, which must never be silently omitted.

---

## §3 In-Scope

- `GET /audit-log` — paginated list of `dbo.audit_log` rows, newest first, with query params:
  - `tab` (optional — one of `dbo.audit_log`'s `CK_audit_log_tab_module` values: `Auth`,
    `Keyword Search`, `O-Drive Sync`, `Quarantine`, `Task Manager`, `Audit Log`, `Reports`;
    omitted/`All` returns every tab).
  - `dateFrom`, `dateTo` (optional, ISO 8601 UTC) — inclusive range against `created_at`.
  - `page` (default 1), `pageSize` (default 50) — page-number pagination, per this project's
    existing offset-pagination precedent (no cursor/keyset pagination introduced elsewhere in this
    codebase).
  - Response includes `items[]` (each shaped per API design doc §8.1: `user`, `tab`, `action`,
    `filePath`, `timestamp`, `result`, plus `resultDetail` when present) and `page`/`pageSize`/
    `totalCount`.
- New stored procedure `dbo.audit_log_lst` — filtered, paginated, ordered `created_at DESC`, joined
  to `dbo.users` for the `username` display value (FR-23's "user" field).
- New `IAuditLogRepository.ListAsync` method (Dapper, multi-mapping query + a `COUNT(*)` for
  `totalCount`) — extends the existing `IAuditLogRepository` (CAS-005), does not modify
  `InsertAsync`.
- New `IAuditLogListService` / `AuditLogListService` — validates the query, maps rows to the
  response DTO. Not audited (read-only, listing is not itself an auditable action — matching
  CAS-008's precedent for `GET /quarantine`).
- New `AuditLogController` (`GET /audit-log` only) — `[Authorize(Policy = "CachetAuthenticated")]`,
  matching this project's existing precedent that a plain read needs only an authenticated session,
  not `CachetFullAccess` (CAS-008 §12).

---

## §4 Out-of-Scope

- **`user`/`action` free-text filters** — the API design doc (§8.1) proposes these as
  "(proposed — not BRD-confirmed)," beyond FR-24's literal minimum (tab + date). **Not built in
  this spec** pending client confirmation — see §15. `tab` and `dateFrom`/`dateTo` only.
- **The Blazor Server Audit Log screen** (`touches.ui: false`) — a separate, future Web-kind spec
  that will consume this endpoint. BRD UI/UX wireframe for this screen exists for reference only.
- **`POST /reports/post-action`'s reuse of this query** (API design doc §9.2, which states the
  Post-Action report should source from "the same underlying query as `GET /audit-log`") — that
  reuse is the Reporting module's concern (a future spec, FR-25–FR-27) and is not implemented here;
  this spec only guarantees `dbo.audit_log_lst`'s filter/shape are stable enough to be reused later.
- **Retention/archival** of `dbo.audit_log` rows — out of scope per CAS-004 (append-only, no
  scheduled purge defined); this spec's pagination is a UI/response concern only, not a retention
  mechanism.
- **Any change to the write path** (`dbo.audit_log_ins`, `IAuditLogService.WriteAsync`,
  `IAuditLogRepository.InsertAsync`) — CAS-005's writer is reused unchanged.
- **Drive encryption-at-rest** (BRD §12.2 open item) — unrelated to this read-only API surface.

---

## §5 Database Objects

### Tables / Columns

No new tables or columns. `dbo.audit_log` (CAS-003) already has the indexes this endpoint's filter
needs: `IX_audit_log_tab_module_created_at (tab_module, created_at)` and
`IX_audit_log_created_at (created_at)` — both created specifically for FR-24 filtering (CAS-003's
own migration comment). No `ALTER TABLE` needed.

### Stored Procedures

| Name                | Schema | Action | Notes                                                                                                                                                                                                                                                                                         |
| ------------------- | ------ | ------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `dbo.audit_log_lst` | dbo    | lst    | Filters by optional `@tab_module`, `@date_from`, `@date_to`; joins `dbo.users` for `username`; orders `created_at DESC`; returns two result sets — the paged rows and a `total_count` scalar (or an `OUTPUT` param) for the filtered (pre-pagination) row count. Backs `GET /audit-log` (§6). |
| `dbo.audit_log_ins` | dbo    | ins    | Reused verbatim from CAS-005 — unchanged, not touched by this spec.                                                                                                                                                                                                                           |

### Migration Strategy

`db-author` writes, at `/implement` time:

- `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-009-audit-log-list-api.sql` — idempotent, adds
  `dbo.audit_log_lst` only. No table DDL, no index changes (CAS-003's existing indexes already
  cover this query's filter/sort shape).
- `Database/CachetArchiveSync/Stored-Procedures/dbo.audit_log_lst.sql` — declarative mirror.

No data migration.

---

## §6 API Endpoints

### Endpoint: `GET /audit-log`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]` — a plain authenticated session; listing
the audit log is a read, not a B2-write action (API design doc §8.1 lists only `401`/`422` as
errors — no `403`).

**Async:** No.

**Request:** query params —
| Param | Type | Required | Notes |
|-------|------|----------|-------|
| `tab` | string | No | One of `Auth\|Keyword Search\|O-Drive Sync\|Quarantine\|Task Manager\|Audit Log\|Reports`, or `All`/omitted for every tab (§7 V1). |
| `dateFrom` | ISO 8601 UTC | No | Inclusive lower bound on `created_at`. |
| `dateTo` | ISO 8601 UTC | No | Inclusive upper bound on `created_at`. Must not be earlier than `dateFrom` (§7 V2). |
| `page` | int | No | Default `1`. Must be ≥ 1 (§7 V3). |
| `pageSize` | int | No | Default `50` (API design doc §8.1 / UI-UX spec §6.1 proposed default). Must be between 1 and 200 (§7 V4) — an upper bound protects against an unbounded scan of an append-only, ever-growing table. |

**Response `200 OK`:**

```json
{
  "items": [
    {
      "user": "m.alvarez",
      "tab": "Quarantine",
      "action": "Deleted",
      "filePath": "contracts/2019/agreement-004.pdf",
      "timestamp": "2026-09-09T14:22:10Z",
      "result": "Success",
      "resultDetail": null
    }
  ],
  "page": 1,
  "pageSize": 50,
  "totalCount": 1
}
```

Fields match FR-23's required set exactly: `user` (joined `dbo.users.username`, not `user_id`),
`tab` (`tab_module`), `action`, `filePath` (`file_path`, nullable), `timestamp` (`created_at`,
UTC), `result`, plus `resultDetail` (`result_detail`, nullable — populated for `Failure` rows,
per CAS-005's `AuditLogEntry.ResultDetail`). Failed actions appear with `result: "Failure"` —
never filtered out or dropped (API design doc §8.1, NFR target #11).

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Success (`items` may be empty) |
| 401 | No valid session |
| 422 | `validation-failed` — malformed/inconsistent query params (§7 V1–V4) |

---

## §7 Validations

| ID  | Rule                                                                                           | On Failure                                  | Test                                              |
| --- | ---------------------------------------------------------------------------------------------- | ------------------------------------------- | ------------------------------------------------- |
| V1  | `tab`, if provided, must be one of `dbo.audit_log`'s `CK_audit_log_tab_module` values or `All` | 422 `validation-failed`                     | `Test_V1_UnknownTabRejected`                      |
| V2  | `dateTo` must not be earlier than `dateFrom` when both are provided                            | 422 `validation-failed`                     | `Test_V2_DateToBeforeDateFromRejected`            |
| V3  | `page` must be ≥ 1                                                                             | 422 `validation-failed`                     | `Test_V3_NonPositivePageRejected`                 |
| V4  | `pageSize` must be between 1 and 200 (inclusive); defaults to 50 when omitted                  | 422 `validation-failed`                     | `Test_V4_PageSizeOutOfRangeRejected`              |
| V5  | Rows with `result = 'Failure'` are included in `items[]`, never filtered out by default        | N/A — proven by presence                    | `Test_V5_FailedActionsAppearInResults`            |
| V6  | `totalCount` reflects the filtered (pre-pagination) row count, not the page's row count        | N/A — proven by count mismatch across pages | `Test_V6_TotalCountReflectsFilteredNotPagedCount` |

---

## §8 UI / UX

N/A — `touches.ui: false`. No `.razor` components; the Web project is untouched by this spec. The
BRD UI/UX wireframe for the Audit Log screen exists for reference but its implementation is a
separate, future Web-kind spec that consumes this endpoint.

---

## §9 Configuration Changes

N/A — no new configuration keys. Reuses the existing `ConnectionStrings:CachetArchiveSync` key
(CAS-003/CAS-005), untouched here.

---

## §10 Success Criteria

| ID   | Criterion                                                                                                                                                                       | Test                                                          |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------- |
| SC-1 | `GET /audit-log` with no query params returns `200` with the most recent `pageSize` (default 50) rows across all tabs, newest first, correctly shaped per §6                    | `Test_SC1_DefaultRequestReturnsPagedRowsNewestFirst`          |
| SC-2 | `GET /audit-log?tab=Quarantine` returns only rows where `tab_module = 'Quarantine'`                                                                                             | `Test_SC2_TabFilterReturnsOnlyMatchingRows`                   |
| SC-3 | `GET /audit-log?dateFrom=...&dateTo=...` returns only rows with `created_at` inside the inclusive range                                                                         | `Test_SC3_DateRangeFilterReturnsOnlyRowsInRange`              |
| SC-4 | `GET /audit-log?page=2&pageSize=10` returns the second page of 10 rows, distinct from page 1's rows, with `totalCount` unchanged between the two calls (same filter)            | `Test_SC4_PaginationReturnsDistinctPagesWithStableTotalCount` |
| SC-5 | A row written with `result: Failure` and a populated `resultDetail` (e.g. a CAS-007 `SyncActionFailedB2` entry) appears in the response with both fields populated, not omitted | `Test_SC5_FailureRowsIncludedWithResultDetail`                |
| SC-6 | An unauthenticated request to `GET /audit-log` returns `401` before any query executes                                                                                          | `Test_SC6_UnauthenticatedRequestReturns401`                   |
| SC-7 | No B2 application key, session token/cookie value, or raw file content appears in any log line for `GET /audit-log`                                                             | `Test_SC7_NoCredentialOrContentLeakageInLogs`                 |

---

## §11 Telemetry

```csharp
_logger.LogInformation(
    "Audit log list requested by user {UserId}: tab={Tab}, page={Page}, pageSize={PageSize}, returned={ReturnedCount}, totalCount={TotalCount}",
    userId, tab, page, pageSize, items.Count, totalCount);
```

- Never log: the B2 application key or any part of it, a session token/cookie value, full file
  content, or raw archive file paths beyond what the audit log (FR-23) already records.

---

## §12 Security Notes

### B2 Credential Handling

N/A — this endpoint never touches `IB2Client` or any B2 credential; it is a pure SQL Server read
against `dbo.audit_log`/`dbo.users` (`username` only, never `b2_application_key`/
`b2_application_key_id` — those two columns must never be selected by `dbo.audit_log_lst`'s join).

### Audit Trail

`GET /audit-log` is **not audited** — listing the audit log is a read, not an action FR-23 requires
to be logged (consistent with CAS-008's `GET /quarantine` precedent: "not audited (read-only, §12
only names the write endpoints)"). No new row is written to `dbo.audit_log` by this spec.

### Session / Auth Requirements

`[Authorize(Policy = "CachetAuthenticated")]` — a plain authenticated session, same tier as
`GET /quarantine` (CAS-008 §12). Not blocked by BRD §12.2 — the login method underlying this policy
is already resolved (CAS-002); drive encryption-at-rest is unrelated to this API surface.

---

## §13 Test Plan

### Unit Tests

| Class                        | Method                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       | Proves           |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------- |
| `AuditLogListServiceTests`   | `Test_V1_UnknownTabRejected`, `Test_V2_DateToBeforeDateFromRejected`, `Test_V3_NonPositivePageRejected`, `Test_V4_PageSizeOutOfRangeRejected`, `Test_SC1_DefaultRequestReturnsPagedRowsNewestFirst`, `Test_SC2_TabFilterReturnsOnlyMatchingRows`, `Test_SC3_DateRangeFilterReturnsOnlyRowsInRange`, `Test_SC4_PaginationReturnsDistinctPagesWithStableTotalCount`, `Test_SC5_FailureRowsIncludedWithResultDetail`, `Test_V5_FailedActionsAppearInResults`, `Test_V6_TotalCountReflectsFilteredNotPagedCount` | V1–V6, SC-1–SC-5 |
| `LogHygieneTests` (extended) | `Test_SC7_NoCredentialOrContentLeakageInLogs`                                                                                                                                                                                                                                                                                                                                                                                                                                                                | SC-7             |

### Integration Tests

| Class                                                 | Method                                                                                                                      | Proves                    |
| ----------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------- | ------------------------- |
| `AuditLogControllerTests` (`WebApplicationFactory`)   | `Test_SC6_UnauthenticatedRequestReturns401` (HTTP-level), `Test_SC1_DefaultRequestReturnsPagedRowsNewestFirst` (HTTP-level) | SC-6, SC-1                |
| `AuditLogRepositoryTests` (extended, real SQL Server) | `Test_ListAsync_FiltersByTabAndDateRangeAndPaginatesCorrectly`                                                              | Supports SC-2, SC-3, SC-4 |

### Coverage Target

Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. Deploy the database migration first (`dbo.audit_log_lst`) — additive-only, no existing table or
   procedure altered.
2. Deploy `Core` (interface/DTO additions) and `Api` (new controller route/service/repository
   method) together — synchronous endpoint, no Worker deployment required.
3. Rollback: `dbo.audit_log_lst` is additive — dropping it is safe at any time (no other code path
   depends on it existing).

---

## §15 Open Questions

- [x] **`user`/`action` free-text filters (API design doc §8.1) — resolved 2026-09-22 (R.
      Devarajan, TraceCounts):** deferred, not built in this spec. FR-24's literal minimum is
      `tab` + date range only; the API design doc itself flags the two extra filters as its own
      proposal, "not BRD-confirmed." This spec ships with `tab`/`dateFrom`/`dateTo` only. Adding
      `user`/`action` filters later is a small, additive change (the response already returns both
      fields on every row) — to be picked up as a follow-up change spec if/when confirmed wanted,
      not built speculatively now.
- [x] **Reuse by `POST /reports/post-action` (API design doc §9.2) — resolved 2026-09-22 (R.
      Devarajan, TraceCounts):** deferred to the future Reporting spec (FR-25–FR-27). Per Karpathy
      P4 (no new abstraction until a pattern is needed a second time), this spec does not extract
      any shared lower-level query — only one caller (`GET /audit-log`) exists today.
      `dbo.audit_log_lst` is written generically (filter + pagination over `dbo.audit_log`) so the
      Reporting spec can call it directly or follow its pattern once its own real requirements
      (e.g. whether pagination even applies to a report) are known.
- Not blocked by either open BRD §12.2 item (login method already resolved; drive
  encryption-at-rest is unrelated to this read-only API surface).

All open items resolved as of 2026-09-22 — no remaining blockers to `status → ready`.

---

## §16 Sign-off

| Role     | Name                                        | Date       | Notes   |
| -------- | ------------------------------------------- | ---------- | ------- |
| Author   | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 |         |
| Reviewer |                                             |            | Pending |

---

## §17 Graph Impact

Graphify query run this session:

```
graphify query "Audit Log API endpoint GET audit-log filter by tab and date range pagination" --budget 2000
graphify explain "audit_log"
```

### God Nodes in Blast Radius

None with >10 edges identified in the query result. The highest-degree node touched is
`FR-23: SQL Server Audit Log` (edges to `ASP.NET Core Security Skill`, `P7`, `FR-13d`,
`NFR: Auditability`) — an FR/requirement node, not a code node; no single code file in the blast
radius exceeds a handful of edges.

### Communities / Modules Affected

- **Community 5** — contains `GET /audit-log`, `FR-23`, `FR-24`, and
  `API spec §8: Audit Log Module (FR-23-FR-24)` (all from `0.Specifications/`), confirming this
  endpoint's design source is the API design doc's §8, exactly as cited above.
- **Community 35** — `AuditLogServiceTests.cs` (the existing CAS-005 write-path unit tests) —
  touched only in the sense that this spec's new `AuditLogListServiceTests` is a sibling test class
  in the same project, not a modification to the existing write-path tests themselves.
- **Community 166** — the CAS-005 migration file node; unrelated to this spec's own (separate,
  new) migration beyond sharing the `dbo.audit_log` table.
- Projects touched: `Api` only (`Core` interface/DTO additions, `Api` controller/service/repository
  additions). **Not** `Worker`, **not** `Web`.

### Surprising Connections

- The graph confirms `0.Specifications/CACHET_API_Design_Specification_v1.md` already contains a
  fully-specified `GET /audit-log` design (§8.1) predating this spec — this spec implements that
  existing design rather than inventing a new contract, which is why §6 above matches its request/
  response shape closely.
- No cross-module surprise: `GET /audit-log` has no edge to any Quarantine/O-Drive Sync/Keyword
  Search code node in the 2-hop result — it is a pure read over the shared `dbo.audit_log` table,
  consistent with FR-24's design as a cross-cutting, tab-filterable view rather than a per-module
  endpoint.

---

## §18 Verification Plan

### Reviewer Checklist

The reviewer will independently verify each of the following. Mark N/A only if the section itself
is N/A.

- [ ] All §10 Success Criteria (SC-1–SC-7) have a passing test.
- [ ] All §7 Validations (V1–V6) are implemented and tested.
- [ ] `GET /audit-log` exists with `[Authorize(Policy = "CachetAuthenticated")]` — no
      `CachetFullAccess` requirement (§6, §12).
- [ ] `GET /audit-log` does **not** write an audit log row (§12 — this is a read-only, unaudited
      endpoint).
- [ ] Response `items[]` never omits `result: "Failure"` rows (§7 V5, §10 SC-5).
- [ ] `totalCount` reflects the filtered row count, not the current page's row count (§7 V6, §10
      SC-4).
- [ ] `dbo.audit_log_lst`'s join to `dbo.users` selects only `username` — never
      `b2_application_key`/`b2_application_key_id` (§12).
- [ ] `dbo.audit_log_ins` / `IAuditLogService.WriteAsync` / `IAuditLogRepository.InsertAsync`
      (CAS-005) are unmodified by this spec's changes.
- [ ] No B2 application key, session token, or raw file content appears in any log line (§11,
      SC-7).
- [ ] §17 Graph Impact is still consistent with actual code changes — no unintended
      `Worker`/`Web`/other-module changes.
- [ ] No out-of-scope changes from §4 were made (no `user`/`action` filters, no UI, no Reporting
      module changes).

### Verification Report Format

Per template: `CAS-009.review.md` with overall verdict, per-SC verdict table, security gap list,
scope violations.

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage Controller 91.3%, Service 100%; Repository 0% disclosed (no live SQL Server, matches CAS-008 precedent); interface 75% (record-generated members, same precedent) | Endpoints ✓

### Files Changed

| File                                                                                       | Change                                                                                    |
| ------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------- |
| `Database/migrations/20260922_193000__CAS-009-audit-log-list-api.sql`                      | New - dbo.audit_log_lst                                                                   |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.audit_log_lst.sql`                       | New mirror                                                                                |
| `src/Cachet.ArchiveSync.Core/Dtos/AuditLogRow.cs`                                          | New                                                                                       |
| `src/Cachet.ArchiveSync.Core/Dtos/AuditLogListItem.cs`                                     | New                                                                                       |
| `src/Cachet.ArchiveSync.Core/Interfaces/IAuditLogListService.cs`                           | New                                                                                       |
| `src/Cachet.ArchiveSync.Core/Interfaces/IAuditLogRepository.cs`                            | Modified - added ListAsync                                                                |
| `src/Cachet.ArchiveSync.Api/Services/AuditLogListService.cs`                               | New                                                                                       |
| `src/Cachet.ArchiveSync.Api/Controllers/AuditLogController.cs`                             | New - GET /audit-log                                                                      |
| `src/Cachet.ArchiveSync.Api/Repositories/AuditLogRepository.cs`                            | Modified - added ListAsync                                                                |
| `src/Cachet.ArchiveSync.Api/Program.cs`                                                    | Modified - DI registration for IAuditLogListService                                       |
| `src/Cachet.ArchiveSync.Api.Tests/AuditLogListServiceTests.cs`                             | New                                                                                       |
| `src/Cachet.ArchiveSync.Api.Tests/LogHygieneTests.cs`                                      | Modified - extended with Test_SC7_NoCredentialOrContentLeakageInLogs_AuditLogListEndpoint |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/AuditLog/AuditLogApiWebApplicationFactory.cs` | New                                                                                       |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/AuditLogControllerTests.cs`                   | New                                                                                       |

### Tests Added

| Class                                        | Method                                                             | Covers            |
| -------------------------------------------- | ------------------------------------------------------------------ | ----------------- |
| `AuditLogListServiceTests`                   | `Test_SC1_DefaultRequestReturnsPagedRowsNewestFirst`               | SC-1              |
| `AuditLogListServiceTests`                   | `Test_SC1_DefaultPageSizeIs50WhenOmitted`                          | SC-1              |
| `AuditLogListServiceTests`                   | `Test_SC2_TabFilterReturnsOnlyMatchingRows`                        | SC-2              |
| `AuditLogListServiceTests`                   | `Test_SC2_AllSentinelPassesNullTabToRepository`                    | V-1               |
| `AuditLogListServiceTests`                   | `Test_SC3_DateRangeFilterReturnsOnlyRowsInRange`                   | SC-3              |
| `AuditLogListServiceTests`                   | `Test_SC4_PaginationReturnsDistinctPagesWithStableTotalCount`      | SC-4              |
| `AuditLogListServiceTests`                   | `Test_SC5_FailureRowsIncludedWithResultDetail`                     | SC-5              |
| `AuditLogListServiceTests`                   | `Test_V5_FailedActionsAppearInResults`                             | V-5               |
| `AuditLogListServiceTests`                   | `Test_V6_TotalCountReflectsFilteredNotPagedCount`                  | V-6               |
| `AuditLogListServiceTests`                   | `Test_V1_UnknownTabRejected`                                       | V-1               |
| `AuditLogListServiceTests`                   | `Test_V2_DateToBeforeDateFromRejected`                             | V-2               |
| `AuditLogListServiceTests`                   | `Test_V3_NonPositivePageRejected`                                  | V-3               |
| `AuditLogListServiceTests`                   | `Test_V4_PageSizeOutOfRangeRejected`                               | V-4               |
| `LogHygieneTests`                            | `Test_SC7_NoCredentialOrContentLeakageInLogs_AuditLogListEndpoint` | SC-7              |
| `AuditLogControllerTests (IntegrationTests)` | `Test_SC1_DefaultRequestReturnsPagedRowsNewestFirst`               | SC-1 (HTTP-level) |
| `AuditLogControllerTests (IntegrationTests)` | `Test_SC6_UnauthenticatedRequestReturns401`                        | SC-6              |
| `AuditLogControllerTests (IntegrationTests)` | `Test_V1_UnknownTabRejectedReturns422OverHttp`                     | V-1 (HTTP-level)  |

### Migration

`20260922_193000__CAS-009-audit-log-list-api.sql`
