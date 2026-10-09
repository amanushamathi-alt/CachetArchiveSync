---
id: CAS-034
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-25
priority: P2
change_type: enhancement
related_specs: [CAS-001, CAS-018]
touches:
  database: false
  api: false
  ui: false
  worker: true
  b2: true
  config: true
  security: false
migrations: []
---

# CAS-034 — Change: Bounded Retry/Backoff for Transient B2 API Failures in B2Client

## §0 Context Note (read this first)

This spec was requested as an "implementation spec for the server-side B2 client module: how it
loads and refreshes the brokered application key, the B2 operations it exposes, and error/retry
handling for B2 API failures." Before drafting, the current repo state was checked against that
ask:

- **Credential loading and refresh** — already designed (CAS-001 §6.1) and implemented, `merged`:
  `B2Client`'s constructor reads the server-held system key exclusively from `IConfiguration`
  (`B2:SystemKeyId`/`SystemApplicationKey`/`BucketName`), and the cached `authorizationToken` is
  transparently refreshed on a `401` via `SendWithReauthAsync`. No gap found.
- **B2 operations exposed** (list, download, delete, existence-check) — already designed and
  implemented, `merged`: `IB2Client` exposes `ListFileNamesAsync`, `DownloadFileByIdAsync`,
  `DeleteFileVersionAsync`, `GetFileInfoAsync`, plus `AuthorizeAsync`/`GetUploadUrlAsync`/
  `UploadFileAsync`. No gap found.
