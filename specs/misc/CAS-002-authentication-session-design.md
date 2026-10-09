---
id: CAS-002
target_app: cachet-archive-sync
status: merged
owner: devarajan@tracecounts.org
created: 2026-09-10
priority: P1
related_specs: [CAS-001, CAS-003]    # CAS-001 §6.1/§12 explicitly defer the policy-name/session
                                      # mechanics decided here; this document also revises one of
                                      # CAS-001's own points (§0 Reconciliation Note). CAS-003
                                      # (added 2026-09-10, later same day) is the spec that now
                                      # actually creates dbo.users, referenced throughout §5/§6.1.
touches:
  database: true                     # dbo.users_get_by_username SP added during /implement — see
                                      # §5 update below; no new table, dbo.users itself unchanged
  api: true                          # session middleware around the already-specified §3 endpoints
  ui: true                           # Blazor Server auth-state/circuit integration only — screen
                                      # layout itself is already specified (UI/UX spec §2)
  worker: true                       # task ownership vs. B2-key-resolution reconciliation, §6.4
  b2: true                           # session -> capability -> B2 authorization mapping
  config: true                       # cookie/session config keys
  security: true
migrations:
  - 20260922_161500__CAS-002-users-get-by-username.sql
  - 20260922_163000__CAS-002-session-token-version.sql
---

# CAS-002 — Authentication and Session Design

WBS Task 11 (Design phase). Implements FR-01, FR-02, FR-03; architecture basis in BRD Section
10.3, Section 12.1 item 8 (resolved decision log).

---

## §0 Dependency Note and Scope Correction (read this first)

**This design was requested against a stale framing of the problem.** The task brief that
initiated this spec described the login method as "TBD" / "still an open decision" and asked
for a design that keeps username/password and SSO interchangeable. That framing predates BRD
v2.2 (2026-09-09) and no longer matches the source-of-truth documents in this repo:

- **BRD §12.1, item 8** (resolved, client-confirmed 2026-09-09): the login method is
  **username/password only**. SSO against an identity provider was considered and not chosen.
  This is no longer an open item under §12.2 — it moved to the resolved log the same day it was
  raised.
- The **current WBS Task 11 prompt** (`1.WBS/CACHET_WBS_v1.md`, row 19) states explicitly: *"The
  login method is confirmed as username/password against a per-user account... this task designs
  the concrete session/token mechanics, not the method choice."*
- `CACHET_Security_CredentialHandling_Analysis_v1.md` (WBS Task 4) already fixed the account
  model (`Users` table, max 4 rows, one B2 key pair per user) and the B2-brokering shape.
- `CACHET_API_Design_Specification_v1.md` §3/§3.4 (WBS Task 7) already specifies the `POST
  /login`/`POST /logout`/`POST /admin/users` contracts and a sliding-expiration cookie decision,
  explicitly deferring only the **exact TTL number** to this document.

Per Karpathy P4 (no new abstraction until the pattern is actually needed) and P2 (spec is the
single source of truth), **this document does not build a pluggable/swappable
username-password-or-SSO provider abstraction.** Doing so would add interface and configuration
surface for a second login method the client explicitly declined, on a 4-user internal tool with
no identity provider anywhere else in scope. If SSO is reconsidered later, that is a new
client-confirmed decision requiring its own spec change — not something to speculatively build
room for today. (Confirmed with the requester before drafting — see conversation record.)

**What this document actually is:** the WBS Task 11 deliverable — the session/token mechanics,
sliding-expiration behavior, session-to-B2-capability mapping, and Web↔Api session-forwarding
design that the two upstream documents above explicitly left for this task, consolidated into one
place a developer can implement directly from.

**Prerequisite specs that don't exist as `CAS-00X` documents yet:** the database schema (WBS Task
6) and API design (WBS Task 7) exist only as `0.Specifications/*.md` analysis/design documents,
not as CAS-### spec entries — same situation CAS-001 §0 already noted. This document cites
them directly by section rather than by spec ID.

**Update (2026-09-10, later same day):** the database schema (WBS Task 6) is now `CAS-003`, which
creates `dbo.users` exactly as described in §5 below. API design (WBS Task 7) still exists only as
`CACHET_API_Design_Specification_v1.md`, not as a spec entry.

---

## §1 Summary

Defines the concrete session mechanics behind the application-level login that replaces the old
"enter your B2 key" screen: how `POST /login`'s two-stage check (username/password, then live B2
key validation) issues a session; how that session is represented as a cookie and renewed;
what happens on logout or session lapse; and how an authenticated session maps to permission to
use the server-brokered B2 key (FR-03) and to specific B2-write actions (Permanent Delete, Purge,
Copy, Update). Also defines how a Blazor Server (Web) circuit and its calls into the Api project
carry that same session, since this project's two-project split (Api / Web) is otherwise silent on
how a session crosses that internal boundary. Implements FR-01, FR-02, FR-03.

---

## §2 User Story

As a **TraceCounts staff member using the CACHET Archive Files Search and Sync application**, I
want to sign in once with my username and password, have my session stay active while I'm working
without repeated re-logins, and have the application use my Backblaze B2 access on my behalf
without ever showing me or the browser the raw key — so that I can search, review, and act on
archive files for as long as a session reasonably lasts, and so that a lapsed session never leaks
credential material or silently escalates my access.

---

## §3 In-Scope

