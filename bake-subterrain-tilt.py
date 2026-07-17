#!/usr/bin/env python3
# Bäckt progressive Tilt-Frames für Subterrain-Einheiten (Devil's Tongue + SubAPC).
# Erzeugt je 4 Tilt-Stufen (15%..60% Kompression von unten) pro Facing.
# Ausgabe: aot-subtank-tilt.zip + aot-subtank-tilt-remap.png (und sapc-Variante).
#
# Layout: Facing 0..31, je 4 Tilt-Frames → 128 Frames gesamt (ZIP + Remap-Sheet).
# Frame-Index = facing_idx * 4 + tilt_step (0=schwach, 3=stark)
#
# Tilt-Richtung: Kompression von unten (Top bleibt fest, Bottom verschwindet).
# Entspricht "Nase zuerst ins Erdreich" in der isometrischen Ansicht (Süd-Facing).

import zipfile, struct, json, zlib
from pathlib import Path
from PIL import Image
import numpy as np

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
BITS = PROJ / "mods/cnc/bits"

TILT_STEPS = [0.15, 0.30, 0.45, 0.60]   # Kompressionsgrade (15%..60% von unten)
COLS_OUT = 8                               # Spalten im Remap-Output-Sheet


def load_tga(data: bytes) -> np.ndarray:
    """Lädt 32-bit TGA (BGRA, top-left-origin) → RGBA numpy array."""
    id_len = data[0]
    w = struct.unpack_from('<H', data, 12)[0]
    h = struct.unpack_from('<H', data, 14)[0]
    bpp = data[16]
    top_left = bool(data[17] & 0x20)
    offset = 18 + id_len
    px_size = bpp // 8
    raw = np.frombuffer(data[offset:offset + w * h * px_size], np.uint8).reshape(h, w, px_size)
    rgba = np.zeros((h, w, 4), np.uint8)
    rgba[:, :, 0] = raw[:, :, 2]   # B→R
    rgba[:, :, 1] = raw[:, :, 1]
    rgba[:, :, 2] = raw[:, :, 0]   # R→B
    rgba[:, :, 3] = raw[:, :, 3]
    if not top_left:
        rgba = rgba[::-1]
    return rgba


def write_tga(rgba: np.ndarray) -> bytes:
    """Schreibt RGBA numpy array → 32-bit TGA bytes (BGRA, top-left-origin)."""
    h, w = rgba.shape[:2]
    hdr = bytearray(18)
    hdr[2] = 2                                   # Uncompressed TrueColor
    struct.pack_into('<H', hdr, 12, w)
    struct.pack_into('<H', hdr, 14, h)
    hdr[16] = 32
    hdr[17] = 0x28                               # Top-left origin
    bgra = rgba[:, :, [2, 1, 0, 3]].flatten()
    return bytes(hdr) + bytes(bgra)


def tilt_rgba(rgba: np.ndarray, tilt: float) -> np.ndarray:
    """Komprimiert RGBA-Frame von unten um tilt (0=keine Änderung, 0.6=60% weg)."""
    h = rgba.shape[0]
    compress = 1.0 - tilt
    y_src_f = np.arange(h, dtype=float) / compress
    y_src_i = np.round(y_src_f).astype(int)
    valid = y_src_i < h
    result = np.zeros_like(rgba)
    dst_idx = np.where(valid)[0]
    result[dst_idx] = rgba[y_src_i[dst_idx]]
    return result


def tilt_indexed(frame: np.ndarray, tilt: float) -> np.ndarray:
    """Komprimiert indiziertes 2D-Array (Palette-Indices) von unten – kein Interpolieren."""
    h = frame.shape[0]
    compress = 1.0 - tilt
    y_src_f = np.arange(h, dtype=float) / compress
    y_src_i = np.round(y_src_f).astype(int)
    valid = y_src_i < h
    result = np.zeros_like(frame)
    dst_idx = np.where(valid)[0]
    result[dst_idx] = frame[y_src_i[dst_idx]]
    return result


