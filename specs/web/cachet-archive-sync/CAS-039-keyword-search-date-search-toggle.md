---
id: CAS-039
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-30
priority: P2
change_type: enhancement
related_specs: [CAS-022, CAS-030, CAS-038]
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

# CAS-039 — Change: Keyword Search Screen "Keyword Search / Date Search" Toggle with From/To Dates (MM/dd/yyyy)

UI half of the Date Search request (2026-09-30, A. Mathi). **Depends on `CAS-038`** (Api/Core/Worker/DB)
being merged first. Changes `KeywordSearch.razor` (`CAS-022`, as extended by `CAS-024`/`CAS-030`–`CAS-032`)
only. Spec-ID note: this spec was renumbered from `CAS-035` before any code was written (see the
`CAS-038` header).

---

## §1 Current Behavior

`src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor`:
- The toolbar (lines 25–80) holds these controls: search term input `#ks-term`, Bucket (disabled
  single option), Extension select, Search button, and then Task Manager / Quarantine Queue / Audit Log.
- The Search button is disabled when `!SearchTermValid || isSearchInFlight || bucketName` is empty
  (line 55).
- `HandleSearchAsync` (line 704) posts `SearchRequest { Term, Bucket, Extension }` to `POST /search`.
- `ApplyReloadedContext` (line 555) restores term and extension from `TaskSearchParameters`. The
  context header (line 131) reads *Showing results for "term" (ext), submitted … ago.*
- No search-mode toggle exists, and there are no date controls.

## §2 Desired Behavior

Per the requester's mockup (2026-09-30):

1. **Mode toggle:** A radio group sits directly above the search input. It has two options,
   **Keyword Search** and **Date Search**, with `name="ks-mode"`. **Keyword Search is selected by
   default** on every fresh load.
2. **Keyword Search mode:** The screen renders and behaves exactly as today. The date controls are not
   rendered, and the `POST /search` body carries `fromDate: null, toDate: null`.
3. **Date Search mode:** The term input stays visible and required, because the keyword is narrowed by
   the dates (`CAS-038` §3). A **From date** field and a **To date** field appear below the search
   input, each with a label above it.
   - Each field is a text input with placeholder `MM/dd/yyyy` and `inputmode="numeric"`.
   - Input is parsed with `DateOnly.TryParseExact(value, "MM/dd/yyyy", CultureInfo.InvariantCulture, …)`.
   - Parsed dates are sent as `SearchRequest.FromDate` / `ToDate` (`CAS-038` §2).
   - **Calendar picker (Amendment A1, 2026-09-30):** Each field has a calendar icon button
     (`aria-label` "Choose From date" / "Choose To date") to the right of the text input.
     - Clicking it opens the browser's native date picker. The picker is a hidden `<input type="date">`,
       opened by a delegated, browser-side click listener in `js/datepicker.js` that calls
       `showPicker()`. The button's `data-cachet-date-picker-for` attribute names the input. No inline
       script is used and no SignalR round trip is involved.
     - The picker opens on the field's current value, if valid, and its `max` is today (UTC).
     - When a date is picked, the text field receives the value formatted as `MM/dd/yyyy`
       (InvariantCulture). The native picker's value is always ISO `yyyy-MM-dd`, whatever the browser
       locale, so the field always shows MM/dd/yyyy.
     - Typing a date directly stays supported. V6–V9 apply identically to typed and picked values.
     - If the browser lacks `showPicker()`, the JS helper falls back to focusing and clicking the date
       input, and typing still works.
4. **Switching back to Keyword Search** clears both date fields and their hints, so a hidden stale date
   is never sent.
5. **Search button (Date mode):** Disabled unless every existing condition holds **and** both dates
   parse, From ≤ To, and neither is after today. An inline hint names the failing rule (§9).
6. **Reload / deep link (`CAS-030` §8.2):**
   - When the reloaded task's `searchParameters.fromDate` / `toDate` are non-null, the screen selects
     Date Search, fills both fields in `MM/dd/yyyy`, and the context header adds
     *, created MM/dd/yyyy – MM/dd/yyyy* after the extension.
   - When both are null, the screen stays in Keyword Search and the header text is unchanged.
