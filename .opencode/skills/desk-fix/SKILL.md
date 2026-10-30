---
name: Desk Fix
description: Fix-loop conventions for the IT service desk repo — red-before-green tests, evidence docs, branch/merge/CI polling
---

Use this skill whenever changing this repository's code or tests so fixes follow the same discipline each session.

## Loop

1. **Red before green.** Write the test that catches the reported defect first. Prove it fails against the current (buggy) code; when a report lacks a DB pointer, reconstruct the mutant from the real DDL (see `BuildLegacyDatabase` in `tests/ServiceDesk.Tests/DatabaseBootstrapTests.cs`).
2. **Prove selectivity.** Temporarily revert *only the changed method* to the pre-fix shape and run the single new test with `dotnet test ServiceDesk.slnx --filter "FullyQualifiedName~<TestName>"` — it must fail. Do not trust a test that was green before the fix.
3. **Fix minimally, run both configs:** `dotnet test ServiceDesk.slnx` and, for evidence, `dotnet test tests/ServiceDesk.Tests/ServiceDesk.Tests.csproj -c Release`. The suite is xUnit + `DeskFactory` (WebApplicationFactory over a file SQLite DB; seed ids alex=1, linh=2, sam=3, admin=4; password `DeskFactory.Password`; CSRF via `DeskFactory.RefreshToken`).
4. **Evidence doc.** Append/extend `docs/evidence/YYYY-MM-DD-<topic>.md` following `2026-10-30-review-fixes.md`: Scope, Environment, Fixes table, New tests (numbered), Red-before-green verification, Limits. Keep the "(N/N green)" suffix in commit messages.
5. **Ship.** Branch `fix/review-<topic>` → `git merge --no-ff <branch>` into `main` (matches the existing history style) → push → poll CI with the GitHub API (`actions/runs?per_page=2`, match `head_sha`, expect `conclusion: success` on the exact merged commit) → report the run URL.

## Seams and traps

- Tests reach internals via `InternalsVisibleTo ServiceDesk.Tests` (declared in `src/ServiceDesk/ServiceDesk.csproj`). Fault-injection seams on `DeskDbInitializer` (`BetweenHistoryWrites`) are intentional and must stay internal.
- `ServiceDesk.slnx` is the solution file — there is no `.sln`; a build of the missing name fails with MSB1009.
- **Never restore a file with a plain `Copy-Item` backup:** `LastWriteTime` is preserved, MSBuild seams the project "up to date" and tests silently run the OLD binary (this produced a false red/green round with passing suites over stale code). Re-save the file, or delete `bin/`+`obj/` of both projects when binary freshness is in doubt.
- Mutating rebuilds from DDL must land on exactly one statement — assert the mutation count, match on whitespace-normalized SQL, and exclude `sqlite_*`/`__*` names from capture.

## Test inventory anchors

- `DatabaseBootstrapTests` (bootstrap/adoption/maintenance CLI), `SessionRevocationTests` (lock/session races incl. `Two_concurrent_admin_locks_never_leave_zero_active_administrators`), `WorkflowTests`, `CommentTests`, `ReadinessTests`, `UnitTests/DomainTests`.
