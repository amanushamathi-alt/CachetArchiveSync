---
id: CAS-052
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-05
priority: P1
change_type: enhancement
related_specs: [CAS-050, CAS-051, CAS-024, CAS-022, CAS-040]
touches:
  database: false
  api: false
  ui: true
  worker: false
  b2: false
  config: false
  security: true
migrations: []
---

# CAS-052 — Change: Quarantine pop-over and panel — pagination and Files-added items (Web)

> Part 3 of 3 of the Files-quarantine change request (Product Owner change, BRD v2.4 §12.5). Depends on
> CAS-050. BRD: FR-14 (amended: paginated queue, source column), FR-09a, FR-16/FR-17 (permanent delete UI
> safeguards, unchanged). Quarantine is one row per file, so there are no folder rows in the queue.
> (File name keeps the original "folders" slug for traceability.)

## §1 Current Behavior

- `QuarantineQueueModal.razor` (371 lines) loads `GET /quarantine` once, renders every row, with a
  header "select all" over **all** loaded rows, per-row checkboxes, and a "Delete Permanently (n)"
  button (disabled unless Full Access). The Source Tab cell is hard-coded `Keyword Search` (line 68).
  Name click opens the file viewer by `FileIndexId`. No Remove-Item control exists in this modal.
- `KeywordSearch.razor:333-385` embeds a read-only Quarantine panel from `LoadQuarantineAsync`
  (`GET /quarantine`, line 995), unpaged; its header count and the toolbar badge (line 215) use
  `quarantineItems.Count`; Source Tab is hard-coded `Keyword Search`.
- Empty-state text says files "sent to quarantine from Keyword Search".

## §2 Desired Behavior

1. **Pagination in the modal** (explicit requirement): server-side paging via CAS-050 `GET /quarantine`,
   page-size selector **25 / 50** (default 25), Previous / Next, "Page x of y" and range text
   "1–25 of 137". Page size and page index reset to defaults each time the modal opens.
2. **Same pagination in the Keyword Search Quarantine panel** (Q7), with its own independent page state.
   A single shared component `QuarantinePager.razor` renders the controls — justified by P4 (second real
   use). Panel header count and toolbar badge show **`totalCount`**, not the page size.
3. Files quarantined from the Files explorer appear in both places as ordinary file rows (a folder
   action simply adds many file rows). Source Tab renders `sourceTab` from the API ("Files" or
   "Keyword Search") instead of the constant. Status chip for Files-added entries: `Informational`. Name
   click keeps opening the file viewer by `FileIndexId`.
