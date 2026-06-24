#!/usr/bin/env python3
"""
sync-buildorder.py
Liest alle YAML-Quelldateien und synchronisiert buildorder.html:
  - data-orig="X" → aktueller BuildPaletteOrder-Wert
  - value="X"     → selber Wert (damit HTML ohne JS korrekt ist)
  - Entfernt Aktoren aus der HTML, die in YAML mit -Buildable: deaktiviert wurden
Meldet außerdem Aktoren, die in der HTML stehen aber in keiner YAML gefunden wurden.

Ausführen: python3 mods/management/sync-buildorder.py
(vom TiberianDawnHD-Projektordner aus)
"""

import re, sys
from pathlib import Path

ROOT = Path(__file__).parent.parent.parent  # TiberianDawnHD/
HTML = Path(__file__).parent / "buildorder.html"

YAML_FILES = {
    "aot-structures.yaml": ROOT / "mods/cnc/rules/aot-structures.yaml",
    "aot-units.yaml":      ROOT / "mods/cnc/rules/aot-units.yaml",
    "overrides.yaml":      ROOT / "mods/cnc/rules/overrides.yaml",
    "engine/structures.yaml": ROOT / "engine/mods/cnc/rules/structures.yaml",
    "engine/vehicles.yaml":   ROOT / "engine/mods/cnc/rules/vehicles.yaml",
    "engine/infantry.yaml":   ROOT / "engine/mods/cnc/rules/infantry.yaml",
}

# ── 1. YAML auslesen ──────────────────────────────────────────────────────────

def parse_yaml_orders(yaml_files):
    """Gibt {(actor_id, file_key): BuildPaletteOrder} zurück."""
    orders = {}
    for file_key, path in yaml_files.items():
        if not path.exists():
            print(f"  ⚠ Datei nicht gefunden: {path}")
            continue
        with open(path) as f:
            lines = f.readlines()
        current_actor = None
        for line in lines:
            m = re.match(r'^([a-zA-Z][a-zA-Z0-9_\-]*):', line)
            if m:
                current_actor = m.group(1)
            m2 = re.match(r'^\s+BuildPaletteOrder:\s*(\d+)', line)
            if m2 and current_actor:
                orders[(current_actor, file_key)] = int(m2.group(1))
    return orders


def parse_actor_queues(yaml_files):
    """
    Gibt {(actor_id, file_key): set(queues)} zurück.
    Liest die Queue:-Zeile im Buildable-Block jedes Aktors.

    Ein Aktor landet nur im Dict wenn er eine explizite Queue:-Zeile hat (also
    einen Buildable-Block). Das `-Buildable: + Buildable:`-Reset-Pattern (z.B. TRUCK)
    bleibt damit baubar; ein reines `-Buildable:` ohne Queue taucht gar nicht auf.
    """
    queues = {}
    for file_key, path in yaml_files.items():
        if not path.exists():
            continue
        with open(path) as f:
            lines = f.readlines()
        current_actor = None
        for line in lines:
            m = re.match(r'^([a-zA-Z][a-zA-Z0-9_\-]*):', line)
            if m:
                current_actor = m.group(1)
            mq = re.match(r'^\s+Queue:\s*(.+)$', line)
            if mq and current_actor:
                qs = {q.strip() for q in mq.group(1).split(',') if q.strip()}
                key = (current_actor, file_key)
                queues.setdefault(key, set()).update(qs)
    return queues


def parse_html_actor_queues(html_content):
    """
    Gibt {actor_id: set(data-queue der Tabellen in denen er steht)} zurück.
    """
    result = {}
    # Jeden <table data-queue="X"> ... </table> Block durchgehen
    for tm in re.finditer(r'<table\s+data-queue="([^"]+)">(.*?)</table>', html_content, re.DOTALL):
        queue = tm.group(1)
        body = tm.group(2)
        for am in re.finditer(r'data-actor="([^"]+)"', body):
            result.setdefault(am.group(1), set()).add(queue)
    return result


# Mod-Dateien überschreiben Engine-Queues (statt zu ergänzen).
MOD_FILE_KEYS = {"aot-structures.yaml", "aot-units.yaml", "overrides.yaml"}

# Queues, die in der HTML bewusst in eine andere Sektion gemerged wurden.
# YAML-Queue → HTML-data-queue
QUEUE_ALIASES = {
    "Aircraft.GDI.AFLT": "Aircraft.GDI",
}


def check_queue_coverage(actor_queues, html_content):
    """
    Vergleicht für jeden baubaren Aktor die YAML-Queues mit den HTML-Tabellen.
    Meldet fehlende Tabellen-Einträge (z.B. Dual-Queue-Aktor nur in einer Sektion).

    Beachtet:
      - Queue-Override-Präzedenz: hat ein Aktor in einer Mod-Datei eine Queue,
        ersetzt diese die Engine-Queues vollständig (statt sie zu ergänzen).
      - Queue-Aliase: bewusst gemergte Sektionen (z.B. Aircraft.GDI.AFLT → Aircraft.GDI).
    """
    html_queues = parse_html_actor_queues(html_content)

    # Pro Aktor: Mod-Queues und Engine-Queues getrennt sammeln
    mod_q, engine_q = {}, {}
    for (actor, file_key), qs in actor_queues.items():
        target = mod_q if file_key in MOD_FILE_KEYS else engine_q
        target.setdefault(actor, set()).update(qs)

    missing = []
    for actor in set(mod_q) | set(engine_q):
        # Mod-Datei gewinnt: nur wenn keine Mod-Queue existiert, Engine verwenden
        yaml_qs = mod_q.get(actor) if actor in mod_q else engine_q.get(actor, set())
        # Aliase auflösen
        yaml_qs = {QUEUE_ALIASES.get(q, q) for q in yaml_qs}
        if actor not in html_queues:
            continue  # steht gar nicht in HTML — separat über not_found gemeldet
        fehlt = yaml_qs - html_queues[actor]
        if fehlt:
            missing.append((actor, sorted(fehlt), sorted(html_queues[actor])))
    return missing


