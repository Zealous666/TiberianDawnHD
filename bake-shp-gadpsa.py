#!/usr/bin/env python3
# Bäckt gadpsa.shp (TS Deployed Sensor Array) als In-Game-Sprite.
# Pipeline: frame 0 → Scale3x (EPX) → RGBA-Body-ZIP + Player-Color-Remap-PNG (zwei Layer).
# Analog zu bake-shp-mech.py, aber für Gebäude-SHPs:
#   - Body: echte RGBA-Farben aus unittem.pal (nicht schwarz)
#   - Remap: Player-Color-Indizes 80-95 → Rampe 176-191
#
# Voraussetzung: gadpsa-0000.png + unittem.pal in WDIR (via ts --png + ts --extract)

import zipfile, struct, json, zlib
from pathlib import Path
from PIL import Image
import numpy as np

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
WDIR = PROJ / "mods/management/asset wip/aray"
BITS = PROJ / "mods/cnc/bits"
SHP_STEM = "gadpsa"
FRAME    = 0          # nur Frame 0 (statisches Idle)
OUT      = "aot-gadpsa"

# TS unittem.pal (6-bit VGA: Werte 0-63, ×4 → 8-bit)
pal_raw = open(WDIR / "unittem.pal", 'rb').read()
def pal_rgb(i): return (min(255, pal_raw[i*3]*4), min(255, pal_raw[i*3+1]*4), min(255, pal_raw[i*3+2]*4))

# Palette-Luminanz für Player-Color-Ramp-Mapping (80-95 → 176-191)
def lum_pal(i):
    r, g, b = pal_rgb(i)
    return 0.299*r + 0.587*g + 0.114*b

PC_LO, PC_HI = 80, 95    # TS Player-Color-Indizes
RAMP_LO, RAMP_HI = 176, 191  # Ziel-Rampe im aot-vxl-ramp.pal

# Luminanz-Bereich der Player-Color-Pixel → lineare Abbildung auf Ramp
pc_lums = [lum_pal(i) for i in range(PC_LO, PC_HI+1) if lum_pal(i) > 0]
L_MIN = min(pc_lums) if pc_lums else 0
L_MAX = max(pc_lums) if pc_lums else 255

def pc_to_ramp(idx):
    # hell (hohe Lum) → 176 (hell in Rampe), dunkel → 191 (dunkel)
    L = lum_pal(idx)
    t = (L - L_MIN) / max(L_MAX - L_MIN, 1)
    return int(round(RAMP_LO + (1.0 - t) * (RAMP_HI - RAMP_LO)))

# --- Quell-Frame laden ---
src = np.array(Image.open(WDIR / f"{SHP_STEM}-{FRAME:04d}.png"))
H, W = src.shape   # 96×96

# --- Zwei Index-Layer erzeugen ---
body_idx = np.zeros_like(src)   # 0=transparent, sonst echter Palette-Index
remap_idx = np.zeros_like(src)  # 0=transparent, 176-191=Player-Color-Rampe

for y in range(H):
    for x in range(W):
        px = int(src[y, x])
        if px == 0:
            continue
        if PC_LO <= px <= PC_HI:
            remap_idx[y, x] = pc_to_ramp(px)
        else:
            body_idx[y, x] = px

