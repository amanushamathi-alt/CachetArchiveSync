---
id: CAS-041
target_app: cachet-archive-sync
status: in-progress
owner: devarajan@tracecounts.org
created: 2026-09-30
priority: P1
change_type: enhancement
related_specs: [CAS-001, CAS-002, CAS-019, CAS-022]
touches:
  database: true
  api: true
  ui: true
  worker: true
  b2: true
  config: true
  security: true
migrations: [20260930_170000__CAS-041-configuration-tables.sql]
---

# CAS-041 — Change: Api:BaseUrl and B2 System Settings Read from dbo.configuration_detail

## §1 Current Behavior

Per CAS-019 (`docs/environment-variables.md`), four settings are supplied **only** via process
environment variables and read through `IConfiguration`:

| Env var | Config key | Read at |
|---|---|---|
| `Api__BaseUrl` | `Api:BaseUrl` | `Web/Program.cs:140` (startup, throws if unset) |
| `B2__SystemKeyId` | `B2:SystemKeyId` | `Api/B2/B2Client.cs:68`, `Api/B2/UserScopedB2DeleteClient.cs:67` (Api + Worker) |
| `B2__SystemApplicationKey` | `B2:SystemApplicationKey` | `B2Client.cs:69`, `UserScopedB2DeleteClient.cs:68` (Api + Worker) |
| `B2__BucketName` | `B2:BucketName` | `B2Client.cs:70`, `UserScopedB2DeleteClient.cs:69`, `Api/Services/KeywordSearchService.cs:204`, `Web/Components/Pages/KeywordSearch.razor:709`, `Worker/B2MirrorSyncTaskProcessor.cs:66`, `Worker/SyncCopyTaskProcessor.cs:112` |

The client has created `dbo.configuration`, `dbo.configuration_detail` and
`dbo.configuration_detail_lst @ConfigurationName` directly in SSMS (mirrors exist under
`Database/CachetArchiveSync/`, but no migration). Rows: group `Api_Url` → `Api__BaseUrl`;
group `B2_Keys` → `B2__SystemKeyId`, `B2__SystemApplicationKey`, `B2__BucketName`, value in
`configuration_value_1`. No application code reads these tables today.

## §2 Desired Behavior

1. Api, Web and Worker each add a **database configuration source** to `builder.Configuration`
   immediately after `CreateBuilder`/`CreateApplicationBuilder`, which calls
   `dbo.configuration_detail_lst` once at startup per configured group and maps
   `configuration_detail_name` → config key by replacing `__` with `:` (same convention as
   environment variables), value = `configuration_value_1`.
2. Each process loads only an explicit allow-list:
   - Api, Worker: `B2_Keys` → `B2__SystemKeyId`, `B2__SystemApplicationKey`, `B2__BucketName`
   - Web: `Api_Url` → `Api__BaseUrl`; `B2_Keys` → `B2__BucketName` **only** (Web never receives
     the B2 system key — it never calls B2, CAS-019/CAS-022).
3. Precedence: the database source is added last, so a non-empty DB value overrides the
   environment variable; a missing row or NULL/blank value falls through to the environment
   variable (and then to the existing fail-fast checks, unchanged).
4. Source is skipped entirely when `ConfigurationDatabase:Enabled` is `false` or the connection
   string is unset. If the connection string is set but the load fails, startup fails with an
   `InvalidOperationException` naming the group — never any value.
5. Values are loaded once; changing a DB value requires restarting the process.
6. No consumer (`B2Client`, `UserScopedB2DeleteClient`, `KeywordSearchService`,
   `KeywordSearch.razor`, Worker processors, `Web/Program.cs:140`) changes.

## §3 Reason for Change

- **Type:** enhancement
- **Trigger:** Client decision (Manoj, 2026-09-30): these four environment variables are to be
  read from the database tables the client created; plain-text storage explicitly accepted
  ("dont bother about plain text"). Supersedes CAS-019's env-var-only rule for these four keys.
- **Risk if not changed:** settings continue to require per-host environment-variable
  maintenance (IIS app pool + Worker service host).

## §4 Blast Radius (Graph Impact)

### Primary Change Target
Entity: the `IConfiguration` pipeline of the three hosts (new `DatabaseConfigurationSource`).
Current edges: consumers read by string key only — no direct edge to the new source.

### Direct Dependents
All §1 read sites. None change: they read the same keys, only the value's origin changes.

### Downstream Modules
Keyword Search (bucket name), O-Drive Sync / mirror sync (bucket, system key), Quarantine
Permanent Delete (`UserScopedB2DeleteClient`), Web → Api forwarding (`Api:BaseUrl`). **Auth /
login is not affected** — `LoginService` uses the per-user key via `UserB2KeyAuthorizer`, not
the system key.

