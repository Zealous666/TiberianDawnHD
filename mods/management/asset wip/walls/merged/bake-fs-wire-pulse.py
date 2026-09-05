"""
Baut die pulsierende Firestorm-Zaun-Krone DIREKT in die aot-wall-gdi-fs
Textur ein (kein separates Overlay-Sprite mehr -- das verursachte die
Additive-Blend/Scale-Probleme der vorherigen Versuche).

Technik: Fuer jeden der 16 Verbindungszustaende wird dieselbe Layer-
Komposition wie im normalen FS-Bake verwendet (Brick+Sandsack unveraendert),
aber die Zaun-Layer (fence_back/fence_front aus fence_parts()) werden pro
Frame Richtung Hellblau/Weiss eingefaerbt -- 5 Phasen, aufsteigend bis fast
weiss und wieder abfallend ("Pulse"). Frame-Index = Maske + 16*Phase, wie
beim bereits funktionierenden AotWithWallPulseBody (NOD-Laserzaun-System).
"""
import importlib.util, sys
from PIL import Image, PngImagePlugin
import numpy as np

SCRATCH = "/private/tmp/claude-501/-Users-moritzgiuliani-Documents-openRA-Projekte/b69a688a-de17-4f40-9dba-d553da3ca5dd/scratchpad"
spec = importlib.util.spec_from_file_location("cw", f"{SCRATCH}/canonical_walls3.py")
cw = importlib.util.module_from_spec(spec)
sys.modules["cw"] = cw
spec.loader.exec_module(cw)

TILE = cw.TILE
BITS_DIR = "/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD/mods/cnc/bits"

LIGHT_BLUE = np.array([140, 200, 255], dtype=np.float32)
WHITE = np.array([245, 250, 255], dtype=np.float32)
PULSE = [0.25, 0.6, 0.95, 0.55, 0.25]  # 5 Phasen: aufsteigend bis fast weiss, wieder abfallend


def recolor(img, t):
    """Faerbt NUR die sichtbaren (alpha>0) Pixel des Zaun-Layers Richtung
    Hellblau->Weiss ein, Alpha unveraendert. t=0 => unveraendert."""
    arr = np.array(img, dtype=np.float32)
    rgb = arr[..., :3]
    a = arr[..., 3:4]
    glow_color = LIGHT_BLUE + (WHITE - LIGHT_BLUE) * t
    blended = rgb * (1 - t) + glow_color * t
    out = np.concatenate([blended, a], axis=-1)
    return Image.fromarray(np.clip(out, 0, 255).astype("uint8"), "RGBA")


def build_tile_pulsed(bits, t):
    back, front = cw.build_layers(bits)
    fence_back, fence_front = cw.fence_parts(bits)
    out = Image.new("RGBA", (TILE, TILE), (0, 0, 0, 0))
    for img, pos in back:
        out.alpha_composite(img, pos)
    if fence_back is not None:
        out.alpha_composite(recolor(fence_back, t), (0, 0))
    out.alpha_composite(cw.brik(bits))
    for img, pos in front:
        out.alpha_composite(img, pos)
    if fence_front is not None:
        out.alpha_composite(recolor(fence_front[0], t), fence_front[1])
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


frames = []
for offs in [(0, 0, 0), (16, 0, 0), (32, 16, 16)]:  # idle / scratched / damaged
    cw.BRIK_OFF, cw.SBAG_OFF, cw.CYCL_OFF = offs
    for t in PULSE:
        for bits in range(16):
            frames.append(build_tile_pulsed(bits, t))
cw.BRIK_OFF = cw.SBAG_OFF = cw.CYCL_OFF = 0
save_sheet(frames, f"{BITS_DIR}/aot-wall-gdi-fs-pulse.png")
print("done", len(frames), "total frames")
