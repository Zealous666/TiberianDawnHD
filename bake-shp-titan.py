#!/usr/bin/env python3
# Bäckt den TS-Titan (mmch.shp) als zwei-Layer Sprites für MTNK-Swap.
# Body: Frames 0-119 (8 Facings × 15 Walk-Frames) — alles Player-Color (wie AAPC)
# Turret Housing: Frames 120-151 (32 Facings × 1 Frame) — explizite Index-Erkennung:
#   TS-Indices 16-31 → Player-Color-Remap (176-191)
#   alle anderen Nicht-Transparent-Pixel → echte RGB-Farben im Body-TGA (kein Schwarz)
# Facings REVERSED: Frame 0 in ZIP = mmch frame 151, Frame 31 = mmch frame 120

import zipfile, struct, json, zlib
from pathlib import Path
from PIL import Image
import numpy as np

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
WDIR = PROJ / "mods/management/asset wip/titan/ts"
BITS = PROJ / "mods/cnc/bits"
SHP_STEM = "mmch"

# --- Body: 120 Frames, 8 Facings × 15 Walk ---
BODY_NFRAMES = 120
BODY_C0, BODY_C1 = 17, 77   # 60px crop; body rows

# --- Turret Housing: 32 Frames, 32 Facings × 1 (reversed) ---
TURR_NFRAMES = 32
TURR_C0, TURR_C1 = 4, 90    # 86px crop; pivot y=47 → crop y=43 → pixel 129 in 258px
TURR_START = 120             # Frames 120-151 in mmch.shp
TURR_BRIGHTNESS = 0.75       # TS 6-bit × 4 → zu hell, auf 75% reduzieren

# Body-Bake: Luminanz-Schwelle für schwarze Pixel
DARK = 2
PCT_LO, PCT_HI = 5, 90

pal = open(WDIR / "unittem.pal", 'rb').read()
def lum6(i): i = int(i); return 0.299*pal[i*3] + 0.587*pal[i*3+1] + 0.114*pal[i*3+2]
def pal_rgb(i): i = int(i); return (min(255,pal[i*3]*4), min(255,pal[i*3+1]*4), min(255,pal[i*3+2]*4))

def get_crop(fi, C0, C1):
    return np.array(Image.open(WDIR / f"{SHP_STEM}-{fi:04d}.png"))[C0:C1, C0:C1]

def compute_percentile(frame_range, C0, C1):
    allL = []
    for fi in frame_range:
        arr = get_crop(fi, C0, C1)
        for idx in np.unique(arr[arr > 0]):
            L = lum6(idx)
            if L >= DARK: allL.append(L)
    allL = np.array(allL) if allL else np.array([0.0, 63.0])
    return np.percentile(allL, PCT_LO), np.percentile(allL, PCT_HI)

def classify_body(fi, C0, C1, LMIN, LMAX):
    """Body: alles Nicht-Dunkle → Player-Color (wie AAPC/Wolverine)."""
    arr = get_crop(fi, C0, C1)
    CROP = C1 - C0
    out = np.zeros((CROP, CROP), dtype=np.uint8)
    for idx in np.unique(arr[arr > 0]):
        m = arr == idx; L = lum6(idx)
        if L < DARK:
            out[m] = 1
        else:
            t = np.clip((L - LMIN) / max(LMAX - LMIN, 1e-9), 0, 1)
            out[m] = int(round(191 - t*15))
    return out

def classify_turret(fi, C0, C1):
    """Turret Housing: explizite Index-Erkennung.
    Indices 16-31 (TS Player-Color) → Remap-Layer (176-191, luminanzbasiert).
    Alle anderen sichtbaren Pixel → echte RGB-Farben im Body-RGBA.
    Sehr dunkle Pixel → Schwarz.
    """
    arr = get_crop(fi, C0, C1)
    CROP = C1 - C0
    body_rgba = np.zeros((CROP, CROP, 4), dtype=np.uint8)
    remap_idx = np.zeros((CROP, CROP), dtype=np.uint8)

    # Perzentil nur über Player-Color-Indices
    pc_lums = [lum6(idx) for idx in np.unique(arr[arr > 0]) if 16 <= int(idx) <= 31]
    if pc_lums:
        LMIN = np.percentile(pc_lums, PCT_LO)
        LMAX = np.percentile(pc_lums, PCT_HI)
    else:
        LMIN, LMAX = 0.0, 63.0

    for idx in np.unique(arr[arr > 0]):
        idx = int(idx)
        m = arr == idx
        L = lum6(idx)
        if 16 <= idx <= 31:
            # Player-Color: in Remap-Layer
            t = np.clip((L - LMIN) / max(LMAX - LMIN, 1e-9), 0, 1)
            remap_val = int(round(191 - t * 15))
            remap_idx[m] = remap_val
        elif L < DARK:
            # Tiefdunkel: Schwarz im Body
            body_rgba[m, 3] = 255
        else:
            # Echte TS-Farbe im Body (gedimmt)
            r, g, b = pal_rgb(idx)
            f = TURR_BRIGHTNESS
            body_rgba[m, 0] = int(r * f)
            body_rgba[m, 1] = int(g * f)
            body_rgba[m, 2] = int(b * f)
            body_rgba[m, 3] = 255

    return body_rgba, remap_idx

