---
id: CAS-059
target_app: cachet-archive-sync
status: review
owner: anushamathi@scientificvision.net
created: 2026-10-07
priority: P1
change_type: bug-fix
related_specs: [CAS-037, CAS-007, CAS-011]
touches:
  database: false
  api: false
  ui: false
  worker: true
  b2: true
  config: false
  security: true
migrations: []
---

# CAS-059 — Change: B2 Mirror Sync Checks the Mirror File and Stays Inside the Sync Folder

## §1 Current Behavior

`B2MirrorSyncTaskProcessor` (CAS-037), started by `POST /sync/mirror-sync`:
- Lists the **whole** configured bucket. It ignores `dbo.odrive_bucket_mapping.b2_path_prefix`
  (`D/Shares/Operations`), which the rest of the app uses.
- Skips an object when `dbo.file_index.sha1_hash` equals the object's `contentSha1` (§7 V3). It
  **never checks that the mirror file exists**.
- Downloads straight onto the final mirror path (`FileMode.Create`) without verifying the content.

**Observed on 2026-10-07 (dev, bucket `tc-test-Server`):**
- **Copy-created rows skipped (task 101397):** `SyncCopy` uploads to B2 and inserts the B2
  `file_index` row with the SHA-1, but writes no mirror file. Mirror Sync then skipped these files
  as "unchanged": 230 skipped. The copied files never reached `\\svsws10\B2mirror`, so the File
  Viewer could not open them from the B2 side.
  - Rerun task 101454: same result for the newly copied `Special Occasion` and `Stone Studded`
    folders.
- **Out-of-folder objects indexed:** the same run inserted 271 objects from outside the sync folder.
- **Incident:** Content Scan then listed those 271 objects as "Removed from O-Drive". Between
  12:14 and 12:21 UTC they were purged from B2 with `SyncPurge` tasks 101434, 101437, 101444 and
  101448. Purge deletes every version. The incident is recorded separately.

## §2 Desired Behavior

- **A. Disk check:** skip only when the SHA-1 matches **and** the mirror file exists with
  `size == contentLength`. Otherwise download, then update or insert the row as before.
- **B. Sync-folder scope:** process only objects whose name starts with `<b2_path_prefix>/`. An empty
  prefix means the bucket root, as `IB2PathPrefixProvider` defines it. With no active mapping
  (`null`), the task fails before listing, the same as V5.
  - Listing starts at the prefix and stops at the first name outside it. B2 returns names in sorted
    order, so prefixed names form one contiguous run.
- **C. Verified download:**
  - Write to `<path>.cachet-partial` while computing SHA-1.
  - Require the byte count to equal `contentLength`.
  - Require the SHA-1 to equal `contentSha1` when that is a real 40-hex value (`unverified:<hex>`
    accepted). `none` means size only.
  - Then replace the mirror file and set its last-write time to the upload time.
  - On any failure, the partial file is deleted, the existing mirror file is untouched and the
    object counts as failed (V4).
- **Content currency (code review, 2026-10-08):** the size check alone missed an Update in B2 to
  same-size content. The index then has the new SHA-1 while the mirror keeps the old bytes. A file
  now counts as current only when its size matches and either:
  - its last-write time equals the B2 upload time. Every Mirror Sync download sets this, so it is the
    cheap path; or
  - its content hashes to the B2 SHA-1. This covers files placed by `refresh_b2_index.py`, which
    stamps the source's last-modified time. The upload time is then adopted, so the next run takes
    the cheap path.

  With `contentSha1 = "none"`, a time mismatch means download. The first run after deployment
  hashes each script-placed mirror file once.
- **Folder markers:** a zero-byte object whose name ends in `/` (e.g. `D/Shares/Operations/`) is
  skipped like a non-upload object. Before, it failed on every run (`UnauthorizedAccessException`:
  the path is a directory), so `FailedCount` was always at least 1 and hid real failures.
