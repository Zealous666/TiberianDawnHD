# OpenRA Mod – Projektplan (Kanban)

## Backlog

- [ ] To-Do
  - [ ] V2 & LARS
  - [ ] buggy & laser
  - [ ] bike icon
  - [ ] stealthtank & cargo
  - [x] hind & harpy sprites & upgrades
  - [x] apache sprite & upgrade
  - [ ] mig & banshee
  - [ ] mobile gap genererator
  - [ ] mobile stealth generator
  - [ ] sensor array
  - [x] helipad icon
  - [x] repair facility stages
  - [x] ttnk & tick upgrade
  - [x] refinery icon
  - [x] atech centre icon
  - [x] stech centre icon & stech centre
  - [x] light factory icon
  - [x] Tibsun voxel-Implementation
  - [x] weap & afld icons
  - [ ] MLRS  & AA
  - [x] gunboat / destroyer
  - [ ] cyborg commando
  - [ ] thief & highjacker
  - [ ] spy
  - [ ] gdi commando railgun
  - [ ] nuke silo & toxic rocket
  - [ ] uplink centre
  - [ ] silo upgrade (nuke silo)
  - [ ] drop-pod upgrade
  - [ ] summer terrain (ra)
  - [ ] winter terrain (td)
  - [x] submarine / wolf / drone carrir
  - [ ] cruiser & railgun & carrier
  - [ ] titan / juggernaut
  - [ ] wolverine
  - [ ] transport ships
  - [ ] jungle terrain?
  - [ ] avatar / laser avatar
  - [ ] APC AA & Amphibious APC
  - [ ] silo & ore transport changes
- [ ] Fixes
  - [x] ATEC satelli fixen & strom
  - [x] EYE als selbst bau entfernen
  - [x] Ion upgrade korrekt implementieren
  - [x] GDI helipad coraussetzung: comm centre & orca / hind ammo
  - [x] mammoth upgrade asset
  - [x] mtnk upgrade assets
  - [x] LTNK icon & upgrade assets
  - [x] HQ & MCV selling nicht möglich
  - [x] MCV redeploy nicht möglich
  - [ ] einheiten auf repair facility verkaufen
  - [ ] ion cannon strike auf repair facility crash?! // lite factory destroy crash?!
  - [ ] age power requirement
  - [ ] upgrades wenn tec center selektiert
  - [ ] LITE eigene queue (statt defense?)
  - [ ] barracks / hand zu groß
  - [ ] infantry icons
  - [ ] temple icon
  - [ ] upgrades nur wenn power
  - [ ] heli & jet start/lande sounds
  - [ ] russian voices?

## Design / Konzept

- [ ] Techtree & Buildorder
  file:///Users/moritzgiuliani/Documents/openRA%20Projekte/TiberianDawnHD/mods/management/kanban-board.html
  - [x] chrome webinterface
  - [ ] techtree web tool
- [ ] Mod-Konzept festlegen (Setting, Fraktionen, USP)
- [ ] Zielplattformen / Engine-Version festlegen
- [ ] Referenz-Mods / Inspiration sichten
- [ ] Wirtschaftssystem festlegen
- [ ] Fraktionen & ihre Identität definieren

## Rules / Balancing

- [ ] rules.yaml Grundstruktur anlegen
  Webinterface um alle Einheiten, Gebäude und Waffenwerte zu verändern
- [ ] NOD Units balancing
- [ ] GDI Units Balancing
- [ ] NOD Building Balancing
- [ ] GDI Building Balancing

## Maps

- [ ] ☑️ Zoomap
  Erste Testmap (klein, 2 Spieler) bauen
  - [x] 2 startpunkte
  - [x] 2 ore mines
  - [x] 2 tiberium felder
  - [x] wasser
- [ ] Map-Pool für Releases definieren

## Programmierung

- [ ] Mod-Ordnerstruktur aufsetzen
  (mod.yaml, manifest)
  - [x] eigene github connection
  - [x] openra github experimental
  - [ ] code-struktur & comments sauber
- [ ] ☑️ Red Alert Remastered Assets
- [ ] ☑️ Tiberian Sun Vox-Assets
- [ ] Naval Warfare & Amphibic
  - [x] Gunboat / Patrol Boat / Destroyer
  - [x] Submarine / Cargo Submarine
  - [x] Missile Sub / Drone Attack Sub / Mine Layer Sub
  - [x] Cruiser / Heli Carrier / Advanced Cruiser
  - [x] Transport Vessel -> Hovercraft
  - [x] Transport Vessel -> Armed
  - [ ] Hover MLRS / Hover AA
  - [ ] Amphibious APC
- [ ] Infantry & Special Infantry
  - [ ] Nod Cyborg / Reaper
  - [ ] Nod Cyborg Command
  - [ ] Nod Rifle
  - [ ] Nod Grenade
  - [ ] Nod Saboteur
  - [ ] Nod Highjacker
  - [ ] Nod Dog
  - [ ] Nod Flame Thrower / Toxic
- [ ] Aircrafts
  - [x] Apache / Orca
  - [x] HIND / Harpy
  - [ ] Transport Helo / Carryall
  - [ ] A10 / Firehawk
  - [ ] MiG? / Banshee
- [ ] Stealth area generator & sensors
- [ ] Shroud / Gap Generation
- [ ] Carryall Pickup
- [ ] Coop unit-sharing
- [ ] Standalone Release

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

