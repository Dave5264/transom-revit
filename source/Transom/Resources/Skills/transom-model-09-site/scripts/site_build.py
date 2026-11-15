"""Site layout from the MAIN plan linework -> site_out.json for site_place.cs, and site_check.png.
Pavement = the LW-5 outer outline minus the LW-5 island round the building. Asphalt sits 6" below the adjacent grass inside a 6" concrete curb.
Walks and pads are flush with the grass. All heights come from grade.G (inches, model frame)."""
import json, math
import numpy as np
from shapely.geometry import Polygon, LineString, Point, box, MultiPolygon
from shapely.ops import unary_union
import grade
G = grade.G
D = json.load(open("site_poly_model.json"))
def poly(layer, pred):
    return [c["pts"] for c in D if c["layer"] == layer and pred(c["pts"])]
def bb(P): xs = [p[0] for p in P]; ys = [p[1] for p in P]; return min(xs), min(ys), max(xs), max(ys)
def pick(layer, x0, y0, x1, y1, n=None):
    """the LW polyline whose first point is near (x0,y0) and last near (x1,y1), in either direction"""
    for c in D:
        if c["layer"] != layer: continue
        P = [tuple(p[:2]) for p in c["pts"]]
        if n and len(P) != n: continue
        if math.dist(P[0], (x0, y0)) < 3 and math.dist(P[-1], (x1, y1)) < 3: return P
        if math.dist(P[-1], (x0, y0)) < 3 and math.dist(P[0], (x1, y1)) < 3: return P[::-1]
    raise KeyError((layer, x0, y0, x1, y1))
EXT = box(-1100, -1300, 1720, 1120)                     # the toposolid's extents
CAPX, CAPY = 1720, 1120
# outer edge of pavement, walked from the west lot's NE corner
O = []
O += pick("LW-5", -590, 729, -590, -359)                 # west lot: north edge, west edge, south edge
O += pick("LW-5", -590, -359, -62, -862)[1:]             # south-west sweep
O += pick("LW-5", -62, -862, 1259, -857)[1:]             # south row 2, outer edge
O += pick("LW-5", 1259, -857, 1431, -781)[1:]
O += pick("LW-5", 1431, -781, 1680, -1011)[1:]
O += [(CAPX, -1011), (CAPX, CAPY)]                       # east drive: its outer edge is off the drawing; capped at the toposolid edge
xl = 1443 + (1464 - 1443) * (CAPY - 1087) / (2215 - 1087)
O += [(xl, CAPY)]
O += pick("LW-5", 1443, 1087, 1279, 969)
O += pick("LW-5", 1279, 969, -590, 979)[1:]
outer = Polygon(O).buffer(0)
I = []
I += pick("LW-5", 1562, -245, 1350, -568)
I += pick("LW-5", 1350, -568, 1259, -568)[1:]
I += pick("LW-5", 1259, -568, -62, -574)[1:]
I += pick("LW-5", -62, -574, -301, -358)[1:]
I += pick("LW-5", -301, -358, -302, 405)[1:]
I += pick("LW-5", -302, 405, -62, 693)[1:]
I += pick("LW-5", -62, 693, 1398, 682)[1:]
I += pick("LW-5", 1398, 682, 1526, 579)[1:]
island = Polygon(I).buffer(0)
pave = outer.difference(island).intersection(EXT)
print("pavement area %.0f sf, island %.0f sf" % (pave.area / 144, island.area / 144))
# building footprint: MAIN slab outline + 3.25 (the slab sits 3 1/4" inside the wall faces)
slab = [(1191.88,-126.99),(1082.50,-126.99),(1082.50,-247.33),(549.00,-247.33),(549.00,-55.14),(326.50,-55.14),(326.50,-175.96),(40.25,-175.96),(40.25,393.83),
        (327.00,393.83),(327.00,322.17),(435.00,322.17),(435.00,274.17),(549.25,274.17),(549.25,418.45),(1023.88,418.45),(1023.88,322.80),(1191.88,322.80)]
