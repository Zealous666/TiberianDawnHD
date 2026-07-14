#!/usr/bin/env python3
# postprocess-aray.py  v3
# Mobile Sensor Array (aot-aray) — Player-Color-Remap überarbeiten.
# Liest den AKTUELLEN Body-ZIP (gold bereits desaturiert).
# Strategie:
#   Lum > THRESHOLD → Body transparent machen + Ramp 176-183 (heller Bereich)
#   Damit erscheinen die Player-Color-Akkzente hell und eindeutig sichtbar.
#   Gold-Desaturierung hier nicht nötig (bereits in v1 erledigt).

import zipfile, struct, io, zlib
from pathlib import Path
from PIL import Image
import numpy as np

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
BITS = PROJ / "mods/cnc/bits"
BODY_ZIP  = BITS / "aot-aray-test.zip"
REMAP_PNG = BITS / "aot-aray-test-remap.png"
RAMP_PAL  = BITS / "aot-vxl-ramp.pal"

FACINGS = 32
COLS    = 8
ROWS    = FACINGS // COLS

# Schwellwert: Pixel mit Lum > THRESHOLD werden Player-Color.
# 80 ≈ 18% der Body-Pixel, deckt die hellsten Flächen ab (Dach, Panels, Antenne).
THRESHOLD = 80
# Ramp-Bereich: 176 (hell) … 183 (mittel). Alles unter 183 ist klar sichtbar.
RAMP_MIN, RAMP_MAX = 176, 183

# ── Laden ─────────────────────────────────────────────────────────────────
frames_data = {}
meta_data   = {}

with zipfile.ZipFile(BODY_ZIP, 'r') as zf:
    for name in sorted(zf.namelist()):
        data = zf.read(name)
        if name.endswith('.tga'):
            frames_data[name] = np.array(Image.open(io.BytesIO(data)).convert("RGBA"))
        elif name.endswith('.meta'):
            meta_data[name] = data

frame_names = sorted(frames_data.keys())
H, W = frames_data[frame_names[0]].shape[:2]
print(f"Frames: {len(frame_names)}  {W}×{H}px")

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

# ── PNG-Metadaten-Chunk ────────────────────────────────────────────────────
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
            out += make_text_chunk("FrameAmount", str(frame_count))
    return bytes(out)

# ── Verarbeitung ───────────────────────────────────────────────────────────
new_frames   = {}
remap_arrays = []
total_pc     = 0

for fname in frame_names:
    flat = frames_data[fname].reshape(-1, 4).copy()
    am   = flat[:, 3] > 0

    # Luminanz der sichtbaren Pixel
    lum = 0.299 * flat[:, 0].astype(np.float32) \
        + 0.587 * flat[:, 1].astype(np.float32) \
        + 0.114 * flat[:, 2].astype(np.float32)

    # Player-Color-Maske: opaque + lum > THRESHOLD
    pc_mask = am & (lum > THRESHOLD)
    total_pc += int(np.sum(pc_mask))

    # Ramp-Index: helle Pixel → 176 (hell), dunklere → 183 (mittel)
    # Lineares Mapping innerhalb [THRESHOLD, max_lum] → [RAMP_MAX, RAMP_MIN]
    pc_lum = lum[pc_mask]
    lmax   = float(np.max(pc_lum)) if len(pc_lum) > 0 else 255.0
    lrange = max(lmax - THRESHOLD, 1.0)
    ramp_v = np.clip(
        np.round(RAMP_MIN + (1.0 - (pc_lum - THRESHOLD) / lrange) * (RAMP_MAX - RAMP_MIN)),
        RAMP_MIN, RAMP_MAX
    ).astype(np.uint8)

    remap_flat = np.zeros(H * W, dtype=np.uint8)
    remap_flat[pc_mask] = ramp_v

    # Body: Player-Color-Pixel transparent setzen (damit Remap sauber zeigt)
    flat[pc_mask, 3] = 0

    new_frames[fname] = flat.reshape(H, W, 4)
    remap_arrays.append(remap_flat.reshape(H, W))

print(f"Player-Color-Pixel: {total_pc} ({total_pc/len(frame_names):.0f}/frame)")

# ── Body-ZIP schreiben ─────────────────────────────────────────────────────
with zipfile.ZipFile(BODY_ZIP, 'w', zipfile.ZIP_DEFLATED) as zf:
    for fname in frame_names:
        zf.writestr(fname, write_tga(new_frames[fname]))
    for mname, mdata in meta_data.items():
        zf.writestr(mname, mdata)
print(f"Body: {BODY_ZIP.name}")

# ── Remap-PNG-Sheet schreiben ──────────────────────────────────────────────
sheet_w = COLS * W
sheet_h = ROWS * H
sheet   = np.zeros((sheet_h, sheet_w), dtype=np.uint8)
for i, remap in enumerate(remap_arrays):
    col, row = i % COLS, i // COLS
    sheet[row*H:(row+1)*H, col*W:(col+1)*W] = remap

simg = Image.fromarray(sheet, 'P')
ramp_raw = RAMP_PAL.read_bytes()
outpal   = [0] * 768
for i in range(176, 192):
    outpal[i*3]   = min(255, ramp_raw[i*3]   * 4)
    outpal[i*3+1] = min(255, ramp_raw[i*3+1] * 4)
    outpal[i*3+2] = min(255, ramp_raw[i*3+2] * 4)
simg.putpalette(outpal)
simg.save(REMAP_PNG, transparency=bytes([0] + [255] * 255))
REMAP_PNG.write_bytes(inject_metadata(REMAP_PNG.read_bytes(), W, H, len(frame_names)))

total_remap = sum(int(np.sum(r > 0)) for r in remap_arrays)
print(f"Remap: {REMAP_PNG.name}  {sheet_w}×{sheet_h}px  {total_remap} Pixel ({total_remap/len(frame_names):.0f}/frame)")
print("=== Fertig ===")
