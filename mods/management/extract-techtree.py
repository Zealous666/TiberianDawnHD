#!/usr/bin/env python3
"""
extract-techtree.py
Liest alle YAML-Quelldateien und schreibt techtree-data.json.
Ausführen: python3 mods/management/extract-techtree.py (vom TiberianDawnHD-Root)
"""

import re, json
from pathlib import Path
from datetime import datetime, timezone

ROOT = Path(__file__).parent.parent.parent
BITS = ROOT / "mods/cnc/bits"

FTL_FILES = [
    ROOT / "engine/mods/cnc/fluent/rules.ftl",
    ROOT / "mods/cnc/fluent/aot-rules.ftl",
]

def load_ftl_names():
    names = {}
    for ftl in FTL_FILES:
        if not ftl.exists():
            continue
        cur_key = None
        for line in open(ftl, encoding='utf-8', errors='replace'):
            m = re.match(r'^(actor-[\w\-]+)\s*=', line)
            if m:
                cur_key = m.group(1)
                continue
            if cur_key:
                n = re.match(r'^\s+\.name\s*=\s*(.+)', line)
                if n:
                    names[cur_key] = n.group(1).strip()
                    cur_key = None
                elif line.strip() and not line.startswith(' '):
                    cur_key = None
    return names

UNIT_FILES = {
    "aot-units.yaml":         ROOT / "mods/cnc/rules/aot-units.yaml",
    "aot-structures.yaml":    ROOT / "mods/cnc/rules/aot-structures.yaml",
    "overrides.yaml":         ROOT / "mods/cnc/rules/overrides.yaml",
    "engine/vehicles.yaml":   ROOT / "engine/mods/cnc/rules/vehicles.yaml",
    "engine/infantry.yaml":   ROOT / "engine/mods/cnc/rules/infantry.yaml",
    "engine/structures.yaml": ROOT / "engine/mods/cnc/rules/structures.yaml",
    "engine/aircraft.yaml":   ROOT / "engine/mods/cnc/rules/aircraft.yaml",
}
WEAPONS_FILE   = ROOT / "mods/cnc/weapons/aot-weapons.yaml"
SEQUENCES_FILE  = ROOT / "mods/cnc/sequences/aot-sequences.yaml"
SEQUENCE_OVERRIDES = [
    # Engine base sequences first (lowest priority)
    ROOT / "engine/mods/cnc/sequences/structures.yaml",
    ROOT / "engine/mods/cnc/sequences/vehicles.yaml",
    ROOT / "engine/mods/cnc/sequences/aircraft.yaml",
    ROOT / "engine/mods/cnc/sequences/infantry.yaml",
    ROOT / "engine/mods/cnc/sequences/misc.yaml",
    # Mod overrides (higher priority)
    ROOT / "mods/cnc/sequences/structures-overrides.yaml",
    ROOT / "mods/cnc/sequences/vehicles-overrides.yaml",
    ROOT / "mods/cnc/sequences/aircraft-overrides.yaml",
    ROOT / "mods/cnc/sequences/infantry-overrides.yaml",
    ROOT / "mods/cnc/sequences/misc-overrides.yaml",
]

MOD_KEYS = {'aot-units.yaml', 'aot-structures.yaml', 'overrides.yaml'}

# ── YAML parser ──────────────────────────────────────────────────────────────

