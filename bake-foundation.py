#!/usr/bin/env python3
# Bäckt den Fortified Foundation (aot-foundation-cell) Sprite aus den TD-Remaster-BIB2-Kacheln.
# Algorithmus: 4-Seiten-Adjacenz-Bitmask (N=1, E=2, S=4, W=8) → 16 Frames.
# Jede Zelle (128×128) wird aus 4 Quadranten (64×64) aus den 4 BIB2-Eck-Kacheln komponiert.
# Ausgabe: aot-foundation-cell.png (128×2048, 16 Frames vertikal) + aot-foundation-icon.png.

from pathlib import Path
import numpy as np
from PIL import Image

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
BITS = PROJ / "mods/cnc/bits"
TMP  = Path("/private/tmp")

BIB_BASE = r"bib-DATA\ART\TEXTURES\SRGB\TIBERIAN_DAWN\TERRAIN\TEMPERATE\BIB2\BIB2.TEM"

def load_tile(idx: int) -> np.ndarray:
    path = TMP / f"{BIB_BASE}-{idx:04d}.png"
    img = Image.open(path).convert("RGBA")
    assert img.size == (128, 128), f"Unexpected size: {img.size} for {path}"
    return np.array(img)

# BIB2 Layout (3×2):  tile0(TL) tile1(TC) tile2(TR)
#                     tile3(BL) tile4(BC) tile5(BR)
tile0 = load_tile(0)  # TL outer corner: N+W edge
tile2 = load_tile(2)  # TR outer corner: N+E edge
tile3 = load_tile(3)  # BL outer corner: S+W edge
tile5 = load_tile(5)  # BR outer corner: S+E edge

# Jede Kachel wird in 4×64×64 Quadranten aufgeteilt.
# NW quadrant = rows[0:64], cols[0:64]; NE = rows[0:64], cols[64:128]; usw.
#
# Kompositions-Tabelle (face-based, 2 Bits pro Quadrant):
#
# NW-Quadrant — N-Bit & W-Bit:
#   N=0, W=0  → Außenecke NW   = tile0[0:64, 0:64]
#   N=0, W=1  → N-Kante         = tile0[0:64, 64:128]
#   N=1, W=0  → W-Kante         = tile0[64:128, 0:64]
#   N=1, W=1  → Interior        = tile0[64:128, 64:128]
#
# NE-Quadrant — N-Bit & E-Bit:
#   N=0, E=0  → Außenecke NE   = tile2[0:64, 64:128]
#   N=0, E=1  → N-Kante         = tile2[0:64, 0:64]
#   N=1, E=0  → E-Kante         = tile2[64:128, 64:128]
#   N=1, E=1  → Interior        = tile2[64:128, 0:64]
#
# SW-Quadrant — S-Bit & W-Bit:
#   S=0, W=0  → Außenecke SW   = tile3[64:128, 0:64]
#   S=0, W=1  → S-Kante         = tile3[64:128, 64:128]
#   S=1, W=0  → W-Kante         = tile3[0:64, 0:64]
#   S=1, W=1  → Interior        = tile3[0:64, 64:128]
#
# SE-Quadrant — S-Bit & E-Bit:
#   S=0, E=0  → Außenecke SE   = tile5[64:128, 64:128]
#   S=0, E=1  → S-Kante         = tile5[64:128, 0:64]
#   S=1, E=0  → E-Kante         = tile5[0:64, 64:128]
#   S=1, E=1  → Interior        = tile5[0:64, 0:64]

NW_PIECES = {
    (0, 0): tile0[0:64,   0:64],
    (0, 1): tile0[0:64,   64:128],
    (1, 0): tile0[64:128, 0:64],
    (1, 1): tile0[64:128, 64:128],
}
NE_PIECES = {
    (0, 0): tile2[0:64,   64:128],
    (0, 1): tile2[0:64,   0:64],
    (1, 0): tile2[64:128, 64:128],
    (1, 1): tile2[64:128, 0:64],
}
SW_PIECES = {
    (0, 0): tile3[64:128, 0:64],
    (0, 1): tile3[64:128, 64:128],
    (1, 0): tile3[0:64,   0:64],
    (1, 1): tile3[0:64,   64:128],
}
SE_PIECES = {
    (0, 0): tile5[64:128, 64:128],
    (0, 1): tile5[64:128, 0:64],
    (1, 0): tile5[0:64,   64:128],
    (1, 1): tile5[0:64,   0:64],
}


def bake_frame(adj: int) -> np.ndarray:
    """adj = N|E<<1|S<<2|W<<3. Composites 4 quadrants into a 128×128 RGBA frame."""
    n = (adj >> 0) & 1
    e = (adj >> 1) & 1
    s = (adj >> 2) & 1
    w = (adj >> 3) & 1

    frame = np.zeros((128, 128, 4), dtype=np.uint8)
    frame[0:64,   0:64]   = NW_PIECES[(n, w)]
    frame[0:64,   64:128] = NE_PIECES[(n, e)]
    frame[64:128, 0:64]   = SW_PIECES[(s, w)]
    frame[64:128, 64:128] = SE_PIECES[(s, e)]
    return frame


# --- Haupt-Sprite: 128×2048 (16 Frames vertikal, Frame 0 oben) ---
NFRAMES = 16
FSIZE   = 128
sheet = np.zeros((NFRAMES * FSIZE, FSIZE, 4), dtype=np.uint8)
for i in range(NFRAMES):
    sheet[i * FSIZE:(i + 1) * FSIZE] = bake_frame(i)

out_sheet = BITS / "aot-foundation-cell.png"
Image.fromarray(sheet, "RGBA").save(out_sheet)
print(f"aot-foundation-cell.png: {FSIZE}×{NFRAMES * FSIZE} ({NFRAMES} Frames)")

# --- Placement-Preview (128×128): Frame 15 = vollständig Interior (keine Kanten sichtbar) ---
# Eigene Datei notwendig: OpenRA lädt jede PNG-Referenz als eigenständige Sprite-Datei,
# Start: 15 auf dem 16-Frame-Sheet würde deshalb scheitern (kein Frame 15 in 1-Frame-PNG).
Image.fromarray(bake_frame(15), "RGBA").save(BITS / "aot-foundation-idle.png")
print("aot-foundation-idle.png: 128×128 (1 Frame, Interior = Placement-Preview)")

# --- Icon (64×48): Frame 0 = isolierte Zelle (alle Außenecken) ---
icon_src = Image.fromarray(bake_frame(0), "RGBA").resize((64, 64), Image.LANCZOS)
icon = Image.new("RGBA", (64, 48), (0, 0, 0, 0))
icon.paste(icon_src.crop((0, 8, 64, 56)), (0, 0))
icon.save(BITS / "aot-foundation-icon.png")
print("aot-foundation-icon.png: 64×48")

# --- Preview: alle 16 Frames als 4×4-Grid ---
preview = np.zeros((4 * FSIZE, 4 * FSIZE, 4), dtype=np.uint8)
for i in range(NFRAMES):
    row, col = divmod(i, 4)
    preview[row * FSIZE:(row + 1) * FSIZE, col * FSIZE:(col + 1) * FSIZE] = bake_frame(i)
Image.fromarray(preview, "RGBA").save(TMP / "foundation_preview.png")
print(f"foundation_preview.png: {4*FSIZE}×{4*FSIZE} (4×4-Grid, i=adj-Bitmask)")
