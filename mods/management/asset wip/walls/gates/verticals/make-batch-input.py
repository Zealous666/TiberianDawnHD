#!/usr/bin/env python3
"""Baut EIN Batch-Input-Bild je Fraktion: alle Sequenz-Frames in einem sauberen
Raster (transparenter Hintergrund, gleiche Zellgroesse, duenne Nummern), damit
ChatGPT/Gemini das GANZE Sheet in EINEM Edit vertikal drehen kann."""
import os
from PIL import Image, ImageDraw, ImageFont

BITS = os.path.join(os.path.dirname(__file__), "..", "..", "..", "..", "..",
                    "cnc", "bits")
OUT = os.path.dirname(__file__)


def font(sz):
    for p in ["/System/Library/Fonts/SFNSMono.ttf", "/System/Library/Fonts/Menlo.ttc",
              "/Library/Fonts/Arial.ttf"]:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, sz)
            except Exception:
                pass
    return ImageFont.load_default()


def slice_sheet(path, scale):
    raw = Image.open(path)
    txt = dict(raw.text) if hasattr(raw, "text") else {}
    im = raw.convert("RGBA")
    if "FrameSize" in txt:
        fw, fh = (int(x) for x in txt["FrameSize"].split(","))
        n = int(txt.get("FrameAmount", "1"))
        frames = [im.crop((i * fw, 0, (i + 1) * fw, fh)) for i in range(n)]
    else:
        frames = [im]
    if scale != 1.0:
        frames = [f.resize((int(f.width * scale), int(f.height * scale)), Image.NEAREST)
                  for f in frames]
    return frames


def build(outname, entries, cols, gap=24, lab=26):
    """entries: list of (label, PIL.Image). Transparent grid."""
    cw = max(e[1].width for e in entries)
    ch = max(e[1].height for e in entries)
    rows = (len(entries) + cols - 1) // cols
    W = cols * cw + (cols + 1) * gap
    H = rows * (ch + lab) + (rows + 1) * gap
    c = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(c)
    f = font(20)
    for i, (label, img) in enumerate(entries):
        r, cc = divmod(i, cols)
        x = gap + cc * (cw + gap)
        y = gap + r * (ch + lab + gap)
        d.text((x, y), label, font=f, fill=(150, 150, 150, 255))
        c.alpha_composite(img, (x + (cw - img.width) // 2, y + lab))
    c.save(os.path.join(OUT, outname))
    print("wrote", os.path.join(OUT, outname), c.size)


# GDI: 11 open frames + damaged (open Scale 0.1875, damaged 0.1 -> gleiche Ingame-Groesse)
g_open = slice_sheet(os.path.join(BITS, "aot-gate-gdi-open.png"), 0.1875 / 0.1875)
g_dmg = slice_sheet(os.path.join(BITS, "aot-gate-gdi-damaged.png"), 0.1 / 0.1875)
gdi = [(f"{i}", f) for i, f in enumerate(g_open)] + [("dmg", g_dmg[0])]
build("input-gdi-batch.png", gdi, cols=4)

# NOD: idle 0/1, damaged 0/1, shimmer 0/1 (alle Scale 0.1 -> unskaliert)
n_idle = slice_sheet(os.path.join(BITS, "aot-gate-nod-idle.png"), 1.0)
n_dmg = slice_sheet(os.path.join(BITS, "aot-gate-nod-damaged-idle.png"), 1.0)
n_shim = slice_sheet(os.path.join(BITS, "aot-gate-nod-idle-shimmer.png"), 1.0)
nod = [("idle-0 (zu/Laser an)", n_idle[0]), ("idle-1 (offen/Laser aus)", n_idle[1]),
       ("dmg-0", n_dmg[0]), ("dmg-1", n_dmg[1]),
       ("shimmer-0", n_shim[0]), ("shimmer-1", n_shim[1])]
build("input-nod-batch.png", nod, cols=2)
