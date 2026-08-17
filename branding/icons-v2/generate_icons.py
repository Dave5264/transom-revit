# Transom icons-v2 generator (ux1).
# Matches the existing icon language sampled from RibbonIcon32.png:
#   tile gradient  #2B83C9 (top) -> #13447D (bottom), dark edge #0F3456
#   glyph white    #E9EEF5
#   orange accent  #FFAB20 (highlight #F7C05E)
# Renders at 256px supersample, downscales with LANCZOS to 32 and 16.
# Pillow 8.0.1 compatible (no rounded_rectangle).

import math
import os
from PIL import Image, ImageDraw

OUT = os.path.dirname(os.path.abspath(__file__))
M = 256  # master canvas

TILE_TOP = (43, 131, 201)
TILE_BOT = (19, 68, 125)
TILE_EDGE = (15, 52, 86)
WHITE = (233, 238, 245)
BLUE_TEXT = (33, 100, 162)
ORANGE = (255, 171, 32)
ORANGE_DK = (224, 138, 18)


def rounded_rect_mask(size, box, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    x0, y0, x1, y1 = box
    r = radius
    d.rectangle([x0 + r, y0, x1 - r, y1], fill=255)
    d.rectangle([x0, y0 + r, x1, y1 - r], fill=255)
    d.pieslice([x0, y0, x0 + 2 * r, y0 + 2 * r], 180, 270, fill=255)
    d.pieslice([x1 - 2 * r, y0, x1, y0 + 2 * r], 270, 360, fill=255)
    d.pieslice([x0, y1 - 2 * r, x0 + 2 * r, y1], 90, 180, fill=255)
    d.pieslice([x1 - 2 * r, y1 - 2 * r, x1, y1], 0, 90, fill=255)
    return m


def vertical_gradient(size, top, bot):
    g = Image.new("RGB", (size, size))
    px = g.load()
    for y in range(size):
        t = y / (size - 1)
        c = tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3))
        for x in range(size):
            px[x, y] = c
    return g


def new_tile():
    """Blue gradient rounded tile with subtle dark edge, on transparent canvas."""
    img = Image.new("RGBA", (M, M), (0, 0, 0, 0))
    edge_mask = rounded_rect_mask(M, (6, 6, M - 7, M - 7), 42)
    inner_mask = rounded_rect_mask(M, (11, 11, M - 12, M - 12), 38)
    edge = Image.new("RGBA", (M, M), TILE_EDGE + (255,))
    img.paste(edge, (0, 0), edge_mask)
    grad = vertical_gradient(M, TILE_TOP, TILE_BOT)
    img.paste(grad, (0, 0), inner_mask)
    return img


def paste_color(img, mask, color):
    layer = Image.new("RGBA", (M, M), color + (255,))
    img.paste(layer, (0, 0), mask)


def gear_mask(cx, cy, r_out, r_root, r_hole, teeth=8, tip_half_deg=11.0, root_pad_deg=4.0):
    """L-mask of a flat-tooth gear with a punched hole."""
    m = Image.new("L", (M, M), 0)
    d = ImageDraw.Draw(m)
    pts = []
    step = 360.0 / teeth
    for i in range(teeth):
        a = i * step
        # root arc points before the tooth
        for ang in (a - step / 2 + root_pad_deg, a - tip_half_deg - root_pad_deg):
            pts.append((cx + r_root * math.cos(math.radians(ang)),
                        cy + r_root * math.sin(math.radians(ang))))
        # tooth tip (flat)
        for ang in (a - tip_half_deg, a + tip_half_deg):
            pts.append((cx + r_out * math.cos(math.radians(ang)),
                        cy + r_out * math.sin(math.radians(ang))))
        # root after the tooth
        pts.append((cx + r_root * math.cos(math.radians(a + tip_half_deg + root_pad_deg)),
                    cy + r_root * math.sin(math.radians(a + tip_half_deg + root_pad_deg))))
    d.polygon(pts, fill=255)
    # fill the root circle so arcs between teeth are round, then punch the hole
    d.ellipse([cx - r_root, cy - r_root, cx + r_root, cy + r_root], fill=255)
    d.ellipse([cx - r_hole, cy - r_hole, cx + r_hole, cy + r_hole], fill=0)
    return m


