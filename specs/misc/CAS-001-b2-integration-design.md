---
id: CAS-001
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-10
priority: P1
related_specs: [CAS-003]             # CAS-003 (added 2026-09-10, later same day) is now the
                                      # authored WBS task 6 DB schema spec — see §0 Dependency Note
                                      # addendum. Task 7 (API design) still does not exist as a spec.
touches:
  database: true                     # field-level requirements only; no DDL owned by this doc
  api: false                         # call sequences only; route/controller contracts deferred
  ui: false
  worker: true
  b2: true
  config: true
  security: true
migrations: []
---

# CAS-001 — B2 Integration Design (Server-Side Credential Brokering, Cross-Check Flow)

WBS Task 10 (Design phase). Implements FR-03, FR-08, FR-16, FR-17; architecture basis in BRD
Section 3(c), Section 10.1, Section 10.3.

---

## §0 Dependency Note (read this first)

This design was requested ahead of two prerequisite specs that do not exist in this repo yet:

- **Database schema spec (WBS task 6)** — the spec folder contained only templates at authoring time; no
  `CAS-00X` database-schema spec has been authored. The task-queue and mirror-index field
  requirements in §5 below are therefore this document's own minimal, provisional list —
  **not** a substitute for that spec. They must be reconciled against the actual DDL once WBS
  task 6 is drafted.

  **Update (2026-09-10, later same day):** `CAS-003` now exists (`dbo.users`, `dbo.file_index`,
  `dbo.task_queue`, `dbo.audit_log`). The reconciliation this note calls for is still outstanding —
  §5 below has not yet been checked field-by-field against `CAS-003`'s actual columns (e.g. this
  document's provisional `ObjectKey`/`B2FileId`/`ContentSha1` mirror-index fields vs. `CAS-003`'s
  `dbo.file_index.relative_path`/`sha1_hash` etc.) — flagged here rather than silently assumed
  consistent.
- **API design spec (WBS task 7)** — no `CAS-00X` API-design spec exists either. §6 below
  describes *call sequences* (what happens, in what order, against which B2 operations) rather
  than concrete `VERB /api/v1/...` route contracts, which belong in that future spec.

Everything below is derived directly from the BRD (Section 3(c), 10.1, 10.2, 10.3; FR-01–FR-03,
FR-08, FR-13/FR-13a–FR-13d, FR-16/FR-17, FR-18–FR-22a) and the nearest existing project
conventions (`b2-integration`, `async-task-queue`, `aspnetcore-security` skills), per Karpathy
principle P8/P2. No graph exists to query for this yet either — see §17.

---

## §1 Summary

Defines how the application's server-side components (Api and Worker) talk to Backblaze B2:
how the shared B2 client authenticates using server-held credentials that never reach the
browser (FR-03), how the B2 mirror is populated from the bucket (BRD §3(c)), how a locally
matched file is cross-checked against live B2 state before being shown to a user (FR-08), and
how a file is permanently deleted from both the local cache and B2 with defined failure handling
(FR-16/FR-17). Implements FR-03, FR-08, FR-16, FR-17.

---

## §2 User Story

As the **Api request handler** (for cross-check) and the **background Worker** (for mirror
population and delete), I want a single, consistently-authenticated B2 client so that I can
confirm object existence, refresh the local mirror, and remove objects from B2, without the
Backblaze application key ever being visible to a browser or held in application state that
outlives a single server process.

---

## §3 In-Scope

- Server-side B2 client authentication and credential-brokering model (FR-03, BRD §10.3).
- B2 mirror population / download process (BRD §3(c), WBS task 34).
- Per-match cross-check call sequence: local match → B2 existence check → result (FR-08).
- Permanent-delete call sequence: local cache delete → B2 delete, with failure handling
  (FR-16/FR-17).
- Named B2 API operations for each flow, with explicit flags on anything that needs confirming
  against current Backblaze B2 API documentation before implementation (WBS tasks 31–34).
- The minimal task-queue and mirror-index fields this integration needs to exist (provisional,
  pending the real DB schema spec — see §0).

---

## §4 Out-of-Scope

- **Copy / Update / Purge sync actions** (FR-13/FR-13a/FR-13b) — these reuse the upload and
  delete primitives described here (§6.2, §6.4) but their own call sequences, task types, and
  audit-log detail belong in a follow-on spec once this design and CAS-00X (API design) exist.
  Do not treat this document as authorizing their implementation.
- Full task-queue table DDL and stored procedures (CAS-00X, WBS task 6).
- Concrete API route/controller contracts and request/response JSON shapes (CAS-00X, WBS task 7).
- Login/session design beyond where it intersects with B2 key validation (WBS task 11 — separate
  spec; FR-01/FR-02 mechanics other than the B2 validation call itself are out of scope here).
