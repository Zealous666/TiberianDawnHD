# Gemini-Polish: Bodenplatte + Säulen (vertikales Laser Gate)

**Upload:** `gemini-input-plate-posts-INTAKT@2x.png` (bzw. `-DAMAGED@2x.png`) — Platte + 2 Säulen,
KEIN Laser, KEIN Smudge, transparent. Ich re-integriere Laser + Smudge danach selbst.

**Ziel:** gleiche Geometrie, höhere Qualität, **Säulen-Schatten ergänzen** (die Säulen wurden
freigestellt und haben keinen Bodenschatten mehr).

## Prompt (INTAKT)

> Here is a top-down isometric (2:1 dimetric) game sprite on a transparent background: a tall
> vertical **concrete base plate** (a parallelogram running top-to-bottom) with **two dark
> metallic laser-emitter posts** — one near the top, one near the bottom — each with a glowing
> red emitter node.
>
> **Repaint it at higher quality, but keep the geometry EXACTLY identical:**
> - Redraw the concrete plate with cleaner, more realistic weathered-concrete material,
>   sharper detail and consistent lighting from the **upper-left**. Keep its **exact silhouette,
>   size and isometric orientation**, and keep the **vertical central channel/groove** down the
>   middle clear (a laser beam runs there later).
> - Keep **both posts in EXACTLY the same position, size and orientation** — do not move, resize
>   or rotate them. Keep the glowing red emitters.
> - **ADD a soft dark cast shadow** on the plate beneath and to the lower-right of each post
>   (isometric light from the upper-left).
> - No other elements: **no laser beam, no dirt/smudge, no text**. Keep the canvas and
>   proportions identical. **Fully transparent background.**

## Prompt (DAMAGED)

Gleicher Prompt, aber ergänze:
> This plate is **battle-damaged**: keep the existing cracks, the large impact crater and the
> scorch marks, but render them more realistically. Same rules for the two posts and their new
> cast shadows.

## Wichtig / danach
- Gemini backt Transparenz oft als Schachbrett/opak ein → ich re-keye das nachher (Standard-
  Workflow flatten-gemini-frame / gemini-building-import).
- Falls Gemini die Säulen doch leicht verschiebt: ich re-detektiere die Emitter-Positionen aus
  dem Ergebnis und richte den Laser daran aus.
- Canvas/Seitenverhältnis der Ausgabe **nicht** ändern (sonst passt die 1:1-Reintegration nicht).
