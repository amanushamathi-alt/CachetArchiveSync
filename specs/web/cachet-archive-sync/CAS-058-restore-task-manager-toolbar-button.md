---
id: CAS-058
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-10-07
priority: P2
change_type: bug-fix
related_specs: [CAS-030, CAS-033, CAS-048]
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

# CAS-058 — Change: Restore the Task Manager Toolbar Button (CSS only)

## §1 Current Behavior

- `TaskManagerBadge.razor` renders an enabled **Task Manager** button with the class
  `cachet-btn cachet-btn-square cachet-tm-trigger-btn`, a live count, and the hosted
  `TaskManagerModal`. `KeywordSearch.razor` (updated 2026-10-07) and `ODriveSyncContentScan.razor`
  both place `<TaskManagerBadge />` in the toolbar.
- `src/Cachet.ArchiveSync.Web/wwwroot/app.css:1087-1091` (last written 2026-10-05 21:08) contained:
  ```css
  /* Task Manager and Audit Log toolbar buttons hidden for all users (UI-only). To restore, delete this rule. */
  .cachet-tm-trigger-btn,
  .cachet-al-trigger-btn {
      display: none !important;
  }
  ```
  This hid the Task Manager **and** Audit Log buttons for every user on both pages, with no condition.
  **No spec records this rule.**
- `cachet-task-badge-attention` is unrelated: it only adds a red border and chip when a finished task
  has not yet been viewed (`TaskManagerBadge.razor.css`).
- **CAS-048** (absorbs CAS-033; review SC-9 PASS) requires the Task Manager button to be enabled,
  visible and able to open the modal. The CSS rule contradicts it.

## §2 Desired Behavior

- The **Task Manager** button is visible again on Keyword Search and O-Drive Sync, behaving as CAS-048
  specifies.
- The **Audit Log** button **stays hidden**. Its restoration was not confirmed and is out of scope.
- No markup, C# or test change.

## §3 Reason for Change

- **Type:** `bug-fix`.
- **Trigger:** A. Mathi, 2026-10-07. The manager confirmed that the Task Manager button must be
  visible. The team re-added the button to the page, but it stayed hidden because the global CSS rule
  remained.

## §4 Blast Radius

Mapped by manual grep:
- `.cachet-tm-trigger-btn` is used only by `TaskManagerBadge.razor`, which is rendered on
  `KeywordSearch.razor` and `ODriveSyncContentScan.razor`.
- `app.css` is global, but only the selector list of one rule changes. No other rule is affected.

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `src/Cachet.ArchiveSync.Web/wwwroot/app.css` | modify | Remove `.cachet-tm-trigger-btn` from the `display: none !important` rule; keep `.cachet-al-trigger-btn`; comment updated. |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `TaskManagerBadge.razor`, `TaskManagerModal.razor`, page `.razor` files | The button and modal already work (CAS-048). |
| Audit Log button / `AuditLogModal` | Its hidden state is unchanged (not confirmed). |

## §7 Database Changes

N/A.

## §8 API Contract Changes

N/A.

## §9 Validations Changed

N/A.

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | The Task Manager button is visible on Keyword Search and O-Drive Sync, sits immediately left of Quarantine Queue, and clicking it opens the Task Manager modal. | manual (§16) |
| SC-2 | The Audit Log button remains hidden. | manual (§16) |
| SC-3 | Existing `TaskManagerBadgeTests` / `MainLayoutTests` pass unmodified. | existing suites |

## §11 Security Notes

No security impact. This is CSS only. The Task Manager modal and its APIs keep their existing
`[Authorize]` and ownership rules (CAS-026/CAS-030).

## §12 Rollback Plan

Re-add `.cachet-tm-trigger-btn,` to the rule in `app.css` and redeploy Web.

## §13 Test Plan

The existing suites cover the component (bUnit does not evaluate CSS). The visibility check is
manual (§16).

## §14 Open Questions

- [ ] **Audit Log button:** should it also be restored? Who requested the original hide (with the
      Pre-/Post-Action Report buttons, 2026-10-05)? Record a change reference either way.
- [ ] CAS-048's review did not flag this CSS rule. Its owner may add a note.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | A. Mathi, via Claude Code | 2026-10-07 | Manager confirmed the Task Manager button must be visible |
| Reviewer | | | |

## §16 Verification Plan

- [ ] After a rebuild and Ctrl+F5 (the browser caches `app.css`), the Task Manager button is visible on both pages and opens the modal (SC-1)
- [ ] The Audit Log button is still hidden (SC-2)

## Implementation Log

**Completed:** 2026-10-07
**Gates:** Build ✓ (Web, Release, 0 errors) | Tests ✓ (TaskManagerBadge + MainLayout filter: 20/20) | Format — (CSS only) | Coverage — (CSS only) | Endpoints — (skip)

| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/wwwroot/app.css` | Removed `.cachet-tm-trigger-btn` from the hide rule; Audit Log is still hidden |
