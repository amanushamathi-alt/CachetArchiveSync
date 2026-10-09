---
spec_id: CAS-001
reviewer: independent-agent
reviewed_at: 2026-09-22 00:00 UTC
verdict: APPROVED WITH NOTES
spec_type: feature
---

# Review Report — CAS-001 (B2 Integration Design — §6.1 Client Authentication/Credential Brokering)

## Verdict: APPROVED WITH NOTES

This spec is a design document whose §0 Dependency Note and §4 Out-of-Scope explicitly defer
§6.2 (mirror population), §6.3 (cross-check), and §6.4 (permanent delete) to follow-on specs —
CAS-003 (DB schema) exists, but the API-design spec (WBS task 7) still does not. The
Implementation Log confirms this `/implement` run narrowed the build to §6.1 only. Judged against
that legitimately-implementable scope: the two in-scope items (V1, SC-5) both pass, verified
independently by re-running the tests, not by trusting the Implementation Log's claim. No security
gaps and no scope violations were found. The verdict is APPROVED WITH NOTES rather than plain
APPROVED because of an honestly-disclosed coverage shortfall (6.8% vs. the 85% project standard)
that the reviewer confirms is real and tied to the scope narrowing, not an oversight — see §7/§10
below.

---

## 1. Success Criteria Verification

| SC | Description | Status | Evidence |
|----|-------------|--------|---------|
| SC-1 | Cross-check includes/excludes correctly, exclusion logged | NOT-YET-IN-SCOPE | No `CrossCheckServiceTests` class or `CrossCheckService` exists anywhere under `src/` (grep confirmed). §6.3 was not built in this run — consistent with §0/§4. |
| SC-2 | Mirror population skips unchanged objects | NOT-YET-IN-SCOPE | No `MirrorSyncServiceTests`/`MirrorSyncService` exists. §6.2 was not built — consistent with §0/§4. |
| SC-3 | Permanent delete: local before B2, never calls B2 after local failure | NOT-YET-IN-SCOPE | No `PermanentDeleteServiceTests`/`PermanentDeleteService` exists. §6.4 was not built. |
| SC-4 | Permanent delete removes every version, not just current | NOT-YET-IN-SCOPE | Same as SC-3 — the delete flow does not exist yet; only the underlying `IB2Client.ListFileVersionsAsync`/`DeleteFileVersionAsync` primitives exist, unused by any caller. |
| SC-5 | No B2 key value ever appears in a log line, HTTP response, or Blazor parameter | **PASS** | `B2ClientTests.Test_SC5_NoKeyLeakage` (`src/Cachet.ArchiveSync.Api.Tests/B2ClientTests.cs:156-202`) exercises a real `AuthorizeAsync` round trip against a stub transport and asserts the raw token never appears in any captured `ILogger` message. `B2Client.AuthorizeCoreAsync` (`B2Client.cs:107-109`) logs only `AccountId` and `Capabilities`, never `AuthorizationToken`. `B2AuthorizationInfo.ToString()` (`B2AuthorizationInfo.cs:33-36`) and `B2UploadUrlInfo.ToString()` (`B2UploadUrlInfo.cs:19-21`) both redact their token fields — both asserted directly in the test. No Blazor project (`Cachet.ArchiveSync.Web`) references `IB2Client`, `B2Client`, or any B2 DTO (grep confirmed no matches outside Core/Api/Worker). Re-ran the test independently: 9/9 pass in `B2ClientTests` (`dotnet test` output: `Passed! - Failed: 0, Passed: 9, Skipped: 0, Total: 9`). |
| SC-6 | B2-delete failure after successful local delete leaves `DeleteFailed-B2`, audit-logged before task marked `Failed` | NOT-YET-IN-SCOPE | Depends entirely on the (not-yet-built) `PermanentDeleteService` and task-queue wiring. |

### SC Failures / Gaps
None. All in-scope SCs pass; all out-of-scope SCs are correctly and explicitly not built, matching §0/§4's own framing rather than being silently skipped.

---

## 2. Validation Coverage

