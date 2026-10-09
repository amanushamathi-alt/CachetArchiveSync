---
id: CAS-008
target_app: cachet-archive-sync
status: ready
owner: devarajan@tracecounts.org
created: 2026-09-22
priority: P1
related_specs: [CAS-001, CAS-002, CAS-005, CAS-006, CAS-007]
touches:
  database: true
  api: true
  ui: false
  worker: false
  b2: true
  config: false
  security: true
migrations:
  - 20260922_183000__CAS-008-quarantine-permanent-delete-api.sql
  - 20260922_190000__CAS-008-quarantine-permanent-delete-api-addendum.sql
---

# CAS-008 — Quarantine List and Permanent Delete API

WBS Task 18 (Quarantine/Permanent Delete API). Implements FR-14, FR-15, FR-16, FR-17. Architecture
basis: `CACHET_API_Design_Specification_v1.md` §6 (Quarantine and Permanent Delete Module), BRD
§7.4 "Quarantine Management," BRD §9 (irreversibility as an accepted business decision), and
CAS-006 §0/§5 (which created `dbo.quarantine_entry`'s schema, including the closed `status` enum,
specifically so this spec could reuse it without a schema change).

---

## §0 Provenance Note (read this first)

CAS-006 already implements the **add** path for Quarantine (FR-09/FR-15) end to end, but only under
the route `POST /search/results/quarantine` (CAS-006 §6.3, `SearchController.AddToQuarantine`,
backed by `IQuarantineService.AddAsync`). The API design doc (§6.2) explicitly anticipates this and
recommends the literal `POST /quarantine` route "call through to this same endpoint internally (or
be literally the same endpoint) rather than maintaining two parallel 'add to quarantine' code
paths" (§6.2 note). This spec adds the missing literal route and **reuses
`IQuarantineService.AddAsync` unchanged** — it does not re-implement add logic (Karpathy P4: no new
abstraction/duplicate code path for something that already exists once).

This spec is the **first** to implement:

- `GET /quarantine` (list — FR-14, API spec §6.1) — new.
- `POST /quarantine/delete` (Permanent Delete — FR-16/FR-17, API spec §6.4) — new.
- The `dbo.quarantine_entry` **status transition** to `'Deleted'` (schema already supports this;
  CAS-006 only ever wrote `'Active'` — §0 of CAS-006).

