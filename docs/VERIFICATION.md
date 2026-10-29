# Verification evidence — 29 October 2026 (addendum: P1-2 readiness + correlation)

P1-2 (readiness probe, correlation ids, structured error logs) landed on branch `upgrade/p1-2-readiness`:

- `/health` remains the liveness endpoint; the new `/health/ready` checks reachability **and** that every EF migration is applied on SQLite, returning 503 with a message that names no paths or connection details.
- 5 acceptance tests written first (4 red against a missing endpoint/behavior, 1 leak guard already green); the full suite then reached **54 passed, 0 failed** (49 + 5).
- Every response carries `X-Correlation-ID` (caller-supplied values of 8–64 ASCII alphanumerics are honored, otherwise a 12-hex id is generated); validation/unhandled-error bodies embed the same id as `correlationId`.
- Hand-run demo against a seeded file: readiness 200 → break the store on disk → readiness 503; a validation failure returned 400 with `correlationId 5b3a01e93361`; after the store broke, a login attempt returned 500 with `X-Correlation-ID: 7e62c084d785` and the server log contained exactly `Unhandled request failure 7e62c084d785 POST /api/login`. A scan of the full log found no password, cookie name or connection string.

# Verification evidence — 29 October 2026 (addendum: P1-1 ticket comments)

P1-1 (comments within ticket visibility) landed on branch `upgrade/p1-1-ticket-comments`:

- Migration `20261029152456_TicketComments` adds the `TicketComments` table (body ≤ 2000, index on `TicketId, CreatedAt`, restrictive FKs to Tickets/Users).
- Acceptance tests written first (7, in `CommentTests`): 6 red before the implementation (missing endpoints), 1 guard already green; the full suite then reached **49 passed, 0 failed** (42 prior + 7 new).
- Endpoints: `GET /api/tickets/{id}/comments` (paging 1..50, ordered by `CreatedAt` then `Id` so same-timestamp rows never duplicate or drop) and `POST /api/tickets/{id}/comments` (validation: non-empty, ≤ 2000 chars). Both resolve the ticket through the caller's visibility boundary, so outsiders get 404 both for reading and writing.
- The comment insert and the `Commented` audit entry commit in one transaction; an injected audit failure rolls the comment back (409).
- The UI adds a `Discussion` section in the ticket dialog. Comment text is stored and served as plain data and rendered with `createElement`/`textContent` only — a static test proves `renderComments` (kept as the file's last function) contains no `innerHTML`.

# Verification evidence — 29 October 2026 (addendum: P0-2 session revocation)

P0-2 (lock accounts, revoke live sessions) landed on branch `upgrade/p0-2-session-revocation`:

- Migration `20261029150755_UserLockoutState` adds `IsActive` (default 1) and `SessionVersion` (default 1) to `Users`; upgraded databases keep every existing account active.
- Acceptance tests written first (7, in `SessionRevocationTests`): all red before the implementation against the migration-only commit; the full suite then reached **42 passed, 0 failed** locally (28 original + 7 migration/backup + 7 revocation).
- Hand-run demo against a fresh seeded file: admin signs in on one "window", a technician on another; admin locks the technician (`POST /api/users/3/lock` → 204); the technician's immediate next request with the same cookie is rejected — server log `Session for user 3 rejected (account-locked)` and HTTP 401. Server logs contain no password or cookie material.
- The legacy-adoption test from P0-1 was reframed to pin the "pre-migration schema" to the baseline migration instead of the live model, so it stays correct as future migrations pile up.

# Verification evidence — 29 October 2026

P0-1 (controlled migrations, legacy adoption, backup/restore) was implemented on branch `upgrade/p0-1-migration`:

- Local Release build: 0 warnings, 0 errors. Local test suite: **35 passed, 0 failed** — the previous 28 plus 7 new bootstrap/backup tests written first and confirmed failing (`NotImplementedException`) before the implementation existed.
- Migrations shipped: `20261029143459_Initial` (baseline schema) and `20261029143523_AuditLookupIndex` (second schema change, `IX_AuditEvents_Entity_EntityId`).
- Hand-run demo from a clean file: fresh start migrated + seeded (`FreshMigrated`), CLI `db-backup` produced a 73728-byte snapshot, CLI `db-restore` passed the integrity check, and a second instance booted on the restored copy reporting `AlreadyCurrent`, answered `/health` with 200 and rejected a wrong login with 401 after a real read of the restored `Users` table.
- A first e2e attempt exposed a real bug (migration history misread as a legacy schema because `__EFMigrationsHistory` was filtered out of inspection). Fixed, with a regression test written first (`Database_with_migration_history_is_never_adopted_as_legacy_on_restart`).
- Details, acceptance criteria mapping and demo flow: `docs/evidence/2026-10-29-p0-1-migration.md`.
- CI for this branch: run 40895759648 (pull_request, Windows and Ubuntu) — **success** on the code commits; the docs-only commit that adds this line triggers a re-run with the same test suite. Dashboard: https://github.com/ThanhVu90/-it-service-desk/actions/runs/40895759648

# Verification evidence — 27 October 2026

- Debug integration suite: **28 passed, 0 failed, 0 skipped** against isolated SQLite files. This successful run used EF Core SQLite 10.0.14 and Npgsql provider 10.0.0. Tests cover authorization boundaries, antiforgery, ticket lifecycle, optimistic concurrency, concurrent device handover, audit rollback, validation, rate limiting and UTC deadlines.
- Release build: succeeded with 0 warnings and 0 errors before the final whitespace-only formatting pass and PostgreSQL provider version update.
- Browser smoke test: sign-in, ticket creation, technician claim/resolve, administrator device handover and persisted updates all worked.
- `node --check src/ServiceDesk/wwwroot/app.js`: passed.
- Desktop and narrow mobile layouts were inspected.
- NuGet vulnerability scan reported no vulnerable entries before the PostgreSQL provider update.

The PostgreSQL provider was then updated to 10.0.3. The local machine could not resolve `api.nuget.org`, so that package update could not be restored or tested here. The full SQLite test suite passed before that package-only change. PostgreSQL is documented as experimental and has no provider integration tests. A subsequent Release test-host invocation stalled before reporting any test result. These checks are not claimed as passing.

GitHub Actions is configured to restore, build and test on Windows and Linux. The workflow has been added to the repository; a remote run is still pending.
