---
id: CAS-033
target_app: cachet-archive-sync
status: draft
owner: devarajan@tracecounts.org
created: 2026-09-25
priority: P1
change_type: bug-fix
related_specs: [CAS-026, CAS-027, CAS-030, CAS-032]
superseded_by: CAS-048   # 2026-10-04: scope absorbed into CAS-048; do not implement separately
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

# CAS-033 — Change: Make the Task Manager Modal Reachable and Align It with CAS-030

Change to CAS-027 (WBS Task 25, Task Manager screen), required because CAS-030 changed the
Task Manager API and header layout after CAS-027 was implemented. Implements FR-19, FR-21, FR-22.

---

> **SUPERSEDED 2026-10-04 by CAS-048.** Its work (enable the button, host the modal in
> `TaskManagerBadge`, drop `mark-viewed`, `pageSize` ≤ 100, acknowledge on open, retarget CAS-027 SC-8)
> is implemented under CAS-048 §2.3, V10 and SC-14. Q1–Q3 below were answered there with the recommended
> options (acknowledge only on open; badge hosts the modal; one 100-row page). Status left as `draft`
> because the spec tooling has no "superseded" status. Do not run `/implement` on this file.

## §1 Current Behavior

Verified by a manual read on 2026-09-25:

- `Components/Layout/TaskManagerBadge.razor:10-13` renders the Task Manager button with
  `cachet-header-btn-disabled`, `disabled` and `title="Coming soon"`. This follows CAS-030 §8.1 and
  §15 Q3, which ship it disabled until WBS Task 25 turns it on.
- `Components/Shared/TaskManagerModal.razor` (CAS-027) exists, but **no component renders it**.
  CAS-027 hosted it in `MainLayout.razor`. CAS-030 later made `MainLayout.razor` static and moved
  the toolbar into each page, so the modal was left with no host. The Task Manager screen
  (FR-19/FR-21) **cannot be opened in the running app**.
- The modal still calls API behaviour that CAS-030 replaced:
  - `TaskManagerModal.razor:165` calls `POST /tasks/mark-viewed`. That endpoint no longer exists
    (it now returns 405). CAS-030 replaced it with per-task `POST /tasks/{taskId}/acknowledge`.
  - `TaskManagerModal.razor:188` requests `GET /tasks?pageSize=200`. CAS-030 V1 caps `pageSize`
    at 100, so the list load would get a 400 `validation-failed`.
- `Web.Tests/Layout/MainLayoutTests.cs` `Test_SC8_OpeningModalClearsUnviewedCountOnly` (CAS-027
  SC-8) is **skipped**, with its reason pointing to this spec.

## §2 Desired Behavior

- The Task Manager button in `TaskManagerBadge.razor` is enabled. It keeps its live badge and no
  longer has the "Coming soon" title.
- Clicking it opens `TaskManagerModal`, the same modal on both `KeywordSearch.razor` and
  `ODriveSyncContentScan.razor`.
- The modal lists the user's own tasks with a `pageSize` of 100 or less.
- The modal no longer calls `POST /tasks/mark-viewed`. How the badge clears follows CAS-030:
  terminal tasks are acknowledged per task. **§14 Q1** decides when.
- CAS-027 SC-8 is retargeted to the new host and un-skipped.

## §3 Reason for Change

- **Type:** bug-fix. FR-19/FR-21 cannot be reached in the running app, and the dormant modal calls
  a removed endpoint.
- **Trigger:** found during CAS-031's gate run on 2026-09-25. The user chose "skip and change spec".
- **Risk if not changed:** users have no way to see their task list or reach a failed task's Restart
  (CAS-016), and FR-22's badge links to nothing.

## §4 Blast Radius (Graph Impact)

The graph is out of date for `src/`, so this is a manual read.

- **Primary:** `TaskManagerBadge.razor`, `TaskManagerModal.razor`.
- **Hosts:** `KeywordSearch.razor` and `ODriveSyncContentScan.razor`. Both are shared with
  CAS-031 (Audit Log button and modal) and CAS-032 (report buttons and `ReportModal`). Those
  elements must be left exactly as they are.
- **Tests:** `Layout/TaskManagerBadgeTests.cs`, `TaskManager/TaskManagerModalTests.cs`,
  `Layout/MainLayoutTests.cs` (SC-8, which should probably move to `TaskManagerBadgeTests`).

## §5 Files to Change

TODO. Confirm once §14 is answered. Likely: `TaskManagerBadge.razor` (enable the button and host
or raise the open event), `TaskManagerModal.razor` (drop mark-viewed, set `pageSize` to 100 or less,
use acknowledge semantics), and the matching tests.

## §6 Files NOT to Change

`MainLayout.razor` stays static (CAS-030). Also out of bounds: every CAS-031 element (`AuditLogModal`,
the Audit Log button), every CAS-032 element (report buttons, `ReportModal`), and all Api, Core and
database code. CAS-030's API is used as it stands.

## §7 Database Changes

None.

## §8 API Contract Changes

None. This change consumes CAS-030's `GET /tasks`, `GET /tasks/summary` and
`POST /tasks/{taskId}/acknowledge` as they are.

## §9 Validations Changed

TODO (after §14).

## §10 Success Criteria

TODO (after §14). At minimum:
- The button is enabled and opens the modal on both pages.
- The modal loads with `pageSize` 100 or less.
- No request goes to `/tasks/mark-viewed`.
- The retargeted CAS-027 SC-8 passes.

## §11 Security Notes

No change to the security posture. Every endpoint used is own-scoped
(`CachetAuthenticated`, CAS-030 §12), and no B2 key path is involved.

## §12 Rollback Plan

Revert the Web files. The button goes back to disabled, as it is today.

## §13 Test Plan

TODO (after §14).

## §14 Open Questions

- [ ] **Q1: Badge clearing.** CAS-027 cleared the "unviewed" count when the modal opened
      (mark-viewed). CAS-030 acknowledges each task when its results are shown. Should opening the
      modal acknowledge every terminal task it lists, or only the tasks the user opens through View
      Results or View Details?
- [ ] **Q2: Modal host.** Should each page host `TaskManagerModal` next to its other modals, with the
      badge raising an event, or should `TaskManagerBadge` host the modal itself? Hosting it in the
      badge keeps both pages untouched, which avoids conflicts with CAS-031 and CAS-032.
- [ ] **Q3: Paging.** Is one page of up to 100 tasks enough (CAS-027 §4 had no pager), or is a pager
      needed now that the cap is 100 rather than 200?

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code, on behalf of R. Devarajan (TraceCounts) | 2026-09-25 | Draft, raised from the CAS-031 gate run |
| Reviewer | | | Pending |

## §16 Verification Plan

The reviewer checks that:
- the modal is reachable from both pages;
- no mark-viewed call remains;
- `pageSize` is 100 or less;
- CAS-027 SC-8 is un-skipped and passes;
- the CAS-031 and CAS-032 elements on both pages are unchanged;
- `MainLayout.razor` is unchanged.

Results go in `CAS-033.review.md`.
