#!/usr/bin/env python3
"""
Extrahiert Dateien direkt aus TS MIX-Archiven.
Implementiert beide Hash-Algorithmen aus PackageEntry.cs:
  - Classic (TD/RA): rotate-and-add
  - CRC32 (TS/RA2): CRC32 mit TS-spezifischem Padding

Verwendung:
  python3 extract-from-mix.py <mix-datei> <ausgabe-dir> <dateiname> [<dateiname> ...]
"""

import struct
import sys
import os

# ── CRC32-Tabelle (Standard IEEE 802.3) ────────────────────────────────────────
def _make_crc_table():
    table = []
    for i in range(256):
        crc = i
        for _ in range(8):
            if crc & 1:
                crc = (crc >> 1) ^ 0xEDB88320
            else:
                crc >>= 1
        table.append(crc & 0xFFFFFFFF)
    return table

_CRC_TABLE = _make_crc_table()

def crc32_calculate(data: bytes) -> int:
    crc = 0
    for b in data:
        crc = (crc >> 8) ^ _CRC_TABLE[(crc ^ b) & 0xFF]
    return crc & 0xFFFFFFFF


# ── Hash-Algorithmen aus PackageEntry.cs ───────────────────────────────────────

def hash_classic(name: str) -> int:
    """TD/RA MIX: rotate-and-add über uint32-Blöcke des gepadded Großbuchstaben-Namens."""
    upper = name.upper()
    padding = (4 - len(upper) % 4) % 4
    padded = (upper + '\x00' * padding).encode('ascii')
    result = 0
    for i in range(0, len(padded), 4):
        chunk = struct.unpack_from('<I', padded, i)[0]
        result = ((result << 1) | (result >> 31)) & 0xFFFFFFFF
        result = (result + chunk) & 0xFFFFFFFF
    return result


def hash_crc32(name: str) -> int:
    """TS/RA2 MIX: CRC32 mit TS-spezifischem Padding (Länge-mod-4 als Fill-Byte)."""
    upper = name.upper()
    length = len(upper)
    padding = (4 - length % 4) % 4
    if padding == 0:
        data = upper.encode('ascii')
    else:
        remainder = length % 4
        fill = chr(remainder)
        padded = upper + fill + upper[length - (4 - padding - 1):length - (4 - padding - 1) + padding - 1]
        # Nach PackageEntry.cs CRC32-Branch:
        # upperPaddedName[length] = (char)(length - lengthRoundedDownToFour)  = remainder
        # upperPaddedName[length+1..] = upperPaddedName[lengthRoundedDownToFour]
        chars = list(upper) + ['\x00'] * padding
        length_rounded = (length // 4) * 4
        chars[length] = chr(length - length_rounded)  # remainder
        for p in range(1, padding):
            chars[length + p] = upper[length_rounded] if length_rounded < length else '\x00'
        data = ''.join(chars).encode('ascii')
    return crc32_calculate(data)


def compute_hashes(filename: str):
    return hash_classic(filename), hash_crc32(filename)


# ── MIX-Parser ────────────────────────────────────────────────────────────────

def parse_mix(path: str):
    """
    Gibt zurück: dict { hash(uint32) → (offset, size) }, data_start
    Unterstützt Classic (TD/RA) und New (TS/RA2, unverschlüsselt).
    """
    with open(path, 'rb') as f:
        data = f.read()

    # Format-Erkennung: erstes uint16 != 0 → Classic (TD/RA)
    first_word = struct.unpack_from('<H', data, 0)[0]
    is_cnc = first_word != 0

    if is_cnc:
        # Classic (TD/RA): [uint16 file_count][uint32 body_size][entries...]
        file_count = first_word
        header_start = 6
    else:
        # New TS/RA2: [uint16 flags=0][uint16 flags2][uint16 file_count][uint32 body_size][entries...]
        # OpenRA ruft ParseHeader(stream, offset=4) auf:
        # → file_count an Offset 4, body_size an Offset 6, entries ab Offset 10
        flags = struct.unpack_from('<H', data, 2)[0]
        if flags & 0x2:
            raise ValueError(f"{path}: verschlüsselt (Blowfish) — nur OpenRA's ts-Mod kann das lesen")
        file_count = struct.unpack_from('<H', data, 4)[0]
        header_start = 10

    entries = {}
    for i in range(file_count):
        off = header_start + i * 12
        h, rel_off, size = struct.unpack_from('<III', data, off)
        entries[h] = (rel_off, size)

    data_start = header_start + file_count * 12
    return entries, data_start, data


def extract_from_mix(mix_path: str, out_dir: str, filenames: list[str]) -> list[str]:
    """
    Versucht jede der angegebenen Dateien aus mix_path zu extrahieren.
    Gibt Liste der extrahierten Dateinamen zurück.
    """
    try:
        entries, data_start, raw = parse_mix(mix_path)
    except Exception as e:
        print(f"  Überspringe {os.path.basename(mix_path)}: {e}")
        return []

    extracted = []
    for filename in filenames:
        h_classic, h_crc32 = compute_hashes(filename)
        entry = entries.get(h_crc32) or entries.get(h_classic)
        if entry is None:
            continue
        rel_off, size = entry
        abs_off = data_start + rel_off
        file_data = raw[abs_off:abs_off + size]
        out_path = os.path.join(out_dir, filename)
        with open(out_path, 'wb') as f:
            f.write(file_data)
        print(f"  ✓ {filename} ({size:,} bytes) → {out_path}")
        extracted.append(filename)
    return extracted


# ── Main ──────────────────────────────────────────────────────────────────────

if __name__ == '__main__':
    if len(sys.argv) < 4:
        print(f"Verwendung: {sys.argv[0]} <mix-datei> <ausgabe-dir> <dateiname> [...]")
        sys.exit(1)

    mix_path = sys.argv[1]
    out_dir = sys.argv[2]
    filenames = sys.argv[3:]

    os.makedirs(out_dir, exist_ok=True)
    extracted = extract_from_mix(mix_path, out_dir, filenames)

    missing = [f for f in filenames if f not in extracted]
    if missing:
        print(f"  Nicht gefunden: {', '.join(missing)}")
        sys.exit(2)
