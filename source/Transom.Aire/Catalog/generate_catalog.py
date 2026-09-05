#!/usr/bin/env python3
"""
Regenerates higgsfield-models.json (the AIRE Video tab's model catalog) from Higgsfield's published
OpenAPI spec, so the catalog never has to be typed by hand and can be refreshed when the vendor ships
or renames models.

    python generate_catalog.py                         # fetch the live spec
    python generate_catalog.py --from hf-openapi.json  # use a saved copy

Only image-to-video endpoints that take a single source image are emitted (plus the higgsfield-ai/dop
models, which are the only ones with camera-motion presets). Text-to-video, first/last-frame and
reference-to-video endpoints are deliberately left out of the tab's dropdown; the request is still
built from the parameter bag in this file, so adding one later is a catalog edit, not a client change.

The generated file is embedded in Transom.Aire.dll. A copy at %AppData%\\Transom\\higgsfield-models.json
overrides it at runtime (that is also where hand-added motion preset ids and names go).
"""
import argparse
import json
import re
import sys
import urllib.request
from datetime import date

SPEC_URL = "https://docs.higgsfield.ai/docs/openapi.json"

# Human labels for the endpoints known on 2026-09-05, in the order the dropdown shows them. Anything the
# spec adds later that the filter accepts gets an automatic label and goes at the end of the list.
LABELS = [
    ("/higgsfield-ai/dop/standard", "Higgsfield DoP Standard", "Higgsfield DoP"),
    ("/higgsfield-ai/dop/lite", "Higgsfield DoP Lite", "Higgsfield DoP"),
    ("/higgsfield-ai/dop/turbo", "Higgsfield DoP Turbo", "Higgsfield DoP"),
    ("/kling-video/v2.5-turbo/pro/image-to-video", "Kling 2.5 Turbo Pro", "Kling"),
    ("/kling-video/v2.5-turbo/standard/image-to-video", "Kling 2.5 Turbo Standard", "Kling"),
    ("/kling-video/v2.1/master/image-to-video", "Kling 2.1 Master", "Kling"),
    ("/kling-video/v2.1/pro/image-to-video", "Kling 2.1 Pro", "Kling"),
    ("/kling-video/v2.1/standard/image-to-video", "Kling 2.1 Standard", "Kling"),
    ("/veo3.1/image-to-video", "Veo 3.1", "Veo"),
    ("/veo3.1/fast/image-to-video", "Veo 3.1 Fast", "Veo"),
    ("/bytedance/seedance/v1/lite/image-to-video", "Seedance 1.0 Lite", "Seedance"),
    ("/bytedance/seedance/v1/pro/fast/image-to-video", "Seedance 1.0 Pro Fast", "Seedance"),
    ("/minimax/hailuo-2.3/pro/image-to-video", "Hailuo 2.3 Pro", "Hailuo"),
    ("/minimax/hailuo-2.3/standard/image-to-video", "Hailuo 2.3 Standard", "Hailuo"),
    ("/minimax/hailuo-2.3-fast/pro/image-to-video", "Hailuo 2.3 Fast Pro", "Hailuo"),
    ("/minimax/hailuo-2.3-fast/standard/image-to-video", "Hailuo 2.3 Fast Standard", "Hailuo"),
    ("/minimax/hailuo-02/pro/image-to-video", "Hailuo 02 Pro", "Hailuo"),
    ("/minimax/hailuo-02/standard/image-to-video", "Hailuo 02 Standard", "Hailuo"),
    ("/sora-2/image-to-video", "Sora 2", "Sora"),
    ("/sora-2/image-to-video/pro", "Sora 2 Pro", "Sora"),
    ("/wan-25-preview/image-to-video", "Wan 2.5 (preview)", "Wan"),
]
ORDER = {path: i for i, (path, _, _) in enumerate(LABELS)}
LABEL = {path: (label, family) for path, label, family in LABELS}

DEFAULT_MODEL = "/higgsfield-ai/dop/standard"


def wanted(path, schema):
    props = schema.get("properties", {})
    if "image_url" not in props:
        return False
    if path.startswith("/higgsfield-ai/dop/"):
        return True
    return "image-to-video" in path


