# NOD-Gate → vertikal (EIN Prompt, ganzes Sheet auf einmal)

**Hochladen (2 Bilder):**
1. `input-nod-batch.png` — alle 6 Frames (idle 0/1, dmg 0/1, shimmer 0/1) in einem Raster
2. `target-orientation-nod.png` — die Ziel-Ausrichtung (rechte Zelle „Wand #1", „\")

Das NOD-„Tor" ist eine **Laser-Barriere**: Betonplatte + 2 Laser-Säulen + 3 rote Laserlinien.

**Prompt:**

> Attached: (1) a grid of 6 top-down isometric (2:1 dimetric) sprite frames of a **Nod laser
> gate** — a concrete base plate with two laser emitter posts (left and right) and three red
> laser beams between them; transparent background. The frames are: idle-0 (closed, beams
> ON), idle-1 (open, beams OFF), two damaged variants, two shimmer variants (brighter glow).
> Every frame currently runs left→right across the screen (horizontal wall orientation). (2)
> A reference image; its right cell "Wand #1" shows the **target orientation** — long axis
> running **vertically down the screen** (top → bottom), rotated 90° on the ground plane onto
> the other diagonal ("\").
>
> Redraw **EVERY frame in grid (1)** into that vertical orientation, keeping the **exact same
> grid layout, cell positions, labels and frame count**.
>
> **PROJECTION (critical):** use **orthographic / parallel isometric projection like the input
> and reference — NO perspective, NO vanishing point, NO foreshortening.**
> - The barrier (base plate + the three laser beams) runs as a **vertical strip of CONSTANT
>   WIDTH**: the top (far) end is the **same width** as the bottom (near) end — do not taper.
> - The concrete base plate **stays a PARALLELOGRAM** (parallel edges), the same flat ground
>   tile as the input — it must **NOT** become a **trapezoid narrowing toward the top**.
> - It is a **low, flat barrier lying on the ground** at the **same low height** as the input,
>   **NOT a tall upright tower**. Same bird's-eye isometric camera angle as the input.
> - Both posts lie on the **same vertical center line** (near post lower, far post upper); the
>   three laser beams run straight up-and-down between them. Keep the posts upright. Keep each frame's individual state unchanged (beams on/off, damage,
> glow). Keep all cells **mutually consistent** — identical plate, posts, smudge, scale and
> position. Preserve the dark metallic posts, the red beams with orange glow and upper-left
> lighting. **No color remap, no house color, no text.** Fully transparent background.

## Zurück in den Mod
Je Sequenz die 2 Frames zu **einem horizontalen Sheet** (721×560 je Frame) mit `tEXt`-Chunks
`FrameSize=721,560` / `FrameAmount=2`. Transparenz auf farbigem Hintergrund prüfen. Laserfarbe
nicht im Bild ändern — Recolor läuft ingame über `WithGateSpriteBody: Palette: effect`.