6a. **Amendment A2 (2026-09-30): date-only search.** The keyword is **optional in Date mode**. This
   supersedes the earlier "term stays required" rule in point 3, and depends on `CAS-038` A2.
   - Date mode's Search condition becomes: (term empty **or** term valid per V1) **and** V6–V9
     **and** V10. Keyword mode is unchanged, so the term is still required there.
   - In Date mode the term input's placeholder reads **"Keyword (optional) - filename or path…"**.
     Keyword mode keeps the existing placeholder.
   - An empty term is posted as `Term = ""` with both dates.
   - **V10:** When the term is empty and both dates are valid, the inclusive range must be ≤ 31 days.
     Otherwise Search is disabled and the hint "Without a keyword, the date range can be at most 31
     days." appears.
   - Completed date-only search:
     - The Filename panel lists the returned rows. Their status chip is `Informational` and uses the
       existing chip mapping.
     - Zero rows show the copy "No files created in this date range.".
     - The Content panel shows "Content matching needs a keyword." and no table.
   - Context header for a reloaded date-only task:
     *Showing results for all files (ext), created MM/dd/yyyy – MM/dd/yyyy, submitted … ago.*
     A task with a term keeps the header format from point 6.
6b. **Amendment A3 (2026-09-30): separate, exclusive modes.** Requester: "if I select Date then only
   date, if I select Keyword then only keyword; clear both on radio button select". Decision the same
   day (A. Mathi): **hide** the keyword box in Date mode. A3 **supersedes** the following:
   - point 3's "term input stays visible";
   - A2's "keyword optional" and the "Keyword (optional)…" placeholder (6a);
   - SC-2's "term stays visible" and A2's V1 note.

   A3 behavior:
   - **Selecting Date Search** clears the keyword (`searchTerm = ""`) and hides the term input. The
     `#ks-term` element and its V1 hint are not rendered. Only From/To (with the A1 calendar
     buttons), Bucket and Extension remain.
   - **Selecting Keyword Search** clears both dates, as today (point 4). The term input is shown
     empty.
   - **Date mode Search** requires the V6–V9 date rules plus V10. Because the term is always empty in
     Date mode, the 31-day cap always applies there. The post carries `Term = ""` and both dates.
   - **Keyword mode:** unchanged. The term is required and dates are always null.
   - **Reload (point 6):** a task with both dates opens in Date mode with the dates filled and no term
     input.
     - A date-only task shows the 6a "all files" header.
     - A legacy keyword + date task, created before A3, keeps its existing header, including its
       term, so the displayed results remain correctly described.
   - **Out of scope:** the backend (`CAS-038`) still accepts keyword + date requests. No Api change
     is needed, and none is made.
6c. **Amendment A4 (2026-09-30): defaults on open, and toolbar alignment.** Requester, from a
   screenshot after sign-in: "keyword search wants to by default select after logged in and want to
   clear last using keyword from textbox; alignment ... moved up/down, please fix".
   - **Filter bar resets on every open.** Whenever the screen opens (after sign-in, on navigation, on
     a reload, or through a `?taskId=` deep link), the filter bar shows its defaults: **Keyword Search
     selected**, keyword box **empty**, both dates empty, Extension **All**.
     - The reloaded task's parameters are **no longer copied into the filter bar**.
     - This **supersedes, for this screen only, `CAS-030` §8.2 step 3's "set the filter bar's search
       term and extension from `searchParameters`"**, and CAS-039 §2 point 6 / A3's "reload selects
       Date mode and fills the dates".
     - Everything else in `CAS-030` §8.2 is kept:
       - the reloaded task's **results still fill the panels**;
       - the **context header** ("Showing results for …", including the A2 "all files" and the
         date-range suffix) still describes which search the results belong to, which satisfies
         CAS-030's "a user must never have to guess" rationale;
       - polling of an in-progress task, the failure banner and the acknowledge are unchanged.
     - The date-only Content notice for a reloaded date-only task still follows the **task's**
       parameters (`lastSearchWasDateOnly`), not the filter bar.
     - The `ODriveSyncContentScan` screen is not affected.
   - **Toolbar alignment:** in both modes, the term input, the From/To date inputs (with their
     calendar buttons), the Bucket and Extension selects and the Search button sit on **one shared
     bottom line**, whatever labels or radio rows are stacked above them. The right-hand buttons (Task
     Manager, Quarantine Queue, Audit Log) align on the same line. Nothing shifts vertically when
     switching modes, beyond the date fields appearing or disappearing. Layout only; no behavior
     change.
