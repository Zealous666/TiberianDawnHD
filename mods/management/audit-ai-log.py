#!/usr/bin/env python3
"""Audit an OpenRA debug.log for AoT bot-AI defects.

Written after a whole class of bug -- missions that kill themselves on their first tick --
survived many test runs because it hid behind a plausible message ("convoy lost before reaching
the site"). Reading the log by eye and by ad-hoc grep is how that happened: it invites a story
that fits the few lines you happened to look at. This tool answers the questions that actually
distinguish "not working yet" from "structurally broken", the same way every time:

  * which operations NEVER got off the ground (planned/created but no progress at all)
  * which ones are stuck in one state for the whole run
  * whether the saving gates are ever passed
  * per-player timeline of age, cash and operation states

Usage:
    python3 mods/management/audit-ai-log.py [path/to/debug.log]

Defaults to the standard macOS log location.
"""

import os
import re
import sys
from collections import Counter, defaultdict

DEFAULT_LOG = os.path.expanduser(
    "~/Library/Application Support/OpenRA/Logs/debug.log")

# Player tag is "InternalName/PlayerName" since 2026-08-10: every bot in a skirmish shares the
# display name "bot-cabal.name", so grouping on it merged all of them into one entry and made
# three bots' lines read as one bot's sequence.
OPS = re.compile(r"^\[AotOps\]\[([^\]]+)\](?:\[([^\]]+)\])? (.*)$")
STATUS = re.compile(
    r"^\[AotStatus\] (\S+) (\S+) '([^']+)' \[([^/\]]+)/\S*\] \(([^)]*)\) (.*)$")
FIELD = re.compile(r"(\w+)=(\S+)")

# Message fragments that mean "this operation reached a real milestone", as opposed to merely
# being planned. Used to separate genuine progress from endless re-planning.
PROGRESS = ("saved up", "ordered", "under way", "underway", "deployed", "arrived",
            "repair underway", "boarding", "loaded", "attacking", "settling")


def normalise(msg):
    """Collapse numbers so repeated messages group into one kind."""
    return re.sub(r"\d+", "#", msg)


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_LOG
    if not os.path.exists(path):
        sys.exit(f"log not found: {path}")

    stillborn = []
    ops_kinds = defaultdict(Counter)          # (player, mission) -> message kinds
    ops_progress = defaultdict(bool)          # (player, mission) -> saw real progress
    players = {}                              # player -> (name, colour, faction)
    field_states = defaultdict(lambda: defaultdict(Counter))  # player -> field -> values
    last_status = {}
    lines = 0

    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            lines += 1
            line = line.rstrip("\n")

            m = OPS.match(line)
            if m:
                player, mission, msg = m.group(1), m.group(2) or "-", m.group(3)
                key = (player, mission)
                ops_kinds[key][normalise(msg)] += 1
                if any(p in msg.lower() for p in PROGRESS):
                    ops_progress[key] = True
                if "STILLBORN" in msg:
                    stillborn.append((player, mission, msg))
                continue

            m = STATUS.match(line)
            if m:
                _, label, botname, colour, faction, rest = m.groups()
                players[label] = (botname, colour, faction)
                last_status[label] = rest
                for field, value in FIELD.findall(rest):
                    field_states[label][field][re.sub(r"\(.*?\)", "", value)] += 1

    print(f"=== {path}")
    print(f"    {lines} lines\n")

    # 1. Stillborn missions -- always a bug, never a tuning issue.
    print("--- Stillborn missions (ended without ever holding a unit or an order) ---")
    if stillborn:
        for player, mission, msg in stillborn[:20]:
            print(f"  !! {player} [{mission}] {msg}")
        print(f"  total: {len(stillborn)}")
    else:
        print("  none")

    # 2. Operations that were planned but never progressed.
    print("\n--- Operations with no real progress ---")
    dead = [k for k, kinds in ops_kinds.items()
            if not ops_progress[k] and sum(kinds.values()) > 2]
    if dead:
        for player, mission in sorted(dead):
            kinds = ops_kinds[(player, mission)]
            top = ", ".join(f"{k} x{v}" for k, v in kinds.most_common(3))
            print(f"  ?? {player} [{mission}]: {top}")
    else:
        print("  none")

    # 3. Per-player operation states across the whole run. A field stuck on a single value for
    #    every sample is the signature of a system that never ran at all.
    print("\n--- Per-player status fields (value: samples) ---")
    for label in sorted(players):
        botname, colour, faction = players[label]
        print(f"\n  {label} '{botname}' [{colour}] ({faction})")
        for field, values in field_states[label].items():
            if field in ("cash",):
                continue
            total = sum(values.values())
            summary = ", ".join(f"{v}:{c}" for v, c in values.most_common(4))
            stuck = " <-- NEVER CHANGED" if len(values) == 1 and total > 20 else ""
            print(f"    {field:<10} {summary}{stuck}")
        print(f"    last: {last_status[label]}")


if __name__ == "__main__":
    main()
