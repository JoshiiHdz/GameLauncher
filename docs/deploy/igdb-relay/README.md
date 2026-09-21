# IGDB relay for a small friends-only launcher

**Status: deployed on the owner's existing small Linux server, HTTPS on port 443.**

## What it is

One nginx config, a token-refresh script and a certificate script. The launcher asks the relay instead of `api.igdb.com`; the relay attaches
the project's IGDB access token, caches answers, and keeps IGDB's 4 requests/second limit for everyone. The Twitch **Client Secret is not in
the launcher**, in nginx config, or in any log - only on the server, in a root-only file read by the refresh script.

No signup, no per-user keys. Anyone who knows the relay's address can use it within the limits below. That protects the secret and the shared
IGDB quota; it does not make the endpoint private.

## HTTPS without a domain, Cloudflare or an account

The relay serves HTTPS with a **self-signed certificate that the launcher pins**. The launcher build embeds the SHA-256 of the certificate's
public key (`sha256/...`, printed by `igdb-relay-make-cert.sh`) and trusts the relay by that key alone - not by a certificate authority or a DNS
name. Anyone who cannot present that private key (an interceptor, a hijacked DNS name, a stolen address) fails the TLS handshake before a single
request byte is sent, so game identities, cross-references and cover ids cannot be substituted on the path. Cover *images* still come from IGDB's
CDN over ordinary HTTPS. Plain HTTP is not served.

The private key never leaves the server (`/etc/igdb-relay/tls/relay.key`, root, 0600). A new key means a new pin and strands builds carrying the
old one (they fall back to SteamGridDB), so the script refuses to replace it without `--force`; the launcher accepts up to three pins so a rotation
can be rolled out (add the new pin in a release, then rotate the key).

## What it does (each line is a test in `test/relay_test.py`, run against the real server's nginx build)

| Behaviour | Setting |
|---|---|
| Only the five endpoints the launcher uses (`games`, `covers`, `external_games`, `external_game_sources`, `alternative_names`), POST only | everything else 404/403 |
| Transport | TLS 1.2/1.3 only; plain HTTP to the TLS port refused; a wrong key pin refused before any request |
| Body cap | 2 KB (launcher queries are a few hundred bytes) |
| Cache | keyed by endpoint + exact query, 2 days, 64 MB, query strings ignored; identical concurrent misses cost one upstream request |
| Outage | an expired entry is served stale if IGDB errors or times out; errors are never cached |
| Per-address limit | 5 requests/second, burst 40, then `429` + `Retry-After` |
| Upstream limit | 3 requests/second (IGDB allows 4; nginx's limiter measured 5 in one second at a 4/s setting), queue of 20, then `429` + `Retry-After` |
| Cache hits | spend none of the upstream budget |
| Credentials | the relay's own token/Client-ID always replace anything a client sends |

### Token refresh (`igdb-relay-refresh-token.sh`, weekly root cron)

The Twitch response is validated as **data**: a JSON object whose `access_token` is a *string* of plain token characters, `token_type` `bearer`,
and `expires_in` an *integer* between one hour and one year; over 8 KB, non-JSON, wrongly typed or out-of-range responses are refused. The secret is
passed to curl through stdin (never on a command line) and never logged. The token file is replaced atomically and is only ever left as the new
token (nginx accepted it and reloaded) **or exactly as it was**; if nothing existed before (first installation) a failed run leaves a valid empty
placeholder, never a missing file and never a stale backup. This holds for a rejected config, a failed reload, or the script being killed.

## Files

- `igdb-relay.conf.tpl` -> `render-conf.py` -> `igdb-relay.conf`: installed at `/etc/nginx/conf.d/igdb-relay.conf` (cache `/var/cache/nginx/igdb`,
  loopback hop on `8181`, token file `/etc/nginx/igdb-token.inc`, listens on 443).
- `igdb-relay-refresh-token.sh`, `igdb-relay-make-cert.sh`: `/usr/local/sbin/`, root, mode 0750; the refresh script runs weekly from
  `/etc/cron.d/igdb-relay-token`.
- `test/`: the throwaway test kit (fake IGDB + driver). It starts its own nginx on loopback high ports under `/tmp` and removes itself; it never
  touches the system nginx.

Credentials live in `/etc/igdb-relay/credentials` (root, 0600, `IGDB_CLIENT_ID=` / `IGDB_CLIENT_SECRET=`). To rotate: replace that file and run the
refresh script once. If the secret is ever revoked, the launcher degrades to SteamGridDB (a relay 401 is "unavailable", never a cleared cover).

## Rollback

Delete `/etc/nginx/conf.d/igdb-relay.conf` and `/etc/cron.d/igdb-relay-token`, `nginx -t && systemctl reload nginx`. Optionally delete
`/etc/igdb-relay/` and `/etc/nginx/igdb-token.inc`. Nothing else was touched.

## Releases

The launcher build embeds `src/GameLauncher/default-igdb-relay.txt` (gitignored; two lines: the `https://` address, then the pin). A **release
cannot build without it**: `release.yml` runs `installer/Check-RelayConfig.ps1` before the build (fails on a missing, plain-http, pin-less or
malformed configuration) and again on the finished package (opens the real `.nupkg`, reads what is actually embedded in the packaged launcher and
fails unless it is present, valid and equal to what was written). Set the repository **variables** `IGDB_RELAY_URL` and `IGDB_RELAY_PIN`
(Settings > Secrets and variables > Actions > Variables). Development builds may still omit the file and simply have no built-in IGDB.

## Not verified

The launcher on other people's networks and machines; the hosting-side settings (public-IP type, actual bill); certificate behaviour beyond what the
pinned client and curl exercise.
