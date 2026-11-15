"""Remaining walls: reconciled runs, the junction list, and the ruling patches. Writes data_v2/runs_rest.json. CAD frame = model - (37, -178.58)."""
import json
DX, DY = 37.0, -178.58
R = json.load(open("data_v2/runs.json"))


def mx(r):
    c = r["c"] + (DX if r["axis"] == "X" else DY)
    off = DY if r["axis"] == "X" else DX
    return c, r["u0"] + off, r["u1"] + off


rest = []
for r in R:
    c, u0, u1 = mx(r)
    if (c if r["axis"] == "X" else u1) <= 332 and not (r["axis"] == "Y" and abs(c - 330.58) < 1 and u1 > 400):
        continue                                  # west mass, already rebuilt
    rest.append(r)


def find(axis, s, cm, mat=None, band=None, u0m=None):
    out = []
    for r in rest:
        c, a, b = mx(r)
        if r["axis"] == axis and r["facing"] == s and abs(c - cm) < 0.6 and (mat is None or r["material"] == mat) and (band is None or r["base"] == band) and (u0m is None or abs(a - u0m) < 0.6):
            out.append(r)
    assert out, (axis, s, cm, mat, band, u0m)
    return out


# Center west plane: one plane through the gap, so the gable reads straight
for r in find("X", -1, 545.8):
    r["c"] = 509.0
for r in find("X", -1, 546.0):
    r["c"] = 509.0
# Tower ruling: ends follow the moved faces
for r in find("X", 1, 444.5, "Siding", "UPPER"):
    r["n0"] = 1
for r in find("Y", 1, 156.4, "Siding", "UPPER"):
    r["u0"], r["n0"] = 407.47, 1
for r in find("Y", 1, 330.6, "Siding", "MAIN"):
    if r["u1"] + DX > 400:
        r["u0"], r["n0"] = 293.25, 1
# Stale stub from the old tower position
rest = [r for r in rest if not (r["axis"] == "Y" and abs(mx(r)[0] - 330.6) < 1 and r["u1"] - r["u0"] < 12)]
# The 5.5in return at the center SE corner is one wall, MAIN to the roof
ret = [r for r in rest if r["axis"] == "X" and abs(mx(r)[0] - 978.5) < 0.6 and r["u1"] - r["u0"] < 12]
assert len(ret) == 2
ret[0]["base"], ret[0]["top"] = "MAIN", "UPPER"
rest.remove(ret[1])
# Vestibule north wall top: the north elevation's line at z 191.2
for r in find("Y", 1, 277.4, "Siding", "MAIN"):
    r["top_offset"] = 53.2


def J(name, axis, s, cm, u0m, u1m, n0, n1, base, base_off=0.0, top="UPPER", material="Siding"):
    return dict(plane=900, axis=axis, facing=s, c=cm - (DX if axis == "X" else DY), u0=u0m - (DY if axis == "X" else DX), u1=u1m - (DY if axis == "X" else DX),
                base=base, top=top, material=material, n0=n0, n1=n1, base_offset=base_off, tag=name)


x_c_ne = mx(find("X", 1, 977.8, "Siding", "UPPER")[0])[0]
y_e = mx(find("Y", -1, -42.0, "Siding", "UPPER")[0])[0]
rest += [
    J("J1 center gable over link", "X", -1, 546.0, -58.42, 156.42, 0, 0, "UPPER", 158.66),
    J("J2 west mass east face over link and tower", "X", 1, 330.25, -58.39, 330.58, -1, 1, "UPPER"),
    J("J4 wall above tower shed", "Y", 1, 192.65, 330.25, 444.47, 1, 1, "UPPER", 112.0),
    J("J5 center east wall", "X", 1, x_c_ne, -250.58, 326.0, -1, 0, "UPPER"),
    J("J6 closure over center north slope", "X", -1, x_c_ne, 237.5, 326.0, 0, 1, "UPPER", 132.0),
    J("J7 east wing south over lean-to", "Y", -1, y_e, x_c_ne, 1092.0, 1, 1, "UPPER", 107.0),
]
json.dump(rest, open("data_v2/runs_rest.json", "w"), indent=0)
print(len(rest), "walls to build")
for r in sorted(rest, key=lambda r: (r["axis"], r["facing"], r["c"], r["u0"])):
    c, a, b = mx(r)
    print(r["axis"], "s%+d" % r["facing"], "c", round(c, 2), "u", round(a, 2), round(b, 2), r["base"], r["top"], r["material"][:5], "n", r["n0"], r["n1"], r.get("base_offset", ""), r.get("top_offset", ""), r.get("tag", ""))
