# Aida — AI chat over your own data

Aida is a self-hosted AI chat application with a Vietnamese UI. It answers questions over
documents you upload (retrieval-augmented generation on PostgreSQL + pgvector), can hand
multi-step work to a small crew of agents, and enforces a per-user daily usage budget.
It runs as a .NET 10 Blazor app in a `docker-compose.yaml` stack (podman/docker) together
with PostgreSQL and Redis.

## Features

- **Chat** — streaming conversations over OpenRouter models (`Models:ChatModels` in
  `appsettings.json`), conversation sidebar, regenerate/steer/cancel, model labels and
  provider icons.
- **Documents & RAG** — upload `.pdf`, `.md`, `.docx`, `.xlsx` (multiple files, 5 MB each),
  plus images. Files are chunked, embedded and searched with pgvector; answers carry
  citations back to the source chunks. Each user only sees their own documents.
- **OCR** — scanned PDFs are read locally with Tesseract (`vie+eng`, installed in the image),
  bounded by `Ocr:MaxPages`, `Ocr:OcrTimeoutSeconds`, `Ocr:MinLettersPerPage`.
- **Agent tools** — `read_document`, `save_document` (exports the answer as `.docx` or `.pdf`),
  `web_search` (Tavily, only when `WebSearch__ApiKey` is set), `ask_user`, `tool_search`,
  plus the crew tools `parallel_research` and `draft_document` (Director / Researcher / Writer
  models and thinking levels are configurable per user in Settings).
- **Auth** — Google sign-in and local email/password accounts. Google OAuth is **required in
  Production**: the app fails fast at boot without it. Emails listed in `Admin:Emails` get the
  `Admin` role automatically at Google login; Development offers `/dev-login`.
- **Admin panel** — `/admin` (Admin role): requests, tokens, cost per UTC day, per model and
  per user, with unpriced calls called out.
- **Usage budget & rate limit** — `Budget:DailyUsd` caps what one user may spend per UTC day
  (`0` = off, `Budget:WarningPercent` warns before the limit); `RateLimit:ChatPerMinute`
  limits chat turns (`0` disables).
- **Backups** — `scripts/backup.sh`: `pg_dump` + config archive to S3-compatible storage
  (`mc` > `aws` > `rclone` auto-detected), optional cron, failure alerts to a webhook.
- **Tracing** — optional OpenTelemetry export (ASP.NET Core, EF Core, HttpClient and
  `Aida.Llm` model-call spans) when `OpenTelemetry__Endpoint` is set.

## Tech stack

| Layer | Choice |
|---|---|
| Runtime | .NET 10, Blazor Server (interactive server components) |
| Data | EF Core + Npgsql + pgvector, PostgreSQL 17 (`pgvector/pgvector:pg17`) |
| Cache / state | Redis 7 (DataProtection keys, distributed cache, shared rate limiting) |
| LLM | OpenRouter (chat, embeddings, live model pricing catalog) |
| OCR | Tesseract 5 with `vie` + `eng` models (installed in the Dockerfile) |
| Files | QuestPDF (PDF), OpenXML (DOCX) |
| Packaging | `docker-compose.yaml` / `Dockerfile`, default engine `podman` |
| Tests | xUnit in `tests/aida.Tests` |

## Quickstart

```sh
cp .env.example .env
# edit .env — at minimum set POSTGRES_PASSWORD (and keep ConnectionStrings__Postgres in sync
# for local `dotnet run`; compose rebuilds it from POSTGRES_*)

# the compose file references both as external
podman network create edge-network
podman volume create aida-postgresql

podman compose up -d --build
```

The app listens on `http://127.0.0.1:8080` (Redis on `127.0.0.1:6379`).

First-run notes:

- **Production fails fast without Google OAuth**: `Authentication__Google__ClientId` and
  `Authentication__Google__ClientSecret` must be set, otherwise boot throws
  (`Missing Google OAuth configuration…`). The same applies to `ConnectionStrings__Postgres`.
- **Admin access**: set `Admin__Emails__0=<your Google email>` in `.env` — that address
  receives the `Admin` role at Google login and can open `/admin`. Non-secret values can also
  go into the committed `Admin:Emails` array in `appsettings.json`.
- **Migrations**: compose applies them on boot (`Database__AutoMigrate=true`).
- **Development without Docker**: create a local DB, set `.env`, then `dotnet run` and open
  `/dev-login` for an instant admin dev user.

## Configuration

All keys live in `.env` (start from `.env.example`):

| Key | Purpose |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` locally, `Production` behind the proxy |
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | database created by the postgres container; compose builds the app's connection string from them |
| `ConnectionStrings__Postgres` | connection string used by local `dotnet run` (compose overrides it with `POSTGRES_*`) |
| `OpenRouter__ApiKey` | LLM calls, embeddings and the live pricing catalog |
| `Budget__DailyUsd` | per-user spend cap per UTC day in USD; `0` = budget off |
| `Budget__WarningPercent` | share of the cap that warns (default `80`; values outside 1–100 fall back to 80) |
| `WebSearch__ApiKey` | optional Tavily key — enables the agent's `web_search` tool; empty removes the tool |
| `Authentication__Google__ClientId`, `Authentication__Google__ClientSecret` | Google login; mandatory in Production (boot fails without them) |
| `S3_ENDPOINT`, `S3_REGION`, `S3_ACCESS_KEY_ID`, `S3_SECRET_ACCESS_KEY` | backup upload target (`S3_BUCKET`/`S3_PREFIX`, default `s3://aidagent/backups/`) |
| `BACKUP_WEBHOOK_URL` | optional Discord/Slack/Telegram URL — backup failure alerts only |

