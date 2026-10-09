---
id: CAS-053
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-10-05
priority: P1
change_type: bug-fix
related_specs: [CAS-050, CAS-051, CAS-049]
touches:
  database: false
  api: true
  ui: false
  worker: false
  b2: false
  config: false
  security: true
migrations: []
---

# CAS-053 — Change: Quarantine browse routes resolve the B2 path prefix (Api)

> Follow-up to CAS-050 (merged), found by the CAS-051 review (finding **D-1**,
> `specs/web/cachet-archive-sync/CAS-051.review.md`). BRD: FR-09a, FR-14, FR-23; BRD §12.3 item 2
> (O-Drive/B2 relative-path mapping via the configuration table). The Web contract (root-relative
> `folderPath`) is unchanged, so CAS-051 needs no code change.

## §1 Current Behavior

- `FileBrowseService.BrowseAsync` (`Services/FileBrowseService.cs:62-81`) takes a **root-relative** `folder`,
  looks up `b2_path_prefix` for the bucket via `IB2PathPrefixProvider.GetPrefixAsync`, and queries with
  `fullPath = prefix + "/" + folder` (or just `prefix` for the root). Prefix `null` ⇒ `MappingMissing`
  (HTTP 500 `mirror-mapping-missing`). The prefix is never returned to the client.
- `QuarantineBrowseService` (`Services/QuarantineBrowseService.cs`) passes `request.FolderPath` /
  `folderPath` **unchanged** to `InsertBrowseFolderAsync` (line 88), `HardDeleteBrowseFolderAsync`
  (line 142) and `GetBrowseStateAsync` (line 182). The stored procedures compare it against
  `file_index.relative_path`, which is the **full key path including the prefix** (CAS-050 §"file_index").
  The same unprefixed value is written to the audit row (`FilePath`, lines 96 and 150).
- The Web client (`FileExplorer.razor`) sends the root-relative path it got from the browse endpoint.
- Result (the prefix is always non-empty in production): state returns no/incorrect folders, so folder
  checkboxes are missing or wrong and already-quarantined files show unchecked; folder add/remove return 404
  `folder-not-found`. The CAS-050/CAS-051 tests passed only because they used fake or empty-prefix data.

## §2 Desired Behavior

`QuarantineBrowseService` converts the incoming root-relative `folderPath` to the full
key path before any repository call, using the same rule as `FileBrowseService`:

1. Prefix = `IB2PathPrefixProvider.GetPrefixAsync(request.BucketName)` (the **request's** bucket, not
   configuration — the request already carries it).
2. `fullPath = folderPath.Length == 0 ? prefix : prefix + "/" + folderPath`. The prefix is always non-empty
   (the mapping row always holds `D/Shares/Operations`), so there is no empty-prefix case. The state route's
   empty `folderPath` (bucket root) maps to the prefix itself.
3. Applies to all three routes: `GET /quarantine/browse/state`, `POST /quarantine/browse` (Folder scope),
   `POST /quarantine/browse/remove` (Folder scope). File scope uses `fileIndexId` and is unchanged.
4. Prefix `null` (no mapping row for that bucket) ⇒ new outcome `MappingMissing` ⇒ HTTP 500 ProblemDetails
   `mirror-mapping-missing`, same title as browse. No repository call, no audit row.
5. `fullPath.Length > 1000` ⇒ validation failure (422), because the SPs' `@folder_path` is NVARCHAR(1000)
   and must never truncate silently (same guard as `FileBrowseService`).
6. The **audit** row for a folder add/remove records the **full key path** (`fullPath`), consistent with file
   rows, which already record `file.RelativePath`. The log lines keep counts only.
7. The state response is unchanged: `folders[].folderName` is a leaf name and `fileIndexIds` are ids, so no
   prefix is exposed to the browser (FR-03 spirit; prefix is configuration, never a secret).

## §3 Reason for Change

- **Type:** bug-fix
- **Trigger:** CAS-051 independent review, finding D-1 (contract mismatch between the root-relative browse
  paths and the full-key-path matching in the CAS-050 quarantine routes).
- **Risk if not changed:** in production (`odrive_bucket_mapping.b2_path_prefix` is always `D/Shares/Operations`), the
  Files quarantine folder feature silently shows wrong state and fails on every folder action.

## §4 Blast Radius (Graph Impact)

_graphify has no node-level data for this module — blast radius mapped via manual grep/read._

### Primary Change Target

