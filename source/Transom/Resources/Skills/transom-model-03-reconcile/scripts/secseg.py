"""List long segments of the section sheet in a box, merged when collinear. usage: python secseg.py x0 x1 y0 y1 [minlen] [zdatum] [sloped]"""
import csv, math, sys
x0, x1, y0, y1 = map(float, sys.argv[1:5])
minlen = float(sys.argv[5]) if len(sys.argv) > 5 else 40
zd = float(sys.argv[6]) if len(sys.argv) > 6 else 0
only = len(sys.argv) > 7
S = []
for r in csv.reader(open("a40_full.csv")):
    if len(r) < 5:
        continue
    try:
        a, b, c, d = map(float, r[-4:])
    except ValueError:
        continue
    if min(a, c) < x0 or max(a, c) > x1 or min(b, d) < y0 or max(b, d) > y1:
        continue
    if (a, b) > (c, d):
        a, b, c, d = c, d, a, b
    S.append([a, b, c, d, ",".join(r[:-4])])
S.sort()
out = []
for s in S:
    m = None
    for o in out:
        if o[4] != s[4]:
            continue
        d1 = (o[2] - o[0], o[3] - o[1]); d2 = (s[2] - s[0], s[3] - s[1])
        if abs(d1[0] * d2[1] - d1[1] * d2[0]) > 1e-3 * math.hypot(*d1) * math.hypot(*d2):
            continue
        if math.hypot(o[2] - s[0], o[3] - s[1]) < 0.1:
            m = o; break
    if m:
        m[2], m[3] = s[2], s[3]
    else:
        out.append(list(s))
rows = []
for a, b, c, d, lay in out:
    L = math.hypot(c - a, d - b)
    if L < minlen:
        continue
    sl = (d - b) / (c - a) * 12 if abs(c - a) > 1e-6 else float("inf")
    if only and (abs(d - b) < 0.01 or abs(c - a) < 0.01):
        continue
    rows.append((lay, a, b - zd, c, d - zd, L, sl))
for r in sorted(rows, key=lambda r: (-r[5])):
    print(f"{r[0]:14} ({r[1]:8.1f},{r[2]:7.1f}) -> ({r[3]:8.1f},{r[4]:7.1f})  len {r[5]:6.0f}  slope {r[6]:7.2f}:12")
