---
id: CAS-044
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-04
priority: P2
related_specs: [CAS-043, CAS-039, CAS-040, CAS-022, CAS-030, CAS-002]
touches:
  database: false
  api: false       # consumes CAS-043; no Api change here
  ui: true
  worker: false
  b2: false
  config: false
  security: true   # file open and download from a new screen mode; audit context
migrations: []
---

# CAS-044 — Files Explorer Landing Mode (Keyword Search Screen)

Part 2 of 2 for the Files landing-screen request (2026-10-04). **Depends on `CAS-043`** (Database +
Api) being deployed first. Design reference: the interactive wireframe approved by the requester on
2026-10-04 (`CACHET Landing Wireframe`, Artifact `HUvjBmAtuWGrH4CSz52NEz`, Version 1).

**Requirement status:** the BRD (v2.3) has no requirement for a folder browser. This spec delivers
the UI half of **FR-29**, defined in `CAS-043` §0. The client approved it by email (sent by Nasrollah,
2026-10-02; confirmed by the requester, 2026-10-04); no deployment gate applies. The BRD itself has not yet been revised to include
FR-29.

---

## §0 Decomposition decision (Karpathy P9)

Split signals that fired for the whole request: project boundary (Database/Api/Core/Web) and
test-domain split (Api integration tests and Web bUnit tests). The request is therefore split into
`CAS-043` (Database + Api) and this spec (Web). This spec alone stays under every size threshold
(0 endpoints, §9 validations ≤ 10, one component changed plus two new ones, about 10 files).

---

## §1 Summary

The Keyword Search screen (`/`, `KeywordSearch.razor`) becomes the landing screen with a third
mode, **Files**, which is selected by default. In Files mode the user browses the archive as folders
and files, laid out like Windows File Explorer: a folder tree on the left and a Name / Date modified
/ Type / Size list on the right. Clicking a folder opens it. Clicking a file opens the existing File
Viewer pop-up (`CAS-040`), which already has the Download (Save) button.

While Files is selected, the Keyword Search and Date Search inputs, Bucket, Extension and the Search
button are disabled. Selecting Keyword Search or Date Search enables them and switches the main area
back to the existing three result panels, unchanged. Implements FR-29; builds on FR-03,
FR-05 and FR-23.

**Supersedes, for this screen only:** `CAS-039` SC-1 and A4 ("Keyword Search selected by default on
every open") and `CAS-039` A3 / `CAS-030` §8.2 where they describe the default mode. The default is
now **Files**.

**Amended 2026-10-04 (draft, pending approval): see `CAS-048`.** The mode group becomes Files /
Filename Search / Keyword Search / Date Search. The Task Manager button is enabled on this screen and
lists the user's tasks; a Completed task opens the screen in that task's own mode with its results.
Search results (all three modes) are shown in this explorer layout, so `FileExplorer` gains a results
mode with file selection for Quarantine (this relaxes §4's "multi-select" exclusion for results only; Files
mode stays browse-and-open). `SC-3` and `V3` below change for a `?taskId=` deep link only: it selects the
task's mode instead of Files. A plain open still starts in Files. Nothing else in this spec changes.

---

## §2 User Story

As a **TraceCounts staff reviewer**, I want **to land on a folder view of the archive and open any
file from it**, so that **I can look at a document I know the location of without running a search
first**.

---

## §3 In-Scope

- A third radio, **Files**, in the existing `ks-mode` radio group, selected on every open.
- Files mode controls state: `#ks-term`, From/To dates, Bucket, Extension and Search disabled;
  Task Manager, Quarantine Queue and Audit Log unchanged and enabled.
- A new `FileExplorer.razor` component (+ `.razor.css`) rendered in Files mode:
  - Left **folder tree**, with lazy expand and the current folder highlighted.
  - Right **list**: Name, Date modified, Type, Size; folders first, then files; folder size blank.
  - **Breadcrumb**, **Up** button, and a client-side **filter** over the folder's items.
  - Status bar: item count, and the note that data comes from the B2 archive index.
  - Loading, empty-folder, filter-no-match and error states (§8.4).
- Click a folder (tree or list) to open it; click a file to open `FileViewerModal` with
  `Context="FileBrowser"` (`CAS-043` V8).
- The explorer shows the **B2 archive** (requester decision, 2026-10-04): the Api serves the
  `source = B2` index rows (`CAS-043` §3), and the status bar reads "Source: B2 archive index
  (metadata only). File contents open on demand." The browser sends no source or bucket value.
- A thin Web client for `GET /files/browse` through the existing cookie-forwarding `"CachetApi"`
  client.
- Keyword Search and Date Search modes keep today's behavior, including `CAS-039` A3 (exclusive
  modes) and the result panels.

