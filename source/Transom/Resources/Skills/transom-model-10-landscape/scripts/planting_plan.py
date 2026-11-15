"""Landscape design for the the first project site with the Enscape placeholder assets (copied from the another of the user's models model and
already in the project). A new design for this building: shade trees in the lawns, a street-tree row on the south edge, an evergreen
screen behind the west fence, honey locusts along the east path and the north edge, foundation beds along the walls (evergreen
anchors at the corners, low shrubs under windows, a perennial edge where the bed is deep), entry accents, a seating group on the patio,
benches on the east path. Writes planting.json (placements) and planting_plan.png."""
import json, math, random
import numpy as np
from shapely.geometry import Point, Polygon, LineString, box
from shapely.ops import unary_union, nearest_points
exec(compile(open("planting_zones.py", encoding="utf-8").read().split("# plan\n")[0], "planting_zones", "exec"))
random.seed(7)
SZ = {}                                                      # name -> (id, width in, height in)
for line in open("asset_sizes.txt", encoding="utf-8"):
    i, n, w, d, h, _ = line.strip().split("|"); SZ[n] = (int(i), max(float(w), float(d)) * 12, float(h) * 12)
SZ["Park Bench"] = (3612281, 60.0, 30.0)
P = []                                                       # placements
def add(name, x, y, rot=None, z="ground", note=""):
    P.append(dict(name=name, id=SZ[name][0], x=round(x, 2), y=round(y, 2), rot=round(random.uniform(0, 360) if rot is None else rot, 1), z=z, note=note))

# ---- trees: (name, x, y, min clearance to the building) placed by hand, checked below
LP, M46, M54, RM, SG, HL, ZK, ZK2, RW, OAK, PB, JM, FIR = ("Deciduous Tree 134 London Plane", "Deciduous Tree 046 Maple", "Deciduous Tree 054 Maple",
    "Deciduous Tree 066 Red Maple", "Deciduous Tree 088 Sweetgum", "Deciduous Tree 259 Honey Locust (spring)", "Deciduous Tree 289 Japanese Zelkova (summer)",
    "Deciduous Tree 291 Japanese Zelkova (summer)", "Deciduous Tree 076 Rowan", "Deciduous Tree 056 Oak", "Deciduous Tree 227 Paper Birch",
    "Deciduous Tree 036 Japanese Maple Beni Maiko", "Coniferous Tree 001 Fir")
PB2 = "Deciduous Tree 301 Paper Birch"
trees = [
    # island, west lawn and the arcs
    (PB2, -45, 60, "island west strip"), (PB2, -45, 300, "island west strip"), (ZK, -40, 585, "north-west arc lawn"), (SG, -140, -420, "south-west arc lawn"),
    (JM, 80, 480, "beside the patio"), (RW, 440, -175, "south recess"),
    # island, east: honey locusts along the east path, a zelkova south-west of it, a birch with the boulder in the north-east arc
    (HL, 1505, 420, "east path"), (HL, 1512, 60, "east path"), (HL, 1520, -290, "east path"), (ZK2, 1305, -450, "south-east island lawn"), (PB, 1450, 615, "north-east arc, with the boulder"),
    # outer: street trees along the south edge
    (LP, 160, -1190, "south edge"), (LP, 520, -1190, "south edge"), (LP, 880, -1190, "south edge"), (LP, 1240, -1190, "south edge"),
    # south-west corner lawn
    (LP, -700, -900, "south-west lawn"), (SG, -430, -1110, "south-west lawn"), (M54, -920, -560, "south-west lawn"), (OAK, -980, -1120, "south-west lawn"), (RM, -200, -1220, "south-west lawn"),
    (FIR, -1030, -820, "south-west evergreens"), (FIR, -995, -735, "south-west evergreens"), (FIR, -1050, -660, "south-west evergreens"),
    # behind the west fence: an evergreen screen with two oaks behind
    *[(FIR, -930, y, "west screen") for y in range(-300, 701, 100)], (OAK, -1035, 150, "west screen"), (OAK, -1035, 520, "west screen"),
    # north-west corner behind the enclosure, the north edge
    (LP, -930, 930, "north-west corner"), (FIR, -775, 790, "enclosure screen"), (FIR, -775, 885, "enclosure screen"),
    *[(HL, x, 1048, "north edge") for x in (-470, -110, 250, 610, 970)],
    # south-east outer lawn
    (RM, 1390, -960, "south-east lawn"), (HL, 1660, -1235, "south-east lawn"),
]
TREE_CLEAR = {LP: 180, SG: 160, M46: 130, M54: 130, OAK: 96, RM: 84, ZK: 120, ZK2: 72, RW: 84, HL: 84, PB: 60, PB2: 48, JM: 36, FIR: 48}
hardscape = unary_union([pave_poly, conc, stoops, wells])
bad = []
for name, x, y, note in trees:
    p = Point(x, y); r = SZ[name][1] / 2
    ok = lawn.contains(p) and hardscape.distance(p) >= min(36, r * 0.4) and foot.distance(p) >= TREE_CLEAR[name] and boulders.distance(p) >= 24
    clash = [q for q in P if q["name"] in TREE_CLEAR and math.dist((q["x"], q["y"]), (x, y)) < 0.55 * (SZ[q["name"]][1] / 2 + r)]
    def good(x, y):
        p = Point(x, y)
        return (lawn.contains(p) and hardscape.distance(p) >= min(36, r * 0.4) and foot.distance(p) >= TREE_CLEAR[name] and boulders.distance(p) >= 24
                and not [q for q in P if q["name"] in TREE_CLEAR and math.dist((q["x"], q["y"]), (x, y)) < 0.55 * (SZ[q["name"]][1] / 2 + r)])
    if good(x, y): add(name, x, y, note=note); continue
    found = None
    for rr in range(12, 85, 12):                                   # nudge: nearest clear spot within 7 ft
        for k in range(16):
            a = 2 * math.pi * k / 16; xx, yy = x + rr * math.cos(a), y + rr * math.sin(a)
            if good(xx, yy): found = (xx, yy); break
        if found: break
    if found: add(name, found[0], found[1], note=note + " (nudged %d in)" % round(math.dist(found, (x, y))))
    else: bad.append((name[:28], x, y, note, "lawn" if not lawn.contains(p) else "clear", round(hardscape.distance(p)), round(foot.distance(p)), len(clash)))
