# AI Chat with Custom Data

This project is an AI chat application that demonstrates how to chat with custom data using an AI language model. Please note that this template is currently in an early preview stage. If you have feedback, please take a [brief survey](https://aka.ms/dotnet-chat-templatePreview2-survey).

>[!NOTE]
> Before running this project you need to configure the API keys or endpoints for the providers you have chosen. See below for details specific to your choices.

# Configure the AI Model Provider
## Using OpenAI

To call the OpenAI REST API, you will need an API key. To obtain one, first [create a new OpenAI account](https://platform.openai.com/signup) or [log in](https://platform.openai.com/login). Next, navigate to the API key page and select "Create new secret key", optionally naming the key. Make sure to save your API key somewhere safe and do not share it with anyone.

From the command line, configure your API key for this project using .NET User Secrets by running the following commands:

```sh
cd <<your-project-directory>>
dotnet user-secrets set OpenAI:Key YOUR-API-KEY
```


---

## Production deployment checklist

### Required environment (`.env` — see `.env.example`)

| Key | Purpose |
|---|---|
| `OpenRouter__ApiKey` | LLM calls |
| `Authentication__Google__*` | login (app **fails fast at boot** in Production if missing) |
| `ConnectionStrings__Postgres`, `POSTGRES_*` | database |
| `WebSearch__ApiKey` | optional Tavily key — enables the agent's `web_search` tool |
| `S3_ENDPOINT`, `S3_REGION`, `S3_ACCESS_KEY_ID`, `S3_SECRET_ACCESS_KEY` | backup cron → `s3://aidagent/backups/` (see *Backups*) |
| `BACKUP_WEBHOOK_URL` | optional Discord/Slack/Telegram URL — failure alerts only |
| `ASPNETCORE_ENVIRONMENT` | `Production` behind the proxy |
| `Admin__Emails__0` = your Google email | **grants access to `/admin`** (or add to `appsettings.json` `Admin:Emails` — that file is committed, so only add non-secret values you're happy to share) |

### Stack

```sh
docker compose up -d        # app + postgres + redis (all healthchecked)
```

- **Redis** (`Redis:Enabled=true` only in compose): DataProtection keys
  (logins survive redeploys) + shared chat rate limiting + conversation-list cache.
  Local `dotnet run` falls back to memory automatically (`Redis:Enabled=false`).
- **Database migrations**: compose applies them on boot (`Database__AutoMigrate=true`).
  For a local DB run `dotnet ef database update` once (pending: `AddUsageEvents`).
- **Rate limit**: `RateLimit:ChatPerMinute` (default 20, `0` disables).
- **Daily budget** (`Budget:*`): `Budget:DailyUsd` caps what one user may spend per **UTC**
  day (default `0` = off, no spend is read and nothing changes). `Budget:WarningPercent`
  (default 80) warns from that share of the limit but still runs the turn; at 100 % the turn
  is refused with a status line. Spend is the `CostUsd` sum of the day's usage events, i.e.
  the same dollars `/admin` reports; a failed read logs a warning and lets the turn through.
- **Document OCR** (`Ocr:*`): `Ocr:MaxPages` (default 50) and `Ocr:OcrTimeoutSeconds`
  (default 120) bound a *scanned* PDF — a page limit only fires when a page actually
  needs OCR, so a long text PDF is still read. `Ocr:Enabled` (default true),
  `Ocr:Language` (default `vie+eng`) and `Ocr:MinLettersPerPage` (default 20) sit beside them.
- **Model pricing**: fetched live from the OpenRouter catalog
  (`GET /api/v1/models`) at boot and every 6 h — no prices in config. Models
  absent from the catalog show as "unpriced" in `/admin`; a failed refresh
  logs a warning and retries on the next cycle.
- **Forwarded headers** are trusted from any address (single-proxy setup) so the
  Google OAuth callback sees `https`. If you expose the app *directly* to the
  internet without a proxy, revisit `ForwardedHeadersOptions` in `Program.cs`.
- **Admin portal**: `/admin` requires the `Admin` role — members of
  `Admin:Emails` receive it automatically at Google login (dev user gets it via
  `/dev-login` in Development).

### Tests — OCR integration test and image smoke check

```sh
dotnet test                       # 278 tests; the OCR IT self-skips unless AIDA_OCR_IT=1
./scripts/ocr-it.sh               # OCR IT in a Linux container (podman + dotnet SDK image + tesseract vie/eng)
./scripts/smoke-ocr.sh [--keep]   # build the app image, check tesseract/vie/aliases inside it, remove it
```

Both scripts need a container engine (`CONTAINER_ENGINE`, default `podman`); the Tesseract native
names the NuGet interop expects are aliased from the distro sonames by `scripts/link-tesseract-libs.sh`,
which is also the step CI (`.github/workflows/ci.yml`) runs before `dotnet test`.

### Backups — `scripts/backup.sh`

```sh
./scripts/backup.sh --dry-run      # prove pg_dump + archive, no upload
./scripts/backup.sh                # first real upload
./scripts/backup.sh --install-cron # daily 02:00 Asia/Ho_Chi_Minh + logrotate
```

- **Payload**: `pg_dump` → `chatbot_<dd-MM-yyyy_HH-mm-ss>.dump.gz` + config
  archive (`.env`, `appsettings*`, compose, Dockerfile, `docs/`, `prompts/`)
  → `s3://aidagent/backups/`; upload tool auto-detected (`mc` > `aws` > `rclone`);
  30-day prune (`BACKUP_KEEP_DAYS`).
- **Tracing**: every log line carries `[run=<id>]` + per-stage durations →
  `/var/log/aida-backup.log` (logrotated weekly, keep 8); one JSON object per
  run → `/var/log/aida-backup-runs.jsonl` (`BACKUP_TRACE_FILE`).
- **Alerts**: failures only, to `BACKUP_WEBHOOK_URL` (Discord/Slack/Telegram
  auto-detected) — the alert carries the same `run=` id as the log lines.

### Observability — OpenTelemetry + Jaeger

Compose runs Jaeger; the app exports when `OpenTelemetry__Endpoint` is set
(compose sets `http://jaeger:4318/v1/traces`). UI: `http://localhost:16686`.

- **One trace per request**: Blazor circuit → EF Core queries → HttpClient
  calls (OpenRouter, Tavily, SystemOne).
- **Nested `llm <model>` spans per model call** (`Aida.Llm`) tagged with
  `aida.mode`, `aida.step` (`plan` / `after read_document` / `worker: …`),
  `gen_ai.usage.input_tokens` / `output_tokens`, `aida.tool_calls` — an Agent
  turn shows plan → tool rounds → synthesis as child spans of one trace.
- Local opt-in: `OpenTelemetry__Endpoint=http://localhost:4318/v1/traces`.
- ⚠️ Jaeger storage is in-memory — history resets when the container restarts.


### Known local-dev gotcha
`ConnectionStrings__Postgres` in `.env` must match your running Postgres —
if `dotnet ef` reports `28P01 password authentication failed`, the password
in `.env` is stale for that server.
