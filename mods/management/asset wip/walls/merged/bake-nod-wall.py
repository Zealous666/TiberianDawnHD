"""
Bake NOD Barbwire Fence + Laser-Fences-Upgrade.

- aot-wall-nod.png: 32 Frames (idle 0-15, damaged 16-31) — purer RA-FENC.
- aot-wall-nod-laser.png: 128 Frames — [idle: 4 Pulsphasen x 16][damaged: dito].
  Frame-Index im Spiel (AotWithWallPulseBody) = Nachbarschaftsmaske + 16*Phase.
  Pulsphasen: sin-Verlauf [0.5, 1.0, 0.5, 0.0] -> weicher Loop.
  Saeulen an allen Knoten (Ecke/T/Kreuz/Ende/Solo) aus brik-0000-Enden,
  Laser-Dreieck (2 Seitenstrahlen +-7px, Hauptstrahl mittig) IMMER oberste
  Ebene, laeuft sichtbar zum Emitter auf der Saeulenspitze.
"""
import importlib.util, sys, math
from PIL import Image, ImageDraw, PngImagePlugin

SCRATCH = "/private/tmp/claude-501/-Users-moritzgiuliani-Documents-openRA-Projekte/b69a688a-de17-4f40-9dba-d553da3ca5dd/scratchpad"
spec = importlib.util.spec_from_file_location("cwn", f"{SCRATCH}/canonical_walls_nod.py")
cw = importlib.util.module_from_spec(spec)
sys.modules["cwn"] = cw
spec.loader.exec_module(cw)
N, E, S, W = cw.N, cw.E, cw.S, cw.W
TILE = 128
CX, CY = 63, 63
SIDE_D = 7
EMIT = (63, 45)
PULSES = [0.5, 1.0, 0.5, 0.0]

SIDE_CORE = (255, 60, 40, 210)
TOP_CORE = (255, 90, 60, 255)


def make_pillar():
    b0 = cw.brik(0)
    left = b0.crop((2, 28, 26, 100))
    right = b0.crop((100, 28, 124, 100))
    p = Image.new("RGBA", (48, 72), (0, 0, 0, 0))
    p.alpha_composite(left, (0, 0))
    p.alpha_composite(right, (24, 0))
    return p


def pillar_box():
    p = make_pillar()
    return p, CX - p.width // 2 + 1, CY - p.height // 2 + 1


def laser_layer(bits, pulse, to_node, px, pr, py, pb):
    img = Image.new("RGBA", (TILE, TILE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    glow = (255, 40, 20, int(45 + 75 * pulse))

    def beams(direction):
        for off, core, gw in [(-SIDE_D, SIDE_CORE, 4), (SIDE_D, SIDE_CORE, 4), (0, TOP_CORE, 7)]:
            cwid = 2 if off == 0 else 1
            if direction == "N":
                pts = [(CX + off, 0), (CX + off, py + 4), EMIT] if to_node else [(CX + off, 0), (CX + off, CY + SIDE_D)]
            elif direction == "S":
                pts = [(CX + off, TILE), (CX + off, pb - 6), EMIT] if to_node else [(CX + off, CY - SIDE_D), (CX + off, TILE)]
            elif direction == "W":
                pts = [(0, CY + off), (px + 2, CY + off), EMIT] if to_node else [(0, CY + off), (CX + SIDE_D, CY + off)]
            else:
                pts = [(TILE, CY + off), (pr - 2, CY + off), EMIT] if to_node else [(CX - SIDE_D, CY + off), (TILE, CY + off)]
            d.line(pts, fill=glow, width=gw, joint="curve")
            d.line(pts, fill=core, width=cwid, joint="curve")

    if bits & N:
        beams("N")
    if bits & S:
        beams("S")
    if bits & W:
        beams("W")
    if bits & E:
        beams("E")
    return img


def tile_base(bits):
    out = Image.new("RGBA", (TILE, TILE), (0, 0, 0, 0))
    out.alpha_composite(cw.sbag(bits))  # sbag() laedt FENC (mit Damage-Offset/Fallback)
    return out


def tile_laser(bits, pulse):
    out = Image.new("RGBA", (TILE, TILE), (0, 0, 0, 0))
    out.alpha_composite(cw.sbag(bits))
    pillar, px, py = pillar_box()
    pr, pb = px + pillar.width, py + pillar.height
    is_node = bits not in (N | S, E | W)
    if is_node:
        out.alpha_composite(pillar, (px, py))
    out.alpha_composite(laser_layer(bits, pulse, is_node, px, pr, py, pb))
    if is_node:
        d = ImageDraw.Draw(out)
        a = int(120 + 80 * pulse)
        d.ellipse([EMIT[0] - 5, EMIT[1] - 5, EMIT[0] + 5, EMIT[1] + 5], fill=(255, 120, 90, a))
        d.ellipse([EMIT[0] - 2, EMIT[1] - 2, EMIT[0] + 2, EMIT[1] + 2], fill=(255, 235, 210, 255))
    return out


def save_sheet(frames, path):
    out = Image.new("RGBA", (TILE, TILE * len(frames)), (0, 0, 0, 0))
    for i, f in enumerate(frames):
        out.alpha_composite(f, (0, i * TILE))
    meta = PngImagePlugin.PngInfo()
    meta.add_text("FrameSize", f"{TILE},{TILE}")
    meta.add_text("FrameAmount", str(len(frames)))
    out.save(path, pnginfo=meta)
    print("baked", path, len(frames), "frames")


BITS_DIR = "/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD/mods/cnc/bits"

# --- Basis: Barbwire Fence (FENC pur) ---
frames = []
for fenc_off in [0, 16]:
    cw.SBAG_OFF = fenc_off
    for bits in range(16):
        frames.append(tile_base(bits))
cw.SBAG_OFF = 0
save_sheet(frames, f"{BITS_DIR}/aot-wall-nod.png")

# --- Laser-Variante: 4 Pulsphasen x 16, je Damage-Stufe ---
frames = []
for fenc_off, brik_off in [(0, 0), (16, 32)]:
    cw.SBAG_OFF = fenc_off
    cw.BRIK_OFF = brik_off  # beschaedigte Saeule im Damaged-Block
    for pulse in PULSES:
        for bits in range(16):
            frames.append(tile_laser(bits, pulse))
cw.SBAG_OFF = 0
cw.BRIK_OFF = 0
save_sheet(frames, f"{BITS_DIR}/aot-wall-nod-laser.png")
print("done")
