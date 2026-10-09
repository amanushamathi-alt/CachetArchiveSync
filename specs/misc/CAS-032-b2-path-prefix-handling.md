---
id: CAS-032
target_app: cachet-archive-sync
status: draft
owner: devarajan@tracecounts.org
created: 2026-09-30
priority: P0
change_type: enhancement               # bug-fix | enhancement | refactor | security | deprecation
related_specs: [CAS-001, CAS-007, CAS-008, CAS-011, CAS-012, CAS-013, CAS-018, CAS-031]
touches:
  database: false
  api: true
  ui: false
  worker: true
  b2: true
  config: false
  security: true
migrations: []
---

# CAS-032 — Change: B2 Path Prefix Handling (sync root inside the bucket)

> **SUPERSEDED 2026-09-30 by CAS-033 — do not implement.** The decision was to fix the prefix issue in
> the index-refresh scripts and the folder layout (full B2 key stored in `relative_path`) instead of in the
> application. Kept for history only.

> **Hand-off note.** File paths and line numbers in §1 and §5 are from the codebase as of 2026-09-30; locate
> by class and method name if they have moved. All open questions (§14) are resolved. No database migration
> and no endpoint change.

## §1 Current Behavior

`dbo.odrive_bucket_mapping.b2_path_prefix` (added by CAS-031) records the folder inside the B2 bucket
that is the sync root. Production: bucket `Cachet-Server-C`, prefix `D/Shares/Operations`. Dev: bucket
`tc-test-Server`, prefix `Operations`. `dbo.odrive_bucket_mapping_get` already returns the column.

**No C# code reads it.** Every B2 call uses `dbo.file_index.relative_path` as the B2 object name, so the
app treats the bucket root as the sync root:

| Operation | Location | B2 name used today |
|---|---|---|
| Keyword Search cross-check | `KeywordSearchTaskProcessor.IsConfirmedOnB2Async` (`KeywordSearchTaskProcessor.cs:244-258`) | `candidate.RelativePath` (list_file_names start name, and the equality check on the first result) |
| Sync Copy upload | `SyncCopyTaskProcessor.cs:103` | `row.OdriveRelativePath` |
| Sync Update upload | `SyncUpdateTaskProcessor.cs:80,102` | `row.B2RelativePath ?? row.OdriveRelativePath` |
| Sync Purge delete | `SyncPurgeTaskProcessor.cs:80` → `DeleteAllVersionsAsync` (`:158-190`) | `row.B2RelativePath` (list_file_versions prefix, exact-name check, delete) |
| Quarantine Permanent Delete | `QuarantinePermanentDeleteService.cs:140` → `DeleteAllVersionsAsync` (`:293-321`) | `entry.Path` (= `file_index.relative_path` via `dbo.quarantine_entry_get`) |

Local reads are not affected: `MirrorFileReader` and `ODriveFileReader` join `b2_mirror_root_path` /
`odrive_root_path` with `relative_path`, and the CAS-031 refresh scripts already load `relative_path` as
relative to the sync root (`scripts/index_refresh/common.py:8`, `:140`).

**Failure mode in production:**
- Copy and Update upload to `finance/a.xlsx` at the bucket root instead of
  `D/Shares/Operations/finance/a.xlsx`.
- The Keyword Search cross-check reports real B2 files as "not on B2".
- Purge and Permanent Delete look for a key that does not exist, so they delete nothing and report
  success or a B2 error. The one risk in the other direction: an unrelated object that happens to sit
  at the bucket root under the same name could be matched.

CAS-031 §4 recorded this as out of scope: "until then, the app's B2 cross-check and sync actions address
the wrong B2 key whenever a prefix is set."

---

## §2 Desired Behavior

For every B2 operation listed in §1, the B2 object name is the **full object key**:

```
object_key = b2_path_prefix + "/" + relative_path        (prefix non-empty)
object_key = relative_path                                (prefix NULL or empty → bucket root)
```

- `relative_path` in `dbo.file_index`, `sync_result`, `search_result` and `quarantine_entry` stays
  **relative to the sync root** and is never modified. The prefix is added when calling B2 and never
  stored in `relative_path`.
- The prefix is read from `dbo.odrive_bucket_mapping` for the **configured bucket** (`B2:BucketName`),
  because `IB2Client` and `IUserScopedB2DeleteClient` always act on that one bucket
  (single-bucket scope, by design). The prefix must belong to the bucket the call actually hits.
