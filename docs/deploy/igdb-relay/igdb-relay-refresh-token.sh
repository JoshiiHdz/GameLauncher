#!/usr/bin/env bash
# Refreshes the IGDB app access token the relay attaches to upstream requests.
# Run weekly as root (Twitch app tokens last roughly two months) AND once at boot (see README - a `@reboot` cron line). The Client
# Secret is read from a root-only file, sent to Twitch through curl's stdin config (so it never appears in a process listing), and
# is never written to the nginx token file or any log.
#
# Guarantees:
#  - the Twitch response is validated as data, not text: a JSON object whose access_token is a STRING of plain token characters,
#    token_type "bearer", and expires_in an INTEGER between one hour and one year. Anything else is refused.
#  - the token file is replaced atomically and only ever left in one of two states: the new token (nginx accepted it and reloaded)
#    or exactly what it was before. If nothing existed before (first installation) the file is left as a valid EMPTY placeholder, never
#    missing, because the nginx config includes it. This holds for every failure after the swap - config test, reload, or the script
#    being killed - via the EXIT trap.
#  - EXPIRY-AWARE, REPEATABLE renewal: the weekly cron is a safety net, not the only clock. Twitch app tokens normally last ~60 days,
#    but the validated range allows as little as one hour - if a run ever gets one, and the EARLY renewal it schedules ALSO gets a
#    short-lived one, and so on, the chain must not break. Each early-renewal timer gets a FRESH, UNIQUE unit name plus --collect
#    (auto-removed once it finishes, success or failure): a FIXED, reused name (the first version of this script) made a run try to
#    recreate the very unit it is currently executing as - systemd refuses that (a transient unit can't be recreated while the old
#    one is still loaded/active; confirmed against this VM's real systemd, not just a mocked scheduler). A token that comfortably
#    outlives the week cancels any still-pending early renewal instead of leaving it live.
#  - FAILURE RECOVERY: a failed attempt (Twitch unreachable, a bad response, nginx rejecting the reload...) schedules a BOUNDED
#    number of short retries of its own (RENEW_MAX_RETRIES, the same unique-unit mechanism, the count threaded through systemd-run's
#    --setenv) rather than leaving the weekly cron as the only way back - but it never retries forever.
#  - REBOOT: an early-renewal or retry timer is a systemd TRANSIENT unit and does not survive a reboot. Recovery is the boot-time
#    cron run: it always fetches a fresh token, so whatever was scheduled in memory before the reboot becomes moot within roughly a
#    minute of the machine coming back up, every time - with no schedule to persist or resurrect.
set -euo pipefail
umask 077

CRED_FILE="${CRED_FILE:-/etc/igdb-relay/credentials}"     # two lines: IGDB_CLIENT_ID=...   IGDB_CLIENT_SECRET=...   (root, 0600)
TOKEN_INC="${TOKEN_INC:-/etc/nginx/igdb-token.inc}"
TOKEN_URL="${TOKEN_URL:-https://id.twitch.tv/oauth2/token}"
NGINX_TEST_CMD="${NGINX_TEST_CMD:-nginx -t}"
NGINX_RELOAD_CMD="${NGINX_RELOAD_CMD:-nginx -s reload}"
MAX_RESPONSE_BYTES=8192

# How an early renewal / a failure retry is scheduled or cancelled - systemd-run by default (systemd is already required for nginx,
# no extra package). RENEW_SCHEDULE_CMD/RENEW_CANCEL_CMD/RENEW_RETRY_CMD are test seams (see test/relay_test.py and
# test/real_systemd_renewal_test.py): with any of them set, that command runs instead of the real one. Never set in production.
RENEW_UNIT_PREFIX="${RENEW_UNIT_PREFIX:-igdb-relay-token-renew}"     # a PREFIX, not a fixed name - see the unique-naming note above
RENEW_SAFETY_MARGIN="${RENEW_SAFETY_MARGIN:-300}"      # renew this long before the token would actually expire
RENEW_MIN_DELAY="${RENEW_MIN_DELAY:-30}"               # never schedule sooner than this, however short the token turned out to be
RENEW_CHECK_WINDOW="${RENEW_CHECK_WINDOW:-604800}"     # the cron's own worst-case gap (weekly) - only tokens shorter than this need help
RENEW_RETRY_DELAY="${RENEW_RETRY_DELAY:-600}"          # how soon to retry after a FAILED attempt (network blip, Twitch hiccup, ...)
RENEW_MAX_RETRIES="${RENEW_MAX_RETRIES:-5}"            # bounded: never retry forever if something is genuinely broken
RENEW_RETRY_COUNT="${RENEW_RETRY_COUNT:-0}"            # how many consecutive failure-retries already happened; threaded via --setenv
SELF_PATH="$(readlink -f "$0" 2>/dev/null || echo "$0")"

