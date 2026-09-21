"""A fake IGDB + Twitch for testing the relay. Loopback only. Data port answers like IGDB; control port changes its behaviour."""
import json, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

DATA_PORT, CTL_PORT = int(sys.argv[1]), int(sys.argv[2])
LOCK = threading.Lock()
STATE = {"mode": "ok", "requests": [], "tokens": 0}


class Data(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):  # silence
        pass

    def _send(self, code, body, ctype="application/json", extra=None):
        raw = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(raw)))
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(raw)

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length).decode(errors="replace")
        path = urlparse(self.path)
        with LOCK:
            mode = STATE["mode"]
        if path.path == "/oauth2/token":
            with LOCK:
                STATE["tokens"] += 1
                n = STATE["tokens"]
            if mode == "token_400":
                return self._send(400, '{"status":400,"message":"invalid client secret"}')
            if mode == "token_slow":
                time.sleep(2.0)
            if mode == "token_evil":
                return self._send(200, json.dumps({"access_token": 'abc"; return 200; #', "expires_in": 5000000, "token_type": "bearer"}))
            good = {"access_token": f"tok{n:04d}abcdefghij", "expires_in": 5000000, "token_type": "bearer"}
            variants = {
                "tk_num": {**good, "access_token": 12345678901234567},
                "tk_null": {**good, "access_token": None},
                "tk_list": {**good, "access_token": ["tok0001abcdefghij"]},
                "tk_dict": {**good, "access_token": {"v": "tok0001abcdefghij"}},
                "tk_bool": {**good, "access_token": True},
                "tk_mac": {**good, "token_type": "mac"},
                "tk_notype": {k: v for k, v in good.items() if k != "token_type"},
                "tk_noexp": {k: v for k, v in good.items() if k != "expires_in"},
                "tk_expstr": {**good, "expires_in": "5000000"},
                "tk_expneg": {**good, "expires_in": -5},
                "tk_expsmall": {**good, "expires_in": 10},
                "tk_exphuge": {**good, "expires_in": 999999999},
                "tk_expfloat": {**good, "expires_in": 5000000.5},
                "tk_expbool": {**good, "expires_in": True},
                "tk_array": [good],
            }
            if mode in variants:
                return self._send(200, json.dumps(variants[mode]))
            if mode == "tk_html":
                return self._send(200, "<html><body>maintenance</body></html>", "text/html")
            if mode == "tk_big":
                return self._send(200, json.dumps({**good, "padding": "x" * 20000}))
            return self._send(200, json.dumps({"access_token": f"tok{n:04d}abcdefghij", "expires_in": 5000000, "token_type": "bearer"}))
        rec = {"t": time.time(), "path": self.path, "body": body, "client_id": self.headers.get("Client-ID"), "auth": self.headers.get("Authorization"),
               "host": self.headers.get("Host")}
        with LOCK:
            STATE["requests"].append(rec)
        if mode == "500":
            return self._send(500, '{"message":"boom"}')
        if mode == "401":
            return self._send(401, '{"message":"Authorization Failure"}')
        if mode == "slow":
            time.sleep(0.6)
        return self._send(200, json.dumps([{"id": 1, "name": "echo", "q": body}]))


class Ctl(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def do_GET(self):
        u = urlparse(self.path)
        with LOCK:
            if u.path == "/mode":
                STATE["mode"] = parse_qs(u.query)["m"][0]
            elif u.path == "/reset":
                STATE["requests"].clear()
            out = json.dumps({"mode": STATE["mode"], "requests": STATE["requests"], "tokens": STATE["tokens"]})
        raw = out.encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


class QuietServer(ThreadingHTTPServer):
    def handle_error(self, request, client_address):   # clients that hang up (a refused TLS pin, a killed script) are expected here
        pass


for port, handler in ((DATA_PORT, Data), (CTL_PORT, Ctl)):
    srv = QuietServer(("127.0.0.1", port), handler)
    srv.daemon_threads = True
    threading.Thread(target=srv.serve_forever, daemon=True).start()
while True:
    time.sleep(3600)