def parse_removed_buildable(yaml_files):
    """
    Gibt ein Set von Actor-IDs zurück, bei denen irgendwo -Buildable: steht.
    Das bedeutet: Buildable-Trait wurde entfernt → nicht mehr baubar → raus aus HTML.
    """
    removed = set()
    for file_key, path in yaml_files.items():
        if not path.exists():
            continue
        with open(path) as f:
            lines = f.readlines()
        current_actor = None
        for line in lines:
            m = re.match(r'^([a-zA-Z][a-zA-Z0-9_\-]*):', line)
            if m:
                current_actor = m.group(1)
            if re.match(r'^\s+-Buildable:\s*$', line) and current_actor:
                removed.add(current_actor)
    return removed

# ── 2. HTML patchen ───────────────────────────────────────────────────────────

def sync_html(orders, removed_buildable):
    with open(HTML) as f:
        content = f.read()

    # Alle <tr data-actor="X" data-file="Y" data-orig="Z"> finden
    pattern = re.compile(
        r'(<tr\s[^>]*data-actor="([^"]+)"\s+data-file="([^"]+)"\s+data-orig=")(\d+)(")'
    )

    updated = 0
    not_found = []

    def replacer(m):
        nonlocal updated
        prefix, actor, file_key, old_orig, suffix = m.group(1), m.group(2), m.group(3), m.group(4), m.group(5)
        key = (actor, file_key)
        if key not in orders:
            not_found.append((actor, file_key, old_orig))
            return m.group(0)  # unverändert
        new_val = str(orders[key])
        if new_val == old_orig:
            return m.group(0)
        updated += 1
        print(f"  ✓ {actor} ({file_key}): {old_orig} → {new_val}")
        return f"{prefix}{new_val}{suffix}"

    content = pattern.sub(replacer, content)

    # value="X" in <input class="order-input"> ebenfalls patchen
    def fix_value(m_row):
        row_html = m_row.group(0)
        orig_m = re.search(r'data-orig="(\d+)"', row_html)
        if not orig_m:
            return row_html
        orig = orig_m.group(1)
        row_html = re.sub(
            r'(<input[^>]*class="order-input"[^>]*value=")(\d+)(")',
            lambda m: f"{m.group(1)}{orig}{m.group(3)}",
            row_html
        )
        row_html = re.sub(
            r'(<span class="original-val hidden">)(\d+)(</span>)',
            lambda m: f"{m.group(1)}{orig}{m.group(3)}",
            row_html
        )
        return row_html

    content = re.sub(r'<tr [^>]*data-orig="\d+"[^>]*>.*?</tr>', fix_value, content, flags=re.DOTALL)

    # -Buildable: Aktoren aus HTML entfernen — nur wenn KEIN BuildPaletteOrder mehr vorhanden
    # (d.h. wirklich komplett unbaubar, nicht nur via Proxy ersetzt)
    actors_with_order = {actor for (actor, _) in orders}
    truly_removed = removed_buildable - actors_with_order

    removed_rows = []
    def remove_row(m_row):
        actor_m = re.search(r'data-actor="([^"]+)"', m_row.group(0))
        if not actor_m:
            return m_row.group(0)
        actor = actor_m.group(1)
        if actor in truly_removed:
            removed_rows.append(actor)
            return ""
        return m_row.group(0)

    content = re.sub(r'\s*<tr [^>]*data-orig="\d+"[^>]*>.*?</tr>', remove_row, content, flags=re.DOTALL)

    with open(HTML, "w") as f:
        f.write(content)

    return updated, not_found, removed_rows

# ── 3. Main ───────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    print("=== sync-buildorder ===\n")
    print("Lese YAML-Dateien...")
    orders = parse_yaml_orders(YAML_FILES)
    removed_buildable = parse_removed_buildable(YAML_FILES)
    actor_queues = parse_actor_queues(YAML_FILES)
    print(f"  {len(orders)} BuildPaletteOrder-Einträge gefunden")
    print(f"  {len(removed_buildable)} Aktoren mit -Buildable: gefunden\n")

    print("Patche buildorder.html...")
    updated, not_found, removed_rows = sync_html(orders, removed_buildable)

    print(f"\n✅ {updated} Werte aktualisiert")

    # Queue-Coverage prüfen (Dual-Queue-Aktoren, die in einer HTML-Sektion fehlen)
    with open(HTML) as f:
        missing_coverage = check_queue_coverage(actor_queues, f.read())
    if missing_coverage:
        print(f"\n⚠ {len(missing_coverage)} Aktoren fehlen in einer Queue-Sektion der HTML:")
        for actor, fehlt, vorhanden in missing_coverage:
            print(f"   - {actor}: fehlt in {fehlt} (steht in {vorhanden})")
        print("  → Aktor steht in YAML in mehreren Queues, aber nicht in allen HTML-Tabellen.")

    if removed_rows:
        print(f"\n🗑 {len(removed_rows)} Aktoren entfernt (-Buildable: gesetzt):")
        for actor in removed_rows:
            print(f"   - {actor}")

    if not_found:
        print(f"\n⚠ {len(not_found)} Aktoren in HTML, aber nicht in YAML gefunden:")
        for actor, file_key, orig in not_found:
            print(f"   - {actor} (file: {file_key}, data-orig: {orig})")
        print("  → Prüfe ob Aktor umbenannt oder Queue gewechselt wurde.")
