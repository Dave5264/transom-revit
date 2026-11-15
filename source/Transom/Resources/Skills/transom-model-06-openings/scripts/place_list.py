"""Drafted units of one facade -> openings_<FACE>.json for the Revit placer, plus a check report.
usage: python place_list.py <dir> <FACE>
reads  units_<FACE>.json, fix_<FACE>.json (optional), elev_<FACE>.json (R, facing), walls.json, plan_<level name>.csv
Each unit goes to the FRONTMOST exterior wall facing the view whose face holds it (u) and whose height band holds
its sill (z). The plan's glazing/door linework inside that wall is the cross-check; plan openings no unit claims are
listed, since a face hidden behind a nearer mass never shows in the elevation."""
import json, csv, os, re, sys
D, FACE = sys.argv[1], sys.argv[2]
J = lambda n: json.load(open(os.path.join(D, n)))
meta = J(f"elev_{FACE}.json"); R, F = meta["R"], meta["facing"]
units = J(f"units_{FACE}.json")
fix = J(f"fix_{FACE}.json") if os.path.exists(os.path.join(D, f"fix_{FACE}.json")) else {}
FAM = {"win": ["Window-Fixed", "SA {w} x {h}"], "french": ["Window-Fixed", "SA French Door {w} x {h}"],
       "side": ["Window-Fixed", "SA Sidelite {w} x {h}"], "slider": ["Window-Fixed-Double", "SA Slider {w} x {h}"],
       "stair": ["Window-Fixed-Double", "SA Stair {w} x {h}"], "door": ["Masonry_Single", "SA Entry Door {w} x {h}"]}
FAM.update(fix.get("families", {}))
EXCL = re.compile(fix.get("wall_exclude", r"(?i)site|egress|retaining|foundation - concrete"))
GLAZ = set(fix.get("plan_layers", ["A-Glaz", "A-Door"]))

def at(u0, z0): return [u for u in units if abs(u["u0"] - u0) < 0.6 and abs(u["z0"] - z0) < 0.6]
for u0, z0 in fix.get("drop", []):
    m = at(u0, z0); assert len(m) == 1, ("drop", u0, z0, len(m)); units.remove(m[0])
for s in fix.get("set", []):
    m = at(*s["at"]); assert len(m) == 1, ("set", s["at"], len(m)); m[0].update({k: v for k, v in s.items() if k != "at"})
for a in fix.get("add", []): units.append(dict(a))
for rule in fix.get("rules", []):          # by drafted size, e.g. {"h": 68.36, "dz0": 2.0} or {"w": 36.32, "kind": "french"}
    for u in units:
        if "w" in rule and abs((u["u1"] - u["u0"]) - rule["w"]) > 0.05: continue
        if "h" in rule and abs((u["z1"] - u["z0"]) - rule["h"]) > 0.05: continue
        if "dz0" in rule: u["z0"] += rule["dz0"]
        if "kind" in rule and "kind" not in u: u["kind"] = rule["kind"]

dot = lambda p, d: p[0] * d[0] + p[1] * d[1]
walls = [w for w in J("walls.json") if w["ext"] and not EXCL.search(w["type"]) and dot(w["o"], F) > 0.99]
for w in walls:
    us = [dot(w["min"], R), dot(w["max"], R), dot([w["min"][0], w["max"][1]], R), dot([w["max"][0], w["min"][1]], R)]
    ds = [dot(w["min"], F), dot(w["max"], F), dot([w["min"][0], w["max"][1]], F), dot([w["max"][0], w["min"][1]], F)]
    w["ulo"], w["uhi"], w["depth"] = min(us), max(us), max(ds)
levels = sorted(J("levels.json"), key=lambda l: l["z"]) if os.path.exists(os.path.join(D, "levels.json")) else []
def level_for(u, w):
    """A continuous wall spans levels. An opening belongs to the level it sits on, so its sill is measured from there."""
    on = [l for l in levels if w["z0"] - 0.5 <= l["z"] <= u["z0"] + 0.5]
    return (on[-1]["name"], on[-1]["z"]) if on else (w["level"], w["levelZ"])
plan = {}
def plan_segs(level):
    if level not in plan:
        p = os.path.join(D, f"plan_{level}.csv")
        plan[level] = [tuple(map(float, r[1:5])) for r in csv.reader(open(p)) if r[0] in GLAZ] if os.path.exists(p) else []
    return plan[level]
