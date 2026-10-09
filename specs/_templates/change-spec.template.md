---
id: SPEC_ID
target_app: cachet-archive-sync
status: draft
owner: devarajan@tracecounts.org
created: YYYY-MM-DD
priority: P1
change_type: bug-fix               # bug-fix | enhancement | refactor | security | deprecation
related_specs: []
touches:
  database: false
  api: false
  ui: false
  worker: false
  b2: false
  config: false
  security: false
migrations: []
---

# SPEC_ID — Change: Short Title

## §1 Current Behavior

> Describe exactly what the system does TODAY. Be specific: file names, method names, routes, DB columns.
> This section is the ground truth — the reviewer will verify the change started from this state.

TODO: Describe current behavior with specifics (file:line references where helpful).

---

## §2 Desired Behavior

> Describe exactly what the system must do AFTER this change.
> Must be verifiable — "faster" is not verifiable, "responds in < 200ms at p95" is.

TODO: Describe desired behavior.

---

## §3 Reason for Change

> Why is this change being made? Bug report, BRD revision, security finding, client decision?

- **Type:** `bug-fix | enhancement | refactor | security | deprecation`
- **Trigger:** TODO (e.g. "BRD §12.2 decision resolved: login method confirmed as SSO")
- **Risk if not changed:** TODO

---

## §4 Blast Radius (Graph Impact)

> Pre-filled by `/spec-change` from graphify analysis, once vendored (see CLAUDE.md). Until then,
> fill this section from a manual grep/read of the codebase and say so explicitly.
> Lists every node/file that may be affected by this change — used to bound the reviewer's scope check.

### Primary Change Target
File: TODO
Entity: TODO
Current edges (from graphify explain, or manual read): TODO

### Direct Dependents (nodes with edges INTO the changed entity)
TODO: (filled by /spec-change)

### Downstream Modules
TODO: (filled by /spec-change — which of Auth / Keyword Search / O-Drive Sync / Quarantine /
Task Manager / Audit Log / Reporting feel this change?)

### Cross-Project Risk
TODO: Does this change propagate across the Api ↔ Web ↔ Worker boundary within
`cachet-archive-sync`? A contract change in `Cachet.ArchiveSync.Core` shared by Api and Worker,
or an Api change the Blazor Web project calls, needs to be traced here.

### God Node Involvement
TODO: Is the changed entity or any dependent a god node (>10 edges)? High-risk flag if yes.

### Graphify Queries Run
```
python -m graphify explain "..."
python -m graphify path "..." "..."
python -m graphify query "what depends on ..."
```
(Or: "graphify not yet vendored — analysis done via manual code read.")

---

## §5 Files to Change

> Explicit list of files that will be modified. The reviewer will check that ONLY these files changed
> (plus test files). Any edit outside this list is a scope violation.

| File | Change Type | Description |
|------|------------|--------------|
| TODO | modify / add / delete | |

---

## §6 Files NOT to Change

> Files graphify (or a manual read) flagged as connected but that this spec deliberately leaves
> untouched. The reviewer will verify these are unmodified.

| File | Why left untouched |
|------|--------------------|
| TODO | |

---

## §7 Database Changes

> Or `N/A`. If the change touches a stored procedure name or signature, list it here.

### Changed Objects
| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| TODO | cas / dbo | parameter added / renamed / removed | |

### Migration Required
TODO: Yes / No. If yes, the change produces TWO consistent artifacts (hybrid model):
  - Migration: `Database/migrations/<YYYYMMDD_HHMMSS>__<spec-id>-<slug>.sql` (append-only)
  - Mirror update(s): `Database/CachetArchiveSync/{Entities,Stored-Procedures}/<schema>.<object>.sql`

---

## §8 API Contract Changes

> Or `N/A`. Breaking changes MUST bump the API version.

| Endpoint | Field | Before | After | Breaking? |
|----------|-------|--------|-------|-----------|
| TODO | | | | Yes / No |

---

## §9 Validations Changed

> Validations being added, modified, or removed. Map to test names.

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | TODO | TODO | `Test_V1_...` |

---

## §10 Success Criteria

> Each SC is specific and verifiable. The reviewer checks every one independently.

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | TODO | `Test_SC1_...` |

---

## §11 Security Notes

> Required if change_type = security, or if the changed code touches the B2 key, session/auth,
> or the audit log.

TODO: Describe security implications of this change, or state "No security impact."

---

## §12 Rollback Plan

> If this change causes a production issue, how do we revert?

1. TODO: Step-by-step rollback
2. Database rollback: TODO (if applicable)
3. Config rollback: TODO (if applicable)
4. Task-queue rollback: TODO — if this change alters the task-queue table shape or job
   contract, what happens to any in-flight `Queued`/`Running` rows created under the old
   contract (FR-18–FR-21)? They must not be silently dropped or corrupted.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| TODO | | |

Coverage target: ≥ 85% on changed files.

---

## §14 Open Questions

- [ ] TODO

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | | | |
| Reviewer | | | |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches what is actually in the code BEFORE the change
- [ ] Desired behavior (§2) is fully implemented
- [ ] Only files in §5 were modified (§6 files are untouched)
- [ ] All §10 Success Criteria have passing tests
- [ ] All §9 Validations are implemented and tested
- [ ] §8 API contract is correct (no undeclared breaking changes)
- [ ] §11 Security Notes implemented as described
- [ ] §4 Blast Radius is accurate — no unintended side effects in connected nodes
- [ ] §12 in-flight task-queue rows (if applicable) are handled, not silently broken

### Verification Report Format
Reviewer writes `<spec-id>.review.md`:
- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5 file list)
- Unintended blast radius (graphify, or manual read, found changes in §6 files)
- Security gaps
