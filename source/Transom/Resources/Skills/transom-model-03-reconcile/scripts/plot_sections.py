"""Plot the section sheet: overview with a grid, then a zoom by box. usage: python plot_sections.py x0 x1 y0 y1 out.png [gridstep]"""
import csv, sys
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection
x0, x1, y0, y1 = map(float, sys.argv[1:5]); out = sys.argv[5]; step = float(sys.argv[6]) if len(sys.argv) > 6 else 100
S, L = [], []
for r in csv.reader(open("a40_full.csv")):
    if len(r) < 5:
        continue
    try:
        a, b, c, d = map(float, r[-4:])
    except ValueError:
        continue
    if max(a, c) < x0 or min(a, c) > x1 or max(b, d) < y0 or min(b, d) > y1:
        continue
    S.append(((a, b), (c, d))); L.append(",".join(r[:-4]))
w = 19; h = max(4, w * (y1 - y0) / (x1 - x0))
fig, ax = plt.subplots(figsize=(w, h + 0.4), dpi=100)
ax.add_collection(LineCollection(S, linewidths=0.35, colors="k"))
ax.set_xlim(x0, x1); ax.set_ylim(y0, y1); ax.set_aspect("equal")
ax.set_xticks(np.arange(step * np.ceil(x0 / step), x1, step)); ax.set_yticks(np.arange(step * np.ceil(y0 / step), y1, step))
ax.tick_params(labelsize=7); ax.grid(True, lw=0.25, color="#c33", alpha=.5)
fig.tight_layout(); fig.savefig(out); print(out, len(S))