def clusters(w, level=None):
    iv = []
    for (ax, ay, bx, by) in plan_segs(level or w["level"]):
        da, db = dot((ax, ay), F), dot((bx, by), F)
        if abs(da - db) > 0.05 or not (w["depth"] - w["width"] - 1 <= da <= w["depth"] + 0.5): continue
        a, b = sorted([dot((ax, ay), R), dot((bx, by), R)])
        if b - a >= 6 and b > w["ulo"] - 6 and a < w["uhi"] + 6: iv.append([a, b])
    out = []
    for a, b in sorted(iv):
        if out and a <= out[-1][1] + 0.5: out[-1][1] = max(out[-1][1], b)
        else: out.append([a, b])
    return [tuple(round(v, 2) for v in c) for c in out]

r05 = lambda v: round(v * 2) / 2
rows, claimed, report = [], {}, []
for u in units:
    kind = u.get("kind", "slider" if u.get("panes", 1) >= 2 else "win")
    cands = [w for w in walls if u["u1"] > w["ulo"] and u["u0"] < w["uhi"] and u["z1"] > w["z0"] + 1 and u["z0"] < w["z1"] - 1]
    if not cands: report.append(f"NO WALL   u {u['u0']}..{u['u1']} z {u['z0']}..{u['z1']}"); continue
    front = max(w["depth"] for w in cands)
    w = max((w for w in cands if front - w["depth"] < 0.5),
            key=lambda w: (w["z0"] <= u["z0"] + 0.5) * 1e4 + min(u["u1"], w["uhi"]) - max(u["u0"], w["ulo"]))
    uc = (u["u0"] + u["u1"]) / 2
    W = float(round(u["u1"] - u["u0"])) if kind in ("door", "french") else r05(u["u1"] - u["u0"])
    lname, lz = level_for(u, w)
    Hh, sill = r05(u["z1"] - u["z0"]), r05(u["z0"] - lz)
    best = max(clusters(w, lname), key=lambda c: min(c[1], u["u1"]) - max(c[0], u["u0"]), default=None)
    pc = best if best and min(best[1], u["u1"]) - max(best[0], u["u0"]) > 6 else None
    if pc: claimed.setdefault((w["id"], lname), set()).add(pc)
    flags = []
    if u["z1"] > w["z1"] + 0.5 or u["z0"] < w["z0"] - 0.5: flags.append("SPANS-WALL-HEIGHT")
    if min(u["u0"] - w["ulo"], w["uhi"] - u["u1"]) < 4: flags.append(f"END-CLEAR {min(u['u0'] - w['ulo'], w['uhi'] - u['u1']):.1f}")
    if pc and abs((pc[0] + pc[1]) / 2 - uc) > 3: flags.append(f"PLAN-OFF {(pc[0] + pc[1]) / 2 - uc:+.1f}")
    fam, tn = FAM[kind]
    rows.append({"fam": fam, "type": tn.format(w=f"{W:g}", h=f"{Hh:g}"), "w": W, "h": Hh, "sill": sill, "wall": w["id"],
                 "level": lname, "uc": round(uc, 3), "kind": kind, "src": "elev", "plan": pc, "flags": flags})
for p in fix.get("plan_only", []):         # openings on faces the elevation can't show, placed from the plan
    w = next(x for x in walls if x["id"] == p["wall"]); fam, tn = FAM[p.get("kind", "win")]
    rows.append({"fam": fam, "type": tn.format(w=f"{p['w']:g}", h=f"{p['h']:g}"), "w": p["w"], "h": p["h"], "sill": p["sill"],
                 "wall": p["wall"], "level": p.get("level", w["level"]), "uc": p["uc"], "kind": p.get("kind", "win"), "src": "plan", "plan": None, "flags": []})
json.dump({"face": FACE, "R": R, "facing": F, "openings": rows}, open(os.path.join(D, f"openings_{FACE}.json"), "w"), indent=0)
for line in report: print(line)
print(f"{FACE}: {len(rows)} openings")
for r in sorted(rows, key=lambda r: (r["level"], r["uc"])):
    print(f"  {r['level'][:5]} {r['kind']:6} wall {r['wall']} uc {r['uc']:9.2f} {r['type']:26} sill {r['sill']:6g}  plan {r['plan']}  {' '.join(r['flags'])}")
print("plan openings no drafted unit claims (hidden faces? place via fix 'plan_only'):")
for w in walls:
    for lname in ([l["name"] for l in levels if w["z0"] - 0.5 <= l["z"] < w["z1"] - 12] or [w["level"]]):
        for c in clusters(w, lname):
            if c not in claimed.get((w["id"], lname), set()) and c[1] - c[0] >= 12: print(f"  wall {w['id']} {lname} u {c[0]}..{c[1]} ({c[1] - c[0]:.2f})")
