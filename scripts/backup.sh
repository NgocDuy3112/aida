#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[[ -f "$APP_DIR/.env" ]] && set -a && . "$APP_DIR/.env" && set +a

BACKUP_TZ="${BACKUP_TZ:-Asia/Ho_Chi_Minh}"
S3_BUCKET="${S3_BUCKET:-aidagent}"
S3_PREFIX="${S3_PREFIX:-backups}"          
KEEP_DAYS="${BACKUP_KEEP_DAYS:-30}"
PG_CONTAINER="${PG_CONTAINER:-aida-postgresql}"
DB_NAME="${POSTGRES_DB:-chatbot}"
DB_USER="${POSTGRES_USER:-chatbot}"
TRACE_FILE="${BACKUP_TRACE_FILE:-/var/log/aida-backup-runs.jsonl}"
WORK=""                                   

STAMP="$(TZ="$BACKUP_TZ" date +'%d-%m-%Y_%H-%M-%S')"
RUN_ID="$(TZ="$BACKUP_TZ" date +%y%m%d-%H%M%S)-$$-${RANDOM}"

LOG() { printf '[%s] [run=%s] %s\n' \
  "$(TZ="$BACKUP_TZ" date '+%d-%m-%Y %H:%M:%S')" "$RUN_ID" "$*"; }
die() { LOG "ERROR: $*" >&2; exit 1; }

STAGE="startup"; STAGE_T0=$SECONDS
STAGES_JSON=""; FILES_JSON=""; SKIP_TRACE=0

end_stage() {
  local d=$((SECONDS - STAGE_T0))
  if [[ -n "$STAGES_JSON" ]]; then STAGES_JSON+=","; fi
  STAGES_JSON+="{\"name\":\"$STAGE\",\"seconds\":$d}"
  LOG "stage '$STAGE' finished in ${d}s"
}

stage_begin() {           
  end_stage
  STAGE="$1"; STAGE_T0=$SECONDS
  LOG "stage '$STAGE' starting"
}

record_file() {           
  local f="$1" b
  if [[ ! -f "$f" ]]; then
    LOG "WARN: expected artifact '$(basename "$f")' missing — omitted from run trace"
    return 0
  fi
  b="$(wc -c < "$f" | tr -d ' ')"
  if [[ -n "$FILES_JSON" ]]; then FILES_JSON+=","; fi
  FILES_JSON+="{\"name\":\"$(basename "$f")\",\"bytes\":$b}"
}

write_trace() {           
  local rc="$1" status="ok"
  if [[ $rc -ne 0 ]]; then status="failed"; fi
  local when
  when="$(TZ="$BACKUP_TZ" date +'%d-%m-%Y %H:%M:%S')"
  printf '{"run":"%s","status":"%s","exit":%d,"stamp":"%s","finished":"%s %s","stages":[%s],"files":[%s]}\n' \
    "$RUN_ID" "$status" "$rc" "$STAMP" "$when" "$BACKUP_TZ" \
    "$STAGES_JSON" "$FILES_JSON" >> "$TRACE_FILE" 2>/dev/null \
    || LOG "WARN: could not append trace to $TRACE_FILE"
}

notify_failure() {        
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
  local err
  if ! err=$(curl -fsS -m 10 -o /dev/null -H 'Content-Type: application/json' \
      -d "$payload" "$BACKUP_WEBHOOK_URL" 2>&1); then
    LOG "WARN: failure alert NOT delivered to webhook: ${err:-no detail} (stage: $STAGE)"
  fi
}

on_exit() {               
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

[[ -n "${S3_ENDPOINT:-}" ]]         || die "S3_ENDPOINT missing in .env"
[[ -n "${S3_ACCESS_KEY_ID:-}" ]]    || die "S3_ACCESS_KEY_ID missing in .env"
[[ -n "${S3_SECRET_ACCESS_KEY:-}" ]] || die "S3_SECRET_ACCESS_KEY missing in .env"
command -v docker >/dev/null || die "docker not found"
docker info >/dev/null 2>&1  || die "docker daemon not reachable (need sudo?)"

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
if ! tar_err=$(tar -czf "$CFG_FILE" -C "$APP_DIR" \
    --exclude='*.log' \
    .env appsettings.json appsettings.Development.json \
    docker-compose.yaml Dockerfile docs prompts 2>&1); then
  LOG "WARN: config archive incomplete — missing paths? ${tar_err//$'\n'/; }"
fi
if [[ -f "$CFG_FILE" ]]; then
  LOG "config archive: $(du -h "$CFG_FILE" | cut -f1)"
else
  LOG "WARN: config archive not created — only the database dump will be uploaded"
fi
record_file "$CFG_FILE"

if [[ "${1:-}" == "--dry-run" ]]; then
  LOG "dry-run: skipping upload (trace still recorded)"
  exit 0
fi

stage_begin "upload"
DEST="$S3_BUCKET/$S3_PREFIX"
upload() { 
  local f="$1"
  if command -v mc >/dev/null; then
    mc alias set aidabk "$S3_ENDPOINT" "$S3_ACCESS_KEY_ID" "$S3_SECRET_ACCESS_KEY" >/dev/null
    mc cp "$f" "$DEST/" >/dev/null
  elif command -v aws >/dev/null; then
    AWS_ACCESS_KEY_ID="$S3_ACCESS_KEY_ID" AWS_SECRET_ACCESS_KEY="$S3_SECRET_ACCESS_KEY" \
    aws s3 cp "$f" "s3://$DEST/" --endpoint-url "$S3_ENDPOINT" --region "${S3_REGION:-auto}" >/dev/null
  elif command -v rclone >/dev/null; then
    if ! rc_err=$(rclone config create aidabk s3 endpoint "$S3_ENDPOINT" \
        access_key_id "$S3_ACCESS_KEY_ID" secret_access_key "$S3_SECRET_ACCESS_KEY" 2>&1 >/dev/null); then
      LOG "WARN: rclone remote 'aidabk' not configured: ${rc_err:-no detail} (continuing; may already exist)"
    fi
    rclone copy "$f" "aidabk:$DEST" >/dev/null
  else
    die "no upload tool found — install mc, aws cli, or rclone"
  fi
  LOG "uploaded -> s3://$(basename "$f")"
}

upload "$DB_FILE"
if [[ -f "$CFG_FILE" ]]; then
  upload "$CFG_FILE"
else
  LOG "WARN: skipping config-archive upload — file does not exist"
fi

stage_begin "prune"
prune() {
  LOG "pruning objects older than $KEEP_DAYS days ..."
  if command -v mc >/dev/null; then
    mc rm --recursive --force --older-than "${KEEP_DAYS}d" "$DEST/" >/dev/null \
      || LOG "WARN: prune reported errors — continuing (backup itself is safe)"
  elif command -v aws >/dev/null; then
    local cutoff=""
    cutoff="$(TZ="$BACKUP_TZ" date -d "-${KEEP_DAYS} days" +%Y-%m-%d 2>/dev/null \
             || TZ="$BACKUP_TZ" date -v-"${KEEP_DAYS}"d +%Y-%m-%d 2>/dev/null \
             || true)"
    if [[ -z "$cutoff" ]]; then
      LOG "WARN: could not compute prune cutoff — skipping age-based prune"
      return 0
    fi
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
    rclone delete "aidabk:$DEST" --min-age "${KEEP_DAYS}d" >/dev/null \
      || LOG "WARN: rclone prune reported errors — continuing"
  fi
  LOG "prune done"
}
prune

LOG "backup finished OK"
