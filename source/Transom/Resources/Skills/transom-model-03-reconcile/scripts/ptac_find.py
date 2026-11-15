"""Find drafted PTAC grilles (hatched louver blocks) in the elevations and keep only those under a window that is next to a door.
usage: python ptac_find.py CAD_DIR openings_uv.json out.json
A block is >= 6 horizontal lines of one length (20-60") and the same ends (within 1"), stacked 0.75-3" apart. A 'door' is any opening
whose type says Door, or whose bottom is within 10" of a floor (MAIN 0, UPPER 138, LOWER -120) and is at least 70" tall. Rule (the user, 2026-11-07): no grille at a
window that is not next to a door; next to = the door's jamb within 12" of the window's jamb on the same floor."""
import csv, json, math, sys
from collections import defaultdict
CAD, OPEN, OUT = sys.argv[1:4]
FLOORS = (-120.0, 0.0, 138.0)
ops = json.load(open(OPEN))
for o in ops:
    o["is_door"] = "door" in o["type"].lower() or (any(abs(o["z0"] - f) <= 10 for f in FLOORS) and (o["z1"] - o["z0"]) >= 70)
found = []
for face in ("SOUTH", "NORTH", "EAST", "WEST"):
    H = []
    for r in csv.reader(open(f"{CAD}/elev_{face}.csv")):
        if len(r) < 5: continue
        a = list(map(float, r[1:5]))
        if abs(a[1] - a[3]) < 0.05 and 20 <= abs(a[2] - a[0]) <= 60:
            H.append((round(min(a[0], a[2]), 0), round(max(a[0], a[2]), 0), a[1]))
    groups = defaultdict(list)
    for u0, u1, z in H: groups[(u0, u1)].append(z)
    for (u0, u1), zs in groups.items():
        zs = sorted(set(round(z, 2) for z in zs))
        run = [zs[0]]
        for z in zs[1:] + [1e9]:
            if 0.75 <= z - run[-1] <= 3.0: run.append(z); continue
            if len(run) >= 6:
                win = [o for o in ops if o["face"] == face and not o["is_door"] and min(o["u1"], u1) - max(o["u0"], u0) > 0.6 * (u1 - u0) and -6 <= o["z0"] - run[-1] <= 14]   # a window's box reaches a few inches below its sill
                w = min(win, key=lambda o: o["z0"] - run[-1]) if win else None
                door = None
                if w:
                    for d in ops:
                        if d["face"] == face and d["is_door"] and abs(d["z0"] - w["z0"]) < 60 and min(abs(d["u0"] - w["u1"]), abs(w["u0"] - d["u1"])) <= 12: door = d
                found.append(dict(face=face, u0=u0, u1=u1, z0=run[0], z1=run[-1], lines=len(run), spacing=round((run[-1] - run[0]) / (len(run) - 1), 2),
                                  window=w["id"] if w else None, window_type=w["type"] if w else None, door=door["id"] if door else None, door_type=door["type"] if door else None,
                                  keep=bool(w and door)))
            run = [z] if z < 1e9 else []
found.sort(key=lambda f: (f["face"], f["z0"], f["u0"]))
for f in found:
    print(f"{f['face']:5s} u {f['u0']:7.1f}..{f['u1']:7.1f} ({f['u1']-f['u0']:4.1f} w)  z {f['z0']:6.1f}..{f['z1']:6.1f} ({f['z1']-f['z0']:4.1f} h, {f['lines']} lines @ {f['spacing']})  window {f['window']} {f['window_type']}  door {f['door']} {f['door_type']}  -> {'GRILLE' if f['keep'] else 'no grille'}")
print("doors (bottom at a floor, 70in+):", sorted({(o['family'], o['type']) for o in ops if o['is_door']}))
print(f"kept {sum(f['keep'] for f in found)} of {len(found)} drafted blocks")
json.dump(found, open(OUT, "w"), indent=0)