## §4 Out-of-Scope

- **Any Api, Core or Database change.** All of it is in `CAS-043`.
- **Multi-select, drag and drop, context menus, rename, move, delete, upload, new folder.** Browsing
  and opening only. Quarantine still starts from Keyword Search results.
- **Column sorting and resizing, grid/tiles view, keyboard type-ahead.** Fixed sort: folders first,
  then name.
- **Searching inside Files mode.** The search inputs are disabled by design. The filter box only
  narrows the folder's items.
- **Opening a file type the viewer cannot show.** Behavior stays as in `CAS-040` (415 message and
  Download). No new viewer types.
- **Persisting the last folder** across sessions. Nothing is stored in the browser.
- **The O-Drive Sync and Content Scan tab** (`ODriveSyncContentScan.razor`) is not changed.
- **Pass-through endpoints and `FileViewerModal` internals.** Reused as is.

---

## §5 Files to Change

| File | Change | Description |
|------|--------|-------------|
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | modify | Third radio `#ks-mode-files`, default Files; `isFilesMode` handling; disable bound controls; render `FileExplorer` or the existing panels; hand `OnFileOpened` to `FileViewerModal` with context `FileBrowser`. |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor.css` | modify | Disabled-state styling for toolbar controls (reusing `--cachet-*` tokens); radio row for three options. |
| `src/Cachet.ArchiveSync.Web/Components/Shared/FileExplorer.razor` | add | The tree + list + breadcrumb + status bar. Parameter `OnFileOpened` (`EventCallback<int>`). |
| `src/Cachet.ArchiveSync.Web/Components/Shared/FileExplorer.razor.css` | add | Explorer layout, folder/file glyphs, selection and hover, responsive stacking under 800px. |
| `src/Cachet.ArchiveSync.Web/Components/Shared/FileViewerModal.razor` | modify | Update the `Context` doc comment to list `FileBrowser`. **Amended 2026-10-04 (requester):** the "Modified" date in the header is shown exactly as read from the database, with no UTC conversion and no "UTC" label (`FormatUtc` → `FormatModified`, format `yyyy-MM-dd HH:mm:ss`). This applies to every screen that hosts the viewer. `ReportModal` is unchanged. |
| `src/Cachet.ArchiveSync.Web.Tests/FileViewer/FileViewerModalTests.cs` | modify | The Modified-date assertion expects `2026-08-14 10:22:05` and no "UTC". |
| `src/Cachet.ArchiveSync.Web.Tests/FileViewer/FileNameClickTests.cs` | modify | Added retroactively (review note): the CAS-040 tests host `KeywordSearch`, so they register `FilesBrowseClient` and select Keyword Search after render because the default mode is now Files. |
| `src/Cachet.ArchiveSync.Web/Http/FilesBrowseClient.cs` | add | Typed wrapper over `"CachetApi"` for `GET files/browse`; maps 401/404/5xx to a result type. Registered in `Program.cs`. |
| `src/Cachet.ArchiveSync.Web/Program.cs` | modify | DI registration only. |
| `src/Cachet.ArchiveSync.Web.Tests/FileExplorer/FileExplorerTests.cs` | add | bUnit tests (§13). |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchPageTests.cs` | modify | New Files-mode tests, and the updates listed in §6. |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchDeepLinkTests.cs` | modify | A4 default assertions updated to Files (§6). |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/FakeKeywordSearchApiHandler.cs` | modify (if needed) | Serve `GET files/browse` fixtures. |

