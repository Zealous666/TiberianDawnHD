#!/usr/bin/env python3
# Bäckt mmchbarl.vxl als aot-titan-barrel-black.zip + aot-titan-barrel-white.zip.
# 32 Facings, 260×260px Frames (VXL-Render 104px + PAD_X=78/PAD_Y=33 für Höhen-Alignment).
# Barrel-Farbe: schwarz (normal) oder hellgrau (railgun).
# Facing: --facing-flip --facing-offset 45. FORWARD_SHIFT negativ = Richtung Mündung.

import subprocess, tempfile, shutil, os, struct, zipfile, json, math
import numpy as np
from pathlib import Path

PROJ = Path("/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD")
ENGINE = PROJ / "engine"
BITS = PROJ / "mods/cnc/bits"
SRC_DIR = PROJ / "mods/management/asset wip/titan/ts"
VXL = "mmchbarl"

SCALE = 36
N_FACINGS = 32
NEW_SIZE = 260
# Barrel measured at scale=36 → 104px content; place at y=33 (58-13=45px above center)
# User confirmed PAD_Y=20 was slightly too high → +13px lower → PAD_Y=33
PAD_Y = 33
RENDER_W = 104  # actual VXL render size at scale=36

PAD_X = (NEW_SIZE - RENDER_W) // 2   # = 78 (horizontally centered)

# Barrel offsets — derived from actual content axis (PCA), not ClassicFacing angle.
# FORWARD_SHIFT: barrel base moves toward turret center (positive = toward muzzle direction).
# LATERAL_SHIFT: barrel offset to the right arm (positive = right arm side).
# Muzzle reference: frame 8, muzzle is at rightmost x → calibrates muzzle direction.
# Right arm reference: frame 16 (South), right arm appears screen-LEFT → calibrates arm side.
FORWARD_SHIFT = -25  # px: NEGATIV = Richtung zur Mündung (thin end = vorne)
LATERAL_SHIFT = 0    # px: arm offset disabled until forward dir is confirmed correct


def render_barrel(tmp: Path):
    """Render VXL to 32 PNGs in tmp/render/ using two-layer mode (body + remap-sheet)."""
    render_dir = tmp / "render"
    render_dir.mkdir()
    cmd = [
        "dotnet", str(ENGINE / "bin/OpenRA.Utility.dll"), "cnc",
        "--vxl-to-png", str(SRC_DIR / f"{VXL}.vxl"), str(SRC_DIR / f"{VXL}.hva"),
        str(SRC_DIR / "unittem.pal"),
        "--facings", str(N_FACINGS),
        "--scale", str(SCALE),
        "--pitch", "30", "--yaw", "225",
        "--light-yaw", "240", "--light-pitch", "50",
        "--ambient", "0.6", "--diffuse", "0.4",
        "--supersample", "8",
        "--facing-offset", "45", "--facing-flip",
        "--remap-sheet", str(tmp / f"{VXL}-remap.png"),
        "--output-dir", str(render_dir),
    ]
    env = {**os.environ,
           "MOD_SEARCH_PATHS": str(PROJ / "mods"),
           "ENGINE_DIR": ".."}
    result = subprocess.run(cmd, cwd=ENGINE, env=env, capture_output=True, text=True)
    print(result.stdout.strip())
    if result.returncode != 0:
        print(result.stderr)
        raise RuntimeError("VXL render failed")
    return render_dir, tmp / f"{VXL}-remap.png"


def load_png_rgba(path) -> np.ndarray:
    from PIL import Image
    return np.array(Image.open(path).convert("RGBA"), dtype=np.uint8)


def load_remap_sheet(path, n_frames) -> list:
    """Load remap PNG sheet → list of 2D alpha arrays (one per frame)."""
    from PIL import Image
    img = Image.open(path).convert("RGBA")
    arr = np.array(img, dtype=np.uint8)
    COLS = 8
    frames = []
    for fi in range(n_frames):
        col = fi % COLS
        row = fi // COLS
        h = arr.shape[0] // ((n_frames + COLS - 1) // COLS)
        w = arr.shape[1] // COLS
        cell = arr[row*h:(row+1)*h, col*w:(col+1)*w]
        frames.append(cell[:, :, 3])
    return frames


def combined_alpha(body_rgba, remap_alpha) -> np.ndarray:
    return np.maximum(body_rgba[:, :, 3], remap_alpha)


def pca_axis(body_rgba, remap_alpha):
    """Raw PCA major axis (unit vector, direction arbitrary) from alpha mask."""
    alpha = combined_alpha(body_rgba, remap_alpha)
    ys, xs = np.where(alpha > 0)
    if len(xs) < 4:
        return 1.0, 0.0
    cx, cy = float(xs.mean()), float(ys.mean())
    dx, dy = (xs - cx).astype(float), (ys - cy).astype(float)
    cov = np.array([[np.dot(dx, dx), np.dot(dx, dy)],
                    [np.dot(dx, dy), np.dot(dy, dy)]])
    _, vecs = np.linalg.eigh(cov)
    fx, fy = float(vecs[0, 1]), float(vecs[1, 1])
    n = math.hypot(fx, fy) or 1.0
    return fx / n, fy / n


# Per-frame calibrated forward axes, filled once before baking.
_fwd_axes = None   # list of (fx, fy) per frame
_right_sign = None  # +1 or -1 for right-arm lateral direction