4. **Selection / delete safety (irreversible action):** the header checkbox selects **the current page
   only** (label "Select all on this page"); selection persists across page changes; the button label and
   confirm text show the **total selected count**; selection is cleared on page-size change; after a
   delete, the list reloads and the page is clamped to the last valid page.
   - The confirm text also states how many selected items are **not on the current page**, e.g.
     "12 selected (7 on other pages)", so the user sees that the action reaches beyond the visible rows.
   - A visible **"Clear selection"** control (shown when ≥ 1 item is selected) empties the selection.
   - **Stale selection (V6):** after any reload (post-delete, modal reopen, page change), selected IDs that
     were deleted by this session, or that are on the current page and absent from the reloaded rows, are
     dropped from the selection before the count is shown. Selected IDs on other pages cannot be checked
     client-side; the existing delete endpoint's handling of already-removed IDs is unchanged and is
     confirmed in review (§16).
   - **Empty queue:** when `totalCount` is 0 (including after deleting the last rows), show page 1 of 1 and
     the empty-state copy — never "Page 1 of 0".
   - **Sort order:** paging relies on the API's deterministic order (`created_at ASC, quarantine_entry_id
     ASC`, `dbo.quarantine_entry_lst`); the Web does not re-sort client-side.
   - **Accessibility:** pager buttons have accessible names (Previous page / Next page); the header
     checkbox's accessible label states its scope ("Select all on this page").
5. **Permanent Delete is unchanged:** Files-added rows are normal file rows and are selectable and
   deletable through the existing flow and confirmation (FR-16/FR-17). Because a folder action can add many
   rows at once, the page-scoped select-all (item 4) and the total-count confirm text are the safeguards.
6. **No filter box** in the modal (per requirement). Empty-state copy updated to "Files sent to quarantine
   from Keyword Search or the Files browser appear here…".
7. Read Only users see both views read-only as today (delete disabled).
8. **Panel refresh (Q5):** the Keyword Search Quarantine panel reloads when the modal closes (via the
   existing close callback), so deletes made in the modal are reflected without a manual refresh. Items
   added from the Files browser appear on the next panel load or modal open.

## §3 Reason for Change

- **Type:** enhancement
- **Trigger:** change request (2026-10-05): pagination in the pop-over; quarantined items visible in the
  pop-over and in the Keyword Search quarantine panel.
- **Risk if not changed:** large queues (bulk folder actions) make the modal unusable; Files-added items
  would be mislabelled "Keyword Search".

## §4 Blast Radius (Graph Impact)

*Mapped by manual grep/read.*

### Primary Change Target
`Components/Shared/QuarantineQueueModal.razor` and the panel block + `LoadQuarantineAsync` in
`Components/Pages/KeywordSearch.razor`.

### Direct Dependents
`ODriveSyncContentScan.razor` — **in scope** (CAS-050 review): `LoadQuarantineCountAsync` (~line 959) calls
`GET quarantine` and sets its toolbar badge from `body.Items.Count` (~line 980), which under paging would
cap at 25; it must read `TotalCount`. It also hosts `QuarantineQueueModal`, `QuarantineQueueModalTests`, `QuarantineQueueFlowIntegrationTests`, `KeywordSearchPageTests`,
`FakeKeywordSearchApiHandler`, `FakeCachetApiHandler`, `TaskManagerBadge`/`MainLayout` only if they read
the quarantine count (grep before implementing).

### Downstream Modules
Quarantine, Keyword Search, O-Drive Sync and Content Scan (shared modal).

### Cross-Project Risk
**CROSS-PROJECT RISK:** consumes the changed `GET /quarantine` contract and Core DTO changes (CAS-050
§8). Ship Web + Api together. Dependency satisfied: CAS-050 review verified `totalCount` and `sourceTab`
in the list contract, and `dbo.quarantine_entry_lst` orders by `created_at ASC, quarantine_entry_id ASC`
with OFFSET/FETCH.

### God Node Involvement
`KeywordSearch.razor` (1,400 lines) is a god-sized page → **HIGH RISK**; edits limited to the quarantine
panel block, `LoadQuarantineAsync`, the badge expression and one callback wiring.

### Graphify Queries Run
graphify not queried at node level — manual code read.

## §5 Files to Change

| File | Change Type | Description |
|------|------------|-------------|
| `src/Cachet.ArchiveSync.Web/Components/Shared/QuarantineQueueModal.razor` (+`.css`) | modify | Paging state, pager, page-scoped select-all, source tab |
| `src/Cachet.ArchiveSync.Web/Components/Shared/QuarantinePager.razor` (+`.css`) | add | Shared pager (page, page size 25/50, range text) |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` (+`.css`) | modify | Panel paging, `totalCount` in header and badge, source-tab rendering |
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | modify | `LoadQuarantineCountAsync`: badge uses `TotalCount` (not `Items.Count`) — one-line change; request stays `GET quarantine` (first page is enough for a count) |
| `src/Cachet.ArchiveSync.Web.Tests/Quarantine/*`, `KeywordSearch/*`, `ODriveSync/*`, fakes | modify/add | Tests per §13 |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `FileExplorer.razor` | CAS-051 |
| `FileViewerModal.razor` | Viewer unchanged (every queue row is a file) |
| `QuarantinePermanentDeleteService.cs`, delete endpoint | Delete semantics unchanged |

## §7 Database Changes
N/A.

