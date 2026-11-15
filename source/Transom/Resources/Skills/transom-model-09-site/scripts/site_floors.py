"""Site as FLOORS on a plain grass toposolid (the user's workflow, 2026-11-07). Writes site_floors.json for site_floors.cs.

- Ground: one toposolid at finish grade G everywhere (grass). The old one's vertices are reused (they carry the building pit and the
  egress-well pits), minus everything in and around the pavement, plus a 36" grid at G there. No depression, no edge pairs.
- Asphalt: one floor (outer outline + island hole) with TRUE arcs from the CAD, shape points at G - 5.35 (6" below the grass, less the
  paint and stop heights already in the model). It excavates the toposolid.
- Walks, patio, existing pad: floors at G + 0.5, stopping at the back of the curb. They excavate the toposolid too.
- Curbs: railing paths 3" behind the pavement edge (the curb's centreline), arcs offset as concentric arcs, corners joined by
  intersection, cut out at the toposolid edge, the enclosure, the building, the wells and the stoops. The railings are hosted on the
  toposolid, so they follow the grass; the curb profile (SA Curb 6x18) puts its top 1/2" above the grass and its face 1/4" over the asphalt.
Units: inches, model frame."""
import json, math, os
import numpy as np
from shapely.geometry import Polygon, Point, LineString, box
from shapely.ops import unary_union
from scipy.spatial import Delaunay
import site_build as S
G = S.G
SCR = r"<path>"
ASPH_DZ, CONC_DZ, CURB_OFF = -5.5, 0.5, 3.0
GRID_ASPH, EDGE_ASPH, GRID_CONC, EDGE_CONC = 96.0, 60.0, 48.0, 36.0

def piece(layer, x0, y0, x1, y1):
    for c in S.D:
        if c["layer"] != layer: continue
        P = [tuple(p[:2]) for p in c["pts"]]
        if math.dist(P[0], (x0, y0)) < 3 and math.dist(P[-1], (x1, y1)) < 3: return c["kind"], P
        if math.dist(P[-1], (x0, y0)) < 3 and math.dist(P[0], (x1, y1)) < 3: return c["kind"], P[::-1]
    raise KeyError((layer, x0, y0, x1, y1))

def circle(P):
    P = np.array(P); x, y = P[:, 0], P[:, 1]
    cx, cy, c = np.linalg.lstsq(np.c_[2 * x, 2 * y, np.ones(len(x))], x * x + y * y, rcond=None)[0]
    return (cx, cy), math.sqrt(c + cx * cx + cy * cy)

# primitives: {"t": "L", "a": (x, y), "b": (x, y), "tag"} or {"t": "A", "c": (x, y), "r", "a0", "a1" (radians, a1 reached from a0 in direction s), "s": +1/-1, "tag"}
def arc_prim(P, tag):
    c, r = circle(P)
    ang = [math.atan2(p[1] - c[1], p[0] - c[0]) for p in P]
    d = (ang[1] - ang[0] + math.pi) % (2 * math.pi) - math.pi
    s = 1 if d > 0 else -1
    a0 = ang[0]; sweep = 0
    for u, v in zip(ang, ang[1:]): sweep += (v - u + math.pi) % (2 * math.pi) - math.pi
    return {"t": "A", "c": c, "r": r, "a0": a0, "a1": a0 + sweep, "s": s, "tag": tag}
def pts_of(p, step=1.0):
    if p["t"] == "L":
        n = max(1, int(math.ceil(math.dist(p["a"], p["b"]) / step)))
        return [(p["a"][0] + (p["b"][0] - p["a"][0]) * i / n, p["a"][1] + (p["b"][1] - p["a"][1]) * i / n) for i in range(n + 1)]
    L = abs(p["a1"] - p["a0"]) * p["r"]; n = max(2, int(math.ceil(L / step)))
    return [(p["c"][0] + p["r"] * math.cos(p["a0"] + (p["a1"] - p["a0"]) * i / n), p["c"][1] + p["r"] * math.sin(p["a0"] + (p["a1"] - p["a0"]) * i / n)) for i in range(n + 1)]
def start(p): return p["a"] if p["t"] == "L" else (p["c"][0] + p["r"] * math.cos(p["a0"]), p["c"][1] + p["r"] * math.sin(p["a0"]))
def end(p): return p["b"] if p["t"] == "L" else (p["c"][0] + p["r"] * math.cos(p["a1"]), p["c"][1] + p["r"] * math.sin(p["a1"]))
def length(p): return math.dist(p["a"], p["b"]) if p["t"] == "L" else abs(p["a1"] - p["a0"]) * p["r"]

def ring(spec, close_tag):
    """spec: list of (tag, kind, points). Consecutive pieces are joined; gaps over 0.05" get a line."""
    prims = []
    for tag, kind, P in spec:
        if kind == "Arc": prims.append(arc_prim(P, tag))
        else:
            for a, b in zip(P, P[1:]):
                if math.dist(a, b) > 0.05: prims.append({"t": "L", "a": a, "b": b, "tag": tag})
    out = []
    for i, p in enumerate(prims):
        q = prims[(i + 1) % len(prims)]
        out.append(p)
        g = math.dist(end(p), start(q))
        if 0.0 < g < 1.0 and p["t"] == "L": p["b"] = start(q)                 # snap a small gap by moving a line end, never an arc
        elif 0.0 < g < 1.0 and q["t"] == "L": q["a"] = end(p)
        elif g > 0.05: out.append({"t": "L", "a": end(p), "b": start(q), "tag": close_tag if i == len(prims) - 1 else "gap"})
    return out

