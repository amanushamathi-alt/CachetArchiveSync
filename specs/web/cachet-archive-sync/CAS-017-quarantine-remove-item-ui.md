---
id: CAS-017
target_app: cachet-archive-sync
status: draft
owner: devarajan@tracecounts.org
created: 2026-09-22
priority: P2
change_type: enhancement
related_specs: [CAS-014, CAS-008]
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

# CAS-017 — Change: Quarantine Queue "Remove from Queue" UI Affordance

Opened as the resolution of CAS-014 §14's third open question (resolved 2026-09-22, R. Devarajan,
TraceCounts). CAS-014 builds the `POST /quarantine/remove` API endpoint (BRD §12.3 items 3, 7) but
ships `touches.ui: false` by design — it has no UI consumer. This spec's job is to give that endpoint a
UI consumer: a "Remove from Queue" action in the Quarantine Queue screen, and the corresponding
correction to `CACHET_UIUX_Design_Specification_v1.md` §4.3.

**Sequencing dependency:** this spec depends on CAS-014 shipping first — it consumes CAS-014's
`POST /quarantine/remove` contract as a given, not something this spec defines or changes.

---

## §1 Current Behavior

`CACHET_UIUX_Design_Specification_v1.md` §4.3 documents exactly two actions in the Quarantine Queue
modal: "Send to Quarantine" (add) and "Delete Permanently" (CAS-008's `POST /quarantine/delete`,
multi-select-then-batch-act pattern per §1.4). No "Remove from Queue" / non-destructive removal
affordance exists in the design doc or in any Blazor component today.

TODO at `/spec-change` or `/implement` time: confirm this against the actual Blazor component once one
exists — per CAS-014 §4/§16, no graphify graph is available yet for this repo, and no Quarantine Queue
`.razor` component has been located in `src/Cachet.ArchiveSync.Web/` as of this spec's drafting (WBS
UI tasks for Quarantine were `Not Started` at BRD v2.3, 2026-09-22).

---

## §2 Desired Behavior

Add a "Remove from Queue" action to the Quarantine Queue screen, following the same
multi-select-then-batch-act interaction pattern already used for "Delete Permanently"
(`CACHET_UIUX_Design_Specification_v1.md` §1.4, §4.3), calling CAS-014's `POST /quarantine/remove`
with the selected `quarantine_entry_id` values.

No `confirm: true` / second-confirmation dialog is required for this action — CAS-014 §8 and §11
establish that removal is reversible in effect (the file itself is untouched and can be re-quarantined
later) even though the catalog row itself is a genuine hard delete with no row-level undo. This is a
materially different posture than "Delete Permanently," which does require a confirmation dialog
(FR-17). The UI must not conflate the two.

Also update `CACHET_UIUX_Design_Specification_v1.md` §4.3 (documentation-only) to describe this third
action alongside the two it already documents, and to correct any residual `/quarantine/release`
naming to `/quarantine/remove` per CAS-014 §14's route-naming resolution.

**BLOCKED items for this spec — require design/product input before `status → ready`:**
- Exact placement, label copy, and iconography for the "Remove from Queue" action relative to the
  existing "Send to Quarantine" / "Delete Permanently" actions in the Quarantine Queue modal.