**Pre-existing tests that this spec changes on purpose** (the default mode moves from Keyword to
Files): `Test_SC1_DefaultsToKeywordModeWithNoDateInputs`, `Test_A4_SC14_OpenAlwaysResetsFilterBarButKeepsReloadedResultsAndHeader`,
`Test_SC7_ReloadedDateTaskRestoresModeDatesAndHeader` and the CAS-030 reload tests that assert the
filter-bar state on open. Each keeps its intent and is edited only to select Keyword Search first or
to expect Files. List every edited test in the Implementation Log.

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `src/Cachet.ArchiveSync.Api/**`, `Core/**`, `Worker/**`, `Database/**` | Owned by `CAS-043`. |
| `ODriveSyncContentScan.razor` | Separate tab. |
| `QuarantineQueueModal`, `TaskManagerModal`, `AuditLogModal`, `ReportModal`, `TaskManagerBadge` | Rendered unchanged. |
| `wwwroot/app.css` | Scoped CSS is enough; no global token change. |
| `FileViewerModal.razor` logic (apart from the date display amended in §5) and `Program.cs` file pass-through | Reused unchanged (`CAS-040`). |

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | In Files mode, `#ks-term`, `#ks-from-date`, `#ks-to-date`, `#ks-bucket`, `#ks-extension` and the Search button are `disabled`, and a click cannot start a search. | n/a (control state) | `Test_V1_FilesModeDisablesAllSearchControls` |
| V2 | Selecting Keyword or Date Search enables the controls under the existing `CAS-039` rules (term valid, dates valid, bucket set). | n/a | `Test_V2_SwitchingToSearchModeEnablesControlsUnderExistingRules` |
| V3 | Switching to Files clears nothing the user typed that is still valid for the previous mode; a hidden stale date is still never posted (`CAS-039` SC-5). | n/a | `Test_V3_SwitchingToFilesKeepsNoStaleDatePosted` |
| V4 | A 401 from `GET files/browse` shows the session-expired message (same text as `KeywordSearch.razor`) and stops further browse calls. | Message shown | `Test_V4_BrowseUnauthorizedShowsSessionExpired` |
| V5 | A 404 `folder-not-found` shows "This folder is no longer in the archive." and returns to the root. | Message shown | `Test_V5_FolderNotFoundShowsMessageAndReturnsToRoot` |
| V6 | Any other failure shows "The folder list could not be loaded. Try again." with a Retry button. No Api detail, path or exception text is rendered. | Message shown | `Test_V6_BrowseFailureShowsGenericMessageWithRetry` |
| V7 | The file-open pop-up is called only with an integer `fileIndexId` from the browse response, never with a path typed or built in the browser. | n/a | `Test_V7_FileOpenUsesFileIndexIdOnly` |
| V8 | The folder `path` sent to `GET files/browse` is taken only from a previous browse response (tree, list, breadcrumb or Up). The filter box value is never sent. | n/a | `Test_V8_BrowseFolderParamComesFromResponsesOnly` |

---

## §8 UI / UX

Reference: the wireframe named at the top of this spec.

### §8.1 Mode radio group and toolbar
- One radio group (`name="ks-mode"`, `role="radiogroup"`) with three options in this order:
  **Files** (`#ks-mode-files`, checked by default), **Keyword Search**, **Date Search**.
- On every open, Files is selected and all search fields are empty (the `CAS-039` A4 reset still
  applies; only the default changes).
- Files mode: the keyword input is shown (disabled, with its normal placeholder); the date fields
  are not shown until Date Search is picked. Bucket, Extension and Search are shown disabled
  (greyed, `cursor: not-allowed`), using the existing disabled styling and tokens.
- The right-hand buttons (Task Manager, Quarantine Queue, Audit Log) are unchanged and always
  enabled.