CAPX_F, CAPY_F = S.CAPX - 1.0, S.CAPY - 1.0          # the asphalt stops 1" inside the toposolid's edge: a cut face on the edge fails to excavate
xl = 1443 + (1464 - 1443) * (CAPY_F - 1087) / (2215 - 1087)
outer = ring([
    ("W lot", *piece("LW-5", -590, 729, -590, -359)),
    ("SW sweep", *piece("LW-5", -590, -359, -62, -862)),
    ("S row 2", *piece("LW-5", -62, -862, 1259, -857)),
    ("SE arc 1", *piece("LW-5", 1259, -857, 1431, -781)),
    ("SE arc 2", *piece("LW-5", 1431, -781, 1680, -1011)),
    ("cap", "Line", [(1680.0, -1011.0), (CAPX_F, -1011.0), (CAPX_F, CAPY_F), (xl, CAPY_F)]),
    ("E drive", "Line", [(xl, CAPY_F), (1443.0, 1087.0)]),
    ("NE arc", *piece("LW-5", 1443, 1087, 1279, 969)),
    ("N edge", *piece("LW-5", 1279, 969, -590, 979)),
], "enclosure opening")
island = ring([
    ("I SE arc", *piece("LW-5", 1562, -245, 1350, -568)),
    ("I S arc", *piece("LW-5", 1350, -568, 1259, -568)),
    ("I S edge", *piece("LW-5", 1259, -568, -62, -574)),
    ("I SW arc", *piece("LW-5", -62, -574, -301, -358)),
    ("I W edge", *piece("LW-5", -301, -358, -302, 405)),
    ("I NW arc", *piece("LW-5", -302, 405, -62, 693)),
    ("I N edge", *piece("LW-5", -62, 693, 1398, 682)),
    ("I NE arc", *piece("LW-5", 1398, 682, 1526, 579)),
], "I E edge")
for name, R in (("outer", outer), ("island", island)):
    print(name, len(R), "prims;", sum(1 for p in R if p["t"] == "A"), "arcs;", [p["tag"] for p in R if p["tag"] == "gap"].__len__(), "gap lines")
pave_poly = Polygon([q for p in outer for q in pts_of(p, 4)[:-1]]).buffer(0).difference(Polygon([q for p in island for q in pts_of(p, 4)[:-1]]).buffer(0))
print("pavement %.0f sf (site_build had %.0f)" % (pave_poly.area / 144, S.pave.area / 144))

# ---- curb paths: offset CURB_OFF toward the grass
def offset(p, d):
    if p["t"] == "L":
        (ax, ay), (bx, by) = p["a"], p["b"]; L = math.dist(p["a"], p["b"]); nx, ny = -(by - ay) / L, (bx - ax) / L
        mx, my = (ax + bx) / 2, (ay + by) / 2
        s = -1 if pave_poly.contains(Point(mx + nx, my + ny)) else 1
        return dict(p, a=(ax + s * nx * d, ay + s * ny * d), b=(bx + s * nx * d, by + s * ny * d))
    am = (p["a0"] + p["a1"]) / 2; mx, my = p["c"][0] + p["r"] * math.cos(am), p["c"][1] + p["r"] * math.sin(am)
    out_in_pave = pave_poly.contains(Point(mx + math.cos(am), my + math.sin(am)))
    return dict(p, r=p["r"] - d if out_in_pave else p["r"] + d)
def inter(p, q, near):
    """intersection of the supporting line/circle of p and q nearest to `near`"""
    def lc(L):  # line through a, b
        return np.array(L["a"]), np.array(L["b"]) - np.array(L["a"])
    cands = []
    if p["t"] == "L" and q["t"] == "L":
        a, u = lc(p); b, v = lc(q); M = np.array([u, -v]).T
        if abs(np.linalg.det(M)) > 1e-9: t = np.linalg.solve(M, b - a); cands.append(tuple(a + t[0] * u))
    elif p["t"] == "A" and q["t"] == "A":
        c0, r0, c1, r1 = np.array(p["c"]), p["r"], np.array(q["c"]), q["r"]; dd = np.linalg.norm(c1 - c0)
        if 1e-9 < dd <= r0 + r1 and dd >= abs(r0 - r1):
            a = (r0 * r0 - r1 * r1 + dd * dd) / (2 * dd); h = math.sqrt(max(0, r0 * r0 - a * a)); m = c0 + a * (c1 - c0) / dd; perp = np.array([-(c1 - c0)[1], (c1 - c0)[0]]) / dd
            cands += [tuple(m + h * perp), tuple(m - h * perp)]
    else:
        L, A = (p, q) if p["t"] == "L" else (q, p)
        a, u = lc(L); u = u / np.linalg.norm(u); c = np.array(A["c"]); f = a - c
        B, C = 2 * f.dot(u), f.dot(f) - A["r"] ** 2; disc = B * B - 4 * C
        if disc >= 0:
            for t in ((-B + math.sqrt(disc)) / 2, (-B - math.sqrt(disc)) / 2): cands.append(tuple(a + t * u))
    if not cands: return None
    return min(cands, key=lambda c: math.dist(c, near))
