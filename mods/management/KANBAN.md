# OpenRA Mod – Projektplan (Kanban)

## Backlog

- [ ] To-Do
  - [ ] V2 & LARS
  - [ ] buggy & laser
  - [ ] bike icon
  - [ ] stealthtank & cargo
  - [ ] mig & banshee
  - [ ] mobile gap genererator
  - [ ] mobile stealth generator
  - [ ] sensor array (dome hat das!)
  - [ ] repair facility stage 3
  - [x] nuke silo & toxic rocket
  - [x] uplink centre & super power
  - [ ] titan / juggernaut
  - [ ] wolverine
  - [ ] shrine & avatar / laser avatar
  - [x] civlian cars
  - [ ] age power requirements (requires: atec/stec, tmple/slab ,shrine/uplink
  - [ ] civilian buildings
  - [ ] starting units
  - [ ] firestorm-system.md
  - [ ] GDI mine layer
  - [x] dropship routine (2 jeeps, 2 mtnk)
  - [ ] highres ts upscaling
  - [ ] coop archon system
  - [ ] 1st coop mission
  - [ ] NOD GATE
- [ ] Fixes
  - [ ] einheiten auf repair facility verkaufen
  - [ ] age power requirement
  - [ ] upgrades wenn tec center selektiert
  - [ ] LITE eigene queue (statt defense?)
  - [ ] upgrades nur wenn power
  - [ ] heli & jet start/lande sounds
  - [ ] KI-bau routine
  - [ ] ore um ore-mine

## Design / Konzept

- [ ] Techtree & Buildorder
  file:///Users/moritzgiuliani/Documents/openRA%20Projekte/TiberianDawnHD/mods/management/kanban-board.html
  - [x] chrome webinterface
  - [x] techtree web tool
  - [x] build order web tool
  - [x] terrain web status

## Rules / Balancing

- [ ] rules.yaml Grundstruktur anlegen
  Webinterface um alle Einheiten, Gebäude und Waffenwerte zu verändern
- [ ] NOD Units balancing
- [ ] GDI Units Balancing
- [ ] NOD Building Balancing
- [ ] GDI Building Balancing

## Maps

- [ ] ☑️ Testmap
  Erste Zoomap (klein, 2 Spieler) bauen
  - [x] 2 startpunkte
  - [x] 2 ore mines
  - [x] 2 tiberium felder
  - [x] wasser
- [ ] MP Forest Fires
  Temperate (RA)
- [ ] NOD - A0 - 01

## Programmierung

- [ ] Mod-Ordnerstruktur aufsetzen
  (mod.yaml, manifest)
  - [x] eigene github connection
  - [x] openra github experimental
  - [ ] code-struktur & comments sauber
- [ ] ☑️ Red Alert Remastered Assets
- [ ] ☑️ Tiberian Sun Vox-Assets
- [ ] Aircrafts
  - [x] Apache / Orca
  - [x] HIND / Harpy
  - [ ] Transport Helo / Carryall
  - [ ] A10 / Firehawk
  - [ ] MiG? / Banshee
- [ ] Wall & Gate System
  Gates, Reinforced Walls, Barbwire, Firestorm, Laser Fences
  - [x] Reinforce Wall (w. Sandbags)
  - [x] Barbwire
  - [ ] Laser-Fences
  - [ ] Firestorm Matrix
  - [ ] Gates
- [ ] Stealth area generator & sensors
- [ ] Shroud / Gap Generation
- [ ] Carryall Pickup
- [ ] Standalone Release
- [ ] Coop unit-sharing
- [ ] Infantry & Special Infantry
  Both Factions
  - [x] Nod Cyborg
  - [x] Nod Cyborg Command
  - [x] Nod Rifle
  - [x] Nod Bazooka
  - [x] Nod Saboteur
  - [x] Nod Carjacker
  - [x] Nod Dog
  - [x] Nod Flame Thrower / Toxic
  - [x] GDI Rifle
  - [x] GDI Engineer
  - [x] GDI Grenadier
  - [x] GDI Undercover Spy
  - [x] GDI Commando
  - [x] Nod Chameleon Spy
  - [x] Gdi Medic
  - [x] Gdi Mechanic
- [ ] Naval Warfare & Amphibic
  - [x] Gunboat / Patrol Boat / Destroyer
  - [x] Submarine / Cargo Submarine
  - [x] Missile Sub / Drone Attack Sub / Mine Layer Sub
  - [x] Cruiser / Heli Carrier / Advanced Cruiser
  - [x] Transport Vessel -> Hovercraft
  - [x] Transport Vessel -> Armed
  - [x] Hover MLRS / Hover AA
  - [x] Amphibious APC

## Testing / Bugs

- [ ] Smoke-Test: Mod lädt ohne Fehler
- [ ] Multiplayer-Test (Lobby, Sync)

## Units (Blue)

- [ ] Jeep
  Light Scout Unit
  - [x] RA Jeep (1 cargo)
  - [x] TD Humvee (1 cargo)
  - [x] TD APC (non-AA)
  - [x] Upgrades
  - [x] Icons
- [ ] Medium Tank
  Battletank with GUN turret once Predator upgrade an greyish barrel once Railgun Upgrade
  - [x] TD Tank
  - [x] Predator Upgrade
  - [x] Railgun Upgrade
  - [ ] Icons
  - [ ] Predator Assets
  - [ ] Railgun Assets

## Buildings (Blue)

- [ ] MCV / Construction Yard
  Looks like RA in Age 0 tier before switching to TD with age 1 following.
  - [x] RA Assets
  - [x] TD Assets
  - [x] Animations
  - [x] Icons
- [ ] Powerplant
  - [ ] Age 0: Coalplant (RA)
  - [ ] Age 1: Wind Turbine (TS)
- [ ] Comm Centre
  Looks like GDI comm centre (Requires Vehicle Production)
  - [x] Unlocks Ion Cannon Uplink (Advanced Comm Centre)
  - [ ] Requirements
- [ ] Light Factory
  RA warfactory for light vehicles
  - [x] RA assets
  - [ ] Requirements
  - [ ] Icon
- [ ] Tech Centre
  RA allied Tech Centre
  - [ ] RA assets
  - [ ] RA super-power "Map reveal"
  - [ ] Requirements
  - [ ] Icon

## Buildings (Red)

- [ ] MCV / Construction Yard
  Looks like RA in Age 0 tier before switching to TD with age 1 following.
  - [x] RA Assets
  - [x] TD Assets
  - [x] Animations
  - [x] Icons
- [ ] Powerplant
  - [ ] Age 0: Coalplant (RA)
  - [ ] Age 1: Nucelar Plant (TD)
- [ ] Radar
  Looks like RA dome (Requires Vehicle Production)
  - [x] RA assets
  - [ ] Requirements
  - [ ] Icon
- [ ] Light Factory
  RA warfactory for light vehicles
  - [x] RA assets
  - [ ] Requirements
  - [ ] Icon
- [ ] Tech Centre
  RA Soviet Tech Centre
  - [ ] RA assets
  - [ ] RA super-power?
  - [ ] Requirements
  - [ ] Icon
- [ ] Temple
  - [ ] TD Temple. Unlocks silo
  - [ ] Icon
- [ ] Nuke Silo
  - [ ] RA assets
  - [ ] Icon
  - [ ] Supoer-Power transfer

## Buildings (Neutral)

- [ ] Ore Mine
  Indestructible.
  - [ ] Asset
  - [ ] Function
  - [ ] Decaying