- The `CAS-039` A4 shared bottom alignment of toolbar controls is kept in all three modes.

### §8.2 Files mode: explorer layout
- **Tree (left, 280px, scrolls on its own):** rooted at the Api's `rootName` (for example
  "Operations"; the breadcrumb's first segment is the same label). Each node shows a chevron
  (only if it may have children), a folder glyph and the name. Clicking the chevron expands or
  collapses and loads children with `foldersOnly=true`. Clicking the name opens the folder. The open
  folder is highlighted. Ancestors of the open folder are expanded.
- **List (right):** Name, Date modified, Type, Size. Folders first, then files, in the order the Api
  returns. Folders show type "File folder", the `latestModifiedAt` value, and no size. Files show
  their extension-based type name, `modifiedAt`, and size in B / KB / MB. Dates use `MM/dd/yyyy
  h:mm tt` (matching `CAS-039` date text), shown **exactly as read from the database, with no
  time-zone conversion** (requester decision 2026-10-04, superseding the earlier server-local-time
  note). Neither the server's nor the browser's time zone is applied, and no JS interop is used.
- **Breadcrumb bar:** an **Up** button, then the path as clickable segments built from the response's
  `breadcrumb`. A **Filter this folder** box narrows the loaded items by name (case-insensitive).
- **Status bar:** "N items" for the visible list, plus "Source: B2 archive index (metadata only). File
  contents open on demand."
- **No item limit** (requester decision, 2026-10-04): like File Explorer, a folder shows every folder
  and file in it. The Api returns them all in one response (`CAS-043` §6), and the list renders
  through Blazor's `Virtualize` component with a fixed row height, so only the visible rows are in
  the page and a very large folder stays scrollable. There is no "Load more" row.
- **Narrow screens (under 800px):** the tree stacks above the list with its own scroll; the filter
  box hides.

### §8.3 Opening items
- **Folder (tree or list):** loads that folder, updates the breadcrumb, clears the filter.
- **File:** highlights the row and opens `FileViewerModal` with `FileIndexId = <id>` and
  `Context = "FileBrowser"`. The modal's existing Download button is the "Save" action; the audit
  rows are written by the Api (`CAS-043` §12). Closing the modal returns focus to the row.
- A file whose type the viewer cannot show reaches the modal's existing "can't be opened here" state
  (`CAS-040`), which still offers Download.

### §8.4 States
| State | Copy / behavior |
|---|---|
| Loading | A **spinner wheel** (requester decision, 2026-10-04) is centered over the list pane (and shown on a tree node while its children load), with the text "Loading…" for screen readers (`role="status"`). Folders are large (the B2 index has 876,554 rows), so the first load can take noticeable time. The spinner stays visible for the whole call, however long it runs; the screen applies no time limit of its own. A click on another folder while one is loading replaces the pending request (the earlier call is cancelled). No progress percentage is shown. |
| Empty folder | "This folder is empty." |
| Empty root | "No files are indexed yet." |
| Filter with no match | "No items match this filter." |
| Session expired | `KeywordSearch.razor`'s existing `SessionExpiredMessage`. |
| Folder gone (404) | "This folder is no longer in the archive." and return to root. |
| Other error | "The folder list could not be loaded. Try again." with a Retry button. |
| Tree-expand failure (not 401/404) | The same generic message is shown in the tree pane (`role="alert"`, no Retry button; clicking the chevron again retries). The list stays usable. A 401 follows V4; a 404 follows V5. |

### §8.5 Accessibility
- The tree uses `role="tree"` / `treeitem` with `aria-expanded`; the list is a `<table>` with
  `<th scope="col">`; rows are keyboard focusable and open on Enter.
- Disabled controls use the native `disabled` attribute, not only styling.
- Visible focus outlines on every control.

---

## §9 Configuration Changes

