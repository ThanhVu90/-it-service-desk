# Verification evidence — 27 October 2026

- `dotnet build ServiceDesk.slnx -c Release --no-restore -m:1 -p:UseSharedCompilation=false`: succeeded, 0 warnings and 0 errors before the final whitespace-only formatting pass.
- Debug integration suite: 28 passed, 0 failed, 0 skipped on SQLite with isolated database files. Covers authorization boundaries, anti-forgery, ticket workflow, optimistic concurrency, concurrent device handover, transaction rollback, input validation, rate limiting and UTC deadlines.
- Live ASP.NET Core demo: login, create ticket, technician claim/resolve flow, admin device assignment and database-backed results verified in the browser.
- `dotnet list ... package --vulnerable --include-transitive`: no vulnerable package entries reported after upgrading SQLitePCLRaw via EF Core 10.0.14.
- `node --check src/ServiceDesk/wwwroot/app.js`: succeeded.
- Desktop and narrow mobile layouts inspected; screenshot included in the README.

A second Release test invocation stalled after the test host announced the assembly. The prior full 28-test run passed. This runner behavior is recorded instead of claiming the repeated run completed. GitHub Actions is configured to repeat restore, build and tests on Windows and Linux; remote CI has not yet run for this commit.
