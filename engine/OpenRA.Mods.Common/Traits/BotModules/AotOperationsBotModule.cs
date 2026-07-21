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

	public abstract class AotMission
	{
		protected readonly AotOperationsBotModule Ops;
		public readonly HashSet<Actor> Units = [];
		public readonly string Name;
		public bool Done { get; protected set; }

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
			"When B and/or C are set, exactly one unit per defined role forms the group instead",
			"(mixed composition, e.g. an early-tier infantry scout squad).")]
		public readonly string[] ScoutTypes = [];

		[ActorReference]
		[Desc("Scout role B variant chain. Leave empty for a homogeneous ScoutTypes group.")]
		public readonly string[] ScoutRoleBTypes = [];

		[ActorReference]
		[Desc("Scout role C variant chain. Leave empty for a homogeneous ScoutTypes group.")]
		public readonly string[] ScoutRoleCTypes = [];

		public readonly int ScoutGroupSize = 2;

		[Desc("Waypoint ring radius around scouted spawns.")]
		public readonly int ScoutRingRadius = 8;

		[Desc("Cooldown before a destroyed scout group is rebuilt.")]
		public readonly int ScoutRespawnCooldown = 1500;

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
		int derrickTicks;
		int productionTicks;
		int starportTicks;
		int missionTicks;
		readonly Dictionary<int, int> scoutRespawnTicks = [];
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

			// Ground combat units only: mobile, not a harvester/transporter, not aircraft.
			if (a.TraitOrDefault<Mobile>() == null)
				return false;

			if (a.Info.HasTraitInfo<HarvesterInfo>() || a.Info.HasTraitInfo<AircraftInfo>())
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

				var request = requests.FirstOrDefault(r => r.Remaining > 0 && r.Chain.Contains(a.Info.Name));
				if (request != null)
				{
					request.Remaining--;
					if (request.Ordered > 0)
						request.Ordered--;
					owned[a] = request.Mission;
					request.Mission.OnUnitAssigned(a);
					Log($"claim {a.Info.Name} -> {request.Mission.Name} ({request.Role}, {request.Remaining} open)");
				}
				else
				{
					pool.Add(a);
					Log($"claim {a.Info.Name} -> pool ({pool.Count})");
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
			// Module 2: regular attack waves.
			if (Info.EnableWaves && !Missions.OfType<AotRegularWaveMission>().Any())
			{
				if (--waveCooldownTicks <= 0)
				{
					waveIndex++;
					var wave = new AotRegularWaveMission(this, waveIndex);
					Missions.Add(wave);
					waveCooldownTicks = Info.WaveCooldown;
					Log($"wave {waveIndex} scheduled (tier {AgeTier()})");
				}
			}

			// Module 3: scout expeditions (50% spawn coverage, self-regenerating).
			if (Info.EnableScouts && Intel.AllSpawns.Count > 1)
			{
				if (scoutAssignments.Count == 0)
					BuildScoutAssignments();

				foreach (var (index, spawns) in scoutAssignments)
				{
					if (Missions.OfType<AotScoutMission>().Any(m => m.GroupIndex == index))
						continue;

					scoutRespawnTicks.TryGetValue(index, out var cooldown);
					if (cooldown > 0)
					{
						scoutRespawnTicks[index] = cooldown - 1;
						continue;
					}

					Missions.Add(new AotScoutMission(this, index, spawns));
					scoutRespawnTicks[index] = Info.ScoutRespawnCooldown;
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
