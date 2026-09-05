#!/usr/bin/env python3
# Bäckt alle Juggernaut-Sprites. EXAKT gleicher Workflow wie bake-shp-titan.py.
#
# jugger.shp  Frames 0-119  (8 Facings × 15 Walk)  → bake_body (alles Player-Color)
# djugg.shp   Frames 0-2    (deployed body)         → bake_body (alles Player-Color)
# djugg_a.shp Frames 0-31   (32 Facings Turret)     → bake_turret (explizite Indizes)
# djuggmk.shp Frames 0-17   (Deploy-Animation)      → bake_body (alles Player-Color)
#
# Facings:
#   Walk body: forward (Facings: -8 in sequences macht Reversal)
#   Deployed turret: REVERSED (UseClassicFacings: True, kein -8)
#   Deployed body + Make: forward (kein Facing-Reversal nötig)

import zipfile, struct, json, zlib
from pathlib import Path
from PIL import Image
import numpy as np

PROJ  = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
WDIR  = PROJ / "mods/management/asset wip/jugg/ts"
BITS  = PROJ / "mods/cnc/bits"

# Crop-Bounds (Zeilen/Spalten im Original-Frame, quadratischer Schnitt)
# jugger.shp (95px): content rows 13-55, cols 20-72
BODY_C0, BODY_C1   = 10, 80    # 70px → 210px Scale3x
# djugg_a.shp (96px): content rows 31-55, cols 31-71
# djugg.shp  (96px): content rows 43-69, cols 31-69
# djuggmk.shp(96px): content rows 33-69
# Alle deployed/make auf gleiches Raster → überlagern korrekt
DEPL_C0, DEPL_C1   = 27, 75    # 48px → 144px Scale3x

DARK = 2
PCT_LO, PCT_HI = 5, 90
TURR_BRIGHTNESS = 1.4

pal = open(WDIR / "unittem.pal", 'rb').read()
def lum6(i): i = int(i); return 0.299*pal[i*3] + 0.587*pal[i*3+1] + 0.114*pal[i*3+2]
def pal_rgb(i): i = int(i); return (min(255,pal[i*3]*4), min(255,pal[i*3+1]*4), min(255,pal[i*3+2]*4))

def get_crop(stem, fi, C0, C1):
    return np.array(Image.open(WDIR / f"{stem}-{fi:04d}.png"))[C0:C1, C0:C1]

def compute_percentile(stem, frame_range, C0, C1):
    allL = []
    for fi in frame_range:
        arr = get_crop(stem, fi, C0, C1)
        for idx in np.unique(arr[arr > 0]):
            L = lum6(idx)
            if L >= DARK: allL.append(L)
    allL = np.array(allL) if allL else np.array([0.0, 63.0])
    return np.percentile(allL, PCT_LO), np.percentile(allL, PCT_HI)

def classify_body(stem, fi, C0, C1, LMIN, LMAX):
    """Body: alles Nicht-Dunkle → Player-Color (wie AAPC/Wolverine/Titan)."""
    arr = get_crop(stem, fi, C0, C1)
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

def classify_turret(stem, fi, C0, C1):
    """Turret: explizite Index-Erkennung (wie Titan-Turret).
    Indices 16-31 (TS Player-Color) → Remap-Layer (176-191, luminanzbasiert).
    Alle anderen sichtbaren Pixel → echte RGB-Farben im Body-RGBA (gedimmt).
    Sehr dunkle Pixel → Schwarz.
    """
    arr = get_crop(stem, fi, C0, C1)
    CROP = C1 - C0
    body_rgba = np.zeros((CROP, CROP, 4), dtype=np.uint8)
    remap_idx  = np.zeros((CROP, CROP), dtype=np.uint8)

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
            t = np.clip((L - LMIN) / max(LMAX - LMIN, 1e-9), 0, 1)
            remap_idx[m] = int(round(191 - t * 15))
        elif L < DARK:
            body_rgba[m, 3] = 255
        else:
            r, g, b = pal_rgb(idx)
            f = TURR_BRIGHTNESS
            body_rgba[m, 0] = min(255, int(r * f))
            body_rgba[m, 1] = min(255, int(g * f))
            body_rgba[m, 2] = min(255, int(b * f))
            body_rgba[m, 3] = 255

    return body_rgba, remap_idx