- **Error/retry handling for B2 API failures** — this is the one genuine gap. `B2Client`'s
  `EnsureSuccessAsync` ([B2Client.cs:200](../../src/Cachet.ArchiveSync.Api/B2/B2Client.cs#L200))
  throws immediately on any non-success response, and `SendWithReauthAsync`
  ([B2Client.cs:179](../../src/Cachet.ArchiveSync.Api/B2/B2Client.cs#L179)) only special-cases a
  single retry for `401` (token refresh). Nothing retries a transient `429`/`5xx`/network failure.
  This spec covers that gap only — it does not re-litigate or re-implement §6.1's already-shipped
  credential brokering.

Per the project's spec-change workflow (`.claude/commands/spec-change.md`), this is a **change to
existing, merged code** (`B2Client.cs`), not a new feature — hence a change spec against CAS-001's
delivered module, not a `/spec-new` design doc.

---

## §1 Current Behavior

`B2Client` ([src/Cachet.ArchiveSync.Api/B2/B2Client.cs](../../src/Cachet.ArchiveSync.Api/B2/B2Client.cs))
implements `IB2Client` and is the sole implementation used by both the Api and the Worker (Worker
references it via a direct `ProjectReference` to the Api project, per
[Worker/Program.cs:59](../../src/Cachet.ArchiveSync.Worker/Program.cs#L59)'s comment — CAS-001
§6.1/§17's accepted pattern).

- `SendWithReauthAsync` ([B2Client.cs:179-198](../../src/Cachet.ArchiveSync.Api/B2/B2Client.cs#L179-L198))
  sends a request built from the cached authorization; on `HttpStatusCode.Unauthorized` it
  re-authenticates exactly once (`AuthorizeAsync`) and retries the request exactly once. Any other
  non-success status is returned as-is to the caller.
- `EnsureSuccessAsync` ([B2Client.cs:200-231](../../src/Cachet.ArchiveSync.Api/B2/B2Client.cs#L200-L231))
  is called by every one of the seven wire operations after the response comes back. On any
  non-2xx status it throws immediately: `B2NotFoundException` for `404`, `B2ApiException`
  (status + B2 error code/message) for everything else — `429`, `500`, `502`, `503`, `504`
  included, with no retry.
- A transport-level failure (`HttpRequestException` from a DNS/connection failure, or a timeout)
  is not caught anywhere in `B2Client` — it propagates directly to the caller unmodified.
- `UploadFileAsync` ([B2Client.cs:460-485](../../src/Cachet.ArchiveSync.Api/B2/B2Client.cs#L460-L485))
  already documents, in its own comment, that retry is deliberately the caller's responsibility
  ("a failed upload call should be retried against a freshly obtained upload URL, not
  transparently retried here") — this existing design decision is unchanged by this spec (see §6).

Six consumers call into `IB2Client` today (`graphify query "what depends on IB2Client"`, §4):
`KeywordSearchTaskProcessor`, `SyncCopyTaskProcessor`, `SyncUpdateTaskProcessor`,
`SyncPurgeTaskProcessor` (all Worker), and `QuarantinePermanentDeleteService` (Api). None of them
currently retry a failed `IB2Client` call themselves — a single transient B2 failure today
surfaces as an immediate task/step failure (e.g. `DeleteFailed-B2`, a `Failed` sync task, or an
aborted mirror-population run).

---

## §2 Desired Behavior

Add a bounded, backoff-based retry to `B2Client` for **transient** B2 API failures, scoped to six
of the seven operations (`AuthorizeAsync`, `ListFileNamesAsync`, `ListFileVersionsAsync`,
`GetFileInfoAsync`, `DownloadFileByIdAsync`, `GetUploadUrlAsync`, `DeleteFileVersionAsync` —
`UploadFileAsync` is explicitly excluded, see §6). The existing `401`-reauthenticate-once behavior
in `SendWithReauthAsync` is unchanged and composes with this new retry (a call can be reauth-
retried once for a `401`, and separately retried up to the bounded count for a transient failure
on any attempt).

**Retryable:**
- HTTP `429` (Too Many Requests)
- HTTP `500`, `502`, `503`, `504`
- `HttpRequestException` (connection/DNS/TLS failures)
- `TaskCanceledException`/`OperationCanceledException`, but **only** when the token passed to the
  call is not itself the source of cancellation (`!ct.IsCancellationRequested`) — i.e. an internal
  timeout, not a caller-requested cancellation.

**Not retryable (fail on first attempt, exactly as today):**
- `400`, `403`, `404` (→ unchanged `B2NotFoundException` for `404`)
- Caller-requested cancellation (`ct.IsCancellationRequested == true`) — propagates immediately,
  never retried.

**Retry policy:**
- Bounded attempts: `B2:MaxRetryAttempts` (config, default `3` total attempts — i.e. up to 2
  retries after the first).
- Exponential backoff with jitter: `delay = B2:RetryBaseDelayMs * 2^(attempt-1)` (default base
  `500`ms → `500ms, 1000ms, 2000ms, ...`) plus a small random jitter (`0-100ms`), capped at a fixed
  ceiling (`8000`ms) so a Worker loop cannot stall indefinitely on one call.
- If a `429` response includes a `Retry-After` header, honor it in place of the computed backoff
  for that attempt (per Backblaze's own rate-limit guidance) — take the greater of `Retry-After`
  and the computed floor only if `Retry-After` is implausibly small (< the computed delay), else
  use `Retry-After` as-is.
- Delay is awaited via `Task.Delay(delay, ct)` — a caller cancellation during the delay itself is
  honored immediately (propagates `OperationCanceledException`, no further attempts).
- After `B2:MaxRetryAttempts` consecutive retryable failures, the original exception (the last
  `B2ApiException`/`HttpRequestException`/`OperationCanceledException` observed) propagates to the
  caller exactly as attempt 1 would have propagated it today — no new exception type, no swallowed
  failure. Every existing downstream failure-handling contract (§6.2 mirror-sync task failure,
  §6.3 cross-check "excluded-on-miss" — which must **not** be triggered by an exhausted-retry
  failure, only by a real `404`/hidden result, §6.4 `DeleteFailed-B2`) is unchanged; this spec only
  changes how many attempts happen before that existing contract is honored.
- Each retry attempt logs a warning (operation name, attempt number/total, computed delay,
  status code or exception type) — never the B2 key or an authorization/upload token (extends
  CAS-001 SC-5, see §11).

**`DownloadFileByIdAsync` special case:** retry applies only up to the point where the response
headers are received and a `B2DownloadedFile` is successfully constructed. Once the caller has
been handed the returned `B2DownloadedFile` (and therefore its live network stream), a later
mid-stream read failure is the caller's problem, not retried inside `B2Client` — this mirrors the
same reasoning `UploadFileAsync`'s existing comment already gives for its own stream (§6).

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** Gap identified 2026-09-25 while assessing the WBS "B2 API client and server-side
  credential management" task against the current repo. CAS-001 §6.1 and its implementation
  already cover credential loading/refresh and the seven named B2 operations, but neither the
  spec nor the code define retry/backoff behavior for transient B2 failures beyond the narrow
  `401`-reauthenticate-once path.
- **Risk if not changed:** §6.2 mirror population is a long-running Worker job (FR-22a: up to a
  full day, enumerating/downloading potentially ~500GB) with no resilience to a single transient
  `5xx`/`429`/network blip — today, one such failure aborts the entire task, forcing a full
  crashed-task restart (FR-21) rather than transparently retrying the one call that actually
  failed. §6.3 cross-check batches can also spuriously "exclude" a file from search/scan results
  on a transient failure that is, to the caller, indistinguishable from a real `404` — degrading
  FR-08 result accuracy for reasons unrelated to whether the file actually exists on B2.

---

## §4 Blast Radius (Graph Impact)

### Primary Change Target
File: `src/Cachet.ArchiveSync.Api/B2/B2Client.cs`
Entity: `B2Client` (implements `IB2Client`)
Current edges: `graphify explain "B2Client"` → file-level node, degree 2 (`contains` `B2Client`,
`ResponseOwningStream`). The interface it implements, `IB2Client`, has degree 6.

### Direct Dependents (nodes with edges INTO `IB2Client`)
From `graphify query "what depends on IB2Client" --budget 2000`:
`B2Client` (the implementation being changed), `KeywordSearchTaskProcessor` (Worker),
`SyncCopyTaskProcessor` (Worker), `SyncUpdateTaskProcessor` (Worker), `SyncPurgeTaskProcessor`
(Worker), `QuarantinePermanentDeleteService` (Api). All six consume `IB2Client` through its
existing interface contract, which this spec does not change (no new methods, no signature
changes) — only the internal retry behavior inside `B2Client`'s existing method bodies changes.
None require a source change; they are listed here as behaviorally affected (a retryable call may
now take longer before it fails, vs. failing immediately today) but code-unchanged — confirmed in
§6.

### Downstream Modules
Keyword Search (cross-check, content scan — via `KeywordSearchTaskProcessor`), O-Drive Sync /
Copy-Update-Purge (via `SyncCopyTaskProcessor`/`SyncUpdateTaskProcessor`/`SyncPurgeTaskProcessor`),
Quarantine (Permanent Delete submission path via `QuarantinePermanentDeleteService` — note per
CAS-018, the per-file B2 delete calls in Permanent Delete were moved to `IUserScopedB2DeleteClient`,
a separate class this spec does not touch; `QuarantinePermanentDeleteService`'s residual
`IB2Client` edge is flagged for confirmation at implementation time, §14). Task Manager
(indirectly — affects how long a `Running` task can take before transitioning to `Failed`). Audit
Log (indirectly — no new audit actions; existing failure-outcome audit writes are unchanged, only
reached after retries are exhausted instead of on the first failure).

### Cross-Project Risk
**YES.** `B2Client.cs` lives under `Cachet.ArchiveSync.Api/B2/` but is compiled into both the Api
and the Worker process via Worker's direct `ProjectReference` to the Api project (confirmed in
[Worker/Program.cs:59](../../src/Cachet.ArchiveSync.Worker/Program.cs#L59)). Any change to
`B2Client.cs` ships to both processes. This is a pre-existing, already-accepted architectural
pattern from CAS-001 — flagged per the spec-change template's requirement, not a new risk this
spec introduces.

### God Node Involvement
No. `IB2Client` degree is 6, below the project's >10 god-node threshold.

### Graphify Queries Run
```
python -m graphify explain "B2Client"
python -m graphify explain "IB2Client"
python -m graphify query "what depends on IB2Client" --budget 2000
python -m graphify path "IB2Client" "SyncPurgeTaskProcessor"   # no path found — explain/query
                                                                 # above already gave the complete
                                                                 # degree-6 dependent list needed
```

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `src/Cachet.ArchiveSync.Api/B2/B2Client.cs` | modify | Add a private bounded retry/backoff helper wrapping the six retry-eligible operations (§2); handle `Retry-After` on `429`; log each retry attempt without leaking key/token material. |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | modify | Add `B2:MaxRetryAttempts` / `B2:RetryBaseDelayMs` (non-secret; same category as the existing `B2:CrossCheckMaxConcurrency`). |
| `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | modify | Same two keys, mirroring `CrossCheckMaxConcurrency`'s existing per-project duplication. |
| `src/Cachet.ArchiveSync.Api.Tests/B2ClientTests.cs` | modify | New test cases per §13. |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `src/Cachet.ArchiveSync.Core/Interfaces/IB2Client.cs` | Interface contract unchanged — retry is an internal implementation detail of `B2Client`, not a new capability callers opt into. |
| `src/Cachet.ArchiveSync.Api/B2/UploadFileAsync` (within `B2Client.cs`, but this method's body) | Deliberately excluded from the retry wrapper — its existing comment already documents that a failed upload must be retried by the caller against a freshly obtained upload URL, since the request `Stream content` parameter cannot be safely re-read. This spec preserves that decision rather than overriding it. |
| `src/Cachet.ArchiveSync.Api/B2/UserB2KeyAuthorizer.cs` | Separate class (CAS-002), a synchronous, login-time-only call. Retrying a user-facing login request risks a worse UX (a longer hang before surfacing an error) than failing fast; a retry policy for it is a distinct design decision this spec does not make. |
| `src/Cachet.ArchiveSync.Api/B2/UserScopedB2DeleteClient.cs` | Separate class (CAS-018), synchronous, request-scoped per-file delete under a live user authorization. Same reasoning as above — out of scope. |
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs`, `SyncCopyTaskProcessor.cs`, `SyncUpdateTaskProcessor.cs`, `SyncPurgeTaskProcessor.cs` | Consume `IB2Client` through its existing, unchanged interface — no source change needed. |
| `src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs` | Consumes `IB2Client` through its existing, unchanged interface — no source change needed. |
| `src/Cachet.ArchiveSync.Api/Program.cs`, `src/Cachet.ArchiveSync.Worker/Program.cs` | DI registration unchanged — `B2Client`'s constructor signature does not change. |
| `docs/environment-variables.md` | The two new config keys are non-secret, ordinary appsettings values (same category as the existing `B2:CrossCheckMaxConcurrency`, which this doc also does not list) — this doc documents only the CAS-019 secret keys that must be environment-var-supplied. Not applicable here. |

---

## §7 Database Changes

N/A — no database objects added or changed.

---

## §8 API Contract Changes

N/A — no endpoint/route changes. `B2Client` is not itself exposed via any controller route; its
retry behavior is an internal implementation detail invisible to any HTTP contract.

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | No retry exists for any failure type | A retryable failure (`429`/`5xx`/transient network) is retried up to `B2:MaxRetryAttempts` (default 3) with exponential backoff before surfacing to the caller | `Test_V1_TransientFailureRetriedUpToMaxAttempts` |
| V2 | N/A | A non-retryable failure (`400`/`403`/`404`, or a caller-cancelled request) is never retried — surfaces on the first attempt, unchanged from today | `Test_V2_NonRetryableFailureSurfacesImmediately` |
| V3 | N/A | `UploadFileAsync` is explicitly excluded from this spec's retry wrapper — unchanged from today's caller-owns-retry design | `Test_V3_UploadFileAsyncNotWrappedInRetry` |
| V4 | N/A | A B2 `Retry-After` response header on a `429` is honored in preference to the computed exponential-backoff delay | `Test_V4_RetryAfterHeaderHonoredOverComputedBackoff` |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A transient failure (`429`/`500`/`502`/`503`/`504`, or `HttpRequestException`) on any of the six retry-eligible operations, followed by a success within `B2:MaxRetryAttempts`, returns the successful result to the caller — the caller never observes an exception | `Test_SC1_TransientFailureEventuallySucceeds` |
| SC-2 | After `B2:MaxRetryAttempts` consecutive transient failures, the original failure (same exception type/content attempt 1 would have thrown today) propagates to the caller — no new exception type, no swallowed failure | `Test_SC2_ExhaustedRetriesSurfacesOriginalFailure` |
| SC-3 | Caller-requested cancellation (`ct.IsCancellationRequested`) during a retry delay is honored immediately — no further attempts, `OperationCanceledException` propagates | `Test_SC3_CallerCancellationDuringRetryDelayHonoredImmediately` |
| SC-4 | No retry log line, at any attempt, includes the B2 key or an authorization/upload token, even partially (extends CAS-001 SC-5) | `Test_SC4_NoKeyOrTokenLeakageInRetryLogLines` |
| SC-5 | §6.4's `DeleteFailed-B2`, §6.2's mirror-sync task failure, and §6.3's cross-check exclusion-on-miss are reached only after retries are exhausted (or immediately for a non-retryable/real-404 case) — never triggered by a single transient blip that a retry would have resolved | `Test_SC5_ExistingFailureContractOnlyReachedAfterExhaustion` |
| SC-6 | All existing `B2ClientTests` (`Test_V1_B2ClientRejectsNonServerKey`, `Test_V1_B2ClientConstructsWhenAllServerConfigPresent`, `Test_SC5_NoKeyLeakage` from CAS-001) continue to pass unmodified — no regression to construction-time validation or key-leakage protection | Existing suite green (regression) |

---

## §11 Security Notes

No new security-sensitive surface is introduced. Retry logic changes only failure/timing
behavior, not key handling or capability checks:

- The system key is loaded exactly as before (CAS-001 §6.1/§7 V1) — this spec does not touch
  `B2Client`'s constructor or `RequireConfig`.
- Extends CAS-001 SC-5's no-key-leakage rule to the new retry log lines (§10 SC-4) — a retry log
  message includes only the operation name, attempt count, delay, and status/exception type, never
  a request/response body or header value.
- Does not alter any authorization/capability decision. A request that fails on `401`/`403` today
  still fails the same way — retries apply only to transient infrastructure-level failures
  (`429`/`5xx`/network), never to authentication or authorization outcomes.
- **Retry-storm consideration:** if B2 is genuinely degraded or broadly rate-limiting, multiple
  concurrent cross-check calls (CAS-001 §6.3's bounded concurrency, e.g. 8) could each
  independently retry with backoff, compounding load at exactly the moment B2 is already
  struggling. This spec's retry is per-call, not coordinated across calls — a shared
  circuit-breaker or global backoff coordinator is a larger abstraction not justified by a single
  client class today (Karpathy P4: no new abstraction until the pattern appears twice). Flagged as
  a §14 open item to revisit only if a real production incident shows compounding load.

---

## §12 Rollback Plan

1. Revert `B2Client.cs` to its pre-change version — restores today's fail-immediately-on-any-
   non-401-failure behavior. No other file's revert is required, since `IB2Client`'s contract
   never changed and no consumer was modified.
2. Database rollback: N/A — no database objects added.
3. Config rollback: N/A — `B2:MaxRetryAttempts`/`B2:RetryBaseDelayMs` are read with safe in-code
   defaults (`?? 3`, `?? 500`), not `RequireConfig`-style required-or-throw values (unlike
   `B2:SystemKeyId`). Reverting the `appsettings.Development.json` edits is optional and has no
   failure mode either way.
4. Task-queue rollback: N/A — this spec does not alter the task-queue table shape, task contract,
   or any in-flight row's meaning. A `Running` Worker task mid-retry at deploy time simply
   completes its current bounded retry sequence (at most a few seconds, per §2's capped backoff)
   under whichever code version is active when the process is recycled — no draining logic needed.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `B2ClientTests` | `Test_V1_TransientFailureRetriedUpToMaxAttempts` | V1 |
| `B2ClientTests` | `Test_V2_NonRetryableFailureSurfacesImmediately` | V2 |
| `B2ClientTests` | `Test_V3_UploadFileAsyncNotWrappedInRetry` | V3 |
| `B2ClientTests` | `Test_V4_RetryAfterHeaderHonoredOverComputedBackoff` | V4 |
| `B2ClientTests` | `Test_SC1_TransientFailureEventuallySucceeds` | SC-1 |
| `B2ClientTests` | `Test_SC2_ExhaustedRetriesSurfacesOriginalFailure` | SC-2 |
| `B2ClientTests` | `Test_SC3_CallerCancellationDuringRetryDelayHonoredImmediately` | SC-3 |
| `B2ClientTests` | `Test_SC4_NoKeyOrTokenLeakageInRetryLogLines` | SC-4 |
| `B2ClientTests` | `Test_SC5_ExistingFailureContractOnlyReachedAfterExhaustion` | SC-5 |
| `B2ClientTests` (existing, unmodified) | `Test_V1_B2ClientRejectsNonServerKey`, `Test_V1_B2ClientConstructsWhenAllServerConfigPresent`, `Test_SC5_NoKeyLeakage` | SC-6 (regression) |

Coverage target: ≥ 85% on changed lines in `B2Client.cs` (the retry helper and its call sites
specifically — CAS-001's own Implementation Log noted `B2Client.cs`'s overall coverage was
accepted below threshold for scope reasons at the time; this spec's new lines must independently
clear 85%, which the existing `StubAuthorizeHandler`-style `HttpMessageHandler` seam in
`B2ClientTests.cs` already makes practical to exercise).

---

## §14 Open Questions

- [ ] Retry-storm / global backoff coordination under a sustained B2 outage — out of scope for
      this spec (per-call bounded retry only, §11); revisit only if a production incident shows
      compounding load across concurrent cross-check calls.
- [ ] `QuarantinePermanentDeleteService`'s residual `IB2Client` edge (graphify, §4) — confirm at
      implementation time exactly which calls remain on `IB2Client` vs. `IUserScopedB2DeleteClient`
      post-CAS-018, so §4's dependent list is verified against current code, not only the graph
      edge.
- [ ] Default values `B2:MaxRetryAttempts = 3` / `B2:RetryBaseDelayMs = 500` are proposed, not
      confirmed against any production B2 rate-limit incident data — tune empirically if observed
      retry exhaustion becomes common, same "tune empirically" caveat CAS-001 §6.3 already applies
      to `B2:CrossCheckMaxConcurrency`.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts), via Claude Code | 2026-09-25 | Drafted after confirming credential-loading/refresh and B2-operations coverage are already shipped (CAS-001, CAS-002, CAS-018) — scoped to the retry/backoff gap only |
| Reviewer | | | Pending |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] Current behavior (§1) matches what is actually in the code BEFORE the change
- [ ] Desired behavior (§2) is fully implemented
- [ ] Only files in §5 were modified (§6 files are untouched)
- [ ] `UploadFileAsync` is confirmed NOT wrapped in the new retry logic (§6 exclusion preserved)
- [ ] All §10 Success Criteria have passing tests
- [ ] All §9 Validations are implemented and tested
- [ ] §8 API contract is correct (no undeclared breaking changes — N/A confirmed)
- [ ] §11 Security Notes implemented as described — no key/token leakage in new log lines
- [ ] §4 Blast Radius is accurate — no unintended side effects in connected nodes (the six
      `IB2Client` consumers require no source changes)
- [ ] §12 in-flight task-queue rows are unaffected (N/A confirmed — no task-queue contract change)

### Verification Report Format
Reviewer writes `CAS-034.review.md`:
- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5 file list)
- Unintended blast radius (graphify, or manual read, found changes in §6 files)
- Security gaps

## Implementation Log

**Completed:** 2026-09-28
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 85.9% | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Api/B2/B2Client.cs` | Added SendWithRetryAsync bounded exponential-backoff-with-jitter retry helper (IsRetryableStatusCode, ComputeDelay, TryGetRetryAfterMs, LogRetryWarning); rewired AuthorizeAsync/ListFileNamesAsync/ListFileVersionsAsync/GetFileInfoAsync/DownloadFileByIdAsync/GetUploadUrlAsync/DeleteFileVersionAsync through it; UploadFileAsync left untouched per spec exclusion |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | Added B2:MaxRetryAttempts (3) / B2:RetryBaseDelayMs (500) non-secret config keys |
| `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | Mirrored the same two config keys |
| `src/Cachet.ArchiveSync.Api.Tests/B2ClientTests.cs` | Extended with 22 new test methods (36 total incl. 3 pre-existing CAS-001 tests) covering SC-1..SC-6, V1..V4, and all 8 IB2Client operations |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `B2ClientTests` | `Test_SC1_TransientFailureEventuallySucceeds` | SC-1 |
| `B2ClientTests` | `Test_SC2_ExhaustedRetriesSurfacesOriginalFailure` | SC-2 |
| `B2ClientTests` | `Test_SC3_CallerCancellationDuringRetryDelayHonoredImmediately` | SC-3 |
| `B2ClientTests` | `Test_SC4_NoKeyOrTokenLeakageInRetryLogLines` | SC-4 |
| `B2ClientTests` | `Test_SC5_ExistingFailureContractOnlyReachedAfterExhaustion` | SC-5 |
| `B2ClientTests` | `Test_V1_TransientFailureRetriedUpToMaxAttempts` | V1 |
| `B2ClientTests` | `Test_V2_NonRetryableFailureSurfacesImmediately` | V2 |
| `B2ClientTests` | `Test_V3_UploadFileAsyncNotWrappedInRetry` | V3 |
| `B2ClientTests` | `Test_V4_RetryAfterHeaderHonoredOverComputedBackoff` | V4 |
| `B2ClientTests` | `Test_ListFileNamesAsync_HappyPath_ReturnsFilesAndNextFileName` | coverage |
| `B2ClientTests` | `Test_ListFileNamesAsync_MaxFileCountOutOfRange_ThrowsArgumentOutOfRangeException` | coverage |
| `B2ClientTests` | `Test_ListFileNamesAsync_TransientFailureThenSuccess_Retries` | coverage |
| `B2ClientTests` | `Test_ListFileVersionsAsync_HappyPath_ReturnsFilesNextFileNameAndNextFileId` | coverage |
| `B2ClientTests` | `Test_ListFileVersionsAsync_MaxFileCountOutOfRange_ThrowsArgumentOutOfRangeException` | coverage |
| `B2ClientTests` | `Test_GetFileInfoAsync_HappyPath_ReturnsMappedFileInfo` | coverage |
| `B2ClientTests` | `Test_GetFileInfoAsync_NotFound_ThrowsB2NotFoundException` | coverage |
| `B2ClientTests` | `Test_GetFileInfoAsync_TransientFailureThenSuccess_Retries` | coverage |
| `B2ClientTests` | `Test_DownloadFileByIdAsync_HappyPath_ReturnsMappedFileWithHeaders` | coverage |
| `B2ClientTests` | `Test_DownloadFileByIdAsync_MissingFileNameHeader_FallsBackToFileId` | coverage |
| `B2ClientTests` | `Test_DownloadFileByIdAsync_DisposingContentStream_DisposesUnderlyingResponse` | coverage |
| `B2ClientTests` | `Test_GetUploadUrlAsync_HappyPath_ReturnsMappedUploadUrlInfo` | coverage |
| `B2ClientTests` | `Test_GetUploadUrlAsync_TransientFailureThenSuccess_Retries` | coverage |

### Gate 2 Note — Pre-Existing Unrelated Failures Observed

The full-solution `dotnet test` run (both before and after the coverage top-up) shows failures
outside this spec's scope, logged here for auditability since the deterministic `append-log` tool
only records a binary pass/fail and this nuance would otherwise be lost:

- **`Cachet.ArchiveSync.Api.Tests`** (includes this spec's `B2ClientTests.cs`): **364/364 passed**,
  both runs — CAS-034's own gate is unambiguously green.
- **`Cachet.ArchiveSync.Worker.Tests`**: 6 failures, identical and deterministic across both runs —
  all `WorkerTaskModuleMapTests.Test_TaskTypeToTabModule_MatchesSweepProcCase` (one per task type),
  each throwing `System.IO.FileNotFoundException` for
  `Database/CachetArchiveSync/Stored-Procedures/dbo.task_queue_sweep_interrupted.sql`, confirmed
  genuinely absent from the repo. CAS-034 has `touches.database: false` and never reads or writes
  anything under `Database/` — unrelated pre-existing gap, not a regression from this spec.
- **`Cachet.ArchiveSync.Web.Tests`**: 3 failures on the first run, 4 on the second, with the
  specific failing test names differing between runs (all within
  `ODriveSyncPageTests`/`KeywordSearchPageTests`, bUnit `WaitForState` timeouts and one button-state
  assertion) — the non-reproducible, run-to-run-varying failure set is itself the signal that these
  are flaky UI-timing tests, not a deterministic regression. CAS-034 never touches
  `Cachet.ArchiveSync.Web` or anything it depends on.

Recorded per the reviewer's checklist (§16) so `CAS-034.review.md` can verify this characterization
independently rather than take it on faith.