def set_start(p, P):
    if p["t"] == "L": p["a"] = P
    else:
        a = math.atan2(P[1] - p["c"][1], P[0] - p["c"][0]); p["a0"] = p["a0"] + ((a - p["a0"] + math.pi) % (2 * math.pi) - math.pi)
def set_end(p, P):
    if p["t"] == "L": p["b"] = P
    else:
        a = math.atan2(P[1] - p["c"][1], P[0] - p["c"][0]); p["a1"] = p["a1"] + ((a - p["a1"] + math.pi) % (2 * math.pi) - math.pi)
def offset_ring(R, d):
    O = [dict(offset(p, d), src=i) for i, p in enumerate(R)]
    for i in range(len(O)):
        p, q = O[i], O[(i + 1) % len(O)]
        if math.dist(end(p), start(q)) < 0.01: continue
        X = inter(p, q, end(R[i]))
        if X is None or math.dist(X, end(R[i])) > 4 * d: X = ((end(p)[0] + start(q)[0]) / 2, (end(p)[1] + start(q)[1]) / 2); print("  join fallback at", [round(v) for v in end(R[i])])
        set_end(p, X); set_start(q, X)
    return O
cap_zone = box(S.CAPX - 12, -3000, S.CAPX + 100, 3000).union(box(-3000, S.CAPY - 12, 3000, S.CAPY + 100))
excl = unary_union([cap_zone, S.U.buffer(1.0), S.foot.buffer(1.0)] + [w.buffer(1.0) for w in S.wells] + [s.buffer(1.0) for s, _ in S.stoops.values()])
def runs(O):
    """split the offset ring into chains outside the exclusion zones; 1" sampling"""
    subs = []                                                   # (sub-primitive, touches its start, touches its end)
    for p in O:
        P = pts_of(p, 1.0); n = len(P) - 1
        flags = [not excl.contains(Point(q)) for q in P]
        i = 0
        while i <= n:
            if not flags[i]: i += 1; continue
            j = i
            while j < n and flags[j + 1]: j += 1
            if j > i:
                sub = dict(p, a=P[i], b=P[j]) if p["t"] == "L" else dict(p, a0=p["a0"] + (p["a1"] - p["a0"]) * i / n, a1=p["a0"] + (p["a1"] - p["a0"]) * j / n)
                subs.append((sub, i == 0, j == n))
            i = j + 1
    chains, cur, prev_end = [], [], False
    for sub, ts, te in subs:
        if cur and prev_end and ts: cur.append(sub)
        else:
            if cur: chains.append(cur)
            cur = [sub]
        prev_end = te
    if cur: chains.append(cur)
    closed = len(subs) == len(O) and all(ts and te for _, ts, te in subs)
    if not closed and len(chains) > 1 and subs[-1][2] and subs[0][1]: chains[0] = chains[-1] + chains[0]; chains.pop()
    return [c for c in chains if sum(length(p) for p in c) > 12], closed
curb_chains = []
for name, R in (("outer", outer), ("island", island)):
    O = offset_ring(R, CURB_OFF)
    ch, closed = runs(O)
    print(name, "curb chains", len(ch), "closed" if closed else "open", ["%.0f ft" % (sum(length(p) for p in c) / 12) for c in ch])
    for c in ch: curb_chains.append(dict(ring=name, closed=closed and len(ch) == 1, prims=c, R=R))
# curb paths ON the asphalt (1/2" in from its edge), for railings hosted on the asphalt floor: each curb chain moved 3 1/2" toward the
# pavement (concentric arcs, parallel lines), corners re-joined; the curb profile then rides on the asphalt with a 5 1/2" rail height and
# a 3 1/2" rail offset, so it lands where the grass-hosted version did.
def toward_pave(p, d):
    q = dict(p)
    if p["t"] == "L":
        (ax, ay), (bx, by) = p["a"], p["b"]; L = math.dist(p["a"], p["b"]); nx, ny = -(by - ay) / L, (bx - ax) / L
        sg = 1 if pave_poly.contains(Point((ax + bx) / 2 + nx * (d + 0.5), (ay + by) / 2 + ny * (d + 0.5))) else -1
        q["a"] = (ax + sg * nx * d, ay + sg * ny * d); q["b"] = (bx + sg * nx * d, by + sg * ny * d)
    else:
        am = (p["a0"] + p["a1"]) / 2; mx, my = p["c"][0] + p["r"] * math.cos(am), p["c"][1] + p["r"] * math.sin(am)
        inward_in_pave = pave_poly.contains(Point(mx - math.cos(am) * (d + 0.5), my - math.sin(am) * (d + 0.5)))
        q["r"] = p["r"] - d if inward_in_pave else p["r"] + d
    return q