7. **Everything else is unchanged:** Bucket, Extension, polling, the result panels and their columns,
   Send to Quarantine, the Quarantine panel, modals, banners, and empty-state copy.

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** User request, 2026-09-30 (A. Mathi), with the mockup attached to the request. Clarified
  the same day: keyword stays required, the filter field is `created_at`, both dates are required, and
  From ≤ To. **(Superseded:** "keyword stays required" was replaced by A2, which made the keyword
  optional in Date mode, and then by A3, which made Date mode date-only with no keyword box. See
  §2 points 6a and 6b.**)**
- **Risk if not changed:** The `CAS-038` backend capability has no UI entry point.

## §4 Blast Radius (Graph Impact)

The blast radius was mapped by manual read of `KeywordSearch.razor`, its scoped CSS, and
`Cachet.ArchiveSync.Web.Tests/KeywordSearch/*`. Graphify was not relied on for code edges.

### Primary Change Target
File: `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor`. Entity: the `KeywordSearch`
component's filter bar, submit path, and reload-context path.

### Direct Dependents
- `KeywordSearchPageTests`
- `KeywordSearchDeepLinkTests`
- `FakeKeywordSearchApiHandler`, which may need to capture the posted body for the date assertions.

No other component embeds `KeywordSearch`, since it is a routable page at `/`.

### Downstream Modules
Keyword Search only. Not affected: `TaskManagerBadge`, `QuarantineQueueModal`, `ReportModal` and
`AuditLogModal`. They are rendered by the page but receive no new parameters.

### Cross-Project Risk
Web consumes Core's `SearchRequest` and `TaskSearchParameters`, whose new fields come from `CAS-038`.
**Deploy order: CAS-038 (DB → Api → Worker) before CAS-039 (Web).** An old Api would ignore the date
fields and run an unfiltered search that looks correct. That silent-wrong-result risk is why the
sequencing in §12 is mandatory.

### God Node Involvement
`KeywordSearch.razor` is the largest Web component (about 1,000 lines, more than 10 edges), so it is
**HIGH RISK by size**. Mitigations:
- The change is additive.
- Every existing `KeywordSearchPageTests` / `KeywordSearchDeepLinkTests` test must pass unchanged,
  apart from new body-capture helpers.

### Graphify Queries Run
Manual code read. Graphify was not relied on for code edges.

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | modify | Radio toggle; conditional From/To inputs; date parsing and validation; `FromDate` / `ToDate` in `SearchRequest`; reload restores mode and dates; context-header suffix. |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor.css` | modify | Layout for the radio row and the two date fields (matches existing `cachet-*` tokens). |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchPageTests.cs` | modify | New tests (§13). |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchDeepLinkTests.cs` | modify | SC-7 reload test. |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/FakeKeywordSearchApiHandler.cs` | modify (if needed) | Capture the last `POST /search` body. |
| `src/Cachet.ArchiveSync.Web/wwwroot/js/datepicker.js` | add (A1) | `window.cachetDatePicker.open(element)`: calls `showPicker()`, falling back to focus+click. Same global-namespace pattern as `js/download.js` (`cachetDownload`). |
| `src/Cachet.ArchiveSync.Web/Components/App.razor` | modify (A1) | One `<script src="js/datepicker.js"></script>` tag, placed after the existing `js/download.js` tag. |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `src/Cachet.ArchiveSync.Api/**`, `Core/**`, `Worker/**`, `Database/**` | Owned by `CAS-038`. |
| `ODriveSyncContentScan.razor` | Separate tab; the request is scoped to Keyword Search. |
| `TaskManagerBadge`, `QuarantineQueueModal`, `ReportModal`, `AuditLogModal` | Rendered unchanged. |
| `wwwroot/app.css` | Scoped CSS is sufficient; no global token change. |

## §7 Database Changes

N/A.

## §8 API Contract Changes

N/A: this spec consumes `CAS-038` §8 as defined there.

## §9 Validations Changed

Client-side mirrors of `CAS-038` V11–V13. The server stays the source of truth (`CAS-022` §7
convention).

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V6 | n/a | In Date mode, each field must parse exactly as `MM/dd/yyyy` (e.g. `09/30/2026`). Hint: "Enter a date as MM/dd/yyyy." | `Test_V6_DateModeRejectsNonMmDdYyyyInput` |
| V7 | n/a | In Date mode, both dates are required. The Search button stays disabled until both are valid. | `Test_V7_DateModeSearchDisabledUntilBothDatesValid` |
| V8 | n/a | From must be on or before To. Hint: "From date must be on or before To date." | `Test_V8_FromAfterToDisablesSearchWithHint` |
| V9 | n/a | Neither date may be after today (UTC). Hint: "Dates cannot be in the future." | `Test_V9_FutureDateDisablesSearchWithHint` |
| V1–V5 | unchanged | V1 is unchanged in Keyword mode. **A2:** in Date mode an empty term is allowed, and a non-empty term must still be 1–200 characters. V2–V5 are unchanged. | existing `CAS-022` tests + `Test_A2_V1_DateModeAllowsEmptyTerm` |
| V10 (A2) | n/a | Date mode with an empty term: the inclusive range `To − From + 1` must be ≤ 31 days. Hint: "Without a keyword, the date range can be at most 31 days." Search is disabled. A keyword + date search has no limit. | `Test_V10_DateOnlyRangeOver31DaysDisablesSearchWithHint` |