def scale3x(src):
    def sh(a, dy, dx):
        r = np.roll(np.roll(a, dy, 0), dx, 1)
        if dy == 1:  r[0, :]  = a[0, :]
        elif dy ==-1: r[-1, :] = a[-1, :]
        if dx == 1:  r[:, 0]  = a[:, 0]
        elif dx ==-1: r[:, -1] = a[:, -1]
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

def classify_jugg_walk(stem, fi, C0, C1, LMIN, LMAX):
    """Hybrid: neutral-grau (sat<0.12) → weiß/silber; alles andere sichtbare → Player-Color-Remap."""
    arr = get_crop(stem, fi, C0, C1)
    CROP = C1 - C0
    body_rgba = np.zeros((CROP, CROP, 4), dtype=np.uint8)
    remap_idx  = np.zeros((CROP, CROP), dtype=np.uint8)
    for idx in np.unique(arr[arr > 0]):
        idx = int(idx)
        m = arr == idx
        L = lum6(idx)
        r, g, b = pal_rgb(idx)
        mx = max(r, g, b); sat = (mx - min(r,g,b)) / max(mx, 1)
        if L < DARK:
            body_rgba[m, 3] = 255  # schwarz
        elif sat < 0.12:
            # neutral grau → weiß/silber (Kanonenrohre, Kappe)
            f = TURR_BRIGHTNESS
            body_rgba[m, 0] = min(255, int(r * f))
            body_rgba[m, 1] = min(255, int(g * f))
            body_rgba[m, 2] = min(255, int(b * f))
            body_rgba[m, 3] = 255
        else:
            # farbig (warm-gold Beine, blau Schatten, TS-Spielerfarbe 16-31) → Player-Color-Remap
            t = np.clip((L - LMIN) / max(LMAX - LMIN, 1e-9), 0, 1)
            remap_idx[m] = int(round(191 - t * 15))
    return body_rgba, remap_idx

def bake_body(stem, frame_range, C0, C1, out_stem, tga_prefix):
    """Deployed-Body/Make-Bake: alles Player-Color (einfache Variante für djugg/djuggmk)."""
    NFRAMES = len(frame_range)
    CROP = C1 - C0; DST = CROP * 3
    print(f"Baking body {out_stem}: {NFRAMES} frames, {CROP}px → {DST}px Scale3x")
    LMIN, LMAX = compute_percentile(stem, frame_range, C0, C1)
    meta = json.dumps({"size":[DST,DST],"crop":[0,0,DST,DST]}, separators=(',',':'))
    bodies, remaps = [], []
    for fi in frame_range:
        big = scale3x(classify_body(stem, fi, C0, C1, LMIN, LMAX))
        bodies.append((big == 1).astype(np.uint8) * 255)
        remaps.append(np.where(big >= 176, big, 0).astype(np.uint8))
    z = zipfile.ZipFile(BITS / f"{out_stem}.zip", 'w', zipfile.ZIP_DEFLATED)
    for i, a in enumerate(bodies):
        z.writestr(f"{tga_prefix}-{i:04d}.tga", write_tga_alpha(a))
        z.writestr(f"{tga_prefix}-{i:04d}.meta", meta)
    z.close()
    write_remap_png(remaps, DST, NFRAMES, BITS / f"{out_stem}-remap.png")
    print(f"  → {out_stem}.zip + {out_stem}-remap.png ({DST}px, {NFRAMES} frames)")

def bake_walk_body(stem, frame_range, C0, C1, out_stem, tga_prefix):
    """Walk-Body-Bake mit Hybrid-Klassifizierung: grau→weiß, farbig→Player-Color."""
    NFRAMES = len(frame_range)
    CROP = C1 - C0; DST = CROP * 3
    print(f"Baking walk body {out_stem}: {NFRAMES} frames, {CROP}px → {DST}px Scale3x (hybrid)")
    # LMIN/LMAX aus allen farbigen Pixeln (inkl. TS-PC 16-31 + warm-gold) über alle Frames
    allL = []
    for fi in frame_range:
        arr = get_crop(stem, fi, C0, C1)
        for idx in np.unique(arr[arr > 0]):
            idx = int(idx); L = lum6(idx)
            r, g, b = pal_rgb(idx)
            mx = max(r,g,b); sat = (mx - min(r,g,b)) / max(mx, 1)
            if L >= DARK and sat >= 0.12:
                allL.append(L)
    allL = np.array(allL) if allL else np.array([0.0, 63.0])
    LMIN = np.percentile(allL, PCT_LO)
    LMAX = np.percentile(allL, PCT_HI)
    meta = json.dumps({"size":[DST,DST],"crop":[0,0,DST,DST]}, separators=(',',':'))
    bodies, remaps = [], []
    for fi in frame_range:
        body_rgba, remap_idx = classify_jugg_walk(stem, fi, C0, C1, LMIN, LMAX)
        big_rgba  = scale3x_rgba(body_rgba)
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

