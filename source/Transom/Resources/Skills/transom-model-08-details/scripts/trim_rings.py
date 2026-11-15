"""Window and door trim rings from the drafted elevations, cut into boards.
Group the model's openings per host wall (gap <= 7.5", overlapping heights; wider gaps are separate rings that may share a jamb), then find
the drafted rectangle round each group: a head line just above it, a bottom line below it and two jamb lines ending on the same corners.
Rules for the cases the drafting leaves open:
  - LOWER level openings sit in egress wells: no ring.
  - A group with a door at the floor and no bottom line: the ring runs to the door bottom, no bottom piece.
  - No bottom line, no door: the bottom width = the head width (a dropped PTAC leaves the ring tight to the sill).
  - No lines at all: copy the widths of a ringed group of the same unit types on the same host and level (a unit hidden in the drafting).
  - A jamb or head more than 1" off the median of the other sides is read as a neighbour's line and set to that median.
The ring minus its units, its kept PTAC blocks and any base band is cut into rectangles (horizontal strips, merged downward).
usage: python trim_rings.py <elev csv folder> model_openings.json ptac_blocks.json bands.json  ->  trim_rings.json, trim_pieces.json"""
import csv, json, sys, statistics
EL, OPS, PT, BANDS = sys.argv[1:5]
O = json.load(open(OPS))
P = [p for p in json.load(open(PT)) if p.get("keep")]
BAND = json.load(open(BANDS))            # {face: [[u0,u1,z0,z1],...]} the base band, a hole for the trim
MINP = 0.75                               # drop slivers thinner than this
def lines(face):
    H, V = [], []
    for r in csv.reader(open(f"{EL}/elev_{face}.csv")):
        if len(r) < 5: continue
        u0, z0, u1, z1 = map(float, r[1:5])
        if abs(z0 - z1) < 0.05 and abs(u1 - u0) > 6: H.append((z0, min(u0, u1), max(u0, u1)))
        if abs(u0 - u1) < 0.05 and abs(z1 - z0) > 6: V.append((u0, min(z0, z1), max(z0, z1)))
    return H, V
def pieces(L, R, B, T, holes):
    """rectangle minus holes -> list of [u0,u1,z0,z1] boards"""
    zs = sorted({B, T} | {min(max(h[k], B), T) for h in holes for k in (2, 3)})
    strips = []
    for za, zb in zip(zs, zs[1:]):
        if zb - za < 1e-6: continue
        cuts = sorted((max(h[0], L), min(h[1], R)) for h in holes if h[2] < zb - 1e-6 and h[3] > za + 1e-6 and h[1] > L and h[0] < R)
        x, segs = L, []
        for a, b in cuts:
            if a > x + 1e-6: segs.append((x, a))
            x = max(x, b)
        if R > x + 1e-6: segs.append((x, R))
        strips.append((za, zb, segs))
    out = []
    for za, zb, segs in strips:
        for s in segs:
            for o in out:                     # merge with the board just below when it has the same span
                if abs(o[3] - za) < 1e-6 and abs(o[0] - s[0]) < 1e-6 and abs(o[1] - s[1]) < 1e-6: o[3] = zb; break
            else: out.append([s[0], s[1], za, zb])
    return [[round(v, 3) for v in o] for o in out if o[1] - o[0] >= MINP and o[3] - o[2] >= MINP]