- Whether the action appears per-row, in a multi-select batch toolbar, or both (mirroring "Delete
  Permanently (3)"-style batch count labeling per §4.3).
- Toast/notification copy for success and partial-failure (`Failed`/`file-not-found`) outcomes,
  consistent with how "Delete Permanently" surfaces per-file batch results today.

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** CAS-014 §14 (resolved 2026-09-22, R. Devarajan, TraceCounts) — CAS-014 builds the API
  endpoint but deliberately ships with no UI consumer (`touches.ui: false`); this spec closes that gap.
- **Risk if not changed:** `POST /quarantine/remove` (CAS-014) ships as a functioning but unreachable
  endpoint — a Keyword Search reviewer still has no way, from the UI, to take a mis-quarantined file out
  of the queue without either leaving it there or using the destructive "Delete Permanently" action.

---

## §4 Blast Radius (Graph Impact)

**Graphify not yet vendored for this repo** — manual analysis only, per this project's CLAUDE.md status
note.

### Primary Change Target
File: Quarantine Queue Blazor component (path TBD — not yet built as of this spec's drafting; likely
`src/Cachet.ArchiveSync.Web/Pages/Quarantine/*.razor` or equivalent, matching whatever CAS-008's UI
counterpart used, if one exists — **BLOCKED**: confirm actual file at `/spec-change`/`/implement` time).

### Direct Dependents
TODO — depends on the as-built Quarantine Queue component structure, which does not yet exist in
`src/Cachet.ArchiveSync.Web/` as of 2026-09-22.

### Downstream Modules
- **Quarantine** (this spec's entire scope) — UI only, consumes CAS-014's already-built API contract
  without modifying it.

### Cross-Project Risk
`Web`-only change, consuming an existing `Api` contract (CAS-014). No `Core`, `Api`, or `Worker` file
is expected to change.

### God Node Involvement
None identified — no graph available; manual read found no existing high-degree Blazor component this
would attach to.

### Graphify Queries Run
None — not vendored. A future session with graphify available should run:
```
python -m graphify explain "QuarantineController"
python -m graphify query "Quarantine Queue Blazor component" --budget 1000
```

---

## §5 Files to Change

TODO — cannot be finalized until the actual Quarantine Queue Blazor component location is confirmed
(§4). Expected shape, subject to confirmation at `/spec-change` time:

| File | Change Type | Description |
|------|------------|--------------|
| TODO: Quarantine Queue `.razor` component | modify | Add "Remove from Queue" action, wired to `POST /quarantine/remove` |
| TODO: corresponding `.razor.cs` code-behind or component logic file | modify | Batch-select handling, result toast rendering |
| `CACHET_UIUX_Design_Specification_v1.md` §4.3 | modify (doc, outside `src/`) | Document the third action; correct residual `/release` naming to `/remove` |
| Corresponding Blazor component tests (`src/Cachet.ArchiveSync.Web.Tests/`) | add/modify | Per §13 |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `src/Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs` | CAS-014 already built and owns `POST /quarantine/remove`; this spec only consumes it |
| `src/Cachet.ArchiveSync.Api/Services/QuarantineRemoveService.cs` | Same — CAS-014's contract is taken as given |
| Any `Cachet.ArchiveSync.Worker` file | Not touched — `touches.worker: false` |

---

## §7 Database Changes

N/A — `touches.database: false`. No schema or stored-procedure change.

---

## §8 API Contract

N/A — `touches.api: false`. This spec consumes CAS-014's existing `POST /quarantine/remove` contract
unmodified; it does not define or change an API contract itself.

---

## §9 Validations

TODO — client-side validation rules (e.g., disable the action when no rows are selected) to be defined
once the component's actual structure is confirmed. Server-side validation (V1–V5) is already owned by
CAS-014 and is not restated or duplicated here.

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | The Quarantine Queue screen exposes a "Remove from Queue" action reachable without navigating away from the queue view | TODO once component exists |
| SC-2 | Selecting one or more rows and invoking "Remove from Queue" calls `POST /quarantine/remove` with the selected `quarantine_entry_id` values and reflects per-file success/failure results to the user | TODO |
| SC-3 | No confirmation dialog is shown for "Remove from Queue" (distinguishing it from "Delete Permanently", which does show one) | TODO |
| SC-4 | `CACHET_UIUX_Design_Specification_v1.md` §4.3 is updated to describe the new action and uses `/quarantine/remove` (not `/quarantine/release`) | Doc review, not an automated test |

---

## §11 Security Notes

No new security surface — this spec adds no new endpoint, auth policy, or B2/credential handling. The
underlying `POST /quarantine/remove` action already requires only `CachetAuthenticated`
(CAS-014 §11); this UI must not gate the button behind a stricter policy than the endpoint itself
enforces, nor bypass the endpoint's own authorization.

---

## §12 Rollback Plan

1. **Code rollback:** revert the Blazor component change; the "Remove from Queue" UI action disappears,
   `POST /quarantine/remove` (CAS-014) remains reachable via direct API call but has no UI consumer
   again — equivalent to CAS-014's pre-CAS-017 state. No data impact.
2. **Database rollback:** N/A — no schema change.
3. **Config rollback:** N/A.
4. **Task-queue rollback:** N/A — this is a synchronous UI action, not a queued task.

---

## §13 Test Plan

TODO — to be filled once the Quarantine Queue Blazor component's actual structure is known. Expected
shape: bUnit or equivalent component tests asserting the action calls `POST /quarantine/remove` with
the correct payload, renders per-file results, and shows no confirmation dialog.

Coverage target: ≥ 85% on changed files, per this project's standard gate.

---

## §14 Open Questions

- [ ] **Exact Quarantine Queue Blazor component file/location** — not yet built in `src/` as of this
      spec's drafting (WBS UI tasks for Quarantine were `Not Started` at BRD v2.3). Must be confirmed
      before `status → ready`.
- [ ] **UI placement, copy, and iconography for "Remove from Queue"** (§2 BLOCKED items) — needs
      design/product sign-off, ideally alongside whatever UI/UX design doc addendum is drafted for
      §4.3.
- [ ] **Whether this spec should be merged into whatever spec eventually builds the Quarantine Queue
      screen itself**, rather than standing alone — if the Quarantine Queue UI has not been built yet at
      all (§1 TODO), "add a third action to an existing screen" may not be the right framing; this may
      instead need to be a requirement folded into that screen's own initial build spec.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-22 | Opened as CAS-014 §14 follow-up; not yet drafted in full |
| Reviewer | | | Pending |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] §14 open questions are resolved before this spec reaches `ready`
- [ ] The UI action calls CAS-014's `POST /quarantine/remove` contract exactly as CAS-014 §8 defines
      it, with no request/response shape drift
- [ ] No confirmation dialog was added for this action (§2, §10 SC-3) — would incorrectly imply
      Permanent-Delete-level irreversibility
- [ ] `CACHET_UIUX_Design_Specification_v1.md` §4.3 is updated and uses `/quarantine/remove`, not the
      superseded `/quarantine/release` name
- [ ] No `Api`, `Core`, `Worker`, or database file was modified (`touches.api/database/worker: false`)

### Verification Report Format
Per template: `CAS-017.review.md` — overall verdict, per-SC verdict table, scope violations.
