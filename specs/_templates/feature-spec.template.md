---
id: SPEC_ID                          # e.g. CAS-001, CAS-012
target_app: cachet-archive-sync      # the only target_app on this project
status: draft                        # draft | ready | in-progress | review | merged | abandoned
owner: devarajan@tracecounts.org
created: YYYY-MM-DD
priority: P1                         # P0 ship-blocker | P1 release | P2 nice-to-have | P3 future
related_specs: []
touches:
  database: false
  api: false
  ui: false
  worker: false                      # async task-queue / background-worker logic (FR-18–FR-22a)
  b2: false                          # Backblaze B2 integration (FR-03, FR-08, FR-16)
  config: false
  security: false
migrations: []
---

# SPEC_ID — Feature Title

## §1 Summary

> One paragraph. What changes, why it's needed, and who benefits. Cite the BRD FR-ID(s) this
> implements (e.g. "Implements FR-04–FR-09").
> Do NOT list acceptance criteria here — save those for §10.

TODO: Write summary.

---

## §2 User Story

> As a **[role — e.g. TraceCounts staff reviewer, Cachet business owner, background worker]**,
> I want **[capability]**, so that **[outcome]**.

TODO: Write user story.

---

## §3 In-Scope

> Explicit bullets only. If it's not listed here, it's out of scope.

- TODO: List in-scope items.

---

## §4 Out-of-Scope

> Prevents scope creep. Be specific about what this spec deliberately does NOT address.

- TODO: List out-of-scope items.
- Anything flagged in §17 Graph Impact that we are not changing in this spec.
- Note explicitly if this spec assumes a decision that is still open per BRD §12.2 (login
  method, drive encryption-at-rest) — if so, it belongs in §15 Open Questions instead, not here.

---

## §5 Database Objects

> Tables, columns, indexes, foreign keys, stored procedures, seed data. Or `N/A`.

### Tables / Columns

TODO: Describe schema changes or N/A.

### Stored Procedures

| Name | Schema | Action | Notes |
|------|--------|--------|-------|
| TODO | cas / dbo | ins / upd / del / get / lst | |

### Migration Strategy

TODO: Describe how migration will be applied. Hybrid model — db-author writes BOTH:
  - Idempotent timestamped migration: `Database/migrations/<YYYYMMDD_HHMMSS>__<spec-id>-<slug>.sql`
  - Per-object mirror update(s): `Database/CachetArchiveSync/{Entities,Stored-Procedures}/<schema>.<object>.sql`

---

## §6 API Endpoints

> One table per endpoint. Include request/response contracts and all status codes. Or `N/A` if
> this spec is UI-only or database-only.

### Endpoint: `VERB /api/v1/path`

**Auth:** `[Authorize(Policy = "cachet:read")]` (adjust policy name to the actual module —
e.g. `cachet:quarantine`, `cachet:admin` — once the auth/session design, BRD §10.3, is final)
**Async:** Yes / No — if this endpoint submits a search/scan/report job (FR-18), it must return
a task reference immediately, not block on job completion (see `async-task-queue` skill).

**Request:**
```json
{
  "field": "type — description"
}
```

**Response 200:**
```json
{
  "data": {}
}
```

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Success |
| 202 | Accepted — task queued (async endpoints only, FR-18) |
| 400 | Validation failure (V1, V2) |
| 401 | Missing or invalid session |
| 403 | Insufficient scope |
| 404 | Resource not found (or excluded — see FR-08 edge case) |
| 409 | Conflict (e.g. duplicate submission) |

---

## §7 Validations

> Each validation must have: rule, failure HTTP status, response body, and a mapped test name.

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | TODO | 400 `{ "error": "..." }` | `Test_V1_...` |
| V2 | | | |

---

## §8 UI / UX

> Blazor Server components (`.razor`), view/component structure, navigation, design decisions.
> Or `N/A`. Reference the relevant BRD §11 wireframe screen if one exists for this feature.

TODO: Describe UI changes or N/A.

---

## §9 Configuration Changes

> New `appsettings.json` keys, feature flags, environment overrides. Never a raw B2 application
> key value — only the config *key name* that will hold a reference to it (FR-03).

| Key | Default | Description | Env Override |
|-----|---------|-------------|--------------|
| TODO | | | |

---

## §10 Success Criteria

