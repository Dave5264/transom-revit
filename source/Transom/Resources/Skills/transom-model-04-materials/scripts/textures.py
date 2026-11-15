"""Texture maps for the placeholder materials: copy, measure, correct, and write the manifest the materials read.

usage:
  python textures.py copy  <library_dir> <textures_dir> <file> [<file> ...]   copy maps out of the Autodesk material library
  python textures.py check <textures_dir>                                      mean brightness (0-255) of every image
  python textures.py darken <src> <dst> <target_mean>                          scale a too-light colour map to a mean
  python textures.py tone   <src> <dst> <lo> <hi>                              stretch a too-dark map into lo..hi
  python textures.py bump   <src> <dst>                                       a bump map from a colour map (grey, contrast)
  python textures.py manifest <textures_dir> <manifest.json>                   start textures.json: key -> color/bump/size

Why (first project): the library asphalt map averages 171/255 and renders like concrete, so a darkened copy ("SA Asphalt -
fresh dark", mean ~70) was used; the stockade fence map averages 58 and renders black, so it was tone-mapped into a cedar
range ("SA Fence - cedar stockade light"); the fieldstone had no bump map, so one was made from its colour image. Measure
EVERY map before trusting it. Keep the copies in the project's Textures folder next to textures.json, so the model carries
its maps to the machine that renders (Enscape: Options > Rendering > Additional Render Appearance Paths if paths break).
The library on the modeling machine: C:\\Program Files\\Common Files\\Autodesk Shared\\Materials\\Textures (1\\Mats, 3\\Mats).
"""
import json, os, shutil, sys
from PIL import Image, ImageOps, ImageStat

IMG = (".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff")


def mean(p):
    return ImageStat.Stat(Image.open(p).convert("L")).mean[0]


def main(a):
    cmd = a[0]
    if cmd == "copy":
        lib, dst, files = a[1], a[2], a[3:]
        os.makedirs(dst, exist_ok=True)
        for f in files:
            hits = [os.path.join(r, f) for r, _, fs in os.walk(lib) if f in fs]
            if not hits:
                print("not found:", f); continue
            shutil.copy2(hits[0], os.path.join(dst, f)); print("copied", hits[0])
    elif cmd == "check":
        for f in sorted(os.listdir(a[1])):
            if f.lower().endswith(IMG):
                m = mean(os.path.join(a[1], f))
                flag = "  TOO LIGHT for a dark surface?" if m > 160 else ("  TOO DARK, renders black?" if m < 70 else "")
                print(f"{m:6.1f}  {f}{flag}")
    elif cmd == "darken":
        im = Image.open(a[1]).convert("RGB"); k = float(a[3]) / mean(a[1])
        im.point(lambda v: max(0, min(255, int(v * k)))).save(a[2]); print(f"{a[2]}: mean {mean(a[2]):.1f}")
    elif cmd == "tone":
        lo, hi = float(a[3]), float(a[4]); im = Image.open(a[1]).convert("RGB")
        im = ImageOps.autocontrast(im)
        im.point(lambda v: int(lo + (hi - lo) * v / 255)).save(a[2]); print(f"{a[2]}: mean {mean(a[2]):.1f}")
    elif cmd == "bump":
        im = ImageOps.autocontrast(Image.open(a[1]).convert("L"), cutoff=2); im.save(a[2]); print("bump", a[2])
    elif cmd == "manifest":
        d = a[1]; out = {}
        for f in sorted(os.listdir(d)):
            if f.lower().endswith(IMG) and "bump" not in f.lower():
                key = os.path.splitext(f)[0]
                bump = next((b for b in os.listdir(d) if b.lower().startswith(key.lower()) and "bump" in b.lower()), None)
                out[key] = {"width_in": 48, "height_in": 48, "color": os.path.join(d, f)}
                if bump:
                    out[key]["bump"] = os.path.join(d, bump)
        json.dump(out, open(a[2], "w"), indent=1); print(len(out), "maps ->", a[2], "(set each real-world size)")
    else:
        print(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:] or ["help"])
