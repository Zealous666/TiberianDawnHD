# Hovercraft Transport System (aot-transport-hover)

## Status: IMPLEMENTIERT (2026-06-28)

## Design
GDI-exklusives Upgrade für den Transport Vessel. Hovercraft fährt auf Wasser UND Land.

## Sprite
- Platzhalter: `aot-transport.zip` (RA LST, gleiche Sprites wie normaler Transport)
- TODO: TD LST Sprite extrahieren → `mods/cnc/bits/aot-transport-hover.zip`
- Facings: 1 (keine Rotation, wie normaler Transport)

## Locomotor: `hover`
Definiert in `engine/mods/cnc/rules/world.yaml`:
- Water: 100% Geschwindigkeit
- Beach: 100% 
- Clear/Rough/Road/River/Tiberium/BlueTiberium: 33% (1/3)

## Cargo
- `Types: Infantry, GroundVehicle, MaxWeight: 5`
- 5 Infanteristen (Weight 1 je) ODER 1 Fahrzeug (Weight 5)
- Fahrzeuge mit CargoType GroundVehicle (in overrides.yaml): JEEP, BGGY, BIKE

## Upgrade-Ausschluss mit Gun Upgrade
- `~!aot-transport-hover-upgrade` in Gun-Upgrade-Prerequisites (verschwindet wenn Hover gekauft)
- `~!aot-transport-gun-upgrade` in Hover-Upgrade-Prerequisites (verschwindet wenn Gun gekauft)
- Fraktions-getrennt: Gun = Upgrade.Nod, Hover = Upgrade.GDI

## YAML-Aktoren
- `aot-transport-hover` — Basis-Aktor (Locomotor: hover, Cargo: gemischt)
- `aot-transport-hover-proxy` — Baumenü-Proxy (Naval.GDI, nach Hover-Upgrade)
- `aot-upgrade-transport-hover` — Upgrade-Item (Upgrade.GDI, Cost: 800, Age1+Shipyard)

## Proxy-Struktur (aot-units.yaml)
| Proxy | Sichtbar wenn |
|---|---|
| aot-transport-base | WEDER gun NOCH hover upgrade |
| aot-transport-gun-proxy | gun-upgrade aktiv, NICHT hover-upgrade |
| aot-transport-hover-proxy | hover-upgrade aktiv, NICHT gun-upgrade (GDI only) |

## Sequences (aot-sequences.yaml)
- `aot-transport-hover` Image: idle/open/close/unload = aot-transport.zip (Platzhalter)
- `aot-upgrade-transport-hover` Icon: vesl_hover.png

## Fluent (aot-rules.ftl)
- `actor-aot-transport-hover` — name + description
- `actor-aot-upgrade-transport-hover` — name + description