**Deliberately not built here:** `POST /quarantine/release` (API spec §6.3) — **resolved 2026-09-22
(R. Devarajan, TraceCounts): deferred to a follow-up spec, not added to this one.** The task
instructions for this spec name exactly three endpoints (list, add, delete); release is a real,
confirmed-for-build capability in the API design doc but is a materially different operation
(non-destructive catalog undo, no `CachetFullAccess` gate, a not-yet-existing `QuarantineRelease`
audit action) — pulling it in here would be scope creep against what was asked. `dbo.quarantine_entry`
is already release-ready (CAS-006's schema), so nothing is lost by sequencing it into a future spec
(see §4, §15).

---

## §1 Summary

Implements FR-14, FR-15, FR-16, and FR-17: `GET /quarantine` lists every `Active` row in the shared
Quarantine Queue (FR-14, one queue, not per-tab, per BRD v2.2 §12.1 item 9 — Quarantine is Keyword
Search-only, so "shared across tabs" here means shared across every user's view of the one queue,
not shared across Keyword Search and O-Drive Sync); `POST /quarantine` adds file(s) to the queue as
a local cataloging action only, never touching the local mirror cache or B2 (FR-15) — implemented
as a thin route alias over CAS-006's existing `IQuarantineService.AddAsync`; and
`POST /quarantine/delete` permanently deletes one or more quarantined files from **both** the local
mirror cache and Backblaze B2 via API (FR-16), gated by a server-side `confirm: true` backstop and
with no recovery path once confirmed (FR-17) — a deliberate, accepted, irreversible business
decision (BRD §9), the same posture already implemented for O-Drive Sync's Purge action (CAS-007).

---

## §2 User Story

As a **TraceCounts reviewer**, I want to see every file I've sent to Quarantine in one shared queue,
add more files to it as a simple bookkeeping action, and permanently delete files I've reviewed and
decided to remove — from both the local cache and Backblaze B2 — with an explicit confirmation step
so that I never delete something by accident, knowing that once confirmed, the deletion cannot be
undone.

---

## §3 In-Scope

- `GET /quarantine` — list every `dbo.quarantine_entry` row with `status = 'Active'`, returning each
  file's stable identity (`quarantine_entry_id`), name, path, `quarantinedOn`, and the
  `match_status_snapshot` taken at add-time (API spec §6.1). Not paginated (queue is a small,
  curated subset — §6.1).
- `POST /quarantine` **[audited]** — literal route alias for the add operation CAS-006 already
  built; delegates directly to `IQuarantineService.AddAsync` (no new service/logic).
- `POST /quarantine/delete` **[audited]** — for each requested `quarantine_entry_id` (the stable
  identity returned by `GET /quarantine`, per §1.7's convention): delete every version of the file's
  B2 object (`IB2Client.ListFileVersionsAsync` + `DeleteFileVersionAsync`, reusing CAS-007's
  `SyncPurgeTaskProcessor.DeleteAllVersionsAsync` pattern), soft-delete the corresponding
  `dbo.file_index` row (`is_active = 0`, via `IFileIndexRepository.UpdateAsync`), transition the
  `dbo.quarantine_entry` row to `status = 'Deleted'`, and write an audit row — all before returning
  the per-file result in the response (FR-16, FR-17, FR-23). Requires `confirm: true` in the
  request body; every file in a batch is attempted independently regardless of an earlier file's
  failure (same posture as CAS-007's Purge and the API design doc's §6.4 confirmed decision).
- New repository methods on `IQuarantineEntryRepository`: `ListActiveAsync` (backs `GET /quarantine`)
  and a delete-transition method (backs `POST /quarantine/delete`) — extending, not replacing,
  CAS-006's existing add-only interface.
- New `QuarantineController` (`Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs`) owning
  all three routes below — kept separate from `SearchController` (§15, resolved 2026-09-22) since
  it is a distinct resource with a distinct auth-policy mix (`CachetAuthenticated` for list/add,
  `CachetFullAccess` for delete), matching this project's existing precedent of one controller per
  resource (`SearchController` vs. `SyncController`) rather than per shared implementation detail.
- New stored procedures: `dbo.quarantine_entry_lst` (Active rows) and
  `dbo.quarantine_entry_delete_upd` (status → `Deleted`).
- `AuditAction` enum members reused, not added: `PermanentDeleteRequested`, `Deleted`,
  `DeleteFailedLocal`, `DeleteFailedB2` already exist in
  `Cachet.ArchiveSync.Core/Enums/AuditAction.cs` (present since before CAS-006, unused by any spec
  until now) — this spec is their first consumer.

---

## §4 Out-of-Scope

- **`POST /quarantine/release`** (API spec §6.3) — confirmed-for-build in the API design doc but not
  requested by this spec's task instructions. **Resolved 2026-09-22 (R. Devarajan, TraceCounts):**
  deferred to a future spec, not this one — see §0.
- **Blazor Server UI** (`touches.ui: false`) — no `.razor` components; the Web project is untouched.
  BRD UI/UX spec §4.2/§4.3 (Quarantine Queue screen) exists for reference but its implementation is
  a separate, future Web-kind spec.
- **Re-implementing Quarantine add logic** — `POST /quarantine` is a route alias over CAS-006's
  existing `IQuarantineService.AddAsync`; this spec does not touch that service's validation rules
  (§7 V6–V8 in CAS-006 already cover them) beyond exposing the second route.
- **O-Drive Sync's Copy/Update/Purge actions** (CAS-007) — a separate, already-implemented action
  set; this spec's Permanent Delete reuses CAS-007's B2 multi-version-delete _pattern_ but does not
  modify `SyncActionService`, `SyncPurgeTaskProcessor`, or any `sync_result`/O-Drive Sync code.
- **Task Manager endpoints** (`GET /tasks`, etc., FR-19–FR-22a) — Permanent Delete is synchronous,
  not queued (API spec §1.5's confirmed table), so no `dbo.task_queue` row is created by this spec.
- **Drive encryption-at-rest** (BRD §12.2 open item) — a physical O-Drive/B2-mirror-population
  concern, unrelated to this API surface. Not blocking this spec.
- **Retention/archival** of `Deleted`-status `quarantine_entry` rows — rows are never hard-deleted
  from `dbo.quarantine_entry` itself (only their `status` transitions), consistent with this
  project's "never hard-delete audit-adjacent data" posture (CAS-003/CAS-004/CAS-006 precedent).

---

## §5 Database Objects

### Tables / Columns

No new tables. `dbo.quarantine_entry` (CAS-006 §5) already has the closed `status` enum
(`'Active'`/`'Released'`/`'Deleted'`) created for exactly this purpose — no `ALTER TABLE` needed.

### Stored Procedures

| Name                                                    | Schema | Action | Notes                                                                                                                                                                                                                                                                                                 |
| ------------------------------------------------------- | ------ | ------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `dbo.quarantine_entry_lst`                              | dbo    | lst    | All rows `WHERE status = 'Active'`, joined to `dbo.file_index` for `name`/`path` display fields — backs `GET /quarantine` (§6). Not paginated (§6, API spec §6.1).                                                                                                                                    |
| `dbo.quarantine_entry_delete_upd`                       | dbo    | upd    | Sets `status = 'Deleted'`, `updated_by`, `updated_at` for one `quarantine_entry_id` where `status = 'Active'` — backs the per-file delete step (§6). Returns 0 rows affected if the entry is not `Active` (already deleted/released or unknown id), which the repository maps to a not-found outcome. |
| `dbo.quarantine_entry_ins`                              | dbo    | ins    | Reused verbatim from CAS-006 — unchanged.                                                                                                                                                                                                                                                             |
| `dbo.file_index_soft_delete_upd` / `dbo.file_index_upd` | dbo    | upd    | Reused verbatim from CAS-006/CAS-007's `IFileIndexRepository.UpdateAsync(isActive: false)` path.                                                                                                                                                                                                      |
| `dbo.audit_log_ins`                                     | dbo    | ins    | Reused from CAS-005.                                                                                                                                                                                                                                                                                  |

### Migration Strategy

`db-author` writes, at `/implement` time:

- `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-008-quarantine-permanent-delete-api.sql` — idempotent,
  adds `dbo.quarantine_entry_lst` and `dbo.quarantine_entry_delete_upd` only. No table DDL.
- `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_lst.sql`,
  `dbo.quarantine_entry_delete_upd.sql` — declarative mirrors.

No data migration.

---

## §6 API Endpoints

### Endpoint: `GET /quarantine`

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]` — a plain authenticated session; listing the
queue is a read, not a B2-write action (API spec §6.1 lists only `401` as an error).

**Async:** No.

**Response `200 OK`:**

```json
{
  "items": [
    {
      "fileId": 100801,
      "name": "agreement-004.pdf",
      "path": "contracts/2019/agreement-004.pdf",
      "quarantinedOn": "2026-09-09T14:10:00Z",
      "status": "Flagged"
    }
  ]
}
```

`fileId` is the `quarantine_entry_id` (the file's stable identity while quarantined — API spec §1.7,
§6.1) — a different value than the `search_result_id` used to add it. `status` here is
`match_status_snapshot`, frozen at add-time (CAS-006 §5's design note) — not re-derived from any
later search.

Not paginated (§6.1) — every `Active` row is returned in one response.

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Success (possibly an empty `items` array) |
| 401 | No valid session |

### Endpoint: `POST /quarantine` **[audited]**

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]` — same policy as
`POST /search/results/quarantine` (CAS-006 §6) — add is a cataloging action only, not a B2-write
action, so `CachetFullAccess` is not required (consistent with CAS-006's rationale that Read Only
sessions may fully use Keyword Search/Quarantine-add).

**Async:** No.

**Request:**

```json
{ "taskId": 100234, "fileIds": [100501] }
```

Same shape and semantics as CAS-006 §6's `QuarantineAddRequest` — `fileIds` are `search_result_id`
values scoped to `taskId` (API spec §6.2's note that `POST /quarantine`'s `fileIds` are
`search_result_id`-scoped, identical to §4.3).

**Response `200 OK`:** identical shape to CAS-006 §6's `POST /search/results/quarantine` response
(`items`/`warnings`) — this endpoint calls the exact same `IQuarantineService.AddAsync` method.

**Sequence:** the new `QuarantineController` (§15 — resolved 2026-09-22, R. Devarajan: a dedicated
controller, not folded into `SearchController`) calls `IQuarantineService.AddAsync` directly; no new
validation, no new audit write (CAS-006's `QuarantineAdd` audit write already fires inside
`AddAsync`, before the response, regardless of which route/controller invoked it).

**Status codes:** identical to CAS-006 §6's `POST /search/results/quarantine` table (200, 404
`task-not-found`, 404 `file-not-found`, 409 `file-already-quarantined`, 422 `validation-failed`,
401).

### Endpoint: `POST /quarantine/delete` **[audited]** — Permanent Delete

**Auth:** `[Authorize(Policy = "CachetFullAccess")]` — CAS-002 §12's confirmed mapping: Permanent
Delete is a B2-write action and must be blocked for a `Read Only` session at the authorization
policy layer (same pattern as CAS-007's `SyncController` for Copy/Update/Purge). A `Read Only`
session never reaches this action's code — the framework returns `403` before the controller
method runs, which is what satisfies the API design doc's `403 b2-capability-denied` error (§12 —
no additional runtime per-request B2-key-capability check is made inside the service, since the
underlying `IB2Client` call always uses the server-held **system** key regardless of the caller's
own B2 key, per CAS-001 §6.1; capability enforcement happens once, at the policy layer).

**Async:** No — synchronous per API spec §1.5's confirmed table (Permanent Delete is described as a
direct call sequence, not queued).