- Normalization: prefix backslashes become `/`, leading and trailing `/` are trimmed. Leading `/` on
  `relative_path` is trimmed before joining. No double slashes. No other change to `relative_path`.
- If the configured bucket has **no active mapping row**, B2 operations **fail closed**: no B2 call is
  made. A mapping row with a NULL or empty prefix is a deliberate "bucket root" and works as today.
- Prefix is resolved **once per task or request** and held in a local, not once per file.
- The existing exact-name safety checks in both `DeleteAllVersionsAsync` methods compare against the
  full object key, so a prefix match on a longer-named object can never be deleted.
- Audit-log `FilePath` values are not changed (see §14 Q1).

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** production layout confirmed 2026-09-30 (bucket `Cachet-Server-C`, sync root
  `D/Shares/Operations`); sync scope agreed 2026-09-24 as "bucket + folder prefix in typed config";
  CAS-031 deferred the app-side use.
- **Risk if not changed:** the first production Sync Copy or Update writes files to the wrong place in
  the archive bucket, Keyword Search cross-checks are wrong, and the two irreversible actions
  (Purge, Permanent Delete — FR-16/FR-17/FR-13d) target keys that do not match the catalog. Blocks
  go-live.

---

## §4 Blast Radius (Graph Impact)

> graphify: `graphify-out/` exists (generated 2026-09-24) but `graphify explain "B2Client"` returns only
> the file node (degree 2), so it does not trace `IB2Client` call sites. Blast radius mapped via manual
> grep and read of every `IB2Client`, `IUserScopedB2DeleteClient` and `b2_path_prefix` reference.

### Primary Change Target
Files: the five call sites in §1, plus one new Core helper and one new provider.
Entity: construction of the B2 object name passed to `IB2Client` / `IUserScopedB2DeleteClient`.
Current edges: `IB2Client` has 8 members and 4 production consumers (KeywordSearch, SyncCopy,
SyncUpdate, SyncPurge). `IUserScopedB2DeleteClient` has 1 consumer (`QuarantinePermanentDeleteService`).
Neither interface's signature changes.

### Direct Dependents
- `KeywordSearchTaskProcessor` — cross-check
- `SyncCopyTaskProcessor`, `SyncUpdateTaskProcessor`, `SyncPurgeTaskProcessor`
- `QuarantinePermanentDeleteService` (Api)
- DI composition roots: `Api/Program.cs`, `Worker/Program.cs`
- Test fixtures that construct the five classes above (new constructor parameter)

### Downstream Modules
Keyword Search (FR-08 cross-check), O-Drive Sync (FR-13a–FR-13d), Quarantine (FR-16/FR-17), Audit Log
(unchanged values, §14 Q1). Not affected: Content Scan (never calls B2), Reporting, Task Manager, Auth.

### Cross-Project Risk
**CROSS-PROJECT RISK** (Core ↔ Api ↔ Worker). New Core abstraction implemented in Api and consumed by
Worker (Worker already references Api and registers Api repositories — `Worker/Program.cs:36,52`).
No contract change to any Api endpoint or to `task_queue` payloads; Web is unaffected.

### God Node Involvement
None. Neither `IB2Client` nor `IUserScopedB2DeleteClient` changes shape.

### Graphify Queries Run
```
python -m graphify explain "B2Client"   → file node only, no call-site edges
Manual: grep IB2Client|_b2Client\.|b2_path_prefix|IUserScopedB2DeleteClient across src/ and scripts/
```

