"""Generates src/Echodeck.App/Assets/echodeck.ico (+ .png) — run: python tools/make_icon.py src/Echodeck.App/Assets"""
import math, sys
from PIL import Image, ImageDraw

out_dir = sys.argv[1]
S = 1024

def tile(detail: bool) -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # vertical gradient, Discord-ish blurple → violet
    grad = Image.new("RGBA", (S, S))
    top, bottom = (124, 92, 255), (64, 78, 214)
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / (S - 1)
        gd.line([(0, y), (S, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([24, 24, S - 24, S - 24], radius=230, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    white = (255, 255, 255, 255)
    cx, cy = S / 2, S / 2 + 10
    r = 300 if detail else 290
    w = 70 if detail else 120
    # replay arc: open gap at the top-left where the arrowhead sits
    start, end = 215, 495  # degrees (PIL: 0 = 3 o'clock, clockwise)
    d.arc([cx - r, cy - r, cx + r, cy + r], start=start, end=360, fill=white, width=w)
    d.arc([cx - r, cy - r, cx + r, cy + r], start=0, end=end - 360, fill=white, width=w)
    # arrowhead at the arc start, pointing counter-clockwise (replay)
    a = math.radians(start)
    px, py = cx + r * math.cos(a), cy + r * math.sin(a)
    tx, ty = -math.sin(a), math.cos(a)          # tangent (clockwise direction)
    nx, ny = math.cos(a), math.sin(a)           # outward normal
    L = 150 if detail else 210   # arrowhead length along the tangent
    H = 120 if detail else 170   # arrowhead half-width across the stroke
    tip = (px - tx * L, py - ty * L)
    p1 = (px + nx * H, py + ny * H)
    p2 = (px - nx * H, py - ny * H)
    d.polygon([tip, p1, p2], fill=white)

    if detail:
        # waveform bars inside the circle
        heights = [120, 230, 330, 200, 140]
        bw, gap = 46, 30
        total = len(heights) * bw + (len(heights) - 1) * gap
        x = cx - total / 2
        for h in heights:
            d.rounded_rectangle([x, cy - h / 2, x + bw, cy + h / 2], radius=bw / 2, fill=white)
            x += bw + gap
    return img

big, small = tile(True), tile(False)
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = [(small if s <= 24 else big).resize((s, s), Image.LANCZOS) for s in sizes]
frames[-1].save(f"{out_dir}/echodeck.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
big.resize((256, 256), Image.LANCZOS).save(f"{out_dir}/echodeck.png")