def parse_blocks(path):
    actors = []
    current_id = None
    current_data = {}
    in_buildable = in_health = in_valued = in_power = in_tooltip = in_render = False

    for line in open(path, encoding='utf-8', errors='replace'):
        depth = len(line) - len(line.lstrip('\t'))
        strip = line.strip()
        if not strip or strip.startswith('#'):
            continue
        if depth == 0 and re.match(r'^[A-Za-z][A-Za-z0-9_\-]*:\s*$', line):
            if current_id:
                actors.append((current_id, current_data))
            current_id = strip.rstrip(':')
            current_data = {'_file': path.name}
            in_buildable = in_health = in_valued = in_power = in_tooltip = in_render = False
            continue
        if current_id is None:
            continue
        if depth == 1:
            in_buildable = strip.startswith('Buildable:')
            in_health    = strip == 'Health:'
            in_valued    = strip == 'Valued:'
            in_power     = strip.startswith('Power')
            in_tooltip   = strip == 'Tooltip:'
            in_render    = strip.startswith('RenderSprites:')
            in_provides  = strip.startswith('ProvidesPrerequisite')
            in_preview   = strip.startswith('SequencePlaceBuildingPreview:')
            if strip == '-Buildable:':
                current_data['_removed'] = True
            if strip.startswith('Inherits'):
                val = strip.split(':', 1)[1].strip()
                current_data.setdefault('_inherits', []).append(val)
        if depth == 2:
            if in_buildable:
                for k in ('Queue','Prerequisites','BuildPaletteOrder','BuildLimit','Icon'):
                    if strip.startswith(k + ':'):
                        current_data[k] = strip.split(':',1)[1].strip()
            if in_health and strip.startswith('HP:'):
                current_data['HP'] = strip.split(':',1)[1].strip()
            if in_valued and strip.startswith('Cost:'):
                current_data['Cost'] = strip.split(':',1)[1].strip()
            if in_power and strip.startswith('Amount:'):
                current_data['Power'] = strip.split(':',1)[1].strip()
            if in_tooltip and strip.startswith('Name:'):
                current_data['Tooltip'] = strip.split(':',1)[1].strip()
            if in_render and strip.startswith('Image:'):
                current_data.setdefault('RenderImage', strip.split(':',1)[1].strip())
            if in_provides and strip.startswith('Prerequisite:'):
                current_data.setdefault('_provides', []).append(strip.split(':',1)[1].strip())
            if in_preview and strip.startswith('Sequence:'):
                current_data['PreviewSequence'] = strip.split(':',1)[1].strip()
    if current_id:
        actors.append((current_id, current_data))
    return actors

def load_all_actors():
    merged = {}
    for file_key, path in UNIT_FILES.items():
        if not path.exists():
            continue
        is_mod = file_key in MOD_KEYS
        for actor_id, data in parse_blocks(path):
            data['_filekey'] = file_key
            if actor_id not in merged:
                merged[actor_id] = dict(data)
                if is_mod:
                    merged[actor_id]['_in_mod'] = True
            else:
                ex = merged[actor_id]
                if is_mod:
                    ex['_in_mod'] = True
                for k, v in data.items():
                    if k == '_removed':
                        ex[k] = True
                    elif k == '_inherits':
                        ex.setdefault('_inherits', []).extend(v)
                    elif k == '_provides':
                        ex.setdefault('_provides', []).extend(v)
                    elif k not in ex or is_mod:
                        ex[k] = v
                if is_mod and 'Queue' in data:
                    ex['Queue'] = data['Queue']
    return merged

def resolve_inheritance(all_actors):
    """Copy HP/Cost/Power/Tooltip from parent actors if child doesn't have them."""
    INHERIT_KEYS = ('HP', 'Cost', 'Power', 'Tooltip', 'RenderImage', 'Icon')
    changed = True
    passes = 0
    while changed and passes < 10:
        changed = False
        passes += 1
        for aid, data in all_actors.items():
            for parent_ref in data.get('_inherits', []):
                parent_id = parent_ref.lstrip('^')
                parent = all_actors.get(parent_id)
                if not parent:
                    continue
                for k in INHERIT_KEYS:
                    if k not in data and k in parent:
                        data[k] = parent[k]
                        changed = True

def filter_buildable(all_actors):
    result = {}
    for aid, data in all_actors.items():
        if 'Queue' not in data:
            continue
        # Only include actors defined or overridden in mod files
        if not data.get('_in_mod'):
            continue
        # Skip actors where Buildable was explicitly removed (-Buildable:)
        if data.get('_removed'):
            continue
        result[aid] = data
    return result