**Request:**

```json
{
  "fileIds": [100801],
  "confirm": true
}
```

`fileIds` are `quarantine_entry_id` values, as returned by `GET /quarantine` (§1.7) — **not** the
`search_result_id` values used at add-time. `confirm` must be `true`; missing/`false` rejects the
**entire** request before any deletion is attempted (FR-17's server-side backstop).

**Response `200 OK`:**

```json
{
  "results": [
    { "fileId": 100801, "result": "Success" },
    { "fileId": 100802, "result": "Failed", "reason": "b2-object-not-found" }
  ]
}
```

Each file is attempted independently — a failure on one file does not block or roll back deletion
of the others (API spec §6.4, confirmed 2026-09-09). Per file, on success: B2 delete (all versions)
→ local mirror soft-delete → `quarantine_entry.status = 'Deleted'` → audit write, in that order,
before this response returns. On a per-file failure, an audit row is still written with a
non-success result — failures are never silently dropped from the log (FR-23).

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Request processed — see `results[]` for per-file outcomes (may include per-file `Failed` entries) |
| 422 | `confirmation-required` — `confirm` missing or `false`; whole request rejected |
| 422 | `validation-failed` — `fileIds` empty |
| 404 | `file-not-found` — **whole-request** 404 only if every requested `fileId` fails to resolve to any `quarantine_entry` row (unknown id, or `status` already `Released`/`Deleted`); an unresolvable id inside an otherwise-valid batch appears as a per-file `Failed` result instead. **Resolved 2026-09-22 (R. Devarajan, TraceCounts):** this per-file shape is confirmed, matching CAS-007 Purge's precedent for the identical failure mode (§15). |
| 403 | `b2-capability-denied` — enforced by `CachetFullAccess` policy failure, not a per-request body |
| 401 | No valid session |

---

## §7 Validations

| ID  | Rule                                                                                                                                                                                                                                                                         | On Failure                                                                                                                                                     | Test                                                                         |
| --- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------- |
| V1  | `POST /quarantine/delete` `fileIds` must be non-empty                                                                                                                                                                                                                        | 422 `validation-failed`                                                                                                                                        | `Test_V1_DeleteEmptyFileIdsRejected`                                         |
| V2  | `POST /quarantine/delete` `confirm` must be `true`; checked before any other validation or deletion attempt                                                                                                                                                                  | 422 `confirmation-required`                                                                                                                                    | `Test_V2_DeleteWithoutConfirmRejectedBeforeAnyDeletion`                      |
| V3  | `POST /quarantine/delete` each `fileId` must resolve to a `dbo.quarantine_entry` row with `status = 'Active'`                                                                                                                                                                | Per-file `Failed` result (`reason: "file-not-found"`); whole-request `404` only if every file fails this way — **resolved 2026-09-22, R. Devarajan (§6, §15)** | `Test_V3_DeleteUnknownOrNonActiveEntryReportedPerFile`                       |
| V4  | `POST /quarantine/delete` deletes every B2 version of the file's object, not just the current version (reuses CAS-007's `DeleteAllVersionsAsync` pattern)                                                                                                                    | N/A — proven by asserting all versions are targeted                                                                                                            | `Test_V4_DeletePurgesAllB2VersionsNotJustCurrent`                            |
| V5  | `POST /quarantine/delete` on B2 delete failure for a file: `dbo.file_index`/`dbo.quarantine_entry` are left unchanged for that file, and a `DeleteFailedB2` audit row is written                                                                                             | Per-file `Failed` result                                                                                                                                       | `Test_V5_B2DeleteFailureLeavesLocalStateUnchangedAndAudits`                  |
| V6  | `POST /quarantine/delete` on local soft-delete failure after a successful B2 delete: a `DeleteFailedLocal` audit row is written (the B2 object is already gone at this point — the file is not resurrected on B2 to "undo" this, consistent with FR-17's no-rollback design) | Per-file `Failed` result                                                                                                                                       | `Test_V6_LocalFailureAfterB2SuccessAuditsDeleteFailedLocal`                  |
| V7  | `GET /quarantine` returns only `status = 'Active'` rows — never `Released` or `Deleted` entries                                                                                                                                                                              | N/A — proven by absence                                                                                                                                        | `Test_V7_ListExcludesNonActiveEntries`                                       |
| V8  | `POST /quarantine` (new route) produces byte-identical validation/audit behavior to `POST /search/results/quarantine` for the same request (proves the route is a true alias, not a divergent reimplementation)                                                              | N/A — behavioral equivalence                                                                                                                                   | `Test_V8_PostQuarantineRouteDelegatesToSameServiceAsSearchResultsQuarantine` |

---

## §8 UI / UX

N/A — `touches.ui: false`. No `.razor` components; the Web project is untouched by this spec. BRD
UI/UX spec §4.2/§4.3 (Quarantine Queue / Permanent Delete confirmation dialog) exists for reference
but its implementation is a separate, future Web-kind spec that consumes these endpoints.

---

## §9 Configuration Changes

N/A — no new configuration keys. Reuses `B2:SystemKeyId`/`B2:SystemApplicationKey`/`B2:BucketName`
(CAS-001 §9), untouched here.

---

## §10 Success Criteria

| ID   | Criterion                                                                                                                                                                                                                                                                                                                 | Test                                                               |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------ |
| SC-1 | `GET /quarantine` returns `200` with every `Active` `quarantine_entry` row, correctly shaped per §6, and excludes `Released`/`Deleted` rows                                                                                                                                                                               | `Test_SC1_ListReturnsOnlyActiveEntries`                            |
| SC-2 | `POST /quarantine` with a valid request behaves identically (same response shape, same audit write) to `POST /search/results/quarantine` for the same input                                                                                                                                                               | `Test_SC2_PostQuarantineAliasProducesSameResultAsExistingEndpoint` |
| SC-3 | `POST /quarantine/delete` with `confirm: true` and valid `fileIds` deletes every B2 version of each file, soft-deletes the corresponding `dbo.file_index` row, transitions `quarantine_entry.status` to `'Deleted'`, writes a `Deleted` audit row before the response, and returns `200` with a `Success` result per file | `Test_SC3_DeleteSucceedsAndAuditsBeforeResponse`                   |
| SC-4 | `POST /quarantine/delete` without `confirm: true` returns `422 confirmation-required` and no B2 call, local mutation, or `quarantine_entry` status change occurs for any requested file                                                                                                                                   | `Test_SC4_MissingConfirmMakesNoStateChange`                        |
| SC-5 | `POST /quarantine/delete` on a batch where one file's B2 delete fails and another succeeds: the failing file's `quarantine_entry` remains `Active` and is audited `DeleteFailedB2`, while the succeeding file transitions to `Deleted` and is audited `Deleted` — both in the same response                               | `Test_SC5_PartialBatchFailureIsolatedPerFile`                      |
| SC-6 | A `Read Only`-capability session receives `403` from `POST /quarantine/delete` before any deletion logic runs                                                                                                                                                                                                             | `Test_SC6_ReadOnlySessionBlockedFromPermanentDelete`               |
| SC-7 | No B2 application key, session token/cookie value, or raw file content ever appears in a log line across `GET /quarantine`, `POST /quarantine`, or `POST /quarantine/delete`                                                                                                                                              | `Test_SC7_NoCredentialOrContentLeakageInLogs`                      |
| SC-8 | Once a `quarantine_entry` row is `Deleted`, a repeat `POST /quarantine/delete` for the same `fileId` reports a per-file `Failed`/`file-not-found` result, not a second successful delete                                                                                                                                  | `Test_SC8_RepeatDeleteOfAlreadyDeletedEntryFailsCleanly`           |

---

## §11 Telemetry

```csharp
_logger.LogInformation("Quarantine list requested by user {UserId}: {ActiveCount} active entries", userId, activeCount);
_logger.LogInformation(
    "Permanent Delete batch by user {UserId}: {SucceededCount} succeeded, {FailedCount} failed",
    userId, succeededCount, failedCount);
_logger.LogWarning("Permanent Delete failed for quarantine entry {QuarantineEntryId}: {ErrorSummary}", quarantineEntryId, errorSummary);
```

- Never log: the B2 application key or any part of it, a session token/cookie value, full file
  content, or raw archive file paths beyond what the audit log (FR-23) already records.

---

## §12 Security Notes

### B2 Credential Handling

`POST /quarantine/delete` uses the same **system-level** `IB2Client` (CAS-001 §6.1) already used by
CAS-006's cross-check and CAS-007's Purge — the server-held system key, never a per-user key. No B2
key or key ID appears in any request/response DTO in §6, and none is logged (§11).

### Audit Trail

Two audited endpoints (FR-23), both writing **before** their response:

- `POST /quarantine` — `action: QuarantineAdd` (reused from CAS-006's `AddAsync`, unchanged).
- `POST /quarantine/delete` — per file, on success: `action: Deleted`, `tab_module: 'Quarantine'`,
  `file_path` populated, `result: Success`; on failure: `action: DeleteFailedB2` or
  `DeleteFailedLocal` (whichever step failed), `result: Failure`, `result_detail` populated. Every
  file in the batch is audited, success or failure — none are silently dropped (§6, NFR target #11).

### Session / Auth Requirements

`GET /quarantine` and `POST /quarantine`: `[Authorize(Policy = "CachetAuthenticated")]`.
`POST /quarantine/delete`: `[Authorize(Policy = "CachetFullAccess")]` (CAS-002 §12's confirmed
mapping — Permanent Delete is one of the four actions, alongside Copy/Update/Purge, that a
`Read Only` session must never reach). Not blocked by BRD §12.2 — the login method underlying both
policies is already resolved (CAS-002); drive encryption-at-rest is unrelated to this API surface.

### Irreversibility (FR-17)

No soft-delete, undo, grace period, or recovery affordance exists or may be added to
`POST /quarantine/delete` without an explicit spec change and new client sign-off (CLAUDE.md's
binding rule; BRD §9's accepted, deliberate business decision). The `quarantine_entry.status =
'Deleted'` transition is a catalog-row status change, not a soft-delete of the _file_ — the
underlying file is genuinely, irreversibly gone from both B2 and the local mirror once this
endpoint returns `Success` for it.

---

## §13 Test Plan

### Unit Tests

| Class                                                                                                                              | Method                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     | Proves                        |
| ---------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------- |
| `QuarantineListServiceTests`                                                                                                       | `Test_SC1_ListReturnsOnlyActiveEntries`, `Test_V7_ListExcludesNonActiveEntries`                                                                                                                                                                                                                                                                                                                                                                                                                                                            | SC-1, V7                      |
| `QuarantineDeleteServiceTests` (NSubstitute `IB2Client`, `IQuarantineEntryRepository`, `IFileIndexRepository`, `IAuditLogService`) | `Test_V1_DeleteEmptyFileIdsRejected`, `Test_V2_DeleteWithoutConfirmRejectedBeforeAnyDeletion`, `Test_V3_DeleteUnknownOrNonActiveEntryReportedPerFile`, `Test_V4_DeletePurgesAllB2VersionsNotJustCurrent`, `Test_V5_B2DeleteFailureLeavesLocalStateUnchangedAndAudits`, `Test_V6_LocalFailureAfterB2SuccessAuditsDeleteFailedLocal`, `Test_SC3_DeleteSucceedsAndAuditsBeforeResponse`, `Test_SC4_MissingConfirmMakesNoStateChange`, `Test_SC5_PartialBatchFailureIsolatedPerFile`, `Test_SC8_RepeatDeleteOfAlreadyDeletedEntryFailsCleanly` | V1–V6, SC-3, SC-4, SC-5, SC-8 |
| `QuarantineAddRouteTests`                                                                                                          | `Test_V8_PostQuarantineRouteDelegatesToSameServiceAsSearchResultsQuarantine`, `Test_SC2_PostQuarantineAliasProducesSameResultAsExistingEndpoint`                                                                                                                                                                                                                                                                                                                                                                                           | V8, SC-2                      |
| `LogHygieneTests` (extended)                                                                                                       | `Test_SC7_NoCredentialOrContentLeakageInLogs`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              | SC-7                          |

### Integration Tests

| Class                                                                       | Method                                                                                                                                                                                      | Proves              |
| --------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------- |
| `QuarantineControllerTests` (`WebApplicationFactory`)                       | `Test_SC1_ListReturnsOnlyActiveEntries` (HTTP-level), `Test_SC6_ReadOnlySessionBlockedFromPermanentDelete`, `Test_SC2_PostQuarantineAliasProducesSameResultAsExistingEndpoint` (HTTP-level) | SC-1, SC-6, SC-2    |
| `QuarantineEntryRepositoryTests` (real SQL Server, extended from CAS-006's) | Insert/list-active/delete-transition round-trip against `dbo.quarantine_entry`                                                                                                              | Supports SC-3, SC-8 |

### Coverage Target

Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. Deploy the database migration first (`dbo.quarantine_entry_lst`, `dbo.quarantine_entry_delete_upd`)
   — additive-only, no existing table or procedure altered.
2. Deploy `Core` (interface additions) and `Api` (new controller routes/service methods) together
   — both endpoints are synchronous; no Worker deployment is required for this spec.
3. Rollback: both new stored procedures are additive — dropping them is safe pre-traffic.
   Post-traffic, do not drop them once any `Deleted`-status row exists that depends on this code
   path having run; roll back code only, per this project's established posture.

---

## §15 Open Questions

- [x] **`POST /quarantine/release` (§6.3) — resolved 2026-09-22 (R. Devarajan, TraceCounts):**
      deferred to a follow-up spec. It is a build-worthy, confirmed capability per the API design
      doc, but a materially different (non-destructive, non-`CachetFullAccess`-gated) operation not
      named in this spec's task instructions. `dbo.quarantine_entry`'s schema is already
      release-ready (CAS-006), so no rework is needed when that follow-up spec is written — see §0,
      §4.
- [x] **Unknown/non-Active `fileId` in `POST /quarantine/delete` — resolved 2026-09-22 (R.
      Devarajan, TraceCounts):** per-file `Failed` result (`reason: "file-not-found"`), with a
      whole-request `404` only when _every_ requested file fails this way — matching CAS-007
      Purge's existing precedent for the identical failure mode, and consistent with the API design
      doc's own "every file attempted independently" framing for this endpoint (§6.4).
- [x] **Controller placement — resolved 2026-09-22 (R. Devarajan, TraceCounts):** a new
      `QuarantineController` owns all three endpoints, kept separate from `SearchController` since
      Quarantine is a distinct resource with a distinct auth-policy mix — matching this project's
      existing `SearchController`/`SyncController` split by resource, not by shared implementation
      detail.
- Not blocked by either open BRD §12.2 item (login method already resolved; drive
  encryption-at-rest is unrelated to this API surface).

All open items resolved as of 2026-09-22 — no remaining blockers to `status → ready`.

---

## §16 Sign-off

| Role     | Name                                        | Date       | Notes   |
| -------- | ------------------------------------------- | ---------- | ------- |
| Author   | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 |         |
| Reviewer |                                             |            | Pending |

---

## §17 Graph Impact

**Graphify not queried live for this spec** — `graphify-out/` exists (built 2026-09-10, refreshed
since CAS-006/CAS-007) but this session did not have `graphify` on `PATH` to run a live query;
analysis below is a manual read of the BRD (§7 FR-14–FR-17), the API design doc (§6), and the
existing CAS-006/CAS-007 codebase (`QuarantineService`, `IQuarantineEntryRepository`,
`SyncPurgeTaskProcessor`, `AuditAction` enum), consistent with this project's documented fallback
for when graphify cannot be invoked from the current environment.

### God Nodes in Blast Radius

None identified via manual read beyond what CAS-006 §17 already established for
`dbo.quarantine_entry`/`FR-09` — this spec extends that same node set (adds `FR-14`/`FR-16`/`FR-17`
edges to it) rather than introducing a new high-degree node.

### Communities / Modules Affected

- **Community 25 ("Quarantine")**, per CAS-006 §17 — this spec completes the FR-14–FR-17 portion of
  that community CAS-006 explicitly deferred (§0/§4 there).
- Projects touched: `Api` only (`Core` interface/DTO additions, `Api` controller/service/repository
  additions). **Not** `Worker`, **not** `Web`, **not** any O-Drive Sync (CAS-007) code — reused only
  as a _pattern_ (`DeleteAllVersionsAsync`), not a shared dependency.

### Surprising Connections

- `AuditAction.PermanentDeleteRequested`, `Deleted`, `DeleteFailedLocal`, `DeleteFailedB2` were
  already present in the enum before CAS-006 or CAS-007 touched it — evidence this module's shape
  was anticipated early (likely at CAS-005's initial audit-log design) even though no spec
  implemented it until now. `PermanentDeleteRequested` is defined but this spec's §6 design (based
  on the API design doc's exact §6.4 sequence — B2 delete → local delete → catalog update → audit,
  per file, no separate "batch submitted" step) has no natural place to use it, since the endpoint
  is synchronous and never separately "requests" before acting. Flagged rather than forcing a use
  for an enum member that may simply be unused dead vocabulary — reviewer to confirm this is
  acceptable or to identify a use this spec missed.
- CAS-006 §17's own "AMBIGUOUS edge" note (`FR-18` → `POST /tasks/{taskId}/cancel`) does not extend
  to this spec — Permanent Delete's confirmed synchronous design (API spec §1.5) means no task-queue
  interaction exists here to be ambiguous about.

### Graphify Queries Run

None — `graphify` was not available on `PATH` in this session; see note above. A future session
with graphify vendored/available should run:

```
python -m graphify query "Quarantine list Permanent Delete FR-14 FR-16 FR-17" --budget 1500
python -m graphify explain "QuarantineEntry"
```

and reconcile any findings against this manual analysis before `status → ready`, per this project's
"graphify before any spec" rule once the tool is actually invocable.

---

## §18 Verification Plan

### Reviewer Checklist

The reviewer will independently verify each of the following. Mark N/A only if the section itself
is N/A.

- [ ] All §10 Success Criteria (SC-1–SC-8) have a passing test.
- [ ] All §7 Validations (V1–V8) are implemented and tested.
- [ ] `GET /quarantine` requires only `CachetAuthenticated`; `POST /quarantine/delete` requires
      `CachetFullAccess` (§6, §12) — a `Read Only` session must receive `403` before any deletion
      logic executes.
- [ ] `POST /quarantine` produces identical behavior to `POST /search/results/quarantine` for the
      same input — no divergent second implementation of add logic (§0, §4, V8).
- [ ] `POST /quarantine/delete` writes a `Deleted` (success) or `DeleteFailedB2`/`DeleteFailedLocal`
      (failure) audit row **per file, before** the response returns (§12).
- [ ] Every version of a deleted file's B2 object is removed, not just the current version (§6, V4).
- [ ] No soft-delete, undo, or recovery affordance was added to `POST /quarantine/delete` (§12
      Irreversibility — binding project rule).
- [ ] `dbo.quarantine_entry` rows are never hard-deleted — only their `status` transitions.
- [ ] No B2 application key, session token, or raw file content appears in any log line (§11, SC-7).
- [ ] §17 Graph Impact's manual analysis is still consistent with actual code changes — no
      unintended `Worker`/`Web`/O-Drive Sync changes.
- [ ] No out-of-scope changes from §4 were made (no `POST /quarantine/release`, no UI, no Task
      Manager/queued-task code for Permanent Delete).

### Verification Report Format

Per template: `CAS-008.review.md` with overall verdict, per-SC verdict table, security gap list,
scope violations.

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage QuarantineListService 100%, QuarantineController 100%, QuarantinePermanentDeleteService 97.6%, QuarantineEntryRepository 0% (no live SQL Server, disclosed CAS-006/007 precedent), interfaces 66.7% (record-generated members, same precedent) | Endpoints ✓

### Files Changed

| File                                                                                           | Change                                                                                                        |
| ---------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| `Database/migrations/20260922_183000__CAS-008-quarantine-permanent-delete-api.sql`             | New — dbo.quarantine_entry_lst, dbo.quarantine_entry_delete_upd                                               |
| `Database/migrations/20260922_190000__CAS-008-quarantine-permanent-delete-api-addendum.sql`    | New — dbo.quarantine_entry_get (addendum, needed for delete-path file_index_id resolution)                    |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_lst.sql`                    | New mirror                                                                                                    |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_delete_upd.sql`             | New mirror                                                                                                    |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_get.sql`                    | New mirror (addendum)                                                                                         |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineEntryListRow.cs`                                   | New                                                                                                           |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineEntryForDeleteRow.cs`                              | New                                                                                                           |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineListItem.cs`                                       | New                                                                                                           |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineListResponse.cs`                                   | New                                                                                                           |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineDeleteRequest.cs`                                  | New                                                                                                           |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineDeleteResultItem.cs`                               | New                                                                                                           |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineDeleteResponse.cs`                                 | New                                                                                                           |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineListService.cs`                             | New                                                                                                           |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantinePermanentDeleteService.cs`                  | New                                                                                                           |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineEntryRepository.cs`                         | Modified — added ListActiveAsync, GetActiveForDeleteAsync, TryTransitionToDeletedAsync                        |
| `src/Cachet.ArchiveSync.Api/Services/QuarantineListService.cs`                                 | New                                                                                                           |
| `src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs`                      | New — per-file B2 delete-all-versions, local soft-delete, status transition, audit                            |
| `src/Cachet.ArchiveSync.Api/Repositories/QuarantineEntryRepository.cs`                         | Modified — added ListActiveAsync, GetActiveForDeleteAsync, TryTransitionToDeletedAsync                        |
| `src/Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs`                               | New — GET quarantine, POST quarantine (alias to existing IQuarantineService.AddAsync), POST quarantine/delete |
| `src/Cachet.ArchiveSync.Api/Program.cs`                                                        | Modified — DI registration for IQuarantineListService, IQuarantinePermanentDeleteService                      |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantineListServiceTests.cs`                               | New                                                                                                           |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantinePermanentDeleteServiceTests.cs`                    | New                                                                                                           |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantineAddRouteTests.cs`                                  | New                                                                                                           |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantineControllerTests.cs`                                | New                                                                                                           |
| `src/Cachet.ArchiveSync.Api.Tests/LogHygieneTests.cs`                                          | Modified — extended with Test_SC7_NoCredentialOrContentLeakageInLogs                                          |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/Quarantine/QuarantineApiWebApplicationFactory.cs` | New                                                                                                           |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/QuarantineControllerTests.cs`                     | New                                                                                                           |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/QuarantineEntryRepositoryTests.cs`                | Modified — 3 new Skip-marked round-trip tests (no live SQL Server in sandbox)                                 |

### Tests Added

| Class                                                     | Method                                                                       | Covers                                    |
| --------------------------------------------------------- | ---------------------------------------------------------------------------- | ----------------------------------------- |
| `QuarantineListServiceTests`                              | `Test_SC1_ListReturnsOnlyActiveEntries`                                      | SC-1                                      |
| `QuarantineListServiceTests`                              | `Test_V7_ListExcludesNonActiveEntries`                                       | V-7                                       |
| `QuarantineAddRouteTests`                                 | `Test_SC2_PostQuarantineAliasProducesSameResultAsExistingEndpoint`           | SC-2                                      |
| `QuarantineAddRouteTests`                                 | `Test_V8_PostQuarantineRouteDelegatesToSameServiceAsSearchResultsQuarantine` | V-8                                       |
| `QuarantinePermanentDeleteServiceTests`                   | `Test_SC3_DeleteSucceedsAndAuditsBeforeResponse`                             | SC-3                                      |
| `QuarantinePermanentDeleteServiceTests`                   | `Test_SC4_MissingConfirmMakesNoStateChange`                                  | SC-4                                      |
| `QuarantinePermanentDeleteServiceTests`                   | `Test_SC5_PartialBatchFailureIsolatedPerFile`                                | SC-5                                      |
| `QuarantinePermanentDeleteServiceTests`                   | `Test_SC8_RepeatDeleteOfAlreadyDeletedEntryFailsCleanly`                     | SC-8                                      |
| `QuarantinePermanentDeleteServiceTests`                   | `Test_V1_DeleteEmptyFileIdsRejected`                                         | V-1                                       |
| `QuarantinePermanentDeleteServiceTests`                   | `Test_V2_DeleteWithoutConfirmRejectedBeforeAnyDeletion`                      | V-2                                       |
| `QuarantinePermanentDeleteServiceTests`                   | `Test_V3_DeleteUnknownOrNonActiveEntryReportedPerFile`                       | V-3                                       |
| `QuarantinePermanentDeleteServiceTests`                   | `Test_V4_DeletePurgesAllB2VersionsNotJustCurrent`                            | V-4                                       |
| `QuarantinePermanentDeleteServiceTests`                   | `Test_V5_B2DeleteFailureLeavesLocalStateUnchangedAndAudits`                  | V-5                                       |
| `QuarantinePermanentDeleteServiceTests`                   | `Test_V6_LocalFailureAfterB2SuccessAuditsDeleteFailedLocal`                  | V-6                                       |
| `LogHygieneTests`                                         | `Test_SC7_NoCredentialOrContentLeakageInLogs`                                | SC-7                                      |
| `QuarantineControllerTests (IntegrationTests)`            | `Test_SC6_ReadOnlySessionBlockedFromPermanentDelete`                         | SC-6                                      |
| `QuarantineEntryRepositoryTests (IntegrationTests, Skip)` | `Test_ListActiveAsync_ReturnsOnlyActiveRowsCorrectlyShaped`                  | SC-1 (DB-level, skip: no live SQL Server) |
| `QuarantineEntryRepositoryTests (IntegrationTests, Skip)` | `Test_GetActiveForDeleteAsync_UnknownOrNonActiveIdReturnsNull`               | V-3 (DB-level, skip: no live SQL Server)  |
| `QuarantineEntryRepositoryTests (IntegrationTests, Skip)` | `Test_TryTransitionToDeletedAsync_ActiveRowTransitionsOnceThenReturnsFalse`  | SC-8 (DB-level, skip: no live SQL Server) |

### Migration

`20260922_183000__CAS-008-quarantine-permanent-delete-api.sql, 20260922_190000__CAS-008-quarantine-permanent-delete-api-addendum.sql`