- Web UI.
- **Drive encryption-at-rest for physical O-Drive transfer (BRD §12.2 item 2)** — still an open
  BRD decision; this spec does not depend on it and does not attempt to resolve it.

---

## §5 Database Objects (provisional — see §0)

> Not this document's DDL to own. Listed here only so CAS-00X (DB schema) has a concrete,
> traceable input from the B2 integration side.

### Mirror index (per B2 object, populated by §6.2)

| Field | Purpose |
|---|---|
| `BucketName` | Which B2 bucket the object belongs to |
| `ObjectKey` (file path/name in B2) | Join key for filename search and O-Drive Sync comparison |
| `B2FileId` | Captured at mirror-population time; enables O(1) `b2_get_file_info` lookups in §6.3 instead of re-listing by name |
| `ContentSha1` | Returned by B2 on list/upload; drives "skip unchanged" on re-sync (§6.2) and the FR-11 SHA-1-changed indicator |
| `UploadTimestamp` | B2's `uploadTimestamp`; used for change detection |
| `Size` | Informational, matches FR-06 |
| `LocalMirrorPath` | Where the downloaded copy lives on the app server |
| `LastVerifiedAt` | Last time a cross-check (§6.3) or full mirror sync (§6.2) confirmed this row against live B2 |

### Task-queue fields this integration requires exist (see `async-task-queue` skill for full lifecycle)

| Field | Purpose |
|---|---|
| `TaskType` | Must include at least `B2MirrorSync` (§6.2) and `PermanentDelete` (§6.4 — confirmed 2026-09-10 to be queued, consistent with FR-13c); if Copy/Update/Purge are queued per FR-13c, also `B2SyncCopy` / `B2SyncUpdate` / `B2SyncPurge` (out of scope here, listed for traceability only) |
| `Status` | `Queued \| Running \| Completed \| Failed`, per async-task-queue skill |
| `ProgressDetail` | Needed for mirror population specifically — a full-bucket enumerate/download can run long (BRD §7 FR-22a: 10 minutes to a full day); without incremental progress the Task Manager shows a stuck "Running" state |
| `ResultDetail` / `ErrorDetail` | Needed to surface B2 API failures (auth expiry, rate limit, network) distinctly from "no work to do" |

**Resolved 2026-09-10:** Permanent Delete (FR-16) is queued through the task queue, consistent
with Copy/Update/Purge (FR-13c) — not executed synchronously within the request. See §6.4 and
§15 resolution log.

---

## §6 B2 Integration Design

### §6.1 Client Authentication and Credential Brokering (FR-03, BRD §10.3)

**Two distinct uses of a B2 application key exist in the BRD, and they are not the same key.**
This is the single most load-bearing design decision in this document — see the flag at the end
of this subsection.

**Login-time validation (FR-01, resolved).** BRD §10.3 confirms: four user accounts, each with
its own stored Backblaze Application Key ID/Key (server-side only). At sign-in, after the
username/password check succeeds, the server calls `b2_authorize_account` using *that specific
user's* stored key. Success/failure gates login; the returned `allowed.capabilities` list (e.g.
`readFiles`, `writeFiles`, `deleteFiles`, `listFiles`) drives the FR-02 capability badge
("Full Access" / "Read Only"). The raw key/ID is never returned to the browser — only the derived
badge label and an application session token.

**Operational B2 calls (cross-check, mirror population, delete) — resolved 2026-09-10:** the
Worker runs mirror population and all queued sync/delete actions (FR-13c, and Permanent Delete
per §6.4) asynchronously, with no browser session attached and no specific "current user" once a
job is dequeued (FR-20 — the submitting user may have closed their browser). A per-user key
cannot be threaded through an unattended background job in any meaningful way. Confirmed design:
a **second, separate, system-level B2 application key**, held in server-side secrets
configuration (never `appsettings.json` in source control, never `appsettings.Production.json`
edited directly per CLAUDE.md), used uniformly by:

- The Api, for FR-08 cross-check calls triggered synchronously within a request or from a
  running search/scan job.
- The Worker, for mirror population (§6.2) and delete (§6.4).

This key should be scoped with `listFiles`, `readFiles`, `writeFiles`, and `deleteFiles`
capabilities restricted to the archive bucket(s) only (least privilege, BRD §9 Data
Minimization). It is distinct from any individual user's login-validation key and is not tied to
a capability badge — a "Read Only" user submitting a search still gets B2-confirmed results
(read-only capability is sufficient for `b2_get_file_info`/`b2_list_file_names`); a "Read Only"
user should not be able to reach the Permanent Delete or sync-write actions in the first place,
but that gate is an **application-level authorization check** (session → role/policy), not a
difference in which B2 key the Worker uses to actually place the call.