def _parse_seq_file(path, seqs):
    cur_img = cur_seq = None
    for line in open(path, encoding='utf-8', errors='replace'):
        depth = len(line) - len(line.lstrip('\t'))
        strip = line.strip()
        if not strip or strip.startswith('#'):
            continue
        if depth == 0:
            cur_img = strip.rstrip(':')
            seqs.setdefault(cur_img, {})
            cur_seq = None
        elif depth == 1 and cur_img:
            cur_seq = strip.rstrip(':')
            seqs[cur_img].setdefault(cur_seq, {})
        elif depth == 2 and cur_img and cur_seq:
            if strip.startswith('Filename:'):
                v = strip.split(':',1)[1].strip()
                if v:
                    seqs[cur_img][cur_seq]['fn'] = v
            if strip.startswith('RemasteredFilename:'):
                v = strip.split(':',1)[1].strip()
                if v:
                    seqs[cur_img][cur_seq]['rfn'] = v
            if strip.startswith('Length:'):
                v = strip.split(':',1)[1].strip()
                try:
                    seqs[cur_img][cur_seq]['length'] = int(v)
                except ValueError:
                    pass

def load_sequences():
    seqs = {}
    for path in [SEQUENCES_FILE] + SEQUENCE_OVERRIDES:
        if path.exists():
            _parse_seq_file(path, seqs)
    return seqs

def detect_sprite(data, seqs):
    img = data.get('RenderImage','')
    if not img:
        return 'td'
    has_ra = False
    for seq in seqs.get(img, {}).values():
        fn  = seq.get('fn','').lower()
        rfn = seq.get('rfn','')
        rfn_up = rfn.upper()
        # TS: raw VXL/HVA files
        if '.vxl' in fn or '.hva' in fn:
            return 'ts'
        # TS: baked local ZIP/PNG (not a DATA\ remaster path = custom VXL render)
        if rfn and not rfn.startswith('DATA\\') and (rfn.lower().endswith('.zip') or rfn.lower().endswith('.png')):
            # Exclude known standard RA/TD icon overrides (small single-frame PNGs)
            if not rfn.lower().endswith('_base.png') and not rfn.lower().endswith('_icon.png'):
                return 'ts'
        # RA: remaster path from RA assets
        if 'RED_ALERT' in rfn_up:
            has_ra = True
        # RA: classic SHP names from RA
        for ra_name in ('hind','lrotorlg','lrotor','siege','dome','stek','v2rl'):
            if ra_name in fn:
                has_ra = True
    return 'ra' if has_ra else 'td'

