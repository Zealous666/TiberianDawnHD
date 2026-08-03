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
                for fk, fn in snode["__children__"].items():
                    s[fk] = fn["__val__"]
    # apply per-image Defaults
    for img, seqs in images.items():
        defaults = seqs.get("Defaults", {})
        if not defaults:
            continue
        for seq, fields in seqs.items():
            if seq == "Defaults":
                continue
            for dk, dv in defaults.items():
                fields.setdefault(dk, dv)
    return images


def classify(filename, remastered):
    if filename and "gemini" in filename.lower():
        return "GEMINI"
    if remastered and remastered.strip():
        return "REMASTER-ZIP"
    if filename:
        if filename.lower().endswith(".shp"):
            return "SHP-vanilla"
        if filename.lower().endswith(".png"):
            return "PNG-repack"
        return "PNG-repack"
    return "NONE"


def seq_source(images, image, seqname):
    """Return (kind, filename) for image/seqname."""
    if not image or image not in images:
        return ("NONE", None)
    seqs = images[image]
    if seqname not in seqs:
        return ("NONE", None)
    f = seqs[seqname]
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
                })

    # group by (image, body, idle, make) to collapse proxy actors
    groups = OrderedDict()
    for f in findings:
        key = (f["image"], f["body"], f["idle"], f["make"], f["problem"])
        groups.setdefault(key, []).append(f["actor"])

    print(f"{'IMAGE':<18} {'BODY':<22} {'PROBLEM':<42} {'IDLE':<46} {'MAKE'}")
    print("=" * 190)
    for (image, body, idle, make, problem), acts in sorted(groups.items()):
        print(f"{image:<18} {body:<22} {problem:<42} {idle:<46} {make}")
        print(f"{'':<18} └─ Aktoren ({len(acts)}): {', '.join(sorted(acts))}")
    print()
    print(f"Gesamt: {len(groups)} eindeutige Sprite/Body-Kombinationen, {len(findings)} Aktor-Instanzen")


if __name__ == "__main__":
    main()
