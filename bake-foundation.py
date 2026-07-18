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

# === CORNER-BASED (Dual-Grid Marching Squares) mit DURCHGEHENDEN Kantenzuegen ===
# Face-basiert (4 orthogonale Nachbarn) kann KEINE Innenkurven (Hohlkehle) darstellen -> corner-
# basiert: config = nw|ne<<1|sw<<2|se<<3, Ecke solide wenn alle 4 Zellen am Eckpunkt Foundation.
#
# Kanten-Problem der ersten Fassung: jede Rand-Zelle wiederholte identische 64px-Stuecke aus 4
# VERSCHIEDENEN Quellkacheln -> Franse brach an jeder Naht ab ("wie Puzzle-Stuecke").
# Loesung: EIN durchgehender, seamless-gemachter Kantenstreifen pro Richtung. Alle Rand-Zellen
# derselben Richtung samplen denselben Streifen -> die Franse laeuft fugenlos durch.

def wrap_blend_axis(img, axis):
    """Wrap-Blend NUR entlang einer Achse: Dreieck-Gewicht 1 Mitte -> 0 Rand, Gegenstueck ist
    die halb-gerollte Kopie. Ergebnis kachelt nahtlos in dieser Achse."""
    f = img.astype(np.float32)
    n = f.shape[axis]
    rolled = np.roll(f, n // 2, axis=axis)
    t = (1.0 - np.abs(2.0 * np.arange(n) / (n - 1) - 1.0))
    shape = [1, 1, 1]; shape[axis] = n
    w = t.reshape(shape)
    return np.clip(f * w + rolled * (1.0 - w), 0, 255).astype(np.uint8)

def blend_into(piece, target, axis, side, width=24):
    """Blendet den Rand von `piece` (an `side` der Achse) in die `target`-Werte, damit der
    Anschluss an den Nachbar-Quadranten pixel-kontinuierlich wird. ramp 0 (innen) -> 1 (Randpixel)."""
    out = piece.astype(np.float32)
    tgt = target.astype(np.float32)
    n = piece.shape[axis]
    idx = np.arange(n, dtype=np.float32)
    ramp = np.clip((idx - (n - width)) / (width - 1), 0, 1) if side == 'high' else \
           np.clip(((width - 1) - idx) / (width - 1), 0, 1)
    shape = [1, 1, 1]; shape[axis] = n
    r = ramp.reshape(shape)
    return np.clip(out * (1 - r) + tgt * r, 0, 255).astype(np.uint8)

# --- Durchgehende Kantenstreifen (Zellhaelften), seamless in Laufrichtung ---
# N: TC-Nordhaelfte (64h x 128w), x-seamless. S: BC-Suedhaelfte.
# W: tile0-SW ueber tile3-NW (im Original vertikal benachbart -> kontinuierlich), y-seamless.
# E: tile2-SE ueber tile5-NE, y-seamless.
EDGE_N = wrap_blend_axis(tile1[0:64].copy(),    axis=1)
EDGE_S = wrap_blend_axis(tile4[64:128].copy(),  axis=1)
EDGE_W = wrap_blend_axis(np.vstack([tile0[64:128, 0:64],   tile3[0:64, 0:64]]).copy(),   axis=0)
EDGE_E = wrap_blend_axis(np.vstack([tile2[64:128, 64:128], tile5[0:64, 64:128]]).copy(), axis=0)

# --- Interior-Anschluss: Kanten-Innenraender in die INTERIOR-Werte ueberblenden ---
# Naht N-Kante(y=63) -> Interior(y=64):  EDGE_N rows 40..63 -> INTERIOR rows 40..63.
EDGE_N = blend_into(EDGE_N, INTERIOR[0:64],    axis=0, side='high')
EDGE_S = blend_into(EDGE_S, INTERIOR[64:128],  axis=0, side='low')
EDGE_W = blend_into(EDGE_W, INTERIOR[:, 0:64], axis=1, side='high')
EDGE_E = blend_into(EDGE_E, INTERIOR[:, 64:128], axis=1, side='low')

# --- Konvexe Ecken: Original-Eckquadranten, Anschlussraender in die Kantenstreifen geblendet ---
# Naht liegt IN der Eckzelle (Ecke|Kante als Nachbar-Quadranten) -> Raender angleichen:
# z.B. CORNER_NW[:,63] soll EDGE_N[:,63] entsprechen (rechts schliesst EDGE_N[:,64:] an).
CORNER_NW = blend_into(blend_into(tile0[0:64, 0:64].copy(),     EDGE_N[:, 0:64],   axis=1, side='high'),
                       EDGE_W[0:64],    axis=0, side='high')
CORNER_NE = blend_into(blend_into(tile2[0:64, 64:128].copy(),   EDGE_N[:, 64:128], axis=1, side='low'),
                       EDGE_E[0:64],    axis=0, side='high')
CORNER_SW = blend_into(blend_into(tile3[64:128, 0:64].copy(),   EDGE_S[:, 0:64],   axis=1, side='high'),
                       EDGE_W[64:128],  axis=0, side='low')
CORNER_SE = blend_into(blend_into(tile5[64:128, 64:128].copy(), EDGE_S[:, 64:128], axis=1, side='low'),
                       EDGE_E[64:128],  axis=0, side='low')

# --- Konkav (Innenkurve): Interior mit kleiner Gras-Kerbe an der Aussenspitze ---
# Radialer Falloff zur Quadrant-Aussenecke: Kerbe endet VOR den Quadrant-Naehten (sonst Spruenge).
def make_concave(interior_quad, outer_alpha_quad, corner_xy, lo=25, hi=115, full_r=40, zero_r=60):
    out = interior_quad.copy()
    a = outer_alpha_quad[..., 3].astype(np.float32)
    na = np.clip((a - lo) / (hi - lo), 0.0, 1.0) * 255.0     # aufgesteilte Ecken-Alpha (Kerbe)
    yy, xx = np.mgrid[0:64, 0:64].astype(np.float32)
    dist = np.hypot(xx - corner_xy[0], yy - corner_xy[1])
    w = np.clip((zero_r - dist) / (zero_r - full_r), 0.0, 1.0)  # 1 nahe Ecke, 0 ab zero_r
    alpha = 255.0 - (255.0 - na) * w
    out[..., 3] = np.minimum(out[..., 3], alpha.astype(np.uint8))
    return out

CC_NW = make_concave(INT_TL, tile0[0:64,   0:64],   (0, 0))
CC_NE = make_concave(INT_TR, tile2[0:64,   64:128], (63, 0))
CC_SW = make_concave(INT_BL, tile3[64:128, 0:64],   (0, 63))
CC_SE = make_concave(INT_BR, tile5[64:128, 64:128], (63, 63))

# Pro Quadrant: (corner, h_adjacent, v_adjacent) -> 64×64 Piece.
# c=1: interior. c=0: h&v -> concave; nur h -> W/E-Rand; nur v -> N/S-Rand; sonst Aussenecke.
def _pick(interior, concave, outer, edge_h, edge_v, c, h, v):
    if c:               return interior
    if h and v:         return concave
    if h and not v:     return edge_h
    if v and not h:     return edge_v
    return outer

def q_nw(nw, ne, sw):
    return _pick(INT_TL, CC_NW, CORNER_NW, EDGE_W[0:64],   EDGE_N[:, 0:64],   nw, ne, sw)

def q_ne(ne, nw, se):
    return _pick(INT_TR, CC_NE, CORNER_NE, EDGE_E[0:64],   EDGE_N[:, 64:128], ne, nw, se)

def q_sw(sw, se, nw):
    return _pick(INT_BL, CC_SW, CORNER_SW, EDGE_W[64:128], EDGE_S[:, 0:64],   sw, se, nw)

def q_se(se, sw, ne):
    return _pick(INT_BR, CC_SE, CORNER_SE, EDGE_E[64:128], EDGE_S[:, 64:128], se, sw, ne)


def bake_frame(config: int) -> np.ndarray:
    """config = nw|ne<<1|sw<<2|se<<3 (Ecken-Solidität). Komponiert 4 Quadranten."""
    nw = (config >> 0) & 1
    ne = (config >> 1) & 1
    sw = (config >> 2) & 1
    se = (config >> 3) & 1

    frame = np.zeros((128, 128, 4), dtype=np.uint8)
    frame[0:64,   0:64]   = q_nw(nw, ne, sw)
    frame[0:64,   64:128] = q_ne(ne, nw, se)
    frame[64:128, 0:64]   = q_sw(sw, se, nw)
    frame[64:128, 64:128] = q_se(se, sw, ne)
    return frame


def corner_config(present, x, y):
    """Runtime-identische Ecken-Config für Zelle (x,y): Ecke solide wenn alle 4 Zellen um den
    Eckpunkt in `present` sind. Ecke nw=Punkt(x,y), ne=(x+1,y), sw=(x,y+1), se=(x+1,y+1)."""
    def solid(cx, cy):
        return all((cx+dx, cy+dy) in present for dx in (-1, 0) for dy in (-1, 0))
    nw = 1 if solid(x,   y  ) else 0
    ne = 2 if solid(x+1, y  ) else 0
    sw = 4 if solid(x,   y+1) else 0
    se = 8 if solid(x+1, y+1) else 0
    return nw | ne | sw | se


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
# 3×3-Layout: Config je Zelle runtime-identisch via corner_config über das volle 3×3-Present-Set.
PREVIEW_PRESENT = {(x, y) for x in range(3) for y in range(3)}
preview3x3 = np.zeros((3 * FSIZE, 3 * FSIZE, 4), dtype=np.uint8)
for row in range(3):
    for col in range(3):
        cfg = corner_config(PREVIEW_PRESENT, col, row)
        preview3x3[row*FSIZE:(row+1)*FSIZE, col*FSIZE:(col+1)*FSIZE] = bake_frame(cfg)

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