File: `src/Cachet.ArchiveSync.Api/Services/QuarantineBrowseService.cs`
Entity: `QuarantineBrowseService` (`AddAsync`, `RemoveAsync`, `GetStateAsync`).
Edges: injected `IQuarantineEntryRepository`, `IFileIndexRepository`, `IAuditLogService`; consumed only by
`QuarantineController` via `IQuarantineBrowseService`.

### Direct Dependents

- `Controllers/QuarantineController.cs` (maps outcomes → HTTP; needs the new `MappingMissing` arm)
- `Core/Interfaces/IQuarantineBrowseService.cs` (outcome enums)
- `Core/Interfaces/IB2PathPrefixProvider.cs` + `Repositories/B2PathPrefixProvider.cs` (already registered in
  `Program.cs:272`; consumed, not changed)
- Tests: `QuarantineBrowseServiceTests`, `QuarantineBrowseControllerTests`, `QuarantineAddRouteTests`,
  `QuarantineControllerTests` (constructor/DI), `Api.IntegrationTests/QuarantineControllerTests`

### Downstream Modules

Quarantine (Files browser add/remove/state); Audit Log (folder-action `FilePath` value becomes the full key
path); Web Files explorer (CAS-051) — behaviour fixed, contract unchanged.

### Cross-Project Risk

**CROSS-PROJECT RISK (low):** Core enum additions are additive (`MappingMissing`); the only consumer of the
enums is the Api controller. Web needs no change.

### God Node Involvement

None. `QuarantineBrowseService` is a leaf service (one controller consumer).

### Graphify Queries Run

graphify not queried at node level — manual grep/read of `FileBrowseService`, `QuarantineBrowseService`,
`QuarantineController`, `B2PathPrefixProvider`, and the CAS-050 spec.

### Decomposition check (P9)

Single Api-only unit: 3 production files plus a new Core helper and a 3-line swap in 2 browse services,
0 SPs, 0 new endpoints, ≤ 8 SCs — no split.

## §5 Files to Change

| File                                                                                                                                       | Change Type | Description                                                                                                                                   |
| ------------------------------------------------------------------------------------------------------------------------------------------ | ----------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/Cachet.ArchiveSync.Core/Helpers/B2KeyPath.cs`                                                                                         | create      | Static `Combine(prefix, folder)` (the 3-line join; Q-C)                                                                                       |
| `src/Cachet.ArchiveSync.Api/Services/FileBrowseService.cs`, `SearchResultBrowseService.cs`                                                 | modify      | Replace the inline join with `B2KeyPath.Combine` — no behaviour change (Q-C)                                                                  |
| `src/Cachet.ArchiveSync.Api/Services/QuarantineBrowseService.cs`                                                                           | modify      | Inject `IB2PathPrefixProvider`; resolve `fullPath` via `B2KeyPath.Combine` for B2 folder add/remove/state; length guard; audit with full path |
| `src/Cachet.ArchiveSync.Core/Interfaces/IQuarantineBrowseService.cs`                                                                       | modify      | Add `MappingMissing` to the three outcome enums                                                                                               |
| `src/Cachet.ArchiveSync.Api/Controllers/QuarantineController.cs`                                                                           | modify      | Map `MappingMissing` ⇒ 500 `mirror-mapping-missing` on the three routes                                                                       |
| `Api.Tests/FileBrowseServiceTests.cs`, `Api.Tests/SearchResultBrowseServiceTests.cs`, `Api.IntegrationTests/FilesBrowseRepositoryTests.cs` | modify      | Delete the empty/blank-prefix test cases only (moot scenario)                                                                                 |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantineBrowseServiceTests.cs`                                                                         | modify      | New ctor arg; prefix cases                                                                                                                    |
| `src/Cachet.ArchiveSync.Api.Tests/QuarantineBrowseControllerTests.cs`                                                                      | modify      | `MappingMissing` ⇒ 500                                                                                                                        |
| `src/Cachet.ArchiveSync.Api.IntegrationTests/QuarantineControllerTests.cs` (+ harness if needed)                                           | modify      | End-to-end with a non-empty prefix                                                                                                            |
| `specs/api/cachet-archive-sync/CAS-050-files-browse-quarantine-api.md`                                                                     | modify      | Add a one-line revision note: `folderPath` on the browse routes is root-relative; the service prefixes it (CAS-053)                           |

## §6 Files NOT to Change

