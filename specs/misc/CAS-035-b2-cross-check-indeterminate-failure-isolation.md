---
id: CAS-035
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-09-28
priority: P2
change_type: enhancement
related_specs: [CAS-001, CAS-006, CAS-012, CAS-016, CAS-029, CAS-034]
touches:
  database: false
  api: false
  ui: false
  worker: true
  b2: true
  config: false
  security: false
migrations: []
---

# CAS-035 — Change: Isolate Indeterminate B2 Cross-Check Failures from Confirmed-Absent Exclusions

## §0 Context Note (read this first)

This spec was requested against WBS Task 32 ("B2 cross-check confirmation logic (local match
verified via API)"), drafted from: "given a local match record, how it calls the B2 client to
confirm current existence, how it handles a file that no longer exists on B2 (excluded from
results, and how that's logged), and how this is batched efficiently for potentially thousands of
candidate matches." Before drafting, the current repo state was checked against that ask, per this
project's established `/spec-change` pattern (CAS-034 §0):

- **The per-candidate B2 existence check** — already designed (CAS-001 §6.3) and implemented,
  `merged`: `KeywordSearchTaskProcessor.IsConfirmedOnB2Async`
  ([KeywordSearchTaskProcessor.cs:244-258](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L244-L258))
  calls `IB2Client.ListFileNamesAsync(candidate.RelativePath, 1, ct)` per candidate and treats an
  exact filename match with `action == "upload"` as confirmed. This is a **documented deviation**
  from CAS-001 §6.3's original `b2_get_file_info`-by-`B2FileId` design (the deviation is flagged
  in the processor's own class-doc comment, lines 22-31, because `dbo.file_index` — CAS-003 — was
  never given a `B2FileId` column). No further gap here; re-opening that deviation (e.g. adding a
  `B2FileId` column to make cross-check an O(1) `GetFileInfoAsync` lookup) is a schema change no
  one has asked for and is explicitly **out of scope** for this spec (§14).
- **How a confirmed-absent file is excluded and logged** — already designed and implemented,
  `merged`: a candidate B2 reports as gone (`B2NotFoundException`, or a name/status mismatch) is
  soft-deleted from `dbo.file_index` (self-heal) and audited with `AuditAction.CrossCheckExcluded`
  **before** the candidate can ever become a `search_result` row — never inserted first (CAS-006
  §7 V9, §12; [KeywordSearchTaskProcessor.cs:150-170](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L150-L170)).
  No gap here.
- **How this is batched for thousands of candidates** — already designed and implemented,
  `merged`: bounded concurrency via a `SemaphoreSlim` sized by `B2:CrossCheckMaxConcurrency`
  (config key named by CAS-001 §9, default `8`, [KeywordSearchTaskProcessor.cs:93-94](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L93-L94)),
  with per-candidate progress reported via CAS-029's `TaskProgressReporter`. `IB2Client`'s full
  surface (`AuthorizeAsync`, `ListFileNamesAsync`, `ListFileVersionsAsync`, `GetFileInfoAsync`,
  `DownloadFileByIdAsync`, `GetUploadUrlAsync`/`UploadFileAsync`, `DeleteFileVersionAsync` —
  [IB2Client.cs](../../src/Cachet.ArchiveSync.Core/Interfaces/IB2Client.cs)) has no bulk/batch
  existence-check operation across an arbitrary list of paths — B2's own API doesn't expose one —
  so bounded-concurrency fan-out over individual per-candidate calls is the correct and only
  available strategy. It has simply never been written down as a considered decision with
  rationale; §2.3 of this spec formalizes it (no code change).
