# Gemini: NUR die Bodenplatte drehen (horizontal → vertikal)

Ziel: die **gute horizontale** Bodenplatte auf die vertikale Wandachse drehen. NUR die Platte —
KEINE Säulen, KEIN Laser, KEIN Smudge. Pfosten + Laser + Smudge setze ich (Claude) danach drauf.

**Upload:**
1. `gemini-input-HPLATE@2x.png` — die horizontale Betonplatte (allein, transparent)
2. `target-orientation-nod.png` — Orientierungs-Referenz (rechte Zelle „Wand #1" = vertikale Achse)

**Prompt:**

> Here is a top-down isometric (2:1 dimetric) game sprite of a **concrete base plate** (a wall-gate
> foundation slab), currently oriented **horizontally** — its long axis runs left→right across the
> screen. Transparent background.
>
> Rotate ONLY this plate **90° within the isometric ground plane** so its long axis runs **vertically
> down the screen** (top→bottom) — the other ground diagonal, matching the attached vertical-wall
> reference (its right cell "Wand #1").
>
> Use **orthographic / parallel isometric projection exactly like the input: NO perspective, NO
> vanishing point.** The plate stays a **PARALLELOGRAM of constant width** (do NOT taper it into a
> trapezoid) and **lies flat on the ground** (it is NOT a tall standing slab). Keep the same low
> isometric camera angle as the input.
>
> Keep the **exact same concrete material, weathering, cracks, colour, lighting (upper-left) and
> quality** as the input — only re-oriented. Keep a subtle **vertical central seam/channel** down the
> middle (a laser beam will run there). Output the rotated plate **alone** on a **fully transparent
> background** — no posts, no laser beam, no dirt/smudge, no text.

**Damaged-Variante:** denselben Prompt auf eine beschädigte Platte anwenden ODER ich baue den
Schaden nachträglich rein — je nachdem was besser aussieht. Erst die intakte Platte sauber kriegen.

## Danach (Claude)
- Rotierte Platte auf 3-Zellen-Länge einpassen (Scale 0.1 / 240px-Zelle).
- Fence-Pfosten (aus `laser-frames-export/`) oben/unten drauf, Fence-Laser (Frame 1, gestreckt) als 11-Frame-Fade.
- Smudge (transponiert vom H-Gate) dahinter backen.
- Transparenz von Geminis Ausgabe re-keyen (Schachbrett/opak) — Standard-Workflow.