- `POST /login` session-issuance mechanics: what the session/cookie contains, how it's produced
  from the two-stage check already specified in API spec §3.1 and Security Analysis §9.
- Cookie-based session implementation: `HttpOnly`/`Secure`/`SameSite` attributes, sliding
  expiration window and its concrete duration, and what happens when the window lapses.
- `POST /logout` session invalidation mechanics.
- The capability label (`Full Access` / `Read Only`) as a session claim: how it's derived once at
  login, carried in the session, and never re-derived or cached elsewhere.
- Mapping from session claims to ASP.NET Core authorization policies, and which endpoints/actions
  require which policy (reconciling CAS-001 §12's request for a concrete policy name).
- How a Blazor Server (Web) circuit obtains and holds the authenticated user's identity, and how
  Web's server-side code forwards that identity when calling Api endpoints.
- How the session model interacts with the task queue: what `GET /tasks`' own-tasks-only scoping
  needs from the session (API spec §7.1), and reconciling how a background Worker task's B2 access
  is resolved once the originating session may have expired (FR-20) — see §0's revision to
  Security Analysis §2.4, made explicit in §6.5 below.
- Session/token TTL: resolving the "exact TTL" API spec §3.4 explicitly deferred here.

---

## §4 Out-of-Scope

- **Login method choice** — resolved (§0); this document does not revisit username/password vs.
  SSO.
- **`Users` table schema** — already defined, DB Schema Design §4. No changes proposed here.
- **`POST /login`/`POST /logout`/`POST /admin/users` request/response contracts** — already fully
  specified, API spec §3.1–§3.3. This document does not restate or change those shapes; it defines
  the session mechanism *behind* them.
- **Provisioning endpoint's admin-token protection mechanics** — already specified, Security
  Analysis §5, API spec §3.3. Out of scope here; this is a separate, lower-frequency
  administrative concern from ordinary user sessions.
- **Sign-In screen layout, field validation copy, error message text** — already specified, UI/UX
  spec §2.1/§2.2. This document only adds how the Blazor circuit consumes the resulting session,
  not how the form looks or what it says on failure.
- **The two operational-vs-per-user B2 key question's *B2 API mechanics*** (which B2 operations
  are called, request/response shapes) — that belongs to CAS-001 §6.1–§6.4. This document only
  resolves *which key the session model implies should be used where*, since that's a session-
  design question CAS-001 explicitly left to this task (CAS-001 §12).
- **Drive encryption-at-rest (BRD §12.2, remaining open item)** — unrelated to this document's
  scope; not depended upon here.

---

## §5 Database Objects

No new tables or columns. `dbo.users` (DB Schema Design §4) already holds everything this design
needs: `user_id`, `username`, `password_hash`, `b2_application_key_id`, `b2_application_key`
(ciphertext), `is_active`. This design is a **cookie-based** session model (§6.2) — the session
itself is a self-contained, server-signed/encrypted cookie, not a row in a database table, so no
`Sessions` table is introduced. `dbo.task_queue.submitted_by_user_id` (DB Schema Design §6)
already exists and is sufficient for §6.5's task-ownership model.

**Added during `/implement` (2026-09-22):** CAS-003 created only generic CRUD procs for
`dbo.users`, none of which look up a row by `username` — the one lookup §6.1 step 1 actually
needs. One new stored procedure:

- `dbo.users_get_by_username` — `@username NVARCHAR(...)` in; returns the single matching row
  (`user_id`, `username`, `password_hash`, `b2_application_key_id`, `b2_application_key`,
  `is_active`, `session_token_version` — see below), or zero rows if no match. Read-only.

**Added during `/implement` (2026-09-22), §6.3a fix:** one new column and one new stored
procedure, to make `POST /logout` actually revoke a session server-side (§6.3/§6.3a):

- `dbo.users.session_token_version INT NOT NULL DEFAULT 0` — new column. Incremented by 1 on
  every logout for that user; embedded in the `session_ver` login claim (§6.1) and re-checked on
  every request (§6.3a).
- `dbo.users_get_session_token_version` — `@user_id INT` in; returns the single current
  `session_token_version` value for that user. Read-only, indexed point lookup, called from
  `OnValidatePrincipal` (§6.3a) on every authenticated request.
- `dbo.users_increment_session_token_version` — `@user_id INT` in; increments that user's
  `session_token_version` by 1. Called from `POST /logout` (§6.3), same transaction as the
  audit-log write.

---

## §6 Session Design

### §6.1 Login-Time Session Issuance (builds on API spec §3.1, Security Analysis §9)

`POST /login`'s two-stage check is already fully specified elsewhere; this section defines only
what happens *after* both checks succeed:

1. Look up the `dbo.users` row by `username`; verify `password_hash` (Security Analysis §2.1).
2. Decrypt that user's `b2_application_key_id`/`b2_application_key` (application-layer decryption,
   key material held outside the database — Security Analysis §2.1) and call B2's
   `b2_authorize_account` to validate the key live and retrieve its capability set (Security
   Analysis §9).
3. **On success**, derive a single capability label from the returned `allowed.capabilities` list:
   `"Full Access"` if the list includes `writeFiles`/`deleteFiles`; otherwise `"Read Only"`.
   **(design decision — not BRD-specified, but the only two labels the UI shows, UI/UX spec §1.1):**
   any capability set that includes write/delete access maps to `Full Access`; every other
   combination maps to `Read Only`. This is a two-value simplification of B2's finer-grained
   capability list, matching FR-02's stated header badge values exactly.
