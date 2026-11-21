"""Serves the real UI against made-up windows, for README screenshots - so no screenshot of the
project ever shows anyone's actual window titles.  python tools/demo_server.py  -> :47399"""
import http.server, json, pathlib
UI = (pathlib.Path(__file__).resolve().parent.parent / "src" / "ui" / "index.html").read_text(encoding="utf-8").replace("__TOKEN__", "demo")
M = [dict(i=0, name="Left", x=-2560, y=0, w=2560, h=1440, primary=False),
     dict(i=1, name="Main", x=0, y=0, w=2560, h=1440, primary=True),
     dict(i=2, name="Right", x=2560, y=0, w=2560, h=1440, primary=False)]
def quad(mon, k):
    ox = M[mon]["x"]; cx, cy = (k % 2), (k // 2)
    return ox - 7 + cx * 1280, cy * 720, 1294, 727
names = {0: ["Website redesign", "Design system", "Copy review", "Analytics"],
         1: ["API refactor", "Release notes", "Bug triage", "Data pipeline"],
         2: ["Mobile app", "Store listing", "Onboarding docs"]}
W, h = [], 1000
for mon, ts in names.items():
    for k, t in enumerate(ts):
        x, y, w, hh = quad(mon, k)
        if mon == 2: x, y, w, hh = M[2]["x"] + 120 + k * 380, 120 + k * 160, 1500, 900
        W.append(dict(h=str(h), title=t, process="claude", claude=True, x=x, y=y, w=w, h2=hh, vx=x + 7, vy=y, vw=w - 14, vh=hh - 7, min=False, max=False, z=len(W), mon=mon)); h += 1
def slots(mon, titles, missing=()):
    out = []
    for k, t in enumerate(titles):
        x, y, w, hh = quad(mon, k)
        win = next((v for v in W if v["title"] == t and t not in missing), None)
        out.append(dict(i=k, title=t, savedTitle=t, sessionId=None, process="claude", x=x, y=y, w=w, h2=hh, max=False, z=k, mon=mon, open=win is not None, h=win["h"] if win else None))
    return out
G = [dict(id="web", name="Website", auto=True, saved="21 Nov 09:20", slots=slots(0, names[0])),
     dict(id="core", name="Platform", auto=True, saved="20 Nov 18:02", slots=slots(1, ["API refactor", "Release notes", "Load testing", "Data pipeline"], missing=("Load testing",)))]
for g in G: g["open"] = sum(s["open"] for s in g["slots"])
D = [dict(mon=0, monName="Left", tiled=True, coverage=1, behind=0, handles=[w["h"] for w in W if w["mon"] == 0], savedAs="Website"),
     dict(mon=1, monName="Main", tiled=True, coverage=1, behind=1, handles=[w["h"] for w in W if w["mon"] == 1], savedAs=None),
     dict(mon=2, monName="Right", tiled=False, coverage=.7, behind=0, handles=[w["h"] for w in W if w["mon"] == 2], savedAs=None)]
STATE = dict(monitors=M, windows=W, groups=G, detected=D, detectedAll=D, settings=dict(autoRestore=True, startWithWindows=True), claudeFound=True, version="1.1.0")
class H(http.server.BaseHTTPRequestHandler):
    def send(self, code, body, ct):
        b = body.encode(); self.send_response(code); self.send_header("Content-Type", ct); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        if self.path.startswith("/api/state"): return self.send(200, json.dumps(STATE), "application/json")
        return self.send(200, UI, "text/html; charset=utf-8")
    def do_POST(self): return self.send(200, json.dumps(dict(ok=True, message="Demo - nothing moved.")), "application/json")
    def log_message(self, *a): pass
http.server.ThreadingHTTPServer(("127.0.0.1", 47399), H).serve_forever()