V-IDs continue from `CAS-022`'s V1–V5.

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | On first render, **Keyword Search** is checked, **Date Search** is unchecked, and no From/To inputs are in the markup. | `Test_SC1_DefaultsToKeywordModeWithNoDateInputs` |
| SC-2 | Selecting **Date Search** renders the "From date" and "To date" labelled inputs, each with placeholder `MM/dd/yyyy`. **(Amended by A3: the search term input is not rendered in Date mode.)** | `Test_SC2_DateModeShowsFromAndToInputsAndKeepsTerm` |
| SC-3 | In Keyword mode, Search posts `SearchRequest` with `FromDate == null && ToDate == null`. Term, bucket and extension are unchanged from today. | `Test_SC3_KeywordModePostsNullDates` |
| SC-4 | In Date mode with `09/01/2026` and `09/30/2026`, Search posts `FromDate = 2026-09-01` and `ToDate = 2026-09-30`. | `Test_SC4_DateModePostsParsedDates` |
| SC-5 | Switching Date → Keyword clears both fields. A subsequent Search posts null dates. | `Test_SC5_SwitchingBackToKeywordClearsDates` |
| SC-6 (amended A3) | Polling, panels, Send to Quarantine and empty states behave identically in both modes. The one exception: every Date-mode search is date-only under A3, so the Content panel shows the A2 notice "Content matching needs a keyword." instead of content rows. All pre-existing `KeywordSearchPageTests` pass unmodified. | existing suite + `Test_SC6_DateModeResultsFlowIdenticalToKeywordMode` |
| SC-7 | A reloaded task with `searchParameters.fromDate` / `toDate` selects Date mode, shows both dates as `MM/dd/yyyy`, and the context header includes "created 09/01/2026 – 09/30/2026". A task without dates stays in Keyword mode with the unchanged header. | `Test_SC7_ReloadedDateTaskRestoresModeDatesAndHeader` |
| SC-14 (A4) | Opening the screen when the user's latest task is a Completed **date-only** task, a Completed **keyword** task, or a Completed **keyword + date** task: Keyword Search is checked, `#ks-term` is rendered with an empty value, no date inputs are shown, and Extension is `All`. The panels still show that task's results, the context header still describes that task, and a date-only task still shows the Content notice. The same applies to a `?taskId=` deep link. | `Test_A4_SC14_OpenAlwaysResetsFilterBarButKeepsReloadedResultsAndHeader` |
| SC-15 (A4) | Toolbar alignment. The term, date inputs, Bucket, Extension and Search controls share one bottom alignment in both modes: the toolbar container's scoped CSS uses bottom (`flex-end`) cross-axis alignment. **Manual visual check**, recorded in §16; there is no automated layout test. | manual (§16) |
| SC-12 (A3) | Selecting Date Search hides `#ks-term` (not rendered) and clears the typed keyword. Switching back to Keyword Search shows `#ks-term` empty and both dates cleared. Date mode Search posts `Term = ""` with both dates. Keyword mode posts null dates. | `Test_A3_SC12_ModesAreExclusiveAndClearEachOther` |
| SC-13 (A3) | Reloading a legacy keyword + date task selects Date mode, fills both dates, renders no `#ks-term`, and keeps the header with the term ("Showing results for "<term>" (ext), created …"). | `Test_A3_SC13_ReloadedKeywordPlusDateTaskOpensDateModeWithTermHeader` |
| SC-9 (A2, amended A3) | In Date mode with 01/01/2025–01/31/2025, Search is enabled and posts `Term = ""`, `FromDate = 2025-01-01`, `ToDate = 2025-01-31`. (A3: there is no term input in Date mode, so the "Keyword (optional)" placeholder no longer applies.) | `Test_A2_SC9_DateOnlySearchPostsEmptyTermWithDates` |
| SC-10 (A2) | A completed date-only task shows its rows in Filename Matches with the `Informational` chip. Zero rows show "No files created in this date range.". The Content panel shows "Content matching needs a keyword." | `Test_A2_SC10_DateOnlyResultsRenderInformationalAndContentNotice` |
| SC-11 (A2) | A reloaded date-only task (term `""`, both dates) selects Date mode, leaves the term empty, and renders the header "Showing results for all files (ext), created MM/dd/yyyy – MM/dd/yyyy, submitted …". | `Test_A2_SC11_ReloadedDateOnlyTaskHeader` |
| SC-8 (A1) | In Date mode each field has a calendar button (aria-label "Choose From date" / "Choose To date") carrying `data-cachet-date-picker-for` = the id of that field's hidden `type=date` input, whose `max` is today (UTC). Picking ISO `2025-01-15` sets the text field to `01/15/2025`. Keyword mode renders no calendar buttons. (The browser-side open is a delegated click listener in `js/datepicker.js`, so it runs within the user's click with no SignalR round trip that could expire `showPicker()`'s user-activation requirement. bUnit cannot execute it, so it is verified manually per §16.) | `Test_SC8_CalendarButtonOpensPickerAndWritesMmDdYyyy` |