def scale3x(src):
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

def scale3x_rgba(src_rgba):
    """Scale3x auf jeden Kanal separat, Alpha bestimmt ob Pixel "leer" ist."""
    alpha = (src_rgba[:, :, 3] > 0).astype(np.uint8)
    big_alpha = scale3x(alpha)
    out = np.zeros((src_rgba.shape[0]*3, src_rgba.shape[1]*3, 4), dtype=np.uint8)
    for ch in range(4):
        out[:, :, ch] = scale3x(src_rgba[:, :, ch])
    return out

def write_tga_alpha(alpha):
    h, w = alpha.shape
    rgba = np.zeros((h, w, 4), dtype=np.uint8)
    rgba[:, :, 3] = alpha
    hdr = bytearray(18); hdr[2] = 2
    struct.pack_into('<H', hdr, 12, w); struct.pack_into('<H', hdr, 14, h)
    hdr[16] = 32; hdr[17] = 0x28
    return bytes(hdr) + bytes(rgba[:, :, [2,1,0,3]].flatten())

def write_tga_rgba(rgba):
    h, w = rgba.shape[:2]
    hdr = bytearray(18); hdr[2] = 2
    struct.pack_into('<H', hdr, 12, w); struct.pack_into('<H', hdr, 14, h)
    hdr[16] = 32; hdr[17] = 0x28
    return bytes(hdr) + bytes(rgba[:, :, [2,1,0,3]].flatten())

def tc(k, v):
    d = k.encode() + b'\x00' + v.encode()
    return struct.pack('>I', len(d)) + b'tEXt' + d + struct.pack('>I', zlib.crc32(b'tEXt'+d))

def write_remap_png(remaps, DST, NFRAMES, out_path):
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
    tmp = Path(out_path)
    simg.save(tmp, transparency=bytes([0] + [255]*255))
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

def bake_body(frame_range, C0, C1, out_stem, tga_prefix):
    """Body-Bake: alles Player-Color (wie Wolverine/AAPC)."""
    NFRAMES = len(frame_range)
    CROP = C1 - C0; DST = CROP * 3
    print(f"Baking body {out_stem}: {NFRAMES} frames, {CROP}px → {DST}px Scale3x")
    LMIN, LMAX = compute_percentile(frame_range, C0, C1)
    meta = json.dumps({"size":[DST,DST],"crop":[0,0,DST,DST]}, separators=(',',':'))
    bodies, remaps = [], []
    for fi in frame_range:
        big = scale3x(classify_body(fi, C0, C1, LMIN, LMAX))
        bodies.append((big == 1).astype(np.uint8) * 255)
        remaps.append(np.where(big >= 176, big, 0).astype(np.uint8))
    z = zipfile.ZipFile(BITS / f"{out_stem}.zip", 'w', zipfile.ZIP_DEFLATED)
    for i, a in enumerate(bodies):
        z.writestr(f"{tga_prefix}-{i:04d}.tga", write_tga_alpha(a))
        z.writestr(f"{tga_prefix}-{i:04d}.meta", meta)
    z.close()
    write_remap_png(remaps, DST, NFRAMES, BITS / f"{out_stem}-remap.png")
    print(f"  → {out_stem}.zip + {out_stem}-remap.png ({DST}px, {NFRAMES} frames)")

def bake_turret(frame_range_list, C0, C1, out_stem, tga_prefix):
    """Turret-Bake: explizite Index-Erkennung. Frames aus frame_range_list (für Reversal)."""
    NFRAMES = len(frame_range_list)
    CROP = C1 - C0; DST = CROP * 3
    print(f"Baking turret {out_stem}: {NFRAMES} frames, {CROP}px → {DST}px Scale3x (explicit index)")
    meta = json.dumps({"size":[DST,DST],"crop":[0,0,DST,DST]}, separators=(',',':'))
    bodies, remaps = [], []
    for fi in frame_range_list:
        body_rgba, remap_idx = classify_turret(fi, C0, C1)
        # Scale3x auf RGBA Body
        big_rgba = scale3x_rgba(body_rgba)
        big_remap = scale3x(remap_idx)
        bodies.append(big_rgba)
        remaps.append(np.where(big_remap >= 176, big_remap, 0).astype(np.uint8))
    z = zipfile.ZipFile(BITS / f"{out_stem}.zip", 'w', zipfile.ZIP_DEFLATED)
    for i, rgba in enumerate(bodies):
        z.writestr(f"{tga_prefix}-{i:04d}.tga", write_tga_rgba(rgba))
        z.writestr(f"{tga_prefix}-{i:04d}.meta", meta)
    z.close()
    write_remap_png(remaps, DST, NFRAMES, BITS / f"{out_stem}-remap.png")
    print(f"  → {out_stem}.zip + {out_stem}-remap.png ({DST}px, {NFRAMES} frames)")

