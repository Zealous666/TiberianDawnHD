#!/usr/bin/env python3
"""
Extrahiert eine Einheit (ZIP-Archiv) aus einem C&C Remastered MEGv3-Archiv
und konvertiert die enthaltenen DDS-Frames in PNG-Dateien.

Verwendung:
  python3 extract-meg-unit.py <meg-datei> <zip-name-im-meg> <ausgabe-dir>
"""

import struct
import sys
import os
import zipfile
import io

UNENCRYPTED_MEG_ID = 0xFFFFFFFF
MEG_VERSION = 0x3F7D70A4


def parse_meg(path: str) -> dict:
    """Parst MEGv3-Datei und gibt dict {filename: (offset, size)} zurück."""
    with open(path, "rb") as f:
        data = f.read()

    pos = 0

    def read_u16():
        nonlocal pos
        v = struct.unpack_from("<H", data, pos)[0]
        pos += 2
        return v

    def read_u32():
        nonlocal pos
        v = struct.unpack_from("<I", data, pos)[0]
        pos += 4
        return v

    meg_id = read_u32()
    meg_ver = read_u32()

    if meg_id != UNENCRYPTED_MEG_ID or meg_ver != MEG_VERSION:
        raise ValueError("Keine gültige MEGv3-Datei")

    header_size = read_u32()
    num_strings = read_u32()
    num_files = read_u32()
    strings_size = read_u32()
    strings_start = pos

    filenames = []
    for _ in range(num_strings):
        length = read_u16()
        name = data[pos:pos + length].decode("ascii")
        pos += length
        filenames.append(name)

    if pos != strings_start + strings_size:
        raise ValueError(f"Name-Tabelle inkonsistent: erwartet {strings_start + strings_size}, bin bei {pos}")

    contents = {}
    for _ in range(num_files):
        pos += 10  # flags(2) + crc(4) + index(4)
        size = read_u32()
        offset = read_u32()
        name_idx = read_u16()
        contents[filenames[name_idx]] = (offset, size)

    if pos != header_size:
        raise ValueError(f"Unerwartete Position nach Header: {pos} != {header_size}")

    return contents, data


def extract_unit(meg_path: str, zip_key: str, out_dir: str):
    print(f"Lade MEG: {meg_path}")
    contents, raw = parse_meg(meg_path)

    # Suche nach dem Key (case-insensitive)
    found_key = None
    for k in contents:
        if k.upper() == zip_key.upper():
            found_key = k
            break

    if found_key is None:
        print(f"FEHLER: '{zip_key}' nicht im MEG gefunden.")
        print("Vorhandene Einträge (gefiltert):")
        for k in sorted(contents.keys()):
            if "2TNK" in k.upper() or "UNITS" in k.upper():
                print(f"  {k}")
        sys.exit(1)

    offset, size = contents[found_key]
    zip_bytes = raw[offset:offset + size]
    print(f"ZIP gefunden: {found_key} ({size} Bytes)")

    os.makedirs(out_dir, exist_ok=True)

    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as zf:
        entries = sorted(zf.namelist())
        print(f"ZIP enthält {len(entries)} Dateien: {entries[:5]}{'...' if len(entries) > 5 else ''}")

        image_files = [e for e in entries if e.lower().endswith(".tga") or e.lower().endswith(".dds")]
        print(f"Bild-Frames: {len(image_files)}")

        try:
            from PIL import Image
            has_pillow = True
        except ImportError:
            has_pillow = False
            print("Pillow nicht installiert — speichere rohe Dateien")

        for entry in image_files:
            img_data = zf.read(entry)
            base = os.path.splitext(os.path.basename(entry))[0]
            ext = os.path.splitext(entry)[1].lower()

            if has_pillow:
                try:
                    img = Image.open(io.BytesIO(img_data))
                    out_path = os.path.join(out_dir, base + ".png")
                    img.save(out_path)
                    print(f"  → {base}.png ({img.size})")
                except Exception as e:
                    out_path = os.path.join(out_dir, base + ext)
                    with open(out_path, "wb") as f:
                        f.write(img_data)
                    print(f"  → {base}{ext} (PNG-Fehler: {e})")
            else:
                out_path = os.path.join(out_dir, base + ext)
                with open(out_path, "wb") as f:
                    f.write(img_data)

    print(f"\nFertig — Dateien in: {out_dir}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        print(__doc__)
        sys.exit(1)

    extract_unit(sys.argv[1], sys.argv[2], sys.argv[3])
