---
id: CAS-013
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-22
change_type: enhancement
related_specs: [CAS-007, CAS-011]
touches:
  database: true
  api: true
  ui: false
  worker: true
  b2: true
  config: true
  security: false
migrations:
  - 20260922_220000__CAS-013-odrive-sync-bucket-root-path-mapping.sql
---

# CAS-013 — Change: Resolve O-Drive/B2 File Paths via Per-Bucket Root Mapping (BRD §12.3 Item 2)

Implements BRD v2.2 §12.3 item 2 (confirmed 2026-09-22, R. Devarajan, TraceCounts) against CAS-007's
already-merged O-Drive Sync and Content Scan feature. **Depends on CAS-011** for the config table
this spec consumes. **CAS-011 is merged and deployed** (status `review`, Implementation Log
complete, migration `20260922_210000__CAS-011-file-index-content-removal-and-path-mapping-config.sql`
applied) — the migration-ordering dependency this spec originally flagged (§14) is satisfied.
**Reconciliation note (2026-09-22, corrected):** an earlier revision of this note described CAS-011's
table as `dbo.odrive_bucket_mapping` (`bucket_name NVARCHAR(255)`, a single `local_root_path
NVARCHAR(1000)`, `is_active BIT`, standard audit columns). That was based on CAS-011's draft, not its
final as-built shape. **CAS-011's actual, shipped table carries two root-path columns per bucket
row, not one:** `bucket_name NVARCHAR(255)`, `odrive_root_path NVARCHAR(1000)`,
`b2_mirror_root_path NVARCHAR(1000)`, `is_active BIT`, standard audit columns,
`UQ_odrive_bucket_mapping_bucket_name` (filtered, `WHERE is_active = 1`), read via
`dbo.odrive_bucket_mapping_get`/`_lst`/`_ins`/`_upd`/`_del`. This spec (O-Drive Sync's `SyncCopy`/
`SyncUpdate`) only ever needs the **O-Drive-mirror-side** path — it never reads a local B2-mirror
file (Sync Purge talks to B2 over `IB2Client`, not the local B2 mirror; Keyword Search's local
B2-mirror reads are CAS-012's concern, not this spec's). So `IBucketRootMappingRepository`'s single
`GetRootPathAsync(bucketName, ct)` (§5) resolves correctly to `odrive_root_path` specifically — this
spec's own previously-open question about a "separate B2-mirror-side path" (§14) is resolved: no gap
exists, because this spec never needed that column in the first place.

---

## §1 Current Behavior

**Content Scan comparison (`ContentScanTaskProcessor.cs`) — already correct, no filesystem access
involved.**

`ContentScanTaskProcessor.cs:47-48` builds two in-memory maps, one per `source` (`ODrive`/`B2`),
keyed by `(bucket_name, relative_path)` (`ContentScanTaskProcessor.cs:133-134`,
`Key(FileIndexRow row) => (row.BucketName ?? string.Empty, row.RelativePath)`). The comparison never
opens a file or touches a physical path (CAS-007 §3/§5 — "Never calls `IB2Client`", comment at
`ContentScanTaskProcessor.cs:12-14`); it only reads `dbo.file_index` metadata rows via
`IFileIndexRepository.ListActiveBySourceAsync` and compares `sha1_hash` values
(`ContentScanTaskProcessor.cs:82-105`). Because BRD §12.3 item 2 confirms `relative_path` is
identical on both sides of the mirror (only the root differs, and the root is per-bucket, not
per-file), the existing `(bucket_name, relative_path)` key **already implements the confirmed
convention correctly, by construction** — it was written before the convention was made explicit
(CAS-003 §15 flagged it open; CAS-007 §4 explicitly deferred it), but the join key it chose happens
to match the now-confirmed rule exactly. **No code change is needed in this file.**

**Sync action processors — real gap: local root resolution is single-root, not per-bucket.**

