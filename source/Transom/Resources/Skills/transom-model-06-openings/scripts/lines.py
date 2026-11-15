"""Distinct vertical and horizontal lines of one elevation inside a box, for reading a unit by hand.
usage: python lines.py <dir> <FACE> u0 u1 z0 z1 [minlen=12]"""
import csv, sys, os
from collections import defaultdict
D, FACE = sys.argv[1], sys.argv[2]
u0, u1, z0, z1 = map(float, sys.argv[3:7])
minlen = float(sys.argv[7]) if len(sys.argv) > 7 else 12
V, H = defaultdict(set), defaultdict(set)
for r in csv.reader(open(os.path.join(D, f"elev_{FACE}.csv"))):
    a, za, b, zb = map(float, r[1:5]); lay = r[0]
    if abs(a - b) < 0.01 and u0 <= a <= u1:
        lo, hi = max(min(za, zb), z0), min(max(za, zb), z1)
        if hi - lo >= minlen: V[round(a, 2)].add((round(lo, 2), round(hi, 2), lay))
    elif abs(za - zb) < 0.01 and z0 <= za <= z1:
        lo, hi = max(min(a, b), u0), min(max(a, b), u1)
        if hi - lo >= minlen: H[round(za, 2)].add((round(lo, 2), round(hi, 2), lay))
print("VERTICAL  u: [z-range layer]")
for k in sorted(V): print(f"  u={k:9.2f}  " + "  ".join(f"[{a}..{b} {l}]" for a, b, l in sorted(V[k])))
print("HORIZONTAL  z: [u-range layer]")
for k in sorted(H): print(f"  z={k:9.2f}  " + "  ".join(f"[{a}..{b} {l}]" for a, b, l in sorted(H[k])))