ICON_MAP = {
    'aot-nuke-age0':'nuke_base.png','aot-nuke-age1':'nuke_ages.png',
    'aot-nuke-turbine':'nuke_turbine.png','aot-nuk2-age0':'nuk2_base.png',
    'aot-nuk2-age1':'nuk2_ages.png','aot-pyle-age0':'pyle_base.png',
    'aot-pyle-age1':'pyle_ages.png','aot-hand-age0':'hand_base.png',
    'aot-hand-age1':'hand_ages.png','aot-fix-age0':'fix_base.png',
    'aot-fix-age1':'fix_ages.png','aot-hq-base':'hq_base.png',
    'aot-hq-ion':'hq_ion.png','aot-hpad-gdi':'helo_base.png',
    'aot-hpad-nod':'helo_base.png','aot-htnk-base':'htnk_base.png',
    'aot-htnk-laser':'htnk_base.png','aot-htnk-rocket':'htnk_base.png',
    'aot-mtnk-base':'mtnk_1_base.png','aot-mtnk-pred':'mtnk_1_base.png',
    'aot-mtnk-rail':'mtnk_1_base.png','aot-mcv-age0':'mcv_base.png',
    'aot-mcv-age1':'mcv_ages.png','aot-jeep-base':'jeep_1_base.png',
    'aot-jeep-humvee':'jeep_1_base.png','aot-ltnk-base':'ltnk_base.png',
    'aot-ltnk-laser':'ltnk_1_base.png','aot-ltnk-tick':'ltnk_base.png',
    'aot-ttnk-base':'ttnk_base.png','aot-ttnk-flame':'ttnk_base.png',
    'aot-ttnk-toxin':'ttnk_base.png','aot-ttnk-cloak':'ttnk_base.png',
    'aot-harv':'harv_base.png','aot-oret-lite':'oret_base.png',
    'aot-hind-base':'hind_base.png','aot-harpy-upgraded':'hind_hrpy.png',
    'aot-sub-base':'sub_base.png','aot-sub-cargo-proxy':'sub_cargo.png',
    'aot-gunboat-base':'crus_base.png','aot-msam-hover-aa':'msam_base.png',
    'aot-msam-hover-mlrs':'msam_base.png',
    'TRUCK':'truk_base.png','PROC':'proc_base.png','WEAP':'weap_base.png',
    'AFLD':'afld_icon.png','AJET':'ajet_icon.png','ATEC':'atec_base.png',
    'STEC':'stec_base.png','GUN':'gun_base.png',
}

def detect_idle(data, seqs, icon_file=None):
    """Find world-sprite PNG. Returns (filename, frame_count) or (None, 1)."""
    render_img = data.get('RenderImage', '')
    if not render_img:
        return None, 1
    img_seqs = seqs.get(render_img, {})

    def valid_png(fn):
        return fn and fn.lower().endswith('.png') and (BITS / fn).exists()

    def seq_info(sd):
        return sd.get('fn', ''), sd.get('length', 1)

    # 1. SequencePlaceBuildingPreview — OpenRA's own placement preview sequence
    preview_seq = data.get('PreviewSequence', '')
    if preview_seq and preview_seq in img_seqs:
        fn, length = seq_info(img_seqs[preview_seq])
        if valid_png(fn):
            return fn, length

    # 2. Standard idle/body names
    for seq_name in ('idle', 'body', 'damaged-idle'):
        sd = img_seqs.get(seq_name, {})
        fn, length = seq_info(sd)
        if valid_png(fn):
            return fn, length

    # 3. Non-icon -idle/-body sequences, single-frame preferred
    candidates = []
    for seq_name, sd in img_seqs.items():
        if 'icon' in seq_name.lower():
            continue
        if not (seq_name.endswith('-idle') or seq_name.endswith('-body')):
            continue
        fn, length = seq_info(sd)
        if valid_png(fn):
            candidates.append((length, seq_name, fn, length))
    if candidates:
        candidates.sort()
        _, _, fn, length = candidates[0]
        return fn, length

    # 4. Any non-icon non-bib PNG, single-frame preferred
    candidates2 = []
    for seq_name, sd in img_seqs.items():
        if 'icon' in seq_name.lower() or seq_name == 'bib':
            continue
        fn, length = seq_info(sd)
        if valid_png(fn):
            candidates2.append((length, fn, length))
    if candidates2:
        candidates2.sort()
        _, fn, length = candidates2[0]
        return fn, length

    # 5. Fallback: icon sequences — actor's own icon first
    if icon_file and valid_png(icon_file):
        return icon_file, 1

    seen = set()
    for sd in img_seqs.values():
        fn, length = seq_info(sd)
        if not valid_png(fn) or fn in seen:
            continue
        seen.add(fn)
        if not fn.lower().endswith('_base.png') and not fn.lower().endswith('_ages.png'):
            return fn, length

    seen2 = set()
    for sd in img_seqs.values():
        fn, length = seq_info(sd)
        if not valid_png(fn) or fn in seen2:
            continue
        seen2.add(fn)
        return fn, length

    return None, 1

