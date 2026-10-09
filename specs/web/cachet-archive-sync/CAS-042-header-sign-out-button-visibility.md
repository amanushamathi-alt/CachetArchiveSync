---
id: CAS-042
target_app: cachet-archive-sync
status: review
owner: devarajan@tracecounts.org
created: 2026-10-01
priority: P3
change_type: bug-fix
related_specs: [CAS-021, CAS-025]
touches:
  database: false
  api: false
  ui: true
  worker: false
  b2: false
  config: false
  security: false
migrations: []
---

# CAS-042 — Change: Header Sign Out Button Visibility (CSS only)

## §1 Current Behavior

- `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor` renders the Sign Out control as
  `<button type="submit" class="cachet-sign-out-btn">Sign Out</button>` inside a plain `POST /logout`
  form (`CAS-021` §8.4, `CAS-025` §8.3).
- `src/Cachet.ArchiveSync.Web/wwwroot/app.css` (`.cachet-sign-out-btn`) styles it for a **dark navy
  header**:
  - `background: transparent`;
  - `color: var(--cachet-header-text)`, which is white;
  - `border: 1px solid var(--cachet-navy-line)`;
  - on hover only, `background: var(--cachet-navy-mid)`.
- The header bar is white, so the button text is white on white and **cannot be seen until the
  pointer hovers over it**. Reported by A. Mathi on 2026-10-01 with a screenshot.

## §2 Desired Behavior

- The Sign Out button is readable at all times: navy text and a navy 1px border on a white background,
  at weight 600. This matches the header's existing `.cachet-header-btn` navy-outline style.
- On hover **and on keyboard focus** (`:focus-visible`), the button has a navy background and white
  text.
- No behavior change: the same markup, the same `POST /logout` form, and the same antiforgery token.

## §3 Reason for Change

- **Type:** `bug-fix` (visual/accessibility: the text failed to show against its background).
- **Trigger:** requester report, 2026-10-01.
- **Risk if not changed:** users cannot find Sign Out. The session stays open on shared workstations,
  which matters for SOC 2 session-management expectations.

## §4 Blast Radius (Graph Impact)

Mapped by manual grep; graphify does not index `.razor` or CSS edges.
- `.cachet-sign-out-btn` is used only in `MainLayout.razor:40`.
- `.cachet-sign-out-form` / `.cachet-sign-out-btn` are defined in `app.css:180-197`, and
  `.cachet-sign-out-form` again in `MainLayout.razor.css`.
- The change lives in MainLayout's **scoped** CSS. Blazor CSS isolation applies it only to elements
  rendered by `MainLayout`, so no other page or component is affected.
- Cross-project risk: none (Web only). God node: none.

## §5 Files to Change

| File | Change Type | Description |
|------|------------|--------------|
| `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor.css` | modify | Adds scoped `.cachet-sign-out-btn` (white background, navy border and text, weight 600) and `:hover, :focus-visible` (navy background, white text) rules, with a comment. |

## §6 Files NOT to Change

| File | Why left untouched |
|------|--------------------|
| `src/Cachet.ArchiveSync.Web/wwwroot/app.css` | Shared stylesheet. The global rule stays for any future dark-header context; it is overridden only within MainLayout. |
| `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor` | Markup and the `POST /logout` form are unchanged. |
| `src/Cachet.ArchiveSync.Web/Program.cs` (logout handler) | Behavior unchanged. |

## §7 Database Changes

N/A.

## §8 API Contract Changes

N/A.

## §9 Validations Changed

N/A.

## §10 Success Criteria

| ID | Criterion | Test |
|----|-----------|------|
| SC-1 | Without hover, the Sign Out text and border are visible on the white header: navy (`#142A4D`) on white, a contrast ratio above WCAG AA 4.5:1. | manual (§16) |
| SC-2 | On hover and on keyboard focus, the button shows a navy background with white text. | manual (§16) |
| SC-3 | Signing out still works (`POST /logout` clears the session and redirects to sign-in), and the existing MainLayout, Login and SignOut tests pass unmodified. | existing `MainLayout*`, `Login*`, `SignOut*` tests |

## §11 Security Notes

No security impact. The antiforgery token, the POST-only logout and the cookie clearing are
unchanged. Better visibility supports users actually ending their sessions.

## §12 Rollback Plan

1. Remove the two added rules from `MainLayout.razor.css` and redeploy Web.
2. Database, config, task queue: N/A.

## §13 Test Plan

| Class | Method | Proves SC / V |
|-------|--------|--------------|
| existing MainLayout / Login / SignOut test classes | unchanged | SC-3 |
| manual browser check | Sign Out readable at rest, on hover and on Tab focus | SC-1, SC-2 |

bUnit does not evaluate CSS, so no new automated test is added (P4).

## §14 Open Questions

- [ ] Should `app.css` `.cachet-sign-out-btn` itself be corrected now that the header is white
      everywhere? It is left as-is for the smallest diff; the owner may consolidate later.

## §15 Sign-off

| Role | Name | Date | Notes |
|------|------|------|-------|
| Author | A. Mathi, via Claude Code | 2026-10-01 | Requested directly; spec written after the one-rule CSS fix for traceability |
| Reviewer | | | |

## §16 Verification Plan

- [ ] Only `MainLayout.razor.css` changed (§5); `app.css`, `MainLayout.razor` and `Program.cs` are unchanged (§6)
- [ ] SC-1/SC-2 manual check done (record the result and date here)
- [ ] SC-3 existing tests pass

## Implementation Log

**Completed:** 2026-10-01
**Gates:** Build ✓ (Release, 0 errors) | Tests ✓ (MainLayout/Login/SignOut filter: 19 passed, 1 pre-existing skip, 0 failed) | Format — (CSS only) | Coverage — (CSS only) | Endpoints — (skip)

| File | Change |
| --- | --- |
| `src/Cachet.ArchiveSync.Web/Components/Layout/MainLayout.razor.css` | Scoped Sign Out visibility rules added |

**Process note:** the fix was applied on the requester's direct instruction before this spec existed.
This spec was written immediately afterwards to record it. Status is `review` pending the §16 manual
check and an independent `/review CAS-042`.
