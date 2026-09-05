#!/usr/bin/env python3
# Bäckt ein TS-SHP-Walking-Mech (z.B. smech.shp = Wolverine) als glatte In-Game-Sprites
# mit dynamischer Player-Color. Zwei-Layer: Body (schwarze Details) + Remap (voller Körper
# in Player-Color-Rampe). Scale3x/EPX für präzise Kanten (KEIN LANCZOS-Blur).
#
# Referenz-Import: Wolverine (smech.shp). Siehe Memory [[ts-shp-mech-import]].
#
# ANPASSEN pro Mech (Block unten): SHP_NAME, OUT, C0/C1 (Crop), NFRAMES, Frame-Layout-Kommentar.
# Vorher: SHP + unittem.pal via `ts --extract <shp> unittem.pal` nach WDIR extrahieren,
#         `ts --png <shp> unittem.pal` → Einzel-PNG-Frames erzeugen.

import zipfile, struct, json, zlib
from pathlib import Path
from PIL import Image
import numpy as np

# === PARAMETER (pro Mech anpassen) ===
PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
SHP_STEM = "smech"          # PNG-Frames heißen <SHP_STEM>-NNNN.png
OUT      = "aot-wolverine"  # Ausgabe: <OUT>.zip (body) + <OUT>-remap.png
WDIR = PROJ / "mods/management/asset wip/wolverine/ts"
BITS = PROJ / "mods/cnc/bits"
C0, C1 = 15, 79   # symmetrischer Crop um Frame-Center (behält Sprite-Anchor). SHP-Frame ist 95px, Center 47.
NFRAMES = 136     # Anzahl gebackener Frames (Body-Frames, OHNE Schatten-Frames)
DARK = 2          # lum6 < DARK -> schwarz (Miniguns, Füße, tiefe Schatten). Rest -> Player-Color.
PCT_LO, PCT_HI = 5, 90   # Perzentil-Streckung der Luminanz (clippt Ausreißer)
# =====================================

CROP = C1 - C0
DST = CROP * 3    # Scale3x = 3x
pal = open(WDIR / "unittem.pal", 'rb').read()  # 6-bit VGA
def lum6(i): i = int(i); return 0.299*pal[i*3] + 0.587*pal[i*3+1] + 0.114*pal[i*3+2]

# Globale Perzentil-Streckung über alle Frames
allL = []
for fi in range(NFRAMES):
    arr = np.array(Image.open(WDIR / f"{SHP_STEM}-{fi:04d}.png"))[C0:C1, C0:C1]
    for idx in np.unique(arr[arr > 0]):
        L = lum6(idx)
        if L >= DARK:
            allL.append(L)
allL = np.array(allL)
LMIN, LMAX = np.percentile(allL, PCT_LO), np.percentile(allL, PCT_HI)

def classify(fi):
    arr = np.array(Image.open(WDIR / f"{SHP_STEM}-{fi:04d}.png"))[C0:C1, C0:C1]
    out = np.zeros((CROP, CROP), dtype=np.uint8)  # 0=transparent, 1=schwarz, 176-191=rampe
    for idx in np.unique(arr[arr > 0]):
        m = arr == idx; L = lum6(idx)
        if L < DARK:
            out[m] = 1
        else:
            t = np.clip((L - LMIN) / (LMAX - LMIN), 0, 1)
            out[m] = int(round(191 - t*15))  # volle Rampe, hell->176 dunkel->191
    return out

