#!/usr/bin/env python3
"""
Audit: Welche Gebaeude-Aktoren haben eine Make-Animation, deren Quell-Sprite NICHT
zum Idle-Sprite desselben Bodies passt (oder gar keine Make-Animation)?

Beruecksichtigt die echte mod.yaml-Ladereihenfolge inkl. Vanilla-Basis
(cnc| = engine/mods/cnc, cnchd| = mods/cnc  -- siehe mod-package-alias-trap).
"""
import re, os
from collections import OrderedDict

ROOT = "/Users/moritzgiuliani/Documents/openRA Projekte/TiberianDawnHD"
CNC = f"{ROOT}/engine/mods/cnc"      # cnc|   -> VANILLA
CNCHD = f"{ROOT}/mods/cnc"           # cnchd| -> Mod

RULE_FILES = [
    f"{CNC}/rules/defaults.yaml",
    f"{CNC}/rules/structures.yaml",
    f"{CNC}/rules/misc.yaml",
    f"{CNCHD}/rules/overrides.yaml",
    f"{CNCHD}/rules/aot-structures.yaml",
]
SEQ_FILES = [
    f"{CNC}/sequences/structures.yaml",
    f"{CNCHD}/sequences/structures-overrides.yaml",
    f"{CNCHD}/sequences/aot-sequences.yaml",
]


def parse_miniyaml(path):
    """Parse a tab-indented MiniYaml file into nested OrderedDicts.
    Returns {topkey: {childkey: {grandchild: value}}} -- 3 levels are enough here."""
    if not os.path.exists(path):
        return OrderedDict()
    out = OrderedDict()
    stack = []  # (indent, container)
    with open(path, encoding="utf-8") as f:
        for raw in f:
            line = raw.rstrip("\n")
            if not line.strip() or line.strip().startswith("#"):
                continue
            ind = len(line) - len(line.lstrip("\t"))
            m = re.match(r"^\t*([^:]+):\s*(.*)$", line)
            if not m:
                continue
            key, val = m.group(1).strip(), m.group(2).strip()
            while stack and stack[-1][0] >= ind:
                stack.pop()
            container = stack[-1][1] if stack else out
            node = OrderedDict()
            # store scalar under special marker so children can still attach
            entry = {"__val__": val, "__children__": node}
            if isinstance(container, OrderedDict):
                # merge on duplicate key (OpenRA merge semantics)
                if key in container and isinstance(container[key], dict):
                    container[key]["__val__"] = val or container[key].get("__val__", "")
                    node = container[key]["__children__"]
                    entry = container[key]
                else:
                    container[key] = entry
            stack.append((ind, node))
    return out


def merge_rules(files):
    """Merge actor definitions across files in load order (later merges into earlier)."""
    actors = OrderedDict()
    for path in files:
        doc = parse_miniyaml(path)
        src = os.path.relpath(path, ROOT)
        for actor, node in doc.items():
            a = actors.setdefault(actor, {"traits": OrderedDict(), "sources": []})
            if src not in a["sources"]:
                a["sources"].append(src)
            for tkey, tnode in node["__children__"].items():
                if tkey.startswith("-"):
                    # removal: drop matching trait (with or without @suffix)
                    base = tkey[1:]
                    for existing in list(a["traits"]):
                        if existing == base or existing.startswith(base + "@"):
                            del a["traits"][existing]
                    continue
                fields = a["traits"].setdefault(tkey, {})
                fields["__val__"] = tnode["__val__"]
                for fk, fn in tnode["__children__"].items():
                    fields[fk] = fn["__val__"]
    return actors


def resolve_inherits(actors):
    """Flatten Inherits chains. Returns {actor: merged_traits}."""
    cache = {}

    def resolve(name, seen=None):
        if name in cache:
            return cache[name]
        seen = seen or set()
        if name in seen or name not in actors:
            return OrderedDict()
        seen = seen | {name}
        merged = OrderedDict()
        own = actors[name]["traits"]
        # collect parents (Inherits, Inherits@xyz) in declaration order
        for tkey, fields in own.items():
            if tkey == "Inherits" or tkey.startswith("Inherits@"):
                parent = fields.get("__val__", "").strip()
                if parent:
                    for pk, pv in resolve(parent, seen).items():
                        merged[pk] = dict(pv)
        # apply own traits on top
        for tkey, fields in own.items():
            if tkey == "Inherits" or tkey.startswith("Inherits@"):
                continue
            if tkey.startswith("-"):
                base = tkey[1:]
                for existing in list(merged):
                    if existing == base or existing.startswith(base + "@"):
                        del merged[existing]
                continue
            tgt = merged.setdefault(tkey, {})
            tgt.update(fields)
        cache[name] = merged
        return merged

    return {n: resolve(n) for n in actors}