def bake_turret_railgun(frame_range_list, C0, C1, out_stem, tga_prefix):
    """Railgun Turret: wie normales Housing (Player-Color schwarz) +
    horizontale Streifen-Akzente am Pivot-Y: weißer Streifen, feinere blaue Linie mittig."""
    NFRAMES = len(frame_range_list)
    CROP = C1 - C0; DST = CROP * 3
    print(f"Baking railgun turret {out_stem}: {NFRAMES} frames, {CROP}px → {DST}px (stripe)")
    LMIN, LMAX = compute_percentile(frame_range_list, C0, C1)
    meta = json.dumps({"size":[DST,DST],"crop":[0,0,DST,DST]}, separators=(',',':'))

    # Turret pivot at y=47 in 86px crop → y=141 in 258px (47*3).
    # Stripe centered there.
    CY = 47 * 3  # = 141
    WHITE_W = 4   # Halbbreite weiß (±4px → 8px Gesamtbreite)
    BLUE_W  = 1   # Halbbreite blau (±1px → 2px, innerhalb des Weiß)
    WHITE_Y0, WHITE_Y1 = CY - WHITE_W, CY + WHITE_W
    BLUE_Y0,  BLUE_Y1  = CY - BLUE_W,  CY + BLUE_W

    bodies, remaps = [], []
    for fi in frame_range_list:
        big = scale3x(classify_body(fi, C0, C1, LMIN, LMAX))
        # Alle sichtbaren Pixel: entweder Schwarz (==1) oder Player-Color (>=176)
        alpha = np.where((big == 1) | (big >= 176), 255, 0).astype(np.uint8)

        body_rgba = np.zeros((DST, DST, 4), dtype=np.uint8)
        body_rgba[:, :, 3] = alpha  # schwarz mit Alpha-Maske

        remap = np.where(big >= 176, big, 0).astype(np.uint8)

        # Weißen Streifen einzeichnen (überschreibt Player-Color-Remap in diesen Zeilen)
        for y in range(WHITE_Y0, WHITE_Y1):
            row_mask = alpha[y, :] > 0
            body_rgba[y, row_mask] = [255, 255, 255, 255]
            remap[y, :] = 0  # kein Remap-Overlay im Streifen

        # Blaue Linie durch die Mitte des weißen Streifens
        for y in range(BLUE_Y0, BLUE_Y1):
            row_mask = alpha[y, :] > 0
            body_rgba[y, row_mask] = [80, 160, 240, 255]
            remap[y, :] = 0

        bodies.append(body_rgba)
        remaps.append(remap)

    z = zipfile.ZipFile(BITS / f"{out_stem}.zip", 'w', zipfile.ZIP_DEFLATED)
    for i, rgba in enumerate(bodies):
        z.writestr(f"{tga_prefix}-{i:04d}.tga", write_tga_rgba(rgba))
        z.writestr(f"{tga_prefix}-{i:04d}.meta", meta)
    z.close()
    write_remap_png(remaps, DST, NFRAMES, BITS / f"{out_stem}-remap.png")
    print(f"  → {out_stem}.zip + {out_stem}-remap.png (weiß y={WHITE_Y0}-{WHITE_Y1}, blau y={BLUE_Y0}-{BLUE_Y1})")

# --- Ausführung ---

# Body (walk, 8 facings × 15 frames) — unverändert
bake_body(range(BODY_NFRAMES), BODY_C0, BODY_C1, "aot-titan-body", "titan-body")

# Turret Housing (32 facings, REVERSED: frame 0 = mmch 151, frame 31 = mmch 120)
# Alles Player-Color (wie Body) — kein RGB-Body-Layer
turr_frames = list(range(TURR_START + TURR_NFRAMES - 1, TURR_START - 1, -1))
bake_body(turr_frames, TURR_C0, TURR_C1, "aot-titan-turret", "titan-turr")

# Railgun Housing: schwarz wie normal + weißer Streifen + blaue Linie
bake_turret_railgun(turr_frames, TURR_C0, TURR_C1, "aot-titan-turret-railgun", "titan-turr-rail")

print("Done.")
