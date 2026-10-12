# Deploying & operating TimeFlip2Crm

Background service that syncs registered time from **TimeFlip 2** into the
**CRM** (<https://crm.green-cloud.nl>) as time entries. Runs as a Docker
container; one CRM time entry per TimeFlip session (two when a session runs past
midnight), idempotent (safe to re-run — it never creates duplicates).

Sibling of TimeFlip2MoneyBird; both can run side by side on the Mac mini
(`household.local`). This one listens on host port **8081**.

---

## 1. Prerequisites

- Docker Desktop (macOS) — `docker` + `docker compose` available.
- A **CRM personal API token**: in the CRM go to **Profile → API tokens**, create
  one with the scopes **time.read · time.write · links.read** and a validity of
  30, 90 or 365 days. The token (`crm_pat_…`) is shown once.
  It writes hours as *you*, in the organization that was active when you made it.
  **It expires** — put a reminder in your calendar to replace it (see §8).
- TimeFlip account email + password.

## 2. Configuration

Secrets and tunables come from `.env` (gitignored, not baked into the image);
task mapping is edited in the web UI and stored in the database.

### `.env` (secrets + tunables)
Copy `.env.example` → `.env` and fill in:

| Key | Meaning |
|-----|---------|
| `TimeFlip__Email` / `TimeFlip__Password` | TimeFlip login |
| `Crm__Token` | personal API token (`crm_pat_…`) |
| `Crm__BaseUrl` | CRM root (default `https://crm.green-cloud.nl`) |
| `Crm__TimeZone` | organization time zone (default `Europe/Amsterdam`) |
| `Sync__IntervalMinutes` | how often to sync (default 60) |
| `Sync__LookbackDays` | window re-scanned each *scheduled* pass, catches late edits (default 1) |
| `Sync__MaxManualLookbackDays` | largest window a manual sync may request (default 90) |
| `Sync__MinIntervalSeconds` | ignore sessions shorter than this — filters accidental cube flips (default 60) |
| `Sync__DryRun` | `true` = log actions, write nothing to the CRM |
| `Sync__SkipUnmapped` | `true` (default) = ignore tasks with no mapping rule, e.g. "Break" |
| `Sync__TriggerToken` | optional secret protecting `POST /sync` (sent as `X-Trigger-Token`) |

### Task mapping — the web UI
Mapping rules live in the SQLite database on the `/data` volume and are edited in
the browser at **<http://household.local:8081/>** — see §5.

> ⚠️ The UI has no login. Anyone who can reach port 8081 on the LAN can change the
> mapping. Keep the port off the public internet.

## 3. Run

```bash
docker compose up -d --build     # build image + start
docker compose logs -f           # follow logs
docker compose ps                # status (should be "healthy")
```

**Recommended first run:** set `Sync__DryRun=true`, start, add your rules in the
UI, press **Sync now**, and confirm the logs show the expected
`[dry-run] would create entry … 2026-10-05 09:00-10:30 …` lines (local times).
Then set `Sync__DryRun=false` and `docker compose up -d` again to go live.

Dry-run without starting the loop — just print sessions + how they'd map:

```bash
docker compose run --rm timeflip-crm-sync --dump
docker compose run --rm timeflip-crm-sync --dump --days 30   # wider window
```

## 4. Endpoints (port 8081 on the host)

| Endpoint | Purpose |
|----------|---------|
| `GET /` | mapping UI (see §5) |
| `GET /healthz` | health check (JSON: last run/success, summary). Used by Docker HEALTHCHECK. |
| `POST /sync` | run a sync pass now. `?lookbackDays=N` re-scans further back (1…`Sync__MaxManualLookbackDays`). `200` + summary, `409` if already running, `401` on bad token, `400` on an out-of-range window. |
| `GET/POST /api/rules`, `PUT/DELETE /api/rules/{id}` | mapping rules (JSON, no auth) |
| `GET /api/timeflip/tasks` | cube tasks, for the UI's task picker |
| `GET /api/crm/links/{task\|project\|relation}` | CRM search for the link picker; `?search=` narrows by name, `?refresh=true` bypasses the 5-minute cache |
| `GET /api/status` | last run/success/summary + whether a pass is running |

```bash
curl http://localhost:8081/healthz
curl -X POST http://localhost:8081/sync        # add -H "X-Trigger-Token: <token>" if set
curl -X POST "http://localhost:8081/sync?lookbackDays=30"   # catch up on older edits
curl http://localhost:8081/api/rules
```

## 5. Managing the task mapping (web UI)

Open **<http://household.local:8081/>**. The page shows the sync status and the list
of mapping rules, and lets you add, edit and delete them:

- **TimeFlip task** — pick from the cube's tasks (stores the task *id*, so it keeps
  matching after a rename), or type a name for a task the cube hasn't reported yet.
- **Link to in the CRM** — choose *Task*, *Project* or *Relation*, then search by
  name and pick one. One link is enough: the CRM fills in the rest (a task brings
  its project and relation; a project brings its relation). Only open tasks and
  projects are offered.
