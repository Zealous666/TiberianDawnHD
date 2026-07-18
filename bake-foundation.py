#!/usr/bin/env python3
# Bäckt den Fortified Foundation (aot-foundation-cell) Sprite aus den TD-Remaster-BIB2-Kacheln.
# Algorithmus: 4-Seiten-Adjacenz-Bitmask (N=1, E=2, S=4, W=8) → 16 Frames.
# Jede Zelle (128×128) wird aus 4 Quadranten (64×64) aus den 4 BIB2-Eck-Kacheln komponiert.
# Ausgabe:
#   aot-foundation-cell.zip  — 16 TGA-Frames + .meta (OpenRA RemasteredFilename-Format)
#   aot-foundation-idle.png  — 1-Frame-PNG für Placer-Preview (Frame 15 = Interior)
#   aot-foundation-icon.png  — 64×48 Baupaletten-Icon

import io
import json
import zipfile
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
tile1 = load_tile(1)  # TC oben: nur N-Kante verschneit, untere ~2/3 sauberer Dreck
tile2 = load_tile(2)  # TR outer corner: N+E edge
tile3 = load_tile(3)  # BL outer corner: S+W edge
tile4 = load_tile(4)  # BC unten: nur S-Kante verschneit, obere ~2/3 sauberer Dreck
tile5 = load_tile(5)  # BR outer corner: S+E edge

