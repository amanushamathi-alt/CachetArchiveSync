---
id: CAS-062
target_app: cachet-archive-sync
status: review
owner: anushamathi@scientificvision.net
created: 2026-10-08
priority: P2
change_type: bug-fix
related_specs: [CAS-001, CAS-007, CAS-008, CAS-013, CAS-014, CAS-031, CAS-037, CAS-059, CAS-061]
touches:
  database: false
  api: true
  ui: false
  worker: true
  b2: false
  config: false
  security: true
migrations: []
---

# CAS-062 — Change: Purge and Quarantine Permanent Delete Also Remove the Local B2 Mirror File

## §1 Current Behavior

Two user actions permanently delete a file from Backblaze B2. Neither removes the file's local copy
in the B2 mirror folder (`dbo.odrive_bucket_mapping.b2_mirror_root_path`, e.g.
`\\svsws10\B2mirror`).

| Action | Code | Today |
|---|---|---|
| **Purge from B2** (O-Drive Sync, "Removed from O-Drive") | `Worker/SyncPurgeTaskProcessor.cs` (CAS-007) | Deletes every B2 version → B2 `file_index` row `is_active = 0` → `sync_result` `Synced` → audit `SyncPurgeFromB2`. **No file-system access.** |
| **Quarantine Permanent Delete** | `Api/Services/QuarantinePermanentDeleteService.cs` (CAS-008/CAS-014/CAS-018) | Deletes every B2 version (user key) → the quarantined `file_index` row `is_active = 0` → `quarantine_entry` `Deleted` → audit `Deleted`. **No file-system access.** |

The local copy is removed only by running `scripts/index_refresh/refresh_b2_index.py --apply
--remove-untracked` by hand. After the action, the row is already inactive, so the script treats
the file as "untracked" and deletes it only with that flag (CAS-031).

**Specs vs. intent:**
- CAS-007 (implemented) does not mention the local copy. CAS-013 says Purge "talks to B2 over
  `IB2Client`, not the local B2 mirror".
- CAS-001 §6.2 step 6 assigns local-copy removal to "the explicit Copy/Purge/Update actions (FR-13)".
- CAS-001 §6.4 quotes BRD §9: "permanently deleted files are removed from **both the local cache
  and B2** with no retained copy".
- CAS-014 line 434 states that Purge leaves the file "gone from B2 and the local mirror". That is
  not true of the code.
- Mirror Sync (CAS-037/CAS-059) must never delete local files, and CAS-061 documents that purged
  files stay in the mirror.

**Effect:** purged or permanently deleted files stay in `B2mirror` until someone runs the script.
Until then they remain readable on the server's share, which contradicts BRD §9.

## §2 Desired Behavior

After a file's **B2 delete has succeeded**, the same action removes that file's local copy from the
**B2 mirror folder only**:

