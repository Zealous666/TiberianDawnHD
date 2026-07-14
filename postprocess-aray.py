#!/usr/bin/env python3
# postprocess-aray.py  v2
# Post-processing für aot-aray mobile sprite:
#   1. Goldene Elemente desaturieren (Sättigung entfernen, Helligkeit beibehalten)
#   2. Player-Color: hellste Pixel nach Desaturierung → Ramp 176-191 (Highlight-Remap)
#      Strategie: Body behält die grauen Pixel; Remap legt Player-Farbe OBEN DRAUF.
#      Schwellwert: Lum > HIGHLIGHT_THRESHOLD → Player-Color-Overlay.
#
# Output: aot-aray-test.zip (body) + aot-aray-test-remap.png (remap)

import zipfile, struct, io, zlib
from pathlib import Path
from PIL import Image
import numpy as np

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
BITS = PROJ / "mods/cnc/bits"
BODY_IN   = BITS / "aot-aray-test.zip"
BODY_OUT  = BITS / "aot-aray-test.zip"
REMAP_OUT = BITS / "aot-aray-test-remap.png"
RAMP_PAL  = BITS / "aot-vxl-ramp.pal"

FACINGS = 32
COLS    = 8
ROWS    = FACINGS // COLS

# Schwellwert: Lum > X → gehört zu Player-Color-Highlights
# Nach Desaturierung sind ehemalige Goldpixel ~Lum 98 (dunkelgrau).
# Echte Highlights (Glanzlichter auf Karosserie) liegen bei Lum > 180.
HIGHLIGHT_THRESHOLD = 120   # 0-255  → ~5-6% der opaquen Pixel

# ── Laden ─────────────────────────────────────────────────────────────────
frames_data = {}
meta_data   = {}

with zipfile.ZipFile(BODY_IN, 'r') as zf:
    for name in sorted(zf.namelist()):
        data = zf.read(name)
        if name.endswith('.tga'):
            img = Image.open(io.BytesIO(data))
            frames_data[name] = np.array(img.convert("RGBA"))
        elif name.endswith('.meta'):
            meta_data[name] = data

frame_names = sorted(frames_data.keys())
H, W = frames_data[frame_names[0]].shape[:2]
FACINGS_FOUND = len(frame_names)
print(f"Frames: {FACINGS_FOUND}  Größe: {W}×{H}px")

# ── HSV (numpy) ────────────────────────────────────────────────────────────
def rgb_to_hsv(flat_rgb_f):
    r, g, b = flat_rgb_f[:, 0], flat_rgb_f[:, 1], flat_rgb_f[:, 2]
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

def lum_f(rgb_u8):
    """rgb_u8: (...,3) uint8. Returns luminance float 0-255."""
    return 0.299 * rgb_u8[..., 0] + 0.587 * rgb_u8[..., 1] + 0.114 * rgb_u8[..., 2]

# ── Verarbeitung ───────────────────────────────────────────────────────────
new_frames   = {}
remap_arrays = []
gold_total   = 0
hl_total     = 0