def json_type(prop):
    """The spec is OpenAPI 3.1: 'type' may be a string, a list (nullable), absent (anyOf), or missing
    entirely while an enum carries the values. Reduce all of that to one of the JSON scalar kinds."""
    t = prop.get("type")
    if isinstance(t, list):
        t = next((x for x in t if x != "null"), None)
    if t is None and "anyOf" in prop:
        for alt in prop["anyOf"]:
            at = alt.get("type")
            if at and at != "null":
                t = at
                break
    if t is None and "enum" in prop:
        vals = [v for v in prop["enum"] if v is not None]
        if all(isinstance(v, bool) for v in vals):
            t = "boolean"
        elif all(isinstance(v, int) for v in vals):
            t = "integer"
        elif all(isinstance(v, (int, float)) for v in vals):
            t = "number"
        else:
            t = "string"
    return t or "string"


def enum_of(prop):
    if "enum" in prop:
        return [v for v in prop["enum"] if v is not None]
    if "anyOf" in prop:
        for alt in prop["anyOf"]:
            if "enum" in alt:
                return [v for v in alt["enum"] if v is not None]
    return None


def auto_label(path):
    parts = [p for p in path.strip("/").split("/") if p not in ("image-to-video",)]
    words = []
    for p in parts:
        p = p.replace("higgsfield-ai", "Higgsfield").replace("bytedance", "").replace("minimax", "")
        words += [w for w in re.split(r"[-/]", p) if w]
    label = " ".join(w[:1].upper() + w[1:] for w in words if w)
    return label, (words[0].title() if words else "Other")


def build(spec):
    models = []
    for path, item in spec["paths"].items():
        op = item.get("post")
        if not op:
            continue
        schema = op.get("requestBody", {}).get("content", {}).get("application/json", {}).get("schema", {})
        if not wanted(path, schema):
            continue
        required = set(schema.get("required", []))
        params = []
        for name, prop in schema.get("properties", {}).items():
            if name in ("prompt", "image_url"):
                continue  # always sent; the tab owns these two
            entry = {
                "name": name,
                "title": prop.get("title") or name,
                "type": json_type(prop),
                "required": name in required,
            }
            enum = enum_of(prop)
            if enum is not None:
                entry["enum"] = enum
            if prop.get("default") is not None:
                entry["default"] = prop["default"]
            if "minimum" in prop:
                entry["minimum"] = prop["minimum"]
            if "maximum" in prop:
                entry["maximum"] = prop["maximum"]
            if name == "motions":
                entry["max_items"] = prop.get("maxItems", 2)
            params.append(entry)
        label, family = LABEL.get(path) or auto_label(path)
        motions = next((p for p in params if p["name"] == "motions"), None)
        models.append({
            "path": path,
            "label": label,
            "family": family,
            "image_param": "image_url",
            "supports_motions": motions is not None,
            "max_motions": motions["max_items"] if motions else 0,
            "params": params,
        })
    models.sort(key=lambda m: (ORDER.get(m["path"], 10_000), m["path"]))
    return {
        "_comment": "Generated by source/Transom.Aire/Catalog/generate_catalog.py from Higgsfield's OpenAPI spec. "
                    "Copy this file to %AppData%\\Transom\\higgsfield-models.json to override the built-in catalog, "
                    "e.g. to add motion_presets (id = Higgsfield's preset UUID, name = what the dropdown shows).",
        "generated_from": SPEC_URL,
        "generated_on": date.today().isoformat(),
        "spec_version": spec.get("info", {}).get("version", ""),
        "default_model": DEFAULT_MODEL,
        "motion_presets": [],
        "models": models,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--from", dest="src", help="saved openapi.json instead of fetching")
    ap.add_argument("--out", default="higgsfield-models.json")
    args = ap.parse_args()
    if args.src:
        with open(args.src, encoding="utf-8") as f:
            spec = json.load(f)
    else:
        with urllib.request.urlopen(SPEC_URL, timeout=30) as r:
            spec = json.load(r)
    catalog = build(spec)
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(catalog, f, indent=2)
        f.write("\n")
    print(f"{len(catalog['models'])} models -> {args.out}", file=sys.stderr)


if __name__ == "__main__":
    main()
