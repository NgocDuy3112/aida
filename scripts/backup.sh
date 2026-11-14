#!/usr/bin/env bash
# =============================================================================
# aida — daily backup to the S3-compatible bucket "aidagent", folder backups/
#
#   What is saved
#     1. chatbot_<dd-MM-yyyy_HH-mm-ss>.dump.gz   — pg_dump (custom format, gz)
#     2. aida-config_<dd-MM-yyyy_HH-mm-ss>.tar.gz — .env, appsettings*.json,
#        docker-compose.yaml, Dockerfile, docs/, prompts/
#
#   Timestamp is Vietnamese order: day-month-year, dashes instead of slashes
#   (slashes would create folders inside the bucket), clock in Asia/Ho_Chi_Minh.
#   Example: backups/chatbot_29-11-2025_02-00-00.dump.gz
#
#   Required lines in .env (the script sources them):
#     S3_ENDPOINT=https://<provider-endpoint>
#     S3_REGION=<region>                 # often "auto" or "us-east-1"
#     S3_ACCESS_KEY_ID=<key>
#     S3_SECRET_ACCESS_KEY=<secret>
#     # optional overrides:
#     # S3_BUCKET=aidagent  S3_PREFIX=backups  BACKUP_KEEP_DAYS=30
#     # BACKUP_TZ=Asia/Ho_Chi_Minh  PG_CONTAINER=aida-postgresql
#     # BACKUP_WEBHOOK_URL=https://discord.com/api/webhooks/… (failure alerts;
#     #   payload shape auto-detected: Discord / Slack / Telegram / generic)
#     # BACKUP_TRACE_FILE=/var/log/aida-backup-runs.jsonl
#
#   Upload tool: auto-detected — mc (MinIO Client) > aws cli > rclone.
#   Needs docker (for docker exec into the Postgres container).
#
#   Tracing
#     - every log line carries [run=<id>] — the same id appears in the Discord
#       failure alert, so an alert can be grepped straight out of the log
#     - per-stage durations are logged as each stage closes
#       (startup / pg_dump / config archive / upload / prune)
#     - one JSON object per run is appended to $BACKUP_TRACE_FILE (default
#       /var/log/aida-backup-runs.jsonl) — machine-readable run history:
#       {"run":"…","status":"ok|failed","exit":0,
#        "stamp":"29-11-2025_02-00-00","finished":"29-11-2025 02:00:14",
#        "stages":[{"name":"pg_dump","seconds":3}],
#        "files":[{"name":"chatbot_….dump.gz","bytes":4194304}]}
#     - successes stay off Discord; only failures post (EXIT trap, one shot)
#
#   Usage
#     backup.sh                run one backup now
#     backup.sh --dry-run      same, but stop before uploading
#     backup.sh --install-cron daily 02:00 Vietnam time + logrotate rule
#                              (no docker/S3 preconditions needed for install)
# =============================================================================
set -Eeuo pipefail

# ---------- config -----------------------------------------------------------
APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[[ -f "$APP_DIR/.env" ]] && set -a && . "$APP_DIR/.env" && set +a

BACKUP_TZ="${BACKUP_TZ:-Asia/Ho_Chi_Minh}"
S3_BUCKET="${S3_BUCKET:-aidagent}"
S3_PREFIX="${S3_PREFIX:-backups}"          # folder in the bucket
KEEP_DAYS="${BACKUP_KEEP_DAYS:-30}"
PG_CONTAINER="${PG_CONTAINER:-aida-postgresql}"
DB_NAME="${POSTGRES_DB:-chatbot}"
DB_USER="${POSTGRES_USER:-chatbot}"
TRACE_FILE="${BACKUP_TRACE_FILE:-/var/log/aida-backup-runs.jsonl}"
WORK=""                                   # temp dir, cleaned by the EXIT trap

# Vietnamese timestamp: 29-11-2025_02-00-00
STAMP="$(TZ="$BACKUP_TZ" date +'%d-%m-%Y_%H-%M-%S')"
# Correlation id shared by log lines, JSONL trace, and the Discord alert
RUN_ID="$(TZ="$BACKUP_TZ" date +%y%m%d-%H%M%S)-$$-${RANDOM}"

LOG() { printf '[%s] [run=%s] %s\n' \
  "$(TZ="$BACKUP_TZ" date '+%d-%m-%Y %H:%M:%S')" "$RUN_ID" "$*"; }
die() { LOG "ERROR: $*" >&2; exit 1; }

# ---------- tracing: stages, JSONL run summary, failure webhook --------------
STAGE="startup"; STAGE_T0=$SECONDS
STAGES_JSON=""; FILES_JSON=""; SKIP_TRACE=0

end_stage() {
  local d=$((SECONDS - STAGE_T0))
  if [[ -n "$STAGES_JSON" ]]; then STAGES_JSON+=","; fi
  STAGES_JSON+="{\"name\":\"$STAGE\",\"seconds\":$d}"
  LOG "stage '$STAGE' finished in ${d}s"
}