for fname in frame_names:
    arr  = frames_data[fname].copy()
    flat = arr.reshape(-1, 4).copy()
    am   = flat[:, 3] > 0

    rgb_f = flat[:, :3].astype(np.float32) / 255.0
    h, s, v = rgb_to_hsv(rgb_f)

    # Golden: H 20-65, S > 0.30
    gold_mask = am & (h >= 20) & (h <= 65) & (s > 0.30) & (v > 0.06)

    # Schritt 1: Goldpixel desaturieren
    lum_g = lum_f(flat[:, :3])
    lum_g_u8 = np.clip(lum_g, 0, 255).astype(np.uint8)
    flat[gold_mask, 0] = lum_g_u8[gold_mask]
    flat[gold_mask, 1] = lum_g_u8[gold_mask]
    flat[gold_mask, 2] = lum_g_u8[gold_mask]
    gold_total += int(np.sum(gold_mask))

    # Schritt 2: Luminanz NACH Desaturierung berechnen
    lum_after = lum_f(flat[:, :3])

    # Player-Color: opaque Pixel mit Lum > HIGHLIGHT_THRESHOLD
    hl_mask = am & (lum_after > HIGHLIGHT_THRESHOLD)

    # Ramp-Index: Lum→ 176 (hell) … 191 (dunkel) — innerhalb der Highlight-Pixel
    hl_lum = lum_after[hl_mask]
    # Bereich festlegen: nutze tatsächlichen Min/Max der Highlights
    lmin = np.percentile(hl_lum, 5) if len(hl_lum) > 0 else HIGHLIGHT_THRESHOLD
    lmax = np.percentile(hl_lum, 95) if len(hl_lum) > 0 else 255.0
    lrange = max(lmax - lmin, 1.0)
    ramp_v = np.clip(np.round(176.0 + (1.0 - (hl_lum - lmin) / lrange) * 15.0), 176, 191).astype(np.uint8)

    remap_flat = np.zeros(H * W, dtype=np.uint8)
    remap_flat[hl_mask] = ramp_v
    hl_total += int(np.sum(hl_mask))

    # Body behält die Pixel (Remap liegt OBEN DRAUF in OpenRA)
    new_frames[fname]  = flat.reshape(H, W, 4)
    remap_arrays.append(remap_flat.reshape(H, W))

print(f"Goldpixel desaturiert : {gold_total} ({gold_total/FACINGS_FOUND:.0f}/frame)")
print(f"Highlight-Remap Pixel : {hl_total}  ({hl_total/FACINGS_FOUND:.0f}/frame)")

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

# ── Body-ZIP ───────────────────────────────────────────────────────────────
with zipfile.ZipFile(BODY_OUT, 'w', zipfile.ZIP_DEFLATED) as zf:
    for fname in frame_names:
        zf.writestr(fname, write_tga(new_frames[fname]))
    for mname, mdata in meta_data.items():
        zf.writestr(mname, mdata)
print(f"Body: {BODY_OUT.name}")

# ── Remap-PNG-Sheet ────────────────────────────────────────────────────────
sheet_w = COLS * W
sheet_h = ROWS * H
sheet   = np.zeros((sheet_h, sheet_w), dtype=np.uint8)
for i, remap in enumerate(remap_arrays):
    col = i % COLS
    row = i // COLS
    sheet[row*H:(row+1)*H, col*W:(col+1)*W] = remap

simg = Image.fromarray(sheet, 'P')

ramp_raw = RAMP_PAL.read_bytes()
outpal   = [0] * 768
for i in range(176, 192):
    outpal[i*3]   = min(255, ramp_raw[i*3]   * 4)
    outpal[i*3+1] = min(255, ramp_raw[i*3+1] * 4)
    outpal[i*3+2] = min(255, ramp_raw[i*3+2] * 4)
simg.putpalette(outpal)
simg.save(REMAP_OUT, transparency=bytes([0] + [255] * 255))

def tc(k, v):
    d = k.encode() + b'\x00' + v.encode()
    return struct.pack('>I', len(d)) + b'tEXt' + d + struct.pack('>I', zlib.crc32(b'tEXt' + d) & 0xFFFFFFFF)

raw = REMAP_OUT.read_bytes()
pos, kept = 8, []
while pos < len(raw):
    length = struct.unpack('>I', raw[pos:pos+4])[0]
    ct = raw[pos+4:pos+8]
    if ct != b'tEXt':
        kept.append((ct, raw[pos:pos+12+length]))
    pos += 12 + length
    if ct == b'IEND':
        break

outb = bytearray(raw[:8])
for ct, chunk in kept:
    outb += chunk
    if ct == b'IHDR':
        outb += tc("FrameSize", f"{W},{H}") + tc("FrameAmount", str(FACINGS_FOUND))
REMAP_OUT.write_bytes(outb)
print(f"Remap: {REMAP_OUT.name}  ({sheet_w}×{sheet_h}px, {FACINGS_FOUND} frames)")
print("=== Fertig ===")
