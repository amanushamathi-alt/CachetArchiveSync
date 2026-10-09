---
id: CAS-060
target_app: cachet-archive-sync
status: review
owner: anushamathi@scientificvision.net
created: 2026-10-08
priority: P2
change_type: enhancement
related_specs: [CAS-007, CAS-037, CAS-045, CAS-059]
touches:
  database: false
  api: false
  ui: true
  worker: false
  b2: false
  config: true
  security: false
migrations: []
---

# CAS-060 — Change: "Copy to B2" Starts B2 Mirror Sync When It Finishes

## §1 Current Behavior

- **Copy to B2** (O-Drive Sync & Content Scan screen, "New on O-Drive" panel):
  - `SubmitSyncActionAsync` posts `sync/actions/copy`.
  - `PollActionAsync` polls `GET tasks/{id}` until the task is `Completed` or `Failed`, then reloads
    the New panel (`ODriveSyncContentScan.razor:1270-1399`).
  - `ResumePendingActionAsync` resumes polling when the page reopens during a running Copy.
- The Worker's `SyncCopyTaskProcessor` uploads to B2 and inserts the B2 `file_index` row. It
  **does not write the local B2 mirror** (`\\svsws10\B2mirror`).
- `POST /sync/mirror-sync` (CAS-037, fixed by CAS-059) fills the mirror, but **nothing in the app
  calls it**; it is Swagger-only. Until someone runs it, copied files cannot be opened from the B2
  side in the File Viewer.

## §2 Desired Behavior

- **Unchanged:** Copy to B2 itself — request, Worker processing, panel reload, toasts and errors.
- **New:** when the screen sees the Copy task reach `Completed`, it calls `POST sync/mirror-sync`
  once:

| Response | Screen behavior |
|---|---|
| `202 Accepted` | Toast: "B2 mirror refresh started (Task #<id>)." The task appears in the Task Manager as "B2 Mirror Sync". |
| `409 Conflict` (one already Queued/Running) | Retry every `PollInterval`, up to `Worker:MirrorSyncMaxAttempts` tries (default 30 = about 5 minutes at the default 10 s interval), so a sync that was already running does not miss the new files. Then stop with the message: "Files copied. The B2 mirror will be refreshed by the next B2 Mirror Sync." |
| Any other error / Api unreachable | Log a warning; non-blocking message: "Files copied. The B2 mirror could not be refreshed automatically." The Copy result is not affected. |
| `401` | Same session-expired handling as the existing poll (CAS-030 V9). |

- **Not triggered:** when the Copy task ends `Failed`, or when polling stops early (page closed, 401).
- Applies equally when polling was resumed by `ResumePendingActionAsync`.
- The screen does **not** wait for the Mirror Sync to finish. Progress is visible in the Task Manager.

## §3 Reason for Change

- **Type:** `enhancement`.
- **Trigger:** client request, 2026-10-08 (via A. Mathi). After Copy to B2 completes, call the B2
  Mirror Sync API, so copied files can be opened from the B2 side without a manual Swagger step.

## §4 Blast Radius

Mapped by reading code:
- One page component: `ODriveSyncContentScan.razor`, inside `PollActionAsync` only.
- Uses the existing endpoint and DTO (`TaskSubmittedResponse`); no Api, Worker or database change.
- Mirror Sync scans the whole sync folder (`D/Shares/Operations`) on every Copy completion. That
  takes about 4 s on the test bucket; production bucket size is unknown (see §14).

## §5 Files to Change