## §8 API Contract Changes
N/A (consumes CAS-050 §8).

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | all rows loaded | page size ∈ {25, 50}; page ≥ 1 and ≤ last page | `Test_V1_PageBounds` |
| V2 | select-all = all rows | select-all = current page only | `Test_V2_SelectAll_CurrentPageOnly` |
| V3 | hard-coded "Keyword Search" | Source Tab renders `sourceTab` from the API | `Test_V3_SourceTab_FromApi` |
| V4 | count = list length | header/badge count = `totalCount` | `Test_V4_Count_IsTotal` |
| V5 | n/a | selection survives page change; cleared on page-size change and modal reopen | `Test_V5_SelectionLifecycle` |
| V6 | n/a | after reload, selected IDs deleted this session or absent from the current page's reloaded rows are pruned before the count is shown; empty queue renders page 1 of 1 | `Test_V6_StaleSelection_Pruned`, `Test_V6_EmptyQueue_PageOneOfOne` |

## §10 Success Criteria

*Proposed — for review.*

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Modal with 60 entries shows 25 rows, "1–25 of 60", Next enabled, Previous disabled | `Test_SC1_Modal_FirstPage` |
| SC-2 | Next → rows 26–50; size 50 → rows 1–50, "1–50 of 60" | `Test_SC2_Modal_PagingAndSize` |
| SC-3 | Panel paginates independently of the modal | `Test_SC3_Panel_IndependentPaging` |
| SC-4 | Files added from the Files browser (single file or via a folder action) are visible in modal and panel after a refresh/open | `Test_SC4_FilesItems_Visible` |
| SC-5 | A Files-added row shows Source Tab "Files", status `Informational`, a working viewer link and a selectable delete checkbox; a Keyword Search row shows "Keyword Search" | `Test_SC5_SourceTab_And_Selectable` |
| SC-6 | Toolbar badge (Keyword Search **and** O-Drive Sync pages) and panel count show total (e.g., 60) not 25 | `Test_SC6_Badge_Total`, `Test_SC6_ODriveSync_Badge_Total` |
| SC-7 | Delete across pages: confirm shows total selected; after delete page is clamped | `Test_SC7_Delete_AcrossPages` |
| SC-8 | Read Only: delete disabled; paging works | `Test_SC8_ReadOnly` |
| SC-9 | Existing modal flow tests (delete, error rows, viewer focus return) remain green | existing tests |
| SC-10 | Selecting rows on two pages then opening Delete shows total selected and the "on other pages" count; "Clear selection" empties the selection and the button label | `Test_SC10_CrossPage_Confirm_And_Clear` |
| SC-11 | Deleting every row leaves page 1 of 1 with the empty-state copy; deleting the last page's rows clamps to the new last page | `Test_SC11_Clamp_And_EmptyQueue` |
| SC-12 | Deleting in the modal then closing it updates the panel's rows and count without a manual refresh | `Test_SC12_Panel_ReloadsOnModalClose` |
| SC-13 | Pager buttons expose accessible names; header checkbox label reads "Select all on this page" | `Test_SC13_Pager_Accessibility` |

## §11 Security Notes

Read path only plus the existing delete trigger. Select-all scope reduction (V2) is a safety control for
the irreversible Permanent Delete (FR-17): a user can no longer delete rows they have never seen on screen
by one click — important now that a single folder action can add many rows. No B2 key or content in
payloads.

## §12 Rollback Plan

1. Revert Web + Api together (the paged contract exists only in the new Api build).
2. Database / config / task-queue: N/A.

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `QuarantineQueueModalTests` (bUnit) | paging, select-all scope, source tab, stale selection, clamp/empty | V1–V6, SC-1, SC-2, SC-5, SC-7, SC-8, SC-10, SC-11 |
| `QuarantinePagerTests` | pager unit behaviour, accessible names | V1, SC-13 |
| `KeywordSearchPageTests` | panel paging, badge total, reload on modal close | SC-3, SC-4, SC-6, SC-12 |
| `ODriveSyncPageTests` | Quarantine badge uses `totalCount` | SC-6 |
| `QuarantineQueueFlowIntegrationTests` | unchanged flow | SC-9 |

