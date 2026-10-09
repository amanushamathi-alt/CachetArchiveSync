---
id: CAS-048
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-10-04
priority: P2
change_type: enhancement
related_specs: [CAS-047, CAS-049, CAS-044, CAS-033, CAS-039, CAS-030, CAS-027, CAS-040, CAS-024]
absorbs: [CAS-033]   # decided 2026-10-04 (Q6): button enablement + modal hosting move into this spec
touches:
  database: false
  api: false       # consumes CAS-047 and CAS-049
  ui: true
  worker: false
  b2: false
  config: false
  security: true   # multi-select to Quarantine from a new view
migrations: []
---

# CAS-048 — Change: Filename / Keyword / Date Search with Explorer-Style Results, and Task Manager Click-Through (Web)

Part 3 of 3. Needs **CAS-047** and **CAS-049** deployed. **Absorbs CAS-033** (enables the Task Manager
button and hosts its modal; decision 2026-10-04). Amends **CAS-044**: mode group, SC-3, V3, and its
out-of-scope "multi-select".

---

## §0 Decomposition decision (Karpathy P9)

One Web project, about 13 files, 10 validations, 14 SCs. The explorer reuse and the Task Manager
click-through share the same mode/result-state logic, so they stay together.

**Scope growth (2026-10-04):** the requester chose to fold CAS-033 (Task Manager reachability) into this
spec, so P9 is knowingly relaxed: the reachability fix is small, touches the same click-through, and
avoids a hard deploy dependency. If the review shows the spec is too large, split it back out by
CAS-033's own file list (`TaskManagerBadge`, modal API fixes) rather than by layer.

---

## §1 Current Behavior

Read from code on 2026-10-04:

- **Mode group** (`KeywordSearch.razor:32-60`): Files (default) / Keyword Search / Date Search, backed by
  `isFilesMode` and `isDateMode` (`:679-680`).
- **Results:** two panels, **Filename Matches** and **Content Matches**, each with its own paging,
  checkboxes and "Send to Quarantine (N)" (`:283-480`; `selectedFilenameIds`, `selectedContentIds`,
  `SendToQuarantineAsync` `:1435`, which posts `search/results/quarantine` with the task id and
  `search_result_id` list). A read-only **Quarantine** panel follows (`:509`).
- **Explorer:** `FileExplorer.razor` (605 lines, CAS-044) shows the B2 folder tree + list for **Files mode
  only**. It has no selection and takes only `OnFileOpened`.
- **Task Manager button** (`TaskManagerBadge.razor:10-14`): `disabled`, "Coming soon". No component hosts
  `TaskManagerModal` (CAS-033 §1, `draft`; the badge file is unchanged). The modal still calls the removed
  `POST tasks/mark-viewed` and requests `pageSize=200`, which the Api caps at 100. It cannot be opened
  today.
- **Modal rows** (`TaskManagerModal.razor:52-101`): own tasks; Completed rows have "View Results", Failed
  rows View Details + Restart. `ViewResults` (`:214`) navigates to `/?taskId=`, `/odrive-sync?taskId=` or
  `/odrive-sync`. The modal is not closed on navigation.
- **Deep link** (`KeywordSearch.razor:819-932`): `ReloadTaskAsync` runs only in `OnInitializedAsync`; it
  loads `?taskId=` or the latest KeywordSearch task. With CAS-044 SC-3 the page then stays in Files mode.

## §2 Desired Behavior

### §2.1 Modes
- Radio order: **Files**, **Filename Search**, **Keyword Search**, **Date Search**. Files is the default on
  a plain open (CAS-044 unchanged). New radio id `#ks-mode-filename`; existing ids unchanged.
- **Filename Search:** term input (trimmed **minimum 3 characters**, max 200; the Search button stays
  disabled and a hint reads "Enter at least 3 characters" below that), Bucket, Extension, Search. Submits
  `mode=Filename`. Matches **file names in the B2 archive index** only.
- **Keyword Search:** text **inside files**, B2 files only. Same **3-character minimum** to enable Search.
  Submits `mode=Keyword`. It shortlists the matching files and shows their folders in the same drill-down
  view; no snippet is shown in the list.
- **Date Search:** unchanged inputs; submits `mode=Date`.
- Term placeholder per mode (copy to confirm, §14 Q2): Filename "Filename contains (min 3 characters)…",
  Keyword "Text to find inside files…".