def parse_sequences(files):
    """{image: {seqname: {field: value}}} with Defaults inheritance applied."""
    images = OrderedDict()
    for path in files:
        doc = parse_miniyaml(path)
        for img, node in doc.items():
            im = images.setdefault(img, OrderedDict())
            for seq, snode in node["__children__"].items():
                if seq.startswith("-"):
                    im.pop(seq[1:], None)
                    continue
                s = im.setdefault(seq, {})
                s["__val__"] = snode["__val__"]
                s.setdefault("__own__", set())
                for fk, fn in snode["__children__"].items():
                    s[fk] = fn["__val__"]
                    s["__own__"].add(fk)   # explicitly set ON THIS SEQUENCE
    # apply per-image Defaults (only where the sequence itself says nothing)
    for img, seqs in images.items():
        defaults = seqs.get("Defaults", {})
        if not defaults:
            continue
        for seq, fields in seqs.items():
            if seq == "Defaults":
                continue
            for dk, dv in defaults.items():
                if dk.startswith("__"):
                    continue
                fields.setdefault(dk, dv)
    return images


# TS-Originalnamen, an denen ein PNG/ZIP-Repack als Tiberian-Sun-Import erkennbar ist.
TS_STEMS = (
    "gtpowr", "ntpowr", "ntapwr", "gtcnst", "gtpile", "nthand", "gadept", "gtdept",
    "namisl", "nttech", "natech", "gtradr", "ntradr", "natmpl", "ntpyra", "gtdept",
    "weap2", "weap2n", "silomake-ts", "slab-ts", "tmpl-ts", "atec-ts", "stec-ts",
    "orbc", "shrine", "oremine", "apc2", "nod-radar-age2", "hq-age2", "nod-silo-ts",
)


def classify(filename, remastered):
    """Woher stammt das Sprite? filename/remastered sind bereits aufgeloest."""
    src = (remastered or filename or "").strip()
    if not src:
        return "NONE"
    low = src.lower()
    if "gemini" in low:
        return "GEMINI"
    if "red_alert" in low:
        return "RA-Remaster"
    if "tiberian_dawn" in low or "\\common\\" in low:
        return "TD-Remaster"
    if low.endswith(".shp"):
        return "SHP-vanilla"
    if any(stem in low for stem in TS_STEMS):
        return "TS-Import"
    if low.endswith(".zip"):
        return "ZIP-custom"
    if low.endswith(".png"):
        return "PNG-repack"
    return "PNG-repack"


# Was faellt in das beauftragte Arbeitspaket (Gemini-/TS-Sprites)?
IN_SCOPE = {"GEMINI", "TS-Import"}


def age_tier(cond):
    """In welchem Age-Tier ist dieser Body sichtbar? Aus der RequiresCondition."""
    c = (cond or "").replace(" ", "")
    if not c:
        return "alle"
    for tier, tok in (("Age3", "aot-age3"), ("Age2", "aot-age2"), ("Age1", "aot-age1")):
        if tok in c and f"!{tok}" not in c.replace(f"&&!{tok}", "&&"):
            # positive Nennung des Tokens -> Body gehoert zu diesem Tier
            idx = c.find(tok)
            if idx == 0 or c[idx - 1] != "!":
                return tier
    if "!aot-age1" in c:
        return "Age0"
    return "alle"


def seq_source(images, image, seqname):
    """Return (kind, filename) for image/seqname.
    Bevorzugt explizit AUF DER SEQUENZ gesetzte Keys vor geerbten Defaults --
    sonst schlaegt ein vanilla `Defaults: Filename:` durch und verfaelscht die
    Quellen-Zuordnung (Bug: SAM ra-idle wurde als `sam.shp` statt als
    `aot-sam-make.zip` ausgewiesen)."""
    if not image or image not in images:
        return ("NONE", None)
    seqs = images[image]
    if seqname not in seqs:
        return ("NONE", None)
    f = seqs[seqname]
    own = f.get("__own__", set())
    if "RemasteredFilename" in own and f.get("RemasteredFilename", "").strip():
        rem = f["RemasteredFilename"]
        return (classify(None, rem), rem)
    if "Filename" in own and f.get("Filename", "").strip():
        fn = f["Filename"]
        return (classify(fn, None), fn)
    fn = f.get("Filename") or f.get("__val__") or None
    rem = f.get("RemasteredFilename")
    return (classify(fn, rem), fn or (rem if rem else None))


