#!/usr/bin/env python3
"""
Erstellt aot-ttnk-toxin.zip: FTNK.ZIP aus MEG extrahieren,
die weißen Gas-Tanks auf hell-leuchtendes Grün recoloren,
als OpenRA-kompatibles TGA+meta ZIP neu packen.

Verwendung:
  python3 recolor-ttnk-toxin.py

Ausgabe:
  mods/cnc/bits/aot-ttnk-toxin.zip
  mods/management/asset wip/ttnk/toxin/ (Preview-PNGs)
"""

import struct
import sys
import os
import zipfile
import io

# ── Pfade ─────────────────────────────────────────────────────────────────────
MEG_PATH = (
    "/Users/moritzgiuliani/Library/Application Support/Steam/steamapps/common/"
    "CnCRemastered/Data/TEXTURES_TD_SRGB.MEG"
)
ZIP_KEY = "DATA/ART/TEXTURES/SRGB/TIBERIAN_DAWN/UNITS/FTNK.ZIP"
OUT_ZIP  = "mods/cnc/bits/aot-ttnk-toxin.zip"
PREVIEW_DIR = "mods/management/asset wip/ttnk/toxin"

# ── Zielfarbe: hell-leuchtendes Giftgrün ──────────────────────────────────────
TARGET_R, TARGET_G, TARGET_B = 160, 220, 0

# ── Gas-Tank-Erkennung: helle, fast-weiße Pixel (schwach gesättigte Grautöne) ─
# Kriterium: Helligkeit > 160, Sättigung niedrig (max Kanal - min Kanal < 40)
def is_gas_tank_pixel(r, g, b, a):
    if a < 128:
        return False
    # schwach gesättigt = grau/weiß (auch im Schatten)
    hi = max(r, g, b)
    lo = min(r, g, b)
    saturation = hi - lo
    if saturation > 50:
        return False
    # Mindesthelligkeit tief genug für Schatten
    if hi < 60:
        return False
    return True

def recolor_pixel(r, g, b, a):
    """Erhält Helligkeit, ersetzt Farbe durch Grün."""
    brightness = (r + g + b) / 765.0  # 0..1
    nr = int(TARGET_R * brightness)
    ng = int(TARGET_G * brightness)
    nb = int(TARGET_B * brightness)
    # Mindestsättigung für leuchtend grün auch in dunklen Bereichen
    ng = max(ng, int(TARGET_G * 0.4))
    return (min(nr, 255), min(ng, 255), min(nb, 255), a)

# ── MEGv3 Parser ──────────────────────────────────────────────────────────────
UNENCRYPTED_MEG_ID = 0xFFFFFFFF
MEG_VERSION = 0x3F7D70A4

def parse_meg(path):
    with open(path, "rb") as f:
        data = f.read()
    pos = 0
    def ru16():
        nonlocal pos
        v = struct.unpack_from("<H", data, pos)[0]; pos += 2; return v
    def ru32():
        nonlocal pos
        v = struct.unpack_from("<I", data, pos)[0]; pos += 4; return v

    meg_id = ru32(); meg_ver = ru32()
    if meg_id != UNENCRYPTED_MEG_ID or meg_ver != MEG_VERSION:
        raise ValueError("Keine gültige MEGv3-Datei")
    header_size = ru32(); num_strings = ru32(); num_files = ru32()
    strings_size = ru32(); strings_start = pos

    filenames = []
    for _ in range(num_strings):
        length = ru16()
        filenames.append(data[pos:pos+length].decode("ascii"))
        pos += length
    if pos != strings_start + strings_size:
        raise ValueError("Name-Tabelle inkonsistent")

    contents = {}
    for _ in range(num_files):
        pos += 10
        size = ru32(); offset = ru32(); name_idx = ru16()
        contents[filenames[name_idx]] = (offset, size)
    return contents, data

