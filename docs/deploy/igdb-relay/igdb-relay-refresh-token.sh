#!/usr/bin/env bash
# Refreshes the IGDB app access token the relay attaches to upstream requests.
# Run weekly as root (Twitch app tokens last roughly two months). The Client Secret is read from a root-only file, sent to Twitch
# through curl's stdin config (so it never appears in a process listing), and is never written to the nginx token file or any log.
#
# Guarantees:
#  - the Twitch response is validated as data, not text: a JSON object whose access_token is a STRING of plain token characters,
#    token_type "bearer", and expires_in an INTEGER between one hour and one year. Anything else is refused.
#  - the token file is replaced atomically and only ever left in one of two states: the new token (nginx accepted it and reloaded)
#    or exactly what it was before. If nothing existed before (first installation) the file is left as a valid EMPTY placeholder, never
#    missing, because the nginx config includes it. This holds for every failure after the swap - config test, reload, or the script
#    being killed - via the EXIT trap.
set -euo pipefail
umask 077

CRED_FILE="${CRED_FILE:-/etc/igdb-relay/credentials}"     # two lines: IGDB_CLIENT_ID=...   IGDB_CLIENT_SECRET=...   (root, 0600)
TOKEN_INC="${TOKEN_INC:-/etc/nginx/igdb-token.inc}"
TOKEN_URL="${TOKEN_URL:-https://id.twitch.tv/oauth2/token}"
NGINX_TEST_CMD="${NGINX_TEST_CMD:-nginx -t}"
NGINX_RELOAD_CMD="${NGINX_RELOAD_CMD:-nginx -s reload}"
MAX_RESPONSE_BYTES=8192

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
  [ -n "$tmp" ] && rm -f "$tmp"
  [ -n "$backup" ] && rm -f "$backup"
  exit "$rc"
}
trap on_exit EXIT

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