def scale3x(src):
    # EPX/Scale3x, indexed-erhaltend (keine neuen Farben). HxW -> 3H x 3W.
    def sh(a, dy, dx):
        r = np.roll(np.roll(a, dy, 0), dx, 1)
        if dy == 1: r[0, :] = a[0, :]
        elif dy == -1: r[-1, :] = a[-1, :]
        if dx == 1: r[:, 0] = a[:, 0]
        elif dx == -1: r[:, -1] = a[:, -1]
        return r
    A=sh(src,1,1); B=sh(src,1,0); C=sh(src,1,-1)
    D=sh(src,0,1); E=src;         F=sh(src,0,-1)
    G=sh(src,-1,1); H_=sh(src,-1,0); I=sh(src,-1,-1)
    cond = (B != H_) & (D != F)
    def sel(c, a, b): return np.where(c, a, b)
    E0=sel(cond & (D==B), D, E)
    E1=sel(cond & (((D==B)&(E!=C))|((B==F)&(E!=A))), B, E)
    E2=sel(cond & (B==F), F, E)
    E3=sel(cond & (((D==B)&(E!=G))|((D==H_)&(E!=A))), D, E)
    E5=sel(cond & (((B==F)&(E!=I))|((H_==F)&(E!=C))), F, E)
    E6=sel(cond & (D==H_), D, E)
    E7=sel(cond & (((D==H_)&(E!=I))|((H_==F)&(E!=G))), H_, E)
    E8=sel(cond & (H_==F), F, E)
    H, W = src.shape
    out = np.zeros((H*3, W*3), dtype=src.dtype)
    out[0::3,0::3]=E0; out[0::3,1::3]=E1; out[0::3,2::3]=E2
    out[1::3,0::3]=E3; out[1::3,1::3]=E;  out[1::3,2::3]=E5
    out[2::3,0::3]=E6; out[2::3,1::3]=E7; out[2::3,2::3]=E8
    return out

bodies, remaps = [], []
for fi in range(NFRAMES):
    big = scale3x(classify(fi))
    bodies.append((big == 1).astype(np.uint8) * 255)          # Alpha-Silhouette schwarz
    remaps.append(np.where(big >= 176, big, 0).astype(np.uint8))

def write_tga_alpha(alpha):
    h, w = alpha.shape; rgba = np.zeros((h, w, 4), dtype=np.uint8); rgba[:, :, 3] = alpha
    hdr = bytearray(18); hdr[2] = 2
    struct.pack_into('<H', hdr, 12, w); struct.pack_into('<H', hdr, 14, h); hdr[16] = 32; hdr[17] = 0x28
    return bytes(hdr) + bytes(rgba[:, :, [2,1,0,3]].flatten())
def tc(k, v):
    d = k.encode() + b'\x00' + v.encode()
    return struct.pack('>I', len(d)) + b'tEXt' + d + struct.pack('>I', zlib.crc32(b'tEXt'+d))
meta = json.dumps({"size":[DST,DST],"crop":[0,0,DST,DST]}, separators=(',',':'))

z = zipfile.ZipFile(BITS / f"{OUT}.zip", 'w', zipfile.ZIP_DEFLATED)
for fi, a in enumerate(bodies):
    z.writestr(f"wolv-{fi:04d}.tga", write_tga_alpha(a)); z.writestr(f"wolv-{fi:04d}.meta", meta)
z.close()

COLS = 8; ROWS = (NFRAMES + COLS - 1) // COLS
sheet = np.zeros((ROWS*DST, COLS*DST), dtype=np.uint8)
for fi, r in enumerate(remaps):
    c, row = fi % COLS, fi // COLS
    sheet[row*DST:(row+1)*DST, c*DST:(c+1)*DST] = r
simg = Image.fromarray(sheet, 'P')
outpal = [0]*768
ramp = open(BITS / "aot-vxl-ramp.pal", 'rb').read()
for i in range(176, 192):
    outpal[i*3]=min(255,ramp[i*3]*4); outpal[i*3+1]=min(255,ramp[i*3+1]*4); outpal[i*3+2]=min(255,ramp[i*3+2]*4)
simg.putpalette(outpal)
tmp = BITS / f"{OUT}-remap.png"
simg.save(tmp, transparency=bytes([0] + [255]*255))
# tEXt-Chunks NACH IHDR einfügen (PngSheetLoader liest FrameSize/FrameAmount)
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
print(f"{OUT}: {DST}px, {NFRAMES} frames, Scale3x, Player-Color-Rampe 176-191, DARK={DARK}")
