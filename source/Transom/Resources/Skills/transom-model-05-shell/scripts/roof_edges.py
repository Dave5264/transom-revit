"""Sort every footprint-roof edge: does it end in the open (gets a fascia board, pulled back by the board's thickness) or run into a
wall or another roof (left alone)? usage: python roof_edges.py roofs.json wall_faces.json > roof_edges.json
An edge runs into a wall when a parallel wall face within 14" inside to 3" outside rises above the roof there. An edge shared with another
roof for part of its length prints that roof: the rest of it is still open (a jog end) and needs its own split. Read the list before using it."""
import json, math, sys
from shapely.geometry import Polygon, Point
roofs = json.load(open(sys.argv[1])); faces = json.load(open(sys.argv[2]))
def top_at(r, x, y):
    z = []
    for e in r["edges"]:
        if not e["slope"]: continue
        (ax, ay), (bx, by) = e["a"], e["b"]
        dx, dy = bx - ax, by - ay; L = math.hypot(dx, dy); nx, ny = -dy / L, dx / L      # left normal
        cx, cy = sum(p["a"][0] for p in r["edges"]) / len(r["edges"]), sum(p["a"][1] for p in r["edges"]) / len(r["edges"])
        if (cx - ax) * nx + (cy - ay) * ny < 0: nx, ny = -nx, -ny                    # inward
        d = (x - ax) * nx + (y - ay) * ny
        t = math.atan(e["angle"])
        z.append(r["lvz"] + r["base"] + e["offset"] + r["thick"] / math.cos(t) + d * e["angle"])
    return min(z) if z else r["lvz"] + r["base"] + r["thick"]
out = []
for r in roofs:
    poly = Polygon([e["a"] for e in r["edges"]])
    for i, e in enumerate(r["edges"]):
        (ax, ay), (bx, by) = e["a"], e["b"]
        L = math.hypot(bx - ax, by - ay); ux, uy = (bx - ax) / L, (by - ay) / L
        mx, my = (ax + bx) / 2, (ay + by) / 2
        nx, ny = uy, -ux
        if poly.contains(Point(mx + nx * 0.5, my + ny * 0.5)): nx, ny = -nx, -ny       # outward
        axis = "X" if abs(ux) < 0.01 else ("Y" if abs(uy) < 0.01 else "?")
        ztop = top_at(r, mx - nx * 1, my - ny * 1)
        why = []
        for f in faces:                                                               # a wall face parallel to the edge, near it, rising above the roof
            if axis == "X" and f["axis"] != "X" or axis == "Y" and f["axis"] != "Y" or axis == "?": continue
            c = mx if axis == "X" else my
            sd = (f["plane"] - c) * (nx if axis == "X" else ny)                          # + = outside the edge
            if not (-14 <= sd <= 3): continue
            lo, hi = (min(ay, by), max(ay, by)) if axis == "X" else (min(ax, bx), max(ax, bx))
            outl = Polygon(max((l for l in f["loops"]), key=lambda l: Polygon(l).area))
            u0, z0, u1, z1 = outl.bounds
            ov = min(hi, u1) - max(lo, u0)
            if ov < 0.3 * (hi - lo): continue
            um = (max(lo, u0) + min(hi, u1)) / 2
            px, py = (mx, um) if axis == "X" else (um, my)                                # the roof top at the same point along the edge as the wall test
            zt = top_at(r, px - nx * 1, py - ny * 1)
            if outl.contains(Point(um, zt + 3)): why.append(f"wall {f['id']} ({f['tag'][6:]}) {sd:+.1f}in")
        for q in roofs:                                                               # a shared edge with another roof
            if q is r: continue
            for g in q["edges"]:
                (cx, cy), (dx, dy) = g["a"], g["b"]
                if axis == "X" and abs(cx - dx) < 0.01 and abs(cx - ax) < 1.5 and min(max(cy, dy), max(ay, by)) - max(min(cy, dy), min(ay, by)) > 6: why.append(f"roof {q['id']}")
                if axis == "Y" and abs(cy - dy) < 0.01 and abs(cy - ay) < 1.5 and min(max(cx, dx), max(ax, bx)) - max(min(cx, dx), min(ax, bx)) > 6: why.append(f"roof {q['id']}")
        out.append(dict(roof=r["id"], idx=i, kind="eave" if e["slope"] else "rake/edge", a=e["a"], b=e["b"], len=round(L, 1), out=[round(nx), round(ny)], ztop=round(ztop, 1), exposed=not why, why=why, comment=r["comment"][:40]))
for o in out:
    print(f"{o['roof']} #{o['idx']} {o['kind']:9s} {str(o['a']):22s}->{str(o['b']):22s} L {o['len']:6.1f} out {o['out']} top {o['ztop']:6.1f}  {'EXPOSED' if o['exposed'] else 'into ' + '; '.join(o['why'][:2])}   [{o['comment']}]", file=sys.stderr)
json.dump(out, sys.stdout, indent=0)