rings, report = [], []
for face in ["SOUTH", "WEST", "NORTH", "EAST"]:
    H, V = lines(face)
    ops = sorted([dict(o, u0=o["u"] - o["w"] / 2, u1=o["u"] + o["w"] / 2) for o in O if o["face"] == face and o["level"] != "LOWER LEVEL"], key=lambda o: o["u0"])
    groups = []
    for o in ops:
        for g in groups:
            if any(m["host"] == o["host"] and m["level"] == o["level"] and -0.5 <= o["u0"] - m["u1"] <= 7.5 and min(o["z1"], m["z1"]) - max(o["z0"], m["z0"]) > 12 for m in g):
                g.append(o); break
        else: groups.append([o])
    found = []
    for g in groups:
        U0, U1 = min(m["u0"] for m in g), max(m["u1"] for m in g); Z0, Z1 = min(m["z0"] for m in g), max(m["z1"] for m in g)
        door_floor = any(m["cat"] == "door" and abs(m["z0"] - Z0) < 0.6 for m in g)
        def hline(zlo, zhi, pick):
            c = [h for h in H if zlo < h[0] < zhi and U0 - 9 <= h[1] <= U0 - 1 and U1 + 1 <= h[2] <= U1 + 9]
            return pick(c, key=lambda h: h[0])[0] if c else None
        def vline(ulo, uhi, pick, zb, zt):
            c = [v for v in V if ulo < v[0] < uhi and v[1] <= zb + 1 and v[2] >= zt - 1]
            return pick(c, key=lambda v: v[0])[0] if c else None
        top = hline(Z1 + 1.5, Z1 + 9, min); bot = hline(Z0 - 12, Z0 - 1.5, max); note = ""
        if top is not None and bot is None and door_floor: bot, note = Z0, " (to the floor)"
        lef = rig = None
        if top is not None:
            zb = bot if bot is not None else Z0
            if door_floor: zb = Z0 + 20                      # entry jambs: the drafting may start them above a stoop or band
            lef = vline(U0 - 9, U0 - 1.5, max, zb, top); rig = vline(U1 + 1.5, U1 + 9, min, zb, top)
        if top is not None and lef is not None and rig is not None and bot is None: bot, note = Z0 - (top - Z1), " (bottom = head width)"
        g_info = dict(face=face, level=g[0]["level"], host=g[0]["host"], units=[m["id"] for m in g], types=sorted(m["type"] for m in g),
                      U0=U0, U1=U1, Z0=Z0, Z1=Z1, holes=[[m["u0"], m["u1"], m["z0"], m["z1"]] for m in g], door_floor=door_floor)
        if None in (top, bot, lef, rig):
            g_info["miss"] = f"top {top is not None} bot {bot is not None} left {lef is not None} right {rig is not None}"; found.append(g_info); continue
        w = dict(L=U0 - lef, R=rig - U1, B=Z0 - bot, T=top - Z1)
        for k in (() if door_floor else ("L", "R", "T")):
            others = [w[j] for j in ("L", "R", "T") if j != k]
            if abs(w[k] - statistics.median(others)) > 1 and abs(others[0] - others[1]) < 0.3: note += f" ({k} {w[k]:.2f} -> {others[0]:.2f})"; w[k] = others[0]
        g_info.update(w=w, note=note); found.append(g_info)
    for g in found:
        if "w" not in g:
            sib = [s for s in found if "w" in s and s["host"] == g["host"] and s["types"] == g["types"] and s["level"] == g["level"]] or \
                  [s for s in found if "w" in s and s["host"] == g["host"] and s["types"] == g["types"]]
            if not sib or g["types"][0].startswith("SA Stair") or "64 x 84" in g["types"][0]:
                report.append(f"{face} {g['level'][:5]} u {g['U0']:.1f}..{g['U1']:.1f} z {g['Z0']:.1f}..{g['Z1']:.1f} {g['types']}: no ring ({g['miss']})"); continue
            g["w"] = dict(sib[0]["w"]); g["note"] = f" (hidden in the drafting: widths copied from u {sib[0]['U0']:.1f})"
        w = g["w"]; L, R, B, T = g["U0"] - w["L"], g["U1"] + w["R"], g["Z0"] - w["B"], g["Z1"] + w["T"]
        holes = list(g["holes"]); ptac = False
        for p in P:
            if p["face"] == face and L - 1 <= p["u0"] and p["u1"] <= R + 1 and B - 1 <= p["z0"] and p["z1"] <= T + 1: holes.append([p["u0"], p["u1"], p["z0"], p["z1"]]); ptac = True
        holes += [b for b in BAND.get(face, []) if b[0] < R and b[1] > L and b[2] < T and b[3] > B]
        pcs = pieces(L, R, B, T, holes)
        rings.append(dict(face=face, level=g["level"], host=g["host"], units=g["units"], L=round(L, 3), R=round(R, 3), B=round(B, 3), T=round(T, 3), pieces=pcs, ptac=ptac))
        report.append(f"{face} {g['level'][:5]} u {g['U0']:.1f}..{g['U1']:.1f} z {g['Z0']:.1f}..{g['Z1']:.1f} {[t.replace('SA ', '') for t in g['types']]}{' +PTAC' if ptac else ''}: "
                      f"L {w['L']:.2f} R {w['R']:.2f} B {w['B']:.2f} T {w['T']:.2f}, {len(pcs)} boards{g['note']}")
json.dump(rings, open("trim_rings.json", "w"), indent=1)
print("\n".join(report)); print(len(rings), "rings,", sum(len(r["pieces"]) for r in rings), "boards")
