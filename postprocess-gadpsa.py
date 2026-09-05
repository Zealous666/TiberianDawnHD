#!/usr/bin/env python3
# postprocess-gadpsa.py  v2
# Deployed Sensor Array (aot-gadpsa):
#   1. Body 20% dunkler (Helligkeit × 0.80)
#   2. Rote Pixel (Warnanzeigen im TS-Sprite) → Player-Color-Remap (176-183)
#      + Body an diesen Positionen transparent
#   3. Bestehenden Remap (188 SHP-native Pixel) behalten + neue hinzufügen
# Gold-Desaturierung bereits in v1 erledigt.

import zipfile, struct, io, zlib
from pathlib import Path
from PIL import Image
import numpy as np

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
BITS = PROJ / "mods/cnc/bits"
ZIP_IN    = BITS / "aot-gadpsa.zip"
ZIP_OUT   = BITS / "aot-gadpsa.zip"
REMAP_IN  = BITS / "aot-gadpsa-remap.png"
REMAP_OUT = BITS / "aot-gadpsa-remap.png"
RAMP_PAL  = BITS / "aot-vxl-ramp.pal"

DARKEN    = 0.80    # Helligkeitsmultiplikator
RAMP_MIN  = 176     # Heller Player-Color-Bereich
RAMP_MAX  = 183

# ── Laden ─────────────────────────────────────────────────────────────────
frames_data = {}
meta_data   = {}

with zipfile.ZipFile(ZIP_IN, 'r') as zf:
    for name in sorted(zf.namelist()):
        data = zf.read(name)
        if name.endswith('.tga'):
            frames_data[name] = np.array(Image.open(io.BytesIO(data)).convert("RGBA"))
        elif name.endswith('.meta'):
            meta_data[name] = data

frame_names = sorted(frames_data.keys())
H, W = frames_data[frame_names[0]].shape[:2]
print(f"gadpsa-Frames: {len(frame_names)}  {W}×{H}px")

# Existierenden Remap laden (als numpy-Array, indiziert)
existing_remap = np.array(Image.open(REMAP_IN))
print(f"Existierender Remap: {int(np.sum(existing_remap > 0))} Pixel")

# ── HSV ───────────────────────────────────────────────────────────────────
def rgb_to_h(rgb_u8_flat):
    """Returns Hue (°) array, shape (N,)."""
    f = rgb_u8_flat.astype(np.float32) / 255.0
    r, g, b = f[:, 0], f[:, 1], f[:, 2]
    cmax = np.maximum(np.maximum(r, g), b)
    cmin = np.minimum(np.minimum(r, g), b)
    delta = cmax - cmin
    safe_d = np.where(delta > 1e-6, delta, 1.0)
    s = np.where(cmax > 1e-6, delta / cmax, 0.0)
    v = cmax
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

# ── TGA-Schreiber ──────────────────────────────────────────────────────────
def write_tga(rgba):
    h2, w2 = rgba.shape[:2]
    hdr = bytearray(18)
    hdr[2] = 2
    struct.pack_into('<H', hdr, 12, w2)
    struct.pack_into('<H', hdr, 14, h2)
    hdr[16] = 32
    hdr[17] = 0x28
    return bytes(hdr) + rgba[:, :, [2, 1, 0, 3]].flatten().tobytes()

# ── Verarbeitung ───────────────────────────────────────────────────────────
new_frames   = {}
new_remap    = existing_remap.copy()   # starte mit vorhandenen SHP-Pixeln
red_total    = 0

for fname in frame_names:
    flat = frames_data[fname].reshape(-1, 4).copy()
    am   = flat[:, 3] > 0

    # 1. Helligkeit × DARKEN
    flat[:, :3] = np.clip(flat[:, :3].astype(np.float32) * DARKEN, 0, 255).astype(np.uint8)

    # 2. Rote Pixel identifizieren (Warn-LEDs / Indikatoren)
    h, s, v = rgb_to_h(flat[:, :3])
    red_mask = am & ((h <= 20) | (h >= 340)) & (s > 0.40) & (v > 0.05)

    # Ramp-Index für rote Pixel: Luminanz → 176-183
    lum = 0.299 * flat[:, 0].astype(np.float32) \
        + 0.587 * flat[:, 1].astype(np.float32) \
        + 0.114 * flat[:, 2].astype(np.float32)
    red_lum = lum[red_mask]
    if len(red_lum) > 0:
        lmax   = float(np.max(red_lum))
        lmin   = float(np.min(red_lum))
        lrange = max(lmax - lmin, 1.0)
        ramp_v = np.clip(
            np.round(RAMP_MIN + (1.0 - (red_lum - lmin) / lrange) * (RAMP_MAX - RAMP_MIN)),
            RAMP_MIN, RAMP_MAX
        ).astype(np.uint8)

        # In neuen Remap eintragen (1-frame Sprite, direkt in 2D-Array)
        positions = np.where(red_mask.reshape(H, W))
        new_remap[positions] = ramp_v

        # Body transparent
        flat[red_mask, 3] = 0
        red_total += int(np.sum(red_mask))

    new_frames[fname] = flat.reshape(H, W, 4)

print(f"Rote Pixel → Remap: {red_total}")
print(f"Remap gesamt nach Update: {int(np.sum(new_remap > 0))} Pixel")

# ── Body-ZIP schreiben ─────────────────────────────────────────────────────
with zipfile.ZipFile(ZIP_OUT, 'w', zipfile.ZIP_DEFLATED) as zf:
    for fname in frame_names:
        zf.writestr(fname, write_tga(new_frames[fname]))
    for mname, mdata in meta_data.items():
        zf.writestr(mname, mdata)
print(f"Body: {ZIP_OUT.name}")

# ── Remap-PNG schreiben ────────────────────────────────────────────────────
def make_text_chunk(k, v):
    d = k.encode() + b'\x00' + v.encode()
    return struct.pack('>I', len(d)) + b'tEXt' + d + struct.pack('>I', zlib.crc32(b'tEXt' + d) & 0xFFFFFFFF)

def inject_metadata(png_bytes, frame_w, frame_h, frame_count):
    pos, kept = 8, []
    while pos < len(png_bytes):
        length = struct.unpack('>I', png_bytes[pos:pos+4])[0]
        ct = png_bytes[pos+4:pos+8]
        if ct != b'tEXt':
            kept.append((ct, png_bytes[pos:pos+12+length]))
        pos += 12 + length
        if ct == b'IEND':
            break
    out = bytearray(png_bytes[:8])
    for ct, chunk in kept:
        out += chunk
        if ct == b'IHDR':
            out += make_text_chunk("FrameSize", f"{frame_w},{frame_h}")
            out += make_text_chunk("FrameAmount", "1")
    return bytes(out)

simg = Image.fromarray(new_remap, 'P')
ramp_raw = RAMP_PAL.read_bytes()
outpal   = [0] * 768
for i in range(176, 192):
    outpal[i*3]   = min(255, ramp_raw[i*3]   * 4)
    outpal[i*3+1] = min(255, ramp_raw[i*3+1] * 4)
    outpal[i*3+2] = min(255, ramp_raw[i*3+2] * 4)
simg.putpalette(outpal)
simg.save(REMAP_OUT, transparency=bytes([0] + [255] * 255))
REMAP_OUT.write_bytes(inject_metadata(REMAP_OUT.read_bytes(), W, H, 1))
print(f"Remap: {REMAP_OUT.name}  {W}×{H}px")
print("=== Fertig ===")