log() { printf '%s igdb-relay-token: %s\n' "$(date -u +%FT%TZ)" "$*" >&2; }
die() { log "FAILED: $*  (the previous token, if any, is still in use)"; exit 1; }

tmp=""; backup=""; had_prev=0; swapped=0; committed=0

restore_previous() {
  if [ "$had_prev" = 1 ]; then
    mv -f "$backup" "$TOKEN_INC"                       # exactly what it was (same mode, same content)
    backup=""
  else
    local empty; empty="$(mktemp "$TOKEN_INC.XXXXXX")"
    chmod 600 "$empty"; mv -f "$empty" "$TOKEN_INC"    # first installation: a valid, empty placeholder - never a missing file
  fi
}

on_exit() {
  local rc=$?
  if [ "$swapped" = 1 ] && [ "$committed" = 0 ]; then
    restore_previous && log "restored the previous token file after a failure"
  fi
  if [ "$committed" = 0 ] && [ "$rc" != 0 ]; then
    maybe_retry_after_failure
  fi
  [ -n "$tmp" ] && rm -f "$tmp"
  [ -n "$backup" ] && rm -f "$backup"
  exit "$rc"
}
trap on_exit EXIT

unique_renew_unit_name() { printf '%s-%s-%s-%s' "$RENEW_UNIT_PREFIX" "$(date -u +%s)" "$$" "$RANDOM"; }

# systemd-run's transient unit starts with SYSTEMD'S OWN default environment, not this process's - it does NOT inherit CRED_FILE,
# TOKEN_INC, TOKEN_URL, the nginx commands or any RENEW_* override just because this shell has them set. In production nothing is
# ever overridden, so every hop already reaches the same hardcoded defaults regardless - this only matters for a customised
# deployment or a test, but there it matters completely (a scheduled hop that silently drops back to production defaults is not a
# real test of the chain at all), so every var that CAN be overridden is forwarded explicitly, every time, to every scheduled hop.
RENEW_FORWARDED_VARS=(CRED_FILE TOKEN_INC TOKEN_URL NGINX_TEST_CMD NGINX_RELOAD_CMD RENEW_UNIT_PREFIX RENEW_SAFETY_MARGIN
  RENEW_MIN_DELAY RENEW_CHECK_WINDOW RENEW_RETRY_DELAY RENEW_MAX_RETRIES RENEW_SCHEDULE_CMD RENEW_CANCEL_CMD RENEW_RETRY_CMD)

renew_setenv_args() {
  local var value
  for var in "${RENEW_FORWARDED_VARS[@]}"; do
    value="${!var-}"
    [ -n "$value" ] && printf -- '--setenv=%s=%s\0' "$var" "$value"
  done
}

# Best-effort in every direction: none of these ever fail the CALLER (the token/reload above already succeeded, or the failure
# path is already exiting non-zero on its own - a scheduling problem must never mask or worsen either).
schedule_early_renewal() {
  local delay="$1"
  cancel_early_renewal || true   # at most one pending early renewal at a time
  if [ -n "${RENEW_SCHEDULE_CMD:-}" ]; then
    eval "$RENEW_SCHEDULE_CMD" "$delay"
    return $?
  fi
  local unit; unit="$(unique_renew_unit_name)"
  local -a setenv_args; readarray -d '' -t setenv_args < <(renew_setenv_args)
  systemd-run --unit="$unit" --collect \
    --description="IGDB relay early token renewal (this token expires sooner than the weekly cron)" \
    --on-active="${delay}s" --timer-property=AccuracySec=30s \
    "${setenv_args[@]}" "$SELF_PATH"
}

cancel_early_renewal() {
  if [ -n "${RENEW_CANCEL_CMD:-}" ]; then
    eval "$RENEW_CANCEL_CMD"
    return
  fi
  local unit
  for unit in $(systemctl list-units --all --no-legend --plain "${RENEW_UNIT_PREFIX}-*.timer" 2>/dev/null | awk '{print $1}'); do
    systemctl stop "$unit" >/dev/null 2>&1 || true
    systemctl reset-failed "$unit" "${unit%.timer}.service" >/dev/null 2>&1 || true
  done
}