# Sauberer, NAHTLOS KACHELBARER Interior (KEIN Schnee, keine sichtbaren Zell-Grenzen im Kern).
# Problem vorher: benachbarte Interior-Zellen sampleten aus verschiedenen Quell-Kacheln
# (tile1 vs tile4) -> an den Zell-Grenzen Diskontinuität -> "Kanten im Kern".
# Loesung: EINE saubere 128×128-Dreckflaeche bauen und seamless machen. Alle Interior-Zellen
# nutzen dieselbe nahtlose Kachel -> Kern kachelt perfekt, keine Grenzen sichtbar.
def make_seamless(img):
    """Center-weighted blend mit halb-gerollter Kopie -> Raender wrappen nahtlos."""
    f = img.astype(np.float32)
    h, w = f.shape[:2]
    rolled = np.roll(np.roll(f, h // 2, axis=0), w // 2, axis=1)
    # Dreieck-Gewicht: 1 in der Mitte, 0 an den Raendern (pro Achse).
    tri_y = (1.0 - np.abs(2.0 * np.arange(h) / (h - 1) - 1.0)).reshape(h, 1, 1)
    tri_x = (1.0 - np.abs(2.0 * np.arange(w) / (w - 1) - 1.0)).reshape(1, w, 1)
    weight = tri_y * tri_x  # 1 Mitte -> Original, 0 Rand -> gerollt (= Original-Mitte, matcht Gegenkante)
    out = f * weight + rolled * (1.0 - weight)
    return np.clip(out, 0, 255).astype(np.uint8)

# Reiner Dreck: TC-untere Haelfte (128×64) ueber BC-obere Haelfte (128×64) = 128×128 sauber.
clean_dirt = np.zeros((128, 128, 4), dtype=np.uint8)
clean_dirt[0:64]   = tile1[64:128]   # TC unten (sauber)
clean_dirt[64:128] = tile4[0:64]     # BC oben (sauber)
INTERIOR = make_seamless(clean_dirt)
# Voll-deckend: seamless-Dreck hat i.d.R. Alpha 255, aber sicherstellen.
INTERIOR[..., 3] = 255
INT_TL = INTERIOR[0:64,   0:64]
INT_TR = INTERIOR[0:64,   64:128]
INT_BL = INTERIOR[64:128, 0:64]
INT_BR = INTERIOR[64:128, 64:128]

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

# (1,1) = beide Nachbarn da = Interior -> saubere Mittelkachel-Quadranten (kein Schnee).
NW_PIECES = {
    (0, 0): tile0[0:64,   0:64],
    (0, 1): tile0[0:64,   64:128],
    (1, 0): tile0[64:128, 0:64],
    (1, 1): INT_TL,
}
NE_PIECES = {
    (0, 0): tile2[0:64,   64:128],
    (0, 1): tile2[0:64,   0:64],
    (1, 0): tile2[64:128, 64:128],
    (1, 1): INT_TR,
}
SW_PIECES = {
    (0, 0): tile3[64:128, 0:64],
    (0, 1): tile3[64:128, 64:128],
    (1, 0): tile3[0:64,   0:64],
    (1, 1): INT_BL,
}
SE_PIECES = {
    (0, 0): tile5[64:128, 64:128],
    (0, 1): tile5[64:128, 0:64],
    (1, 0): tile5[0:64,   64:128],
    (1, 1): INT_BR,
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


# --- Haupt-Sprite: aot-foundation-cell.zip (16 TGA-Frames + .meta, OpenRA-RemasteredFilename-Format) ---
# Format identisch zu aot-ice-cell.zip, aot-subtank-tilt.zip etc.
# Jeder Frame: <name>-{N:04d}.tga + <name>-{N:04d}.meta (JSON {"size":[w,h],"crop":[x,y,w,h]})
NFRAMES = 16
FSIZE   = 128
ZIPNAME = "foundation"
# WICHTIG: MetaRegex in ShpRemasteredLoader.cs verlangt KOMPAKTES JSON ohne Leerzeichen
# (^\{"size":\[w,h\],"crop":\[l,t,r,b\]\}$). json.dumps mit separators=(",",":") -> keine Spaces.
# crop = LTRB (Rectangle.FromLTRB): left,top,right,bottom = 0,0,FSIZE,FSIZE.
meta_str = json.dumps({"size": [FSIZE, FSIZE], "crop": [0, 0, FSIZE, FSIZE]},
                      separators=(",", ":"))

out_zip = BITS / "aot-foundation-cell.zip"
with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as zf:
    for i in range(NFRAMES):
        frame_img = Image.fromarray(bake_frame(i), "RGBA")
        buf = io.BytesIO()
        frame_img.save(buf, format="TGA")
        zf.writestr(f"{ZIPNAME}-{i:04d}.tga", buf.getvalue())
        zf.writestr(f"{ZIPNAME}-{i:04d}.meta", meta_str)
print(f"aot-foundation-cell.zip: {NFRAMES} Frames à {FSIZE}×{FSIZE} TGA")

# --- Placement-Preview: aot-foundation-idle.zip (384×384, 1 Frame, RemasteredFilename) ---
# Zeigt das fertige 3×3-Ergebnis mit korrekten Kanten. MUSS RemasteredFilename (ZIP) sein:
# ueber classic Filename wuerde OpenRA das PNG um Faktor ~5.3 hochskalieren (klassisch->HD)
# -> riesiger, unscharfer Klotz. RemasteredFilename rendert 1:1 (128px = 1 Zelle) -> 384px = 3 Zellen.
#
# 3×3-Layout: jede Zelle bekommt die Config, die sie im vollen 3×3-Block haette (N|E<<1|S<<2|W<<3):
#   NW=6(E+S)   N=14(E+S+W)  NE=12(S+W)
#   W=7(N+E+S)  C=15(alle)   E=13(N+S+W)
#   SW=3(N+E)   S=11(N+E+W)  SE=9(N+W)
PREVIEW_CONFIGS = [
    [6, 14, 12],
    [7, 15, 13],
    [3, 11, 9],
]
preview3x3 = np.zeros((3 * FSIZE, 3 * FSIZE, 4), dtype=np.uint8)
for row in range(3):
    for col in range(3):
        preview3x3[row*FSIZE:(row+1)*FSIZE, col*FSIZE:(col+1)*FSIZE] = bake_frame(PREVIEW_CONFIGS[row][col])

PSIZE = 3 * FSIZE
preview_meta = json.dumps({"size": [PSIZE, PSIZE], "crop": [0, 0, PSIZE, PSIZE]},
                          separators=(",", ":"))
with zipfile.ZipFile(BITS / "aot-foundation-idle.zip", "w", zipfile.ZIP_DEFLATED) as zf:
    buf = io.BytesIO()
    Image.fromarray(preview3x3, "RGBA").save(buf, format="TGA")
    zf.writestr("foundationidle-0000.tga", buf.getvalue())
    zf.writestr("foundationidle-0000.meta", preview_meta)
print(f"aot-foundation-idle.zip: 1 Frame {PSIZE}×{PSIZE} (3×3-Composite)")

# --- Icon: aot-foundation-icon.png wird NICHT mehr hier generiert ---
# Der User setzt manuell mods/management/asset wip/base_smudge.png (64×48) als Baupaletten-Icon.
# Frueher wurde hier ein Icon aus Frame 0 gebacken -> wuerde die manuelle Wahl ueberschreiben.

# --- Preview: alle 16 Frames als 4×4-Grid ---
preview = np.zeros((4 * FSIZE, 4 * FSIZE, 4), dtype=np.uint8)
for i in range(NFRAMES):
    row, col = divmod(i, 4)
    preview[row * FSIZE:(row + 1) * FSIZE, col * FSIZE:(col + 1) * FSIZE] = bake_frame(i)
Image.fromarray(preview, "RGBA").save(TMP / "foundation_preview.png")
print(f"foundation_preview.png: {4*FSIZE}×{4*FSIZE} (4×4-Grid, i=adj-Bitmask)")
