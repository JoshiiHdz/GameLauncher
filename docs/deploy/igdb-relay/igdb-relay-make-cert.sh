#!/usr/bin/env bash
# Creates the relay's TLS identity: a self-signed ECDSA P-256 certificate and its private key, and prints the PIN the launcher build
# embeds (the SHA-256 of the certificate's public key). The launcher authenticates the relay by that pin - not by a certificate
# authority or a DNS name - so no domain, no Cloudflare and no account is involved, and nobody who can only tamper with DNS or the
# network can impersonate the relay without this private key.
#
#   usage: igdb-relay-make-cert.sh <public-ip-or-name> [--force]
#
# The private key never leaves this server (root, 0600). It refuses to overwrite an existing key unless --force is given, because a new
# key means a new pin and every launcher build carrying the old pin would stop trusting the relay (they fall back to SteamGridDB).
# The certificate is valid for 10 years; the launcher pins the KEY, not the validity dates.
set -euo pipefail
umask 077

HOST="${1:?usage: $0 <public-ip-or-name> [--force]}"
FORCE="${2:-}"
DIR="${TLS_DIR:-/etc/igdb-relay/tls}"
KEY="$DIR/relay.key"; CRT="$DIR/relay.crt"

install -d -m 0700 "$DIR"
if [ -e "$KEY" ] && [ "$FORCE" != "--force" ]; then
  echo "refusing to replace the existing key ($KEY): a new key means a new pin. Use --force to rotate on purpose." >&2
  exit 1
fi

if [[ "$HOST" =~ ^[0-9.]+$ ]]; then SAN="IP:$HOST"; else SAN="DNS:$HOST"; fi
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -sha256 -days 3650 \
  -keyout "$KEY" -out "$CRT" -subj "/CN=igdb-relay" -addext "subjectAltName=$SAN" 2>/dev/null
chmod 600 "$KEY"; chmod 644 "$CRT"

printf 'sha256/%s\n' "$(openssl x509 -in "$CRT" -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | base64)"