def load_barrel_frames(barrel_stem, remap_path, nframes=32):
    """Load VXL-rendered barrel frames (body RGBA + remap indices) from WDIR."""
    FSZ = None
    bodies, remaps = [], []
    for i in range(nframes):
        img = np.array(Image.open(WDIR / f"{barrel_stem}-{i:04d}.png").convert("RGBA"))
        if FSZ is None: FSZ = img.shape[0]
        bodies.append(img)
    sheet = np.array(Image.open(WDIR / remap_path).convert("P"))
    COLS = 8
    for i in range(nframes):
        col, row = i % COLS, i // COLS
        frame = sheet[row*FSZ:(row+1)*FSZ, col*FSZ:(col+1)*FSZ].astype(np.uint8)
        remaps.append(frame)
    return bodies, remaps, FSZ

def alpha_over(dst, src):
    """Alpha-composite src over dst (both HxWx4 uint8). Returns new array."""
    a_src = src[:, :, 3:4].astype(np.float32) / 255.0
    a_dst = dst[:, :, 3:4].astype(np.float32) / 255.0
    a_out = a_src + a_dst * (1 - a_src)
    out = np.zeros_like(dst)
    for ch in range(3):
        out[:, :, ch] = np.where(
            a_out[:, :, 0] > 0,
            (src[:, :, ch] * a_src[:, :, 0] + dst[:, :, ch] * a_dst[:, :, 0] * (1 - a_src[:, :, 0]))
            / a_out[:, :, 0],
            0
        ).astype(np.uint8)
    out[:, :, 3] = (a_out[:, :, 0] * 255).astype(np.uint8)
    return out

def scale_frame_about_center(arr, factor, is_index=False):
    """Scale an HxW(x4) array by `factor` about its own center, keeping same canvas size."""
    H, W = arr.shape[0], arr.shape[1]
    nh, nw = max(1, round(H*factor)), max(1, round(W*factor))
    mode = "RGBA" if not is_index else "L"
    src_img = Image.fromarray(arr, mode)
    resized = src_img.resize((nw, nh), Image.NEAREST if is_index else Image.LANCZOS)
    out = np.zeros_like(arr)
    oy = (H - nh) // 2
    ox = (W - nw) // 2
    out[oy:oy+nh, ox:ox+nw] = np.array(resized)
    return out

# Facings, bei denen das Geschuetzrohr zur Kamera zeigt und deshalb VOR dem Turmgehaeuse
# gezeichnet werden muss. Herleitung: Kamera-Yaw 225, Rohr zeigt zur Kamera wenn
# cos(yaw - theta_i) > 0; theta_i folgt aus dem Facing-Mapping des Renderers
# (classic facings, --facing-flip, --facing-offset 45), kalibriert an Frame 8.
# Empirisch bestaetigt am flachen Render: bei f9..f23 liegt die Muendung tiefer auf dem
# Schirm als das Heck (= naeher an der Kamera). f16 ist dort PCA-degeneriert (Rohre fast
# frontal), gehoert aber ebenfalls in die Menge.
BARREL_IN_FRONT = set(range(9, 25))

