---
id: CAS-024
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-23
priority: P1
related_specs: [CAS-008, CAS-014, CAS-017, CAS-022, CAS-023]
touches:
  database: false
  api: false # consumes CAS-008's already-merged GET/POST /quarantine(/delete) as-is; no Api/Core change
  ui: true
  worker: false
  b2: false # no direct B2 call — B2 deletion already happens server-side inside CAS-008's service
  config: false
  security: true # Delete Permanently is CachetFullAccess-gated; second-confirmation dialog is FR-17-binding
migrations: []
---

# CAS-024 — Quarantine Queue Modal and Delete-Confirmation Flow

WBS Task 24 (Web UI Development — Quarantine Queue modal). Implements FR-14, FR-16, FR-17. UI contract
fixed by `CACHET_UIUX_Design_Specification_v1.md` §4 (Layout §4.1, Empty State §4.2, Behavior §4.3);
API contract fixed by `CAS-008` (status `merged`) — `GET /quarantine` and `POST /quarantine/delete`.

---

## §0 Scope Note (read this first)

**This spec builds exactly two of the Quarantine Queue's four capabilities: List and Delete
Permanently.** It deliberately does not build "Remove from Queue":

- **Add to Quarantine** (FR-09/FR-15) is already built and shipped as `CAS-022`'s embedded,
  read-only Quarantine panel and its "Send to Quarantine" action on the Filename/Content Matches
  panels — untouched here.
- **Remove from Queue** (`POST /quarantine/remove`, non-destructive catalog removal) is `CAS-014`
  (API, `merged`) and `CAS-017` (UI, `draft`) — a separate, already-opened spec pair. `CAS-017` §14
  item 3 explicitly raised whether "Remove from Queue" should be folded into whatever spec builds the
  Quarantine Queue screen's initial UI. **Resolved here:** no — this spec's task instructions name only
  the list and the Delete Permanently/confirmation flow (per the assigned task's Description and
  Instructions), so "Remove from Queue" stays `CAS-017`'s scope, to be layered onto the modal this spec
  builds once `CAS-017` itself is drafted to `ready` (Karpathy P3 — smallest diff that satisfies the
  spec; no scope creep into a capability `CAS-017` already owns).

This spec's `QuarantineQueueModal.razor` is therefore the modal `CAS-022` §4/§15 and `CAS-017` §1/§4
both already anticipated but deferred: `CAS-022` §4 named it "the full modal with Delete Permanently,
second-confirmation dialog… a separate, future Web-kind spec"; `CAS-017` §1 could not locate it in
`src/` as of 2026-09-22. As of this spec's drafting, it still does not exist in
`src/Cachet.ArchiveSync.Web/Components/` — confirmed by directory listing (only `KeywordSearch.razor`,
`ODriveSyncContentScan.razor`, `Login.razor`, `Error.razor`, `MainLayout.razor` exist).

---

## §1 Summary

Builds `QuarantineQueueModal.razor`, a shared Blazor component presenting the full Quarantine Queue
(FR-14) — every `Active` `dbo.quarantine_entry` row, checkbox-selectable — with a **Delete Permanently**
action gated by an explicit second-confirmation dialog before calling `CAS-008`'s
`POST /quarantine/delete` (FR-16, FR-17). The modal is reachable from both existing screens
(`KeywordSearch.razor` and `ODriveSyncContentScan.razor`) via a header-style trigger button on each page,
since no global application header exists yet (`CAS-022` §0/§4, `CAS-023` §0/§4's already-disclosed gap,
carried forward here rather than re-litigated). Each selected file's delete is attempted independently;
a succeeded row disappears from the modal's list, a failed row remains with an inline per-row error
(UI/UX spec §4.3) — mirroring `CAS-023`'s identical per-row Purge outcome pattern for O-Drive Sync.

---

## §2 User Story

As a **TraceCounts reviewer** signed into Cachet Archive Sync, I want to open the Quarantine Queue from
wherever I am in the app, see every file currently quarantined, select one or more I've decided to
remove for good, and — after an explicit, clearly-worded confirmation that the action cannot be undone —
permanently delete them from both the local cache and Backblaze B2, with each file's outcome reported
independently, so that I never delete something by accident and always know exactly which files
succeeded or failed.

---

## §3 In-Scope

