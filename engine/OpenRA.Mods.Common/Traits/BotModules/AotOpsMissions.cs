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
	public abstract class AotMissionWithOrders : AotMission
	{
		protected AotMissionWithOrders(AotOperationsBotModule ops, string name)
			: base(ops, name) { }

		protected void AttackMoveGroup(IBot bot, IReadOnlyCollection<Actor> units, CPos cell)
		{
			var movable = units.Where(a => !Ops.CannotOrder(a)).ToArray();
			if (movable.Length > 0)
				bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(Ops.World, cell), false, groupedActors: movable));
		}

		protected void MoveUnit(IBot bot, Actor a, CPos cell, bool queued)
		{
			bot.QueueOrder(new Order("Move", a, Target.FromCell(Ops.World, cell), queued));
		}

		protected void ForceAttack(IBot bot, Actor a, Actor target)
		{
			bot.QueueOrder(new Order("ForceAttack", a, Target.FromActor(target), false));
		}

		protected CPos Centroid(IEnumerable<Actor> units)
		{
			var list = units.Where(a => !Ops.CannotOrder(a)).ToList();
			if (list.Count == 0)
				return Ops.BaseCentre();

			var x = 0;
			var y = 0;
			foreach (var a in list)
			{
				x += a.Location.X;
				y += a.Location.Y;
			}

			return new CPos(x / list.Count, y / list.Count);
		}

		protected void HoldAt(IBot bot, IReadOnlyCollection<Actor> units, CPos anchor, int leash)
		{
			var stray = units.Where(a => !Ops.CannotOrder(a) && (a.Location - anchor).LengthSquared > leash * leash).ToList();
			if (stray.Count > 0)
				bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(Ops.World, anchor), false, groupedActors: stray.ToArray()));
		}
	}

	// ======================================================================
	// Module 1: Starting Unit Operations.
	// Phase 1 is the 1:1 port of the APPROVED SquadManagerBotModule.UpdateChokeReserve
	// behaviour (choke reserve fill, secondary/beach guard, obstacle clearing).
	// Phase 2+ is the briefing follow-up: ARCO raid -> attack -> hold centre.
	// Per user decision 2026-07-21 the choke stays EMPTY after the group leaves.
	// ======================================================================
	public sealed class AotStartingUnitsMission : AotMissionWithOrders
	{
		enum Phase { ChokeHold, ArcoRaid, CrateWait, FinalAttack, HoldCentre }

		Phase phase = Phase.ChokeHold;
		readonly HashSet<Actor> chokeReserve = [];
		readonly HashSet<Actor> secondaryReserve = [];
		bool reserveAssigned;
		int clearChecks;

		readonly List<Actor> arcoTargets = [];
		readonly HashSet<Actor> arcoBlacklist = [];
		int arcosProcessed;
		Actor currentArco;
		CPos currentArcoCell;
		Actor crateCollector;
		int crateWaitTicks;
		int stallTicks;
		Actor finalTarget;

		public AotStartingUnitsMission(AotOperationsBotModule ops)
			: base(ops, "starting-units") { }

		public override void Tick(IBot bot)
		{
			if (Units.Count == 0)
			{
				Done = true;
				return;
			}

			switch (phase)
			{
				case Phase.ChokeHold: TickChokeHold(bot); break;
				case Phase.ArcoRaid: TickArcoRaid(bot); break;
				case Phase.CrateWait: TickCrateWait(bot); break;
				case Phase.FinalAttack: TickFinalAttack(bot); break;
				case Phase.HoldCentre: TickHoldCentre(bot); break;
			}

			// The secondary/beach guard holds its post through every phase.
			if (phase != Phase.ChokeHold && secondaryReserve.Count > 0)
			{
				secondaryReserve.RemoveWhere(a => Ops.CannotOrder(a));
				var secTarget = SecondaryTarget();
				if (secTarget != null)
					HoldAt(bot, secondaryReserve.ToList(), secTarget.Value, Ops.Info.ChokepointHoldRadius);
			}
		}

		CPos? SecondaryTarget()
		{
			var choke = Ops.ChokeProvider?.Chokepoint;
			if (!choke.HasValue)
				return null;

			// Approved behaviour: prefer a BEACH approach clearly distinct from the main choke,
			// else another distinct approach, else the main choke.
			if (Ops.ApproachProvider != null)
			{
				var far = Ops.ApproachProvider.BaseApproaches
					.Where(a => Math.Abs(a.Gate.X - choke.Value.X) + Math.Abs(a.Gate.Y - choke.Value.Y) > 8)
					.ToList();
				var beaches = far.Where(a => a.Type == BaseApproachType.Beach).ToList();
				return beaches.Count > 0 ? beaches[0].Gate
					: far.Count > 0 ? far[0].Gate
					: choke.Value;
			}

			return choke.Value;
		}

		// 1:1 port of the approved UpdateChokeReserve distribution + clearing.
		void TickChokeHold(IBot bot)
		{
			var choke = Ops.ChokeProvider?.Chokepoint;
			if (!choke.HasValue)
			{
				// No chokepoint (e.g. GDI has no planner instance yet, or open map):
				// after a grace period the whole group skips straight to the follow-up.
				if (++clearChecks >= 10)
				{
					foreach (var unit in Units)
						chokeReserve.Add(unit);
					reserveAssigned = true;
					BuildArcoTargets();
					phase = arcoTargets.Count > 0 ? Phase.ArcoRaid : Phase.FinalAttack;
					Log($"no chokepoint provider -> skipping to {phase}");
				}
				else
					Log("no chokepoint detected");
				return;
			}

			chokeReserve.RemoveWhere(a => Ops.CannotOrder(a));
			secondaryReserve.RemoveWhere(a => Ops.CannotOrder(a));

			if (!reserveAssigned)
			{
				clearChecks = 0;

				// Stronger units (vehicles/tanks) fill the main-choke reserve first;
				// everyone else guards the secondary approach.
				static bool IsStrong(Actor a) =>
					a.Info.TraitInfos<TargetableInfo>().Any(t => t.TargetTypes.Contains("Vehicle") || t.TargetTypes.Contains("Tank"));

				foreach (var unit in Units.OrderByDescending(IsStrong))
				{
					if (chokeReserve.Count < Ops.Info.ChokepointReserveSize)
						chokeReserve.Add(unit);
					else
						secondaryReserve.Add(unit);
				}

				reserveAssigned = true;
			}

			var secondary = SecondaryTarget();
			Log($"choke={choke.Value} secondary={secondary?.ToString() ?? "none"} " +
				$"reserve={chokeReserve.Count}/{Ops.Info.ChokepointReserveSize} secReserve={secondaryReserve.Count}");

			var holdR2 = Ops.Info.ChokepointHoldRadius * Ops.Info.ChokepointHoldRadius;

			var secTarget = secondary ?? choke.Value;
			var secOut = secondaryReserve.Where(a => !Ops.CannotOrder(a) && (a.Location - secTarget).LengthSquared > holdR2).ToList();
			if (secOut.Count > 0)
				bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(Ops.World, secTarget), false, groupedActors: secOut.ToArray()));

			var readyUnits = chokeReserve.Where(a => !Ops.CannotOrder(a)).ToList();
			if (readyUnits.Count == 0)
				return;

			var inPosition = readyUnits.Where(a => (a.Location - choke.Value).LengthSquared <= holdR2).ToList();
			var outOfPosition = readyUnits.Where(a => (a.Location - choke.Value).LengthSquared > holdR2).ToList();

			if (outOfPosition.Count > 0)
				bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(Ops.World, choke.Value), false, groupedActors: outOfPosition.ToArray()));

			if (inPosition.Count == 0)
				return;

			var chokeW = Ops.World.Map.CenterOfCell(choke.Value);
			var obstacles = Ops.World.FindActorsInCircle(chokeW, WDist.FromCells(Ops.Info.ChokeClearRadius))
				.Where(a => !a.IsDead && a.IsInWorld
					&& a.Info.HasTraitInfo<HealthInfo>()
					&& a.Owner.NonCombatant
					&& Ops.Player.RelationshipWith(a.Owner) != PlayerRelationship.Ally
					&& !Ops.Info.ChokeClearExcludeTypes.Contains(a.Info.Name)
					&& !a.Info.HasTraitInfo<BridgeInfo>()
					&& !a.Info.HasTraitInfo<GroundLevelBridgeInfo>()
					&& !a.Info.HasTraitInfo<LegacyBridgeHutInfo>())
				.OrderBy(a => (a.Location - choke.Value).LengthSquared)
				.ToList();

			if (obstacles.Count > 0)
			{
				clearChecks = 0;
				Log($"clearing {obstacles.Count} obstacle(s), nearest={obstacles[0].Info.Name}@{obstacles[0].Location}");

				var targets = obstacles.Take(2).ToList();
				for (var i = 0; i < inPosition.Count; i++)
					ForceAttack(bot, inPosition[i], targets[i % targets.Count]);
				return;
			}

			// Clearing done. Wait until the whole reserve is actually in position for a
			// few consecutive checks, then hand the group its follow-up mission.
			if (outOfPosition.Count == 0 && ++clearChecks >= 3)
			{
				BuildArcoTargets();
				phase = arcoTargets.Count > 0 ? Phase.ArcoRaid : Phase.FinalAttack;
				Log($"choke cleared -> {phase} ({arcoTargets.Count} arco target(s)); choke stays empty (user decision)");
			}
		}

		List<Actor> RaidGroup() => chokeReserve.Where(a => !Ops.CannotOrder(a)).ToList();

		void BuildArcoTargets()
		{
			var choke = Ops.ChokeProvider?.Chokepoint ?? Ops.BaseCentre();
			arcoTargets.Clear();
			arcoTargets.AddRange(Ops.Intel.Arcos()
				.Where(a => Ops.Intel.IsReachable(a.Location) && !arcoBlacklist.Contains(a))
				.OrderBy(a => (a.Location - choke).LengthSquared)
				.Take(Ops.Info.ArcoMaxTargets));
		}

		void TickArcoRaid(IBot bot)
		{
			var group = RaidGroup();
			if (group.Count == 0)
			{
				phase = Phase.HoldCentre;
				return;
			}

			if (arcosProcessed >= Ops.Info.ArcoMaxTargets)
			{
				phase = Phase.FinalAttack;
				return;
			}

			if (currentArco == null || currentArco.IsDead)
			{
				if (currentArco != null && currentArco.IsDead)
				{
					// Destroyed: check for the dropped crate before moving on.
					arcosProcessed++;
					currentArco = null;
					crateWaitTicks = 0;
					phase = Phase.CrateWait;
					return;
				}

				currentArco = arcoTargets.FirstOrDefault(a => !a.IsDead && a.IsInWorld);
				if (currentArco == null)
				{
					phase = Phase.FinalAttack;
					return;
				}

				currentArcoCell = currentArco.Location;
				Log($"arco raid -> {currentArco.Info.Name}@{currentArcoCell}");
			}

			// No endless re-routing: a group that sits idle for a while cannot path to
			// the target -> blacklist it and move on (briefing hard requirement).
			if (group.All(a => a.IsIdle))
			{
				if (++stallTicks >= 12)
				{
					Log($"arco {currentArco.Info.Name}@{currentArcoCell} unreachable -> blacklisted");
					arcoBlacklist.Add(currentArco);
					arcoTargets.Remove(currentArco);
					currentArco = null;
					stallTicks = 0;
					return;
				}
			}
			else
				stallTicks = 0;

			foreach (var a in group)
				if (a.IsIdle)
					ForceAttack(bot, a, currentArco);
		}

		void TickCrateWait(IBot bot)
		{
			var group = RaidGroup();
			if (group.Count == 0)
			{
				phase = Phase.HoldCentre;
				return;
			}

			crateWaitTicks++;
			var crate = Ops.Intel.CrateNear(currentArcoCell, 3);
			if (crate == null || crateWaitTicks >= 20)
			{
				// Nothing dropped, already collected, or the pickup is taking too long.
				if (crate == null && crateWaitTicks < 3)
					return;

				crateCollector = null;
				arcoTargets.RemoveAll(a => a.IsDead);
				phase = Phase.ArcoRaid;
				return;
			}

			// Exactly one collector drives over the crate (no pile-up on one crate).
			if (crateCollector == null || Ops.CannotOrder(crateCollector))
				crateCollector = group.MinBy(a => (a.Location - crate.Location).LengthSquared);

			if (crateCollector.IsIdle)
				MoveUnit(bot, crateCollector, crate.Location, false);
		}

		void TickFinalAttack(IBot bot)
		{
			var group = RaidGroup();
			if (group.Count == 0)
			{
				phase = Phase.HoldCentre;
				return;
			}

			var centre = Centroid(group);
			if (finalTarget == null || finalTarget.IsDead || !finalTarget.IsInWorld)
			{
				finalTarget = Ops.Intel.NearestEnemyYard(centre);
				if (finalTarget == null)
				{
					var spawn = Ops.Intel.NearestEnemySpawn(centre);
					if (spawn.HasValue)
					{
						Log($"final attack -> enemy spawn {spawn.Value}");
						AttackMoveGroup(bot, group, spawn.Value);
						return;
					}

					Log("no reachable enemy -> holding map centre");
					phase = Phase.HoldCentre;
					return;
				}

				Log($"final attack -> {finalTarget.Info.Name}@{finalTarget.Location}");
			}

			foreach (var a in group)
				if (a.IsIdle)
					bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(Ops.World, finalTarget.Location), false));
		}

		void TickHoldCentre(IBot bot)
		{
			var group = RaidGroup();
			if (group.Count > 0)
				HoldAt(bot, group, Ops.Intel.MapCentreFallback, Ops.Info.GuardLeashRadius);
		}
	}

	// ======================================================================
	// Module 2: Regular Attack Waves.
	// 75% static faction core + 25% adaptive; budget escalates per wave;
	// GDI retreats at WaveRetreatLossPercent (unit count), Nod fights to the end.
	// ======================================================================
	public sealed class AotRegularWaveMission : AotMissionWithOrders
	{
		enum Phase { Forming, Ferrying, Executing, Retreating }

		Phase phase = Phase.Forming;
		readonly int index;
		int formingTicks;
		int initialCount;
		Actor targetActor;
		CPos? targetCell;
		bool ecoWave;
		bool composed;

		// Naval ferry (no land route to the enemy): transports are tracked separately from the
		// combat Units they carry, reused across waves via the pool.
		readonly List<Actor> ferries = [];
		readonly HashSet<Actor> inTransit = [];
		readonly HashSet<Actor> ferriedAshore = [];
		CPos? embarkCell;
		CPos? ferryLandingCell;
		int ferryTicks;
		bool ferryRequested;
		bool ashore;

		public AotRegularWaveMission(AotOperationsBotModule ops, int index)
			: base(ops, $"wave-{index}")
		{
			this.index = index;
		}

		public override void OnUnitAssigned(Actor a)
		{
			if (Ops.Info.FerryTypes.Contains(a.Info.Name))
				ferries.Add(a);
			else
				base.OnUnitAssigned(a);
		}

		void FinishWave()
		{
			if (ferries.Count > 0)
			{
				Ops.ReleaseToPool(this, ferries.ToList());
				ferries.Clear();
			}

			Finish();
		}

		public override void Tick(IBot bot)
		{
			if (!composed)
			{
				Compose(bot);
				composed = true;
			}

			if (Done)
				return;

			switch (phase)
			{
				case Phase.Forming: TickForming(bot); break;
				case Phase.Ferrying: TickFerrying(bot); break;
				case Phase.Executing: TickExecuting(bot); break;
				case Phase.Retreating: TickRetreating(bot); break;
			}
		}

		void Compose(IBot bot)
		{
			var info = Ops.Info;
			var tier = Ops.AgeTier();
			var n = info.WaveVehiclesPerAge[Math.Min(tier, info.WaveVehiclesPerAge.Length - 1)];

			var mult = Math.Min(
				Math.Pow(1.0 + info.WaveBudgetEscalationPercent / 100.0, index - 1),
				info.WaveBudgetCapPercent / 100.0);

			// Resolve the currently buildable variant + cost per role.
			var roles = new List<(string Role, string[] Chain, int Share, string Variant, int Cost)>();
			foreach (var (role, chain, share) in new[]
			{
				("tank", info.WaveTankTypes, info.WaveTankShare),
				("light", info.WaveLightTypes, info.WaveLightShare),
				("support", info.WaveSupportTypes, info.WaveSupportShare)
			})
			{
				var variant = Ops.FirstBuildable(chain);
				if (variant == null)
					continue;

				var cost = Ops.World.Map.Rules.Actors[variant].TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 500;
				roles.Add((role, chain, share, variant, cost));
			}

			if (roles.Count == 0)
			{
				Log("no wave role buildable yet -> wave skipped");
				FinishWave();
				return;
			}

			// Static core (75%) split by shares; adaptive block (25%) counters the observed enemy.
			var adaptCount = n * info.WaveAdaptiveSharePercent / 100;
			var staticCount = n - adaptCount;
			var totalShare = roles.Sum(r => r.Share);
			var counts = roles.ToDictionary(r => r.Role, r => Math.Max(0, staticCount * r.Share / Math.Max(1, totalShare)));
			while (counts.Values.Sum() < staticCount)
				counts[roles[0].Role]++;

			// Budget escalation: scale the composition up round-robin (biggest share deficit first)
			// until the escalated budget is spent or the hard unit cap is reached.
			var baseBudget = roles.Sum(r => counts[r.Role] * r.Cost);
			var budget = (int)(baseBudget * mult);
			var spent = baseBudget;
			while (spent < budget && counts.Values.Sum() + adaptCount < info.WaveMaxUnits)
			{
				var role = roles.MinBy(r => counts[r.Role] * 100.0 / Math.Max(1, r.Share));
				if (spent + role.Cost > budget)
					break;

				counts[role.Role]++;
				spent += role.Cost;
			}

			var adaptChain = AdaptiveChain();

			Log($"compose tier={tier} n={n} mult={mult:F2} budget={budget} " +
				$"static=[{string.Join(", ", roles.Select(r => $"{r.Role}:{counts[r.Role]}x{r.Variant}"))}] adaptive={adaptCount}");

			// Pool first (leftovers from earlier waves), then production.
			foreach (var r in roles)
			{
				var want = counts[r.Role];
				if (want <= 0)
					continue;

				var fromPool = Ops.TakeFromPool(r.Chain, want);
				Ops.AssignFromPool(this, fromPool);
				if (want - fromPool.Count > 0)
					Ops.QueueRequest(this, r.Role, r.Chain, want - fromPool.Count);
			}

			if (adaptCount > 0 && adaptChain.Length > 0)
			{
				var fromPool = Ops.TakeFromPool(adaptChain, adaptCount);
				Ops.AssignFromPool(this, fromPool);
				if (adaptCount - fromPool.Count > 0)
					Ops.QueueRequest(this, "adaptive", adaptChain, adaptCount - fromPool.Count);
			}

			ecoWave = Ops.World.LocalRandom.Next(100) < info.WaveEcoTargetPercent;
		}

		string[] AdaptiveChain()
		{
			var info = Ops.Info;
			int infantry = 0, vehicles = 0, air = 0;
			foreach (var a in Ops.World.Actors)
			{
				if (!AotOpsUtils.IsPreferredEnemyUnit(Ops.Player, a) || !a.CanBeViewedByPlayer(Ops.Player))
					continue;

				if (a.Info.HasTraitInfo<AircraftInfo>())
					air++;
				else if (a.Info.TraitInfos<TargetableInfo>().Any(t => t.TargetTypes.Contains("Infantry")))
					infantry++;
				else if (a.Info.HasTraitInfo<MobileInfo>())
					vehicles++;
			}

			if (air >= infantry && air >= vehicles && air > 0 && info.AntiAirTypes.Length > 0)
				return info.AntiAirTypes;
			if (infantry > vehicles && info.AntiInfantryTypes.Length > 0)
				return info.AntiInfantryTypes;
			if (info.AntiTankTypes.Length > 0)
				return info.AntiTankTypes;
			return info.WaveTankTypes;
		}

		CPos RallyPoint()
		{
			var baseCentre = Ops.BaseCentre();
			var choke = Ops.ChokeProvider?.Chokepoint;
			if (!choke.HasValue)
				return baseCentre;

			// Halfway between base and choke: out of the builders' way, on the way out.
			return new CPos((baseCentre.X + choke.Value.X) / 2, (baseCentre.Y + choke.Value.Y) / 2);
		}

		void TickForming(IBot bot)
		{
			formingTicks += Ops.Info.MissionInterval;

			var rally = RallyPoint();
			foreach (var a in Units)
				if (a.IsIdle && (a.Location - rally).LengthSquared > 25)
					bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(Ops.World, rally), false));

			var open = Ops.OpenRequests(this);
			var launch = open == 0 && Units.Count > 0;
			if (!launch && formingTicks >= Ops.Info.WaveFormingTimeout)
				launch = Units.Count >= open; // at least half assembled

			// Nothing producible at all (e.g. every factory lost): don't block the wave slot forever.
			if (!launch && formingTicks >= Ops.Info.WaveFormingTimeout * 2)
			{
				if (Units.Count > 0)
					launch = true;
				else
				{
					Log("forming dead end -> wave cancelled");
					FinishWave();
					return;
				}
			}

			if (!launch)
				return;

			initialCount = Units.Count;
			ChooseTarget();

			if (targetActor == null && targetCell == null && TryStartFerry())
			{
				phase = Phase.Ferrying;
				Log($"launch: {initialCount} unit(s), eco={ecoWave}, no ground route to the enemy -> " +
					$"staging at embark={embarkCell} (landing={ferryLandingCell}), " +
					$"{(Ops.HasNavalProduction() ? "requesting transports" : "waiting for naval production")}");
			}
			else
			{
				phase = Phase.Executing;
				Log($"launch: {initialCount} unit(s), eco={ecoWave}, target={DescribeTarget()}");
			}
		}

		string DescribeTarget() =>
			targetActor != null ? $"{targetActor.Info.Name}@{targetActor.Location}" : targetCell?.ToString() ?? "none";

		void ChooseTarget()
		{
			var centre = Centroid(Units);
			targetActor = null;
			targetCell = null;

			// Once the wave has ferried across water it stands on the far shore, outside the AI's
			// own base-side ground-reachability set -- stop requiring reachability from there on.
			var requireReachable = !ashore;

			if (ecoWave)
			{
				// Economy pressure: visible enemy harvesters first (outposts fall back to the main path).
				targetActor = Ops.Intel.NearestVisibleEnemyHarvester(centre, requireReachable);
				if (targetActor != null)
					return;
			}

			targetActor = Ops.Intel.NearestEnemyYard(centre, requireReachable);
			if (targetActor == null)
				targetCell = Ops.Intel.NearestEnemySpawn(centre, requireReachable);
		}

		// No ground path to the enemy: find a coastal embark/landing cell so the wave can stage at
		// the coast. Transports are requested lazily in TickFerrying once naval production exists --
		// until then the wave just waits at the beach (user spec). Returns false (wave proceeds/ends
		// normally) only if ferrying could never work at all (no chain configured, no coast found).
		bool TryStartFerry()
		{
			if (Ops.Info.FerryTypes.Length == 0)
				return false;

			if (Ops.Intel.EnemySpawns.Count == 0)
				return false;

			var enemyRef = Ops.Intel.EnemySpawns.MinBy(s => (s - Ops.BaseCentre()).LengthSquared);
			ferryLandingCell = Ops.Intel.FindCoastalCellNear(enemyRef, Ops.Info.FerrySearchRadius, requireOwnReachable: false);
			embarkCell = Ops.Intel.FindCoastalCellNear(Ops.BaseCentre(), Ops.Info.FerrySearchRadius, requireOwnReachable: true);

			if (ferryLandingCell == null || embarkCell == null)
			{
				Log("naval ferry unavailable: no coastal embark/landing cell found nearby");
				return false;
			}

			return true;
		}

		void TickFerrying(IBot bot)
		{
			ferries.RemoveAll(Ops.CannotOrder);
			ferriedAshore.RemoveWhere(Ops.CannotOrder);
			inTransit.RemoveWhere(Ops.CannotOrder);

			if (!ferryRequested)
			{
				if (Ops.HasNavalProduction())
				{
					// Reuse transports released by an earlier wave first, only produce the shortfall.
					var fromPool = Ops.TakeFromPool(Ops.Info.FerryTypes, Ops.Info.FerryCount);
					Ops.AssignFromPool(this, fromPool);
					if (Ops.Info.FerryCount - fromPool.Count > 0)
						Ops.QueueRequest(this, "ferry", Ops.Info.FerryTypes, Ops.Info.FerryCount - fromPool.Count);

					ferryRequested = true;
					Log("naval production ready -> transports requested");
				}

				// Else: no Sub Pen/Shipyard yet. No timeout here -- the wave just holds at the coast
				// (see the walk-to-embark loop below) until one is eventually built.
			}

			if (ferryRequested)
				ferryTicks += Ops.Info.MissionInterval;

			if (ferryRequested && ferries.Count == 0 && Ops.OpenRequests(this) == 0 && ferriedAshore.Count == 0)
			{
				Log("no transports available -> ferry cancelled, wave dissolved");
				FinishWave();
				return;
			}

			var pending = Units.Where(a => !Ops.CannotOrder(a) && !ferriedAshore.Contains(a) && !inTransit.Contains(a)).ToList();

			// Walk not-yet-embarked units to the coast.
			foreach (var u in pending)
				if (u.IsIdle && (u.Location - embarkCell.Value).LengthSquared > 9)
					bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(Ops.World, embarkCell.Value), false));

			foreach (var ship in ferries)
			{
				var cargo = ship.TraitOrDefault<Cargo>();
				if (cargo == null)
					continue;

				if (cargo.IsEmpty())
				{
					if ((ship.Location - embarkCell.Value).LengthSquared <= 9)
					{
						var unit = pending.FirstOrDefault(u => u.IsIdle && (u.Location - embarkCell.Value).LengthSquared <= 9);
						if (unit != null)
						{
							bot.QueueOrder(new Order("EnterTransport", unit, Target.FromActor(ship), false));
							inTransit.Add(unit);
							pending.Remove(unit);
						}
					}
					else if (ship.IsIdle && (pending.Count > 0 || inTransit.Count > 0))
						bot.QueueOrder(new Order("Move", ship, Target.FromCell(Ops.World, embarkCell.Value), false));
				}
				else
				{
					if ((ship.Location - ferryLandingCell.Value).LengthSquared <= 9)
						bot.QueueOrder(new Order("Unload", ship, false));
					else if (ship.IsIdle)
						bot.QueueOrder(new Order("Move", ship, Target.FromCell(Ops.World, ferryLandingCell.Value), false));
				}
			}

			// Detect disembarked units: still tracked as in-transit but no longer aboard any ferry.
			foreach (var u in inTransit.ToList())
			{
				if (!ferries.Any(s => s.TraitOrDefault<Cargo>()?.Passengers.Contains(u) == true))
				{
					inTransit.Remove(u);
					ferriedAshore.Add(u);
				}
			}

			var stillToGo = Units.Count(a => !Ops.CannotOrder(a) && !ferriedAshore.Contains(a));
			if (stillToGo == 0)
			{
				if (ferriedAshore.Count == 0)
				{
					Log("wave lost (wiped out crossing, or while waiting for naval production)");
					FinishWave();
					return;
				}

				ashore = true;
				ChooseTarget();
				phase = Phase.Executing;
				Log($"ferry complete: {ferriedAshore.Count} unit(s) landed near the enemy, target={DescribeTarget()}");
				return;
			}

			if (ferryRequested && ferryTicks >= Ops.Info.FerryTimeout)
			{
				if (ferriedAshore.Count > 0)
				{
					ashore = true;
					ChooseTarget();
					phase = Phase.Executing;
					Log($"ferry timeout -> proceeding with {ferriedAshore.Count} unit(s) already ashore");
				}
				else
				{
					Log("ferry timeout, nobody made it across -> wave cancelled");
					FinishWave();
				}
			}
		}

		void TickExecuting(IBot bot)
		{
			if (Units.Count == 0)
			{
				Log("wave wiped out");
				FinishWave();
				return;
			}

			// GDI: retreat at the configured loss percentage (by unit count). Nod: 0 = never.
			var retreat = Ops.Info.WaveRetreatLossPercent;
			if (retreat > 0 && initialCount > 0 && (initialCount - Units.Count) * 100 / initialCount >= retreat)
			{
				Log($"loss threshold reached ({Units.Count}/{initialCount}) -> retreating");
				phase = Phase.Retreating;
				return;
			}

			if (targetActor != null && (targetActor.IsDead || !targetActor.IsInWorld))
				targetActor = null;

			if (targetActor == null && targetCell == null)
			{
				ChooseTarget();
				if (targetActor == null && targetCell == null)
				{
					Log("no target left -> wave done");
					FinishWave();
					return;
				}
			}

			// Keep the wave together: stragglers regroup on the unit closest to the centroid.
			var centre = Centroid(Units);
			var leader = Units.Where(a => !Ops.CannotOrder(a)).MinByOrDefault(a => (a.Location - centre).LengthSquared);
			if (leader == null)
				return;

			var spreadRadius = Math.Max(5, Units.Count / 3);
			var stragglers = Units.Where(a => !Ops.CannotOrder(a) && (a.Location - leader.Location).LengthSquared > spreadRadius * spreadRadius).ToList();
			if (stragglers.Count > Units.Count / 2)
			{
				bot.QueueOrder(new Order("Stop", leader, false));
				AttackMoveGroup(bot, stragglers, leader.Location);
				return;
			}

			var goal = targetActor?.Location ?? targetCell.Value;
			foreach (var a in Units)
				if (a.IsIdle)
					bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(Ops.World, goal), false));
		}

		void TickRetreating(IBot bot)
		{
			if (Units.Count == 0)
			{
				FinishWave();
				return;
			}

			var home = Ops.BaseCentre();
			var centre = Centroid(Units);
			if ((centre - home).LengthSquared <= 64)
			{
				Log("retreat complete -> units to pool");
				FinishWave();
				return;
			}

			AttackMoveGroup(bot, Units, home);
		}
	}

	// ======================================================================
	// Module 3: Scout Expeditions. Two light vehicles per group, up to two
	// spawn areas each, edge/ring sweep for maximum fog reveal, then an
	// observation post. Groups self-regenerate via the module scheduler.
	// ======================================================================
	public sealed class AotScoutMission : AotMissionWithOrders
	{
		enum Phase { Forming, Touring, Posting, Holding }

		public readonly int GroupIndex;
		readonly List<CPos> spawns;
		readonly int groupTarget;
		Phase phase = Phase.Forming;
		int spawnCursor;
		List<CPos> currentWaypoints;
		int waypointCursor;
		CPos post;

		public AotScoutMission(AotOperationsBotModule ops, int groupIndex, List<CPos> spawns)
			: base(ops, $"scouts-{groupIndex}")
		{
			GroupIndex = groupIndex;
			this.spawns = spawns;

			// Role B/C set -> mixed composition (e.g. an early-tier infantry scout squad where no
			// vehicle scout chain is buildable yet; all roles MUST share the same move speed or the
			// group spreads out). Otherwise: a homogeneous ScoutTypes group of ScoutGroupSize.
			var mixed = ops.Info.ScoutRoleBTypes.Length > 0 || ops.Info.ScoutRoleCTypes.Length > 0;
			var roles = new List<(string[] Chain, int Count)> { (ops.Info.ScoutTypes, mixed ? ops.Info.ScoutRoleACount : ops.Info.ScoutGroupSize) };
			if (ops.Info.ScoutRoleBTypes.Length > 0)
				roles.Add((ops.Info.ScoutRoleBTypes, ops.Info.ScoutRoleBCount));
			if (ops.Info.ScoutRoleCTypes.Length > 0)
				roles.Add((ops.Info.ScoutRoleCTypes, ops.Info.ScoutRoleCCount));

			groupTarget = roles.Sum(r => r.Count);
			foreach (var (chain, count) in roles)
			{
				if (count <= 0)
					continue;

				var fromPool = ops.TakeFromPool(chain, count);
				ops.AssignFromPool(this, fromPool);
				if (count - fromPool.Count > 0)
					ops.QueueRequest(this, $"scout-{chain[0]}", chain, count - fromPool.Count);
			}
		}

		public override void Tick(IBot bot)
		{
			if (Units.Count == 0 && Ops.OpenRequests(this) == 0)
			{
				Done = true;
				return;
			}

			switch (phase)
			{
				case Phase.Forming:
					if (Units.Count >= groupTarget)
					{
						phase = Phase.Touring;
						Log($"touring {spawns.Count} spawn(s)");
					}

					break;

				case Phase.Touring: TickTouring(bot); break;

				case Phase.Posting:
					post = FindPost();
					phase = Phase.Holding;
					Log($"observation post @ {post}");
					break;

				case Phase.Holding:
					HoldAt(bot, Units, post, Ops.Info.GuardLeashRadius);
					break;
			}
		}

		void TickTouring(IBot bot)
		{
			if (currentWaypoints == null)
			{
				if (spawnCursor >= spawns.Count)
				{
					phase = Phase.Posting;
					return;
				}

				currentWaypoints = RouteAround(spawns[spawnCursor]);
				waypointCursor = 0;
				Log($"sweep spawn {spawnCursor + 1}/{spawns.Count} @ {spawns[spawnCursor]} ({currentWaypoints.Count} waypoint(s))");

				if (currentWaypoints.Count == 0)
				{
					spawnCursor++;
					currentWaypoints = null;
					return;
				}
			}

			var live = Units.Where(a => !Ops.CannotOrder(a)).ToList();
			if (live.Count == 0)
				return;

			if (waypointCursor >= currentWaypoints.Count)
			{
				spawnCursor++;
				currentWaypoints = null;
				return;
			}

			// Move as one grouped order per waypoint (not per-unit chains) so the squad stays
			// together instead of spreading out; advance once the group's centroid arrives.
			var target = currentWaypoints[waypointCursor];
			if ((Centroid(live) - target).LengthSquared <= 9)
			{
				waypointCursor++;
				return;
			}

			if (live.All(a => a.IsIdle))
				AttackMoveGroup(bot, live, target);
		}

		List<CPos> RouteAround(CPos spawn)
		{
			// Perimeter ring around the spawn, reachable cells only, in angular order —
			// coverage over shortest path (map/cliff edges naturally bound the ring).
			var cells = AotOpsUtils.Ring(spawn, Ops.Info.ScoutRingRadius)
				.Where(Ops.Intel.IsReachable)
				.OrderBy(c => Math.Atan2(c.Y - spawn.Y, c.X - spawn.X))
				.ToList();

			var waypoints = new List<CPos>();
			for (var i = 0; i < cells.Count; i += Math.Max(1, cells.Count / 6))
				waypoints.Add(cells[i]);

			if (waypoints.Count == 0 && Ops.Intel.IsReachable(spawn))
				waypoints.Add(spawn);

			return waypoints;
		}

		CPos FindPost()
		{
			// Bridges and crossings make the best posts; fall back to the map centre.
			var centre = Centroid(Units);
			var bridgeHut = Ops.World.Actors
				.Where(a => !a.IsDead && a.IsInWorld
					&& (a.Info.HasTraitInfo<LegacyBridgeHutInfo>() || a.Info.HasTraitInfo<BridgeHutInfo>()))
				.MinByOrDefault(a => (a.Location - centre).LengthSquared);

			return bridgeHut?.Location ?? Ops.Intel.MapCentreFallback;
		}
	}

	// ======================================================================
	// Module 4: Derrick Engineer Squads. One full squad per uncontrolled
	// derrick in the own map quarter; blocking fences are force-fired away;
	// the four escorts stay as a permanent guard. Rescans every 10 minutes.
	// ======================================================================
	public sealed class AotDerrickMission : AotMissionWithOrders
	{
		enum Phase { Forming, Moving, Capturing, Holding }

		public readonly Actor Derrick;
		Phase phase = Phase.Forming;
		int formingTicks;

		public AotDerrickMission(AotOperationsBotModule ops, Actor derrick)
			: base(ops, $"derrick-{derrick.ActorID}")
		{
			Derrick = derrick;
			ops.QueueRequest(this, "engineer", ops.Info.EngineerTypes, 1);
			ops.QueueRequest(this, "rocket", ops.Info.RocketInfantryTypes, 2);
			ops.QueueRequest(this, "mg", ops.Info.MgInfantryTypes, 2);
		}

		Actor Engineer() => Units.FirstOrDefault(a => Ops.Info.EngineerTypes.Contains(a.Info.Name));

		List<Actor> Escorts() => Units.Where(a => !Ops.Info.EngineerTypes.Contains(a.Info.Name) && !Ops.CannotOrder(a)).ToList();

		public override void Tick(IBot bot)
		{
			if (Derrick.IsDead || !Derrick.IsInWorld)
			{
				Log("derrick destroyed -> mission over");
				Finish();
				return;
			}

			if (Units.Count == 0 && Ops.OpenRequests(this) == 0)
			{
				Done = true;
				return;
			}

			if (Derrick.Owner == Ops.Player && phase != Phase.Holding)
			{
				phase = Phase.Holding;
				Log("derrick captured -> permanent guard");

				// The engineer is consumed by the capture; release a survivor to the pool.
				var engineer = Engineer();
				if (engineer != null && !Ops.CannotOrder(engineer))
				{
					Units.Remove(engineer);
					Ops.ReleaseToPool(this, [engineer]);
				}
			}

			switch (phase)
			{
				case Phase.Forming: TickForming(bot); break;
				case Phase.Moving: TickMoving(bot); break;
				case Phase.Capturing: TickCapturing(bot); break;
				case Phase.Holding: HoldAt(bot, Units, Derrick.Location, Ops.Info.GuardLeashRadius); break;
			}
		}

		void TickForming(IBot bot)
		{
			formingTicks += Ops.Info.MissionInterval;

			// The engineer is mandatory; escorts may launch short-handed after a timeout.
			if (Engineer() != null && (Ops.OpenRequests(this) == 0 || formingTicks >= 4500))
			{
				phase = Phase.Moving;
				Log($"moving to derrick @ {Derrick.Location} with {Units.Count} unit(s)");
			}
		}

		void TickMoving(IBot bot)
		{
			var engineer = Engineer();
			if (engineer == null)
			{
				// Engineer lost en route: order a replacement and wait.
				if (Ops.OpenRequests(this) == 0)
					Ops.QueueRequest(this, "engineer", Ops.Info.EngineerTypes, 1);
				return;
			}

			var escorts = Escorts();
			foreach (var a in escorts)
				if (a.IsIdle)
					bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(Ops.World, Derrick.Location), false));

			if (engineer.IsIdle)
				MoveUnit(bot, engineer, Derrick.Location, false);

			if ((Centroid(Units) - Derrick.Location).LengthSquared <= 36)
			{
				phase = Phase.Capturing;
				Log("at derrick -> capturing");
			}
		}

		void TickCapturing(IBot bot)
		{
			var engineer = Engineer();
			if (engineer == null)
			{
				phase = Phase.Moving;
				return;
			}

			// Blocking fences/walls around the derrick are removed by force fire.
			var blockers = Ops.World.FindActorsInCircle(Ops.World.Map.CenterOfCell(Derrick.Location), WDist.FromCells(3))
				.Where(a => !a.IsDead && a.IsInWorld
					&& a.Info.HasTraitInfo<LineBuildInfo>()
					&& a.Info.HasTraitInfo<HealthInfo>()
					&& a.Owner != Ops.Player
					&& Ops.Player.RelationshipWith(a.Owner) != PlayerRelationship.Ally)
				.OrderBy(a => (a.Location - Derrick.Location).LengthSquared)
				.ToList();

			if (blockers.Count > 0)
			{
				var escorts = Escorts();
				for (var i = 0; i < escorts.Count; i++)
					if (escorts[i].IsIdle)
						ForceAttack(bot, escorts[i], blockers[i % blockers.Count]);
			}

			if (engineer.IsIdle)
				bot.QueueOrder(new Order("CaptureActor", engineer, Target.FromActor(Derrick), true));
		}
	}
}
