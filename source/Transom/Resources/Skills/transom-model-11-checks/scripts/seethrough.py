"""See-through check: look at the building from outside and report anywhere the first thing seen should only be seen from inside.
usage: python seethrough.py MESH_DUMP --skip ID,ID --ok ID,ID --inner "Gypsum,Plywood" [--px 1] [--min 4]
Orthographic rays from 8 compass directions at two tilts. Each pixel keeps its nearest face. A pixel fails when that face is a floor, or a wall
face made of an --inner material: a hole in the shell lets that face be seen, however narrow the hole is. Window and door boxes block rays, so
glazing is not a failure. --skip names elements to leave out entirely (open site walls such as egress wells). --ok names exterior elements that
block rays but are never a failure (a soffit is a floor that is meant to be seen). Never --skip an element that closes the shell: the rays then
reach whatever it hides and report it. --min is the smallest cluster reported, in square inches.
It finds a hole of any size down to about --px wherever a floor or an inside finish shows through it. A hole that only shows the siding of
another wall behind it passes: in the control run it caught the gap over a low roof and one slot, and missed a slot that looked onto the
siding face of the wall behind. So slots.py still runs. It cannot find a fin or a wrong shape, so the look at section-box views is still needed."""
import math, sys
import numpy as np
from scipy import ndimage


def arg(name, default):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


PX = float(arg("--px", 1.0))
MIN = float(arg("--min", 4.0))
SKIP = {int(x) for x in arg("--skip", "").split(",") if x}
OK = {int(x) for x in arg("--ok", "").split(",") if x}
INNER = [x.strip().lower() for x in arg("--inner", "gypsum").split(",") if x.strip()]

tris, meta = [], []
cur = None
for line in open(sys.argv[1], encoding="utf-8"):
    if line.startswith("F "):
        head, _, mat = line.rstrip("\n").partition(" |")
        p = head.split(" ")
        cur = (p[1], int(p[2]), mat)
    elif line.startswith("T "):
        if cur[1] in SKIP:
            continue
        tris.append(np.array(list(map(float, line.split(" ")[1:10]))).reshape(3, 3))
        meta.append(cur)
    elif line.startswith("B "):
        p = line.split(" ")
        a = np.array(list(map(float, p[3:6])))
        b = np.array(list(map(float, p[6:9])))
        c = [np.array([a[0] if i & 1 == 0 else b[0], a[1] if i & 2 == 0 else b[1], a[2] if i & 4 == 0 else b[2]]) for i in range(8)]
        for q in ((0, 1, 3, 2), (4, 5, 7, 6), (0, 1, 5, 4), (2, 3, 7, 6), (0, 2, 6, 4), (1, 3, 7, 5)):
            for t in ((q[0], q[1], q[2]), (q[0], q[2], q[3])):
                tris.append(np.array([c[i] for i in t]))
                meta.append(("Opening", int(p[2]), ""))
tris = np.array(tris)
print("triangles", len(tris), " materials:", sorted({m[2] for m in meta if m[0] == "Walls"}))


def bad(m):
    if m[1] in OK:
        return False
    return m[0] == "Floors" or (m[0] == "Walls" and any(k in m[2].lower() for k in INNER))


fails = []
for az in range(0, 360, 45):
    for tilt in (0.0, 25.0):
        d = np.array([math.cos(math.radians(az)) * math.cos(math.radians(tilt)), math.sin(math.radians(az)) * math.cos(math.radians(tilt)), -math.sin(math.radians(tilt))])
        r = np.cross(d, [0, 0, 1.0]); r /= np.linalg.norm(r)
        u = np.cross(r, d)
        P = np.stack([tris @ r, tris @ u, tris @ d], axis=-1)
        x0, y0 = P[..., 0].min(), P[..., 1].min()
        W, H = int((P[..., 0].max() - x0) / PX) + 2, int((P[..., 1].max() - y0) / PX) + 2
        depth = np.full((H, W), np.inf)
        idx = np.full((H, W), -1, int)
        for i, t in enumerate(P):
            a, b, c = t
            den = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
            if abs(den) < 1e-9:
                continue
            ix0, ix1 = int((t[:, 0].min() - x0) / PX), int((t[:, 0].max() - x0) / PX) + 1
            iy0, iy1 = int((t[:, 1].min() - y0) / PX), int((t[:, 1].max() - y0) / PX) + 1
            xs, ys = np.meshgrid(x0 + (np.arange(ix0, ix1 + 1) + .5) * PX, y0 + (np.arange(iy0, iy1 + 1) + .5) * PX)
            l0 = ((b[1] - c[1]) * (xs - c[0]) + (c[0] - b[0]) * (ys - c[1])) / den
            l1 = ((c[1] - a[1]) * (xs - c[0]) + (a[0] - c[0]) * (ys - c[1])) / den
            l2 = 1 - l0 - l1
            ok = (l0 >= -1e-6) & (l1 >= -1e-6) & (l2 >= -1e-6)
            if not ok.any():
                continue
            z = l0 * a[2] + l1 * b[2] + l2 * c[2]
            sub = depth[iy0:iy1 + 1, ix0:ix1 + 1]
            better = ok & (z < sub)
            sub[better] = z[better]
            idx[iy0:iy1 + 1, ix0:ix1 + 1][better] = i
        isbad = np.zeros(idx.shape, bool)
        badtri = np.array([bad(m) for m in meta])
        hit = idx >= 0
        isbad[hit] = badtri[idx[hit]]
        lab, n = ndimage.label(isbad)
        for k in range(1, n + 1):
            ys, xs = np.nonzero(lab == k)
            area = len(xs) * PX * PX
            if area < MIN:
                continue
            ids = idx[ys, xs]
            pts = (x0 + (xs + .5) * PX)[:, None] * r + (y0 + (ys + .5) * PX)[:, None] * u + depth[ys, xs][:, None] * d
            names = sorted({f"{meta[i][0]} {meta[i][1]} {meta[i][2]}" for i in np.unique(ids)})
            fails.append((az, tilt, area, pts.min(axis=0).round(0), pts.max(axis=0).round(0), names))
fails.sort(key=lambda f: -f[2])
print(f"see-through clusters over {MIN} sq in: {len(fails)}")
for az, tilt, area, lo, hi, names in fails[:40]:
    print(f"  from az {az:3d} tilt {tilt:2.0f}: {area:7.0f} sq in  xyz {lo.tolist()} .. {hi.tolist()}  {names}")
print("SEE-THROUGH: " + ("PASS" if not fails else "FAIL"))