- `QuarantineQueueModal.razor` (shared component, not a routed page — instantiated from within
  `KeywordSearch.razor` and `ODriveSyncContentScan.razor`):
  - **Trigger button** — a "Quarantine Queue" button with a count badge, placed in each host page's
    existing top control row (§8.1) — the header-button-with-badge affordance
    `CACHET_UIUX_Design_Specification_v1.md` §1.1/§4.1 specifies, built directly on each page rather
    than in a not-yet-existing global header shell (§4, same scoped deviation `CAS-022`/`CAS-023`
    already took for their own header-dependent controls).
  - **Modal body** — table: checkbox, Name/Path, Quarantined On, Status (FR-14; UI/UX spec §4.1's
    exact column set — no "Source Tab" column, per that section's explicit drop). Backed by its own
    `GET /quarantine` call, loaded when the modal opens and refreshed after every delete attempt.
  - **Delete Permanently** button — disabled until ≥1 row is checked; label shows the count (e.g.,
    "Delete Permanently (3)") — the same disabled-until-selected pattern already used by `CAS-022`'s
    "Send to Quarantine" and `CAS-023`'s three sync-action buttons (UI/UX spec §1.4).
  - **Second-confirmation dialog** (FR-17) — nested dialog with the UI/UX spec §4.3 exact copy:
    _"This will permanently delete `<N>` file(s) from both the local cache and Backblaze B2. This
    action cannot be undone."_, a destructive-confirm button ("Yes, delete permanently") and a Cancel
    option. Only confirming this dialog calls `POST /quarantine/delete`.
  - **Per-file independent outcome handling** (UI/UX spec §4.3) — on the `200` response, a row whose
    result is `Success` is removed from the modal's list; a row whose result is `Failed` remains
    visible with an inline error (e.g., "Delete failed — see Audit Log"). Retrying a failed row needs
    no special affordance — the user re-selects it and clicks Delete Permanently again.
  - **Empty state** (UI/UX spec §4.2, exact copy): _"Quarantine is empty. Files sent to quarantine from
    Keyword Search appear here for final review before permanent deletion."_
  - **Close** button — dismisses the modal without side effects; does not cancel an in-flight delete
    request (there is none to cancel — `POST /quarantine/delete` is synchronous, `CAS-008` §6).
  - `CachetFullAccess` UI gating on Delete Permanently: rendered visibly but disabled with a tooltip
    (e.g., "Requires Full Access") for a session lacking that capability — same posture `CAS-023`
    V5/SC-11 already established for this project's other B2-write action buttons, applied here for
    consistency rather than re-derived independently (§7 V3).
  - After a successful delete (≥1 row actually removed), refresh `KeywordSearch.razor`'s existing
    embedded read-only Quarantine panel (`CAS-022` §8.4) if the modal was opened from that page, via a
    parent-supplied `EventCallback` — keeping that panel's view of the queue in sync with the modal's
    own action, without either component owning the other's state.
- Client-side validations (§7): non-empty selection gating, confirmation-before-call gating,
  `CachetFullAccess` UI gating, and graceful inline error handling for unexpected server responses.

---

## §4 Out-of-Scope

- **"Remove from Queue"** (`POST /quarantine/remove`) — `CAS-014`/`CAS-017`'s scope (§0). Not built,
  wired, or stubbed here. `CAS-017`, once drafted to `ready`, is expected to add its own action button
  to this same modal component in a follow-up change spec against the file this spec creates.
- **Add to Quarantine** (FR-09/FR-15) — `CAS-022`'s scope, unchanged. This spec does not modify
  `KeywordSearch.razor`'s "Send to Quarantine" button or its `POST /search/results/quarantine` call.
- **`CAS-022`'s embedded read-only Quarantine panel structure** — left as-is beyond adding the optional
  refresh hook described in §3; its own checkbox-less, action-less design (`CAS-022` §15, resolved
  2026-09-23) is not revisited or duplicated by this spec's own checkbox-bearing list.
- **Global header / tab-navigation shell, Task Manager button+badge** — same genuine gap `CAS-022` §0/§4
  and `CAS-023` §0/§4 already disclosed: no header/footer/tab-navigation component exists in
  `src/Cachet.ArchiveSync.Web/` yet. This spec's per-page trigger button is a scoped substitute, not a
  header implementation (§14 note on future consolidation).
- **Any `Cachet.ArchiveSync.Api`, `Core`, or `Worker` change** — `CAS-008`'s contracts (`GET /quarantine`,
  `POST /quarantine/delete`) are consumed exactly as merged; no field, status code, or route is added or
  altered.
- **Pre-/Post-Action Report modals** (FR-25–FR-27) — separate, header-triggered feature, still unbuilt
  (same gap `CAS-022`/`CAS-023` §4 already flagged).
- **Drive encryption-at-rest / O-Drive refresh cadence / web caching layer mechanism** (BRD §12.4, still
  open) — not depended on; this modal always calls the live API (same deferral `CAS-022`/`CAS-023` §4
  already made for the identical open item).

---

## §5 Database Objects

N/A — `touches.database: false`. No schema change. Consumes `dbo.quarantine_entry`/`dbo.file_index`
only indirectly, through `CAS-008`'s already-built endpoints.

---

## §6 API Endpoints

No `Cachet.ArchiveSync.Api` endpoint is added or changed (`touches.api: false`). This modal is a pure
consumer of `CAS-008`'s two already-merged contracts, restated here only to fix the build target:

| Endpoint                  | Source spec  | Used for                                                                          |
| ------------------------- | ------------ | --------------------------------------------------------------------------------- |
| `GET /quarantine`         | `CAS-008` §6 | Populate the modal's list on open and after every delete attempt (§8.1, §8.2)     |
| `POST /quarantine/delete` | `CAS-008` §6 | Delete Permanently, only after the second-confirmation dialog is confirmed (§8.3) |

