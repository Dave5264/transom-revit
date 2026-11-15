"""Plot drafted elevation lines (gray) and the Revit export (orange) in a window. usage: python cadzoom.py FACE u0 u1 z0 z1 name"""
import csv, sys
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection
face, u0, u1, z0, z1, name = sys.argv[1], *map(float, sys.argv[2:6]), sys.argv[6]
def load(p):
    S = []
    for r in csv.reader(open(p)):
        if len(r) < 5: continue
        a = list(map(float, r[1:5]))
        if max(a[0], a[2]) < u0 or min(a[0], a[2]) > u1 or max(a[1], a[3]) < z0 or min(a[1], a[3]) > z1: continue
        S.append(((a[0], a[1]), (a[2], a[3])))
    return S
C = load(f"openings/elev_{face}.csv"); M = load(f"rvt_elev_export/rvt_{face}.csv")
w = 14 if (u1 - u0) > (z1 - z0) else 8
fig, ax = plt.subplots(figsize=(w, w * (z1 - z0) / (u1 - u0) + 0.6), dpi=110)
ax.add_collection(LineCollection(M, colors="#f08c00", linewidths=0.9, alpha=0.8))
ax.add_collection(LineCollection(C, colors="#222", linewidths=0.6))
ax.set_xlim(u0, u1); ax.set_ylim(z0, z1); ax.set_aspect("equal"); ax.grid(True, lw=0.2)
ax.set_title(f"{face} {name}: drafted (black) vs model (orange)")
fig.tight_layout(); fig.savefig(f"cadzoom/{face}_{name}.png"); plt.close(fig)
