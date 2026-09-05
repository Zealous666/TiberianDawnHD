# GDI-Gate → vertikal (EIN Prompt, ganzes Sheet, NEUE Session)

**Immer vom sauberen Original starten — nie eine ChatGPT-Ausgabe nachbearbeiten** (sonst
sackt die Qualität mit jeder Runde ab). Neue Session, beide Bilder hochladen:
1. `input-gdi-batch.png` — alle 12 Frames (0–10 Öffnungsanim + dmg) in einem Raster
2. `target-orientation-gdi.png` — Ziel-Ausrichtung (vertikale Wand #1, mit roten Parallel-Linien)

**Prompt:**

> I need you to re-orient a game sprite sheet in a single pass, keeping strict isometric
> projection. Attached:
> **(1)** `input-gdi-batch.png` — a 4×3 grid of 12 top-down isometric sprite frames of a **GDI
> concrete sliding gate**. Frames 0–10 show the gate panel progressively lowering into the
> foundation (0 = fully closed/up, 10 = fully open/gone into the slab); the last cell "dmg" is
> a damaged version. Transparent background. In this input every gate runs **left→right across
> the screen** (horizontal wall orientation).
> **(2)** `target-orientation-gdi.png` — right cell shows the **target orientation**: the same
> structure as a **vertical concrete strip** running straight up-and-down the screen (the red
> lines mark its two parallel edges).
>
> **Task:** output the **same 4×3 grid** (same cell positions, same frame count, order and
> numbering), with **every** gate redrawn so its long axis runs **straight vertically down the
> screen**.
>
> **PROJECTION — this is where you keep failing, read carefully:** use **orthographic /
> parallel isometric projection, exactly like the input and the reference. NO perspective, NO
> vanishing point, NO camera foreshortening.**
> - The gate band must be a **vertical strip of CONSTANT WIDTH** — the top (far) end is the
>   **same width** as the bottom (near) end. Do **not** taper it.
> - The concrete **foundation stays a PARALLELOGRAM** (opposite edges parallel), the same flat
>   ground tile as in the input. It must **NOT** become a **trapezoid that narrows toward the
>   top**, and it must not recede to a vanishing point.
> - It is a **low, flat barrier lying on the ground** at the **same low height** as the
>   horizontal input — **NOT a tall upright monument/tower standing up**. Keep the same
>   bird's-eye isometric camera angle as the input.
> - Both end posts lie on the **same vertical center line** of the strip (near post lower, far
>   post upper); the lattice panel between them is that constant-width vertical strip.
>
> **Per frame:** each cell keeps its own opening state (0–10); "dmg" keeps its cracks/scorch/
> rubble. All 12 cells **mutually consistent** — identical foundation, posts, smudge, scale and
> position, differing only by opening state.
>
> **Style & quality:** keep the exact concrete material, grey/tan palette, lattice pattern and
> upper-left lighting from input (1). **Render at full sharpness — do not blur, soften or
> downscale.** No color remap, no house color, no text, no extra props. **Fully transparent
> background.**

Falls eine Zelle noch schräg/perspektivisch wird: EINE Korrektur *"cell N still has
perspective — make the foundation a parallelogram (parallel edges, not a trapezoid) and the
band a constant-width vertical strip"* — greift das nicht, **neuer Thread** statt weiter
nachtreten.

## Zurück in den Mod
11 Öffnungs-Frames ausschneiden, zu **einem horizontalen Sheet** (380×252/Frame) mit `tEXt`-
Chunks `FrameSize=380,252` / `FrameAmount=11`; "dmg" separat als `…-damaged.png`. Transparenz
auf farbigem Hintergrund prüfen. Körperfarbe nicht im Bild ändern.
