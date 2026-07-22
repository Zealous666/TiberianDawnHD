#region Copyright & License Information
/*
 * Age of Tiberium mod addition.
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public static class AotOpsUtils
	{
		public static IEnumerable<CPos> Ring(CPos centre, int r)
		{
			if (r == 0)
			{
				yield return centre;
				yield break;
			}

			for (var x = -r; x <= r; x++)
			{
				yield return new CPos(centre.X + x, centre.Y - r);
				yield return new CPos(centre.X + x, centre.Y + r);
			}

			for (var y = -r + 1; y <= r - 1; y++)
			{
				yield return new CPos(centre.X - r, centre.Y + y);
				yield return new CPos(centre.X + r, centre.Y + y);
			}
		}

		public static bool IsPreferredEnemyUnit(Player player, Actor a)
		{
			if (a == null || a.IsDead || !a.IsInWorld
				|| player.RelationshipWith(a.Owner) != PlayerRelationship.Enemy
				|| a.Info.HasTraitInfo<HuskInfo>())
				return false;

			var targetTypes = a.GetEnabledTargetTypes();
			if (targetTypes.IsEmpty || targetTypes.Contains("Air"))
				return false;

			var hasModifier = false;
			foreach (var v in a.TraitsImplementing<IVisibilityModifier>())
			{
				if (v.IsVisible(a, player))
					return true;
				hasModifier = true;
			}

			return !hasModifier;
		}
	}

	public sealed class AotProductionRequest
	{
		public AotMission Mission;
		public string Role;
		public string[] Chain = [];
		public int Remaining;
		public int Ordered;
		public int LastOrderTick;
	}

	// Reported by wave/air-raid missions on completion so AotOperationsBotModule can drive the
	// escalation ladder (secondary route after N failures, air raid after more). Unknown = the
	// mission never really got going (e.g. nothing buildable) and shouldn't move the streak either way.
	public enum AotMissionOutcome { Unknown, Success, Failure }

	public abstract class AotMission
	{
		protected readonly AotOperationsBotModule Ops;
		public readonly HashSet<Actor> Units = [];
		public readonly string Name;
		public bool Done { get; protected set; }
		public AotMissionOutcome Outcome { get; protected set; } = AotMissionOutcome.Unknown;

		protected AotMission(AotOperationsBotModule ops, string name)
		{
			Ops = ops;
			Name = name;
		}

		public abstract void Tick(IBot bot);

		public virtual void OnUnitAssigned(Actor a) { Units.Add(a); }

		protected void Log(string message)
		{
			OpenRA.Log.Write("debug", $"[AotOps][{Ops.Player.PlayerName}][{Name}] {message}");
		}

		protected void Finish()
		{
			Done = true;
			Ops.ReleaseToPool(this, Units.ToList());
			Units.Clear();
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Age of Tiberium: mission-based army operations framework. Owns all ground combat",
		"units exclusively (replaces SquadManagerBotModule), produces mission compositions",
		"itself and runs the operation types as data-driven missions with a shared lifecycle.",
		"One instance per faction (Faction filter), like AotBaseBuilderBotModule.")]
	public class AotOperationsBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Only act for players of this faction (internal name).")]
		public readonly string Faction = null;

		[ActorReference]
		[Desc("Actor types that are considered construction yards.")]
		public readonly HashSet<string> ConstructionYardTypes = [];

		[ActorReference]
		[Desc("Actor types never claimed for missions (economy, MCVs, special vehicles).")]
		public readonly HashSet<string> ExcludeFromOpsTypes = [];

		// ---- Feature flags (one per operation type, individually testable) ----
		public readonly bool EnableStartingUnits = true;
		public readonly bool EnableWaves = true;
		public readonly bool EnableScouts = true;
		public readonly bool EnableDerricks = true;

		// ---- Module 1: Starting Unit Operations (ported approved choke behaviour) ----
		[Desc("Ground units held as stationary reserve at the chokepoint (approved behaviour).")]
		public readonly int ChokepointReserveSize = 4;
		public readonly int ChokepointHoldRadius = 4;
		public readonly int ChokeClearRadius = 5;

		[ActorReference]
		[Desc("Actor types excluded from chokepoint obstacle clearing.")]
		public readonly HashSet<string> ChokeClearExcludeTypes = [];

		[Desc("Maximum ARCO raid targets after the choke is cleared.")]
		public readonly int ArcoMaxTargets = 2;

		// ---- Module 2: Regular Attack Waves ----
		[ActorReference]
		[Desc("Tank role variant chain. Must contain ALL exclusive upgrade branches;",
			"the first currently buildable variant wins.")]
		public readonly string[] WaveTankTypes = [];

		[ActorReference]
		[Desc("Light vehicle role variant chain (all exclusive branches).")]
		public readonly string[] WaveLightTypes = [];

		[ActorReference]
		[Desc("Support/artillery role variant chain (all exclusive branches).")]
		public readonly string[] WaveSupportTypes = [];

		[Desc("Role shares in percent (tank, light, support).")]
		public readonly int WaveTankShare = 60;
		public readonly int WaveLightShare = 25;
		public readonly int WaveSupportShare = 15;

		[Desc("Base vehicles per wave for age tier 0, 1, 2, 3.")]
		public readonly int[] WaveVehiclesPerAge = [6, 8, 10, 12];

		[Desc("Prerequisites that mark age tiers 1-3.")]
		public readonly string[] AgePrerequisites = ["aot-age1", "aot-age2", "aot-age3"];

		[Desc("Budget escalation per successive wave, percent.")]
		public readonly int WaveBudgetEscalationPercent = 25;

		[Desc("Budget cap as percent of the tier base budget.")]
		public readonly int WaveBudgetCapPercent = 300;

		[Desc("Hard cap on wave unit count.")]
		public readonly int WaveMaxUnits = 20;

		[Desc("Share of the wave chosen adaptively against the observed enemy army, percent.")]
		public readonly int WaveAdaptiveSharePercent = 25;

		[ActorReference]
		[Desc("Adaptive counter chains. Fall back to the role chains when empty.")]
		public readonly string[] AntiInfantryTypes = [];

		[ActorReference]
		public readonly string[] AntiTankTypes = [];

		[ActorReference]
		public readonly string[] AntiAirTypes = [];

		[Desc("Percent of waves that target economy/outposts instead of the main base.")]
		public readonly int WaveEcoTargetPercent = 25;

		[Desc("Loss percent (by unit count) at which a wave retreats. 0 = fight to the death.")]
		public readonly int WaveRetreatLossPercent = 0;

		[Desc("Ticks between waves (after the previous wave ended).")]
		public readonly int WaveCooldown = 1500;

		[Desc("Delay before the first wave is formed.")]
		public readonly int WaveInitialDelay = 3000;

		[Desc("Forming timeout; the wave launches with what it has (if at least half).")]
		public readonly int WaveFormingTimeout = 4500;

		[Desc("Executing-phase stall safety net: if a launched wave neither wipes out, retreats nor",
			"reaches/loses its target within this many ticks (e.g. stuck on an unreachable waypoint",
			"or unclearable obstacle), it is abandoned as a failure so the escalation ladder and the",
			"wave scheduler are never blocked indefinitely by a single stuck wave (User 2026-07-22).")]
		public readonly int WaveExecutingTimeout = 9000;

		[Desc("Consecutive FAILED waves (wiped out, GDI retreat, or stall timeout) at the primary route",
			"before the next wave routes via a secondary approach instead (User 2026-07-22, reduced",
			"from 2). If no distinct secondary approach exists, the wave still launches via the",
			"primary route.")]
		public readonly int WaveSecondaryRouteAfterFailures = 1;

		[Desc("Consecutive failed waves (primary + secondary route attempts) before switching to an",
			"air raid instead of another ground wave (User 2026-07-22, reduced from 3). Only triggers",
			"once HelipadTypes exists; while it doesn't, ground waves keep escalating on the same",
			"counter. Once the first air raid has fired, every later escalation decision (4th attempt",
			"onward) is instead chosen at random each time -- primary choke, secondary choke, or",
			"another air raid -- rather than following this fixed ladder again (User 2026-07-22).")]
		public readonly int WaveAirRaidAfterFailures = 2;

		[ActorReference]
		[Desc("Helipad actor types. The air-raid escalation tier only triggers once one is owned.")]
		public readonly HashSet<string> HelipadTypes = [];

		[ActorReference]
		[Desc("Repair facility (FIX) actor types. A retreating wave sends damaged survivors here to",
			"repair before releasing them back to the pool (User 2026-07-22). No effect if empty",
			"or none owned/alive.")]
		public readonly HashSet<string> RepairTypes = [];

		[ActorReference]
		[Desc("Attack helicopter variant chain (all exclusive branches) used for the air-raid",
			"escalation tier.")]
		public readonly string[] AirRaidHelicopterTypes = [];

		[Desc("Number of helicopters built for an air raid.")]
		public readonly int AirRaidCount = 4;

		[Desc("Air raid forming timeout; launches short-handed if hit.")]
		public readonly int AirRaidFormingTimeout = 9000;

		[Desc("Air raid executing-phase stall safety net; same purpose as WaveExecutingTimeout.")]
		public readonly int AirRaidExecutingTimeout = 9000;

		[ActorReference]
		[Desc("Shipyard/Sub Pen actor types. A wave only switches to naval ferrying once one of",
			"these is owned and alive.")]
		public readonly HashSet<string> NavalProductionTypes = [];

		[ActorReference]
		[Desc("Transport Vessel / Hovercraft variant chain (all exclusive branches) used to ferry",
			"a wave across water when no ground route to the enemy exists.")]
		public readonly string[] FerryTypes = [];

		[Desc("Number of transports built (and reused across waves) to ferry a wave across water.")]
		public readonly int FerryCount = 2;

		[Desc("Radius in cells to search for a coastal embark/landing cell around the reference point.")]
		public readonly int FerrySearchRadius = 14;

		[Desc("Ferry phase timeout; proceeds with whoever made it ashore, or cancels the wave if",
			"nobody did.")]
		public readonly int FerryTimeout = 9000;

		// ---- Module 3: Scout Expeditions ----
		[ActorReference]
		[Desc("Scout role A variant chain (all exclusive branches). When roles B/C are empty,",
			"this role alone is duplicated ScoutGroupSize times (homogeneous vehicle group).",
			"When B and/or C are set, ScoutRoleACount/B/C units of each defined role form the",
			"group instead (mixed composition, e.g. an early-tier infantry scout squad).",
			"All roles in a mixed squad MUST have the same move speed -- mixed speeds make the",
			"group spread out badly, since they still path and move together as one unit.")]
		public readonly string[] ScoutTypes = [];

		[ActorReference]
		[Desc("Scout role B variant chain. Leave empty for a homogeneous ScoutTypes group.")]
		public readonly string[] ScoutRoleBTypes = [];

		[ActorReference]
		[Desc("Scout role C variant chain. Leave empty for a homogeneous ScoutTypes group.")]
		public readonly string[] ScoutRoleCTypes = [];

		[Desc("Group size for a homogeneous ScoutTypes group (Role B/C empty).")]
		public readonly int ScoutGroupSize = 2;

		[Desc("Role A unit count when Role B and/or C are set (mixed composition).")]
		public readonly int ScoutRoleACount = 1;

		[Desc("Role B unit count when set.")]
		public readonly int ScoutRoleBCount = 1;

		[Desc("Role C unit count when set.")]
		public readonly int ScoutRoleCCount = 1;

		[Desc("Waypoint ring radius around scouted spawns.")]
		public readonly int ScoutRingRadius = 8;

		[ActorReference]
		[Desc("Radar/HQ actor types. Scout expeditions only start once one of these is owned",
			"and alive (the scout mission's whole purpose is feeding the radar/minimap).")]
		public readonly HashSet<string> RadarTypes = [];

		[ActorReference]
		[Desc("Infantry-producing building types (Barracks/Hand). Without a rally point these have",
			"multiple Exit cells and the engine picks one at RANDOM per unit, scattering freshly",
			"produced infantry across the base. A fixed rally point near the building keeps every",
			"exit consistent and holds new units there until a mission claims them.")]
		public readonly HashSet<string> InfantryRallyTypes = [];

		[Desc("Ticks between checks for infantry-producing buildings that still need a rally point.")]
		public readonly int RallyCheckInterval = 50;

		// ---- Module 4: Derrick Engineer Squads ----
		[ActorReference]
		public readonly string[] EngineerTypes = [];

		[ActorReference]
		public readonly string[] RocketInfantryTypes = [];

		[ActorReference]
		public readonly string[] MgInfantryTypes = [];

		[Desc("Ticks between scans for uncontrolled derricks (map-wide, 10 min).")]
		public readonly int DerrickCheckInterval = 15000;

		[Desc("Maximum number of derricks to capture and hold at the same time.",
			"This caps derrick TARGETS, not squads: a captured derrick keeps its slot",
			"(permanent guard) until it is lost, so at most this many derricks are ever",
			"pursued or held simultaneously.")]
		public readonly int DerrickMaxTargets = 2;

		[Desc("Guard leash radius around held positions (derricks, posts).")]
		public readonly int GuardLeashRadius = 6;

		[Desc("Last-resort timeout before a derrick squad departs short-handed. Kept generous:",
			"EngineerTypes/RocketInfantryTypes/MgInfantryTypes can share an actor type with",
			"other missions (e.g. Scout), which can genuinely delay production well past a",
			"short timeout while the shared production queue works through other requests first.")]
		public readonly int DerrickFormingTimeout = 9000;

		// ---- Module 5: Base Defense (User 2026-07-22) ----
		public readonly bool EnableBaseDefense = true;

		[Desc("Minimum garrison size guaranteed via dedicated production (the floor). The rest of",
			"the garrison, up to ProtectionTargetIsWaveSize, is opportunistically filled from the",
			"shared unit pool (idle survivors from finished missions) at no extra production cost.")]
		public readonly int ProtectionMinProduced = 3;

		[ActorReference]
		[Desc("Variant chain used for the guaranteed production floor (fast, cheap responders).",
			"Falls back to WaveLightTypes when empty.")]
		public readonly string[] ProtectionFloorTypes = [];

		[ActorReference]
		[Desc("Buildings the garrison watches over. Empty = every owned building.")]
		public readonly HashSet<string> ProtectionTypes = [];

		[Desc("Radius (cells) around each protected building that is scanned for enemies.")]
		public readonly int ProtectionScanRadius = 10;

		[Desc("Ticks between threat scans.")]
		public readonly int ProtectionScanInterval = 250;

		[Desc("Responders sent per detected enemy (rounded up), so a lone scout doesn't empty the",
			"whole garrison. Always at least ProtectionMinResponse, never more than available.")]
		public readonly int ProtectionResponseRatio = 2;

		[Desc("Minimum responders dispatched once any threat is detected at all.")]
		public readonly int ProtectionMinResponse = 2;

		// ---- Production ----
		[Desc("Only order production above this cash level.")]
		public readonly int ProductionMinCash = 300;

		[Desc("Ticks between production pump runs.")]
		public readonly int ProductionInterval = 30;

		[Desc("Ticks between Starport bulk delivery flushes.")]
		public readonly int StarportFlushInterval = 250;

		[Desc("Ticks between mission ticks.")]
		public readonly int MissionInterval = 25;

		public override object Create(ActorInitializer init) { return new AotOperationsBotModule(init.Self, this); }
	}

	public class AotOperationsBotModule : ConditionalTrait<AotOperationsBotModuleInfo>, IBotTick, INotifyActorDisposing
	{
		public readonly World World;
		public readonly Player Player;
		public AotMapIntelBotModule Intel { get; private set; }
		public IBotChokepointProvider ChokeProvider { get; private set; }
		public IBotBaseApproachProvider ApproachProvider { get; private set; }

		public readonly List<AotMission> Missions = [];
		readonly Dictionary<Actor, AotMission> owned = [];
		readonly List<Actor> pool = [];
		readonly List<AotProductionRequest> requests = [];
		readonly HashSet<Actor> knownUnits = [];
		readonly Predicate<Actor> unitCannotBeOrdered;
		readonly ActorIndex.NamesAndTrait<BuildingInfo> constructionYards;

		PlayerResources playerResources;
		TechTree techTree;

		bool initialClaimDone;
		int waveIndex;
		int waveCooldownTicks;
		int waveFailureStreak;

		// Once the fixed 3-attempt ladder (primary -> secondary -> air raid) has fired its first
		// air raid, every later escalation decision is instead chosen at random each time (User
		// 2026-07-22) -- this flips permanently and never reverts to the deterministic ladder.
		bool randomEscalationPhase;

		int derrickTicks;
		int productionTicks;
		int starportTicks;
		int missionTicks;
		int rallyCheckTicks;
		readonly HashSet<int> scoutGroupsLaunched = [];
		readonly Dictionary<int, List<CPos>> scoutAssignments = [];

		public AotOperationsBotModule(Actor self, AotOperationsBotModuleInfo info)
			: base(info)
		{
			World = self.World;
			Player = self.Owner;
			unitCannotBeOrdered = a => a == null || a.Owner != Player || a.IsDead || !a.IsInWorld;
			constructionYards = new ActorIndex.NamesAndTrait<BuildingInfo>(World, info.ConstructionYardTypes);
		}

		protected override void Created(Actor self)
		{
			Intel = self.Owner.PlayerActor.TraitsImplementing<AotMapIntelBotModule>().FirstOrDefault();
			ChokeProvider = self.Owner.PlayerActor.TraitsImplementing<IBotChokepointProvider>().FirstOrDefault();
			ApproachProvider = self.Owner.PlayerActor.TraitsImplementing<IBotBaseApproachProvider>().FirstOrDefault();
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
			techTree = self.Owner.PlayerActor.Trait<TechTree>();
		}

		protected override void TraitEnabled(Actor self)
		{
			waveCooldownTicks = Info.WaveInitialDelay + World.LocalRandom.Next(0, 200);
			derrickTicks = 1500 + World.LocalRandom.Next(0, 200);
			productionTicks = World.LocalRandom.Next(0, Info.ProductionInterval);
			starportTicks = World.LocalRandom.Next(0, Info.StarportFlushInterval);
			missionTicks = World.LocalRandom.Next(0, Info.MissionInterval);
			rallyCheckTicks = World.LocalRandom.Next(0, Info.RallyCheckInterval);
		}

		public CPos BaseCentre()
		{
			var yard = constructionYards.Actors.FirstOrDefault(a => a.Owner == Player && !a.IsDead && a.IsInWorld);
			return yard?.Location ?? (Intel?.Ready == true ? Intel.BaseCentre : CPos.Zero);
		}

		public int AgeTier()
		{
			for (var i = Info.AgePrerequisites.Length - 1; i >= 0; i--)
				if (techTree.HasPrerequisites([Info.AgePrerequisites[i]]))
					return i + 1;

			return 0;
		}

		public bool HasNavalProduction() =>
			World.Actors.Any(a => a.Owner == Player && !a.IsDead && a.IsInWorld && Info.NavalProductionTypes.Contains(a.Info.Name));

		public bool HasRadar() =>
			World.Actors.Any(a => a.Owner == Player && !a.IsDead && a.IsInWorld && Info.RadarTypes.Contains(a.Info.Name));

		public bool HasHelipad() =>
			World.Actors.Any(a => a.Owner == Player && !a.IsDead && a.IsInWorld && Info.HelipadTypes.Contains(a.Info.Name));

		public Actor NearestOwnRepairFacility(CPos near) =>
			World.Actors
				.Where(a => a.Owner == Player && !a.IsDead && a.IsInWorld && Info.RepairTypes.Contains(a.Info.Name))
				.MinByOrDefault(a => (a.Location - near).LengthSquared);

		// Halfway between base and primary choke: out of the builders' way, on the way out. Shared
		// (User 2026-07-22) by both regular wave staging and the base defense garrison muster point.
		public CPos GarrisonMusterPoint()
		{
			var baseCentre = BaseCentre();
			var choke = ChokeProvider?.Chokepoint;
			if (!choke.HasValue)
				return baseCentre;

			return new CPos((baseCentre.X + choke.Value.X) / 2, (baseCentre.Y + choke.Value.Y) / 2);
		}

		// ROOT CAUSE FOUND (debug.log evidence): stock BaseBuilderBotModule (still active on @aot for
		// its PauseUnitProduction economy service) has its OWN rally-point assignment
		// (AssignRallyPointsInterval) that re-validates every RallyPoint-trait actor the player owns
		// via IsRallyPointValid -- which calls world.IsCellBuildable on the rally cell. A cell right
		// at the production apron/smudge (exactly where we want it) essentially NEVER passes that
		// check, so BaseBuilderBotModule perpetually considered our rally point "invalid" and
		// overwrote it with its own (distant) ChooseRallyLocationNear location every cycle.
		// Real fix: BaseBuilderBotModule.cs patched with a RallyPointExcludeTypes list
		// (RallyPointExcludeTypes: InfantryRallyTypes in aot-ai.yaml) so it never touches HAND/PYLE
		// at all. With that conflict gone, we set the engine RallyPoint ourselves (once per building)
		// so fresh exits walk straight to the smudge -- and additionally cache the cell ourselves so
		// TickForming's active gathering (pool-reused stragglers, multiple missions) doesn't depend
		// on re-reading the (still generally reassignable-by-us-only) RallyPoint.Path.
		readonly Dictionary<Actor, CPos> infantryRallyCells = [];
		readonly HashSet<Actor> rallySet = [];

		void EnsureInfantryRallyPoints(IBot bot)
		{
			if (Info.InfantryRallyTypes.Count == 0)
				return;

			foreach (var stale in infantryRallyCells.Keys.Where(a => unitCannotBeOrdered(a)).ToList())
				infantryRallyCells.Remove(stale);
			rallySet.RemoveWhere(a => unitCannotBeOrdered(a));

			foreach (var building in World.Actors)
			{
				if (building.Owner != Player || building.IsDead || !building.IsInWorld
					|| !Info.InfantryRallyTypes.Contains(building.Info.Name))
					continue;

				if (!infantryRallyCells.ContainsKey(building))
				{
					var cell = FindRallyCellNear(building);
					if (cell != null)
					{
						infantryRallyCells[building] = cell.Value;
						Log($"[AotRally] cell for {building.Info.Name}@{building.Location}#{building.ActorID} -> {cell.Value}");
					}
				}

				if (infantryRallyCells.TryGetValue(building, out var rallyCell) && rallySet.Add(building))
				{
					bot.QueueOrder(new Order("SetRallyPoint", building, Target.FromCell(World, rallyCell), false));
					Log($"[AotRally] QUEUED SetRallyPoint for {building.Info.Name}@{building.Location}#{building.ActorID} -> {rallyCell}");
				}
			}
		}

		// Use the building's own highest-priority Exit cell -- verified against the actually-loaded
		// ruleset (engine/mods/cnc/rules/structures.yaml): HAND's Exit@1 has an EXPLICIT Priority:2,
		// the inherited Exit@fallback1 has none (defaults to 1) -> NOT a tie, RandomExitOrDefault
		// always picks Exit@1 deterministically even without any rally point. Reading the live
		// Exit trait also automatically reflects the mod's aot-structures.yaml ExitCell override
		// (which merges into the base Priority, not replacing it), landing exactly on the "=="
		// (OccupiedPassable) footprint row -- the actual smudge -- without us having to duplicate
		// that footprint-row math ourselves. Geometry (Dimensions.Y - 1 = the smudge row, NOT
		// Dimensions.Y) is only a defensive fallback for a building with no Exit trait at all.
		CPos? FindRallyCellNear(Actor building)
		{
			var exit = building.TraitsImplementing<Exit>()
				.Where(e => !e.IsTraitDisabled)
				.OrderByDescending(e => e.Info.Priority)
				.FirstOrDefault();

			CPos candidate;
			if (exit != null)
				candidate = building.Location + exit.Info.ExitCell;
			else
			{
				var dims = building.Info.TraitInfoOrDefault<BuildingInfo>()?.Dimensions ?? new CVec(1, 1);
				candidate = building.Location + new CVec(dims.X / 2, dims.Y - 1);
			}

			if (Intel.IsPassable(candidate))
				return candidate;

			return null;
		}

		// The rally cell of the first infantry-producing building found -- OUR OWN computed cell
		// (infantryRallyCells), never the engine RallyPoint.Path (see EnsureInfantryRallyPoints for
		// why: BaseBuilderBotModule fights over that). Missions use this to actively gather ALL their
		// units there during Forming -- not just freshly produced ones, but also POOL-REUSED units,
		// which can be standing anywhere on the map wherever an earlier mission left them.
		public CPos? PrimaryInfantryRallyCell()
		{
			foreach (var building in World.Actors)
			{
				if (building.Owner != Player || building.IsDead || !building.IsInWorld
					|| !Info.InfantryRallyTypes.Contains(building.Info.Name))
					continue;

				if (infantryRallyCells.TryGetValue(building, out var cell))
					return cell;

				return building.Location;
			}

			return null;
		}

		// Concurrent missions (e.g. 2 derrick squads + 2 scout groups at once) must NOT all target
		// the exact same rally cell -- a cell only fits ~5 infantry via subcell stacking, so with
		// more than that constantly waiting, the engine keeps bumping the overflow to a free
		// neighbour, and our own active gathering (Forming) kept sending them straight back to the
		// same single point every tick -- a permanent tug-of-war that looked like nobody ever
		// settling. Each mission gets its OWN nearby staging cell instead, allocated once and cached
		// by the mission, cycling through a small spiral so several missions stay close together
		// without competing for one cell.
		static readonly CVec[] StagingOffsets =
		[
			new(0, 0), new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
			new(1, 1), new(-1, 1), new(1, -1), new(-1, -1),
			new(2, 0), new(-2, 0), new(0, 2), new(0, -2),
		];

		int stagingCellCursor;

		public CPos? AllocateInfantryStagingCell()
		{
			var baseCell = PrimaryInfantryRallyCell();
			if (baseCell == null)
				return null;

			var offset = StagingOffsets[stagingCellCursor % StagingOffsets.Length];
			stagingCellCursor++;
			var cell = baseCell.Value + offset;
			return Intel.IsPassable(cell) ? cell : baseCell.Value;
		}

		public bool CannotOrder(Actor a) => unitCannotBeOrdered(a);

		void IBotTick.BotTick(IBot bot)
		{
			if (Info.Faction != null && Player.Faction.InternalName != Info.Faction)
				return;

			if (Intel == null || !Intel.Ready)
				return;

			CleanDead();

			if (!initialClaimDone)
			{
				InitialClaim(bot);
				initialClaimDone = true;

				if (Info.EnableBaseDefense)
					Missions.Add(new AotBaseDefenseMission(this));
			}

			ClaimNewUnits(bot);

			if (--productionTicks <= 0)
			{
				productionTicks = Info.ProductionInterval;
				PumpProduction(bot);
			}

			if (--starportTicks <= 0)
			{
				starportTicks = Info.StarportFlushInterval;
				FlushStarport(bot);
			}

			if (--rallyCheckTicks <= 0)
			{
				rallyCheckTicks = Info.RallyCheckInterval;
				EnsureInfantryRallyPoints(bot);
			}

			Schedule(bot);

			if (--missionTicks <= 0)
			{
				missionTicks = Info.MissionInterval;
				foreach (var m in Missions.ToList())
				{
					if (!m.Done)
						m.Tick(bot);

					if (m.Done)
					{
						// Drives the wave escalation ladder (secondary route, then air raid) --
						// only wave/air-raid missions report an Outcome; other mission types stay
						// Unknown and don't touch the streak.
						if (m is AotAirRaidMission)
						{
							Log($"escalation: air raid finished ({m.Outcome}) -> streak reset, further attacks now random (secondary/primary/air raid)");
							waveFailureStreak = 0;
						}
						else if (m.Outcome == AotMissionOutcome.Failure)
						{
							waveFailureStreak++;
							Log($"escalation: {m.Name} failed -> streak={waveFailureStreak}");
						}
						else if (m.Outcome == AotMissionOutcome.Success)
						{
							if (waveFailureStreak > 0)
								Log($"escalation: {m.Name} succeeded -> streak reset (was {waveFailureStreak})");
							waveFailureStreak = 0;
						}

						CancelRequests(m);
						Missions.Remove(m);
					}
				}
			}
		}

		void CleanDead()
		{
			foreach (var m in Missions)
				m.Units.RemoveWhere(unitCannotBeOrdered);
			pool.RemoveAll(unitCannotBeOrdered);
			knownUnits.RemoveWhere(unitCannotBeOrdered);
			foreach (var dead in owned.Keys.Where(a => unitCannotBeOrdered(a)).ToList())
				owned.Remove(dead);
		}

		bool IsEligibleCombatUnit(Actor a)
		{
			if (a.Owner != Player || a.IsDead || !a.IsInWorld)
				return false;

			if (Info.ExcludeFromOpsTypes.Contains(a.Info.Name))
				return false;

			// Ground/naval combat units: mobile, not a harvester. Aircraft (Mobile == null) are
			// claimable too -- AotAirRaidMission requests helicopters -- but only via a genuine
			// pending request; see the AircraftInfo branch below.
			if (a.Info.HasTraitInfo<HarvesterInfo>())
				return false;

			if (a.TraitOrDefault<Mobile>() == null && !a.Info.HasTraitInfo<AircraftInfo>())
				return false;

			return true;
		}

		void InitialClaim(IBot bot)
		{
			var starting = Info.EnableStartingUnits ? new AotStartingUnitsMission(this) : null;
			if (starting != null)
				Missions.Add(starting);

			foreach (var a in World.ActorsHavingTrait<IPositionable>().Where(IsEligibleCombatUnit).ToList())
			{
				knownUnits.Add(a);
				if (starting != null)
				{
					owned[a] = starting;
					starting.OnUnitAssigned(a);
				}
				else
					pool.Add(a);
			}

			Log($"initial claim: {(starting != null ? starting.Units.Count : pool.Count)} unit(s)");
		}

		void ClaimNewUnits(IBot bot)
		{
			foreach (var a in World.ActorsHavingTrait<IPositionable>()
				.Where(a => a.Owner == Player && !knownUnits.Contains(a)).ToList())
			{
				knownUnits.Add(a);
				if (!IsEligibleCombatUnit(a))
					continue;

				// Multiple concurrent missions can share an actor type (e.g. Scout's Role A and
				// Derrick's MG escort both use the same infantry). Prefer a request that actually
				// has a production order in flight (Ordered > 0) over one that's merely stalled
				// waiting its turn on a busy queue -- otherwise a stalled mission "steals" a unit
				// another mission's order actually paid for, and the stolen unit immediately walks
				// off toward the stalled mission's (unrelated) destination the moment it exits.
				var request = requests
					.Where(r => r.Remaining > 0 && r.Chain.Contains(a.Info.Name))
					.OrderByDescending(r => r.Ordered > 0)
					.FirstOrDefault();
				if (request != null)
				{
					request.Remaining--;
					if (request.Ordered > 0)
						request.Ordered--;
					owned[a] = request.Mission;
					request.Mission.OnUnitAssigned(a);
					Log($"claim {a.Info.Name}@{a.Location} -> {request.Mission.Name} ({request.Role}, {request.Remaining} open)");
				}
				else
				{
					pool.Add(a);
					Log($"claim {a.Info.Name}@{a.Location} -> pool ({pool.Count})");
				}
			}

			requests.RemoveAll(r => r.Remaining <= 0);
		}

		// ---- Production -----------------------------------------------------

		public void QueueRequest(AotMission mission, string role, string[] chain, int count)
		{
			if (count <= 0 || chain.Length == 0)
				return;

			requests.Add(new AotProductionRequest { Mission = mission, Role = role, Chain = chain, Remaining = count });
		}

		public void CancelRequests(AotMission mission)
		{
			requests.RemoveAll(r => r.Mission == mission);
		}

		public int OpenRequests(AotMission mission)
		{
			return requests.Where(r => r.Mission == mission).Sum(r => r.Remaining);
		}

		public string FirstBuildable(string[] chain)
		{
			var queuesByCategory = AIUtils.FindQueuesByCategory(Player);
			return FirstBuildable(chain, queuesByCategory).Name;
		}

		(string Name, ProductionQueue Queue) FirstBuildable(string[] chain, ILookup<string, ProductionQueue> queuesByCategory)
		{
			foreach (var name in chain)
			{
				if (!World.Map.Rules.Actors.TryGetValue(name, out var actorInfo))
					continue;

				var bi = actorInfo.TraitInfoOrDefault<BuildableInfo>();
				if (bi == null)
					continue;

				foreach (var category in bi.Queue)
				{
					foreach (var queue in queuesByCategory[category])
					{
						if (queue.BuildableItems().Any(i => i.Name == name))
							return (name, queue);
					}
				}
			}

			return (null, null);
		}

		void PumpProduction(IBot bot)
		{
			if (requests.Count == 0)
				return;

			if (playerResources.GetCashAndResources() < Info.ProductionMinCash)
				return;

			var queuesByCategory = AIUtils.FindQueuesByCategory(Player);
			var usedQueues = new HashSet<ProductionQueue>();

			foreach (var request in requests)
			{
				// Reconcile leaked orders: nothing of this chain is anywhere in production anymore.
				if (request.Ordered > 0 && World.WorldTick > request.LastOrderTick + 1500)
				{
					var stillProducing = queuesByCategory.SelectMany(g => g)
						.Any(q => q.AllQueued().Any(i => request.Chain.Contains(i.Item)));
					if (!stillProducing)
						request.Ordered = 0;
				}

				while (request.Ordered < request.Remaining)
				{
					var (name, queue) = FirstBuildable(request.Chain, queuesByCategory);
					if (name == null || queue == null || usedQueues.Contains(queue))
						break;

					// Keep regular queues at one outstanding item; bulk queues take a batch.
					if (queue is not BulkProductionQueue && queue.AllQueued().Any())
					{
						usedQueues.Add(queue);
						break;
					}

					bot.QueueOrder(Order.StartProduction(queue.Actor, name, 1));
					request.Ordered++;
					request.LastOrderTick = World.WorldTick;
					Log($"produce {name} for {request.Mission.Name} ({request.Role})");

					if (queue is not BulkProductionQueue)
					{
						usedQueues.Add(queue);
						break;
					}
				}
			}
		}

		void FlushStarport(IBot bot)
		{
			foreach (var queue in Player.PlayerActor.TraitsImplementing<BulkProductionQueue>())
			{
				if (queue.GetActorsReadyForDelivery().Count > 0 && !queue.HasDeliveryStarted())
				{
					bot.QueueOrder(new Order("PurchaseOrder", queue.Actor, false));
					Log("starport flush: delivery ordered");
				}
			}
		}

		// ---- Pool -----------------------------------------------------------

		public void ReleaseToPool(AotMission mission, List<Actor> units)
		{
			foreach (var a in units)
			{
				if (unitCannotBeOrdered(a))
					continue;
				owned.Remove(a);
				pool.Add(a);
			}
		}

		public List<Actor> TakeFromPool(string[] chain, int count)
		{
			var taken = pool.Where(a => chain.Contains(a.Info.Name)).Take(count).ToList();
			foreach (var a in taken)
				pool.Remove(a);
			return taken;
		}

		// Base Defense (User 2026-07-22): opportunistically adopts ANY idle pool unit regardless of
		// type, unlike TakeFromPool's chain filter -- the garrison isn't picky about composition,
		// it just wants bodies that are otherwise sitting around doing nothing.
		public List<Actor> TakeAnyFromPool(int count)
		{
			var taken = pool.Take(count).ToList();
			foreach (var a in taken)
				pool.Remove(a);
			return taken;
		}

		public void AssignFromPool(AotMission mission, List<Actor> units)
		{
			foreach (var a in units)
			{
				owned[a] = mission;
				mission.OnUnitAssigned(a);
			}
		}

		// ---- Scheduler ------------------------------------------------------

		void Schedule(IBot bot)
		{
			// Module 2: regular attack waves, with an escalation ladder (User 2026-07-22, reduced):
			// attempt 1 = primary choke; if it fails, attempt 2 = secondary choke (Wave-
			// SecondaryRouteAfterFailures=1); if that also fails, attempt 3 = an air raid instead of
			// a ground wave (WaveAirRaidAfterFailures=2, only once a helipad exists). From then on
			// (attempt 4 onward) the fixed ladder is abandoned for good -- randomEscalationPhase
			// picks, each time, at random between a ground wave via a random choke and another air
			// raid, so the enemy stops being predictable once it has escalated once already.
			if (Info.EnableWaves && !Missions.OfType<AotRegularWaveMission>().Any() && !Missions.OfType<AotAirRaidMission>().Any())
			{
				if (--waveCooldownTicks <= 0)
				{
					waveCooldownTicks = Info.WaveCooldown;

					var canAirRaid = Info.AirRaidHelicopterTypes.Length > 0 && HasHelipad();
					bool doAirRaid;
					bool useSecondaryRoute;

					if (randomEscalationPhase)
					{
						doAirRaid = canAirRaid && World.LocalRandom.Next(2) == 0;
						useSecondaryRoute = !doAirRaid && World.LocalRandom.Next(2) == 0;
					}
					else
					{
						doAirRaid = waveFailureStreak >= Info.WaveAirRaidAfterFailures && canAirRaid;
						useSecondaryRoute = !doAirRaid && waveFailureStreak >= Info.WaveSecondaryRouteAfterFailures;
					}

					if (doAirRaid)
					{
						Missions.Add(new AotAirRaidMission(this));
						randomEscalationPhase = true;
						Log($"air raid scheduled (streak={waveFailureStreak}, random={randomEscalationPhase})");
					}
					else
					{
						waveIndex++;
						var wave = new AotRegularWaveMission(this, waveIndex, useSecondaryRoute);
						Missions.Add(wave);
						Log($"wave {waveIndex} scheduled (tier {AgeTier()}, secondaryRoute={useSecondaryRoute}, streak={waveFailureStreak}, random={randomEscalationPhase})");
					}
				}
			}

			// Module 3: scout expeditions (50% spawn coverage). Gated on Radar/HQ: no point
			// scouting before there's a radar/minimap to show the results on. One-shot per group
			// (User 2026-07-22): each group launches exactly once and is never rebuilt, even if
			// wiped out en route -- scouting is a single initial sweep, not a standing patrol.
			if (Info.EnableScouts && Intel.AllSpawns.Count > 1 && HasRadar())
			{
				if (scoutAssignments.Count == 0)
					BuildScoutAssignments();

				foreach (var (index, spawns) in scoutAssignments)
				{
					if (scoutGroupsLaunched.Contains(index))
						continue;

					scoutGroupsLaunched.Add(index);
					Missions.Add(new AotScoutMission(this, index, spawns));
					Log($"scout group {index} scheduled ({spawns.Count} spawn(s))");
				}
			}

			// Module 4: derrick engineer squads (periodic 10-minute check, map-wide, capped
			// at DerrickMaxTargets DERRICKS held/pursued — not squads. A captured derrick's
			// mission never finishes (permanent guard), so it keeps occupying its slot.
			if (Info.EnableDerricks && --derrickTicks <= 0)
			{
				derrickTicks = Info.DerrickCheckInterval;
				var baseCentre = BaseCentre();
				var pursued = Missions.OfType<AotDerrickMission>().ToList();
				var freeSlots = Info.DerrickMaxTargets - pursued.Count;

				if (freeSlots > 0)
				{
					var candidates = Intel.UncontrolledDerricksAnywhere()
						.Where(d => Intel.IsReachable(d.Location) && !pursued.Any(m => m.Derrick == d))
						.OrderBy(d => (d.Location - baseCentre).LengthSquared)
						.Take(freeSlots);

					foreach (var derrick in candidates)
					{
						Missions.Add(new AotDerrickMission(this, derrick));
						Log($"derrick target acquired -> {derrick.Info.Name}@{derrick.Location} " +
							$"({pursued.Count + 1}/{Info.DerrickMaxTargets} derricks held/pursued)");
					}
				}
			}
		}

		void BuildScoutAssignments()
		{
			var groups = Math.Max(1, Intel.AllSpawns.Count / 2);
			var targets = Intel.EnemySpawns.OrderBy(s => (s - Intel.OwnSpawn).LengthSquared).ToList();
			var index = 0;
			for (var i = 0; i < targets.Count && index < groups; i += 2, index++)
				scoutAssignments[index] = targets.Skip(i).Take(2).ToList();
		}

		void Log(string message)
		{
			OpenRA.Log.Write("debug", $"[AotOps][{Player.PlayerName}] {message}");
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			constructionYards.Dispose();
		}
	}
}