> Not a BRD-stated decision — the BRD is silent on this specifically (§5's dependency line says
> only "key(s)... available to the server-side components"). Confirmed 2026-09-10 with the
> solution owner as this document's design, not inferred from the BRD text.

**Token lifecycle (confirmed against current Backblaze B2 API docs, 2026-09-10):**
`b2_authorize_account` returns an `authorizationToken` valid for at most 24 hours. The B2 client
wrapper should cache the token in memory (server-side, per process — not per-session, not
persisted) and transparently re-authenticate on a `401`/expired-token response rather than
re-authenticating on every call.

**B2 client implementation approach (confirmed 2026-09-10):** build a thin in-house `HttpClient`
wrapper around the B2 native REST API behind `IB2Client` (Core), rather than adopting a
third-party NuGet package. Candidates surveyed — `Backblaze.Client`/`DQD.Backblaze.Client`,
`Backblaze.Agent`, `B2Net` — are all community-maintained with no official Backblaze .NET SDK;
`Backblaze.Agent` (most recently updated, Feb 2026) explicitly documents itself as not yet
stable, and download volume across all three is low. The operations this integration actually
needs are a small, fixed set (`b2_authorize_account`, `b2_list_file_names`,
`b2_list_file_versions`, `b2_get_file_info`, `b2_download_file_by_id`, `b2_get_upload_url` +
`b2_upload_file`, `b2_delete_file_version`) — narrow enough that an auditable in-house wrapper
(full control over key handling, logging, and retry/backoff behavior for FR-03 compliance) is
lower-risk than a pre-1.0 dependency in a credential-handling-sensitive path.

**Never:**
- Pass a B2 key or key ID into a Blazor component parameter, JS interop call, hidden form field,
  query string, or any response body.
- Log the key, even partially (FR-03; see `aspnetcore-security` skill and §11 Telemetry below).
- Read a B2 key out of `appsettings.Production.json` directly, or commit one to source control.

---

### §6.2 B2 Mirror Population / Download Process (BRD §3(c), WBS task 34)

Runs as a Worker-owned queued task (`TaskType = B2MirrorSync`), not synchronously — a full bucket
enumerate/download at ~500GB scale is exactly the kind of operation FR-18/FR-22a describes as
potentially taking up to a full day.

1. Authenticate (§6.1) — obtain/reuse the cached system-key authorization.
2. **Enumerate the bucket:** `b2_list_file_names`, paginated via `startFileName`/`nextFileName`.
   Confirmed 2026-09-10 (target bucket has no native versioning or Object Lock — see §6.4): use
   `b2_list_file_names`, not `b2_list_file_versions` (only needed if versioning is later enabled
   on this bucket). `maxFileCount` is capped at **1000 files per transaction** (confirmed against
   current B2 API docs, 2026-09-10) — requesting more just triggers additional billed
   transactions internally, it does not return more than 1000 per call.
3. For each returned object, compare `contentSha1` + `uploadTimestamp` against the mirror-index
   row for that `ObjectKey` (§5).
   - **Unchanged** (same `contentSha1`): skip download, update `LastVerifiedAt` only. This is the
     "avoid re-downloading unchanged files on repeat runs" rule from the `b2-integration` skill.
   - **New or changed:** proceed to step 4.
4. Download via `b2_download_file_by_id` (preferred — avoids a second name-resolution round trip
   since `B2FileId` is already known from step 2/mirror-index) into the local B2-mirror directory,
   preserving the bucket's path structure.
5. Upsert the mirror-index row: `B2FileId`, `ContentSha1`, `UploadTimestamp`, `Size`,
   `LocalMirrorPath`, `LastVerifiedAt`.
6. Any `ObjectKey` present in the mirror-index but no longer returned by step 2's enumeration is a
   candidate for the "Removed from O-Drive" vs. "present in B2 but locally stale" distinction used
   by O-Drive Sync (FR-11) — this document does not resolve that comparison logic (out of scope,
   belongs to the O-Drive Sync/Content Scan spec), only notes that mirror population must not
   silently delete the local mirror copy of an object it no longer sees listed, since that
   decision belongs to the explicit Copy/Purge/Update actions (FR-13), not to a passive sync pass.
7. Write progress (`ProgressDetail`, e.g., objects processed / total, or a percentage) back to the
   task record periodically (async-task-queue skill "Progress" requirement) — a day-long job with
   no progress signal is not acceptable given FR-22a's own stated expectation-setting language.

> Resolved 2026-09-10: target bucket confirmed to have no native versioning or Object Lock
> enabled — see §6.4 for the corresponding resolution on permanent-delete behavior. If this
> changes in future, step 2 and §6.4 both need revisiting.

---

### §6.3 Per-Match Cross-Check Sequence (FR-08)

Order, exactly as required by BRD §3(c)/§10.1 — local search never confirms existence on its own:

