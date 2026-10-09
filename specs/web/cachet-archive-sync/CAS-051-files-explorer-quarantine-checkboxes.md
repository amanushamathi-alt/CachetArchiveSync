---
id: CAS-051
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-05
priority: P1
change_type: enhancement
related_specs: [CAS-050, CAS-052, CAS-044, CAS-048, CAS-043]
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

# CAS-051 — Change: Quarantine checkboxes and folder select-all in the Files explorer (Web)

> Part 2 of 3 of the Files-quarantine change request (Product Owner change, BRD v2.4 §12.5). Depends on
> CAS-050 (API). BRD: **FR-09a** (new), FR-14 (amended), FR-23. Quarantine stays **one row per file**;
> a folder control is a "select all / deselect all" over the files beneath it (CAS-050 revision note).

## §1 Current Behavior

- `FileExplorer.razor` (`Components/Shared/`, 959 lines) renders the Files tree + list. In **Files mode**
  (`ResultsTaskId` null) it has no selection and no quarantine awareness; `ShowSelection` is true only
  in results mode with `Selectable` (CAS-048), where checkboxes select **search_result ids** for the
  batch "Send to Quarantine (n)" button in `KeywordSearch.razor:310-314`.
- `KeywordSearch.razor:299` hosts Files mode: `<FileExplorer @ref="fileExplorer" OnFileOpened="…" />`.
- Full Access is read in components from the `b2_capability` claim (`QuarantineQueueModal.razor:123-152`,
  `ODriveSyncContentScan.razor:461`); not exposed by `FileExplorer`.
- Right-hand list rows: folders and files, columns Name / Date modified / Type / Size. The left tree has
  no checkboxes.

## §2 Desired Behavior

1. **Full Access only.** Read Only users see no checkbox column at all and no "Quarantined" marker (§14
   Q-B); they use the Quarantine panel. Hiding is cosmetic; enforcement is the API (CAS-050 §11).
2. In **Files mode**, rows of the right-hand list show a checkbox:
   - **File row:** checked = that file is quarantined (its id is in `fileIndexIds` from the state
     endpoint).
   - **Folder row:** a tri-state checkbox from `totalFiles` / `quarantinedFiles` — **checked** = all files
     under the folder (all depths) are quarantined, **partial** (indeterminate) = some, **unchecked** =
     none. A folder with `totalFiles = 0` shows no checkbox. The left tree gets no checkboxes (§14 Q-A).
3. **File toggle** calls `POST /quarantine/browse` (`scope=File`) to check and
   `POST /quarantine/browse/remove` to uncheck. Immediate, no "Send" button — pessimistic: the checkbox is
   disabled while the call is in flight and reverts with an inline error if it fails.