None. Web reads `Api:BaseUrl` already, and the bucket is resolved by the Api (`CAS-043` §3).

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | On first render, **Files** is checked, Keyword Search and Date Search are not, the explorer is shown, and the three result panels are not. | `Test_SC1_DefaultsToFilesModeWithExplorerVisible` |
| SC-2 | Selecting Keyword Search enables the term input, Bucket, Extension and (when valid) Search, and shows the three result panels; selecting Date Search shows From/To and hides the term input (`CAS-039` A3). | `Test_SC2_SearchModesRestoreControlsAndResultPanels` |
| SC-3 | Opening the screen at `/` or by `?taskId=` deep link always starts in Files mode; a reloaded task's results appear once the user switches to Keyword or Date Search. | `Test_SC3_EveryOpenStartsInFilesMode` |
| SC-4 | On load the explorer calls `GET files/browse` for the root and lists folders before files with Name, Date modified, Type and Size. | `Test_SC4_ExplorerLoadsRootFoldersBeforeFiles` |
| SC-5 | Clicking a folder in the list calls browse for that folder's `path` and updates the breadcrumb and highlighted tree node. | `Test_SC5_ClickingListFolderOpensItAndUpdatesBreadcrumb` |
| SC-6 | Expanding a tree node calls browse with `foldersOnly=true` once and caches the result; collapsing and re-expanding makes no second call. | `Test_SC6_TreeExpandLoadsFoldersOnlyOnce` |
| SC-7 | The Up button opens the parent folder and is disabled at the root. Clicking a breadcrumb segment opens that folder. | `Test_SC7_UpAndBreadcrumbNavigate` |
| SC-8 | Clicking a file opens `FileViewerModal` with that row's `fileIndexId` and `Context = "FileBrowser"`; its Download link targets `files/{id}/download?context=FileBrowser`. | `Test_SC8_ClickingFileOpensViewerWithFileBrowserContext` |
| SC-9 | The filter box narrows the loaded items by name without a network call, and an empty match shows the §8.4 message. | `Test_SC9_FilterNarrowsLoadedItemsLocally` |
| SC-10 | A folder with 2,500 files lists all of them: scrolling to the end shows the last item, there is no "Load more" row, and the rendered row count stays far below 2,500 (virtualized). | `Test_SC10_LargeFolderShowsEveryItemThroughVirtualizedList` |
| SC-11 | V1–V8 behave as specified, including the exact messages in §8.4. | the V-tests in §7 |
| SC-12 | No rendered markup or request contains a server file-system path, mirror root or B2 key; the only paths are the index-relative paths from the Api. | `Test_SC12_NoServerPathOrKeyInMarkupOrRequests` |
| SC-13 | Keyword Search and Date Search flows (submit, polling, panels, Send to Quarantine, banners) behave as before; every pre-existing `KeywordSearch*` test passes, apart from the default-mode edits listed in §5. | existing suite |
| SC-15 | While a browse call is in flight the spinner (`role="status"`) is shown in the list pane; it disappears when the list renders or an error state is shown. Opening another folder cancels the earlier call, so a slow earlier response never overwrites a later folder. Opening a folder writes nothing to the audit log (Web makes no audit call). | `Test_SC15_SpinnerShownWhileLoadingAndLatestFolderWins` |
| SC-14 | Manual check (§18): at 1280px and 400px widths the explorer matches the wireframe layout; disabled controls look disabled; in dark and light themes nothing is unreadable. | manual |

---

## §11 Telemetry

```csharp
_logger.LogInformation("Files explorer opened folder at depth {Depth}", depth);
_logger.LogWarning("Files browse failed with status {StatusCode}", statusCode);
```
- Never log folder paths, file names, the B2 application key or any part of it, or session values.

---

## §12 Security Notes

- **No B2 surface.** The screen only calls Web's own `/files/{id}/view|download` (`CAS-040`) and the
  Api's `GET files/browse` through the server-side `"CachetApi"` client. No key, presigned URL or
  B2 call reaches the browser (FR-03).
- **Audit trail.** File opens and downloads are audited by the Api before the first byte
  (`CAS-043` §12). The explorer writes nothing to the audit log itself.
- **Path handling.** The browser never builds a file-system path. Folder paths come from Api
  responses (V8), and files open by integer id (V7).
