#!/usr/bin/env python3
"""Legt die diagonalen TS-Gate-Sprites (isometrisch, Steigung 1:2) per
vertikalem Shear horizontal, damit sie in das orthogonale TD-Grid der
aot-Walls passen. Senkrechte Elemente (Pfosten, Tormechanik) bleiben
dabei senkrecht; nur die Laufrichtung der Anlage wird flachgelegt.

Input:  ts/<name>-NNNN.png  (indizierte Frames aus `ts --png <shp> isotem.pal`)
Output: horizontal/<name>-h-NNNN.png (gleiches Canvas fuer alle Frames,
        Palette bleibt erhalten -> spaeteres Compositing auf Index-Ebene moeglich)

Shear: y_out = y_in - k*(x - cx), k = +0.5 fuer *_a (Steigung +1:2),
       k = -0.5 fuer *_b. Exakt die TS-Grid-Steigung (Zelle 48x24).
NEAREST haelt die Palette-Indizes intakt (Pixel-Art, kein Blur).
"""
import os
from PIL import Image

BASE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(BASE, "ts")
DST = os.path.join(BASE, "horizontal")
PAD = 56  # vertikaler Puffer, damit der Shear nichts abschneidet

# name -> (Shear-Faktor, Frameanzahl)
GATES = {
    "gtgate_a": (0.5, 42),   # GDI 3x1: 0-9 idle/open, 10-19 damaged, 20 dead, 21-41 Schatten
    "gtgate_b": (-0.5, 42),  # GDI 1x3 (Gegenrichtung, alternative Ansicht)
    "ntgate_a": (0.5, 30),   # NOD 3x1: 0-6 idle/open, 7-13 damaged, 14 dead, 15-29 Schatten
    "ntgate_b": (-0.5, 30),
}


def shear(im, k):
    w, h = im.size
    big = Image.new("P", (w, h + 2 * PAD), 0)
    big.putpalette(im.getpalette())
    big.paste(im, (0, PAD))
    cx = w / 2
    # PIL-AFFINE ist die INVERSE Abbildung: input_y = y_out + k*(x - cx)
    return big.transform(big.size, Image.AFFINE, (1, 0, 0, k, 1, -k * cx),
                         resample=Image.NEAREST, fillcolor=0)


def main():
    os.makedirs(DST, exist_ok=True)
    for name, (k, count) in GATES.items():
        for f in range(count):
            src = os.path.join(SRC, f"{name}-{f:04d}.png")
            out = shear(Image.open(src), k)
            out.save(os.path.join(DST, f"{name}-h-{f:04d}.png"))
        print(f"{name}: {count} Frames -> horizontal (k={k})")


if __name__ == "__main__":
    main()