- `ODriveFileReader.cs:13-28` (the sole `IODriveFileReader` implementation) reads exactly one
  filesystem root from a single flat configuration key, `Sync:ODriveMirrorRootPath`
  (`ODriveFileReader.cs:17`), and joins it with `relativePath` via `Path.Combine(_rootPath,
  relativePath)` (`ODriveFileReader.cs:26`). There is no `bucketName` parameter on
  `IODriveFileReader.ReadAllBytesAsync` (`IODriveFileReader.cs:27`) and no lookup against
  `dbo.file_index.bucket_name` at all. This is correct only as long as exactly one bucket/root pair
  exists in the deployment; it silently reads from the wrong physical location the moment a second
  bucket (with its own O-Drive root, per BRD §12.3 item 2's own `CachetServer-C` example) is
  introduced.
- `SyncCopyTaskProcessor.cs:61-64` calls `_odriveFileReader.ReadAllBytesAsync(row.OdriveRelativePath,
  ct)` — no bucket is passed, because none is available: `SyncResultRow` (§`SyncResultRow.cs:1-47`)
  exposes `OdriveRelativePath`/`OdriveFileName`/etc. but **no `OdriveBucketName` field**, even though
  the underlying `dbo.file_index` row it is joined from already has one (`FileIndexRow.cs:22`,
  `BucketName`). The bucket identity is dropped between `dbo.file_index` and `SyncResultRow`.
- `SyncCopyTaskProcessor.cs:62` additionally reads a single global `_configuration["B2:BucketName"]`
  and stamps every newly-inserted B2-side `dbo.file_index` row with that one value
  (`SyncCopyTaskProcessor.cs:94-98`), regardless of which bucket the O-Drive source row actually
  belongs to. Today this is harmless only because exactly one bucket is configured; it is a latent
  correctness gap the moment `dbo.file_index.bucket_name` ever holds more than one distinct value,
  since a wrong `bucket_name` on the new B2-side row would silently break the next Content Scan's
  `(bucket_name, relative_path)` join (§1's own confirmed-correct key would then compare the wrong
  pairs).
- `SyncUpdateTaskProcessor.cs:75-76` has the same single-root read gap as Copy (no bucket passed to
  `ReadAllBytesAsync`); it does not stamp a new `file_index` row (it updates the existing B2-side
  row in place, `SyncUpdateTaskProcessor.cs:92-94`), so it does not share Copy's bucket-name-stamping
  gap, only the root-resolution gap.
- `SyncPurgeTaskProcessor.cs` never reads a local O-Drive file at all (`§0` doc comment: "No local
  O-Drive read") — it only calls `IB2Client.ListFileVersionsAsync`/`DeleteFileVersionAsync` against
  `row.B2RelativePath` (`SyncPurgeTaskProcessor.cs:69,141-173`). **No change needed here.**
- `IB2Client`/`B2Client.cs` (CAS-001) is itself scoped to exactly **one** configured bucket for its
  entire lifetime (`B2Client.cs:57` `_bucketName = RequireConfig(configuration, "B2:BucketName")`,
  used to resolve a single cached `bucketId` for every call). BRD §12.3 item 2's mapping decision is
  about the **local O-Drive mirror root**, not about `IB2Client` supporting multiple B2 buckets —
  that is a separate, larger architectural question this spec does not open (see §6/§14).

---

## §2 Desired Behavior

- **Content Scan comparison: unchanged.** `ContentScanTaskProcessor.cs`'s `(bucket_name,
  relative_path)` key stays exactly as-is — it already implements BRD §12.3 item 2 correctly.
  Confirming this in writing resolves the previously-open "O-Drive-to-bucket mapping convention"
  item (CAS-003 §15, CAS-007 §4) without touching the file.
- **Every place that needs an actual local filesystem path resolves the per-bucket root from
  CAS-011's new configuration table, then joins it with `relative_path`:**
  - `IODriveFileReader.ReadAllBytesAsync` gains a required `bucketName` parameter:
    `Task<byte[]> ReadAllBytesAsync(string bucketName, string relativePath, CancellationToken ct)`.
  - `ODriveFileReader` (Api project) looks up `bucketName` against CAS-011's bucket-root-mapping
    table (via a new repository interface, e.g. `IBucketRootMappingRepository.GetRootPathAsync
    (bucketName, ct)`) instead of reading a single flat `Sync:ODriveMirrorRootPath` value, then joins
    the resolved root with `relativePath` exactly as today (`Path.Combine(root, relativePath)`).
  - `SyncResultRow` gains an `OdriveBucketName` field, populated by `dbo.sync_result_get`/
    `dbo.sync_result_lst`'s existing join to `dbo.file_index` (the column already exists there,
    `FileIndexRow.BucketName`/`dbo.file_index.bucket_name` — this is a SELECT-list addition, not new
    DDL).
  - `SyncCopyTaskProcessor` and `SyncUpdateTaskProcessor` pass `row.OdriveBucketName` to
    `ReadAllBytesAsync` instead of relying on a single implicit root.
  - `SyncCopyTaskProcessor` stamps the new B2-side `dbo.file_index` row's `bucket_name` with
    `row.OdriveBucketName` (falling back to `_configuration["B2:BucketName"]` only if the source row
    somehow has no bucket — see §9 V2) instead of unconditionally using the global config value.
- **Sync Purge: unchanged.** No local path is ever read; nothing in this spec's scope touches
  `SyncPurgeTaskProcessor.cs`.
- **Fail clearly, not silently, when no mapping exists.** If `dbo.file_index.bucket_name` is `NULL`
  or has no matching row in CAS-011's mapping table, `SyncCopy`/`SyncUpdate` must fail that specific
  file with `SyncActionFailedLocal` (the existing failure path, `SyncCopyTaskProcessor.cs:80-85`) and
  a clear, non-path-leaking error, not fall back to a wrong or empty root (§9 V1/V2).
- **Content Scan's own out-of-scope note is corrected.** CAS-007 §4's "the O-Drive-to-bucket mapping
  convention... assumes the mapping already exists on every `dbo.file_index` row it reads" is now
  formally resolved by BRD §12.3 item 2 and CAS-011's table — CAS-007 itself is not reopened or
  re-implemented; this spec only records the resolution and fixes the two Worker processors that
  actually need a resolved filesystem path.

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** BRD v2.2 §12.3 item 2 (confirmed 2026-09-22, R. Devarajan, TraceCounts) — the B2
  bucket and the local O-Drive mirror follow the same relative directory structure beneath a
  per-bucket root folder, stored in a new configuration table, not hardcoded or inferred. This
  resolves the previously open "O-Drive-to-bucket mapping convention" item flagged at CAS-003 §15,
  carried forward as an explicit Out-of-Scope assumption in CAS-007 §4, and originally raised by WBS
  Task 34's note on B2 mirror population.
- **Risk if not changed:** `SyncCopy`/`SyncUpdate` continue reading from a single hardcoded
  filesystem root (`Sync:ODriveMirrorRootPath`) regardless of which bucket a file belongs to, and
  `SyncCopy` continues stamping every new B2-side `file_index` row with one global bucket name
  regardless of the source row's actual bucket. Both are latent, not yet triggered by a production
  incident (only one bucket is configured today) — but they will silently corrupt `file_index.
  bucket_name` (and, downstream, the next Content Scan's comparison) the moment a second bucket/root
  pair is onboarded, with no validation error to surface the mistake.

---

## §4 Blast Radius (Graph Impact)

> graphify not yet vendored for a fresh analysis of this change — this section is a manual
> grep/read of the codebase, not a `graphify explain`/`query` run.

### Primary Change Target
File: `src/Cachet.ArchiveSync.Core/Interfaces/IODriveFileReader.cs` (signature change) and
`src/Cachet.ArchiveSync.Api/Storage/ODriveFileReader.cs` (implementation).
Entity: `IODriveFileReader.ReadAllBytesAsync`.
Current edges (manual read): called by `SyncCopyTaskProcessor` and `SyncUpdateTaskProcessor` only.

### Direct Dependents (nodes with edges INTO the changed entity)
- `src/Cachet.ArchiveSync.Worker/SyncCopyTaskProcessor.cs` (`ReadAllBytesAsync` call site,
  `SyncCopyTaskProcessor.cs:78`)
- `src/Cachet.ArchiveSync.Worker/SyncUpdateTaskProcessor.cs` (`ReadAllBytesAsync` call site,
  `SyncUpdateTaskProcessor.cs:76`)
- Any test double implementing `IODriveFileReader` (`Cachet.ArchiveSync.Worker.Tests`:
  `SyncCopyTaskProcessorTests.cs`, `SyncUpdateTaskProcessorTests.cs`,
  `SyncActionProcessorSecurityTests.cs`, `SyncTaskProcessorLogHygieneTests.cs` — grepped via
  `IODriveFileReader`, all four reference it) — each of these must update its mock/fake signature.

### Downstream Modules
- **O-Drive Sync** (this spec's whole subject) — directly affected.
- **B2 Integration** — indirectly: the new B2-side `file_index.bucket_name` stamping logic in
  `SyncCopyTaskProcessor` touches the same `IFileIndexRepository.InsertAsync` call CAS-001/CAS-007
  already use; `IB2Client`/`B2Client.cs` itself is untouched (§1's single-bucket-client note is
  out of scope, not modified).
- **Task Manager**, **Keyword Search**, **Quarantine**, **Auth**, **Audit Log**, **Reporting** — not
  affected; no shared file touches any of those modules' code.

### Cross-Project Risk
- `Core` (`IODriveFileReader`, `SyncResultRow`) is shared by both `Api` (implementation,
  `ODriveFileReader`) and `Worker` (`SyncCopyTaskProcessor`/`SyncUpdateTaskProcessor` consumers) —
  a signature change here requires both projects to be redeployed together, same posture CAS-007 §14
  already established for its own rollout.
- `Web` is not touched (`touches.ui: false` — the O-Drive Sync screen, WBS Task 23, does not exist
  yet and is unaffected).

### God Node Involvement
`IB2Client` is this codebase's highest-edge-count interface (used by Api cross-check, Worker mirror
population, and all three sync action processors per CAS-001/CAS-007) but is **not modified** by
this spec — flagged only because `SyncCopyTaskProcessor`, one of this spec's two changed files,
depends on it. `IODriveFileReader` itself is a low-edge-count node (two Worker consumers) — not a
god node.

### Graphify Queries Run
None — "graphify not yet vendored for this change; analysis done via manual code read and grep for
`bucket_name`/`BucketName`/`root`/`ODriveMirrorRootPath`/`ODriveFileReader` across `src/`."

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `src/Cachet.ArchiveSync.Core/Interfaces/IODriveFileReader.cs` | modify | Add required `bucketName` parameter to `ReadAllBytesAsync`; update XML doc to remove the now-resolved BLOCKED note about mapping convention and reference CAS-013/CAS-011 instead. |
| `src/Cachet.ArchiveSync.Api/Storage/ODriveFileReader.cs` | modify | Resolve the local root per `bucketName` via a new bucket-root-mapping lookup (CAS-011's config table) instead of the single flat `Sync:ODriveMirrorRootPath` key; throw a clear, non-path-leaking exception if no mapping row exists for the bucket. |
| `src/Cachet.ArchiveSync.Core/Interfaces/IBucketRootMappingRepository.cs` | add | New interface (Core), one method: `Task<string?> GetRootPathAsync(string bucketName, CancellationToken ct)` — wraps CAS-011's `dbo.odrive_bucket_mapping_get` read and returns its `odrive_root_path` column specifically (this spec never needs `b2_mirror_root_path` — §0 reconciliation note). Not a duplicate of CAS-012's `IBucketMirrorRootRepository` (which is source-aware, selecting either root column); kept as a separate, narrower interface here per Karpathy P4 — see §14's existing note on CAS-007/CAS-012 consolidation as a future follow-on. |
| `src/Cachet.ArchiveSync.Api/Repositories/BucketRootMappingRepository.cs` | add | Dapper implementation of the interface above, calling `dbo.odrive_bucket_mapping_get` (CAS-011, confirmed name and shape) and projecting only `odrive_root_path`. |
| `src/Cachet.ArchiveSync.Core/Dtos/SyncResultRow.cs` | modify | Add `public string? OdriveBucketName { get; init; }` (populated from `dbo.file_index.bucket_name` via the existing join in `dbo.sync_result_get`/`_lst`; no new DDL — an additional SELECT column on an already-existing join). |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_get.sql` | modify | Add `bucket_name` (aliased `odrive_bucket_name`) to the SELECT list from the existing O-Drive-side `file_index` join. Owned by CAS-007; modified here since no other spec currently touches it. |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_lst.sql` | modify | Same SELECT-list addition as `_get`. |
| `src/Cachet.ArchiveSync.Api/Repositories/SyncResultRepository.cs` | modify | Map the new `odrive_bucket_name` column onto `SyncResultRow.OdriveBucketName` in both the `Get` and `List` Dapper row projections. |
| `src/Cachet.ArchiveSync.Worker/SyncCopyTaskProcessor.cs` | modify | Pass `row.OdriveBucketName` to `ReadAllBytesAsync`; stamp the new B2-side `file_index` row's `bucket_name` with `row.OdriveBucketName` (falling back to `_configuration["B2:BucketName"]` only if `OdriveBucketName` is null, per §9 V2) instead of the unconditional config read at line 62. |
| `src/Cachet.ArchiveSync.Worker/SyncUpdateTaskProcessor.cs` | modify | Pass `row.OdriveBucketName` to `ReadAllBytesAsync`. No `file_index.bucket_name` stamping change needed — Update does not insert a new row. |
| `src/Cachet.ArchiveSync.Worker.Tests/SyncCopyTaskProcessorTests.cs` | modify | Update `IODriveFileReader` mock call signature and add a test asserting `bucket_name` propagation (see §13). |
| `src/Cachet.ArchiveSync.Worker.Tests/SyncUpdateTaskProcessorTests.cs` | modify | Update `IODriveFileReader` mock call signature. |
| `src/Cachet.ArchiveSync.Worker.Tests/SyncActionProcessorSecurityTests.cs` | modify | Update `IODriveFileReader` mock call signature (compile fix only — no behavioral assertions change). |
| `src/Cachet.ArchiveSync.Worker.Tests/SyncTaskProcessorLogHygieneTests.cs` | modify | Update `IODriveFileReader` mock call signature (compile fix only). |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | modify | Remove or deprecate `Sync:ODriveMirrorRootPath` (superseded by the per-bucket mapping table) — see §9 for the exact decision. |
| `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | modify | Same as above, if the Worker project independently declares the key. |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `src/Cachet.ArchiveSync.Worker/ContentScanTaskProcessor.cs` | §1/§2: its `(bucket_name, relative_path)` comparison key already implements BRD §12.3 item 2 correctly — it never resolves or reads a physical path, only `dbo.file_index` metadata. |
| `src/Cachet.ArchiveSync.Worker/SyncPurgeTaskProcessor.cs` | Never reads a local O-Drive file; only calls `IB2Client` against `row.B2RelativePath`. No per-bucket root resolution applies to Purge. |
| `src/Cachet.ArchiveSync.Core/Interfaces/IB2Client.cs`, `src/Cachet.ArchiveSync.Api/B2/B2Client.cs` | Scoped to exactly one configured B2 bucket by design (CAS-001) — BRD §12.3 item 2 is about the **local O-Drive mirror root**, not B2-side multi-bucket support. Genuinely out of scope; flagged as a separate, larger question in §14, not silently absorbed into this spec. |
| `src/Cachet.ArchiveSync.Core/Dtos/FileIndexRow.cs` | Already carries `BucketName` (`FileIndexRow.cs:22`) — no change needed; the gap is that this value isn't propagated onward into `SyncResultRow`, which §5 fixes at the DTO/SP level, not here. |
| `src/Cachet.ArchiveSync.Core/Dtos/RemovedFromODriveItem.cs` | API response DTO for the "Removed" panel — never needs a resolved filesystem path (Purge, which consumes "Removed" rows, doesn't read local files either). |
| `specs/api/cachet-archive-sync/CAS-007-o-drive-sync-content-scan-api.md` | Merged spec, out of this change's file list per template convention — its §4/§15 open item is resolved by *this* spec's existence, not by editing CAS-007's own text. |
| `src/Cachet.ArchiveSync.Api/Controllers/SyncController.cs` | No request/response contract change (§8) — the five existing endpoints are unaffected; the fix is entirely inside the Worker-side file-read path. |

---

## §7 Database Changes

### Changed Objects
| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| `dbo.sync_result_get` | dbo | SELECT-list addition (`bucket_name` from the existing O-Drive-side `file_index` join, aliased `odrive_bucket_name`) | Owned by CAS-007; no signature (parameter) change, additive column only — not breaking. |
| `dbo.sync_result_lst` | dbo | Same SELECT-list addition | Same as above. |

### New Objects Consumed (owned by CAS-011, not this spec)
This spec introduces **no new tables or stored procedures of its own** — it consumes CAS-011's
`dbo.odrive_bucket_mapping` table (**confirmed, as-built and deployed**, corrected 2026-09-22):
`odrive_bucket_mapping_id INT IDENTITY PK`, `bucket_name NVARCHAR(255)` (unique among active rows,
filtered index), `odrive_root_path NVARCHAR(1000)`, `b2_mirror_root_path NVARCHAR(1000)`,
`is_active BIT`, plus standard audit columns; and its read stored procedure
`dbo.odrive_bucket_mapping_get`. `IBucketRootMappingRepository` (§5) calls
`dbo.odrive_bucket_mapping_get` and projects only the `odrive_root_path` column — this spec's Sync
actions (`SyncCopy`/`SyncUpdate`) never read a local B2-mirror file, so `b2_mirror_root_path` is not
needed here; that column exists in CAS-011 to serve CAS-012 (Keyword Search's filesystem content
search), which does read both mirrors. **This resolves the gap previously flagged in §14** — there is
no gap against CAS-011 to raise; CAS-011 already carries both roots, and this spec simply doesn't
consume the one it doesn't need.

### Migration Required
Yes, for the two SELECT-list additions above:
- Migration: `Database/migrations/20260922_220000__CAS-013-odrive-sync-bucket-root-path-mapping.sql`
  (append-only, `ALTER PROCEDURE` for `dbo.sync_result_get`/`dbo.sync_result_lst` only — no table
  DDL owned by this spec).
- Mirror update(s): `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_get.sql`,
  `dbo.sync_result_lst.sql`.
- **Ordering dependency satisfied:** CAS-011's migration (`20260922_210000__...`) is merged and
  deployed — `dbo.odrive_bucket_mapping` already exists in the target environment, so this spec's
  migration (timestamped `20260922_220000`, later than CAS-011's) can proceed without the sequencing
  risk originally flagged here.

---

## §8 API Contract Changes

N/A — no request/response field changes. All five `POST /sync/*` / `GET /sync/{taskId}/results`
endpoints (CAS-007 §6) keep their existing contracts; this change is entirely internal to the Worker
processors and their local-file-read dependency. `fileId`/`path`/`name` fields returned by
`GET /sync/{taskId}/results` are unaffected — `bucket_name` is a resolution detail, never surfaced in
any API response (consistent with CAS-007 §11's existing rule against exposing raw local paths).

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | `ODriveFileReader` throws only if `Sync:ODriveMirrorRootPath` is entirely unset (a startup-time/first-call config check, not per-bucket) | `ODriveFileReader.ReadAllBytesAsync(bucketName, relativePath, ct)` throws a clear, descriptive exception (not a generic `FileNotFoundException`) when `bucketName` has no row in the CAS-011 mapping table — this failure is caught by the existing `SyncActionFailedLocal` per-file failure path in `SyncCopyTaskProcessor`/`SyncUpdateTaskProcessor` (§1), it does not abort the batch | `Test_V1_UnmappedBucketFailsThatFileLocalReadNotWholeBatch` |
| V2 | `SyncCopyTaskProcessor` always stamps the new B2-side `file_index.bucket_name` with `_configuration["B2:BucketName"]`, regardless of the source row | `SyncCopyTaskProcessor` stamps `row.OdriveBucketName` when present; only falls back to `_configuration["B2:BucketName"]` if `row.OdriveBucketName` is `null` (an O-Drive-side `file_index` row somehow has no bucket) — the fallback path logs a warning so a silent legacy-data gap is visible, not hidden | `Test_V2_NewB2FileIndexRowInheritsSourceBucketName` |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Given two `dbo.file_index` fixtures with different `bucket_name` values, each mapped to a distinct root in the bucket-root-mapping table, `SyncCopy`'s Worker processor reads each file's bytes from its own bucket's root, not a single shared root | `Test_SC1_SyncCopyReadsFromCorrectPerBucketRoot` |
| SC-2 | A `SyncCopy` targeting a `sync_result` row whose O-Drive-side bucket has no mapping row fails only that file (`SyncActionFailedLocal`, `sync_status = 'Failed'`), leaving other files in the same batch unaffected (reuses CAS-007 SC-9's per-file-independence guarantee) | `Test_SC2_UnmappedBucketFailsOnlyThatFilePerFileIndependenceHolds` |
| SC-3 | A newly-copied file's B2-side `dbo.file_index` row carries the same `bucket_name` as its O-Drive-side source row, verified end-to-end through a subsequent `ContentScan` producing no false "Removed"/"New" pair for that file | `Test_SC3_CopiedFileBucketNameRoundTripsThroughNextContentScan` |

---

## §11 Security Notes

No new credential handling — this spec introduces no new B2 keys, session tokens, or auth policy
changes (`touches.security: false`). The resolved local filesystem path (bucket root joined with
`relative_path`) must never appear in a log line, matching CAS-007 §11's existing rule ("Never log:
... a raw local O-Drive filesystem path beyond what the audit log's own `file_path` column already
captures") — this rule already covers the new per-bucket-resolved path, since it is the same category
of value CAS-007 already restricted, just computed differently. No change to `AuditAction` entries or
audit-before-response sequencing (CAS-007 §12 stands unchanged).

---

## §12 Rollback Plan

1. Revert `SyncCopyTaskProcessor.cs`/`SyncUpdateTaskProcessor.cs`/`ODriveFileReader.cs`/
   `IODriveFileReader.cs`/`SyncResultRow.cs` to their CAS-007-merged state (single global
   `Sync:ODriveMirrorRootPath`, unconditional `B2:BucketName` stamping). Safe as long as only one
   bucket/root pair is in production use — the prior behavior was correct for that single-bucket
   case, only incorrect for a second bucket.
2. Database rollback: revert `dbo.sync_result_get`/`dbo.sync_result_lst` to their pre-CAS-013 bodies
   (drop the `odrive_bucket_name` SELECT column) — additive-only change, safe to revert without data
   loss since no table DDL was introduced by this spec.
3. Config rollback: restore `Sync:ODriveMirrorRootPath` in `appsettings.Development.json` if it was
   removed (§9 decision) rather than merely deprecated.
4. Task-queue rollback: this spec does not alter `dbo.task_queue`'s shape, any `task_type` value, or
   any in-flight `SyncCopy`/`SyncUpdate`/`SyncPurge` row's contract — a `Queued`/`Running` row created
   under the old code continues to process correctly against the reverted code, since neither
   `request_parameters` nor `result_detail` shapes change in this spec.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `ODriveFileReaderTests` (new or extended) | `Test_SC1_SyncCopyReadsFromCorrectPerBucketRoot` (reader-level: two bucket fixtures, two roots) | SC-1 |
| `SyncCopyTaskProcessorTests` | `Test_SC1_SyncCopyReadsFromCorrectPerBucketRoot` (processor-level, mocked `IODriveFileReader` asserting `bucketName` argument) | SC-1 |
| `SyncCopyTaskProcessorTests` | `Test_SC2_UnmappedBucketFailsOnlyThatFilePerFileIndependenceHolds` | SC-2 |
| `SyncCopyTaskProcessorTests` | `Test_V2_NewB2FileIndexRowInheritsSourceBucketName` | V2 |
| `SyncUpdateTaskProcessorTests` | Equivalent bucket-argument-propagation assertion for Update (no new SC — covered under SC-1's reader-level test plus a processor-level mock assertion) | SC-1 (Update case) |
| `BucketRootMappingRepositoryTests` (integration, real SQL Server, depends on CAS-011's migration) | `Test_V1_UnmappedBucketFailsThatFileLocalReadNotWholeBatch` (repository returns null for an unmapped bucket) | V1 |
| `SyncActionServiceIntegrationTests` or `ContentScanTaskProcessorTests` (extended) | `Test_SC3_CopiedFileBucketNameRoundTripsThroughNextContentScan` | SC-3 |

Coverage target: ≥ 85% on changed files, consistent with CAS-007's own accepted-deviation note
(§13 caveat: DB-repository classes such as `BucketRootMappingRepository` may require a live SQL
Server instance not available in the sandbox, same structural gap CAS-007 §"Coverage gate note"
already documented and had accepted by R. Devarajan — flag, don't silently re-fail the same gate).

---

## §14 Open Questions

- [x] **RESOLVED 2026-09-22 — CAS-011 has been drafted.** `IBucketRootMappingRepository`'s shape,
      table name (`dbo.odrive_bucket_mapping`), and column names (`bucket_name`/`local_root_path`)
      are reconciled against CAS-011's actual draft throughout this spec (§5, §7). No longer a
      blocker on this spec's readiness — remaining open items below are unrelated design questions.
- [x] **RESOLVED 2026-09-22 — CAS-011's root-path column shape.** Corrected: CAS-011's as-built
      `dbo.odrive_bucket_mapping` already carries **both** `odrive_root_path` and
      `b2_mirror_root_path` per bucket row (not the single `local_root_path` this spec's prior
      revision assumed). No gap exists — `SyncCopy`/`SyncUpdate` only ever need the O-Drive-mirror
      side (§0 reconciliation note, §7), which `odrive_root_path` already provides; the
      `b2_mirror_root_path` column exists to serve CAS-012, a different spec's different need, not
      this one's.
- [x] **RESOLVED 2026-09-22 (R. Devarajan, TraceCounts) — `Sync:ODriveMirrorRootPath` is deleted
      outright, not kept as a fallback.** Every bucket written into `dbo.file_index` must have an
      active row in CAS-011's `dbo.odrive_bucket_mapping` table; `SyncCopy`/`SyncUpdate` fail that
      specific file (`SyncActionFailedLocal`, §9 V1) with no silent fallback if the mapping is
      missing. §5's file list (removing/deprecating the key in both projects' `appsettings.
      Development.json`) and §12's rollback note already reflect this decision as-written — no other
      section needs to change as a result of this confirmation.
- [x] **RESOLVED 2026-09-22 (R. Devarajan, TraceCounts) — `IB2Client`'s single-bucket architecture
      stays out of scope.** Recorded here for visibility only; no CAS-001 change is committed by this
      decision. If true multi-bucket B2 support is ever needed, that is tracked as a separate,
      future CAS-001-owned spec, not opened now.
- [x] **RESOLVED 2026-09-22 — Migration ordering dependency on CAS-011 (§7).** CAS-011 is merged and
      deployed (migration `20260922_210000__...` applied); `dbo.odrive_bucket_mapping` exists in the
      target environment ahead of this spec's own migration. Remaining requirement before
      `/implement CAS-013`: confirm the table has active mapping rows populated for every bucket
      currently written into `dbo.file_index`, so `SyncCopy`/`SyncUpdate` don't fail every file with
      "no mapping" errors on day one — a data-population check, not a schema/ordering blocker.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 | Draft — reconciled against CAS-011; remaining open items are design questions, not blockers (§14) |
| Author (correction) | via Claude Code | 2026-09-22 | Corrected §0/§5/§7/§14 to match CAS-011's actual as-built `dbo.odrive_bucket_mapping` shape (`odrive_root_path` + `b2_mirror_root_path`, not one `local_root_path`) and confirmed CAS-011 is merged/deployed, resolving the migration-ordering item. Remaining open items (`Sync:ODriveMirrorRootPath` fallback decision, `IB2Client` multi-bucket scope) are unrelated design questions, still pending owner sign-off — `status` remains `draft` until those are resolved. |
| Owner sign-off | R. Devarajan (TraceCounts) | 2026-09-22 | Resolved both remaining §14 items: `Sync:ODriveMirrorRootPath` is deleted outright (no fallback default); `IB2Client`'s single-bucket architecture stays out of scope, noted for visibility only, no follow-on spec opened now. All §14 items resolved — `status` moved to `ready`. |
| Reviewer | | | Pending |

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage SyncCopy 90% / SyncUpdate 98.1%; DB-repo files 0% (accepted gap, no live SQL Server, per CAS-007 precedent) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Database/migrations/20260922_220000__CAS-013-odrive-sync-bucket-root-path-mapping.sql` | add |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_get.sql` | modify |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.sync_result_lst.sql` | modify |
| `src/Cachet.ArchiveSync.Core/Interfaces/IODriveFileReader.cs` | modify |
| `src/Cachet.ArchiveSync.Core/Interfaces/IBucketRootMappingRepository.cs` | add |
| `src/Cachet.ArchiveSync.Core/Dtos/SyncResultRow.cs` | modify |
| `src/Cachet.ArchiveSync.Api/Repositories/BucketRootMappingRepository.cs` | add |
| `src/Cachet.ArchiveSync.Api/Repositories/SyncResultRepository.cs` | modify |
| `src/Cachet.ArchiveSync.Api/Storage/ODriveFileReader.cs` | modify |
| `src/Cachet.ArchiveSync.Worker/SyncCopyTaskProcessor.cs` | modify |
| `src/Cachet.ArchiveSync.Worker/SyncUpdateTaskProcessor.cs` | modify |
| `src/Cachet.ArchiveSync.Api/Program.cs` | modify |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | modify |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | modify |
| `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | modify |
| `src/Cachet.ArchiveSync.Worker.Tests/SyncCopyTaskProcessorTests.cs` | modify |
| `src/Cachet.ArchiveSync.Worker.Tests/SyncUpdateTaskProcessorTests.cs` | modify |
| `src/Cachet.ArchiveSync.Worker.Tests/SyncActionProcessorSecurityTests.cs` | modify |
| `src/Cachet.ArchiveSync.Worker.Tests/SyncTaskProcessorLogHygieneTests.cs` | modify |
| `src/Cachet.ArchiveSync.Api.Tests/ODriveFileReaderTests.cs` | add |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/BucketRootMappingRepositoryTests.cs` | add |
| `src/Cachet.ArchiveSync.Worker.Tests/ContentScanTaskProcessorTests.cs` | modify |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `SyncCopyTaskProcessorTests` | `Test_SC1_SyncCopyReadsFromCorrectPerBucketRoot` | SC-1 |
| `SyncCopyTaskProcessorTests` | `Test_SC2_UnmappedBucketFailsOnlyThatFilePerFileIndependenceHolds` | SC-2 |
| `SyncCopyTaskProcessorTests` | `Test_V2_NewB2FileIndexRowInheritsSourceBucketName` | V2 |
| `SyncCopyTaskProcessorTests` | `Test_V2_NewB2FileIndexRowFallsBackToConfiguredBucketWithWarningWhenSourceHasNoBucket` | V2 (fallback branch) |
| `SyncUpdateTaskProcessorTests` | `Test_SC1_SyncUpdateReadsFromCorrectPerBucketRoot` | SC-1 (Update case) |
| `SyncUpdateTaskProcessorTests` | `Test_UnprocessableSyncResultRow_SkippedAsFailedWithoutTouchingReaderOrB2` | coverage top-up |
| `SyncUpdateTaskProcessorTests` | `Test_ProcessAsync_LocalReadFailure_WritesFailedLocalAuditAndSkipsB2` | coverage top-up |
| `SyncUpdateTaskProcessorTests` | `Test_ProcessAsync_MalformedRequestParameters_MarksTaskFailedViaOuterCatch` | coverage top-up |
| `ODriveFileReaderTests` | `Test_SC1_SyncCopyReadsFromCorrectPerBucketRoot` | SC-1 (reader-level) |
| `ODriveFileReaderTests` | `Test_V1_UnmappedBucketFailsThatFileLocalReadNotWholeBatch` | V1 (unit-level) |
| `BucketRootMappingRepositoryTests` | `Test_V1_UnmappedBucketFailsThatFileLocalReadNotWholeBatch` | V1 (DB-integration, skipped - no live SQL Server) |
| `ContentScanTaskProcessorTests` | `Test_SC3_CopiedFileBucketNameRoundTripsThroughNextContentScan` | SC-3 |

### Migration
`20260922_220000__CAS-013-odrive-sync-bucket-root-path-mapping.sql`
