#!/usr/bin/env python3
"""REAL systemd, no mocks: proves the renewal chain survives two consecutive short-lived tokens (no unit-name collision - the exact
bug a mocked RENEW_SCHEDULE_CMD cannot see, since it never asks real systemd to recreate anything), and that a FAILED attempt
genuinely recovers through a real scheduled retry. Needs sudo (system-scope transient units; this VM's azureuser has passwordless
sudo). Isolated from production by a unique --unit prefix per run; cleans up every unit it creates, even on failure.

Deliberately separate from relay_test.py: this one needs root and real wall-clock waits (worst case a couple of minutes), so it is
not part of the fast, sudo-free suite that runs for every change - run it manually after any change to the renewal SCHEDULING logic
in igdb-relay-refresh-token.sh specifically (schedule_early_renewal / cancel_early_renewal / maybe_retry_after_failure)."""
import json, os, shutil, subprocess, sys, time, urllib.request, uuid

HERE = os.path.dirname(os.path.abspath(__file__))
FAKE_PORT, CTL_PORT = 19100, 19101
PREFIX = f"igdb-relay-selftest-{uuid.uuid4().hex[:8]}"
SCRIPT = f"{HERE}/igdb-relay-refresh-token.sh"
BASE = f"/tmp/relay-systemd-test-{uuid.uuid4().hex[:8]}"
RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append(bool(ok))
    print(("PASS  " if ok else "FAIL  ") + name + (f"   [{detail}]" if detail else ""), flush=True)


def ctl(path):
    return json.loads(urllib.request.urlopen(f"http://127.0.0.1:{CTL_PORT}{path}", timeout=5).read())


def units():
    out = subprocess.run(["systemctl", "list-units", "--all", "--no-legend", "--plain", f"{PREFIX}-*"], capture_output=True, text=True).stdout
    return [line.split()[0] for line in out.splitlines() if line.strip()]


def pending_timers():
    return [u for u in units() if u.endswith(".timer")]


def cleanup():
    for u in units():
        subprocess.run(["sudo", "-n", "systemctl", "stop", u], capture_output=True)
        subprocess.run(["sudo", "-n", "systemctl", "reset-failed", u], capture_output=True)


def run_once(mode, extra_env=None):
    """One FOREGROUND invocation of the real script, root, exactly like cron's own run - never itself scheduled."""
    ctl(f"/mode?m={mode}")
    env = {"CRED_FILE": f"{BASE}/cred", "TOKEN_INC": f"{BASE}/token.inc", "TOKEN_URL": f"http://127.0.0.1:{FAKE_PORT}/oauth2/token",
           "NGINX_TEST_CMD": "true", "NGINX_RELOAD_CMD": "true", "RENEW_UNIT_PREFIX": PREFIX,
           "RENEW_SAFETY_MARGIN": "3599", "RENEW_MIN_DELAY": "4", "RENEW_RETRY_DELAY": "4", "RENEW_MAX_RETRIES": "2"}
    env.update(extra_env or {})
    cmd = ["sudo", "-n", "env"] + [f"{k}={v}" for k, v in env.items()] + ["bash", SCRIPT]
    return subprocess.run(cmd, capture_output=True, text=True)


def tokens():
    return ctl("/x")["tokens"]


def read_root_file(path):
    """The token file is written by the script running as root (mode 0600) - this process is the ordinary azureuser, so a plain
    open() would hit PermissionError. None (not "") means the file doesn't exist; "" means it exists but is empty."""
    r = subprocess.run(["sudo", "-n", "cat", path], capture_output=True, text=True)
    return r.stdout if r.returncode == 0 else None


def wait_for_at_least(count, timeout=40):
    """Waits for ctl('/x')['tokens'] to reach `count` - an ABSOLUTE value the caller computed from its own baseline, since /reset
    clears the request log but never this counter (it is cumulative for the whole run)."""
    deadline = time.time() + timeout
    n = tokens()
    while time.time() < deadline and n < count:
        time.sleep(0.5)
        n = tokens()
    return n


def wait_until(predicate, timeout=40):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if predicate():
            return True
        time.sleep(0.5)
    return predicate()


