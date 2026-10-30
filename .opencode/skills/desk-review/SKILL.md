---
name: Desk Review
description: Review checklist for the IT service desk repo — defect classes already found here, semantic-completeness rules, and empirical verification of claims
---

Use this skill when reviewing this repository's changes (own work or another tool's report). Each rule maps to a defect that was actually caught here; treat them as the minimum bar, not as advice.

## Destructive operations (`DbMaintenance`, any overwrite/restore path)

- Never delete the destination as a pre-step. `--force` must stage the replacement next to the destination, verify it (SQLite `integrity_check`), and only then `File.Move(…, overwrite: true)`. A failed copy or integrity check must leave the old file byte-for-byte intact and must clean up its `.stage-*` file.
- Refuse a source/destination pair that resolves to the same file (`EnsureDistinctPaths`, case-insensitive on Windows) before touching anything — "backup a file into itself" destroyed the source once.
- Review both the helper (`Backup`/`Restore`) and the CLI wrapper: fixes often land in one and are bypassed by the other.

## Schema fingerprints and equality comparisons (`DeskDbInitializer.ReadLayoutAsync`)

A fingerprint that matches "by name" is the canonical elite-class defect of this repo. It must cover **every** semantic element, read from SQLite pragmas each time the schema changes:

- Columns via `PRAGMA table_info`: name **plus** declared type (col 2), NOT NULL (col 3), primary-key position (col 5), and the default clause (col 4).
- Foreign keys via `PRAGMA foreign_key_list`: the **referenced table (col 2)** — `Users(UserId->Id)` and `Assets(UserId->Id)` must never share a fingerprint — plus source/target columns and the `on update`/`on delete` actions (cols 5, 6).
- Indexes via `PRAGMA index_list` (unique flag incl. UNIQUE/PK auto-indexes, origin) and `PRAGMA index_info` (exact key columns, `#expression` for expression keys).
- Partial indexes: the WHERE condition from `sqlite_master.sql`, whitespace-normalised.

When reviewing a mutation that claims to prove fingerprint strictness, require: mutations applied to **exactly one** DDL statement (asserted), whitespace-normalised matching, and a faithful-rebuild control that must still adopt — otherwise the rebuild pipeline itself may be broken and the test vacuous.

## Check-then-write invariants (`DeskService.LockUserAsync`)

- Any "refuse if condition" guard followed by a write (e.g. the last active administrator) is a race when the check runs outside the save transaction. The correct shape: `BeginTransactionAsync(IsolationLevel.Serializable)` (BEGIN IMMEDIATE in Microsoft.Data.Sqlite) taken **before** the count, held through the write and the audit insert.
- Review the forcing function, not the test list: the test must make **two concurrent requests fight over the same invariant** and assert the end state (exactly one lock accepted, the invariant holds), not just check a sequential case.

## Two-step writes (`MarkBaselineAppliedAsync` and similar)

- "Create X, then write into X" must share one explicit transaction; otherwise a failure in between leaves a half-initialised state that the next start interprets differently (empty history table → misdiagnosed as migration-managed).
- Require a failure-in-between test with a fault-injection seam (here: `DeskDbInitializer.BetweenHistoryWrites` via `InternalsVisibleTo`), asserting nothing half-written survives and a clean retry succeeds.

## Claims and evidence

- Investigate before trusting any "suite is green": confirm the binary is fresh (a plain `Copy-Item` restore preserves `LastWriteTime`, MSBuild skips the rebuild, and tests run old code — see `desk-fix`), confirm the test count actually changed as claimed, and require Debug + Release runs for evidence.
- Unreviewed tools' review of this repo missed the same classes three times; every finding should name file, method and line, and every accepted "already fixed" claim is false until re-verified in code (the referenced-table FK gap survived one full round because it was assumed fixed).
- Red-before-green is part of review: for each fix, the paired old code must fail its own test. A fix whose test was never red is unproven.

## CI and shipping

- A green run on the branch commit is not the shipping gate; require `conclusion: success` on the **exact merged main SHA**, polled via `api.github.com/repos/<owner>/<repo>/actions/runs?per_page=2` matching `head_sha`.
- Commit messages carry the "(N/N green)" suffix; evidence lives in `docs/evidence/YYYY-MM-DD-<topic>.md`.