### §2.2 Results replace the two panels
- The **Filename Matches** and **Content Matches** panels are **removed**. In their place, once a task is
  Completed, the page shows the **same explorer layout as Files mode** (`FileExplorer`, tree on the left,
  Name / Date modified / Type / Size list on the right, breadcrumb, Up, filter box), in **results mode**:
  - Data comes from `GET /search/{taskId}/browse` (CAS-049) instead of `GET /files/browse`.
  - The **top level lists the root folders that contain matches**; clicking a folder opens its subfolders
    and the matched files in it. The tree shows only folders that lead to matches.
  - A **checkbox column** is added for **files and folders** (decided 2026-10-04, Q3). A file checkbox
    selects that file's `searchResultId`. A **folder checkbox selects every matched file beneath that folder**
    (all depths), resolved by the Api (CAS-049 amendment, §8), never by the Web walking the tree. A folder
    shows checked when all its matches are selected, indeterminate when some are. A header checkbox
    selects or clears everything listed in the **current folder** (files and folders). The selection (a set
    of `searchResultId`) **persists while navigating folders** and is cleared after a successful send, a new
    search, or a mode switch. The button count N is the number of distinct files, not folders.
  - Because one click can select many files, a folder selection that would resolve to more than
    the cap (**1,000 files total in the selection**, CAS-049 Q8) shows "Too many files selected (limit
    1,000); narrow the selection" and selects nothing. The button label shows the count (N) before sending.
  - A **Send to Quarantine (N)** button above the list sends the selected `searchResultId`s to the
    existing `POST search/results/quarantine` with the current task id (reuse `SendToQuarantineAsync`),
    then reloads the Quarantine panel and shows the existing success/warning toast.
  - Clicking a **file name** still opens `FileViewerModal` (context `KeywordSearch`, as today; not
    `FileBrowser`, so the audit trail stays tied to search).
  - Status bar: "N items in this folder" plus "M matches in total" (from `GET /search/{id}/results?pageSize=1`
    totals; if the call fails the total is omitted).
  - Loading, empty, filter-no-match, error and spinner states are the Files-mode ones (CAS-044 §8.4).
    Empty result: "No files matched." (Filename/Keyword), "No files in that date range." (Date).
- The **Quarantine panel stays exactly as is** (read-only, below the results).
- While the task is Queued/Running, the existing in-progress banner and polling stay; on completion the
  explorer loads the root.
- Results belong to the mode that produced them: the explorer shows only while the current mode equals
  the result's mode; otherwise the mode shows its empty prompt ("Enter a search…"). No stale rows.
- `FileExplorer` change is additive: a `ResultsTaskId` parameter (null = Files mode, current behavior),
  `Selectable`, `SelectedIds` / `OnSelectionChanged`. Files mode is unchanged.

### §2.3 Task Manager (list and click-through)
- The button is enabled and opens the modal on the Files landing and in every mode, and on the O-Drive
  Sync screen. **This spec now owns that work (absorbed from CAS-033):**
  - `TaskManagerBadge` **hosts `TaskManagerModal` itself** (CAS-033 Q2, recommended option), so
    `KeywordSearch.razor` and `ODriveSyncContentScan.razor` need no new markup and CAS-031 (Audit Log) and
    CAS-032 (reports) elements stay untouched. `MainLayout.razor` stays static (CAS-030).
  - The "Coming soon" title and disabled class are removed; the live badge stays.
  - The modal stops calling the removed `POST /tasks/mark-viewed` and requests `pageSize=100`
    (Api cap, CAS-030 V1). One page of up to 100 tasks, no pager (CAS-033 Q3).
  - **Acknowledgement (CAS-033 Q1):** a terminal task is acknowledged
    (`POST /tasks/{taskId}/acknowledge`, CAS-030) only when the user opens it through View Results,
    View Details, or the new row click. Merely opening the modal does not clear the badge.
  - The skipped CAS-027 SC-8 test is retargeted to `TaskManagerBadgeTests` and un-skipped.