## §11 Security Notes

No security impact:
- No credential, session or B2 surface is involved.
- Dates are user-entered, non-sensitive values, and nothing new is logged. The existing rule stays: the
  term is never logged (`CAS-022` §11).
- The screen still relies on the page-level `[Authorize(Policy = "CachetAuthenticated")]`.

## §12 Rollback Plan

1. Revert `KeywordSearch.razor` and `KeywordSearch.razor.css`. For A1, also delete
   `wwwroot/js/datepicker.js` and remove its `<script src="js/datepicker.js">` tag from
   `Components/App.razor`. Then redeploy Web. The screen returns to keyword-only.
   - To roll back only A3 (keep Date Search, restore the visible optional keyword), revert
     `KeywordSearch.razor` to its A2 state.
   - To roll back only A1, remove the calendar buttons, `datepicker.js` and the script tag; typing
     dates keeps working.
2. **Deploy sequencing:** Deploy only after `CAS-038` is live on Api and Worker (§4 Cross-Project Risk).
   Rolling back `CAS-038` requires rolling back this spec **first**.
3. Database, config and task queue: N/A.

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `KeywordSearchPageTests` (bUnit) | `Test_SC1_…`, `Test_SC2_…`, `Test_V6_…`–`Test_V9_…` | SC-1, SC-2, V6–V9 |
| `KeywordSearchPageTests` (bUnit, `FakeKeywordSearchApiHandler` body capture) | `Test_SC3_…`, `Test_SC4_…`, `Test_SC5_…`, `Test_SC6_…` | SC-3–SC-6 |
| `KeywordSearchDeepLinkTests` (bUnit) | `Test_SC7_…` | SC-7 |
| `KeywordSearchPageTests` (bUnit) | `Test_SC8_…` | SC-8 (A1); plus a manual browser check that the calendar opens in Chrome/Edge and that picking a date fills MM/dd/yyyy |

Tests pin "today" with a fixed clock where V9 depends on it. If the component has no clock seam,
V9 tests use a date far in the future (e.g. `12/31/2099`) so they need no clock injection (P4).

Coverage target: ≥ 85% on changed files.

## §14 Open Questions

