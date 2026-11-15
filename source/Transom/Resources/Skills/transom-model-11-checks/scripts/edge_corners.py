"""Roof edge profile check: compare the corner points of the drafted and modeled lines in small windows of an elevation.
usage: python edge_corners.py EXPORT_DIR CAD_DIR FACE u0 u1 z0 z1 [FACE u0 u1 z0 z1 ...]
Use a window around an eave end where the eave is seen end-on (the corner where an eave meets a rake), so the window holds the eave's
cross-section: roof top, fascia face, any step, underside. Every drafted corner should have a modeled corner within 1/4".
Line-length percentages miss this: on the first project a plumb cut left the bottom corner 5" out and 1 5/8" low while the roof
lines still read 95% within 3" of the drawing."""
import csv, math, os, sys

EXP, CAD = sys.argv[1], sys.argv[2]
args = sys.argv[3:]


def corners(path, u0, u1, z0, z1, roof_only):
    pts = set()
    for r in csv.reader(open(path)):
        if len(r) < 5:
            continue
        if roof_only and "ROOF" not in r[0].upper() and "FASC" not in r[0].upper():
            continue
        a = list(map(float, r[1:5]))
        if math.hypot(a[2] - a[0], a[3] - a[1]) < 0.3:
            continue
        for p in ((a[0], a[1]), (a[2], a[3])):
            if u0 <= p[0] <= u1 and z0 <= p[1] <= z1:
                pts.add((round(p[0], 1), round(p[1], 1)))
    return sorted(pts)


worst_all = 0
for i in range(0, len(args), 5):
    face, u0, u1, z0, z1 = args[i], *map(float, args[i + 1:i + 5])
    C = corners(os.path.join(CAD, f"elev_{face}.csv"), u0, u1, z0, z1, False)
    M = corners(os.path.join(EXP, f"rvt_{face}.csv"), u0, u1, z0, z1, True)
    far = [(p, min((math.hypot(p[0] - q[0], p[1] - q[1]) for q in M), default=99)) for p in C]
    worst = max((d for _, d in far), default=0)
    worst_all = max(worst_all, worst)
    print(f"{face} u {u0:g}..{u1:g} z {z0:g}..{z1:g}: drafted {len(C)} corners, model {len(M)}, worst {worst:.2f} in")
    for p, d in far:
        if d > 0.25:
            print(f"   drafted corner {p} has no model corner within {d:.2f} in")
print("EDGE PROFILE: " + ("PASS" if worst_all <= 0.25 else "FAIL"))