| File | Change Type | Description |
|------|------------|-------------|
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | modify | After a `SyncCopy` poll ends `Completed`, call a new private `TriggerMirrorSyncAsync` (`202`/`409` retry/error handling per §2) |
| `src/Cachet.ArchiveSync.Web/TaskPollingOptions.cs` | modify | New `MirrorSyncMaxAttempts` (default 30, range 1–360, bound from `Worker:`); no appsettings change needed |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/FakeODriveSyncApiHandler.cs` | modify | Fake `POST sync/mirror-sync` (configurable `202`/`409`/`500`) and count calls |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncCopyMirrorSyncTests.cs` | create | Tests for §10 (same harness as `ODriveSyncActionProgressTests`) |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `SyncController`, `B2MirrorSyncService` (Api) | Endpoint used as-is |
| `SyncCopyTaskProcessor`, `B2MirrorSyncTaskProcessor` (Worker) | Copy unchanged; Mirror Sync fixed separately in CAS-059 |
| Update in B2 / Purge from B2 flows | Out of scope (see §14) |
| Database | No change |

## §7 Database Changes

N/A.

## §8 API Contract Changes

N/A. The existing `POST /sync/mirror-sync` (`CachetAuthenticated`) is called. Copy already requires
`CachetFullAccess`, so every user who can Copy can also start Mirror Sync.

## §9 Validations Changed

| ID | Rule | Failure behavior |
|----|------|------------------|
| V1 | Trigger only for `SyncCopy` and only when the task status is `Completed`. *(Extended to `SyncPurge` by CAS-061 and to `SyncUpdate` by CAS-063.)* | No call |
| V2 | `409` → retry every `PollInterval`, at most `MirrorSyncMaxAttempts` tries | Then the informational message; no error banner |
| V3 | Any other failure never changes the Copy outcome or the panel reload | Warning log + non-blocking message |

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Copy completes → exactly one `POST sync/mirror-sync`; toast shows the mirror task ID | `Test_CAS060_CopyCompleted_TriggersMirrorSync` |
| SC-2 | Copy ends `Failed` → no mirror-sync call | `Test_CAS060_CopyFailed_DoesNotTriggerMirrorSync` |
| SC-3 | Update in B2 / Purge from B2 complete → no mirror-sync call. **Superseded for Purge by CAS-061** (2026-10-08) and **for Update by CAS-063** (2026-10-09): both now trigger | `Test_CAS060_UpdateAndPurge_DoNotTriggerMirrorSync` → renamed `Test_CAS060_Update_DoesNotTriggerMirrorSync` by CAS-061 → **removed by CAS-063** (replaced by `Test_CAS063_*`) |
| SC-4 | `409` then `202` → retried and accepted | `Test_CAS060_MirrorSyncConflict_RetriedUntilAccepted` |
| SC-5 | `409` for longer than the limit → stops with the informational message | `Test_CAS060_MirrorSyncConflict_GivesUpWithMessage` |
| SC-6 | `500` → New panel still reloads; non-blocking message shown | `Test_CAS060_MirrorSyncError_CopyResultUnaffected` |
| SC-7 | Resumed Copy poll (page reopened) also triggers on completion | `Test_CAS060_ResumedCopy_TriggersMirrorSync` |
| SC-8 | All existing O-Drive Sync Web tests pass | existing suites |
| SC-9 | `401` from mirror-sync → session-expired banner, no retry | `Test_CAS060_MirrorSyncUnauthorized_ShowsSessionExpired` |
| SC-10 | Api unreachable or HttpClient timeout → non-blocking "could not be refreshed" message, no error banner | `Test_CAS060_MirrorSyncApiUnreachable_ShowsNonBlockingMessage`, `Test_CAS060_MirrorSyncTimeout_ShowsNonBlockingMessage` |
| SC-11 | `202` with an unreadable body → still reported as started | `Test_CAS060_MirrorSyncAcceptedWithUnreadableBody_StillReportsStarted` |

## §11 Security Notes

- No new endpoint, permission or data path. The Web uses the same authenticated `CachetApi`
  `HttpClient` and the user's own session. The mirror task is submitted and audited under that user
  (`B2MirrorSyncSubmitted`).
- No file names or content in the new log lines (status code and task ID only).

## §12 Rollback Plan

Redeploy the previous Web build. No data or schema change.

