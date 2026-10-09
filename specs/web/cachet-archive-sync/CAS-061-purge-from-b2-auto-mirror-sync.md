---
id: CAS-061
target_app: cachet-archive-sync
status: review
owner: anushamathi@scientificvision.net
created: 2026-10-08
priority: P3
change_type: enhancement
related_specs: [CAS-007, CAS-037, CAS-059, CAS-060]
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

# CAS-061 — Change: "Purge from B2" Starts B2 Mirror Sync When It Finishes

## §1 Current Behavior

- **Purge from B2** (O-Drive Sync & Content Scan screen, "Removed from O-Drive" panel):
  - confirmation dialog;
  - `POST sync/actions/purge`;
  - the Worker's `SyncPurgeTaskProcessor` deletes every B2 version and sets the B2 `file_index` row
    `is_active = 0`;
  - the screen polls the task and reloads the Removed panel.
- CAS-060 added a `POST sync/mirror-sync` call after a **completed Copy to B2** (`TriggerMirrorSyncAsync`
  in `ODriveSyncContentScan.razor`). Purge does not call it.

## §2 Desired Behavior

- **Unchanged:** Purge from B2 itself — confirmation, request, Worker processing, panel reload and
  messages.
- **New:** when the screen sees the Purge task reach `Completed`, it calls `POST sync/mirror-sync`
  once, with the same handling as CAS-060 §2:
  - `202` → toast "B2 mirror refresh started (Task #id)."
  - `409` → retry up to `MirrorSyncMaxAttempts`, then "Files purged. The B2 mirror will be refreshed
    by the next B2 Mirror Sync."
  - Other error, unreachable or timeout → "Files purged. The B2 mirror could not be refreshed
    automatically."
  - `401` → session-expired banner.
- **Not triggered:** when the Purge task ends `Failed`, or when polling stops early.
- **Unchanged elsewhere:** Copy keeps the CAS-060 behavior; its messages still start with "Files
  copied." Update in B2 still does not trigger Mirror Sync. *(Superseded by CAS-063, 2026-10-09:
  Update now triggers too.)*

**Known limitation (told to the client 2026-10-08):** Mirror Sync only downloads what exists in B2.
It never deletes local files, so **purged files stay in `\\svsws10\B2mirror`**. The call keeps the
mirror current with any other new B2 files only. Removing purged files from the mirror would need a
separate change (e.g. Purge also removing or archiving the local copy).
**Update 2026-10-08:** superseded for purged files by **CAS-062**. Purge itself now removes the
file's local B2 mirror copy after the B2 delete; this Mirror Sync call is unchanged.

## §3 Reason for Change

- **Type:** `enhancement`.
- **Trigger:** client request, 2026-10-08 (via A. Mathi): after the existing Purge functionality
  completes, call the B2 Mirror Sync API, without impact on the current Purge behavior. Plan
  confirmed by A. Mathi, 2026-10-08.

## §4 Blast Radius

- One page component: `ODriveSyncContentScan.razor`. The trigger condition in `PollActionAsync` is
  extended, and `TriggerMirrorSyncAsync` takes the action type to build its messages.
- Same endpoint, DTOs and options as CAS-060. No Api, Worker or database change.

## §5 Files to Change

| File | Change Type | Description |
|------|------------|-------------|
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | modify | Trigger also for `SyncPurge` + `Completed`; message prefix "Files purged." / "Files copied." by action; log line includes the action type |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncCopyMirrorSyncTests.cs` | modify | CAS-060 "Update and Purge do not trigger" narrowed to Update only; 4 CAS-061 tests |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `SyncPurgeTaskProcessor` (Worker), `SyncController` / `SyncActionService` (Api) | Purge itself is unchanged |
| `B2MirrorSyncTaskProcessor` | No deletion handling added (§2 limitation) |
| Purge confirmation dialog and panel markup | Unchanged |

## §7 Database Changes

N/A.

## §8 API Contract Changes

N/A. Uses the existing `POST /sync/mirror-sync` (`CachetAuthenticated`). Purge already requires
`CachetFullAccess`.

## §9 Validations Changed

| ID | Rule | Failure behavior |
|----|------|------------------|
| V1 (CAS-060, amended) | Trigger only for `SyncCopy` or `SyncPurge`, and only when the task status is `Completed` | No call |

CAS-060 V2 and V3 (409 retry; failures never affect the action) apply unchanged.

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Purge completes → existing panel reload happens, then exactly one mirror-sync call; toast shows the task ID | `Test_CAS061_PurgeCompleted_TriggersMirrorSync` |
| SC-2 | Purge ends `Failed` → no mirror-sync call | `Test_CAS061_PurgeFailed_DoesNotTriggerMirrorSync` |
| SC-3 | Mirror-sync error after a Purge → "Files purged. …could not be refreshed automatically."; no error banner | `Test_CAS061_PurgeMirrorSyncError_MessageSaysPurged` |
| SC-4 | Resumed Purge poll (page reopened) also triggers | `Test_CAS061_ResumedPurge_TriggersMirrorSync` |
| SC-5 | Update in B2 still does not trigger. **Superseded by CAS-063** (2026-10-09): this test was replaced by `Test_CAS063_*` | `Test_CAS060_Update_DoesNotTriggerMirrorSync` (removed by CAS-063) |
| SC-6 | All existing Web tests pass (incl. CAS-060 Copy tests and the existing Purge tests) | existing suites |

## §11 Security Notes

- No new endpoint or permission. The call uses the user's own session client, and the submit is
  audited server-side (`B2MirrorSyncSubmitted`).
- Logs contain the action type, status code and task ID only. No file names.
- Purge remains destructive and unchanged. The Purge safeguard (refuse objects outside
  `D/Shares/Operations`, CAS-059 §14) is still open and recommended.

## §12 Rollback Plan

Redeploy the previous Web build. No data or schema change.

## §13 Test Plan

bUnit tests in `ODriveSyncCopyMirrorSyncTests` (§10), same harness as CAS-060.

## §14 Open Questions

- [x] Goal confirmed with the client: call the API after Purge only. Removing purged files from the
      mirror is not requested (§2 limitation).
- [ ] Purge safeguard (separate spec), still recommended.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | A. Mathi, via Claude Code | 2026-10-08 | Local change only; not checked in |
| Client approval | | | |
| Reviewer | | | |

## §16 Verification Plan

- [ ] Purge 1 file (test data) → confirmation and result are as before; toast "B2 mirror refresh started"; Task Manager shows a B2 Mirror Sync task
- [ ] Copy to B2 still triggers as in CAS-060; Update in B2 does not
- [ ] Purge while a Mirror Sync is already running → retried

## Implementation Log

**Completed:** 2026-10-08 (local, not checked in)
**Gates:** Build ✓ (Release) | Tests ✓ (Web.Tests 567 passed / 2 skipped / 0 failed; O-Drive Sync suites 134 passed / 1 skipped) | Format ✓ (changed .cs file; `.razor` not covered by `dotnet format`) | Coverage — (not measured) | Endpoints — (no API change)

| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | Trigger for `SyncCopy` or `SyncPurge`; action-specific message prefix; action type in the log line |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncCopyMirrorSyncTests.cs` | CAS-060 SC-3 test narrowed to Update; `Test_CAS061_*` ×4 |
