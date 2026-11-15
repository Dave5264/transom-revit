"""Envelope coverage: does a modeled surface exist everywhere the reconciled building says it should?
usage: python coverage.py MESH_DUMP PRISMS.json [--dist 10] [--step 6]
PRISMS.json: list of {"name", "axis": "x"|"y", "a": [start, end], "profile": [[v, z], ...]} in model inches. Each prism is a
closed profile extruded along axis (v is the other plan coordinate). A sample on a prism's surface passes when a wall, roof,
floor, window or door lies within --dist. Samples inside another prism and on the prism's underside are skipped."""
import json, math, sys
import numpy as np
from scipy.spatial import cKDTree

MESH, PRISMS = sys.argv[1], sys.argv[2]
DIST = float(sys.argv[sys.argv.index("--dist") + 1]) if "--dist" in sys.argv else 10.0
STEP = float(sys.argv[sys.argv.index("--step") + 1]) if "--step" in sys.argv else 6.0
prisms = json.load(open(PRISMS))

chunks = []
boxes = []   # a door or window box can be deep (a door swing), so a sample inside one counts as covered
for line in open(MESH, encoding="utf-8"):
    p = line.split(" ")
    if p[0] == "T":
        v = np.array(list(map(float, p[1:10]))).reshape(3, 3)
        n = int(max(np.linalg.norm(v[1] - v[0]), np.linalg.norm(v[2] - v[0]), np.linalg.norm(v[2] - v[1])) / 4) + 1
        i, j = np.meshgrid(np.arange(n + 1), np.arange(n + 1))
        m = i + j <= n
        chunks.append(v[0] + np.outer(i[m] / n, v[1] - v[0]) + np.outer(j[m] / n, v[2] - v[0]))
    elif p[0] == "B":
        a = np.array(list(map(float, p[3:6]))); b = np.array(list(map(float, p[6:9])))
        boxes.append((a, b))
        ax = [np.append(np.arange(a[k], b[k], 4), b[k]) for k in range(3)]
        for k in range(3):
            o = [q for q in range(3) if q != k]
            g0, g1 = np.meshgrid(ax[o[0]], ax[o[1]])
            for side in (a[k], b[k]):
                c = np.zeros((g0.size, 3)); c[:, o[0]] = g0.ravel(); c[:, o[1]] = g1.ravel(); c[:, k] = side
                chunks.append(c)
pts = np.concatenate(chunks)
tree = cKDTree(np.unique(np.round(pts, 1), axis=0), leafsize=64, balanced_tree=False, compact_nodes=False)   # flat, duplicate-heavy data overflows the default builder
print("shell sample points:", len(pts))


def inside(profile, v, z):
    c = False
    n = len(profile)
    for i in range(n):
        v0, z0 = profile[i]; v1, z1 = profile[(i + 1) % n]
        if (z0 > z) != (z1 > z) and v < v0 + (z - z0) * (v1 - v0) / (z1 - z0):
            c = not c
    return c


def xyz(pr, u, v, z):
    return (u, v, z) if pr["axis"] == "x" else (v, u, z)


def in_any(q, skip):
    for pr in prisms:
        if pr is skip:
            continue
        u, v = (q[0], q[1]) if pr["axis"] == "x" else (q[1], q[0])
        if pr["a"][0] <= u <= pr["a"][1] and inside(pr["profile"], v, q[2]):
            return True
    return False


miss = []
total = 0
for pr in prisms:
    prof = pr["profile"]
    a0, a1 = pr["a"]
    us = np.arange(a0, a1 + 0.01, STEP)
    cx, cz = np.mean([p[0] for p in prof]), np.mean([p[1] for p in prof])
    n = len(prof)
    for i in range(n):
        v0, z0 = prof[i]; v1, z1 = prof[(i + 1) % n]
        L = math.hypot(v1 - v0, z1 - z0)
        nv, nz = (z1 - z0) / L, -(v1 - v0) / L
        if (cx - v0) * nv + (cz - z0) * nz > 0:
            nv, nz = -nv, -nz
        if nz < -0.9:  # underside
            continue
        for t in np.arange(0, L + 0.01, STEP):
            v = v0 + (v1 - v0) * t / L; z = z0 + (z1 - z0) * t / L
            for u in us:
                q = xyz(pr, u, v, z)
                out = xyz(pr, u, v + nv * 3, z + nz * 3)
                total += 1
                if in_any(out, pr):
                    continue
                d, _ = tree.query(q)
                if d > DIST:
                    miss.append(q)
    # end caps
    vs = [p[0] for p in prof]; zs = [p[1] for p in prof]
    for u, du in ((a0, -3), (a1, 3)):
        for v in np.arange(min(vs), max(vs) + 0.01, STEP):
            for z in np.arange(min(zs), max(zs) + 0.01, STEP):
                if not inside(prof, v, z):
                    continue
                q = xyz(pr, u, v, z)
                out = xyz(pr, u + du, v, z)
                total += 1
                if in_any(out, pr):
                    continue
                d, _ = tree.query(q)
                if d > DIST:
                    miss.append(q)
miss = np.array(miss).reshape(-1, 3)
if len(miss):
    keep = np.ones(len(miss), bool)
    for a, b in boxes:
        keep &= ~np.all((miss >= a - 1) & (miss <= b + 1), axis=1)
    miss = miss[keep]
print(f"samples {total}, uncovered {len(miss)} (surface further than {DIST} in from the model)")
if len(miss) == 0:
    print("COVERED")
    sys.exit(0)
# cluster uncovered samples
tr = cKDTree(miss)
seen = np.zeros(len(miss), bool)
rows = []
for i in range(len(miss)):
    if seen[i]:
        continue
    stack = [i]; seen[i] = True; mem = []
    while stack:
        k = stack.pop(); mem.append(k)
        for j in tr.query_ball_point(miss[k], STEP * 1.8):
            if not seen[j]:
                seen[j] = True; stack.append(j)
    m = miss[mem]
    rows.append((len(mem) * STEP * STEP / 144, m.min(axis=0).round(0).tolist(), m.max(axis=0).round(0).tolist()))
rows.sort(key=lambda r: -r[0])
print("GAPS (approx sf of uncovered surface, min xyz, max xyz):")
for r in rows[:25]:
    print(f"  {r[0]:7.1f} sf  {r[1]} .. {r[2]}")
sys.exit(1)
