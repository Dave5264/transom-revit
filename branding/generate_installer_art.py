# Transom installer art generator (code3, 2026-06-20) — BIM / cyber / slick.
# Produces the 3 WiX (WixUI_FeatureTree) assets, replacing the off-brand red-bird art:
#   BackgroundImage.png  493x312  (Welcome/Finish splash panel)
#   BannerImage.png      493x58   (interior page top strip)
#   ShellIcon.ico        multi-res (16/32/48/64/128/256)  program icon
# Brand language matches branding/icons-v2/generate_icons.py:
#   tile gradient #2B83C9 -> #13447D, dark edge #0F3456, glyph #E9EEF5, orange accent #FFAB20
# Aesthetic: dark navy backdrop + blueprint grid + glowing cyan data-grid + spreadsheet/round-trip mark + "Transom".
# Pillow 8.0.1 / Python 3.8 compatible (supersample + LANCZOS; no rounded_rectangle()).

import math
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

OUT_ICONS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "install", "Resources", "Icons")
OUT_ICONS = os.path.abspath(OUT_ICONS)
PREVIEW = os.path.join(os.path.dirname(os.path.abspath(__file__)), "installer_art_preview.png")

# palette
TILE_TOP = (43, 131, 201)
TILE_BOT = (19, 68, 125)
TILE_EDGE = (15, 52, 86)
WHITE = (233, 238, 245)
ORANGE = (255, 171, 32)
ORANGE_DK = (224, 138, 18)
BG_TOP = (12, 22, 40)      # dark navy
BG_BOT = (5, 11, 22)       # near-black
GRID = (40, 86, 140)       # blueprint grid line
GRID_HI = (64, 150, 220)   # brighter grid
CYAN = (90, 200, 255)      # electric edge glow


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def rounded_rect_mask(size, box, radius):
    w, h = size
    m = Image.new("L", (w, h), 0)
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


def vgrad(w, h, top, bot):
    g = Image.new("RGB", (w, h))
    px = g.load()
    for y in range(h):
        c = lerp(top, bot, y / max(1, h - 1))
        for x in range(w):
            px[x, y] = c
    return g


