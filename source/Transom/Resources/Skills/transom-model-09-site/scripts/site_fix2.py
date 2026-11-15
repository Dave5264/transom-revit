"""Second pass on the floors site (2026-11-07, late): walks meet the curb tops flush, the grass along the walks follows them closely,
and the building pit and egress-well pits are replaced by clean cuts (the foundation slab and a gravel floor in each well excavate the
ground; the ground itself stays at grade there). Run after site_floors.py with a fresh topo_after.json dump. Writes site_fix2.json."""
import json, math, os
import numpy as np
from shapely.geometry import Point, LineString, Polygon, box
from shapely.ops import unary_union, nearest_points
from scipy.spatial import cKDTree
src = open("site_floors.py", encoding="utf-8").read()
exec(compile(src[:src.index("# ---- ground:")], "site_floors_head", "exec"))
SCR = r"<path>"
V = json.load(open(SCR + "/topo_after.json"))

# ---- curb top at any point on the curb back (pavement edge + 6"): asphalt edge height there + 6"
edge_prims = [(R, i, p) for R in (outer, island) for i, p in enumerate(R)]
edge_lines = [LineString(pts_of(p, 3.0)) for R, i, p in edge_prims]
def curb_top(q):
    P = Point(q); k = min(range(len(edge_lines)), key=lambda j: edge_lines[j].distance(P))
    R, i, p = edge_prims[k]; e = nearest_points(edge_lines[k], P)[0]
    T, XY, Z = prof[id(R)][i]
    return float(np.interp(src_t(p, (e.x, e.y)), T, Z)) + 6.0
curb_back = pave_poly.buffer(CURB_OFF + 3.0, join_style=2).boundary

# ---- walks pinned to the curb top where they meet a curb
def runs_eval(zp, x, y):
    m0, u, v = np.array(zp["m0"]), np.array(zp["u"]), np.array(zp["v"])
    U = (np.asarray(x) - m0[0]) * u[0] + (np.asarray(y) - m0[1]) * u[1]; Vv = (np.asarray(x) - m0[0]) * v[0] + (np.asarray(y) - m0[1]) * v[1]
    return np.interp(U, zp["UB"], zp["ZB"]) + zp["c"] * (Vv - zp["vm"]), U
report = []
for c in concrete:
    zp = c["zf"]
    if zp["type"] != "runs": continue
    g = Polygon(c["loops"][0], c["loops"][1:])
    contact = g.boundary.intersection(curb_back.buffer(0.6))
    if contact.is_empty or contact.length < 6: continue
    segs = [contact] if contact.geom_type == "LineString" else [s for s in getattr(contact, "geoms", []) if s.geom_type == "LineString"]
    UB = np.array(zp["UB"]); umin, umax = UB[0], UB[-1]; Lu = max(umax - umin, 1.0)
    grid = np.arange(umin, umax + 6.0, 6.0); grid[-1] = umax
    m0_, u_, v_ = np.array(zp["m0"]), np.array(zp["u"]), np.array(zp["v"])
    pts_c = []
    for sg in segs:
        n = max(1, int(math.ceil(sg.length / 6.0)))
        pts_c += [(sg.interpolate(k * sg.length / n).x, sg.interpolate(k * sg.length / n).y) for k in range(n + 1)]
    Pc = np.array(pts_c); uc = (Pc[:, 0] - m0_[0]) * u_[0] + (Pc[:, 1] - m0_[1]) * u_[1]; vc = (Pc[:, 0] - m0_[0]) * v_[0] + (Pc[:, 1] - m0_[1]) * v_[1]
    tc = np.array([curb_top(q) for q in pts_c])
    if contact.length > 0.5 * Lu:                                         # the walk runs along the curb: its profile follows the curb top
        z0 = np.array([float(runs_eval(zp, q[0], q[1])[0]) for q in pts_c]); ms = tc - z0
        o = np.argsort(uc); delta = np.interp(grid, uc[o], ms[o]); kind = "along the curb"
    else:                                                                 # ends at the curb: fit their heights and the cross slope, ramp between
        sides = [s for s in (umin, umax) if np.any(np.abs(uc - s) < 0.25 * Lu)]
        cols = [(np.abs(uc - s) < 0.25 * Lu).astype(float) for s in sides] + [vc - zp["vm"]]
        Pold = np.interp(uc, UB, zp["ZB"])
        sol = np.linalg.lstsq(np.c_[tuple(cols)], tc - Pold, rcond=None)[0]
        zp["c"] = float(np.clip(sol[-1], -0.05, 0.05))
        ends = []
        for j, s in enumerate(sides):
            sel = np.abs(uc - s) < 0.25 * Lu
            ends.append((s, float(np.mean(tc[sel] - Pold[sel] - zp["c"] * (vc[sel] - zp["vm"])))))
        if len(ends) == 2: delta = np.interp(grid, [ends[0][0], ends[1][0]], [ends[0][1], ends[1][1]])
        else: s0, m = ends[0]; delta = m * np.clip(1 - np.abs(grid - s0) / Lu, 0, 1)
        kind = "%d end(s) at the curb, cross slope %.1f%%" % (len(ends), 100 * zp["c"])
    ms = tc - np.array([float(runs_eval(zp, q[0], q[1])[0]) for q in pts_c])
    Pn = np.interp(grid, UB, zp["ZB"]) + delta
    K = dp_idx(grid, Pn, 0.5)
    zp["UB"] = [float(grid[k]) for k in K]; zp["ZB"] = [float(Pn[k]) for k in K]
    # crease points again, 1" inside the edges
    m0, u, v = np.array(zp["m0"]), np.array(zp["u"]), np.array(zp["v"]); gi = g.buffer(-1.0); inner = []
    for t in zp["UB"][1:-1]:
        X = LineString([tuple(m0 + u * t - v * 1e4), tuple(m0 + u * t + v * 1e4)]).intersection(gi)
        for sg in ([X] if X.geom_type == "LineString" else list(getattr(X, "geoms", []))):
            if sg.is_empty or sg.geom_type != "LineString": continue
            n = max(1, int(math.ceil(sg.length / 24.0)))
            inner += [(sg.interpolate(k * sg.length / n).x, sg.interpolate(k * sg.length / n).y) for k in range(n + 1)]
    c["inner"] = [[round(x, 3), round(y, 3), round(float(runs_eval(zp, x, y)[0]), 3)] for x, y in inner]
    after = [abs(curb_top((q[0], q[1])) - float(runs_eval(zp, q[0], q[1])[0])) for q in [(sg.interpolate(f * sg.length).x, sg.interpolate(f * sg.length).y) for sg in segs for f in (0, 0.25, 0.5, 0.75, 1)]]
    report.append((c["kind"], c["area_sf"], kind, round(float(np.abs(ms).max()), 2), round(max(after), 2), len(zp["UB"])))