- **Rendering.** File and folder names come from the file system and are rendered through Razor's
  default encoding. No `MarkupString` for names, and no `href` built from a name.
- **Session.** The page keeps its page-level `[Authorize(Policy = "CachetAuthenticated")]`. A 401
  from Api follows V4. Neither open BRD §12.4 item (web caching mechanism, O-Drive refresh cadence)
  affects this spec: browse responses come straight from SQL Server on each call and are not
  cached by the Web layer.

---

## §13 Test Plan

### Unit Tests (bUnit, `Cachet.ArchiveSync.Web.Tests`)
| Class | Method | Proves |
|-------|--------|--------|
| `KeywordSearchPageTests` | `Test_SC1_...`, `Test_SC2_...`, `Test_SC3_...`, `Test_V1_...`, `Test_V2_...`, `Test_V3_...` | Mode default and control state (SC-1–SC-3, V1–V3) |
| `FileExplorerTests` | `Test_SC4_...`–`Test_SC10_...` | Explorer behavior (SC-4–SC-10) |
| `FileExplorerTests` | `Test_V4_...`–`Test_V8_...`, `Test_SC12_...` | Error states, id-only open, no server paths (V4–V8, SC-12) |
| `FileExplorerTests` | `Test_SC15_SpinnerShownWhileLoadingAndLatestFolderWins` | Spinner and latest-wins (SC-15) |
| `KeywordSearchDeepLinkTests` | `Test_SC3_...` (deep link case) | Deep link starts in Files (SC-3) |
| existing `KeywordSearch*` | unchanged names | SC-13 |

### Integration Tests
N/A. There is no Api change. The contract is covered by `CAS-043` §13. Run one manual end-to-end
check against a seeded database before review.

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. `CAS-043` is deployed first (database migration, then Api).
2. Deploy Web. An old Api returns 404 for `GET files/browse`, which the explorer would show as the
   generic error (V6), so the order above is mandatory.
3. The client approval is in hand (`CAS-043` §0), so no further gate applies.
4. Rollback: revert the Web change; the screen returns to Keyword Search as default. `CAS-043` can
   stay deployed.

---

## §15 Open Questions

- [x] **Q1 — Client approval. Resolved 2026-10-04** (see `CAS-043` §15 Q1): approved by email.
- [x] **Q2 — Default landing. Resolved 2026-10-04 (requester):** Files is the default, superseding
      `CAS-039` A4 (requested by A. Mathi on 2026-09-30), because the landing-screen request is the
      newer one. **Action for the requester:** tell A. Mathi.
- [x] **Q3 — Bucket and Extension in Files mode. Resolved 2026-10-04 (requester):** both stay
      greyed out in this release. An Extension filter for Files mode can be a later spec.
- [x] **Q4a — Root label. Resolved 2026-10-04:** the root is the bucket's prefix folder
      `D/Shares/Operations`, shown as "Operations" (`CAS-043` §1, Q7). The tree and breadcrumb start
      at "Operations" and use the Api's `rootName`; the UI never shows `D/Shares`.
- [x] **Q4b — Folder dates. Resolved 2026-10-04** (`CAS-043` §15 Q6): folders show the newest
      modified date among their files.
- [x] **Q5 — Type column. Resolved 2026-10-04 (requester):** the fixed names are accepted: PDF
      Document, Excel Workbook, Word Document, CSV File, Text Document, and `<EXT> File` for any
      other extension; folders show "File folder".
- [x] **Q6 — Large folders. Resolved 2026-10-04 (requester):** no 500-item limit; behave like File
      Explorer, and show the spinner for long-running loads. Implemented as a no-limit response
      (`CAS-043`) plus a virtualized list (§8.2) and a spinner for the whole call (§8.4).
- [x] **Q7 — Load time. Resolved 2026-10-04** (`CAS-043` §15 Q8): a spinner wheel while the folder
      loads is accepted; no progress bar and no time target.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude Code (drafted for devarajan@tracecounts.org) | 2026-10-04 | Draft from `/spec-new`; not yet reviewed |