floor_paths = []
for c in curb_chains:
    O = [toward_pave(p, CURB_OFF + 0.5) for p in c["prims"]]
    for i in range(len(O) - 1):
        p, q = O[i], O[i + 1]
        if math.dist(end(p), start(q)) < 0.01: continue
        X = inter(p, q, end(c["prims"][i]))
        if X is not None and math.dist(X, end(c["prims"][i])) < 20: set_end(p, X); set_start(q, X)
    floor_paths.append(dict(ring=c["ring"], prims=O))
def export_chain(prims, closed):
    """[L, x0, y0, x1, y1] / [A, x0, y0, xm, ym, x1, y1]; each start is snapped to the previous end so Revit sees one continuous chain"""
    out, prev = [], None
    for k, p in enumerate(prims):
        s0, e = start(p), end(p)
        if prev is not None: s0 = prev
        if closed and k == len(prims) - 1: e = start(prims[0])
        if p["t"] == "L": out.append(["L", round(s0[0], 4), round(s0[1], 4), round(e[0], 4), round(e[1], 4)])
        else:
            am = (p["a0"] + p["a1"]) / 2; m = (p["c"][0] + p["r"] * math.cos(am), p["c"][1] + p["r"] * math.sin(am))
            out.append(["A", round(s0[0], 4), round(s0[1], 4), round(m[0], 4), round(m[1], 4), round(e[0], 4), round(e[1], 4)])
        prev = (out[-1][-2], out[-1][-1])
    return out

# ---- floors
def grid_in(geom, step, inset):
    g = geom.buffer(-inset); x0, y0, x1, y1 = g.bounds; out = []
    for x in np.arange(math.floor(x0 / step) * step, x1 + 0.1, step):
        for y in np.arange(math.floor(y0 / step) * step, y1 + 0.1, step):
            if g.contains(Point(x, y)): out.append((float(x), float(y)))
    return out
def ring_samples(prims, step):
    out = []
    for p in prims:
        n = max(1, int(round(length(p) / step))); P = pts_of(p, length(p) / n)
        out += P[:-1]
    return out
# ---- adaptive grading: boundary points by Douglas-Peucker on the grade profile, interior points added where a linear TIN misses the grade
import shapely
def fz(dz): return lambda x, y: G(x, y) + dz
def dp_idx(s, z, tol):
    keep = {0, len(s) - 1}; stack = [(0, len(s) - 1)]
    while stack:
        i, j = stack.pop()
        if j - i < 2: continue
        t = (s[i + 1:j] - s[i]) / max(s[j] - s[i], 1e-9); e = np.abs(z[i + 1:j] - (z[i] + t * (z[j] - z[i])))
        k = int(np.argmax(e))
        if e[k] > tol: m = i + 1 + k; keep.add(m); stack += [(i, m), (m, j)]
    return sorted(keep)
def chain_pts(polys, f, tol, maxgap, step=6.0):
    """polys: list of point lists (each a primitive or edge sampled at `step`); keeps the ends, DP between, at most `maxgap` apart"""
    out = []
    for P in polys:
        P = np.array(P); s = np.r_[0, np.cumsum(np.hypot(*np.diff(P, axis=0).T))]; z = np.asarray(f(P[:, 0], P[:, 1]), float)
        K = dp_idx(s, z, tol); K2 = [K[0]]
        for u, v in zip(K, K[1:]):
            n = int(math.ceil((s[v] - s[u]) / maxgap))
            for q in range(1, n): K2.append(int(np.searchsorted(s, s[u] + (s[v] - s[u]) * q / n)))
            K2.append(v)
        out += [tuple(P[k]) for k in K2[:-1]]
    return out
def adaptive(geom, fixed, f, tol, coarse, check, maxit=80):
    pts = list(fixed) + grid_in(geom, coarse, 6)
    C = np.array(grid_in(geom, check, 1.0)); fC = np.asarray(f(C[:, 0], C[:, 1]), float)
    for it in range(maxit):
        P = np.array(pts); Z = np.asarray(f(P[:, 0], P[:, 1]), float); tri = Delaunay(P)
        sx = tri.find_simplex(C); T = tri.transform[sx]; bc = np.einsum("nij,nj->ni", T[:, :2], C - T[:, 2]); w = np.c_[bc, 1 - bc.sum(1)]
        e = np.abs((w * Z[tri.simplices[sx]]).sum(1) - fC)
        cen = P[tri.simplices].mean(1); inside = shapely.contains_xy(geom, cen[:, 0], cen[:, 1])
        e[(sx < 0) | ~inside[sx]] = 0
        if e.max() < tol: break
        seen, new = set(), []
        for k in np.argsort(-e):
            if e[k] < tol: break
            if sx[k] in seen: continue
            seen.add(sx[k]); new.append((float(C[k, 0]), float(C[k, 1])))
        pts += new
    return pts, float(e.max()), it
