#!/usr/bin/env python3
"""Gemini/Nano-Banana-Gate-Frame nachbearbeiten: Schachbrett-Hintergrund
entfernen + diagonal -> horizontal shearen.

Gemini backt das Transparenz-Schachbrett in die Pixel ein (Alpha ueberall
255) und liefert das Tor weiterhin diagonal (haelt aber fast exakt die
TS-Steigung 1:2 ein, Frame 0 gemessen: 0.5138, Kruemmung ~1px auf 1191px).

Pipeline:
  1. Keying: Schachbrett = neutral (Chroma < 16) UND Helligkeit >= 203
     (Grau ~215 / Weiss ~255, beide verrauscht 207-217 bzw. 250-255)
  2. Majority-Filter 3x: Keying-Loecher auf hellen Tor-Flaechen stopfen
  3. Steigung der Unterkante messen (robuster Linear-Fit im Mittelbereich,
     Ausreisser-Trimmen) — oder per --k festnageln (fuer Frame-Serien den
     Wert von Frame 0 wiederverwenden, sonst wackelt die Animation!)
  4. vertikaler Shear (premultiplied, BICUBIC), 1px-Feather-Alpha
  5. Staub entfernen (Band-Crop + isolierte Pixel), Crop mit 8px Rand

Aufruf:
  python3 flatten-gemini-frame.py <input.png> <output.png> [--k 0.5138]
"""
import sys
import numpy as np
from PIL import Image, ImageFilter


def key_checkerboard(rgb):
    v = rgb.mean(axis=2)
    chroma = (np.abs(rgb[:, :, 0] - rgb[:, :, 1])
              + np.abs(rgb[:, :, 1] - rgb[:, :, 2])
              + np.abs(rgb[:, :, 0] - rgb[:, :, 2]))
    gate = ~((chroma < 16) & (v >= 203))
    for _ in range(3):
        g = gate.astype(np.int8)
        n = sum(np.roll(np.roll(g, dy, 0), dx, 1)
                for dy in (-1, 0, 1) for dx in (-1, 0, 1)) - g
        gate = gate | ((~gate) & (n >= 6))
    return gate


def measure_slope(gate):
    cols = []
    for x in range(gate.shape[1]):
        ys = np.where(gate[:, x])[0]
        if len(ys):
            cols.append((x, ys.max()))
    cols = np.array(cols)
    xmin, xmax = cols[:, 0].min(), cols[:, 0].max()
    span = xmax - xmin
    mid = cols[(cols[:, 0] > xmin + 0.32 * span) & (cols[:, 0] < xmin + 0.68 * span)]
    A = None
    for _ in range(3):
        A = np.polyfit(mid[:, 0], mid[:, 1], 1)
        res = np.abs(np.polyval(A, mid[:, 0]) - mid[:, 1])
        mid = mid[res < max(3, res.std() * 2)]
    return A[0]


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    src, dst = args[0], args[1]
    k = None
    if "--k" in sys.argv:
        k = float(sys.argv[sys.argv.index("--k") + 1])

    rgb = np.array(Image.open(src).convert("RGB"), dtype=np.float64)
    h, w, _ = rgb.shape
    gate = key_checkerboard(rgb)
    if k is None:
        k = measure_slope(gate)
        print(f"gemessene Steigung: {k:.4f}")

    alpha = np.array(Image.fromarray((gate * 255).astype(np.uint8))
                     .filter(ImageFilter.GaussianBlur(0.8)), dtype=np.float64)
    rgba = np.dstack([rgb, alpha])
    rgba[..., :3] *= rgba[..., 3:] / 255.0
    pil = Image.fromarray(rgba.astype(np.uint8))

    pad = int(abs(k) * w / 2) + 40
    big = Image.new("RGBA", (w, h + 2 * pad), (0, 0, 0, 0))
    big.paste(pil, (0, pad))
    cx = w / 2
    sh = big.transform(big.size, Image.AFFINE, (1, 0, 0, k, 1, -k * cx),
                       resample=Image.BICUBIC, fillcolor=(0, 0, 0, 0))

    o = np.array(sh, dtype=np.float64)
    al = o[..., 3:]
    m = al[..., 0] > 8
    o[m, :3] = np.clip(o[m, :3] / (al[m] / 255.0), 0, 255)
    o[~m] = 0
    a = o.astype(np.uint8)

    # Staub: nur das dichte Inhalts-Band behalten
    op = a[..., 3] > 30
    rows = np.where(op.sum(axis=1) > 15)[0]
    cols = np.where(op.sum(axis=0) > 10)[0]
    y0, y1 = rows.min(), rows.max()
    x0, x1 = cols.min(), cols.max()
    a[:max(0, y0 - 4)] = 0
    a[y1 + 5:] = 0
    a[:, :max(0, x0 - 4)] = 0
    a[:, x1 + 5:] = 0
    mm = (a[..., 3] > 30).astype(np.int16)
    n = sum(np.roll(np.roll(mm, dy, 0), dx, 1)
            for dy in (-1, 0, 1) for dx in (-1, 0, 1)) - mm
    a[(mm == 1) & (n <= 1)] = 0

    out = Image.fromarray(a).crop((max(0, x0 - 8), max(0, y0 - 8), x1 + 9, y1 + 9))
    out.save(dst)
    print(f"{dst}: {out.size[0]}x{out.size[1]}")


if __name__ == "__main__":
    main()