foot = Polygon(slab).buffer(3.25, join_style=2)
foot = foot.union(box(977.75, -256.08, 1092.0, -247.33))    # the SE stair bay steps out 5 1/2" past the stone below it
# egress wells: outer faces of their 2" walls (the walls themselves are in the model)
wells = [box(848, 418, 924, 459), box(580, 418, 656, 459), box(1088, 326, 1164, 365), box(874, -289, 950, -247), box(594, -289, 670, -247)]
# U-shaped enclosure (11" CMU), open to the east
U = box(-728.1, 729.0, -590.4, 978.8)
# walks, pads and stoops
def ring(P): return Polygon(P).buffer(0)
path = ring([tuple(p[:2]) for p in [c for c in D if c["layer"] == "LW-4" and len(c["pts"]) == 16][0]["pts"]])
walks = {
  "S walk": box(71, -353, 1258, -295),
  "SE entry walk": box(1090, -295, 1255, -237),
  "N entry walk": box(444.5, 336.6, 546, 463),
  "E entry walk": box(1027.1, 408, 1087, 463),
  "N patio": box(124, 397, 331, 637),
  "E path": path,
  "existing pad": ring([tuple(p[:2]) for p in [c for c in D if c["layer"] == "SL-EX-CONC" and len(c["pts"]) == 5][0]["pts"]]),
}
stoops = {"SE porch": (box(1092, -226, 1186, -130.24), 0.0), "SE step": (box(1092, -237, 1186, -226), -6.75),
          "N porch": (box(444.5, 277.4, 546, 336.6), 0.0), "E stoop": (box(1027.1, 335, 1063, 408), 0.0)}
# curb: 6" on the grass side of every pavement edge, except the artificial cap at the toposolid edge and along the enclosure
cap = box(CAPX - 12, -2000, CAPX + 12, 3000).union(box(-3000, CAPY - 12, 3000, CAPY + 12))
curb = pave.buffer(6.25, join_style=2).difference(pave.buffer(-0.2, join_style=2)).difference(cap).difference(U.buffer(1)).difference(foot).intersection(EXT)
for w in wells: curb = curb.difference(w.buffer(0.5))
def clean(g):
    for w in wells: g = g.difference(w.buffer(0.5))
    for s, _ in stoops.values(): g = g.difference(s)
    return g.difference(foot).intersection(EXT)
for s, _ in stoops.values(): curb = curb.difference(s)
curb = clean(curb)
ring6 = pave.buffer(6.25, join_style=2)
walk_parts = {k: clean(w.difference(ring6)) for k, w in walks.items()}      # walks stop at the back of the curb
WALK_KEYS = ["S walk", "SE entry walk", "N entry walk", "E entry walk", "E path"]
walk_all = unary_union([walk_parts[k] for k in WALK_KEYS])
conc = unary_union([curb, walk_all, walk_parts["N patio"], walk_parts["existing pad"]])
asph = pave.buffer(-0.2, join_style=2)
# stripes and stops
deck = [c["pts"] for c in D if c["layer"] == "A-Flor-Deck"]
stripes, blue, stops = [], [], []
ada = box(430, 455, 510, 690)
for P in deck:
    P = [tuple(p[:2]) for p in P]
    L = LineString(P)
    closed = math.dist(P[0], P[-1]) < 0.5 and len(P) == 5
    if closed and abs(bb(P)[2] - bb(P)[0] - 82) < 2:      # drafted wheel stop, N row
        x0, y0, x1, y1 = bb(P); stops.append(((x0 + x1) / 2, (y0 + y1) / 2, 0.0, "N row (drafted)")); continue
    if ada.contains(L.centroid) or ada.intersects(L) and L.length < 260:
        for a, b in zip(P, P[1:]): blue.append(LineString([a, b]))
        continue
    if L.length > 100 and len(P) == 2: stripes.append(L)
def strip_poly(L, w=4.0):
    s = L.intersection(asph.buffer(-1.0))                  # stop 1" short of the curb
    return s.buffer(w / 2, cap_style=2) if not s.is_empty else None
SP = [p for p in (strip_poly(L) for L in stripes) if p is not None]
BP = [p for p in (strip_poly(L) for L in blue) if p is not None]
# stops for the rows the drafting leaves without them: one per stall, centred, 30" off the head of the stall
def row_stops(lines_at, lo, hi, head, axis, name, sign):
    cuts = sorted(lines_at); edges = [lo] + cuts + [hi]
    for a, b in zip(edges, edges[1:]):
        if b - a < 90 or b - a > 130: continue
        c = (a + b) / 2
        stops.append(((head + sign * 30, c, 90.0, name) if axis == "y" else (c, head + sign * 30, 0.0, name)))
