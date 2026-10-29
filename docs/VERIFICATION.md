# Verification evidence — 29 October 2026

P0-1 (controlled migrations, legacy adoption, backup/restore) was implemented on branch `upgrade/p0-1-migration`:

- Local Release build: 0 warnings, 0 errors. Local test suite: **35 passed, 0 failed** — the previous 28 plus 7 new bootstrap/backup tests written first and confirmed failing (`NotImplementedException`) before the implementation existed.
- Migrations shipped: `20261029143459_Initial` (baseline schema) and `20261029143523_AuditLookupIndex` (second schema change, `IX_AuditEvents_Entity_EntityId`).
- Hand-run demo from a clean file: fresh start migrated + seeded (`FreshMigrated`), CLI `db-backup` produced a 73728-byte snapshot, CLI `db-restore` passed the integrity check, and a second instance booted on the restored copy reporting `AlreadyCurrent`, answered `/health` with 200 and rejected a wrong login with 401 after a real read of the restored `Users` table.
- A first e2e attempt exposed a real bug (migration history misread as a legacy schema because `__EFMigrationsHistory` was filtered out of inspection). Fixed, with a regression test written first (`Database_with_migration_history_is_never_adopted_as_legacy_on_restart`).
- Details, acceptance criteria mapping and demo flow: `docs/evidence/2026-10-29-p0-1-migration.md`.
- CI for this branch: pending — this file will be updated with the run link after push. Do not advertise the CI badge for this branch yet.

# Verification evidence — 27 October 2026

- Debug integration suite: **28 passed, 0 failed, 0 skipped** against isolated SQLite files. This successful run used EF Core SQLite 10.0.14 and Npgsql provider 10.0.0. Tests cover authorization boundaries, antiforgery, ticket lifecycle, optimistic concurrency, concurrent device handover, audit rollback, validation, rate limiting and UTC deadlines.
- Release build: succeeded with 0 warnings and 0 errors before the final whitespace-only formatting pass and PostgreSQL provider version update.
- Browser smoke test: sign-in, ticket creation, technician claim/resolve, administrator device handover and persisted updates all worked.
- `node --check src/ServiceDesk/wwwroot/app.js`: passed.
- Desktop and narrow mobile layouts were inspected.
- NuGet vulnerability scan reported no vulnerable entries before the PostgreSQL provider update.

The PostgreSQL provider was then updated to 10.0.3. The local machine could not resolve `api.nuget.org`, so that package update could not be restored or tested here. The full SQLite test suite passed before that package-only change. PostgreSQL is documented as experimental and has no provider integration tests. A subsequent Release test-host invocation stalled before reporting any test result. These checks are not claimed as passing.

GitHub Actions is configured to restore, build and test on Windows and Linux. The workflow has been added to the repository; a remote run is still pending.