def main():
    actors_raw = merge_rules(RULE_FILES)
    actors = resolve_inherits(actors_raw)
    images = parse_sequences(SEQ_FILES)

    # Which images belong to at least one player-buildable actor?
    # Neutral/editor-only structures never play a make animation -> irrelevant.
    buildable_images = set()
    for name, traits in actors.items():
        if name.startswith("^"):
            continue
        if not any(t == "Buildable" or t.startswith("Buildable@") for t in traits):
            continue
        img = None
        for t, f in traits.items():
            if t == "RenderSprites" or t.startswith("RenderSprites@"):
                if f.get("Image"):
                    img = f["Image"]
        buildable_images.add(img or name.lower())

    findings = []
    for name, traits in sorted(actors.items()):
        if name.startswith("^"):
            continue
        # only real buildings: must have a Building trait
        if not any(t == "Building" or t.startswith("Building@") for t in traits):
            continue

        image = None
        for t, f in traits.items():
            if t == "RenderSprites" or t.startswith("RenderSprites@"):
                if f.get("Image"):
                    image = f["Image"]
        if not image:
            image = name.lower()

        if image not in buildable_images:
            continue  # neutral/editor-only -> never plays a make animation

        # sprite bodies: name -> idle sequence
        bodies = OrderedDict()
        for t, f in traits.items():
            if re.match(r"^With(Facing)?SpriteBody(@|$)", t):
                bname = f.get("Name", "body")
                bseq = f.get("Sequence", "idle")
                cond = f.get("RequiresCondition", "")
                bodies[bname] = (bseq, cond)

        # make animations: body -> make sequence
        makes = {}
        for t, f in traits.items():
            if re.match(r"^WithMakeAnimation(@|$)", t):
                bnames = [b.strip() for b in f.get("BodyNames", "body").split(",")]
                mseq = f.get("Sequence", "make")
                for b in bnames:
                    makes[b] = mseq

        for bname, (bseq, cond) in bodies.items():
            ikind, ifile = seq_source(images, image, bseq)
            mseq = makes.get(bname)
            if mseq:
                mkind, mfile = seq_source(images, image, mseq)
            else:
                mkind, mfile = ("NONE", None)

            problem = None
            if ikind == "NONE":
                continue  # body has no resolvable idle -> skip (inherited/vanilla)
            if ifile and "blank" in ifile.lower():
                continue  # intentionally empty placeholder layer (e.g. MSLO doors)
            if "!build-incomplete" in cond.replace(" ", ""):
                continue  # deliberately hidden during construction -> no make anim needed
            if mseq is None or mkind == "NONE":
                problem = "KEINE Make-Animation"
            elif ikind == "GEMINI" and mkind != "GEMINI":
                problem = f"Mismatch (idle=GEMINI, make={mkind})"
            elif ikind != "GEMINI" and mkind == "SHP-vanilla":
                problem = f"Platzhalter (idle={ikind}, make=SHP-vanilla)"

            if problem:
                findings.append({
                    "actor": name, "image": image, "body": bname,
                    "idle": f"{ikind}:{ifile}", "make": f"{mkind}:{mfile}" if mseq else "-",
                    "mseq": mseq or "-", "problem": problem,
                    "cond": cond, "sources": actors_raw[name]["sources"],
                    "age": age_tier(cond), "ikind": ikind,
                    "scope": "Gemini/TS" if ikind in IN_SCOPE else f"AUSSERHALB ({ikind})",
                })

    # group by (image, body, idle, make) to collapse proxy actors
    # Nach Sprite/Body gruppieren. Age NICHT in den Schluessel nehmen: Editor-/
    # Proxy-Aktoren tragen Tautologie-Conditions ("aot-age2-active ||
    # !aot-age2-active"), die sonst dieselbe Zeile kuenstlich aufspalten.
    groups = OrderedDict()
    ages = {}
    for f in findings:
        key = (f["image"], f["body"], f["idle"], f["make"], f["problem"], f["scope"])
        groups.setdefault(key, []).append(f["actor"])
        ages.setdefault(key, set()).add(f["age"])

    print(f"{'IMAGE':<18} {'BODY':<20} {'AGE':<7} {'SCOPE':<24} {'PROBLEM':<40} {'IDLE':<44} {'MAKE'}")
    print("=" * 210)
    for key, acts in sorted(
            groups.items(), key=lambda kv: (kv[0][5].startswith("AUSSERHALB"), kv[0][0], kv[0][1])):
        image, body, idle, make, problem, scope = key
        tiers = ages[key] - {"alle"} or {"alle"}
        age = "/".join(sorted(tiers))
        print(f"{image:<18} {body:<20} {age:<7} {scope:<24} {problem:<40} {idle:<44} {make}")
        print(f"{'':<18} └─ Aktoren ({len(acts)}): {', '.join(sorted(acts))}")
    print()
    n_scope = sum(1 for k in groups if k[5] == "Gemini/TS")
    print(f"Gesamt: {len(groups)} eindeutige Sprite/Body-Kombinationen "
          f"({n_scope} im Gemini/TS-Scope, {len(groups) - n_scope} ausserhalb), "
          f"{len(findings)} Aktor-Instanzen")


if __name__ == "__main__":
    main()
