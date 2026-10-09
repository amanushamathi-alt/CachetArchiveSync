---
id: CAS-063
target_app: cachet-archive-sync
status: review
owner: anushamathi@scientificvision.net
created: 2026-10-09
priority: P2
change_type: enhancement
related_specs: [CAS-007, CAS-037, CAS-059, CAS-060, CAS-061]
touches:
  database: false
  api: false
  ui: true
  worker: false
  b2: false
  config: false
  security: false
migrations: []
---

# CAS-063 — Change: "Update in B2" Starts B2 Mirror Sync When It Finishes

## §1 Current Behavior

- **Update in B2** (O-Drive Sync & Content Scan screen, "Modified files on O-Drive" panel):
  - `POST sync/actions/update`;
  - the Worker's `SyncUpdateTaskProcessor` (CAS-007) uploads the O-Drive file's current version to
    B2 and updates the B2 `file_index` row's `sha1_hash` / `file_size_bytes` / `modified_at`;
  - the screen polls the task (`PollActionAsync`) and reloads the Modified panel.
- **The local B2 mirror file is not touched**, so `\\svsws10\B2mirror` keeps the **old content**. The
  File Viewer (B2 side) and content search read the old version until someone runs B2 Mirror Sync
  from Swagger.
- CAS-060 (Copy) and CAS-061 (Purge) already call `POST sync/mirror-sync` when their task completes
  (`TriggerMirrorSyncAsync` in `ODriveSyncContentScan.razor`). Update was left out:
  - CAS-060 §14: "Update in B2: not in scope";
  - CAS-061 SC-5: `Test_CAS060_Update_DoesNotTriggerMirrorSync`.
- Mirror Sync can already detect updated content (CAS-059, V3 amended: time-or-hash check, SC-17),
  even when the new version has the same size. Only the automatic trigger is missing.

## §2 Desired Behavior

- **Unchanged:** Update in B2 itself — request, Worker processing, panel reload, toasts and errors.
- **New:** when the screen sees the Update task reach `Completed`, it calls `POST sync/mirror-sync`
  once, through the existing `TriggerMirrorSyncAsync`, with the same handling as CAS-060 §2 and
  CAS-061 §2:

| Response | Screen behavior |
|---|---|
| `202 Accepted` | Toast: "B2 mirror refresh started (Task #<id>)." |
| `409 Conflict` | Retry each poll interval up to `MirrorSyncMaxAttempts`, then: "Files updated. The B2 mirror will be refreshed by the next B2 Mirror Sync." |
| Other error / unreachable / timeout | "Files updated. The B2 mirror could not be refreshed automatically." The Update result is not affected. |
| `401` | Session-expired banner (CAS-030 V9) |

- **Not triggered:** when the Update task ends `Failed`, or when polling stops early (page closed,
  401).
- Applies equally when polling was resumed by `ResumePendingActionAsync` (page reopened during a
  running Update).
- **Messages per action:** the toast prefix is "Files updated." for Update. Copy keeps "Files
  copied." and Purge keeps "Files purged."
- **Result:** after Mirror Sync runs, the updated file's mirror copy is downloaded again. Its
  last-write time no longer equals the new B2 upload time and its content hash differs (CAS-059
  V3), so the B2 mirror holds the new version.

## §3 Reason for Change

- **Type:** `enhancement` (completes the CAS-060/CAS-061 pattern for the third sync action).
- **Trigger:** A. Mathi, 2026-10-09. Modified files updated in B2 are not refreshed in the B2 mirror.

## §4 Blast Radius

- One page component: `ODriveSyncContentScan.razor`. Only the trigger condition in `PollActionAsync`
  and the message prefix in `TriggerMirrorSyncAsync` change.
- Same endpoint, DTOs and options as CAS-060. No Api, Worker or database change.
- Mirror Sync scans the sync folder after every completed Update, as it already does after Copy and
  Purge (CAS-060 §14: accepted).

## §5 Files to Change

