"""Give unlabeled contours their levels. Contours are 2 ft apart and every 5th is a major (GR-EX-CONT 10). Walk transects across
the map; two different contours met one after the other are one level apart. The sign comes from the direction of travel and a
global trend (ground rises toward +x and +y here: the drafted grade at the building is lowest on the west and south faces).
Solve k per contour by least squares, then check that majors land on multiples of 5 and report the misfits.
usage: python contour_levels.py site_poly_model.json -> contour_levels.json"""
import json, sys, math
import numpy as np
D = json.load(open(sys.argv[1]))
C = [c for c in D if c["layer"].startswith("GR-EX-CONT")]
for i, c in enumerate(C): c["i"] = i; c["major"] = c["layer"].endswith("10")
segs = []                                     # (i, x0, y0, x1, y1)
for c in C:
    P = c["pts"]
    for a, b in zip(P, P[1:]): segs.append((c["i"], a[0], a[1], b[0], b[1]))
S = np.array(segs)
def crossings(horizontal, v):
    """positions along a transect where contours cross it, sorted"""
    if horizontal: a0, a1, s0, s1 = S[:, 2], S[:, 4], S[:, 1], S[:, 3]
    else:          a0, a1, s0, s1 = S[:, 1], S[:, 3], S[:, 2], S[:, 4]
    m = ((a0 - v) * (a1 - v) < 0)
    t = (v - a0[m]) / (a1[m] - a0[m]); s = s0[m] + t * (s1[m] - s0[m])
    ids = S[m, 0].astype(int); o = np.argsort(s)
    return list(zip(s[o], ids[o]))
eqs = []                                      # (a, b, +1): k_b - k_a = +1 travelling in the up direction
for horizontal, lo, hi in [(True, -1350, 2600), (False, -1330, 1660)]:
    for v in np.arange(lo, hi, 18.0):
        cr = crossings(horizontal, v)
        for (s1, a), (s2, b), (s3, c3) in zip(cr, cr[1:], cr[2:] + [(None, -1)]):
            if a == b: continue
            if c3 == a: continue              # a, b, a: b is a closed hump or hollow; its sign is unknown here
            eqs.append((a, b, horizontal))
n = len(C)
# majority vote per pair, keeping both travel directions
from collections import Counter
pair = Counter((min(a, b), max(a, b), (1 if a < b else -1)) for a, b, h in eqs)
rows, rhs, w = [], [], []
for (a, b, sg), cnt in pair.items():
    r = np.zeros(n); r[b] += 1; r[a] -= 1; rows.append(r); rhs.append(sg); w.append(cnt)
A = np.array(rows) * np.sqrt(np.array(w))[:, None]; y = np.array(rhs) * np.sqrt(np.array(w))
A = np.vstack([A, np.eye(n)[0] * 1e-3]); y = np.append(y, 0)       # fix the gauge
k, *_ = np.linalg.lstsq(A, y, rcond=None)
kr = np.round(k).astype(int)
# majors should share one residue mod 5
res = Counter(int(v) % 5 for v, c in zip(kr, C) if c["major"])
best = res.most_common(1)[0][0]
bad = [c["i"] for v, c in zip(kr, C) if c["major"] and int(v) % 5 != best]
print("contours", n, "pairs", len(pair), "major residues", dict(res), "-> majors off:", len(bad))
# pair misfits
mis = sum(1 for (a, b, sg) in pair if (kr[b] - kr[a]) * sg != 1)
print("pairs violated:", mis, "of", len(pair))
out = [dict(i=c["i"], layer=c["layer"], k=int(v - best), pts=c["pts"]) for v, c in zip(kr, C)]
json.dump(out, open("contour_levels.json", "w"))
print("k range", min(o["k"] for o in out), max(o["k"] for o in out))
