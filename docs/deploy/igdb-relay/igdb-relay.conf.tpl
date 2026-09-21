# IGDB relay for a small friends-only launcher. Lives in /etc/nginx/conf.d/igdb-relay.conf (included inside http{}).
#
#  - The IGDB Client Secret is NOT in this file or anywhere nginx reads. Only a short-lived app ACCESS TOKEN is (igdb-token.inc,
#    root-only), refreshed by igdb-relay-refresh-token.sh. A leaked token expires; the secret never leaves the refresh script.
#  - Two hops on purpose: the public server answers from cache; only cache MISSES pass through the loopback server, which is the
#    one place the global 4 requests/second IGDB limit is enforced and where credentials are attached. A cache hit costs no budget.
#  - The public hop is HTTPS with a self-signed certificate that the launcher build PINS (see igdb-relay-make-cert.sh): a network attacker
#    cannot substitute game identities, cross-references or cover ids without this server's private key.
#  - No signup, no per-user keys: anyone who knows the address can use it, within these limits. That is a deliberate trade for
#    "friends only, zero setup"; it protects the secret and the IGDB quota, not the endpoint's privacy.

proxy_cache_path @@CACHE_DIR@@ levels=1:2 keys_zone=igdb_cache:2m max_size=64m inactive=14d use_temp_path=off;
limit_req_zone $binary_remote_addr zone=igdb_per_ip:1m rate=5r/s;
limit_req_zone "igdb" zone=igdb_upstream:64k rate=3r/s;   # IGDB allows 4/s; nginx's limiter can let 5 through in one second, so keep a margin
limit_req_log_level notice;                                                   # delayed/refused requests are normal operation, not warnings

# ---- hop 2: loopback only. Rate limit + credentials + TLS to IGDB. --------------------------------------------------------
server {
    listen 127.0.0.1:@@LOOP@@;
    server_tokens off;
    access_log off;

    location ~ ^/v4/(games|covers|external_games|external_game_sources|alternative_names)$ {
        limit_req zone=igdb_upstream burst=20;
        limit_req_status 429;
        error_page 429 = @igdb_busy;

        include @@TOKEN_INC@@;                      # sets Authorization + Client-ID (overrides anything a client sent)
@@UPSTREAM_BLOCK@@
    }

    location @igdb_busy {
        add_header Retry-After 2 always;
        return 429;
    }
}

# ---- hop 1: public. Allowlist + size cap + per-IP limit + cache. ----------------------------------------------------------
server {
    listen @@FRONT_LISTEN@@;
    server_name @@SERVER_NAME@@;
    server_tokens off;
@@TLS_BLOCK@@

    client_max_body_size 2k;                        # the launcher's queries are a few hundred bytes
    client_body_buffer_size 4k;                     # body must stay in memory: it is part of the cache key

    location ~ ^/v4/(games|covers|external_games|external_game_sources|alternative_names)$ {
        limit_except POST { deny all; }
        limit_req zone=igdb_per_ip burst=40 nodelay;
        limit_req_status 429;
        error_page 429 = @busy;

        proxy_pass http://127.0.0.1:@@LOOP@@$uri;   # $uri, not $request_uri: a query string can neither bust the cache nor reach IGDB
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_connect_timeout 3s;
        proxy_read_timeout 20s;

        proxy_cache igdb_cache;
        proxy_cache_methods POST;
        proxy_cache_key "$uri|$request_body";
        proxy_cache_lock on;                        # identical concurrent misses cost ONE upstream request
        proxy_cache_lock_timeout 10s;
        proxy_cache_valid 200 @@TTL@@;              # errors (401/429/5xx) are never cached
        proxy_cache_use_stale error timeout updating http_500 http_502 http_503 http_504 http_429;   # outage -> serve what we have
        proxy_ignore_headers Cache-Control Expires Set-Cookie;
        add_header X-Relay-Cache $upstream_cache_status always;
    }

    location @busy {
        add_header Retry-After 1 always;
        return 429;
    }

    location / { return 404; }
}
