# Verification evidence — 30 October 2026 (review fixes: `--force` staging, strict adoption fingerprint, atomic baseline history, serialised last-admin guard)

## Scope of this run

Four defects from the code review of `04be021` (and its green CI run): a P1 in the maintenance CLI (`--force` deleted the destination before the source was validated, so identical source/destination paths destroyed the source), a P1 in the legacy-adoption fingerprint (it compared only column and index names, so UNIQUE constraints, foreign keys, column types, NOT NULL and partial-index conditions never decided the match), a P2 in baseline adoption (the history table creation and the baseline row insert were two separate statements outside one transaction), and a new P1 concurrency defect in `LockUserAsync` (the "other active admins" count ran before the save transaction, so two concurrent requests could lock the last two administrators).

## Environment

- Windows 11 x64, .NET SDK 10.0.403, EF Core Sqlite 10.0.14.
- All tests via `dotnet test tests/ServiceDesk.Tests/ServiceDesk.Tests.csproj -c Release` → **65 passed, 0 failed, 0 skipped** (54 before this run + 11 new).

## Fixes

| Review item | Change |
|---|---|
| P1 `RunCli` (`src/ServiceDesk/Data/DbMaintenance.cs`) | `--force` never deletes first. The replacement is built at a `.stage-<guid>` file next to the destination, verified with the normal `integrity_check`, and only then swapped in with `File.Move(…, overwrite: true)`; failed runs delete only their own staging copy. `Backup`/`Restore` and the CLI reject a `--source`/`--out` pair that resolves to the same file (case-insensitive on Windows) before touching anything. |
| P1 `ReadLayoutAsync` (`src/ServiceDesk/Data/DeskDbInitializer.cs`) | The fingerprint now carries the full column definition from `PRAGMA table_info` (name, declared type, NOT NULL, primary-key position, default clause), foreign keys from `PRAGMA foreign_key_list` (referenced table, source/target columns, `on update`/`on delete`), and every index from `PRAGMA index_list` (unique flag incl. UNIQUE/PK auto-indexes, origin, exact key columns) plus the stored `CREATE INDEX` text for partial indexes (the WHERE condition), whitespace-normalised. |
| P2 `MarkBaselineAppliedAsync` (`src/ServiceDesk/Data/DeskDbInitializer.cs`) | The history table creation and the baseline insert share one explicit transaction (`connection.BeginTransaction()`), so a failure in between rolls back to a database with no history at all — the next start re-adopts it instead of treating a half-initialised table as migration-managed. A small internal seam (`BetweenHistoryWrites`) exists only to let tests inject the in-between failure. |
| P1 `LockUserAsync` (`src/ServiceDesk/Services/DeskService.cs`) | The admin path opens `BeginTransactionAsync(IsolationLevel.Serializable)` — BEGIN IMMEDIATE in Microsoft.Data.Sqlite — so the database write lock is taken **before** the "other active admins" count is read and held through the save and the audit insert. Concurrent lock requests serialise at the database layer and can never both pass the guard. |

## New tests (11)

1. `Rebuilt_baseline_without_any_semantic_change_is_still_adopted` — control: a database rebuilt from the baseline DDL still adopts, proving the rebuild pipeline is faithful.
2–6. `Schema_that_lost_semantics_never_matches_the_baseline_fingerprint` (theory) — five mutants rebuilt from the baseline DDL, each losing exactly one semantic detail: a UNIQUE constraint, a NOT NULL, a column type, a foreign key, and a partial-index WHERE condition. All five must be refused with "Schema does not match baseline".
7. `Failure_between_the_history_writes_leaves_no_half_created_history_table` — adoption with the injected failure leaves `sqlite_master` without `__EFMigrationsHistory` (rolled back), and the retry without the fault adopts cleanly.
8. `Forced_replace_writes_a_verified_copy_in_place_of_the_old_destination` — `db-restore --force` over a stale byte-garbage destination yields exit 0 and a database that opens and reads back the seeded rows.
9. `Forced_replace_never_touches_the_destination_when_the_copy_fails` — corrupt source + existing destination + `--force` → exit 3, destination bytes intact, no `.stage-*` leftovers.
10. `RunCli_refuses_a_destination_that_resolves_to_the_source` — backup and restore, forced or not, all return exit 3 and the source file survives byte for byte.
11. `Two_concurrent_admin_locks_never_leave_zero_active_administrators` — two admins seeded, two concurrent HTTP lock requests (each locking the other); exactly one returns 204, the surviving state keeps exactly one active administrator.

## Red-before-green verification

Every fix was proven by running its own test against the pre-fix code (temporary revert of the single changed method):

- Old `LockUserAsync` (count outside the transaction) → `Two_concurrent_admin_locks…` **failed** (both locks succeeded, zero active admins remained).
- Old non-transactional `MarkBaselineAppliedAsync` → `Failure_between_the_history_writes…` **failed** (an empty history table survived the injected failure).
- Old `RunCli` pre-delete → `Forced_replace_never_touches_the_destination…` **failed** (the guard destination was destroyed by a failed copy).
- Old name-only fingerprint → all five mutants of `Schema_that_lost_semantics…` were **incorrectly adopted** (the five theory cases failed, the faithful-rebuild control stayed green).

## Limits of this run

- Local run on one machine; the GitHub Actions run for the pushed commit re-executes the same suite.
- The strict fingerprint flags legacy databases whose DDL spells out the same semantics differently (cosmetic whitespace is normalised, but e.g. `TEXT` vs `VARCHAR(80)` refuses) — adoption refusing such files is the intended safe behaviour and is covered by the control test.
- The concurrency test depends on Microsoft.Data.Sqlite's busy handling for the waiting `BEGIN IMMEDIATE`; both requests wait or serialise rather than coring, asserted by the end state rather than by timing.
