"""Map every drafted elevation line with no model line within TOL, so missing details can be named.
usage: python missing_map.py EXPORT_DIR CAD_DIR OUT_DIR [TOL]"""
import csv, math, os, sys
import numpy as np
from scipy.spatial import cKDTree
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection
EXP, CAD, OUT = sys.argv[1:4]; TOL = float(sys.argv[4]) if len(sys.argv) > 4 else 3.0
def load(p, skip=()):
    S = []
    for r in csv.reader(open(p)):
        if len(r) < 5 or any(r[0].upper().startswith(s) for s in skip): continue
        a = list(map(float, r[1:5]))
        if math.hypot(a[2]-a[0], a[3]-a[1]) > 0.25: S.append((r[0], a))
    return S
def dens(S, step=1.0):
    P, I = [], []
    for k, (_, a) in enumerate(S):
        n = max(1, int(math.hypot(a[2]-a[0], a[3]-a[1]) / step))
        for t in np.linspace(0, 1, n + 1): P.append((a[0] + (a[2]-a[0]) * t, a[1] + (a[3]-a[1]) * t)); I.append(k)
    return np.array(P), np.array(I)
for face in ("SOUTH", "NORTH", "EAST", "WEST"):
    C = load(os.path.join(CAD, f"elev_{face}.csv"), ("LW-GRADE",)); M = load(os.path.join(EXP, f"rvt_{face}.csv"), ("A-FLOR-HRAL",))
    pC, iC = dens(C); pM, _ = dens(M, 0.5)
    d, _ = cKDTree(np.unique(np.round(pM, 2), axis=0), leafsize=64, balanced_tree=False, compact_nodes=False).query(pC)
    med = np.array([np.median(d[iC == k]) for k in range(len(C))])
    miss = [C[k][1] for k in range(len(C)) if med[k] > TOL]
    L = sum(math.hypot(a[2]-a[0], a[3]-a[1]) for a in miss) / 12
    xs = [a[0] for _, a in C] + [a[2] for _, a in C]; zs = [a[1] for _, a in C] + [a[3] for _, a in C]
    fig, ax = plt.subplots(figsize=(22, 22 * (max(zs) - min(zs) + 40) / (max(xs) - min(xs) + 40)), dpi=100)
    ax.add_collection(LineCollection([((a[0], a[1]), (a[2], a[3])) for _, a in M], colors="#9aa3ad", linewidths=0.5))
    ax.add_collection(LineCollection([((a[0], a[1]), (a[2], a[3])) for a in miss], colors="#d62828", linewidths=0.9))
    ax.set_xlim(min(xs) - 20, max(xs) + 20); ax.set_ylim(min(zs) - 20, max(zs) + 20); ax.set_aspect("equal"); ax.grid(True, lw=0.2)
    ax.set_title(f"{face}: drafted lines with no model line within {TOL:g} in (red), model (gray). {L:.0f} lf missing")
    fig.tight_layout(); fig.savefig(os.path.join(OUT, f"missing_{face}.png")); plt.close(fig)
    print(face, f"{L:.0f} lf of drafted line has no model line within {TOL:g} in")
