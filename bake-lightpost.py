#!/usr/bin/env python3
"""Legt die Sprites fuer den baubaren Lightpost (aot-lightpost) an.

Der Post soll GENAUSO aussehen wie die Editor-Light-Posts (User 2026-07-28) -- die Sprites sind
daher unveraenderte Kopien von bits/aot-ts-galite*.png (gebacken aus gtlite.shp). Kein
Player-Color-Remap: die Spielerfarbe steckt ausschliesslich im Licht
(TerrainLightSource.UsePlayerColor, siehe mods/cnc/rules/aot-lighting.yaml).

Eigene Dateinamen statt direkter Wiederverwendung von aot-ts-galite, damit der baubare Post
seinen Sprite spaeter unabhaengig vom Editor-Post aendern kann.

Das Icon (bits/aot-lightpost-icon.png) ist die User-Vorlage
"mods/management/asset wip/post/icon/post_base.png" (64x48, passt 1:1) und wird hier nur kopiert.
"""

import os
import shutil

ROOT = os.path.dirname(os.path.abspath(__file__))
BITS = os.path.join(ROOT, 'mods', 'cnc', 'bits')
ICON_SRC = os.path.join(ROOT, 'mods', 'management', 'asset wip', 'post', 'icon', 'post_base.png')

COPIES = [
    ('aot-ts-galite.png', 'aot-lightpost.png'),
    ('aot-ts-galite-damaged.png', 'aot-lightpost-damaged.png'),
]


def main():
    for src, dst in COPIES:
        shutil.copyfile(os.path.join(BITS, src), os.path.join(BITS, dst))
        print(f'{dst} <- {src}')

    shutil.copyfile(ICON_SRC, os.path.join(BITS, 'aot-lightpost-icon.png'))
    print('aot-lightpost-icon.png <- asset wip/post/icon/post_base.png')


if __name__ == '__main__':
    main()
