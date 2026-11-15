"""Window/door units in one elevation's drafted 2D linework.
usage: python units.py <dir> <FACE> [--skip LAYER,LAYER]
reads  <dir>/elev_<FACE>.csv  rows: layer,u0,z0,u1,z1 (inches; u = along the view's right direction, z = model Z)
writes <dir>/units_<FACE>.json [{u0,u1,z0,z1,w,h,panes}]

A 'glass' is an innermost closed rectangle (>= 12" each way, no rectangle >= 6" inside it). A unit frame is the
largest closed rectangle holding exactly one glass with every margin <= MARGIN (trim sits further out). Panes that
touch with the same head and sill (slider sashes, a pair sharing a mullion) merge into one unit, which then grows to
an enclosing closed rectangle within OUTER (the slider's outer frame)."""
import csv, json, sys, os
from collections import defaultdict

D, FACE = sys.argv[1], sys.argv[2]
SKIP = set(sys.argv[sys.argv.index("--skip") + 1].split(",")) if "--skip" in sys.argv else set()
TOL, MARGIN, PANE_GAP, OUTER = 0.2, 6.6, 1.0, 1.5

H, V = [], []
for r in csv.reader(open(os.path.join(D, f"elev_{FACE}.csv"))):
    if r[0].split("|")[0] in SKIP: continue
    u0, z0, u1, z1 = map(float, r[1:5])
    if abs(z0 - z1) < 0.01 and abs(u0 - u1) > 0.05: H.append((round((z0 + z1) / 2, 3), min(u0, u1), max(u0, u1)))
    elif abs(u0 - u1) < 0.01 and abs(z0 - z1) > 0.05: V.append((round((u0 + u1) / 2, 3), min(z0, z1), max(z0, z1)))
Hby, Vby = defaultdict(list), defaultdict(list)
for h in H: Hby[round(h[0])].append(h)
for v in V: Vby[round(v[0])].append(v)
def near(d, c): return d[round(c) - 1] + d[round(c)] + d[round(c) + 1]
def cover(lines, c, a, b):
    iv = sorted((max(a, s0), min(b, s1)) for (cc, s0, s1) in lines if abs(cc - c) <= TOL and s1 > a and s0 < b)
    tot, cur = 0.0, a
    for s0, s1 in iv:
        if s1 <= cur: continue
        tot += s1 - max(s0, cur); cur = s1
    return tot / (b - a)
def closed(u0, u1, z0, z1):
    return (cover(near(Hby, z0), z0, u0, u1) >= 0.95 and cover(near(Hby, z1), z1, u0, u1) >= 0.95 and
            cover(near(Vby, u0), u0, z0, z1) >= 0.95 and cover(near(Vby, u1), u1, z0, z1) >= 0.95)

vu = sorted({v[0] for v in V})
rects = set()
for (zb, a0, a1) in H:
    for (zt, b0, b1) in H:
        if not (3 <= zt - zb <= 130): continue
        lo, hi = max(a0, b0), min(a1, b1)
        if hi - lo < 3: continue
        cand = [u for u in vu if lo - TOL <= u <= hi + TOL]
        for i, u0 in enumerate(cand):
            if cover(near(Vby, u0), u0, zb, zt) < 0.95: continue
            for u1 in cand[i + 1:]:
                if 3 <= u1 - u0 <= 150 and closed(u0, u1, zb, zt): rects.add((u0, u1, zb, zt))
rects = sorted(rects)
def inside(a, b, m=0.05): return a != b and a[0] >= b[0] - m and a[1] <= b[1] + m and a[2] >= b[2] - m and a[3] <= b[3] + m
def area(r): return (r[1] - r[0]) * (r[3] - r[2])
glass = [r for r in rects if r[1] - r[0] >= 12 and r[3] - r[2] >= 12 and
         not any(inside(s, r) and s[1] - s[0] >= 6 and s[3] - s[2] >= 6 for s in rects)]
units, seen = [], set()
for g in glass:
    best = g
    for r in rects:
        if not inside(g, r): continue
        if max(g[0] - r[0], r[1] - g[1], g[2] - r[2], r[3] - g[3]) > MARGIN: continue
        if any(inside(g2, r) for g2 in glass if g2 != g): continue
        if area(r) > area(best): best = r
    k = tuple(round(v, 2) for v in best)
    if k not in seen: seen.add(k); units.append({"u0": k[0], "u1": k[1], "z0": k[2], "z1": k[3], "panes": 1})
units.sort(key=lambda u: (u["z0"], u["u0"]))
merged = []
for u in units:
    m = next((p for p in merged if abs(p["z1"] - u["z1"]) < 0.6 and abs(p["z0"] - u["z0"]) < 0.6
              and -3 <= u["u0"] - p["u1"] <= PANE_GAP), None)
    if m: m["u1"] = u["u1"]; m["panes"] += 1
    else: merged.append(dict(u))
for u in merged:
    if u["panes"] < 2: continue
    box = (u["u0"], u["u1"], u["z0"], u["z1"])
    outer = [r for r in rects if inside(box, r, 0.6) and max(box[0] - r[0], r[1] - box[1], box[2] - r[2], r[3] - box[3]) <= OUTER]
    if outer:
        r = max(outer, key=area); u["u0"], u["u1"], u["z0"], u["z1"] = (round(v, 2) for v in r)
out = []
for u in merged:
    u["w"] = round(u["u1"] - u["u0"], 3); u["h"] = round(u["z1"] - u["z0"], 3)
    if u["w"] >= 12 and u["h"] >= 20: out.append(u)
out.sort(key=lambda u: (u["z0"] // 60, u["u0"]))
json.dump(out, open(os.path.join(D, f"units_{FACE}.json"), "w"), indent=0)
print(FACE, len(rects), "rects", len(glass), "glass ->", len(out), "units")
for u in out: print(f"  u {u['u0']:9.2f}..{u['u1']:9.2f}  z {u['z0']:8.2f}..{u['z1']:8.2f}  {u['w']:6.2f} x {u['h']:6.2f}  panes {u['panes']}")