### Cross-Project Risk
**CROSS-PROJECT RISK** — all three hosts' startup changes. Core gains the shared source/provider
(and a `Microsoft.Extensions.Configuration` package reference, not ASP.NET Core — consistent with
the clean-architecture rule). Worker reuses Api's SQL loader via its existing Api project
reference; Web gets its own loader (Web does not reference Api — same precedent as
`Web/Data/UserRepository.cs`).

### God Node Involvement
`IConfiguration` is a high-degree node, but its contract is untouched; only a source is added.

### Graphify Queries Run
```
python -m graphify explain "B2Client"
python -m graphify query "what depends on IConfiguration B2:SystemKeyId Api:BaseUrl B2:BucketName" --budget 1500
```
Confirmed with a manual grep of every `B2:*` / `Api:BaseUrl` read site (§1).

**Amendment (2026-10-01):** the original grep covered `src/` only and missed the CAS-031 index-refresh
scripts (`scripts/index_refresh/`), which read `B2__BucketName`, `B2__SystemKeyId` and
`B2__SystemApplicationKey` directly from the environment and failed once those variables were removed.
They now use the same source, allow-list and precedence (DB non-blank value → environment variable;
`ConfigurationDatabase__Enabled=false` skips the DB); an explicit `--bucket` argument still wins.