| File                                                                                      | Why left untouched                                                                                                       |
| ----------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `Database/**` (all `quarantine_entry_*_browse_*` SPs)                                     | They correctly take the full key path; the defect is the value passed in                                                 |
| `B2PathPrefixProvider.cs`, the browse services' validation/guard logic                    | Provider reused as is; the two browse services change only by swapping the inline join for `B2KeyPath.Combine` (§5, Q-C) |
| `src/Cachet.ArchiveSync.Web/**` (incl. `FileExplorer.razor`, `QuarantineBrowseClient.cs`) | Web contract stays root-relative (CAS-051 unchanged)                                                                     |
| `Core/Dtos/QuarantineBrowse*.cs`                                                          | No request/response shape change                                                                                         |

## §7 Database Changes

N/A.

## §8 API Contract Changes

| Endpoint                                                                                    | Field             | Before                       | After                                                                            | Breaking?                                                                                                                               |
| ------------------------------------------------------------------------------------------- | ----------------- | ---------------------------- | -------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| `GET /quarantine/browse/state`, `POST /quarantine/browse`, `POST /quarantine/browse/remove` | `folderPath` (B2) | Treated as the full key path | Treated as **root-relative**; the service prepends the bucket's `b2_path_prefix` | **No** — this _is_ the fix; no consumer could have worked with the old behaviour (Web is the only caller and sends root-relative paths) |
| same three                                                                                  | status codes      | —                            | + 500 `mirror-mapping-missing` when no mapping row exists                        | No (additive)                                                                                                                           |

## §9 Validations Changed

| ID  | Rule (Before)                                                        | Rule (After)                                                                                       | Test                                      |
| --- | -------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------- | ----------------------------------------- |
| V1  | n/a                                                                  | B2 `folderPath` is prefixed using the request bucket's `b2_path_prefix` before any repository call | `Test_V1_B2FolderPath_PrefixApplied`      |
| V2  | n/a                                                                  | Mapping row missing ⇒ `MappingMissing`; no repository/audit call                                   | `Test_V2_MappingMissing_NoSideEffects`    |
| V3  | n/a                                                                  | `fullPath` > 1000 chars ⇒ 422 validation failure                                                   | `Test_V3_FullPathTooLong_422`             |
| V4  | existing shape rules (no leading/trailing `/`, non-empty for writes) | unchanged, evaluated on the **incoming** root-relative value                                       | existing `Test_V1` of CAS-050 stays green |

## §10 Success Criteria

_Proposed — for review._

| ID   | Criterion                                                                                                                                              | Test                                      |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------- |
| SC-1 | With prefix `D/Shares/Operations`, folder add for `Ops/Audit` calls the repository with `D/Shares/Operations/Ops/Audit`                                | `Test_SC1_Add_UsesFullPath`               |
| SC-2 | Same for folder remove                                                                                                                                 | `Test_SC2_Remove_UsesFullPath`            |
| SC-3 | State for `folderPath=""` with prefix queries the prefix itself; with `Ops` queries `D/Shares/Operations/Ops`                                          | `Test_SC3_State_RootAndSubfolder`         |
| SC-4 | Audit row `FilePath` for folder add/remove is the full key path                                                                                        | `Test_SC4_Audit_FullPath`                 |
| SC-5 | File-scope routes and all other CAS-050 behaviour are unchanged (regression for CAS-050 SC-1..SC-9)                                                    | existing CAS-050 service tests stay green |
| SC-6 | Missing mapping ⇒ 500 `mirror-mapping-missing` on all three routes; no audit row                                                                       | `Test_SC6_MappingMissing_500`             |
| SC-7 | End-to-end (real SQL, non-empty prefix): state shows correct tri-state counts and folder add/remove succeed for a path returned by `GET /files/browse` | `Test_SC7_EndToEnd_PrefixedBucket`        |
| SC-8 | Response bodies and logs never contain the prefix (state response has leaf `folderName` only)                                                          | `Test_SC8_NoPrefixInResponse`             |

## §11 Security Notes

No B2 key or token is involved; `b2_path_prefix` is configuration data, not a secret, and is not returned to
the client. Authorization is unchanged (`CachetFullAccess` on the writes, `CachetAuthenticated` on state).
Audit is still written after the mutation and before the 200 (FR-23); only the recorded path value changes
(full key path — more precise, identical to file-scope rows). The new prefix lookup reads one mapping row; a
failure to read it must fail closed (500), never fall back to an unprefixed path, which could otherwise
match the wrong rows.

## §12 Rollback Plan

1. Revert the three Api files (service, enum, controller) and the tests; redeploy Api. No data or contract
   migration involved.
2. Database: N/A. 3. Config: N/A. 4. Task-queue: N/A.
3. Audit rows already written with full-path values remain valid; no cleanup needed.

## §13 Test Plan

