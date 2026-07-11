#!/usr/bin/env python3
# Bäckt synthetische Jugg-Barrels (3 weiße Kanonenrohre, 32 Facings).
# djuggbar.vxl nicht vorhanden → prozedural generiert.
# Frame-Reihenfolge: list(range(31,-1,-1)) wie aot-jugg-turret, UseClassicFacings: True.

import zipfile, struct, json, zlib, math
from pathlib import Path
import numpy as np

BITS = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD/mods/cnc/bits")

DST = 144
CX, CY = DST // 2, DST // 2

BARREL_LENGTH  = 62    # Länge in Pixel
BARREL_WIDTH   = 6     # Breite pro Rohr
N_BARRELS      = 3
BARREL_SPACING = 10    # Abstand zwischen Rohr-Zentren (quer zur Richtung)
BARREL_OFFSET  = 8     # Start-Abstand vom Sprite-Zentrum (Turret-Housing überlappend)

C_MAIN   = (235, 235, 240)
C_EDGE   = (155, 155, 160)
C_TIP    = (190, 190, 195)


def draw_frame(angle_deg):
    frame = np.zeros((DST, DST, 4), dtype=np.uint8)
    rad = math.radians(angle_deg)
    # 0° = N (oben), 90° = E (rechts), clockwise
    dx = math.sin(rad)
    dy = -math.cos(rad)
    # Senkrecht (für Abstand der 3 Rohre)
    px = dy
    py = -dx

    offsets = [-(BARREL_SPACING), 0, BARREL_SPACING]
    for off in offsets:
        bx = CX + px * off
        by = CY + py * off
        for t in range(BARREL_OFFSET, BARREL_OFFSET + BARREL_LENGTH):
            cx_f = bx + dx * t
            cy_f = by + dy * t
            for w in range(-(BARREL_WIDTH // 2), BARREL_WIDTH // 2 + 1):
                fx = int(round(cx_f + px * w))
                fy = int(round(cy_f + py * w))
                if 0 <= fx < DST and 0 <= fy < DST:
                    is_tip  = (t == BARREL_OFFSET + BARREL_LENGTH - 1)
                    is_edge = (abs(w) == BARREL_WIDTH // 2)
                    c = C_TIP if is_tip else (C_EDGE if is_edge else C_MAIN)
                    frame[fy, fx] = [c[0], c[1], c[2], 255]
    return frame


def write_tga_rgba(rgba):
    h, w = rgba.shape[:2]
    hdr = bytearray(18)
    hdr[2] = 2
    struct.pack_into('<H', hdr, 12, w)
    struct.pack_into('<H', hdr, 14, h)
    hdr[16] = 32
    hdr[17] = 0x28
    return bytes(hdr) + bytes(rgba[:, :, [2, 1, 0, 3]].flatten())


# Natürliche Reihenfolge: Frame 0 = N, Frame 1 = NNE ... Frame 31 = NNW
natural = [draw_frame(i * (360.0 / 32)) for i in range(32)]

# Gleiche Reversal wie aot-jugg-turret: output[j] = natural[31-j]
frames = [natural[31 - i] for i in range(32)]

meta = json.dumps({"size": [DST, DST], "crop": [0, 0, DST, DST]}, separators=(',', ':'))
with zipfile.ZipFile(BITS / "aot-jugg-barrel.zip", 'w', zipfile.ZIP_DEFLATED) as z:
    for i, rgba in enumerate(frames):
        z.writestr(f"jugg-barl-{i:04d}.tga", write_tga_rgba(rgba))
        z.writestr(f"jugg-barl-{i:04d}.meta", meta)

print(f"→ aot-jugg-barrel.zip ({DST}px, 32 frames)")
print("Done.")