- **Same name filters as the index-refresh script** (`scripts/index_refresh/common.py`), so Mirror
  Sync never indexes what `refresh_odrive_index.py` / `refresh_b2_index.py` would not:
  - **Excluded names** (`is_excluded`): `Thumbs.db`, `desktop.ini`, `.DS_Store`, `~$*`,
    `*.cachet-partial`, `.bzEmpty`, ignoring case. Ignored silently and not counted.
    - `.bzEmpty` matters: B2 creates it for folders made in its web UI. It never exists in the
      O-Drive index, so once indexed on the B2 side it would appear as "Removed from O-Drive" and be
      offered for Purge.
  - **Unusable names** (`validate_relative_path`): skipped with a warning log, not counted as
    failed:
    - an empty, `.` or `..` segment;
    - a Windows-invalid character (`<>:"|?*` or a control character), or a backslash `\`. The
      backslash is an ordinary character in B2 but a separator on Windows, so `a/b\c.txt` would
      share a mirror file with `a/b/c.txt`. This is stricter than the script.
  - Skip warnings log the object name with control characters replaced by `?`, so a CR/LF in a
    name cannot forge log lines.
    - `relative_path` longer than 1000, or longer than 1024 UTF-8 bytes;
    - `file_name` longer than 500;
    - an extension longer than 10.
  - **Case-only duplicates:** a later name differing only by case from an earlier one is skipped
    with a warning (cannot coexist on Windows).
- **Path containment:** the resolved mirror path must stay under the mirror root. Names with `.` or
  `..` segments or a `\` are already skipped by the unusable-name rule (not counted as failed).
  Containment stays as defense in depth; anything that still resolves outside the root is counted as
  failed and nothing is written.
- **Unchanged:** endpoint, request/response, task type, summary DTO, audit row, progress
  reporting, per-object failure isolation, and the V5 missing-root behavior.

## §3 Reason for Change

- **Type:** `bug-fix` (data integrity) and a safety fix.
- **Trigger:** A. Mathi, 2026-10-07. Copied files are not available in the B2 mirror; the 271
  out-of-folder objects led to permanent deletion. The user requested the permanent fix after the
  client proposal `Cachet_MirrorSync_Fix_Proposal_2026-10-07.txt`.

## §4 Blast Radius

Mapped by manual grep:
- `B2MirrorSyncTaskProcessor` is constructed only through Worker DI. No other code constructs it.
- `IB2PathPrefixProvider` / `B2PathPrefixProvider` already exist (Api, Files explorer). The class is
  reused unchanged and newly registered in the Worker.
- `POST /sync/mirror-sync` had no UI or script caller (Swagger only) when this change was written.
  CAS-060 (2026-10-08) adds a Web call after a completed Copy to B2, and CAS-061 (2026-10-08) after
  a completed Purge from B2, both in `ODriveSyncContentScan.razor`. That makes this fix a
  prerequisite for CAS-060 and CAS-061.

## §5 Files to Change

| File | Change Type | Description |
|------|------------|-------------|
| `src/Cachet.ArchiveSync.Worker/B2MirrorSyncTaskProcessor.cs` | modify | A, B, C and path containment; new constructor dependency `IB2PathPrefixProvider` |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | modify | `AddScoped<IB2PathPrefixProvider, B2PathPrefixProvider>()` |
| `src/Cachet.ArchiveSync.Worker.Tests/B2MirrorSyncTaskProcessorTests.cs` | modify | Fixture wires the prefix provider; SC-2 writes mirror files (new skip rule); CAS-059 tests for SC-1 – SC-16 (see §10) |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `SyncCopyTaskProcessor`, `SyncPurgeTaskProcessor`, `ContentScanTaskProcessor` | Out of scope; see §14 for the Purge safeguard |
| `SyncController`, `B2MirrorSyncService`, DTOs | No contract change |
| `B2PathPrefixProvider`, `IB2Client`, DB objects | Reused unchanged |

## §7 Database Changes

N/A. There are no schema or stored-procedure changes. The one-time clean-up proposed earlier is
withdrawn: the 271 rows are already `is_active = 0` after the purge.

## §8 API Contract Changes

N/A.

## §9 Validations Changed

| ID | Rule | Failure behavior |
|----|------|------------------|
| V3 (amended) | Skip only if the index SHA-1 matches AND the mirror file exists with the expected size AND (its last-write time = B2 upload time OR its content hashes to the B2 SHA-1) | Otherwise download |
| V6 (new) | Object name must be under `b2_path_prefix` | Not listed or processed |
| V7 (new) | No active mapping (prefix `null`) | Task `Failed` before listing |
| V8 (new) | Downloaded size/SHA-1 must match B2 | Object failed; mirror file untouched; no partial left |
| V9 (new) | Resolved path must stay under the mirror root (defense in depth: V12 already skips such names) | Object failed; nothing written |
| V10 (new) | Name ending in `/` (folder marker) | Skipped; not downloaded, indexed or counted |
| V11 (new) | Excluded file name (script's `EXCLUDED_NAME_PATTERNS`) | Ignored; not downloaded, indexed or counted |
| V12 (new) | Unusable name (script's `validate_relative_path` rules, plus `\`) | Skipped with a warning log (control characters replaced); not counted as failed |
| V13 (new) | Name differing only by case from an earlier name | Later one skipped with a warning log |

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A row with a matching SHA-1 but no mirror file is downloaded and counted as Updated | `Test_CAS059_ShaMatchButMirrorFileMissing_IsDownloaded` |
| SC-2 | A matching SHA-1 with a wrong-size mirror file is re-downloaded | `Test_CAS059_ShaMatchButMirrorFileSizeDiffers_IsDownloaded` |
| SC-3 | Only objects under the prefix are processed; paging continues while a page is fully inside the prefix and stops at the first outside name | `Test_CAS059_OnlyObjectsUnderPrefixProcessed`, `Test_CAS059_PageFullyInsidePrefix_NextPageFetched` |
| SC-4 | With no mapping, the task fails without listing | `Test_CAS059_NoPrefixMapping_FailsTask` |
| SC-5 | A SHA-1 mismatch keeps the old mirror file, leaves no partial file and does not update the row | `Test_CAS059_Sha1Mismatch_ExistingFileKeptAndNoPartialLeft` |
| SC-6 | A name escaping the root is rejected without a download or index row | `Test_CAS059_NameOutsideMirrorRoot_Rejected` |
| SC-7 | Matching SHA-1 and present, correct-size file are still skipped (no download) | `Test_SC2_UnchangedRerunSkipsAllDownloads` (amended) |
| SC-8 | All existing CAS-037 tests pass | existing suite |
| SC-9 | A folder-marker object is ignored and not counted as failed | `Test_CAS059_FolderMarkerObject_Ignored` |
| SC-10 | A sibling folder sharing the prefix text (`Operations2`) is excluded | `Test_CAS059_SiblingFolderWithSamePrefixText_Excluded` |
| SC-11 | A downloaded size that differs from B2 is rejected; nothing written, no partial left | `Test_CAS059_DownloadSizeMismatch_Rejected` |
| SC-12 | `contentSha1 = "none"` is accepted on size; `unverified:<hex>` is checked against the hex | `Test_CAS059_NoneOrUnverifiedSha1_Accepted`, `Test_CAS059_UnverifiedSha1Mismatch_Rejected` |
| SC-13 | Excluded names (incl. `.bzEmpty`, case-insensitive) are not downloaded, indexed or counted | `Test_CAS059_ExcludedNames_Ignored` (7 cases) |
| SC-14 | Unusable names (incl. `\` and control characters) are skipped with a warning, not counted as failed | `Test_CAS059_UnusableNames_SkippedNotFailed` (8 cases), `Test_CAS059_NameTooLong_Skipped` |
| SC-16 | A skipped name's control characters never reach the log (no CR/LF in any log line) | `Test_CAS059_SkippedNameWithControlChars_LoggedWithoutThem` |
| SC-17 | Same-size changed content (index already on the new SHA-1) is downloaded again and dated to the upload time | `Test_CAS059_SameSizeUpdatedContent_Redownloaded` |
| SC-18 | A correct mirror file with a different date is verified by hash, skipped, and dated to the upload time | `Test_CAS059_CurrentFileWithOtherDate_VerifiedByHashAndDateAdopted` |
| SC-19 | Path containment works for drive-root, folder and share roots, with or without a trailing separator; a sibling folder of the root is rejected | `Test_CAS059_ResolveInsideRoot_RootForms` (5 cases), `Test_CAS059_ResolveInsideRoot_SiblingOfRootRejected` |
| SC-15 | Of two names differing only by case, the later one is skipped | `Test_CAS059_NamesDifferingOnlyByCase_LaterOneSkipped` |

## §11 Security Notes

- **Path containment:** prevents writes outside the mirror root from a crafted object name.
- **Scope:** limiting to the sync folder stops unrelated bucket content from entering the index and
  the Content Scan "Removed" list. That list is where Purge, which is destructive, is offered.
- **No sensitive data in logs:** no new log lines with file content. Existing per-object warning
  logs keep only the relative path. No B2 key handling changes (SC-7 of CAS-037 still passes).

## §12 Rollback Plan

Redeploy the previous Worker build. There is no data or schema change to undo.

## §13 Test Plan

Unit tests in `B2MirrorSyncTaskProcessorTests` with NSubstitute fakes and a real temporary mirror
root (§10). Manual verification is in §16.

## §14 Open Questions

- [ ] **Purge safeguard (recommended, not in scope):** `SyncPurge` should refuse objects outside
      `b2_path_prefix`. It would have prevented the 07-Oct deletions. Needs approval, as a separate
      spec.
- [ ] Client approval of this change (proposal sent 2026-10-07). Do not check in or deploy before
      approval.
- [ ] `GET /tasks/{id}` returned `resultDetail: null` for task 101454 although the processor saves
      the summary. Investigate separately.
- [ ] The 3 objects that failed in tasks 101397 and 101402: cause not yet read from the Worker log.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | A. Mathi, via Claude Code | 2026-10-07 | Local change only; not checked in |
| Reviewer | | | |
| Client approval | | | |

## §16 Verification Plan

Local; the server Worker on SVSWS10 must be stopped, because only one Worker can hold the
instance lock.

- [x] `POST /sync/mirror-sync`, then `GET /tasks/{id}`: Completed (local Api + Worker, 2026-10-07, confirmed by A. Mathi)
- [x] Files in `Special Occasion` / `Stone Studded` and earlier copied files appear under `\\svsws10\B2mirror\D\Shares\Operations\...` (SC-1) (confirmed by A. Mathi, 2026-10-07)
- [ ] They open in the File Viewer from the B2 side
- [ ] No active B2 `file_index` rows outside `D/Shares/Operations/` after the run (SC-3)
- [ ] A second run skips them (SC-7)
- [ ] No `*.cachet-partial` files left in the mirror

## Implementation Log

**Completed:** 2026-10-07 (local, not checked in)
**Gates:** Build ✓ (Release) | Tests ✓ (Worker.Tests 194 passed / 1 skipped / 0 failed; B2MirrorSync 17/17) | Format ✓ (changed files) | Coverage — (not measured) | Endpoints — (no API change)
**Manual test:** 2026-10-07, local Api + Worker against the dev DB and `\\svsws10\B2mirror`: copied files downloaded to the mirror (reported working by A. Mathi). The remaining §16 items are not yet confirmed.

| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Worker/B2MirrorSyncTaskProcessor.cs` | Disk-check skip rule, prefix-scoped listing, verified temp-file download, path containment |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | Registered `IB2PathPrefixProvider` |
| `src/Cachet.ArchiveSync.Worker.Tests/B2MirrorSyncTaskProcessorTests.cs` | Fixture + SC-2 updated; 6 CAS-059 tests |

