#!/usr/bin/env python3
"""Runs on the relay server as an ordinary user. Starts a THROWAWAY nginx (the VM's own binary, its own config, high loopback ports, files only under
/tmp/relay-test) and a fake IGDB, runs the relay tests, measures memory, and removes everything. It never touches the system nginx service, the
bot's container, the firewall, DNS, any credential, or anything outside /tmp/relay-test."""
import http.client, json, os, re, shutil, signal, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
BASE = "/tmp/relay-test"
NGINX = "/usr/sbin/nginx"
FRONT, LOOP, FAKE, CTL = 18080, 18181, 19000, 19001
RESULTS = []
procs = {}


def check(name, ok, detail=""):
    RESULTS.append(bool(ok))
    print(("PASS  " if ok else "FAIL  ") + name + (f"   [{detail}]" if detail else ""), flush=True)


# ---- plumbing ---------------------------------------------------------------------------------------------------------------
UP_FAKE = f"        proxy_set_header Host igdb.test;\n        proxy_pass http://127.0.0.1:{FAKE}$uri;"
UP_REAL = ("        proxy_set_header Host api.igdb.com;\n        proxy_ssl_server_name on;\n        proxy_ssl_name api.igdb.com;\n"
           "        proxy_ssl_verify on;\n        proxy_ssl_trusted_certificate /etc/ssl/certs/ca-certificates.crt;\n        proxy_ssl_protocols TLSv1.2 TLSv1.3;\n"
           "        resolver 127.0.0.53 valid=300s ipv6=off;\n        set $igdb_up api.igdb.com;\n        proxy_pass https://$igdb_up$uri;")


TLS_TEXT = """    ssl_certificate     {crt};
    ssl_certificate_key {key};
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_session_cache shared:igdb_ssl:1m;
    ssl_session_timeout 1h;
    ssl_session_tickets off;"""


def render(upstream, ttl, tls=False):
    text = open(f"{HERE}/igdb-relay.conf.tpl").read()
    for k, v in {"@@CACHE_DIR@@": f"{BASE}/cache", "@@LOOP@@": str(LOOP), "@@TOKEN_INC@@": f"{BASE}/igdb-token.inc",
                 "@@FRONT_LISTEN@@": f"127.0.0.1:{FRONT}" + (" ssl" if tls else ""), "@@SERVER_NAME@@": "_", "@@TTL@@": ttl,
                 "@@TLS_BLOCK@@": TLS_TEXT.format(crt=f"{BASE}/tls/relay.crt", key=f"{BASE}/tls/relay.key") if tls else "",
                 "@@UPSTREAM_BLOCK@@": UP_FAKE if upstream == "fake" else UP_REAL}.items():
        text = text.replace(k, v)
    open(f"{BASE}/igdb-relay.conf", "w").write(text)
    open(f"{BASE}/nginx.conf", "w").write(f"""worker_processes 2;
daemon off;
pid {BASE}/nginx.pid;
error_log {BASE}/error.log warn;
events {{ worker_connections 768; }}
http {{
  access_log {BASE}/access.log;
  client_body_temp_path {BASE}/tmp/body; proxy_temp_path {BASE}/tmp/proxy; fastcgi_temp_path {BASE}/tmp/fcgi;
  uwsgi_temp_path {BASE}/tmp/uwsgi; scgi_temp_path {BASE}/tmp/scgi;
  include {BASE}/igdb-relay.conf;
}}
""")


