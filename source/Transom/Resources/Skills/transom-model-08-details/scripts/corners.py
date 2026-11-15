"""Outside siding corners from wall_faces.json. A corner is two perpendicular siding faces whose vertical outline edges meet,
convex (each face runs away from the corner on the side its partner faces away from). Writes corners.json."""
import json
F = [f for f in json.load(open("wall_faces.json")) if "Siding" in f["type"]]
TOL = 0.75
def area(l):
    return abs(sum(l[i][0]*l[(i+1)%len(l)][1]-l[(i+1)%len(l)][0]*l[i][1] for i in range(len(l))))/2
def verticals(f):
    out = max(f["loops"], key=area)
    us = [p[0] for p in out]
    segs = []
    for i in range(len(out)):
        a, b = out[i], out[(i+1) % len(out)]
        if abs(a[0]-b[0]) < 0.05 and abs(a[1]-b[1]) > 1:
            u = (a[0]+b[0])/2
            # which side of u does the face lie on (sample the outline's u extent)
            side = 1 if abs(u-min(us)) < abs(u-max(us)) else -1
            segs.append((u, min(a[1], b[1]), max(a[1], b[1]), side))
    return segs
V = {f["id"]: verticals(f) for f in F}
corners = []
for A in F:
    if A["axis"] != "Y": continue        # A faces +/-y (u = x), B faces +/-x (u = y)
    for B in F:
        if B["axis"] != "X": continue
        for (ua, za0, za1, da) in V[A["id"]]:
            if abs(ua - B["plane"]) > TOL: continue
            for (ub, zb0, zb1, db) in V[B["id"]]:
                if abs(ub - A["plane"]) > TOL: continue
                if not (da == -B["s"] and db == -A["s"]): continue      # convex
                z0, z1 = max(za0, zb0), min(za1, zb1)
                if z1 - z0 < 6: continue
                corners.append(dict(x=B["plane"], y=A["plane"], z0=round(z0, 2), z1=round(z1, 2), A=A["id"], sA=A["s"], dA=da, B=B["id"], sB=B["s"], dB=db))
corners.sort(key=lambda c: (c["x"], c["y"], c["z0"]))
for c in corners: print(c)
print(len(corners), "corners")
json.dump(corners, open("corners.json", "w"), indent=1)
