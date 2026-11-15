"""Reconcile per-level exterior faces into one set of wall planes and continuous wall runs.
usage: python planes.py DATA_DIR OUT_DIR LOWER=Stone MAIN=Siding UPPER=Siding [--gov MAIN] [--tol 2.0]
Levels bottom up, each with its wall material. Reads faces_<LEVEL>.json (records with f = x0,y0,x1,y1,nx,ny in inches).
Writes planes.json (every plane and what moved) and runs.json (the walls to build)."""
import json, os, sys
from collections import Counter, defaultdict

args = sys.argv[1:]
DATA, OUT = args[0], args[1]
LV = [a.split("=") for a in args[2:] if "=" in a and not a.startswith("--")]
LEVELS = [l for l, _ in LV]
MAT = dict(LV)
GOV = args[args.index("--gov") + 1] if "--gov" in args else LEVELS[len(LEVELS) // 2]
TOL_ARG = float(args[args.index("--tol") + 1]) if "--tol" in args else None
MIN_OVERLAP, MAX_GAP, MAX_STEP = 12.0, 12.0, 14.0

F = []
for lv in LEVELS:
    raw = json.load(open(os.path.join(DATA, f"faces_{lv}.json")))
    for i, r in enumerate(raw):
        v = r["f"] if isinstance(r, dict) else r
        x0, y0, x1, y1, nx, ny = [float(a) for a in v[:6]]
        if abs(nx) > 0.5:
            F.append(dict(level=lv, axis="X", c=x0, a=min(y0, y1), b=max(y0, y1), s=1 if nx > 0 else -1, i=i))
        else:
            F.append(dict(level=lv, axis="Y", c=y0, a=min(x0, x1), b=max(x0, x1), s=1 if ny > 0 else -1, i=i))
for k, f in enumerate(F):
    f["k"] = k


def overlap(p, q):
    return min(p["b"], q["b"]) - max(p["a"], q["a"])


def same_run(p, q):
    return p["axis"] == q["axis"] and p["s"] == q["s"]


# 1. Offsets between stacked faces: find the natural break between drafting scatter and real steps
pairs = []
for lo, hi in zip(LEVELS, LEVELS[1:]):
    for p in F:
        if p["level"] != hi:
            continue
        for q in F:
            if q["level"] == lo and same_run(p, q) and overlap(p, q) >= MIN_OVERLAP and abs(p["c"] - q["c"]) <= MAX_STEP:
                pairs.append((abs(p["c"] - q["c"]), hi, lo, p, q))
vals = sorted({round(v[0], 3) for v in pairs if v[0] >= 1 / 32})
if TOL_ARG is not None:
    TOL = TOL_ARG
else:
    TOL = 2.0
    if len(vals) > 1:
        gaps = [(vals[i + 1] - vals[i], vals[i], vals[i + 1]) for i in range(len(vals) - 1)]
        g = max(gaps)
        TOL = (int(g[1] * 4) + 1) / 4  # round the top of the scatter up to the next 1/4"
print(f"stack offsets: {vals}")
if len(vals) > 1:
    print(f"largest break: {g[1]} to {g[2]} in. Tolerance {TOL} in. A wide, clean break means scatter and real steps separate. A narrow one: ask the user.")

# 2. Connect faces that are one plane: same facing, within tolerance, and overlapping or collinear neighbours
par = list(range(len(F)))


def find(a):
    while par[a] != a:
        par[a] = par[par[a]]
        a = par[a]
    return a


for p in F:
    for q in F:
        if q["k"] <= p["k"] or not same_run(p, q) or abs(p["c"] - q["c"]) > TOL:
            continue
        if overlap(p, q) >= MIN_OVERLAP or (p["level"] == q["level"] and -MAX_GAP <= overlap(p, q)):
            par[find(p["k"])] = find(q["k"])
groups = defaultdict(list)
for f in F:
    groups[find(f["k"])].append(f)

# 3. One governing coordinate per plane: the drawn line carrying the most length, governing level winning ties
planes = []
for g in groups.values():
    w = Counter()
    for f in g:
        w[round(f["c"] * 32) / 32] += f["b"] - f["a"] + (0.001 if f["level"] == GOV else 0)
    c = w.most_common(1)[0][0]
    pid = len(planes)
    for f in g:
        f["pc"], f["plane"] = c, pid
    planes.append(dict(id=pid, axis=g[0]["axis"], facing=g[0]["s"], c=c,
                       spread=round(max(f["c"] for f in g) - min(f["c"] for f in g), 3),
                       members=[dict(level=f["level"], drawn=f["c"], moved=round(c - f["c"], 3), a=f["a"], b=f["b"]) for f in g]))
drift = [p for p in planes if p["spread"] > TOL]
print(f"planes: {len(planes)} from {len(F)} faces. Spread over tolerance: {len(drift)}")
for lv in LEVELS:
    fs = [f for f in F if f["level"] == lv]
    print(f"  {lv}: {len({f['plane'] for f in fs})} planes, largest move {max(abs(f['pc'] - f['c']) for f in fs):.3f} in")
n0 = sum(1 for v in pairs if v[0] < 1 / 32)
after = [(abs(v[3]["pc"] - v[4]["pc"]), v) for v in pairs]
print(f"stacked pairs: {len(pairs)}. Coplanar before {n0}, after {sum(1 for d, _ in after if d < 1 / 32)}")

# 4. Real steps need a second drawing to confirm them
print("REAL STEPS to confirm against an elevation or section:")
steps = []
seen = set()
for d, v in sorted(after, key=lambda t: -t[0]):
    if d < 1 / 32:
        continue
    key = tuple(sorted((v[3]["plane"], v[4]["plane"])))
    if key in seen:
        continue
    seen.add(key)
    steps.append(dict(step=round(d, 3), axis=v[3]["axis"], upper=v[1], lower=v[2], upper_c=v[3]["pc"], lower_c=v[4]["pc"], along=[max(v[3]["a"], v[4]["a"]), min(v[3]["b"], v[4]["b"])]))
    print(f"  {d:6.3f} in  {v[3]['axis']}: {v[1]} at {v[3]['pc']} over {v[2]} at {v[4]['pc']}, along {steps[-1]['along']}")

# 5. Close each level's loop on the planes: a face end takes the plane of the perpendicular face that met it
open_ends = 0
for lv in LEVELS:
    fs = [f for f in F if f["level"] == lv]
    for f in fs:
        for end in ("a", "b"):
            P = (f["c"], f[end]) if f["axis"] == "X" else (f[end], f["c"])
            best = None
            for q in fs:
                if q["axis"] == f["axis"]:
                    continue
                for qe in ("a", "b"):
                    Q = (q["c"], q[qe]) if q["axis"] == "X" else (q[qe], q["c"])
                    d = max(abs(P[0] - Q[0]), abs(P[1] - Q[1]))
                    if best is None or d < best[0]:
                        best = (d, q)
            if best and best[0] <= 2.0:
                f["p" + end] = best[1]["pc"]
                f["n" + end] = best[1]["s"]     # the neighbour's facing sets where this wall's centerline must stop
            else:
                f["p" + end] = f[end]
                f["n" + end] = 0
                open_ends += 1
print(f"open ends after snapping: {open_ends} (0 expected; anything else is a hole in the plan loop)")

# 6. Continuous runs: per plane and material, one wall per stretch with the same contiguous level band
runs = []
for p in planes:
    fs = [f for f in F if f["plane"] == p["id"]]
    for mat in sorted({MAT[f["level"]] for f in fs}):
        m = [f for f in fs if MAT[f["level"]] == mat]
        cuts = sorted({v for f in m for v in (f["pa"], f["pb"])})
        nbr = {}
        for f in m:
            nbr[f["pa"]] = f["na"]
            nbr[f["pb"]] = f["nb"]
        spans = []
        for u0, u1 in zip(cuts, cuts[1:]):
            if u1 - u0 < 0.01:
                continue
            mid = (u0 + u1) / 2
            have = sorted({LEVELS.index(f["level"]) for f in m if min(f["pa"], f["pb"]) <= mid <= max(f["pa"], f["pb"])})
            if not have:
                continue
            blocks, cur = [], [have[0]]
            for i in have[1:]:
                if i == cur[-1] + 1:
                    cur.append(i)
                else:
                    blocks.append(cur)
                    cur = [i]
            blocks.append(cur)
            for b in blocks:
                if spans and spans[-1]["u1"] == u0 and spans[-1]["base"] == LEVELS[b[0]] and spans[-1]["top"] == LEVELS[b[-1]]:
                    spans[-1]["u1"] = u1
                else:
                    spans.append(dict(plane=p["id"], axis=p["axis"], facing=p["facing"], c=p["c"], u0=u0, u1=u1, base=LEVELS[b[0]], top=LEVELS[b[-1]], material=mat))
        for r in spans:
            r["n0"], r["n1"] = nbr.get(r["u0"], 0), nbr.get(r["u1"], 0)
        # Where two runs continue along a plane, the corner belongs to the taller one. A mitre at an inside corner stops its face at the plane, so
        # the shorter run must end on the plane, not on the neighbour's centerline, or a slot as wide as half a wall shows between them.
        band = lambda r: LEVELS.index(r["top"]) - LEVELS.index(r["base"])
        for r in spans:
            for q in spans:
                if q is not r and band(q) > band(r):
                    if abs(q["u1"] - r["u0"]) < 0.01:
                        r["n0"] = 0
                    if abs(q["u0"] - r["u1"]) < 0.01:
                        r["n1"] = 0
        runs += spans
print(f"continuous runs: {len(runs)} (was {len(F)} per-level walls)")
for mat, n in Counter(r["material"] for r in runs).items():
    print(f"  {mat}: {n} runs from {sum(1 for f in F if MAT[f['level']] == mat)} faces")
print("  height bands:", Counter((r["base"], r["top"]) for r in runs).most_common())
loops = {lv: [dict(axis=f["axis"], c=f["pc"], a=f["pa"], b=f["pb"], facing=f["s"]) for f in F if f["level"] == lv] for lv in LEVELS}
json.dump(loops, open(os.path.join(OUT, "loops.json"), "w"), indent=0)
short = [r for r in runs if r["u1"] - r["u0"] < 12]
print(f"runs under 12in, check each is real: {len(short)}")
for r in short:
    print(f"  {r['axis']} {r['c']} u {r['u0']}..{r['u1']} {r['base']}-{r['top']} {r['material']}")
os.makedirs(OUT, exist_ok=True)
json.dump(dict(tol=TOL, governing=GOV, planes=planes, steps=steps), open(os.path.join(OUT, "planes.json"), "w"), indent=0)
json.dump(runs, open(os.path.join(OUT, "runs.json"), "w"), indent=0)