# ── TGA lesen (uncompressed RGBA, type 2 oder 10) ────────────────────────────
def read_tga(raw):
    id_len = raw[0]; cmap_type = raw[1]; img_type = raw[2]
    w = struct.unpack_from("<H", raw, 12)[0]
    h = struct.unpack_from("<H", raw, 14)[0]
    bpp = raw[16]
    data_start = 18 + id_len + (cmap_type * struct.unpack_from("<H", raw, 5)[0] * ((raw[7] + 7) // 8))

    pixels = []
    if img_type == 2:  # uncompressed
        if bpp == 32:
            for i in range(w * h):
                b, g, r, a = raw[data_start + i*4 : data_start + i*4 + 4]
                pixels.append((r, g, b, a))
        elif bpp == 24:
            for i in range(w * h):
                b, g, r = raw[data_start + i*3 : data_start + i*3 + 3]
                pixels.append((r, g, b, 255))
    elif img_type == 10:  # RLE compressed
        i = data_start
        while len(pixels) < w * h:
            hdr = raw[i]; i += 1
            count = (hdr & 0x7F) + 1
            if hdr & 0x80:  # RLE packet
                if bpp == 32:
                    b, g, r, a = raw[i:i+4]; i += 4
                    pixels.extend([(r, g, b, a)] * count)
                else:
                    b, g, r = raw[i:i+3]; i += 3
                    pixels.extend([(r, g, b, 255)] * count)
            else:  # raw packet
                for _ in range(count):
                    if bpp == 32:
                        b, g, r, a = raw[i:i+4]; i += 4
                        pixels.append((r, g, b, a))
                    else:
                        b, g, r = raw[i:i+3]; i += 3
                        pixels.append((r, g, b, 255))
    else:
        raise ValueError(f"Unbekannter TGA-Typ: {img_type}")
    return w, h, pixels

# ── TGA schreiben (uncompressed RGBA, type 2) ─────────────────────────────────
def write_tga(w, h, pixels):
    hdr = bytearray(18)
    hdr[2] = 2; hdr[12:14] = struct.pack("<H", w); hdr[14:16] = struct.pack("<H", h)
    hdr[16] = 32; hdr[17] = 8
    body = bytearray(w * h * 4)
    for i, (r, g, b, a) in enumerate(pixels):
        body[i*4:i*4+4] = bytes([b, g, r, a])
    return bytes(hdr) + bytes(body)

# ── Hauptlogik ────────────────────────────────────────────────────────────────
def main():
    try:
        from PIL import Image as PILImage
        has_pillow = True
    except ImportError:
        has_pillow = False
        print("Hinweis: Pillow nicht installiert, keine PNG-Previews")

    print(f"Lade MEG: {MEG_PATH}")
    contents, raw = parse_meg(MEG_PATH)

    # FTNK.ZIP aus MEG
    found_key = None
    for k in contents:
        if k.upper() == ZIP_KEY.upper():
            found_key = k; break
    if not found_key:
        # Fallback: Backslash
        alt = ZIP_KEY.replace("/", "\\")
        for k in contents:
            if k.upper() == alt.upper():
                found_key = k; break
    if not found_key:
        print("FEHLER: FTNK.ZIP nicht im MEG gefunden")
        print("Vorhandene FTNK-Einträge:")
        for k in sorted(contents):
            if "FTNK" in k.upper():
                print(f"  {k}")
        sys.exit(1)

    offset, size = contents[found_key]
    zip_bytes = raw[offset:offset+size]
    print(f"FTNK.ZIP gefunden ({size} Bytes)")

    os.makedirs(PREVIEW_DIR, exist_ok=True)
    os.makedirs(os.path.dirname(OUT_ZIP), exist_ok=True)

    out_entries = {}  # name → bytes
    recolored_count = 0
    total_pixels_changed = 0

    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as zf:
        entries = sorted(zf.namelist())
        tga_files = [e for e in entries if e.lower().endswith(".tga")]
        meta_files = [e for e in entries if not e.lower().endswith(".tga")]

        print(f"Frames: {len(tga_files)} TGAs + {len(meta_files)} Meta-Dateien")

        # Meta-Dateien unverändert übernehmen
        for mf in meta_files:
            out_entries[mf] = zf.read(mf)

        for tga_name in tga_files:
            tga_raw = zf.read(tga_name)
            w, h, pixels = read_tga(tga_raw)

            changed = 0
            new_pixels = []
            for r, g, b, a in pixels:
                if is_gas_tank_pixel(r, g, b, a):
                    new_pixels.append(recolor_pixel(r, g, b, a))
                    changed += 1
                else:
                    new_pixels.append((r, g, b, a))

            total_pixels_changed += changed
            if changed > 0:
                recolored_count += 1

            out_entries[tga_name] = write_tga(w, h, new_pixels)

            # Preview PNG
            if has_pillow:
                base = os.path.splitext(os.path.basename(tga_name))[0]
                preview_path = os.path.join(PREVIEW_DIR, f"{base}.png")
                img = PILImage.new("RGBA", (w, h))
                img.putdata(new_pixels)
                img.save(preview_path)

    print(f"Recolored: {recolored_count}/{len(tga_files)} Frames, {total_pixels_changed} Pixel total")

    # Neues ZIP schreiben
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_STORED) as zout:
        for name, data in sorted(out_entries.items()):
            zout.writestr(name, data)
    with open(OUT_ZIP, "wb") as f:
        f.write(buf.getvalue())
    print(f"Geschrieben: {OUT_ZIP}")
    if has_pillow:
        print(f"Preview-PNGs: {PREVIEW_DIR}/")

if __name__ == "__main__":
    main()