W_lines = sorted({round(L.coords[0][1]) for L in stripes if L.bounds[0] < -700})
E_lines = sorted({round(L.coords[0][1]) for L in stripes if -400 < L.bounds[0] < -200})
S1 = sorted({round(L.coords[0][0]) for L in stripes if L.bounds[1] > -700 and L.bounds[3] < -300 and L.bounds[2] - L.bounds[0] < 5})
S2 = sorted({round(L.coords[0][0]) for L in stripes if L.bounds[3] < -800 and L.bounds[2] - L.bounds[0] < 5})
print("rows: W", W_lines, "\n E", E_lines, "\n S1", S1, "\n S2", S2)
row_stops(W_lines, -358, 728, -806, "y", "W lot west row", +1)
row_stops(E_lines, -358, 400, -86, "y", "W lot east row", -1)
row_stops(S1, 71, 1258, -353, "x", "S row 1", -1)
row_stops(S2, 71, 1259, -1073, "x", "S row 2", +1)
# toposolid points
pts = {}
def put(x, y, z): pts[(round(x, 2), round(y, 2))] = z
def classify(x, y):
    p = Point(x, y)
    if foot.buffer(-1.0).contains(p): return -127.0
    for w in wells:
        if w.buffer(-0.5).contains(p): return -82.0
    g = float(G(x, y))
    if pave.buffer(-0.15).contains(p): return g - 6.0
    return g
for x in np.arange(-1100, 1720.1, 36):
    for y in np.arange(-1300, 1120.1, 36): put(x, y, classify(x, y))
for x in np.arange(-1100, 1720.1, 18):                   # finer inside the pavement, so the paint and stops sit on the TIN
    for y in np.arange(-1300, 1120.1, 18):
        if pave.buffer(-2).contains(Point(x, y)): put(x, y, float(G(x, y)) - 6.0)
def edge_pairs(geom, step, din, dout, zin, zout):
    rings = []
    gs = geom.geoms if hasattr(geom, "geoms") else [geom]
    for g in gs:
        rings.append(g.exterior); rings += list(g.interiors)
    for r in rings:
        n = max(4, int(r.length / step))
        for i in range(n):
            q = r.interpolate(i * r.length / n)
            a = r.interpolate(((i + 0.01) % n) * r.length / n)
            tx, ty = a.x - q.x, a.y - q.y; L = math.hypot(tx, ty) or 1; nx, ny = -ty / L, tx / L
            pi = Point(q.x + nx * 0.5, q.y + ny * 0.5)
            s = 1 if geom.contains(pi) else -1                   # inward normal
            xi, yi = q.x + s * nx * din, q.y + s * ny * din; xo, yo = q.x - s * nx * dout, q.y - s * ny * dout
            if EXT.contains(Point(xi, yi)): put(xi, yi, zin(xi, yi))
            if EXT.contains(Point(xo, yo)): put(xo, yo, zout(xo, yo))
edge_pairs(pave, 12, 0.15, 0.15, lambda x, y: float(G(x, y)) - 6.0, lambda x, y: float(G(x, y)))
edge_pairs(foot, 18, 1.0, 0.02, lambda x, y: -127.0, lambda x, y: float(G(x, y)))
for w in wells: edge_pairs(w, 6, 0.5, 0.02, lambda x, y: -82.0, lambda x, y: float(G(x, y)))
print(len(pts), "toposolid points")
def loops(geom):
    gs = geom.geoms if hasattr(geom, "geoms") else [geom]
    out = []
    for g in gs:
        if g.area < 4: continue
        out.append([list(map(lambda c: [round(c[0], 3), round(c[1], 3)], g.exterior.coords[:-1]))] + [[[round(c[0], 3), round(c[1], 3)] for c in r.coords[:-1]] for r in g.interiors])
    return out
def slab(geom, ztop, t=0.1):
    """a thin paint slab draped on the asphalt: per-vertex top heights"""
    g = geom.simplify(0.05)
    if g.geom_type != "Polygon": return None
    P = list(g.exterior.coords[:-1])
    return [[round(x, 3), round(y, 3), round(float(G(x, y)) + ztop, 3)] for x, y in P]