- **Billable** — *CRM default* leaves it to the CRM (the project's or relation's
  setting; work for a relation is billable, internal work is not).
- **Description** — `{task}` and `{date}` placeholders, previewed as you type.

Rules are matched top to bottom and **the first match wins**; new rules go to the
bottom of the list. The table shows the CRM name as it was when the rule was
saved; renaming it in the CRM doesn't break the rule (it stores the id).

**Changes take effect on the next sync pass — no restart, no redeploy.** Press
**Sync now** to apply them immediately. Editing a rule does not rewrite entries
that were already synced; delete those in the CRM and **Sync now** to recreate
them with the new rule.

Rules are stored in the same SQLite database as the sync state
(`/data/sync-state.db`, the `sync-state` Docker volume), so they survive
`docker compose up --build` but not deleting that volume.

## 6. Everyday operations

| Task | Command |
|------|---------|
| Change task mapping | edit in the web UI — takes effect on the next pass |
| Change interval/secrets | edit `.env`, then `docker compose up -d` |
| Force a sync | `curl -X POST http://localhost:8081/sync` |
| Re-sync older days | `curl -X POST "http://localhost:8081/sync?lookbackDays=30"`, or set **Days back** in the UI |
| View logs | `docker compose logs -f` |
| Stop / start | `docker compose down` / `docker compose up -d` |
| Rebuild after code change | `docker compose up -d --build` |

State (the session → CRM-entry map) lives in the `sync-state` Docker volume
(`/data/sync-state.db`). It's what makes re-runs idempotent — don't delete it unless
you want the service to re-evaluate every session in the lookback window.

## 7. Reboot survival (important on macOS)

Docker Desktop only runs while a user is logged in, so after a reboot the container
won't return on its own. To make it always-on:

1. **Docker Desktop → Settings → General → ✅ "Start Docker Desktop when you log in"**
2. **System Settings → Users & Groups → enable automatic login**

The container's `restart: unless-stopped` policy then brings it back automatically.

## 8. Troubleshooting

CRM errors carry a stable code in square brackets in the logs and the UI.

- **`[api.token_expired]` / `[api.token_invalid]`** — the token ran out or was
  revoked. Create a new one (§1), put it in `.env` as `Crm__Token`, then
  `docker compose up -d`. Nothing is lost: the next pass catches up on the
  lookback window; use **Days back** if the token was dead for longer.
- **`[api.insufficient_scope]` … token needs scope …** — the token lacks a scope;
  make a new one with all three (§1).
- **`[api.two_factor_required]`** — the organization requires 2FA and your account
  has none. Set it up in the CRM.
- **`[validation_failed]` / `[timetracking.…]` on create** — the CRM rejected the
  entry, usually because the linked task/project is closed, in the trash, or a
  private task of someone else. Re-pick the link in the UI.
- **"… lasts 25 h; the CRM only takes entries under 24 h"** — the cube was left on.
  Shorten or split the session in TimeFlip; the next pass picks it up.
- **`Unhealthy` / `POST /sync` returns 500** — check `docker compose logs`. Most often
  one of the CRM codes above, or a TimeFlip auth failure (wrong credentials).
- **Everything shows "SKIP (unmapped)"** — no rule matches. Open the UI and check the
  rules, or run `--dump` to see the exact task names and ids TimeFlip reports.
- **The link picker says "Could not search the CRM"** — the message after it is the
  CRM's reason; usually the token (see above).
- **A wrong entry landed in the CRM** — delete it in the CRM, fix the rule in the UI,
  then **Sync now**. It's recreated from the corrected mapping.
- **Duplicate entries** — shouldn't happen; the SQLite state prevents it. If the volume was
  wiped while entries already existed in the CRM, the service would recreate them.

## 9. API notes (for maintainers)

**CRM** (spec: <https://crm.green-cloud.nl/apidocs/v1.json>):
- Times are a local `date` + `startTime`/`endTime` (`HH:mm`) in Europe/Amsterdam;
  TimeFlip's instants are rounded to the nearest minute and converted. An end before
  the start means the next day: the CRM then **splits the entry at midnight** and
  answers with two entries. The sync state stores every id per session.
- Updating (`PUT`) a split entry keeps the first and only ever *adds* a next-day
  entry, so the sync deletes a stale next-day entry itself when an edit shortens a
  session.
- Deletion is detected with one `GET /time/entries?from&to` listing per pass (not a
  `GET` per entry), to stay under the **120 requests/minute** per-token limit. 429s
  are retried after `Retry-After`.
- Link lookups return at most 100 results and have no lookup by id, which is why
  rules store the link's name for display.

**TimeFlip** — the live API differs from its published Swagger:
- Intervals join to tasks by `taskLocalId` == task `localId` (`taskId` is usually null).
- `startedAt` offset is `+02` (no minutes) — normalized before parsing.
- `duration` is in **seconds**. Auth: `POST /api/auth/email/sign-in` returns the JWT in a
  `token` response header; sent back as both `Token` and `Authorization: Bearer`.
- Source endpoint: `GET /api/sync/all` (all tasks + intervals; filtered client-side).
