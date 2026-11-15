"""Finish grade G(x, y), inches, model frame, from the grade lines drafted on the four elevations (the CAD has no spot elevations and the
contours have no levels). Each elevation's finish-grade line is placed 6" outside the face it is drawn against; where an elevation has
no finish line, its existing line is used. A smoothing thin-plate spline through those points; far from the building the spline is
blended toward its own best-fit plane so the edges of the site don't run away.
usage: import grade; grade.G(x, y)  (numpy arrays or scalars). python grade.py prints a check table."""
import csv, json, os
import numpy as np
from scipy.interpolate import Rbf
EL = r"<path>"
def face_xy(face, u):
    if face == "SOUTH":
        x = u
        y = -185 if x < 329.75 else -64 if x < 546 else -257 if x < 977.75 else -262 if x < 1092 else -243 if x < 1195 else -257
    elif face == "NORTH":
        x = -u
        y = 403 if x < 330.25 else 337 if x < 444.5 else 343 if x < 546 else 428 if x < 1027 else 332 if x < 1195 else 332
    elif face == "WEST":
        y = -u
        x = 31 if -179.2 <= y <= 397.1 else 540 if -251 <= y < -179.2 else 31
    else:
        y = u
        x = 1098 if -256 <= y < -130 else 1033 if 326 < y <= 422 else 1201
    return x, y
pts = []
for face in ["SOUTH", "NORTH", "WEST", "EAST"]:
    lines = {"Finish": [], "Existing": []}
    for r in csv.reader(open(f"{EL}/elev_{face}.csv")):
        if len(r) < 5 or not r[0].startswith("LW-Grade"): continue
        u0, z0, u1, z1 = map(float, r[1:5])
        if abs(u1 - u0) < 0.5: continue                      # vertical steps: both ends are already in the neighbours
        lines["Finish" if "Finish" in r[0] else "Existing"].append((u0, z0, u1, z1))
    fin = lines["Finish"]
    fu = [(min(a[0], a[2]), max(a[0], a[2])) for a in fin]
    for kind, L in (("Finish", fin), ("Existing", lines["Existing"])):
        for u0, z0, u1, z1 in L:
            n = max(2, int(abs(u1 - u0) / 24) + 1)
            for t in np.linspace(0, 1, n):
                u = u0 + t * (u1 - u0); z = z0 + t * (z1 - z0)
                if kind == "Existing" and any(a - 30 <= u <= b + 30 for a, b in fu): continue
                x, y = face_xy(face, u)
                pts.append((x, y, z, face, kind))
P = np.array([[p[0], p[1]] for p in pts]); Z = np.array([p[2] for p in pts])
_rbf = Rbf(P[:, 0], P[:, 1], Z, function="thin_plate", smooth=50.0)
_tps = lambda xy: _rbf(xy[:, 0], xy[:, 1])
A = np.c_[P, np.ones(len(P))]; _plane, *_ = np.linalg.lstsq(A, Z, rcond=None)
BX0, BX1, BY0, BY1 = 6, 1195, -256, 422
def G(x, y):
    x = np.asarray(x, float); y = np.asarray(y, float)
    xy = np.c_[x.ravel(), y.ravel()]
    t = _tps(xy)
    pl = _plane[0] * xy[:, 0] + _plane[1] * xy[:, 1] + _plane[2]
    dx = np.maximum(0, np.maximum(BX0 - xy[:, 0], xy[:, 0] - BX1)); dy = np.maximum(0, np.maximum(BY0 - xy[:, 1], xy[:, 1] - BY1))
    d = np.hypot(dx, dy)
    w = np.clip((d - 240) / 600, 0, 1)                         # spline within 20 ft of the building, plane beyond 70 ft
    out = (1 - w) * t + w * pl
    return out.reshape(x.shape) if x.shape else float(out[0])
if __name__ == "__main__":
    print(len(pts), "grade points; plane dz/dx %.4f dz/dy %.4f z0 %.1f" % tuple(_plane))
    res = Z - G(P[:, 0], P[:, 1]); print("fit residual rms %.2f max %.2f in" % (np.sqrt((res**2).mean()), abs(res).max()))
    for name, x, y in [("W lot centre", -450, 180), ("W lot NW", -700, 700), ("U enclosure", -660, 850), ("N drive W", 0, 830), ("N drive E", 1200, 830),
                       ("N lot", 800, 580), ("E drive", 1600, 200), ("S row 1", 650, -460), ("S aisle", 650, -715), ("S row 2", 650, -965), ("SW arc", -300, -650),
                       ("SE corner", 1500, -900), ("site NW", -1000, 1100), ("site SE", 1700, -1250), ("S walk", 650, -324), ("SE stoop front", 1140, -240)]:
        print(f"  {name:15s} ({x:6.0f},{y:6.0f})  G {G(x, y):7.1f} in")