Both calls go through the existing `"CachetApi"` named `HttpClient` (`CAS-002` §6.6,
`Cachet.ArchiveSync.Web/Program.cs`), which already forwards the circuit's session cookie — the same
client `KeywordSearch.razor` already uses for its own `GET /quarantine` call — no new DI registration or
client is needed.

---

## §7 Validations

Client-side gating so the user gets immediate feedback before a round trip — the server (`CAS-008` §7
V1–V6) remains the source of truth in every case; a client-side pass never substitutes for the server's
own rejection.

| ID  | Rule                                                                                                                                                          | On Failure                                                                                      | Test                                                      |
| --- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- | --------------------------------------------------------- |
| V1  | Delete Permanently is disabled until ≥1 row in the modal's list is checked (UI/UX spec §4.3 "disabled-until-selected" pattern)                                | Button remains disabled; no request sent                                                        | `Test_V1_DeletePermanentlyDisabledUntilRowChecked`        |
| V2  | Delete Permanently never calls `POST /quarantine/delete` without the user completing the second-confirmation dialog, regardless of how many rows are selected | No request sent until confirmed; canceling the dialog sends nothing and preserves the selection | `Test_V2_DeleteNeverSubmittedWithoutExplicitConfirmation` |
| V3  | Delete Permanently is disabled (never hidden) for a session lacking `CachetFullAccess`, with a tooltip explaining why — same posture `CAS-023` V5 established | Button rendered disabled with a tooltip (e.g., "Requires Full Access")                          | `Test_V3_DeletePermanentlyUnavailableWithoutFullAccess`   |
| V4  | A server-side `403`/`404`/`422` from either §6 endpoint is surfaced as a modal-level inline error banner, never an unhandled exception/blank modal            | Inline error banner per §8.4                                                                    | `Test_V4_ServerErrorResponsesRenderInlineErrorNotCrash`   |

---

## §8 UI / UX

### §8.1 Trigger and Layout — Exactly `CACHET_UIUX_Design_Specification_v1.md` §4.1

- **Trigger:** a "Quarantine Queue" button with a count badge (current `Active` row count, refreshed
  each time the modal opens), placed in the top control row of both `KeywordSearch.razor` and
  `ODriveSyncContentScan.razor` — a scoped, per-page substitute for the header button
  `CACHET_UIUX_Design_Specification_v1.md` §1.1/§4.1 specify, matching the identical deviation
  `CAS-022`/`CAS-023` already took for their own header-dependent affordances (§4).
- **Modal:** titled **"Quarantine Queue"** (the wireframe's "— Shared Across All Tabs" suffix is
  dropped, per §4.1 — Quarantine is Keyword Search-only in origin, though the modal itself is openable
  from either screen). Table columns: checkbox, Name/Path, Quarantined On, Status. Footer: **Close**
  and **Delete Permanently** buttons.

### §8.2 Load and Refresh

- On open: call `GET /quarantine` (`CAS-008` §6) and render the response's `items[]` (§8.1's column
  set maps directly: `name`/`path`, `quarantinedOn`, `status` — no client-side reshaping beyond
  binding, same convention `CAS-022`/`CAS-023` already established for their own list panels).
- On any request failure (network error or non-2xx): inline error banner (§8.4), list left in its
  last-known state rather than blanked.
- After a `POST /quarantine/delete` response (§8.3): re-run the load step so the list reflects
  server-confirmed state rather than an optimistic client-side removal — a `Success` row disappearing
  because the server said so, not because the client assumed it would.
- If the modal was opened from `KeywordSearch.razor`, invoke the parent-supplied refresh callback
  (§3) after a delete attempt so that page's own embedded read-only Quarantine panel (`CAS-022` §8.4)
  reflects the same state without a full page reload.

### §8.3 Delete Permanently and Confirmation (FR-16, FR-17)

1. **Delete Permanently clicked** (V1): opens the second-confirmation dialog with the exact UI/UX spec
   §4.3 copy: _"This will permanently delete `<N>` file(s) from both the local cache and Backblaze B2.
   This action cannot be undone."_ — `<N>` is the current selection count.
2. **Confirmed:** calls `POST /quarantine/delete` (`CAS-008` §6) with
   `{ fileIds: [...selected quarantine_entry_id values...], confirm: true }`. Button and dialog show a
   spinner/disabled state for the duration of the call (synchronous endpoint, `CAS-008` §6 — no
   task-queue submission, no polling).
3. **Canceled:** dialog closes, no request sent, selection preserved (V2).
4. **Response `200`:** per §8.2, per-row handling — a `Success` result removes that row from the
   modal's list on the next load; a `Failed` result keeps that row visible with an inline per-row
   error (e.g., "Delete failed — see Audit Log," UI/UX spec §4.3). Selection is cleared for the
   attempted rows regardless of outcome (retrying a failed row means re-selecting it fresh).