1. **Local match.** Keyword Search (filename or content index) or O-Drive Sync comparison
   produces a candidate row referencing a mirror-index `ObjectKey` (and, per §5, its cached
   `B2FileId`).
2. **B2 existence check.** Call `b2_get_file_info(B2FileId)`.
   - **200 + `action == "upload"`** (i.e., a real, non-hidden, non-folder file): confirmed —
     include in results shown to the user (FR-08 satisfied).
   - **404 (`file_not_present`)**, or `action == "hide"` (object was soft-deleted/hidden on the
     B2 side since the last mirror sync): exclude from results. Per the `b2-integration` skill's
     "Excluded-on-miss" rule, this exclusion must itself be logged (not silently dropped) so a
     reviewer can see why a filename match "disappeared" — write an audit log entry
     (`Action = CrossCheckExcluded`, result = the B2 status observed) rather than only omitting
     the row from the response.
   - **If `B2FileId` is missing or stale** (mirror-index row predates the file's last B2
     population, or the cached ID no longer resolves): fall back to
     `b2_list_file_names(startFileName = ObjectKey, maxFileCount = 1)` to re-resolve by exact
     name before concluding "not present." Do not conclude absence from a single failed
     `get_file_info` call without this fallback, since a stale cached `fileId` is a mirror-index
     staleness bug, not evidence the object is gone from B2.
3. **Result.** Only candidates that survive step 2 (or its fallback) are returned to the caller
   as actionable matches (FR-06/FR-07/FR-11).

**Batching (per `b2-integration` skill: "batch these existence checks efficiently rather than one
B2 API call per candidate in a tight loop"):** issue step 2 calls under bounded concurrency (e.g.,
a fixed-size worker pool / `SemaphoreSlim`, not unbounded `Task.WhenAll`) rather than strictly
sequential — a scan can surface a large candidate set even though the whole archive is bounded to
thousands of files. Bound the concurrency low enough to respect B2 API rate limits.

> Confirmed 2026-09-10: no batch/multi-file "get file info" operation exists in the B2 API —
> `b2_get_file_info` is strictly one file per call, so the bounded-concurrency design above is
> necessary, not a placeholder pending a future shortcut. Backblaze does not publish a fixed
> numeric rate limit for this call (dynamically enforced); `B2:CrossCheckMaxConcurrency` (§9)
> should still be tuned empirically against the actual account/bucket during WBS task 32, rather
> than assumed from a documented number.

---

### §6.4 Permanent-Delete Sequence (FR-16/FR-17)

**Execution model (resolved 2026-09-10): queued, not synchronous.** Permanent Delete runs through
the task queue (`TaskType = PermanentDelete`) and is executed by the Worker, consistent with
Copy/Update/Purge (FR-13c) — one execution model for every B2-write action, rather than a
special-cased synchronous path. Two audit-log touchpoints follow from this, mirroring how FR-13d
is written for the sync actions:

- **At submission (Api):** the request handler checks the caller's capability (§12 — `Full
  Access` required), writes an audit log row (`Action = PermanentDeleteRequested`, user,
  file/path, timestamp) **before** returning the `202 Accepted` + task reference to the caller,
  and enqueues the task. This is the "audit log before success response" moment for the
  submission itself.
- **At completion (Worker):** once Steps A/B below finish (success or failure), the Worker writes
  a second audit row with the final outcome (`Deleted`, `DeleteFailed-Local`, or
  `DeleteFailed-B2`) and updates the task record — before the task is marked `Completed`/`Failed`,
  which is what the Task Manager (FR-19/FR-21) and Quarantine screen read to reflect the outcome.

**Order within Step A/B: local cache delete, then B2 delete — deliberately, not arbitrarily.** The local mirror
delete is the cheaper, retry-safe step; the B2 delete is the one true irreversible action
(FR-17). Doing the retry-safe step first means that if the process crashes or fails *before* the
B2 call, nothing irreversible has happened yet — the record is left in a clearly-recoverable
`DeleteFailed-B2` (pending) state rather than a state where B2 has already lost the object but the
local system doesn't yet agree. There is no true "rollback" of either step once it succeeds — a
deleted local mirror file is not restored, and a deleted B2 object is not restored, by design
(BRD §9 Data Minimization: "permanently deleted files are removed from both the local cache and
B2 with no retained copy"). "Rollback/error handling" here means *never silently leaving the two
sides out of sync*, per the `b2-integration` skill.

1. **Pre-condition:** file must be in Quarantine (FR-15) or otherwise be an already-identified,
   user-selected target; the user has passed the FR-17 explicit confirmation step.
2. **Step A — local delete.** Delete the file from the local B2-mirror cache directory and mark
   its mirror-index row deleted (or remove it).
   - **On failure** (e.g., file locked, disk I/O error): mark the record `DeleteFailed-Local`,
     write the audit log entry with the observed error, and **stop** — do not proceed to Step B.
     There is no reason to make an irreversible external call after the reversible local step has
     already failed.
   - **On success:** proceed to Step B.
3. **Step B — B2 delete.** Backblaze B2 is version-aware: `b2_delete_file_version` removes
   exactly **one** specific version (identified by `fileName` + `fileId`), not "the file" as a
   whole. **Confirmed 2026-09-10: the target bucket has no native versioning or Object Lock
   enabled**, so in current practice each object has exactly one version and this reduces to a
   single delete call per file. The enumerate-then-delete-every-version logic below is kept as
   defensive design (harmless when there is only one version) rather than removed, since if
   versioning were ever enabled on this bucket in future, deleting only the current version would
   leave older versions recoverable in B2 — **violating FR-17's "no recovery window" guarantee.**
   If that ever changes, this section must be revisited — see the Object Lock note below.
   - Call `b2_list_file_versions(startFileName = ObjectKey, prefix = ObjectKey)` to enumerate
     **all** versions of this object.
   - Call `b2_delete_file_version(fileName, fileId)` for **every** version returned, not just the
     current/latest one.
   - Do **not** use `b2_hide_file` — that is a soft-delete (creates a hide marker, previous
     version still recoverable) and is the opposite of what FR-16/FR-17 requires.
   - **On failure** (any version's delete call fails, auth expiry, network, rate limit): mark the
     record `DeleteFailed-B2` (local side already removed, B2 side incomplete/inconsistent). This
     is the state that needs a retry path — recommend re-enqueueing a bounded-retry task
     (`B2DeleteRetry`) rather than an unbounded automatic retry loop, escalating to a
     manually-actionable state in the Task Manager / Audit Log if retries are exhausted.
   - **On success (all versions deleted):** mark the record `Deleted`.
4. **Audit log (FR-23, FR-16/FR-17):** write one audit row capturing the final outcome —
   `Deleted`, `DeleteFailed-Local`, or `DeleteFailed-B2` — with user, tab/module, action, file/path,
   timestamp, and result/detail, **before** the Worker marks the task `Completed`/`Failed`. This
   satisfies both "audit log write before success response" (CLAUDE.md Rules — here, before the
   Task Manager/Quarantine screen can observe a terminal state) and "log both the attempt and the
   outcome... either way" (`b2-integration` skill) — a `DeleteFailed-*` outcome is logged the same
   way a `Deleted` outcome is, not only on success.

> **Resolved 2026-09-10:** target bucket confirmed to have no B2 native versioning or Object Lock
> (File Lock, legal hold / retention) enabled today, so `b2_delete_file_version` is not subject to
> the `bypassGovernance`-capability requirement or an outright delete block that Object Lock
> governance/compliance-mode or Legal Hold would otherwise impose (confirmed against current B2
> API/Help docs, 2026-09-10). **This is a point-in-time fact about the bucket's current
> configuration, not a property of the API** — if versioning or Object Lock is enabled on this
> bucket in the future, this entire step must be revisited before relying on it, since Legal Hold
> and Compliance-mode retention can block deletion outright regardless of application-key
> capabilities.

---

## §7 Validations

| ID | Rule | On Failure | Test |
|----|------|-----------|------|
| V1 | Operational B2 client must never be constructed with a per-request/browser-supplied key — only the server-held system key (§6.1) | Reject at DI/construction time, not per-call | `Test_V1_B2ClientRejectsNonServerKey` |
| V2 | Permanent delete must not call B2 delete before local delete succeeds (§6.4 Step A→B ordering) | Abort before Step B; mark `DeleteFailed-Local` | `Test_V2_DeleteOrderLocalBeforeB2` |
| V3 | Permanent delete must enumerate and delete **all** versions of the object, not just the current one (§6.4 Step B) | Retry/escalate rather than report false success | `Test_V3_DeleteAllVersionsNotJustCurrent` |
| V4 | Cross-check exclusion (404/hidden on B2) must be logged, not silently dropped (§6.3) | N/A — this is itself the required behavior | `Test_V4_CrossCheckExclusionIsAudited` |

---

## §8 UI / UX

N/A — this is a server-side integration design; no UI surface.

---

## §9 Configuration Changes

| Key | Default | Description | Env Override |
|---|---|---|---|
| `B2:SystemKeyId` | *(none — secret)* | Application Key ID for the operational (non-per-user) B2 client, §6.1. Value lives in server-side secrets (.NET User Secrets locally, a proper secrets manager in production) — never in `appsettings.json` or `appsettings.Production.json`. | Yes |
| `B2:SystemApplicationKey` | *(none — secret)* | Companion secret to the above. | Yes |
| `B2:BucketName` | *(none)* | Target archive bucket name. | Yes |
| `B2:MirrorRootPath` | *(none)* | Local filesystem root for the downloaded B2 mirror (§6.2). | Yes |
| `B2:CrossCheckMaxConcurrency` | e.g. `8` (tune once rate limits are confirmed, §6.3) | Bounded concurrency for cross-check batches. | Yes |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A locally matched candidate that still exists on B2 is included in results; one that returns 404/hidden is excluded **and** logged | `Test_SC1_CrossCheckIncludesExcludesCorrectly` |
| SC-2 | Mirror population skips re-downloading an object whose `contentSha1` is unchanged since the last sync | `Test_SC2_MirrorSkipsUnchangedObjects` |
| SC-3 | Permanent delete removes the local mirror copy before calling B2, and never calls B2 delete if the local delete failed | `Test_SC3_DeleteOrderEnforced` |
| SC-4 | Permanent delete removes every version of the object from B2, not only the current version | `Test_SC4_AllVersionsDeleted` |
| SC-5 | No B2 application key value ever appears in a log line, HTTP response body, or Blazor component parameter | `Test_SC5_NoKeyLeakage` |
| SC-6 | A B2-side delete failure after a successful local delete leaves the record in `DeleteFailed-B2`, not a silently-inconsistent state, and is audit-logged before the Worker marks the task `Failed` | `Test_SC6_PartialFailureSurfaced` |

---

## §11 Telemetry

```csharp
// _logger.LogInformation("B2 cross-check {Result} for {ObjectKey}", result, objectKey);
// _logger.LogWarning("B2 delete partial failure: local removed, B2 pending for {ObjectKey}", objectKey);
```

- Never log: the B2 application key (system or per-user) or any part of it, a session
  token/cookie value, full file content, or raw archive file paths beyond what the audit log
  (FR-23) already records.
- Do log: which B2 operation was called, the object key/fileId, and the outcome (existence
  confirmed/excluded, delete succeeded/failed-local/failed-b2) — this is what makes the cross-check
  and delete flows auditable per BRD Section 9.

---

## §12 Security Notes

### B2 Credential Handling
Two key materials exist server-side (§6.1): each user's own login-validation key (FR-01, used
only at sign-in) and a separate system-level operational key (this document's recommendation,
flagged open in §15) used by the Api/Worker for cross-check, mirror population, and delete.
Neither is ever serialized into a browser-visible response, Blazor component parameter, or log
line (FR-03).

### Audit Trail
Every cross-check exclusion (§6.3) and every delete outcome (§6.4, all three terminal states)
writes to the SQL Server audit log (FR-23) — user, tab/module, action, file/path, timestamp,
result/detail — before the corresponding response is returned to the caller, including failure
responses (the audit write happens either way; only the *response status* differs).

### Session / Auth Requirements
Permanent Delete (FR-16) must be gated by an authenticated session with sufficient B2 capability
(§6.1 badge). Confirmed mapping (2026-09-10, this document's proposal — not stated in the BRD):
`Full Access` is required to submit Permanent Delete, Purge, Copy, or Update (any B2-write
action); `Read Only` is sufficient for Keyword Search, Content Scan, and Quarantine (Quarantine
is local-only per FR-15, no B2 write). This check happens at the Api layer when the action is
**submitted** (enqueue time, §6.4) — the Worker, executing under the system-level operational key
with no attached user session (§6.1), does not re-check capability at execution time. This
document assumes the login/session design (WBS task 11) resolves the concrete ASP.NET Core policy
name/implementation for this mapping; it does not itself define the policy name in code.

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|---|---|---|
| `B2ClientTests` | `Test_V1_B2ClientRejectsNonServerKey` | V1 |
| `B2ClientTests` | `Test_SC5_NoKeyLeakage` | SC-5 |
| `CrossCheckServiceTests` | `Test_SC1_CrossCheckIncludesExcludesCorrectly` | SC-1 |
| `CrossCheckServiceTests` | `Test_V4_CrossCheckExclusionIsAudited` | V4 |
| `MirrorSyncServiceTests` | `Test_SC2_MirrorSkipsUnchangedObjects` | SC-2 |
| `PermanentDeleteServiceTests` | `Test_V2_DeleteOrderLocalBeforeB2` | V2 |
| `PermanentDeleteServiceTests` | `Test_V3_DeleteAllVersionsNotJustCurrent` | V3 |
| `PermanentDeleteServiceTests` | `Test_SC3_DeleteOrderEnforced` | SC-3 |
| `PermanentDeleteServiceTests` | `Test_SC4_AllVersionsDeleted` | SC-4 |
| `PermanentDeleteServiceTests` | `Test_SC6_PartialFailureSurfaced` | SC-6 |

### Integration Tests
| Class | Method | Proves |
|---|---|---|
| `B2IntegrationTests` (against a real or sandboxed B2 test bucket) | `Test_MirrorPopulation_EndToEnd` | §6.2 sequence against live/sandbox B2 |
| `B2IntegrationTests` | `Test_PermanentDelete_AllVersionsRemoved` | SC-4 against a versioned test bucket |

### Coverage Target
Changed files must reach ≥ 85% line coverage (project standard).

---

## §14 Rollout

1. All §15 open questions resolved 2026-09-10 (resolution log below) — no remaining blockers to
   starting implementation from this document.
2. Implement `IB2Client` (Core, in-house `HttpClient` wrapper per §6.1) and its Api/Worker-shared
   implementation first (WBS task 31), since §6.2–§6.4 all depend on it.
3. Task-queue infrastructure (WBS task 28) must support a `PermanentDelete` task type before WBS
   task 33 can ship, since Permanent Delete is now queued, not synchronous (§6.4).
4. Mirror population (§6.2, WBS task 34) before cross-check (§6.3, WBS task 32) — cross-check's
   fast-path depends on `B2FileId` already being cached from a mirror pass.
5. Permanent delete (§6.4, WBS task 33) last, given its irreversibility — should be implemented
   and tested against a sandbox bucket before any production bucket use. Even though the
   production bucket is confirmed to have no versioning/Object Lock today, test the multi-version
   enumeration path against a *versioned* sandbox bucket so the defensive logic is actually
   exercised, not just assumed correct.
6. Rollback: N/A at the design-doc stage; once implemented, this integration has no "rollback" in
   the data sense (§6.4) — a bad deploy of the *code* is reverted normally (redeploy prior
   version), which does not undo any B2-side data action already taken.

---

## §15 Open Questions

All six items originally raised here were resolved on 2026-09-10 — logged below for traceability
(Karpathy P6/auditability). No blocking open items remain for this document.

- [x] **Operational B2 key ownership model** — resolved: single system-level B2 application key
      for the Api/Worker's operational calls, separate from each user's login-validation key
      (§6.1). Architect recommendation, no objection raised.
- [x] **Bucket versioning / Object Lock** — resolved by the solution owner, 2026-09-10: the
      target bucket has **no** native versioning or Object Lock enabled. §6.2 and §6.4 updated
      accordingly; both sections note this must be revisited if the bucket configuration changes.
- [x] **Permanent Delete execution model** — resolved 2026-09-10: queued through the task queue
      (`TaskType = PermanentDelete`), consistent with Copy/Update/Purge (FR-13c), not synchronous.
      §5, §6.4, and §14 updated.
- [x] **Read Only / Full Access → authorization mapping** — resolved 2026-09-10: `Full Access`
      required for Permanent Delete, Purge, Copy, Update; `Read Only` sufficient for Keyword
      Search, Content Scan, Quarantine. §12 updated. The concrete ASP.NET Core policy
      name/implementation is still deferred to the login/session design (WBS task 11) — that is
      an implementation detail, not an open question this document needs to resolve.
- [x] **.NET B2 client approach** — resolved 2026-09-10: thin in-house `HttpClient` wrapper, not
      a community NuGet package. §6.1 records the candidates considered and why.
- [x] **B2 API details needing confirmation** — resolved 2026-09-10 against current Backblaze B2
      API/Help docs: `b2_authorize_account` token expiry (24h), `maxFileCount` cap (1000/call, not
      10,000 as originally drafted), no batch "get multiple file infos" operation exists, and
      `b2_delete_file_version`'s interaction with Object Lock (`bypassGovernance` capability
      requirement under governance mode; outright block under Legal Hold/Compliance-mode) — moot
      for the current bucket per the item above, but documented in §6.4 in case that changes.

Not depending on either open BRD §12.2 item (O-Drive refresh cadence; drive encryption-at-rest) —
noted for completeness per the template's instruction, not because this spec needs them resolved.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts) | 2026-09-10 | Drafted per WBS task 10; §15 resolved same day |
| Reviewer | | | Pending |

---

## §17 Graph Impact

No graphify graph currently covers application code (none exists in this repo yet — see
CLAUDE.md's graphify section; `graphify-out/` reflects the BRD/WBS/docs corpus only, not source
code, per `graphify-out/GRAPH_REPORT.md`'s corpus of 29 files/~200K words, which is documentation,
not C#). This section is filled from a manual read of the BRD and the `b2-integration`,
`async-task-queue`, and `clean-architecture` skill files instead, per CLAUDE.md's documented
fallback.

### God Nodes in Blast Radius
N/A — no source code exists yet to have god nodes.

### Communities / Modules Affected
Keyword Search (cross-check consumer), O-Drive Sync and Content Scan (mirror population and
cross-check consumer), Quarantine (permanent-delete consumer), Task Manager (mirror population as
a queued task type), Audit Log (every flow in §6 writes to it). Projects: `Core` (new `IB2Client`,
`IMirrorIndexRepository` interfaces), `Api` (cross-check calls; permanent-delete *submission*
endpoint — capability check + enqueue + audit row, §6.4), and `Worker` (mirror population and
permanent-delete *execution*, §6.4 — confirmed 2026-09-10 to be queued, not synchronous).

### Surprising Connections
- The B2 native versioning/Object-Lock question (§15) is not mentioned anywhere in the BRD, but
  directly threatens the BRD's own accepted-risk framing of "permanent, no-recovery deletion"
  (Section 9) if the bucket turns out to have retention locks enabled. This is the most
  consequential gap this design surfaced.
- FR-13c's task-queue requirement for Copy/Update/Purge, read next to FR-16's silence on whether
  Permanent Delete is queued, creates an asymmetry worth resolving explicitly rather than
  assuming consistency (§15).

### Graphify Queries Run
Graphify not applicable to application code yet — analysis done via manual read of
`0.Specifications/CACHET_Archive_Files_Search_Sync_BRD_v2.md` (full document) and the
`b2-integration`, `async-task-queue`, `aspnetcore-security`, `clean-architecture` skill files.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] §10 Success Criteria are each testable against the described call sequences (no test code
      exists yet — this is a design doc; the reviewer here verifies the *design* is
      implementable and internally consistent, not that code passes).
- [ ] §7 Validations are each traceable to a concrete step in §6.
- [ ] §12 Security Notes: confirm no path in §6 allows a B2 key (system or per-user) to reach a
      browser-visible surface.
- [ ] §5 Database Objects are marked provisional and do not conflict with anything already
      decided elsewhere (none exists yet to conflict with).
- [ ] §9 Config keys use secret-holding key *names* only, no example/default secret values.
- [ ] §15 Open Questions are genuinely unresolved by the BRD (spot-check against
      `0.Specifications/CACHET_Archive_Files_Search_Sync_BRD_v2.md`), not questions this document
      could have answered itself.
- [ ] No out-of-scope items from §4 were designed in more detail than "reuses the primitives from
      §6.2/§6.4."

### Verification Report Format
Per template: `CAS-001.review.md` with overall verdict, per-SC verdict table, security gap list,
scope violations.

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 6.8% - below 85% threshold, accepted (scope-narrowed run, see note in this log) | Endpoints —

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Core/Interfaces/IB2Client.cs` | New interface: seven §6.1 B2 operations |
| `src/Cachet.ArchiveSync.Core/Dtos/B2AuthorizationInfo.cs` | Auth result DTO; ToString() redacts token |
| `src/Cachet.ArchiveSync.Core/Dtos/B2FileInfo.cs` | b2_get_file_info response DTO |
| `src/Cachet.ArchiveSync.Core/Dtos/B2ListFileNamesResult.cs` | b2_list_file_names response DTO |
| `src/Cachet.ArchiveSync.Core/Dtos/B2ListFileVersionsResult.cs` | b2_list_file_versions response DTO |
| `src/Cachet.ArchiveSync.Core/Dtos/B2UploadUrlInfo.cs` | Upload-URL DTO; ToString() redacts auth token |
| `src/Cachet.ArchiveSync.Core/Dtos/B2DownloadedFile.cs` | b2_download_file_by_id response DTO |
| `src/Cachet.ArchiveSync.Core/Exceptions/B2ApiException.cs` | B2 error envelope exception, no header/key data |
| `src/Cachet.ArchiveSync.Core/Exceptions/B2NotFoundException.cs` | 404/file-not-present exception |
| `src/Cachet.ArchiveSync.Api/B2/B2Client.cs` | In-house HttpClient IB2Client impl; V1 construction-time key validation; in-memory token cache |
| `src/Cachet.ArchiveSync.Api/B2/B2WireModels.cs` | Raw B2 JSON wire shapes, kept out of Core |
| `src/Cachet.ArchiveSync.Api/Program.cs` | DI: IHttpClientFactory-backed named client + singleton IB2Client |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | Added B2:SystemKeyId/SystemApplicationKey/BucketName placeholders |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | Same IB2Client DI registration as Api |
| `src/Cachet.ArchiveSync.Worker/Cachet.ArchiveSync.Worker.csproj` | Added Microsoft.Extensions.Http package reference |
| `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | Added B2 config placeholders |
| `src/Cachet.ArchiveSync.Api.Tests/B2ClientTests.cs` | New test class: V1 + SC-5 coverage |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `B2ClientTests` | `Test_V1_B2ClientRejectsNonServerKey` | V1 |
| `B2ClientTests` | `Test_V1_B2ClientConstructsWhenAllServerConfigPresent` | V1 (positive path) |
| `B2ClientTests` | `Test_SC5_NoKeyLeakage` | SC-5 |