4. Issue an ASP.NET Core cookie-authentication ticket (`AuthenticationScheme = "CachetAuth"`)
   carrying exactly these claims:

   | Claim | Value | Notes |
   |---|---|---|
   | `ClaimTypes.NameIdentifier` | `user_id` (integer, as string) | The only identity value background work needs (§6.5). |
   | `ClaimTypes.Name` | `username` | For display and audit-log attribution. |
   | `"b2_capability"` (custom) | `"Full Access"` \| `"Read Only"` | Drives §6.4's authorization policies and the FR-02 header badge. Derived once, at login, from the live B2 call — **never** re-derived from a cached value, and never persisted to `dbo.users` (DB Schema Design §4's "Explicitly not a column" note already establishes this). |
   | `"session_ver"` (custom) | `dbo.users.session_token_version` at login time (integer, as string) | Added during `/implement`, §6.3a. Re-checked against the live DB value on every request; a mismatch (set by logout) rejects the principal. |

   **No claim ever holds `b2_application_key_id`, `b2_application_key`, or `password_hash`, at
   any point.** This is the same invariant Security Analysis §4/§13 already states for every
   browser-reachable surface; a claims-bearing cookie is a browser-reachable surface, so it is
   bound by the same rule.

5. Return the `200 OK` body already specified in API spec §3.1 (`userId`, `displayName`,
   `b2AccessLabel`); the cookie itself travels via the `Set-Cookie` response header, `HttpOnly`,
   never as a JSON field (already required, API spec §3.1).

### §6.2 Cookie Mechanics

**(RESOLVED 2026-09-09 in API spec §3.4 — restated here as the binding mechanism, with the
deferred number now fixed.)**

- **Type:** ASP.NET Core `CookieAuthenticationHandler`, self-contained (claims encrypted/signed
  into the cookie via ASP.NET Core Data Protection) — no server-side session-store table.
- **Attributes:** `HttpOnly = true`, `Secure = true` (TLS-only, per Security Analysis §5/§15),
  `SameSite = Strict` **(design decision — not BRD-specified):** Strict is safe and sufficient
  here since the Blazor Server UI never needs the cookie sent on a cross-site top-level navigation
  or embed — there is no third-party integration or email-link entry point into this application.
- **Expiration model:** sliding expiration. `ExpireTimeSpan = 8 hours`, `SlidingExpiration = true`.
  **Resolved 2026-09-10 (this document): 8 hours is adopted as the concrete value**, not just the
  proposed default API spec §3.4 left open — a plain, round number matching a single working day
  for TraceCounts' ≤4 named staff (BRD Section 8), renewed transparently on every authenticated
  request. No numeric business requirement drives a different figure; if usage patterns later show
  8 hours is wrong in practice, it is a one-line configuration change (§9), not a design change.
- **No `POST /refresh` endpoint** (API spec §3.4 final) — the cookie-authentication middleware
  renews the sliding window automatically; the Blazor Server client and any Api caller never call
  anything to keep a session alive.
- **On window lapse:** the next request's cookie fails validation; ASP.NET Core's
  `CookieAuthenticationEvents.OnRedirectToLogin` (for Web's own pages) or a plain `401` (for direct
  Api calls) results. The user re-authenticates via the existing `POST /login` (§6.1) — no new
  endpoint shape, matching API spec §3.4.

### §6.3 Logout (builds on API spec §3.2)

**Corrected during `/implement` (2026-09-22) — see §6.3a.** `POST /logout` calls
`HttpContext.SignOutAsync("CachetAuth")`, which clears the cookie client-side (instructs the
browser to discard it), **and** bumps `dbo.users.session_token_version` (§6.3a) so a captured
pre-logout cookie is rejected on its next use, not just after the 8-hour sliding window lapses.
Audit row (`action: "Logout"`, `tab: "Auth"`) is written before the `204` response, matching API
spec §3.2. No B2-side action occurs on logout — there is nothing to revoke, since no B2 credential
is ever held client-side or in the session claims themselves (Security Analysis §3).

### §6.3a Server-Side Session Revocation (added during `/implement`, corrects §6.3's original claim)

**What was wrong:** the original §6.3 assumed `SignOutAsync` "invalidates the
Data-Protection-encrypted payload for future replay." Plain ASP.NET Core cookie authentication
does not do this — `SignOutAsync` only clears the browser's copy. A captured raw cookie value
remains valid, and accepted by the server, until its own sliding-expiration window naturally
lapses, regardless of logout. This was caught by `Test_SC3_LogoutInvalidatesSession` failing
end-to-end against the real implementation (login → logout → replay pre-logout cookie → `200 OK`,
not the spec-required `401`) — not a hypothetical, a reproduced defect.

**Fix — per-user session version, checked on every request:**
- `dbo.users` gains one column: `session_token_version INT NOT NULL DEFAULT 0` (DB Schema Design
  §4 supersession — see §5).
- At login (§6.1 step 4), the issued claims include one more: `"session_ver"` (custom claim) =
  the user's current `session_token_version` value at the moment of login.
- `POST /logout` (§6.3) increments `session_token_version` for that `user_id` by 1, in the same
  transaction as the audit-log write, before returning `204`.
