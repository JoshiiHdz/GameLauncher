#!/usr/bin/env python3
"""Renders igdb-relay.conf.tpl into the production igdb-relay.conf (HTTPS on 443, IGDB reached over verified TLS)."""
import os
here = os.path.dirname(os.path.abspath(__file__))
UPSTREAM = ("        proxy_set_header Host api.igdb.com;\n        proxy_ssl_server_name on;\n        proxy_ssl_name api.igdb.com;\n"
            "        proxy_ssl_verify on;\n        proxy_ssl_trusted_certificate /etc/ssl/certs/ca-certificates.crt;\n        proxy_ssl_protocols TLSv1.2 TLSv1.3;\n"
            "        resolver 127.0.0.53 valid=300s ipv6=off;    # Ubuntu's systemd-resolved stub\n        set $igdb_up api.igdb.com;\n        proxy_pass https://$igdb_up$uri;")
TLS = """    ssl_certificate     /etc/igdb-relay/tls/relay.crt;
    ssl_certificate_key /etc/igdb-relay/tls/relay.key;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_session_cache shared:igdb_ssl:1m;
    ssl_session_timeout 1h;
    ssl_session_tickets off;"""
text = open(os.path.join(here, "igdb-relay.conf.tpl"), encoding="utf-8").read()
for k, v in {"@@CACHE_DIR@@": "/var/cache/nginx/igdb", "@@LOOP@@": "8181", "@@TOKEN_INC@@": "/etc/nginx/igdb-token.inc",
             "@@FRONT_LISTEN@@": "443 ssl default_server", "@@SERVER_NAME@@": "_", "@@TTL@@": "2d",
             "@@TLS_BLOCK@@": TLS, "@@UPSTREAM_BLOCK@@": UPSTREAM}.items():
    text = text.replace(k, v)
assert "@@" not in text
open(os.path.join(here, "igdb-relay.conf"), "w", encoding="utf-8", newline="\n").write(text)
print("rendered igdb-relay.conf")