# --- Scale3x (EPX) auf Index-Arrays ---
def scale3x(s):
    def sh(a, dy, dx):
        r = np.roll(np.roll(a, dy, 0), dx, 1)
        if dy ==  1: r[0,  :] = a[0,  :]
        if dy == -1: r[-1, :] = a[-1, :]
        if dx ==  1: r[:,  0] = a[:,  0]
        if dx == -1: r[:, -1] = a[:, -1]
        return r
    A=sh(s,1,1); B=sh(s,1,0); C=sh(s,1,-1)
    D=sh(s,0,1); E=s;         F=sh(s,0,-1)
    G=sh(s,-1,1); H_=sh(s,-1,0); I_=sh(s,-1,-1)
    c = (B != H_) & (D != F)
    def sel(cond, a, b): return np.where(cond, a, b)
    E0=sel(c&(D==B),D,E); E1=sel(c&(((D==B)&(E!=C))|((B==F)&(E!=A))),B,E); E2=sel(c&(B==F),F,E)
    E3=sel(c&(((D==B)&(E!=G))|((D==H_)&(E!=A))),D,E); E5=sel(c&(((B==F)&(E!=I_))|((H_==F)&(E!=C))),F,E)
    E6=sel(c&(D==H_),D,E); E7=sel(c&(((D==H_)&(E!=I_))|((H_==F)&(E!=G))),H_,E); E8=sel(c&(H_==F),F,E)
    out = np.zeros((H*3,W*3), dtype=s.dtype)
    out[0::3,0::3]=E0; out[0::3,1::3]=E1; out[0::3,2::3]=E2
    out[1::3,0::3]=E3; out[1::3,1::3]=E;  out[1::3,2::3]=E5
    out[2::3,0::3]=E6; out[2::3,1::3]=E7; out[2::3,2::3]=E8
    return out

body_3x  = scale3x(body_idx)
remap_3x = scale3x(remap_idx)

SZ = H*3  # 288

# --- Body → RGBA TGA (echter Farbinhalt aus pal) ---
rgba = np.zeros((SZ, SZ, 4), dtype=np.uint8)
for y in range(SZ):
    for x in range(SZ):
        idx = int(body_3x[y, x])
        if idx > 0:
            r, g, b = pal_rgb(idx)
            rgba[y, x] = [r, g, b, 255]

def write_tga(rgba_arr):
    h, w = rgba_arr.shape[:2]
    hdr = bytearray(18); hdr[2] = 2
    struct.pack_into('<H', hdr, 12, w); struct.pack_into('<H', hdr, 14, h)
    hdr[16] = 32; hdr[17] = 0x28
    return bytes(hdr) + bytes(rgba_arr[:, :, [2,1,0,3]].flatten())

meta = json.dumps({"size":[SZ,SZ],"crop":[0,0,SZ,SZ]}, separators=(',',':'))

z = zipfile.ZipFile(BITS / f"{OUT}.zip", 'w', zipfile.ZIP_DEFLATED)
z.writestr(f"gadpsa-0000.tga", write_tga(rgba))
z.writestr(f"gadpsa-0000.meta", meta)
z.close()
print(f"Written {BITS}/{OUT}.zip  ({SZ}×{SZ}px, 1 frame)")

# --- Remap → indiziertes PNG mit aot-vxl-ramp-Palette ---
def tc(k, v):
    d = k.encode() + b'\x00' + v.encode()
    return struct.pack('>I', len(d)) + b'tEXt' + d + struct.pack('>I', zlib.crc32(b'tEXt'+d))

simg = Image.fromarray(remap_3x, 'P')
ramp_raw = open(BITS / "aot-vxl-ramp.pal", 'rb').read()
outpal = [0]*768
for i in range(RAMP_LO, RAMP_HI+1):
    outpal[i*3]   = min(255, ramp_raw[i*3]*4)
    outpal[i*3+1] = min(255, ramp_raw[i*3+1]*4)
    outpal[i*3+2] = min(255, ramp_raw[i*3+2]*4)
simg.putpalette(outpal)
tmp = BITS / f"{OUT}-remap.png"
simg.save(tmp, transparency=bytes([0]+[255]*255))

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
        outb += tc("FrameSize", f"{SZ},{SZ}") + tc("FrameAmount", "1")
tmp.write_bytes(outb)
print(f"Written {BITS}/{OUT}-remap.png  ({SZ}×{SZ}px, 1 frame, ramp 176-191)")

# Statistik
pc_count = int(np.sum(remap_3x > 0))
body_count = int(np.sum(body_3x > 0))
print(f"Body pixels: {body_count}, Player-Color pixels: {pc_count}")