- `CookieAuthenticationOptions.Events.OnValidatePrincipal` (registered identically in both Api's
  and Web's `Program.cs`, alongside the shared scheme/key-ring config from §6.6) reads the
  `session_ver` claim off the incoming principal, looks up the current
  `dbo.users.session_token_version` for that `user_id` (a cheap indexed point lookup — new SP,
  §5), and calls `context.RejectPrincipal()` if they don't match. A rejected principal behaves
  exactly like an expired one (§6.2's existing 401/redirect-to-login behavior) — no new failure
  path to design.
- **Why a DB column, not an in-memory store:** Api and Web are separate processes (§6.6) that must
  agree on revocation state without a shared-memory mechanism, and `dbo.users` is already the
  single source of truth both processes read at login. A distributed cache would add a new moving
  part (Karpathy P4) for a ≤4-user internal tool where the existing database round-trip on every
  request is already acceptable (NFRs, BRD Section 8 — no high-throughput requirement here).
- **Cost:** one extra indexed lookup per authenticated request. Acceptable at this project's scale
  (≤4 concurrent users); flagged here in case a future NFR tightens request-latency budgets enough
  to warrant caching this lookup.

### §6.4 Session → Authorization Policy Mapping (resolves CAS-001 §12's deferred item)

CAS-001 §12 states: *"`Full Access` is required to submit Permanent Delete, Purge, Copy, or
Update... This document assumes the login/session design (WBS task 11) resolves the concrete ASP.
NET Core policy name/implementation for this mapping."* Resolved here:

```csharp
// Program.cs (Api and Web both register these identically)
services.AddAuthorization(options =>
{
    options.AddPolicy("CachetFullAccess", policy =>
        policy.RequireClaim("b2_capability", "Full Access"));

    // Any authenticated session qualifies — Read Only is the floor, not a separate claim check.
    options.AddPolicy("CachetAuthenticated", policy =>
        policy.RequireAuthenticatedUser());
});
```

| Action class | Policy | Basis |
|---|---|---|
| Keyword Search, Content Scan, Quarantine add/release, `GET /tasks`, `GET /audit-log`, report generation | `CachetAuthenticated` | Any signed-in user; B2 read access is sufficient (CAS-001 §12). |
| Permanent Delete, Purge, Copy, Update | `CachetFullAccess` | CAS-001 §12's confirmed mapping — a `Read Only` session must never reach these actions. |
| `POST /admin/users` (provisioning) | *(none of the above — separate admin-token check, Security Analysis §5, API spec §3.3)* | Not a user session at all; explicitly out of scope here (§4). |

Applied at the controller-action level (`[Authorize(Policy = "CachetFullAccess")]`), consistent
with this project's `[Authorize]`-at-action-level convention (CLAUDE.md Skill Precedence table).
A request against a policy the session's capability doesn't satisfy returns `403`, distinct from
the `401` an unauthenticated request gets — this is an application-level authorization decision,
separate from (and in addition to) the fact that the underlying B2 call would itself fail if it
somehow reached B2 with insufficient key capability (CAS-001 §12's "belt and suspenders" framing).

### §6.5 Reconciliation Note: Task-Queue B2 Key Resolution (supersedes Security Analysis §2.4/§6 item 10, in part)

**This is the one place this document changes, rather than merely restates, an earlier
document.** Flagging it explicitly here per Karpathy P6 (auditability) rather than letting the
conflict sit silently between two specs.

- `CACHET_Security_CredentialHandling_Analysis_v1.md` §2.4 and §6 (requirement 10), dated
  2026-09-08, describe a model where **each background task resolves the submitting user's own
  B2 key** at execution time, independent of session liveness, so results are deliverable after a
  browser is closed (FR-20).
- `CAS-001` §6.1, dated 2026-09-10 (today, later than the analysis above), revises this: because a
  dequeued task has no attached session and threading a specific user's key through an unattended
  Worker job adds real complexity for no confirmed benefit, **all operational B2 calls (mirror
  population, cross-check, Copy/Update/Purge, Permanent Delete execution) use a single,
  separate, system-level B2 application key**, not the submitting user's own key. Only the
  **login-time validation call** (§6.1 above) still uses each user's individual stored key.
- **This document follows CAS-001's model, as the more recent, explicitly-reasoned decision.**
  `dbo.task_queue.submitted_by_user_id` (DB Schema Design §6) is **still required and still
  populated** — it remains load-bearing for three things this session design depends on: (1)
  `GET /tasks`' own-tasks-only scoping (API spec §7.1), (2) audit-log attribution (FR-23) for
  every action a task performs, and (3) the `CachetFullAccess` policy check that must happen
  **at submission time** (Api, when the session is still live), not at execution time (Worker,
  when it may not be) — consistent with CAS-001 §12's "checked at enqueue, not re-checked by the
  Worker" design.
- **Practical effect on this document's own scope:** a session's job is to (a) gate task
  *submission* through the right policy (§6.4) and (b) tag the task row with the submitting
  user's id for ownership/audit — not to hand the Worker a usable B2 credential. The Worker's own
  system-key usage is CAS-001's concern, not this document's.
- **Recommendation:** `CACHET_Security_CredentialHandling_Analysis_v1.md` §2.4 and §6 item 10
  should receive a dated addendum pointing to this section, the same way that document's own §9
  was added as an addendum rather than silently edited — keeps both documents' history intact and
  traceable.

### §6.6 Web ↔ Api Session Forwarding

**(proposed — not BRD-specified; a consequence of this project's own Api/Web project split, not a
requirement the BRD states.)** The BRD and UI/UX spec describe the browser calling endpoints like
`POST /login` and `POST /search` directly (API spec throughout), but this project's assumed
architecture (CLAUDE.md) splits those into a separate `Api` project from the Blazor Server `Web`
project, with Blazor Server component code executing entirely server-side and communicating with
the browser only over its SignalR circuit. Something has to decide how a signed-in user's identity
crosses that internal Web→Api boundary, and no existing document resolves it. This document
resolves it as follows, since CAS-001 and the API spec both implicitly assume *some* answer exists
here:

- **Web hosts the authentication cookie** (§6.1/§6.2) — the browser's only direct relationship is
  with Web. The Blazor Server circuit reads the authenticated user's identity via the standard
  `AuthenticationStateProvider`/`HttpContext.User` mechanism already built into ASP.NET Core's
  Blazor Server hosting model — no custom claims plumbing needed beyond §6.1's claim list.
- **Server-side Blazor code (never browser JS) calls Api** using a server-side `HttpClient`. That
  `HttpClient` forwards the current request's `Cookie` header onto the outgoing call to Api, and
  **Api validates the same cookie** — both projects share one ASP.NET Core Data Protection key
  ring (persisted to a location both processes can read, e.g., a shared filesystem path or a
  registered key-ring store) and one cookie-authentication scheme configuration (name, claims
  shape). This requires Web and Api to sit behind the same origin/reverse-proxy host (e.g.,
  `cachet.tracecounts.internal/` for Web, `cachet.tracecounts.internal/api/` for Api) so the
  forwarded cookie is valid for both.
- **Why this, and not a service-to-service token or a second auth scheme:** with ≤4 users and no
  external caller of Api ever expected (CLAUDE.md: "this project has exactly one application"),
  introducing OAuth client-credentials or a signed internal JWT would add a second credential
  type, a second set of expiry/rotation rules, and a second thing that must never leak — for a
  problem a shared cookie already solves at zero extra moving parts (Karpathy P4).
- **Consequence for `dotnet` implementation:** register the same
  `AddAuthentication().AddCookie("CachetAuth", ...)` call, with identical `CookieName` and a
  shared `IDataProtectionProvider` (via `.PersistKeysToFileSystem(...)` pointed at a shared path,
  or `.PersistKeysToDbContext(...)` against the same SQL Server instance — **(recommendation, pick
  one during WBS Task 15 implementation, not this design)** — in both `Cachet.ArchiveSync.Api`'s
  and `Cachet.ArchiveSync.Web`'s `Program.cs`.

---

## §7 API Endpoints

N/A for new endpoints — `POST /login`, `POST /logout`, `POST /admin/users` are already fully
specified in API spec §3.1–§3.3 and are not re-specified here (§4). This document defines the
authentication middleware and policy configuration (§6.4) that every *other* endpoint in the API
spec (§4–§9) implicitly relies on via its `[Authorize]`/`[Authorize(Policy = "...")]` attribute,
per API spec §1.2 ("per whichever the Authentication/session design, WBS Task 11, confirms").

---

## §8 UI / UX

Screen layout, fields, and copy are already fully specified (UI/UX spec §2.1–§2.3) and are not
restated here. The one addition this document makes at the UI layer: the Blazor Server
`MainLayout`/header component reads the `"b2_capability"` claim (§6.1) from the current
`AuthenticationState` to render the FR-02 badge (UI/UX spec §1.1's "B2 Access: Full Access" /
"B2 Access: Read Only") — it does not call a separate endpoint to fetch this value, since it is
already present in the session claims from login.

---

## §9 Configuration Changes

| Key | Default | Description | Env Override |
|---|---|---|---|
| `Auth:CookieName` | `CachetAuth` | Shared cookie name between Api and Web (§6.6). | Yes |
| `Auth:SlidingExpirationHours` | `8` | Session sliding-expiration window (§6.2). | Yes |
| `Auth:DataProtection:KeyRingPath` | *(none — must be set)* | Shared filesystem path (or equivalent store) both Api and Web point at for the shared Data Protection key ring (§6.6). Not a secret itself, but must be a location only the application server account can write to. | Yes |
| `Admin:ProvisioningKey` | *(none — secret)* | Already specified, Security Analysis §5 / API spec §3.3; listed here only for completeness since it lives in the same `Auth:`-adjacent configuration surface. | Yes |

No B2 key value appears in any configuration key here (that remains CAS-001 §9's
`B2:SystemKeyId`/`B2:SystemApplicationKey`, and each user's own key stays in `dbo.users`,
encrypted — §5).

---

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | A successful `POST /login` issues a cookie containing `user_id`, `username`, and `b2_capability` claims, and no claim contains the raw B2 key, key ID, or password hash | `Test_SC1_LoginClaimsExcludeSecrets` |
| SC-2 | An authenticated request made 7 hours after login (within the 8-hour sliding window) succeeds and the window renews; a request made after 8 hours of inactivity returns `401` | `Test_SC2_SlidingExpirationRenewsAndLapses` |
| SC-3 | `POST /logout` invalidates the session such that a subsequent request with the same (pre-logout) cookie value returns `401` | `Test_SC3_LogoutInvalidatesSession` |
| SC-4 | A session whose `b2_capability` claim is `"Read Only"` receives `403` from an endpoint gated by `CachetFullAccess` (e.g., Permanent Delete submission) | `Test_SC4_ReadOnlyBlockedFromFullAccessActions` |
| SC-5 | A session whose `b2_capability` claim is `"Full Access"` succeeds against a `CachetFullAccess`-gated endpoint | `Test_SC5_FullAccessAllowedOnGatedActions` |
| SC-6 | `GET /tasks` called by user A never returns a task row whose `submitted_by_user_id` belongs to user B | `Test_SC6_TaskListScopedToOwnUser` |
| SC-7 | A server-side call from Web to Api, made on behalf of an authenticated Blazor Server circuit, is recognized by Api as the same authenticated session (no separate re-login) | `Test_SC7_WebToApiSessionForwardingWorks` |
| SC-8 | No response body, header, cookie value (decoded), log line, or exception message contains the raw B2 application key, key ID, or password hash | `Test_SC8_NoCredentialLeakageAcrossAuthSurface` |

---

## §11 Telemetry

```csharp
// _logger.LogInformation("Login succeeded for {UserId}, capability {Capability}", userId, capability);
// _logger.LogWarning("Login failed: {Reason}", reason); // reason is a fixed enum value, never raw B2/DB error text
// _logger.LogInformation("Session policy check failed: {Policy} for {UserId}", policyName, userId);
```

- Never log: the B2 application key or key ID (any user's, or the system key), the password or its
  hash, the raw cookie/session value, or the Data Protection key material.
- Do log: login success/failure (with a fixed reason enum, not raw exception text), logout events,
  and `403` policy-check failures (useful for spotting a misconfigured or probing client) — all
  consistent with the audit log (FR-23) already capturing Login/Logout at the business-event level
  (API spec §3.1/§3.2); telemetry here is operational/debug-level, not a substitute for that audit
  trail.

---

## §12 Security Notes

### B2 Credential Handling
No B2 key material ever enters a session claim, cookie, or any Web/Api telemetry (§6.1, §11). The
session carries only a derived, non-sensitive capability label — consistent with Security Analysis
§9's addendum, which is the one place the BRD accepts a B2-derived value crossing into
browser-visible territory (the label itself, never the key).

### Audit Trail
Login and Logout audit rows (`tab: "Auth"`) are written before their respective responses return,
per API spec §3.1/§3.2 — unchanged by this document. This document adds no new audited actions;
`403` policy failures are telemetry (§11), not audit-log entries, since they are not a completed
business action (no state changed) — consistent with the audit log's action-oriented, not
access-oriented, design (FR-23's four-action baseline plus the already-added Login/Logout, API
spec §11 item 12).

### Session / Auth Requirements
This document *is* the session/auth requirement other specs (API spec §1.2, CAS-001 §12, and the
feature-spec template's §12 placeholder) point to. Every endpoint except `POST /login` requires
policy `CachetAuthenticated` at minimum; Permanent Delete, Purge, Copy, and Update additionally
require `CachetFullAccess` (§6.4). `POST /admin/users` uses neither policy — a separate admin-token
check (Security Analysis §5), unaffected by this document.

---

## §13 Test Plan

### Unit Tests
| Class | Method | Proves |
|---|---|---|
| `LoginServiceTests` | `Test_SC1_LoginClaimsExcludeSecrets` | SC-1 |
| `LoginServiceTests` | `Test_SC8_NoCredentialLeakageAcrossAuthSurface` (claims/log assertions) | SC-8 (partial) |
| `AuthorizationPolicyTests` | `Test_SC4_ReadOnlyBlockedFromFullAccessActions` | SC-4 |
| `AuthorizationPolicyTests` | `Test_SC5_FullAccessAllowedOnGatedActions` | SC-5 |
| `TaskQueueRepositoryTests` | `Test_SC6_TaskListScopedToOwnUser` | SC-6 |

### Integration Tests
| Class | Method | Proves |
|---|---|---|
| `AuthIntegrationTests` (`WebApplicationFactory`, Api) | `Test_SC2_SlidingExpirationRenewsAndLapses` | SC-2 |
| `AuthIntegrationTests` | `Test_SC3_LogoutInvalidatesSession` | SC-3 |
| `WebToApiIntegrationTests` | `Test_SC7_WebToApiSessionForwardingWorks` | SC-7 |
| `AuthIntegrationTests` | `Test_SC8_NoCredentialLeakageAcrossAuthSurface` (full HTTP round trip) | SC-8 |

### Coverage Target
Changed files must reach ≥ 85% line coverage (project standard).

---

## §14 Rollout

1. No database migration required (§5) — implementation can start once `dbo.users` exists (WBS
   Task 13).
2. Implement the shared cookie-authentication configuration (§6.2, §6.6) in both `Cachet.
   ArchiveSync.Api` and `Cachet.ArchiveSync.Web` `Program.cs` together — they must agree on cookie
   name, Data Protection key ring, and claim shape from day one; standing one up without the other
   leaves session forwarding broken, not degraded.
3. Implement `POST /login`/`POST /logout` (Api, WBS Task 15) against §6.1/§6.3 before any other
   authenticated endpoint can be meaningfully tested end-to-end.
4. Implement the `CachetFullAccess`/`CachetAuthenticated` policies (§6.4) and apply them across
   the already-specified endpoint set (API spec §4–§9) as those modules are built (WBS Tasks
   16–20) — each module's own implementation spec should reference this document for which policy
   to attach, rather than re-deriving it.
5. Rollback: a bad deploy of the auth middleware is reverted by redeploying the prior Api/Web
   version; no B2-side or database-side action is taken by this design that would need separate
   rollback (§5 — no schema change).

---

## §15 Open Questions

All items originally identified while drafting this document were resolved before completion —
logged below for traceability (Karpathy P6), matching CAS-001's own resolution-log convention. No
blocking open items remain.

- [x] **Login method choice** — not actually open; corrected framing, §0.
- [x] **Exact sliding-expiration TTL** (API spec §3.4's explicitly deferred number) — resolved:
      8 hours (§6.2), configurable via `Auth:SlidingExpirationHours` (§9) without a design change
      if usage data later suggests a different value.
- [x] **Concrete ASP.NET Core policy name(s)** (CAS-001 §12's deferred item) — resolved:
      `CachetFullAccess` / `CachetAuthenticated` (§6.4).
- [x] **Task-queue B2 key resolution model conflict** between Security Analysis §2.4/§6 item 10
      and CAS-001 §6.1 — resolved by following CAS-001's more recent model, with the discrepancy
      flagged explicitly (§6.5) rather than silently picked.
- [x] **How a Web (Blazor Server) circuit's calls to Api carry the user's session** — not
      addressed by any existing document; resolved as a shared-cookie, same-origin design (§6.6),
      marked as this document's own proposal since neither the BRD nor the assumed-stack note in
      CLAUDE.md specifies it.

Not depending on either remaining open BRD §12.2 item (O-Drive refresh cadence; drive
encryption-at-rest) — noted for completeness per the template's instruction, not because this
spec needs them resolved.

**Recommendation carried forward (not a blocker):** `CACHET_Security_CredentialHandling_Analysis_
v1.md` should receive a dated addendum cross-referencing §6.5 above, the same way its own §9 was
added as an addendum rather than an edit-in-place — keeps the document's revision history
auditable.

---

## §16 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | R. Devarajan (TraceCounts) | 2026-09-10 | Drafted per WBS Task 11; scope corrected from the original (stale) task brief per §0 |
| Reviewer | | | Pending |

---

## §17 Graph Impact

No graphify graph currently covers application code (none exists in this repo yet — WBS Tasks
13–34 are `Not Started`; `graphify-out/` indexes only the BRD/WBS/tooling corpus, per
`graphify-out/GRAPH_REPORT.md`'s 29-file documentation-only corpus). Same fallback CAS-001 used:
this section is filled from a manual read of the BRD, the existing `0.Specifications/*.md`
documents, and the `aspnetcore-security`, `blazor-server`, `clean-architecture` skill files.

### God Nodes in Blast Radius
N/A — no source code exists yet.

### Communities / Modules Affected
Auth (primary). Every other module (Keyword Search, O-Drive Sync, Quarantine, Task Manager, Audit
Log, Reporting) depends on this document's `[Authorize(Policy = ...)]` decisions (§6.4) being
applied consistently at implementation time — flagged so each module's own future `CAS-00X` spec
references this document rather than re-deciding policy names independently. Projects: `Api`
(cookie/policy configuration, `POST /login`/`/logout` implementation), `Web` (Blazor Server
`AuthenticationStateProvider` integration, §6.6 forwarding), `Core` (no direct change — claims and
policies are ASP.NET Core framework concerns, not domain entities). `Worker` is affected only
insofar as §6.5 clarifies it does *not* consume per-user session state, which CAS-001 already
assumed.

### Surprising Connections
- The Security Analysis document (2026-09-08) and CAS-001 (2026-09-10) quietly disagree on whether
  the Worker resolves a per-user or a system-level B2 key — a two-day-old document and a
  same-day-earlier document reaching different conclusions on the same question, neither one aware
  it was revising the other. Surfaced and resolved explicitly in §6.5 rather than left for an
  implementer to notice (or not) later.
- This project's own two-project (Api/Web) split, an architectural assumption this repo's CLAUDE.md
  states rather than a BRD requirement, turns out to require its own session-forwarding design
  decision (§6.6) that no prior document addresses — a gap created by an assumed-stack choice, not
  by the BRD itself.

### Graphify Queries Run
Graphify not applicable to application code yet — analysis done via manual read of
`0.Specifications/CACHET_Archive_Files_Search_Sync_BRD_v2.md` §10.3/§12.1/§12.2,
`CACHET_Security_CredentialHandling_Analysis_v1.md` (full document), `CACHET_API_Design_
Specification_v1.md` §1.2/§3/§3.4/§7.1, `CACHET_Database_Schema_Design_v1.md` §4/§6, `CACHET_UIUX_
Design_Specification_v1.md` §2, `1.WBS/CACHET_WBS_v1.md` row 19, and `CAS-001-b2-integration-
design.md` §6.1/§12/§15.

---

## §18 Verification Plan

### Reviewer Checklist
- [ ] All §10 Success Criteria have a passing test.
- [ ] §6.4's policy-to-action mapping matches CAS-001 §12's confirmed capability requirements
      exactly (Full Access for Permanent Delete/Purge/Copy/Update; Read Only sufficient elsewhere).
- [ ] §6.5's reconciliation is actually implemented — the Worker must not attempt to resolve or use
      a per-user B2 key at execution time; only the login path (§6.1) touches a user's own key.
- [ ] §12 Security Notes: confirm no path in §6 allows a B2 key, password, or password hash to
      reach a session claim, cookie payload, log line, or HTTP response.
- [ ] §6.2's cookie attributes (`HttpOnly`, `Secure`, `SameSite=Strict`) and 8-hour sliding
      expiration are configured exactly as specified, not left at framework defaults.
- [ ] §6.6's Web→Api forwarding actually works end-to-end (SC-7) and both projects share one
      Data Protection key ring, not two independently-generated ones (which would silently break
      forwarding rather than error clearly).
- [ ] No SSO-related code, configuration, or abstraction was introduced (§0 — explicitly rejected
      scope for this document).
- [ ] §17 Graph Impact matches actual code changes (no unintended nodes), once code exists.
- [ ] No out-of-scope changes from §4 were made (login screen layout, `Users` schema, or
      provisioning endpoint mechanics untouched).

### Verification Report Format
Per template: `CAS-002.review.md` with overall verdict, per-SC verdict table, security gap list,
scope violations.

## Implementation Log

**Completed:** 2026-09-22
**Gates:** Build ✓ | Tests ✓ | Format ✓ | Coverage 83-100% per-file (verified manually; cachet_agent_tools coverage.py cross-XML bug flagged separately) | Endpoints ✓

### Files Changed
| File | Change |
| --- | --- |
| `Cachet.ArchiveSync.Core/Dtos/LoginRequest.cs` | API spec §3.1 request DTO |
| `Cachet.ArchiveSync.Core/Dtos/LoginResponse.cs` | API spec §3.1 response DTO |
| `Cachet.ArchiveSync.Core/Dtos/UserAccount.cs` | dbo.users entity mirror |
| `Cachet.ArchiveSync.Core/Dtos/LoginOutcome.cs` | testable service-layer login result |
| `Cachet.ArchiveSync.Core/Enums/LoginFailureReason.cs` | login failure reason enum |
| `Cachet.ArchiveSync.Core/Interfaces/IUserRepository.cs` | user lookup + session version repo interface |
| `Cachet.ArchiveSync.Core/Interfaces/ILoginService.cs` | login service interface |
| `Cachet.ArchiveSync.Core/Interfaces/IUserB2KeyAuthorizer.cs` | per-user B2 key authorization seam |
| `Cachet.ArchiveSync.Core/Interfaces/ICredentialProtector.cs` | B2 key ciphertext decrypt seam |
| `Cachet.ArchiveSync.Api/Repositories/UserRepository.cs` | Dapper impl: get-by-username, get/increment session version |
| `Cachet.ArchiveSync.Api/B2/UserB2KeyAuthorizer.cs` | live b2_authorize_account for per-user key |
| `Cachet.ArchiveSync.Api/Services/DataProtectionCredentialProtector.cs` | Data Protection based key decryption |
| `Cachet.ArchiveSync.Api/Services/LoginService.cs` | two-stage login check, claim derivation, audit writes |
| `Cachet.ArchiveSync.Api/Controllers/AuthController.cs` | POST /login, POST /logout |
| `Cachet.ArchiveSync.Api/Program.cs` | cookie auth, policies, OnValidatePrincipal revocation check, DI |
| `Cachet.ArchiveSync.Web/Http/CookieForwardingHandler.cs` | forwards session cookie Web to Api (§6.6) |
| `Cachet.ArchiveSync.Web/Program.cs` | OnValidatePrincipal revocation check, DI, named HttpClient |
| `Cachet.ArchiveSync.Web/Data/UserRepository.cs` | Web-local session-version-only repo for revocation check |
| `Cachet.ArchiveSync.Web/appsettings.Development.json` | Api:BaseUrl config key |
| `Database/CachetArchiveSync/Entities/dbo.users.sql` | adds session_token_version column |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.users_get_by_username.sql` | new SP |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.users_get_session_token_version.sql` | new SP |
| `Database/CachetArchiveSync/Stored-Procedures/dbo.users_increment_session_token_version.sql` | new SP |

### Tests Added
| Class | Method | Covers |
| --- | --- | --- |
| `LoginServiceTests` | `Test_SC1_LoginClaimsExcludeSecrets` | SC-1 |
| `AuthIntegrationTests` | `Test_SC2_SlidingExpirationRenewsAndLapses` | SC-2 |
| `AuthIntegrationTests` | `Test_SC3_LogoutInvalidatesSession` | SC-3 |
| `AuthIntegrationTests` | `Test_SC4_ReadOnlyBlockedFromFullAccessActions` | SC-4 (policy-level) |
| `AuthIntegrationTests` | `Test_SC5_FullAccessAllowedOnGatedActions` | SC-5 (policy-level) |
| `LoginServiceTests` | `Test_SC6_TaskListScopedToOwnUser` | SC-6 (claim-level) |
| `CookieForwardingHandlerTests` | `Test_SC7_WebToApiSessionForwardingWorks` | SC-7 |
| `AuthIntegrationTests` | `Test_SC8_NoCredentialLeakageAcrossAuthSurface` | SC-8 |

### Migration
`20260922_161500__CAS-002-users-get-by-username.sql; 20260922_163000__CAS-002-session-token-version.sql`
