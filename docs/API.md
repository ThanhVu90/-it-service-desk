# API quick reference

All `/api` responses are non-cacheable. Fetch `/api/csrf` before the first POST, retain its cookie and send its `token` in `X-CSRF-TOKEN`. Fetch again after login because the antiforgery token is tied to identity. The UI implements this flow in `app.js`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Process liveness only; not a database readiness check |
| GET | `/api/csrf` | Obtain antiforgery token |
| POST | `/api/login` | `{email,password}`; issue session cookie |
| GET | `/api/me` | Current signed-in identity |
| POST | `/api/logout` | Clear this browser's session cookie |
| GET | `/api/dashboard` | Counts restricted to visible tickets |
| GET | `/api/tickets?search=&status=&page=1&pageSize=10` | Paginated visible tickets; pageSize max 50 |
| GET | `/api/tickets/{id}` | Visible ticket and its audit events |
| POST | `/api/tickets` | `{title,description,priority}`; requester comes from identity |
| POST | `/api/tickets/{id}/transition` | `{version,status,resolution}` |
| GET | `/api/assets` | Staff inventory or employee's current allocations |
| POST | `/api/assets` | Admin: `{tag,model}` |
| POST | `/api/assets/{id}/assign` | Admin: `{userId,version}` |
| POST | `/api/assets/{id}/return` | Admin: `{version}` |
| GET | `/api/assets/{id}/history` | Admin: handover history |
| GET | `/api/users` | Admin: recipient ID/name/role; never password hashes |

Priority: `Low`, `Normal`, `High`. Status: `Open`, `InProgress`, `Resolved`, `Closed`.

Errors use Problem Details where handled: 400 invalid input/CSRF, 401 no valid login, 403 role restriction, 404 missing or non-visible ticket, 409 state/version/data conflict, 429 login rate limit. Some framework-generated errors may have an empty body. Clients must handle both.

A 409 requires reading the current state and deciding again, not blind automatic resubmission. No delete operation is exposed. These endpoints do not accept externally chosen requester IDs or roles.
