#!/usr/bin/env python3
"""Slice das 4x3 ChatGPT-Raster (nod_vertical_interpoliert.png), registriere alle
12 Frames am Beton-Platten-Schwerpunkt (killt den Interpolations-Drift) und packe
sie zu einem sauberen horizontalen Multi-Frame-Sheet mit FrameSize/FrameAmount.
Frame 0-10 = Laser-Fade (open-anim), Frame 11 = damaged (separat)."""
import os
import numpy as np
from PIL import Image, PngImagePlugin

HERE = os.path.dirname(__file__)
SRC = os.path.join(HERE, "nod_vertical_interpoliert.png")
COLS, ROWS = 4, 3


def concrete_centroid(cell):
    r, g, b, a = (cell[..., i].astype(int) for i in range(4))
    mx = np.maximum(np.maximum(r, g), b)
    mn = np.minimum(np.minimum(r, g), b)
    plate = (a > 90) & (mx > 150) & (mx < 242) & (mx - mn < 24)  # helles entsaettigtes Beton
    ys, xs = np.nonzero(plate)
    return xs.mean(), ys.mean()


def alpha_bbox(cell):
    a = cell[..., 3]
    ys, xs = np.nonzero(a > 16)
    return xs.min(), ys.min(), xs.max(), ys.max()


im = Image.open(SRC).convert("RGBA")
W, H = im.size
A = np.array(im)
cw, ch = W / COLS, H / ROWS

frames = []
for i in range(12):
    r0, c0 = divmod(i, COLS)
    x0, y0 = int(round(c0 * cw)), int(round(r0 * ch))
    x1, y1 = int(round((c0 + 1) * cw)), int(round((r0 + 1) * ch))
    cell = A[y0:y1, x0:x1]
    frames.append(cell)

# Schwerpunkte + Bbox relativ zum Schwerpunkt
cents = [concrete_centroid(f) for f in frames]
rel = []  # (left,top,right,bottom) relativ zum Centroid
for f, (cx, cy) in zip(frames, cents):
    x0, y0, x1, y1 = alpha_bbox(f)
    rel.append((x0 - cx, y0 - cy, x1 - cx, y1 - cy))
L = min(r[0] for r in rel); T = min(r[1] for r in rel)
R = max(r[2] for r in rel); B = max(r[3] for r in rel)
pad = 6
FW = int(np.ceil(R - L)) + 2 * pad
FH = int(np.ceil(B - T)) + 2 * pad
# Ziel-Centroid im Frame:
gx = pad - L
gy = pad - T
print("registered frame size", FW, FH, "centroid at", round(gx, 1), round(gy, 1))

reg = []
for f, (cx, cy) in zip(frames, cents):
    src = Image.fromarray(f, "RGBA")
    canvas = Image.new("RGBA", (FW, FH), (0, 0, 0, 0))
    # so verschieben, dass Centroid -> (gx,gy)
    ox = int(round(gx - cx)); oy = int(round(gy - cy))
    canvas.alpha_composite(src, (ox, oy))
    reg.append(canvas)

open_frames = reg[:11]
dmg = reg[11]


def pack(frames_list, path, meta=True):
    n = len(frames_list)
    sheet = Image.new("RGBA", (FW * n, FH), (0, 0, 0, 0))
    for i, fr in enumerate(frames_list):
        sheet.alpha_composite(fr, (i * FW, 0))
    info = PngImagePlugin.PngInfo()
    if meta:
        info.add_text("FrameSize", f"{FW},{FH}")
        info.add_text("FrameAmount", str(n))
    sheet.save(path, pnginfo=info)
    print("wrote", os.path.basename(path), sheet.size, f"({n} frames)")


pack(open_frames, os.path.join(HERE, "aot-gate-nod-b-open.png"))
pack([dmg], os.path.join(HERE, "aot-gate-nod-b-damaged.png"), meta=False)

# Filmstrip-Preview (skaliert, dunkler Hintergrund) zum Sichten
S = 0.5
strip = Image.new("RGBA", (int(FW * S) * 12 + 13, int(FH * S) + 2), (40, 42, 48, 255))
for i, fr in enumerate(reg):
    small = fr.resize((int(FW * S), int(FH * S)), Image.LANCZOS)
    strip.alpha_composite(small, (1 + i * (int(FW * S) + 1), 1))
strip.save(os.path.join(HERE, "preview-nod-vertical-registered.png"))
print("wrote preview-nod-vertical-registered.png", strip.size)