| File | Change Type | Description |
|------|------------|-------------|
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | modify | Trigger condition `SyncCopy` or `SyncPurge` → `SyncCopy`, `SyncUpdate` or `SyncPurge`; message prefix "Files updated." for `SyncUpdate`; comment updated |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncCopyMirrorSyncTests.cs` | modify | `Test_CAS060_Update_DoesNotTriggerMirrorSync` is replaced by the CAS-063 tests (§10); class summary updated |
| `specs/web/cachet-archive-sync/CAS-060-copy-to-b2-auto-mirror-sync.md` | modify (note) | SC-3 / §14 Update item: "superseded by CAS-063" |
| `specs/web/cachet-archive-sync/CAS-061-purge-from-b2-auto-mirror-sync.md` | modify (note) | SC-5 / §2 "Update in B2 does not trigger": "superseded by CAS-063" |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `SyncUpdateTaskProcessor` (Worker), `SyncController`, `SyncActionService` (Api) | Update itself is unchanged |
| `B2MirrorSyncTaskProcessor` | Already detects updated content (CAS-059) |
| Copy / Purge flows | Unchanged (CAS-060 / CAS-061 / CAS-062) |
| Database | No change |

## §7 Database Changes

N/A.

## §8 API Contract Changes

N/A. Uses the existing `POST /sync/mirror-sync` (`CachetAuthenticated`). Update already requires
`CachetFullAccess`.

## §9 Validations Changed

| ID | Rule | Failure behavior |
|----|------|------------------|
| V1 (CAS-060/CAS-061, amended) | Trigger only for `SyncCopy`, `SyncUpdate` or `SyncPurge`, and only when the task status is `Completed` | No call |

CAS-060 V2 and V3 (409 retry; failures never affect the action) apply unchanged.

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Update completes → existing Modified-panel reload happens, then exactly one mirror-sync call; toast shows the task ID | `Test_CAS063_UpdateCompleted_TriggersMirrorSync` |
| SC-2 | Update ends `Failed` → no mirror-sync call | `Test_CAS063_UpdateFailed_DoesNotTriggerMirrorSync` |
| SC-3 | Mirror-sync error after an Update → "Files updated. …could not be refreshed automatically."; no error banner | `Test_CAS063_UpdateMirrorSyncError_MessageSaysUpdated` |
| SC-4 | Resumed Update poll (page reopened) also triggers | `Test_CAS063_ResumedUpdate_TriggersMirrorSync` |
| SC-5 | Copy (CAS-060) and Purge (CAS-061) behavior and messages unchanged | existing `Test_CAS060_*` / `Test_CAS061_*` |
| SC-6 | All existing Web tests pass | existing suites |

## §11 Security Notes

- No new endpoint or permission. The call uses the user's own session client, and the submit is
  audited server-side (`B2MirrorSyncSubmitted`).
- Logs contain the action type, status code and task ID only. No file names.

## §12 Rollback Plan

Redeploy the previous Web build. No data or schema change.

## §13 Test Plan

bUnit tests in `ODriveSyncCopyMirrorSyncTests` (§10), same harness and fake handler as CAS-060 /
CAS-061. Manual verification is in §16.

## §14 Open Questions

- [ ] **Prerequisite:** CAS-059 (Worker) deployed first or together. Without its time-or-hash
      check, a same-size updated file is skipped by Mirror Sync.
- [ ] **Large files with no B2 SHA-1 (`contentSha1 = "none"`):** CAS-059 downloads them again
      whenever the file time differs. That is correct after an Update. Recorded for completeness;
      no action.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | A. Mathi, via Claude Code | 2026-10-09 | Draft; implement after confirmation |
| Client approval | | | |
| Reviewer | | | |

## §16 Verification Plan

- [ ] Change a test file on O-Drive (same size, if possible), run Content Scan, then **Update in B2** → toast "B2 mirror refresh started"; Task Manager shows a B2 Mirror Sync task
- [ ] After it completes, the file in `\\svsws10\B2mirror\D\Shares\Operations\...` has the new content and the File Viewer (B2 side) shows it
- [ ] Copy to B2 and Purge from B2 behave as before

## Implementation Log

**Completed:** 2026-10-09
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage not measured (Web .razor) | Endpoints ✓

### Files Changed
[src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor,src/Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncCopyMirrorSyncTests.cs,specs/web/cachet-archive-sync/CAS-060-copy-to-b2-auto-mirror-sync.md,specs/web/cachet-archive-sync/CAS-061-purge-from-b2-auto-mirror-sync.md]

### Tests Added
[Test_CAS063_UpdateCompleted_TriggersMirrorSync,Test_CAS063_UpdateFailed_DoesNotTriggerMirrorSync,Test_CAS063_UpdateMirrorSyncError_MessageSaysUpdated,Test_CAS063_ResumedUpdate_TriggersMirrorSync]

Test counts: Web.Tests 570 passed / 2 skipped / 0 failed (O-Drive Sync suites 137 passed / 1 skipped). `Test_CAS060_Update_DoesNotTriggerMirrorSync` removed (replaced by SC-1–SC-4). Backup of the pre-change files: `scratchpad\CAS-063-backup\` (see `RESTORE.txt`).
