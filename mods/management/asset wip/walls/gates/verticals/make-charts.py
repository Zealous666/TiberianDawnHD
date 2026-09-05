#!/usr/bin/env python3
"""Beschriftete Kontaktboegen der ECHTEN Ingame-Gate-Sequenzen (Gemini-Sprites
aus mods/cnc/bits/), als Referenz-Charts fuer den Vertical-Render-Prompt."""
import os
from PIL import Image, ImageDraw, ImageFont

BITS = os.path.join(os.path.dirname(__file__), "..", "..", "..", "..", "..",
                    "cnc", "bits")
OUT = os.path.dirname(__file__)
PAD, LABEL_H, TITLE_H = 14, 20, 40
BG = (32, 34, 40, 255)
CELLBG = (18, 19, 23, 255)
GRID = (70, 74, 84, 255)


def font(sz, mono=False):
    cands = (["/System/Library/Fonts/SFNSMono.ttf", "/System/Library/Fonts/Menlo.ttc"]
             if mono else []) + ["/System/Library/Fonts/SFNS.ttf",
             "/Library/Fonts/Arial.ttf", "/System/Library/Fonts/Menlo.ttc"]
    for p in cands:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, sz)
            except Exception:
                pass
    return ImageFont.load_default()


def slice_sheet(path):
    raw = Image.open(path)
    txt = dict(raw.text) if hasattr(raw, "text") else {}
    im = raw.convert("RGBA")
    fs = txt.get("FrameSize")
    if not fs:
        return [im]
    fw, fh = (int(x) for x in fs.split(","))
    n = int(txt.get("FrameAmount", "1"))
    return [im.crop((i * fw, 0, (i + 1) * fw, fh)) for i in range(n)]


def build(title, entries, scale, cols):
    """entries: list of (label, PIL.Image, highlight_bool, seqscale).
    seqscale = Sequenz-Scale aus aot-sequences.yaml, damit alle Frames in
    ihrer wahren relativen Ingame-Groesse erscheinen."""
    # auf gemeinsame Referenz (groesster seqscale) normalisieren
    ref = max(e[3] for e in entries)
    entries = [(l, im, hi, s / ref) for (l, im, hi, s) in entries]
    fw = max(e[1].width * e[3] for e in entries)
    fh = max(e[1].height * e[3] for e in entries)
    cw, ch = int(fw * scale), int(fh * scale)
    cellw, cellh = cw + PAD, ch + PAD + LABEL_H
    rows = (len(entries) + cols - 1) // cols
    W, H = cols * cellw + PAD, TITLE_H + rows * cellh + PAD
    sheet = Image.new("RGBA", (W, H), BG)
    d = ImageDraw.Draw(sheet)
    d.text((PAD, 12), title, font=font(19), fill=(235, 238, 245, 255))
    fl = font(12, mono=True)
    for i, (lbl, img, hi, ss) in enumerate(entries):
        r, c = divmod(i, cols)
        x, y = PAD + c * cellw, TITLE_H + r * cellh
        d.rectangle([x, y + LABEL_H, x + cw, y + LABEL_H + ch], fill=CELLBG, outline=GRID)
        iw, ih = int(img.width * ss * scale), int(img.height * ss * scale)
        cell = img.resize((iw, ih), Image.NEAREST)
        sheet.alpha_composite(cell, (x + (cw - iw) // 2, y + LABEL_H + (ch - ih) // 2))
        d.text((x + 2, y + 2), lbl, font=fl,
               fill=(120, 200, 255, 255) if hi else (170, 175, 185, 255))
    return sheet


# ---- GDI: 11-Frame-Oeffnungsanim (aot-gate-gdi-open.png) + Damaged ----
gdi_open = slice_sheet(os.path.join(BITS, "aot-gate-gdi-open.png"))
gdi_dmg = slice_sheet(os.path.join(BITS, "aot-gate-gdi-damaged.png"))[0]
gdi_entries = []
for i, f in enumerate(gdi_open):
    tag = "  ZU" if i == 0 else ("  OFFEN" if i == len(gdi_open) - 1 else "")
    gdi_entries.append((f"idle #{i}{tag}", f, i in (0, len(gdi_open) - 1), 0.1875))
gdi_entries.append(("damaged-idle", gdi_dmg, True, 0.1))  # Sequenz-Scale 0.1
build("aot-gate-gdi  -  Ingame (Gemini-Beton-Tor, 11-Frame open-anim 0=zu ... 10=offen + damaged)",
      gdi_entries, scale=1.05, cols=4).save(os.path.join(OUT, "chart-gdi-gate-ingame.png"))

# ---- NOD: idle(2) + damaged-idle(2) + idle-shimmer(2) ----
nod_idle = slice_sheet(os.path.join(BITS, "aot-gate-nod-idle.png"))
nod_dmg = slice_sheet(os.path.join(BITS, "aot-gate-nod-damaged-idle.png"))
nod_shim = slice_sheet(os.path.join(BITS, "aot-gate-nod-idle-shimmer.png"))
nod_entries = [  # alle NOD-Sequenzen Scale 0.1
    ("idle #0  ZU / Laser AN", nod_idle[0], True, 0.1),
    ("idle #1  OFFEN / Laser AUS", nod_idle[1], True, 0.1),
    ("damaged-idle #0", nod_dmg[0], False, 0.1),
    ("damaged-idle #1", nod_dmg[1], False, 0.1),
    ("idle-shimmer #0 (Glow A)", nod_shim[0], False, 0.1),
    ("idle-shimmer #1 (Glow B)", nod_shim[1], False, 0.1),
]
build("aot-gate-nod  -  Ingame (Laser-Barriere)",
      nod_entries, scale=0.34, cols=2).save(os.path.join(OUT, "chart-nod-gate-ingame.png"))

for f in ("chart-gdi-gate-ingame.png", "chart-nod-gate-ingame.png"):
    print("wrote", os.path.join(OUT, f))
