#!/usr/bin/env python3
"""
Nod Avatar: dunklere Palette (Chrome-Highlights crushen) + Face-Glow auf
House-Color-Index (16-31) ummappen fuer Player-Color.
"""
from PIL import Image

SRC_PAL = "unittem.pal"
FRAME_COUNT = 168

FACE_BRIGHT_IDX = 9   # (85,255,255) -> bright team-color ramp slot
FACE_DARK_IDX = 2     # (0,170,170)  -> darker team-color ramp slot
FACE_BRIGHT_TARGET = 17
FACE_DARK_TARGET = 23


def darken(r, g, b):
    lum = 0.299 * r + 0.587 * g + 0.114 * b
    if lum <= 0:
        return r, g, b
    t = lum / 255.0
    base = t * 0.5
    if t > 0.5:
        excess = (t - 0.5) / 0.5
        suppression = excess ** 1.4
        base = base * (1 - suppression * 0.92)
    newlum = max(0.0, min(255.0, base * 255.0))
    factor = newlum / lum
    return (
        min(255, round(r * factor)),
        min(255, round(g * factor)),
        min(255, round(b * factor)),
    )


def build_palette():
    raw = open(SRC_PAL, "rb").read()
    assert len(raw) == 768
    src = [v * 255 // 63 for v in raw]  # 6-bit VGA -> 8-bit
    out6 = bytearray(768)
    for i in range(256):
        r, g, b = src[i * 3], src[i * 3 + 1], src[i * 3 + 2]
        if i == 0:
            nr, ng, nb = 0, 0, 0
        else:
            nr, ng, nb = darken(r, g, b)
        out6[i * 3] = round(nr * 63 / 255)
        out6[i * 3 + 1] = round(ng * 63 / 255)
        out6[i * 3 + 2] = round(nb * 63 / 255)
    with open("aot-avatar-base.pal", "wb") as f:
        f.write(bytes(out6))
    print("aot-avatar-base.pal written")


def process_frames():
    for i in range(FRAME_COUNT):
        im = Image.open(f"defender-{i:04d}.png")
        assert im.mode == "P"
        pal = im.getpalette()
        w, h = im.size
        data = bytearray(im.tobytes())
        for p in range(len(data)):
            if data[p] == FACE_BRIGHT_IDX:
                data[p] = FACE_BRIGHT_TARGET
            elif data[p] == FACE_DARK_IDX:
                data[p] = FACE_DARK_TARGET
        out = Image.frombytes("P", (w, h), bytes(data))
        out.putpalette(pal)
        out.save(f"avatarbody-{i:04d}.png")
    print(f"{FRAME_COUNT} frames processed -> avatarbody-*.png")


if __name__ == "__main__":
    build_palette()
    process_frames()