def main():
    if subprocess.run(["sudo", "-n", "true"], capture_output=True).returncode != 0:
        print("FAIL  passwordless sudo is required for this test and is not available here"); sys.exit(2)

    os.makedirs(BASE, exist_ok=True)
    with open(f"{BASE}/cred", "w") as f:
        f.write("IGDB_CLIENT_ID=testclientid0000000001\nIGDB_CLIENT_SECRET=supersecrettestvalue0002\n")
    os.chmod(f"{BASE}/cred", 0o600)
    fake = subprocess.Popen([sys.executable, f"{HERE}/fake_igdb.py", str(FAKE_PORT), str(CTL_PORT)])
    time.sleep(0.5)
    try:
        # ---- A: two consecutive SHORT-LIVED tokens must both successfully self-reschedule, through REAL systemd ------------------
        # STATE["tokens"] is CUMULATIVE for the whole run (/reset only clears the request log) - every scenario below captures its
        # own baseline right after /reset and checks increases relative to it, never an absolute value.
        ctl("/reset"); base_a = tokens()
        r1 = run_once("tk_hour")
        check("A1 a short-lived token succeeds and schedules a real transient timer", r1.returncode == 0 and pending_timers(),
              f"rc={r1.returncode} units={units()} stderr={r1.stderr.strip()[-300:]!r}")

        n1 = wait_for_at_least(base_a + 2)   # the SCHEDULED renewal fires for real and fetches token #2
        check("A2 the scheduled renewal actually fired and fetched a second token", n1 - base_a >= 2, f"tokens={n1 - base_a}")

        # That second run is ITSELF running as the scheduled unit, and the fake server keeps answering short-lived - it must
        # reschedule a THIRD one without colliding with its own currently-active unit name (the bug a fixed name hit).
        third = wait_until(lambda: len(pending_timers()) >= 1)
        check("A3 the SECOND renewal (running AS a scheduled unit) reschedules a THIRD - no self-name collision", third, f"units={units()}")

        ctl("/mode?m=ok")   # let the chain end: the third renewal gets a long-lived token
        n2 = wait_for_at_least(base_a + 3)
        check("A4 the third (chained) renewal also fired for real", n2 - base_a >= 3, f"tokens={n2 - base_a}")

        settled = wait_until(lambda: not pending_timers())
        check("A5 once a long-lived token arrives, no early-renewal timer is left pending", settled, f"units={units()}")
        cleanup()

        # ---- B: a FAILED attempt must recover on its own, not just wait for next week ----------------------------------------
        # token.inc is shared across the whole run (TOKEN_INC is fixed in run_once) - deleted here so its presence below can only be
        # attributed to what happens IN this scenario, never a leftover from A. fake_igdb.py counts a token_400 request too (it
        # increments STATE["tokens"] before answering 400), so "the counter went up" alone does NOT distinguish the FAILED first
        # attempt from a genuinely successful retry - only a SECOND increase, past what the failure itself already caused, does.
        token_path = f"{BASE}/token.inc"
        if os.path.exists(token_path): os.remove(token_path)
        ctl("/reset"); ctl("/mode?m=token_400"); base_b = tokens()
        rb1 = run_once("token_400")
        check("B1 a failed foreground attempt schedules its own retry as a real timer", rb1.returncode != 0 and pending_timers(),
              f"rc={rb1.returncode} units={units()}")
        check("B1b the failed attempt is counted once, but commits no token (the script dies before ever touching the token file)",
              tokens() - base_b == 1 and not os.path.exists(token_path), f"tokens={tokens() - base_b} file_exists={os.path.exists(token_path)}")

        ctl("/mode?m=ok")   # the transient problem clears before the retry fires
        nb = wait_for_at_least(base_b + 2)   # base+1 is the ORIGINAL failed attempt (already counted above) - recovery needs a SECOND request
        check("B2 the scheduled retry fires as a genuinely SEPARATE (second) request, not just the original failed one counted again",
              nb - base_b >= 2, f"tokens={nb - base_b}")

        committed = wait_until(lambda: "Bearer tok" in (read_root_file(token_path) or ""), timeout=10)   # root-owned (0600); read via sudo cat
        check("B3 the retry's token was actually WRITTEN to the isolated token file - a real commit, not merely a request that happened",
              committed, f"content={(read_root_file(token_path) or '')[:60]!r}")

        settled_b = wait_until(lambda: not pending_timers())
        check("B4 a successful recovery leaves no retry timer pending", settled_b, f"units={units()}")
        cleanup()

        # ---- B-guard: a retry that FIRES but FAILS must NOT look like recovery - the negative control for B2/B3 above ----------
        # Keeps failing throughout (never switches to "ok"): the retry mechanism still fires for real (a second counted request,
        # satisfying what B2 alone checks), but nothing is ever committed. If B2's request-count check were the ONLY thing recovery
        # was judged by, THIS scenario would wrongly look like success - B3's file check is what actually tells them apart.
        if os.path.exists(token_path): os.remove(token_path)
        ctl("/reset"); ctl("/mode?m=token_400"); base_g = tokens()
        rg1 = run_once("token_400")
        check("B-guard1 the first failed attempt schedules a retry, same as B1", rg1.returncode != 0 and pending_timers(), f"units={units()}")

        ng = wait_for_at_least(base_g + 2)   # the retry DOES fire for real and IS counted - this alone must not be mistaken for recovery
        check("B-guard2 the retry fires for real as a second request (proving the mechanism itself still works even though it will fail again)",
              ng - base_g >= 2, f"tokens={ng - base_g}")
        check("B-guard3 ...but it commits NOTHING (a real failed retry is correctly distinguished from a real successful one)",
              not os.path.exists(token_path), f"file_exists={os.path.exists(token_path)}")
        cleanup()
        cleanup()

        # ---- C: repeated real failures are BOUNDED - retries must actually stop, not loop forever -----------------------------
        ctl("/reset"); ctl("/mode?m=token_400"); base_c = tokens()
        rc1 = run_once("token_400", {"RENEW_MAX_RETRIES": "2", "RENEW_RETRY_DELAY": "3"})
        check("C1 the first real failure schedules retry 1/2", rc1.returncode != 0 and pending_timers(), f"units={units()}")

        after1 = wait_for_at_least(base_c + 2)   # token_400 itself is counted (fake_igdb.py increments before answering 400)
        check("C2 the first retry fired for real (a second failed attempt reached Twitch) and scheduled retry 2/2",
              after1 - base_c >= 2 and pending_timers(), f"tokens={after1 - base_c} units={units()}")

        after2 = wait_for_at_least(base_c + 3)
        check("C3 the second retry ALSO fired for real (a third failed attempt reached Twitch)", after2 - base_c >= 3, f"tokens={after2 - base_c}")

        gave_up = wait_until(lambda: not pending_timers(), timeout=20)
        check("C4 once the bound is reached it gives up for real - no fourth attempt is ever scheduled", gave_up, f"units={units()}")

        stayed = not wait_until(lambda: tokens() >= base_c + 4, timeout=20)   # a runaway retry would show up here as a 4th real request
        check("C5 no fourth attempt reaches Twitch even after waiting past when it would have fired if the bound didn't hold",
              stayed, f"tokens={tokens() - base_c}")
        cleanup()

        # ---- D: a FRESH success must cancel a DIFFERENT, still-pending (not yet fired) early renewal --------------------------
        ctl("/reset"); ctl("/mode?m=tk_hour")
        rd1 = run_once("tk_hour", {"RENEW_MIN_DELAY": "20"})   # schedule a renewal well in the future - it must NOT have fired yet below
        pending_before = pending_timers()
        check("D1 a short-lived token leaves a real timer PENDING (not yet due)", rd1.returncode == 0 and pending_before, f"units={units()}")

        rd2 = run_once("ok")   # a second, independent trigger (e.g. someone running it by hand) gets a long-lived token right away
        check("D2 the fresh long-lived success itself succeeds", rd2.returncode == 0, f"rc={rd2.returncode}")
        cancelled_promptly = wait_until(lambda: not pending_timers(), timeout=10)   # well before the 20s the stale timer would have needed to fire
        check("D3 the EARLIER, still-pending renewal is cancelled promptly by the fresh success - not left to fire on its own later",
              cancelled_promptly, f"units={units()}")
    finally:
        cleanup()
        fake.kill()
        shutil.rmtree(BASE, ignore_errors=True)

    print(f"\n{sum(RESULTS)}/{len(RESULTS)} checks passed")
    sys.exit(0 if RESULTS and all(RESULTS) else 1)


main()