Coverage target: ≥ 85% on changed files.

## §14 Open Questions

Resolved by the Product Owner (2026-10-05): no folder rows (one row per file), so no folder-delete
question; pagination 25/50.

- [x] **Q-A** Modal-level "Release" (remove) control — **Resolved 2026-10-05: not in this change.** Release
      stays in the Files view (uncheck / folder deselect). Candidate follow-up spec; it would be Full Access
      only (`POST /quarantine/remove`, CAS-050 §2.8).
- [x] **Q-B** Default panel page size — **Resolved 2026-10-05: 25**, same as the modal. The panel has its own
      scroll region; pager fit at the narrow breakpoint (`KeywordSearch.razor.css` ~line 395) is a visual
      check in §16.
- [x] **Q-C** Status chip for Files-added items — **Resolved 2026-10-05: `Informational`**, the only value
      that fits `CK_quarantine_entry_match_status_snapshot` without a schema change (CAS-050 SPs already
      write it). Render as a neutral/muted chip; the Source Tab column carries the origin.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Senior Systems Analyst | 2026-10-05 | draft |
| Reviewer | | | |

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] §1 matches code; §2 implemented; only §5 files changed
- [ ] Select-all is page-scoped; delete count = total selected
- [ ] Badge/panel header use `totalCount`
- [ ] Source Tab comes from `sourceTab`, not a constant
- [ ] Both views page independently; no filter control added to the modal
- [ ] `ODriveSyncContentScan` host still works with the modal, and its Quarantine badge shows `totalCount`
      (e.g. 60 with a 25-row first page)
- [ ] Confirm text shows total selected and the "on other pages" count; "Clear selection" present (SC-10)
- [ ] Stale-selection pruning (V6); the existing delete endpoint tolerates an already-removed ID (confirm
      in `QuarantinePermanentDeleteService`, no change expected)
- [ ] Empty-queue and clamp behaviour (SC-11); panel reloads on modal close (SC-12)
- [ ] Pager visual check in the panel at the narrow breakpoint

### Verification Report Format
`CAS-052.review.md` — Verdict, per-SC table, scope violations, blast radius, security gaps.

## Implementation Log

**Completed:** 2026-10-05
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 88.4% | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `Components/Shared/QuarantinePager.razor(+.css)` | added: shared pager |
| `Components/Shared/QuarantineQueueModal.razor` | paging, page-scoped select-all, confirm text, OnClosed |
| `Components/Pages/KeywordSearch.razor` | panel paging, totalCount, sourceTab, reload on close |
| `Components/Pages/ODriveSyncContentScan.razor` | badge uses TotalCount |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `QuarantinePagerTests` | `Test_V1_PageBounds; Test_SC13_Pager_Accessibility` | V1, SC-13 |
| `QuarantineQueueModalTests` | `Test_V1..V6, Test_SC1,2,5,7,8,10,11,13` | V1-V6, SC-1..13 |
| `KeywordSearchPageTests` | `Test_SC3,SC4,SC6,SC12` | SC-3,4,6,12 |
| `ODriveSyncPageTests` | `Test_SC6_ODriveSync_Badge_Total` | SC-6 |

> **Gate 2 waiver (2026-10-05, owner-accepted):** one full Web run failed
> `ODriveSyncPageTests.Test_ToggleSelectionUncheckRemovesFileIdFromSelection`; it passed 3/3 in isolation and
> in a full `ODriveSyncPageTests` class run. A second intermittent failure
> (`Test_SC5_ActionButtonsReflectOwnPanelSelectionCountIndependently`) was seen once earlier. Not shown to be
> pre-existing (no pre-change baseline); treated as suspected timing flakiness. Reviewer: re-run the Web suite
> and confirm. The log row above reads "pass" because the helper accepts only pass/fail.