# the user (2026-11-07): floors are simple shapes that approximate the grade, the way they'd be built. So the asphalt edges break only
# where the grade line bends by more than 1/2", and the interior gets a point only where the surface would miss the grade by 1 1/2".
A = fz(ASPH_DZ)
EDGE_TOL, EDGE_GAP, IN_TOL, IN_COARSE = 2.0, 1200.0, 3.0, 600.0
def prim_profile(p, f, tol, maxgap):
    """DP breakpoints of f along primitive p: (t in [0, 1], xy, z) lists, ends included"""
    P = np.array(pts_of(p, 6.0)); s = np.r_[0, np.cumsum(np.hypot(*np.diff(P, axis=0).T))]; z = np.asarray(f(P[:, 0], P[:, 1]), float)
    K = dp_idx(s, z, tol); K2 = [K[0]]
    for u, v in zip(K, K[1:]):
        n = int(math.ceil((s[v] - s[u]) / maxgap))
        for q in range(1, n): K2.append(int(np.searchsorted(s, s[u] + (s[v] - s[u]) * q / n)))
        K2.append(v)
    K2 = sorted(set(K2))
    return [float(s[k] / s[-1]) for k in K2], [tuple(P[k]) for k in K2], [float(z[k]) for k in K2]
prof = {id(outer): [prim_profile(p, A, EDGE_TOL, EDGE_GAP) for p in outer], id(island): [prim_profile(p, A, EDGE_TOL, EDGE_GAP) for p in island]}
asph_edge = [xy for R in prof.values() for T, XY, Z in R for xy in XY[:-1]]
asph_xy, asph_err, asph_it = adaptive(pave_poly, asph_edge, A, IN_TOL, IN_COARSE, 24)
asph_pts = [[round(x, 3), round(y, 3), round(float(A(x, y)), 3)] for x, y in asph_xy]
print("asphalt: %d edge + %d interior points, worst miss %.2f in after %d passes" % (len(asph_edge), len(asph_xy) - len(asph_edge), asph_err, asph_it))
# curb path heights follow the ASPHALT edge (6" reveal), not the grass: the grass is graded to the curb
def src_t(p, q):
    if p["t"] == "L":
        a, b = np.array(p["a"]), np.array(p["b"]); return float(np.clip((np.array(q) - a).dot(b - a) / (b - a).dot(b - a), 0, 1))
    ang = math.atan2(q[1] - p["c"][1], q[0] - p["c"][0]); sw = p["a1"] - p["a0"]; sg = 1 if sw > 0 else -1
    d = ((ang - p["a0"]) * sg) % (2 * math.pi)
    if d > abs(sw): d = 0.0 if d > (abs(sw) + 2 * math.pi) / 2 else abs(sw)
    return d / abs(sw)
# The asphalt ring (with the island hole) will not excavate ("Can't cut joined element"), so it is split into a north and a south piece by
# two cuts along y = 0 (across the west lot and the east drive). Each piece keeps the ring's edge break points, so the curbs still match.
def cross(R, west):
    for i, p in enumerate(R):
        if p["t"] != "L": continue
        (ax, ay), (bx, by) = p["a"], p["b"]
        if (ay < 0) != (by < 0) and ((min(ax, bx) < 500) == west):
            t = (0 - ay) / (by - ay); return i, (ax + t * (bx - ax), 0.0)
    raise ValueError("no crossing")
def halves(p, P): return dict(p, b=P), dict(p, a=P)
iw, PW = cross(outer, True); ie, PE = cross(outer, False); jw, QW = cross(island, True); je, QE = cross(island, False)
assert iw < ie and jw < je, (iw, ie, jw, je)
o_iw, o_ie, i_jw, i_je = halves(outer[iw], PW), halves(outer[ie], PE), halves(island[jw], QW), halves(island[je], QE)
outer_S = [o_iw[1]] + outer[iw + 1:ie] + [o_ie[0]]
outer_N = [o_ie[1]] + outer[ie + 1:] + outer[:iw] + [o_iw[0]]
island_S = [i_je[1]] + island[je + 1:] + island[:jw] + [i_jw[0]]
island_N = [i_jw[1]] + island[jw + 1:je] + [i_je[0]]
cutline = lambda a, b: {"t": "L", "a": a, "b": b, "tag": "piece cut"}
piece_S = outer_S + [cutline(PE, QE)] + island_S + [cutline(QW, PW)]
piece_N = outer_N + [cutline(PW, QW)] + island_N + [cutline(QE, PE)]
bp = [(xy, z) for R in prof.values() for T, XY, Z in R for xy, z in zip(XY, Z)]
def zcut(i, R, P): T, XY, Z = prof[id(R)][i]; return float(np.interp(src_t(R[i], P), T, Z))
bp += [(PW, zcut(iw, outer, PW)), (PE, zcut(ie, outer, PE)), (QW, zcut(jw, island, QW)), (QE, zcut(je, island, QE))]
asph_pieces = []
for name, pc in (("south", piece_S), ("north", piece_N)):
    poly = Polygon([q for p in pc for q in pts_of(p, 4)[:-1]]).buffer(0)
    bnd = LineString([q for p in pc for q in pts_of(p, 2)] + [start(pc[0])])
    fixed = [(xy, z) for xy, z in bp if bnd.distance(Point(xy)) < 0.02]
    seen = set(); fx = []
    for xy, z in fixed:
        k = (round(xy[0], 2), round(xy[1], 2))
        if k not in seen: seen.add(k); fx.append((xy, z))
    xy_all, err, _ = adaptive(poly, [xy for xy, z in fx], A, IN_TOL, IN_COARSE, 24)
    zmap = {(round(xy[0], 2), round(xy[1], 2)): z for xy, z in fx}
    pts = [[round(x, 3), round(y, 3), round(zmap.get((round(x, 2), round(y, 2)), float(A(x, y))), 3)] for x, y in xy_all]
    asph_pieces.append(dict(name=name, prims=pc, pts=pts, area_sf=round(poly.area / 144), err=round(err, 2)))
    print("asphalt %s piece: %d sf, %d points (%d on the edge), worst miss %.2f in" % (name, poly.area / 144, len(pts), len(fx), err))
