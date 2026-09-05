#!/usr/bin/env python3
"""
Baut aus 17 handerzeugten Frames (ajet-0000..ajet-0016) die vollen 32 Facings.
Frames 1..15 werden horizontal gespiegelt -> Slots 17..31. Frame 0 (Nord) und
16 (Sued) liegen auf der Spiegelachse und bleiben unveraendert.

Eingabe:  ./repaint/ajet-0000.png ... ajet-0016.png   (17 Stueck)
          Hintergrund egal: Magenta (#FF00FF) wird auf Alpha gekeyt.
Ausgabe:  ./out/ajet-0000.png ... ajet-0031.png   +  ./out/_preview_32.png

Aufruf:   python3 mirror-assemble-ajet.py
"""
import os, glob
from PIL import Image

IN_DIR   = "repaint"
OUT_DIR  = "out"
KEY      = (255, 0, 255)   # Magenta Key-Color
KEY_TOL  = 60              # Toleranz fuer Anti-Aliasing-Rand
CANVAS   = 256             # gemeinsame quadratische Leinwand

def key_to_alpha(im: Image.Image) -> Image.Image:
    im = im.convert("RGBA")
    px = im.load()
    kr, kg, kb = KEY
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if abs(r-kr) < KEY_TOL and abs(g-kg) < KEY_TOL and abs(b-kb) < KEY_TOL:
                px[x, y] = (r, g, b, 0)
    return im

def normalize(im: Image.Image) -> Image.Image:
    """Auf Alpha-BBox zuschneiden und zentriert auf feste Leinwand setzen."""
    bbox = im.getbbox()
    if bbox:
        im = im.crop(bbox)
    canvas = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    ox = (CANVAS - im.width) // 2
    oy = (CANVAS - im.height) // 2
    canvas.alpha_composite(im, (ox, oy))
    return canvas

def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    src = sorted(glob.glob(os.path.join(IN_DIR, "ajet-*.png")))
    if len(src) != 17:
        raise SystemExit(f"FEHLER: erwarte 17 Frames in {IN_DIR}/, habe {len(src)}")

    base = {}
    for i, fp in enumerate(src):          # i = 0..16
        im = normalize(key_to_alpha(Image.open(fp)))
        base[i] = im
        im.save(os.path.join(OUT_DIR, f"ajet-{i:04d}.png"))

    # Spiegeln: f in 1..15 -> Slot 32-f  (17..31)
    for f in range(1, 16):
        slot = 32 - f
        mirror = base[f].transpose(Image.FLIP_LEFT_RIGHT)
        mirror.save(os.path.join(OUT_DIR, f"ajet-{slot:04d}.png"))

    # Preview-Sheet 8x4
    cols, rows = 8, 4
    sheet = Image.new("RGBA", (cols*CANVAS, rows*CANVAS), (255, 0, 255, 255))
    for idx in range(32):
        im = Image.open(os.path.join(OUT_DIR, f"ajet-{idx:04d}.png")).convert("RGBA")
        sheet.alpha_composite(im, ((idx % cols)*CANVAS, (idx // cols)*CANVAS))
    sheet.save(os.path.join(OUT_DIR, "_preview_32.png"))
    print(f"Fertig: 32 Frames + _preview_32.png in {OUT_DIR}/")

if __name__ == "__main__":
    main()
