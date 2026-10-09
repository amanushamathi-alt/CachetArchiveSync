---
id: CAS-018
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-23
priority: P1
change_type: security
related_specs: [CAS-001, CAS-002, CAS-008]
touches:
  database: true
  api: true
  ui: false
  worker: false
  b2: true
  config: false
  security: true
migrations: ["20260923_150000__CAS-018-users-get-by-id.sql"]
---

# CAS-018 — Change: Permanent Delete Uses the Acting User's Own B2 Key, Not the System Key

## §1 Current Behavior

`POST /quarantine/delete` ([QuarantineController.cs:96](../../src/Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs#L96))
is gated by `[Authorize(Policy = "CachetFullAccess")]` and calls
`QuarantinePermanentDeleteService.DeleteAsync(request, userId, ct)` directly, awaited
synchronously in the same HTTP request (not queued, despite CAS-001 §6.4's text describing this
action as queued — a pre-existing spec/code discrepancy, noted but out of scope here; see §14).

Inside `DeleteAsync` ([QuarantinePermanentDeleteService.cs:48](../../src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs#L48)),
for each requested `fileId`: B2 delete runs first (`DeleteAllVersionsAsync`, calling
`IB2Client.ListFileVersionsAsync`/`DeleteFileVersionAsync`), then the local `dbo.file_index` row
is soft-deleted, then `dbo.quarantine_entry.status` transitions to `Deleted` — each step audited
on failure via `DeleteFailedB2`/`DeleteFailedLocal` (`AuditAction.cs`).

`IB2Client`'s only implementation, `B2Client` ([B2Client.cs:50-58](../../src/Cachet.ArchiveSync.Api/B2/B2Client.cs#L50-L58)),
is a process-wide singleton constructed exclusively from the server-held system key
(`B2:SystemKeyId`/`SystemApplicationKey`/`BucketName`) — by design, per CAS-001 §6.1/§7 V1, there
is no way to construct or call it with any other credential. Every B2-touching action in this
application (login-time key check aside) executes as this one system identity, including
Permanent Delete. Backblaze's own server-side access logs therefore always attribute every
delete to the system account, never to the person who actually triggered it, and B2 itself never
independently confirms that the _specific acting user_ still has delete rights — only the app's
own `CachetFullAccess` policy claim (set once, at login) stands between a session and this route.

Separately, `dbo.users.b2_application_key`/`_id` (per-user key, encrypted) is today used exactly
once, at login (`LoginService.cs:79-103`), via `IUserB2KeyAuthorizer.AuthorizeAsync`, purely to
derive the Full Access/Read Only capability badge. It is never touched again afterward.

`IUserRepository` currently exposes only `GetByUsernameAsync` — there is no by-`user_id` lookup,
so nothing outside the login flow can currently re-fetch a user's stored B2 key.

## §2 Desired Behavior

`POST /quarantine/delete` must authorize itself against Backblaze using **the acting user's own
B2 key**, checked live at the time of the call, before any B2 delete is attempted — not the
system key, and not the stale `b2_capability` claim from login.

Specifically, at the start of `DeleteAsync`, after the existing §7 V1/V2 validation and before
the per-file loop:

1. Look up the acting user's row by `userId` (new `IUserRepository.GetByIdAsync`), decrypt their
   stored `b2_application_key_id`/`b2_application_key` (`ICredentialProtector.Unprotect`, same
   path `LoginService` already uses).
2. Call `IUserB2KeyAuthorizer.AuthorizeAsync(keyId, applicationKey, ct)` — live, fresh, not
   reused from login — to get a current `B2AuthorizationInfo`.
3. If that call fails (revoked/invalid key, network failure), or succeeds but
   `Capabilities` does not include `deleteFiles`: **abort the entire request before touching any
   file.** No B2 call, no local mutation, for any requested `fileId`. Write one audit entry
   (`AuditAction.DeleteAuthorizationDenied`, new value) and return a new whole-request outcome
   (`QuarantineDeleteOutcome.AuthorizationDenied`, new value) → `403 Forbidden` Problem response.
4. If the live check passes, use _that_ authorization's token for every B2 call in this request's
   per-file loop, in place of the system key. The bucket id is not taken from this authorization
   response — it is the already-resolved single-bucket id (system-key-derived, same as
   `B2Client.GetBucketIdAsync`; see §14) — since bucket identity doesn't vary by acting user.
   `B2Client`/`IB2Client` are not modified — see the
   new, narrowly-scoped `IUserScopedB2DeleteClient` in §5, so none of `IB2Client`'s other four
   consumers (search cross-check, Sync Copy/Update/Purge, mirror population) are touched or
   affected.

All other B2-touching actions (Keyword Search cross-check, Sync Copy/Update/Purge, mirror
population, login-time key validation) are **unchanged** — they continue to use the system key
exactly as today. This is a Permanent-Delete-only change.

A B2 failure _mid_-loop (e.g. the user's key is revoked in the window between step 3's check and
a later `DeleteFileVersionAsync` call within the same request) is not a new failure category to
build — it flows into the existing per-file `DeleteFailedB2` path unchanged. This spec's §11
states explicitly that a live-key-revocation-mid-request is now an anticipated cause of that
existing outcome, not merely a generic API error.

## §3 Reason for Change

- **Type:** `security`
- **Trigger:** User-identified gap (Rakesh Devarajan, 2026-09-23, in conversation): with the
  system key executing every operation, (a) B2's own access logs cannot distinguish which portal
  user actually performed a delete, and (b) B2 itself never independently confirms the acting
  user's delete rights — the app-level `CachetFullAccess` policy claim is the only gate, and it is
  set once at login, so it cannot reflect a mid-session B2-side permission change. This spec
  narrows CAS-001 §6.1's original system-key-for-everything decision (confirmed 2026-09-10) to
  exclude Permanent Delete specifically, because — unlike Sync/Search — Permanent Delete already
  executes synchronously within an authenticated request with a live, known `userId`, so it does
  not hit the "no live session once a job is dequeued" problem CAS-001 §6.1 cites as the reason
  the system key exists at all.
- **Risk if not changed:** B2-side forensic/audit trail cannot attribute a permanent, irreversible
  deletion (FR-16/FR-17, no-recovery by design) to the actual acting person — only to the shared
  system account. Additionally, a stale app-level capability claim (e.g. a user downgraded from
  Full Access to Read Only at the B2 side mid-session) would not be caught until this change adds
  a live re-check.

## §4 Blast Radius (Graph Impact)

### Primary Change Target

File: `src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs`
Entity: `QuarantinePermanentDeleteService` (implements `IQuarantinePermanentDeleteService`, degree 2)

### Direct Dependents (nodes with edges INTO the changed entity)

- `QuarantineController` — calls `DeleteAsync`; unchanged signature, only new outcome branch added.
- `QuarantinePermanentDeleteServiceTests` — must be extended for the new pre-batch check.

### Downstream Modules

Quarantine (Permanent Delete only). Auth is touched only via the new `IUserRepository.GetByIdAsync`
method (Auth module's interface, not its login flow). No other module (Keyword Search, O-Drive
Sync, Task Manager, Audit Log, Reporting) is affected.

### Cross-Project Risk

None. `graphify explain "IUserRepository"` shows degree 4 (`LoginService`, `AuthController`, two
`UserRepository` implementations — Api's and Web's). This spec adds a new _method_ to the
interface and Api's implementation only; Web's separate `UserRepository` implementation
(`src/Cachet.ArchiveSync.Web/Data/UserRepository.cs`) does not need `GetByIdAsync` and is left
unchanged (its existing `GetByUsernameAsync`/`IncrementSessionTokenVersionAsync` are already
`NotSupportedException` stubs for methods Web doesn't use — same pattern applies to leaving
`GetByIdAsync` unimplemented-by-omission there, since C# requires every implementor to satisfy
the interface — **flagged**: Web's `UserRepository` MUST implement the new method too, even if
only as a `NotSupportedException` stub matching its existing convention, or the Web project fails
to compile. This is a required, mechanical touch, not a logic change — see §5.

No Worker changes. `graphify explain "IB2Client"` shows degree 6 (`B2Client`,
`KeywordSearchTaskProcessor`, `SyncCopyTaskProcessor`, `SyncUpdateTaskProcessor`,
`SyncPurgeTaskProcessor`, `QuarantinePermanentDeleteService`) — this spec deliberately does not
modify `IB2Client`/`B2Client` at all, specifically to avoid rippling into the four Worker task
processors. A new, separate, narrowly-scoped interface (`IUserScopedB2DeleteClient`, §5) carries
the user-authorized delete calls instead.

### God Node Involvement

None. `IB2Client` (degree 6) and `IUserRepository` (degree 4) are both below the >10-edge god-node
threshold, and this spec avoids modifying `IB2Client`'s contract regardless.

### Graphify Queries Run

```
python -m graphify explain "QuarantinePermanentDeleteService"
python -m graphify query "what depends on QuarantinePermanentDeleteService" --budget 1500
python -m graphify explain "IB2Client"
python -m graphify explain "IUserRepository"
```

---

## §5 Files to Change

| File                                                                      | Change Type | Description                                                                                                                                                                                                                                                                                                      |
| ------------------------------------------------------------------------- | ----------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Cachet.ArchiveSync.Core/Interfaces/IUserRepository.cs`                   | modify      | Add `Task<UserAccount?> GetByIdAsync(int userId, CancellationToken ct)`.                                                                                                                                                                                                                                         |
| `Cachet.ArchiveSync.Api/Repositories/UserRepository.cs`                   | modify      | Implement `GetByIdAsync`, calling new `dbo.users_get_by_id` sproc.                                                                                                                                                                                                                                               |
| `Cachet.ArchiveSync.Web/Data/UserRepository.cs`                           | modify      | Implement `GetByIdAsync` as `NotSupportedException`, matching this class's existing stub convention for methods Web doesn't use (required for the project to compile once the interface gains a member).                                                                                                         |
| `Cachet.ArchiveSync.Core/Enums/AuditAction.cs`                            | modify      | Append `DeleteAuthorizationDenied` (existing members not reordered, per file's own convention).                                                                                                                                                                                                                  |
| `Cachet.ArchiveSync.Core/Interfaces/IQuarantinePermanentDeleteService.cs` | modify      | Append `QuarantineDeleteOutcome.AuthorizationDenied`.                                                                                                                                                                                                                                                            |
| `Cachet.ArchiveSync.Core/Interfaces/IUserScopedB2DeleteClient.cs`         | add         | New, minimal interface: `ListFileVersionsAsync`/`DeleteFileVersionAsync` equivalents, each taking an explicit `B2AuthorizationInfo` + `bucketId` (or key-derived bucket context) parameter instead of resolving one internally — deliberately not an extension of `IB2Client` (§4 cross-project risk avoidance). |
| `Cachet.ArchiveSync.Api/B2/UserScopedB2DeleteClient.cs`                   | add         | Implementation — reuses `B2WireModels`' internal wire types (same reuse rationale `UserB2KeyAuthorizer.cs`'s doc comment already gives for living in this namespace).                                                                                                                                            |
| `Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs`     | modify      | Add the live pre-batch authorization check (§2 steps 1-4); route the per-file loop's B2 calls through `IUserScopedB2DeleteClient` with the live authorization instead of `IB2Client`/the system key.                                                                                                             |
| `Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs`              | modify      | Map `QuarantineDeleteOutcome.AuthorizationDenied` to `403 Forbidden` Problem response.                                                                                                                                                                                                                           |
| `Cachet.ArchiveSync.Api/Program.cs`                                       | modify      | Register `IUserScopedB2DeleteClient`; inject `IUserRepository`, `ICredentialProtector`, `IUserB2KeyAuthorizer`, `IUserScopedB2DeleteClient` into `QuarantinePermanentDeleteService`'s constructor (first three are already registered as singletons/scoped for the login path — reused, not duplicated).         |
| `Database/migrations/20260923_150000__CAS-018-users-get-by-id.sql`        | add         | New `dbo.users_get_by_id` stored procedure.                                                                                                                                                                                                                                                                      |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.users_get_by_id.sql`    | add         | Mirror of the above (hybrid model, per CLAUDE.md §"Database Changes").                                                                                                                                                                                                                                           |
| `Cachet.ArchiveSync.Api.Tests/QuarantinePermanentDeleteServiceTests.cs`   | modify      | Extend for the new pre-batch check (success, revoked-key, downgraded-capability, mid-loop-revocation cases).                                                                                                                                                                                                     |
| `Cachet.ArchiveSync.Api.Tests/UserScopedB2DeleteClientTests.cs`           | add         | New test class.                                                                                                                                                                                                                                                                                                  |
| `Cachet.ArchiveSync.Api.Tests/UserRepositoryTests.cs`                     | modify      | `GetByIdAsync` coverage (mirrors existing `GetByUsernameAsync` test shape).                                                                                                                                                                                                                                      |
| `Cachet.ArchiveSync.Api.IntegrationTests/QuarantineControllerTests.cs`    | modify      | New `403 authorization-denied` case.                                                                                                                                                                                                                                                                             |

---

## §6 Files NOT to Change

| File                                                                                         | Why left untouched                                                                                                                                                |
| -------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Cachet.ArchiveSync.Core/Interfaces/IB2Client.cs`                                            | Deliberately not extended — avoids rippling into 4 Worker task processors (§4).                                                                                   |
| `Cachet.ArchiveSync.Api/B2/B2Client.cs`                                                      | System-key client for every other B2 operation, unchanged.                                                                                                        |
| `Cachet.ArchiveSync.Api/B2/UserB2KeyAuthorizer.cs`                                           | Reused as-is (already does exactly the "authorize a caller-supplied key" call this spec needs at delete time — no new logic).                                     |
| `Cachet.ArchiveSync.Api/Services/LoginService.cs`                                            | Login flow unchanged — still one-time capability-badge derivation only.                                                                                           |
| `Cachet.ArchiveSync.Worker/*TaskProcessor.cs` (all four)                                     | Out of scope — Sync/Search retain the system key; the "no live session once dequeued" problem CAS-001 §6.1 cites still applies to them and is not addressed here. |
| `Cachet.ArchiveSync.Api/Repositories/QuarantineEntryRepository.cs`, `FileIndexRepository.cs` | Steps 2/3 of the existing per-file pipeline (local soft-delete, status transition) are unchanged — only which B2 identity executes Step 1 changes.                |

---

## §7 Database Changes

### Changed Objects

| Object                | Schema | Change               | Notes                                                                                                                                                                                                                 |
| --------------------- | ------ | -------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `dbo.users_get_by_id` | dbo    | new stored procedure | Single-row lookup by `user_id`, same column shape as `dbo.users_get_by_username` (CAS-002's sproc) — `user_id, username, password_hash, b2_application_key_id, b2_application_key, is_active, session_token_version`. |

### Migration Required

Yes.

- Migration: `Database/migrations/20260923_150000__CAS-018-users-get-by-id.sql` (append-only; timestamp set at implementation time)
- Mirror update: `Database/CachetArchiveSync/Stored-Procedures/dbo.users_get_by_id.sql`

---

## §8 API Contract Changes

| Endpoint                  | Field                       | Before                                                                                         | After                                                                                                                   | Breaking?                                                                           |
| ------------------------- | --------------------------- | ---------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| `POST /quarantine/delete` | response (new failure case) | Outcomes: `422 validation-failed`, `422 confirmation-required`, `404 file-not-found`, `200 OK` | Adds `403 Forbidden` (`authorization-denied`) when the acting user's live B2 authorization fails or lacks `deleteFiles` | No — additive; existing success/failure shapes for `200`/`422`/`404` are unchanged. |

---

## §9 Validations Changed

| ID  | Rule (Before)                                                                 | Rule (After)                                                                                                                                                                     | Test                                                        |
| --- | ----------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------- |
| V7  | Delete authorization relies solely on the login-time `CachetFullAccess` claim | Delete additionally requires a live `b2_authorize_account` check of the acting user's own key, including `deleteFiles` in its current capability set, before any file is touched | `Test_V7_LiveB2AuthorizationRequiredBeforeAnyDelete`        |
| V8  | N/A (system key never varies mid-request)                                     | A live-authorization failure (revoked key, missing capability, or B2 API error) aborts the entire batch — zero files touched, one audit entry written                            | `Test_V8_AuthorizationFailureAbortsWholeBatchBeforeAnyFile` |

---

## §10 Success Criteria

| ID   | Criterion                                                                                                                                                                                                                  | Test                                                                                      |
| ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| SC-1 | A Permanent Delete request from a user whose live B2 key check succeeds and includes `deleteFiles` completes exactly as today (all existing per-file behavior unchanged)                                                   | `Test_SC1_LiveAuthorizationSuccessPreservesExistingBehavior`                              |
| SC-2 | A Permanent Delete request from a user whose B2 key has been revoked at B2 (live check fails) touches zero files, writes one `DeleteAuthorizationDenied` audit entry, returns `403`                                        | `Test_SC2_RevokedKeyAbortsBeforeAnyFileTouched`                                           |
| SC-3 | A Permanent Delete request from a user whose live B2 capabilities no longer include `deleteFiles` (downgraded, key not revoked) is rejected identically to SC-2                                                            | `Test_SC3_DowngradedCapabilityRejected`                                                   |
| SC-4 | The stale login-time `b2_capability`/`CachetFullAccess` claim alone is insufficient — a session holding that claim is still rejected at delete time if the live B2 check fails                                             | `Test_SC4_StaleClaimDoesNotBypassLiveCheck`                                               |
| SC-5 | A B2 failure occurring _between_ individual `DeleteFileVersionAsync` calls within one file's version loop (mid-loop key revocation) is recorded via the existing `DeleteFailedB2` per-file path, not a new/undefined state | `Test_SC5_MidLoopRevocationMapsToExistingDeleteFailedB2`                                  |
| SC-6 | Every other B2-touching action (Search cross-check, Sync Copy/Update/Purge, mirror population, login) is provably unaffected — no change to `B2Client`, `IB2Client`, or any Worker task processor                          | `Test_SC6_OtherB2ConsumersUnchanged` (static/compile-level check: no diff in those files) |
| SC-7 | The user's live B2 key/token from this flow is never logged, in whole or in part (same rule as CAS-001 SC-5 / CAS-002 §11)                                                                                                 | `Test_SC7_NoKeyOrTokenLeakageInLogs`                                                      |

---

## §11 Security Notes

- This spec is a _narrowing_, not a reversal, of CAS-001 §6.1's system-key decision: it applies
  only to Permanent Delete, and only because that action already runs synchronously within an
  authenticated request (unlike Sync/Search, which remain correctly on the system key per CAS-001
  §6.1's "no live session once dequeued" reasoning).
- The user's decrypted key and live authorization token exist only for the duration of the
  request (scoped/local variables) — never cached beyond it, never written to the task queue,
  never logged (extends CAS-001 SC-5 / CAS-002 §11's existing no-key-leakage rule to this new call
  site).
- The live re-check at delete time is the entire point of this change and is not optional: relying
  on the login-time `CachetFullAccess` claim alone would not close the gap this spec exists to
  close (see SC-4).
- A B2-side revocation mid-request (between the live check and a later per-version delete call)
  can leave a file's B2 versions partially deleted. This is not new to this spec — the existing
  no-rollback design (FR-17) already accepts that a B2-side partial failure after some progress is
  audited, not undone. This spec adds a new _cause_ of that existing state, not a new state.
- `IUserScopedB2DeleteClient` must enforce the same construction-time/call-time discipline
  `B2Client` already has for the system key (CAS-001 §7 V1): it must never be reachable from a
  code path where the caller could substitute the system key or another user's key for the
  acting user's own — the authorization object it's called with must originate only from this
  request's own live `AuthorizeAsync` call, never a cached/shared one.

---

## §12 Rollback Plan

1. Revert `QuarantinePermanentDeleteService.cs`, `QuarantineController.cs`, `Program.cs` DI
   registrations, and the new `IUserScopedB2DeleteClient`/`UserScopedB2DeleteClient` files — this
   restores Permanent Delete to the system-key path unchanged.
2. Database rollback: the new `dbo.users_get_by_id` sproc is additive and read-only — no rollback
   needed; it can be left in place harmlessly if the rest of the change is reverted.
3. Config rollback: N/A — no config keys added or changed by this spec.
4. Task-queue rollback: N/A — Permanent Delete does not use the task queue (confirmed §1); no
   `Queued`/`Running` row shape is touched by this spec.

---

## §13 Test Plan

| Class                                     | Method                                                                  | Proves SC / V |
| ----------------------------------------- | ----------------------------------------------------------------------- | ------------- |
| `QuarantinePermanentDeleteServiceTests`   | `Test_V7_LiveB2AuthorizationRequiredBeforeAnyDelete`                    | V7            |
| `QuarantinePermanentDeleteServiceTests`   | `Test_V8_AuthorizationFailureAbortsWholeBatchBeforeAnyFile`             | V8            |
| `QuarantinePermanentDeleteServiceTests`   | `Test_SC1_LiveAuthorizationSuccessPreservesExistingBehavior`            | SC-1          |
| `QuarantinePermanentDeleteServiceTests`   | `Test_SC2_RevokedKeyAbortsBeforeAnyFileTouched`                         | SC-2          |
| `QuarantinePermanentDeleteServiceTests`   | `Test_SC3_DowngradedCapabilityRejected`                                 | SC-3          |
| `QuarantinePermanentDeleteServiceTests`   | `Test_SC4_StaleClaimDoesNotBypassLiveCheck`                             | SC-4          |
| `QuarantinePermanentDeleteServiceTests`   | `Test_SC5_MidLoopRevocationMapsToExistingDeleteFailedB2`                | SC-5          |
| `UserScopedB2DeleteClientTests`           | (construction/call discipline cases, mirrors `B2ClientTests`' V1 shape) | §11           |
| `LogHygieneTests` (or equivalent)         | `Test_SC7_NoKeyOrTokenLeakageInLogs`                                    | SC-7          |
| `QuarantineControllerTests` (integration) | 403 authorization-denied case                                           | SC-2/SC-3     |
| `UserRepositoryTests`                     | `GetByIdAsync` happy path + not-found                                   | §5            |

Coverage target: ≥ 85% on changed files.

---

## §14 Open Questions — Resolved 2026-09-23

- [x] **Bucket context for the user's live authorization.** Resolved: no `b2_list_buckets` call is
      needed on the user's own key at all, and the open question's premise doesn't apply.
      `B2Client.cs:133-168` already resolves and caches the bucket id today via one
      `b2_list_buckets` call made with the **system key**, matched against configured
      `B2:BucketName`. Bucket identity is a fact about the bucket, not about which key is asking —
      and this project deliberately targets exactly one B2 bucket (confirmed 2026-09-23), so the
      bucket id never varies by acting user. `IUserScopedB2DeleteClient`/`UserScopedB2DeleteClient`
      should reuse that already-resolved bucket id rather than deriving one from the user's own
      authorization response — either (a) expose it from `B2Client` via a small internal accessor
      and inject it, or (b) have `UserScopedB2DeleteClient` do its own one-time `b2_list_buckets`
      resolution using the **system key**, cached the same way `B2Client` does. The user's own key
      is used _only_ for the live `deleteFiles` capability check, never for bucket resolution.
      CAS-001 §6.1's seven-operations table does not need a `b2_list_buckets`-per-user row added.
- [x] **Rollback toggle.** Resolved: not adding a config switch
      (e.g. `Quarantine:UseSystemKeyForDelete`). §12's rollback plan (revert the small, narrowly
      scoped file set + redeploy) is already sufficient and low-risk for this change's size. A
      toggle would also be a standing security liability of its own: it's a switch that silently
      drops the live-permission check this spec exists to add, with no visible signal in the code
      if left on after an incident. Consistent with this project's no-speculative-abstraction
      convention (`karpathy-coding-principles`) — not building it until a real, separate need for
      fast rollback tooling is identified.
- [x] **Pre-existing spec/code discrepancy** (found during CAS-001 review, 2026-09-23): confirmed
      as a genuine, separate, doc-only fix. CAS-001 §6.4 describes Permanent Delete as
      queued/asynchronous; the code runs it synchronously in-request, matching BRD §12.3 item 8's
      B2-then-local sequencing — and that synchronous execution is exactly what makes this spec's
      live per-user check feasible. Track as a small follow-up documentation correction to CAS-001
      §6.4; not bundled into CAS-018's scope and not a blocker for it.
- [x] **Audit action/outcome naming** (`DeleteAuthorizationDenied` / `AuthorizationDenied`).
      Resolved: matches existing convention, no change needed. `AuditAction` already uses
      `DeleteFailedLocal`/`DeleteFailedB2`, and every `*Outcome` enum in this codebase
      (`QuarantineDeleteOutcome`, `SyncActionSubmitOutcome`, `QuarantineAddOutcome`, etc.) uses
      plain outcome names (`ValidationFailed`, `ConfirmationRequired`, `FileNotFound`) with no
      inconsistent prefixing to reconcile. Both proposed names fit their respective families as-is.

---

## §15 Sign-off

| Role     | Name                                        | Date       | Notes   |
| -------- | ------------------------------------------- | ---------- | ------- |
| Author   | R. Devarajan (TraceCounts), via Claude Code | 2026-09-23 |         |
| Reviewer |                                             |            | Pending |

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

Reviewer writes `CAS-018.review.md`:

- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5 file list)
- Unintended blast radius (graphify, or manual read, found changes in §6 files)
- Security gaps

## Implementation Log

**Completed:** 2026-09-23
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 95.9%/96.5%/100% new logic; 67.8% overall (pre-existing DB-integration-only files, see report) | Endpoints ✓

### Files Changed

| File                                                                                           | Change                                        |
| ---------------------------------------------------------------------------------------------- | --------------------------------------------- |
| `src/Cachet.ArchiveSync.Core/Interfaces/IUserRepository.cs`                                    | add GetByIdAsync                              |
| `src/Cachet.ArchiveSync.Api/Repositories/UserRepository.cs`                                    | implement GetByIdAsync                        |
| `src/Cachet.ArchiveSync.Web/Data/UserRepository.cs`                                            | GetByIdAsync NotSupportedException stub       |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs`                                             | append DeleteAuthorizationDenied              |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantinePermanentDeleteService.cs`                  | append AuthorizationDenied outcome            |
| `src/Cachet.ArchiveSync.Core/Interfaces/IUserScopedB2DeleteClient.cs`                          | new interface                                 |
| `src/Cachet.ArchiveSync.Api/B2/UserScopedB2DeleteClient.cs`                                    | new implementation                            |
| `src/Cachet.ArchiveSync.Api/Services/QuarantinePermanentDeleteService.cs`                      | live pre-batch B2 authorization check         |
| `src/Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs`                               | map AuthorizationDenied to 403                |
| `src/Cachet.ArchiveSync.Api/Program.cs`                                                        | DI registration                               |
| `Database/migrations/20260923_150000__CAS-018-users-get-by-id.sql`                             | new migration                                 |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.users_get_by_id.sql`                         | new mirror                                    |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantinePermanentDeleteServiceTests.cs`                    | extended for V7/V8/SC1-7                      |
| `src/Cachet.ArchiveSync.Api.Tests/UserScopedB2DeleteClientTests.cs`                            | new test class                                |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/QuarantineControllerTests.cs`                     | 403 authorization-denied case                 |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/UserRepositoryDatabaseTests.cs`                   | GetByIdAsync DB-only tests                    |
| `src/Cachet.ArchiveSync.Api.Tests/UserRepositoryTests.cs`                                      | doc comment update                            |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/Quarantine/QuarantineApiWebApplicationFactory.cs` | register IUserScopedB2DeleteClient substitute |

### Tests Added

| Class                                   | Method                                                       | Covers                            |
| --------------------------------------- | ------------------------------------------------------------ | --------------------------------- |
| `QuarantinePermanentDeleteServiceTests` | `Test_V7_LiveB2AuthorizationRequiredBeforeAnyDelete`         | V7                                |
| `QuarantinePermanentDeleteServiceTests` | `Test_V8_AuthorizationFailureAbortsWholeBatchBeforeAnyFile`  | V8                                |
| `QuarantinePermanentDeleteServiceTests` | `Test_SC1_LiveAuthorizationSuccessPreservesExistingBehavior` | SC-1                              |
| `QuarantinePermanentDeleteServiceTests` | `Test_SC2_RevokedKeyAbortsBeforeAnyFileTouched`              | SC-2                              |
| `QuarantinePermanentDeleteServiceTests` | `Test_SC3_DowngradedCapabilityRejected`                      | SC-3                              |
| `QuarantinePermanentDeleteServiceTests` | `Test_SC4_StaleClaimDoesNotBypassLiveCheck`                  | SC-4                              |
| `QuarantinePermanentDeleteServiceTests` | `Test_SC5_MidLoopRevocationMapsToExistingDeleteFailedB2`     | SC-5                              |
| `QuarantinePermanentDeleteServiceTests` | `Test_SC6_OtherB2ConsumersUnchanged`                         | SC-6                              |
| `QuarantinePermanentDeleteServiceTests` | `Test_SC7_NoKeyOrTokenLeakageInLogs`                         | SC-7                              |
| `QuarantineControllerTests`             | `Test_PermanentDelete_LiveAuthorizationDenied_Returns403`    | SC-2/SC-4 (integration)           |
| `UserRepositoryDatabaseTests`           | `Test_GetByIdAsync_KnownUserId_ReturnsMappedRow`             | CAS-018 §5 (DB-only, skip-marked) |
| `UserRepositoryDatabaseTests`           | `Test_GetByIdAsync_UnknownUserId_ReturnsNull`                | CAS-018 §5 (DB-only, skip-marked) |

### Migration

`20260923_150000__CAS-018-users-get-by-id.sql`