print("walks pinned to the curb (kind, sf, contact, worst step before, after, breaks):")
for r in report: print("  ", r)
def zf_of(c):
    zp = c["zf"]
    if zp["type"] == "patio": return lambda x, y: zp["z0"] - zp["s"] * (np.asarray(y) - zp["y0"])
    return lambda x, y: runs_eval(zp, x, y)[0]

# ---- wells: inner boxes (walls are 2" on the outer box), gravel floor top at -82
wells = []
for w in S.wells:
    x0, y0, x1, y1 = w.bounds
    wells.append(dict(box=[x0, y0, x1, y1], inner=[x0 + 2, y0 + 2, x1 - 2, y1 - 2], top=-82.0))
wells.append(dict(box=[71.0, -217.2, 147.0, -174.9], inner=[73.0, -215.2, 145.0, -176.9], top=-82.0))   # the SW well added later (walls 7771344-48)
well_geoms = [box(*w["box"]) for w in wells]

# ---- ground edits
foot = S.foot
conc_geoms = [Polygon(c["loops"][0], c["loops"][1:]) for c in concrete]
conc_lines = unary_union([g.exterior for g in conc_geoms])
stoops = [s for s, _ in S.stoops.values()]
zone_del = unary_union([foot.buffer(3.0)] + [g.buffer(2.5) for g in well_geoms] + [conc_lines.buffer(2.5)] + conc_geoms)
protect = pave_poly.buffer(7.0)                                       # never touch the asphalt zone, its edge or the curb line
topo_delete = [v[:2] for v in V if zone_del.contains(Point(v[0], v[1])) and not protect.contains(Point(v[0], v[1]))
               and S.EXT.exterior.distance(Point(v[0], v[1])) > 0.5]
blocked = unary_union([protect, *conc_geoms, *stoops, *well_geoms])
add = []
def ring_pts(geom, off, step, zfun, skip):
    r = geom.buffer(off, join_style=2).exterior; n = max(4, int(r.length / step)); out = []
    for i in range(n):
        p = r.interpolate(i * r.length / n)
        if not skip.contains(p): out.append([round(p.x, 3), round(p.y, 3), round(float(zfun(p.x, p.y)), 3)])
    return out
add += ring_pts(foot, 1.5, 12, lambda x, y: G(x, y), unary_union([blocked, *[g.buffer(1.0) for g in well_geoms]]))     # grass at grade along the wall faces
for g in well_geoms: add += ring_pts(g, 1.5, 12, lambda x, y: G(x, y), unary_union([protect, *conc_geoms, *stoops, foot]))
for c, g in zip(concrete, conc_geoms):
    zf = zf_of(c)
    add += ring_pts(g, 1.5, 12, lambda x, y, zf=zf: zf(x, y) - 0.5, unary_union([protect, foot.buffer(2.0), *[w.buffer(2.0) for w in well_geoms], *stoops, *[h for h in conc_geoms if h is not g]]))
# under every concrete floor the ground sits 3" below the floor's top, so an excavation never meets a nearly coincident surface
# (that is what made walks fail to cut: "Can't cut joined element")
for c, g in zip(concrete, conc_geoms):
    zf = zf_of(c)
    for x, y in grid_in(g, 30, 3.0) or [g.representative_point().coords[0]]:
        add.append([round(x, 3), round(y, 3), round(float(zf(x, y)) - 3.0, 3)])
dset = set((round(x, 2), round(y, 2)) for x, y in topo_delete)
keep = np.array([v[:2] for v in V if (round(v[0], 2), round(v[1], 2)) not in dset]); tk = cKDTree(keep)
A = np.array([p[:2] for p in add]); drop = set(j for i, j in cKDTree(A).query_pairs(0.5))
add = [p for k, p in enumerate(add) if k not in drop and tk.query(p[:2])[0] >= 0.5 and S.EXT.buffer(-0.5).contains(Point(p[0], p[1]))]
print("ground %d: delete %d (building pit, well pits, old walk edges), add %d -> %d (limit 9999)" % (len(V), len(topo_delete), len(add), len(V) - len(topo_delete) + len(add)))
json.dump(dict(concrete=[dict(kind=c["kind"], zf=c["zf"], inner=c["inner"]) for c in concrete], wells=wells, topo_delete=topo_delete, topo_add=add),
          open(SCR + "/site_fix2.json", "w"))