5. **Response `422 confirmation-required` / `validation-failed`, or a network error:** modal-level
   inline error banner (§8.4, V4) — this should not normally be reachable given V1/V2's client-side
   gating, but is handled defensively rather than left to throw.

### §8.4 Empty / Error States — Exactly `CACHET_UIUX_Design_Specification_v1.md` §4.2

| State                                                                                             | Copy/behavior                                                                                                                                                                                                                                      |
| ------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| No quarantined files                                                                              | _"Quarantine is empty. Files sent to quarantine from Keyword Search appear here for final review before permanent deletion."_ (UI/UX spec §4.2, reused verbatim — same copy `CAS-022` §8.5 already uses for its own embedded panel's empty state). |
| `GET /quarantine` fails on open or refresh                                                        | Non-field-specific banner: _"Something went wrong. Please try again."_ — logged server-side (§11) with the endpoint/status code, never shown raw to the user.                                                                                      |
| A row's delete attempt fails (`Failed` result)                                                    | That row remains visible with its own inline error, per §8.3 point 4 — not a modal-level banner (the failure is scoped to that file, consistent with `CAS-008`'s per-file-independent-outcome design).                                             |
| `POST /quarantine/delete` returns an unexpected `403`/`404`/`422` at the whole-request level (V4) | Modal-level inline error banner: _"Something went wrong. Please try again."_                                                                                                                                                                       |

---

## §9 Configuration Changes

No new configuration key. Reuses the existing `"CachetApi"` named `HttpClient` (`CAS-002` §6.6),
already configured for `KeywordSearch.razor`'s own `GET /quarantine` call.

---

## §10 Success Criteria

| ID    | Criterion                                                                                                                                                                                                          | Test                                                                       |
| ----- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------- |
| SC-1  | The Quarantine Queue trigger button (with count badge) opens the modal on both `KeywordSearch.razor` and `ODriveSyncContentScan.razor`                                                                             | `Test_SC1_TriggerButtonOnBothPagesOpensModal`                              |
| SC-2  | Opening the modal calls `GET /quarantine` and renders every `Active` row with the exact column set (checkbox, Name/Path, Quarantined On, Status)                                                                   | `Test_SC2_ModalOpenLoadsAndRendersQuarantineList`                          |
| SC-3  | Delete Permanently is disabled with 0 rows checked, and shows the correct count once ≥1 row is checked                                                                                                             | `Test_SC3_DeletePermanentlyReflectsSelectionCount`                         |
| SC-4  | Clicking Delete Permanently never calls `POST /quarantine/delete` before the second-confirmation dialog is explicitly confirmed; canceling the dialog sends no request and preserves the selection                 | `Test_SC4_DeleteRequiresExplicitConfirmationBeforeApiCall`                 |
| SC-5  | Confirming the dialog calls `POST /quarantine/delete` with `confirm: true` and exactly the selected `fileId`s                                                                                                      | `Test_SC5_ConfirmedDeleteCallsApiWithConfirmTrueAndCorrectFileIds`         |
| SC-6  | On a response where one file succeeds and one fails: the succeeded row is removed from the list on refresh, the failed row remains visible with an inline error, both reflected without a modal-level error banner | `Test_SC6_PartialBatchIsolatedPerFileNoModalLevelError`                    |
| SC-7  | A session without `CachetFullAccess` cannot trigger Delete Permanently from the modal (button rendered disabled with a tooltip per V3) while viewing the list remains available                                    | `Test_SC7_ReadOnlySessionCannotDeleteButCanView`                           |
| SC-8  | Zero-result completion renders the exact UI/UX spec §4.2 empty-state copy, not an empty table with no explanation                                                                                                  | `Test_SC8_EmptyQuarantineShowsExplicitEmptyStateCopy`                      |
| SC-9  | A successful delete initiated from the modal while opened on `KeywordSearch.razor` also refreshes that page's embedded read-only Quarantine panel                                                                  | `Test_SC9_SuccessfulDeleteRefreshesEmbeddedQuarantinePanelOnKeywordSearch` |
| SC-10 | No B2 application key, session cookie value, or raw file content ever appears in `QuarantineQueueModal.razor`'s rendered markup or in Web's log output                                                             | `Test_SC10_NoCredentialOrContentLeakageInUiSurfaceOrLogs`                  |

---

## §11 Telemetry

```csharp
// _logger.LogInformation("QuarantineQueueModal: opened from {SourcePage}", sourcePage);
// _logger.LogInformation("QuarantineQueueModal: delete submitted, FileCount={FileCount}", fileIds.Count);
// _logger.LogInformation("QuarantineQueueModal: delete completed, SucceededCount={SucceededCount}, FailedCount={FailedCount}", succeededCount, failedCount);
// _logger.LogWarning("QuarantineQueueModal: unexpected {StatusCode} from {Endpoint}", statusCode, endpoint);
```

