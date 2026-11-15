"""Exterior faces of one level's ACA walls, from data/walls_<LEVEL>.json.

Flood-fills a 2" occupancy grid from outside, then walks each wall side in 2" steps and keeps the stretches
whose samples reach outside air (so a wall that is exterior for only part of its length still contributes
that part). Closes L corners within 24", merges collinear pieces across gaps up to 12".

Writes data/faces_<LEVEL>.json: list of {"f":[x0,y0,x1,y1,nx,ny], "h":[wall handles], "band":max band}
in CAD inches, plus data/freestanding_<LEVEL>.json for walls with outside air on both sides (site walls).
Usage: python exterior_faces.py LOWER [x0 y0 x1 y1]   (optional bounds drop separate structures)
"""
import json, sys
from collections import deque

level = sys.argv[1]
bounds = [float(v) for v in sys.argv[2:6]] if len(sys.argv) >= 6 else None
walls = json.load(open(f"data/walls_{level}.json"))

W = []   # (horizontal, a0, a1, b0, b1, handle)
for w in walls:
    (x0, y0, _), (x1, y1, _) = w["ext"]
    if bounds and (x1 < bounds[0] or y1 < bounds[1] or x0 > bounds[2] or y0 > bounds[3]):
        continue
    s, e = w["start"], w["end"]
    if abs(s[1] - e[1]) < 0.05:
        W.append((True, x0, x1, y0, y1, w["h"]))
    elif abs(s[0] - e[0]) < 0.05:
        W.append((False, y0, y1, x0, x1, w["h"]))

C = 2.0
gx0 = min(w[1] if w[0] else w[3] for w in W) - 60
gy0 = min(w[3] if w[0] else w[1] for w in W) - 60
gx1 = max(w[2] if w[0] else w[4] for w in W) + 60
gy1 = max(w[4] if w[0] else w[2] for w in W) + 60
nx, ny = int((gx1 - gx0) / C) + 1, int((gy1 - gy0) / C) + 1
occ = bytearray(nx * ny)
for h, a0, a1, b0, b1, _ in W:
    x0, x1, y0, y1 = (a0, a1, b0, b1) if h else (b0, b1, a0, a1)
    for i in range(max(0, int((x0 - gx0) / C) - 1), min(nx - 1, int((x1 - gx0) / C) + 1) + 1):
        row = i * ny
        for j in range(max(0, int((y0 - gy0) / C) - 1), min(ny - 1, int((y1 - gy0) / C) + 1) + 1):
            occ[row + j] = 1
out = bytearray(nx * ny)
q = deque([i * ny for i in range(nx)] + [i * ny + ny - 1 for i in range(nx)] + list(range(ny)) + [(nx - 1) * ny + j for j in range(ny)])
while q:
    k = q.popleft()
    if out[k] or occ[k]:
        continue
    out[k] = 1
    i, j = divmod(k, ny)
    if i > 0: q.append(k - ny)
    if i < nx - 1: q.append(k + ny)
    if j > 0: q.append(k - 1)
    if j < ny - 1: q.append(k + 1)

def is_out(x, y):
    i, j = int((x - gx0) / C), int((y - gy0) / C)
    return 0 <= i < nx and 0 <= j < ny and out[i * ny + j] == 1

faces, free = [], []
for h, a0, a1, b0, b1, hd in W:
    sides = {}
    for sgn in (-1, 1):
        face = b0 if sgn < 0 else b1
        steps = max(1, int((a1 - a0) / 2.0))
        flags = []
        for k in range(steps + 1):
            a = a0 + (a1 - a0) * k / steps
            b = face + sgn * 5
            flags.append(is_out(a, b) if h else is_out(b, a))
        runs, start = [], None
        for k, fl in enumerate(flags + [False]):
            if fl and start is None: start = k
            if not fl and start is not None:
                r0 = a0 + (a1 - a0) * max(0, start - 1) / steps
                r1 = a0 + (a1 - a0) * min(steps, k) / steps
                if r1 - r0 >= 12: runs.append((r0, r1))
                start = None
        sides[sgn] = (runs, sum(flags) / len(flags))
    if sides[-1][1] > 0.6 and sides[1][1] > 0.6:
        free.append({"h": hd, "horizontal": h, "along": [a0, a1], "band": [b0, b1]})
        continue
    for sgn in (-1, 1):
        fpos = b0 if sgn < 0 else b1
        for r0, r1 in sides[sgn][0]:
            f = [r0, fpos, r1, fpos, 0, sgn] if h else [fpos, r0, fpos, r1, sgn, 0]
            faces.append({"f": f, "h": [hd], "band": b1 - b0})

# Drafting compensation 1: faces of one wall line drawn a hair apart (<= 1/4") snap to the longest one's position.
def pos(F): f = F["f"]; return f[1] if abs(f[1] - f[3]) < 1e-6 else f[0]
def horiz(F): f = F["f"]; return abs(f[1] - f[3]) < 1e-6
def length(F): f = F["f"]; return abs(f[2] - f[0]) + abs(f[3] - f[1])
for F in sorted(faces, key=length, reverse=True):
    for G in faces:
        if G is F or horiz(G) != horiz(F) or G["f"][4:] != F["f"][4:]: continue
        d = pos(G) - pos(F)
        if 1e-6 < abs(d) <= 0.25:
            g = G["f"]
            if horiz(G): g[1] = g[3] = pos(F)
            else: g[0] = g[2] = pos(F)
            G.setdefault("snapped", round(d, 3))

