#!/usr/bin/env python3
# postprocess-gadpsa.py
# Desaturiert goldene Elemente im deployed Sensor Array (aot-gadpsa.zip).
# Remap (aot-gadpsa-remap.png) bleibt unverändert — er enthält die nativen
# Player-Color-Einträge aus gadpsa.shp (Palette-Indices 80-95 → Ramp 176-191).

import zipfile, struct, io
from pathlib import Path
from PIL import Image
import numpy as np

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
BITS = PROJ / "mods/cnc/bits"
ZIP_IN  = BITS / "aot-gadpsa.zip"
ZIP_OUT = BITS / "aot-gadpsa.zip"

# ── Laden ─────────────────────────────────────────────────────────────────
frames_data = {}
meta_data   = {}

with zipfile.ZipFile(ZIP_IN, 'r') as zf:
    for name in sorted(zf.namelist()):
        data = zf.read(name)
        if name.endswith('.tga'):
            img = Image.open(io.BytesIO(data))
            frames_data[name] = np.array(img.convert("RGBA"))
        elif name.endswith('.meta'):
            meta_data[name] = data

frame_names = sorted(frames_data.keys())
H, W = frames_data[frame_names[0]].shape[:2]
print(f"gadpsa-Frames: {len(frame_names)}  Größe: {W}×{H}px")

# ── HSV (numpy) ────────────────────────────────────────────────────────────
def rgb_to_hsv(rgb_u8_flat):
    """Input: (N,3) uint8. Returns h(°), s(0-1), v(0-1) each shape (N,)."""
    f = rgb_u8_flat.astype(np.float32) / 255.0
    r, g, b = f[:, 0], f[:, 1], f[:, 2]
    cmax = np.maximum(np.maximum(r, g), b)
    cmin = np.minimum(np.minimum(r, g), b)
    delta = cmax - cmin
    s = np.where(cmax > 1e-6, delta / cmax, 0.0)
    v = cmax
    safe_d = np.where(delta > 1e-6, delta, 1.0)
    hr = (g - b) / safe_d % 6.0
    hg = (b - r) / safe_d + 2.0
    hb = (r - g) / safe_d + 4.0
    h = np.zeros_like(r)
    h = np.where(cmax == r, hr, h)
    h = np.where(cmax == g, hg, h)
    h = np.where(cmax == b, hb, h)
    h = (h * 60.0) % 360.0
    h = np.where(delta > 1e-6, h, 0.0)
    return h, s, v

# ── Verarbeitung ───────────────────────────────────────────────────────────
new_frames = {}
gold_total = 0

for fname in frame_names:
    arr  = frames_data[fname].copy()
    flat = arr.reshape(-1, 4).copy()
    am   = flat[:, 3] > 0

    h, s, v = rgb_to_hsv(flat[:, :3])

    # Golden: H 20-65, S > 0.28, V > 0.06
    gold_mask = am & (h >= 20) & (h <= 65) & (s > 0.28) & (v > 0.06)

    lum = 0.299 * flat[:, 0] + 0.587 * flat[:, 1] + 0.114 * flat[:, 2]
    lum_u8 = np.clip(lum, 0, 255).astype(np.uint8)

    flat[gold_mask, 0] = lum_u8[gold_mask]
    flat[gold_mask, 1] = lum_u8[gold_mask]
    flat[gold_mask, 2] = lum_u8[gold_mask]

    gold_total += int(np.sum(gold_mask))
    new_frames[fname] = flat.reshape(H, W, 4)

print(f"Goldene Pixel desaturiert: {gold_total}")

# ── TGA-Schreiber ──────────────────────────────────────────────────────────
def write_tga(rgba):
    h2, w2 = rgba.shape[:2]
    hdr = bytearray(18)
    hdr[2] = 2
    struct.pack_into('<H', hdr, 12, w2)
    struct.pack_into('<H', hdr, 14, h2)
    hdr[16] = 32
    hdr[17] = 0x28
    bgra = rgba[:, :, [2, 1, 0, 3]].flatten()
    return bytes(hdr) + bgra.tobytes()

# ── ZIP schreiben ──────────────────────────────────────────────────────────
with zipfile.ZipFile(ZIP_OUT, 'w', zipfile.ZIP_DEFLATED) as zf:
    for fname in frame_names:
        zf.writestr(fname, write_tga(new_frames[fname]))
    for mname, mdata in meta_data.items():
        zf.writestr(mname, mdata)

print(f"Geschrieben: {ZIP_OUT.name}")
print("=== Fertig ===")
