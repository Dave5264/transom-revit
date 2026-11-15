"""Compare two inventory.cs outputs (a demo run against the reference result).

usage: python compare_inventory.py <reference_inventory.json> <run_inventory.json>
Prints the levels of both and every kind whose count differs. On the first replay (2026-11-11) the only differences were
expected ones: detail items, annotations, a title block and old CAD imports that came with the copied office project, the
superseded toposolids on the hidden OLD workset, and (before the fascia stage existed) the roof fascias.
"""
import json, sys

a = json.load(open(sys.argv[1], encoding="utf-8"))
b = json.load(open(sys.argv[2], encoding="utf-8"))
print("levels  reference:", *a.get("levels", []), sep="\n  ")
print("levels  run:", *b.get("levels", []), sep="\n  ")
ka, kb = a["counts"], b["counts"]
keys = sorted(set(ka) | set(kb))
diff = [(k, ka.get(k, 0), kb.get(k, 0)) for k in keys if ka.get(k, 0) != kb.get(k, 0)]
print(f"\n{len(keys) - len(diff)} kinds identical, {len(diff)} differ")
for k, x, y in diff:
    print(f"  {k}: reference {x}, run {y}")
