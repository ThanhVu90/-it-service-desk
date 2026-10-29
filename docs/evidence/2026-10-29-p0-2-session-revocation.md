# Evidence — P0-2: session revocation on account lock (29 October 2026)

Branch `upgrade/p0-2-session-revocation`, based on the merged `main` containing P0-1.

## Environment

Windows 11 x64, .NET SDK 10.0.403, EF Core 10.0.14. All runs on fresh SQLite files; no personal data.

## Sequence

1. **Model + migration first** (`1ce4030`): `IsActive` bool default true, `SessionVersion` int default 1, explicit DB defaults. Existing suites: **35/35 green** — the migration alone changes no behavior.
2. **Acceptance tests written first** (`4d4cef7`): 7 tests in `SessionRevocationTests` — all **red** (`404` on the missing lock endpoint, stale cookies still `200`). Baseline 35 stayed green.
3. **Implementation** (`3c85891`): login carries a `sd_session` version claim and rejects inactive accounts with the generic 401; `OnValidatePrincipal` re-checks the database per request and signs out on mismatch (`RejectPrincipal` + `SignOutAsync`), including fail-closed on a database outage; `POST /api/users/{id}/lock` is admin-only, CSRF-checked, bumps the session version and appends the audit event in the same `SaveChangesAsync` transaction; the last active administrator cannot be locked.
4. **Full suite: 42/42 green.**

## Acceptance criteria mapping

| Guide criterion | Proof |
|---|---|
| Lock kills the live session at the next request | `Locked_account_loses_access_on_the_next_request_and_relogin`; hand-run demo returned 401 right after the 204 lock with the log line `Session … rejected (account-locked)` |
| Re-login refused while locked | same test (401 after lock) |
| Employee cannot lock | `Locking_is_admin_only` (403, audit count 0) |
| CSRF enforced on lock | `Locking_requires_csrf_token` (400, account unchanged) |
| Audit failure rolls the lock back | `Failed_audit_insert_rolls_back_the_lock` (409; `IsActive` true, version 1, no audit row) |
| Last active admin protected | `Last_active_administrator_is_protected_from_locking` (409) |
| Version bump alone revokes sessions | `Bumped_session_version_rejects_existing_cookies_without_a_lock` |
| Unknown/deleted user rejected | `Deleted_user_is_rejected_by_session_validation` |
| Passwords/cookies never logged | reasons are fixed constants (`account-locked`, `version-mismatch`, …); only user id + reason reach the logger |

## Upgrade semantics

- Migration 0003 runs through the P0-1 machinery: snapshot → verify fingerprint (against baseline) → adopt/migrate. Databases at 0001 (legacy) and 0001+0002 (previous release) both upgrade cleanly; the bootstrap test proves adoption ends with all three migrations applied and data intact.
- Cookies signed before this release have no `sd_session` claim: first request after upgrade is rejected and the user signs in again. Documented in README as intentional.
- No unlock endpoint by design of this item; the audit trail records every lock.

## Limits

- Session validation reads the users row on every authenticated request; fine at demo scale, and it fails closed during outages (401, not 500). Not a distributed-session store.
- Lock is irreversible through the app in this release.

## Demo note

Two browser windows: sign in as admin and as an employee; lock the employee from the admin window via the API; from the employee window the very next action (e.g. clicking any page) lands on the sign-in screen. Also demonstrate the generic 401 on re-login and — for contrast — the same generic 401 for a wrong password, then reopen the response to show the `User Locked` audit entry and that the admin list marks `isActive: false`.
