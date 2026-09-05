#!/usr/bin/env python3
"""
Erstellt ein synthetisches VXL+HVA+PAL für den --vxl-to-png Test.
VXL-Format korrekt nach VxlReader.cs Spezifikation.
"""
import struct, os, math

OUT = "/tmp/testvxl"
os.makedirs(OUT, exist_ok=True)

# ── PAL ───────────────────────────────────────────────────────────────────────
def make_pal():
    pal = bytearray(768)
    # 0 = transparent (bleibt 0)
    # 1..15: Graustufen (6-bit 0-63)
    for i in range(1, 16):
        v = i * 4
        pal[i*3:i*3+3] = [v, v, v]
    # 16..31: GDI Olivgrün (Fahrzeug-Körper, dunkel→hell)
    for i in range(16, 32):
        t = (i - 16) / 15.0
        r = int((24 + 12*t))
        g = int((30 + 10*t))
        b = int((10 + 8*t))
        pal[i*3:i*3+3] = [min(63,r), min(63,g), min(63,b)]
    # 32..63: Metall-Blaugrau
    for i in range(32, 64):
        t = (i - 32) / 31.0
        v = int(15 + 20*t)
        pal[i*3:i*3+3] = [min(63,v), min(63,v+2), min(63,v+4)]
    return bytes(pal)

# ── VXL ───────────────────────────────────────────────────────────────────────
# Format nach VxlReader.cs:
#
# Offset 0    : 16 bytes "Voxel Animation\0"
# Offset 16   : uint32 (0)
# Offset 20   : uint32 LimbCount
# Offset 24   : uint32 (0)
# Offset 28   : uint32 bodySize
# Offset 32   : 770 bytes (palette stub + padding)
# Offset 802  : 28 bytes × LimbCount Limb-Header (16 Name + 12 unused)
# Offset 802+28*N : body_data (bodySize bytes)
#   Per Limb at body_offset[i]:
#     int32[sx*sy] colStart (offset von dataStart, oder -1)
#     int32[sx*sy] colEnd   (duplikat, ignoriert)
#     dataStart:
#       Per Spalte i (colStart[i] != -1):
#         Spans bis z == sz:
#           uint8 skip, uint8 count, count×(uint8 color, uint8 normal), uint8 dup
# Offset 802+28*N+bodySize : 92 bytes × LimbCount Limb-Footer
#   uint32 dataOffset, uint32×2 unknown, float scale, 48 bytes unknown,
#   6×float bounds, 3×uint8 size, uint8 normalType

def build_body(sx, sy, sz, vox_dict):
    """
    vox_dict: {(x,y,z): (color, normal)}  — color=0 → transparent
    Gibt (body_bytes, data_offset_in_body) zurück.
    data_offset = Anfang dieser Limb-Daten im Gesamt-Body.
    """
    base = sx * sy
    col_table_size = 2 * 4 * base  # zwei int32-Arrays

    # Baue Spalten-Daten
    col_spans = {}  # col_idx → bytes
    for iy in range(sy):
        for ix in range(sx):
            col_i = iy * sx + ix
            # Erzeuge einen einzigen Span der alle z-Ebenen abdeckt
            # (einfacher als sparse spans)
            span = bytearray()
            span += struct.pack("B", 0)    # skip=0
            span += struct.pack("B", sz)   # count=sz
            for z in range(sz):
                c, n = vox_dict.get((ix, iy, z), (0, 0))
                span += struct.pack("BB", c, n)
            span += struct.pack("B", sz)   # dup
            col_spans[col_i] = bytes(span)

    # Berechne colStart-Offsets (relativ zu dataStart)
    col_starts = []
    offset = 0
    for col_i in range(base):
        col_starts.append(offset)
        offset += len(col_spans[col_i])

    # Serialisiere
    body = bytearray()
    for cs in col_starts:
        body += struct.pack("<i", cs)   # colStart
    for cs in col_starts:
        body += struct.pack("<i", cs + len(col_spans[base-1]))  # colEnd (stub)
    # dataStart = hier
    for col_i in range(base):
        body += col_spans[col_i]

    return bytes(body)