### Decomposition Check (P9)
One logical unit spanning Core/Api/Worker. No size threshold crossed (0 endpoints, 0 DB objects,
~10 production files, <10 validations, <12 SCs). **No split.**

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `src/Cachet.ArchiveSync.Core/Storage/B2ObjectKey.cs` (new folder, or `Dtos/`) | add | Static, pure helpers: `Compose(prefix, relativePath)` and `NormalizePrefix(raw)`. No I/O. |
| `src/Cachet.ArchiveSync.Core/Interfaces/IB2PathPrefixProvider.cs` | add | `Task<string> GetPrefixAsync(CancellationToken ct)` — normalized prefix for the configured bucket, `""` = bucket root; throws a non-path-leaking `InvalidOperationException` if no active mapping row. |
| `src/Cachet.ArchiveSync.Api/Repositories/B2PathPrefixProvider.cs` | add | Dapper via `dbo.odrive_bucket_mapping_get(@bucket_name = B2:BucketName)`; same shape as `BucketRootMappingRepository`. |
| `src/Cachet.ArchiveSync.Api/Program.cs` | modify | Register `IB2PathPrefixProvider` (scoped, next to line 186). |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | modify | Register `IB2PathPrefixProvider` (next to line 52). |
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs` | modify | Resolve prefix once per task; compose key in `IsConfirmedOnB2Async`. |
| `src/Cachet.ArchiveSync.Worker/SyncCopyTaskProcessor.cs` | modify | Compose key for `UploadFileAsync`; `file_index` insert keeps `OdriveRelativePath`. |
| `src/Cachet.ArchiveSync.Worker/SyncUpdateTaskProcessor.cs` | modify | Compose key for `UploadFileAsync`; `file_index` update unchanged. |
| `src/Cachet.ArchiveSync.Worker/SyncPurgeTaskProcessor.cs` | modify | Compose key before `DeleteAllVersionsAsync`. |
| `src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs` | modify | Resolve prefix once per request (after the live authorization check); compose key before `DeleteAllVersionsAsync`. |
| Test files (see §13) | modify / add | New constructor parameter in existing fixtures; new prefix tests. |
| `docs/environment-variables.md` | modify | One note: the folder prefix is not an env var, it lives in `odrive_bucket_mapping.b2_path_prefix`. |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `IB2Client.cs`, `B2Client.cs` | Signatures stay; callers pass the full key. Keeps the system-key-only invariant (CAS-001 §7 V1) and its tests. |
| `IUserScopedB2DeleteClient.cs`, `UserScopedB2DeleteClient.cs` | Same: the service passes the full key. CAS-018 per-user key logic is untouched. |
| `MirrorFileReader.cs`, `ODriveFileReader.cs`, `Bucket*Repository.cs` | Local roots already pair with sync-root-relative paths. |
| `ContentScanTaskProcessor.cs` | Never calls B2. |
| All `dbo.*` stored procedures, migrations, `quarantine_entry_get` | No schema change. The configured bucket's prefix needs no per-row bucket name. |
| `scripts/index_refresh/*` | Already prefix-aware (CAS-031). |
| `AuditLogService` and audit action enums | Values unchanged (§14 Q1). |
| Web project | No contract change. |

---

## §7 Database Changes

N/A. `dbo.odrive_bucket_mapping_get` already returns `b2_path_prefix` (CAS-031 migration
`20260924_140000`). No migration.

**Data prerequisite (not code):** production needs one active `odrive_bucket_mapping` row. Only the dev
row exists today. Production layout confirmed 2026-09-30:

| Column | Production value |
|---|---|
| `bucket_name` | `Cachet-Server-C` (must equal `B2__BucketName` exactly; `B2Client` compares case-sensitively) |
| `odrive_root_path` | `D:\O-Drive\Operations` |
| `b2_mirror_root_path` | `D:\Cachet-Server-C\D\Shares\Operations` |
| `b2_path_prefix` | `D/Shares/Operations` |

`b2_mirror_root_path` must be the folder that holds the prefix-relative files, i.e. the full
`...\D\Shares\Operations` folder, **not** `D:\Cachet-Server-C`. Local paths are `root + relative_path`
(`refresh_b2_index.py` `local_path(root, rel)`, `MirrorFileReader`), and `relative_path` has the prefix
stripped. With the shorter root, files would be written and read one level too high.

---

## §8 API Contract Changes

N/A. No endpoint, request or response shape changes.

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | B2 name = `relative_path` | B2 name = `Compose(prefix, relative_path)` at all five call sites | `Test_V1_B2NameIsPrefixPlusRelativePath_AtAllFiveCallSites` |
| V2 | n/a | Empty, whitespace-only or directory-style (`/` suffix) `relative_path`, or a composed key over B2's 1,024-byte (UTF-8) object-name limit, is rejected: no B2 call, per-file failure. (`relative_path` allows up to 1,000 characters, so a long path plus the prefix can exceed the limit.) | `Test_V2_InvalidRelativePathOrOverlongKey_MakesNoB2Call` |
| V3 | n/a | No active mapping row for the configured bucket: no B2 call is made (per-file failure for Copy/Update/Purge/Delete; task failure for Keyword Search) | `Test_V3_NoMappingRow_MakesNoB2Call` |
| V4 | n/a | NULL or empty prefix: key = relative path (bucket-root behavior preserved) | `Test_V4_EmptyPrefix_KeyEqualsRelativePath` |
| V5 | n/a | Prefix normalization: `\D\Shares\Operations\` and `D/Shares/Operations` yield identical keys; no `//` | `Test_V5_PrefixNormalization_NoDoubleSlashes` |
| V6 | Exact-name check on `relative_path` | Exact-name check on the full key in both `DeleteAllVersionsAsync` methods | `Test_V6_DeleteAllVersions_ExactMatchOnFullKey` |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | With prefix `D/Shares/Operations`, Sync Copy uploads `D/Shares/Operations/<relative>` and the new B2 `file_index` row keeps `<relative>` | `Test_SC1_SyncCopy_UploadsPrefixedKey_CatalogKeepsRelativePath` |
| SC-2 | Sync Update uploads to the prefixed key; `file_index` row unchanged apart from sha1/size/times | `Test_SC2_SyncUpdate_UploadsPrefixedKey` |
| SC-3 | Sync Purge lists versions by the prefixed key and deletes only exact-key versions; an object at the bucket root with the same relative path is never touched | `Test_SC3_SyncPurge_DeletesOnlyExactPrefixedKey` |
| SC-4 | Permanent Delete does the same via the user-scoped client, after the live `deleteFiles` check, and still writes the audit entry before the success result | `Test_SC4_PermanentDelete_UsesPrefixedKey_AuditBeforeSuccess` |
| SC-5 | Keyword Search cross-check confirms a file only when B2 returns the exact prefixed key with `Action == "upload"` | `Test_SC5_CrossCheck_ConfirmsOnlyExactPrefixedKey` |
| SC-6 | With NULL/empty prefix every operation behaves exactly as before this change (existing tests pass unchanged apart from the constructor parameter) | existing suites |
| SC-7 | With no active mapping row for the configured bucket, no B2 call is made by any of the five operations | `Test_SC7_NoMappingRow_NoB2CallFromAnyOperation` |
| SC-8 | No log line or audit entry contains the prefix beyond what `FilePath` already contained (§11) | `Test_SC8_NoNewPathOrPrefixInLogsOrAudit` |
| SC-9 | Prefix is read from the database once per task/request, not once per file | `Test_SC9_PrefixResolvedOncePerTaskOrRequest` |

---

## §11 Security Notes

`touches.security: true`: this change decides *which object* the two irreversible, no-recovery actions
(Purge, Permanent Delete — BRD §9, FR-17) act on.

- **Fail closed** (V3): a missing mapping row never falls back to bucket root for a delete.
- **Exact key match** preserved before any `DeleteFileVersionAsync` (V6), so a longer-named sibling under
  the same prefix is never deleted.
- **FR-03 unchanged:** no B2 key or token reaches the browser; the prefix is configuration, not a secret.
- **FR-23 unchanged:** the audit write still precedes the success result. Only the B2 name changes.
- **Logging:** no new log lines carrying paths. Existing hygiene tests (`LogHygieneTests`,
  `SyncTaskProcessorLogHygieneTests`, CAS-010 §12) must pass without modification.
- **Permanent Delete's per-user key (CAS-018):** the new key must stay inside what the user's application
  key is allowed to touch. If a user's B2 key is restricted to a name prefix, it must include
  `D/Shares/Operations/` (§14 Q4: keys are bucket-wide).

---

## §12 Rollback Plan

1. Revert the code change; the five call sites return to bare `relative_path`. No data is rewritten by
   this change, so reverting restores prior behavior.
2. Database rollback: none.
3. Config rollback: none. Setting `b2_path_prefix` to NULL on the mapping row restores bucket-root
   behavior without a redeploy.
4. Task-queue rollback: no change to `task_queue` payloads or shape. A `Queued` or `Running` task
   created before deployment resolves the prefix at run time, so it is processed under the new
   behavior. Deploy while the queue is idle.
5. **Objects already written by the old behavior stay where they are.** Production is not deployed, so
   this only affects test-bucket objects uploaded by earlier Copy/Update test runs (§14 Q2).

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `B2ObjectKeyTests` (Core/Api.Tests) | Compose and normalize cases: backslash, leading/trailing slash, empty/null prefix, leading slash on relative path | V1, V4, V5 |
| `B2PathPrefixProviderTests` (Api.Tests, NSubstitute/sproc fake) | row with prefix, row with NULL prefix, no row → throws | V3, V4, SC-7 |
| `B2PathPrefixProviderTests` (Api.IntegrationTests) | against dev DB row (`tc-test-Server` / `Operations`) | V3, V4 |
| `SyncCopyTaskProcessorTests` | assert `UploadFileAsync` receives prefixed key; `InsertAsync` receives relative path; no-mapping case | SC-1, SC-6, SC-7 |
| `SyncUpdateTaskProcessorTests` | same for Update | SC-2 |
| `SyncPurgeTaskProcessorTests` | `ListFileVersionsAsync`/`DeleteFileVersionAsync` receive prefixed key; root-level same-name object not matched | SC-3, V6 |
| `QuarantinePermanentDeleteServiceTests` | user-scoped client receives prefixed key; audit before success; no-mapping case | SC-4, V6, SC-7 |
| `KeywordSearchTaskProcessorTests` | `ListFileNamesAsync` receives prefixed key; equality check on prefixed key | SC-5 |
| Worker/Api log-hygiene suites | unchanged, must pass | SC-8 |
| Each processor test | provider invoked once for a multi-file batch | SC-9 |

Existing fixtures that construct the five classes need the new constructor parameter, with the provider
stubbed to `""` so the old assertions hold (SC-6).

Coverage target: ≥ 85% on changed files.

---

## §14 Open Questions

- [x] **Q1 — Audit `FilePath`.** Resolved 2026-09-30: keep `relative_path`. It matches search results,
  sync results, quarantine entries, `file_index` and the Post-Action Report, which prints the audit
  `FilePath`. The full key is derivable from the mapping row. The audit column (`NVARCHAR(1000)`) is the
  same size as `relative_path`, so the full key could not always fit. No change to audit values.
- [x] **Q2 — Objects already uploaded without the prefix.** Resolved 2026-09-30: production is not
  deployed (testing only), so there are no production orphans. The test bucket (`tc-test-Server`) may
  hold root-level objects from earlier Copy/Update test runs. Handling is a test-environment cleanup,
  outside this spec: query `dbo.audit_log` for successful `SyncCopyToB2` / `SyncUpdateInB2` entries
  (verify the stored action strings) and list the bucket top level. Note that `refresh_b2_index.py`
  marks catalog rows inactive (and deletes their working-copy files) when they are not under the prefix.
- [x] **Q3 — Keyword Search with no mapping row.** Resolved 2026-09-30: fail the task up front, before any
  B2 call, with a clear non-path-leaking message (V3). Treating candidates as "not on B2" would turn a
  configuration error into empty results and misleading `CrossCheckExcluded` audit rows. Copy, Update,
  Purge and Permanent Delete fail per file.
- [x] **Q4 — Scope of B2 application keys.** Resolved 2026-09-30 (recommendation accepted): keys are
  bucket-wide with no name-prefix restriction. If a key ever carries a name-prefix restriction, it must
  include `D/Shares/Operations/`. Verify during testing: a prefix-restricted key surfaces as a generic B2
  authorization failure on every prefixed call.
- [x] **Q5 — Prefix lookup keyed by configured bucket.** Resolved 2026-09-30: confirmed. The prefix comes
  from the mapping row for `B2:BucketName`, since `IB2Client` always acts on that bucket (single-bucket
  scope). Existing per-row-bucket tests keep exercising the local-root lookup; they only get the new
  prefix provider stubbed in. No rewrite.
- [x] **Q6 — Mapping row for production.** Values resolved 2026-09-30 (see §7). Still to do before
  go-live: someone inserts the row (`dbo.odrive_bucket_mapping_ins`) in the production database.
- [x] **Q7 — Store the full key in `relative_path` instead?** Rejected 2026-09-30. The production
  O-Drive copy (`D:\O-Drive\Operations`) has no `D\Shares` folders, so both sides' `relative_path` must
  stay prefix-free to match in Content Scan and to resolve local files.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | | | |
| Reviewer | | | |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches the code before the change
- [ ] Desired behavior (§2) is implemented at all five call sites and nowhere else
- [ ] Only files in §5 were modified (§6 files untouched)
- [ ] All §10 Success Criteria have passing tests
- [ ] All §9 Validations implemented and tested
- [ ] No endpoint or `task_queue` contract change (§8)
- [ ] §11 Security Notes implemented as described (fail closed, exact-key match, FR-23 ordering)
- [ ] §4 Blast Radius accurate; no unintended change to Content Scan, Reporting, Auth
- [ ] §12: in-flight task-queue rows handled

### Verification Report Format
Reviewer writes `CAS-032.review.md`:
- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5)
- Unintended blast radius
- Security gaps
