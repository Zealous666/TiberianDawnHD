#!/usr/bin/env python3
# Bäckt die Firehawk-Sprites (aus out/ajet-0000..0031.png, 258x258 uniform) als Zwei-Layer:
#   Body  = volles Truecolor-Sprite, Cockpit-Blau zu Neutralgrau (damit Remap sauber überlagert)
#   Remap = NUR das Cockpit, in Player-Color-Rampe 176-191 (indiziert), Grid 8x4, PngSheet-tEXt.
# Muster: bake-shp-mech.py (Wolverine-Two-Layer). Siehe [[aflt-ajet-system]], [[ts-voxel-playercolor]].

import zipfile, struct, json, zlib
from pathlib import Path
from PIL import Image
import numpy as np

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
SRC  = PROJ / "mods/management/asset wip/ajet/out"
BITS = PROJ / "mods/cnc/bits"
OUT  = "aot-firehawk"
NFRAMES = 32
DST = 258                 # Frame-Kantenlänge (out/ ist 258x258 uniform)

def load(i):
    a = np.asarray(Image.open(SRC / f"ajet-{i:04d}.png").convert("RGBA")).astype(np.int32)
    if a.shape[0] != DST or a.shape[1] != DST:
        raise SystemExit(f"Frame {i} ist {a.shape[:2]}, erwarte {DST}x{DST}")
    return a

def cockpit_mask(a):
    R, G, B, A = a[...,0], a[...,1], a[...,2], a[...,3]
    return (A > 60) & (B > 120) & (B > R + 40) & (B > G + 20)

# --- Body-Layer: Cockpit neutralisieren (Luminanz-Grau), Rest unverändert ---
def write_tga_rgba(a):
    h, w = a.shape[:2]
    hdr = bytearray(18); hdr[2] = 2
    struct.pack_into('<H', hdr, 12, w); struct.pack_into('<H', hdr, 14, h)
    hdr[16] = 32; hdr[17] = 0x28                       # 32-bit, top-left origin (wie Banshee/Wolverine)
    rgba = a.astype(np.uint8)
    return bytes(hdr) + bytes(rgba[:, :, [2,1,0,3]].flatten())   # BGRA

z = zipfile.ZipFile(BITS / f"{OUT}.zip", 'w', zipfile.ZIP_DEFLATED)
meta = json.dumps({"size":[DST,DST],"crop":[0,0,DST,DST]}, separators=(',',':'))
remaps = []
for i in range(NFRAMES):
    a = load(i)
    ck = cockpit_mask(a)
    lum = (0.299*a[...,0] + 0.587*a[...,1] + 0.114*a[...,2]).astype(np.int32)
    body = a.copy()
    body[ck, 0] = body[ck, 1] = body[ck, 2] = lum[ck]    # Cockpit -> Grau
    z.writestr(f"firehawk-{i:04d}.tga", write_tga_rgba(body))
    z.writestr(f"firehawk-{i:04d}.meta", meta)

    # Remap: Cockpit-Luminanz -> Rampe 176(hell)..191(dunkel), Rest 0
    rm = np.zeros((DST, DST), np.uint8)
    if ck.any():
        cl = lum[ck].astype(float)
        lo, hi = np.percentile(cl, 5), np.percentile(cl, 95)
        t = np.clip((cl - lo) / max(1.0, hi - lo), 0, 1)
        rm[ck] = np.round(176 + (1 - t) * 15).astype(np.uint8)   # hell->176, dunkel->191
    remaps.append(rm)
z.close()

# --- Remap-Sheet: Grid 8x4, indiziert, Palette aus aot-vxl-ramp.pal (176-191, *4), tEXt-Chunks ---
COLS = 8; ROWS = (NFRAMES + COLS - 1) // COLS
sheet = np.zeros((ROWS*DST, COLS*DST), np.uint8)
for i, rm in enumerate(remaps):
    r, c = i // COLS, i % COLS
    sheet[r*DST:(r+1)*DST, c*DST:(c+1)*DST] = rm
simg = Image.fromarray(sheet, 'P')
outpal = [0]*768
ramp = open(BITS / "aot-vxl-ramp.pal", 'rb').read()
for i in range(176, 192):
    outpal[i*3]   = min(255, ramp[i*3]*4)
    outpal[i*3+1] = min(255, ramp[i*3+1]*4)
    outpal[i*3+2] = min(255, ramp[i*3+2]*4)
simg.putpalette(outpal)
tmp = BITS / f"{OUT}-remap.png"
simg.save(tmp, transparency=bytes([0] + [255]*255))

def tc(k, v):
    d = k.encode() + b'\x00' + v.encode()
    return struct.pack('>I', len(d)) + b'tEXt' + d + struct.pack('>I', zlib.crc32(b'tEXt'+d))
raw = tmp.read_bytes(); pos, kept = 8, []
while pos < len(raw):
    length = struct.unpack('>I', raw[pos:pos+4])[0]; ct = raw[pos+4:pos+8]
    if ct != b'tEXt': kept.append((ct, raw[pos:pos+12+length]))
    pos += 12 + length
    if ct == b'IEND': break
outb = bytearray(raw[:8])
for ct, chunk in kept:
    outb += chunk
    if ct == b'IHDR':
        outb += tc("FrameSize", f"{DST},{DST}") + tc("FrameAmount", str(NFRAMES))
tmp.write_bytes(outb)
print(f"{OUT}: body zip ({NFRAMES} TGA {DST}px) + remap {COLS}x{ROWS} grid, Rampe 176-191")