- Never log: the B2 application key, the session/cookie value, full file content, or raw local
  mirror/B2 filesystem paths beyond what the audit log's own `file_path` column already captures
  (same restriction `CAS-022`/`CAS-023` §11 and `CAS-008` §11 already apply).

---

## §12 Security Notes

### B2 Credential Handling

This modal never touches the B2 application key at any point. It consumes only the already-sanitized
response shapes `CAS-008` §6 defines (`CAS-008` §12 already confirms none of those DTOs carry a key or
key ID). No new B2 client or capability check is introduced here.

### Audit Trail

No change — `CAS-008` §12 already writes the `Deleted`/`DeleteFailedB2`/`DeleteFailedLocal` audit rows
on the Api side, per file, before `POST /quarantine/delete`'s response returns (FR-23). This spec adds
no new audited action; it is a pure UI/transport layer in front of an already-audited backend call.

### Session / Auth Requirements

The modal's list view requires only an authenticated session (`CachetAuthenticated`, same global gate
`CAS-021` establishes) — matching `GET /quarantine`'s own auth requirement (`CAS-008` §6/§12). Delete
Permanently additionally requires `CachetFullAccess` — `CAS-008` §6/§12 already enforces this
server-side (`403` on a `Read Only` session); V3/SC-7 make this a UI-level affordance (disabled button
with a tooltip) rather than a trap the user only discovers after confirming the destructive dialog,
same posture `CAS-023` V5/SC-11 established. **Same sequencing dependency `CAS-022`/`CAS-023` §12
recorded:** this modal carries no `[Authorize]`-equivalent gate of its own beyond whatever `CAS-021`'s
`FallbackPolicy` establishes app-wide; deploy after `CAS-021` (§14).

### Irreversibility (FR-17)

