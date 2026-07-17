# OpenRA Mod – Projektplan (Kanban)

## Backlog

- [ ] To-Do
  - [x] V2 & LARS / Howitzer (Toxic / Heavy Arty)
  - [x] buggy & laser
  - [x] bike icon & laser upgrade
  - [x] stealthtank & cargo
  - [x] gap & stealth generators
  - [x] mobile gap genererator (GDI)
  - [x] mobile stealth generator (NOD)
  - [x] sensor array (dome hat das!)
  - [x] nuke silo & toxic rocket
  - [x] uplink centre & super power
  - [x] titan / juggernaut (heavy mechs)
  - [x] wolverine
  - [x] shrine & avatar / laser avatar
  - [x] civlian cars
  - [x] starting units (nach NOD V2)
  - [x] GDI mine layer (vs. AAPC)
  - [x] dropship routine (2 jeeps, 2 mtnk)
  - [ ] coop archon system
  - [ ] coop mission 1 (test)
  - [x] 1. upgrade toggle
  - [x] 2. bridge repair mechanik & destruction
  - [x] 3. spaceport/shuttle airfield
  - [x] 4. ice grow & destruction
  - [x] 5. special unit tab
  - [ ] 6. subterrain upgrade (flametank, apc?) & betonplatten
  - [ ] gdi radar & nod dome age 3
  - [ ] powerplants & turbines age 3
  - [ ] construction yard age 3
  - [ ] LITE nod & LITE gdi age 3
  - [ ] hand of nod & gdi-pyle age 3
  - [x] civilian ts streets, buildings & garrissons
  - [ ] TS & RA soundtracks
  - [ ] idle menü map szene
  - [ ] hospital super-power (heal all) & iron dome super power (invicible all)
  - [ ] todo vor ki: upgrade toggle, spaceport system, bridge huts, special unit tab/subterain systems
  - [ ] TS lightning & ambient sounds
  - [ ] subterrain upgrade (flametank, apc?) & betonplatten
- [ ] Fixes
  - [ ] einheiten auf repair facility verkaufen
  - [x] upgrades wenn tec center selektiert
  - [ ] support buildings in support tab (statt defense?)
  - [ ] upgrades nur wenn power
  - [ ] heli & jet start/lande sounds
  - [ ] KI-bau routine
  - [ ] fire position power bedarf entfernen
  - [ ] platzhalter-icons (tiberium wars)
  - [ ] repair facility bib-smudge
  - [ ] super-power impact icons (beacons)
  - [ ] power shutdown AA & defenses
  - [x] oil pums spawnen 1000 credits wenn zerstört
  - [x] aot-age upgrade kosten (5000, 10000, 25000)
  - [ ] age power toggle / direkt oben links
  - [ ] NOD transport vessel cargo ändern

## Design / Konzept

- [ ] Techtree & Buildorder
  file:///Users/moritzgiuliani/Documents/openRA%20Projekte/TiberianDawnHD/mods/management/kanban-board.html
  - [x] chrome webinterface
  - [x] techtree web tool
  - [x] build order web tool
  - [x] terrain web status
- [ ] Upscaling Building sprites (TS)
  Tiberian Sun voxel assets
  - [x] GDI powerplant turbines
  - [x] NOD radar dome
  - [x] NOD temple
  - [x] NOD secret shrine (pyramid)
  - [x] GDI orbital command (uplink)
  - [x] GDI tech centre
  - [x] NOD tech centre
  - [x] NOD missile silo
  - [x] NOD powerplant
  - [x] NOD advanced powerplant
  - [x] GDI barracks
  - [x] NOD hand
  - [x] GDI radar
  - [x] GDI firestorm generator
  - [x] GDI & NOD lite factories
  - [x] Construction yard
  - [x] Refinery
  - [x] Repair Facility
  - [x] Storage Silo
  - [ ] Proxy Icons
  - [ ] Damage Models
  - [ ] Make Animations
  - [ ] Stealth Generator
- [ ] Defense Ideas
  Garrison types: Gunner, Gren, Rocket, Flame, Toxic, Zone, Cyborg, Commando
  - [x] GDI Pillbox -> Guard Tower (Infantry Garrison)
  - [x] NOD 1 Gun Turret -> 2 Laser Turret (nach Laser upgrade)
  - [x] NOD 3 Obelisk (nach laser upgrade)
  - [x] NOD 0 Flame Turret -> 2 Toxic Turret (Toxic upgrade zusammen mit Flame trooper und Flame tank)
  - [x] GDI Fire Position (vehicle garrison)
- [ ] Nod Artillery Idea
  - [x] V2 -> MLRS / Howitzer (age 1)
  - [x] MLRS -> Toxic MLRS (age 3)
  - [x] Howitzer -> Heavy Artillery (age 3)
- [ ] GDI Tank & Mech idea
  - [x] Hmvee -> Wolverine
  - [x] APC -> AAPC / Mine Layer
  - [x] MTNK -> Predator / Titan
  - [x] MLRS -> Hover MLRS & Hover AA / Jugg
- [ ] Subterrain Systems
  Age 2 system
  - [ ] Sub Terrain Flame Tank Upgrade
  - [ ] Sub Terrain Transport
  - [ ] Concrete Plates (Grey Smudge?)

## Rules / Balancing

- [ ] rules.yaml Grundstruktur anlegen
  Webinterface um alle Einheiten, Gebäude und Waffenwerte zu verändern
- [ ] NOD Units balancing
- [ ] GDI Units Balancing
- [ ] NOD Building Balancing
- [ ] GDI Building Balancing

## Skirmish Maps

- [ ] ☑️ Testmap
  Erste Zoomap (klein, 2 Spieler) bauen
  - [x] 2 startpunkte
  - [x] 2 ore mines
  - [x] 2 tiberium felder
  - [x] wasser
- [ ] Forest Fires (6)
  Temperate (RA)
  - [x] 6 player
  - [ ] test gegen AI
  - [ ] ressource balancing
  - [x] tiberium bäume
  - [x] ore refineries
  - [x] oil derricks & pumps
  - [x] bridge repair huts
  - [ ] civilian TS buildings
  - [x] tree-destruction
  - [x] Ant valley
- [ ] Polar Panic (8)
  Snow (RA)
  - [x] 8 player
  - [ ] test gegen AI
  - [ ] ressource balancing
  - [x] tiberium bäume
  - [ ] Iron dome island
  - [x] oil derricks & pumps
  - [x] bridge repair huts
  - [x] ice system
- [ ] Desert?
  - [ ] Critter waves (spawner wie ants, random base attack order)
  - [ ] toxic tiberium
  - [ ] toxic tiberium refinery ruin
- [ ] Winter?
  - [ ] hq island (paradrops) im norden
  - [ ] snow water-cliffs & snow cliffs
  - [ ] ice system
- [ ] Jungle?
  - [ ] Ion Storm?
  - [ ] Dinosaur valley
  - [ ] hospital in valley
  - [ ] destructable cliff

## Campaigns

- [ ] Age 0 NOD

