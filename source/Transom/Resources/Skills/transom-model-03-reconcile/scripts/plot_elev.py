"""Plot drafted elevation zones with a labelled grid. usage: python plot_elev.py out.png step FACE,u0,u1,z0,z1 [FACE,u0,u1,z0,z1 ...]"""
import csv, sys
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection
COL = {"LW-0": "#999999", "LW-1": "#2a9d8f", "LW-2": "#d62828", "LW-3": "#f77f00", "LW-4": "#7b2cbf", "LW-5": "#1d3557"}
out, step = sys.argv[1], float(sys.argv[2])
zones = [z.split(",") for z in sys.argv[3:]]
fig, axs = plt.subplots(len(zones), 1, figsize=(17, 6.2 * len(zones)), dpi=95, squeeze=False)
for ax, (f, u0, u1, z0, z1) in zip(axs[:, 0], zones):
    u0, u1, z0, z1 = map(float, (u0, u1, z0, z1))
    by = {}
    for r in csv.reader(open(f"openings/elev_{f}.csv")):
        if len(r) < 5:
            continue
        a, b, c, d = map(float, r[1:5])
        if max(a, c) < u0 or min(a, c) > u1 or max(b, d) < z0 or min(b, d) > z1:
            continue
        by.setdefault(r[0], []).append(((a, b), (c, d)))
    for k, S in by.items():
        ax.add_collection(LineCollection(S, colors=COL.get(k, "#e9c46a"), linewidths=0.8, label=k))
    ax.set_xlim(u0, u1); ax.set_ylim(z0, z1); ax.set_aspect("equal")
    ax.set_xticks(np.arange(step * np.ceil(u0 / step), u1, step)); ax.set_yticks(np.arange(step * np.ceil(z0 / step), z1, step))
    ax.tick_params(labelsize=7); ax.grid(True, lw=0.2, color="#bbb"); ax.legend(fontsize=6, loc="lower right"); ax.set_title(f"{f}  u {u0}..{u1}  z {z0}..{z1}", fontsize=9)
    plt.setp(ax.get_xticklabels(), rotation=90)
fig.tight_layout(); fig.savefig(out); print(out)