No soft-delete, undo, grace period, or recovery affordance may be added to this modal's Delete
Permanently flow without an explicit spec change and new client sign-off (CLAUDE.md's binding rule;
BRD §9's accepted, deliberate business decision) — the second-confirmation dialog's copy must say
exactly what `CAS-008` does: the file is genuinely, irreversibly gone from both B2 and the local mirror
once the server reports `Success` for it.

---

## §13 Test Plan

### Unit Tests

| Class                                                    | Method                                                                                                                                                                                                                                                                                            | Proves                |
| -------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------- |
| `QuarantineQueueModalTests` (bUnit)                      | `Test_V1_DeletePermanentlyDisabledUntilRowChecked`, `Test_V2_DeleteNeverSubmittedWithoutExplicitConfirmation`, `Test_V3_DeletePermanentlyUnavailableWithoutFullAccess`, `Test_SC3_DeletePermanentlyReflectsSelectionCount`, `Test_SC7_ReadOnlySessionCannotDeleteButCanView`                      | V1–V3, SC-3, SC-7     |
| `QuarantineQueueModalTests` (bUnit, mocked `HttpClient`) | `Test_SC2_ModalOpenLoadsAndRendersQuarantineList`, `Test_SC4_DeleteRequiresExplicitConfirmationBeforeApiCall`, `Test_SC5_ConfirmedDeleteCallsApiWithConfirmTrueAndCorrectFileIds`, `Test_SC6_PartialBatchIsolatedPerFileNoModalLevelError`, `Test_SC8_EmptyQuarantineShowsExplicitEmptyStateCopy` | SC-2, SC-4–SC-6, SC-8 |
| `QuarantineQueueModalTests` (bUnit)                      | `Test_V4_ServerErrorResponsesRenderInlineErrorNotCrash`                                                                                                                                                                                                                                           | V4                    |
| `KeywordSearchPageTests` (bUnit, extended)               | `Test_SC1_TriggerButtonOnBothPagesOpensModal` (KeywordSearch host), `Test_SC9_SuccessfulDeleteRefreshesEmbeddedQuarantinePanelOnKeywordSearch`                                                                                                                                                    | SC-1, SC-9            |
| `ODriveSyncPageTests` (bUnit, extended)                  | `Test_SC1_TriggerButtonOnBothPagesOpensModal` (O-Drive Sync host)                                                                                                                                                                                                                                 | SC-1                  |

### Integration Tests

| Class                                                                                                                                                                             | Method                                                                                                                                                          | Proves                               |
| --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------ |
| `QuarantineQueueFlowIntegrationTests` (`WebApplicationFactory`, Web, against a stubbed/in-memory Api double or the real `CAS-008` controller with a test double repository layer) | `Test_SC5_ConfirmedDeleteCallsApiWithConfirmTrueAndCorrectFileIds` (full round trip), `Test_SC6_PartialBatchIsolatedPerFileNoModalLevelError` (full round trip) | SC-5, SC-6 (end-to-end confirmation) |
| Log-output assertion (test log sink)                                                                                                                                              | `Test_SC10_NoCredentialOrContentLeakageInUiSurfaceOrLogs`                                                                                                       | SC-10                                |

### Coverage Target

Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. No database migration (§5).
2. Deploy `Cachet.ArchiveSync.Web` only — no `Api`/`Worker`/`Core` change (§6, §4). `CAS-008`'s Api
   endpoints are already merged/live.
3. **Deploy after `CAS-021` merges** (§12) — same sequencing dependency `CAS-022`/`CAS-023` §14 already
   established: this modal depends on `CAS-021`'s global authorization gate to be unreachable without a
   session.
4. **Note for a future consolidation pass:** once a global header/tab-navigation shell is built (the gap
   `CAS-022` §0/§4 and `CAS-023` §0/§4 both disclosed), the per-page trigger button this spec adds to
   `KeywordSearch.razor` and `ODriveSyncContentScan.razor` should be replaced by a single header-level
   trigger — tracked here as a forward note, not a blocking dependency for this spec.
5. Rollback: revert the new `QuarantineQueueModal.razor` component and its two trigger-button insertions;
   no schema or Api change to unwind. A rollback after any delete has already run through this modal
   does not and cannot restore the deleted files — `CAS-008`'s own accepted, deliberate risk posture
   (BRD §9), unaffected by this spec being UI-only.

---

## §15 Open Questions

All three items below were raised while drafting this spec and resolved directly with R. Devarajan,
TraceCounts (Solution Owner), 2026-09-23 — logged below for traceability (Karpathy P6), matching
`CAS-022`/`CAS-023`'s own resolution-log convention. No blocking open item remains against this spec.

- [x] **Whether "Remove from Queue" belongs in this spec — resolved 2026-09-23 (R. Devarajan,
      TraceCounts): no, stays `CAS-017`'s scope.** This spec's assigned task instructions name only the
      list and Delete Permanently/confirmation flow; `CAS-017` remains the spec that adds a third action
      to this same modal component in a follow-up change once it reaches `ready` (§0/§4).
- [x] **Trigger placement without a global header — resolved 2026-09-23 (R. Devarajan, TraceCounts):
      a per-page trigger button on both `KeywordSearch.razor` and `ODriveSyncContentScan.razor`, not a
      new header component.** Matches the identical scoped deviation `CAS-022`/`CAS-023` already took;
      building a full header shell is a separate, larger UI task not requested here (§14 point 4 tracks
      the future consolidation).
- [x] **Whether the modal should optimistically remove rows client-side on delete, or always re-fetch —
      resolved 2026-09-23 (R. Devarajan, TraceCounts): always re-fetch via `GET /quarantine` after a
      delete attempt (§8.2/§8.3).** An optimistic client-side removal risks showing a row as gone when
      the server actually reported `Failed` for it; re-fetching keeps the displayed state
      server-confirmed, consistent with FR-17's "no ambiguity about what actually happened" posture.
- Not blocked by BRD §12.4 (web caching layer mechanism, O-Drive refresh cadence) — this modal always
  calls the live API and makes no caching-layer assumption; confirmed not load-bearing here, same
  deferral `CAS-022`/`CAS-023` §15 already made for the identical open item.
- Not blocked by BRD §12.2/§12.1 items — login method is resolved (`CAS-002`); this spec depends only on
  `CAS-021`'s deployment sequencing (§14), not on any still-open decision.

---

## §16 Sign-off

| Role     | Name                                        | Date       | Notes                   |
| -------- | ------------------------------------------- | ---------- | ----------------------- |
| Author   | R. Devarajan (TraceCounts), via Claude Code | 2026-09-23 | Drafted per WBS Task 24 |
| Reviewer |                                             |            | Pending                 |

---

## §17 Graph Impact

Graph exists for this repo (`graphify-out/`, built 2026-09-10, refreshed since) and indexes the
BRD/WBS/API-spec/decision-log documentation corpus. Queried live from this Windows host session:

```
python -m graphify query "Quarantine Queue modal Delete Permanently confirmation FR-14 FR-16 FR-17" --budget 1200
```

Result: a 17-node, depth-2 BFS anchored on `API spec §6: Quarantine and Permanent Delete Module
(FR-14-FR-17)`, `IQuarantinePermanentDeleteService.cs`, and `QuarantineEntryForDeleteRow.cs` — confirming
`FR-14`/`FR-16`/`FR-17`, `GET /quarantine`, and `POST /quarantine/delete` (all `CAS-008`, community 60/77)
as this spec's sole upstream dependency, with `POST /quarantine/release` (the dead-vocabulary route name
`CAS-014` already superseded with `/remove`) surfacing only as an `AMBIGUOUS`-confidence edge to
`FR-14` — not a load-bearing connection for this spec. No `src/Cachet.ArchiveSync.Web` node was returned
by the query, consistent with `CAS-022`/`CAS-023` §17's own finding that the graph has no `.razor`
nodes for this project yet.