CURB_RISE = 6.0 - 0.5                                  # the profile's top sits 1/2" above the railing base; the top is 6" above the asphalt
def curb_z(chain, sub, q):
    T, XY, Z = prof[id(chain["R"])][sub["src"]]
    return float(np.interp(src_t(chain["R"][sub["src"]], q), T, Z)) + CURB_RISE
curbline = []
for c in curb_chains:
    for sub in c["prims"]:
        P = pts_of(sub, 6.0); z = np.array([curb_z(c, sub, q) for q in P]); s = np.r_[0, np.cumsum([math.dist(a, b) for a, b in zip(P, P[1:])])]
        K = dp_idx(s, z, 0.05); K2 = [K[0]]
        for u, v in zip(K, K[1:]):
            n = int(math.ceil((s[v] - s[u]) / 36.0))
            for q in range(1, n): K2.append(int(np.searchsorted(s, s[u] + (s[v] - s[u]) * q / n)))
            K2.append(v)
        curbline += [[round(P[k][0], 3), round(P[k][1], 3), round(float(z[k]), 3)] for k in sorted(set(K2))]
dev = [p[2] - CURB_RISE - (float(G(p[0], p[1])) + ASPH_DZ) for p in curbline]
print("curb-line points %d; curb vs the old grass-following height: %.2f .. %.2f in" % (len(curbline), min(dev), max(dev)))
ring6 = pave_poly.buffer(CURB_OFF + 3.0, join_style=2)
walks = {k: S.clean(S.walks[k].difference(ring6)) for k in S.walks}
walk_all = unary_union([walks[k] for k in S.WALK_KEYS])
# the E entry walk's west edge lay 0.03" inside the building pit's steep face and would not excavate: run it 1.6" into the wall instead
walk_all = unary_union([walk_all, box(1025.5, 408.0, 1027.2, float(walks["E entry walk"].bounds[3]))])
patio = walks["N patio"].difference(walk_all)
pad = walks["existing pad"].difference(walk_all).difference(patio)
Cz = fz(CONC_DZ)
def floor_parts(geom, kind):
    gs = geom.geoms if hasattr(geom, "geoms") else [geom]; out = []
    for g in gs:
        if g.area < 36: continue
        g = g.simplify(0.05)
        loops = [[[round(x, 3), round(y, 3)] for x, y in g.exterior.coords[:-1]]] + [[[round(x, 3), round(y, 3)] for x, y in r.coords[:-1]] for r in g.interiors]
        # concrete goes in flat or at a consistent slope (the user): runs of constant slope along the floor's long axis, a crease only where
        # the grade line bends by more than 1", cross slope at most 2%. The patio is flat at 1.5% falling away from the building.
        P = np.array(g.exterior.coords[:-1]); m0 = P.mean(0); w_, V = np.linalg.eigh(np.cov((P - m0).T)); u = V[:, 1]; v = np.array([-u[1], u[0]])
        U = lambda x, y: (np.asarray(x) - m0[0]) * u[0] + (np.asarray(y) - m0[1]) * u[1]
        Vv = lambda x, y: (np.asarray(x) - m0[0]) * v[0] + (np.asarray(y) - m0[1]) * v[1]
        q = np.array(grid_in(g, 6, 0.5) or [g.representative_point().coords[0]]); zq = np.asarray(Cz(q[:, 0], q[:, 1]), float)
        uq, vq = U(q[:, 0], q[:, 1]), Vv(q[:, 0], q[:, 1])
        if kind == "patio":
            y0 = 397.0; z0 = float(np.mean(Cz(np.linspace(130, 325, 20), np.full(20, y0))))
            zf = lambda x, y, z0=z0: z0 - 0.015 * (np.asarray(y) - y0); breaks = []
            zpar = dict(type="patio", z0=z0, y0=y0, s=0.015)
        else:
            ub = np.arange(uq.min(), uq.max() + 6, 6.0); zb = np.array([zq[np.abs(uq - t) <= 3.0].mean() if np.any(np.abs(uq - t) <= 3.0) else np.nan for t in ub])
            ok = ~np.isnan(zb); ub, zb = ub[ok], zb[ok]
            K = dp_idx(ub, zb, 1.0); UB, ZB = ub[K], zb[K]
            res = zq - np.interp(uq, UB, ZB); vm = float(vq.mean())
            c = float(np.clip(np.linalg.lstsq(np.c_[vq - vm], res, rcond=None)[0][0], -0.02, 0.02)) if len(q) > 3 else 0.0
            zf = lambda x, y, UB=UB, ZB=ZB, c=c, vm=vm: np.interp(U(x, y), UB, ZB) + c * (Vv(x, y) - vm)
            breaks = list(UB[1:-1])
            zpar = dict(type="runs", m0=[float(m0[0]), float(m0[1])], u=[float(u[0]), float(u[1])], v=[float(v[0]), float(v[1])], UB=[float(t) for t in UB], ZB=[float(z) for z in ZB], c=c, vm=vm)
        err = float(np.abs(zf(q[:, 0], q[:, 1]) - zq).max())
        pts = [tuple(p) for Lp in loops for p in Lp]; splits = []; inner = []
        gi = g.buffer(-1.0)
        for t in breaks:                                                 # crease points 1" inside the edges, so a slightly different outline still holds them
            far = 1e4; X = LineString([tuple(m0 + u * t - v * far), tuple(m0 + u * t + v * far)]).intersection(gi)
            for seg in ([X] if X.geom_type == "LineString" else list(getattr(X, "geoms", []))):
                if seg.is_empty or seg.geom_type != "LineString": continue
                n = max(1, int(math.ceil(seg.length / 24.0)))
                inner += [(seg.interpolate(k * seg.length / n).x, seg.interpolate(k * seg.length / n).y) for k in range(n + 1)]
        for t in breaks:
            far = 1e4; a0 = m0 + u * t - v * far; a1 = m0 + u * t + v * far
            X = LineString([tuple(a0), tuple(a1)]).intersection(g.boundary)
            xs = [X] if X.geom_type == "Point" else [p for p in getattr(X, "geoms", []) if p.geom_type == "Point"]
            xs = sorted([(p.x, p.y) for p in xs], key=lambda p: float(Vv(*p)))
            pts += xs
            if len(xs) == 2: splits.append([list(map(lambda z: round(z, 3), xs[0])), list(map(lambda z: round(z, 3), xs[1]))])
        out.append(dict(kind=kind, loops=loops, pts=[[round(x, 3), round(y, 3), round(float(zf(x, y)), 3)] for x, y in pts], splits=splits, zf=zpar, inner=[[round(x, 3), round(y, 3), round(float(zf(x, y)), 3)] for x, y in inner], area_sf=round(g.area / 144),
                        err=round(err, 2), runs=len(breaks) + 1))
        ZF.append(zf)
    return out