stage_begin() {           # close previous stage, open the next one
  end_stage
  STAGE="$1"; STAGE_T0=$SECONDS
  LOG "stage '$STAGE' starting"
}

record_file() {           # capture name + size for the JSONL summary
  local f="$1" b
  [[ -f "$f" ]] || return 0
  b="$(wc -c < "$f" | tr -d ' ')"
  if [[ -n "$FILES_JSON" ]]; then FILES_JSON+=","; fi
  FILES_JSON+="{\"name\":\"$(basename "$f")\",\"bytes\":$b}"
}

write_trace() {           # $1 = exit code — one JSON line per run
  local rc="$1" status="ok"
  if [[ $rc -ne 0 ]]; then status="failed"; fi
  local when
  when="$(TZ="$BACKUP_TZ" date +'%d-%m-%Y %H:%M:%S')"
  printf '{"run":"%s","status":"%s","exit":%d,"stamp":"%s","finished":"%s %s","stages":[%s],"files":[%s]}\n' \
    "$RUN_ID" "$status" "$rc" "$STAMP" "$when" "$BACKUP_TZ" \
    "$STAGES_JSON" "$FILES_JSON" >> "$TRACE_FILE" 2>/dev/null \
    || LOG "WARN: could not append trace to $TRACE_FILE"
}

notify_failure() {        # $1 = exit code — never fails the script itself
  local rc="$1"
  [[ -n "${BACKUP_WEBHOOK_URL:-}" ]] || return 0
  local when msg payload
  when="$(TZ="$BACKUP_TZ" date +'%d-%m-%Y %H:%M:%S')"
  msg="aida backup FAILED (run $RUN_ID, exit $rc, stage: $STAGE) on $(hostname) at $when $BACKUP_TZ — see /var/log/aida-backup.log"
  case "$BACKUP_WEBHOOK_URL" in
    *discord.com*)     payload="{\"content\":\"$msg\"}" ;;
    *hooks.slack.com*) payload="{\"text\":\"$msg\"}" ;;
    *api.telegram.org*) payload="{\"chat_id\":\"${BACKUP_TELEGRAM_CHAT_ID:-}\",\"text\":\"$msg\"}" ;;
    *)                 payload="{\"text\":\"$msg\",\"content\":\"$msg\",\"message\":\"$msg\"}" ;;
  esac
  curl -fsS -m 10 -H 'Content-Type: application/json' -d "$payload" \
    "$BACKUP_WEBHOOK_URL" >/dev/null 2>&1 || true
}

on_exit() {               # ONE trap: cleanup + trace + failure alert
  local rc=$?
  if [[ "$SKIP_TRACE" != "1" ]]; then
    end_stage
    write_trace "$rc"
  fi
  if [[ $rc -ne 0 ]]; then notify_failure "$rc"; fi
  if [[ -n "$WORK" && -d "$WORK" ]]; then rm -rf "$WORK"; fi
  return 0
}
trap on_exit EXIT

# ---------- cron + logrotate install ------------------------------------------
if [[ "${1:-}" == "--install-cron" ]]; then
  SKIP_TRACE=1
  MARKER="# aida-backup (managed by scripts/backup.sh)"
  ENTRY="CRON_TZ=$BACKUP_TZ"
  LINE="0 2 * * * $APP_DIR/scripts/backup.sh >> /var/log/aida-backup.log 2>&1"
  if crontab -l 2>/dev/null | grep -qF "$MARKER"; then
    LOG "cron already installed"
  else
    { echo "$MARKER"; echo "$ENTRY"; echo "$LINE"; } | crontab -
    LOG "cron installed: daily 02:00 $BACKUP_TZ"
  fi
  crontab -l | tail -3

  # logrotate rule for /var/log/aida-backup.log (weekly, keep 8, compress)
  LR_FILE=/etc/logrotate.d/aida-backup
  LR_SRC="$APP_DIR/scripts/aida-backup.logrotate"
  cat > "$LR_SRC" <<'EOF'
/var/log/aida-backup.log {
    weekly
    rotate 8
    compress
    missingok
    notifempty
    copytruncate
}
EOF
  if [[ $EUID -eq 0 ]]; then
    cp "$LR_SRC" "$LR_FILE" && LOG "logrotate installed -> $LR_FILE"
  elif command -v sudo >/dev/null && sudo -n true 2>/dev/null; then
    sudo cp "$LR_SRC" "$LR_FILE" && LOG "logrotate installed -> $LR_FILE"
  else
    LOG "WARN: logrotate not installed (need root) — copy $LR_SRC to /etc/logrotate.d/ manually"
  fi
  exit 0
fi

# ---------- preconditions ----------------------------------------------------
[[ -n "${S3_ENDPOINT:-}" ]]         || die "S3_ENDPOINT missing in .env"
[[ -n "${S3_ACCESS_KEY_ID:-}" ]]    || die "S3_ACCESS_KEY_ID missing in .env"
[[ -n "${S3_SECRET_ACCESS_KEY:-}" ]] || die "S3_SECRET_ACCESS_KEY missing in .env"
command -v docker >/dev/null || die "docker not found"
docker info >/dev/null 2>&1  || die "docker daemon not reachable (need sudo?)"

