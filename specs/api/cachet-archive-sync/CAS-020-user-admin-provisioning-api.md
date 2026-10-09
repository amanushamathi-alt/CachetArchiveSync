---
id: CAS-020
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-23
priority: P1
related_specs: [CAS-001, CAS-002, CAS-003, CAS-018, CAS-019]
touches:
  database: true
  api: true
  ui: false
  worker: false
  b2: true
  config: true
  security: true
migrations:
  - 20260923_160000__CAS-020-users-admin-crud.sql
---

# CAS-020 — User Admin Provisioning API (Add / Edit / Deactivate / Reactivate)

## §1 Summary

Adds the admin-only API surface for managing `dbo.users` rows: create (`POST`), edit
(`PUT` — password/B2 key rotation only, username is immutable), deactivate (`PATCH`,
soft `is_active=0`), and reactivate (`PATCH`, `is_active=1`). This implements the "provisioning
endpoint" concept named but never fully built by
`CACHET_Security_CredentialHandling_Analysis_v1.md` §2.2/§5 and
`CACHET_API_Design_Specification_v1.md` §3.3 (create only — FR-01–FR-03) plus a scope
**extension**, confirmed with Rakesh Devarajan on 2026-09-23, to also cover edit,
deactivate, and reactivate so the delivery team can fully manage accounts — including undoing an
accidental deactivation — without a DBA touching the table directly. No hard delete of a `users`
row is in scope (see §4).

## §2 User Story

As a **TraceCounts administrator**, I want to create, edit, deactivate, and reactivate CACHET user
accounts via a protected API endpoint (e.g. via Swagger), so that I can onboard/rotate credentials
for, retire, and restore the ≤4 named Cachet staff without any UI for user management (confirmed
non-goal — Security Analysis §6 item 4) and without direct database access.

## §3 In-Scope

- `POST /admin/users` — create a user row (username, password, B2 key ID, B2 key). The supplied
  B2 key/key ID is live-validated against Backblaze B2 (via the existing `IUserB2KeyAuthorizer`)
  before the row is persisted — see §6, §12.
- `PUT /admin/users/{userId}` — edit an existing user's password and/or B2 key ID/key (partial
  update — only supplied fields change). `username` is immutable after creation and is not
  accepted by this endpoint (§15 resolved). A supplied B2 key/key ID is live-validated the same
  way as on create.
- `PATCH /admin/users/{userId}/deactivate` — soft-deactivate (`is_active = 0`); does not delete
  the row.
- `PATCH /admin/users/{userId}/reactivate` — soft-reactivate (`is_active = 1`); undoes a
  deactivation without DB access (§15 resolved — symmetric with deactivate).
- A new admin-token gate (`X-Admin-Provisioning-Key` header, checked by an action filter/
  middleware) protecting all four endpoints, per Security Analysis §5 — this does not exist in
  the codebase yet and is a new build item, not a reuse of existing auth.
- Extending `ICredentialProtector` with a `Protect(string plaintext)` method (currently
  `Unprotect`-only) so the B2 key/key ID can be encrypted at write time using the same Data
  Protection purpose string (`Cachet.ArchiveSync.B2CredentialProtection`) `LoginService` already
  decrypts with.
- New stored procedures: `dbo.users_ins`, `dbo.users_upd`, `dbo.users_set_active` (shared by
  deactivate/reactivate — see §5).
- Password hashing at write time via the same `PasswordHasher<object>` (ASP.NET Core Identity)
  `LoginService` already uses to verify, so provisioned hashes are guaranteed compatible, with a
  minimum length of 8 characters enforced at the Api layer (§7 V2, §15 resolved).
- Audit log write (FR-23) for every create/edit/deactivate/reactivate, before the success response
  returns.
- These four endpoints are visible in Swagger (post-review amendment, 2026-09-23 — see §12);
  Security Analysis §5's original recommendation to hide them was reconsidered in favor of
  internal Swagger-based testing. The `X-Admin-Provisioning-Key` gate itself is unchanged.

## §4 Out-of-Scope

- **Hard delete of a `users` row.** Not requested, not BRD-confirmed, and inconsistent with
  FK references from `dbo.task_queue.submitted_by_user_id` and `dbo.audit_log.user_id`
  (CAS-003 §4) — a hard delete would either orphan or cascade-corrupt audit history. Deactivate/
  reactivate (soft `is_active` toggle) is the only removal/restoration mechanism in this spec.
