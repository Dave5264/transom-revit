"""Feed the 2026-11-05 tower ruling into the faces, reconcile, and list the walls still to rebuild."""
import json, os, shutil, subprocess, sys
SRC = r"<path>"
D = "data_v2"
os.makedirs(D, exist_ok=True)
for lv in ("LOWER", "MAIN", "UPPER"):
    shutil.copy(os.path.join(SRC, f"faces_{lv}.json"), D)
u = json.load(open(os.path.join(D, "faces_UPPER.json")))
m = json.load(open(os.path.join(D, "faces_MAIN.json")))
print("UPPER faces near the north tower (CAD inches):")
for r in u:
    f = r["f"]
    if 280 < min(f[0], f[2]) < 420 and 320 < max(f[1], f[3]) < 520:
        print("  ", [round(v, 2) for v in f])
print("MAIN faces near the north tower:")
for r in m:
    f = r["f"]
    if 280 < min(f[0], f[2]) < 420 and 320 < max(f[1], f[3]) < 520:
        print("  ", [round(v, 2) for v in f])
# Ruling (the user, 2026-11-05, "let the drawn elevations govern"): the upper tower walls sit on the MAIN lines.
X_OLD, X_NEW = 402.12, 407.47
Y_OLD, Y_NEW = 504.62, 509.16
n = 0
for r in u:
    f = r["f"]
    horiz = abs(f[1] - f[3]) < 1e-6
    if not horiz and abs(f[0] - X_OLD) < 0.2 and max(f[1], f[3]) < 520 and min(f[1], f[3]) > 320:
        f[0] = f[2] = X_NEW; n += 1
        for i in (1, 3):
            if abs(f[i] - Y_OLD) < 0.2:
                f[i] = Y_NEW
    elif horiz and abs(f[1] - Y_OLD) < 0.2 and f[0] < 420 and f[2] < 420:
        f[1] = f[3] = Y_NEW; n += 1
        for i in (0, 2):
            if abs(f[i] - X_OLD) < 0.2:
                f[i] = X_NEW
print("moved onto MAIN lines:", n)
for r in u:
    f = r["f"]
    if abs(f[1] - f[3]) < 1e-6 and abs(f[1] - 335) < 0.2 and min(f[0], f[2]) < 403:
        f[0 if f[0] < f[2] else 2] = X_NEW   # the wall that met the tower's east side
for r in u:
    f = r["f"]
    if abs(f[0] - f[2]) < 1e-6 and abs(f[0] - 294.47) < 0.3:
        for i in (1, 3):
            if abs(f[i] - Y_OLD) < 0.3:
                f[i] = Y_NEW                      # the west mass east face that met the tower's north wall
# Any face end that met the moved faces must follow
for r in u:
    f = r["f"]
    horiz = abs(f[1] - f[3]) < 1e-6
    if horiz and f[0] < 420 and f[2] < 420:
        continue
    if horiz:
        pass
json.dump(u, open(os.path.join(D, "faces_UPPER.json"), "w"), indent=0)