# ---------- dump -------------------------------------------------------------
WORK="$(mktemp -d /tmp/aida-backup.XXXXXX)"
LOG "starting backup (container=$PG_CONTAINER, stamp=$STAMP)"

stage_begin "pg_dump"
DB_FILE="$WORK/chatbot_$STAMP.dump.gz"
docker exec "$PG_CONTAINER" \
  pg_dump -U "$DB_USER" -d "$DB_NAME" -Fc --no-owner --no-privileges \
  | gzip -9 > "$DB_FILE"
[[ -s "$DB_FILE" ]] || die "pg_dump produced no data"
LOG "database dump: $(du -h "$DB_FILE" | cut -f1)"
record_file "$DB_FILE"

stage_begin "config archive"
CFG_FILE="$WORK/aida-config_$STAMP.tar.gz"
tar -czf "$CFG_FILE" -C "$APP_DIR" \
  --exclude='*.log' \
  .env appsettings.json appsettings.Development.json \
  docker-compose.yaml Dockerfile docs prompts 2>/dev/null \
  || LOG "WARN: some config paths missing, continuing with what exists"
LOG "config archive: $(du -h "$CFG_FILE" | cut -f1)"
record_file "$CFG_FILE"

if [[ "${1:-}" == "--dry-run" ]]; then
  LOG "dry-run: skipping upload (trace still recorded)"
  exit 0
fi

# ---------- upload -----------------------------------------------------------
stage_begin "upload"
DEST="$S3_BUCKET/$S3_PREFIX"
upload() { # $1 = local file
  local f="$1"
  if command -v mc >/dev/null; then
    mc alias set aidabk "$S3_ENDPOINT" "$S3_ACCESS_KEY_ID" "$S3_SECRET_ACCESS_KEY" >/dev/null
    mc cp "$f" "$DEST/" >/dev/null
  elif command -v aws >/dev/null; then
    AWS_ACCESS_KEY_ID="$S3_ACCESS_KEY_ID" AWS_SECRET_ACCESS_KEY="$S3_SECRET_ACCESS_KEY" \
    aws s3 cp "$f" "s3://$DEST/" --endpoint-url "$S3_ENDPOINT" --region "${S3_REGION:-auto}" >/dev/null
  elif command -v rclone >/dev/null; then
    rclone config create aidabk s3 endpoint "$S3_ENDPOINT" \
      access_key_id "$S3_ACCESS_KEY_ID" secret_access_key "$S3_SECRET_ACCESS_KEY" >/dev/null 2>&1 || true
    rclone copy "$f" "aidabk:$DEST" >/dev/null
  else
    die "no upload tool found — install mc, aws cli, or rclone"
  fi
  LOG "uploaded -> s3://$(basename "$f")"
}

upload "$DB_FILE"
upload "$CFG_FILE"

# ---------- prune old backups ------------------------------------------------
stage_begin "prune"
prune() {
  LOG "pruning objects older than $KEEP_DAYS days ..."
  if command -v mc >/dev/null; then
    mc rm --recursive --force --older-than "${KEEP_DAYS}d" "$DEST/" >/dev/null 2>&1 || true
  elif command -v aws >/dev/null; then
    # cutoff as YYYY-MM-DD (GNU date on Linux, BSD date on macOS)
    local cutoff
    cutoff="$(TZ="$BACKUP_TZ" date -d "-${KEEP_DAYS} days" +%Y-%m-%d 2>/dev/null \
             || TZ="$BACKUP_TZ" date -v-"${KEEP_DAYS}"d +%Y-%m-%d)"
    # s3 ls lines: 2025-11-29 02:00:00      12345      name
    AWS_ACCESS_KEY_ID="$S3_ACCESS_KEY_ID" AWS_SECRET_ACCESS_KEY="$S3_SECRET_ACCESS_KEY" \
    aws s3 ls "s3://$DEST/" --endpoint-url "$S3_ENDPOINT" --region "${S3_REGION:-auto}" \
    | awk -v c="$cutoff" '$1 < c { for (i = 4; i <= NF; i++) printf "%s%s", $i, (i < NF ? OFS : ORS) }' \
    | while IFS= read -r file; do
        [[ -n "$file" ]] || continue
        LOG "pruning $file"
        AWS_ACCESS_KEY_ID="$S3_ACCESS_KEY_ID" AWS_SECRET_ACCESS_KEY="$S3_SECRET_ACCESS_KEY" \
        aws s3 rm "s3://$DEST/$file" --endpoint-url "$S3_ENDPOINT" --region "${S3_REGION:-auto}" >/dev/null
      done
  elif command -v rclone >/dev/null; then
    rclone delete "aidabk:$DEST" --min-age "${KEEP_DAYS}d" >/dev/null 2>&1 || true
  fi
  LOG "prune done"
}
prune

LOG "backup finished OK"
