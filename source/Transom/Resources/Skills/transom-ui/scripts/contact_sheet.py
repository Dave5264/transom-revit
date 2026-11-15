"""DEMO: one contact sheet of every stage shot, captioned, for the report and for showing someone afterwards.

usage: python contact_sheet.py <shots_dir> [out.png]
Takes every PNG in the folder in name order (the NN_ prefix keeps stage order), crops each to the drawing area
(drops Revit's ribbon and side palettes when the window is 1281 wide), captions it with its name, 3 per row.
"""
import os, sys
from PIL import Image, ImageDraw, ImageFont

d = sys.argv[1]
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(d, "contact_sheet.png")
files = sorted(f for f in os.listdir(d) if f.lower().endswith(".png") and not f.startswith("contact_sheet"))
W, H, PAD, CAP = 620, 470, 12, 30
try:
    font = ImageFont.truetype("segoeui.ttf", 20)
except OSError:
    font = ImageFont.load_default()
cols = 3
rows = (len(files) + cols - 1) // cols
sheet = Image.new("RGB", (cols * (W + PAD) + PAD, rows * (H + CAP + PAD) + PAD), (245, 245, 242))
dr = ImageDraw.Draw(sheet)
for k, f in enumerate(files):
    im = Image.open(os.path.join(d, f)).convert("RGB")
    if im.width >= 1200 and im.height >= 900:            # a Revit or AutoCAD window: keep the canvas
        left = 348 if "cad" not in f else 20
        im = im.crop((left, 160, im.width - 12, im.height - 40))
    im.thumbnail((W, H))
    x = PAD + (k % cols) * (W + PAD); y = PAD + (k // cols) * (H + CAP + PAD)
    sheet.paste(im, (x + (W - im.width) // 2, y + CAP))
    dr.text((x + 4, y + 4), os.path.splitext(f)[0].replace("_", " "), fill=(30, 30, 30), font=font)
sheet.save(out)
print(out, sheet.size, len(files), "shots")
