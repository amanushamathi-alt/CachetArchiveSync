---
id: CAS-036
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-28
priority: P2
change_type: enhancement
related_specs: [CAS-001, CAS-008, CAS-014, CAS-018, CAS-034]
touches:
  database: false
  api: false
  ui: false
  worker: false
  b2: true
  config: false
  security: false
migrations: []
---

# CAS-036 — Change: Bounded Retry/Backoff for `UserScopedB2DeleteClient` (Permanent Delete)

## §0 Context Note (read this first)

This spec was requested against WBS Task 33 ("B2 permanent-delete integration"), drafted from: "how
the B2 delete module's exact B2 API call sequence works, how success/failure is confirmed and
returned to the caller, how a partial failure is handled and surfaced, and whether this module is
shared with the O-Drive Sync Purge action." Before drafting, the current repo state was checked
against that ask, per this project's established `/spec-change` pattern (CAS-034 §0, CAS-035 §0):

- **B2 API call sequence for permanent deletion** — already designed and implemented, `ready`/
  `merged`: `QuarantinePermanentDeleteService.DeleteAllVersionsAsync`
  ([QuarantinePermanentDeleteService.cs:293-321](../../src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs#L293-L321))
  enumerates every version of the object via `IUserScopedB2DeleteClient.ListFileVersionsAsync` and
  deletes each via `DeleteFileVersionAsync` (CAS-008 §6.4, CAS-018 §2). No gap found.
- **Success/failure confirmed and returned to the caller** — already designed and implemented,
  `ready`: per-file `QuarantineDeleteResultItem.Result`/`Reason`, aggregated into
  `QuarantineDeleteResult` and returned by `QuarantineController` (CAS-008 §6, §10 SC-3/SC-5). No
  gap found.
- **Partial-failure handling and surfacing** — already designed and implemented, `ready`: each
  file's B2-delete/local-delete/status-transition steps are wrapped independently
  ([QuarantinePermanentDeleteService.cs:118-260](../../src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs#L118-L260)),
  a failure at any step is audited (`DeleteFailedB2`/`DeleteFailedLocal`) and reported per-file
  without blocking or rolling back the rest of the batch (CAS-008 §7 V5/V6, §10 SC-5, FR-17's
  no-rollback design). No gap found. **Note:** the task's own framing ("after it's already removed
  from the local cache") describes CAS-001's original 2026-09-10 design; BRD v2.3 §12.3 item 8
  (2026-09-22) reversed this to B2-then-local, which is what is actually implemented (Step 1 = B2
  delete, Step 2 = local soft-delete, per the file reference above) — flagged so this correction
  isn't lost.
- **Whether this is a shared module with O-Drive Sync's Purge action** — already considered and
  deliberately decided, `merged`: no.
  [QuarantinePermanentDeleteService.cs:280-286](../../src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs#L280-L286)
  documents the reasoning — sharing would require an Api→Worker dependency (`SyncPurgeTaskProcessor`
  lives in `Cachet.ArchiveSync.Worker`), so the all-versions-delete *pattern* is intentionally
  duplicated rather than extracted into one cross-project module (Karpathy P4 — a second call site
  of the same pattern, not yet a second *shared* call site). No gap found; not re-opened here.
- **Error/retry handling for a transient B2 failure during Permanent Delete** — this is the one
  genuine gap. CAS-034 (status `review`, delivered 2026-09-25) added bounded retry/backoff for
  transient failures (`429`/`5xx`/network/internal timeout) to every operation on `IB2Client` — but
  its §6 ("Files NOT to Change") explicitly and deliberately excluded
  `UserScopedB2DeleteClient.cs` (the class CAS-018 introduced specifically for Permanent Delete's
  own B2 calls, under the acting user's live authorization rather than the system key), calling
  that exclusion "a distinct design decision this spec does not make." Today,
  `UserScopedB2DeleteClient`'s four HTTP calls (`b2_authorize_account`, `b2_list_buckets`,
  `b2_list_file_versions`, `b2_delete_file_version`) each call `EnsureSuccessAsync`
  ([UserScopedB2DeleteClient.cs:204-235](../../src/Cachet.ArchiveSync.Api/B2/UserScopedB2DeleteClient.cs#L204-L235)),
  which throws immediately on any non-success response — a merely transient blip (rate limit,
  momentary 5xx) is indistinguishable from a genuine failure and hard-fails that file as
  `DeleteFailedB2` on the first attempt, unlike every other B2-touching path in the app (search
  cross-check, sync Copy/Update/Purge, mirror population — all via `IB2Client`), which now retries.
  This spec closes that gap only. It does not modify `B2Client.cs`, `IB2Client.cs`, or
  `IUserScopedB2DeleteClient.cs`'s public contract, and does not re-litigate CAS-018's per-user-key
  design or CAS-008/CAS-014's sequencing/audit contract.

Per the project's spec-change workflow (`.claude/commands/spec-change.md`), this is a **change to
existing, merged code**, not a new feature — hence a change spec against CAS-018's delivered
module, not a `/spec-new` design doc. This closes out the remaining scope of WBS Task 33 (whose
"Not Started" status in `1.WBS/CACHET_WBS_v1.md` is stale relative to the above).

---

## §1 Current Behavior

`UserScopedB2DeleteClient` ([UserScopedB2DeleteClient.cs](../../src/Cachet.ArchiveSync.Api/B2/UserScopedB2DeleteClient.cs)),
`QuarantinePermanentDeleteService`'s only B2-calling dependency, makes four HTTP calls, each via a
single `_httpClient.SendAsync` followed immediately by `EnsureSuccessAsync`:

1. `AuthorizeSystemKeyAsync` (lines 122-148) → `b2_authorize_account`, called at most once per
   process inside `GetBucketIdAsync`'s cache-fill path (lines 72-120, guarded by `_bucketLock`).
2. The `b2_list_buckets` call inside `GetBucketIdAsync` (lines 89-98), same cache-fill path.
3. `ListFileVersionsAsync` (lines 152-181) → `b2_list_file_versions`, called once per page per file
   from `QuarantinePermanentDeleteService.DeleteAllVersionsAsync`.
4. `DeleteFileVersionAsync` (lines 185-200) → `b2_delete_file_version`, called once per object
   version.

`EnsureSuccessAsync` (lines 204-235) throws `B2NotFoundException` (404) or `B2ApiException` (any
other non-success status) on the **first** non-success response — there is no retry of any kind in
this class today, in contrast to `B2Client`'s `SendWithRetryAsync` (CAS-034 §2), which wraps the
equivalent operations on the system-key client.

Any exception from `UserScopedB2DeleteClient` propagates unchanged into
`QuarantinePermanentDeleteService.DeleteAllVersionsAsync`
([QuarantinePermanentDeleteService.cs:293-321](../../src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs#L293-L321)),
whose caller (`DeleteAsync`, lines 138-170) catches it in a per-file `try/catch`, audits
`AuditAction.DeleteFailedB2`, and reports that file `Failed`/`b2-delete-failed` — a request-level
outcome that requires the user to re-submit Permanent Delete for that file. This per-file isolation
and audit contract (CAS-008 §7 V5, §10 SC-5) is correct and is **not** changed by this spec; only
the trigger condition for reaching it is narrowed.

---

## §2 Desired Behavior

Each of `UserScopedB2DeleteClient`'s four HTTP calls (§1) is wrapped in the same bounded,
exponential-backoff retry policy CAS-034 §2 gave `B2Client`, reusing the **same existing**
configuration keys (no new keys — `B2:MaxRetryAttempts`, default `3`; `B2:RetryBaseDelayMs`,
default `500`; both already present in `appsettings.Development.json` for the Api project, added by
CAS-034):

- Retries only on: HTTP `429`/`500`/`502`/`503`/`504`, `HttpRequestException`, or an internal
  (non-caller-requested) `OperationCanceledException`.
- Does **not** retry: `400`/`403`/`404`, or a caller-requested cancellation (`ct.IsCancellationRequested`)
  — these surface on the first attempt, exactly as today.
- Backoff: `RetryBaseDelayMs * 2^(attempt-1)` plus 0–100ms jitter, capped at 8000ms; a `429`'s
  `Retry-After` header is honored in place of the computed delay when it implies a longer wait
  (CAS-034 §2's exact formula).
- After `MaxRetryAttempts` consecutive retryable failures, the original exception (same type/content
  attempt 1 would have thrown today) propagates to the caller unchanged — `QuarantinePermanentDeleteService`'s
  existing `DeleteFailedB2` handling requires no change.
- No retry log line includes the B2 key, the system authorization token, or the acting user's
  authorization token (extends CAS-001 SC-5 / CAS-034 SC-4).

Net effect: a transient B2 blip during Permanent Delete that resolves within the retry budget now
succeeds instead of hard-failing that file; a genuinely-gone-or-forbidden object, or an outage that
outlasts the retry budget, fails exactly as it does today.

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** CAS-034 §6/§14 deliberately deferred adding retry/backoff to
  `UserScopedB2DeleteClient` as "a distinct design decision this spec does not make." WBS Task 33
  (this spec's origin, §0) asks how a B2 delete partial failure is handled and surfaced; today,
  Permanent Delete is the one B2-write path in the app without transient-failure resilience, an
  inconsistency worth closing now that CAS-034's pattern and config keys already exist.
- **Risk if not changed:** A merely transient B2 hiccup (rate limiting, momentary 5xx) during
  Permanent Delete hard-fails that file as `DeleteFailedB2` — an audited, request-terminal outcome
  requiring the `CachetFullAccess` user to re-submit deletion for that file — while the functionally
  identical O-Drive Sync Purge action (via `IB2Client`) already tolerates the same blip
  transparently. No data-integrity risk (the per-file isolation and no-rollback contract are
  unaffected either way), but an avoidable, inconsistent reliability gap and unnecessary audit-log
  noise/user friction.

---

## §4 Blast Radius (Graph Impact)

Analysis performed via `python -m graphify explain` (graph vendored and present at
`graphify-out/graph.json`, `GRAPH_REPORT.md`; CLAUDE.md's "no initial graph built yet" note is
stale) plus a manual grep, since the graphify BFS query traversal at depth 2 returned a wide,
low-precision node set not useful for bounding this specific change.

### Primary Change Target
File: `src/Cachet.ArchiveSync.Api/B2/UserScopedB2DeleteClient.cs`
Entity: `UserScopedB2DeleteClient` (implements `IUserScopedB2DeleteClient`)
Current edges (`graphify explain "UserScopedB2DeleteClient"`): degree 1 — the file's only recorded
edge is `[contains] UserScopedB2DeleteClient` (file → class). **Not a god node.**

### Direct Dependents (nodes with edges INTO the changed entity)
Confirmed by `grep -rln "IUserScopedB2DeleteClient|UserScopedB2DeleteClient" src --include=*.cs`
(excluding `bin`/`obj`):

- `src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs` — the only production
  consumer, calling `GetBucketIdAsync`/`ListFileVersionsAsync`/`DeleteFileVersionAsync` through the
  unchanged `IUserScopedB2DeleteClient` interface.
- `src/Cachet.ArchiveSync.Api/Program.cs` — DI registration only (singleton, same
  `IHttpClientFactory`-resolved `"B2Client"` named client); constructor signature is unchanged by
  this spec, so no edit needed here.
- Test-only dependents: `UserScopedB2DeleteClientTests.cs`, `QuarantinePermanentDeleteServiceTests.cs`
  (mocks the interface — unaffected by an internal retry change), `LogHygieneTests.cs`,
  `QuarantineApiWebApplicationFactory.cs`, `QuarantineControllerTests.cs` (integration test factory
  wiring — unaffected).

### Downstream Modules
Quarantine (Permanent Delete, FR-16/FR-17) only. Audit Log is touched only in the sense that the
*trigger frequency* of `DeleteFailedB2` audit rows decreases for transient causes — the audit
write path itself (`IAuditLogService`) is untouched.

### Cross-Project Risk
None. `UserScopedB2DeleteClient` and its only consumer both live in `Cachet.ArchiveSync.Api`; no
`Cachet.ArchiveSync.Worker` or `Cachet.ArchiveSync.Web` reference exists (confirmed by the same grep
— zero matches outside `Cachet.ArchiveSync.Api*`). `Cachet.ArchiveSync.Core`'s
`IUserScopedB2DeleteClient` interface is read but not modified (§6).

### God Node Involvement
No. Degree 1 at the file level; a single production consumer at the class level.

### Graphify Queries Run
```
python -m graphify explain "UserScopedB2DeleteClient"
python -m graphify query "what depends on UserScopedB2DeleteClient" --budget 1500
```
The `query` traversal (BFS depth 2, 144 nodes) was too broad to isolate true direct dependents
(it surfaces same-community test files transitively); the grep in the previous section is the
authoritative dependent list for this spec, per Step 2f's manual fallback.

### Decomposition Check (P9)
Single file changed in production code, one test file changed, no DB/API/UI/config touched, no
project-boundary crossing. Well under every split threshold in
`coding-agent/decisions/spec-decomposition-guideline.md` — no split warranted.

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `src/Cachet.ArchiveSync.Api/B2/UserScopedB2DeleteClient.cs` | modify | Add a private bounded retry/backoff wrapper (mirroring `B2Client.SendWithRetryAsync`/`ComputeDelay`/`IsRetryableStatusCode`/`TryGetRetryAfterMs` — CAS-034 §2) around all four HTTP calls (§1); read `B2:MaxRetryAttempts`/`B2:RetryBaseDelayMs` from the existing `IConfiguration` already injected into the constructor, with the same `?? 3`/`?? 500` in-code defaults as `B2Client`; log each retry attempt without the key/token (§2). |
| `src/Cachet.ArchiveSync.Api.Tests/UserScopedB2DeleteClientTests.cs` | modify | New test cases per §13. |

No new files. No config file edits — `B2:MaxRetryAttempts`/`B2:RetryBaseDelayMs` already exist in
`src/Cachet.ArchiveSync.Api/appsettings.Development.json` (added by CAS-034) and are read directly
by key name; `UserScopedB2DeleteClient`'s constructor already receives the same `IConfiguration`
instance `B2Client` reads them from.

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `src/Cachet.ArchiveSync.Api/B2/B2Client.cs` | Already has its own, independently-shipped retry/backoff (CAS-034) for the system-key client. This spec intentionally duplicates the *algorithm* rather than extracting a shared helper both classes call — see §14 for why extraction is deferred, not silently skipped. |
| `src/Cachet.ArchiveSync.Core/Interfaces/IUserScopedB2DeleteClient.cs` | Interface contract unchanged — retry is an internal implementation detail, same posture CAS-034 §6 took for `IB2Client`. No method signature, parameter, or return type changes. |
| `src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs` | Consumes `IUserScopedB2DeleteClient` through its existing, unchanged interface; its per-file `DeleteFailedB2`/`DeleteFailedLocal` handling is exception-type-agnostic (`catch (Exception ex) when (ex is not OperationCanceledException)`) and requires no change to keep working once retries are exhausted. |
| `src/Cachet.ArchiveSync.Api/Program.cs` | DI registration unchanged — `UserScopedB2DeleteClient`'s constructor signature (`HttpClient`, `IConfiguration`, `ILogger<UserScopedB2DeleteClient>`) does not change; the two config keys are read by name from the same already-injected `IConfiguration`. |
| `src/Cachet.ArchiveSync.Worker/*` (all files) | No reference to `UserScopedB2DeleteClient`/`IUserScopedB2DeleteClient` exists in the Worker project (confirmed §4) — this remains an Api-only concern. |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json`, `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | `B2:MaxRetryAttempts`/`B2:RetryBaseDelayMs` already present from CAS-034 — no new keys, no value changes. |
| `docs/environment-variables.md` | These are non-secret, ordinary appsettings values (same category CAS-034 §5 already exempted from this doc). Not applicable here either. |

---

## §7 Database Changes

N/A — no database objects touched.

---

## §8 API Contract Changes

N/A — no HTTP endpoint, request, or response shape changes. `POST /quarantine/delete`'s contract
(CAS-008 §6) is unchanged; only the internal reliability of one of its dependencies improves.

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | No retry exists for any of `UserScopedB2DeleteClient`'s four HTTP calls | A retryable failure (`429`/`5xx`/transient network/internal timeout) on any of the four calls is retried up to `B2:MaxRetryAttempts` (default 3) with exponential backoff before surfacing to the caller | `Test_V1_TransientFailureRetriedUpToMaxAttempts` |
| V2 | N/A | A non-retryable failure (`400`/`403`/`404`, or a caller-cancelled request) is never retried — surfaces on the first attempt, unchanged from today | `Test_V2_NonRetryableFailureSurfacesImmediately` |
| V3 | N/A | A `429` response's `Retry-After` header is honored in preference to the computed exponential-backoff delay, mirroring `B2Client`'s exact formula (CAS-034 V4) | `Test_V3_RetryAfterHeaderHonoredOverComputedBackoff` |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A transient failure (`429`/`500`/`502`/`503`/`504`, or `HttpRequestException`) on any of the four calls, followed by a success within `B2:MaxRetryAttempts`, returns the successful result to the caller — the caller never observes an exception | `Test_SC1_TransientFailureEventuallySucceeds` |
| SC-2 | After `B2:MaxRetryAttempts` consecutive transient failures, the original failure (same exception type/content attempt 1 would have thrown today) propagates to the caller — no new exception type, no swallowed failure | `Test_SC2_ExhaustedRetriesSurfacesOriginalFailure` |
| SC-3 | Caller-requested cancellation (`ct.IsCancellationRequested`) during a retry delay is honored immediately — no further attempts, `OperationCanceledException` propagates | `Test_SC3_CallerCancellationDuringRetryDelayHonoredImmediately` |
| SC-4 | No retry log line, at any attempt, includes the system key, the acting user's B2 application key, or either authorization token, even partially (extends CAS-001 SC-5 / CAS-018 §11 / CAS-034 SC-4) | `Test_SC4_NoKeyOrTokenLeakageInRetryLogLines` |
| SC-5 | `QuarantinePermanentDeleteService`'s existing `DeleteFailedB2` per-file audit/result path (CAS-008 §7 V5, §10 SC-5) is reached only when retry is exhausted or a non-retryable failure occurs — never by a single transient blip that resolves within the retry budget. Proven at `UserScopedB2DeleteClientTests` (interface-level exception behavior, SC-1/SC-2) since the class-under-test's contract change is what CAS-008's per-file handling depends on | `Test_SC5_InterfaceExceptionContractUnchangedOnExhaustionOrNonRetryable` |
| SC-6 | All existing `UserScopedB2DeleteClientTests` (bucket-resolution-uses-system-key-only, `ListFileVersionsAsync`/`DeleteFileVersionAsync`-use-only-caller-supplied-authorization) and all existing `QuarantinePermanentDeleteServiceTests` continue to pass unmodified — no regression to CAS-018's key-scoping guarantees | Existing suites green (regression) |

---

## §11 Security Notes

No new security-sensitive surface. Retry changes only failure/timing behavior, not key handling or
capability decisions:

- The acting user's own live `B2AuthorizationInfo` (CAS-018 §2) and the system key used solely for
  bucket resolution (CAS-018 §14) are read and used exactly as before — this spec does not touch
  `AuthorizeActingUserForDeleteAsync` or any credential-decryption path in
  `QuarantinePermanentDeleteService`.
- Extends CAS-001 SC-5 / CAS-018 §11's no-key/token-leakage rule to the new retry log lines (§10
  SC-4) — a retry log message includes only the operation name, attempt count, delay, and
  status/exception type, never a request/response body, header value, or token.
- Does not alter any authorization/capability decision — a `401`/`403` still fails exactly as
  today (not a retryable status code); retries apply only to transient infrastructure-level
  failures.
- Retry-storm consideration (same as CAS-034 §11): out of scope for this per-call retry; a shared
  circuit-breaker is a larger abstraction not justified by two client classes today (Karpathy P4).
  Flagged as a §14 open item.

---

## §12 Rollback Plan

1. Revert `UserScopedB2DeleteClient.cs` to its pre-change version — restores today's
   fail-immediately-on-any-non-success behavior. `IUserScopedB2DeleteClient`'s contract never
   changed and no consumer was modified, so no other file's revert is required.
2. Database rollback: N/A.
3. Config rollback: N/A — `B2:MaxRetryAttempts`/`B2:RetryBaseDelayMs` are read with safe in-code
   defaults (`?? 3`, `?? 500`), and this spec adds no new config keys or files.
4. Task-queue rollback: N/A — Permanent Delete is synchronous (CAS-018 §1's noted discrepancy with
   CAS-001 §6.4's original queued-execution text, out of scope here), not queued; this spec does
   not alter the task-queue table shape, task contract, or any in-flight row.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `UserScopedB2DeleteClientTests` | `Test_V1_TransientFailureRetriedUpToMaxAttempts` | V1 |
| `UserScopedB2DeleteClientTests` | `Test_V2_NonRetryableFailureSurfacesImmediately` | V2 |
| `UserScopedB2DeleteClientTests` | `Test_V3_RetryAfterHeaderHonoredOverComputedBackoff` | V3 |
| `UserScopedB2DeleteClientTests` | `Test_SC1_TransientFailureEventuallySucceeds` | SC-1 |
| `UserScopedB2DeleteClientTests` | `Test_SC2_ExhaustedRetriesSurfacesOriginalFailure` | SC-2 |
| `UserScopedB2DeleteClientTests` | `Test_SC3_CallerCancellationDuringRetryDelayHonoredImmediately` | SC-3 |
| `UserScopedB2DeleteClientTests` | `Test_SC4_NoKeyOrTokenLeakageInRetryLogLines` | SC-4 |
| `UserScopedB2DeleteClientTests` | `Test_SC5_InterfaceExceptionContractUnchangedOnExhaustionOrNonRetryable` | SC-5 |
| `UserScopedB2DeleteClientTests` (existing, unmodified) | Bucket-resolution/system-key and caller-supplied-authorization tests | SC-6 (regression) |
| `QuarantinePermanentDeleteServiceTests` (existing, unmodified) | Full existing suite | SC-6 (regression) |

Coverage target: ≥ 85% on changed lines in `UserScopedB2DeleteClient.cs` (the new retry wrapper and
its four call sites) — the existing `FakeHandler`-based `HttpMessageHandler` seam in
`UserScopedB2DeleteClientTests.cs` already makes this practical without a live B2 endpoint.

---

## §14 Open Questions

- [ ] `B2Client` and `UserScopedB2DeleteClient` now each contain their own copy of the same
      bounded-retry algorithm. Per Karpathy P4 ("no new abstraction until the pattern appears
      twice — copy is fine"), this spec keeps them separate rather than extracting a shared
      internal helper into `Cachet.ArchiveSync.Api/B2/`, consistent with CAS-018's own established
      idiom of mirroring `B2Client` rather than depending on it (`UserScopedB2DeleteClient.cs`'s own
      doc comments already justify this for bucket resolution and wire-model reuse). Revisit
      extraction only if a third B2-calling class in this project needs the same policy.
- [ ] Retry-storm / global backoff coordination under a sustained B2 outage — out of scope here,
      same as CAS-034 §14; a Permanent Delete batch's per-file loop could still compound retries
      across files in one request, but Permanent Delete requests are user-initiated and
      comparatively low-volume next to search cross-check's fan-out, so this is not expected to be
      the first place a retry storm would surface.
- [ ] `B2:MaxRetryAttempts = 3` / `B2:RetryBaseDelayMs = 500` are reused as-is from CAS-034 without
      re-tuning for Permanent Delete's specific call pattern — acceptable since both clients target
      the same B2 account/bucket and the same underlying rate limits.

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
- [ ] §8 API contract is correct (N/A — confirm no endpoint drift occurred incidentally)
- [ ] §11 Security Notes implemented as described (no key/token leakage in new log lines)
- [ ] §4 Blast Radius is accurate — no unintended change in `B2Client.cs`, the Worker project, or
      any consumer beyond `UserScopedB2DeleteClient.cs`'s own file
- [ ] §12 Rollback plan is accurate (no task-queue, DB, or required-config impact)

### Verification Report Format
Reviewer writes `CAS-036.review.md`:
- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5 file list)
- Unintended blast radius (graphify, or manual read, found changes in §6 files)
- Security gaps

## Implementation Log

**Completed:** 2026-09-28
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 86.7% | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Api/B2/UserScopedB2DeleteClient.cs` | Added bounded exponential-backoff retry wrapper (SendWithRetryAsync/ComputeDelay/IsRetryableStatusCode/TryGetRetryAfterMs/LogRetryWarning) around all four HTTP calls, mirroring B2Client (CAS-034). |
| `src/Cachet.ArchiveSync.Api.Tests/UserScopedB2DeleteClientTests.cs` | Added 8 new Fact/Theory tests proving V1-V3 and SC-1..SC-5, extending existing FakeHandler test seam. |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `UserScopedB2DeleteClientTests` | `Test_V1_TransientFailureRetriedUpToMaxAttempts` | V1 |
| `UserScopedB2DeleteClientTests` | `Test_V2_NonRetryableFailureSurfacesImmediately` | V2 |
| `UserScopedB2DeleteClientTests` | `Test_V3_RetryAfterHeaderHonoredOverComputedBackoff` | V3 |
| `UserScopedB2DeleteClientTests` | `Test_SC1_TransientFailureEventuallySucceeds` | SC-1 |
| `UserScopedB2DeleteClientTests` | `Test_SC2_ExhaustedRetriesSurfacesOriginalFailure` | SC-2 |
| `UserScopedB2DeleteClientTests` | `Test_SC3_CallerCancellationDuringRetryDelayHonoredImmediately` | SC-3 |
| `UserScopedB2DeleteClientTests` | `Test_SC4_NoKeyOrTokenLeakageInRetryLogLines` | SC-4 |
| `UserScopedB2DeleteClientTests` | `Test_SC5_InterfaceExceptionContractUnchangedOnExhaustionOrNonRetryable` | SC-5 |