def make_settings(small=False):
    img = new_tile()
    if small:
        gm = gear_mask(128, 128, 96, 66, 28, teeth=8, tip_half_deg=13.0, root_pad_deg=3.0)
    else:
        gm = gear_mask(128, 128, 88, 62, 27, teeth=8, tip_half_deg=11.0, root_pad_deg=4.0)
    paste_color(img, gm, WHITE)
    return img


def sheet(img, box, radius=14):
    sm = rounded_rect_mask(M, box, radius)
    paste_color(img, sm, WHITE)
    return box


def make_revision(small=False):
    img = new_tile()
    if small:
        bx = (44, 28, 178, 208)
        sheet(img, bx, radius=16)
        lines = [(64, 58, 158, 80), (64, 98, 158, 120)]
        tri_out = [(100, 238), (248, 238), (174, 106)]
        tri_in = [(140, 212), (208, 212), (174, 151)]
    else:
        bx = (56, 32, 186, 210)
        sheet(img, bx, radius=14)
        lines = [(76, 60, 166, 74), (76, 92, 166, 106), (76, 124, 130, 138)]
        tri_out = [(124, 224), (240, 224), (182, 122)]
        tri_in = [(151, 206), (213, 206), (182, 152)]
    d = ImageDraw.Draw(img)
    for ln in lines:
        d.rectangle(ln, fill=BLUE_TEXT + (255,))
    # revision delta: solid orange triangle with inner cut for the classic outline look
    mask = Image.new("L", (M, M), 0)
    md = ImageDraw.Draw(mask)
    md.polygon(tri_out, fill=255)
    # dark keyline behind delta so it separates from the white sheet
    edge = Image.new("L", (M, M), 0)
    ed = ImageDraw.Draw(edge)
    cx = sum(p[0] for p in tri_out) / 3.0
    cy = sum(p[1] for p in tri_out) / 3.0
    tri_grow = [(cx + (x - cx) * 1.10, cy + (y - cy) * 1.10) for (x, y) in tri_out]
    ed.polygon(tri_grow, fill=255)
    paste_color(img, edge, TILE_EDGE)
    paste_color(img, mask, ORANGE)
    inner = Image.new("L", (M, M), 0)
    idr = ImageDraw.Draw(inner)
    idr.polygon(tri_in, fill=255)
    paste_color(img, inner, WHITE)
    return img


def make_uiassist(small=False):
    img = new_tile()
    # cursor arrow, white, tip upper-left-of-centre; orange click arcs at the tip
    if small:
        scale, ox, oy = 1.18, 86, 70
        arc_w = 16
    else:
        scale, ox, oy = 1.05, 92, 74
        arc_w = 13
    # classic cursor polygon (unit ~100 tall)
    base = [(0, 0), (0, 100), (22, 80), (38, 114), (56, 106), (40, 73), (70, 70)]
    pts = [(ox + x * scale, oy + y * scale) for (x, y) in base]
    mask = Image.new("L", (M, M), 0)
    md = ImageDraw.Draw(mask)
    md.polygon(pts, fill=255)
    # orange click ripples centred on the cursor tip
    tipx, tipy = ox, oy
    ar = Image.new("L", (M, M), 0)
    ad = ImageDraw.Draw(ar)
    for r in ((46, 70) if not small else (52,)):
        ad.arc([tipx - r, tipy - r, tipx + r, tipy + r], 150, 330, fill=255, width=arc_w)
    paste_color(img, ar, ORANGE)
    # dark keyline so the white cursor pops over the ripples
    edge = Image.new("L", (M, M), 0)
    ed = ImageDraw.Draw(edge)
    cxp = sum(p[0] for p in pts) / len(pts)
    cyp = sum(p[1] for p in pts) / len(pts)
    pts_grow = [(cxp + (x - cxp) * 1.14, cyp + (y - cyp) * 1.14) for (x, y) in pts]
    ed.polygon(pts_grow, fill=255)
    paste_color(img, edge, TILE_EDGE)
    paste_color(img, mask, WHITE)
    return img


