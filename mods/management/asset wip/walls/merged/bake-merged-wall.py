"""
Kanonischer Wall-Merger v3.

Regeln:
- Durchgehende Achsen (N&S bzw. E&W) bekommen VOLLE kanonische Streifen
  (kein Stueckwerk mittendrin) -> keine Loecher/Stufen hinter der Mauer.
- Halbe Achsen (nur N, nur S, ...) bekommen 46px-Port-Baender mit Feather;
  den Rest fuellt das native Sandsack-Frame derselben Bitmaske
  (= echte Eck-/End-/Kreuzungsstuecke).
- Tiefensortierung: alles oberhalb der horizontalen Mauerlinie (y<81)
  wird VOR der Mauer gezeichnet, alles darunter DANACH -> die Mauer waechst
  aus den vorderen Sandsaecken heraus. Der Zaun kommt immer zuletzt.
"""
import importlib.util, zipfile, io, json
from PIL import Image, ImageDraw, PngImagePlugin

spec = importlib.util.spec_from_file_location(
    "m", "/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD/extract-meg-unit.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

MEG = "/Users/moritzgiuliani/Library/Application Support/Steam/steamapps/common/CnCRemastered/Data/TEXTURES_TD_SRGB.MEG"
contents, raw = m.parse_meg(MEG)
_zips = {}
TILE = 128
OUT = "/private/tmp/claude-501/-Users-moritzgiuliani-Documents-openRA-Projekte/b69a688a-de17-4f40-9dba-d553da3ca5dd/scratchpad"
N, E, S, W = 1, 2, 4, 8


def get_zip(zn):
    if zn not in _zips:
        key = [k for k in contents if k.upper().endswith(zn)][0]
        off, size = contents[key]
        _zips[zn] = zipfile.ZipFile(io.BytesIO(raw[off:off + size]))
    return _zips[zn]


def load(zn, frame):
    zf = get_zip(zn)
    names = set(zf.namelist())
    img = Image.open(io.BytesIO(zf.read(f"{frame}.tga"))).convert("RGBA")
    if f"{frame}.meta" in names:
        meta = json.loads(zf.read(f"{frame}.meta"))
        full = Image.new("RGBA", tuple(meta["size"]), (0, 0, 0, 0))
        full.paste(img, tuple(meta["crop"][:2]))
        return full
    return img


# Damage-Offsets der aktuellen Bake-Variante (idle=0/0/0,
# scratched=16/0/0, damaged=32/16/16). Fehlende Remaster-Frames
# (sbag-0016, cycl-0016) fallen auf das Idle-Frame zurueck.
BRIK_OFF = 0
SBAG_OFF = 0
CYCL_OFF = 0


def _try_load(zn, prefix, i, fallback_i):
    try:
        return load(zn, f"{prefix}-{i:04d}")
    except KeyError:
        return load(zn, f"{prefix}-{fallback_i:04d}")


def brik(i): return _try_load("BRIK.ZIP", "brik", i + BRIK_OFF, i)
def sbag(i): return _try_load("SBAG.ZIP", "sbag", i + SBAG_OFF, i)
def cycl(i): return _try_load("CYCL.ZIP", "cycl", i + CYCL_OFF, i)


def opaque_cols(img, th=200, mc=6):
    px = img.load(); w, h = img.size
    c = [x for x in range(w) if sum(1 for y in range(h) if px[x, y][3] >= th) >= mc]
    return min(c), max(c)


def opaque_rows(img, th=200, mc=6):
    px = img.load(); w, h = img.size
    r = [y for y in range(h) if sum(1 for x in range(w) if px[x, y][3] >= th) >= mc]
    return min(r), max(r)


BX0, BX1 = opaque_cols(brik(5))
BY0, BY1 = opaque_rows(brik(10))
SL, SR = opaque_cols(sbag(5))
ST, SB = opaque_rows(sbag(10))

TUCK = 4
VL_W = BX0 + TUCK
VR_X = BX1 - TUCK
VR_W = TILE - VR_X
HT_H = BY0 + TUCK
HB_Y = BY1 - TUCK
HB_H = TILE - HB_Y

sb5, sb10 = sbag(5), sbag(10)
STRIP_L = sb5.crop((SL, 0, SL + VL_W, TILE))
STRIP_R = sb5.crop((SR + 1 - VR_W, 0, SR + 1, TILE))
STRIP_T = sb10.crop((0, ST, TILE, ST + HT_H))
# Unterer Streifen in VOLLER Sandsack-Hoehe: natuerliche Ober-Silhouette
# ueberlappt den Mauersockel (keine geschnittene Oberkante), natuerliche
# Unterkante schliesst mit der Zellkante ab.
STRIP_B = sb10.crop((0, ST, TILE, SB + 1))
SB_Y = TILE - (SB + 1 - ST)  # Paste-Y des unteren Streifens

BAND = 46
FEATHER = 12

# Zaun-Geometrie: vertikales Band (bleibt vorn, zwischen Mauer und Ost-Flanke),
# horizontaler Teil wird HINTER die Mauer geschoben (zwischen Mauer und Nord-Flanke)
_cb = cycl(5).getbbox()
CV0, CV1 = _cb[0], _cb[2]
FENCE_DY = -34  # Verschiebung des horizontalen Zaunteils nach oben (hinter die Mauer)
TURN_N = 46     # Ecke von Norden: vertikaler Zaun endet hier (innen an der Mauer)
TURN_S = 72     # Ecke von Sueden: vertikaler Zaun beginnt hier (laeuft in die Mauer)


def feather(img, side, width=None):
    width = width or FEATHER
    img = img.copy(); px = img.load(); w, h = img.size
    for i in range(width):
        f = i / width
        if side in ("top", "bottom"):
            y = i if side == "top" else h - 1 - i
            for x in range(w):
                r, g, b, a = px[x, y]; px[x, y] = (r, g, b, int(a * f))
        else:
            x = i if side == "left" else w - 1 - i
            for y in range(h):
                r, g, b, a = px[x, y]; px[x, y] = (r, g, b, int(a * f))
    return img


def feather_sides(img, sides):
    for side, width in sides.items():
        if width:
            img = feather(img, side, width)
    return img


def shifted(img, dx, dy):
    """Ganzes Frame um (dx,dy) verschoben, Canvas bleibt 128x128."""
    out = Image.new("RGBA", (TILE, TILE), (0, 0, 0, 0))
    src = img.crop((max(0, -dx), max(0, -dy), TILE - max(0, dx), TILE - max(0, dy)))
    out.alpha_composite(src, (max(0, dx), max(0, dy)))
    return out


def clear_cols(img, x0, x1):
    img = img.copy()
    img.paste(Image.new("RGBA", (x1 - x0, img.size[1]), (0, 0, 0, 0)), (x0, 0))
    return img


def arm_rows(img):
    px = img.load()
    rows = [y for y in range(TILE) if any(px[x, y][3] >= 100 for x in range(BX0, BX1 + 1))]
    return (min(rows), max(rows)) if rows else (BY0, BY1)


def arm_cols(img):
    px = img.load()
    cols = [x for x in range(TILE) if any(px[x, y][3] >= 100 for y in range(BY0, BY1 + 1))]
    return (min(cols), max(cols)) if cols else (BX0, BX1)


# Flanken-Verschiebungen relativ zur nativen Sandsack-Position
DXL = -SL                       # linke Flanke
DXR = VR_X - (SR + 1 - VR_W)    # rechte Flanke
DYT = -ST                       # obere Flanke
DYB = SB_Y - ST                 # untere Flanke


def curve_piece(frame_bits, dx, dy, box, feathers):
    """Kurvenstueck: natives Eck-Frame diagonal verschoben, sodass seine
    beiden Arme exakt auf den kanonischen Flanken-Positionen liegen — die
    Biegung dazwischen ist echte native Kurvengrafik. Auf die Tasche
    gecroppt und an allen Uebergaengen gefeathert."""
    img = shifted(sbag(frame_bits), dx, dy).crop(box)
    return feather_sides(img, feathers), (box[0], box[1])


def build_layers(bits):
    back, front = [], []
    bf = brik(bits)
    vb = bits & (N | S)
    hb = bits & (E | W)
    junction = bool(vb) and bool(hb)
    corner = junction and bin(bits).count("1") == 2

    # 1. Native Basis fuer Stummel/Solo (Endkappen) -- AUSSER bits==0:
    # der native Solo-Frame ist nur EIN Sandsack-Haufen (fast komplett im
    # Sueden, kaum Nordreihe) -- sieht "einseitig" aus im Vergleich zu
    # verbundenen Stuecken. Bits==0 nutzt daher direkt die vollen
    # Nord-/Sued-Streifen wie ein gerader Durchgang (Abschnitt 3).
    if bits == 0:
        back.append((STRIP_T, (0, 0)))
        front.append((STRIP_B, (0, SB_Y)))
    elif not junction and bits not in (N | S, E | W):
        base = sbag(bits)
        base_n = base.crop((0, 0, TILE, HB_Y))
        base_s = base.crop((0, HB_Y, TILE, TILE))
        if bits & S:
            base_s = clear_cols(base_s, SL, SR + 1)
        back.append((base_n, (0, 0)))
        front.append((base_s, (0, HB_Y)))

    # 2. Vertikale Flanken
    if vb == (N | S):
        back.append((STRIP_L.crop((0, 0, VL_W, HB_Y)), (0, 0)))
        back.append((STRIP_R.crop((0, 0, VR_W, HB_Y)), (VR_X, 0)))
        front.append((STRIP_L.crop((0, HB_Y, VL_W, TILE)), (0, HB_Y)))
        front.append((STRIP_R.crop((0, HB_Y, VR_W, TILE)), (VR_X, HB_Y)))
    elif vb == N:
        if bits == N:
            l_end = r_end = arm_rows(bf)[1]
        else:
            # Ecke: Aussenflanke laeuft weiter bis zur Aussenkurve
            l_end = HB_Y + 6 if (corner and hb == E) else BAND
            r_end = HB_Y + 6 if (corner and hb == W) else BAND
        back.append((feather(STRIP_L.crop((0, 0, VL_W, l_end)), "bottom"), (0, 0)))
        back.append((feather(STRIP_R.crop((0, 0, VR_W, r_end)), "bottom"), (VR_X, 0)))
    elif vb == S:
        if bits == S:
            l_start = r_start = arm_rows(bf)[0]
        else:
            l_start = HT_H - 6 if (corner and hb == E) else TILE - BAND
            r_start = HT_H - 6 if (corner and hb == W) else TILE - BAND
        for strip, start, x, w in ((STRIP_L, l_start, 0, VL_W), (STRIP_R, r_start, VR_X, VR_W)):
            piece = feather(strip.crop((0, start, w, TILE)), "top")
            if start < HB_Y:
                back.append((piece.crop((0, 0, w, HB_Y - start)), (x, start)))
                front.append((piece.crop((0, HB_Y - start, w, TILE - start)), (x, HB_Y)))
            else:
                front.append((piece, (x, start)))

    # 3. Horizontale Flanken
    if hb == (E | W):
        back.append((STRIP_T, (0, 0)))
        strip_b = clear_cols(STRIP_B, BX0, BX1) if bits & S else STRIP_B
        front.append((strip_b, (0, SB_Y)))
    elif hb == W:
        if bits == W:
            t_end = b_end = arm_cols(bf)[1]
        else:
            t_end = VR_X + 8 if (corner and vb == S) else VL_W + 20
            b_end = VR_X + 8 if (corner and vb == N) else VL_W + 20
        # Sued-Arm-Korridor freihalten (sonst liegen Front-Saecke quer ueber
        # der nach Sueden laufenden Mauer — Bug an S|W-Ecken/T-Stuecken)
        strip_b = clear_cols(STRIP_B, BX0, BX1) if bits & S else STRIP_B
        back.append((feather(STRIP_T.crop((0, 0, t_end, HT_H)), "right"), (0, 0)))
        front.append((feather(strip_b.crop((0, 0, b_end, strip_b.size[1])), "right"), (0, SB_Y)))
    elif hb == E:
        if bits == E:
            t_start = b_start = arm_cols(bf)[0]
        else:
            t_start = VL_W - 8 if (corner and vb == S) else VR_X - 20
            b_start = VL_W - 8 if (corner and vb == N) else VR_X - 20
        strip_b = clear_cols(STRIP_B, BX0, BX1) if bits & S else STRIP_B
        back.append((feather(STRIP_T.crop((t_start, 0, TILE, HT_H)), "left"), (t_start, 0)))
        front.append((feather(strip_b.crop((b_start, 0, TILE, strip_b.size[1])), "left"), (b_start, SB_Y)))

    # 4. Kurvenstuecke an Knoten: Innenkurven in jeder Tasche zwischen
    #    zwei Armen, Aussenkurve bei 2er-Ecken
    if junction:
        if (bits & N) and (bits & E):
            back.append(curve_piece(N | E, DXR, DYT, (VR_X - 6, 0, TILE, HT_H + 16),
                                    {"left": 10, "bottom": 12, "top": 10, "right": 10}))
        if (bits & N) and (bits & W):
            back.append(curve_piece(N | W, DXL, DYT, (0, 0, VL_W + 6, HT_H + 16),
                                    {"right": 10, "bottom": 12, "top": 10, "left": 10}))
        if (bits & S) and (bits & E):
            front.append(curve_piece(S | E, DXR, DYB, (VR_X - 4, HB_Y, TILE, TILE),
                                     {"left": 10, "top": 10, "right": 10}))
        if (bits & S) and (bits & W):
            front.append(curve_piece(S | W, DXL, DYB, (0, HB_Y, VL_W + 4, TILE),
                                     {"right": 10, "top": 10, "left": 10}))
        if corner:
            if bits == (N | E):
                front.append(curve_piece(N | E, DXL, DYB, (0, HB_Y - 12, VL_W + 36, TILE),
                                         {"top": 10, "right": 12}))
            elif bits == (N | W):
                front.append(curve_piece(N | W, DXR, DYB, (VR_X - 36, HB_Y - 12, TILE, TILE),
                                         {"top": 10, "left": 12}))
            elif bits == (S | E):
                back.append(curve_piece(S | E, DXL, DYT, (0, 0, VL_W + 36, HT_H + 18),
                                        {"bottom": 10, "right": 12}))
            elif bits == (S | W):
                back.append(curve_piece(S | W, DXR, DYT, (VR_X - 36, 0, TILE, HT_H + 18),
                                        {"bottom": 10, "left": 12}))

    return back, front


def fence_parts(bits):
    """(hinterer Teil, vorderer Teil) des Zauns.
    Zusammengesetzt aus den PUREN Richtungs-Frames der Zaun-Familie:
    vertikaler Anteil = cycl(bits&NS) nativ vorn (zwischen Mauer und
    Ost-Flanke, mit nativen Endpfosten), horizontaler Anteil =
    cycl(bits&EW) nach oben hinter die Mauer geschoben (zwischen Mauer
    und Nord-Sandsackreihe). Keine Frame-Zerlegung -> keine Fragmente."""
    if bits == 0:
        # Solo-Segment: keine Nachbarn, aber optisch soll die gerollte
        # Stacheldraht-Krone trotzdem zu sehen sein (gleiche Textur/Position
        # wie bei geraden Ost-West-Stuecken), damit FS/Non-FS unterscheidbar
        # bleibt UND die Sandsaecke nicht verdeckt werden (der urspr. Bug).
        back_part = cycl(E | W).crop((0, -FENCE_DY, TILE, TILE))
        return back_part, None
    vbits = bits & (N | S)
    hbits = bits & (E | W)
    back_part = None
    if hbits:
        back_part = cycl(hbits).crop((0, -FENCE_DY, TILE, TILE))
        if vbits:
            # Ecke/T: horizontaler Zaun beginnt INNEN an der vertikalen Mauer;
            # der Schnitt liegt verdeckt hinter der Mauer, der native
            # End-Pfosten wuerde sonst jenseits der Mauer herausragen
            if hbits == E:
                back_part = clear_cols(back_part, 0, BX0 + TUCK)
            elif hbits == W:
                back_part = clear_cols(back_part, BX1 - TUCK, TILE)
    if vbits and (not hbits or vbits == (N | S)):
        # gerade Durchfahrt, Kreuzung oder purer Stummel: natives Frame
        front_part = (cycl(vbits), (0, 0))
    elif vbits == N:
        # Ecke/T von Norden: Zaun endet INNEN an der Mauer (kein Pfosten
        # jenseits der Mauer); Uebergang zum horizontalen Zaun an der
        # Kreuzungsstelle der beiden Zaunlinien
        piece = feather(cycl(N | S).crop((0, 0, TILE, TURN_N)), "bottom")
        front_part = (piece, (0, 0))
    elif vbits == S:
        piece = feather(cycl(N | S).crop((0, TURN_S, TILE, TILE)), "top")
        front_part = (piece, (0, TURN_S))
    else:
        front_part = None
    return back_part, front_part


def build_tile(bits, with_fence=True):
    back, front = build_layers(bits)
    fence_back, fence_front = fence_parts(bits) if with_fence else (None, None)
    out = Image.new("RGBA", (TILE, TILE), (0, 0, 0, 0))
    for img, pos in back:
        out.alpha_composite(img, pos)
    if fence_back is not None:
        out.alpha_composite(fence_back, (0, 0))
    out.alpha_composite(brik(bits))
    for img, pos in front:
        out.alpha_composite(img, pos)
    if fence_front is not None:
        out.alpha_composite(fence_front[0], fence_front[1])
    return out


LABELS = {5: "Nord-Sued", 10: "Ost-West", 3: "Ecke (N+O)", 15: "Kreuzung"}


def sheet(with_fence, title, fname):
    pad, lab = 16, 22
    sh = Image.new("RGBA", (2 * TILE + 3 * pad, 2 * (TILE + lab) + 3 * pad + 30), (40, 40, 44, 255))
    d = ImageDraw.Draw(sh)
    d.text((pad, 8), title, fill=(255, 255, 255, 255))
    for i, f in enumerate([5, 10, 3, 15]):
        x = pad + (i % 2) * (TILE + pad)
        y = 30 + pad + (i // 2) * (TILE + lab + pad)
        sh.alpha_composite(build_tile(f, with_fence), (x, y))
        d.text((x, y + TILE + 2), LABELS[f], fill=(230, 230, 230, 255))
    sh.resize((sh.width * 2, sh.height * 2), Image.NEAREST).save(f"{OUT}/{fname}")


def assembly(cells, cols, rows, fname, with_fence=True):
    img = Image.new("RGBA", (cols * TILE, rows * TILE), (30, 30, 30, 255))
    for (cx, cy), bits in cells.items():
        img.alpha_composite(build_tile(bits, with_fence), (cx * TILE, cy * TILE))
    img.resize((img.width * 2, img.height * 2), Image.NEAREST).save(f"{OUT}/{fname}")


def rebuild_strips():
    """Streifen aus den Quellframes der aktuellen Damage-Variante neu bauen
    (Geometrie/Positionen bleiben die der Idle-Vermessung)."""
    global STRIP_L, STRIP_R, STRIP_T, STRIP_B
    s5, s10 = sbag(5), sbag(10)
    STRIP_L = s5.crop((SL, 0, SL + VL_W, TILE))
    STRIP_R = s5.crop((SR + 1 - VR_W, 0, SR + 1, TILE))
    STRIP_T = s10.crop((0, ST, TILE, ST + HT_H))
    STRIP_B = s10.crop((0, ST, TILE, SB + 1))


def set_variant(b, s, c):
    global BRIK_OFF, SBAG_OFF, CYCL_OFF
    BRIK_OFF, SBAG_OFF, CYCL_OFF = b, s, c
    rebuild_strips()


def bake(with_fence, out_path):
    """48-Frame-Sheet: 0-15 idle, 16-31 scratched, 32-47 damaged —
    identisches Layout wie das originale BRIK."""
    frames = []
    for offs in [(0, 0, 0), (16, 0, 0), (32, 16, 16)]:
        set_variant(*offs)
        for bits in range(16):
            frames.append(build_tile(bits, with_fence))
    set_variant(0, 0, 0)
    out = Image.new("RGBA", (TILE, TILE * len(frames)), (0, 0, 0, 0))
    for i, f in enumerate(frames):
        out.alpha_composite(f, (0, i * TILE))
    meta = PngImagePlugin.PngInfo()
    meta.add_text("FrameSize", f"{TILE},{TILE}")
    meta.add_text("FrameAmount", str(len(frames)))
    out.save(out_path, pnginfo=meta)
    print("baked", out_path, len(frames), "frames")


if __name__ == "__main__":
    sheet(True, "V3: Brick + Sandbag + Cyclone", "canon3_sheet_with_cycl.png")
    sheet(False, "V3: Brick + Sandbag", "canon3_sheet_without_cycl.png")
    assembly({(0, 0): S, (0, 1): N | S, (0, 2): N | E, (1, 2): E | W, (2, 2): W}, 3, 3, "canon3_L_wall.png")
    assembly({(1, 0): S, (1, 1): N | E | S | W, (1, 2): N, (0, 1): E, (2, 1): W}, 3, 3, "canon3_cross.png")

    BITS_DIR = "/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD/mods/cnc/bits"
    bake(False, f"{BITS_DIR}/aot-wall-gdi.png")
    bake(True, f"{BITS_DIR}/aot-wall-gdi-fs.png")
    print("done")