def detect_icon(aid, data, seqs):
    """Resolve icon PNG: Sequence lookup → ICON_MAP → stem guessing."""
    render_img = data.get('RenderImage', aid.lower())
    icon_seq   = data.get('Icon', 'icon')  # default sequence name is 'icon'

    # 1. Sequence lookup: seqs[renderImage][iconSeq]['fn']
    fn = seqs.get(render_img, {}).get(icon_seq, {}).get('fn', '')
    if fn and (BITS / fn).exists():
        return True, fn
    # Also try default 'icon' sequence if a custom Icon name didn't match
    if icon_seq != 'icon':
        fn2 = seqs.get(render_img, {}).get('icon', {}).get('fn', '')
        if fn2 and (BITS / fn2).exists():
            return True, fn2

    # 2. ICON_MAP hardcoded fallback
    if aid in ICON_MAP:
        f = ICON_MAP[aid]
        if (BITS / f).exists():
            return True, f

    # 3. Stem-based guessing
    stem = aid.lower().replace('-','_').replace('aot_','')
    for suf in ('_base.png', '_icon.png', '_ages.png', '.png'):
        if (BITS / (stem + suf)).exists():
            return True, stem + suf

    return False, None

def detect_tier(prereqs):
    """Tier = highest aot-ageN referenced (including ~aot-ageN visibility gates).
    Excludes !aot-ageN (negation = available WITHOUT that age)."""
    tier = 0
    for m in re.finditer(r'(?<!!)aot-age(\d)', prereqs):
        tier = max(tier, int(m.group(1)))
    return tier

def resolve_tiers(actors_list):
    """Second pass: if an actor's visibility gate points to a condition provided
    by another actor with a higher tier, upgrade this actor's tier to match."""
    # Build condition → max_tier map
    condition_tier = {}
    for a in actors_list:
        tier = a['tier']
        for cond in a.get('_provides_raw', []):
            condition_tier[cond] = max(condition_tier.get(cond, 0), tier)
    # Apply
    for a in actors_list:
        for vp in a['visibilityPrereqs']:
            cond = vp.lstrip('~')
            if cond in condition_tier:
                a['tier'] = max(a['tier'], condition_tier[cond])

def queue_to_cat(q):
    ql = q.lower()
    if 'upgrade'   in ql: return 'upgrades'
    if 'support'   in ql: return 'defense'
    if 'building'  in ql: return 'main-buildings'
    if 'infantry'  in ql: return 'infantry'
    if 'naval'     in ql: return 'navy'
    if 'aircraft'  in ql: return 'aircraft'
    # LightVehicle + Vehicle → vehicles
    return 'vehicles'

def queue_to_faction(q):
    gdi = bool(re.search(r'\bGDI\b', q))
    nod = bool(re.search(r'\bNod\b', q))
    if gdi and nod: return 'both'
    return 'nod' if nod else 'gdi'

def resolve_name(aid, data, ftl_names):
    raw = data.get('Tooltip', '')
    # FTL reference like "actor-afld.name"
    if raw and raw.endswith('.name'):
        key = raw[:-5]  # strip ".name"
        if key in ftl_names:
            return ftl_names[key]
    if raw and not raw.startswith('actor-'):
        return raw
    # Try direct FTL lookup by actor id
    key = 'actor-' + aid.lower()
    if key in ftl_names:
        return ftl_names[key]
    # Pretty-print fallback
    return aid.replace('aot-','').replace('-',' ').title()