ZF = []
concrete = floor_parts(walk_all, "walk") + floor_parts(patio, "patio") + floor_parts(pad, "pad")
print("concrete (kind, sf, points, slope runs, splits, worst miss vs grade):", [(c["kind"], c["area_sf"], len(c["pts"]), c["runs"], len(c["splits"]), c["err"]) for c in concrete])

# ---- ground: from the CURRENT toposolid's vertices. The asphalt can't excavate the toposolid in Revit 2025 (big floors fail with "Can't
# cut joined element"; small ones work), so under the asphalt the ground is dropped well below the floor instead, and the step back up to
# the grass happens inside the curb's footprint: low points 1" in from the pavement edge, curb-line points 3" out. Where there is no
# curb (enclosure opening, the two north wells) the grass meets the asphalt flush instead. Walks, patio, pad and stoops still excavate.
cur = json.load(open("topo_vertices_now.json"))
curb_lines = unary_union([LineString([q for p in c["prims"] for q in pts_of(p, 4.0)]) for c in curb_chains])
conc_geoms = [Polygon(c["loops"][0], c["loops"][1:]) for c in concrete]
conc_lines = unary_union([g.exterior for g in conc_geoms])
near = pave_poly.buffer(2.0).union(curb_lines.buffer(0.6)).union(conc_lines.buffer(2.5))
topo_delete = [v[:2] for v in cur if near.contains(Point(v[0], v[1])) and S.EXT.exterior.distance(Point(v[0], v[1])) > 0.5]   # never the toposolid's own boundary
low_grid = [[x, y, round(float(G(x, y)) - 20.0, 3)] for x, y in grid_in(pave_poly, 36, 2.5)]
def inward(p, q, d):
    """the point d inches from q (on primitive p) toward the pavement side, and d inches toward the grass side"""
    if p["t"] == "L":
        (ax, ay), (bx, by) = p["a"], p["b"]; L = math.dist(p["a"], p["b"]); nx, ny = -(by - ay) / L, (bx - ax) / L
    else:
        nx, ny = (q[0] - p["c"][0]) / p["r"], (q[1] - p["c"][1]) / p["r"]
    sgn = 1 if pave_poly.contains(Point(q[0] + nx, q[1] + ny)) else -1
    return (q[0] + sgn * nx * d, q[1] + sgn * ny * d), (q[0] - sgn * nx * d, q[1] - sgn * ny * d)
edge_low, flush = [], []
for R in (outer, island):
    for i, p in enumerate(R):
        T, XY, Z = prof[id(R)][i]
        n = max(1, int(math.ceil(length(p) / 24.0)))
        for k in range(n + 1):
            q = pts_of(p, length(p) / n)[k]; t = src_t(p, q); ze = float(np.interp(t, T, Z))
            _, out3 = inward(p, q, CURB_OFF)
            if cap_zone.contains(Point(q)): continue
            if excl.contains(Point(out3)):                                  # no curb here: flush grass
                lo, _ = inward(p, q, 6.0); _, fl_ = inward(p, q, 0.25)
                edge_low.append([round(lo[0], 3), round(lo[1], 3), round(ze - 6.5, 3)]); flush.append([round(fl_[0], 3), round(fl_[1], 3), round(ze, 3)])
            else:
                lo, _ = inward(p, q, 1.0); edge_low.append([round(lo[0], 3), round(lo[1], 3), round(ze - 14.0, 3)])