Other keys deployments commonly set (environment variables or `appsettings.json`):

| Key | Purpose |
|---|---|
| `Admin__Emails__0`, `Admin__Emails__1`, … | emails granted the `Admin` role at Google login |
| `RateLimit__ChatPerMinute` | chat turns per minute per user (default `20`, `0` disables) |
| `Database__AutoMigrate` | run EF migrations at boot (compose sets `true`) |
| `Redis__Enabled`, `Redis__ConnectionString` | compose sets `true` + `redis:6379`; local `dotnet run` defaults to `false` and falls back to in-memory cache/DataProtection |
| `OpenTelemetry__Endpoint` | OTLP trace endpoint; unset = no tracing |
| `Ocr__Enabled`, `Ocr__Language`, `Ocr__MinLettersPerPage`, `Ocr__MaxPages`, `Ocr__OcrTimeoutSeconds` | OCR bounds (defaults: on, `vie+eng`, `20`, `50`, `120`) |
| `OpenRouter__BaseUrl`, `Models:*`, `Crew:*` | model selection and crew defaults in `appsettings.json` (committed, non-secret) |
| `BACKUP_KEEP_DAYS`, `BACKUP_TZ`, `BACKUP_TRACE_FILE` | backup pruning (default 30 days), cron timezone (default `Asia/Ho_Chi_Minh`), trace file |

Model pricing is **not** configured anywhere: it is fetched from the OpenRouter catalog at
boot and refreshed every 6 hours. Models missing from the catalog appear as unpriced in
`/admin`; a failed refresh logs a warning and retries on the next cycle.

## Architecture

`Program.cs` wires one Blazor Server app:

1. `Env.Load()` reads `.env`, then identity + EF Core register with a **fail-fast** check for
   the connection string and (outside Development) Google OAuth.
2. With `Redis:Enabled` the app persists DataProtection keys (logins survive redeploys),
   the distributed cache and the shared chat rate limiter in Redis; otherwise it falls back
   to in-memory equivalents.
3. Middleware: forwarded headers → exception handler + HSTS + HTTPS redirect (non-Development)
   → authentication/authorization → antiforgery → static files → `MapRazorComponents`,
   auth endpoints (`/account/*`, `/dev-login`), document endpoint (`GET /documents/{name}`),
   auto-migrate, then run.
4. Background services: OpenRouter pricing refresh (6 h); scoped services handle ingestion,
   semantic search, usage tracking and the daily budget.

`docker-compose.yaml` runs three services — `app` (built from the Dockerfile, Tesseract
baked in), `postgres` and `redis`, both healthchecked — plus an external `edge-network` and
an external `aida-postgresql` volume.

## Development

```sh
dotnet run                          # http://localhost:5172 (or https://localhost:7141)
dotnet test                         # unit tests; the OCR integration test self-skips without AIDA_OCR_IT=1
```

OCR-related helpers (need a container engine, `CONTAINER_ENGINE` defaults to `podman`):

```sh
./scripts/ocr-it.sh                 # OCR IT in a Linux container (SDK image + tesseract vie/eng)
./scripts/smoke-ocr.sh [--keep]      # build the app image, verify tesseract/vie/aliases inside it
./scripts/link-tesseract-libs.sh DIR # alias distro sonames next to a test assembly (what CI runs)
```

Local gotcha: `ConnectionStrings__Postgres` in `.env` must match your running Postgres — a
stale password shows up as `28P01 password authentication failed` from `dotnet ef`. For a
local DB run `dotnet ef database update` once.

## API

A machine-readable OpenAPI document is served at `/openapi/v1.json` for clients that want to
call the HTTP endpoints directly.

## Production deployment checklist

- [ ] `.env` filled in — see the configuration table above; never commit it.
- [ ] Google OAuth credentials set: the app **refuses to start** in Production without
      `Authentication__Google__ClientId` / `Authentication__Google__ClientSecret`.
- [ ] `Admin__Emails__0` set to the admin's Google email.
- [ ] Behind a proxy: forwarded headers are trusted from **any** address (single-proxy
      setup) so the Google OAuth callback sees `https`. If you expose the app directly to
      the internet, revisit `ForwardedHeadersOptions` in `Program.cs`.
- [ ] `ASPNETCORE_ENVIRONMENT=Production`.
- [ ] Redis available (compose provides it) so DataProtection keys and rate-limit counters
      survive redeploys.
- [ ] Backups:

  ```sh
  ./scripts/backup.sh --dry-run      # prove pg_dump + archive, no upload
  ./scripts/backup.sh                # first real upload
  ./scripts/backup.sh --install-cron # daily 02:00 (BACKUP_TZ, default Asia/Ho_Chi_Minh)
  ```

  Payload: `chatbot_<dd-MM-yyyy_HH-mm-ss>.dump.gz` + a config archive (`.env`,
  `appsettings*`, compose file, Dockerfile, `docs/`, `prompts/`) to `s3://aidagent/backups/`;
  30-day prune (`BACKUP_KEEP_DAYS`). Every run writes `[run=<id>]` stage lines to
  `/var/log/aida-backup.log` and one JSON object per run to
  `/var/log/aida-backup-runs.jsonl` (`BACKUP_TRACE_FILE`). Failures alert to
  `BACKUP_WEBHOOK_URL` (Discord/Slack/Telegram auto-detected) with the same `run=` id.
- [ ] Optional: set `OpenTelemetry__Endpoint` (e.g. `http://<otlp-collector>:4318/v1/traces`)
      to export traces including nested `Aida.Llm` model-call spans.