def dark_backdrop(w, h, vignette=True):
    """Dark navy gradient + faint blueprint grid + a soft cyan glow upper-left."""
    img = vgrad(w, h, BG_TOP, BG_BOT).convert("RGBA")
    d = ImageDraw.Draw(img, "RGBA")
    step = max(18, h // 9)
    for x in range(0, w, step):
        d.line([(x, 0), (x, h)], fill=GRID + (38,), width=1)
    for y in range(0, h, step):
        d.line([(0, y), (w, y)], fill=GRID + (38,), width=1)
    # a few brighter "data" gridlines
    for x in range(0, w, step * 3):
        d.line([(x, 0), (x, h)], fill=GRID_HI + (60,), width=1)
    # soft radial glow upper-left
    glow = Image.new("L", (w, h), 0)
    gd = ImageDraw.Draw(glow)
    gx, gy, gr = int(w * 0.16), int(h * 0.30), int(h * 0.9)
    gd.ellipse([gx - gr, gy - gr, gx + gr, gy + gr], fill=70)
    glow = glow.filter(ImageFilter.GaussianBlur(gr // 3))
    img = Image.composite(Image.new("RGBA", (w, h), CYAN + (255,)),
                          img, glow.point(lambda v: int(v * 0.5)))
    if vignette:
        vg = Image.new("L", (w, h), 0)
        vd = ImageDraw.Draw(vg)
        vd.rectangle([0, 0, w, h], fill=130)
        vd.ellipse([-w // 4, -h // 4, w + w // 4, h + h // 4], fill=0)
        vg = vg.filter(ImageFilter.GaussianBlur(min(w, h) // 8))
        img = Image.composite(Image.new("RGBA", (w, h), (0, 0, 0, 255)), img, vg)
    return img


def make_mark(S):
    """The Transom mark on a transparent SxS canvas: blue rounded tile w/ cyan glow edge,
    white spreadsheet (orange header row) + orange round-trip arrows. Supersampled."""
    ss = 4
    M = S * ss
    img = Image.new("RGBA", (M, M), (0, 0, 0, 0))
    pad = int(M * 0.06)
    box = (pad, pad, M - pad - 1, M - pad - 1)
    rad = int(M * 0.20)
    # outer cyan glow
    glow = Image.new("L", (M, M), 0)
    ImageDraw.Draw(glow).rounded_rectangle = None  # not available in 8.0.1
    gm = rounded_rect_mask((M, M), (pad - int(M*0.02), pad - int(M*0.02),
                                    M - pad - 1 + int(M*0.02), M - pad - 1 + int(M*0.02)), rad)
    glow = gm.filter(ImageFilter.GaussianBlur(int(M * 0.03)))
    img = Image.composite(Image.new("RGBA", (M, M), CYAN + (255,)), img, glow.point(lambda v: int(v*0.55)))
    # tile edge + gradient face
    edge_mask = rounded_rect_mask((M, M), box, rad)
    inner = (pad + int(M*0.02), pad + int(M*0.02), M - pad - 1 - int(M*0.02), M - pad - 1 - int(M*0.02))
    inner_mask = rounded_rect_mask((M, M), inner, int(rad*0.9))
    img.paste(Image.new("RGBA", (M, M), TILE_EDGE + (255,)), (0, 0), edge_mask)
    grad = vgrad(M, M, TILE_TOP, TILE_BOT).convert("RGBA")
    img.paste(grad, (0, 0), inner_mask)
    # spreadsheet sheet (white) with orange header row, upper-left area
    d = ImageDraw.Draw(img, "RGBA")
    sx0, sy0, sx1, sy1 = int(M*0.26), int(M*0.24), int(M*0.70), int(M*0.60)
    sm = rounded_rect_mask((M, M), (sx0, sy0, sx1, sy1), int(M*0.03))
    img.paste(Image.new("RGBA", (M, M), WHITE + (255,)), (0, 0), sm)
    hh = int((sy1 - sy0) * 0.26)
    hdr = rounded_rect_mask((M, M), (sx0, sy0, sx1, sy0 + hh), int(M*0.03))
    img.paste(Image.new("RGBA", (M, M), ORANGE + (255,)), (0, 0), hdr)
    # square off header bottom corners
    d.rectangle([sx0, sy0 + hh - int(M*0.03), sx1, sy0 + hh], fill=ORANGE + (255,))
    # grid lines on the sheet
    gl = lerp(TILE_TOP, TILE_BOT, 0.2)
    cols = 3
    rows = 3
    for i in range(1, cols):
        x = sx0 + (sx1 - sx0) * i // cols
        d.line([(x, sy0), (x, sy1)], fill=gl + (255,), width=max(1, ss))
    for j in range(1, rows + 1):
        y = sy0 + hh + (sy1 - sy0 - hh) * j // rows
        if y < sy1:
            d.line([(sx0, y), (sx1, y)], fill=gl + (255,), width=max(1, ss))
    # round-trip arrows (orange) lower-right, two curved arrows forming a cycle
    acx, acy, ar = int(M*0.60), int(M*0.68), int(M*0.16)
    aw = max(2, int(M*0.045))
    # keyline behind arrows so they read on the tile
    kl = Image.new("L", (M, M), 0)
    kd = ImageDraw.Draw(kl)
    kd.arc([acx-ar, acy-ar, acx+ar, acy+ar], 20, 200, fill=255, width=aw + ss*2)
    kd.arc([acx-ar, acy-ar, acx+ar, acy+ar], 200, 380, fill=255, width=aw + ss*2)
    img = Image.composite(Image.new("RGBA", (M, M), TILE_EDGE + (255,)), img, kl)
    d = ImageDraw.Draw(img, "RGBA")
    d.arc([acx-ar, acy-ar, acx+ar, acy+ar], 25, 195, fill=ORANGE + (255,), width=aw)
    d.arc([acx-ar, acy-ar, acx+ar, acy+ar], 205, 375, fill=ORANGE + (255,), width=aw)
    # arrowheads
    def head(angle_deg, tip_out=True):
        a = math.radians(angle_deg)
        hx, hy = acx + ar * math.cos(a), acy + ar * math.sin(a)
        s = int(M * 0.05)
        perp = a + math.pi / 2
        p1 = (hx + s*math.cos(a-0.5), hy + s*math.sin(a-0.5))
        p2 = (hx + s*math.cos(a+0.5), hy + s*math.sin(a+0.5))
        d.polygon([(hx, hy),
                   (hx - s*1.2*math.cos(a) + s*0.7*math.cos(perp), hy - s*1.2*math.sin(a) + s*0.7*math.sin(perp)),
                   (hx - s*1.2*math.cos(a) - s*0.7*math.cos(perp), hy - s*1.2*math.sin(a) - s*0.7*math.sin(perp))],
                  fill=ORANGE + (255,))
    head(25)
    head(205)
    return img.resize((S, S), Image.LANCZOS)


def load_font(size, bold=True):
    candidates = [
        r"C:\Windows\Fonts\segoeuib.ttf" if bold else r"C:\Windows\Fonts\segoeui.ttf",
        r"C:\Windows\Fonts\seguisb.ttf",
        r"C:\Windows\Fonts\arialbd.ttf" if bold else r"C:\Windows\Fonts\arial.ttf",
        r"C:\Windows\Fonts\Verdana.ttf",
    ]
    for c in candidates:
        if os.path.exists(c):
            try:
                return ImageFont.truetype(c, size)
            except Exception:
                pass
    return ImageFont.load_default()


def text_size(d, txt, font):
    try:
        b = d.textbbox((0, 0), txt, font=font)
        return b[2] - b[0], b[3] - b[1], b[1]
    except Exception:
        w, h = d.textsize(txt, font=font)
        return w, h, 0


def glow_text(img, xy, txt, font, fill, glow=CYAN, gblur=6, ga=0.6):
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ld = ImageDraw.Draw(layer)
    ld.text(xy, txt, font=font, fill=glow + (255,))
    layer = layer.filter(ImageFilter.GaussianBlur(gblur))
    a = layer.split()[3].point(lambda v: int(v * ga))
    layer.putalpha(a)
    img.alpha_composite(layer)
    ImageDraw.Draw(img, "RGBA").text(xy, txt, font=font, fill=fill + (255,))


# --- light text-zone palette (WixUI draws FIXED BLACK title+body text, so the zones
#     under that text MUST be light or the text is unreadable — the bug this fixes) ---
LIGHT_TOP = (244, 248, 252)   # near-white, faint cool tint
LIGHT_BOT = (214, 228, 242)   # light blueprint blue
GRID_LT = (176, 200, 224)     # subtle grid on the light field


def light_panel(w, h):
    """A LIGHT blueprint field for the WixUI text zones: white→light-blue gradient + a
    faint grid so it still reads on-brand (BIM/blueprint) while keeping black text legible."""
    img = vgrad(w, h, LIGHT_TOP, LIGHT_BOT).convert("RGBA")
    d = ImageDraw.Draw(img, "RGBA")
    step = max(20, h // 9)
    for x in range(0, w, step):
        d.line([(x, 0), (x, h)], fill=GRID_LT + (60,), width=1)
    for y in range(0, h, step):
        d.line([(0, y), (w, y)], fill=GRID_LT + (60,), width=1)
    return img


def make_background():
    # WixUI_FeatureTree WelcomeDlg/ExitDlg: 493x312 dialog bitmap. Windows Installer draws the
    # dialog TITLE (top) + BODY text (upper area) in FIXED BLACK, positioned over the RIGHT ~2/3
    # of the bitmap (x ≳ 165). So the RIGHT region must be LIGHT (black text legible); the slick
    # dark art lives in a LEFT graphic band that the text never overlaps. (Bug fix 2026-06-22:
    # the old full-dark image made the black Welcome title+body unreadable.)
    W, H = 493, 312
    BAND = 164                       # left dark graphic band; WixUI text starts to its right
    img = light_panel(W, H)          # light field everywhere (so all text reads)
    # left dark art band
    band = dark_backdrop(BAND, H)
    img.alpha_composite(band, (0, 0))
    # crisp seam: orange keyline between the dark band and the light text field
    ImageDraw.Draw(img, "RGBA").line([(BAND, 0), (BAND, H)], fill=ORANGE + (255,), width=3)
    # the Transom mark, centered in the dark band
    msz = 116
    mark = make_mark(msz)
    img.alpha_composite(mark, ((BAND - msz)//2, (H - msz)//2 - 34))
    # "Transom" wordmark UNDER the mark, inside the dark band (white-on-dark, glowing)
    d = ImageDraw.Draw(img, "RGBA")
    tsize = 40
    while tsize > 20:
        f_title = load_font(tsize, bold=True)
        tw, th, toff = text_size(d, "Transom", f_title)
        if tw <= BAND - 16:
            break
        tsize -= 2
    ty = (H - msz)//2 - 34 + msz + 14
    glow_text(img, ((BAND - tw)//2, ty - toff), "Transom", f_title, WHITE, glow=CYAN, gblur=7, ga=0.55)
    uy = ty - toff + th + 8
    ImageDraw.Draw(img, "RGBA").line([((BAND - tw)//2, uy), ((BAND - tw)//2 + tw, uy)], fill=ORANGE + (255,), width=3)
    # tagline (white-on-dark) under the underline, inside the band
    d = ImageDraw.Draw(img, "RGBA")
    f_sub = load_font(13, bold=False)
    sub_col = (170, 200, 230, 255)
    line1 = "Revit schedules"
    line2 = "↔ Excel"
    s1w, s1h, s1o = text_size(d, line1, f_sub)
    sy = uy + 9
    d.text(((BAND - s1w)//2, sy - s1o), line1, font=f_sub, fill=sub_col)
    # drawn double-arrow + "Excel" on the next line
    s2w, s2h, s2o = text_size(d, "Excel", f_sub)
    arrlen = 22
    grp_w = arrlen + 8 + s2w
    gx = (BAND - grp_w)//2
    ay = sy + s1h + 8
    d.line([(gx, ay), (gx + arrlen, ay)], fill=ORANGE + (255,), width=2)
    d.polygon([(gx + arrlen, ay), (gx + arrlen - 6, ay - 4), (gx + arrlen - 6, ay + 4)], fill=ORANGE + (255,))
    d.polygon([(gx, ay), (gx + 6, ay - 4), (gx + 6, ay + 4)], fill=ORANGE + (255,))
    d.text((gx + arrlen + 8, ay - s2h//2 - s2o), "Excel", font=f_sub, fill=sub_col)
    return img


def make_banner():
    # WixUI interior dialogs: 493x58 top banner. Windows Installer draws the dialog TITLE +
    # SUBTITLE in FIXED BLACK in the TOP-LEFT of this strip. So the LEFT region must be LIGHT
    # (black text legible); the dark art + mark live in a RIGHT band the text never reaches.
    # (Bug fix 2026-06-22: the old full-dark banner made "Custom Setup"/"Ready to Install"
    # titles unreadable.)
    W, H = 493, 58
    RIGHT = 132                      # right dark art band width; WixUI title text sits left of it
    img = light_panel(W, H)          # light field (so the black title/subtitle read)
    # right dark art band
    band = dark_backdrop(RIGHT, H, vignette=False)
    img.alpha_composite(band, (W - RIGHT, 0))
    # orange seam between light text field and the dark band
    ImageDraw.Draw(img, "RGBA").line([(W - RIGHT, 0), (W - RIGHT, H)], fill=ORANGE + (255,), width=2)
    # the mark on the far right, inside the dark band
    msz = 44
    mark = make_mark(msz)
    img.alpha_composite(mark, (W - msz - 8, (H - msz)//2))
    # "Transom" wordmark inside the dark band, left of the mark (white-on-dark)
    d = ImageDraw.Draw(img, "RGBA")
    f = load_font(18, bold=True)
    tw, th, toff = text_size(d, "Transom", f)
    d.text((W - msz - 12 - tw, (H - th)//2 - toff), "Transom", font=f, fill=(220, 232, 245, 255))
    # thin orange accent line along the bottom (full width)
    ImageDraw.Draw(img, "RGBA").line([(0, H-2), (W, H-2)], fill=ORANGE + (255,), width=2)
    return img


def make_icon():
    # build at 256 then emit multi-res .ico
    sizes = [256, 128, 64, 48, 32, 16]
    base = make_mark(256).convert("RGBA")
    imgs = [base.resize((s, s), Image.LANCZOS) for s in sizes]
    out = os.path.join(OUT_ICONS, "ShellIcon.ico")
    imgs[0].save(out, format="ICO", sizes=[(s, s) for s in sizes])
    print("wrote", out)


def main():
    os.makedirs(OUT_ICONS, exist_ok=True)
    bg = make_background()
    bn = make_banner()
    bg.convert("RGB").save(os.path.join(OUT_ICONS, "BackgroundImage.png"))
    print("wrote", os.path.join(OUT_ICONS, "BackgroundImage.png"))
    bn.convert("RGB").save(os.path.join(OUT_ICONS, "BannerImage.png"))
    print("wrote", os.path.join(OUT_ICONS, "BannerImage.png"))
    make_icon()
    # composite preview (stacked) for sign-off
    pad = 12
    icon = make_mark(128)
    pw = max(bg.width, bn.width, 128) + pad*2
    ph = bg.height + bn.height + 128 + pad*4
    prev = Image.new("RGBA", (pw, ph), (30, 30, 30, 255))
    y = pad
    prev.alpha_composite(bg, (pad, y)); y += bg.height + pad
    prev.alpha_composite(bn, (pad, y)); y += bn.height + pad
    prev.alpha_composite(icon, (pad, y))
    prev.convert("RGB").save(PREVIEW)
    print("wrote", PREVIEW)


if __name__ == "__main__":
    main()