def bake_turret(stem, frame_range_list, C0, C1, out_stem, tga_prefix,
                barrel_stem=None, barrel_remap_name=None, barrel_scale=1.0):
    """Turret-Bake: explizite Index-Erkennung (wie Titan-Turret).
    Indices 16-31 → Player-Color-Remap. Grau/Silber → echte RGB. Schwarz → schwarz.
    Optional: composite barrel VXL frames under turret housing."""
    NFRAMES = len(frame_range_list)
    CROP = C1 - C0; TURR_DST = CROP * 3
    print(f"Baking turret {out_stem}: {NFRAMES} frames, {CROP}px → {TURR_DST}px Scale3x (explicit index)")

    barrel_bodies, barrel_remaps, barrel_fsz = (None, None, None)
    if barrel_stem:
        barrel_bodies, barrel_remaps, barrel_fsz = load_barrel_frames(barrel_stem, barrel_remap_name)
        print(f"  + compositing barrel '{barrel_stem}' under turret (barrel canvas {barrel_fsz}px)")

    # Final canvas must be big enough to hold both turret and (possibly larger,
    # off-center-pivot) barrel without clipping — both are centered on the same pivot.
    DST = max(TURR_DST, barrel_fsz) if barrel_fsz else TURR_DST
    meta = json.dumps({"size":[DST,DST],"crop":[0,0,DST,DST]}, separators=(',',':'))

    def center_pad(arr, size, channels):
        h, w = arr.shape[0], arr.shape[1]
        if channels:
            out = np.zeros((size, size, channels), dtype=arr.dtype)
        else:
            out = np.zeros((size, size), dtype=arr.dtype)
        oy = (size - h) // 2
        ox = (size - w) // 2
        out[oy:oy+h, ox:ox+w] = arr
        return out

    bodies, remaps = [], []
    for out_i, fi in enumerate(frame_range_list):
        body_rgba, remap_idx = classify_turret(stem, fi, C0, C1)
        big_rgba  = center_pad(scale3x_rgba(body_rgba), DST, 4)
        big_remap = center_pad(scale3x(remap_idx), DST, None)

        if barrel_bodies is not None:
            b_body = center_pad(barrel_bodies[out_i], DST, 4)
            b_remap = center_pad(barrel_remaps[out_i], DST, None)

            if barrel_scale != 1.0:
                b_body = scale_frame_about_center(b_body, barrel_scale, is_index=False)
                b_remap = scale_frame_about_center(b_remap, barrel_scale, is_index=True)

            # Zeichenreihenfolge haengt vom Facing ab: zeigt das Rohr zur Kamera, gehoert es VOR
            # das Gehaeuse, sonst dahinter. Bei flachen Rohren fiel das nicht auf (kein Ueberlapp);
            # mit der 45deg-Elevation verschwaende das Rohr sonst in der Suedhaelfte hinter dem Turm.
            if out_i in BARREL_IN_FRONT:
                combined = alpha_over(big_rgba, b_body)
                combined_remap = np.where(big_remap >= 176, big_remap, 0).astype(np.uint8)
                barr_remap = np.where(b_remap >= 176, b_remap, 0).astype(np.uint8)
                combined_remap = np.where(barr_remap > 0, barr_remap, combined_remap)
            else:
                combined = alpha_over(b_body, big_rgba)
                combined_remap = np.where(b_remap >= 176, b_remap, 0).astype(np.uint8)
                turr_remap = np.where(big_remap >= 176, big_remap, 0).astype(np.uint8)
                combined_remap = np.where(turr_remap > 0, turr_remap, combined_remap)

            bodies.append(combined)
            remaps.append(combined_remap)
        else:
            bodies.append(big_rgba)
            remaps.append(np.where(big_remap >= 176, big_remap, 0).astype(np.uint8))

    z = zipfile.ZipFile(BITS / f"{out_stem}.zip", 'w', zipfile.ZIP_DEFLATED)
    for i, rgba in enumerate(bodies):
        z.writestr(f"{tga_prefix}-{i:04d}.tga", write_tga_rgba(rgba))
        z.writestr(f"{tga_prefix}-{i:04d}.meta", meta)
    z.close()
    write_remap_png(remaps, DST, NFRAMES, BITS / f"{out_stem}-remap.png")
    print(f"  → {out_stem}.zip + {out_stem}-remap.png ({DST}px, {NFRAMES} frames)")

# ── Ausführung ────────────────────────────────────────────────────────────────

# Walk body: Hybrid (grau/silber → weiß, warm-gold Beine + TS-PC → Player-Color)
bake_walk_body("jugger", range(120), BODY_C0, BODY_C1, "aot-jugg-body", "jugg-body")

# Deployed body: 3 Frames (normal, damaged, dead) — kein Reversal, 1 Facing
bake_body("djugg", range(3), DEPL_C0, DEPL_C1, "aot-jugg-deployed", "jugg-depl")

# Deployed turret: SHP f0=NW, clockwise. UseClassicFacings:True erwartet f0=N → Offset +4
turr_frames = [(1 + i) % 32 for i in range(32)]
bake_turret("djugg_a", turr_frames, DEPL_C0, DEPL_C1, "aot-jugg-turret", "jugg-turr",
            barrel_stem="djuggbar", barrel_remap_name="djuggbar-remap.png", barrel_scale=0.9)

# Deploy-Animation: 18 Frames forward (play-once, kein Facing-Reversal)
bake_body("djuggmk", range(18), DEPL_C0, DEPL_C1, "aot-jugg-make", "jugg-make")

print("Done.")
