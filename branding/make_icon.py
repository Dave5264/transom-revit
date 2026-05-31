"""Render the Transom add-in icon at multiple sizes.
Drawn large (1024) and downscaled with LANCZOS for crisp small-size output.
"""
import math, os
from PIL import Image, ImageDraw

OUT = os.path.dirname(os.path.abspath(__file__))
S = 1024  # master render size

# palette
BLUE_TOP = (42, 127, 193)
BLUE_BOT = (16, 58, 110)
WHITE = (255, 255, 255)
AMBER = (244, 167, 42)
LINES = (197, 212, 228)
NAVY = (14, 45, 86)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def vgradient(size, top, bot):
    img = Image.new("RGB", (size, size), top)
    px = img.load()
    for y in range(size):
        c = lerp(top, bot, y / (size - 1))
        for x in range(size):
            px[x, y] = c
    return img


def rounded_mask(size, inset, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([inset, inset, size - inset, size - inset], radius=radius, fill=255)
    return m


def arc_arrow(d, cx, cy, R, start, end, width, color, headlen, headw):
    """Thick arc from start->end degrees (clockwise, 0=east, y down) + arrowhead at end."""
    bbox = [cx - R, cy - R, cx + R, cy + R]
    d.arc(bbox, start, end, fill=color, width=width)
    th = math.radians(end)
    px, py = cx + R * math.cos(th), cy + R * math.sin(th)
    # clockwise tangent direction (increasing angle, y down)
    dx, dy = -math.sin(th), math.cos(th)
    # perpendicular
    nx, ny = -dy, dx
    tip = (px + dx * headlen, py + dy * headlen)
    b1 = (px + nx * headw - dx * headlen * 0.2, py + ny * headw - dy * headlen * 0.2)
    b2 = (px - nx * headw - dx * headlen * 0.2, py - ny * headw - dy * headlen * 0.2)
    d.polygon([tip, b1, b2], fill=color)


def draw(size):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    # tile (blue gradient, rounded)
    grad = vgradient(size, BLUE_TOP, BLUE_BOT).convert("RGBA")
    mask = rounded_mask(size, int(size * 0.05), int(size * 0.22))
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)

    # schedule grid panel (upper-left), white rounded
    gx0, gy0, gx1, gy1 = int(size*0.15), int(size*0.17), int(size*0.66), int(size*0.62)
    rad = int(size * 0.045)
    d.rounded_rectangle([gx0, gy0, gx1, gy1], radius=rad, fill=WHITE)
    # header row (amber)
    hh = int((gy1 - gy0) * 0.26)
    hdr = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hdr)
    hd.rounded_rectangle([gx0, gy0, gx1, gy0 + hh + rad], radius=rad, fill=AMBER)
    hd.rectangle([gx0, gy0 + hh, gx1, gy0 + hh + rad], fill=AMBER)
    # clip header to panel rounded shape
    pm = Image.new("L", (size, size), 0)
    ImageDraw.Draw(pm).rounded_rectangle([gx0, gy0, gx1, gy1], radius=rad, fill=255)
    img.paste(hdr, (0, 0), Image.composite(pm, Image.new("L", (size, size), 0), hdr.split()[3]))
    d = ImageDraw.Draw(img)
    # grid lines
    lw = max(2, int(size * 0.012))
    for i in (1, 2):  # vertical
        x = gx0 + (gx1 - gx0) * i / 3
        d.line([(x, gy0 + hh), (x, gy1)], fill=LINES, width=lw)
    rows = 3
    for j in range(1, rows):  # horizontal (below header)
        y = gy0 + hh + (gy1 - (gy0 + hh)) * j / rows
        d.line([(gx0, y), (gx1, y)], fill=LINES, width=lw)

    # round-trip sync arrows (lower-right), amber with white halo
    cx, cy, R = int(size * 0.685), int(size * 0.705), int(size * 0.145)
    sw = max(3, int(size * 0.052))
    hl, hw = size * 0.085, size * 0.07
    # white halo
    arc_arrow(d, cx, cy, R, 28, 168, sw + max(4, int(size*0.02)), WHITE, hl*1.05, hw*1.05)
    arc_arrow(d, cx, cy, R, 208, 348, sw + max(4, int(size*0.02)), WHITE, hl*1.05, hw*1.05)
    # amber arrows
    arc_arrow(d, cx, cy, R, 28, 168, sw, AMBER, hl, hw)
    arc_arrow(d, cx, cy, R, 208, 348, sw, AMBER, hl, hw)

    return img.resize((size, size), Image.LANCZOS)


master = draw(S)
master.save(os.path.join(OUT, "icon_preview_256.png"))  # placeholder, resized below
for px in (256, 64, 48, 32, 16):
    master.resize((px, px), Image.LANCZOS).save(os.path.join(OUT, f"icon{px}.png"))
master.resize((512, 512), Image.LANCZOS).save(os.path.join(OUT, "icon_preview_256.png"))
print("wrote icons to", OUT)
