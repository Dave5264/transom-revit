"""Measure each Revit-exported elevation against the drafted CAD elevation, by length of line.
usage: python elev_compare.py EXPORT_DIR CAD_DIR OUT_DIR [FACE ...]
EXPORT_DIR holds rvt_<FACE>.csv (read_export.cs). CAD_DIR holds elev_<FACE>.csv (extract.cs in openings-from-elevations).
Both are layer,u0,z0,u1,z1 in inches. Writes compare_<FACE>.png: green on the drawing, orange 1-3", red over 3"."""
import csv, math, os, sys
import numpy as np
from scipy.spatial import cKDTree
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection

EXP, CAD, OUT = sys.argv[1:4]
FACES = sys.argv[4:] or ["SOUTH", "NORTH", "EAST", "WEST"]
SKIP_MODEL = ("HRAL",)            # railing patterns
SKIP_CAD = ("LW-GRADE",)
ROOF, WALL, GLAZ = ("A-ROOF",), ("A-WALL", "I-WALL"), ("A-GLAZ", "A-DOOR", "A-DOOR-FRAM")


def load(p):
    return [(r[0], float(r[1]), float(r[2]), float(r[3]), float(r[4])) for r in csv.reader(open(p)) if len(r) >= 5]


def length(s):
    return math.hypot(s[3] - s[1], s[4] - s[2])


def dens(segs, step=1.0):
    P, I = [], []
    for k, (l, a, b, c, d) in enumerate(segs):
        n = max(1, int(length((l, a, b, c, d)) / step))
        for t in np.linspace(0, 1, n + 1):
            P.append((a + (c - a) * t, b + (d - b) * t)); I.append(k)
    return np.array(P), np.array(I)


def bands(med, L, mask):
    med, L = med[mask], L[mask]
    T = L.sum() or 1
    return " ".join(f"{n} {L[(med > a) & (med <= b)].sum() / T * 100:5.1f}%" for n, a, b in (("<=1/4", -1, .25), ("<=1", .25, 1), ("<=3", 1, 3), ("<=6", 3, 6), (">6", 6, 1e9))) + f"  ({L.sum() / 12:.0f} lf)"


for fn in FACES:
    M = [s for s in load(os.path.join(EXP, f"rvt_{fn}.csv")) if length(s) > 0.25 and not any(k in s[0].upper() for k in SKIP_MODEL)]
    C = [s for s in load(os.path.join(CAD, f"elev_{fn}.csv")) if length(s) > 0.25 and not s[0].upper().startswith(SKIP_CAD)]
    pM, iM = dens(M); pC, iC = dens(C)
    dM, _ = cKDTree(np.unique(np.round(pC, 2), axis=0), leafsize=64, balanced_tree=False, compact_nodes=False).query(pM); dC, _ = cKDTree(np.unique(np.round(pM, 2), axis=0), leafsize=64, balanced_tree=False, compact_nodes=False).query(pC)   # duplicates overflow the tree builder
    medM = np.array([np.median(dM[iM == k]) for k in range(len(M))]); medC = np.array([np.median(dC[iC == k]) for k in range(len(C))])
    lenM = np.array([length(s) for s in M]); lenC = np.array([length(s) for s in C])
    isRoof = np.array([s[0].upper().startswith(ROOF) for s in M]); isWall = np.array([s[0].upper() in WALL for s in M]); isGlaz = np.array([s[0].upper() in GLAZ for s in M])
    print(f"\n{fn}: model {lenM.sum() / 12:.0f} lf, CAD {lenC.sum() / 12:.0f} lf")
    print("  model ROOF on CAD: ", bands(medM, lenM, isRoof))
    print("  model WALL on CAD: ", bands(medM, lenM, isWall))
    print("  model GLAZ on CAD: ", bands(medM, lenM, isGlaz))
    for title, mask, cat in (("model ROOF lines furthest from any drafted line", isRoof, M), ("model WALL lines furthest from any drafted line", isWall, M)):
        print("  " + title + ":")
        for k in sorted(np.where(mask & (lenM > 12))[0], key=lambda k: -medM[k])[:8]:
            s = M[k]
            print(f"     {medM[k]:5.1f}in  {lenM[k]:6.0f}in  {s[0]} ({s[1]:.1f},{s[2]:.1f}) -> ({s[3]:.1f},{s[4]:.1f})")
    print("  drafted lines 24in+ with no model line within 3in (drawn but not modeled):")
    order = sorted(np.where((lenC >= 24) & (medC > 3))[0], key=lambda k: -lenC[k])[:12]
    for k in order:
        s = C[k]
        print(f"     {medC[k]:5.1f}in  {lenC[k]:6.0f}in  {s[0]} ({s[1]:.1f},{s[2]:.1f}) -> ({s[3]:.1f},{s[4]:.1f})")
    fig, ax = plt.subplots(figsize=(18, 8), dpi=115)
    ax.add_collection(LineCollection([((s[1], s[2]), (s[3], s[4])) for s in C], colors="#b9bec7", linewidths=0.6, label="drafted CAD"))
    for lo, hi, col, lab in ((-1, .25, "#16a34a", "model on CAD (<=1/4in)"), (.25, 1, "#65a30d", "1/4-1in"), (1, 3, "#f59e0b", "1-3in"), (3, 1e9, "#dc2626", ">3in")):
        S = [((M[k][1], M[k][2]), (M[k][3], M[k][4])) for k in range(len(M)) if lo < medM[k] <= hi and (isRoof[k] or isWall[k])]
        ax.add_collection(LineCollection(S, colors=col, linewidths=1.3, label=lab))
    xs = [s[1] for s in M] + [s[3] for s in M]; zs = [s[2] for s in M] + [s[4] for s in M]
    ax.set_xlim(min(xs) - 24, max(xs) + 24); ax.set_ylim(min(zs) - 12, max(zs) + 24); ax.set_aspect("equal"); ax.legend(loc="lower right", fontsize=8); ax.grid(True, lw=0.2, color="#eee")
    ax.set_title(f"{fn}: Revit walls and roofs measured against the drafted elevation")
    fig.tight_layout(); fig.savefig(os.path.join(OUT, f"compare_{fn}.png")); plt.close(fig)
