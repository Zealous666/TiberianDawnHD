#!/usr/bin/env python3
"""
Prueft das 50%-Schadensmodell aller Gebaeude.

Geht NICHT von den vorhandenen "damaged-*"-Sequenzen aus (so entgehen einem alle
Faelle, in denen gar keine existiert oder sie falsch heisst), sondern vom AKTOR:
welche Sequenz rendert jedes WithSpriteBody/WithSpriteTurret tatsaechlich, und
gibt es dazu ein Schadensbild?

Drei Fehlerklassen:
  FEHLT     - "damaged-<seq>" existiert nicht. Engine faellt still auf das
              unbeschaedigte Sprite zurueck. (So war SAM kaputt: das Schadensbild
              hiess "ra-damaged-turret" statt "damaged-ra-turret".)
  ATTRAPPE  - "damaged-<seq>" existiert, zeigt aber exakt dieselben Pixel wie das
              Original (gleiche Filename/Start/Length ...). Sieht in jedem
              Namens-Check gesund aus, ist in-game aber wirkungslos.
  OK        - echtes Schadensbild.

Aufruf:  python3 mods/management/check-damage-sprites.py [--age0] [--all]
         --age0  nur Sprites, die im Grundzustand (ohne Age-/Upgrade-Flags) sichtbar sind
         --all   auch Overlays (Lichter, Rotoren) und Nicht-Gebaeude

WICHTIG: Pfadreihenfolge engine/mods VOR mods -- sonst validiert man den Vanilla-Mod.
"""
import os, re, sys, itertools

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..')
os.chdir(ROOT)

RULES = ['engine/mods/cnc/rules/' + f for f in [
    'misc.yaml', 'ai.yaml', 'player.yaml', 'world.yaml', 'palettes.yaml', 'defaults.yaml',
    'structures.yaml', 'infantry.yaml', 'vehicles.yaml', 'trees.yaml', 'civilian.yaml',
    'civilian-desert.yaml', 'tech.yaml', 'ships.yaml', 'aircraft.yaml', 'husks.yaml',
    'map-generators.yaml']] + \
    ['mods/cnc/rules/' + f for f in [
    'overrides.yaml', 'aot-balance.yaml', 'aot-factions.yaml', 'aot-structures.yaml',
    'aot-units.yaml', 'aot-critters.yaml', 'aot-world.yaml', 'aot-ai.yaml',
    'aot-snow-decor.yaml', 'aot-ts-civilian.yaml', 'aot-lighting.yaml']]

SEQF = ['engine/mods/cnc/sequences/structures.yaml',
        'mods/cnc/sequences/structures-overrides.yaml',
        'mods/cnc/sequences/aot-sequences.yaml']

# Felder, die bestimmen WELCHE PIXEL gezeigt werden
PIX = ['Filename', 'Start', 'Length', 'Frames', 'RemasteredFilename',
       'RemasteredStart', 'RemasteredLength', 'TilesetFilenames']

# Traits, die einen Sprite rendern -> Default-Sequenz
BODY = {'WithSpriteBody': 'idle', 'WithEmbeddedTurretSpriteBody': 'idle',
        'WithWallSpriteBody': 'idle', 'WithSpriteTurret': 'turret'}


def parse(path):
    out, cur, key = [], None, None
    for raw in open(path, encoding='utf-8', errors='replace'):
        line = raw.rstrip('\n')
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        ind = len(line) - len(line.lstrip('\t'))
        s = line.strip()
        if ind == 0:
            cur = (s[:-1] if s.endswith(':') else s.split(':')[0], [])
            out.append(cur)
            key = None
        elif ind == 1 and cur is not None:
            k, _, v = s.partition(':')
            key = (k.strip(), v.strip(), {})
            cur[1].append(key)
        elif ind >= 2 and key is not None:
            k, _, v = s.partition(':')
            if k.strip():
                key[2][k.strip()] = v.strip()
    return out


actors = {}
for p in RULES:
    if not os.path.exists(p):
        print(f"WARNUNG: {p} fehlt", file=sys.stderr)
        continue
    for name, keys in parse(p):
        d = actors.setdefault(name, {})
        for k, v, f in keys:
            if k.startswith('-'):
                d.pop(k[1:], None)
                continue
            if k in d:
                d[k][1].update(f)
                if v:
                    d[k] = (v, d[k][1])
            else:
                d[k] = (v, dict(f))


