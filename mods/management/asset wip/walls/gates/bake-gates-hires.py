#!/usr/bin/env python3
"""HiRes-Bake der horizontalen TS-Gates (nur *_a = 3x1 horizontal).

Finale Pipeline (Vergleich siehe preview-vergleich-3way.png — Scale4x schlug
LANCZOS-Varianten deutlich):
  1. indiziert -> RGBA (idx 0 transparent, isotem-Farben; Remap-Indizes 16-31
     werden zu festem Gold/Tan gebacken -> KEINE Player-Color)
  2. vertikaler Shear bei NATIVER Aufloesung (k=0.5, NEAREST) — die
     2:1-Treppen der Diagonale werden dadurch zu geraden horizontalen Kanten
     (Restjitter 1px wird von Scale2x geschluckt)
  3. 2x Scale2x (AdvMAME-Algorithmus, numpy) = 4x gesamt — glaettet
     1-Pixel-Treppen kantenerhaltend, kein Blur, keine Halos

Output: hires/<name>-hires-NNNN.png, RGBA, alle Frames gleiches Canvas
(deckungsgleich). Schatten-Frames werden NICHT gebacken.
"""
import os
import numpy as np
from PIL import Image

BASE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(BASE, "ts")
DST = os.path.join(BASE, "hires")
PAD = 40   # nativer vertikaler Puffer vor dem Shear
K = 0.5    # TS-Grid-Steigung

# nur Body-Frames (idle/open + damaged + dead), keine Schatten
GATES = {
    "gtgate_a": range(0, 21),   # 0-9 anim, 10-19 damaged, 20 dead
    "ntgate_a": range(0, 15),   # 0-6 anim, 7-13 damaged, 14 dead
}


def to_rgba(im):
    rgba = np.array(im.convert("RGBA"), dtype=np.uint8)
    idx = np.array(im)
    rgba[idx == 0] = 0
    return Image.fromarray(rgba)


def shear_native(pil, k=K, pad=PAD):
    w, h = pil.size
    big = Image.new("RGBA", (w, h + 2 * pad), (0, 0, 0, 0))
    big.paste(pil, (0, pad))
    cx = big.width / 2
    # inverse Abbildung fuer PIL: input_y = y_out + k*(x - cx)
    return big.transform(big.size, Image.AFFINE, (1, 0, 0, k, 1, -k * cx),
                         resample=Image.NEAREST, fillcolor=(0, 0, 0, 0))


def _pack(a):
    return (a[..., 0].astype(np.uint32) | (a[..., 1].astype(np.uint32) << 8)
            | (a[..., 2].astype(np.uint32) << 16) | (a[..., 3].astype(np.uint32) << 24))


def _unpack(p):
    out = np.zeros(p.shape + (4,), dtype=np.uint8)
    out[..., 0] = p & 255
    out[..., 1] = (p >> 8) & 255
    out[..., 2] = (p >> 16) & 255
    out[..., 3] = (p >> 24) & 255
    return out


def scale2x(img):
    a = _pack(np.array(img, dtype=np.uint8))
    E = a
    B = np.vstack([a[:1], a[:-1]])    # oben
    H = np.vstack([a[1:], a[-1:]])    # unten
    D = np.hstack([a[:, :1], a[:, :-1]])  # links
    F = np.hstack([a[:, 1:], a[:, -1:]])  # rechts
    out = np.zeros((a.shape[0] * 2, a.shape[1] * 2), dtype=np.uint32)
    out[0::2, 0::2] = np.where((D == B) & (B != F) & (D != H), D, E)
    out[0::2, 1::2] = np.where((B == F) & (B != D) & (F != H), F, E)
    out[1::2, 0::2] = np.where((D == H) & (D != B) & (H != F), D, E)
    out[1::2, 1::2] = np.where((H == F) & (D != H) & (B != F), F, E)
    return Image.fromarray(_unpack(out))


def bake(im):
    return scale2x(scale2x(shear_native(to_rgba(im))))


def main():
    os.makedirs(DST, exist_ok=True)
    for name, frames in GATES.items():
        for f in frames:
            im = Image.open(os.path.join(SRC, f"{name}-{f:04d}.png"))
            bake(im).save(os.path.join(DST, f"{name}-hires-{f:04d}.png"))
        print(f"{name}: {len(frames)} Frames -> hires (Scale4x)")


if __name__ == "__main__":
    main()
