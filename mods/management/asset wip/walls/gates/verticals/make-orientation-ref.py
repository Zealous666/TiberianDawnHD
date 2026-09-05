#!/usr/bin/env python3
"""Orientierungs-Referenz v2: betont PARALLEL-Projektion (keine Perspektive/Flucht).
Links = aktuelle horizontale Gate-Ausrichtung, rechts = ZIEL = echte vertikale Wand #1
(senkrechter Betonstreifen konstanter Breite) mit eingezeichneten parallelen Fuehrungen."""
import os
from PIL import Image, ImageDraw, ImageFont

BITS = os.path.join(os.path.dirname(__file__), "..", "..", "..", "..", "..",
                    "cnc", "bits")
OUT = os.path.dirname(__file__)


def font(sz):
    for p in ["/System/Library/Fonts/SFNS.ttf", "/Library/Fonts/Arial.ttf",
              "/System/Library/Fonts/Menlo.ttc"]:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, sz)
            except Exception:
                pass
    return ImageFont.load_default()


def wall(png, idx):
    im = Image.open(os.path.join(BITS, png)).convert("RGBA")
    return im.crop((0, idx * 128, 128, (idx + 1) * 128))


def gate0(png, fw, fh):
    im = Image.open(os.path.join(BITS, png)).convert("RGBA")
    return im.crop((0, 0, fw, fh))


def build(name, wall_png, gate_png, gfw, gfh, gscale, guide_x=(37, 61)):
    S = 4
    w1 = wall(wall_png, 1).resize((128 * S, 128 * S), Image.NEAREST)
    gate = gate0(gate_png, gfw, gfh)
    gate = gate.resize((int(gate.width * gscale), int(gate.height * gscale)), Image.NEAREST)

    cell = 128 * S
    pad, top = 24, 116
    W = pad * 3 + cell * 2
    H = top + cell + 130
    c = Image.new("RGBA", (W, H), (28, 30, 36, 255))
    d = ImageDraw.Draw(c)
    d.text((pad, 16), f"{name}  —  Gate von HORIZONTAL (links) auf VERTIKAL (rechts = Ziel)",
           font=font(30), fill=(235, 238, 245, 255))
    d.text((pad, 54), "ZIEL-Projektion = PARALLEL/isometrisch (wie rechts): KEINE Perspektive, KEINE Fluchtlinien, KEIN Trapez.",
           font=font(19), fill=(120, 220, 140, 255))
    d.text((pad, 80), "Das Betonband ist ein SENKRECHTER Streifen KONSTANTER Breite (oben so breit wie unten), flach/niedrig — kein hochstehender Turm.",
           font=font(19), fill=(120, 220, 140, 255))

    # linke Zelle: aktuelles horizontales Gate
    x0 = pad
    d.rectangle([x0, top, x0 + cell, top + cell], fill=(18, 19, 23, 255), outline=(80, 84, 94, 255))
    c.alpha_composite(gate, (x0 + (cell - gate.width) // 2, top + (cell - gate.height) // 2))
    d.text((x0, top + cell + 10), "IST: horizontal ('/'-Band, waagerecht)", font=font(20), fill=(255, 170, 120, 255))

    # rechte Zelle: vertikale Wand #1 + parallele Fuehrungslinien
    x1 = pad * 2 + cell
    d.rectangle([x1, top, x1 + cell, top + cell], fill=(18, 19, 23, 255), outline=(80, 84, 94, 255))
    c.alpha_composite(w1, (x1, top))
    for gx in guide_x:  # senkrechte parallele Linien entlang der Betonkanten
        X = x1 + gx * S
        d.line([(X, top), (X, top + cell)], fill=(255, 60, 60, 255), width=3)
    d.text((x1, top + cell + 10), ">>> ZIEL: vertikale Wand #1 — senkrechter Streifen, parallele Kanten <<<",
           font=font(20), fill=(120, 220, 140, 255))
    p = os.path.join(OUT, f"target-orientation-{name.lower()}.png")
    c.save(p)
    print("wrote", p, c.size)


build("GDI", "aot-wall-gdi.png", "aot-gate-gdi-open.png", 380, 252, 1.4)
build("NOD", "aot-wall-nod.png", "aot-gate-nod-idle.png", 721, 560, 0.75, guide_x=(37, 61))