- **Error/retry handling for a transient B2 failure during cross-check** — this is the one
  genuine gap, and it is the part of the WBS ask ("how this is batched efficiently for potentially
  thousands of candidate matches") that the current code does not actually satisfy: `IsConfirmedOnB2Async`
  only catches `B2NotFoundException`. CAS-034 (status `review`, delivered 2026-09-25) added
  bounded retry/backoff *inside* `B2Client` for transient failures (`429`/`5xx`/network/internal
  timeout) on every operation including `ListFileNamesAsync`, but explicitly documented that "the
  original exception... propagates to the caller exactly as attempt 1 would have" once retries are
  exhausted (CAS-034 §2). Today, that propagated exception — a non-404 `B2ApiException`, an
  `HttpRequestException`, or a non-caller-cancellation `OperationCanceledException` — is **not**
  caught anywhere in `IsConfirmedOnB2Async` or its caller. It escapes the per-candidate task,
  faults `Task.WhenAll(crossCheckTasks)`
  ([KeywordSearchTaskProcessor.cs:100-181](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L100-L181)),
  and is caught only by the processor's outer `try/catch`
  ([KeywordSearchTaskProcessor.cs:200-211](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L200-L211)),
  which marks the **entire task** `Failed` — discarding the fact that every other candidate
  already cross-checked successfully and already has a durably-committed `search_result` row. This
  existing behavior is locked in today by
  `Test_ProcessAsync_B2ApiException_MarksFailedWithGenericReason`
  ([KeywordSearchTaskProcessorTests.cs:632](../../src/Cachet.ArchiveSync.Worker.Tests/KeywordSearchTaskProcessorTests.cs#L632)).
  Per CAS-016 §2 (a deliberately accepted, already-resolved architecture decision: a `Failed` task
  is never resumed/checkpointed — restart re-submits the **entire** search as a brand-new task,
  re-querying and re-cross-checking every candidate from scratch), the practical cost of one flaky
  B2 response among "potentially thousands of candidate matches" is a full, expensive re-run of
  the whole search — not a targeted retry of the one candidate that failed. This spec closes that
  gap only. It does **not** revisit CAS-016's no-resume decision, and does not change `B2Client`,
  `IB2Client`, or CAS-034's retry policy.

Per the project's spec-change workflow (`.claude/commands/spec-change.md`), this is a **change to
existing, merged code**, not a new feature — hence a change spec against CAS-006/CAS-001's
delivered cross-check module, not a `/spec-new` design doc.

---

## §1 Current Behavior

`KeywordSearchTaskProcessor.ProcessAsync`
([KeywordSearchTaskProcessor.cs:77-213](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L77-L213))
fans out one async operation per candidate under a `SemaphoreSlim(maxConcurrency)` (default `8`,
`B2:CrossCheckMaxConcurrency`), calling `IsConfirmedOnB2Async` for each:

```
private async Task<bool> IsConfirmedOnB2Async(FileIndexCandidateRow candidate, CancellationToken ct)
{
    try
    {
        var result = await _b2Client.ListFileNamesAsync(candidate.RelativePath, 1, ct);
        var first = result.Files.FirstOrDefault();
        return first is not null
            && string.Equals(first.FileName, candidate.RelativePath, StringComparison.Ordinal)
            && first.Action == "upload";
    }
    catch (B2NotFoundException)
    {
        return false;
    }
}
```
([KeywordSearchTaskProcessor.cs:244-258](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L244-L258))

Only two outcomes exist today:
1. **Confirmed** (`true`) → insert a `dbo.search_result` row (`FilenameMatchCount`/`ContentMatchCount` incremented).
2. **Confirmed absent** (`false`, only reachable via `B2NotFoundException`, i.e. a real B2 `404`)
   → `IFileIndexRepository.SoftDeleteAsync` (self-heal) + `IAuditLogService.WriteAsync` with
   `AuditAction.CrossCheckExcluded` + `ExcludedCount` incremented
   ([KeywordSearchTaskProcessor.cs:150-170](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L150-L170)).

Any other exception the call can raise — a non-404 `B2ApiException` (`429`/`5xx`/other B2 error
code), a raw `HttpRequestException`, or an `OperationCanceledException` that is **not** the
processor's own `ct` firing (an internal timeout inside `B2Client`, per CAS-034 §2's retry policy)
— is not caught by `IsConfirmedOnB2Async` at all. It propagates out of the `Select` lambda
([KeywordSearchTaskProcessor.cs:100-179](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L100-L179)),
faults `Task.WhenAll(crossCheckTasks)` (line 181), and is caught only by `ProcessAsync`'s outer
`catch (Exception ex) when (ex is not OperationCanceledException)`
([KeywordSearchTaskProcessor.cs:200-211](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L200-L211)),
which writes `task_queue.status = 'Failed'` with a fixed generic reason
(`"B2 cross-check call failed."`) and discards `filenameMatchCount`/`contentMatchCount`/
`excludedCount` entirely — even though those counters, and every `search_result` row already
inserted by concurrently-completed candidates, remain durably committed in SQL Server. The Task
Manager shows the whole search as `Failed`; the only recovery path is CAS-016's user-initiated
restart, which re-submits a brand-new task that re-queries `SearchCandidatesAsync` and
re-cross-checks **every** candidate again, including the ones that already succeeded.

`KeywordSearchResultSummaryDto`
([KeywordSearchResultSummaryDto.cs](../../src/Cachet.ArchiveSync.Core/Dtos/KeywordSearchResultSummaryDto.cs))
has exactly three count fields today: `FilenameMatchCount`, `ContentMatchCount`, `ExcludedCount`.
`AuditAction`
([AuditAction.cs](../../src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs)) has one cross-check
outcome member, `CrossCheckExcluded`, reserved for a B2-confirmed absence.

---

## §2 Desired Behavior

### §2.1 A new, distinct outcome: indeterminate

`IsConfirmedOnB2Async` is restructured to return a three-way result instead of `bool`:

```
private enum CrossCheckOutcome
{
    Confirmed,
    ConfirmedAbsent,
    Indeterminate
}

private async Task<CrossCheckOutcome> CrossCheckAsync(FileIndexCandidateRow candidate, CancellationToken ct)
{
    try
    {
        var result = await _b2Client.ListFileNamesAsync(candidate.RelativePath, 1, ct);
        var first = result.Files.FirstOrDefault();
        var confirmed = first is not null
            && string.Equals(first.FileName, candidate.RelativePath, StringComparison.Ordinal)
            && first.Action == "upload";
        return confirmed ? CrossCheckOutcome.Confirmed : CrossCheckOutcome.ConfirmedAbsent;
    }
    catch (B2NotFoundException)
    {
        return CrossCheckOutcome.ConfirmedAbsent;
    }
    catch (B2ApiException ex)
    {
        _logger.LogWarning(ex,
            "B2 cross-check indeterminate for file_index_id {FileIndexId}: B2 returned status {StatusCode} after retries; candidate omitted from this search's results, not marked absent.",
            candidate.FileIndexId, ex.StatusCode);
        return CrossCheckOutcome.Indeterminate;
    }
    catch (HttpRequestException ex)
    {
        _logger.LogWarning(ex,
            "B2 cross-check indeterminate for file_index_id {FileIndexId}: network failure reaching B2 after retries; candidate omitted from this search's results, not marked absent.",
            candidate.FileIndexId);
        return CrossCheckOutcome.Indeterminate;
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        // An internal timeout inside B2Client (CAS-034 §2's retry policy distinguishes this from
        // the caller's own ct firing) — not this task's own cancellation, so it must not
        // propagate as one. Treated the same as any other exhausted-retry failure.
        _logger.LogWarning(
            "B2 cross-check indeterminate for file_index_id {FileIndexId}: internal timeout after retries; candidate omitted from this search's results, not marked absent.",
            candidate.FileIndexId);
        return CrossCheckOutcome.Indeterminate;
    }
}
```

A genuine caller-requested cancellation (`ct.IsCancellationRequested == true`) is **not** caught by
the new clause — it keeps propagating exactly as today, preserving existing worker
shutdown/restart semantics (CAS-016/CAS-028, untouched by this spec). Any exception type not
listed above (e.g. a `SqlException` from `InsertAsync`/`SoftDeleteAsync`/the audit write, or a
genuine programming defect) is also not caught here and continues to fail the whole task exactly
as today — this spec narrows the fix to a flaky *B2 dependency call specifically*, not to "any
failure during cross-check."

### §2.2 Caller disposition per outcome

`ProcessAsync`'s per-candidate branch (replacing the current `if (confirmed) {...} else {...}` at
[KeywordSearchTaskProcessor.cs:125-170](../../src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs#L125-L170))
switches on `CrossCheckOutcome`:

| Outcome | `search_result` insert | `file_index` self-heal | Audit action | Summary counter |
|---|---|---|---|---|
| `Confirmed` | Yes (unchanged) | No | None (unchanged) | `FilenameMatchCount`/`ContentMatchCount` (unchanged) |
| `ConfirmedAbsent` | No (unchanged) | `SoftDeleteAsync` (unchanged) | `CrossCheckExcluded` (unchanged) | `ExcludedCount` (unchanged) |
| `Indeterminate` (**new**) | No — FR-08 requires a *confirmed* existence before a match is shown; an unresolved outcome must never be shown as if it were confirmed | No — self-heal is reserved for a B2-confirmed absence; soft-deleting on an indeterminate error would hide a file from every future search on no real evidence it is gone | `CrossCheckIndeterminate` (**new**, distinct from `CrossCheckExcluded` so the audit trail can tell "B2 confirmed gone" apart from "B2 could not be reached/erred") | `IndeterminateCount` (**new**) |

Every branch still calls `progress.ItemCompletedAsync(ct)` exactly once per candidate (CAS-029
§3.1 step 4e, unchanged) and the per-candidate `Task` in the `Select` lambda no longer throws for
any of the three outcomes — `Task.WhenAll(crossCheckTasks)` (line 181) can no longer fault because
of a B2 dependency failure. The task reaches `Completed` whenever every candidate reaches one of
the three dispositions above, regardless of how many were `Indeterminate`.

### §2.3 Batching strategy (formalized, no code change)

Bounded concurrency via `B2:CrossCheckMaxConcurrency` (default `8`) remains the batching
mechanism, ratified here as the deliberate answer to "how is this batched efficiently for
potentially thousands of candidate matches": `IB2Client` exposes no bulk/batch
existence-check operation over an arbitrary set of paths (confirmed against its full surface,
§0), so the only lever available is limiting in-flight concurrent per-candidate calls — trading
B2 class-C transaction/rate-limit pressure against wall-clock time, both already configurable and
now protected end-to-end by CAS-034's retry/backoff plus this spec's failure isolation. No new
configuration key is introduced.

### §2.4 New DTO field and enum member

`KeywordSearchResultSummaryDto` gains one nullable field:
```
/// <summary>Count of candidates whose B2 cross-check could not be resolved (transient B2/network
/// failure surviving all retries) — omitted from results, neither confirmed nor excluded. Distinct
/// from <see cref="ExcludedCount"/> (§7 V1, §11 telemetry).</summary>
public int? IndeterminateCount { get; init; }
```

`AuditAction` gains one member, appended (existing members not reordered, per CAS-005 §15's
`Enum.ToString()`-is-the-literal-value rule):
```
// CAS-035 §2 — appended, existing members not reordered (same rule as above).
CrossCheckIndeterminate
```

---

## §3 Reason for Change

- **Type:** `enhancement`
- **Trigger:** WBS Task 32 drafting prompt asked specifically how the cross-check "is batched
  efficiently for potentially thousands of candidate matches"; assessing that question against
  the current repo surfaced that a single unresolved B2 failure among any number of candidates
  fails the entire `KeywordSearch` task today, regardless of how many other candidates already
  succeeded — the opposite of "efficient at scale."
- **Risk if not changed:** For a search returning a large candidate set, one transient B2
  `429`/`5xx`/network blip that survives CAS-034's bounded retries discards every already-resolved
  candidate's disposition from the task's reported outcome (though their `search_result` rows
  remain in SQL Server, orphaned under a `Failed` task) and forces the user, per CAS-016's
  accepted no-resume restart model, to re-submit and fully re-run the entire search — doubling B2
  API cost and Worker time for the whole candidate set because of one flaky call to one candidate.
  At larger candidate counts this cost and the odds of hitting at least one such failure both rise.

---

## §4 Blast Radius (Graph Impact)

> `graphify-out/` exists for this repo (per CLAUDE.md, sandbox usage is read-only). Analysis below
> combines a `graphify explain`/`query` pass with a manual read, per this project's established
> practice (CAS-034 §4).

### Primary Change Target
File: `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs`
Entity: `KeywordSearchTaskProcessor.IsConfirmedOnB2Async` (renamed `CrossCheckAsync`, return type
`bool` → `CrossCheckOutcome`) and its caller's disposition branch in `ProcessAsync`.

### Direct Dependents (nodes with edges INTO the changed entity)
`IsConfirmedOnB2Async`/`CrossCheckAsync` is `private` — its only caller is `ProcessAsync` in the
same class. No other file calls it directly.

### Downstream Modules
- **Keyword Search** (this spec's own module) — `KeywordSearchResultSummaryDto.IndeterminateCount`
  is a new nullable field on an already-`Completed`-task result payload; any Web-side reader of
  this DTO (CAS-022's screen, if it deserializes the full summary rather than just the two match
  lists) sees one new optional field, additive and non-breaking.
- **Audit Log** — one new `AuditAction` member (`CrossCheckIndeterminate`). CAS-005/CAS-031's
  Audit Log screen (list/filter by `tab_module`/`action`) needs no code change — it renders
  whatever string is in `dbo.audit_log.action`; a new literal value requires no schema change
  (`action NVARCHAR(50)`, no `CHECK` constraint on that column — confirmed against
  [dbo.audit_log.sql](../../Database/CachetArchiveSync/Entities/dbo.audit_log.sql)).
- **Task Manager** — a `KeywordSearch` task that previously would have shown `Failed` (with a
  generic reason) on one flaky B2 call now shows `Completed` with a non-zero `IndeterminateCount`
  more often. This is the intended behavior change (§2), not a side effect to guard against.

### Cross-Project Risk
**No.** `KeywordSearchTaskProcessor.cs` is Worker-only; `KeywordSearchResultSummaryDto` and
`AuditAction` live in `Cachet.ArchiveSync.Core` and are shared, but both changes are additive
(new field, new enum member appended at the end) — no existing Api or Web consumer's compiled
contract changes.

### God Node Involvement
No. `IB2Client` (degree 6, per CAS-034 §4) is not touched by this spec — its interface, and
`B2Client`'s implementation, are unchanged.

### Graphify Queries Run
```
python -m graphify explain "KeywordSearchTaskProcessor"
python -m graphify query "what depends on KeywordSearchTaskProcessor"
```
(Read-only sandbox analysis per CLAUDE.md; confirmed no other node calls the changed private
method, and `KeywordSearchTaskProcessor` itself has no inbound edges beyond the Worker's task
dispatch loop, which is unaffected — it dispatches by `TaskType` string, not by this method.)

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs` | modify | Add private `CrossCheckOutcome` enum; rename/restructure `IsConfirmedOnB2Async` → `CrossCheckAsync` per §2.1; replace the `if (confirmed) {...} else {...}` disposition branch with the three-way switch in §2.2; add `indeterminateCount` local counter and thread it into `KeywordSearchResultSummaryDto`; update the class-level doc comment to describe the new outcome. |
| `src/Cachet.ArchiveSync.Core/Dtos/KeywordSearchResultSummaryDto.cs` | modify | Add `IndeterminateCount` per §2.4. |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | modify | Append `CrossCheckIndeterminate` per §2.4. |
| `src/Cachet.ArchiveSync.Worker.Tests/KeywordSearchTaskProcessorTests.cs` | modify | Rewrite `Test_ProcessAsync_B2ApiException_MarksFailedWithGenericReason` (behavior it locks in changes — see §7 V1); add new tests per §13. |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `src/Cachet.ArchiveSync.Api/B2/B2Client.cs` | CAS-034's retry/backoff behavior is unchanged; this spec only changes what the *caller* does with an exception B2Client still ultimately raises after its own retries are exhausted. |
| `src/Cachet.ArchiveSync.Core/Interfaces/IB2Client.cs` | No new or changed operation — cross-check still calls `ListFileNamesAsync` exactly as today. |
| `src/Cachet.ArchiveSync.Core/Interfaces/IFileIndexRepository.cs`, `FileIndexRepository.cs` | `SoftDeleteAsync` is called with the same signature, only for the unchanged `ConfirmedAbsent` case. |
| `Database/CachetArchiveSync/Entities/dbo.audit_log.sql` | `action` is `NVARCHAR(50)` with no `CHECK` constraint (§4) — a new enum member needs no DDL change. |
| `src/Cachet.ArchiveSync.Web/**` (CAS-022 Keyword Search screen) | `IndeterminateCount` is additive on an already-optional summary DTO; surfacing it in the UI is explicitly out of scope (§14) to keep this diff to the Worker-side resilience gap only. |
| `src/Cachet.ArchiveSync.Worker/TaskProgressReporter.cs` | Progress reporting contract (`ItemCompletedAsync` once per candidate) is unchanged — every outcome, including `Indeterminate`, still counts as one completed item (§2.2). |
| CAS-016's restart/re-submission logic | Not revisited — a task that still ends up `Failed` for a non-B2 reason (§2.1's "any other exception type") is recovered exactly as today. |

---

## §7 Database Changes

N/A — no schema, stored-procedure, or migration change. `AuditAction.CrossCheckIndeterminate`
writes a new string literal into the existing, unconstrained `dbo.audit_log.action NVARCHAR(50)`
column (§4, §6).

### Migration Required
No.

---

## §8 API Contract Changes

N/A — this spec touches no HTTP endpoint. `KeywordSearchResultSummaryDto.IndeterminateCount` is
serialized into `dbo.task_queue.result_detail` (an opaque JSON blob from the API's contract
perspective); no endpoint's documented response shape changes.

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | A non-`B2NotFoundException` failure from the B2 cross-check call (after CAS-034's retries are exhausted) propagates out of the candidate task and fails the entire `KeywordSearch` task with a generic reason, discarding every other candidate's disposition. | The same failure is caught, logged, and resolves that one candidate as `Indeterminate` (§2.1) — the task continues processing every other candidate and still reaches `Completed`. | `Test_V1_IndeterminateB2FailureDoesNotFailWholeTask` |
| V2 | No distinction exists between "B2 confirmed this file is gone" and "B2 could not be reached/erred" — both would currently either be `CrossCheckExcluded` (only for a real 404) or an unhandled task failure (everything else). | An `Indeterminate` outcome writes `AuditAction.CrossCheckIndeterminate`, never `CrossCheckExcluded`, and never soft-deletes the `file_index` row (§2.2). | `Test_V2_IndeterminateOutcomeAuditedDistinctlyFromExcluded` |
| V3 | `KeywordSearchResultSummaryDto` has no field to report an unresolved candidate count. | `IndeterminateCount` is populated on a `Completed` task's summary whenever one or more candidates resolve `Indeterminate` (§2.4). | `Test_V3_IndeterminateCountPopulatedOnCompletedSummary` |
| V4 | A genuine caller-requested cancellation (`ct.IsCancellationRequested == true`) during a cross-check call is indistinguishable, in today's single `catch (B2NotFoundException)`, from any other failure mode — it simply isn't caught, and propagates. | A genuine caller-requested cancellation still propagates unchanged — it is explicitly excluded from the new `Indeterminate` path by the `when (!ct.IsCancellationRequested)` guard (§2.1) — worker shutdown/restart semantics (CAS-016/CAS-028) are unaffected. | `Test_V4_CallerCancellationStillPropagatesNotIndeterminate` |
| V5 | A non-B2 exception during a candidate's disposition (e.g. `InsertAsync`/`SoftDeleteAsync`/the audit write throwing) fails the whole task. | Unchanged — this spec's new `catch` clauses only wrap the B2 cross-check call itself (§2.1), not the disposition branch (§2.2); a repository/DB failure still fails the whole task exactly as today. | `Test_V5_NonB2DispositionFailureStillFailsWholeTask` |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A `KeywordSearch` task with N candidates, where exactly one candidate's cross-check raises a non-404 `B2ApiException` (simulating CAS-034's retries already exhausted) and the other N-1 raise no exception, completes with `task_queue.status = 'Completed'`, `N-1` matches recorded across `FilenameMatchCount`/`ContentMatchCount`, and `IndeterminateCount = 1`. | `Test_SC1_OneIndeterminateCandidateAmongManyStillCompletesTask` |
| SC-2 | The one `Indeterminate` candidate from SC-1 has no `dbo.search_result` row inserted, its `dbo.file_index` row is not soft-deleted, and exactly one `AuditLogEntry` with `Action = CrossCheckIndeterminate` is written for it. | `Test_SC2_IndeterminateCandidateNotPersistedNotSoftDeleted` |
| SC-3 | An `HttpRequestException` from the cross-check call resolves the candidate `Indeterminate`, identically to a `B2ApiException` (§2.1's second `catch`). | `Test_SC3_HttpRequestExceptionResolvesIndeterminate` |
| SC-4 | An `OperationCanceledException` raised when `ct.IsCancellationRequested == false` (internal timeout) resolves the candidate `Indeterminate`; the same exception type raised when `ct.IsCancellationRequested == true` is not caught by the new logic and propagates out of `ProcessAsync` unchanged. | `Test_SC4_InternalTimeoutIndeterminate_CallerCancellationPropagates` |
| SC-5 | A B2 `404` (`B2NotFoundException`) continues to resolve `ConfirmedAbsent` exactly as today — `Test_ProcessAsync`'s existing `CrossCheckExcluded`/self-heal test(s) (CAS-006 `Test_V9_CrossCheckExcludedCandidateNeverPersisted`, CAS-012's SC-2) pass unmodified. | Existing tests, re-run, unmodified |
| SC-6 | Existing `Test_ProcessAsync_B2ApiException_MarksFailedWithGenericReason` is rewritten (it asserted the now-superseded whole-task-failure behavior for exactly this exception type) — its replacement (SC-1/SC-2) asserts the new per-candidate isolation instead; a search for the literal old test name in the test project after this change returns nothing. | Manual check + `Test_SC1_...`/`Test_SC2_...` |
| SC-7 | No B2 credential, authorization token, upload token, search term, or file content ever appears in a log line written by the new `catch` clauses — only `file_index_id`, the B2 status code (for `B2ApiException`), and a fixed message template (extends CAS-006 SC-11/CAS-012 SC-4's existing log-hygiene check). | `Test_SC7_NoCredentialOrSensitiveDataInIndeterminateLogLines` |

---

## §11 Security Notes

`change_type` is `enhancement`, not `security`, and this spec does not touch B2 credential
brokering, session/auth, or introduce a new state-changing user-facing endpoint. It does touch the
audit log (a new `AuditAction` member) — per this project's precedence rule (CLAUDE.md: "audit log
completeness — FR-23" ranks above ordinary style/scope concerns), the new `CrossCheckIndeterminate`
write:
- Is issued **before** the candidate's iteration completes (matches the existing
  `CrossCheckExcluded` write's placement, itself already compliant with FR-23's "audit before
  success response" rule as adapted to a background Worker task — CAS-006 §12).
- Carries `TabModule = "Keyword Search"` (existing constant, satisfies `dbo.audit_log`'s
  `CK_audit_log_tab_module` check constraint — no new tab-module value needed).
- Carries `FilePath = candidate.RelativePath` and `TaskQueueId = task.TaskQueueId`, matching
  `CrossCheckExcluded`'s existing fields exactly, for symmetry and so an auditor filtering the
  Audit Log screen by file path sees both possible cross-check outcomes for that file.
- `ResultDetail` is a short, fixed string (no B2 error message body passed through) — B2's own
  error `message` field could theoretically echo back some request context; to stay conservative
  this spec's audit `ResultDetail` uses a fixed literal ("B2 cross-check could not be resolved
  after retries.") rather than the caught exception's `Message`, unlike the *log* line (§10 SC-7),
  which needs the status code for operational triage but is not a durable, exported audit record.

No new PII or B2 secret exposure surface is introduced. No security impact beyond the audit-log
addition described above.

---

## §12 Rollback Plan

1. Revert the four files in §5 to their pre-this-spec state (`git revert` the commit).
2. **Database rollback:** N/A — no migration was applied (§7).
3. **Config rollback:** N/A — no configuration key was added or changed (§2.3).
4. **Task-queue rollback:** A `KeywordSearch` task that completed under this spec's new logic with
   a non-null `IndeterminateCount` and one or more `CrossCheckIndeterminate` audit rows is not
   affected by reverting the code — those rows remain valid historical audit records (append-only,
   never deleted, per `dbo.audit_log`'s own design note) even after rollback; they simply become
   the only rows of that action type, since the reverted code can no longer produce new ones. No
   `Queued`/`Running` row's shape changes (this spec adds no task-queue column), so in-flight tasks
   at the moment of rollback are unaffected either way.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `KeywordSearchTaskProcessorTests` | `Test_SC1_OneIndeterminateCandidateAmongManyStillCompletesTask` | SC-1, V1 |
| `KeywordSearchTaskProcessorTests` | `Test_SC2_IndeterminateCandidateNotPersistedNotSoftDeleted` | SC-2, V2 |
| `KeywordSearchTaskProcessorTests` | `Test_SC3_HttpRequestExceptionResolvesIndeterminate` | SC-3 |
| `KeywordSearchTaskProcessorTests` | `Test_SC4_InternalTimeoutIndeterminate_CallerCancellationPropagates` | SC-4, V4 |
| `KeywordSearchTaskProcessorTests` | `Test_V1_IndeterminateB2FailureDoesNotFailWholeTask` | V1 |
| `KeywordSearchTaskProcessorTests` | `Test_V2_IndeterminateOutcomeAuditedDistinctlyFromExcluded` | V2 |
| `KeywordSearchTaskProcessorTests` | `Test_V3_IndeterminateCountPopulatedOnCompletedSummary` | V3 |
| `KeywordSearchTaskProcessorTests` | `Test_V4_CallerCancellationStillPropagatesNotIndeterminate` | V4 |
| `KeywordSearchTaskProcessorTests` | `Test_V5_NonB2DispositionFailureStillFailsWholeTask` | V5 |
| `KeywordSearchTaskProcessorTests` | `Test_SC7_NoCredentialOrSensitiveDataInIndeterminateLogLines` | SC-7 |

Coverage target: ≥ 85% on changed files (`KeywordSearchTaskProcessor.cs`,
`KeywordSearchResultSummaryDto.cs`, `AuditAction.cs`), per
`python -m cachet_agent_tools coverage parse`.

---

## §14 Open Questions

- [ ] **Web-side surfacing of `IndeterminateCount`** — this spec logs and audits the outcome but
  deliberately does not change CAS-022's Keyword Search screen to display it to the end user
  (§6). A follow-up spec should decide whether/how the screen should say "N candidates could not
  be verified against B2 and were omitted — resubmit to retry them," so the omission is visible to
  the user, not just to an auditor reading the Audit Log screen. Flagged rather than assumed, per
  the smallest-diff principle (CLAUDE.md P3).
- [ ] **CAS-001 §6.3's original `B2FileId`/`b2_get_file_info` design** — noted in §0 as an
  already-flagged, still-open deviation. Revisiting it (adding a `B2FileId` column to
  `dbo.file_index`, captured at mirror-population time) would make cross-check an O(1) lookup
  instead of a `ListFileNamesAsync` scan-by-name, independent of this spec's failure-isolation fix.
  Out of scope here; flagged for a separate spec if the client wants it prioritized.

---

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | | | |
| Reviewer | | | |

---

## §16 Verification Plan

### What the Reviewer Will Check
- [ ] §1 Current Behavior matches the actual pre-change code, including the exact line ranges cited
- [ ] §2's three-way `CrossCheckOutcome` switch is implemented with the exact catch ordering in
      §2.1 (`B2NotFoundException` before `B2ApiException` — subclass before base type is required
      for correct C# catch-clause resolution)
- [ ] Only files in §5 were modified (§6 files are untouched, including `B2Client.cs`/`IB2Client.cs`)
- [ ] All §10 Success Criteria have passing tests
- [ ] All §9 Validations are implemented and tested
- [ ] §11 Security Notes: `CrossCheckIndeterminate` audit write uses a fixed `ResultDetail`
      literal, never the raw caught exception's `Message`
- [ ] §4 Blast Radius is accurate — `KeywordSearchResultSummaryDto`/`AuditAction` changes are
      additive only, no existing Api/Web consumer's compiled contract breaks
- [ ] §12 rollback requires no database/config/task-queue-shape reversal (confirm no migration was
      accidentally introduced)
- [ ] `Test_ProcessAsync_B2ApiException_MarksFailedWithGenericReason` (or its renamed equivalent)
      no longer asserts whole-task failure for a plain `B2ApiException` — it must assert
      `Indeterminate`/`Completed` per SC-1/SC-6

## Implementation Log

**Completed:** 2026-09-28
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 82.4% overall (KeywordSearchTaskProcessor.cs 99.1%; KeywordSearchResultSummaryDto.cs 83.3% - shortfall is an uncalled record with-expr copy ctor, pre-existing, accepted by owner) | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Worker/KeywordSearchTaskProcessor.cs` | Added CrossCheckOutcome enum + restructured cross-check to isolate Indeterminate failures |
| `src/Cachet.ArchiveSync.Core/Dtos/KeywordSearchResultSummaryDto.cs` | Added IndeterminateCount field |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs` | Appended CrossCheckIndeterminate member |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `KeywordSearchTaskProcessorTests` | `Test_SC1_OneIndeterminateCandidateAmongManyStillCompletesTask` | SC-1, V1 |
| `KeywordSearchTaskProcessorTests` | `Test_SC2_IndeterminateCandidateNotPersistedNotSoftDeleted` | SC-2, V2 |
| `KeywordSearchTaskProcessorTests` | `Test_SC3_HttpRequestExceptionResolvesIndeterminate` | SC-3 |
| `KeywordSearchTaskProcessorTests` | `Test_SC4_InternalTimeoutIndeterminate_CallerCancellationPropagates` | SC-4, V4 |
| `KeywordSearchTaskProcessorTests` | `Test_V1_IndeterminateB2FailureDoesNotFailWholeTask` | V1 |
| `KeywordSearchTaskProcessorTests` | `Test_V2_IndeterminateOutcomeAuditedDistinctlyFromExcluded` | V2 |
| `KeywordSearchTaskProcessorTests` | `Test_V3_IndeterminateCountPopulatedOnCompletedSummary` | V3 |
| `KeywordSearchTaskProcessorTests` | `Test_V4_CallerCancellationStillPropagatesNotIndeterminate` | V4 |
| `KeywordSearchTaskProcessorTests` | `Test_V5_NonB2DispositionFailureStillFailsWholeTask` | V5 |
| `KeywordSearchTaskProcessorTests` | `Test_SC7_NoCredentialOrSensitiveDataInIndeterminateLogLines` | SC-7 |