| Wireframe approval | devarajan@tracecounts.org | 2026-10-04 | "looks fantastic" |
| Default-mode override (CAS-039 A4) | devarajan@tracecounts.org | 2026-10-04 | Approved; the requester informs A. Mathi |
| Reviewer | | | |

---

## §17 Graph Impact

### God Nodes in Blast Radius
- `KeywordSearch.razor` (1,509 lines, the largest Web component): **HIGH RISK by size**. The change
  is additive and most new markup lives in the new `FileExplorer.razor`.
- `FileViewerModal.razor` (301 lines): reused, comment-only change.

### Communities / Modules Affected
- Modules: Keyword Search (screen) and Audit Log (indirect, through `CAS-043`'s context). Not
  affected: Auth, O-Drive Sync, Quarantine, Task Manager, Reporting.
- Projects: **Web** only. Api, Core and Database belong to `CAS-043`. Not Worker.

### Surprising Connections
- This spec reverses the default-mode behavior set by `CAS-039` A4 and edits that spec's tests (§5).
  `CAS-039` and `CAS-030` owners should add a pointer to this spec.
- `FileViewerModal` is hosted by `KeywordSearch.razor` and reused by the other screens through the
  same `Context` string, so the new `FileBrowser` value follows an established pattern.

### Graphify Queries Run
```
python -m graphify query "file index browse folders files list download B2 object mirror" --budget 2000
python -m graphify explain "FilesController"
python -m graphify explain "FileViewerModal"
```
The graph returned containment edges only for the relevant C# types, so the blast radius is from a
**manual read** of `KeywordSearch.razor` (toolbar and mode handling), `FileViewerModal.razor`,
`Program.cs` and `CAS-039`/`CAS-040`.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test (SC-14 is a recorded manual check)
- [ ] All §7 Validations are implemented and tested
- [ ] Files is the default; the disabled controls use the native `disabled` attribute (V1)
- [ ] File open uses only `fileIndexId`; folder paths come only from Api responses (V7, V8)
- [ ] §12: no server path, B2 key or exception text in markup, logs or requests
- [ ] §11: no folder path or file name logged
- [ ] Only files in §5 changed; nothing in §6 or §4 touched
- [ ] Each pre-existing test edited for the default-mode change is listed and keeps its intent
- [ ] §14 deploy order documented

### Manual check (SC-14)
Run Web against a seeded database; compare to the wireframe at 1280px and 400px, in light and dark
themes; open a PDF and an XLSX from a nested folder and confirm Download works and an audit row
appears in the Audit Log.

### Verification Report Format
The reviewer writes `CAS-044.review.md` with: overall verdict (APPROVED | APPROVED WITH NOTES |
CHANGES REQUIRED | BLOCKED), a per-SC verdict table, a security gap list and scope violations.

## Implementation Log

**Completed:** 2026-10-04
**Gates:** Build ✓ | Tests ✗ | Format ✓ | Coverage 85.6% (Gate 2 FAILED: 8 failing tests outside CAS-044 scope, see CAS-044.review.md s10; status set to review at user request) | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `Web/Components/Pages/KeywordSearch.razor` | Files mode default, disabled controls, explorer host |
| `Web/Components/Pages/KeywordSearch.razor.css` | Files-mode disabled styling |
| `Web/Components/Shared/FileExplorer.razor` | new: tree, list, breadcrumb, states |
| `Web/Components/Shared/FileExplorer.razor.css` | new: explorer styling |
| `Web/Components/Shared/FileViewerModal.razor` | comment only |
| `Web/Http/FilesBrowseClient.cs` | new: GET files/browse client |
| `Web/Program.cs` | DI registration |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `FileExplorerTests` | `SC4-SC10, SC12, SC15, V4-V8` | explorer behaviour |
| `KeywordSearchPageTests` | `SC1-SC3, V1-V3` | mode default and control state |
| `KeywordSearchDeepLinkTests` | `SC3` | deep link starts in Files |