- **Path:** `b2_mirror_root_path` (from `dbo.odrive_bucket_mapping_get` for the configured
  `B2:BucketName`, source `B2`) + the B2 object name (`/` → `\`).
  - **Purge** uses `sync_result`'s B2 `relative_path`.
  - **Permanent Delete** uses `quarantine_entry`'s `Path`, the same key it just deleted from B2.
- **Never the O-Drive folder.** Even when the quarantined row is an O-Drive row, only the
  B2-mirror file with that key is removed. The O-Drive source file and its index row are untouched;
  that is the open Quarantine decision, `Cachet_Quarantine_Delete_Gap_2026-10-08.txt`.
- **Containment:** the resolved path must stay inside the mirror root. The check works for drive,
  folder and UNC roots, with or without a trailing separator, as in CAS-059 V9/SC-19. If the path
  falls outside the root, nothing is deleted and the case is treated as a local failure.
- **File already absent** (never mirrored, or removed earlier): not an error. Nothing to do, and
  no failure audit.
- **Deletion only, no holding folder** (BRD §9 "no retained copy"). Empty parent folders are left
  in place.
- **Order:** B2 delete first, then the local file. This is the existing order of both actions,
  kept unchanged; see §14 Q1 on CAS-001 §6.4.
  - If the B2 delete fails, the local file is **not** touched (same as today: "local state
    unchanged").

**Outcome when the local removal fails** (locked, access denied, outside root):

| Action | File result | Audit | Status / row |
|---|---|---|---|
| Purge | Stays **`Synced`** and is counted as succeeded, because the B2 purge is done and cannot be undone | Extra row `SyncActionFailedLocal`, `Failure`, detail "Purged from B2; local mirror copy not removed: <reason>" (Permanent Delete: "Deleted from B2; local mirror copy not removed: <reason>"). Reasons are fixed strings, e.g. "File is locked or access was denied." | B2 row stays `is_active = 0` |
| Permanent Delete | **Failed**, reason `local-delete-failed` (existing reason value) | `DeleteFailedLocal` (existing action), as for a `file_index` soft-delete failure today | `quarantine_entry` stays `Active`, so a retry repeats the steps safely: B2 delete is a no-op, soft-delete is idempotent, local delete runs again |

**Unchanged:**
- endpoints, request/response contracts and confirmation steps;
- the B2 delete logic, user-key authorization (CAS-018) and audit actions on success;
- Mirror Sync;
- the index-refresh scripts (they keep `--remove-untracked` for other untracked files).

## §3 Reason for Change

- **Type:** `bug-fix` (gap between the implemented specs and BRD §9 / CAS-001).
- **Trigger:** A. Mathi, 2026-10-08. After Purge the file stays in `B2mirror`, and the team runs
  `refresh_b2_index.py --apply --remove-untracked` by hand to remove it. Confirmed: delete (not
  move), and include Quarantine Permanent Delete.

## §4 Blast Radius

Mapped by grep:
- `SyncPurgeTaskProcessor`:
  - constructed by Worker DI and `SyncPurgeTaskProcessorTests`;
  - consumes `Removed` rows only;
  - the CAS-061 Web trigger is unaffected (it only calls Mirror Sync afterwards).
- `QuarantinePermanentDeleteService`:
  - constructed by Api DI and `QuarantinePermanentDeleteServiceTests`;
  - also exercised by `QuarantineControllerTests` (integration) through `POST /quarantine/delete`.
- `IBucketMirrorRootRepository` is already registered in both Api (`Program.cs:187`) and Worker
  (`Program.cs:91`).
- The local file deletion is new behavior in two places, so it gets one shared, small component
  (P4: the pattern now appears twice).
- `B2MirrorSyncTaskProcessor.ResolveInsideRoot` (CAS-059) is **not** refactored; there are no
  drive-by changes.

## §5 Files to Change

| File | Change Type | Description |
|------|------------|-------------|
| `src/Cachet.ArchiveSync.Core/Interfaces/IB2MirrorFileRemover.cs` | create | `Task<B2MirrorRemoveOutcome> RemoveAsync(string objectName, CancellationToken ct)`; outcome `Removed` / `NotPresent` / `Failed` (with a fixed, path-free reason) |
| `src/Cachet.ArchiveSync.Api/Storage/B2MirrorFileRemover.cs` | create | Resolves the root (`B2:BucketName` + `IBucketMirrorRootRepository`, source `B2`), checks containment, deletes the file if present; never throws for file-system errors (returns `Failed`) |
| `src/Cachet.ArchiveSync.Api/Program.cs` | modify | Register `IB2MirrorFileRemover` |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | modify | Register `IB2MirrorFileRemover` (the Worker already references the Api project) |
| `src/Cachet.ArchiveSync.Worker/SyncPurgeTaskProcessor.cs` | modify | After the B2 delete and row soft-delete: `RemoveAsync`; on `Failed`, the extra `SyncActionFailedLocal` audit and a warning log |
| `src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs` | modify | New step between the `file_index` soft-delete and the status transition: `RemoveAsync`; on `Failed`, `DeleteFailedLocal` + `local-delete-failed`, status stays `Active` |
| `src/Cachet.ArchiveSync.Api.Tests/B2MirrorFileRemoverTests.cs` | create | §10 SC-1 – SC-5 |
| `src/Cachet.ArchiveSync.Worker.Tests/SyncPurgeTaskProcessorTests.cs` | modify | Fixture gets the remover; §10 SC-6 – SC-9 |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantinePermanentDeleteServiceTests.cs` | modify | Fixture gets the remover; §10 SC-10 – SC-13 |
| `src/Cachet.ArchiveSync.Api.Tests/LogHygieneTests.cs`, `src/Cachet.ArchiveSync.Worker.Tests/SyncCopyTaskProcessorTests.cs`, `src/Cachet.ArchiveSync.Worker.Tests/SyncActionProcessorSecurityTests.cs` | modify (constructor only) | Added during implementation: they construct the two changed classes and need the new constructor argument (a remover returning `NotPresent`). No assertion changes. |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/Quarantine/QuarantineApiWebApplicationFactory.cs` | modify (test infra) | Added during implementation: the factory fakes every repository/B2 dependency, so it fakes `IB2MirrorFileRemover` too (default `NotPresent`) instead of reaching the real mapping/file system |
| `specs/web/cachet-archive-sync/CAS-061-purge-from-b2-auto-mirror-sync.md` | modify (note) | §2 limitation: "superseded by CAS-062 for purged files" |
| `specs/api/cachet-archive-sync/CAS-014-quarantine-hard-delete-and-remove-item.md` | modify (note) | Line 434: add "(local mirror file removal implemented by CAS-062)" |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `B2MirrorSyncTaskProcessor.cs` | Mirror Sync must never delete (CAS-037/CAS-059) |
| `SyncCopyTaskProcessor`, `SyncUpdateTaskProcessor`, `ContentScanTaskProcessor` | Not delete actions |
| Controllers, DTOs, Web pages | No contract or UI change |
| `scripts/index_refresh/*` | Still the tool for untracked or bulk clean-up |
| O-Drive folder, O-Drive index rows | Out of scope (open Quarantine decision) |
| Database objects | No change |

## §7 Database Changes

N/A.

## §8 API Contract Changes

N/A. `POST /sync/actions/purge` and `POST /quarantine/delete` keep their request, response and
status codes. Permanent Delete may now return the **existing** per-file reason
`local-delete-failed` for one more cause (the local file could not be removed).

## §9 Validations Changed

| ID | Rule | Failure behavior |
|----|------|------------------|
| V1 | Local removal runs only after the B2 delete succeeded | B2 failure → local file untouched |
| V2 | Target path = B2 mirror root + object name, and must resolve inside the root | Outside → `Failed`, nothing deleted |
| V3 | Only the B2 mirror root is ever used (source `B2`), never the O-Drive root | — |
| V4 | Missing local file | `NotPresent`, treated as success; no failure audit |
| V5 | Local removal failure, including an unexpected error from the remover, never reverses the B2 delete or aborts the batch | Purge: `Synced` + `SyncActionFailedLocal` (if that audit write fails, it is only logged). Permanent Delete: `local-delete-failed` + `DeleteFailedLocal` for that file only, entry stays `Active` |
| V6 | Unsafe object name, which Windows could resolve or fold onto another object's mirror file. Covers: a `\`, control or `<>:"\|?*` character; an empty, `.` or `..` segment; a segment ending in `.` or space. Mirror Sync's CAS-059 V12 rules, plus the trailing `.`/space rule. | Remover returns `Failed`, nothing deleted. Containment (V2) remains as defense in depth. |

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Remover deletes an existing file at mirror root + key and returns `Removed` | `Test_CAS062_Remover_ExistingFile_Removed` |
| SC-2 | Remover returns `NotPresent` for a missing file | `Test_CAS062_Remover_MissingFile_NotPresent` |
| SC-3 | Remover refuses a key that resolves outside the root (`..`, rooted) and deletes nothing | `Test_CAS062_Remover_OutsideRoot_FailedNothingDeleted` |
| SC-4 | Remover returns `Failed` (no throw) when the file cannot be deleted (locked / read-only) | `Test_CAS062_Remover_LockedFile_Failed` |
| SC-5 | Remover resolves the root with source `B2` for the configured bucket (never `ODrive`) | `Test_CAS062_Remover_UsesB2MirrorRootOnly` |
| SC-6 | Purge success removes the local mirror file; result `Synced`, `SyncPurgeFromB2` audited | `Test_CAS062_Purge_RemovesLocalMirrorFile` |
| SC-7 | Purge with B2 delete failure does not call the remover | `Test_CAS062_Purge_B2Failure_LocalUntouched` |
| SC-8 | Purge with remover `Failed` stays `Synced`/succeeded and adds a `SyncActionFailedLocal` audit | `Test_CAS062_Purge_LocalRemovalFailed_AuditedNotReversed` |
| SC-9 | Purge with remover `NotPresent` adds no failure audit | `Test_CAS062_Purge_LocalFileAbsent_NoFailureAudit` |
| SC-10 | Permanent Delete success removes the local mirror file before the status becomes `Deleted` | `Test_CAS062_PermanentDelete_RemovesLocalMirrorFile` |
| SC-11 | Permanent Delete with B2 failure does not call the remover | `Test_CAS062_PermanentDelete_B2Failure_LocalUntouched` |
| SC-12 | Permanent Delete with remover `Failed` returns `local-delete-failed`, audits `DeleteFailedLocal`, does not transition the status | `Test_CAS062_PermanentDelete_LocalRemovalFailed_EntryStaysActive` |
| SC-13 | Permanent Delete of an entry whose row is an O-Drive row removes only the B2 mirror file key | `Test_CAS062_PermanentDelete_ODriveRow_RemovesB2MirrorKeyOnly` |
| SC-14 | All existing Worker and Api tests pass | existing suites |
| SC-15 | Purge stays `Synced` and is never marked failed when the remover throws, or when the local-failure audit write throws | `Test_CAS062_Purge_RemoverThrows_StaysSyncedNotReversed`, `Test_CAS062_Purge_LocalFailureAuditThrows_StaysSynced` |
| SC-16 | Permanent Delete: a remover exception fails only that file (`local-delete-failed`); the rest of the batch completes | `Test_CAS062_PermanentDelete_RemoverThrows_FileFailsBatchContinues` |
| SC-17 | Remover refuses a read-only file, a sibling folder of the root, a `\` or control-character name, and a junction inside the root; accepts a drive-root mapping | `Test_CAS062_Remover_ReadOnlyFile_Failed`, `Test_CAS062_Remover_SiblingFolderOfRoot_FailedNothingDeleted`, `Test_CAS062_Remover_BackslashOrControlCharName_Failed`, `Test_CAS062_Remover_JunctionInsideRoot_FailedTargetKept`, `Test_CAS062_Remover_DriveRoot_ContainmentAccepted` |
| SC-18 | Names that resolve or fold onto another object's file (`./`, `//`, `x/..`, `::$DATA`, trailing `.`/space, `<>?\|"*`) are refused, and the other file is kept | `Test_CAS062_Remover_UnsafeNameFoldingOntoOtherFile_FailedOtherKept` (12 cases) |

## §11 Security Notes

- **Destructive file-system action.** It is gated by the existing confirmations (`confirm: true`,
  `CachetFullAccess`, and the CAS-018 live user-key check for Permanent Delete). It only runs after
  B2 has already been irreversibly deleted.
- **Path safety:**
  - the root comes from the database mapping, never from user input;
  - the object name comes from the existing row;
  - containment (V2) prevents deleting outside the mirror root;
  - the O-Drive root is never resolved (V3).
- **Data minimization:** implements BRD §9 "no retained copy" for these two actions.
- **Logs and audit:** the existing `FilePath` (object name) only. No full local path or file content
  in logs. Failure reasons are fixed strings.

## §12 Rollback Plan

Redeploy the previous Api and Worker builds. There is no schema change. Files already removed are
not restored (by design, BRD §9).

## §13 Test Plan

- `B2MirrorFileRemoverTests`: real temporary directory as the mirror root, with NSubstitute
  `IBucketMirrorRootRepository` and in-memory configuration.
- `SyncPurgeTaskProcessorTests` and `QuarantinePermanentDeleteServiceTests`: NSubstitute
  `IB2MirrorFileRemover`.
- Manual checks in §16.

## §14 Open Questions

- [x] **Q1 – resolved 2026-10-08 (A. Mathi, via `/implement CAS-062`): B2 first, then local.**
  **Order vs CAS-001 §6.4.** CAS-001 designed "local cache delete, then B2 delete". The
      implemented Purge and Permanent Delete both delete B2 first, and CAS-008 V5 relies on "B2
      failure leaves local state unchanged". **Recommendation: keep B2 first, then local** (this
      spec).
      - Deleting the local copy first would buy nothing: Mirror Sync, now called after Purge by
        CAS-061, would download it again if the B2 delete then failed.
      - It would also change CAS-008 V5 behavior.

      Confirm, or ask for local-first.
- [x] Delete, not move to a holding folder (confirmed 2026-10-08; BRD §9).
- [x] Include Quarantine Permanent Delete (confirmed 2026-10-08).
- [ ] **Not in scope:** O-Drive side of Quarantine delete (pending client decision A/B/C); the Purge
      safeguard for objects outside `D/Shares/Operations` (separate spec, still recommended).
- [ ] **Existing leftovers:** files already purged before this change stay in `B2mirror`. Remove
      them once with `refresh_b2_index.py --apply --remove-untracked` (dry run first).

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | A. Mathi, via Claude Code | 2026-10-08 | Draft; implement after confirmation |
| Client approval | | | |
| Reviewer | | | |

## §16 Verification Plan

- [ ] Purge one test file → gone from Backblaze **and** from `\\svsws10\B2mirror\D\Shares\Operations\...`; O-Drive file untouched
- [ ] Quarantine Permanent Delete one test file → gone from Backblaze and `B2mirror`; O-Drive untouched
- [ ] Purge a file whose local copy is open/locked → Purge result Synced; audit shows `SyncActionFailedLocal`
- [ ] `refresh_b2_index.py` dry run afterwards → no untracked file reported for the purged path

## Implementation Log

**Completed:** 2026-10-08
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 87-96% (changed files) | Endpoints ✓

### Files Changed
[src/Cachet.ArchiveSync.Core/Interfaces/IB2MirrorFileRemover.cs,src/Cachet.ArchiveSync.Api/Storage/B2MirrorFileRemover.cs,src/Cachet.ArchiveSync.Api/Program.cs,src/Cachet.ArchiveSync.Worker/Program.cs,src/Cachet.ArchiveSync.Worker/SyncPurgeTaskProcessor.cs,src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs,src/Cachet.ArchiveSync.Api.Tests/B2MirrorFileRemoverTests.cs,src/Cachet.ArchiveSync.Api.Tests/QuarantinePermanentDeleteServiceTests.cs,src/Cachet.ArchiveSync.Worker.Tests/SyncPurgeTaskProcessorTests.cs,src/Cachet.ArchiveSync.Api.Tests/LogHygieneTests.cs,src/Cachet.ArchiveSync.Worker.Tests/SyncCopyTaskProcessorTests.cs,src/Cachet.ArchiveSync.Worker.Tests/SyncActionProcessorSecurityTests.cs,src/Cachet.ArchiveSync.Api.IntegrationTests/Quarantine/QuarantineApiWebApplicationFactory.cs]

### Tests Added
[Test_CAS062_Remover_ExistingFile_Removed,Test_CAS062_Remover_MissingFile_NotPresent,Test_CAS062_Remover_OutsideRoot_FailedNothingDeleted,Test_CAS062_Remover_RootedName_FailedNothingDeleted,Test_CAS062_Remover_LockedFile_Failed,Test_CAS062_Remover_UsesB2MirrorRootOnly,Test_CAS062_Remover_NoMapping_Failed,Test_CAS062_Remover_NoBucketConfigured_Failed,Test_CAS062_Purge_RemovesLocalMirrorFile,Test_CAS062_Purge_B2Failure_LocalUntouched,Test_CAS062_Purge_LocalRemovalFailed_AuditedNotReversed,Test_CAS062_Purge_LocalFileAbsent_NoFailureAudit,Test_CAS062_PermanentDelete_RemovesLocalMirrorFile,Test_CAS062_PermanentDelete_B2Failure_LocalUntouched,Test_CAS062_PermanentDelete_LocalRemovalFailed_EntryStaysActive,Test_CAS062_PermanentDelete_ODriveRow_RemovesB2MirrorKeyOnly]

**Review follow-up (2026-10-08, after `CAS-062.review.md` APPROVED WITH NOTES):**
- F-1: Purge's local step is a separate method that handles every non-cancellation error, including a failing `SyncActionFailedLocal` audit write. The file stays `Synced`, and the per-file catch (`SyncActionFailedB2`) is never reached after a successful purge (SC-15).
- F-2: Permanent Delete wraps the remover call. An unexpected error becomes `local-delete-failed` for that file, and the batch continues (SC-16).
- F-3: the remover rejects `\` and control characters in object names (V6).
- Tests added for a read-only file, a sibling folder, a drive root and a junction (SC-17; the junction test creates a real junction with `mklink /J`).
- Audit text de-duplicated:
  - "Purged from B2; local mirror copy not removed: <reason>" / "Deleted from B2; …";
  - the I/O reason is now "File is locked or access was denied."
- Not changed: F-4 (time-of-check gap between the junction check and the delete; needs write access to the mirror share; same pattern as `MirrorFileReader`).

**Gates:** Build ✓ (Release, 0 errors) | Tests ✓ (Api 749 passed; Worker 233 passed / 1 skipped; Api.IntegrationTests `QuarantineControllerTests` 28/28) | Format ✓ (after auto-fix of one whitespace issue) | Endpoints ✓ (no change)

**Second review follow-up (2026-10-08, re-review APPROVED WITH NOTES):**
- S-1 (Medium) fixed: `B2MirrorFileRemover.IsSafeObjectName` refuses every name Windows would fold onto another object's mirror file, i.e. the CAS-059 V12 rules plus trailing `.`/space (V6, SC-18).
- The junction test now fails visibly when it cannot create a junction, instead of passing without checking.
- Spec §2 audit wording now matches the code.
- Not changed:
  - S-2 (case-only duplicate names share one Windows file; low);
  - S-3/F-4 (time gap between the reparse check and the delete; low);
  - N-3/N-4 (pre-existing CAS-007/CAS-008 audit-failure behavior).

**Gates:** Build ✓ | Tests ✓ (Api 761 passed; Worker 233 passed / 1 skipped; Api.IntegrationTests `QuarantineControllerTests` 28/28) | Format ✓