ASPH_H, CONC_H = 0.25, 0.5
paint_top = -6.0 + ASPH_H + 0.45
out = dict(
  points=[[k[0], k[1], round(v, 3)] for k, v in pts.items()],
  asphalt=loops(asph), curbs=loops(curb), walks=loops(walk_all), patio=loops(walk_parts['N patio']), pad=loops(walk_parts['existing pad']), asph_h=ASPH_H, conc_h=CONC_H,
  stripes=[s for s in (slab(p, paint_top) for p in SP) if s], blue=[s for s in (slab(p, paint_top) for p in BP) if s], paint_t=0.1,
  stops=[[x, y, round(float(G(x, y)) - 6.0 + ASPH_H, 3), a, n] for x, y, a, n in stops],
  stoops=[[n, loops(g)[0][0], top, round(float(min(G(x, y) for x, y in g.exterior.coords)) - 3, 3)] for n, (g, top) in stoops.items()],
  wells=[[w.bounds, round(float(max(G(x, y) for x, y in w.exterior.coords)) + 4, 2)] for w in wells],
  enclosure=dict(base=round(float(min(G(x, y) for x, y in U.exterior.coords)) - 2, 2), height=138.0),
  fence=[[-830, y0, -830, min(y0 + 120, 729), round(float(min(G(-830, y0), G(-830, min(y0 + 120, 729)))) - 2, 2),
          round(float(G(-830, y0 + 60)) + 72, 2)] for y0 in [float(v) for v in np.arange(-359, 729, 120)]],
)
json.dump(out, open("site_out.json", "w"))
print("asphalt loops", len(out["asphalt"]), "curbs", len(out["curbs"]), "walks", len(out["walks"]), "patio", len(out["patio"]), "pad", len(out["pad"]), "stripes", len(out["stripes"]), "blue", len(out["blue"]), "stops", len(out["stops"]))
print("wells", out["wells"]); print("enclosure", out["enclosure"]); print("stoops", [(s[0], s[2], s[3]) for s in out["stoops"]])
# check plot
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Polygon as MP
fig, ax = plt.subplots(figsize=(18, 16), dpi=90)
def draw(geom, fc, ec="none", a=1.0):
    gs = geom.geoms if hasattr(geom, "geoms") else [geom]
    for g in gs:
        if g.is_empty or g.geom_type != "Polygon": continue
        ax.add_patch(MP(list(g.exterior.coords), closed=True, fc=fc, ec=ec, alpha=a, lw=0.5))
        for r in g.interiors: ax.add_patch(MP(list(r.coords), closed=True, fc="white", ec=ec, lw=0.5))
draw(EXT, "#cfe8c0"); draw(asph, "#555"); draw(conc, "#ddd", "#999"); draw(foot, "#c96");
for w in wells: draw(w, "#333")
for s, _ in stoops.values(): draw(s, "#bbb", "#000")
for p in SP: draw(p, "#fff")
for p in BP: draw(p, "#36f")
draw(U, "#a52")
for x, y, a, n in stops: ax.plot([x - (36 if a == 0 else 0), x + (36 if a == 0 else 0)], [y - (36 if a else 0), y + (36 if a else 0)], color="#f0a", lw=2)
ax.plot([-830, -830], [-359, 729], color="#8b4", lw=2)
ax.set_xlim(-1120, 1740); ax.set_ylim(-1320, 1140); ax.set_aspect("equal"); ax.grid(True, lw=0.2)
fig.tight_layout(); fig.savefig("site_check.png")

# ---- stripes as strips that follow the grade (12" pieces), 0.6" over the asphalt top, 1/4" thick
def strip_pairs(L, w=4.0, top=-6.0 + ASPH_H + 0.6):
    s = L.intersection(asph.buffer(-1.0))
    segs = [s] if s.geom_type == "LineString" else list(getattr(s, "geoms", []))
    out = []
    for g in segs:
        if g.is_empty or g.length < 2: continue
        n = max(1, int(math.ceil(g.length / 12)))
        P = [g.interpolate(i * g.length / n) for i in range(n + 1)]
        pairs = []
        for i, q in enumerate(P):
            a = P[max(0, i - 1)]; b = P[min(n, i + 1)]
            tx, ty = b.x - a.x, b.y - a.y; Lt = math.hypot(tx, ty); nx, ny = -ty / Lt * w / 2, tx / Lt * w / 2
            z = float(G(q.x, q.y)) + top
            pairs.append([round(q.x + nx, 3), round(q.y + ny, 3), round(q.x - nx, 3), round(q.y - ny, 3), round(z, 3)])
        out.append(pairs)
    return out
out["stripes2"] = [p for L in stripes for p in strip_pairs(L)]
out["blue2"] = [p for L in blue for p in strip_pairs(L)]
out["paint_t2"] = 0.25
json.dump(out, open("site_out.json", "w"))
print("strips", len(out["stripes2"]), "blue strips", len(out["blue2"]))