print("trees placed %d, rejected %d" % (sum(1 for q in P if q["name"] in TREE_CLEAR), len(bad)))
for b in bad: print("   rejected", b)

# ---- foundation beds: a row along the wall, low shrubs under windows, evergreen anchors at corners, a perennial edge where deep
YEW, LAUR, BOX, RHO, FRG, ROS, LAV, CRN, IND, HYD = ("Bush 061 Yeg", "Bush 009 English Laurel", "Bush 010 English Laurel", "Flower 068 Rhododendron",
    "Bush 019 Feather Reed Grass", "Bush 128 Rose of Sharon", "Bush 117 Lavender", "Perennial 024 Bloody Cranesbill (spring)",
    "Perennial 052 False Blue Indigo (spring)", "Flower 040 Hydrangea")
wins = [o for o in json.load(open("openings_out.json"))["windows"]]
corners = [Point(c) for c in foot.exterior.coords[:-1]]
def under_window(p):
    for o in wins:
        q = np.array([o["x"], o["y"]]); n = np.array(o["out"]); t = np.array([-n[1], n[0]]); d = np.array([p.x, p.y]) - q
        if abs(d.dot(t)) <= o["wdt"] / 2 + 8 and 0 < d.dot(n) < 90: return True
    return False
seq_mid = [LAUR, LAUR, FRG]                                  # rhythm between windows
shrub_n = 0
for bed in getattr(beds, "geoms", [beds]):
    depth_probe = lambda pt, off: bed.contains(pt)
    row = foot.buffer(30, join_style=2).exterior.intersection(bed.buffer(-6))
    for seg in getattr(row, "geoms", [row]):
        if seg.is_empty or seg.length < 24: continue
        s, k = 12.0, 0
        while s < seg.length - 12:
            p = seg.interpolate(s)
            near_corner = min(c.distance(p) for c in corners) < 48
            narrow = not bed.contains(Point(*nearest_points(foot.buffer(52, join_style=2).exterior, p)[0].coords[0]))
            if near_corner and not narrow: name = YEW
            elif under_window(p): name = RHO if not narrow else BOX
            else: name = seq_mid[k % 3] if not narrow else BOX; k += 1
            w = SZ[name][1]
            add(name, p.x, p.y, note="foundation bed"); shrub_n += 1
            s += max(w * 0.85, 24)
    # perennial edge 48" off the wall where the bed is that deep
    edge = foot.buffer(48, join_style=2).exterior.intersection(bed.buffer(-4))
    for seg in getattr(edge, "geoms", [edge]):
        if seg.is_empty or seg.length < 24: continue
        s, k = 10.0, 0
        while s < seg.length - 10:
            p = seg.interpolate(s); name = LAV if k % 2 == 0 else CRN
            add(name, p.x, p.y, note="bed edge"); shrub_n += 1; k += 1; s += 26
# narrow strips (north face between the wells, short returns): one small row at mid-depth where the 30" row found no room
for bed in getattr(beds, "geoms", [beds]):
    row = foot.buffer(17, join_style=2).exterior.intersection(bed.buffer(-2))
    for seg in getattr(row, "geoms", [row]):
        if seg.is_empty or seg.length < 20: continue
        s, k = 8.0, 0
        while s < seg.length - 8:
            p = seg.interpolate(s)
            if all(math.dist((q["x"], q["y"]), (p.x, p.y)) > 30 for q in P):
                name = (BOX, LAV, BOX, HYD)[k % 4]; add(name, p.x, p.y, note="narrow bed"); shrub_n += 1; k += 1
            s += 26
