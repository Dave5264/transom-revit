"""Facade slots: narrow gaps between the exterior faces of walls on one plane.
usage: python slots.py wall_faces.json [--width 12]
The envelope check cannot see these. Its tolerance has to be larger than the wall spacing noise, so a 3" gap between two walls passes.
Each plane's faces are unioned with their openings filled, closed by the width, and what the closing added is a slot.
Check each one: a slot where a perpendicular wall butts into the plane is filled by that wall's end and is hidden (3 of 9 on the first
project). A slot that only shows above a lower wall's top is real. Gaps between walls on different planes (a fin, a missing return) need the look."""
import json, sys
from shapely.geometry import Polygon, box
from shapely.ops import unary_union

WIDTH = float(sys.argv[sys.argv.index("--width") + 1]) if "--width" in sys.argv else 8.0
faces = json.load(open(sys.argv[1]))
planes = {}
for f in faces:
    key = (f["axis"], f["s"], round(f["plane"] * 2) / 2)
    planes.setdefault(key, []).append(f)
count = 0
for (axis, s, c), group in sorted(planes.items()):
    polys = []
    for f in group:
        loops = sorted((Polygon(l) for l in f["loops"]), key=lambda p: -p.area)
        polys.append(loops[0].buffer(0))        # outer loop only: an opening is not a slot
    u = unary_union(polys)
    closed = u.buffer(WIDTH / 2, join_style=2).buffer(-WIDTH / 2, join_style=2)
    gaps = closed.difference(u)
    gaps = [g for g in (gaps.geoms if hasattr(gaps, "geoms") else [gaps]) if g.area > 2]
    for g in gaps:
        a, z0, b, z1 = g.bounds
        if z1 - z0 < 3 or b - a < 0.05:
            continue
        nearby = [f["id"] for f, p in zip(group, polys) if p.distance(g) < 0.1]
        print(f"{axis}={c:<8} facing {'+' if s > 0 else '-'}  u {a:9.2f}..{b:9.2f} ({b - a:5.2f} wide)  z {z0:7.1f}..{z1:7.1f} ({z1 - z0:6.1f} tall)  walls {nearby}")
        count += 1
print("slots:", count)