def write_vxl(path, limbs):
    """
    limbs: Liste von (name_str, sx, sy, sz, vox_dict)
    """
    N = len(limbs)

    # Baue alle Body-Daten
    bodies = []
    body_offset = 0
    body_data_parts = []
    for name, sx, sy, sz, vox_dict in limbs:
        bd = build_body(sx, sy, sz, vox_dict)
        bodies.append((body_offset, sx, sy, sz, vox_dict, bd))
        body_data_parts.append(bd)
        body_offset += len(bd)

    total_body_size = sum(len(bd) for bd in body_data_parts)

    # ── Header ───────────────────────────────────────────────────────────────
    out = bytearray()
    out += b"Voxel Animation\x00"  # 16 bytes
    out += struct.pack("<I", 0)                  # unknown
    out += struct.pack("<I", N)                  # LimbCount
    out += struct.pack("<I", 0)                  # unknown
    out += struct.pack("<I", total_body_size)    # bodySize
    out += bytes(770)                            # palette stub

    # ── Limb-Header ──────────────────────────────────────────────────────────
    for name, sx, sy, sz, vox_dict in limbs:
        out += name.encode("ascii")[:15].ljust(16, b"\x00")
        out += bytes(12)  # unused

    # ── Body ─────────────────────────────────────────────────────────────────
    for bd in body_data_parts:
        out += bd

    # ── Limb-Footer ──────────────────────────────────────────────────────────
    for (data_off, sx, sy, sz, vox_dict, bd) in bodies:
        # Bounds aus echten Voxel-Positionen
        vox_keys = list(vox_dict.keys())
        if vox_keys:
            xs = [p[0] for p in vox_keys]; ys = [p[1] for p in vox_keys]; zs = [p[2] for p in vox_keys]
            bmin = [min(xs)-0.5, min(ys)-0.5, min(zs)-0.5]
            bmax = [max(xs)+0.5, max(ys)+0.5, max(zs)+0.5]
        else:
            bmin = [0.0, 0.0, 0.0]; bmax = [float(sx), float(sy), float(sz)]

        scale = 1.0
        out += struct.pack("<I", data_off)   # body offset
        out += struct.pack("<II", 0, 0)      # unknown × 2
        out += struct.pack("<f", scale)      # scale
        out += bytes(48)                     # 48 bytes unknown (transform stub)
        out += struct.pack("<6f", *bmin, *bmax)
        out += struct.pack("BBB", sx, sy, sz)
        out += struct.pack("B", 2)           # NormalType.TiberianSun

    with open(path, "wb") as f:
        f.write(out)
    print(f"  {path} ({len(out)} bytes)")


# ── HVA ───────────────────────────────────────────────────────────────────────
# Format nach HvaReader.cs:
# Offset 0  : 16 bytes (skipped)
# Offset 16 : uint32 FrameCount
# Offset 20 : uint32 LimbCount
# Offset 24 : 16 × LimbCount bytes (Limb-Namen, übersprungen)
# Offset 24+16*N : 12 × float × FrameCount × LimbCount (3×4 Matrizen)

def write_hva(path, limb_count, frame_count=1):
    data = bytearray(16)                     # 16 bytes header (skipped)
    data += struct.pack("<I", frame_count)
    data += struct.pack("<I", limb_count)
    for i in range(limb_count):              # Limb-Namen
        data += f"BODY{i}".encode("ascii").ljust(16, b"\x00")[:16]
    # Identity 3×4 Matrix (row-major):
    # [1, 0, 0, 0]
    # [0, 1, 0, 0]
    # [0, 0, 1, 0]
    identity = [1,0,0,0, 0,1,0,0, 0,0,1,0]
    for _ in range(frame_count * limb_count):
        for v in identity:
            data += struct.pack("<f", float(v))
    with open(path, "wb") as f:
        f.write(data)
    print(f"  {path} ({len(data)} bytes)")


# ── Modell: Tank-Körper 12×16×6 ───────────────────────────────────────────────
def make_tank_body(sx=12, sy=16, sz=6):
    vox = {}
    cx, cy = sx / 2.0, sy / 2.0
    for x in range(sx):
        for y in range(sy):
            # Ovale Form in XY
            dx = (x - cx + 0.5) / (sx / 2.2)
            dy = (y - cy + 0.5) / (sy / 2.2)
            if dx*dx + dy*dy > 1.0:
                continue
            for z in range(sz):
                on_top   = z == sz - 1
                on_side  = (x == 0 or x == sx-1 or y == 0 or y == sy-1)
                on_edge  = on_side or z == 0
                if on_top:
                    color = 27   # hell oben
                    normal = 32  # index ≈ up
                elif on_edge:
                    color = 20   # mittelhell Seite
                    normal = 0
                else:
                    color = 0    # innen = transparent (skip)
                    continue
                vox[(x, y, z)] = (color, normal)
    return sx, sy, sz, vox


# ── Main ──────────────────────────────────────────────────────────────────────
print(f"Erstelle Test-VXL in {OUT}...")
sx, sy, sz, body_vox = make_tank_body()
print(f"  Voxel-Anzahl: {len(body_vox)} (von {sx*sy*sz} Gitterzellen)")

write_vxl(f"{OUT}/test.vxl", [("BODY", sx, sy, sz, body_vox)])
write_hva(f"{OUT}/test.hva", limb_count=1, frame_count=1)
with open(f"{OUT}/test.pal", "wb") as f:
    f.write(make_pal())
print(f"  {OUT}/test.pal (768 bytes)")
print("\nFertig. Render-Befehl:")
print(f"  cd engine && MOD_SEARCH_PATHS='../mods' ENGINE_DIR='..' \\")
print(f"    dotnet bin/OpenRA.Utility.dll cnc --vxl-to-png \\")
print(f"    {OUT}/test.vxl {OUT}/test.hva {OUT}/test.pal \\")
print(f"    --output-dir {OUT}/out")