maybe_retry_after_failure() {
  if [ "$RENEW_RETRY_COUNT" -ge "$RENEW_MAX_RETRIES" ]; then
    log "already retried ${RENEW_RETRY_COUNT} time(s) after a failure - giving up until the next scheduled run"
    return
  fi
  local next=$((RENEW_RETRY_COUNT + 1))
  if [ -n "${RENEW_RETRY_CMD:-}" ]; then
    eval "$RENEW_RETRY_CMD" "$RENEW_RETRY_DELAY" "$next"
    return
  fi
  local unit; unit="$(unique_renew_unit_name)"
  local -a setenv_args; readarray -d '' -t setenv_args < <(renew_setenv_args)
  if systemd-run --unit="$unit" --collect \
       --description="IGDB relay token refresh retry ${next}/${RENEW_MAX_RETRIES} after a failure" \
       --on-active="${RENEW_RETRY_DELAY}s" --timer-property=AccuracySec=30s \
       "${setenv_args[@]}" --setenv="RENEW_RETRY_COUNT=${next}" "$SELF_PATH"; then
    log "scheduled a retry in ${RENEW_RETRY_DELAY}s (${next}/${RENEW_MAX_RETRIES})"
  else
    log "could not schedule a retry after the failure - relying on the weekly cron alone"
  fi
}

command -v python3 >/dev/null || die "python3 is required to validate the token response"
[ -r "$CRED_FILE" ] || die "cannot read credentials file"
ID="$(sed -n 's/^IGDB_CLIENT_ID=//p' "$CRED_FILE" | head -1)"
SECRET="$(sed -n 's/^IGDB_CLIENT_SECRET=//p' "$CRED_FILE" | head -1)"
[[ "$ID" =~ ^[A-Za-z0-9]{10,64}$ ]] || die "client id missing or malformed"
[[ "$SECRET" =~ ^[A-Za-z0-9]{10,64}$ ]] || die "client secret missing or malformed"

resp="$(curl -fsS --max-time 20 --max-filesize "$MAX_RESPONSE_BYTES" -K - <<CFG
url = "$TOKEN_URL"
data-urlencode = "client_id=$ID"
data-urlencode = "client_secret=$SECRET"
data-urlencode = "grant_type=client_credentials"
CFG
)" || die "token request failed"
unset SECRET

# Prints ONLY the token on success. On any problem it prints a reason (never the response) to stderr and exits 3.
TOKEN="$(printf '%s' "$resp" | python3 -c '
import json, re, sys
def refuse(why):
    sys.stderr.write("refused: " + why + "\n"); sys.exit(3)
raw = sys.stdin.buffer.read()
if len(raw) > int(sys.argv[1]): refuse("response too large")
try: data = json.loads(raw.decode("utf-8"))
except Exception: refuse("response is not JSON")
if not isinstance(data, dict): refuse("response is not a JSON object")
token = data.get("access_token")
if not isinstance(token, str): refuse("access_token is not a string")
# The value is written into an nginx config file: anything but plain token characters could inject configuration.
if not re.fullmatch(r"[A-Za-z0-9]{10,200}", token): refuse("access_token has an unexpected shape")
ttype = data.get("token_type")
if not isinstance(ttype, str) or ttype.lower() != "bearer": refuse("token_type is not bearer")
expires = data.get("expires_in")
if isinstance(expires, bool) or not isinstance(expires, int): refuse("expires_in is not an integer")
if not 3600 <= expires <= 31536000: refuse("expires_in is outside one hour .. one year")
sys.stdout.write(token + " " + str(expires))
' "$MAX_RESPONSE_BYTES")" || die "token response rejected"
EXPIRES="${TOKEN#* }"; TOKEN="${TOKEN%% *}"
unset resp

backup="$(mktemp "$TOKEN_INC.bak.XXXXXX")"
if [ -f "$TOKEN_INC" ]; then cp -p "$TOKEN_INC" "$backup"; had_prev=1; fi

tmp="$(mktemp "$TOKEN_INC.XXXXXX")"
printf '# refreshed %s, valid for %s s\nproxy_set_header Authorization "Bearer %s";\nproxy_set_header Client-ID "%s";\n' \
  "$(date -u +%FT%TZ)" "$EXPIRES" "$TOKEN" "$ID" > "$tmp"
chmod 600 "$tmp"
mv -f "$tmp" "$TOKEN_INC"; tmp=""
swapped=1

eval "$NGINX_TEST_CMD" >/dev/null 2>&1 || die "nginx rejected the new configuration"
eval "$NGINX_RELOAD_CMD" || die "nginx reload failed"
committed=1
log "token refreshed (valid for ${EXPIRES}s) and nginx reloaded"

renew_in=$(( EXPIRES - RENEW_SAFETY_MARGIN ))
if [ "$renew_in" -lt "$RENEW_CHECK_WINDOW" ]; then
  [ "$renew_in" -lt "$RENEW_MIN_DELAY" ] && renew_in=$RENEW_MIN_DELAY
  if schedule_early_renewal "$renew_in"; then
    log "this token lasts only ${EXPIRES}s - scheduled an early renewal in ${renew_in}s so access doesn't lapse before the next weekly run"
  else
    log "this token lasts only ${EXPIRES}s but scheduling an early renewal FAILED - relying on the weekly cron alone"
  fi
else
  cancel_early_renewal || true   # this token outlives the week: nothing to schedule, and no earlier short-lived renewal is still pending
fi