def resolve(name, seen=None):
    seen = seen or set()
    if name in seen or name not in actors:
        return {}
    seen.add(name)
    own, res = actors[name], {}
    for k, (v, f) in own.items():
        if k.startswith('Inherits'):
            for pk, pv in resolve(v, set(seen)).items():
                res[pk] = (pv[0], {**pv[1], **res[pk][1]}) if pk in res else (pv[0], dict(pv[1]))
    for k, (v, f) in own.items():
        if k.startswith('Inherits'):
            continue
        res[k] = (v or res[k][0], {**res[k][1], **f}) if k in res else (v, dict(f))
    return res


seqs = {}
for p in SEQF:
    if not os.path.exists(p):
        print(f"WARNUNG: {p} fehlt", file=sys.stderr)
        continue
    for img, keys in parse(p):
        d = seqs.setdefault(img, {})
        for k, v, f in keys:
            d.setdefault(k, {}).update(f)


def pix(img, s):
    d = dict(seqs.get(img, {}).get('Defaults', {}))
    d.update(seqs.get(img, {}).get(s, {}))
    return {k: d.get(k) for k in PIX}


def age0_active(cond):
    """Ist der Trait im Grundzustand sichtbar (alle Age-/Upgrade-Flags false)?"""
    if not cond.strip():
        return True
    toks = sorted(set(re.findall(r'[A-Za-z_][A-Za-z0-9_.-]*', cond)), key=len, reverse=True)
    facs = [t for t in toks if 'faction' in t]
    for combo in itertools.product([False, True], repeat=len(facs)):
        env = {t: False for t in toks}
        env.update(dict(zip(facs, combo)))
        expr = cond
        for t in toks:
            expr = re.sub(r'(?<![A-Za-z0-9_.-])' + re.escape(t) + r'(?![A-Za-z0-9_.-])',
                          str(env[t]), expr)
        expr = expr.replace('&&', ' and ').replace('||', ' or ').replace('!', ' not ')
        try:
            if eval(expr):
                return True
        except Exception:
            return True
    return False


age0_only = '--age0' in sys.argv
show_all = '--all' in sys.argv

rows = []
for name in sorted(actors):
    r = resolve(name)
    if not show_all:
        if not any(k.split('@')[0] == 'Building' for k in r):
            continue
        if '.Husk' in name:
            continue
    img = next((f['Image'] for k, (v, f) in r.items()
                if k.split('@')[0] == 'RenderSprites' and f.get('Image')), name.lower())
    if img not in seqs:
        continue
    for k, (v, f) in sorted(r.items()):
        b = k.split('@')[0]
        isremap = b == 'WithIdleOverlay' and 'remap' in (f.get('Sequence') or '')
        if b not in BODY and not (isremap or show_all):
            continue
        cond = f.get('RequiresCondition', '')
        if age0_only and not age0_active(cond):
            continue
        s = f.get('Sequence') or BODY.get(b, 'idle')
        if s not in seqs[img]:
            continue
        d = 'damaged-' + s
        st = 'FEHLT' if d not in seqs[img] else ('ATTRAPPE' if pix(img, s) == pix(img, d) else 'OK')
        if st == 'OK':
            continue
        rows.append((name, k, s, st, cond or '(immer aktiv)'))

scope = 'AGE-0-GRUNDZUSTAND' if age0_only else 'ALLE ZUSTAENDE'
print(f"{scope} - Sprites ohne wirksames Schadensbild\n")
print(f"{'AKTOR':<26}{'TRAIT':<30}{'SEQUENCE':<28}STATUS")
print('=' * 104)
cur = None
for n, k, s, st, c in rows:
    if n != cur:
        print()
        cur = n
    print(f"{'X' if st == 'FEHLT' else '!'} {n:<24}{k:<30}{s:<28}{st}")

nf = sum(1 for r in rows if r[3] == 'FEHLT')
na = sum(1 for r in rows if r[3] == 'ATTRAPPE')
print(f"\n\nFEHLT: {nf}   ATTRAPPE: {na}   betroffene Aktoren: {len({r[0] for r in rows})}")