def four_point_star(cx, cy, r_out, r_in, rot=0.0):
    pts = []
    for i in range(8):
        ang = math.radians(rot + i * 45)
        r = r_out if i % 2 == 0 else r_in
        pts.append((cx + r * math.sin(ang), cy - r * math.cos(ang)))
    return pts


def make_aire(small=False):
    """AIRE (AI Render Enhancer): white photo frame with sun + mountains, orange AI sparkle top-right."""
    from PIL import ImageChops
    img = new_tile()
    if small:
        frame_out = (36, 56, 196, 196)
        frame_in = (52, 72, 180, 180)
        fr_r, fi_r = 16, 10
        sun = (66, 86, 96, 116)
        mtn1 = [(52, 180), (106, 106), (148, 180)]
        mtn2 = [(118, 180), (156, 130), (180, 180)]
        star_c, star_ro, star_ri = (198, 72), 48, 15
    else:
        frame_out = (40, 62, 192, 192)
        frame_in = (52, 74, 180, 180)
        fr_r, fi_r = 14, 10
        sun = (66, 88, 92, 114)
        mtn1 = [(52, 180), (102, 112), (140, 180)]
        mtn2 = [(114, 180), (152, 134), (180, 180)]
        star_c, star_ro, star_ri = (202, 70), 42, 13
    # white frame with punched interior (interior shows the blue tile)
    frame_mask = rounded_rect_mask(M, frame_out, fr_r)
    inner_mask = rounded_rect_mask(M, frame_in, fi_r)
    paste_color(img, ImageChops.subtract(frame_mask, inner_mask), WHITE)
    # picture contents, clipped to the interior: sun + two mountain peaks
    content = Image.new("L", (M, M), 0)
    cd = ImageDraw.Draw(content)
    cd.ellipse(sun, fill=255)
    cd.polygon(mtn1, fill=255)
    cd.polygon(mtn2, fill=255)
    paste_color(img, ImageChops.multiply(content, inner_mask), WHITE)
    # orange AI sparkle with the house dark keyline, overlapping the frame corner
    edge = Image.new("L", (M, M), 0)
    ed = ImageDraw.Draw(edge)
    ed.polygon(four_point_star(star_c[0], star_c[1], star_ro * 1.14, star_ri * 1.35), fill=255)
    paste_color(img, edge, TILE_EDGE)
    star = Image.new("L", (M, M), 0)
    sd = ImageDraw.Draw(star)
    sd.polygon(four_point_star(star_c[0], star_c[1], star_ro, star_ri), fill=255)
    paste_color(img, star, ORANGE)
    return img


def save(img, name, size):
    out = img.resize((size, size), Image.LANCZOS)
    out.save(os.path.join(OUT, name))
    print("wrote", name)


def save_ico(name, sizes=(16, 24, 32, 48, 64, 128, 256)):
    """
    Multi-size .ico for the standalone AIRE app (Start Menu / taskbar / Alt-Tab, which reach for 256).
    Every frame is rendered from the 256px master rather than upscaled from Aire32.png. The 16 and 24
    frames use the `small` geometry for the same reason Aire16.png does: the full-detail drawing turns
    to mush at that size.
    """
    large = make_aire(False)
    small = make_aire(True)
    # The BASE image must be the 256 master: Pillow's ICO writer silently drops any requested size
    # larger than the base, so passing the 16px frame first yields a one-frame .ico. Every frame is
    # pre-rendered here and handed over via append_images, which Pillow matches up by size.
    frames = [(small if s <= 24 else large).resize((s, s), Image.LANCZOS) for s in sizes]
    large.save(
        os.path.join(OUT, name),
        format="ICO",
        sizes=[(s, s) for s in sizes],
        append_images=frames,
    )
    print("wrote", name, "sizes", ",".join(str(s) for s in sizes))


if __name__ == "__main__":
    save(make_settings(False), "Settings32.png", 32)
    save(make_settings(True), "Settings16.png", 16)
    save(make_revision(False), "RevisionNarrative32.png", 32)
    save(make_revision(True), "RevisionNarrative16.png", 16)
    save(make_uiassist(False), "UiAssist32.png", 32)
    save(make_uiassist(True), "UiAssist16.png", 16)
    save(make_aire(False), "Aire32.png", 32)
    save(make_aire(True), "Aire16.png", 16)
    save_ico("Aire.ico")