print("foundation plants %d" % shrub_n)

# ---- entry accents: feather reed grass and rose of sharon flanking each stoop where it meets its walk
acc = 0
for st, _ in S.stoops.values():
    x0, y0, x1, y1 = st.bounds
    for (x, y) in [(x0 - 26, y0 + 10), (x1 + 26, y0 + 10), (x0 - 26, y1 - 10), (x1 + 26, y1 - 10)]:
        p = Point(x, y)
        if lawn.contains(p) and hardscape.distance(p) >= 18 and foot.distance(p) >= 20 and all(math.dist((q["x"], q["y"]), (x, y)) > 30 for q in P):
            add(FRG if acc % 2 == 0 else ROS, x, y, note="entry accent"); acc += 1
for name, x, y in [(FRG, 1110, 435), (ROS, 1272, -265), (FRG, 1275, -205), (FRG, 425, 360), (ROS, 1070, 438)]:
    p = Point(x, y)
    if lawn.contains(p) and hardscape.distance(p) >= 14 and foot.distance(p) >= 18 and all(math.dist((q["x"], q["y"]), (x, y)) > 28 for q in P):
        add(name, x, y, note="entry accent"); acc += 1
print("entry accents %d" % acc)

# ---- patio seating (z on the patio floor); chairs and sofa are turned in Revit so their backs face away from the table
pat = Polygon(F["concrete"][6]["loops"][0]) if F["concrete"][6]["kind"] == "patio" else None
cx, cy = pat.centroid.x, pat.centroid.y
add("Table 051 Outdoor", cx, cy, rot=0, z="patio", note="patio")
add("Sofa 052 Outdoor", cx, cy + 62, rot=0, z="patio", note="patio, back away from table")
add("Armchair 046 Outdoor", cx - 60, cy - 6, rot=0, z="patio", note="patio, back away from table")
add("Armchair 046 Outdoor", cx + 60, cy - 6, rot=0, z="patio", note="patio, back away from table")
# lavender along the patio's open (north and east) edges
for x in np.arange(pat.bounds[0] + 18, pat.bounds[2] - 10, 28):
    p = Point(x, pat.bounds[3] + 18)
    if lawn.contains(p) and hardscape.distance(p) >= 8: add(LAV, x, pat.bounds[3] + 18, note="patio edge")
print("patio centre (%.0f, %.0f)" % (cx, cy))

# ---- benches on the west side of the east path, facing it
path = Polygon(F["concrete"][2]["loops"][0])
for y in (150, 420):
    line = LineString([(1150, y), (1600, y)]).intersection(path)
    if line.is_empty: continue
    xw = line.bounds[0]; p = Point(xw - 30, y)
    if lawn.contains(p) and foot.distance(p) > 24: add("Park Bench", xw - 30, y, rot=0, z="ground", note="bench, back away from path (+x faces path)")

# ---- boulders: boulder 3 moves off the curb into the lawn west of the path end; all three sit 6" into the grade (done in Revit)
json.dump(dict(placements=P, boulder_moves={"7771053": [1318, -505]}), open("planting.json", "w"))
from collections import Counter
print("total placements %d" % len(P)); print(Counter(q["name"].split(" ", 2)[-1] if False else q["name"] for q in P).most_common())
# plan
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Polygon as MP, Circle
fig, ax = plt.subplots(figsize=(26, 22), dpi=80)
def draw(geom, fc, ec="none", a=1.0, lw=0.5):
    for g in getattr(geom, "geoms", [geom]):
        if g.is_empty or g.geom_type != "Polygon": continue
        ax.add_patch(MP(list(g.exterior.coords), closed=True, fc=fc, ec=ec, alpha=a, lw=lw))
draw(S.EXT, "#cfe8c0"); draw(pave_poly, "#777"); draw(conc, "#ddd", "#999"); draw(foot, "#c96"); draw(wells, "#333"); draw(stoops, "#aaa", "#000"); draw(beds, "#8a5a2b", a=0.45)
draw(encl, "#a52"); draw(fence, "#8b4")
COL = {LP: "#1f6b2a", SG: "#7a3b1a", M46: "#2e8b3a", M54: "#3a9a45", RM: "#b03020", HL: "#9bc53d", ZK: "#2f7f4f", ZK2: "#2f7f4f", RW: "#c0602a", OAK: "#4f6b2a",
       PB: "#e8e8d0", JM: "#a0203a", FIR: "#0f4f2f"}
for q in P:
    r = SZ[q["name"]][1] / 2; c = COL.get(q["name"], "#6a4" if "Bush" in q["name"] or "Flower" in q["name"] or "Perennial" in q["name"] else "#333")
    ax.add_patch(Circle((q["x"], q["y"]), r, fc=c, ec="#000", lw=0.4, alpha=0.55 if r > 60 else 0.85))
ax.set_xlim(-1110, 1730); ax.set_ylim(-1310, 1130); ax.set_aspect("equal")
fig.tight_layout(); fig.savefig("planting_plan.png")