def extract_actors(buildable, seqs, ftl_names):
    out = []
    for aid, data in sorted(buildable.items()):
        queue   = data.get('Queue','')
        prereqs = data.get('Prerequisites','')
        cost    = data.get('Cost','')
        hp      = data.get('HP','')
        power   = data.get('Power','0') or '0'
        name    = resolve_name(aid, data, ftl_names)

        real_p = [p.strip() for p in prereqs.split(',') if p.strip() and not p.strip().startswith('~')]
        vis_p  = [p.strip() for p in prereqs.split(',') if p.strip() and p.strip().startswith('~')]

        has_icon, icon_file = detect_icon(aid, data, seqs)
        idle_file, idle_length = detect_idle(data, seqs, icon_file)
        out.append({
            'id':              aid,
            'displayName':     name,
            'file':            data.get('_filekey',''),
            'queue':           queue,
            'faction':         queue_to_faction(queue),
            'category':        queue_to_cat(queue),
            'tier':            detect_tier(prereqs),
            'bpo':             int(data.get('BuildPaletteOrder','0') or 0),
            'prereqs':         real_p,
            'visibilityPrereqs': vis_p,
            'provides':        data.get('_provides', []),
            '_provides_raw':   data.get('_provides', []),
            'stats': {
                'cost':  int(cost)  if cost  and cost.lstrip('-').isdigit()  else None,
                'hp':    int(hp)    if hp    and hp.lstrip('-').isdigit()    else None,
                'power': int(power) if power and power.lstrip('-').isdigit() else 0,
            },
            'sprite':     detect_sprite(data, seqs),
            'customIcon': has_icon,
            'iconFile':   icon_file,
            'idleFile':   idle_file,
            'idleLength': idle_length,
            'notBuildable': not (cost and cost.lstrip('-').isdigit()),
            'isProxy':    aid.startswith('aot-') and bool(data.get('_inherits')),
        })
    return out

def build_weapon_usage(all_actors):
    """Parse all unit YAMLs for Weapon: entries.
    Returns (weapon→[actorIds], actor→[weaponIds])."""
    usage = {}   # weapon_id → set of actor ids
    actor_weps = {}  # actor_id → set of weapon ids
    files = list(UNIT_FILES.values())
    for path in files:
        if not path.exists():
            continue
        cur_actor = None
        for line in open(path, encoding='utf-8', errors='replace'):
            strip = line.strip()
            if not strip or strip.startswith('#'):
                continue
            depth = len(line) - len(line.lstrip('\t'))
            if depth == 0 and re.match(r'^[A-Za-z][A-Za-z0-9_\-]*:\s*$', line):
                cur_actor = strip.rstrip(':')
            elif cur_actor and depth >= 2 and strip.startswith('Weapon:'):
                wid = strip.split(':', 1)[1].strip()
                if wid:
                    usage.setdefault(wid, set()).add(cur_actor)
                    actor_weps.setdefault(cur_actor, set()).add(wid)
    return (
        {k: sorted(v) for k, v in usage.items()},
        {k: sorted(v) for k, v in actor_weps.items()},
    )

def extract_weapons():
    weapons = []
    if not WEAPONS_FILE.exists():
        return weapons
    cur = None
    data = {}
    in_wh = in_proj = False
    for line in open(WEAPONS_FILE, encoding='utf-8', errors='replace'):
        depth = len(line) - len(line.lstrip('\t'))
        strip = line.strip()
        if not strip or strip.startswith('#'): continue
        if depth == 0 and re.match(r'^[A-Za-z]', line):
            if cur: weapons.append(_mk_weapon(cur, data))
            cur = strip.rstrip(':'); data = {}; in_wh = in_proj = False
            continue
        if cur is None: continue
        if depth == 1:
            in_wh   = strip.startswith('Warhead')
            in_proj = strip.startswith('Projectile')
            for k in ('ReloadDelay','Burst','BurstDelays','Range','Inherits'):
                if strip.startswith(k+':'): data[k] = strip.split(':',1)[1].strip()
        if depth == 2:
            if in_wh and strip.startswith('Damage:'):
                data.setdefault('Damage', strip.split(':',1)[1].strip())
    if cur: weapons.append(_mk_weapon(cur, data))
    return weapons

