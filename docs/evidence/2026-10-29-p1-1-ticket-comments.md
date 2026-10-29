# Evidence — P1-1: ticket comments within visibility (29 October 2026)

Branch `upgrade/p1-1-ticket-comments`, based on merged `main` containing P0-1 + P0-2.

## Environment

Windows 11 x64, .NET SDK 10.0.403, EF Core 10.0.14. Tests run against fresh SQLite files created by the test factory; no personal data.

## Sequence

1. **Entity + mapping + migration first** (`4abedfb`): `TicketComment` (TicketId, AuthorId, Body ≤ 2000, CreatedAt UTC-stored), index `IX_TicketComments_TicketId_CreatedAt`, restrictive FKs. Existing suites stayed green.
2. **Acceptance tests written first** (`8d95f01`): 7 tests in `CommentTests` — 6 **red** (missing endpoints: 404 everywhere, audit row missing, paging unbounded), 1 already green (`Outsider_cannot_read_or_write…`, the visibility guard). Baseline 42 stayed green.
3. **Implementation:**
   - `DeskService.AddComment` / `ListComments` resolve the ticket strictly through `Visible(actor)`; unauthorized reads and writes are indistinguishable from "ticket not found" (404).
   - Validation via the shared `Rules.Text` (non-empty, ≤ 2000 chars) → 400 otherwise.
   - `Audit(actor, "Ticket", id, "Commented")` written in the same `SaveChangesAsync` transaction as the comment.
   - Endpoints in `Program.cs`: GET with paging guard (page ≥ 1, pageSize 1..50 → 400 outside), POST returns 201 with the created comment.
   - UI: static `Discussion` section in the ticket dialog, replies loaded into it, submit posts through the CSRF-checked `api()` helper and reloads the thread.
4. **Full suite: 49/49 green** (`679d7f3`).

## Acceptance criteria mapping

| Guide criterion | Proof |
|---|---|
| Outsider cannot read or write others' tickets' comments | `Outsider_cannot_read_or_write_comments_of_someone_elses_ticket` (404 both ways, 0 rows persisted) |
| Comments flow within visibility (employee + technician) | `Employee_and_technician_exchange_comments_within_visibility` (201, 201, both listed in order with bodies intact) |
| Script text stored/served as plain data, UI never uses innerHTML | `Script_text_is_returned_as_plain_data` — exact string round-trip, and a static scan of `renderComments` (last function of `app.js`) finds no `innerHTML`; all content added via `textContent` |
| Empty / oversized rejected, nothing saved | `Empty_and_oversized_comments_are_rejected_without_saving` (blank, 2001 chars, empty → 400 ×3, count 0) |
| One audit entry per comment, visible in ticket detail | `Commenting_records_one_audit_entry_and_survives_in_detail` (`Commented` ×1 in `/api/tickets/1` audit; technician sees the same comment) |
| Audit failure rolls the comment back | `Comment_write_is_rolled_back_when_audit_insert_fails` (injected trigger → 409, comment count 0, audit count 0) |
| Paging bounded, no dup/loss on equal timestamps | `Paging_is_bounded_and_never_dups_or_loses_same_timestamp_comments` (3 rows same timestamp → pages yield ids 1,2,3 uniquely; `pageSize=200` → 400) |

## Notes & limits

- Comments are immutable in this release (no edit/delete endpoints); moderation would be a follow-up.
- CSRF applies to the comment POST like every mutation; visibility denies with 404s rather than 403s, so the existence of hidden tickets is not leaked.
- The 2000-char cap matches the resolution-note cap and is enforced at the DB column boundary as well.
- UI cap in the textarea (`maxlength="2000"`) mirrors the server so users get instant feedback; the server remains the enforcement point.

## Demo note

Open a ticket as its requester, add a reply, and show it in `Activity` as `Commented`. Sign in as an unrelated user and try the same URL (`/api/tickets/1/comments`) — 404, and the queue does not show the ticket at all. Post `<script>alert(1)</script>` as a reply: it renders as literal text, never executes.