| V | Rule | Implemented | Tested | Notes |
|---|------|-------------|--------|-------|
| V1 | Operational B2 client must never be constructed with a per-request/browser-supplied key | **Yes** | **Yes** | `B2Client` constructor (`B2Client.cs:50-58`) reads exclusively from `IConfiguration` via `RequireConfig` (`B2Client.cs:60-72`), which throws `InvalidOperationException` if `B2:SystemKeyId`/`B2:SystemApplicationKey`/`B2:BucketName` is missing/blank. There is no constructor overload or parameter through which a caller-supplied key could reach this class — rejection is structural (by construction), not merely a runtime check, exactly as §7 requires ("Reject at DI/construction time, not per-call"). `Test_V1_B2ClientRejectsNonServerKey` (7 `[InlineData]` cases covering each of the three keys null/empty/whitespace) plus `Test_V1_B2ClientConstructsWhenAllServerConfigPresent` (positive path) both pass. |
| V2 | Permanent delete must not call B2 delete before local delete succeeds | Not-Applicable | Not-Applicable | No permanent-delete flow exists yet (§6.4 out of scope for this run) — correctly not implemented or tested. |
| V3 | Permanent delete must enumerate and delete all versions | Not-Applicable | Not-Applicable | Same — `IB2Client.ListFileVersionsAsync`/`DeleteFileVersionAsync` exist as primitives on the interface (§6.1's named-operations list) but no caller enumerates/deletes-all yet. |
| V4 | Cross-check exclusion must be logged, not silently dropped | Not-Applicable | Not-Applicable | No cross-check flow exists yet (§6.3 out of scope). |

---

## 3. API Contract Verification

§6's frontmatter declares `touches.api: false` — §6 is call sequences, not concrete
`VERB /api/v1/...` routes (§0, §4 explicitly defer route/controller contracts to a future
API-design spec). Confirmed no new controller or endpoint was added: `src/Cachet.ArchiveSync.Api/Controllers/`
contains only the pre-existing `WeatherForecastController.cs` scaffold (predates this spec, part
of the project template, unrelated to B2). No B2-specific endpoint exists. This is correct — an
endpoint here would have been a scope violation given the spec's own deferral.

| Endpoint | Verb ✓ | Route ✓ | Auth ✓ | Status Codes ✓ | Notes |
|----------|--------|---------|--------|----------------|-------|
| N/A | — | — | — | — | No endpoints declared or expected for this spec at this scope. |

---

## 4. Security Verification

| Note (§12) | Implemented | Evidence | Gap |
|------|-------------|---------|-----|
| B2 key never serialized into a browser-visible response, Blazor parameter, or log line | Yes | `B2Client.cs:107-109` logs only `AccountId`/`Capabilities`; `B2AuthorizationInfo`/`B2UploadUrlInfo` `ToString()` redact tokens; `Cachet.ArchiveSync.Web` has zero references to any B2 type (grep confirmed) | None |
| Operational client constructed only from server-held config (V1) | Yes | `B2Client.cs:50-72` — see §2 above | None |
| Never read a key from `appsettings.Production.json` or commit one to source control | Yes | No `appsettings.Production.json` exists anywhere in the repo yet (glob confirmed) — CLAUDE.md's rule against editing it was therefore not applicable and not violated. Both `appsettings.Development.json` files (Api and Worker) contain only empty-string placeholders for `B2:SystemKeyId`/`SystemApplicationKey`/`BucketName`, with an explicit inline `"// NOTE"` warning against putting real secrets there. No secret value is committed. | None |
| Audit trail (mirror/cross-check/delete outcomes) written before response | Not-Applicable | The flows that would write these audit rows (§6.2-§6.4) do not exist yet — correctly not built | Not a gap; flagged as not-yet-applicable, consistent with §0/§4 |

### Critical Security Findings
None.

---

## 5. Scope Check

### Files Modified (cross-checked against the Implementation Log's own "Files Changed" table and independently verified on disk)

| File | Expected (§6.1) | Notes |
|------|--------------|-------|
| `Core/Interfaces/IB2Client.cs` | Yes | Matches §6.1's seven named operations exactly |
| `Core/Dtos/B2AuthorizationInfo.cs`, `B2FileInfo.cs`, `B2ListFileNamesResult.cs`, `B2ListFileVersionsResult.cs`, `B2UploadUrlInfo.cs`, `B2DownloadedFile.cs` | Yes | DTOs for §6.1's operations; no secret leakage (verified — see §4) |
| `Core/Exceptions/B2ApiException.cs`, `B2NotFoundException.cs` | Yes | Built only from B2's error envelope, never from headers — cannot carry a key |
| `Api/B2/B2Client.cs`, `B2WireModels.cs` | Yes | In-house `HttpClient` wrapper per §6.1's confirmed design; wire models kept out of Core |
| `Api/Program.cs`, `Worker/Program.cs` | Yes | DI registration only |
| `Api/appsettings.Development.json`, `Worker/appsettings.Development.json` | Yes | Empty placeholders only |
| `Worker/Cachet.ArchiveSync.Worker.csproj` | Yes | Added `Microsoft.Extensions.Http` (needed because Worker's SDK is `Microsoft.NET.Sdk.Worker`, not `Sdk.Web`) |
| `Api.Tests/B2ClientTests.cs` | Yes | V1 + SC-5 coverage |

No file outside this list was found modified. Specifically verified absent: any
`CrossCheckService`/`MirrorSyncService`/`PermanentDeleteService` (§6.2/§6.3/§6.4 caller code), any
new controller, any `Database/` DDL or migration for CAS-001 (`Database/migrations/` contains only
the pre-existing CAS-003 and CAS-005 migrations), and any change to `Worker.cs`'s `ExecuteAsync`
(still the unmodified scaffold — the Worker does not yet call `IB2Client` for anything).

### Scope Violations
None.

**One design-level item worth recording, not a violation:** `Worker.csproj` carries a direct
`ProjectReference` to `Cachet.ArchiveSync.Api.csproj` so it can reuse `Cachet.ArchiveSync.Api.B2.B2Client`
(an Api-project class, not a Core interface implementation living in Core). This is a Worker → Api
dependency, which looks backwards at first glance against a naive "Worker only depends on Core"
reading. It is, however, exactly what this project's own `clean-architecture` skill prescribes:
*"Worker references Core and the same EF Core/B2-client implementations as Api (either via a
shared Infrastructure project if the pattern repeats, or by direct project reference — don't
invent a plugin/abstraction layer for a single worker, per P4)."* The same pattern was already
established by CAS-005's `IAuditLogRepository`/`IAuditLogService` registrations before this spec.
Not a scope violation; flagged in Reviewer Notes (§10) as something to watch if a second
genuinely-different implementation-sharing need appears, per the skill's own conditional language.

---

## 6. Graph Impact Verification

No graphify graph covers application code. `graphify-out/GRAPH_REPORT.md` was independently read
and confirms a corpus of 34 files / ~220,393 words dated 2026-09-10 — this is the BRD/WBS/spec
documentation corpus, predating any application code in this repo, not a code graph. §17's own
claim that no code graph exists is therefore correct and independently confirmed, not taken on
faith. Per the CLAUDE.md fallback and the review brief's Phase 2 instructions, verification was
done by grepping the codebase directly for `IB2Client`/`B2Client` and its callers/DI
registrations.

### Graphify Query Run by Reviewer
Graphify not applicable to application code (confirmed above). Used instead:
```
grep -r "IB2Client|B2Client" src --include=*.cs
grep -r "CrossCheck|MirrorSync|PermanentDelete" src --include=*.cs
```

### Predicted vs Actual
| Predicted (§17) | Found in Code | Match? |
|-----------------|--------------|--------|
| `Core`: new `IB2Client`, `IMirrorIndexRepository` interfaces | `IB2Client` exists (`Core/Interfaces/IB2Client.cs`); `IMirrorIndexRepository` does **not** exist yet | Partial — expected, since the mirror-index repository is a §6.2 consumer, correctly deferred |
| `Api`: cross-check calls; permanent-delete submission endpoint | Neither exists yet; only `B2Client` (the client implementation) and DI registration exist | Partial — expected, §6.3/§6.4 deferred |
| `Worker`: mirror population and permanent-delete execution | Neither exists; only DI registration for `IB2Client` (unused by `Worker.cs`'s `ExecuteAsync`) | Partial — expected, §6.2/§6.4 deferred |
| Communities: Keyword Search, O-Drive Sync, Quarantine, Task Manager, Audit Log all "affected" | None of these modules exist in code yet, and none reference `IB2Client` — the prediction describes the design's eventual blast radius once §6.2-§6.4 land, not this run's actual diff | Expected divergence, not a discrepancy — §17 predicted the full design's footprint, not this narrowed implementation's footprint |

### Unintended Side Effects
None found. The Worker → Api project reference (§5 above) is the only cross-project connection
this run introduced beyond what §17 named, and it is explainable by the `clean-architecture` skill
rather than being a surprise.

---

## 7. Test Coverage

| Test Class | Tests Found | SC / V Covered | Coverage % (if available) |
|------------|------------|----------------|--------------------------|
| `B2ClientTests` | 9 (`Test_V1_B2ClientRejectsNonServerKey` ×7 inline cases, `Test_V1_B2ClientConstructsWhenAllServerConfigPresent`, `Test_SC5_NoKeyLeakage`) | V1, SC-5 | 6.8% overall (per Implementation Log); independently re-ran — all 9 pass |

Independently re-ran `dotnet test` against `Cachet.ArchiveSync.Api.Tests` filtered to `B2ClientTests`:
`Passed! - Failed: 0, Passed: 9, Skipped: 0, Total: 9`. Also ran a full solution `dotnet build` —
0 warnings, 0 errors, all 8 projects (including `Web`, `Worker`, and the three other test projects)
build clean.

### Missing Tests
`ListFileNamesAsync`, `ListFileVersionsAsync`, `GetFileInfoAsync`, `DownloadFileByIdAsync`,
`GetUploadUrlAsync`, `UploadFileAsync`, and `DeleteFileVersionAsync` on `B2Client` are implemented
but have no unit test coverage — this is the source of the 6.8% figure against the project's 85%
standard. The test file's own trailing comment (`B2ClientTests.cs:204-213`) discloses this
explicitly and correctly attributes it to `B2Client` taking a concrete `HttpClient` with no
injectable `HttpMessageHandler` seam beyond the one ad-hoc stub built for the SC-5 test, and to
these seven operations being callers-less until §6.2/§6.3/§6.4 land. This reviewer confirms the
explanation is accurate (no such seam exists) and that none of these methods is currently called by
any in-scope flow — so the gap does not currently mask an untested behavioral requirement (no SC
or V in §10/§7 references these methods directly; they are only reachable through the deferred
services). Recommendation, not a blocker: when §6.2/§6.3/§6.4 are implemented, either introduce an
`IHttpClientFactory`-friendly test seam (e.g. inject `HttpMessageHandler` via
`AddHttpClient(...).ConfigurePrimaryHttpMessageHandler`) or add integration tests per §13's
`B2IntegrationTests` plan, so these methods get real coverage rather than being exercised for the
first time by their first caller.

---

## 8. Database Verification

Spec `touches.database: true` (field-level requirements only, §5, no DDL owned by this doc). No DB
object was added or modified by this implementation run, which is correct — §5 is explicitly
provisional and the reconciliation against `CAS-003`'s actual `dbo.file_index`/`dbo.task_queue`
schema remains outstanding per §0's own note. This run did not attempt that reconciliation, which
is consistent with §6.1-only scope (the reconciliation is a §6.2/mirror-index concern).

| DB Object | Exists in Schema | Migration Present | Idempotent |
|-----------|-----------------|------------------|------------|
| Mirror-index fields (§5) | No (provisional, not yet reconciled against CAS-003) | No — correctly not attempted this run | N/A |
| Task-queue fields (§5) | Partially — CAS-003's `dbo.task_queue` exists but field-by-field reconciliation against §5's provisional list has not been done | No | N/A |

---

## 9. Configuration Verification

Spec `touches.config: true`. Only the three §9 keys needed by §6.1 were added; `B2:MirrorRootPath`
and `B2:CrossCheckMaxConcurrency` were correctly not added since they belong to §6.2/§6.3, not yet
built.

| Key | Present in appsettings | Default Correct | Env Override Works |
|-----|----------------------|----------------|-------------------|
| `B2:SystemKeyId` | Yes (Api + Worker `appsettings.Development.json`, empty placeholder) | Yes — no default value, secret-only per §9 | Yes — read via `IConfiguration` indexer, standard ASP.NET Core config layering applies |
| `B2:SystemApplicationKey` | Yes | Yes | Yes |
| `B2:BucketName` | Yes | Yes (empty placeholder, not a real bucket name) | Yes |
| `B2:MirrorRootPath` | Not present | N/A — correctly deferred to §6.2 | N/A |
| `B2:CrossCheckMaxConcurrency` | Not present | N/A — correctly deferred to §6.3 | N/A |

---

## 10. Reviewer Notes

- The Worker → Api direct `ProjectReference` (see §5) is intentional and matches the project's own
  `clean-architecture` skill guidance and the precedent already set by CAS-005. Flagging only so a
  future reviewer isn't surprised by it, and as a reminder that the skill's own text anticipates
  revisiting this ("via a shared Infrastructure project if the pattern repeats") if a third
  Worker-side implementation-sharing need appears.
- `B2:CrossCheckMaxConcurrency`'s default of "tune once rate limits are confirmed" (§9) is not
  applicable yet since no consumer of that config key exists in code — no action needed now, just
  noting it for the reviewer of the §6.3 follow-on spec.
- The Implementation Log's own coverage disclosure (6.8%, accepted, scope-narrowed run) is accurate
  and was independently verified rather than taken at face value — see §7.
- Consider, in the §6.2/§6.3/§6.4 follow-on specs, deciding up front whether `B2Client` needs an
  `HttpMessageHandler` test seam before those services are built, rather than after, since all
  three depend on `B2Client`'s currently-untested wire-call methods.

---

## 11. Required Changes Before Merge

None. Verdict is APPROVED WITH NOTES — the notes above (test-seam recommendation, coverage gap,
Worker→Api reference) are informational and do not block merge of this §6.1-scoped implementation.

---

*This report was generated by an independent reviewer agent with read-only access to the codebase.
The reviewer had no knowledge of the implementation conversation — only the spec and the code. All
verdicts above were reached by reading the actual source files and by independently re-running
`dotnet build`/`dotnet test`, not by trusting the spec's Implementation Log summary.*
