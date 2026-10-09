---
id: CAS-005
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
priority: P1
related_specs: [CAS-001, CAS-002, CAS-003, CAS-004]   # CAS-003 owns dbo.audit_log's DDL; CAS-004
                                                        # documents its columns/indexes and the
                                                        # action/result enum convention this spec
                                                        # implements against. CAS-001 §6.3/§6.4 and
                                                        # CAS-002 §6.1/§6.3 are this service's first
                                                        # two callers — both were BLOCKED on this
                                                        # exact gap during /implement.
touches:
  database: true                       # one new stored procedure (dbo.audit_log_ins) — no new
                                        # table/column, dbo.audit_log already exists (CAS-003)
  api: false                           # this is a shared Core/Api service, not an endpoint
  ui: false
  worker: true                         # CAS-001 §6.4's Worker-side delete-outcome audit write is
                                        # a caller of this service
  b2: false
  config: false
  security: true
migrations: [20260922_150244__CAS-005-audit-log-write-service.sql]
---

# CAS-005 — Audit Log Write Service

## §0 Provenance Note (read this first)

This spec exists because `/implement CAS-002` and the planned `/implement CAS-001` both stopped
on the identical `BLOCKED: audit-log write mechanism` gap. `dbo.audit_log`'s schema is already
merged (`CAS-003`) and its usage conventions — including the action/result enum pattern this spec
implements against — are already documented (`CAS-004` §5 "Action/Result Catalog & Payload
Convention"). What is missing, and what this spec adds, is the actual C# write path: a stored
procedure to insert a row, and a shared `IAuditLogService` both `Cachet.ArchiveSync.Api`
(CAS-002's login/logout, CAS-001's cross-check-exclusion and delete-submission audit rows) and
`Cachet.ArchiveSync.Worker` (CAS-001's delete-outcome audit row) can call, so neither caller
invents its own audit-log access path — consistent with CAS-004 §2's "every module's audit trail
... stay[s] consistent instead of each module inventing its own convention."

Per CAS-004 §4 Out-of-Scope: *"Stored procedure bodies... written alongside the API endpoints
that call them... or by db-author at /implement time"* — this spec is that authoring point for
`dbo.audit_log_ins` specifically.

---

## §1 Summary

Adds the write path for `dbo.audit_log` (FR-23): a single stored procedure (`dbo.audit_log_ins`)
and a shared `IAuditLogService`/`IAuditLogRepository` pair in `Cachet.ArchiveSync.Core`/`.Api`,
implementing the `AuditAction`/`AuditResult` C# enum convention CAS-004 §5 already resolved. No
new table, no new endpoint — this is infrastructure every state-changing module (Auth, B2
Integration, and later Keyword Search, O-Drive Sync, Quarantine, Task Manager) calls to satisfy
the audit-before-response invariant (FR-23), rather than each module writing its own `INSERT`.
Implements FR-23 (and unblocks FR-01–FR-03, FR-16/FR-17 in CAS-002/CAS-001, which depend on it).

---

## §2 User Story

As a **backend developer implementing any state-changing endpoint or Worker job** (login/logout,
permanent delete, and every future module), I want one shared, already-tested audit-log write
call so that every module's audit trail is written the same way, before the response returns,
without re-deriving the `INSERT` shape or the enum catalog each time.

---

## §3 In-Scope

- `dbo.audit_log_ins` stored procedure — one row per call, matching CAS-003's `dbo.audit_log`
  column list exactly (CAS-004 §5 restates it verbatim; this spec's SP must match that restatement,
  not diverge from it).
- `AuditAction` and `AuditResult` C# enums in `Cachet.ArchiveSync.Core`, seeded with exactly the
  action/result values CAS-002 and CAS-001 already name in their own text: `Login`,
  `LoginFailed`, `Logout` (CAS-002 §6.1/§6.3; API spec §3.1/§3.2), `CrossCheckExcluded` (CAS-001
  §6.3), `PermanentDeleteRequested`, `Deleted`, `DeleteFailed-Local`, `DeleteFailed-B2` (CAS-001
  §6.4) — as enum members with those semantics, not necessarily those exact literal strings (see
  §15 for the one naming question this surfaces).
- `IAuditLogRepository` (Core interface, Dapper implementation in Api) wrapping the stored
  procedure call.
- `IAuditLogService` (Core interface, Api implementation) — the call surface other modules use;
  thin wrapper over the repository, taking a typed `AuditLogEntry` parameter object rather than
  positional arguments, so call sites read clearly at each of CAS-002/CAS-001's call points.
  Registered in both `Cachet.ArchiveSync.Api` and `Cachet.ArchiveSync.Worker` DI containers (both
  are direct callers per §0).
- Enforcing the audit-before-response invariant is each *caller's* responsibility (CAS-004 §12
  already states this division) — this spec provides the call surface, not a middleware/filter
  that auto-injects audit writes.

---

## §4 Out-of-Scope

- Any change to `dbo.audit_log`'s schema (columns, constraints, indexes) — already merged, CAS-003.
- Retention/archival of audit rows — explicitly deferred, client-confirmed, CAS-004 §12/§15.
- Read/query access to the audit log (`GET /audit-log`, Audit Log screen filtering, FR-24) — a
  separate future spec's concern; this spec is write-only.
- Actually wiring CAS-002's `POST /login`/`POST /logout` or CAS-001's cross-check/delete flows to
  call this service — that is CAS-002's and CAS-001's own implementation work, resumed once this
  spec is `ready`/implemented. This spec only unblocks them.
- A generic "audit interceptor" or attribute-based auto-audit mechanism — not asked for, and would
  be premature abstraction (P4) ahead of a second, third, fourth caller actually existing in code.

---

## §5 Database Objects

### Tables / Columns

None — `dbo.audit_log` already exists (CAS-003, restated CAS-004 §5). No columns added.

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| `audit_log_ins` | `dbo` | ins | One row per call. Parameters map 1:1 to `dbo.audit_log`'s insertable columns (all except `audit_log_id` — identity — and `created_at`/`updated_at`, which default to `SYSUTCDATETIME()` per CAS-003). Returns `SCOPE_IDENTITY()` as `audit_log_id` via `OUTPUT` or a `SELECT`, so callers can associate an audit row with e.g. a permanent-delete task record if needed later. |

Signature (params, matching CAS-004 §5's column table exactly):
```sql
CREATE PROCEDURE dbo.audit_log_ins
    @created_by      NVARCHAR(50),
    @updated_by      NVARCHAR(50),
    @user_id         INT,
    @tab_module      NVARCHAR(30),
    @action          NVARCHAR(50),
    @file_path       NVARCHAR(1000) = NULL,
    @task_queue_id   INT = NULL,
    @result          NVARCHAR(30),
    @result_detail   NVARCHAR(MAX) = NULL
AS
...
```

### Migration Strategy

`db-author` writes both the idempotent timestamped migration
(`Database/migrations/<YYYYMMDD_HHMMSS>__CAS-005-audit-log-write-service.sql`, `CREATE OR ALTER
PROCEDURE` for idempotency on re-run) and the per-object mirror
(`Database/CachetArchiveSync/Stored-Procedures/dbo.audit_log_ins.sql`). No table DDL — the
migration is SP-only. Applies independently of CAS-002/CAS-001's own (currently empty)
migration lists.

---

## §6 API Endpoints

N/A — this spec adds no HTTP endpoint. `IAuditLogService` is called in-process by other modules'
controllers/services (Api) and Worker jobs.

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `tab_module` passed to `IAuditLogService` must be one of the `CK_audit_log_tab_module` values already fixed by CAS-003 (`Auth`, `Keyword Search`, `O-Drive Sync`, `Quarantine`, `Task Manager`, `Audit Log`, `Reports`) | Throw `ArgumentException` at the service boundary (fail fast in application code, not a DB constraint violation surfacing as an unhandled SQL exception) | `Test_V1_InvalidTabModuleRejected` |
| V2 | `action`/`result` must be a defined `AuditAction`/`AuditResult` enum member (not an arbitrary string) — enforced by the C# type system, not runtime validation | Compile-time (no test needed beyond confirming the service signature takes the enum type, not `string`) | `Test_V2_ServiceSignatureUsesEnumTypes` |
| V3 | The stored procedure call must never throw on `@file_path`/`@task_queue_id`/`@result_detail` being `NULL` (both are legitimately optional per CAS-003's nullable columns) | N/A — this is the required behavior | `Test_V3_OptionalFieldsAcceptNull` |

---

## §8 UI / UX

N/A — no UI surface.

---

## §9 Configuration Changes

N/A — no new configuration keys.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Calling `IAuditLogService.WriteAsync(...)` with a valid `AuditLogEntry` inserts exactly one row into `dbo.audit_log` with all fields populated as passed | `Test_SC1_WriteAsyncInsertsOneRow` |
| SC-2 | `audit_log_id`, `created_at`, `updated_at` are populated by the database (identity / default), never passed in by the caller | `Test_SC2_DbGeneratedFieldsNotCallerSupplied` |
| SC-3 | A call with an invalid `tab_module` value is rejected before any database round-trip (V1) | `Test_SC3_InvalidTabModuleFailsFast` |
| SC-4 | No exception thrown by `IAuditLogService` ever contains the raw SQL parameter values in a way that could leak file paths or user data beyond what's already in the audit row itself (i.e., no secret/credential material — N/A here since this table never holds B2 key material, CAS-004 §12) — service-level logging follows the same never-log-B2-key/session-token rule as every other module | `Test_SC4_NoSensitiveDataInExceptionMessages` |

---

## §11 Telemetry

```csharp
// _logger.LogInformation("Audit row written: {Action} / {Result} for user {UserId}, tab {TabModule}", action, result, userId, tabModule);
// _logger.LogError(ex, "Audit log write failed for {Action} / user {UserId} — caller must decide whether to fail the parent operation");
```

- Never log: the B2 application key, a session token/cookie value, password/password hash, or full
  file content — consistent with every other module's rule.
- Do log: every audit write attempt and its success/failure, since a failed audit write is itself
  operationally significant (a caller relying on "audit before response" needs to know if the
  write silently failed).

---

## §12 Security Notes

### B2 Credential Handling
N/A — this service never receives or stores B2 key material; `dbo.audit_log` has no column for it
(CAS-004 §12 confirms this).

### Audit Trail
This spec *is* the audit trail's write mechanism — not a consumer of another audit trail. The
"write before response" invariant (FR-23) remains each caller's responsibility (CAS-004 §12); this
service does not itself decide when to be called, only guarantees that when called, it performs a
single, correctly-shaped `INSERT` synchronously (`await`ed, not fire-and-forget) so a caller that
awaits this call before returning its own response genuinely satisfies FR-23 — a fire-and-forget
implementation here would silently break that guarantee for every caller. **This is the one
correctness property every future reviewer (of this spec and of CAS-001/CAS-002 once resumed) must
check.**

### Session / Auth Requirements
N/A directly — this service is called by already-authenticated/already-authorized call sites
(CAS-002 for login/logout, which is itself the pre-authentication case handled specially; CAS-001
for cross-check/delete, gated by that spec's own policy checks). This service does not itself
perform authorization — it trusts its caller, the same way a repository trusts its service layer.

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| `AuditLogServiceTests` | `Test_V1_InvalidTabModuleRejected` | V1 |
| `AuditLogServiceTests` | `Test_V2_ServiceSignatureUsesEnumTypes` | V2 |
| `AuditLogServiceTests` | `Test_SC4_NoSensitiveDataInExceptionMessages` | SC-4 |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| `AuditLogRepositoryTests` (real/test SQL Server instance) | `Test_SC1_WriteAsyncInsertsOneRow` | SC-1 |
| `AuditLogRepositoryTests` | `Test_SC2_DbGeneratedFieldsNotCallerSupplied` | SC-2 |
| `AuditLogRepositoryTests` | `Test_SC3_InvalidTabModuleFailsFast` | SC-3 |
| `AuditLogRepositoryTests` | `Test_V3_OptionalFieldsAcceptNull` | V3 |

### Coverage Target
Changed files must reach ≥ 85% line coverage (project standard).

---

## §14 Rollout

1. Deploy the `dbo.audit_log_ins` migration first (no dependent code can call it until it exists).
2. Deploy `Cachet.ArchiveSync.Core` (enums, interfaces) and `Cachet.ArchiveSync.Api`
   (`AuditLogRepository`/`AuditLogService`, DI registration) together.
3. Deploy `Cachet.ArchiveSync.Worker`'s DI registration for `IAuditLogService` (needed once CAS-001's
   Worker-side delete-outcome write resumes — not itself a caller yet, since CAS-001 is not yet
   implemented).
4. Rollback: reverting the Api/Worker deploy is safe (no data-shape change); the stored procedure
   can remain even if unused. No destructive rollback needed.
5. **Downstream:** once this spec reaches `ready`/merged, re-run `/implement CAS-002` and
   `/implement CAS-001` — both were left `in-progress` specifically pending this service.

---

## §15 Open Questions

All items resolved 2026-09-22 (confirmed with requester before drafting complete) — logged below
for traceability (Karpathy P6), matching CAS-001/CAS-002's own resolution-log convention.

- [x] **Exact `AuditAction`/`AuditResult` enum member names and string values — resolved:**
      PascalCase, no punctuation (e.g. `LoginFailed`, `DeleteFailedLocal`), enum `.ToString()`
      written directly to the `action`/`result` columns — no `[Description]`/explicit string
      mapping needed. CAS-001/CAS-002's prose names (`"Login Failed"`, `"DeleteFailed-Local"`) are
      documentation wording, not the literal stored value.
- [x] **`created_by`/`updated_by` identity value — resolved:** acting *process* identity —
      `api-svc` for Api-originated rows, `worker-svc` for Worker-originated rows — matching CAS-004
      Appendix B's own worked example. `user_id` (FK column) always carries the business-meaningful
      acting/submitting user regardless of which process wrote the row; `created_by`/`updated_by`
      never need to resolve a username for a Worker-executed action with no attached session.

Not depending on either open BRD §12.2 item (O-Drive refresh cadence; drive encryption-at-rest).

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 | Drafted to unblock CAS-002/CAS-001, both stopped mid-`/implement` on this exact gap |
| Reviewer | | | Pending |

---

## §17 Graph Impact

No graphify graph covers application code yet (`graphify-out/` indexes only the BRD/WBS/tooling
documentation corpus, per CAS-001/CAS-002/CAS-004's identical fallback note). This section is
filled from a manual read of CAS-003 (merged schema), CAS-004 (usage convention), and the two
blocked implementation attempts (CAS-001, CAS-002) in this same session.

### God Nodes in Blast Radius
`FR-23: Audit Log` is one of the graph's higher-degree BRD nodes per CAS-004's own §17 framing
(every module writes to it) — this spec is the first piece of *code* to sit behind that node.
Not yet measurable as a code-graph god node since no graphify pass has indexed source yet.

### Communities / Modules Affected
Audit Log (primary — this is its write path). Directly unblocks Auth (CAS-002) and B2 Integration
(CAS-001); indirectly, every future module (Keyword Search, O-Drive Sync, Quarantine, Task
Manager) that will call the same `IAuditLogService` rather than inventing its own audit-log access.
Projects: `Core` (enums, interfaces, `AuditLogEntry` DTO), `Api` (repository + service
implementation, DI registration), `Worker` (DI registration only — first Worker-side *caller* is
CAS-001, not this spec).

### Surprising Connections
None new — this spec is itself the surfaced dependency: both CAS-001 and CAS-002 assumed an
audit-log write mechanism existed (each cites FR-23 as a hard requirement) without either spec
having verified it was actually implemented, which only became visible when `/implement` was run
against real code and grep confirmed no `AuditLog`/`audit_log` C# symbol exists anywhere in `src/`.

### Graphify Queries Run
Graphify not applicable to application code yet — analysis via manual read of CAS-001, CAS-002,
CAS-003, CAS-004, and a `grep` confirming no existing `AuditLog`/`audit_log` C# implementation.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test.
- [ ] All §7 Validations are implemented and tested.
- [ ] §5 `dbo.audit_log_ins` exists in schema and its parameter list matches CAS-004 §5's column
      restatement exactly (no drift from the merged table).
- [ ] §12's "awaited, not fire-and-forget" property actually holds in the implementation — this is
      the property CAS-001/CAS-002 depend on to satisfy their own audit-before-response invariant.
- [ ] §15's two open questions were resolved (not silently defaulted) before this spec moved to
      `ready`.
- [ ] No out-of-scope changes from §4 were made (no `GET /audit-log` endpoint, no retention logic,
      no generic audit-interceptor abstraction).
- [ ] §17 Graph Impact matches actual code changes.

### Verification Report Format
Per template: `CAS-005.review.md` with overall verdict, per-SC verdict table, security gap list,
scope violations.

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage AuditLogService.cs 100% / AuditLogRepository.cs 0% (DB-dependent tests skip-marked, no live SQL Server in this sandbox) | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | New - 8-member enum |
| `src/Cachet.ArchiveSync.Core/Enums/AuditResult.cs` | New - Success/Failure enum |
| `src/Cachet.ArchiveSync.Core/Dtos/AuditLogEntry.cs` | New - sealed record DTO |
| `src/Cachet.ArchiveSync.Core/Interfaces/IAuditLogRepository.cs` | New interface |
| `src/Cachet.ArchiveSync.Core/Interfaces/IAuditLogService.cs` | New interface |
| `src/Cachet.ArchiveSync.Api/Repositories/AuditLogRepository.cs` | New - Dapper impl of dbo.audit_log_ins |
| `src/Cachet.ArchiveSync.Api/Services/AuditLogService.cs` | New - V1 validation, logging, awaited write |
| `src/Cachet.ArchiveSync.Api/Program.cs` | DI registration |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | Added ConnectionStrings:CachetArchiveSync |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | DI registration (no new job logic) |
| `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | Added ConnectionStrings:CachetArchiveSync |
| `src/Cachet.ArchiveSync.Worker/Cachet.ArchiveSync.Worker.csproj` | Added ProjectReference to Api (clean-architecture skill sanctioned) |
| `Database/migrations/20260922_150244__CAS-005-audit-log-write-service.sql` | New migration |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.audit_log_ins.sql` | New SP mirror |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `AuditLogServiceTests` | `Test_V1_InvalidTabModuleRejected` | V-1 |
| `AuditLogServiceTests` | `Test_SC3_InvalidTabModuleFailsFast` | SC-3 |
| `AuditLogServiceTests` | `Test_V2_ServiceSignatureUsesEnumTypes` | V-2 |
| `AuditLogServiceTests` | `Test_V3_OptionalFieldsAcceptNull` | V-3 |
| `AuditLogServiceTests` | `Test_SC4_NoSensitiveDataInExceptionMessages` | SC-4 |
| `AuditLogRepositoryTests` | `Test_SC1_WriteAsyncInsertsOneRow` | SC-1 (skip-marked, no live SQL Server) |
| `AuditLogRepositoryTests` | `Test_SC2_DbGeneratedFieldsNotCallerSupplied` | SC-2 (skip-marked, no live SQL Server) |

### Migration
`20260922_150244__CAS-005-audit-log-write-service.sql`