- The modal lists only the signed-in user's tasks (`GET /tasks`, own-scoped by the Api).
- **Completed** rows are clickable (whole row; `tabindex="0"`, `role="link"`, Enter/Space). Clicking closes
  the modal and navigates:

  | Task | Destination | Screen after load |
  |---|---|---|
  | KeywordSearch, mode Filename / Keyword / Date | `/?taskId=<id>` | That mode selected; explorer-style results loaded |
  | KeywordSearch, legacy (`mode` null) | `/?taskId=<id>` | Keyword Search selected, all its results in the explorer; legacy date-only (blank term + dates) selects Date Search |
  | ContentScan | `/odrive-sync?taskId=<id>` | unchanged (CAS-027) |
  | SyncCopy / SyncUpdate / SyncPurge | `/odrive-sync` | unchanged: opens the screen, no task-specific data (CAS-027 §0) |

- **Queued, Running, Failed, Cancelled** rows are **not clickable**: no handler, no pointer cursor,
  `aria-disabled="true"`, muted style, tooltip "Available when the task completes". Failed rows keep
  View Details and Restart (requester 2026-10-04).
- Type column from `searchMode`: "Filename Search" / "Keyword Search" / "Date Search" (null → "Keyword
  Search").

### §2.4 Deep link (supersedes CAS-044 SC-3 for `?taskId=` only)
- `/?taskId=<id>` selects the mode from the task, not Files. A plain `/` still opens in Files.
- The filter bar is not prefilled (CAS-039 A4); the context header describes the task.
- `ReloadTaskAsync` also runs when `TaskId` **changes while the page is open** (`OnParametersSetAsync`;
  today it runs only in `OnInitializedAsync`, so a click on a task while already on `/` would do nothing).
  Cancel any running poll and clear previous results/selection first.
- **Last task reload kept** (requester 2026-10-04). On a plain open the page still silently loads the
  user's latest KeywordSearch task (any mode) from stored state: a Completed task's results are read from
  `dbo.search_result` through browse (**no search is rerun**, no B2 call); a Running task resumes polling;
  a Failed one shows its failure notice. Because the page still opens in **Files**, those results appear
  when the user selects that task's mode (results are bound to their mode, §2.2). §14 Q1 checks this reading.

## §3 Reason for Change

- **Type:** enhancement.
- **Trigger:** requester 2026-10-04: split search into filename (min 3 characters, B2 index) and content
  search; show filename, keyword and date results in the file-viewer format, replacing the two panels;
  keep Quarantine as is with file selection moving to Quarantine; enable Task Manager, listing the user's
  tasks, with completed tasks reopening their results and others not clickable.
- **Risk if not changed:** two ambiguous panels, no folder context for results, and no way back to past
  results from the landing screen.

## §4 Blast Radius (Graph Impact)

Manual read; the graph cache predates these files.

### Primary Change Target
`KeywordSearch.razor` (1,509 lines, largest Web component), `FileExplorer.razor` (605 lines),
`TaskManagerModal.razor`.

### Direct Dependents
`FileViewerModal` (opened with a different context only), `QuarantineQueueModal` and the Quarantine panel
(`LoadQuarantineAsync`, unchanged), `TaskManagerBadge` (CAS-033), `FilesBrowseClient` (a sibling results
client is added). Tests: `KeywordSearchPageTests`, `KeywordSearchDeepLinkTests`, `FileNameClickTests`,
`FileExplorerTests`, `TaskManagerModalTests`, `FakeKeywordSearchApiHandler`, `FakeTaskManagerApiHandler`.

### Downstream Modules
Keyword Search screen, Quarantine (entry flow only), Task Manager. Not affected: Auth, O-Drive Sync screen,
Audit Log, Files mode behavior.

### Cross-Project Risk
**CROSS-PROJECT RISK:** reads `mode`/`searchMode` (CAS-047) and `searchResultId` + the browse endpoint
(CAS-049). Web must deploy last (§12).

### God Node Involvement
`KeywordSearch.razor`: **HIGH RISK by size and test coupling.** This change **deletes** about 200 lines of
panel markup and the per-panel paging/selection state, which also removes the code behind many existing
tests (§5a). Follow local idiom (a third exclusive bool `isFilenameMode`, not an enum refactor, P3/P4).

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|-------------|
| `Web/Components/Pages/KeywordSearch.razor` | modify | Filename radio + `isFilenameMode`; per-mode term rules and placeholder; submit `mode`; remove the two match panels and their paging/selection state; host `FileExplorer` in results mode + Send to Quarantine; mode-from-task on reload; `OnParametersSetAsync`; remove implicit latest-task reload. |
| `Web/Components/Pages/KeywordSearch.razor.css` | modify | Four-option radio row; results host area. |
| `Web/Components/Shared/FileExplorer.razor` (+ `.razor.css`) | modify | `ResultsTaskId`, selection column, header checkbox, `OnSelectionChanged`; results-mode empty copy. Files mode untouched. |
| `Web/Http/SearchResultsBrowseClient.cs` | add | Typed wrapper for `GET search/{taskId}/browse` (same 401/404/409/5xx result mapping as `FilesBrowseClient`). Registered in `Program.cs`. |
| `Web/Program.cs` | modify | DI registration only. |
| `Web/Components/Layout/TaskManagerBadge.razor` | modify | Enable the button, drop "Coming soon", host `TaskManagerModal` (absorbed from CAS-033). |
| `Web/Components/Shared/TaskManagerModal.razor` | modify | Remove `mark-viewed`, `pageSize=100`, per-task acknowledge (absorbed from CAS-033); Completed-row click/keyboard, non-completed inert styling and `aria-disabled`, Type label, close on navigate. |
| Stylesheet carrying `cachet-tm-*` (located at implementation) | modify | Clickable vs muted row. |
| `Web.Tests/**` (§13, §5a) | add/modify | |

### §5a Pre-existing tests affected
Panel-based tests (`FileNameClickTests`, the `KeywordSearchPageTests` panel/paging/selection/quarantine
tests, CAS-030 reload tests, CAS-044 §5's list) are **rewritten to assert the same intent against the
results explorer**, or deleted where the behavior no longer exists (per-panel paging, panel collapse for
the two removed panels). List every rewritten and deleted test, with its reason, in the Implementation Log.

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `FilesBrowseClient.cs`, Files-mode code paths in `FileExplorer` | Files mode unchanged. |
| `MainLayout.razor` | Stays static (CAS-030). |
| CAS-031 Audit Log button/modal and CAS-032 report buttons/`ReportModal` on both pages | Must remain exactly as they are; the badge hosts the modal so the pages are not edited. |
| `ODriveSyncContentScan.razor` | Deep link unchanged. |
| `QuarantineQueueModal`, Quarantine panel markup, `AuditLogModal`, `ReportModal`, `FileViewerModal` | Reused as is. |
| `Api/**`, `Core/**`, `Worker/**`, `Database/**` | CAS-047 / CAS-049. |

---

## §7 Database Changes
N/A.

## §8 API Contract Changes
N/A in this project. Consumes `POST /search { mode }`, `GET /search/{id}/browse` (CAS-049), `GET /tasks`
`searchMode`, `GET /tasks/{id}` `searchParameters.mode` (CAS-047), `POST /tasks/{id}/acknowledge` (CAS-030).

**Folder selection (Q3):** uses `GET /search/{taskId}/browse/result-ids?folder=` (CAS-049 amendment,
2026-10-04). It returns `{ searchResultIds, count }`, or 400 `selection-too-large` (`limit` 1,000). The Web
adds the returned ids to the selection and enforces the same **1,000 total-selection cap** before sending.
`SearchResultsBrowseClient` gains `GetResultIdsAsync` with the same 401/404/409/5xx mapping; 400
`selection-too-large` maps to the over-cap message. Deploy after CAS-049 includes the amendment.

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | Keyword needs a term of 1–200 | **Filename and Keyword: trimmed 3–200** (hint shown below 3). Date: none. Search is disabled until valid. | `Test_V1_SearchEnabledRulesPerMode` |
| V2 | Files disables all search controls (CAS-044 V1) | Unchanged for Files. | `Test_V2_FilesStillDisablesControls` |
| V3 | Hidden stale date never posted | Also: `mode` posted always matches the selected radio; Filename and Keyword never post dates. | `Test_V3_PostedModeMatchesSelectedRadio` |
| V4 | Bad `?taskId=` shows the not-found notice | Also for a non-search task type or a 404/409 from browse. | `Test_V4_BadOrWrongTypeTaskIdShowsNotice` |
| V5 | n/a | A non-Completed Task Manager row does nothing on click. | `Test_V5_NonCompletedRowNotClickable` |
| V6 | n/a | Only `searchResultId` values from browse responses are sent to Quarantine, with the current task id; never a typed or built value. | `Test_V6_QuarantineSendsBrowseIdsOnly` |
| V7 | n/a | A browse 401 shows the session-expired message; 404/409/5xx show the generic message with Retry; no Api detail or path rendered. | `Test_V7_ResultsBrowseErrorStates` |
| V8 | n/a | The folder sent to browse comes only from earlier browse responses; the filter text is never sent. | `Test_V8_ResultsFolderParamFromResponsesOnly` |
| V9 | n/a | A folder checkbox resolves its files through the Api only; the Web never builds or guesses `searchResultId`s. Over-cap or error selects nothing and shows the message. | `Test_V9_FolderSelectionResolvedByApiOnly` |
| V10 | `POST /tasks/mark-viewed`, `pageSize=200` | No call to `mark-viewed`; list requests `pageSize` ≤ 100; acknowledge is sent only for a task the user opens. | `Test_V10_NoMarkViewedPageSize100AckOnOpenOnly` |

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Radio group is Files, Filename Search, Keyword Search, Date Search; Files checked on a plain open. | `Test_SC1_FourModesFilesDefault` |
| SC-2 | Filename and Keyword Search with 2 characters keep Search disabled with the hint; 3 characters enable it and post `mode=Filename` / `mode=Keyword`. | `Test_SC2_FilenameAndKeywordMinThreeCharacters` |
| SC-3 | Keyword Search posts `mode=Keyword`; Date Search posts `mode=Date`. | `Test_SC3_KeywordAndDateModesPost` |
| SC-4 | The Filename Matches and Content Matches panels no longer exist in any mode; a Completed task shows the explorer (results mode) whose top level lists the folders returned by browse. | `Test_SC4_ResultsShownInExplorerNoPanels` |
| SC-5 | Clicking a result folder calls browse for that folder and lists its subfolders and matched files; breadcrumb, Up and tree highlight work as in Files mode. | `Test_SC5_ResultFolderDrillDown` |
| SC-6 | Checking files in two different folders keeps both selected across navigation; "Send to Quarantine (2)" posts both `searchResultId`s with the task id, then clears the selection and refreshes the Quarantine panel. | `Test_SC6_SelectionPersistsAcrossFoldersAndSendsToQuarantine` |
| SC-7 | The Quarantine panel renders and behaves as before. | `Test_SC7_QuarantinePanelUnchanged` |
| SC-8 | Switching mode or starting a new search hides the old results and clears the selection. | `Test_SC8_ResultsAndSelectionBoundToMode` |
| SC-9 | The Task Manager button is enabled in Files mode and opens the modal; the modal lists the tasks returned by `GET /tasks` with mode labels. | `Test_SC9_TaskManagerOpensAndListsTasks` |
| SC-10 | Clicking a Completed Filename/Keyword/Date task closes the modal and shows that mode with its results; works while already on `/` (reload on parameter change; earlier poll cancelled). | `Test_SC10_CompletedTaskClickRestoresModeAndResults` |
| SC-11 | Queued, Running, Failed, Cancelled rows are not clickable and `aria-disabled`; Failed still offers View Details and Restart. | `Test_SC11_NonCompletedRowsInert` |
| SC-12 | Legacy task opens in Keyword Search with its results in the explorer; legacy date-only opens in Date Search. | `Test_SC12_LegacyTaskOpensAsToday` |
| SC-13 | Checking a folder selects all matched files beneath it (all depths); the folder shows checked/indeterminate correctly; "Send to Quarantine (N)" counts distinct files and posts their `searchResultId`s. Over-cap shows the message and selects nothing. | `Test_SC13_FolderCheckboxSelectsDescendantMatches` |
| SC-14 | The Task Manager button is enabled and hosted by `TaskManagerBadge` on both pages; opening the modal does not clear the badge; opening a task acknowledges only that task; CAS-027 SC-8 is retargeted and passes; CAS-031/CAS-032 elements unchanged. | `Test_SC14_BadgeHostsModalAckOnOpen` |

---

## §11 Security Notes
- **Quarantine selection (security: true):** ids come from Api responses (V6); ownership of the task is
  enforced by the Api (CAS-049 V1) and by the existing quarantine endpoint. The write is audited by the Api
  before success (FR-23). The Web adds no audit call. Permanent Delete stays only in the Quarantine Queue
  modal (FR-16/17), unchanged.
- No B2 key, server path, prefix or session value reaches the browser (FR-03). The search term is never in
  a URL or log; the deep link carries only the integer `taskId`. Names render through Razor encoding;
  no `MarkupString`.

## §12 Rollback Plan
1. Deploy order: CAS-047 → CAS-049 (incl. the folder-resolve amendment) → this spec (CAS-033 is absorbed
   here and is not deployed separately). Rollback: revert Web first; the Task Manager button returns to
   disabled.
2. After rollback the old two-panel screen needs the old API fields, which CAS-047/049 do not remove.
3. Tasks created in new modes remain valid; the older Web shows them as legacy.
4. No database, config or task-queue change.

## §13 Test Plan

| Class | Method | Proves |
|-------|--------|--------|
| `KeywordSearchPageTests` | `Test_SC1_..`–`Test_SC4_..`, `Test_SC6_..`–`Test_SC8_..`, `Test_V1_..`–`Test_V3_..`, `Test_V6_..` | Modes, results explorer, quarantine |
| `FileExplorerTests` | `Test_SC5_..`, `Test_V7_..`, `Test_V8_..` | Results-mode navigation and errors |
| `KeywordSearchDeepLinkTests` | `Test_SC10_..`, `Test_SC12_..`, `Test_V4_..` | Mode-from-task, re-reload |
| `TaskManagerModalTests` | `Test_SC9_..`, `Test_SC11_..`, `Test_V5_..`, `Test_V10_..` | Rows, labels, API usage |
| `TaskManagerBadgeTests` | `Test_SC14_..` (CAS-027 SC-8 retargeted) | Hosting, acknowledge-on-open |
| `KeywordSearchPageTests` / `FileExplorerTests` | `Test_SC13_..`, `Test_V9_..` | Folder selection |

Coverage ≥ 85% on changed files. Manual check: Filename Search for "HRBUTLER" → root folders with matches →
open a folder → tick files in two folders → Send to Quarantine → Quarantine panel updates; then Task
Manager → click the Completed task and a Running one. At 1280px and 400px, light and dark.

## §14 Open Questions

Resolved 2026-10-04 (requester): B2-only strictly; folder drill-down for Filename and Keyword; snippets not
shown; 3-character minimum for Filename and Keyword; Date stays on created date; Failed rows stay visible
(with View Details and Restart) but are **not clickable**; the last task is reloaded.

Resolved 2026-10-04 (requester, this review):
- [x] **Q1 — Last-task reload.** Files stays the landing. The reloaded task's results show when its mode is
      selected, or through Task Manager click-through. CAS-044 SC-3 is amended for `?taskId=` only (§2.4).
- [x] **Q3 — Folder-level selection.** Folder checkbox selects all matched files beneath it. Needs a
      CAS-049 amendment and a cap (new Q8 below). Reflected in §2.2, §8, V9, SC-13.
- [x] **Q4 — Reports.** Pre-/Post-Action Report buttons stay unchanged and apply to all modes. Not
      verified against code yet: confirm during implementation, `BLOCKED: reports` if a mode fails.
- [x] **Q6 — CAS-033.** Folded into this spec (§0, §2.3, §5, V10, SC-14). CAS-033's Q1–Q3 are answered
      in §2.3 using its recommended options (acknowledge on open, badge hosts modal, one 100-row page).
      **Those three defaults were not individually confirmed by the requester.**

Still open:
- [ ] **Q2 — Copy.** Radio label, placeholders, hint and empty-state text are proposals; confirm before
      implementation.
- [ ] **Q5 — Sync task rows.** Completed Sync* rows open `/odrive-sync` without task data (CAS-027 §0).
      Default stands unless the requester wants a result view.
- [ ] **Q7 — Retention** (CAS-049 Q1): reloaded results depend on `search_result` rows and active files.
      Owned by CAS-049; no action here.
- [x] **Q8 — Folder-select cap and endpoint.** Resolved on the author's recommendation: CAS-049 now has
      `GET /search/{taskId}/browse/result-ids` and a 1,000-file cap. Code check: the quarantine endpoint
      (`QuarantineService`) has no size limit and inserts per id, so it is unchanged. The 1,000 value is
      **not yet confirmed by the requester**; CAS-049 SC-10 measures it.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code (drafted for devarajan@tracecounts.org) | 2026-10-04 | Draft; revised same day after requester answers |
| Reviewer | | | |

## §16 Verification Plan

- [ ] §1 matches the code before the change (including Task Manager unreachable until CAS-033)
- [ ] Both match panels are gone; results use the explorer in every mode (SC-4, SC-5)
- [ ] Quarantine selection works across folders and uses browse ids only (SC-6, V6)
- [ ] Filename minimum 3 characters in the UI (V1, SC-2)
- [ ] Completed rows only are clickable; deep link and re-reload work (SC-10, V5)
- [ ] §5a: every rewritten/deleted test listed with its reason
- [ ] Folder checkbox resolves via the Api only, with the cap enforced (SC-13, V9)
- [ ] Badge hosts the modal; no `mark-viewed`; acknowledge on open only; CAS-031/032 elements unchanged (SC-14, V10)
- [ ] Only §5 files changed; nothing in §6 touched; §11 holds

Reviewer writes `CAS-048.review.md`.

## Implementation Log

**Completed:** 2026-10-05
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 90.5-100% on changed files | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `Web/Http/SearchResultsBrowseClient.cs` | add: typed client for search browse and result-ids |
| `Web/Program.cs` | modify: DI registration |
| `Web/Components/Shared/FileExplorer.razor(+css)` | modify: results mode, selection column, folder-select via Api, 1,000 cap |
| `Web/Components/Pages/KeywordSearch.razor(+css)` | modify: Filename mode, panels removed, results explorer, mode-from-task, OnParametersSetAsync |
| `Web/Components/Layout/TaskManagerBadge.razor` | modify: enabled, hosts TaskManagerModal (absorbed CAS-033) |
| `Web/Components/Shared/TaskManagerModal.razor(+css)` | modify: no mark-viewed, pageSize 100, ack on open, clickable Completed rows, inert others, Type label, OnTaskAcknowledged |
| `Web.Tests/** (KeywordSearch, FileExplorer, TaskManager, Layout, fakes)` | add/modify/delete per section 5a |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `KeywordSearchPageTests` | `Test_SC1..SC4,SC6..SC8,SC13,V1..V3,V6` | modes, results explorer, quarantine, folder select |
| `FileExplorerTests` | `Test_SC5,V7,V8,V9` | results navigation, errors, Api-only ids |
| `KeywordSearchDeepLinkTests` | `Test_SC10,SC12,V4` | mode-from-task, re-reload |
| `TaskManagerModalTests` | `Test_SC9,SC11,V5,V10` | rows, labels, API usage |
| `TaskManagerBadgeTests` | `Test_SC14, Test_SC8 (CAS-027 retargeted)` | hosting, ack on open |

### Gate Notes (accepted by requester, option B, 2026-10-05)
- **Tests ✓ is per-project, not whole-solution.** Whole-solution runs intermittently failed 1–2 tests in
  `Web.Tests`. These were not touched by this spec: `TaskPollingOptionsTests.Test_V8_…`,
  `ODriveSyncPageTests.Test_SC9_ConfirmedPurge…`, `…Test_PurgeActionPollReloadsRemovedPanelOnCompletion`,
  `…Test_ToggleSelectionUncheckRemovesFileIdFromSelection`.
- `ODriveSyncPageTests` also failed intermittently (about 1 run in 3) when run alone. The failure is
  immediate ("Sequence contains no matching element" after clicking Purge), which points to a test race, not
  a timeout. **Not verified whether it pre-dates this change**: no git baseline exists, and the O-Drive page
  hosts `TaskManagerBadge`, which this spec modified. The reviewer should check this.
- One flake in the new `Test_SC10_…` (10 s default poll interval in the deep-link host) was fixed by
  registering a 1 s `TaskPollingOptions` in `KeywordSearchDeepLinkTests`.
- Coverage: changed files 90.5% (FileExplorer), 98.2% (TaskManagerModal), 98.6% (TaskManagerBadge), 97.9%
  (KeywordSearch), 100% (SearchResultsBrowseClient). Assembly overall 81.7%.
- Open spec points before sign-off: folder checkbox shows checked/indeterminate only after its ids are
  resolved (§2.2/SC-13 reads stronger than the code); V4 vs V7 conflict on browse 404/409 (code follows V7).
- Endpoint drift gate skipped: `touches.api: false`.