| Class                                     | Method                                                                                   | Proves SC / V                |
| ----------------------------------------- | ---------------------------------------------------------------------------------------- | ---------------------------- |
| `QuarantineBrowseServiceTests`            | `Test_V1..V4`, `Test_SC1..SC4`, `Test_SC6`, `Test_SC8` (stubbed `IB2PathPrefixProvider`) | V1–V4, SC-1–SC-4, SC-6, SC-8 |
| `QuarantineBrowseControllerTests`         | `Test_SC6_MappingMissing_500`                                                            | SC-6 (HTTP)                  |
| `Api.IntegrationTests` quarantine tests   | `Test_SC7_EndToEnd_PrefixedBucket` (real SQL, mapping row with a non-empty prefix)       | SC-7                         |
| Existing CAS-050 service/repository tests | unchanged                                                                                | SC-5                         |

Coverage target: ≥ 85% on changed files.

## §14 Open Questions

- [x] **Q-A** (withdrawn 2026-10-05) Scope is B2 only; the feature is not concerned with `source = "ODrive"`.
      The service's existing source validation (CAS-050 V1) is left exactly as is. (The only "O-Drive" strings
      left in this spec are the BRD §12.3 item 2 title and the table name `odrive_bucket_mapping`; both are
      references, not behaviour.)
- [x] **Q-B** (resolved 2026-10-05, product owner) **Production prefix is never empty; it is static and
      fixed at `D/Shares/Operations`.** D-1 is therefore live in production: every folder action and state
      call is wrong today, and only empty-prefix test data hid it. The prefix is still read from
      `odrive_bucket_mapping.b2_path_prefix` (not hard-coded). No empty-prefix
      handling is specified anywhere in CAS-053. SC-1..SC-4 and SC-7 use `D/Shares/Operations`.
- [x] **Q-C** (resolved 2026-10-05) **Extract the helper now — the third-caller trigger has already fired.**
      The identical 3-line join exists in `FileBrowseService.cs:71-73` and `SearchResultBrowseService.cs:235-237`;
      `QuarantineBrowseService` would be the third copy. Per the spec's own P4 rule, add one static helper
      `B2KeyPath.Combine(prefix, folder)` in Core (`Core/Helpers/B2KeyPath.cs`), use it in the quarantine
      service, and replace the two existing inline joins. The helper has no empty-prefix branch
      (`folder.Length == 0 ? prefix : prefix + "/" + folder`), so the existing empty-prefix test cases in
      `FileBrowseServiceTests` / `SearchResultBrowseServiceTests` (and the blank-prefix case in
      `FilesBrowseRepositoryTests`) are **deleted** — the scenario is moot; all other cases stay green.
      The 1000-char guard stays per service (each maps it to a different outcome).

## §15 Sign-off

| Role          | Name                   | Date       | Notes                                                             |
| ------------- | ---------------------- | ---------- | ----------------------------------------------------------------- |
| Author        | Senior Systems Analyst | 2026-10-05 | draft                                                             |
| Product Owner | Rakesh Devarajan       |            | direction chosen 2026-10-05: prefix in the Api quarantine service |
| Reviewer      |                        |            |                                                                   |

## §16 Verification Plan

### What the Reviewer Will Check

- [ ] §1 matches code before the change (unprefixed `FolderPath` at lines 88/142/182)
- [ ] §2 implemented on all three routes; File scope untouched
- [ ] Only §5 files modified; §6 files (SPs, Web, `B2PathPrefixProvider`, DTOs) unchanged; `FileBrowseService` / `SearchResultBrowseService` change only by the `B2KeyPath.Combine` swap (§5, Q-C)
- [ ] Missing mapping fails closed; audit still precedes the 200 (FR-23)
- [ ] CAS-050 tests stay green; new tests cover a **non-empty** prefix (the gap that hid D-1)
- [ ] Afterwards: `/review CAS-051` re-run clears D-1

### Verification Report Format

`CAS-053.review.md` — Verdict, per-SC table, scope violations, blast radius, security gaps.

## Implementation Log

**Completed:** 2026-10-05
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 94-100% (changed files) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Core/Helpers/B2KeyPath.cs` | new |
| `Api/Services/QuarantineBrowseService.cs` | prefix + full path |
| `Api/Services/FileBrowseService.cs` | use B2KeyPath |
| `Api/Services/SearchResultBrowseService.cs` | use B2KeyPath |
| `Core/Interfaces/IQuarantineBrowseService.cs` | MappingMissing |
| `Api/Controllers/QuarantineController.cs` | map 500 |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `` | `` |  |
| `` | `` |  |
| `` | `` |  |