keepout = unary_union([pave_poly.buffer(7.0), S.foot.buffer(3.0)] + [w.buffer(3.0) for w in S.wells] + [st.buffer(1.0) for st, _ in S.stoops.values()] + conc_geoms)
conc_edge = []                                                         # grass 1/2" below the concrete, 1 1/2" outside it
for c, g, zf in zip(concrete, conc_geoms, ZF):
    ring_ = g.buffer(1.5, join_style=2).exterior; n = max(4, int(ring_.length / 36))
    for i in range(n):
        p = ring_.interpolate(i * ring_.length / n)
        if not keepout.contains(p): conc_edge.append([round(p.x, 3), round(p.y, 3), round(float(zf(p.x, p.y)) - 0.5, 3)])
topo_add = low_grid + edge_low + flush + curbline + conc_edge
from scipy.spatial import cKDTree
dset = set((round(v[0], 2), round(v[1], 2)) for v in topo_delete)
kept = np.array([v[:2] for v in cur if (round(v[0], 2), round(v[1], 2)) not in dset])
tk = cKDTree(kept); ded = []
for p in topo_add:                                                       # Revit refuses points closer than ~1/2" in plan
    if tk.query(p[:2])[0] < 0.5: continue
    if ded and cKDTree(np.array([q[:2] for q in ded])).query(p[:2])[0] < 0.5 if len(ded) < 50 else False: continue
    ded.append(p)
tree, final = None, []
for p in ded:
    if final and tree is not None and tree.query(p[:2])[0] < 0.5: continue
    final.append(p)
    if len(final) % 200 == 0 or len(final) < 200: tree = cKDTree(np.array([q[:2] for q in final]))
# exact pass
fa = np.array([q[:2] for q in final]); tf = cKDTree(fa); drop = set()
for i, j in sorted(tf.query_pairs(0.5)): drop.add(j)
topo_add = [p for k, p in enumerate(final) if k not in drop and S.EXT.buffer(-0.5).contains(Point(p[0], p[1]))]
print("ground: %d vertices now, delete %d (pavement zone, curb paths, concrete edges); add %d low grid + %d low edge + %d flush + %d curb-line + %d concrete-edge = %d"
      % (len(cur), len(topo_delete), len(low_grid), len(edge_low), len(flush), len(curbline), len(conc_edge), len(cur) - len(topo_delete) + len(topo_add)))
topo = []
out = dict(asphalt=dict(loops=[export_chain(outer, True), export_chain(island, True)], pts=asph_pts),
           asphalt_pieces=[dict(name=p["name"], loop=export_chain(p["prims"], True), pts=p["pts"], area_sf=p["area_sf"]) for p in asph_pieces],
           concrete=concrete, curbs=[dict(ring=c["ring"], closed=c["closed"], prims=export_chain(c["prims"], c["closed"])) for c in curb_chains],
           curbs_on_floor=[dict(ring=c["ring"], prims=export_chain(c["prims"], False)) for c in floor_paths],
           topo=topo, topo_delete=topo_delete, topo_add=topo_add, asph_dz=ASPH_DZ, conc_dz=CONC_DZ)
json.dump(out, open("site_floors.json", "w"))
json.dump(out, open(os.path.join(SCR, "site_floors.json"), "w"))
# check plot
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt
fig, ax = plt.subplots(figsize=(18, 16), dpi=90)
def draw(geom, fc, ec="none"):
    from matplotlib.patches import Polygon as MP
    gs = geom.geoms if hasattr(geom, "geoms") else [geom]
    for g in gs:
        if g.is_empty or g.geom_type != "Polygon": continue
        ax.add_patch(MP(list(g.exterior.coords), closed=True, fc=fc, ec=ec, lw=0.5))
        for r in g.interiors: ax.add_patch(MP(list(r.coords), closed=True, fc="#cfe8c0", ec=ec, lw=0.5))
draw(S.EXT, "#cfe8c0"); draw(pave_poly, "#666"); draw(walk_all, "#ddd", "#999"); draw(patio, "#e8d8c0", "#999"); draw(pad, "#bbb", "#999"); draw(S.foot, "#c96")
for c in curb_chains:
    P = [q for p in c["prims"] for q in pts_of(p, 2)]
    ax.plot([q[0] for q in P], [q[1] for q in P], color="#f22" if c["ring"] == "outer" else "#22f", lw=1.6)
    ax.plot(*P[0], "o", color="k", ms=4); ax.plot(*P[-1], "s", color="k", ms=4)
ax.set_xlim(-1120, 1740); ax.set_ylim(-1320, 1140); ax.set_aspect("equal"); ax.grid(True, lw=0.2)
fig.tight_layout(); fig.savefig("site_floors_check.png")