def add_text_chunk(raw: bytes, key: str, value: str) -> bytes:
    """Fügt tEXt-Chunk nach IHDR in PNG ein."""
    d = key.encode() + b'\x00' + value.encode()
    chunk = struct.pack('>I', len(d)) + b'tEXt' + d + struct.pack('>I', zlib.crc32(b'tEXt' + d))
    pos, result = 8, bytearray(raw[:8])
    while pos < len(raw):
        length = struct.unpack('>I', raw[pos:pos + 4])[0]
        ct = raw[pos + 4:pos + 8]
        chunk_data = raw[pos:pos + 12 + length]
        result += chunk_data
        if ct == b'IHDR':
            result += chunk
        pos += 12 + length
        if ct == b'IEND':
            break
    return bytes(result)


def bake_unit(src_zip_name: str, src_remap_name: str,
              out_zip_name: str, out_remap_name: str,
              stem: str, frame_size: int, facings: int = 32):
    """Bäckt Tilt-Frames für eine Einheit."""
    n_out = facings * len(TILT_STEPS)

    # --- Body ZIP ---
    src_z = zipfile.ZipFile(BITS / src_zip_name)
    meta_json = json.dumps({"size": [frame_size, frame_size],
                            "crop": [0, 0, frame_size, frame_size]},
                           separators=(',', ':'))

    out_z = zipfile.ZipFile(BITS / out_zip_name, 'w', zipfile.ZIP_DEFLATED)
    for fi in range(facings):
        rgba = load_tga(src_z.read(f"{stem}-{fi:04d}.tga"))
        for ti, tilt in enumerate(TILT_STEPS):
            out_idx = fi * len(TILT_STEPS) + ti
            tilted = tilt_rgba(rgba, tilt)
            out_z.writestr(f"{stem}-tilt-{out_idx:04d}.tga", write_tga(tilted))
            out_z.writestr(f"{stem}-tilt-{out_idx:04d}.meta", meta_json)
    out_z.close()

    # --- Remap PNG ---
    src_img = Image.open(BITS / src_remap_name)
    src_arr = np.asarray(src_img)                # (ROWS*FS, COLS*FS), indexed values
    src_cols = src_img.width // frame_size       # Quell-Spaltenanzahl (8)

    rows_out = (n_out + COLS_OUT - 1) // COLS_OUT
    sheet = np.zeros((rows_out * frame_size, COLS_OUT * frame_size), dtype=np.uint8)

    for fi in range(facings):
        src_col = fi % src_cols
        src_row = fi // src_cols
        frame = src_arr[src_row * frame_size:(src_row + 1) * frame_size,
                        src_col * frame_size:(src_col + 1) * frame_size].copy()
        for ti, tilt in enumerate(TILT_STEPS):
            out_idx = fi * len(TILT_STEPS) + ti
            tilted = tilt_indexed(frame, tilt)
            dst_col = out_idx % COLS_OUT
            dst_row = out_idx // COLS_OUT
            sheet[dst_row * frame_size:(dst_row + 1) * frame_size,
                  dst_col * frame_size:(dst_col + 1) * frame_size] = tilted

    # Palette von Quelle übernehmen
    out_img = Image.fromarray(sheet, 'P')
    out_img.putpalette(src_img.getpalette())

    tmp = BITS / out_remap_name
    out_img.save(tmp, transparency=bytes([0] + [255] * 255))
    raw = tmp.read_bytes()
    raw = add_text_chunk(raw, "FrameSize", f"{frame_size},{frame_size}")
    raw = add_text_chunk(raw, "FrameAmount", str(n_out))
    tmp.write_bytes(raw)

    print(f"{out_zip_name}: {n_out} Frames ({facings} Facings × {len(TILT_STEPS)} Tilt-Stufen), {frame_size}px")
    print(f"{out_remap_name}: {COLS_OUT}×{rows_out} Grid")


if __name__ == "__main__":
    # Devil's Tongue (SUBTANK) – 215×215, 32 Facings
    bake_unit(
        src_zip_name="aot-subtank.zip",
        src_remap_name="aot-subtank-remap.png",
        out_zip_name="aot-subtank-tilt.zip",
        out_remap_name="aot-subtank-tilt-remap.png",
        stem="subtank",
        frame_size=215,
    )

    # Subterranean APC (SAPC) – 177×177, 32 Facings
    bake_unit(
        src_zip_name="aot-sapc.zip",
        src_remap_name="aot-sapc-remap.png",
        out_zip_name="aot-sapc-tilt.zip",
        out_remap_name="aot-sapc-tilt-remap.png",
        stem="sapc",
        frame_size=177,
    )
