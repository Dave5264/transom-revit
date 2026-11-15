"""Which face data lands on the lines the drafter actually drew? Compare old faces and side-database faces with the plan links."""
import csv, json, os
from collections import defaultdict
DX, DY = 37.0, -178.58
OLD = r"<path>"
NEW = "facetest/data"
PLAN = "openings"
NAMES = {"LOWER": "LOWER LEVEL", "MAIN": "MAIN LEVEL", "UPPER": "UPPER LEVEL"}


def load_plan(lv):
    H, V = [], []
    for r in csv.reader(open(os.path.join(PLAN, f"plan_{NAMES[lv]}.csv"))):
        if len(r) < 5:
            continue
        x0, y0, x1, y1 = map(float, r[1:5])
        if abs(y0 - y1) < 0.02 and abs(x1 - x0) > 6:
            H.append((y0, min(x0, x1), max(x0, x1), r[0]))
        elif abs(x0 - x1) < 0.02 and abs(y1 - y0) > 6:
            V.append((x0, min(y0, y1), max(y0, y1), r[0]))
    return H, V


def score(faces, H, V):
    rows = []
    for rec in faces:
        x0, y0, x1, y1, nx, ny = [float(v) for v in rec["f"][:6]]
        if abs(nx) > 0.5:
            c, a, b, L = x0 + DX, min(y0, y1) + DY, max(y0, y1) + DY, V
        else:
            c, a, b, L = y0 + DY, min(x0, x1) + DX, max(x0, x1) + DX, H
        best = None
        for q, p0, p1, lay in L:
            ov = min(b, p1) - max(a, p0)
            if ov < 0.5 * (b - a) * 0.5 and ov < 24:
                continue
            d = abs(q - c)
            if best is None or d < best[0]:
                best = (d, lay)
        rows.append((best[0] if best else 99, b - a))
    return rows


for lv in NAMES:
    H, V = load_plan(lv)
    for name, d in (("old (open drawing)", OLD), ("side database", NEW)):
        p = os.path.join(d, f"faces_{lv}.json")
        if not os.path.exists(p):
            continue
        rows = score(json.load(open(p)), H, V)
        L = sum(r[1] for r in rows) or 1
        on = sum(r[1] for r in rows if r[0] < 1 / 32) / L * 100
        near = sum(r[1] for r in rows if r[0] < 0.25) / L * 100
        worst = sorted(r[0] for r in rows)[-3:]
        print(f"{lv:6} {name:20} faces {len(rows):3}  on a drawn line within 1/32in: {on:5.1f}% of length, within 1/4in: {near:5.1f}%   worst {[round(w, 2) for w in worst]}")
