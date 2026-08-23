# === Age of Tiberium (aotmod) - Actor-Texte ===

tileset-aot-temperat = AoT - Temperate (RA)
tileset-aot-arctic = AoT - Arctic (TD/RA)
tileset-aot-desert = AoT - Desert (TD)

actor-lite =
    .name = Vehicle Factory
    .description = Basic vehicle production facility. Requires only a Power Plant.

actor-aot-ore-mine =
    .name = Ore Mine
    .generic-name = Ore Mine

actor-aot-nod-radar =
    .name = Radar Dome
    .description = Provides radar and map overview for NOD.
    Requires power to operate.

actor-oret =
    .name = Ore Transporter
    .generic-name = Ore Transporter
    .description = Collects ore from mines and delivers it to the refinery.

actor-atec =
    .name = Tech Centre
    .description = Advanced research facility. Enables Tier 3 technologies and Satellite Surveillance.

# Nuclear Strike moved off TMPL onto the Missile Silo (Atom Bomb upgrade) — see [[nuke-silo-system]].
actor-tmpl =
    .name = Temple of Nod
    .description =
    Unlocks Stealth Tank, Chem. Warrior and Obelisk of Light.
    Requires power to operate.

actor-ttnk =
    .name = Heavy Tank
    .description = NOD main battle tank with a dual-fire cannon.
    .encyclopedia = Heavy Tank

actor-ttnk-flame =
    .name = Flame Tank

actor-ttnk-toxin =
    .name = Toxin Tank

actor-ttnk-cloak =
    .name = Stealth Flame Tank

actor-aot-ttnk-devil =
    .name = Devil's Tongue

actor-aot-ttnk-devil-toxin =
    .name = Devil's Tongue (Toxin)

actor-aot-subapc =
    .name = Subterranean APC

actor-aot-repv =
    .name = Mobile Repair Drone

actor-aot-sub =
    .name = Attack Submarine
    .description = Stealthy torpedo submarine. Auto-submerges after surfacing. Surfaces when damaged critically.

actor-aot-msub =
    .name = Missile Sub
    .description = Wolf-class missile submarine. Fires guided missiles at ground, naval and air targets. Surfaces to fire.

actor-aot-msub-mine =
    .name = Mine Layer Sub

actor-aot-seamine =
    .name = Sea Mine

actor-aot-upgrade-msub-mine =
    .name = Mine Layer Upgrade
    .description = Equips the Missile Sub with a sea mine deployment system. Sub can lay up to 5 mines and rearm at the Sub Pen. Mines are cloaked to enemies. After upgrade, the sub fires 1 missile instead of 2.

actor-aot-upgrade-stnk-cargo =
    .name = Stealth Tank Cargo Upgrade
    .description = Equips all Stealth Tanks with 5 infantry transport slots, including tanks already built.

actor-aot-msub-drone =
    .name = Drone Attack Sub

actor-aot-msub-drone-unit =
    .name = Attack Drone

actor-aot-upgrade-msub-drone =
    .name = Drone Attack Upgrade
    .description = Replaces the Missile Sub's ammobay with an attack drone hangar. Sub loses missiles but launches attack drones.

actor-weap =
    .name = Advanced Factory
    .description = Produces advanced vehicles.
    .encyclopedia =
    Produces advanced vehicles for GDI. GDI vehicles tend to be slow but hard-hitting. Produce harvesters at the start to jumpstart your economy.

actor-aot-transport =
    .name = Transport Vessel
    .description = Naval transport. Carries up to 5 infantry or 1 vehicle. Can beach on shore to deploy troops.

actor-aot-transport-gun =
    .name = Armed Transport Vessel
    .description = Naval transport with machine gun turret. Carries up to 5 infantry or 1 vehicle.

actor-aot-upgrade-transport-gun =
    .name = Vessel Gun Upgrade
    .description = Equips the Transport Vessel with a machine gun turret for self-defense.

actor-aot-transport-hover =
    .name = Hovercraft Transport
    .description = Amphibious hovercraft. Carries up to 5 infantry or 1 light vehicle. Full speed on water, reduced speed on land.

actor-aot-upgrade-transport-hover =
    .name = Hovercraft Upgrade
    .description = Converts the Transport Vessel into an amphibious hovercraft. Can cross any terrain. Carries infantry or vehicles.

actor-aot-tran-carryall =
    .name = Carryall
    .description =
    Heavy lift helicopter. Flies to a ground vehicle, picks it up,
    and delivers it to any location.
      Carries 1 vehicle (hung below)
      Cannot transport infantry

actor-tran-gunship =
    .name = Chinook Gunship

actor-aot-upgrade-tran-doorgunner =
    .name = Transport Helo Doorgunner
    .description =
    Equips all Transport Helicopters with door gunners.
    Garrisoned infantry can fire through the side doors.
      Mutually exclusive with Carryall Upgrade

