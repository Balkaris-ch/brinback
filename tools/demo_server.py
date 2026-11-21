"""Serves the real UI against made-up windows, for README screenshots - so no screenshot of the
project ever shows anyone's actual window titles.  python tools/demo_server.py  -> :47399

Six screens like a real desk: two on top, three across, a tall portrait one on the right. The
Left screen holds two layouts at once - a 2x2 group with a 3-column group stacked in front of it -
because that is the case the map has to make readable."""
import http.server, json, pathlib

UI_FILE = pathlib.Path(__file__).resolve().parent.parent / "src" / "ui" / "index.html"

M = [dict(i=0, name="Left", x=-2560, y=0, w=2560, h=1440, primary=False),
     dict(i=1, name="Top left", x=-1374, y=-1446, w=2560, h=1440, primary=False),
     dict(i=2, name="Main", x=0, y=0, w=2560, h=1440, primary=True),
     dict(i=3, name="Top right", x=1186, y=-1440, w=2560, h=1440, primary=False),
     dict(i=4, name="Right", x=2560, y=0, w=2560, h=1440, primary=False),
     dict(i=5, name="Far right", x=5120, y=-1245, w=1100, h=3840, primary=False)]
MON = {m["i"]: m for m in M}

W = []
def win(title, mon, fx, fy, fw, fh, z, snapped=True, process="claude"):
    """A window by fractions of its screen; x/y/w/h include the invisible 7px border like Windows."""
    m = MON[mon]
    vx, vy, vw, vh = m["x"] + round(fx * m["w"]), m["y"] + round(fy * m["h"]), round(fw * m["w"]), round(fh * m["h"])
    w = dict(h=str(1000 + len(W)), title=title, process=process, claude=process == "claude", x=vx - 7, y=vy, w=vw + 14, h2=vh + 7,
             vx=vx, vy=vy, vw=vw, vh=vh, min=False, max=False, z=z, mon=mon, snapped=snapped)
    W.append(w)
    return w

Q = [(0, 0, .5, .5), (.5, 0, .5, .5), (0, .5, .5, .5), (.5, .5, .5, .5)]
T3 = [(0, 0, 1 / 3, 1), (1 / 3, 0, 1 / 3, 1), (2 / 3, 0, 1 / 3, 1)]
H2 = [(0, 0, .5, 1), (.5, 0, .5, 1)]

# Left: a 2x2 (behind) and three columns (in front)
web = [win(t, 0, *Q[k], z=20 + k) for k, t in enumerate(["Website redesign", "Design system", "Copy review", "Analytics"])]
res = [win(t, 0, *T3[k], z=1 + k) for k, t in enumerate(["Market research", "Interview notes", "Competitor map"])]
# Main: a saved 2x2 with one session not open
core = [win(t, 2, *Q[k], z=4 + k) for k, t in enumerate(["API refactor", "Release notes", "Data pipeline"])]
# Top left: two snapped side by side in Windows, not saved yet
snap = [win(t, 1, *H2[k], z=8 + k) for k, t in enumerate(["Mobile app", "Store listing"])]
# Top right: windows just placed, not grouped
loose = [win("Onboarding docs", 3, .08, .1, .45, .6, 10, snapped=False), win("Pitch outline", 3, .5, .3, .42, .55, 11, snapped=False),
         win("Mail", 3, .3, .55, .3, .35, 12, snapped=False, process="outlook")]
# Right: halves, saved
ops = [win(t, 4, *H2[k], z=14 + k) for k, t in enumerate(["Support inbox", "Billing"])]
# Far right: a tall column of two
tall = [win("Daily log", 5, 0, 0, 1, .5, 16, snapped=False), win("Scratchpad", 5, 0, .5, 1, .5, 17, snapped=False)]

def slots(ws, missing=(), shape=Q, mon=None, layout=True):
    out = []
    for k, w in enumerate(ws):
        out.append(dict(i=k, title=w["title"], savedTitle=w["title"], sessionId=None, process=w["process"], x=w["x"], y=w["y"], w=w["w"], h2=w["h2"],
                        max=False, z=k, mon=w["mon"], open=True, h=w["h"], layout=layout, zone="layout" if layout else None))
    for t in missing:
        k = len(out); fx, fy, fw, fh = shape[k]; m = MON[mon]
        out.append(dict(i=k, title=t, savedTitle=t, sessionId=None, process="claude", x=m["x"] + round(fx * m["w"]) - 7, y=m["y"] + round(fy * m["h"]),
                        w=round(fw * m["w"]) + 14, h2=round(fh * m["h"]) + 7, max=False, z=k, mon=mon, open=False, h=None, layout=layout, zone="layout" if layout else None))
    return out

G = [dict(id="web", name="Website", auto=True, saved="21 Nov 09:20", snap=False, slots=slots(web)),
     dict(id="res", name="Research", auto=True, saved="21 Nov 10:05", snap=False, slots=slots(res)),
     dict(id="core", name="Platform", auto=True, saved="20 Nov 18:02", snap=False, slots=slots(core, missing=("Load testing",), mon=2)),
     dict(id="ops", name="Operations", auto=False, saved="19 Nov 16:40", snap=False, slots=slots(ops))]
for g in G: g["open"] = sum(s["open"] for s in g["slots"])

def det(key, mon, kind, ws, saved=None, hidden=False):
    return dict(key=key, mon=mon, monName=MON[mon]["name"], kind=kind, index=int(key.split("-")[1]), tiled=True, coverage=1, behind=0,
                hidden=hidden, handles=[w["h"] for w in ws], snap=[w["h"] for w in ws] if kind == "snap" else [], savedAs=saved)
D = [det("0-0", 0, "snap", res, "Research"), det("0-1", 0, "snap", web, "Website", hidden=True), det("1-0", 1, "snap", snap),
     det("2-0", 2, "snap", core), det("3-0", 3, "placed", loose[:2]), det("4-0", 4, "snap", ops, "Operations")]
STATE = dict(monitors=M, windows=W, groups=G, detected=D, detectedAll=D, claudeFound=True, version="1.5.0",
             settings=dict(autoRestore=True, startWithWindows=True, keepSnaps=False))

class H(http.server.BaseHTTPRequestHandler):
    def send(self, code, body, ct):
        b = body.encode(); self.send_response(code); self.send_header("Content-Type", ct); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        if self.path.startswith("/api/state"): return self.send(200, json.dumps(STATE), "application/json")
        # read on every request, so UI edits show on reload
        return self.send(200, UI_FILE.read_text(encoding="utf-8").replace("__TOKEN__", "demo"), "text/html; charset=utf-8")
    def do_POST(self): return self.send(200, json.dumps(dict(ok=True, message="Demo - nothing moved.")), "application/json")
    def log_message(self, *a): pass

http.server.ThreadingHTTPServer(("127.0.0.1", 47399), H).serve_forever()