def start_nginx():
    p = subprocess.Popen(["nice", "-n", "10", NGINX, "-p", BASE + "/", "-c", f"{BASE}/nginx.conf", "-e", f"{BASE}/error.log"],
                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    procs["nginx"] = p
    for _ in range(50):
        time.sleep(0.1)
        if p.poll() is not None:
            print("nginx exited:", p.stdout.read().decode()); raise SystemExit(2)
        try:
            http.client.HTTPConnection("127.0.0.1", FRONT, timeout=1).request("GET", "/"); return
        except OSError:
            continue


def stop_nginx():
    p = procs.pop("nginx", None)
    if p and p.poll() is None:
        p.send_signal(signal.SIGTERM)
        try: p.wait(5)
        except subprocess.TimeoutExpired: p.kill()


def reload_nginx():
    os.kill(int(open(f"{BASE}/nginx.pid").read()), signal.SIGHUP)
    time.sleep(0.6)


def ctl(path):
    c = http.client.HTTPConnection("127.0.0.1", CTL, timeout=5)
    c.request("GET", path)
    return json.loads(c.getresponse().read())


def post(path, body="fields name; limit 5;", headers=None, timeout=30):
    c = http.client.HTTPConnection("127.0.0.1", FRONT, timeout=timeout)
    c.request("POST", path, body=body, headers=headers or {})
    r = c.getresponse()
    data = r.read()
    return r.status, {k.lower(): v for k, v in r.getheaders()}, data


def upstream_hits(path_prefix="/v4/"):
    return [r for r in ctl("/x")["requests"] if r["path"].startswith(path_prefix)]


def rss_kb():
    """Resident memory of the throwaway nginx (master + workers)."""
    master = int(open(f"{BASE}/nginx.pid").read())
    pids = [master] + [int(x) for x in subprocess.run(["pgrep", "-P", str(master)], capture_output=True, text=True).stdout.split()]
    total = 0
    for pid in pids:
        for line in open(f"/proc/{pid}/status"):
            if line.startswith("VmRSS:"):
                total += int(line.split()[1])
    return total, len(pids)


def meminfo():
    out = subprocess.run(["free", "-m"], capture_output=True, text=True).stdout.splitlines()
    mem = out[1].split(); swap = out[2].split()
    psi = open("/proc/pressure/memory").read().splitlines()[0]
    return f"available={mem[6]}MB used={mem[2]}MB swap_used={swap[2]}MB | {psi}"


def settle(seconds=10):
    time.sleep(seconds)   # let the per-IP and upstream token buckets refill


# ---- tests ------------------------------------------------------------------------------------------------------------------
def t_fake_upstream():
    ctl("/reset"); ctl("/mode?m=ok")
    # 1. proxied, credentials attached by the relay, never taken from the client
    s, h, b = post("/v4/games", headers={"Authorization": "Bearer FROM-THE-CLIENT", "Client-ID": "client-supplied"})
    hits = upstream_hits()
    check("1  allowed endpoint is proxied (200)", s == 200 and hits, f"status={s}")
    if hits:
        check("1  relay attaches its own token and Client-ID", hits[0]["auth"] == "Bearer tok0000seed" and hits[0]["client_id"] == "testclientid0000000001",
              f"auth={hits[0]['auth']!r} client_id={hits[0]['client_id']!r}")
        check("1  a client-supplied Authorization/Client-ID never reaches upstream", "FROM-THE-CLIENT" not in json.dumps(hits) and "client-supplied" not in json.dumps(hits))

    # 2. cache
    ctl("/reset")
    a1 = post("/v4/covers", "fields image_id; where game = 7;")
    a2 = post("/v4/covers", "fields image_id; where game = 7;")
    a3 = post("/v4/covers", "fields image_id; where game = 8;")
    a4 = post("/v4/games", "fields image_id; where game = 7;")
    check("2  same query twice: MISS then HIT, one upstream request", (a1[1].get("x-relay-cache"), a2[1].get("x-relay-cache")) == ("MISS", "HIT")
          and len([r for r in upstream_hits() if "game = 7" in r["body"] and r["path"] == "/v4/covers"]) == 1,
          f"{a1[1].get('x-relay-cache')},{a2[1].get('x-relay-cache')}")
    check("2  cached body is byte-identical", a1[2] == a2[2])
    check("2  a different query or a different endpoint is a separate entry", a3[1].get("x-relay-cache") == "MISS" and a4[1].get("x-relay-cache") == "MISS")
    ctl("/reset")
    b1 = post("/v4/covers?cachebust=123&x=y", "fields image_id; where game = 7;")
    check("2  a query string cannot bust the cache nor reach upstream", b1[1].get("x-relay-cache") == "HIT" and not upstream_hits())

    # 3. allowlist
    ctl("/reset")
    checks = {"POST /v4/webhooks": post("/v4/webhooks")[0], "POST /v4/games/": post("/v4/games/")[0], "POST /": post("/")[0],
              "POST /v4/../etc": post("/v4/../etc")[0]}
    c = http.client.HTTPConnection("127.0.0.1", FRONT, timeout=5); c.request("GET", "/v4/games"); g = c.getresponse().status
    check("3  anything outside the five endpoints is refused", all(v in (400, 403, 404) for v in checks.values()) and g == 403 and not upstream_hits(),
          f"{checks} GET={g}")

    # 4. size cap
    ctl("/reset")
    s4 = post("/v4/games", "x" * 5000)[0]
    check("4  oversized body is refused before it costs anything", s4 == 413 and not upstream_hits(), f"status={s4}")


def t_rate_and_cache_budget():
    settle(); ctl("/reset"); ctl("/mode?m=ok")
    results = []
    def worker(i):
        t0 = time.time()
        s, h, _ = post("/v4/games", f"fields name; where id = {1000 + i};", timeout=40)
        results.append((s, h.get("retry-after"), time.time() - t0))
    threads = [threading.Thread(target=worker, args=(i,)) for i in range(30)]
    [t.start() for t in threads]; [t.join() for t in threads]
    hits = sorted(r["t"] for r in upstream_hits())
    best = max((sum(1 for x in hits if t0 <= x < t0 + 1.0) for t0 in hits), default=0)
    ok200 = sum(1 for r in results if r[0] == 200); r429 = [r for r in results if r[0] == 429]
    check("5  30 simultaneous distinct queries: never more than IGDB's 4 upstream requests in any second", best <= 4, f"max/s={best} upstream={len(hits)}")
    check("5  the excess is refused with 429 + Retry-After (and costs IGDB nothing)", len(r429) > 0 and all(r[1] for r in r429) and len(hits) == ok200,
          f"200={ok200} 429={len(r429)} upstream={len(hits)}")
    check("5  queued requests are delayed, not dropped, within the launcher's timeout", max(r[2] for r in results if r[0] == 200) < 8, f"slowest={max(r[2] for r in results if r[0]==200):.1f}s")

    settle(); ctl("/reset")
    post("/v4/games", "fields name; where id = 2000;")               # warm one entry
    statuses = [post("/v4/games", "fields name; where id = 2000;")[0] for _ in range(80)]
    check("6  cache hits spend no upstream budget", len(upstream_hits()) == 1, f"upstream={len(upstream_hits())}")
    check("6  a single address hammering the relay is cut off at ~5 req/s (burst 40)", 35 <= statuses.count(200) <= 60 and statuses.count(429) >= 20,
          f"200={statuses.count(200)} 429={statuses.count(429)}")


def t_failure_behaviour():
    settle(); ctl("/reset"); ctl("/mode?m=ok")
    warm = post("/v4/covers", "fields image_id; where game = 555;")   # cached with a 1 s test TTL
    time.sleep(2.7)      # nginx expires cache entries in whole seconds: 1.6 s is sometimes not enough to expire a 1 s entry
    ctl("/mode?m=500")
    s, h, b = post("/v4/covers", "fields image_id; where game = 555;")
    check("7  upstream outage: an expired entry is served stale, not an error", s == 200 and b == warm[2] and h.get('x-relay-cache') == 'STALE', f"status={s} cache={h.get('x-relay-cache')}")
    s2 = post("/v4/covers", "fields image_id; where game = 556;")[0]
    ctl("/mode?m=ok")
    s3, h3, _ = post("/v4/covers", "fields image_id; where game = 556;")
    check("7  an upstream error is passed through and never cached", s2 >= 500 and s3 == 200 and h3.get("x-relay-cache") == "MISS", f"err={s2} then {s3}/{h3.get('x-relay-cache')}")
    ctl("/mode?m=401")
    s4 = post("/v4/games", "fields name; where id = 777;")[0]
    ctl("/mode?m=ok")
    s5, h5, _ = post("/v4/games", "fields name; where id = 777;")
    check("8  a rejected/revoked token (401) is passed through, never cached, and recovers", s4 == 401 and s5 == 200 and h5.get("x-relay-cache") == "MISS", f"{s4} then {s5}")

    settle(); ctl("/reset"); ctl("/mode?m=slow")
    out = []
    ts = [threading.Thread(target=lambda: out.append(post("/v4/games", "fields name; where id = 888;")[0])) for _ in range(10)]
    [t.start() for t in ts]; [t.join() for t in ts]
    ctl("/mode?m=ok")
    check("9  ten identical simultaneous misses cost ONE upstream request", len(upstream_hits()) == 1 and out.count(200) == 10, f"upstream={len(upstream_hits())} ok={out.count(200)}")


def t_token_refresh():
    settle(); ctl("/reset"); ctl("/mode?m=ok")
    open(f"{BASE}/cred", "w").write("IGDB_CLIENT_ID=testclientid0000000001\nIGDB_CLIENT_SECRET=supersecrettestvalue0002\n"); os.chmod(f"{BASE}/cred", 0o600)
    env = dict(os.environ, CRED_FILE=f"{BASE}/cred", TOKEN_INC=f"{BASE}/igdb-token.inc", TOKEN_URL=f"http://127.0.0.1:{FAKE}/oauth2/token",
               NGINX_TEST_CMD=f"{NGINX} -t -p {BASE}/ -c {BASE}/nginx.conf -e {BASE}/error.log",
               NGINX_RELOAD_CMD=f"kill -HUP $(cat {BASE}/nginx.pid)")
    script = f"{HERE}/igdb-relay-refresh-token.sh"
    r = subprocess.run(["bash", script], env=env, capture_output=True, text=True); time.sleep(0.8)
    post("/v4/games", "fields name; where id = 3001;")
    seen = upstream_hits()[-1]["auth"] if upstream_hits() else None
    mode = oct(os.stat(f"{BASE}/igdb-token.inc").st_mode & 0o777)
    check("10 refresh script fetches a token, updates the file (0600) and reloads nginx", r.returncode == 0 and seen and seen != "Bearer tok0000seed" and mode == "0o600",
          f"rc={r.returncode} upstream now sees {seen!r} mode={mode} stderr={r.stderr.strip()[:80]!r}")
    check("10 neither the secret nor the token is written to the script's log", "supersecret" not in r.stderr and "tok0" not in r.stderr)
    before = open(f"{BASE}/igdb-token.inc").read()
    ctl("/mode?m=token_400")
    r2 = subprocess.run(["bash", script], env=env, capture_output=True, text=True)
    check("10 a failed refresh exits non-zero and leaves the working token untouched", r2.returncode != 0 and open(f"{BASE}/igdb-token.inc").read() == before, f"rc={r2.returncode}")
    ctl("/mode?m=token_evil")
    r3 = subprocess.run(["bash", script], env=env, capture_output=True, text=True)
    check("10 a token containing config syntax is refused, never written into nginx config", r3.returncode != 0 and open(f"{BASE}/igdb-token.inc").read() == before, f"rc={r3.returncode}")
    ctl("/mode?m=token_slow")
    p = subprocess.Popen(["bash", script], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    leaked = False; t_end = time.time() + 1.6
    while time.time() < t_end:
        for pid in filter(str.isdigit, os.listdir("/proc")):
            try:
                if b"supersecrettestvalue0002" in open(f"/proc/{pid}/cmdline", "rb").read(): leaked = True
            except OSError:
                pass
        time.sleep(0.05)
    p.wait(); ctl("/mode?m=ok")
    check("10 the client secret never appears in any process's command line while the request is in flight", not leaked)


def t_token_refresh_hardening():
    """The refresh script treats the Twitch response as DATA and can never leave the token file broken (audit findings)."""
    ctl("/reset"); ctl("/mode?m=ok")
    inc = f"{BASE}/hardening-token.inc"
    open(f"{BASE}/cred2", "w").write("IGDB_CLIENT_ID=testclientid0000000001\nIGDB_CLIENT_SECRET=supersecrettestvalue0002\n"); os.chmod(f"{BASE}/cred2", 0o600)
    good_test = f"{NGINX} -t -p {BASE}/ -c {BASE}/nginx.conf -e {BASE}/error.log"
    script = f"{HERE}/igdb-relay-refresh-token.sh"

    def run(mode="ok", reload_cmd="true", test_cmd="true", token_inc=inc):
        ctl(f"/mode?m={mode}")
        env = dict(os.environ, CRED_FILE=f"{BASE}/cred2", TOKEN_INC=token_inc, TOKEN_URL=f"http://127.0.0.1:{FAKE}/oauth2/token",
                   NGINX_TEST_CMD=test_cmd, NGINX_RELOAD_CMD=reload_cmd)
        return subprocess.run(["bash", script], env=env, capture_output=True, text=True)

    def leftovers():
        return sorted(f for f in os.listdir(BASE) if f.startswith("hardening-token.inc") and f != "hardening-token.inc")

    # a good run first, so there IS a previous token to protect
    r = run()
    good = open(inc).read() if r.returncode == 0 else ""
    check("12 baseline: a valid response is accepted, records its lifetime, and leaves no temp files",
          r.returncode == 0 and "Bearer tok" in good and "valid for 5000000 s" in good and not leftovers(), f"rc={r.returncode} leftovers={leftovers()}")

    bad = {"access_token is a NUMBER": "tk_num", "access_token is null": "tk_null", "access_token is a list": "tk_list", "access_token is an object": "tk_dict",
           "access_token is a boolean": "tk_bool", "token_type is not bearer": "tk_mac", "token_type missing": "tk_notype", "expires_in missing": "tk_noexp",
           "expires_in is a string": "tk_expstr", "expires_in negative": "tk_expneg", "expires_in below one hour": "tk_expsmall",
           "expires_in above one year": "tk_exphuge", "expires_in a float": "tk_expfloat", "expires_in a boolean": "tk_expbool",
           "response is a JSON array": "tk_array", "response is HTML": "tk_html", "response is oversized": "tk_big",
           "access_token holds config syntax": "token_evil", "Twitch says 400": "token_400"}
    failures = []
    for label, mode in bad.items():
        r = run(mode)
        if r.returncode == 0 or open(inc).read() != good or leftovers():
            failures.append(f"{label}: rc={r.returncode} unchanged={open(inc).read() == good} leftovers={leftovers()}")
        elif "supersecret" in r.stderr or "tok0" in r.stderr:
            failures.append(f"{label}: log leaked a value")
        elif mode in ("tk_num", "tk_null", "tk_list", "tk_dict", "tk_bool") and "access_token is not a string" not in r.stderr:
            failures.append(f"{label}: refused, but not for the intended reason (a crash is not a validation): {r.stderr.strip()[:80]!r}")
    check("12 every malformed/unsafe Twitch response is refused, the working token is untouched, nothing is left behind, nothing leaks",
          not failures, "; ".join(failures)[:300] or f"{len(bad)} cases")

    r = run("ok", reload_cmd="false")
    check("13 a FAILED RELOAD restores exactly the previous token file", r.returncode != 0 and open(inc).read() == good and not leftovers(), f"rc={r.returncode}")
    r = run("ok", test_cmd="false")
    check("13 a REJECTED CONFIG restores exactly the previous token file", r.returncode != 0 and open(inc).read() == good and not leftovers(), f"rc={r.returncode}")

    first = f"{BASE}/hardening-first.inc"
    stale = first + ".prev"
    open(stale, "w").write("STALE-BACKUP-FROM-A-PREVIOUS-RUN\n")           # the old script would have restored this into the token file
    r = run("ok", reload_cmd="false", token_inc=first)
    placeholder = open(first).read() if os.path.exists(first) else None
    mode_ok = os.path.exists(first) and oct(os.stat(first).st_mode & 0o777) == "0o600"
    check("13 FIRST INSTALLATION with a failed reload leaves a valid empty placeholder (never a missing file, never a stale backup), mode 0600",
          r.returncode != 0 and placeholder == "" and mode_ok and "STALE" not in (placeholder or ""), f"rc={r.returncode} content={placeholder!r} mode_ok={mode_ok}")
    ok_first = run("ok", token_inc=first + ".2")
    check("13 FIRST INSTALLATION that succeeds creates the file (0600)", ok_first.returncode == 0 and "Bearer tok" in open(first + ".2").read())
    for extra in (first, stale, first + ".2"):
        if os.path.exists(extra): os.remove(extra)


def t_token_expiry_aware_renewal():
    """Codex finding: the script validated a token's lifetime but never acted on it - a token good for as little as the accepted
    minimum (one hour) would leave IGDB unavailable for most of the week until the next cron run. Drives the real script's scheduling
    decision through RENEW_SCHEDULE_CMD/RENEW_CANCEL_CMD (test seams), never real systemd - that is proven separately, live, once."""
    ctl("/reset")
    inc = f"{BASE}/expiry-token.inc"
    open(f"{BASE}/cred3", "w").write("IGDB_CLIENT_ID=testclientid0000000001\nIGDB_CLIENT_SECRET=supersecrettestvalue0002\n"); os.chmod(f"{BASE}/cred3", 0o600)
    calls, cancels = f"{BASE}/schedule-calls.txt", f"{BASE}/cancel-calls.txt"
    script = f"{HERE}/igdb-relay-refresh-token.sh"

    def run(mode="ok", extra_env=None):
        ctl(f"/mode?m={mode}")
        env = dict(os.environ, CRED_FILE=f"{BASE}/cred3", TOKEN_INC=inc, TOKEN_URL=f"http://127.0.0.1:{FAKE}/oauth2/token",
                   NGINX_TEST_CMD="true", NGINX_RELOAD_CMD="true",
                   RENEW_SCHEDULE_CMD=f"bash -c 'echo \"$1\" >> {calls}' --", RENEW_CANCEL_CMD=f"bash -c 'echo called >> {cancels}'")
        env.update(extra_env or {})
        return subprocess.run(["bash", script], env=env, capture_output=True, text=True)

    def reset_markers():
        for f in (calls, cancels):
            if os.path.exists(f): os.remove(f)

    reset_markers()
    r = run("tk_hour")   # the accepted minimum, 3600s
    scheduled = open(calls).read().split() if os.path.exists(calls) else []
    check("15 a token good for only one hour schedules an early renewal (5-minute safety margin: 3300s)",
          r.returncode == 0 and scheduled == ["3300"], f"rc={r.returncode} scheduled={scheduled} stderr={r.stderr.strip()[:150]!r}")

    reset_markers()
    r2 = run("ok")   # the default ~57-day response - comfortably past the weekly cadence
    check("15 a token that outlives the week schedules nothing, and cancels any earlier pending early renewal",
          r2.returncode == 0 and not os.path.exists(calls) and os.path.exists(cancels), f"rc={r2.returncode} scheduled={os.path.exists(calls)}")

    reset_markers()
    r3 = run("tk_hour", {"RENEW_SAFETY_MARGIN": "3599"})   # the margin nearly consumes the token's whole life
    scheduled3 = open(calls).read().split() if os.path.exists(calls) else []
    check("15 the delay is never scheduled below its floor, even when the safety margin nearly exceeds the token's life",
          r3.returncode == 0 and scheduled3 == ["30"], f"scheduled={scheduled3}")

    reset_markers()
    r4 = run("tk_hour", {"RENEW_SCHEDULE_CMD": "false"})   # scheduling itself fails
    check("15 a scheduling failure does not roll back the already-committed token/reload, and the run still exits 0",
          r4.returncode == 0 and "early renewal FAILED" in r4.stderr and "FAILED:" not in r4.stderr, f"rc={r4.returncode} stderr={r4.stderr.strip()[:200]!r}")
    reset_markers()


def t_failure_retry_scheduling():
    """Audit finding: a FAILED attempt used to just exit, leaving the weekly cron as the only way back. It now schedules its own
    BOUNDED retry, with the attempt count threaded through so it eventually gives up rather than retrying forever."""
    ctl("/reset")
    inc = f"{BASE}/retry-token.inc"
    open(f"{BASE}/cred4", "w").write("IGDB_CLIENT_ID=testclientid0000000001\nIGDB_CLIENT_SECRET=supersecrettestvalue0002\n"); os.chmod(f"{BASE}/cred4", 0o600)
    calls = f"{BASE}/retry-calls.txt"
    script = f"{HERE}/igdb-relay-refresh-token.sh"

    def run(mode, extra_env=None):
        ctl(f"/mode?m={mode}")
        env = dict(os.environ, CRED_FILE=f"{BASE}/cred4", TOKEN_INC=inc, TOKEN_URL=f"http://127.0.0.1:{FAKE}/oauth2/token",
                   NGINX_TEST_CMD="true", NGINX_RELOAD_CMD="true", RENEW_SCHEDULE_CMD="true", RENEW_CANCEL_CMD="true",
                   RENEW_RETRY_CMD=f"bash -c 'echo \"$1 $2\" >> {calls}' --", RENEW_MAX_RETRIES="2")
        env.update(extra_env or {})
        return subprocess.run(["bash", script], env=env, capture_output=True, text=True)

    def logged():
        return open(calls).read().split() if os.path.exists(calls) else []

    if os.path.exists(calls): os.remove(calls)
    r1 = run("token_400")                                    # a failed attempt (a rejected secret, a transient Twitch problem, ...)
    check("16 a failed attempt schedules its own retry (delay, attempt number 1)", r1.returncode != 0 and logged() == ["600", "1"], f"rc={r1.returncode} logged={logged()}")

    if os.path.exists(calls): os.remove(calls)
    r2 = run("token_400", {"RENEW_RETRY_COUNT": "1"})        # as if THIS run was already that first retry, and it also failed
    check("16 a second consecutive failure schedules attempt number 2, still within the bound", r2.returncode != 0 and logged() == ["600", "2"], f"logged={logged()}")

    if os.path.exists(calls): os.remove(calls)
    r3 = run("token_400", {"RENEW_RETRY_COUNT": "2"})        # RENEW_MAX_RETRIES=2 already reached
    check("16 the retry count is BOUNDED - once the max is reached, no further retry is scheduled", r3.returncode != 0 and not logged() and "giving up" in r3.stderr,
          f"logged={logged()} stderr={r3.stderr.strip()[:150]!r}")

    if os.path.exists(calls): os.remove(calls)
    r4 = run("ok", {"RENEW_RETRY_COUNT": "1"})               # a SUCCESSFUL run never schedules a "failure retry", whatever count it was handed
    check("16 a successful run never schedules a failure-retry, however many failures preceded it", r4.returncode == 0 and not logged())
    if os.path.exists(calls): os.remove(calls)


def curl_code(url, pin=None, extra=(), body="fields name; limit 1;"):
    cmd = ["curl", "-sk", "-m", "10", "-o", "/dev/null", "-w", "%{http_code}", "-X", "POST", url, "-d", body, *extra]
    if pin:
        cmd += ["--pinnedpubkey", "sha256//" + pin.split("/", 1)[1]]      # curl computes the key hash itself: an independent check of the pin
    r = subprocess.run(cmd, capture_output=True, text=True)
    return r.returncode, r.stdout.strip()


def t_tls_pinned():
    """The public hop over HTTPS with a self-signed certificate the launcher pins (audit finding 3: HTTP lets a network attacker substitute
    identities, cross-references and cover ids)."""
    settle(3); ctl("/reset"); ctl("/mode?m=ok")
    env = dict(os.environ, TLS_DIR=f"{BASE}/tls")
    script = f"{HERE}/igdb-relay-make-cert.sh"
    r = subprocess.run(["bash", script, "127.0.0.1"], env=env, capture_output=True, text=True)
    pin = r.stdout.strip()
    key_mode = oct(os.stat(f"{BASE}/tls/relay.key").st_mode & 0o777) if os.path.exists(f"{BASE}/tls/relay.key") else None
    check("14 the certificate script prints a well-formed pin and creates a 0600 key", r.returncode == 0 and re.fullmatch(r"sha256/[A-Za-z0-9+/]{43}=", pin) and key_mode == "0o600",
          f"rc={r.returncode} pin={pin[:12]}... mode={key_mode} {r.stderr.strip()[:80]}")
    before = open(f"{BASE}/tls/relay.crt").read()
    r2 = subprocess.run(["bash", script, "127.0.0.1"], env=env, capture_output=True, text=True)
    check("14 it refuses to replace an existing key without --force (a new key silently breaks every pinned build)", r2.returncode != 0 and open(f"{BASE}/tls/relay.crt").read() == before)

    stop_nginx(); render("fake", "1s", tls=True); start_nginx()
    url = f"https://127.0.0.1:{FRONT}"
    rc_ok, code_ok = curl_code(url + "/v4/games", pin)
    rc_hit, _ = curl_code(url + "/v4/games", pin)
    check("14 over TLS with the right pin the relay serves the request (cache works over TLS too)", rc_ok == 0 and code_ok == "200" and len(upstream_hits()) == 1, f"rc={rc_ok} http={code_ok} upstream={len(upstream_hits())}")
    wrong = "sha256/" + "A" * 43 + "="
    rc_bad, code_bad = curl_code(url + "/v4/games", wrong)
    check("14 with a WRONG pin the connection is refused before any request is sent (an impostor's key is rejected)", rc_bad == 90 and code_bad in ("", "000") and len(upstream_hits()) == 1, f"curl rc={rc_bad}")
    rc_web, code_web = curl_code(url + "/v4/webhooks", pin)
    check("14 the allowlist still applies over TLS", rc_web == 0 and code_web == "404", f"http={code_web}")
    c = http.client.HTTPConnection("127.0.0.1", FRONT, timeout=5); c.request("POST", "/v4/games", body="x"); plain = c.getresponse().status
    check("14 plain HTTP to the TLS port is refused (400), never served", plain == 400, f"http={plain}")
    rc_old, _ = curl_code(url + "/v4/games", pin, extra=("--tlsv1.1", "--tls-max", "1.1"))
    rc_12, code_12 = curl_code(url + "/v4/games", pin, extra=("--tlsv1.2", "--tls-max", "1.2"))
    check("14 TLS 1.1 is refused, TLS 1.2 is accepted", rc_old != 0 and rc_12 == 0 and code_12 == "200", f"tls1.1 rc={rc_old}; tls1.2 rc={rc_12} http={code_12}")
    stop_nginx(); render("fake", "1s"); start_nginx()      # back to the plain test instance for the measurements that follow


def t_real_tls_leg():
    """One request to the REAL api.igdb.com through the relay with a dummy token: expected answer is IGDB's own 401. It proves DNS resolution, TLS
    verification, SNI and the Host header work from this VM. No real credential is involved and nothing is fetched."""
    settle(3)
    stop_nginx()
    render("real", "1s")
    open(f"{BASE}/igdb-token.inc", "w").write('proxy_set_header Authorization "Bearer dummytoken000000000001";\nproxy_set_header Client-ID "dummyclientid00000001";\n')
    start_nginx()
    q = "fields id; limit 1; where id = 424242;"        # unique: no earlier test may have left a cache entry for it
    s, h, b = post("/v4/games", q, timeout=25)
    s2 = post("/v4/games", q, timeout=25)
    err = subprocess.run(f"grep -E 'SSL|resolve|upstream' {BASE}/error.log | tail -2", shell=True, capture_output=True, text=True).stdout.strip()
    check("11 TLS leg to real api.igdb.com works (IGDB answers 401 to a dummy token; not cached)", s == 401 and s2[0] == 401 and h.get("x-relay-cache") == "MISS",
          f"first={s} second={s2[0]} {err[:150]}")


def main():
    shutil.rmtree(BASE, ignore_errors=True)
    for d in ("cache", "tmp/body", "tmp/proxy", "tmp/fcgi", "tmp/uwsgi", "tmp/scgi"):
        os.makedirs(f"{BASE}/{d}")
    print("BEFORE:", meminfo())
    procs["fake"] = subprocess.Popen(["nice", "-n", "10", sys.executable, f"{HERE}/fake_igdb.py", str(FAKE), str(CTL)])
    time.sleep(0.7)
    open(f"{BASE}/igdb-token.inc", "w").write('proxy_set_header Authorization "Bearer tok0000seed";\nproxy_set_header Client-ID "testclientid0000000001";\n')
    render("fake", "1s")
    start_nginx()
    base_rss = rss_kb()
    print(f"nginx idle: {base_rss[0]/1024:.1f} MB across {base_rss[1]} processes")
    try:
        if os.environ.get("RELAY_TESTS") == "hardening":       # quick mode for mutation checks: only the token-refresh hardening
            t_token_refresh_hardening(); t_token_expiry_aware_renewal(); t_failure_retry_scheduling()
        else:
            t_fake_upstream(); t_rate_and_cache_budget(); t_failure_behaviour(); t_token_refresh(); t_token_refresh_hardening()
            t_token_expiry_aware_renewal(); t_failure_retry_scheduling(); t_tls_pinned()
        loaded = rss_kb()
        entries = int(subprocess.run(f"find {BASE}/cache -type f | wc -l", shell=True, capture_output=True, text=True).stdout)
        size = subprocess.run(f"du -sk {BASE}/cache", shell=True, capture_output=True, text=True).stdout.split()[0]
        print(f"nginx after the load tests: {loaded[0]/1024:.1f} MB across {loaded[1]} processes; cache entries={entries} disk={size} KB")
        print("DURING:", meminfo())
        t_real_tls_leg()
    finally:
        stop_nginx()
        p = procs.pop("fake", None)
        if p: p.kill()
        shutil.rmtree(BASE, ignore_errors=True)
        print("AFTER:", meminfo())
    print(f"\n{sum(RESULTS)}/{len(RESULTS)} checks passed")
    sys.exit(0 if all(RESULTS) else 1)


main()