- Any Blazor UI for user management — explicitly a non-goal (Security Analysis §6 item 4).
- Renaming a user (`username` edit) — immutable after creation (§15 resolved). If a username must
  change, deactivate the old row and create a new one.
- Rotating/replacing the static `X-Admin-Provisioning-Key` value itself, or any mechanism to
  manage _that_ token (it lives in server config/secrets per CAS-019, not in `dbo.users`).
- `session_token_version` handling on edit — editing a user's password does not itself force
  logout of an existing session in this spec (no requirement found for it); only CAS-002's
  existing logout/session-revocation path touches that column.

## §5 Database Objects

### Tables / Columns

No schema changes — reuses `dbo.users` exactly as defined in
`Database/CachetArchiveSync/Entities/dbo.users.sql` (CAS-003). All three new stored procedures
write to existing columns only (`username`, `password_hash`, `b2_application_key_id`,
`b2_application_key`, `is_active`, `created_by`/`created_at`, `updated_by`/`updated_at`).

### Stored Procedures

| Name                   | Schema | Action | Notes                                                                                                                                                                                                                                                                                                                                                                                                               |
| ---------------------- | ------ | ------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `dbo.users_ins`        | dbo    | ins    | Inserts a new row; `@username` must be unique (`UQ_users_username` — let the constraint violation surface as the 409 path, do not pre-check-then-insert). Sets `created_by`/`updated_by` to the acting admin identity, fixed constant `"admin-provisioning"` (§12, §15 resolved — mirrors `LoginService`'s `ActingProcessIdentity` pattern since the shared admin token carries no per-person identity).            |
| `dbo.users_upd`        | dbo    | upd    | Partial update by `@user_id` (`password_hash`, `b2_application_key_id`, `b2_application_key` only — `username` is not a parameter, §15 resolved). NULL-means-"leave unchanged" per-parameter — db-author to confirm this matches the nearest existing partial-update SP convention in the codebase before writing (read before writing, per Karpathy P1); if no such precedent exists, this SP sets the convention. |
| `dbo.users_set_active` | dbo    | upd    | Sets `is_active = @is_active` (bit), `updated_by`/`updated_at`, by `@user_id`. Shared by both deactivate (`@is_active = 0`) and reactivate (`@is_active = 1`) — one parameterized SP rather than two near-identical ones (Karpathy P4). Returns rows-affected so the Api can 404 on an unknown id. Idempotent: setting an already-matching value still returns success.                                             |

### Migration Strategy

Hybrid model, per project convention — db-author writes both:

- Idempotent timestamped migration: `Database/migrations/<YYYYMMDD_HHMMSS>__CAS-020-users-admin-crud.sql`
- Per-object mirror updates: `Database/CachetArchiveSync/Stored-Procedures/dbo.users_ins.sql`,
  `dbo.users_upd.sql`, `dbo.users_set_active.sql`

---

## §6 API Endpoints

### Endpoint: `POST /admin/users`

**Auth:** `X-Admin-Provisioning-Key` header (new action filter — no `[Authorize]` cookie policy;
this is a separate, non-session admin control per Security Analysis §5).
**Async:** No.

**Request:**

```json
{
  "username": "m.alvarez",
  "password": "a-strong-password",
  "b2ApplicationKeyId": "0043xxxxxxxxxxxxxxxxxxxx",
  "b2ApplicationKey": "K0043xxxxxxxxxxxxxxxxxxxxxxxxxxxx"
}
```

**Response 201:**

```json
{
  "userId": 100005,
  "username": "m.alvarez",
  "createdAt": "2026-09-23T14:02:11Z"
}
```

Never echoes back `password`, `b2ApplicationKeyId`, or `b2ApplicationKey` (Security Analysis §2.2, §6).

Before insert, the supplied `b2ApplicationKeyId`/`b2ApplicationKey` are live-validated against
Backblaze B2 via the existing `IUserB2KeyAuthorizer.AuthorizeAsync` (§12, §15 resolved) — the same
call `LoginService` makes at sign-in. A failed authorization is rejected with `422` rather than
persisted, so a mistyped B2 key never reaches the table.

**Status codes:**
| Code | Condition |
|------|-----------|
| 201 | Created |
| 400 | Validation failure (V1–V4) |
| 401 | `admin-token-invalid` — missing/incorrect `X-Admin-Provisioning-Key` |
| 409 | `username-already-exists` |
| 422 | `b2-key-invalid` — the supplied B2 key/key ID failed live authorization against Backblaze B2 |

### Endpoint: `PUT /admin/users/{userId}`

**Auth:** `X-Admin-Provisioning-Key` header.
**Async:** No.

**Request** (all fields optional — only supplied fields change; at least one required):

```json
{
  "password": "a-new-strong-password",
  "b2ApplicationKeyId": "0043yyyyyyyyyyyyyyyyyyyy",
  "b2ApplicationKey": "K0043yyyyyyyyyyyyyyyyyyyyyyyyyyyy"
}
```

`username` is immutable and is not accepted here (§15 resolved) — a rename must go through
deactivate + create, not this endpoint.

**Response 200:**

```json
{
  "userId": 100005,
  "username": "m.alvarez",
  "updatedAt": "2026-09-23T14:10:00Z"
}
```

Never echoes back `password`, `b2ApplicationKeyId`, or `b2ApplicationKey`.

A supplied `b2ApplicationKeyId`/`b2ApplicationKey` pair is live-validated the same way as on
create (§12) before the update is persisted.

**Status codes:**
| Code | Condition |
|------|-----------|
| 200 | Updated |
| 400 | Validation failure (V1–V4), or empty request body (V5) |
| 401 | `admin-token-invalid` |
| 404 | `user-not-found` — no row for `userId` |
| 422 | `b2-key-invalid` — the supplied B2 key/key ID failed live authorization against Backblaze B2 |

### Endpoint: `PATCH /admin/users/{userId}/deactivate`

**Auth:** `X-Admin-Provisioning-Key` header.
**Async:** No.

**Request body:** none.

**Response:** `204 No Content`.

**Status codes:**
| Code | Condition |
|------|-----------|
| 204 | Deactivated (idempotent — deactivating an already-inactive user still returns 204, not an error) |
| 401 | `admin-token-invalid` |
| 404 | `user-not-found` |

### Endpoint: `PATCH /admin/users/{userId}/reactivate`

**Auth:** `X-Admin-Provisioning-Key` header.
**Async:** No.

**Request body:** none.

**Response:** `204 No Content`.

**Status codes:**
| Code | Condition |
|------|-----------|
| 204 | Reactivated (idempotent — reactivating an already-active user still returns 204, not an error) |
| 401 | `admin-token-invalid` |
| 404 | `user-not-found` |

---

## §7 Validations

| ID  | Rule                                                                                                                                                                                                            | On Failure                            | Test                                             |
| --- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------- | ------------------------------------------------ |
| V1  | `username` required, non-empty, ≤ 50 chars (matches `NVARCHAR(50)` column)                                                                                                                                      | 400 `{ "type": "validation-failed" }` | `Test_V1_CreateUser_MissingUsername_Returns400`  |
| V2  | `password` required on create, minimum 8 characters, no other complexity rule enforced (§15 resolved)                                                                                                           | 400 `{ "type": "validation-failed" }` | `Test_V2_CreateUser_PasswordTooShort_Returns400` |
| V3  | `b2ApplicationKeyId` and `b2ApplicationKey` required together on create (both or neither is not valid on create — both required)                                                                                | 400 `{ "type": "validation-failed" }` | `Test_V3_CreateUser_MissingB2Key_Returns400`     |
| V4  | On edit (`PUT`), at least one of `password`/`b2ApplicationKeyId`+`b2ApplicationKey` must be supplied — an empty body is a no-op the caller almost certainly didn't intend                                       | 400 `{ "type": "validation-failed" }` | `Test_V4_EditUser_EmptyBody_Returns400`          |
| V5  | `b2ApplicationKeyId`/`b2ApplicationKey` on edit must be supplied together, same as create (V3); if `password` supplied on edit, same 8-character minimum as V2 applies                                          | 400 `{ "type": "validation-failed" }` | `Test_V5_EditUser_PartialB2Key_Returns400`       |
| V6  | On create or edit, a supplied `b2ApplicationKeyId`/`b2ApplicationKey` pair must pass live authorization against Backblaze B2 (`IUserB2KeyAuthorizer.AuthorizeAsync`) before the row is persisted (§15 resolved) | 422 `{ "type": "b2-key-invalid" }`    | `Test_V6_CreateUser_InvalidB2Key_Returns422`     |

---

## §8 UI / UX

N/A — explicitly no UI for user management (Security Analysis §6 item 4; confirmed non-goal, §4).

---

## §9 Configuration Changes

Reuses the existing `Auth:DataProtection:KeyRingPath` (CAS-002 §9). Adds one new config
key/env var, following the exact `Section__Key` convention CAS-019 established (§15 resolved —
CAS-019 does not itself enumerate an admin-token variable, only the connection string and B2
system key, so this extends that document's list rather than deviating from its naming pattern).
Update `docs/environment-variables.md` to add this row alongside the existing four.

| Key                     | Default                               | Description                                                                                                          | Env Override                                                         |
| ----------------------- | ------------------------------------- | -------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| `Admin:ProvisioningKey` | none — fails fast at startup if unset | Static admin/master token value checked against the `X-Admin-Provisioning-Key` request header (Security Analysis §5) | `Admin__ProvisioningKey` (never a literal value in appsettings.json) |

---

## §10 Success Criteria

| ID   | Criterion                                                                                                                                                                                                                                                                                                           | Test                                                        |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------- |
| SC-1 | `POST /admin/users` with a valid admin token and valid body creates a row in `dbo.users` with a bcrypt/PBKDF2 password hash (never plaintext) and encrypted (never plaintext) B2 key/key-ID columns                                                                                                                 | `Test_SC1_CreateUser_PersistsHashedAndEncryptedCredentials` |
| SC-2 | `POST /admin/users` response body never contains `password`, `b2ApplicationKeyId`, or `b2ApplicationKey` in any form                                                                                                                                                                                                | `Test_SC2_CreateUser_ResponseNeverEchoesSecrets`            |
| SC-3 | Any request to any of the three endpoints missing/wrong `X-Admin-Provisioning-Key` returns 401 without touching the database                                                                                                                                                                                        | `Test_SC3_MissingAdminToken_Returns401NoDbWrite`            |
| SC-4 | `PUT /admin/users/{userId}` with a new B2 key persists it encrypted with the same purpose string `LoginService` decrypts with, verified by a round-trip (edit then login succeeds against the new B2 key)                                                                                                           | `Test_SC4_EditUser_NewB2Key_DecryptsCorrectlyAtLogin`       |
| SC-5 | `PATCH /admin/users/{userId}/deactivate` sets `is_active=0`; a subsequent `POST /login` for that user fails with the existing `InvalidCredentials`/inactive-account path (CAS-002 §6.1 step 1), not a new failure mode                                                                                              | `Test_SC5_Deactivate_BlocksSubsequentLogin`                 |
| SC-6 | Every create/edit/deactivate/reactivate call writes exactly one audit log row (FR-23), written before the HTTP response returns, with `tab: "Admin"` (or the module name reviewer confirms matches existing `TabModule` conventions) and `created_by`/`updated_by` set to the fixed `"admin-provisioning"` identity | `Test_SC6_EachOperation_WritesAuditLogBeforeResponse`       |
| SC-7 | `username-already-exists` (409) is returned on a duplicate create without a distinguishable timing/error signature difference vs. a generic validation failure (avoid a secondary enumeration channel)                                                                                                              | `Test_SC7_DuplicateUsername_Returns409`                     |
| SC-8 | `PATCH /admin/users/{userId}/reactivate` sets `is_active=1` for a previously deactivated user; a subsequent `POST /login` succeeds again (assuming credentials are otherwise valid)                                                                                                                                 | `Test_SC8_Reactivate_RestoresLoginAbility`                  |
| SC-9 | `POST /admin/users` and `PUT /admin/users/{userId}` reject a B2 key/key ID pair that fails live `IUserB2KeyAuthorizer.AuthorizeAsync` authorization with `422 b2-key-invalid`, and the row is never persisted/updated (no partial write)                                                                            | `Test_SC9_InvalidB2Key_RejectsWithNoPartialWrite`           |

---

## §11 Telemetry

```csharp
_logger.LogInformation("Admin {Action} succeeded for user {UserId}", action, userId);
_logger.LogWarning("Admin {Action} failed: {Reason}", action, failureReason);
```

- Never log: `password`, `b2ApplicationKeyId`, `b2ApplicationKey` (raw or ciphertext), or the
  `X-Admin-Provisioning-Key` value itself — same rule `UserB2KeyAuthorizerTests` already enforces
  for the B2 authorization path; extend it here.

---

## §12 Security Notes

This spec is the single highest-sensitivity surface in the codebase after login itself — it is the
only place a raw B2 key is ever accepted as input (Security Analysis §1, §2.2).

### B2 Credential Handling

The raw `b2ApplicationKeyId`/`b2ApplicationKey` values arrive in the request body and, before
anything is persisted, are passed to `IUserB2KeyAuthorizer.AuthorizeAsync` (the same interface
`LoginService` calls at sign-in) for live validation against Backblaze B2 — a failed authorization
returns `422 b2-key-invalid` and nothing is written to `dbo.users` (SC-9). Once validated, the raw
values are immediately passed to `ICredentialProtector.Protect(...)` (new method — see §3), and
only the ciphertext is persisted or held past the request. Neither the raw nor ciphertext value is
ever logged, echoed in a response, or held in any object longer than the scope of the single
request. Never sent to the browser at any other point (FR-03). This makes the live B2 call the one
place besides login where the raw key briefly exists in process memory — same handling discipline
`UserB2KeyAuthorizerTests` already enforces (never logs key or response body) applies here too.

### Audit Trail

Each of create/edit/deactivate/reactivate writes one audit log row before the response returns
(FR-23), following the same before-response ordering `LoginService`/
`QuarantinePermanentDeleteService` already use. `CreatedBy`/`UpdatedBy` on the audit row (and on
`dbo.users` itself for create/edit) is the fixed constant `"admin-provisioning"` (§15 resolved) —
mirroring `LoginService`'s `ActingProcessIdentity = "api-svc"` pattern, since the shared admin
token carries no per-person identity to record.

### Session / Auth Requirements

No cookie/session auth applies to these four endpoints — a distinct `X-Admin-Provisioning-Key`
header, validated by a new action filter/middleware before the request body is processed (mirrors
Security Analysis §5 exactly), checked against the `Admin:ProvisioningKey` config value (§9). This
token is static server configuration, never derived from or equal to any user's own login
credential, and is never stored in `dbo.users`.

**Swagger visibility — post-review amendment, 2026-09-23 (confirmed with Rakesh Devarajan,
TraceCounts):** these endpoints were originally excluded from public Swagger "Try it out" via
`[ApiExplorerSettings(IgnoreApi = true)]` (§15 resolved, Security Analysis §5's recommendation).
That attribute has since been removed so the four endpoints are visible/callable from Swagger for
internal testing. This reduces *discoverability* only — the `AdminProvisioningTokenFilter` gate
(above) is unaffected and still enforces the same `X-Admin-Provisioning-Key` check regardless of
Swagger visibility, so authorization posture is unchanged. Anyone who can reach the Swagger UI for
this Api instance can now see these routes exist (verb/route/request shape), but still cannot call
them successfully without the correct admin token.

**Swagger "Authorize" wiring — follow-up (2026-09-23):** making the endpoints visible surfaced a
second gap — `AdminProvisioningTokenFilter` reads `X-Admin-Provisioning-Key` directly off the raw
request headers rather than as a declared action parameter, so Swagger UI had no input field for
it at all; every "Try it out" call sent no header and 401'd regardless of whether the App Pool
config was correct. Added `Swagger/AdminProvisioningKeyOperationFilter.cs`, an `IOperationFilter`
that adds an API-key security requirement scoped only to actions carrying
`[ServiceFilter(typeof(AdminProvisioningTokenFilter))]` (i.e. exactly `UsersAdminController`'s
four endpoints — no other controller is affected), plus the corresponding
`AddSecurityDefinition` in `Program.cs`. This gives Swagger UI an **Authorize** button where the
admin token is entered once and attached to subsequent "Try it out" calls to these four
endpoints only.

---

## §13 Test Plan

### Unit Tests

| Class                                    | Method                                                      | Proves                           |
| ---------------------------------------- | ----------------------------------------------------------- | -------------------------------- |
| `UserProvisioningServiceTests`           | `Test_SC1_CreateUser_PersistsHashedAndEncryptedCredentials` | SC-1                             |
| `UserProvisioningServiceTests`           | `Test_SC2_CreateUser_ResponseNeverEchoesSecrets`            | SC-2                             |
| `UserProvisioningServiceTests`           | `Test_V1_CreateUser_MissingUsername_Returns400`             | V1                               |
| `UserProvisioningServiceTests`           | `Test_V2_CreateUser_PasswordTooShort_Returns400`            | V2                               |
| `UserProvisioningServiceTests`           | `Test_V3_CreateUser_MissingB2Key_Returns400`                | V3                               |
| `UserProvisioningServiceTests`           | `Test_V4_EditUser_EmptyBody_Returns400`                     | V4                               |
| `UserProvisioningServiceTests`           | `Test_V5_EditUser_PartialB2Key_Returns400`                  | V5                               |
| `UserProvisioningServiceTests`           | `Test_V6_CreateUser_InvalidB2Key_Returns422`                | V6                               |
| `DataProtectionCredentialProtectorTests` | `Test_ProtectThenUnprotect_RoundTrips`                      | New `Protect` method correctness |
| `AdminProvisioningTokenFilterTests`      | `Test_SC3_MissingAdminToken_Returns401NoDbWrite`            | SC-3                             |

### Integration Tests

| Class                       | Method                                                | Proves |
| --------------------------- | ----------------------------------------------------- | ------ |
| `UsersAdminControllerTests` | `Test_SC4_EditUser_NewB2Key_DecryptsCorrectlyAtLogin` | SC-4   |
| `UsersAdminControllerTests` | `Test_SC5_Deactivate_BlocksSubsequentLogin`           | SC-5   |
| `UsersAdminControllerTests` | `Test_SC6_EachOperation_WritesAuditLogBeforeResponse` | SC-6   |
| `UsersAdminControllerTests` | `Test_SC7_DuplicateUsername_Returns409`               | SC-7   |
| `UsersAdminControllerTests` | `Test_SC8_Reactivate_RestoresLoginAbility`            | SC-8   |
| `UsersAdminControllerTests` | `Test_SC9_InvalidB2Key_RejectsWithNoPartialWrite`     | SC-9   |

### Coverage Target

Changed files must reach ≥ 85% line coverage.

---

## §14 Rollout

1. Deploy database migration first (new stored procedures only — no table/column change, so this
   is additive and low-risk).
2. Deploy Api (new controller, service, filter, `ICredentialProtector.Protect`). No Web/Worker
   changes required (touches.ui: false, touches.worker: false).
3. Rollback: drop the three new stored procedures and remove the controller/service — no column
   data to roll back since no schema change was made.

---

## §15 Open Questions

All open questions from the original draft were resolved with Rakesh Devarajan on 2026-09-23:

- **Reactivate endpoint**: added (`PATCH /admin/users/{userId}/reactivate`) — see §3, §6, §10 SC-8.
- **Username edit**: immutable after creation — not accepted by `PUT` (§4, §6).
- **Audit/row identity string**: fixed constant `"admin-provisioning"` (§5, §12).
- **Admin token config key**: `Admin:ProvisioningKey` / `Admin__ProvisioningKey`, extending
  CAS-019's naming convention (§9).
- **Password policy**: minimum 8 characters, no other complexity rule (§7 V2).
- **Live B2 validation at provisioning**: yes — `IUserB2KeyAuthorizer.AuthorizeAsync` is called on
  create and on edit-with-new-key, before persisting; failure returns `422 b2-key-invalid` (§6,
  §7 V6, §10 SC-9, §12).
- **Swagger visibility**: `[ApiExplorerSettings(IgnoreApi = true)]` on the controller (§12).

No open questions remain blocking `status: ready`.

---

## §16 Sign-off

| Role     | Name | Date | Notes |
| -------- | ---- | ---- | ----- |
| Author   |      |      |       |
| Reviewer |      |      |       |

---

## §17 Graph Impact

### God Nodes in Blast Radius

None identified — `ICredentialProtector`/`DataProtectionCredentialProtector` and `IUserRepository`/
`UserRepository` are low-degree nodes (graphify `explain "UserAccount"` reported degree 0 for the
DTO itself; `query` on the B2-key-authorization neighborhood returned 22 nodes, none with unusually
high edge count).

### Communities / Modules Affected

- **Module:** Auth (extends the CAS-002 login/credential-handling area with a new, separate
  admin-only surface).
- **Projects:** Api only (`Cachet.ArchiveSync.Api` controller/service/repository additions,
  `Cachet.ArchiveSync.Core` interface additions for `IUserRepository`/`ICredentialProtector`). No
  changes to Web, Worker, or existing Core DTOs beyond adding methods to existing interfaces.

### Surprising Connections

- `ICredentialProtector` currently has no `Protect` method at all (`Unprotect`-only) — this spec is
  the first caller that needs to encrypt rather than decrypt, so the interface itself must grow,
  not just gain a new implementation caller.
- `IUserRepository` currently has no insert/update methods (`GetByUsernameAsync`, `GetByIdAsync`,
  `GetSessionTokenVersionAsync`, `IncrementSessionTokenVersionAsync` only) — this spec is the first
  writer to `dbo.users` in the codebase; CAS-003's migration created the table but no application
  code has inserted a row before.
- No admin-token gate/middleware exists anywhere in the codebase yet, despite being named in the
  Security Analysis since the original design pass — this spec is the first implementation of it.
- `IUserB2KeyAuthorizer` (currently called only from `LoginService`) gains a second caller here for
  provisioning-time validation — confirm this doesn't introduce a circular or awkward dependency
  between the (new) provisioning service and the Auth module during implementation.

### Graphify Queries Run

```
python -m graphify query "user provisioning create edit deactivate admin endpoint B2 key encryption" --budget 2000
python -m graphify explain "UserAccount"
```

---

## §18 Verification Plan

### Reviewer Checklist

The reviewer will independently verify each of the following. Mark N/A only if the section itself is N/A.

- [ ] All §10 Success Criteria have a passing test
- [ ] All §7 Validations are implemented and tested
- [ ] All §6 API endpoints exist with correct verb/route/auth
- [ ] §12 Security Notes are implemented as described (B2 key never browser-visible; audit log
      write happens before success response; correct session/auth requirement enforced — admin
      token, not cookie auth, on all three endpoints)
- [ ] §5 Database objects exist in schema (if database: true)
- [ ] §9 Config keys are present in appsettings.json (env-var-only per CAS-019 pattern — no literal
      secret value committed)
- [ ] §11 Telemetry events are emitted (no B2 key / password / admin token in logs)
- [ ] §17 Graph Impact matches actual code changes (no unintended nodes)
- [ ] No out-of-scope changes in §4 were made (no hard delete, no UI, no `/reactivate` endpoint)

### Verification Report Format

The reviewer writes `CAS-020.review.md` with:

- Overall verdict: APPROVED | APPROVED WITH NOTES | CHANGES REQUIRED | BLOCKED
- Per-SC verdict table
- Security gap list (if any)
- Scope violations (if any)

## Implementation Log

**Completed:** 2026-09-23
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 68.2% overall; 7/8 originally-flagged files at 100%; Repositories/UserRepository.cs (4.3%) is a pre-existing sandbox-wide limitation shared by every Dapper repository (no live SQL Server for [Fact(Skip=SkipReason)] DB tests); Data/UserRepository.cs Web stub explicitly out of scope | Endpoints ✓

### Files Changed

| File                                                                       | Change                                                                                          |
| -------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| `src/Cachet.ArchiveSync.Core/Dtos/CreateUserRequest.cs`                    | New DTO - POST /admin/users request body                                                        |
| `src/Cachet.ArchiveSync.Core/Dtos/EditUserRequest.cs`                      | New DTO - PUT /admin/users/{userId} request body                                                |
| `src/Cachet.ArchiveSync.Core/Dtos/CreateUserResponse.cs`                   | New DTO - 201 response, never echoes secrets                                                    |
| `src/Cachet.ArchiveSync.Core/Dtos/EditUserResponse.cs`                     | New DTO - 200 response, never echoes secrets                                                    |
| `src/Cachet.ArchiveSync.Core/Interfaces/IUserProvisioningService.cs`       | New service interface + outcome/result records                                                  |
| `src/Cachet.ArchiveSync.Api/Services/UserProvisioningService.cs`           | Create/edit/deactivate/reactivate logic, V1-V6 validations, live B2 auth, audit-before-response |
| `src/Cachet.ArchiveSync.Api/Filters/AdminProvisioningTokenFilter.cs`       | New IAsyncActionFilter gating X-Admin-Provisioning-Key                                          |
| `src/Cachet.ArchiveSync.Api/Controllers/UsersAdminController.cs`           | New controller - 4 endpoints, IgnoreApi=true                                                    |
| `src/Cachet.ArchiveSync.Core/Interfaces/ICredentialProtector.cs`           | Added Protect(string) method                                                                    |
| `src/Cachet.ArchiveSync.Api/Services/DataProtectionCredentialProtector.cs` | Implemented Protect(string) using same purpose string as Unprotect                              |
| `src/Cachet.ArchiveSync.Core/Interfaces/IUserRepository.cs`                | Added InsertAsync/UpdateAsync/SetActiveAsync                                                    |
| `src/Cachet.ArchiveSync.Api/Repositories/UserRepository.cs`                | Implemented InsertAsync/UpdateAsync/SetActiveAsync via dbo.users_ins/upd/set_active             |
| `src/Cachet.ArchiveSync.Core/Enums/AuditAction.cs`                         | Added UserCreated/UserUpdated/UserDeactivated/UserReactivated                                   |
| `src/Cachet.ArchiveSync.Api/Program.cs`                                    | DI registration, Admin:ProvisioningKey fail-fast check                                          |
| `src/Cachet.ArchiveSync.Web/Data/UserRepository.cs`                        | NotSupportedException stubs for new interface members                                           |
| `docs/environment-variables.md`                                            | Added Admin\_\_ProvisioningKey row                                                              |
| `src/Cachet.ArchiveSync.Api/appsettings.Development.json`                  | Added Admin:ProvisioningKey placeholder dev value                                               |

### Tests Added

| Class                               | Method                                                      | Covers |
| ----------------------------------- | ----------------------------------------------------------- | ------ |
| `UserProvisioningServiceTests`      | `Test_SC1_CreateUser_PersistsHashedAndEncryptedCredentials` | SC-1   |
| `UserProvisioningServiceTests`      | `Test_SC2_CreateUser_ResponseNeverEchoesSecrets`            | SC-2   |
| `AdminProvisioningTokenFilterTests` | `Test_SC3_MissingAdminToken_Returns401NoDbWrite`            | SC-3   |
| `UsersAdminControllerTests`         | `Test_SC4_EditUser_NewB2Key_DecryptsCorrectlyAtLogin`       | SC-4   |
| `UsersAdminControllerTests`         | `Test_SC5_Deactivate_BlocksSubsequentLogin`                 | SC-5   |
| `UsersAdminControllerTests`         | `Test_SC6_EachOperation_WritesAuditLogBeforeResponse`       | SC-6   |
| `UsersAdminControllerTests`         | `Test_SC7_DuplicateUsername_Returns409`                     | SC-7   |
| `UsersAdminControllerTests`         | `Test_SC8_Reactivate_RestoresLoginAbility`                  | SC-8   |
| `UsersAdminControllerTests`         | `Test_SC9_InvalidB2Key_RejectsWithNoPartialWrite`           | SC-9   |
| `UserProvisioningServiceTests`      | `Test_V1_CreateUser_MissingUsername_Returns400`             | V-1    |
| `UserProvisioningServiceTests`      | `Test_V2_CreateUser_PasswordTooShort_Returns400`            | V-2    |
| `UserProvisioningServiceTests`      | `Test_V3_CreateUser_MissingB2Key_Returns400`                | V-3    |
| `UserProvisioningServiceTests`      | `Test_V4_EditUser_EmptyBody_Returns400`                     | V-4    |
| `UserProvisioningServiceTests`      | `Test_V5_EditUser_PartialB2Key_Returns400`                  | V-5    |
| `UserProvisioningServiceTests`      | `Test_V6_CreateUser_InvalidB2Key_Returns422`                | V-6    |

### Migration

`20260923_160000__CAS-020-users-admin-crud.sql`