## §13 Test Plan

bUnit tests with `FakeODriveSyncApiHandler` (§10), in the same harness as the existing
`ODriveSyncActionProgressTests`. Manual verification is in §16.

## §14 Open Questions

Plan confirmed by A. Mathi, 2026-10-08 ("confirm"): Copy only, behavior as in §2.

- [x] **Prerequisite:** CAS-059 (Worker fix) is deployed first or together with this change.
      Without it, the triggered Mirror Sync skips copied files and pulls out-of-folder objects.
- [x] **Update in B2:** not in scope here (Copy only). *(Added later by CAS-063, 2026-10-09. The
      same-size concern is resolved: CAS-059's time-or-hash check, V3 amended / SC-17, re-downloads
      an updated file even at the same size.)*
- [x] **Page closed before Copy finishes:** accepted. No automatic sync happens; the files arrive
      with the next Mirror Sync (manual or the next Copy).
- [x] **Production bucket size:** a full sync-folder scan after every Copy is accepted for now.
      Revisit with a Copy-only targeted refresh if it proves slow.
- [ ] **Purge safeguard** (separate spec, recommended): Purge should refuse files outside
      `D/Shares/Operations`.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | A. Mathi, via Claude Code | 2026-10-08 | Draft; implementation awaits confirmation |
| Client approval | | | |
| Reviewer | | | |

## §16 Verification Plan

- [ ] Copy 1–2 new O-Drive files → toast "B2 mirror refresh started"; Task Manager shows a B2 Mirror Sync task
- [ ] After it completes, the files exist under `\\svsws10\B2mirror\D\Shares\Operations\...` and open from the B2 side in the File Viewer
- [ ] Update in B2 and Purge from B2 behave exactly as before (no mirror-sync call). *(Superseded: Purge triggers since CAS-061, Update since CAS-063; verify per those specs' §16.)*
- [ ] Copy while a Mirror Sync is already running → retried; files still arrive

## Implementation Log

**Completed:** 2026-10-08 (local, not checked in)
**Gates:** Build ✓ (Release) | Tests ✓ (Web.Tests 559 passed / 2 skipped / 0 failed; CAS-060 7/7) | Format ✓ (changed .cs files; `.razor` not covered by `dotnet format`) | Coverage — (not measured) | Endpoints — (no API change)

| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | `PollActionAsync` records the terminal status; after a `SyncCopy` ends `Completed` it calls new `TriggerMirrorSyncAsync` (202 toast / 409 retry / error toast / 401 session-expired) |
| `src/Cachet.ArchiveSync.Web/TaskPollingOptions.cs` | `MirrorSyncMaxAttempts` (default 30) |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/FakeODriveSyncApiHandler.cs` | `OnPostMirrorSync` + `MirrorSyncCallCount` |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSync/ODriveSyncCopyMirrorSyncTests.cs` | SC-1 – SC-7 |

**Review follow-up (2026-10-08, after `CAS-060.review.md` APPROVED WITH NOTES):**
- N1: an HttpClient timeout (`TaskCanceledException` without the page's token cancelled) is now treated as "Api unreachable" (warning log + message), not as a page close.
- N2: an unreadable 2xx body is caught (`JsonException`) and logged; the toast still says the refresh started.
- N3: the poll's cancellation token is captured when the poll starts, so a page close during the panel reload no longer raises an unobserved `ObjectDisposedException`; the trigger is skipped when the poll was cancelled.
- Tests added: SC-9 (401), SC-10 (unreachable, timeout), SC-11 (unreadable body).
- Note: `src/Cachet.ArchiveSync.Web/Properties/launchSettings.json` shows a local modification not made by this change; exclude it from the check-in unless intended.

**Gates:** Build ✓ (Release) | Tests ✓ (Web.Tests 563 passed / 2 skipped / 0 failed; CAS-060 11/11) | Format ✓ (changed .cs files)