### Decomposition check (P9)
Spans Core/Api/Web/Worker, but each host change is one registration line; total changed files
≈ 12, 0 endpoints, 1 SP (existing). Single spec.

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `Database/migrations/20260930_170000__CAS-041-configuration-tables.sql` | add | Idempotent creation of `dbo.configuration`, `dbo.configuration_detail`, `dbo.configuration_detail_lst` (as already deployed); no seed rows |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.configuration_detail_lst.sql` | modify | Re-save UTF-8, standard mirror header (was UTF-16, header said `[mst]`); body unchanged |
| `src/Cachet.ArchiveSync.Core/Cachet.ArchiveSync.Core.csproj` | modify | Add `Microsoft.Extensions.Configuration` 8.0.0 |
| `src/Cachet.ArchiveSync.Core/Dtos/ConfigurationDetailRow.cs` | add | Row DTO |
| `src/Cachet.ArchiveSync.Core/Configuration/DatabaseConfigurationSource.cs` | add | Source + provider (mapping, allow-list, enable/skip, fail-fast) |
| `src/Cachet.ArchiveSync.Api/Repositories/ConfigurationDetailLoader.cs` | add | Dapper call to `dbo.configuration_detail_lst` |
| `src/Cachet.ArchiveSync.Web/Data/ConfigurationDetailLoader.cs` | add | Same, Web-local |
| `src/Cachet.ArchiveSync.Api/Program.cs` | modify | Register source |
| `src/Cachet.ArchiveSync.Worker/Program.cs` | modify | Register source |
| `src/Cachet.ArchiveSync.Web/Program.cs` | modify | Register source (before line 140) |
| `docs/environment-variables.md` | modify | Document DB source, precedence, `ConfigurationDatabase__Enabled` |
| `scripts/index_refresh/common.py` | modify | Amendment 2026-10-01: `optional_setting`/`require_setting` read `dbo.configuration_detail_lst` (`B2_Keys` allow-list) then env |
| `scripts/index_refresh/refresh_odrive_index.py`, `refresh_b2_index.py` | modify | Amendment: bucket and B2 keys resolved via the above after connecting |
| `scripts/index_refresh/README.md` | modify | Amendment: setup step 4 |
| `src/Cachet.ArchiveSync.Api.Tests/Configuration/DatabaseConfigurationProviderTests.cs` | add | Unit tests |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `Api/B2/B2Client.cs`, `Api/B2/UserScopedB2DeleteClient.cs` | Same keys, same `RequireConfig` fail-fast |
| `Api/Services/KeywordSearchService.cs`, `Web/Components/Pages/KeywordSearch.razor` | Same key |
| `Worker/*TaskProcessor.cs` | Same key |
| `Api/Services/LoginService.cs`, `Api/B2/UserB2KeyAuthorizer.cs` | Login does not use these keys |
| `appsettings*.json` (all) | No values added; secrets still never in appsettings |
| `Database/CachetArchiveSync/Entities/dbo.configuration*.sql` | Already match deployed tables |

## §7 Database Changes

### Changed Objects
| Object | Schema | Change | Notes |
|--------|--------|--------|-------|
| `configuration` | dbo | migration added (create if not exists) | Already deployed by client |
| `configuration_detail` | dbo | migration added (create if not exists) | FK to `configuration` |
| `configuration_detail_lst` | dbo | `CREATE OR ALTER`, body unchanged | Parameters `@ConfigurationName`, `@ConfigurationValue1 = NULL` |

### Migration Required
Yes — `Database/migrations/20260930_170000__CAS-041-configuration-tables.sql` + mirror
re-encoding. No data seeded (values are environment-specific and entered by the client).

## §8 API Contract Changes

N/A — no endpoint changes.

## §9 Validations Changed

| ID | Rule (Before) | Rule (After) | Test |
|----|--------------|-------------|------|
| V1 | — | Only allow-listed `configuration_detail_name` values in allow-listed groups become config keys | `Test_V1_OnlyAllowListedNamesAreLoaded` |
| V2 | — | NULL/whitespace `configuration_value_1` is not added (falls back to env var) | `Test_V2_BlankValueIsSkipped` |
| V3 | — | Load failure → `InvalidOperationException` naming the group, containing no value | `Test_V3_LoadFailureThrowsWithoutValues` |

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | `B2__SystemKeyId` row maps to key `B2:SystemKeyId` with `configuration_value_1` as value | `Test_SC1_DoubleUnderscoreMapsToColon` |
| SC-2 | DB value overrides an existing environment/earlier-source value for the same key | `Test_SC2_DatabaseValueOverridesEarlierSource` |
| SC-3 | Missing row leaves the earlier-source (env var) value in effect | `Test_SC3_MissingRowFallsBackToEarlierSource` |
| SC-4 | Web allow-list never yields `B2:SystemKeyId`/`B2:SystemApplicationKey` even when the group returns them | `Test_SC4_WebAllowListExcludesSystemKey` |
| SC-5 | `ConfigurationDatabase:Enabled=false` or blank connection string → loader never called | `Test_SC5_DisabledOrNoConnectionStringSkipsLoad` |
| SC-6 | Solution builds; all pre-existing unit tests still pass | build + `dotnet test` |

## §11 Security Notes

- **Accepted risk (client decision 2026-09-30):** `B2__SystemApplicationKey` is stored in
  plain text in `dbo.configuration_detail.configuration_value_1`. Anyone with SELECT on the table
  or EXECUTE on `dbo.configuration_detail_lst` can read the B2 system key. Recommended
  compensating control (not in scope): restrict those permissions to the application login.
- Least privilege preserved: Web's allow-list excludes the system key (FR-03 intent — system key
  only in the processes that call B2).
- The provider never logs values; exception messages name the group only.
- The system key was visible in a screenshot shared on 2026-09-30 — rotation recommended
  (operational, out of scope).

## §12 Rollback Plan

1. Set `ConfigurationDatabase__Enabled=false` on the host (or revert the three `Program.cs`
   lines) — environment variables resume as the sole source; keep them set until cut-over is
   verified.
2. Database rollback: none needed (tables/SP pre-existed; migration is create-if-missing).
3. Task-queue rollback: N/A — no task-queue contract change.

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| `DatabaseConfigurationProviderTests` | `Test_SC1_DoubleUnderscoreMapsToColon` | SC-1 |
| | `Test_SC2_DatabaseValueOverridesEarlierSource` | SC-2 |
| | `Test_SC3_MissingRowFallsBackToEarlierSource` | SC-3 |
| | `Test_SC4_WebAllowListExcludesSystemKey` | SC-4 |
| | `Test_SC5_DisabledOrNoConnectionStringSkipsLoad` | SC-5 |
| | `Test_V1_OnlyAllowListedNamesAreLoaded` | V1 |
| | `Test_V2_BlankValueIsSkipped` | V2 |
| | `Test_V3_LoadFailureThrowsWithoutValues` | V3 |

Coverage target: ≥ 85% on `DatabaseConfigurationSource.cs`. The two SQL loaders are thin Dapper
calls covered by manual verification against the dev DB (§16).

## §14 Open Questions

- [ ] Unique index on (`Configuration_id`, `configuration_detail_name`) not added (not
      requested); duplicate rows → last row returned wins.
- [x] (Resolved 2026-10-01: `dbo.odrive_bucket_mapping` has a row for `tc-test-Server`.) `B2__BucketName` in DB (`tc-test-Server`) must match the bucket used in
      `dbo.odrive_bucket_mapping` / mirror roots, or Keyword Search will find no mirror root.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | Claude (for Manoj) | 2026-09-30 | Plan approved in chat |
| Reviewer | | | |

## §16 Verification Plan

- Reviewer checks per template; plus a manual startup of Api/Web/Worker against the dev DB with
  the four env vars unset, confirming sign-in, Keyword Search bucket dropdown, and B2 calls work.