def _mk_weapon(wid, data):
    nl = wid.lower()
    nod_hints = ('nod','ltnk','hind','harpy','ttnk','sub','hand','obeli','bike','bggy','arty','ftnk','stnk','mlrs')
    gdi_hints = ('gdi','htnk','mtnk','orca','ion','satellite','jeep','humvee','ajet','cruiser','gunboat')
    if any(h in nl for h in nod_hints): faction = 'nod'
    elif any(h in nl for h in gdi_hints): faction = 'gdi'
    else: faction = 'both'
    def pr(r):
        if not r: return None
        m = re.match(r'(\d+)c(\d+)', r)
        if m: return round(int(m.group(1)) + int(m.group(2))/256, 1)
        try: return float(r)
        except: return None
    return {
        'id': wid, 'displayName': wid, 'category': 'weapons', 'faction': faction,
        'inherits': data.get('Inherits',''),
        'usedBy': [],  # filled in after build_weapon_usage
        'stats': {
            'damage': int(data['Damage']) if data.get('Damage','').lstrip('-').isdigit() else None,
            'range':  pr(data.get('Range','')),
            'burst':  int(data['Burst']) if data.get('Burst','').isdigit() else 1,
            'burstDelays': data.get('BurstDelays',''),
            'reloadDelay': int(data['ReloadDelay']) if data.get('ReloadDelay','').isdigit() else None,
        }
    }

if __name__ == '__main__':
    print("=== extract-techtree ===\n")
    ftl_names = load_ftl_names()
    print(f"FTL-Namen: {len(ftl_names)} Einträge")
    seqs = load_sequences()
    print(f"Sequenzen: {len(seqs)} Image-Blöcke")
    all_actors = load_all_actors()
    print(f"Aktoren gesamt: {len(all_actors)}")
    resolve_inheritance(all_actors)
    buildable = filter_buildable(all_actors)
    print(f"Baubar (nach Proxy-Filter): {len(buildable)}")
    actors = extract_actors(buildable, seqs, ftl_names)
    resolve_tiers(actors)
    for a in actors:
        a.pop('_provides_raw', None)
    weapons = extract_weapons()
    # Attach usedBy to weapons and weapons list to actors
    buildable_ids = {a['id'] for a in actors}
    weapon_usage, actor_weapons = build_weapon_usage(all_actors)

    # Propagate weapons through inheritance chains (e.g. aot-htnk-base → HTNK)
    changed = True
    while changed:
        changed = False
        for aid, data in all_actors.items():
            if aid in actor_weapons:
                continue
            for parent_ref in data.get('_inherits', []):
                parent_id = parent_ref.lstrip('^')
                if parent_id in actor_weapons:
                    actor_weapons[aid] = list(actor_weapons[parent_id])
                    changed = True
                    break

    for w in weapons:
        raw_users = weapon_usage.get(w['id'], [])
        # Also include actors that inherit weapons (resolved above)
        inherited_users = [aid for aid, weps in actor_weapons.items() if w['id'] in weps]
        all_users = sorted(set(raw_users) | set(inherited_users))
        w['usedBy'] = [uid for uid in all_users if uid in buildable_ids]
    for a in actors:
        a['weapons'] = actor_weapons.get(a['id'], [])
    from collections import Counter
    print(f"Kategorien: {dict(Counter(a['category'] for a in actors))}")
    print(f"Fraktionen: {dict(Counter(a['faction'] for a in actors))}")
    print(f"Sprites: {dict(Counter(a['sprite'] for a in actors))}")
    print(f"Custom Icons: {sum(1 for a in actors if a['customIcon'])}/{len(actors)}")
    print(f"Waffen: {len(weapons)}")
    out = {'generated': datetime.now(timezone.utc).isoformat(), 'actors': actors, 'weapons': weapons}
    p = Path(__file__).parent / "techtree-data.json"
    json.dump(out, open(p,'w',encoding='utf-8'), indent=2, ensure_ascii=False)
    print(f"\n✅ {p.name} ({len(actors)} Aktoren, {len(weapons)} Waffen)")