Supplemented by a manual read of `src/Cachet.ArchiveSync.Web/Components/Pages/` (confirming no
`QuarantineQueueModal.razor` or equivalent exists yet, and that `KeywordSearch.razor`'s embedded
Quarantine panel is read-only per `CAS-022` §8.4/§15), `CAS-008` (full read, §6/§7/§12 — the exact
route/response/validation/audit shapes this spec consumes), `CAS-014`/`CAS-017` (confirming "Remove from
Queue" is a separate, already-opened spec pair this spec does not fold in, §0), and `CAS-022`/`CAS-023`
(as the direct structural precedent for this project's checkbox-select-then-confirm Blazor pattern and
per-page header-button substitute).

### God Nodes in Blast Radius

None of the graph's top-10 numeric god nodes (all `graphify` tool internals) are BRD/spec nodes this
feature touches. Within the documentation graph, `API spec §6: Quarantine and Permanent Delete Module
(FR-14-FR-17)` is the highest-degree node this spec's blast radius passes through — confirming this spec
is purely a UI consumer of that already-implemented surface, with no independent API-layer node of its
own.

### Communities / Modules Affected

- **Quarantine community (60/77)** — `CAS-008`'s `GET /quarantine`/`POST /quarantine/delete`,
  `FR-14`/`FR-16`/`FR-17`, `WBS Task 18` — this modal is a UI consumer of every node in this community
  this spec needs, with no drift from `CAS-008`'s already-merged contract.
- **Auth/Session community** — `CachetFullAccess`/`CachetAuthenticated` policy nodes (`CAS-002` §6.4),
  directly connected to this modal's V3/SC-7 UI-level capability gating, mirroring `CAS-008`'s own
  server-side enforcement.
- **UI/UX design corpus** — `CACHET_UIUX_Design_Specification_v1.md` §4 (this modal's own section) and
  §1.4 (the shared disabled-until-selected interaction pattern this spec reuses).
- Projects touched: `Web` only (one new shared `.razor` component + two existing page edits to add the
  trigger button). **Not** `Api`, `Core`, or `Worker`.

### Surprising Connections

- The graph surfaces `POST /quarantine/release` still linked (at `AMBIGUOUS` confidence) to `FR-14` even
  though `CAS-014` already superseded that route name with `/remove` — confirming `CAS-014` §14's own
  note that `CACHET_API_Design_Specification_v1.md` §6.3 still carries the old name and needs its own
  documentation-only correction pass (not this spec's job, and not blocking it — this modal never calls
  a `/release` or `/remove` route).
- **Confirmed by manual code read, not by the graph (graph has no `src/Web` nodes for this module):** no
  `QuarantineQueueModal.razor` or equivalent exists yet anywhere in `src/Cachet.ArchiveSync.Web/` — this
  spec is a clean build against `CAS-008`'s contract, not a modification of partial existing work.
- **Decomposition check (P9) result:** module fan-out is 1 (Quarantine List + Delete Permanently only —
  no embedded Add or Remove-from-Queue sliver, unlike `CAS-022`'s embedded read-only panel). Size
  thresholds: 2 endpoints _consumed_ (not owned), 4 validations, 10 SCs, 0 DB objects, estimated changed
  files (one new component, two small page edits) well under 15. **Decision: single spec, not split** —
  same conclusion `CAS-022`/`CAS-023` §17 reached for their own comparable UI-only scope.

### Graphify Queries Run

```
python -m graphify query "Quarantine Queue modal Delete Permanently confirmation FR-14 FR-16 FR-17" --budget 1200
```

(God Nodes/Communities/Surprising-Connections sections above also draw on `graphify-out/GRAPH_REPORT.md`
and the manual code/spec read noted above.)

---

## §18 Verification Plan

### Reviewer Checklist

The reviewer will independently verify each of the following. Mark N/A only if the section itself is
N/A.

- [ ] All §10 Success Criteria (SC-1–SC-10) have a passing test.
- [ ] All §7 Validations (V1–V4) are implemented and tested.
- [ ] `QuarantineQueueModal.razor` matches `CACHET_UIUX_Design_Specification_v1.md` §4.1–§4.3 exactly:
      column set (checkbox, Name/Path, Quarantined On, Status — no Source Tab column), the exact
      second-confirmation dialog copy, and the exact empty-state copy (§4.2).
- [ ] No "Remove from Queue" action, route, or affordance was added anywhere in this component (§0/§4 —
      confirms this spec did not absorb `CAS-017`'s scope).
- [ ] Delete Permanently is never submitted without the user explicitly confirming the second dialog
      first (V2, SC-4, SC-5) — cancel leaves the selection intact and sends no request.
- [ ] A row whose delete succeeds is removed from the list on the next `GET /quarantine` refresh; a row
      whose delete fails remains visible with its own inline error, not a modal-level banner (SC-6).
- [ ] The modal is reachable from both `KeywordSearch.razor` and `ODriveSyncContentScan.razor` via a
      per-page trigger button with a count badge (SC-1).
- [ ] A successful delete from the modal, when opened from `KeywordSearch.razor`, also refreshes that
      page's embedded read-only Quarantine panel (SC-9).
- [ ] No `Cachet.ArchiveSync.Api`, `Cachet.ArchiveSync.Core`, or `Cachet.ArchiveSync.Worker` file was
      modified (§4, §6 — this spec is Web-only).
- [ ] No global header/tab-navigation shell component was added (§0/§4 — confirm the per-page
      trigger-button substitute was used instead).
- [ ] §12 B2/session: no B2 key, key ID, or session cookie value appears in rendered markup or logs
      (SC-10); Delete Permanently is unavailable to a session without `CachetFullAccess` (V3, SC-7).
- [ ] §17 Graph Impact matches actual code changes — no unintended nodes (no Api/Core/Worker files, no
      Keyword Search filter/search-flow or O-Drive Sync scan-flow code changed beyond the trigger
      button insertion).

### Verification Report Format

Per template: `CAS-024.review.md` with overall verdict, per-SC verdict table, security gap list, scope
violations.

## Implementation Log

**Completed:** 2026-09-23
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 95.8% QuarantineQueueModal.razor / 99.3% KeywordSearch.razor / 98.4% ODriveSyncContentScan.razor | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Shared/QuarantineQueueModal.razor` | New — shared modal: list, Delete Permanently, second-confirmation dialog |
| `src/Cachet.ArchiveSync.Web/Components/Shared/QuarantineQueueModal.razor.css` | New — scoped styling |
| `src/Cachet.ArchiveSync.Web/Components/_Imports.razor` | Modified — using for Components.Shared |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor` | Modified — trigger button + modal instance wired to LoadQuarantineAsync |
| `src/Cachet.ArchiveSync.Web/Components/Pages/KeywordSearch.razor.css` | Modified — trigger button style |
| `src/Cachet.ArchiveSync.Web/Components/Pages/ODriveSyncContentScan.razor` | Modified — trigger button + modal instance, own quarantine count fetch |
| `src/Cachet.ArchiveSync.Web.Tests/Quarantine/QuarantineQueueModalTests.cs` | New — bUnit unit tests |
| `src/Cachet.ArchiveSync.Web.Tests/Quarantine/FakeQuarantineQueueApiHandler.cs` | New — test double for GET/POST quarantine |
| `src/Cachet.ArchiveSync.Web.Tests/Quarantine/QuarantineQueueFlowIntegrationTests.cs` | New — WebApplicationFactory integration tests |
| `src/Cachet.ArchiveSync.Web.Tests/KeywordSearchPageTests.cs` | Modified — SC-1/SC-9 tests, Full-Access auth setup |
| `src/Cachet.ArchiveSync.Web.Tests/ODriveSyncPageTests.cs` | Modified — SC-1 test |
| `src/Cachet.ArchiveSync.Web.Tests/FakeKeywordSearchApiHandler.cs` | Modified — added quarantine routes |
| `src/Cachet.ArchiveSync.Web.Tests/FakeODriveSyncApiHandler.cs` | Modified — added quarantine routes |
| `src/Cachet.ArchiveSync.Web.Tests/Auth/FakeCachetApiHandler.cs` | Modified — added quarantine routes |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `QuarantineQueueModalTests` | `Test_SC2_ModalOpenLoadsAndRendersQuarantineList` | SC-2 |
| `QuarantineQueueModalTests` | `Test_SC3_DeletePermanentlyReflectsSelectionCount` | SC-3 |
| `QuarantineQueueModalTests` | `Test_V1_DeletePermanentlyDisabledUntilRowChecked` | V-1 |
| `QuarantineQueueModalTests` | `Test_SC4_DeleteRequiresExplicitConfirmationBeforeApiCall` | SC-4 |
| `QuarantineQueueModalTests` | `Test_V2_DeleteNeverSubmittedWithoutExplicitConfirmation` | V-2 |
| `QuarantineQueueModalTests` | `Test_SC5_ConfirmedDeleteCallsApiWithConfirmTrueAndCorrectFileIds` | SC-5 |
| `QuarantineQueueModalTests` | `Test_SC6_PartialBatchIsolatedPerFileNoModalLevelError` | SC-6 |
| `QuarantineQueueModalTests` | `Test_SC7_ReadOnlySessionCannotDeleteButCanView` | SC-7 |
| `QuarantineQueueModalTests` | `Test_V3_DeletePermanentlyUnavailableWithoutFullAccess` | V-3 |
| `QuarantineQueueModalTests` | `Test_SC8_EmptyQuarantineShowsExplicitEmptyStateCopy` | SC-8 |
| `QuarantineQueueModalTests` | `Test_V4_ServerErrorResponsesRenderInlineErrorNotCrash` | V-4 |
| `QuarantineQueueFlowIntegrationTests` | `Test_SC10_NoCredentialOrContentLeakageInUiSurfaceOrLogs` | SC-10 |
| `KeywordSearchPageTests` | `Test_SC1_TriggerButtonOnBothPagesOpensModal` | SC-1 |
| `KeywordSearchPageTests` | `Test_SC9_SuccessfulDeleteRefreshesEmbeddedQuarantinePanelOnKeywordSearch` | SC-9 |
| `ODriveSyncPageTests` | `Test_SC1_TriggerButtonOnBothPagesOpensModal` | SC-1 |
