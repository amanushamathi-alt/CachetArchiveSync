---
id: CAS-014
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
change_type: enhancement
related_specs: [CAS-008, CAS-017]
touches:
  database: true
  api: true
  ui: false
  worker: false
  b2: false
  config: false
  security: false
migrations:
  - 20260922_230000__CAS-014-quarantine-hard-delete-and-remove-item.sql
---

# CAS-014 — Change: Quarantine Hard-Delete Removal + Formalized "Remove Item" Endpoint

Implements BRD v2.2 Section 12.3 items 3, 7, and 8 (Product Manager Review, 2026-09-22, Rakesh
Devarajan, TraceCounts). Related to CAS-008 (Quarantine List and Permanent Delete API), which built
`GET /quarantine`, `POST /quarantine` (add), and `POST /quarantine/delete` (Permanent Delete), and
explicitly deferred the fourth Quarantine capability — non-destructive catalog removal — to "a future
spec" (CAS-008 §0, §4, §15). This is that spec.

---

## §1 Current Behavior

### 1.1 No distinct "remove from quarantine" endpoint exists today

The API layer implements exactly three Quarantine routes, all in
`src/Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs`:

- `GET /quarantine` — list Active rows (`IQuarantineListService.GetActiveAsync`).
- `POST /quarantine` — add (delegates to `IQuarantineService.AddAsync`, reused from CAS-006).
- `POST /quarantine/delete` — **Permanent Delete** (`IQuarantinePermanentDeleteService.DeleteAsync`):
  deletes the file from B2 and the local mirror, FR-16/FR-17.

There is no fourth route for "take this file out of the Quarantine Queue, but don't delete it from B2
or the local mirror." `CACHET_API_Design_Specification_v1.md` §6.3 proposed exactly this capability as
`POST /quarantine/release` (204 response) and its own §11 item 6 / §12.1 item 3 record it as
"**CONFIRMED FOR BUILD (2026-09-09, Rakesh Devarajan)**" — but CAS-008 §0 explicitly did not build it:
"deferred to a follow-up spec, not added to this one... a materially different (non-destructive,
non-`CachetFullAccess`-gated) operation not named in this spec's task instructions." No code,
controller route, service, or repository method for it exists anywhere in `src/` today. The UI/UX
design spec (`CACHET_UIUX_Design_Specification_v1.md` §4.3) does not separately describe this action at
all — it only documents the Add ("Send to Quarantine") and Delete Permanently interactions; the
"remove item" capability exists purely as the API design doc's own proposal plus BRD §12.3 item 7's
2026-09-22 confirmation that it is wanted.

### 1.2 `dbo.quarantine_entry` has no `is_active` column — verified against the entity file

`Database/CachetArchiveSync/Entities/dbo.quarantine_entry.sql` shows the table tracks row state via a
CHECK-constrained `status NVARCHAR(20)` column (`'Active'`/`'Released'`/`'Deleted'`), **not** an
`is_active BIT` flag. This differs from this project's general soft-delete convention
(`is_active BIT NOT NULL DEFAULT (1)`, per `CACHET_Database_Schema_Design_v1.md` §0's project-wide
rule) — `quarantine_entry` was deliberately built with a three-state enum instead, anticipating exactly
the Active → Released / Active → Deleted transitions this module needs (CAS-006 §5's design note,
carried into CAS-008 §0).

### 1.3 `dbo.quarantine_entry_delete_upd` is Permanent Delete's status-transition step — not a
"remove from quarantine" operation

`Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_delete_upd.sql` sets
`status = 'Deleted'` for one `Active` row. It is called exclusively from
`QuarantinePermanentDeleteService.DeleteAsync` (`src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs`,
line 166: `TryTransitionToDeletedAsync`) as the third of four steps in the Permanent Delete pipeline —
**after** the B2 object versions and the local `file_index` row have already been handled (§1.4 below).
It is not, and has never been, a standalone soft-delete "remove from Quarantine" capability; there is
no code path that transitions a row to `'Released'` anywhere in this codebase today. The `'Released'`
enum value exists in the CHECK constraint (`CK_quarantine_entry_status`) but is currently dead
vocabulary — reserved for the not-yet-built release/remove capability, same situation as
`AuditAction.PermanentDeleteRequested` being defined but unused (CAS-008 §17's own finding).

### 1.4 Permanent Delete sequencing — verified against the actual code (BRD §12.3 item 8)

Read in full: `src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs`. Per file,
independently, the pipeline is:

1. **B2 delete** — `DeleteAllVersionsAsync(entry.Path, ct)` (lines 89–121): enumerates and deletes every
   version of the object via `IB2Client.ListFileVersionsAsync` / `DeleteFileVersionAsync`.
2. **Local mirror soft-delete** — `_fileIndexRepository.UpdateAsync(..., isActive: false, ct)`
   (lines 125–160), only reached if step 1 succeeded.
3. **Catalog status transition** — `_quarantineEntryRepository.TryTransitionToDeletedAsync(...)`
   (lines 162–195), only reached if step 2 succeeded.
4. **Audit write** (`AuditAction.Deleted`, `Result: Success`) before the per-file result is appended to
   the response (lines 197–209).

`QuarantinePermanentDeleteServiceTests.Test_SC3_DeleteSucceedsAndAuditsBeforeResponse`
(`src/Cachet.ArchiveSync.Api.Tests/QuarantinePermanentDeleteServiceTests.cs`, lines 289–336) asserts the
exact call order with a `callOrder` list:
`"b2-delete", "local-soft-delete", "status-transition", "audit", "response-returned"`.

**Finding: the current implementation already does this correctly.** BRD §12.3 item 8 confirms
"the file is removed from Backblaze B2 first, then removed from the local mirror cache" — this is
exactly steps 1→2 above. **No reordering or code change is required for item 8.** This spec's only
obligation regarding item 8 is to record this verification (§2.3, §10 SC-4) — not to change
`QuarantinePermanentDeleteService`.

---

## §2 Desired Behavior

### 2.1 Quarantine removal ("remove item") is a genuine hard `DELETE`, not a status transition

A new stored procedure, `dbo.quarantine_entry_del`, physically removes the `dbo.quarantine_entry` row
(`DELETE FROM dbo.quarantine_entry WHERE quarantine_entry_id = @quarantine_entry_id AND status =
'Active'`) — matching this project's documented `_del` naming convention for a delete-class stored
procedure (`CACHET_Database_Schema_Design_v1.md` §"Planned stored procedures" lists `dbo.users_get`,
`_lst`, `_ins`, `_upd`, `_del` for the `users` table, though `users_del` itself is not yet implemented
in this codebase — this spec's `quarantine_entry_del` is the first `_del`-suffixed procedure actually
built under that convention). This satisfies BRD §12.3 item 3 precisely: "Removing a file from the
Quarantine Queue is a hard (physical) delete of that catalog row, not a soft-delete (`is_active` flag)."
No B2 call, no `file_index` mutation — the underlying file is untouched; only its Quarantine catalog
row disappears.

### 2.2 `POST /quarantine/remove` is formalized and built (BRD §12.3 item 7)

A new endpoint on the existing `QuarantineController`, `POST /quarantine/remove`, backed by a new
`IQuarantineRemoveService.RemoveAsync`. **Route naming decision (resolved 2026-09-22, R. Devarajan,
TraceCounts — see §14):** the API design doc's own §6.3 proposal named this route `/quarantine/release`
(carried forward from CAS-008 §0/§4/§15's deferred reference to "release"), but that name is rejected
here in favor of `/quarantine/remove` — "release" implies the catalog action itself is reversible,
which conflicts with BRD §12.3 item 3's explicit hard-delete framing (§2.1). The persistence mechanism
also changes from that document's implied status-to-`'Released'` transition to the BRD
§12.3-item-3-confirmed hard delete. Per requested `fileId` (a `quarantine_entry_id`, same convention as
`POST /quarantine/delete`), independently: resolve the row (must currently be `Active`), hard-delete it,
write an audit row — before that file's result is recorded, matching Permanent Delete's per-file batch
pattern (CAS-008 §6.4) rather than the API design doc's original single-file/`204` shape, since this
project's established Quarantine UI pattern is multi-select-then-act (`CACHET_UIUX_Design_Specification_v1.md`
§1.4, §4.3 — "Delete Permanently (3)").

### 2.3 Permanent Delete sequencing — confirmed correct, no code change

BRD §12.3 item 8 is satisfied by the existing `QuarantinePermanentDeleteService` implementation as-is
(§1.4). This spec adds a regression-locking assertion only (§13) — it does not modify
`QuarantinePermanentDeleteService.cs`.

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** BRD v2.2 Section 12.3, "Additional Decisions (2026-09-22, Product Manager Review)" —
  confirmed directly by Rakesh Devarajan, TraceCounts (Solution Owner / Product Manager):
  - **Item 3 (Quarantine Removal):** "Removing a file from the Quarantine Queue is a hard (physical)
    delete of that catalog row, not a soft-delete (`is_active` flag). This is scoped to Quarantine
    removal specifically."
  - **Item 7 (Quarantine "Remove Item"):** "Formalized as an intentional capability of the Quarantine
    Queue (previously flagged as an invented, BRD-silent endpoint)."
  - **Item 8 (Permanent Delete Sequencing):** "Confirmed synchronous: the file is removed from
    Backblaze B2 first, then removed from the local mirror cache."
- **Risk if not changed:** Without a "remove item" capability, a Keyword Search reviewer has no way to
  act on a Quarantine entry they decide should not be permanently deleted, short of leaving a
  mis-quarantined file sitting in the shared queue indefinitely (the exact gap the API design doc's
  §6.3/§11 item 6 originally identified and BRD §12.3 item 7 now confirms is worth closing). Without
  documenting that Permanent Delete's sequencing is already correct, a future spec or reviewer could
  mistakenly "fix" working code against a decision it already satisfies.

---

## §4 Blast Radius (Graph Impact)

**Graphify not yet vendored for this repo** (`graphify-out/` has not been generated per this project's
CLAUDE.md status note) — this section is a manual grep/read of the codebase, not a live graphify query.

### Primary Change Target
File: `src/Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs` (new `Remove` action);
`src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineEntryRepository.cs` (new
`HardDeleteActiveAsync` method).
Entity: `dbo.quarantine_entry`.
Current edges (manual read): referenced by `dbo.file_index` (FK), `dbo.search_result` (FK, nullable),
`QuarantineEntryRepository`, `QuarantineListService`, `QuarantinePermanentDeleteService`,
`QuarantineController`, `IQuarantineEntryRepository`.

### Direct Dependents (nodes with edges INTO the changed entity)
- `QuarantineController` (new `POST /quarantine/remove` route; existing `GET /quarantine`,
  `POST /quarantine`, `POST /quarantine/delete` routes unchanged).
- `QuarantineEntryRepository` (new `HardDeleteActiveAsync` method; existing methods unchanged).
- `IQuarantineEntryRepository`, `IQuarantineRemoveService` (new interface).
- `AuditAction` enum (one new appended member — see §7).

### Downstream Modules
- **Quarantine** (this spec's entire scope).
- **Audit Log** — one new `AuditAction` member consumed by the new endpoint; no change to
  `AuditLogRepository`/`AuditLogService`/`dbo.audit_log` schema.
- **Keyword Search** — untouched. Removing a quarantine entry does not affect `dbo.search_result` or
  the ability for the same file to be re-flagged/re-quarantined by a later search (the FK from
  `quarantine_entry.search_result_id` is nullable and the filtered unique index on `file_index_id` is
  scoped to `status = 'Active'` rows only — once this row is gone, a fresh `POST /quarantine` add for
  the same file is unblocked, which is expected and desired).
- **O-Drive Sync, Task Manager, Reporting** — not touched; no reference to `quarantine_entry` in
  `SyncPurgeTaskProcessor`, `task_queue`, or the reporting services beyond what CAS-008/CAS-010 already
  established.

### Cross-Project Risk
`Api`-only change. `Cachet.ArchiveSync.Core` gets one new interface
(`IQuarantineRemoveService`) and one new enum member (`AuditAction.QuarantineRemoved`) — both purely
additive, no signature change to any existing shared type Worker or Web would consume. `Worker` and
`Web` projects are untouched (`touches.worker: false`, `touches.ui: false`).

### God Node Involvement
None identified via manual read. `dbo.quarantine_entry` is the same node CAS-006/CAS-008 already
established as a moderate-degree node (FK from `file_index`, `search_result`; consumed by three
existing services) — this spec adds one more edge (`IQuarantineRemoveService` → `quarantine_entry`)
rather than introducing a new high-degree node.

### Graphify Queries Run
None — `graphify` is not vendored/available for this repo yet. A future session with graphify
available should run:
```
python -m graphify explain "QuarantineEntry"
python -m graphify query "Quarantine remove release hard delete FR-14 FR-16 FR-17" --budget 1000
```
and reconcile any findings against this manual analysis before `status → ready`.

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `Database/migrations/20260922_230000__CAS-014-quarantine-hard-delete-and-remove-item.sql` | add | New migration: `dbo.quarantine_entry_del` |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_del.sql` | add | Declarative mirror of the new hard-delete sproc |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | modify | Append `QuarantineRemoved` (no reorder — CAS-006/007/010 append-only convention) |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineEntryRepository.cs` | modify | Add `Task<bool> HardDeleteActiveAsync(int quarantineEntryId, CancellationToken ct)` |
| `src/Cachet.ArchiveSync.Api/Repositories/QuarantineEntryRepository.cs` | modify | Implement `HardDeleteActiveAsync` via `dbo.quarantine_entry_del` |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineRemoveService.cs` | add | New service interface (`RemoveAsync`), mirroring `IQuarantinePermanentDeleteService`'s outcome/result shape |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineRemoveRequest.cs` | add | `{ FileIds: int[] }` |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineRemoveResultItem.cs` | add | `{ FileId, Result, Reason? }` |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineRemoveResponse.cs` | add | `{ Results: [] }` |
| `src/Cachet.ArchiveSync.Api/Services/QuarantineRemoveService.cs` | add | New service: resolve → hard-delete → audit, per file, independent |
| `src/Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs` | modify | Add `POST /quarantine/remove` action |
| `src/Cachet.ArchiveSync.Api/Program.cs` | modify | DI registration for `IQuarantineRemoveService` |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantineRemoveServiceTests.cs` | add | Unit tests (§13) |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantineControllerTests.cs` | modify | Add remove-route test coverage |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/QuarantineEntryRepositoryTests.cs` | modify | Add `HardDeleteActiveAsync` round-trip tests (Skip-marked, no live SQL Server in sandbox, same as CAS-008's existing three) |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/QuarantineControllerTests.cs` | modify | Add HTTP-level remove-route test |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs` | §1.4/§2.3 — already implements the BRD §12.3 item 8-confirmed B2-first-then-local sequencing correctly; no defect found |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_delete_upd.sql` | Remains the Permanent Delete status-transition step (Active → Deleted) — a distinct, still-needed operation; not superseded by this spec's new hard-delete sproc (§7) |
| `Database/CachetArchiveSync/Entities/dbo.quarantine_entry.sql` | No `ALTER TABLE` — the existing `status` CHECK constraint (`'Active'`/`'Released'`/`'Deleted'`) is left as-is (§7's rationale) |
| `src/Cachet.ArchiveSync.Api/Repositories/QuarantineEntryRepository.cs`'s `GetActiveForDeleteAsync`, `TryTransitionToDeletedAsync`, `ListActiveAsync`, `InsertAsync` | Reused as-is by other endpoints; this spec only adds `HardDeleteActiveAsync` alongside them |
| `src/Cachet.ArchiveSync.Api/Controllers/SearchController.cs` | Unrelated — CAS-006's add-only Quarantine route, not touched by removal logic |
| Any `Cachet.ArchiveSync.Worker` or `Cachet.ArchiveSync.Web` file | `touches.worker: false`, `touches.ui: false` — no Blazor component, no `BackgroundService` change |

---

## §7 Database Changes

### Changed Objects

| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| `dbo.quarantine_entry_del` | dbo | new stored procedure | Hard `DELETE` of one `Active` `dbo.quarantine_entry` row by `quarantine_entry_id`. `@rows_affected OUTPUT` — 0 if the id is unknown or the row is not `Active` (already `Deleted`, or a future `Released` value should one ever be written, though no code path writes it). Backs `POST /quarantine/remove`. |

### `dbo.quarantine_entry_delete_upd` — kept, not deprecated

**Decision:** kept as-is, unchanged. It is not a duplicate of the new hard-delete sproc and is not the
"soft-delete quarantine removal" this spec replaces — it is Permanent Delete's own catalog
status-transition step (Active → Deleted), a different operation serving a different endpoint
(`POST /quarantine/delete`, not `POST /quarantine/remove`). Removing it would break Permanent Delete.

### `status = 'Released'` CHECK-constraint value — left in place, flagged as still-unused

**Decision:** no `ALTER TABLE`/constraint change. `'Released'` remains a legal `status` value in
`CK_quarantine_entry_status`, but after this spec ships, **no code path in this codebase writes it** —
the new "remove item" capability now hard-deletes the row instead of transitioning it to `'Released'`
(§2.1, per BRD §12.3 item 3's explicit hard-delete requirement, which supersedes the API design doc's
informal release-via-status-flip assumption). Dropping `'Released'` from the CHECK constraint is
out of scope here (§4/database-object minimalism — Karpathy P3, smallest diff that satisfies the spec;
narrowing a CHECK constraint is a schema change with its own blast radius this spec's task instructions
did not ask for). **Decision (resolved 2026-09-22, R. Devarajan, TraceCounts — see §14): leave `'Released'`
in the CHECK constraint.** No functional cost today; revisit only if a future schema-cleanup pass is
independently justified.

### Migration Strategy

`db-author` writes, at `/implement` time:
- `Database/migrations/20260922_230000__CAS-014-quarantine-hard-delete-and-remove-item.sql` —
  idempotent (`CREATE OR ALTER PROCEDURE`), adds `dbo.quarantine_entry_del` only. No table DDL.
- `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_del.sql` — declarative mirror.

No data migration. No existing row is touched by this migration.

### Migration Required
Yes — see above. Two consistent artifacts per this project's hybrid model (append-only migration +
declarative mirror), matching CAS-008's own pattern for its two new stored procedures.

---

## §8 API Contract

### New Endpoint: `POST /quarantine/remove` **[audited]** — "Remove Item" (BRD §12.3 items 3, 7)

**Route naming decision (resolved 2026-09-22, R. Devarajan, TraceCounts — see §14):**
`CACHET_API_Design_Specification_v1.md` §6.3's original proposal used `/quarantine/release` (confirmed
for build 2026-09-09 per that document's §11 item 6/§12.1 item 3, then deferred to a future spec by
CAS-008 §0/§4/§15). This spec deliberately diverges from that name and builds `/quarantine/remove`
instead: BRD §12.3 item 3 confirms the underlying operation is a hard (physical) delete of the catalog
row, and "release" reads as implying the row-level action itself is reversible, which it is not (§12).
`/remove` is the accurate name for what the endpoint does; the API design doc should be updated in a
follow-up pass to reflect this rename (tracked as a note here, not a separate spec, since it is a
documentation-only correction). Only the persistence mechanism also changes from that document's
assumption (§6.3's `204 No Content`, single-file, implicitly a status-to-`'Released'` transition) to a
batch, per-file, hard-delete contract matching this codebase's actual `POST /quarantine/delete`
precedent (CAS-008 §6.4) — chosen for consistency with this project's established
multi-select-then-batch-act UI pattern (`CACHET_UIUX_Design_Specification_v1.md` §1.4) rather than the
single-file/`204` shape the API design doc assumed before that pattern was confirmed.

**Auth:** `[Authorize(Policy = "CachetAuthenticated")]` — same policy as `GET /quarantine` and
`POST /quarantine` (§12; a catalog-only action, no B2 write, consistent with the API design doc's own
§1.5 table entry: "Quarantine — release... Same basis as add — a catalog-only action").

**Async:** No.

**Request:**

```json
{ "fileIds": [100801, 100802] }
```

`fileIds` are `quarantine_entry_id` values, as returned by `GET /quarantine` — same identity convention
as `POST /quarantine/delete`'s `fileIds` (CAS-008 §6.4), **not** the `search_result_id` values used at
add-time.

**Response `200 OK`:**

```json
{
  "results": [
    { "fileId": 100801, "result": "Success" },
    { "fileId": 100802, "result": "Failed", "reason": "file-not-found" }
  ]
}
```

Each file is attempted independently — a failure on one file does not block removal of the others
(same posture as `POST /quarantine/delete`, CAS-008 §6.4).

**Status codes:**

| Code | Condition |
|------|-----------|
| 200 | Request processed — see `results[]` for per-file outcomes (may include per-file `Failed` entries) |
| 422 | `validation-failed` — `fileIds` empty |
| 404 | `file-not-found` — whole-request 404 only if every requested `fileId` fails to resolve to any `Active` `quarantine_entry` row; an unresolvable id inside an otherwise-valid batch appears as a per-file `Failed` result instead (same rule as `POST /quarantine/delete`, CAS-008 §7 V3) |
| 401 | No valid session |

No `confirm: true` gate — unlike Permanent Delete (FR-17), this action does not delete the underlying
file from B2 or the local mirror; it only removes a catalog row the user can recreate by re-quarantining
the file from a later search hit (§4 Downstream Modules). BRD §12.3 does not impose a confirmation
requirement on this action, and the API design doc's own §6.3 draft (`204`, no confirm) agrees no
second-confirmation gate is warranted here — only Permanent Delete and O-Drive Sync's Purge carry that
requirement (BRD §9's irreversible-delete-class actions).

### Existing Endpoints — unchanged

`GET /quarantine`, `POST /quarantine`, `POST /quarantine/delete` (CAS-008 §6) are not modified by this
spec. No breaking change to any existing contract.

| Endpoint | Field | Before | After | Breaking? |
|----------|-------|--------|-------|-----------|
| `POST /quarantine/remove` | (new endpoint) | N/A | See above | No — additive |

---

## §9 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | `POST /quarantine/remove` `fileIds` must be non-empty | 422 `validation-failed` | `Test_V1_RemoveEmptyFileIdsRejected` |
| V2 | `POST /quarantine/remove` each `fileId` must resolve to a `dbo.quarantine_entry` row with `status = 'Active'` | Per-file `Failed` result (`reason: "file-not-found"`); whole-request 404 only if every file fails this way (same rule as CAS-008 §7 V3) | `Test_V2_RemoveUnknownOrNonActiveEntryReportedPerFile` |
| V3 | `POST /quarantine/remove` hard-deletes the row — a subsequent `GET /quarantine` never returns it, and the row no longer exists in `dbo.quarantine_entry` at all (not merely status-flagged) | N/A — proven by row-count assertion | `Test_V3_RemovedEntryIsPhysicallyGoneNotStatusFlagged` |
| V4 | `POST /quarantine/remove` writes an audit row (`QuarantineRemoved`, `Success`) **before** the response returns, for every successfully removed file (FR-23, this project's binding audit-before-response rule) | N/A — proven by call-order assertion | `Test_V4_RemoveAuditsBeforeResponse` |
| V5 | `POST /quarantine/remove` performs no B2 call and no `dbo.file_index` mutation for any file (distinguishing it from Permanent Delete) | N/A — proven by absence of calls | `Test_V5_RemoveNeverCallsB2OrFileIndex` |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `POST /quarantine/remove` with valid, currently-`Active` `fileIds` physically deletes each row from `dbo.quarantine_entry` (row count for that id becomes 0, not merely `status = 'Deleted'`/`'Released'`), writes a `QuarantineRemoved` audit row before the response, and returns `200` with a `Success` result per file | `Test_SC1_RemoveHardDeletesRowAndAuditsBeforeResponse` |
| SC-2 | A repeat `POST /quarantine/remove` for an already-removed `fileId` reports a per-file `Failed`/`file-not-found` result, not a second success, and does not throw | `Test_SC2_RepeatRemoveOfAlreadyRemovedEntryFailsCleanly` |
| SC-3 | `POST /quarantine/remove` on a batch where one file resolves and one does not: the resolvable file is removed and audited, the other is reported `Failed`/`file-not-found`, both in the same `200` response | `Test_SC3_PartialBatchIsolatedPerFile` |
| SC-4 | (Regression lock, BRD §12.3 item 8) `QuarantinePermanentDeleteService.DeleteAsync`'s existing call order remains `B2 delete → local soft-delete → status transition → audit → response` — this spec adds no change to that ordering | Existing `Test_SC3_DeleteSucceedsAndAuditsBeforeResponse` (CAS-008, unmodified) continues to pass; this spec asserts in its own test plan that the file was not touched |
| SC-5 | After a successful `POST /quarantine/remove`, the same physical file can be re-quarantined via a fresh `POST /quarantine` add without a `409 file-already-quarantined` conflict (proves the filtered unique index on `file_index_id` no longer sees an `Active` row for it) | `Test_SC5_RemovedFileCanBeReQuarantined` |

---

## §11 Security Notes

### Audit Trail (FR-23 — binding project rule)

`POST /quarantine/remove` is a state-changing action and **must** write to the audit log before
returning success, per this project's CLAUDE.md rule and BRD §9/§23. Per file, on success:
`action: QuarantineRemoved`, `tab_module: 'Quarantine'`, `file_path` populated from the resolved entry,
`result: Success` — written before that file's result is appended to the response, matching
`QuarantinePermanentDeleteService`'s existing pattern exactly (§1.4 step 4). On a per-file
`file-not-found` outcome, no audit row is written for that file (nothing happened to audit) — consistent
with `POST /quarantine/delete`'s own V3 behavior (CAS-008 §7 V3), which likewise does not audit
unresolvable ids.

### B2 Credential Handling

N/A — this endpoint never calls `IB2Client`. No B2 key or key ID is referenced by
`QuarantineRemoveService` (§5).

### Session / Auth Requirements

`POST /quarantine/remove`: `[Authorize(Policy = "CachetAuthenticated")]` — **not**
`CachetFullAccess`. This is a deliberate, BRD-consistent choice: unlike Permanent Delete (a B2-write
action gated to full-access sessions per CAS-002 §12), removing a catalog row never touches B2 or the
local mirror, so a Read Only session may use it — matching the API design doc's own basis ("Same basis
as add — a catalog-only action," §1.5) and CAS-008's identical reasoning for why `GET /quarantine` and
`POST /quarantine` (add) require only `CachetAuthenticated`.

### Irreversibility — explicitly NOT applicable here in the FR-17 sense

Removing a Quarantine entry is reversible **in effect** (the underlying file is untouched and can be
re-quarantined later, §10 SC-5) even though the catalog row itself is a genuine hard delete with no
row-level undo (§12). This is a materially different irreversibility posture than Permanent Delete
(FR-17) or O-Drive Sync Purge, where the underlying file itself is gone from B2 and the local mirror
with no recovery path — this spec does not relax or touch either of those two accepted, deliberate
business decisions (BRD §9). *(Note 2026-10-08: until CAS-062, neither action actually removed the
local B2 mirror file. Only the `file_index` row was soft-deleted. CAS-062 implements the physical
local-copy removal for both.)*

---

## §12 Rollback Plan (row-level — see also §12 code/database rollback below)

Once a `POST /quarantine/remove` call successfully removes a `dbo.quarantine_entry` row, **there is no
recovery of that specific catalog row** — it is a genuine hard `DELETE`, not a status flag, so no
application-level undo exists and none may be added without an explicit spec change and client
sign-off (CLAUDE.md's binding rule — analogous to Permanent Delete's FR-17 no-recovery posture, BRD §9).
This is scoped narrowly: the *catalog entry* cannot be un-removed, but the *file itself* is unaffected
and can be independently re-quarantined by a later search hit (§10 SC-5, §11) — this is not the same
irreversibility class as Permanent Delete or Purge, where the underlying file is gone.

### Code / Database Rollback (production incident)

1. **Code rollback:** revert the `QuarantineController`/`QuarantineRemoveService`/DI changes; the
   `POST /quarantine/remove` route stops responding (404 at the routing layer), no data corruption
   results since no in-flight state depends on this endpoint.
2. **Database rollback:** `dbo.quarantine_entry_del` is additive-only — dropping it is safe
   pre-traffic. Post-traffic, do not drop it once any row has been hard-deleted through this path (the
   procedure itself has nothing to roll back, but removing it while the API still calls it would break
   the endpoint uncleanly) — roll back code only, per this project's established posture (CAS-008 §14).
3. **Config rollback:** N/A — no configuration keys added.
4. **Task-queue rollback:** N/A — this spec adds no queued task type; `POST /quarantine/remove` is
   synchronous, like `POST /quarantine/delete` (CAS-008 §6.4, API design doc §1.5).

---

## §13 Test Plan

### Unit Tests

| Class | Method | Proves |
|-------|--------|--------|
| `QuarantineRemoveServiceTests` (NSubstitute `IQuarantineEntryRepository`, `IAuditLogService`) | `Test_V1_RemoveEmptyFileIdsRejected` | V1 |
| `QuarantineRemoveServiceTests` | `Test_V2_RemoveUnknownOrNonActiveEntryReportedPerFile` | V2 |
| `QuarantineRemoveServiceTests` | `Test_V4_RemoveAuditsBeforeResponse` (call-order assertion, same style as CAS-008's `Test_SC3_DeleteSucceedsAndAuditsBeforeResponse`) | V4, SC-1 |
| `QuarantineRemoveServiceTests` | `Test_V5_RemoveNeverCallsB2OrFileIndex` (asserts `IB2Client`/`IFileIndexRepository` are never referenced — note: `QuarantineRemoveService` must not even take these as constructor dependencies) | V5 |
| `QuarantineRemoveServiceTests` | `Test_SC2_RepeatRemoveOfAlreadyRemovedEntryFailsCleanly` | SC-2 |
| `QuarantineRemoveServiceTests` | `Test_SC3_PartialBatchIsolatedPerFile` | SC-3 |
| `QuarantineControllerTests` | Route-level test asserting `POST /quarantine/remove` maps to `IQuarantineRemoveService.RemoveAsync` and requires only `CachetAuthenticated` | §8, §11 |

### Integration Tests

| Class | Method | Proves |
|-------|--------|--------|
| `QuarantineEntryRepositoryTests` (real SQL Server, Skip-marked — no live SQL Server in this sandbox, same convention as CAS-008's three existing Skip-marked methods) | `Test_HardDeleteActiveAsync_ActiveRowIsPhysicallyRemoved` | V3, SC-1 |
| `QuarantineEntryRepositoryTests` | `Test_HardDeleteActiveAsync_AlreadyRemovedRowReturnsFalse` | SC-2 |
| `QuarantineEntryRepositoryTests` | `Test_RemovedFileCanBeReQuarantined_FilteredUniqueIndexNoLongerBlocks` | SC-5 |
| `QuarantineControllerTests` (IntegrationTests, `WebApplicationFactory`) | `Test_SC1_RemoveEndpointHardDeletesViaHttp` | SC-1 (HTTP-level) |

### Regression (no code change, verification only)

| Class | Method | Proves |
|-------|--------|--------|
| `QuarantinePermanentDeleteServiceTests` (CAS-008, unmodified) | `Test_SC3_DeleteSucceedsAndAuditsBeforeResponse` (existing) | SC-4 — confirms B2-first-then-local sequencing is unaffected by this spec |

### Coverage Target

Changed files must reach ≥ 85% line coverage, per this project's standard gate.

---

## §14 Open Questions — Resolved

All three items below were open in the initial draft and are now resolved
(2026-09-22, R. Devarajan, TraceCounts — Solution Owner / Product Manager sign-off). Recorded here for
traceability; no items remain open for this spec.

- [x] **Route naming: `POST /quarantine/release` vs. `POST /quarantine/remove`.**
      **Resolved: `POST /quarantine/remove`.** "Release" implies the catalog action itself is
      reversible, which conflicts with BRD §12.3 item 3's explicit hard-delete framing. `/remove` is
      the accurate name for a physical `DELETE` of the catalog row. This diverges from
      `CACHET_API_Design_Specification_v1.md` §6.3's original `/release` proposal — that document should
      be corrected in a documentation-only follow-up (not a code-affecting spec) to match. Applied
      throughout §2.2, §4, §5, §8, §9, §10, §11, §12, §13, §16 of this spec.
- [x] **Should `'Released'` be dropped from `CK_quarantine_entry_status`?**
      **Resolved: leave it in place.** No functional cost today; the value stays dead vocabulary in the
      constraint but narrowing it is a distinct schema change with its own blast radius, not justified
      by this spec's task instructions (Karpathy P3, §7). No CHECK-constraint change ships with CAS-014.
      Revisit only if a future, independently-justified schema-cleanup spec picks it up.
- [x] **Whether a UI/UX design spec addendum is needed.**
      **Resolved: yes.** `CACHET_UIUX_Design_Specification_v1.md` §4.3 documents only "Send to
      Quarantine" and "Delete Permanently" for the Quarantine Queue modal — it does not yet show a
      "Remove from Queue" affordance for this newly-formalized capability. A follow-up Web-kind spec,
      **CAS-017**, is opened to add the "Remove from Queue" UI affordance (Blazor component work,
      `touches.ui: true`) and to correct the UI/UX design doc, once CAS-014's `POST /quarantine/remove`
      endpoint (this spec, API-only, `touches.ui: false`) has shipped. CAS-014 itself ships with no UI
      consumer, by design.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 | |
| Reviewer | | | Pending |

---

## §16 Verification Plan

### What the Reviewer Will Check

- [ ] Current behavior (§1) matches what is actually in the code BEFORE the change — specifically, that
      `dbo.quarantine_entry_delete_upd` is Permanent Delete's status-transition step, not a
      "remove from quarantine" operation, and that no `is_active` column exists on `quarantine_entry`.
- [ ] Desired behavior (§2) is fully implemented: `dbo.quarantine_entry_del` performs a real `DELETE`,
      not an `UPDATE`.
- [ ] Only files in §5 were modified (§6 files are untouched) — in particular,
      `QuarantinePermanentDeleteService.cs` must be byte-identical to its CAS-008 state.
- [ ] All §10 Success Criteria have passing tests, including SC-4's regression check that Permanent
      Delete's B2-first-then-local sequencing (BRD §12.3 item 8) is unchanged.
- [ ] All §9 Validations (V1–V5) are implemented and tested.
- [ ] §8 API contract is correct: `POST /quarantine/remove` requires only `CachetAuthenticated`
      (not `CachetFullAccess`), and never calls `IB2Client` or `IFileIndexRepository`.
- [ ] §11 Security Notes: a `QuarantineRemoved` audit row is written **before** the response returns,
      for every successfully removed file, none silently dropped.
- [ ] §12 Rollback Plan: confirm no soft-delete/undo affordance was added for the new hard-delete path,
      and that the existing `dbo.quarantine_entry_delete_upd` sproc remains in place and unmodified.
- [ ] §4 Blast Radius is accurate — no unintended `Worker`/`Web`/O-Drive Sync/Task Manager changes.
- [ ] No out-of-scope changes: no `ALTER TABLE` on `dbo.quarantine_entry`, no change to
      `POST /quarantine/delete`'s `confirm: true` gate or its own behavior.

### Verification Report Format

Per template: `CAS-014.review.md` — overall verdict, per-SC verdict table, security gap list, scope
violations.

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 65.7% overall (business-logic files 100%; gap is Skip-marked repo method needing live SQL Server, waived by owner) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20260922_230000__CAS-014-quarantine-hard-delete-and-remove-item.sql` | add |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.quarantine_entry_del.sql` | add |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | modify |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineEntryRepository.cs` | modify |
| `src/Cachet.ArchiveSync.Api/Repositories/QuarantineEntryRepository.cs` | modify |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineRemoveService.cs` | add |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineRemoveRequest.cs` | add |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineRemoveResultItem.cs` | add |
| `src/Cachet.ArchiveSync.Core/Dtos/QuarantineRemoveResponse.cs` | add |
| `src/Cachet.ArchiveSync.Api/Services/QuarantineRemoveService.cs` | add |
| `src/Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs` | modify |
| `src/Cachet.ArchiveSync.Api/Program.cs` | modify |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantineRemoveServiceTests.cs` | add |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantineControllerTests.cs` | modify |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantineAddRouteTests.cs` | modify |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/QuarantineEntryRepositoryTests.cs` | modify |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/QuarantineControllerTests.cs` | modify |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `QuarantineRemoveServiceTests` | `Test_V1_RemoveEmptyFileIdsRejected` | V1 |
| `QuarantineRemoveServiceTests` | `Test_V2_RemoveUnknownOrNonActiveEntryReportedPerFile` | V2 |
| `QuarantineRemoveServiceTests` | `Test_V2_AllFilesUnresolvableReturnsWholeRequestNotFound` | V2 |
| `QuarantineRemoveServiceTests` | `Test_V4_RemoveAuditsBeforeResponse` | V4, SC-1 |
| `QuarantineRemoveServiceTests` | `Test_V5_RemoveNeverCallsB2OrFileIndex` | V5 |
| `QuarantineRemoveServiceTests` | `Test_SC2_RepeatRemoveOfAlreadyRemovedEntryFailsCleanly` | SC-2 |
| `QuarantineRemoveServiceTests` | `Test_SC2_ConcurrentHardDeleteRaceReportedAsFileNotFound` | SC-2 |
| `QuarantineRemoveServiceTests` | `Test_SC3_PartialBatchIsolatedPerFile` | SC-3 |
| `QuarantineControllerTests` | `Test_Remove_RequiresOnlyCachetAuthenticatedPolicy` | §8, §11 |
| `QuarantineControllerTests` | `Test_Remove_DelegatesToQuarantineRemoveService` | §8 |
| `QuarantineControllerTests` | `Test_Remove_NoUserIdClaim_Returns401` | §8 |
| `QuarantineControllerTests` | `Test_Remove_ValidationFailed_Returns422` | V1 |
| `QuarantineControllerTests` | `Test_Remove_AllFilesNotFound_Returns404` | V2 |
| `QuarantineEntryRepositoryTests` | `Test_HardDeleteActiveAsync_ActiveRowIsPhysicallyRemoved` | V3, SC-1 |
| `QuarantineEntryRepositoryTests` | `Test_HardDeleteActiveAsync_AlreadyRemovedRowReturnsFalse` | SC-2 |
| `QuarantineEntryRepositoryTests` | `Test_RemovedFileCanBeReQuarantined_FilteredUniqueIndexNoLongerBlocks` | SC-5 |
| `QuarantineControllerTests` | `Test_SC1_RemoveEndpointHardDeletesViaHttp` | SC-1 |
| `QuarantineControllerTests` | `Test_PostQuarantineRemove_NoSession_Returns401` | §8 |

### Migration
`20260922_230000__CAS-014-quarantine-hard-delete-and-remove-item.sql`
