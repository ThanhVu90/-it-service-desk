# Verification evidence — 27 October 2026

- Debug integration suite: **28 passed, 0 failed, 0 skipped** against isolated SQLite files. This successful run used EF Core SQLite 10.0.14 and Npgsql provider 10.0.0. Tests cover authorization boundaries, antiforgery, ticket lifecycle, optimistic concurrency, concurrent device handover, audit rollback, validation, rate limiting and UTC deadlines.
- Release build: succeeded with 0 warnings and 0 errors before the final whitespace-only formatting pass and PostgreSQL provider version update.
- Browser smoke test: sign-in, ticket creation, technician claim/resolve, administrator device handover and persisted updates all worked.
- `node --check src/ServiceDesk/wwwroot/app.js`: passed.
- Desktop and narrow mobile layouts were inspected.
- NuGet vulnerability scan reported no vulnerable entries before the PostgreSQL provider update.

The PostgreSQL provider was then updated to 10.0.3. The local machine could not resolve `api.nuget.org`, so that package update could not be restored or tested here. The full SQLite test suite passed before that package-only change. PostgreSQL is documented as experimental and has no provider integration tests. A subsequent Release test-host invocation stalled before reporting any test result. These checks are not claimed as passing.

GitHub Actions is configured to restore, build and test on Windows and Linux. The workflow has been added to the repository; a remote run is still pending.