actor-aot-upgrade-tran-carryall =
    .name = Carryall Upgrade
    .description =
    Converts all Transport Helicopters into Carryalls.
    Can pick up and carry any ground vehicle.
      Disables infantry transport
      Mutually exclusive with Doorgunner Upgrade

actor-aot-upgrade-tran-parachute =
    .name = Parachute Bay Upgrade
    .description =
    Equips all NOD Transport Helicopters with parachute bays.
    Passengers can be dropped from altitude via parachute.
      Deploy: drops passengers at current position
      Alt+click: flies to target, drops passengers, returns to origin

actor-hq =
    .name = Radar
    .description = Provides radar and map overview for GDI.
    Requires power to operate.
    .airstrikepower-name = Air Strike
    .airstrikepower-description = Deploy an aerial napalm strike.
    Burns buildings and infantry along a line.
    .encyclopedia =
    Grants GDI access to the minimap. Requires power to operate.

actor-eye =
    .name = Radar
    .description = Provides radar and map overview for GDI.
    Requires power to operate.
    .ioncannonpower-name = Ion Cannon
    .ioncannonpower-description = Initiates an Ion Cannon strike.
    Applies instant damage to a small area.
    .encyclopedia =
    Grants GDI access to the minimap. Requires power to operate.

actor-proc =
    .name = Tiberium Refinery
    .description =
    Processes raw Tiberium into usable resources.
    Spies can infiltrate to steal 50% of stored credits.

actor-silo =
    .name = Silo
    .description =
    Resource storage and ore-delivery dock

actor-aot-v2-base =
    .name = V2 Rocket Launcher
    .description =
    Long-range rocket artillery. Fires a single ballistic missile.
    Effective vs. all ground targets.

actor-aot-v2-napalm =
    .name = Napalm Rocket Launcher
    .description =
    Napalm-upgraded V2. Fires two rockets in rapid succession.
    Highly effective vs. infantry.

actor-aot-v2-toxic =
    .name = Toxic Rocket Launcher
    .description =
    Toxic-upgraded V2. Fires two toxic rockets that leave a
    lingering tiberium gas field at the impact site.

actor-aot-upgrade-v2-napalm =
    .name = Napalm Rocket Launcher Upgrade
    .description =
    Upgrades the V2 Rocket Launcher with dual napalm warheads.
    Requires Age 1, Airstrip and Temple of Nod.
    Mutually exclusive with the Howitzer Upgrade.

actor-aot-upgrade-v2-howitzer =
    .name = Howitzer Upgrade
    .description =
    Replaces the V2 Rocket Launcher with the Mobile Howitzer.
    Medium-range direct-fire artillery, effective vs. armor and structures.
    Requires Age 1, Airstrip and Temple of Nod.
    Mutually exclusive with the Napalm Rocket Launcher Upgrade.

actor-aot-arty-howitzer =
    .name = Mobile Howitzer

actor-aot-arty-heavy =
    .name = Heavy Artillery
    .description =
    Heavy siege artillery. Deploy to fire. Devastating vs. structures and armor.

actor-aot-arty-heavy-deployed =
    .name = Heavy Artillery (Deployed)

actor-aot-upgrade-heavy-artillery =
    .name = Heavy Artillery Upgrade
    .description =
    Upgrades the Mobile Howitzer with heavy siege artillery.
    Adds deploy capability and devastating firepower vs. structures.
    Requires Age 3 and Howitzer Upgrade.

actor-aot-iron-dome =
    .name = Iron Dome Experimental Installation
    .description = Iron Dome

notification-reinforcements-ready = Reinforcements ready.

actor-aot-forward-hq =
    .name = Forward HQ
    .description = Paratrooper Drop

actor-aot-tesla =
    .name = Tesla Coil

actor-aot-hospital =
    .name = Civilian Hospital
    .description = Care under Fire

        Emergency treatment for all troops on the field

actor-aot-ts-ctaray =
    .name = Civilian Array
    .description = Ion Storm Control

        Deploys 5 battle-hardened Gunners and 3 Rocket Troopers behind enemy lines via plane.

        All troops arrive at veteran rank.

meta-ant-generic-name = Ant

actor-aot-ant =
    .name = Worker Ant

actor-aot-fireant =
    .name = Fire Ant

actor-aot-scoutant =
    .name = Scout Ant

actor-aot-critter-spawner =
    .name = Critter Spawner

actor-aot-expansion-marker =
    .name = AI Expansion Marker

actor-aot-ant-nest =
    .name = Ant Nest

actor-aot-light-system =
    .name = Light System (Day/Night Cycle)

actor-aot-foundation =
    .name = Fortified Foundation
    .description = Reinforced foundation plating. Prevents subterranean units from burrowing in or surfacing on it. Cannot be destroyed or sold.

actor-aot-upgrade-foundation =
    .description = Expands the Fortified Foundation from a 2x2 to a 3x3 plate at the same build cost.