**Review follow-up (2026-10-08, after `CAS-059.review.md` APPROVED WITH NOTES):**
- Note 1 fixed: folder-marker objects (`…/`) are skipped (V10, SC-9).
- Note 3: tests added for the sibling prefix, size mismatch, `none` and `unverified:` (SC-10 – SC-12).
- Note 5: test class summary updated; the temp directory leak in `Test_ProcessAsync_UnhandledExceptionDuringEnumeration_MarksTaskFailed` fixed.
- §4: the Swagger-only statement updated for CAS-060.

**Gates:** Build ✓ (Release) | Tests ✓ (Worker.Tests 200 passed / 1 skipped / 0 failed) | Format ✓ (changed files)

**Second review follow-up (2026-10-08):** note 2 — added `Test_CAS059_PageFullyInsidePrefix_NextPageFetched` (SC-3 paging branch). Worker.Tests 201 passed / 1 skipped / 0 failed; format ✓.

**Alignment with the index-refresh script (2026-10-08, requested by A. Mathi):** after comparing
`refresh_b2_index.py`, the prefix rules already matched. Added the script's name filters:
- `IsExcludedName` (V11);
- `InvalidPathReason` (V12);
- the case-duplicate check (V13).

`Test_CAS059_NameOutsideMirrorRoot_Rejected` now expects a skip (V12), not a failure.
Still not in Mirror Sync, by design (the script remains the tool):
- deletion handling;
- the stale-catalog guard;
- adopting an existing local file;
- `modified_at` from `src_last_modified_millis` (Mirror Sync stores the upload time).

Worker.Tests 215 passed / 1 skipped / 0 failed; format ✓.

**Third review follow-up (2026-10-08, after the review of the filters, APPROVED WITH NOTES):**
- N1: `\` rejected by the unusable-name rule (V12).
- N2: skip warnings log the name with control characters replaced (SC-16).
- N3: §2, §4 (CAS-061 caller) and §5 text corrected.
- Not changed: N6 (rows indexed before this change are not deactivated by Mirror Sync). Check with
  a read-only query, and clean up with `refresh_b2_index.py --apply` if needed.

Worker.Tests 219 passed / 1 skipped / 0 failed; format ✓.

**Code-review follow-up (2026-10-08, `/code-review`, 2 findings):**
- `B2MirrorSyncTaskProcessor.cs` skip rule: a same-size changed file was treated as current. Fixed
  with `IsLocalCopyCurrentAsync` (time-or-hash check, V3 amended), SC-17 and SC-18.
- `ResolveInsideRoot`: a drive-root mirror root (`E:\`) produced the prefix `E:\\`, so every object
  failed. Fixed (no second separator) and made `internal` for direct tests (SC-19).

Worker.Tests 227 passed / 1 skipped / 0 failed; format ✓.