- [x] **Input control — resolved 2026-09-30 (A. Mathi): a calendar picker is required** ("date picker
      icon missing to select date"). The approach is Amendment A1 (§2.3): keep the MM/dd/yyyy text
      input and add a calendar button that opens the native picker. Picked dates are written back as
      MM/dd/yyyy, so the format guarantee holds. Original analysis, kept for the audit trail:
      A text input with a `MM/dd/yyyy` placeholder and exact parsing guarantees the
      requested format. The alternative, a native `<input type="date">`, offers a calendar popup but
      shows the **browser locale's** format, so MM/dd/yyyy is not guaranteed. Precedent: `AuditLogModal.razor`
      (`CAS-031`) uses `<input type="date">` with UTC days; this screen differs because of the explicit
      MM/dd/yyyy requirement. **Default applied: text
      input.** Confirm whether a calendar popup is required.
- [ ] **Created vs Modified (from CAS-038 §14):** The panels show *Modified*, while the filter uses
      *created_at* (indexed date). Should a hint read "Filters by date indexed"? **Default applied: no
      extra copy**, because the request says "others are same".
- [ ] **Mode persistence:** The mode resets to Keyword on every fresh load unless a reloaded task
      carries dates (§2.6). Nothing is stored in the browser.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | A. Mathi, via Claude Code | 2026-09-30 | Drafted from `/spec-change`; depends on CAS-038 |
| Approval to implement | A. Mathi | 2026-09-30 | `/implement CAS-039` invoked after the §14 defaults were presented. Accepted as applied: text input with MM/dd/yyyy, no extra "date indexed" hint, mode resets to Keyword on fresh load. CAS-038 is at `review`. |
| Amendment A1 approval | A. Mathi | 2026-09-30 | Requested a calendar picker ("date picker icon missing to select date"). |
| Amendment A2 approval | A. Mathi | 2026-09-30 | Requested date-only search. Chose: keyword optional, Informational status, 31-day cap. |
| Amendment A3 approval | A. Mathi | 2026-09-30 | Requested exclusive modes ("clear both on radio button select"). Chose: hide the keyword box in Date mode. |
| Reviewer | | | |

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches the code BEFORE the change
- [ ] Desired behavior (§2) is fully implemented; Keyword mode is visually and behaviorally unchanged
- [ ] Only files in §5 were modified (no Api/Core/Worker/DB edits)
- [ ] All §10 Success Criteria have passing tests; the pre-existing suites pass unmodified
- [ ] All §9 Validations are implemented and tested
- [ ] Dates are displayed and parsed strictly as `MM/dd/yyyy` (InvariantCulture); sent as ISO `DateOnly`
- [ ] Hidden date values are never posted in Keyword mode (SC-3, SC-5)
- [ ] §12 deploy sequencing is documented

### Verification Report Format
`CAS-039.review.md`, following `specs/_templates/review-report.template.md`.

## Implementation Log

**Completed:** 2026-09-30
**Gates:** Build ✓ | Tests ✗ | Format ✓ | Coverage 94.3% | Endpoints —

### Files Changed
[{file:src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor,change:Modified - ks-mode radio toggle; From/To MM/dd/yyyy inputs (Date mode only); V6-V9 and CanSearch; FromDate/ToDate in SearchRequest; reload restores mode/dates plus header suffix},{file:src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor.css,change:Modified - radio row, stacked search group, wrapping date row}]

### Tests Added
[{class:KeywordSearchPageTests,method:Test_SC1_DefaultsToKeywordModeWithNoDateInputs,covers:SC-1},{class:KeywordSearchPageTests,method:Test_SC2_DateModeShowsFromAndToInputsAndKeepsTerm,covers:SC-2},{class:KeywordSearchPageTests,method:Test_SC3_KeywordModePostsNullDates,covers:SC-3},{class:KeywordSearchPageTests,method:Test_SC4_DateModePostsParsedDates,covers:SC-4},{class:KeywordSearchPageTests,method:Test_SC5_SwitchingBackToKeywordClearsDates,covers:SC-5},{class:KeywordSearchPageTests,method:Test_SC6_DateModeResultsFlowIdenticalToKeywordMode,covers:SC-6},{class:KeywordSearchDeepLinkTests,method:Test_SC7_ReloadedDateTaskRestoresModeDatesAndHeader,covers:SC-7},{class:KeywordSearchPageTests,method:Test_V6_DateModeRejectsNonMmDdYyyyInput,covers:V6},{class:KeywordSearchPageTests,method:Test_V7_DateModeSearchDisabledUntilBothDatesValid,covers:V7},{class:KeywordSearchPageTests,method:Test_V8_FromAfterToDisablesSearchWithHint,covers:V8},{class:KeywordSearchPageTests,method:Test_V9_FutureDateDisablesSearchWithHint,covers:V9}]

### Gate 2 Waiver (2026-09-30)
Same known, out-of-scope failures as the CAS-038 waiver (`CAS-038-keyword-search-created-date-range-filter.md`, "Gate 2 Re-run"). No Keyword Search test failed.
- Api.Tests 387/387 · Api.IntegrationTests 106 pass / 0 fail · Worker.Tests 131/131 · Web.Tests 309/313.
- Failing every time (CAS-023 owner):
  - `ODriveSyncPageTests.Test_V9_PollReceives401_StopsPollingAndShowsSessionExpired(where: "actionPoll")`
  - `ODriveSyncPageTests.Test_SC10_ODriveSync_ReloadsScanAndResumesPendingSyncActions`
- Intermittent under full-suite load; passes in isolation: `ODriveSyncPageTests.Test_SC8_PurgeRequiresExplicitConfirmationBeforeApiCall`.
- KeywordSearch tests: 94/94 on three consecutive runs (15 new CAS-039 cases plus 79 pre-existing, unmodified).
- Gate 4: the only changed .razor file is at 94.3%. Gate 5: skipped (`touches.api: false`).
- Test-clock note: `Test_SC4_DateModePostsParsedDates` uses 09/30/2026, the date the spec pins down. There is no clock seam, so this test passes only when the machine's UTC date is 2026-09-30 or later.


### Amendment A1 Implementation Log (2026-09-30): calendar picker
**Trigger:** requester feedback after first review: "date picker icon missing to select date". This resolves §14 "Input control".
**Gates:** Build ✓ (Release, 0 errors) | Tests ✗ (waived, same 2 known CAS-023 ODrive failures as above) | Format ✓ | Coverage `KeywordSearch.razor` 94.6% | Endpoints — (skip, `touches.api: false`)

| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/wwwroot/js/datepicker.js` | New. `window.cachetDatePicker.open(el)` calls `showPicker()`, falling back to focus+click. A single delegated click listener on `[data-cachet-date-picker-for]` keeps the open inside the user's click; no inline script, so it is CSP-friendly. |
| `src/Cachet.ArchiveSync.Web/Components/App.razor` | Modified. Adds `<script src="js/datepicker.js"></script>` after `js/download.js`. |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | Modified. Adds a calendar button per date field (`#ks-from-date-picker-btn` / `#ks-to-date-picker-btn`, aria-label "Choose From/To date", inline SVG) and a hidden `type=date` input (`#ks-*-date-native`, `max` = today UTC). `OnFromPicked` / `OnToPicked` write ISO values back as MM/dd/yyyy. No component-side JS interop. |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor.css` | Modified. `.cachet-ks-date-input-wrap`, `.cachet-ks-date-picker-btn` (34px, `--cachet-*` tokens, focus-visible), `.cachet-ks-date-native` (1px, opacity 0, not `display:none`). |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchPageTests.cs` | Modified. Adds `Test_SC8_CalendarButtonOpensPickerAndWritesMmDdYyyy` (SC-8). |

**Test results:** KeywordSearch 95/95 on two runs. Full suite: Api.Tests 387/387 · Api.IntegrationTests 106 pass / 0 fail · Worker.Tests 131/131 · Web.Tests 311/314 (2 known ODrive failures, 1 pre-existing skip).
**Manual check still outstanding (§13/§16):** bUnit cannot execute the browser-side listener. Someone must confirm in Chrome/Edge that clicking the calendar icon opens the picker and that picking a date fills the field as MM/dd/yyyy.

### Amendment A2 Implementation Log (2026-09-30): date-only search (UI)
**Gates:** Build ✓ (Release) | Tests ✗ (waived: the same 2 CAS-023 ODrive failures) | Format ✓ | Coverage `KeywordSearch.razor` 95.5% | Endpoints — (skip)

| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | The term is optional in Date mode (`TermConditionMet`). Adds the Date-mode placeholder, the V10 31-day hint, date-only Filename/Content copy, and the "all files" context header. `lastSearchWasDateOnly` is set explicitly on submit and on reload, so a task whose `searchParameters` is null keeps the keyword copy. |
| Tests | Added `Test_A2_V1_DateModeAllowsEmptyTerm`, `Test_V10_DateOnlyRangeOver31DaysDisablesSearchWithHint`, `Test_A2_SC9_DateOnlySearchPostsEmptyTermWithDates`, `Test_A2_SC10_DateOnlyResultsRenderInformationalAndContentNotice`, and `Test_A2_SC11_ReloadedDateOnlyTaskHeader` (with a null-searchParameters regression guard). |

**Results:** KeywordSearch 100/100 on two runs; full suite as in the CAS-038 A2 log.

### Amendment A3 Implementation Log (2026-09-30): exclusive modes
**Trigger:** requester, 2026-09-30: "if I select Date then only date, if I select Keyword then only keyword; clear both on radio button select". Decision: hide the keyword box in Date mode.
**Gates:** Build ✓ (Release) | Tests ✗ (waived: the same 2 CAS-023 ODrive failures only) | Format ✓ | Coverage `KeywordSearch.razor` 95.6% | Endpoints — (skip)

| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | `SelectDateMode` clears the term. The term block is rendered only in Keyword mode. `TermConditionMet = isDateMode \|\| SearchTermValid`. `lastSearchWasDateOnly = isDateMode` on submit. On reload a both-dates task opens in Date mode with an empty term, and a legacy keyword + date task keeps its term header. The Date-mode placeholder const was removed. |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchPageTests.cs` | 13 tests updated **only** to drop the superseded Date-mode keyword interaction; their names and intent are kept. SC-2 now asserts `#ks-term` is absent. A2_V1 was rewritten as "superseded by A3". V9's > 31-day case was replaced so each test still isolates its own rule. SC-6 now expects the date-only Content notice. Adds `Test_A3_SC12_ModesAreExclusiveAndClearEachOther`. |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchDeepLinkTests.cs` | SC-11 asserts `#ks-term` is absent. Adds `Test_A3_SC13_ReloadedKeywordPlusDateTaskOpensDateModeWithTermHeader`. |

**Results:** KeywordSearch 102/102 on two runs · Api.Tests 390/390 · Api.IntegrationTests 107 pass / 0 fail · Worker.Tests 132/132 · Web.Tests 318/321 (2 known ODrive failures, 1 skip).
**Unchanged:** CAS-038. The Api still accepts keyword + date requests; the screen no longer offers that combination.

### Amendment A4 Implementation Log (2026-09-30): defaults on open, toolbar alignment
**Gates:** Build ✓ (Release) | Tests ✗ (waived: the same 2 CAS-023 ODrive failures only) | Format ✓ | Coverage `KeywordSearch.razor` 96.7% | Endpoints — (skip) | SC-15 alignment: **manual check pending** (§16)

| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | `ApplyReloadedContext` no longer copies the task's term, extension or dates into the filter bar; it keeps `lastSearchedTerm`, `reloadedSearchParameters`, `lastSearchWasDateOnly` and ack. The context header reads only `reloadedSearchParameters` (verified). Adds wrappers `cachet-ks-inline-field` (Bucket and Extension label + select) and `cachet-ks-date-hints`. |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor.css` | Scoped `.cachet-toolbar { align-items: flex-end }` with 8/14px padding. The 12px term margin is removed. Adds `.cachet-ks-inline-field` (inline-flex, centered) and absolutely positioned `.cachet-ks-date-hints`, so hints don't shift the controls. `app.css` is untouched. |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchDeepLinkTests.cs` | SC-7, A2_SC11 and A3_SC13: the prefill assertions are replaced with the A4 defaults ("prefill superseded by CAS-039 A4"); the header, results and Content-notice assertions are kept. |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearch/KeywordSearchPageTests.cs` | **CAS-030 tests** `Test_SC7_KeywordSearch_ReloadsCompletedTaskWithoutResubmitting` and `Test_SC9_DeepLink_PopulatesNamedTask`: only the filter-bar prefill assertions were replaced (CAS-030 §8.2 step 3 is superseded for this screen); all no-resubmit, results, header and ack assertions are kept. Adds `Test_A4_SC14_OpenAlwaysResetsFilterBarButKeepsReloadedResultsAndHeader` (4 cases, including a deep link). |

**Results:** KeywordSearch 106/106 on two runs · Api.Tests 491/491 · Api.IntegrationTests 156 pass / 0 fail · Worker.Tests 132/132 · Web.Tests 353/356 (2 known ODrive failures, 1 skip). The higher totals since A3 come from other work that arrived in the working copy; all of it passes.
**Cross-spec note:** CAS-030 §8.2 step 3 ("set the filter bar's search term and extension from `searchParameters`") no longer applies to `KeywordSearch.razor`. The CAS-030 owner should add a pointer to CAS-039 A4. `ODriveSyncContentScan.razor` is unchanged.

### Amendment A5 (2026-10-01): Quarantine Queue button size (CSS only)
**Trigger:** requester, from a screenshot: on Keyword Search only the Quarantine Queue button is larger; it should match the O-Drive Sync screen, and no working code may be touched.
**Cause:** a leftover CAS-024 scoped rule, `.cachet-ks-quarantine-trigger-btn` (padding 0.6rem 1.25rem, navy border, weight 600), in `KeywordSearch.razor.css`. The O-Drive screen has no such override; its trigger uses only `.cachet-btn .cachet-btn-square`.
**Change:** that one CSS rule was deleted (replaced by a comment). No markup, C# or test changed. The class name stays in the markup, because the `KeywordSearchPageTests` selectors use it.
**Verification:** Release build ✓. KeywordSearch and QuarantineQueue tests: results in the log line below. **Manual visual check pending:** the button should now match Task Manager and Audit Log.