# merge collinear pieces with the same outward side across gaps <= 12"
def key(F):
    f = F["f"]
    return ("H", round(f[1], 1), f[5]) if abs(f[1] - f[3]) < 1e-6 else ("V", round(f[0], 1), f[4])
groups = {}
for F in faces: groups.setdefault(key(F), []).append(F)
merged = []
for k, g in groups.items():
    hz = k[0] == "H"
    g.sort(key=lambda F: min(F["f"][0], F["f"][2]) if hz else min(F["f"][1], F["f"][3]))
    cur = None
    for F in g:
        f = F["f"]
        lo, hi = (min(f[0], f[2]), max(f[0], f[2])) if hz else (min(f[1], f[3]), max(f[1], f[3]))
        if cur and lo - cur["hi"] <= 12:
            cur["hi"] = max(cur["hi"], hi); cur["h"] += F["h"]; cur["band"] = max(cur["band"], F["band"])
        else:
            if cur: merged.append(cur)
            cur = {"lo": lo, "hi": hi, "h": list(F["h"]), "band": F["band"], "f": f}
    merged.append(cur)
res = []
for m in merged:
    f = m["f"]
    if abs(f[1] - f[3]) < 1e-6: nf = [m["lo"], f[1], m["hi"], f[1], f[4], f[5]]
    else: nf = [f[0], m["lo"], f[0], m["hi"], f[4], f[5]]
    res.append({"f": nf, "h": m["h"], "band": round(m["band"], 3)})

# close L corners: a horizontal end and a vertical end both within 24" of their intersection
for A in res:
    for B in res:
        a, b = A["f"], B["f"]
        if not (abs(a[1] - a[3]) < 1e-6) or abs(b[1] - b[3]) < 1e-6: continue
        px, py = b[0], a[1]
        ea = 0 if abs(a[0] - px) < abs(a[2] - px) else 2
        eb = 1 if abs(b[1] - py) < abs(b[3] - py) else 3
        if abs(a[ea] - px) <= 24 and abs(b[eb] - py) <= 24:
            a[ea] = px; b[eb] = py

# Drafting compensation 2: a parallel step of <= 12" between two open face ends (a thicker wall standing proud)
# gets a return face: the recessed face extends to the proud face's end, and a jog joins them.
def ends(res):
    cnt = {}
    for F in res:
        f = F["f"]
        for p in ((round(f[0], 2), round(f[1], 2)), (round(f[2], 2), round(f[3], 2))): cnt[p] = cnt.get(p, 0) + 1
    return {p for p, v in cnt.items() if v == 1}
jogs = 0
for A in res:
    for B in res:
        a, b = A["f"], B["f"]
        if A is B or a[4:] != b[4:]: continue
        hz = abs(a[1] - a[3]) < 1e-6
        if hz != (abs(b[1] - b[3]) < 1e-6): continue
        op = ends(res)
        # along-axis index 0/2 for horizontal faces (x), 1/3 for vertical (y); across = the other coordinate
        ax = (0, 2) if hz else (1, 3); ac = 1 if hz else 0
        out = a[5] if hz else a[4]
        # A recessed relative to B (B stands further along the outward normal)
        if (b[ac] - a[ac]) * out <= 0 or abs(b[ac] - a[ac]) > 12: continue
        for ea in ax:
            pa = (round(a[0], 2), round(a[1], 2)) if ea in (0, 1) else (round(a[2], 2), round(a[3], 2))
            if pa not in op: continue
            for eb in ax:
                pb = (round(b[0], 2), round(b[1], 2)) if eb in (0, 1) else (round(b[2], 2), round(b[3], 2))
                if pb not in op or abs(a[ea] - b[eb]) > 12: continue
                a[ea] = b[eb]                                       # extend/trim the recessed face to the step
                other = a[ax[0]] if ea == ax[1] else a[ax[1]]       # A runs away from the step on this side
                nrm = 1 if other > b[eb] else -1                    # the jog faces toward A
                jog = [b[eb], a[ac], b[eb], b[ac], nrm, 0] if hz else [a[ac], b[eb], b[ac], b[eb], 0, nrm]
                res.append({"f": jog, "h": ["jog"], "band": 0}); jogs += 1
                break
            else: continue
            break

json.dump(res, open(f"data/faces_{level}.json", "w"), indent=0)
json.dump(free, open(f"data/freestanding_{level}.json", "w"), indent=0)
open_ends = 0
pts = {}
for F in res:
    f = F["f"]
    for p in ((round(f[0], 2), round(f[1], 2)), (round(f[2], 2), round(f[3], 2))):
        pts[p] = pts.get(p, 0) + 1
open_ends = sum(1 for v in pts.values() if v == 1)
print(f"{level}: walls={len(W)} faces={len(res)} jogs={jogs} snapped={sum(1 for F in faces if 'snapped' in F)} freestanding={len(free)} unmatched face ends={open_ends}")
