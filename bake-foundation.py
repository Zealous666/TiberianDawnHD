#!/usr/bin/env python3
# Bäckt den Fortified Foundation (aot-foundation) Sprite.
# 16 Frames (4-direktionale Wandverbindung: N=bit0, E=bit1, S=bit2, W=bit3).
# Frame-Index = adjacency-Bitmask (0=isoliert, 15=Kreuzung).
# Ausgabe: aot-foundation.png (128×2048, RGBA) + aot-foundation-icon.png (64×48, RGBA).

from pathlib import Path
import numpy as np
from PIL import Image

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
BITS = PROJ / "mods/cnc/bits"
FRAME = 128      # Pixel pro Frame-Seite
BORDER = 6       # Randbreite für unverbundene Seiten (in Pixeln)
NFRAMES = 16

# Farben (warm-grau Beton, ähnlich TD-Bibs)
C_BASE   = np.array([148, 144, 136, 255], dtype=np.uint8)   # Betonfläche
C_EDGE   = np.array([ 80,  76,  70, 255], dtype=np.uint8)   # offene Kante
C_SEAM   = np.array([110, 106,  98, 255], dtype=np.uint8)   # Verbindungsnaht (subtil)
C_CRACK  = np.array([115, 110, 102, 255], dtype=np.uint8)   # Risslinien
rng = np.random.default_rng(42)


def base_texture(size):
    """Erzeugt Beton-Grundtextur mit subtiler Körnung."""
    tex = np.empty((size, size, 4), dtype=np.uint8)
    tex[:] = C_BASE
    noise = rng.integers(-12, 13, (size, size), dtype=np.int16)
    for c in range(3):
        tex[:, :, c] = np.clip(tex[:, :, c].astype(np.int16) + noise, 0, 255).astype(np.uint8)
    # Feine Risslinien (1px, alle ~20px)
    for y in range(0, size, 21):
        tex[y, :, :3] = np.clip(tex[y, :, :3].astype(np.int16) - 15, 0, 255).astype(np.uint8)
    for x in range(0, size, 23):
        tex[:, x, :3] = np.clip(tex[:, x, :3].astype(np.int16) - 10, 0, 255).astype(np.uint8)
    return tex


def bake_frame(adj: int) -> np.ndarray:
    """
    adj = 4-bit Bitmask: bit0=N, bit1=E, bit2=S, bit3=W.
    Rendert die 128×128 Foundation-Fläche mit offenen Rändern wo kein Nachbar.
    """
    f = base_texture(FRAME)
    n_open = not (adj & 0x1)   # Norden frei
    e_open = not (adj & 0x2)   # Osten frei
    s_open = not (adj & 0x4)   # Süden frei
    w_open = not (adj & 0x8)   # Westen frei

    # Offene Seiten: dunkle Randzone (Betonkante sichtbar)
    if n_open:
        f[:BORDER, :] = C_EDGE
    if s_open:
        f[FRAME-BORDER:, :] = C_EDGE
    if w_open:
        f[:, :BORDER] = C_EDGE
    if e_open:
        f[:, FRAME-BORDER:] = C_EDGE

    # Verbindungsnaht: 1px-Linie wo eine Seite verbunden ist (wirkt subtil)
    if not n_open:
        f[0, :] = C_SEAM
    if not s_open:
        f[FRAME-1, :] = C_SEAM
    if not w_open:
        f[:, 0] = C_SEAM
    if not e_open:
        f[:, FRAME-1] = C_SEAM

    # Ecken: wenn beide angrenzenden Seiten offen → volle Ecke abdunkeln
    if n_open and w_open:
        f[:BORDER, :BORDER] = C_EDGE
    if n_open and e_open:
        f[:BORDER, FRAME-BORDER:] = C_EDGE
    if s_open and w_open:
        f[FRAME-BORDER:, :BORDER] = C_EDGE
    if s_open and e_open:
        f[FRAME-BORDER:, FRAME-BORDER:] = C_EDGE

    return f


# --- Haupt-Sprite: 128×2048 (16 Frames vertikal) ---
sheet = np.zeros((NFRAMES * FRAME, FRAME, 4), dtype=np.uint8)
for i in range(NFRAMES):
    sheet[i * FRAME:(i + 1) * FRAME, :, :] = bake_frame(i)

Image.fromarray(sheet, 'RGBA').save(BITS / "aot-foundation.png")
print(f"aot-foundation.png: {FRAME}×{NFRAMES * FRAME} ({NFRAMES} Frames)")

# --- Icon: 64×48 — zentrierter Foundation-Ausschnitt ---
base_frame = bake_frame(0)   # isoliert = alle 4 Seiten sichtbar
icon_src = Image.fromarray(base_frame, 'RGBA').resize((64, 64), Image.LANCZOS)
icon = Image.new('RGBA', (64, 48), (0, 0, 0, 0))
icon.paste(icon_src.crop((0, 8, 64, 56)), (0, 0))
icon.save(BITS / "aot-foundation-icon.png")
print("aot-foundation-icon.png: 64×48")
