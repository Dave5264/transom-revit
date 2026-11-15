"""Planting zones for the landscape design: lawn (plantable grass), foundation beds, and a plan with a labelled grid for placing trees.
Reads the site geometry from site_floors.py (head) and openings_site.json. Writes planting_zones.png and planting_zones.json."""
import json, math
import numpy as np
from shapely.geometry import Point, Polygon, LineString, box
from shapely.ops import unary_union
src = open("site_floors.py", encoding="utf-8").read()
exec(compile(src[:src.index("# ---- curb paths")], "site_floors_head", "exec"))
F = json.load(open("site_floors.json"))
conc = unary_union([Polygon(c["loops"][0], c["loops"][1:]) for c in F["concrete"]])
foot = S.foot
wells = unary_union([box(*w) for w in [(848, 418, 924, 459), (580, 418, 656, 459), (1088, 326, 1164, 365), (874, -289, 950, -247), (594, -289, 670, -247), (71, -217.2, 147, -174.9)]])
stoops = unary_union([s for s, _ in S.stoops.values()])
fence = box(-836, -365, -824, 735)
encl = S.U
boulders = unary_union([Point(1383, 647).buffer(20), Point(1487, -897).buffer(20), Point(1382, -561).buffer(20)])
hard = unary_union([pave_poly.buffer(8), conc, foot, wells.buffer(2), stoops, fence, encl.buffer(2)])
site = S.EXT.buffer(-12)
lawn = site.difference(hard)
op = json.load(open("openings_site.json"))
# outward normal: the side of the opening point that is outside the footprint
for o in op:
    p = np.array([o["x"], o["y"]]); n = np.array([o["nx"], o["ny"]])
    if foot.contains(Point(*(p + n * 24))): n = -n
    o["out"] = n.tolist()
doors = [o for o in op if o["cat"] == "door"]
# door clear zones: 48" deep, door width + 24" wide, in front of every exterior door
door_zones = []
for o in doors:
    p = np.array([o["x"], o["y"]]); n = np.array(o["out"]); t = np.array([-n[1], n[0]]); hw = o["wdt"] / 2 + 12
    q = [p + t * hw, p - t * hw, p - t * hw + n * 60, p + t * hw + n * 60]
    door_zones.append(Polygon([tuple(v) for v in q]))
door_zone = unary_union(door_zones)
bed_band = foot.buffer(54, join_style=2).difference(foot)
beds = bed_band.intersection(lawn).difference(door_zone.buffer(6)).difference(wells.buffer(18))
beds = unary_union([g for g in getattr(beds, "geoms", [beds]) if g.area > 900])
print("lawn %.0f sf, beds %.0f sf in %d pieces, doors %d" % (lawn.area / 144, beds.area / 144, len(getattr(beds, "geoms", [beds])), len(doors)))
json.dump(dict(doors=doors, windows=[o for o in op if o["cat"] == "window"]), open("openings_out.json", "w"))
# plan
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Polygon as MP
fig, ax = plt.subplots(figsize=(26, 22), dpi=80)
def draw(geom, fc, ec="none", a=1.0, lw=0.5):
    for g in getattr(geom, "geoms", [geom]):
        if g.is_empty or g.geom_type != "Polygon": continue
        ax.add_patch(MP(list(g.exterior.coords), closed=True, fc=fc, ec=ec, alpha=a, lw=lw))
        for r in g.interiors: ax.add_patch(MP(list(r.coords), closed=True, fc="white", ec=ec, lw=lw))
draw(S.EXT, "#f4f4f4"); draw(lawn, "#bfe3a8"); draw(pave_poly, "#777"); draw(conc, "#ddd", "#999"); draw(foot, "#c96"); draw(wells, "#333"); draw(stoops, "#aaa", "#000")
draw(beds, "#8a5a2b", a=0.6); draw(door_zone, "#f66", a=0.5); draw(encl, "#a52"); draw(fence, "#8b4"); draw(boulders, "#999", "#000")
for o in op:
    c = "#d00" if o["cat"] == "door" else "#00c"
    ax.plot([o["x"], o["x"] + o["out"][0] * 30], [o["y"], o["y"] + o["out"][1] * 30], color=c, lw=2)
for x in range(-1100, 1721, 100): ax.axvline(x, color="#000", lw=0.2); ax.text(x + 4, 1105, str(x), fontsize=8)
for y in range(-1300, 1121, 100): ax.axhline(y, color="#000", lw=0.2); ax.text(-1095, y + 4, str(y), fontsize=8)
ax.set_xlim(-1110, 1730); ax.set_ylim(-1310, 1130); ax.set_aspect("equal")
fig.tight_layout(); fig.savefig("planting_zones.png")