4. **Folder checkbox = "Select all" / "Deselect all"**, always behind a confirmation dialog (reuse the Web
   project's existing confirm-dialog pattern — read the nearest one first, P1). Counts come from the state
   response, so no extra call is needed:
   - unchecked → "Quarantine **N** files in _folder_ and its sub-folders?" where
     N = `totalFiles − quarantinedFiles`; already-quarantined files are skipped. Confirm →
     `POST /quarantine/browse` (`scope=Folder`).
   - checked → "Remove **M** files in _folder_ from Quarantine, **including files quarantined from
     Keyword Search**?" where M = `quarantinedFiles`. Confirm → `POST /quarantine/browse/remove`
     (`scope=Folder`).
   - **partial** → one dialog offering both actions (Select all / Deselect all) with the counts above.
   - The dialog notes: "Files added to this folder later are not included." There is no size limit and no
     maximum count (BRD §12.5 item 5).
5. After a successful call, show the result ("N files quarantined, K already quarantined" /
   "M files removed"), re-read the listing's quarantine state, and notify the host so the Quarantine panel
   and badge refresh (`OnQuarantineChanged`). Never show a success message before the API 200.
6. No header "select all" checkbox in Files mode (avoids bulk earmarking by accident); bulk action exists
   only per folder, with confirmation.
7. Keyboard/a11y: file checkbox `aria-label="Quarantine <name>"`; folder checkbox
   `aria-label="Select or deselect all files in <name>"` with `aria-checked="mixed"` for partial; Space
   opens the dialog; focus returns to the checkbox after the dialog closes.
8. Results mode (CAS-048) behaves as before **except** the permission gate: because
   `POST /search/results/quarantine` now requires Full Access (CAS-050 §2.8), the "Send to Quarantine (n)"
   button (`KeywordSearch.razor` ~line 314) and the results-mode selection checkboxes are hidden/disabled
   for Read Only users. Everything else in results mode is untouched.

## §3 Reason for Change

- **Type:** enhancement
- **Trigger:** Product Owner usability change (2026-10-05), BRD v2.4 §12.5.
- **Risk if not changed:** quarantine remains reachable only via search tasks.

## §4 Blast Radius (Graph Impact)

_Mapped by manual grep/read (graph report has no node-level data used)._

### Primary Change Target

`Components/Shared/FileExplorer.razor` (+ `.razor.css`), new `Http/QuarantineBrowseClient.cs`.

### Direct Dependents

`KeywordSearch.razor` (hosts it twice: Files mode and results mode), `FileExplorerTests`,
`BrowseFixtures`, `KeywordSearchPageTests`.

### Downstream Modules

Keyword Search (Files mode), Quarantine panel/badge (refresh only).

### Cross-Project Risk

**CROSS-PROJECT RISK:** consumes the CAS-050 endpoints; cannot ship before CAS-050.

### God Node Involvement

`FileExplorer.razor` is large (959 lines) and shared by two modes → **HIGH RISK**. All new behaviour is
gated on `!ResultsMode && quarantineEnabled`; results-mode code paths must not change (SC-9).

### Graphify Queries Run

graphify not queried at node level — manual code read.

## §5 Files to Change

| File                                                                  | Change Type | Description                                                                                                                                        |
| --------------------------------------------------------------------- | ----------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/Cachet.ArchiveSync.Web/Components/Shared/FileExplorer.razor`     | modify      | Files-mode checkbox column, state load, file toggle + folder dialog handlers, result message, `OnQuarantineChanged`                                |
| `src/Cachet.ArchiveSync.Web/Components/Shared/FileExplorer.razor.css` | modify      | Checkbox column / tri-state styling (match `.fe-row` fixed height — `Virtualize`)                                                                  |
| `src/Cachet.ArchiveSync.Web/Http/QuarantineBrowseClient.cs`           | add         | Typed client for the 3 new endpoints (mirror `SearchResultsBrowseClient.cs`)                                                                       |
| Existing confirm dialog, else a new small one                         | reuse/add   | Per §2.4 — add only if no existing dialog fits                                                                                                     |
| `src/Cachet.ArchiveSync.Web/Program.cs`                               | modify      | Register client                                                                                                                                    |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor`     | modify      | Wire `OnQuarantineChanged="LoadQuarantineAsync"` on the Files-mode instance only; gate the results-mode "Send to Quarantine" button on Full Access |
| `src/Cachet.ArchiveSync.Web.Tests/FileExplorer/*`, `KeywordSearch/*`  | modify/add  | Tests per §13, fake API handler additions                                                                                                          |

## §6 Files NOT to Change

| File                                                                                                  | Why left untouched                                                                                                         |
| ----------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| `FileExplorer.razor` results-mode code (`ShowSelection`, `ApplySelectionAsync`, `ResolveFolderAsync`) | Results-mode behaviour unchanged; the Read Only gate is applied in `KeywordSearch.razor` (pass `Selectable` = Full Access) |
| `QuarantineQueueModal.razor`                                                                          | CAS-052                                                                                                                    |
| `Http/SearchResultsBrowseClient.cs`, `FilesController` browse contract                                | No change to browse                                                                                                        |

## §7 Database Changes

N/A.

## §8 API Contract Changes

N/A (consumes CAS-050 §8).

## §9 Validations Changed

| ID  | Rule (Before) | Rule (After)                                                                                 | Test                                   |
| --- | ------------- | -------------------------------------------------------------------------------------------- | -------------------------------------- |
| V1  | n/a           | Checkbox column rendered only when `b2_capability == "Full Access"` and Files mode           | `Test_V1_ReadOnly_NoCheckboxes`        |
| V2  | n/a           | One in-flight call per row; control disabled meanwhile                                       | `Test_V2_Toggle_DisablesWhileInFlight` |
| V3  | n/a           | A folder action never calls the API without the confirmation dialog; cancel = no call        | `Test_V3_FolderAction_RequiresConfirm` |
| V4  | n/a           | API error ⇒ checkbox reverts, generic inline message, no state change                        | `Test_V4_ApiError_Reverts`             |
| V5  | n/a           | Folder checkbox state derives from `quarantinedFiles` vs `totalFiles` (none / partial / all) | `Test_V5_FolderTriState`               |

## §10 Success Criteria

_Proposed — for review._

| ID    | Criterion                                                                                                                                                                              | Test                                                                                          |
| ----- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| SC-1  | Full Access: checking a file calls add (`scope=File`) with its `file_index_id`; row stays checked; panel/badge refresh                                                                 | `Test_SC1_CheckFile`                                                                          |
| SC-2  | Folder with none quarantined: click → dialog shows N = `totalFiles`; confirm calls add (`scope=Folder`, source/bucket/folderPath); on 200 shows the result and the row becomes checked | `Test_SC2_FolderSelectAll`                                                                    |
| SC-3  | Folder fully quarantined: dialog shows M = `quarantinedFiles` and the Keyword Search wording; confirm calls remove (`scope=Folder`)                                                    | `Test_SC3_FolderDeselectAll`                                                                  |
| SC-4  | Partial folder shows the indeterminate state and a dialog with both actions                                                                                                            | `Test_SC4_PartialFolder_BothActions`                                                          |
| SC-5  | Read Only: no checkbox column in DOM                                                                                                                                                   | `Test_SC5_ReadOnly`                                                                           |
| SC-6  | Quarantine state is re-read on every folder navigation (tree click, list double-click, "up") and after each successful action                                                          | `Test_SC6_StateReloaded`                                                                      |
| SC-7  | Filter-this-folder keeps checkbox state correct for filtered rows                                                                                                                      | `Test_SC7_Filter_KeepsState`                                                                  |
| SC-8  | Virtualised list: scrolled-in rows render correct state                                                                                                                                | `Test_SC8_Virtualize_State`                                                                   |
| SC-9  | Results mode renders/behaves exactly as before for Full Access; for Read Only the "Send to Quarantine" button and results selection are hidden/disabled                                | existing `FileExplorerTests` stay green (Full Access); `Test_SC9_ResultsMode_ReadOnly_NoSend` |
| SC-10 | Items quarantined here are visible in the Quarantine modal and Keyword Search panel after the action                                                                                   | `Test_SC10_PanelRefreshed` (with CAS-052)                                                     |

## §11 Security Notes

Touches no B2 key or session token; the claim is read server-side per circuit. UI gating is not a
security boundary (CAS-050 enforces `CachetFullAccess`). Error messages are generic (no paths/SQL).
Audit is written server-side before success (FR-23) — the UI must not claim success before the 200.
Folder deselect also removes Keyword Search-origin rows (Product Owner decision); the confirmation text
must say so and show the count — that is the user's chance to stop a bulk removal.

## §12 Rollback Plan

1. Revert the Web build (feature is additive; API endpoints remain unused).
2. Database: N/A. 3. Config: N/A. 4. Task-queue: N/A.

## §13 Test Plan

| Class                                 | Method                                           | Proves SC / V |
| ------------------------------------- | ------------------------------------------------ | ------------- |
| `FileExplorerQuarantineTests` (bUnit) | V1–V5, SC-1–SC-8 using extended fake API handler | all           |
| `FileExplorerTests`                   | unchanged                                        | SC-9          |
| `KeywordSearchPageTests`              | panel refresh callback                           | SC-10         |

Coverage target: ≥ 85% on changed files.

## §14 Open Questions

Resolved by the Product Owner (2026-10-05): folder actions are per-file select/deselect-all with
confirmation, snapshot semantics, deselect also removes Keyword Search rows, no size limit.

- [x] **Q-A** Checkboxes on tree nodes as well? **Resolved 2026-10-05: list rows only.** The tree and list
      show the same folders; one entry point keeps the confirm-always rule (V3) and SC-6/7/8 provable, and
      avoids tree-node count loading in a HIGH RISK component. Tree checkboxes may come later as a
      separate change spec.
- [x] **Q-B** Show a read-only "Quarantined" marker for Read Only users? **Resolved 2026-10-05: no marker.**
      Read Only users see no checkboxes and use the Quarantine panel. A marker would need a Read
      Only-allowed state endpoint (CAS-050 gates on Full Access) and has no FR-09a basis; follow-up if wanted.

## §15 Sign-off

| Role          | Name                   | Date       | Notes                                       |
| ------------- | ---------------------- | ---------- | ------------------------------------------- |
| Author        | Senior Systems Analyst | 2026-10-05 | draft, revised for per-file design          |
| Product Owner | Rakesh Devarajan       | 2026-10-05 | design decisions recorded in BRD v2.4 §12.5 |
| Reviewer      |                        |            |                                             |

## §16 Verification Plan

### What the Reviewer Will Check

- [ ] §1 matches code; §2 implemented; only §5 files changed
- [ ] Results-mode paths untouched; all §10 SCs have passing tests
- [ ] No B2 key / token in any browser-visible payload (FR-03)
- [ ] UI never shows "quarantined" before the API 200; folder actions always confirm (V3)
- [ ] Accessibility items in §2.7 present

### Verification Report Format

`CAS-051.review.md` — Verdict, per-SC table, scope violations, blast radius, security gaps.

## Implementation Log

**Completed:** 2026-10-05
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage FileExplorer 90.6%, KeywordSearch 94.5%, QuarantineBrowseClient 100% (test-writer run) | Endpoints —

### Files Changed

| File                                           | Change                                          |
| ---------------------------------------------- | ----------------------------------------------- |
| `Web/Http/QuarantineBrowseClient.cs`           | added                                           |
| `Web/Program.cs`                               | register client                                 |
| `Web/Components/Shared/FileExplorer.razor`     | Files-mode quarantine checkboxes, folder dialog |
| `Web/Components/Shared/FileExplorer.razor.css` | error/note styles                               |
| `Web/Components/Pages/KeywordSearch.razor`     | OnQuarantineChanged wiring, Full Access gate    |

### Tests Added

| Class                         | Method                                 | Covers            |
| ----------------------------- | -------------------------------------- | ----------------- |
| `FileExplorerQuarantineTests` | `Test_V1..V5, Test_SC1..SC8`           | V1-V5, SC-1..SC-8 |
| `KeywordSearchPageTests`      | `Test_SC9_ResultsMode_ReadOnly_NoSend` | SC-9              |
| `KeywordSearchPageTests`      | `Test_SC10_PanelRefreshed`             | SC-10             |
