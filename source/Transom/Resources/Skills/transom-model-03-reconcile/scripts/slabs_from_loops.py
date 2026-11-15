"""Slab outlines from the reconciled loops, so no slab face renders from outside.
usage: python slabs_from_loops.py loops.json slabs.json
A framed floor stops INSET in from the exterior face, and inside the wall below wherever that is further in, except at a bay whose walls come down to cover it.
Stair flights are cut from the outer boundary. Set the frame, the slabs and the bays and cuts below (model inches)."""
import json, sys
from shapely.geometry import LineString, Polygon, box
from shapely.ops import polygonize, unary_union

DX, DY = 37.0, -178.58   # the Phase 3 transform, CAD to model inches
INSET = 3.25    # about mid-wall. Closer and Revit's hidden-line views draw the slab edge through a wall face seen at an angle (1 1/4" still showed at 1/8" scale)
CLEAN = 0.2       # steps under about 3/8" collapse, or Revit warns "slightly off axis"
SLABS = {         # level: (level below, bays kept out to their walls, cuts)
    "MAIN": ("LOWER", [box(7.6, -148.5, 42, 122.5), box(972.0, 320.0, 1030.0, 425.0)], []),   # a bay box must overlap the inset slab by more than the inset, or the bay drops off
    "UPPER": ("MAIN", [], [box(975.0, -262.0, 1100.0, -100.0), box(325.0, 175.0, 438.0, 340.0)]),
}
loops = json.load(open(sys.argv[1]))


def poly(level):
    segs = []
    for f in loops[level]:
        if f["axis"] == "X":
            x = f["c"] + DX
            segs.append(LineString([(x, f["a"] + DY), (x, f["b"] + DY)]))
        else:
            y = f["c"] + DY
            segs.append(LineString([(f["a"] + DX, y), (f["b"] + DX, y)]))
    ps = list(polygonize(unary_union(segs)))
    return max(ps, key=lambda p: p.area)


def tidy(g):
    g = g.buffer(-CLEAN, join_style=2).buffer(CLEAN, join_style=2)     # remove thin fins
    g = g.buffer(CLEAN, join_style=2).buffer(-CLEAN, join_style=2)     # remove thin notches
    if g.geom_type != "Polygon":
        g = max(g.geoms, key=lambda p: p.area)
    return g.simplify(0.01)


out = {}
for lv, (below, bays, cuts) in SLABS.items():
    a = poly(lv).buffer(-INSET, join_style=2)
    b = poly(below).buffer(-INSET, join_style=2)
    g = a.intersection(b)
    for bay in bays:
        g = g.union(bay.intersection(a))
    for c in cuts:
        g = g.difference(c)
    if g.geom_type != "Polygon":
        raise SystemExit(f"{lv}: a bay or a cut left {len(g.geoms)} separate pieces; widen the bay box or move the cut")
    g = tidy(g)
    pts = [(round(x, 3), round(y, 3)) for x, y in g.exterior.coords[:-1]]
    bad = [(p, q) for p, q in zip(pts, pts[1:] + pts[:1]) if abs(p[0] - q[0]) > 0.01 and abs(p[1] - q[1]) > 0.01]
    print(f"{lv}: {len(pts)} vertices, {g.area / 144:.0f} sf, holes {len(g.interiors)}, off-axis edges {len(bad)}, shortest edge {min(max(abs(p[0]-q[0]),abs(p[1]-q[1])) for p,q in zip(pts,pts[1:]+pts[:1])):.2f} in")
    out[lv] = pts
json.dump(out, open(sys.argv[2], "w"))