def build_axes(bodies, remaps):
    """Compute per-frame forward axes with continuity propagation from frame 8.
    Frame 8 (East): muzzle is at rightmost x → fwd.x must be positive.
    Frame 16 (South): right arm appears screen-LEFT → right.x must be negative."""
    global _fwd_axes, _right_sign
    raw = [pca_axis(b, r) for b, r in zip(bodies, remaps)]

    fwd = [None] * N_FACINGS
    # Seed: frame 8 (East), without facing-flip: muzzle at right → fwd.x must be positive
    fx8, fy8 = raw[8]
    if fx8 < 0:
        fx8, fy8 = -fx8, -fy8  # ensure rightward
    fwd[8] = (fx8, fy8)

    # Propagate around the ring: 9→10→…→31→0→1→…→7
    for step in range(1, N_FACINGS):
        i = (8 + step) % N_FACINGS
        prev = fwd[(8 + step - 1) % N_FACINGS]
        fx, fy = raw[i]
        if fx * prev[0] + fy * prev[1] < 0:  # >90° from previous → flip
            fx, fy = -fx, -fy
        fwd[i] = (fx, fy)

    _fwd_axes = fwd

    # Right-arm sign from frame 16: right = 90° CW from fwd = (fy16, -fx16)
    fx16, fy16 = fwd[16]
    rx16 = fy16  # 90° CW x-component
    # right arm of South-facing should be screen-LEFT (negative x)
    _right_sign = 1.0 if rx16 < 0 else -1.0

    print("  Per-frame forward axes (calibrated):")
    for i in [0, 4, 8, 12, 16, 20, 24, 28]:
        fx, fy = fwd[i]
        rx, ry = fy * _right_sign, -fx * _right_sign
        print(f"    frame{i:2d}: fwd=({fx:+.2f},{fy:+.2f})  right=({rx:+.2f},{ry:+.2f})")


def make_barrel_frame(body_rgba, remap_alpha, color_rgb, facing_idx):
    """Composite barrel pixels offset forward (muzzle dir) + lateral (right arm)."""
    alpha = combined_alpha(body_rgba, remap_alpha)
    render_h, render_w = body_rgba.shape[:2]
    frame_small = np.zeros((render_h, render_w, 4), dtype=np.uint8)
    mask = alpha > 0
    frame_small[mask, 0] = color_rgb[0]
    frame_small[mask, 1] = color_rgb[1]
    frame_small[mask, 2] = color_rgb[2]
    frame_small[mask, 3] = alpha[mask]

    fx, fy = _fwd_axes[facing_idx]
    rx, ry = fy * _right_sign, -fx * _right_sign  # 90° CW from fwd, sign-corrected

    dx = int(round(FORWARD_SHIFT * fx + LATERAL_SHIFT * rx))
    dy = int(round(FORWARD_SHIFT * fy + LATERAL_SHIFT * ry))

    ox = max(0, min(NEW_SIZE - render_w, PAD_X + dx))
    oy = max(0, min(NEW_SIZE - render_h, PAD_Y + dy))

    big = np.zeros((NEW_SIZE, NEW_SIZE, 4), dtype=np.uint8)
    big[oy:oy + render_h, ox:ox + render_w] = frame_small
    return big


def write_tga(rgba: np.ndarray) -> bytes:
    h, w = rgba.shape[:2]
    hdr = bytearray(18)
    hdr[2] = 2
    struct.pack_into('<H', hdr, 12, w)
    struct.pack_into('<H', hdr, 14, h)
    hdr[16] = 32
    hdr[17] = 0x28
    return bytes(hdr) + bytes(rgba[:, :, [2, 1, 0, 3]].flatten())


def build_zip(frames_rgba, out_path, stem):
    meta = json.dumps({"size": [NEW_SIZE, NEW_SIZE], "crop": [0, 0, NEW_SIZE, NEW_SIZE]},
                      separators=(',', ':'))
    with zipfile.ZipFile(out_path, 'w', zipfile.ZIP_DEFLATED) as z:
        for i, f in enumerate(frames_rgba):
            z.writestr(f"{stem}-{i:04d}.tga", write_tga(f))
            z.writestr(f"{stem}-{i:04d}.meta", meta)
    print(f"  → {out_path.name} ({NEW_SIZE}px, {len(frames_rgba)} frames, PAD_Y={PAD_Y})")


def main():
    tmp = Path(tempfile.mkdtemp())
    try:
        print(f"Rendering {VXL}.vxl at scale={SCALE}…")
        render_dir, remap_sheet = render_barrel(tmp)

        bodies_raw = [load_png_rgba(render_dir / f"{VXL}-{i:04d}.png") for i in range(N_FACINGS)]
        remaps_raw = load_remap_sheet(remap_sheet, N_FACINGS)

        bodies = bodies_raw
        remaps = remaps_raw

        build_axes(bodies, remaps)
        black_frames = [make_barrel_frame(b, r, (0, 0, 0), i) for i, (b, r) in enumerate(zip(bodies, remaps))]
        white_frames = [make_barrel_frame(b, r, (200, 200, 210), i) for i, (b, r) in enumerate(zip(bodies, remaps))]

        build_zip(black_frames, BITS / "aot-titan-barrel-black.zip", "titan-barl-blk")
        build_zip(white_frames, BITS / "aot-titan-barrel-white.zip", "titan-barl-wht")

        print("Done.")
    finally:
        shutil.rmtree(tmp)


if __name__ == "__main__":
    main()
