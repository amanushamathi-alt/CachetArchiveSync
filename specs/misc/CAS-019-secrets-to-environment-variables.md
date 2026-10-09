---
id: CAS-019
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-23
priority: P1
change_type: security
related_specs: [CAS-001, CAS-003]
touches:
  database: false
  api: true
  ui: true
  worker: true
  b2: true
  config: true
  security: true
migrations: []
---

# CAS-019 — Change: DB Connection String and B2 System Credentials Move to Environment Variables (All Environments)

## §1 Current Behavior

The SQL Server connection string and the B2 system credentials are checked into source control,
in plaintext, in each project's `appsettings.Development.json`:

- `Cachet.ArchiveSync.Api/appsettings.Development.json`: `ConnectionStrings:CachetArchiveSync`
  (a real, working localdb connection string) and `B2:SystemKeyId`/`SystemApplicationKey`/
  `BucketName` (currently blank placeholders, per the file's own inline `// NOTE` warning not to
  put a real key there).
- `Cachet.ArchiveSync.Worker/appsettings.Development.json`: same `ConnectionStrings` value and
  same blank `B2:*` placeholders.
- `Cachet.ArchiveSync.Web/appsettings.Development.json`: **does not currently define
  `ConnectionStrings:CachetArchiveSync` at all**, even though `Web/Data/UserRepository.cs:21-23`
  requires it (throws `InvalidOperationException` if missing) for its CAS-002 §6.3a session-
  revocation check — a pre-existing gap this spec also closes (see §14). Note: this is *not*
  general Web-to-database access — Web's `UserRepository` implements only the single
  `GetSessionTokenVersionAsync` read for CAS-002 §6.3a's per-request revocation check; every other
  method is `NotSupportedException` by design, with login/logout/lookup staying Api-only. This
  spec does not expand what Web touches — it only fixes the source of a config value an
  already-existing, already-spec'd code path already reads.

None of the three projects' base `appsettings.json` files (the ones loaded in every environment,
including Production) define these keys at all today — there is no `appsettings.Production.json`
in the repo (confirmed in an earlier conversation), so in a real deployment these values currently
have no defined source at all unless someone creates that file or sets them another way; nothing
in the codebase enforces environment variables as the source today.

All three projects use the .NET default host-builder configuration pipeline
(`WebApplication.CreateBuilder` for Api/Web, `Host.CreateApplicationBuilder` for Worker), which —
**with no code change required** — already layers configuration sources in this order: base
`appsettings.json` → `appsettings.{Environment}.json` → User Secrets (Development only) →
**environment variables** → command-line args, with each later source overriding earlier ones for
the same key.

## §2 Desired Behavior

For all three projects, in every environment (Development included — not just Production):

1. `ConnectionStrings:CachetArchiveSync` and all of `B2:SystemKeyId` / `B2:SystemApplicationKey` /
   `B2:BucketName` are removed from every checked-in `appsettings*.json` file (base and
   `.Development.json`) — no real or placeholder secret value remains in source control for these
   four keys, in any project, in any environment.
2. These four values are supplied exclusively via environment variables, using ASP.NET Core's
   double-underscore (`__`) nested-key convention, which the existing default configuration
   pipeline already understands with no code change (§1):

   | Config key | Environment variable name | Required by |
   |---|---|---|
   | `ConnectionStrings:CachetArchiveSync` | `ConnectionStrings__CachetArchiveSync` | Api, Web, Worker |
   | `B2:SystemKeyId` | `B2__SystemKeyId` | Api, Worker |
   | `B2:SystemApplicationKey` | `B2__SystemApplicationKey` | Api, Worker |
   | `B2:BucketName` | `B2__BucketName` | Api, Worker |

3. Local developers set these in their own machine/session (e.g. `setx`, a local `.env` loaded by
   their shell profile, or IDE launch-profile environment variables — deliberately **not**
   `launchSettings.json` committed to source control, since that file is checked in) rather than
   in `appsettings.Development.json`.