> Each SC must be specific, measurable, and mapped 1:1 to a test name.
> The reviewer will verify every SC independently against the code.

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | TODO — describe observable, verifiable outcome | `Test_SC1_...` |
| SC-2 | | |

---

## §11 Telemetry

> Structured log events, metrics, correlation IDs.

```csharp
// TODO: _logger.LogInformation("{Action} completed for {TaskId}", action, taskId);
```

- Never log: the B2 application key or any part of it, a session token/cookie value, full file
  content, or raw archive file paths beyond what the audit log (FR-23) already records.

---

## §12 Security Notes

> Threat model, credential handling, audit trail requirements.
> Required if `touches.security = true`, or `touches.b2 = true`, or this spec is auth-related.

TODO: Describe security considerations, or state "No security impact."

### B2 Credential Handling
TODO: Does this endpoint/screen ever come near the B2 application key? It must not (FR-03) —
confirm and describe how the server-side B2 client is invoked without exposing the key.

### Audit Trail
TODO: What gets written to the audit log table (FR-23) — user, tab/module, action, file/path,
timestamp, result? Confirm the write happens before the success response is returned, not after.

### Session / Auth Requirements
TODO: Which auth requirement applies (authenticated session required, specific role/policy)?
Note if this spec depends on the still-open login method decision (BRD §12.2) — if so, this
must also appear in §15 Open Questions.

---

## §13 Test Plan

> Test class names, test method names, what each proves.
> Tests must map back to §7 Validations and §10 Success Criteria.

### Unit Tests
| Class | Method | Proves |
|-------|--------|--------|
| TODO | | |

### Integration Tests
| Class | Method | Proves |
|-------|--------|--------|
| TODO | | |

### Coverage Target
Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

> Deployment steps, rollback plan, migration order.

1. TODO: Deploy database migration first (if applicable)
2. TODO: Deploy Api / Web / Worker (note if these must deploy together vs. independently)
3. Rollback: TODO

---

## §15 Open Questions

> Unresolved items that must be answered before status → ready. Explicitly list either of the
> two known-open BRD items (§12.2 — login method, drive encryption-at-rest) if this spec
> depends on one of them.

- [ ] TODO: Question 1?
- [ ] TODO: Question 2?

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | | | |
| Reviewer | | | |

---

## §17 Graph Impact

> Pre-filled by `/spec-new` from graphify analysis, once graphify is vendored into this repo
> (see CLAUDE.md's graphify section). Until then, fill this section from a manual read of the
> nearest existing code and state that graphify was not available.

### God Nodes in Blast Radius
> Nodes with >10 edges that this feature touches or creates.

TODO: (filled by /spec-new from graphify output, or manual read)

### Communities / Modules Affected
> Which of Auth / Keyword Search / O-Drive Sync / Quarantine / Task Manager / Audit Log /
> Reporting are involved? Also note which of the three projects (Api / Web / Worker) this
> touches — a feature spanning all three should confirm each's scope in §3/§4.

TODO: (filled by /spec-new)

### Surprising Connections
> Unexpected dependencies graphify flagged (or found on manual read).

TODO: (filled by /spec-new — if none, state "None identified")

### Graphify Queries Run
```
python -m graphify query "..."
python -m graphify explain "..."
python -m graphify path "..." "..."
```
(Or: "graphify not yet vendored — analysis done via manual code read.")

---

## §18 Verification Plan

> How will the independent reviewer verify this implementation?
> The reviewer sees only this spec and the codebase — no implementation notes.

### Reviewer Checklist
The reviewer will independently verify each of the following. Mark N/A only if the section itself is N/A.

- [ ] All §10 Success Criteria have a passing test
- [ ] All §7 Validations are implemented and tested
- [ ] All §6 API endpoints exist with correct verb/route/auth
- [ ] §12 Security Notes are implemented as described (B2 key never browser-visible; audit log
      write happens before success response; correct session/auth requirement enforced)
- [ ] §5 Database objects exist in schema (if database: true)
- [ ] §9 Config keys are present in appsettings.json
- [ ] §11 Telemetry events are emitted (no B2 key / session token / PII in logs)
- [ ] §17 Graph Impact matches actual code changes (no unintended nodes)
- [ ] No out-of-scope changes in §4 were made

### Verification Report Format
The reviewer writes `<spec-id>.review.md` with:
- Overall verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Security gap list (if any)
- Scope violations (if any)