4. Production/other deployed environments set these through whatever secrets mechanism that
   environment uses to inject process environment variables (a deployment pipeline's secret
   store, an orchestrator's secret injection, etc.) — this spec defines the variable *names* the
   app expects; it does not prescribe which secrets manager sets them, since that decision is
   still open per the earlier deployment discussion (WBS "Production environment" task, not yet
   started).
5. Everything else already in `appsettings*.json` — `Auth:CookieName`, `Auth:SlidingExpirationHours`,
   `Auth:DataProtection:KeyRingPath`, `B2:CrossCheckMaxConcurrency`, `Search:MaxTermLength`,
   `Sync:MaxBatchSize`, `Api:BaseUrl` — is **not** a credential and stays in `appsettings*.json` as
   today. This spec is scoped exactly to the connection string and the B2 system credentials, per
   the request — it does not migrate every config value to environment variables (see §14 for the
   one exception flagged: `Web`'s missing connection string).

## §3 Reason for Change

- **Type:** `security`
- **Trigger:** Client/solution-owner request (Rakesh Devarajan, 2026-09-23): the SQL connection
  string and B2 system credentials must be sourced from environment variables in every
  environment, not checked-in config files — a standard secrets-hygiene requirement for a system
  handling regulated data, and consistent with this project's own existing rule (CLAUDE.md: "the
  B2 application key is a server-side secret ... must never appear in a commit").
- **Risk if not changed:** A real connection string (with credentials, if the deployed SQL Server
  uses SQL auth rather than the current trusted-connection localdb default) or a real B2 system
  key pasted into any `appsettings*.json` file — even `.Development.json` — becomes part of git
  history permanently, even if later removed, and is readable by anyone with repo access
  regardless of their actual need for that credential.

## §4 Blast Radius (Graph Impact)

### Primary Change Target
Files: `appsettings.json` and `appsettings.Development.json` in `Cachet.ArchiveSync.Api`,
`Cachet.ArchiveSync.Web`, `Cachet.ArchiveSync.Worker`. These are configuration data files, not
code entities — not represented as graph nodes.

### Direct Dependents (nodes with edges INTO the changed entity)
Every class that calls `configuration.GetConnectionString("CachetArchiveSync")` or reads
`B2:SystemKeyId`/`SystemApplicationKey`/`BucketName` — confirmed by direct grep (config values
are data, not call-graph edges, so this is enumerated by reference search, not `graphify query`):

- `B2Client.cs` (Api + Worker, shared via project reference)
- `UserRepository.cs` (Api), `UserRepository.cs` (Web)
- `TaskQueueRepository.cs`, `SyncResultRepository.cs`, `SearchResultRepository.cs`,
  `FileIndexRepository.cs`, `BucketRootMappingRepository.cs`, `BucketMirrorRootRepository.cs`,
  `AuditLogRepository.cs` (all Api, all reused by Worker via project reference)

None of these classes change — they already read config by key name (`GetConnectionString(...)`,
`configuration["B2:SystemKeyId"]`), which is source-agnostic in .NET's configuration model. Moving
the *value* from a JSON file to an environment variable requires zero changes to any of them.

### Downstream Modules
All modules — the connection string is read by nearly every repository across Auth, Keyword
Search, O-Drive Sync, Quarantine, Task Manager, Audit Log. This is a config-source change only;
no module's behavior changes.

### Cross-Project Risk
Touches all three deployable projects (Api, Web, Worker) — by nature of the request ("all
environments" implies all processes that read these keys), but each project's change is the same
mechanical edit (strip values from its own `appsettings*.json`), not a shared-contract change.
`Cachet.ArchiveSync.Core` is untouched.

### God Node Involvement
N/A — no code entity is modified.

### Graphify Queries Run
Not applicable — this change touches configuration data files, which are not indexed as graph
nodes. Blast radius was mapped via direct `grep` for every `GetConnectionString`/`B2:System*`
reference (see §1's grep results from the earlier conversation), listed exhaustively above.

---

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `Cachet.ArchiveSync.Api/appsettings.Development.json` | modify | Remove `ConnectionStrings` and `B2:SystemKeyId`/`SystemApplicationKey`/`BucketName`; keep `B2:CrossCheckMaxConcurrency`, `Search`, `Sync` sections as-is. |
| `Cachet.ArchiveSync.Worker/appsettings.Development.json` | modify | Same removal as above. |
| `Cachet.ArchiveSync.Web/appsettings.Development.json` | modify | No `ConnectionStrings` to remove (doesn't have one — §1), but add the same env-var expectation to documentation (§14 also flags this file is currently missing the key it needs at runtime). |
| `Cachet.ArchiveSync.Api/appsettings.json`, `Cachet.ArchiveSync.Worker/appsettings.json`, `Cachet.ArchiveSync.Web/appsettings.json` | verify only | Confirm none of the four keys are present (grep in §1 already shows base files have neither — no edit expected, verification step only). |
| `README.md` or a new `docs/environment-variables.md` (author's choice at implementation time) | add | Document the four required environment variable names (§2 table) and their target processes, for local dev setup and for the deployment runbook once one exists. |

---

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `B2Client.cs`, `UserRepository.cs` (both), and every repository listed in §4 | Already read config by key name via `IConfiguration` — source-agnostic; no code change needed for a value to move from JSON to an environment variable. |
| `Program.cs` (all three projects) | `CreateBuilder`/`Host.CreateApplicationBuilder` already include the environment-variable configuration source by default — no explicit `AddEnvironmentVariables()` call needs adding. |
| `Auth:*`, `Search:*`, `Sync:*`, `B2:CrossCheckMaxConcurrency`, `Api:BaseUrl` config sections | Not credentials — explicitly out of scope per §2 point 5. |

---

## §7 Database Changes

N/A — no schema or stored-procedure change.

---

## §8 API Contract Changes

N/A — no endpoint, request, or response shape changes.

---

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | App can start with `ConnectionStrings:CachetArchiveSync`/`B2:System*` sourced from a checked-in `appsettings.Development.json` | App fails to start with a clear, existing error (`B2Client`'s current `RequireConfig`/`UserRepository`'s current null-check already throw `InvalidOperationException` naming the missing key) if these environment variables are not set — no behavior change needed here, since the existing fail-fast checks already cover a missing value regardless of its source | `Test_V1_AppFailsFastWhenEnvVarsUnset` (manual/CI environment check, not a unit test of app code — see §14) |
| V2 | `git log`/current tree contains a real localdb connection string in `Cachet.ArchiveSync.Api/appsettings.Development.json` | No `ConnectionStrings` or `B2:System*` key with a non-empty value exists in any checked-in `appsettings*.json`, in any of the three projects | Manual review / a simple CI grep-based check (see §14) |

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `grep -r "ConnectionStrings\|SystemKeyId\|SystemApplicationKey\|BucketName" **/appsettings*.json` across all three projects returns no key with a non-placeholder-removed value (i.e., the keys themselves are absent, not merely blanked) | Manual verification at review time |
| SC-2 | With the four environment variables set on the host/shell, `dotnet run` for Api, Web, and Worker each start successfully and each successfully exercises a config-dependent path (Api/Worker: a DB call; Api/Worker: a B2 call) | Manual smoke test (matches the "post-deploy smoke test" already recommended in the earlier deployment conversation) |
| SC-3 | With the four environment variables **unset**, each project fails to start (or fails the first request) with the same existing, already-implemented, non-secret-leaking error messages (`B2Client.RequireConfig`, `UserRepository`'s connection-string null-check) — no new failure mode needed, existing fail-fast behavior is sufficient | Manual verification — no code change required to satisfy this |
| SC-4 | Web's session-revocation check (`Web/Data/UserRepository.cs`) no longer throws at startup/first-request for a missing connection string once `ConnectionStrings__CachetArchiveSync` is set via environment variable for the Web process too (closes the §1/§14 gap) | Manual smoke test |

---

## §11 Security Notes

- This is a config-hygiene change with no code-level attack-surface change — the values were
  already required by existing code; only their *source* moves.
- Environment variables are visible to anything with process-inspection access on the host (e.g.
  `/proc/<pid>/environ` equivalents, or a process listing in some tooling) — this is a strictly
  better posture than a file in git history, but it is not equivalent to a dedicated secrets
  manager (Key Vault, etc.). The earlier deployment conversation already flagged that choice as
  open (WBS "Production environment," not yet started) — this spec supplies the *variable names*
  a future secrets-manager-backed deployment would inject; it does not itself decide the
  production secrets-management mechanism.
- Removing the real localdb connection string from `appsettings.Development.json` means local
  developers must set `ConnectionStrings__CachetArchiveSync` themselves before first run — this is
  a deliberate friction increase in exchange for not having any working connection string (even a
  low-value local one) committed to source control by convention, since "just this once, a dev
  value is fine" is exactly the drift this spec exists to prevent.

---

## §12 Rollback Plan

1. Restore the previous `appsettings.Development.json` contents (git revert) for all three
   projects if environment-variable-based configuration proves unworkable in some environment.
2. Database rollback: N/A.
3. Config rollback: reverting step 1 is the entire rollback — no other config surface changes.
4. Task-queue rollback: N/A — no task-queue contract touched.

---

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| N/A — this is a configuration-file and documentation change; no new unit/integration test class is warranted (Karpathy P3: no test asserts "a JSON file doesn't contain a string," and the fail-fast behavior being relied on in V1/SC-3 is already covered by existing tests — `B2ClientTests`' `Test_V1_B2ClientRejectsNonServerKey` family already proves the missing-config-throws behavior regardless of source) | | |

Coverage target: N/A (no code changed).

---

## §14 Open Questions — Resolved 2026-09-23 (Rakesh Devarajan)

- [x] **`Web`'s missing `ConnectionStrings:CachetArchiveSync`** — **Resolved: fix it as part of
      this spec**, not a separate bug-fix spec. Rationale: `Web/Data/UserRepository.cs:33` already
      calls `configuration.GetConnectionString("CachetArchiveSync")` today, for the CAS-002 §6.3a
      session-revocation check only — this is a narrow, spec-mandated, already-coded exception to
      the "Web calls Api over HTTP, never touches Core's data-access implementations directly"
      convention (clean-architecture skill), not new DB access introduced by CAS-019. Every other
      method on that class (`GetByUsernameAsync`, `GetByIdAsync`,
      `IncrementSessionTokenVersionAsync`) is `NotSupportedException` — login, lookup, and logout
      stay Api-only. Because that call already exists and already throws today (the key is
      undefined in Web's `appsettings.Development.json`), CAS-019's env-var migration is the
      correct place to close the gap: it's the same one-line addition to the same table (§5), and
      splitting it into its own spec would just be a second spec restating this same clarification.
- [x] **Local developer env-var pattern** — **Resolved: document one canonical convention**, not
      leave it fully open, to avoid onboarding drift across developers. Recommendation: a
      git-ignored `env.local.ps1` script per developer at the repo root (added to `.gitignore`),
      dot-sourced (`. .\env.local.ps1`) before `dotnet run`, setting
      `$env:ConnectionStrings__CachetArchiveSync` etc. for the current shell session only —
      reproducible, greppable in documentation, and avoids machine-wide `setx` state that silently
      outlives a project. Document this in the `docs/environment-variables.md` file added in §5,
      including an `env.local.ps1.example` template (with placeholder, non-functional values) that
      *is* checked in. This is a recommendation for the team's convention, not an app behavior
      requirement — adjust if the team prefers a different local pattern.
- [x] **CI check for key reappearance (SC-1)** — **Resolved: defer, tracked as follow-up, not
      blocking this spec.** No CI pipeline exists yet in this repo (WBS "Build/CI pipeline setup,"
      not yet started), so this spec cannot depend on one. Once a CI pipeline is scaffolded, add a
      grep-based step (`grep -rE "SystemKeyId|SystemApplicationKey|BucketName|ConnectionStrings" **/appsettings*.json`
      failing the build on a non-empty value) as part of that pipeline's setup task, not as an
      addition to CAS-019's scope now.

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
Reviewer writes `CAS-019.review.md`:
- Verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Scope violations (edits outside §5 file list)
- Unintended blast radius (graphify, or manual read, found changes in §6 files)
- Security gaps

## Implementation Log

**Completed:** 2026-09-23
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage N/A (no instrumented code changed) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json` | Removed ConnectionStrings:CachetArchiveSync and B2:SystemKeyId/SystemApplicationKey/BucketName |
| `src/Cachet.ArchiveSync.Worker/appsettings.Development.json` | Same removal |
| `docs/environment-variables.md` | Added: documents the four env var names and the env.local.ps1 local-dev convention |
| `env.local.ps1.example` | Added: checked-in placeholder template |
| `.gitignore` | Added (did not previously exist); ignores env.local.ps1 |
| `src/Cachet.ArchiveSync.Api.Tests/LogHygieneTests.cs` | Fixed pre-existing test/constructor drift from CAS-018 (out-of-scope defect, fixed with user approval to unblock Gate 1) |
| `src/Cachet.ArchiveSync.Api/Controllers/WeatherForecastController.cs` | Deleted leftover scaffold file blocking build (out-of-scope, removed with explicit user approval) |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
