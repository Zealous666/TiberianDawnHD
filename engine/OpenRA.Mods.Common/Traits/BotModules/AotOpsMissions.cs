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
using OpenRA.Mods.Common.Activities;
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

		// Force-fires the up-to-2 nearest destructible neutral obstacles (trees, civ buildings)
		// near `target` using the given in-position units. Returns the obstacle list found (empty
		// if none), so callers can react to "still clearing" vs "area clean". Shared (User
		// 2026-07-22): originally only the starting-units choke reserve had this -- plain
		// AttackMove orders never destroy a blocking tree/wall on their own, so any other mission
		// routing through the same choke (e.g. regular waves) could get stuck there forever too.
		protected List<Actor> ClearNearbyObstacles(IBot bot, CPos target, List<Actor> inPosition)
		{
			var targetW = Ops.World.Map.CenterOfCell(target);
			var obstacles = Ops.World.FindActorsInCircle(targetW, WDist.FromCells(Ops.Info.ChokeClearRadius))
				.Where(a => !a.IsDead && a.IsInWorld
					&& a.Info.HasTraitInfo<HealthInfo>()
					&& a.Owner.NonCombatant
					&& Ops.Player.RelationshipWith(a.Owner) != PlayerRelationship.Ally
					&& !Ops.Info.ChokeClearExcludeTypes.Contains(a.Info.Name)
					&& !a.Info.HasTraitInfo<BridgeInfo>()
					&& !a.Info.HasTraitInfo<GroundLevelBridgeInfo>()
					&& !a.Info.HasTraitInfo<LegacyBridgeHutInfo>()

					// An actor whose whole job is to BE terrain (grown ice cells, which carry
					// ChangesTerrain: Ice) is scenery, not a blocking obstacle -- and shooting it away
					// destroys the very surface units want to walk on. Matched by trait, not by name,
					// so future terrain actors are covered automatically (User 2026-07-25: AI was
					// clearing ice floes in chokepoints on Polar Panic).
					&& !a.Info.HasTraitInfo<ChangesTerrainInfo>())
				.OrderBy(a => (a.Location - target).LengthSquared)
				.ToList();

			if (obstacles.Count == 0)
				return obstacles;

			Log($"clearing {obstacles.Count} obstacle(s) near {target}, nearest={obstacles[0].Info.Name}@{obstacles[0].Location}");

			var targets = obstacles.Take(2).ToList();
			for (var i = 0; i < inPosition.Count; i++)
				ForceAttack(bot, inPosition[i], targets[i % targets.Count]);

			return obstacles;
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
		enum Phase { ChokeHold, ArcoRaid, CrateWait, Ferrying, FinalAttack, HoldCentre }

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

		// The starting force could never leave the home landmass: its ARCO search filters on
		// Intel.IsReachable and the final attack needs a reachable enemy, so on a water map it cleared
		// the choke, found "0 arco target(s)", reported "no reachable enemy" and held the map centre
		// for the rest of the match. It books a crossing like every other mission now (user decision
		// 2026-07-29: the WHOLE force crosses, choke reserve included).
		AotTransitTicket ticket;
		bool ashore;

		// Set on the FIRST Tick() call: true only if this mission started with literally zero units,
		// i.e. the spawn has no starting army at all. A normal spawn flips this false immediately and
		// nothing below ever engages -- ZERO behaviour change for the common case.
		bool? startedEmpty;
		int bootstrapWaitTicks;

		// True while THIS spawn started with no units and is still short of a usable clearing crew --
		// consulted by AotOperationsBotModule.ClaimNewUnits, which diverts freshly produced combat
		// units here FIRST, ahead of every other mission's own claim, until satisfied.
		//
		// Why this exists (user spec 2026-08-01): the base planner now treats trees within
		// ChokeClearRadius of the chokepoint as clearable rather than permanently blocking (matching
		// what TickChokeHold's ClearNearbyObstacles actually does at runtime) -- but that assumption
		// only holds if SOMEONE actually goes and clears them. On a normal spawn the starting army
		// already does this in Phase.ChokeHold. On a spawn with NO starting units, nothing ever would:
		// this mission used to see Units.Count==0 on its very first tick and finish immediately, and
		// the idle-pool sweep that could eventually reinforce it only fires after a long delay and
		// prefers attack waves anyway. Without a deliberate fallback, the planner's assumption would
		// silently fail on exactly this spawn type.
		public bool NeedsBootstrapCrew => startedEmpty == true && !Done && Units.Count < Ops.Info.ChokepointReserveSize;

		public AotStartingUnitsMission(AotOperationsBotModule ops)
			: base(ops, "starting-units") { }

		// The standing guard (User 2026-07-31: "chockepoints sind ja zu verteidigen, es bringt nichts,
		// wenn die reserve dann genau da nicht als reserve eingreift wenn schon alles andere tot ist") --
		// exposed for the global self-defense pass. Only meaningful while the reserve is actually
		// standing post: once the force has crossed water (ashore) or moved into FinalAttack/HoldCentre,
		// these sets stop being a "post" and are just the active force, already covered by their own
		// mission logic.
		public IEnumerable<Actor> ReserveUnits() =>
			phase is Phase.ChokeHold or Phase.ArcoRaid or Phase.CrateWait
				? chokeReserve.Concat(secondaryReserve).Where(a => !Ops.CannotOrder(a))
				: [];

		public override void OnUnitAssigned(Actor a)
		{
			if (ReturnStrayNavalSupport(a))
				return;

			base.OnUnitAssigned(a);
		}

		public override void Tick(IBot bot)
		{
			startedEmpty ??= Units.Count == 0;

			if (startedEmpty.Value && Units.Count < Ops.Info.ChokepointReserveSize)
			{
				// Waiting for ClaimNewUnits to hand over a bootstrap crew. Bounded by
				// StartupPriorityTimeout so a spawn that can genuinely never afford a full reserve
				// (destroyed barracks, no cash at all) does not wait forever: past the timeout, proceed
				// with whatever partial crew exists, or give up cleanly if that is still nobody.
				bootstrapWaitTicks += Ops.Info.MissionInterval;
				if (bootstrapWaitTicks < Ops.Info.StartupPriorityTimeout)
					return;

				if (Units.Count == 0)
				{
					Log("bootstrap timed out with no clearing crew ever assembled -> giving up");
					Done = true;
					return;
				}

				Log($"bootstrap timed out with a partial crew ({Units.Count}/{Ops.Info.ChokepointReserveSize}) -> proceeding anyway");

				// Committed to the partial crew now: stop asking ClaimNewUnits to divert more units
				// here (NeedsBootstrapCrew reads startedEmpty), and skip straight to the normal path on
				// every future tick instead of re-logging this same decision forever.
				startedEmpty = false;
			}

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
				case Phase.Ferrying: TickFerrying(bot); break;
				case Phase.FinalAttack: TickFinalAttack(bot); break;
				case Phase.HoldCentre: TickHoldCentre(bot); break;
			}

			// The secondary/beach guard holds its post through every phase EXCEPT the crossing and
			// everything after it: its post is a chokepoint at HOME, so ordering the landed force back
			// to it would send half of them walking into the sea.
			if (phase != Phase.ChokeHold && phase != Phase.Ferrying && !ashore && secondaryReserve.Count > 0)
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

				// Only split into a distinct secondary/beach guard if there actually IS a distinct
				// secondary approach to defend. If SecondaryTarget() falls back to the choke itself
				// (no beach/other approach found), there's nothing separate to guard -- everyone
				// simply joins the main group and does everything together from here on (User
				// 2026-07-22; previously they were split off into a secondaryReserve that never
				// cleared obstacles and never followed the main group's later phases, so they just
				// stood at the choke forever without a real purpose).
				var hasDistinctSecondary = SecondaryTarget() is CPos sc && sc != choke.Value;

				if (!hasDistinctSecondary)
				{
					foreach (var unit in Units)
						chokeReserve.Add(unit);
				}
				else
				{
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
				}

				reserveAssigned = true;
			}

			var secondary = SecondaryTarget();
			Log($"choke={choke.Value} secondary={secondary?.ToString() ?? "none"} " +
				$"reserve={chokeReserve.Count}/{Ops.Info.ChokepointReserveSize} secReserve={secondaryReserve.Count}");

			var holdR2 = Ops.Info.ChokepointHoldRadius * Ops.Info.ChokepointHoldRadius;

			// A genuine secondary/beach post stays purely defensive -- holds position, does not
			// clear obstacles there. (secondaryReserve is simply empty when there's no distinct
			// secondary approach; see the merge above.)
			var secTarget = secondary ?? choke.Value;
			var secOut = secondaryReserve.Where(a => !Ops.CannotOrder(a) && (a.Location - secTarget).LengthSquared > holdR2).ToList();
			if (secOut.Count > 0)
				bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(Ops.World, secTarget), false, groupedActors: secOut.ToArray()));

			var readyUnits = chokeReserve.Where(a => !Ops.CannotOrder(a)).ToList();
			if (readyUnits.Count == 0)
				return;

			// Never let the garrison hold position ON TOP of the gate-defence cluster's own reserved
			// footprint (buildings + fence). Both are independently biased toward the SAME choke cell
			// (the garrison holds AT the choke; the cluster is placed as close to the choke as
			// possible), so a unit "correctly" holding its post can end up parked on a planned
			// building or fence node forever, permanently blocking it -- it LOOKS right (it is exactly
			// at the choke) while structurally colliding with what gets built there (user-fund
			// 2026-08-01). Push it toward the base (same "behind" direction the Obelisk/second Silo
			// use) a few cells at a time; re-issued every check while still inside, same as the
			// out-of-position push below, so it keeps making progress without needing an IsIdle gate.
			var insideCluster = readyUnits.Where(a => Ops.IsInsideGateCluster(a.Location)).ToList();
			if (insideCluster.Count > 0)
			{
				var baseCentre = Ops.BaseCentre();
				foreach (var a in insideCluster)
				{
					var behind = AotBasePlannerBotModule.Cardinal(new CVec(baseCentre.X - a.Location.X, baseCentre.Y - a.Location.Y));
					bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(Ops.World, a.Location + (behind * 3)), false));
				}
			}

			var outside = readyUnits.Where(a => !insideCluster.Contains(a)).ToList();
			var inPosition = outside.Where(a => (a.Location - choke.Value).LengthSquared <= holdR2).ToList();
			var outOfPosition = outside.Where(a => (a.Location - choke.Value).LengthSquared > holdR2).ToList();

			if (outOfPosition.Count > 0)
				bot.QueueOrder(new Order("AttackMove", null, Target.FromCell(Ops.World, choke.Value), false, groupedActors: outOfPosition.ToArray()));

			if (inPosition.Count == 0)
				return;

			var obstacles = ClearNearbyObstacles(bot, choke.Value, inPosition);
			if (obstacles.Count > 0)
			{
				clearChecks = 0;
				return;
			}

			// Clearing done. Wait until the whole reserve is actually in position for a
			// few consecutive checks, then hand the group its follow-up mission. Units still being
			// pushed out of the cluster's own footprint do not count as "settled" either.
			if (outOfPosition.Count == 0 && insideCluster.Count == 0 && ++clearChecks >= 3)
			{
				BuildArcoTargets();
				phase = arcoTargets.Count > 0 ? Phase.ArcoRaid : Phase.FinalAttack;
				Log($"choke cleared -> {phase} ({arcoTargets.Count} arco target(s)); choke stays empty (user decision)");
			}
		}

		// Once the force has shipped out there are no reserves left to speak of -- the beach guard has
		// no beach to guard on this side of the water, so everyone joins the attack.
		List<Actor> RaidGroup() =>
			(ashore ? Units : chokeReserve).Where(a => !Ops.CannotOrder(a)).ToList();

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
				// Once ashore the force is OUTSIDE our own base-side reachable set by definition, so
				// demanding ground reachability finds nothing and the mission concludes there is no
				// enemy at all -- it then walks off toward the map centre, which is across the water,
				// and piles up on the coast (2026-07-29: "10 unit(s) ashore -> resuming the attack"
				// followed immediately by "no reachable enemy -> holding map centre"). Same relaxation
				// the wave and scout missions already apply after landing.
				finalTarget = Ops.Intel.NearestEnemyYard(centre, requireReachable: !ashore);
				if (finalTarget == null)
				{
					var spawn = Ops.Intel.NearestEnemySpawn(centre, requireReachable: !ashore);
					if (spawn.HasValue)
					{
						Log($"final attack -> enemy spawn {spawn.Value}");
						AttackMoveGroup(bot, group, spawn.Value);
						return;
					}

					// Nothing reachable ON FOOT -- but the enemy may simply be across water. Book a
					// crossing for the whole force instead of settling on the map centre forever.
					if (!ashore && TryStartCrossing())
						return;

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

		// The whole starting force ships out together, choke reserve included -- so the reserves stop
		// being reserves and become the landing party.
		bool TryStartCrossing()
		{
			if (Ops.Info.FerryTypes.Length == 0 || Ops.Intel.EnemySpawns.Count == 0)
				return false;

			var target = Ops.Intel.EnemySpawns.MinBy(s => (s - Ops.BaseCentre()).LengthSquared);

			// Expansion priority: ahead of scouting, behind a formed attack wave -- the starting force
			// is valuable but it is not the main effort.
			ticket = Ops.Transit.Request(this, Units, target, AotTransitPriority.Expansion);
			if (ticket == null)
				return false;

			// Deliberately NOT clearing chokeReserve: RaidGroup() is built from it, so emptying it here
			// would leave the final attack with nobody once the force is ashore.
			phase = Phase.Ferrying;
			Log($"no land route to the enemy -> whole force crossing as ticket #{ticket.Id} " +
				$"(landing={ticket.To?.Shore})");
			return true;
		}

		void TickFerrying(IBot bot)
		{
			if (ticket == null || ticket.Cancelled)
			{
				phase = Phase.HoldCentre;
				return;
			}

			if (ticket.Complete || (ticket.Failed && ticket.Delivered.Count > 0))
			{
				// Ashore: from here reachability from our own base says nothing useful, so the final
				// attack re-targets from where the force actually stands.
				ashore = true;
				Log($"{ticket.Delivered.Count} unit(s) ashore -> resuming the attack");
				ticket = null;
				finalTarget = null;
				phase = Phase.FinalAttack;
				return;
			}

			if (ticket.Failed)
			{
				Log("could not get the starting force across -> holding map centre");
				ticket = null;
				phase = Phase.HoldCentre;
			}
		}

		void TickHoldCentre(IBot bot)
		{
			var group = RaidGroup();
			if (group.Count == 0)
				return;

			// The map centre is a HOME-side fallback. A force that has crossed cannot walk there, so
			// sending it that way just marches it into the sea; it holds where it landed instead.
			var anchor = ashore ? Centroid(group) : Ops.Intel.MapCentreFallback;
			HoldAt(bot, group, anchor, Ops.Info.GuardLeashRadius);
		}
	}

	// ======================================================================
	// Module 2: Regular Attack Waves.
	// 75% static faction core + 25% adaptive; budget escalates per wave;
	// GDI retreats at WaveRetreatLossPercent (unit count), Nod fights to the end.
	// ======================================================================
	public sealed class AotRegularWaveMission : AotMissionWithOrders
	{
		// Spare units are welcome: a wave is a loose formation, extra bodies simply join the attack.
		//
		// EXCEPT once the wave has crossed water. A reinforcement is produced at home and has no land
		// route to a wave standing on the far shore, so it just stands in the base -- but it counts as
		// part of the wave, so it drags the group centroid back home, makes the straggler and stall
		// logic fire forever, and the whole wave is eventually abandoned as "stuck" while it is in
		// fact fighting perfectly well (2026-07-29: "landed as one group" followed by ten rounds of
		// "wave stalled at 135,149", which is the AI's own base). It also blocked the next wave from
		// ever being scheduled, since the scheduler only starts one when no wave is running.
		public override bool AcceptsReinforcements => !ashore;

		enum Phase { Forming, Ferrying, Executing, Retreating }

		Phase phase = Phase.Forming;
		readonly int index;
		readonly bool useSecondaryRoute;
		int formingTicks;
		int executingTicks;
		int initialCount;
		Actor targetActor;
		CPos? targetCell;
		bool ecoWave;
		bool composed;

		// Secondary-route escalation (User 2026-07-22): after enough consecutive failures, route
		// through a distinct secondary approach before continuing to the actual target, instead of
		// letting normal pathfinding default back to the (already-failing) primary route.
		CPos? routeWaypoint;
		bool waypointReached;

		// Naval crossing (no land route to the enemy). The wave owns no ships at all: it books a ticket
		// with the module's transit service and waits for it to be fully delivered. That "fully" is
		// what makes the wave land CLOSED -- the vessels shuttle independently, the wave simply does
		// not attack until its last unit is ashore. See ai-transit-system.md.
		AotTransitTicket ticket;
		bool ashore;

		public AotRegularWaveMission(AotOperationsBotModule ops, int index, bool useSecondaryRoute)
			: base(ops, $"wave-{index}")
		{
			this.index = index;
			this.useSecondaryRoute = useSecondaryRoute;
		}

		public override void OnUnitAssigned(Actor a)
		{
			// The transit service owns every ship now; a vessel filed here would be stranded.
			if (ReturnStrayNavalSupport(a))
				return;

			base.OnUnitAssigned(a);

			// A reinforcement that turns up while the wave is CROSSING has to cross as well -- put it
			// on the booking. Without this it counts as part of the wave but stays at home: it drags
			// the group centroid back across the water, so the straggler logic decides the wave is
			// scattered and keeps ordering it to regroup instead of attacking, forever (2026-07-29:
			// "landed as one group" at 46,114, then "wave stalled at 79,129", which is halfway home).
			if (ticket != null && !ticket.Finished && !ticket.Delivered.Contains(a))
				ticket.Waiting.Add(a);
		}

		void FinishWave()
		{
			Ops.Transit.Cancel(ticket);
			ticket = null;

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

			// Adaptive block counters the observed enemy; everything else is the "static" composition,
			// filled either by the new per-slot system (User 2026-07-31) or, if a faction hasn't
			// configured any slots yet, the legacy tank/light/support share split (GDI, for now).
			var adaptCount = n * info.WaveAdaptiveSharePercent / 100;
			var maxStatic = Math.Max(0, info.WaveMaxUnits - adaptCount);

			var slots = Ops.WaveSlots();
			var picked = slots.Count > 0
				? ComposeSlots(slots, tier, mult, maxStatic)
				: ComposeRoles(n - adaptCount, mult, maxStatic);

			if (picked == null)
			{
				Log("no wave role buildable yet -> wave skipped");
				FinishWave();
				return;
			}

			var adaptChain = AdaptiveChain();

			Log($"compose tier={tier} n={n} mult={mult:F2} " +
				$"static=[{string.Join(", ", picked.Select(p => $"{p.Name}:{p.Count}"))}] adaptive={adaptCount}");

			// Pool first (leftovers from earlier waves), then production.
			foreach (var p in picked)
			{
				if (p.Count <= 0)
					continue;

				var fromPool = Ops.TakeFromPool(p.Chain, p.Count);
				Ops.AssignFromPool(this, fromPool);
				if (p.Count - fromPool.Count > 0)
					Ops.QueueRequest(this, p.Name, p.Chain, p.Count - fromPool.Count);
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

		// New slot system (User 2026-07-31): each slot is an independent unit chain with its own
		// per-age-tier Min (always requested if buildable) and Max (hard ceiling, even with full budget
		// escalation) -- replaces the old single-winner-per-role design, where two distinct vehicle
		// lines sharing one role chain (NOD's Tank role mixing TTNK and LTNK) could never both appear:
		// whichever line had an always-buildable base variant permanently starved the other out.
		List<(string Name, string[] Chain, int Count)> ComposeSlots(
			List<(string Name, string[] Chain, int[] Min, int[] Max)> slots, int tier, double mult, int maxStatic)
		{
			var info = Ops.Info;

			// "Wealthy" Age0 override (User 2026-07-31: "wenn in age 0 über 2000 credits: dann sollte
			// welle größer sein: 3-4 TTNK, 2-3 V2" -- since narrowed to 2 TTNK / 1 V2 as the wealthy
			// numbers too, see FIX history). Age1+ already scales via budget escalation below and never
			// needs this. -1 on either bound means that slot's override isn't configured -- keep the
			// normal per-tier value.
			var wealthy = tier == 0 && Ops.AvailableCash() > info.WaveWealthyCashThreshold;

			var resolved = new List<(string Name, string[] Chain, int Cost, int Min, int Max)>();
			foreach (var s in slots)
			{
				var max = tier < s.Max.Length ? s.Max[tier] : s.Max.Length > 0 ? s.Max[^1] : 0;
				var min = tier < s.Min.Length ? s.Min[tier] : 0;

				if (wealthy)
				{
					if (s.Name == "slot3" && info.WaveSlot3MinWealthy >= 0 && info.WaveSlot3MaxWealthy >= 0)
						(min, max) = (info.WaveSlot3MinWealthy, info.WaveSlot3MaxWealthy);
					else if (s.Name == "slot5" && info.WaveSlot5MinWealthy >= 0 && info.WaveSlot5MaxWealthy >= 0)
						(min, max) = (info.WaveSlot5MinWealthy, info.WaveSlot5MaxWealthy);
				}

				if (max <= 0)
				{
					Log($"{s.Name} skipped: Max=0 at tier {tier}");
					continue;
				}

				var variant = Ops.FirstBuildable(s.Chain);
				if (variant == null)
				{
					// Diagnostic (User 2026-07-31: "ist er WIRKLICH in der Lage, das zu aktivieren?" --
					// LTNK never appeared in a whole session's log despite AFLD existing and Age1 being
					// active). Distinguishes "nothing in the chain is a known actor at all" (a typo in
					// the chain) from "every variant's Prerequisites currently fail" (the real, expected
					// case while e.g. waiting on AFLD or an age gate) -- the latter is otherwise
					// indistinguishable from the former without reading FirstBuildable's own source.
					var known = s.Chain.Where(c => Ops.World.Map.Rules.Actors.ContainsKey(c)).ToList();
					Log($"{s.Name} skipped: nothing buildable in [{string.Join(", ", s.Chain)}] " +
						$"(known actors: [{string.Join(", ", known)}], tier={tier})");

					// Follow-up diagnostic (User 2026-08-01: LTNK's own Prerequisites check out -- Age1 +
					// AFLD both confirmed present in-game -- yet it's still never buildable). LTNK/TTNK-
					// flame/toxin/V2-napalm/toxic/Arty all share the SAME single Starport
					// (BulkProductionQueue) category: unlike a regular queue (one item building, still
					// visible as "buildable" for the NEXT slot), BuildableItems() on a Bulk queue returns
					// EMPTY while its cart is full OR a delivery is in flight -- for the ENTIRE ~60s
					// DeliveryDelay. If something ELSE sharing this queue (Module 5's continuous garrison
					// refill, another wave's own request) happens to have it in that state at the exact
					// moment THIS wave composes (once, not retried), every Starport-routed slot silently
					// loses its one shot for the whole wave. Dumps the queue's own state to confirm.
					foreach (var category in known
						.Select(c => Ops.World.Map.Rules.Actors[c].TraitInfoOrDefault<BuildableInfo>())
						.Where(bi => bi != null)
						.SelectMany(bi => bi.Queue)
						.Distinct())
					{
						var bulk = Ops.Player.PlayerActor.TraitsImplementing<BulkProductionQueue>()
							.FirstOrDefault(q => q.Info.Type == category);
						if (bulk != null)
							Log($"  starport diag [{category}]: cart={bulk.GetActorsReadyForDelivery().Count} " +
								$"deliveryInProgress={bulk.HasDeliveryStarted()} buildableNow={bulk.BuildableItems().Count()}");
					}
					continue;
				}

				min = Math.Min(min, max);
				var cost = Ops.World.Map.Rules.Actors[variant].TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 500;
				resolved.Add((s.Name, s.Chain, cost, min, max));
			}

			if (resolved.Count == 0)
				return null;

			var counts = resolved.ToDictionary(r => r.Name, r => r.Min);

			// Budget escalation: fill every slot toward its OWN Max, proportionally, biggest deficit
			// first -- "ab Age2 einfach mehr von jeder Einheit, wenn Cashflow passt" (User 2026-07-31).
			var baseBudget = resolved.Sum(r => counts[r.Name] * r.Cost);
			var budget = (int)(baseBudget * mult);
			var spent = baseBudget;
			while (spent < budget && counts.Values.Sum() < maxStatic)
			{
				var candidates = resolved.Where(r => counts[r.Name] < r.Max).ToList();
				if (candidates.Count == 0)
					break;

				var pick = candidates.MinBy(r => counts[r.Name] * 1.0 / r.Max);
				if (spent + pick.Cost > budget)
					break;

				counts[pick.Name]++;
				spent += pick.Cost;
			}

			return resolved.Select(r => (r.Name, r.Chain, counts[r.Name])).ToList();
		}

		// Legacy role/share composition (unchanged behaviour) -- used only while a faction has no
		// WaveSlot1-5 configured (GDI, for now; see AotOperationsBotModule.WaveSlots).
		List<(string Name, string[] Chain, int Count)> ComposeRoles(int staticCount, double mult, int maxStatic)
		{
			var info = Ops.Info;
			var roles = new List<(string Role, string[] Chain, int Share, int Cost)>();
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
				roles.Add((role, chain, share, cost));
			}

			if (roles.Count == 0)
				return null;

			var totalShare = roles.Sum(r => r.Share);
			var counts = roles.ToDictionary(r => r.Role, r => Math.Max(0, staticCount * r.Share / Math.Max(1, totalShare)));
			while (counts.Values.Sum() < staticCount)
				counts[roles[0].Role]++;

			var baseBudget = roles.Sum(r => counts[r.Role] * r.Cost);
			var budget = (int)(baseBudget * mult);
			var spent = baseBudget;
			while (spent < budget && counts.Values.Sum() < maxStatic)
			{
				var role = roles.MinBy(r => counts[r.Role] * 100.0 / Math.Max(1, r.Share));
				if (spent + role.Cost > budget)
					break;

				counts[role.Role]++;
				spent += role.Cost;
			}

			return roles.Select(r => (r.Role, r.Chain, counts[r.Role])).ToList();
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

		CPos RallyPoint() => Ops.GarrisonMusterPoint();

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
					$"crossing booked as ticket #{ticket.Id} (landing={ticket.To?.Shore}), " +
					$"{(Ops.HasNavalProduction() ? "vessels requested" : "waiting for naval production")}");
			}
			else
			{
				phase = Phase.Executing;

				if (useSecondaryRoute)
				{
					routeWaypoint = FindSecondaryApproachGate();
					Log($"launch: {initialCount} unit(s), eco={ecoWave}, target={DescribeTarget()}, " +
						$"secondary route via {(routeWaypoint.HasValue ? routeWaypoint.Value.ToString() : "none found -> primary route")}");
				}
				else
					Log($"launch: {initialCount} unit(s), eco={ecoWave}, target={DescribeTarget()}");
			}
		}

		// The highest-scored approach that ISN'T the primary chokepoint. Normal pathfinding always
		// picks the cheapest route (the primary choke, since that's what makes it primary), so
		// routing a wave through a genuinely different gate requires an explicit waypoint leg.
		// Returns null if no distinct approach exists (e.g. single-entrance map) -- the wave then
		// just launches via the primary route as usual (User 2026-07-22 spec).
		CPos? FindSecondaryApproachGate()
		{
			var choke = Ops.ChokeProvider?.Chokepoint;
			if (Ops.ApproachProvider == null || !choke.HasValue)
				return null;

			return Ops.ApproachProvider.BaseApproaches
				.Where(a => a.Gate != choke.Value)
				.OrderByDescending(a => (int)a.Type)
				.Select(a => (CPos?)a.Gate)
				.FirstOrDefault();
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
		// No ground path to the enemy: hand the wave to the shared convoy helper, which finds the
		// embark/landing shore on our ships' own water and requests transports plus escorts.
		bool TryStartFerry()
		{
			if (Ops.Info.FerryTypes.Length == 0 || Ops.Intel.EnemySpawns.Count == 0)
				return false;

			var enemyRef = Ops.Intel.EnemySpawns.MinBy(s => (s - Ops.BaseCentre()).LengthSquared);

			// Highest priority class: a wave outranks scouts and expansion squads at the quay, so it is
			// not cut in half by a scout squad boarding between two of its tanks.
			ticket = Ops.Transit.Request(this, Units, enemyRef, AotTransitPriority.AttackWave);
			return ticket != null;
		}

		void TickFerrying(IBot bot)
		{
			// The transit service moves the wave: staging ground -> boarding lane -> ships -> the far
			// staging ground, where it gathers. Nothing here touches a ship.
			if (ticket == null || ticket.Cancelled)
			{
				Outcome = AotMissionOutcome.Failure;
				FinishWave();
				return;
			}

			if (ticket.Complete)
			{
				// On the far shore now -- outside our own base-side reachable set by definition, so the
				// target search must stop demanding ground reachability from here on.
				ashore = true;
				ticket = null;
				ChooseTarget();
				phase = Phase.Executing;
				Log($"landed as one group -> target={DescribeTarget()}");
				return;
			}

			if (ticket.Failed)
			{
				// Some of the wave may already be across. Fighting on with a fraction beats abandoning
				// units on a foreign shore with no orders -- that is how they ended up standing around.
				if (ticket.Delivered.Count > 0)
				{
					// Anyone still on our side will never reach the fight, and keeping them on the
					// roster drags the group centroid back home -- which makes the straggler and stall
					// logic fire forever instead of attacking. Hand them back to the pool.
					var stranded = Units.Where(u => !ticket.Delivered.Contains(u)).ToList();
					if (stranded.Count > 0)
					{
						Ops.ReleaseToPool(this, stranded);
						foreach (var u in stranded)
							Units.Remove(u);
					}

					Log($"crossing failed with {ticket.Delivered.Count} unit(s) ashore -> attacking " +
						$"short-handed ({stranded.Count} left behind, returned to the pool)");
					ashore = true;
					ticket = null;
					ChooseTarget();
					phase = Phase.Executing;
					return;
				}

				Outcome = AotMissionOutcome.Failure;
				FinishWave();
			}
		}
		void TickExecuting(IBot bot)
		{
			if (Units.Count == 0)
			{
				Log("wave wiped out");
				Outcome = AotMissionOutcome.Failure;
				FinishWave();
				return;
			}

			// Stall safety net (User 2026-07-22): a wave that neither wipes out, retreats, nor
			// reaches/loses its target (e.g. stuck on an unreachable secondary waypoint) would
			// otherwise sit here forever, permanently blocking the scheduler from ever creating
			// another wave or air raid.
			executingTicks += Ops.Info.MissionInterval;
			if (executingTicks >= Ops.Info.WaveExecutingTimeout)
			{
				Log($"executing timed out ({Units.Count} unit(s) stuck) -> wave abandoned");
				Outcome = AotMissionOutcome.Failure;
				FinishWave();
				return;
			}

			// GDI: retreat at the configured loss percentage (by unit count). Nod: 0 = never.
			var retreat = Ops.Info.WaveRetreatLossPercent;
			if (retreat > 0 && initialCount > 0 && (initialCount - Units.Count) * 100 / initialCount >= retreat)
			{
				Log($"loss threshold reached ({Units.Count}/{initialCount}) -> retreating");
				Outcome = AotMissionOutcome.Failure;
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
					Outcome = AotMissionOutcome.Success;
					FinishWave();
					return;
				}
			}

			// Clear obstacles blocking the wave's own choke exit (User 2026-07-22): a plain
			// AttackMove order never destroys a blocking tree/wall on its own -- only the starting-
			// units choke reserve had that logic until now, so a wave routing through the same
			// choke could get stuck there indefinitely (eventually caught by the stall timeout
			// above, but that just abandons the wave rather than letting it actually leave).
			var choke = Ops.ChokeProvider?.Chokepoint;
			if (choke.HasValue)
			{
				var holdR2 = Ops.Info.ChokepointHoldRadius * Ops.Info.ChokepointHoldRadius;
				var atChoke = Units.Where(a => !Ops.CannotOrder(a) && (a.Location - choke.Value).LengthSquared <= holdR2).ToList();
				if (atChoke.Count > 0 && ClearNearbyObstacles(bot, choke.Value, atChoke).Count > 0)
					return;
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

			if (routeWaypoint.HasValue && !waypointReached && (centre - routeWaypoint.Value).LengthSquared <= 25)
			{
				waypointReached = true;
				Log("secondary route waypoint reached -> continuing to target");
			}

			var goal = routeWaypoint.HasValue && !waypointReached ? routeWaypoint.Value : targetActor?.Location ?? targetCell.Value;

			// Nothing moved and nobody died for a long time -- the wave is wedged rather than fighting.
			// Break the stuck activities so the loop below can give everyone a fresh order; the executing
			// timeout still decides when to give up entirely.
			if (NoProgress(Units.Count * 31 + centre.X * 7 + centre.Y, Ops.Info.StallRecoveryTicks))
			{
				Log($"wave stalled at {centre} -> re-issuing attack orders toward {goal}");
				foreach (var a in Units.Where(u => !Ops.CannotOrder(u)))
					bot.QueueOrder(new Order("Stop", a, false));

				return;
			}

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
				SendDamagedToRepair(bot);
				FinishWave();
				return;
			}

			AttackMoveGroup(bot, Units, home);
		}

		// User 2026-07-22: retreating survivors that are damaged and have a repair facility
		// available are sent there before rejoining the pool. Issuing the order here and then
		// releasing to the pool right after is fine -- ReleaseToPool only updates our own
		// ownership bookkeeping, it doesn't touch the unit's current activity, so the Repair
		// order keeps running on its own once the unit is pool-owned.
		void SendDamagedToRepair(IBot bot)
		{
			if (Ops.Info.RepairTypes.Count == 0)
				return;

			foreach (var a in Units)
			{
				if (Ops.CannotOrder(a))
					continue;

				var health = a.TraitOrDefault<Health>();
				if (health == null || health.HP >= health.MaxHP)
					continue;

				var fix = Ops.NearestOwnRepairFacility(a.Location);
				if (fix != null)
					bot.QueueOrder(new Order("Repair", a, Target.FromActor(fix), false));
			}
		}
	}

	// ======================================================================
	// Module 3: Scout Expeditions. Two light vehicles per group, up to two
	// spawn areas each, edge/ring sweep for maximum fog reveal, then an
	// observation post. Groups self-regenerate via the module scheduler.
	// ======================================================================
	public sealed class AotScoutMission : AotMissionWithOrders
	{
		enum Phase { Forming, Touring, Ferrying, Posting, Holding }

		public readonly int GroupIndex;
		readonly List<CPos> spawns;
		readonly int groupTarget;
		readonly CPos? stagingCell;
		Phase phase = Phase.Forming;
		int spawnCursor;
		List<CPos> currentWaypoints;
		int waypointCursor;
		CPos post;

		// Naval crossing (User 2026-07-22): a spawn with no ground route gets crossed to instead of
		// just skipped. Stage 2 of the ferry rebuild: this mission no longer owns transports at all --
		// it books a ticket with the module's transit service and waits. See ai-transit-system.md.
		AotTransitTicket ticket;
		bool ashoreForCurrentSpawn;

		// Ticket booked but still HELD: the group keeps scouting reachable ground on our own side
		// until a vessel is genuinely assigned -- see TickTouring's no-ground-route branch.
		bool ferryArmed;

		// Which spawn the booked crossing is FOR. Booking advances spawnCursor on purpose (so the
		// squad tours other spawns while it waits for a ship), which means the cursor no longer points
		// at the spawn we are crossing to. Without remembering it here, a squad that landed went
		// straight to Posting and held station on the beach instead of sweeping the spawn it had just
		// crossed the sea for -- confirmed 2026-07-29: all five scouts ashore, standing still.
		int ferrySpawnIndex = -1;

		public AotScoutMission(AotOperationsBotModule ops, int groupIndex, List<CPos> spawns)
			: base(ops, $"scouts-{groupIndex}")
		{
			GroupIndex = groupIndex;
			this.spawns = spawns;

			// Allocated ONCE per mission (not re-queried every tick) so concurrent missions don't
			// all pile onto the same single cell -- see AllocateInfantryStagingCell.
			stagingCell = ops.AllocateInfantryStagingCell();

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

		public override void OnUnitAssigned(Actor a)
		{
			// The transit service owns every ship now; a vessel filed here would be stranded.
			if (ReturnStrayNavalSupport(a))
				return;

			base.OnUnitAssigned(a);
		}

		public override void Tick(IBot bot)
		{
			if (Units.Count == 0 && Ops.OpenRequests(this) == 0)
			{
				Ops.Transit.Cancel(ticket);
				ticket = null;
				Done = true;
				return;
			}

			// A vessel is genuinely on its way for the spawn we couldn't reach by land -- release the
			// hold so the service can walk the squad to the staging ground, and stop touring.
			if (ferryArmed && phase != Phase.Ferrying && ticket != null && ticket.VesselAssigned)
			{
				Log("transport assigned -> handing the squad to the transit service");
				ticket.Hold = false;
				phase = Phase.Ferrying;
			}

			switch (phase)
			{
				case Phase.Forming:
					// Actively gather every unit at this mission's OWN staging cell each tick --
					// pool-reused units (left over from an earlier mission) never walked there in
					// the first place, and this mission's cell is distinct from other concurrently
					// forming missions' cells (see AllocateInfantryStagingCell).
					if (stagingCell.HasValue)
						foreach (var a in Units)
						{
							var d2 = (a.Location - stagingCell.Value).LengthSquared;
							Log($"[AotGather] {a.Info.Name}@{a.Location}#{a.ActorID} idle={a.IsIdle} " +
								$"activity={a.CurrentActivity?.GetType().Name ?? "none"} staging={stagingCell.Value} dist²={d2}");

							if (!a.IsIdle)
								continue;
							if (d2 > 4)
							{
								Log($"[AotGather] -> re-issuing Move for #{a.ActorID}");
								MoveUnit(bot, a, stagingCell.Value, false);
							}
						}

					if (Units.Count >= groupTarget)
					{
						phase = Phase.Touring;
						Log($"touring {spawns.Count} spawn(s)");
					}

					break;

				case Phase.Touring: TickTouring(bot); break;

				case Phase.Ferrying:
					// The service moves the squad from here: staging ground -> boarding lane -> ship ->
					// far staging ground. This mission only watches its ticket.
					if (ticket == null || ticket.Cancelled)
					{
						ferryArmed = false;
						phase = Phase.Touring;
					}
					else if (ticket.Complete)
					{
						// Ashore on the FAR side now, outside our own base-side reachable set by
						// definition -- resume touring the same spawn from here with the reachability
						// requirement dropped, exactly like AotRegularWaveMission's `ashore` flag.
						Log($"crossed: {ticket.Delivered.Count} unit(s) ashore -> sweeping the spawn we crossed for");
						ashoreForCurrentSpawn = true;
						ferryArmed = false;
						currentWaypoints = null;
						ticket = null;

						// Rewind to the spawn this crossing was for. Booking had already advanced the
						// cursor past it so the squad could tour meanwhile.
						if (ferrySpawnIndex >= 0)
						{
							spawnCursor = ferrySpawnIndex;
							ferrySpawnIndex = -1;
						}

						phase = Phase.Touring;
					}
					else if (ticket.Failed)
					{
						// Part of the squad is already across: scout with those rather than abandoning
						// them on a foreign shore with no orders -- that is exactly how units ended up
						// standing around. Same rule the attack wave uses.
						if (ticket.Delivered.Count > 0)
						{
							Log($"crossing failed with {ticket.Delivered.Count} unit(s) ashore -> sweeping short-handed");
							ashoreForCurrentSpawn = true;
							currentWaypoints = null;
							ferryArmed = false;
							ticket = null;

							if (ferrySpawnIndex >= 0)
							{
								spawnCursor = ferrySpawnIndex;
								ferrySpawnIndex = -1;
							}

							phase = Phase.Touring;
							break;
						}

						// Nobody made it -- give up on this one spawn (not the whole mission) and move
						// on to the next, same as the pre-existing "no waypoints" skip behaviour.
						Log("crossing failed -> skipping this spawn");
						spawnCursor++;
						currentWaypoints = null;
						ashoreForCurrentSpawn = false;
						ferryArmed = false;
						ticket = null;
						phase = Phase.Touring;
					}

					break;

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

				var spawn = spawns[spawnCursor];
				currentWaypoints = RouteAround(spawn, requireReachable: !ashoreForCurrentSpawn);
				waypointCursor = 0;
				Log($"sweep spawn {spawnCursor + 1}/{spawns.Count} @ {spawn} ({currentWaypoints.Count} waypoint(s))");

				if (currentWaypoints.Count == 0)
				{
					// No ground route to this spawn at all -- try crossing to it instead of just
					// skipping it (User 2026-07-22), same as a regular attack wave would. Only
					// attempted once per spawn (not yet ashore there) so a genuinely land-locked-with-
					// no-coast spawn doesn't retry the ferry search forever.
					//
					// Book a HELD ticket: it requests a vessel right away, but the service must not walk
					// the squad to the staging ground yet -- doing so would park it there doing nothing
					// for as long as the AI takes to afford a ship, on a poor spawn the whole match
					// (User 2026-07-24: "mindestens die scouts sollten ja zügig los marschieren während
					// solche gebäude gebaut werden"). Keep touring reachable ground instead; Tick()
					// releases the hold the moment a vessel is actually assigned.
					if (!ashoreForCurrentSpawn && !ferryArmed)
					{
						ticket = Ops.Transit.Request(this, Units, spawn, AotTransitPriority.Scout, hold: true);
						if (ticket != null)
						{
							ferryArmed = true;
							ferrySpawnIndex = spawnCursor;
							Log("no ground route -> crossing booked, scouting reachable ground meanwhile");
						}
					}

					spawnCursor++;
					currentWaypoints = null;
					ashoreForCurrentSpawn = false;
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
				ashoreForCurrentSpawn = false;
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

			// Order whoever is ready, rather than waiting for the WHOLE squad to fall idle. Demanding
			// all-idle meant a single straggler -- one unit still walking in from the landing point, or
			// caught in a nudge -- held the entire group in place, which is why a squad that had fully
			// landed still stood on the beach doing nothing (User 2026-07-29: "die scouts landen an und
			// stehen erstmal lange rum ... es waren alle 5 drüben"). Cohesion is preserved anyway: the
			// waypoint only advances on the group's CENTROID, so the leaders wait up at each stop.
			var ready = live.Where(a => a.IsIdle).ToList();
			if (ready.Count > 0)
				AttackMoveGroup(bot, ready, target);
		}

		// requireReachable: true for the normal own-base-side sweep (a cell must be in the AI's own
		// ground-reachable set to route through it). Once ferried ashore on the far side of water,
		// the group is by definition OUTSIDE that set -- same relaxation AotRegularWaveMission uses
		// once `ashore` is true -- so this drops to false for that spawn's sweep.
		List<CPos> RouteAround(CPos spawn, bool requireReachable)
		{
			// Perimeter ring around the spawn, reachable cells only, in angular order —
			// coverage over shortest path (map/cliff edges naturally bound the ring).
			var cells = AotOpsUtils.Ring(spawn, Ops.Info.ScoutRingRadius)
				.Where(c => !requireReachable || Ops.Intel.IsReachable(c))
				.OrderBy(c => Math.Atan2(c.Y - spawn.Y, c.X - spawn.X))
				.ToList();

			var waypoints = new List<CPos>();
			for (var i = 0; i < cells.Count; i += Math.Max(1, cells.Count / 6))
				waypoints.Add(cells[i]);

			if (waypoints.Count == 0 && (!requireReachable || Ops.Intel.IsReachable(spawn)))
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
		enum Phase { Forming, Moving, Ferrying, Capturing, Holding }

		public readonly Actor Derrick;
		readonly CPos? stagingCell;
		Phase phase = Phase.Forming;
		int formingTicks;

		// Fixed squad composition, built in EXACTLY this order (User 2026-07-31): "engineer-squads
		// sollte nurnoch bestehen aus: 1 rocketeer, 1 engineer, 1 flame, 1 mg (genau in der reihenfolge
		// zu bauen)". Replaces the old "engineer(1) + rocket(2) + mg(2) all requested at once" mix --
		// requesting all 5 in parallel meant they competed for the same single-slot production queue in
		// insertion order with no regard for which role actually mattered, and DID (confirmed via log,
		// FIX 2026-07-31h) starve the mandatory engineer for an entire match once something else kept
		// cutting in. Sequential requesting means each step only enters the queue once the previous one
		// is actually in Units, so there's never more than one open request from this mission at a time.
		//
		// CORRECTION (User 2026-07-31, same day): the Flame Trooper slot needs `anyhq` (Radar/HQ) --
		// unlike Rocket/Engineer/MG, which only need the barracks itself -- so it would have blocked the
		// WHOLE sequential chain (nothing after an unmet step ever gets requested) until Radar exists,
		// deadlocking the entire squad far longer than intended. Replaced with a second MG instead: 1
		// rocketeer, 1 engineer, 1 mg, 1 mg -- every slot buildable off the barracks alone.
		readonly (string Role, string[] Chain)[] squadOrder;
		int squadStep;

		// Derricks across water are valid targets (User 2026-07-24) -- the squad books a crossing with
		// the transit service exactly like a scout group or an attack wave, and owns no ships itself.
		AotTransitTicket ticket;
		bool ashore;

		public AotDerrickMission(AotOperationsBotModule ops, Actor derrick)
			: base(ops, $"derrick-{derrick.ActorID}")
		{
			Derrick = derrick;

			// Allocated ONCE per mission (not re-queried every tick) so concurrent missions (up to
			// DerrickMaxTargets derrick squads plus scout groups) don't all pile onto the same
			// single cell -- see AllocateInfantryStagingCell.
			stagingCell = ops.AllocateInfantryStagingCell();

			squadOrder =
			[
				("rocket", ops.Info.RocketInfantryTypes),
				("engineer", ops.Info.EngineerTypes),
				("mg", ops.Info.MgInfantryTypes),
				("mg", ops.Info.MgInfantryTypes),
			];

			// FIX (User 2026-07-31, "alle AI ... noch nicht ein infanterist produziert"): the OLD
			// constructor queued all its requests immediately, so by the first Tick() call
			// Ops.OpenRequests(this) was already > 0. Moving requests into TickSquadForming (only
			// reached via TickForming, inside the Phase.Forming switch case) meant the mission's own
			// early self-termination check in Tick() -- "Units.Count == 0 && OpenRequests(this) == 0",
			// evaluated BEFORE that switch -- fired on tick ONE, before the squad ever got a chance to
			// request its first unit: both sides were trivially true for a mission that had just been
			// constructed. Confirmed via log: 5 different derricks "acquired" in one session, none ever
			// producing a single unit. Requesting the first slot here, synchronously, closes that gap.
			TickSquadForming();
		}

		// Requests the NEXT squad slot only once the previous one has actually arrived (or is skipped,
		// for an empty chain) -- see squadOrder above. Called every Forming tick; a no-op once the whole
		// squad has been requested (squadStep reaches the end).
		//
		// Counts occurrences rather than a plain Units.Any() check: two steps (both "mg") share the exact
		// same chain, so a naive Any() would see the FIRST MG that arrives and immediately consider the
		// SECOND step already satisfied too, silently dropping the squad to 3 units.
		void TickSquadForming()
		{
			while (squadStep < squadOrder.Length)
			{
				var (role, chain) = squadOrder[squadStep];
				if (chain.Length == 0)
				{
					squadStep++;
					continue;
				}

				var wantedSoFar = squadOrder.Take(squadStep + 1).Count(s => s.Chain == chain);
				var haveSoFar = Units.Count(a => chain.Contains(a.Info.Name));
				if (haveSoFar >= wantedSoFar)
				{
					squadStep++;
					continue;
				}

				if (Ops.OpenRequests(this, role) == 0)
					Ops.QueueRequest(this, role, chain, 1);

				return;
			}
		}

		Actor Engineer() => Units.FirstOrDefault(a => Ops.Info.EngineerTypes.Contains(a.Info.Name));

		List<Actor> Escorts() => Units.Where(a => !Ops.Info.EngineerTypes.Contains(a.Info.Name) && !Ops.CannotOrder(a)).ToList();

		// The permanent guard at a CAPTURED derrick (User 2026-07-30): the one mid-mission exception to
		// the global self-defense pass -- everything still forming/transiting/capturing is excluded like
		// every other mission, but a standing guard post is conceptually the same as Base Defense's own
		// garrison, just anchored somewhere other than the base.
		public List<Actor> HoldingEscorts() => phase == Phase.Holding ? Escorts() : [];

		// Priority pause for AotBaseBuilderBotModule (User 2026-07-31: "sobald barracks steht, direkt bei
		// engineer-squad sein und erst danach weitere gebäude gebaut werden (cashflow!) mit timeout,
		// falls das nicht klappt") -- true while this squad is still being assembled AND within the
		// timeout, so building construction briefly steps aside for the very first derrick squad's cash
		// instead of racing it. Bounded by DerrickFormingTimeout so a genuinely stuck squad (e.g. no
		// reachable derrick at all) can never block base construction forever.
		public bool StillFormingWithinTimeout => phase == Phase.Forming && formingTicks < Ops.Info.DerrickFormingTimeout;

		public override void OnUnitAssigned(Actor a)
		{
			// The transit service owns every ship now; a vessel filed here would be stranded.
			if (ReturnStrayNavalSupport(a))
				return;

			base.OnUnitAssigned(a);
		}

		public override void Tick(IBot bot)
		{
			if (Derrick.IsDead || !Derrick.IsInWorld)
			{
				Log("derrick destroyed -> mission over");
				Ops.Transit.Cancel(ticket);
				ticket = null;
				Finish();
				return;
			}

			if (Units.Count == 0 && Ops.OpenRequests(this) == 0)
			{
				Ops.Transit.Cancel(ticket);
				ticket = null;
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

				case Phase.Ferrying:
				{
					if (ticket == null || ticket.Cancelled)
					{
						Log("crossing lost -> mission over");
						Finish();
					}
					else if (ticket.Complete)
					{
						// On the far shore now -- outside our own base-side reachable set by definition,
						// so the move leg must stop asking for ground reachability from here on.
						ashore = true;
						ticket = null;
						phase = Phase.Moving;
						Log("ashore -> resuming approach to the derrick");
					}
					else if (ticket.Failed)
					{
						// The engineer is what the squad is FOR: without it ashore there is nothing to
						// capture with, so a partial delivery is only worth continuing if it made it.
						var engineerAshore = ticket.Delivered.Any(a => Ops.Info.EngineerTypes.Contains(a.Info.Name));
						if (engineerAshore)
						{
							Log($"crossing failed but the engineer is ashore ({ticket.Delivered.Count} unit(s)) -> continuing");
							ashore = true;
							ticket = null;
							phase = Phase.Moving;
						}
						else
						{
							Log("could not get the capture squad across -> mission over");
							ticket = null;
							Finish();
						}
					}

					break;
				}

				case Phase.Capturing: TickCapturing(bot); break;
				case Phase.Holding: HoldAt(bot, Units, Derrick.Location, Ops.Info.GuardLeashRadius); break;
			}
		}

		void TickForming(IBot bot)
		{
			formingTicks += Ops.Info.MissionInterval;

			TickSquadForming();

			// Actively gather every unit at this mission's OWN staging cell each tick -- pool-reused
			// units (left over from an earlier mission) never walked there in the first place, and
			// this mission's cell is distinct from other concurrently forming missions' cells (see
			// AllocateInfantryStagingCell).
			if (stagingCell.HasValue)
				foreach (var a in Units)
				{
					var d2 = (a.Location - stagingCell.Value).LengthSquared;
					Log($"[AotGather] {a.Info.Name}@{a.Location}#{a.ActorID} idle={a.IsIdle} " +
						$"activity={a.CurrentActivity?.GetType().Name ?? "none"} staging={stagingCell.Value} dist²={d2}");

					if (!a.IsIdle)
						continue;
					if (d2 > 4)
					{
						Log($"[AotGather] -> re-issuing Move for #{a.ActorID}");
						MoveUnit(bot, a, stagingCell.Value, false);
					}
				}

			// Wait for the full 4-man squad at the barracks rally point; the engineer is mandatory.
			// The timeout is a last-resort safety net only (shared actor types with other missions,
			// e.g. Scout, can genuinely delay production well past a short timeout) -- escorts may
			// launch short-handed if it's ever hit.
			if (Engineer() != null && (Ops.OpenRequests(this) == 0 || formingTicks >= Ops.Info.DerrickFormingTimeout))
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

			// No land route to the derrick: cross with a transport instead of walking into the water
			// (User 2026-07-24). Only attempted before the crossing -- once ashore we are on the far
			// side and reachability from our own base no longer says anything useful.
			if (!ashore && !Ops.Intel.IsReachable(Derrick.Location))
			{
				ticket = Ops.Transit.Request(this, Units, Derrick.Location, AotTransitPriority.Expansion);
				if (ticket != null)
				{
					phase = Phase.Ferrying;
					Log($"no ground route to derrick @ {Derrick.Location} -> crossing booked as ticket #{ticket.Id}");
					return;
				}

				Log($"derrick @ {Derrick.Location} is unreachable and no crossing exists -> mission over");
				Finish();
				return;
			}

			// Move as one grouped order (engineer + escorts together), not per-unit chains -- a
			// straggler that joins mid-move (e.g. a delayed MG escort finally produced) simply
			// waits at the rally point until the group is idle again, then joins the next leg.
			var group = Escorts().Append(engineer).Where(a => !Ops.CannotOrder(a)).ToList();
			if (group.Count > 0 && group.All(a => a.IsIdle))
				AttackMoveGroup(bot, group, Derrick.Location);

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

	// ======================================================================
	// Air Raid (escalation tier, User 2026-07-22): once ground waves have failed
	// WaveAirRaidAfterFailures times in a row (primary + secondary route attempts) and a helipad
	// is owned, build AirRaidCountPerAge[tier] helicopters and send them straight at the enemy construction
	// yard, ignoring ground reachability. Success/failure feeds back into the same streak that
	// picked this tier, so the escalation loop resets to ground waves either way (see
	// AotOperationsBotModule's mission-completion handling).
	// ======================================================================
	public sealed class AotAirRaidMission : AotMissionWithOrders
	{
		enum Phase { Forming, Executing }

		Phase phase = Phase.Forming;
		int formingTicks;
		int executingTicks;
		Actor targetActor;
		CPos? targetCell;

		public AotAirRaidMission(AotOperationsBotModule ops)
			: base(ops, "air-raid")
		{
			var variant = ops.FirstBuildable(ops.Info.AirRaidHelicopterTypes);
			if (variant == null)
			{
				Log("no air-raid helicopter buildable -> raid cancelled");
				Outcome = AotMissionOutcome.Failure;
				Done = true;
				return;
			}

			var perAge = ops.Info.AirRaidCountPerAge;
			var count = perAge.Length > 0 ? perAge[Math.Min(ops.AgeTier(), perAge.Length - 1)] : 4;

			var fromPool = ops.TakeFromPool(ops.Info.AirRaidHelicopterTypes, count);
			ops.AssignFromPool(this, fromPool);
			if (count - fromPool.Count > 0)
				ops.QueueRequest(this, "airraid", ops.Info.AirRaidHelicopterTypes, count - fromPool.Count);
		}

		public override void Tick(IBot bot)
		{
			if (Done)
				return;

			switch (phase)
			{
				case Phase.Forming: TickForming(bot); break;
				case Phase.Executing: TickExecuting(bot); break;
			}
		}

		void TickForming(IBot bot)
		{
			formingTicks += Ops.Info.MissionInterval;

			var open = Ops.OpenRequests(this);
			var launch = open == 0 && Units.Count > 0;
			if (!launch && formingTicks >= Ops.Info.AirRaidFormingTimeout)
				launch = Units.Count >= open;

			if (!launch && formingTicks >= Ops.Info.AirRaidFormingTimeout * 2)
			{
				if (Units.Count > 0)
					launch = true;
				else
				{
					Log("air-raid forming dead end -> raid cancelled");
					Outcome = AotMissionOutcome.Failure;
					Finish();
					return;
				}
			}

			if (!launch)
				return;

			ChooseTarget();
			phase = Phase.Executing;
			Log($"air raid launch: {Units.Count} helicopter(s), target={DescribeTarget()}");
		}

		string DescribeTarget() =>
			targetActor != null ? $"{targetActor.Info.Name}@{targetActor.Location}" : targetCell?.ToString() ?? "none";

		void ChooseTarget()
		{
			var centre = Centroid(Units);
			targetActor = Ops.Intel.NearestEnemyYard(centre, requireReachable: false);
			if (targetActor == null)
				targetCell = Ops.Intel.NearestEnemySpawn(centre, requireReachable: false);
		}

		void TickExecuting(IBot bot)
		{
			if (Units.Count == 0)
			{
				Log("air raid wiped out");
				Outcome = AotMissionOutcome.Failure;
				Finish();
				return;
			}

			executingTicks += Ops.Info.MissionInterval;
			if (executingTicks >= Ops.Info.AirRaidExecutingTimeout)
			{
				Log($"air raid executing timed out ({Units.Count} unit(s) stuck) -> raid abandoned");
				Outcome = AotMissionOutcome.Failure;
				Finish();
				return;
			}

			if (targetActor != null && (targetActor.IsDead || !targetActor.IsInWorld))
				targetActor = null;

			if (targetActor == null && targetCell == null)
			{
				ChooseTarget();
				if (targetActor == null && targetCell == null)
				{
					Log("air raid: no target left -> raid done");
					Outcome = AotMissionOutcome.Success;
					Finish();
					return;
				}
			}

			// Aircraft rest in a perpetual FlyIdle activity (not a null CurrentActivity) whenever
			// they have nothing to do -- unlike ground Mobile actors, IsIdle (CurrentActivity ==
			// null) is therefore structurally always false for them, so gating the AttackMove order
			// on IsIdle alone (as the ground wave logic does) means it is NEVER issued (User
			// 2026-07-22 root cause: the raid launches but the helicopters just hover forever).
			var goal = targetActor?.Location ?? targetCell.Value;
			foreach (var a in Units)
				if (a.IsIdle || a.CurrentActivity is FlyIdle)
					bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(Ops.World, goal), false));
		}
	}

	// ======================================================================
	// Bridge Repair (User-Briefing 2026-08-03, Age 0): "scannt map alle 5min, ob eine Bruecke kaputt
	// ist. Schickt dann ein engineer los, sie zu reparieren."
	//
	// One engineer per damaged bridge hut. The engineer is CONSUMED by the repair (RepairsBridges
	// uses EnterBehaviour.Dispose), so this mission never has anything to release afterwards --
	// unlike the derrick squad, whose engineer survivor goes back to the pool.
	//
	// Only E6 carries RepairsBridges in this mod, which is exactly what EngineerTypes lists, so the
	// existing engineer chain is reused as-is. Targets are LegacyBridgeHut actors: its CanRepair
	// property already encapsulates "damaged AND not already being repaired AND actually connected",
	// which is the same predicate RepairsBridges.ResolveOrder re-checks before queueing the activity
	// -- ordering anything else is silently dropped by the engine, so re-using CanRepair keeps the
	// two ends in agreement. BridgeIsDangling is additionally excluded for the same reason: the
	// order handler refuses those outright.
	// ======================================================================
	public sealed class AotBridgeRepairMission : AotMissionWithOrders
	{
		enum Phase { Forming, Moving, Repairing }

		public readonly Actor Hut;
		Phase phase = Phase.Forming;
		int formingTicks;
		int movingTicks;

		public AotBridgeRepairMission(AotOperationsBotModule ops, Actor hut)
			: base(ops, $"bridge-{hut.ActorID}")
		{
			Hut = hut;

			// Requested straight from the constructor, like the derrick squad does since FIX
			// 2026-07-31k: Tick()'s own "no units and no open requests -> done" check runs before the
			// phase switch, so a mission that has not asked for anything yet would kill itself on tick 1.
			if (Ops.Info.EngineerTypes.Length > 0)
				Ops.QueueRequest(this, "engineer", Ops.Info.EngineerTypes, 1);
		}

		Actor Engineer() => Units.FirstOrDefault(a => !Ops.CannotOrder(a));

		// Still repairable AND still worth sending someone to.
		//
		// ⚠️ CanRepair alone is NOT the right bar (User-Beobachtung 2026-08-03: "ein engineer hat eine
		// bereits reparierte bruecke repariert"). It only asks BridgeDamageState != Undamaged, and
		// AggregateDamageState returns the WORST span's ordinary DamageState -- so a single stray shot
		// leaving one span at Light already qualified, even though the bridge looks intact and is fully
		// drivable. Only a span at Dead is actually gone and makes the crossing impassable, which is
		// what "kaputt" meant in the briefing. Since the engineer is CONSUMED by the repair, sending
		// one for cosmetic damage is pure waste.
		public static bool NeedsRepair(Actor hut)
		{
			if (hut == null || hut.IsDead || !hut.IsInWorld)
				return false;

			var legacy = hut.TraitOrDefault<LegacyBridgeHut>();
			if (legacy != null)
				return legacy.CanRepair
					&& legacy.BridgeDamageState == DamageState.Dead
					&& !legacy.BridgeIsDangling;

			var modern = hut.TraitOrDefault<BridgeHut>();
			return modern != null && modern.BridgeDamageState == DamageState.Dead && !modern.Repairing;
		}

		public override void Tick(IBot bot)
		{
			if (Hut.IsDead || !Hut.IsInWorld)
			{
				Log("bridge hut gone -> mission over");
				Finish();
				return;
			}

			// Someone else (a human ally, or a second squad) got there first, or the span came back up
			// on its own -- no point walking an engineer into a repair the engine would refuse anyway.
			if (phase != Phase.Repairing && !NeedsRepair(Hut))
			{
				// Outcome bleibt bewusst Unknown: der Wellen-Eskalationszaehler in
				// AotOperationsBotModule wertet JEDEN gemeldeten Outcome aus (Success setzt
				// waveFailureStreak zurueck, Failure erhoeht ihn). Nur Wellen- und Luftangriffs-
				// Missionen duerfen daran drehen -- eine reparierte Bruecke sagt nichts darueber
				// aus, ob der Angriff am Chokepoint funktioniert.
				Log("bridge no longer needs repair -> releasing engineer");
				Finish();
				return;
			}

			if (Units.Count == 0 && Ops.OpenRequests(this) == 0)
			{
				// The engineer is consumed on a successful repair, so an empty squad in the Repairing
				// phase is the SUCCESS case, not a failure.
				if (phase == Phase.Repairing)
				{
					// Outcome bleibt Unknown -- siehe oben (Wellen-Eskalation nicht verfaelschen).
					Log("engineer consumed -> bridge repair underway");
					Finish();
				}
				else
				{
					Log("engineer lost before reaching the bridge -> mission over");
					Finish();
				}

				return;
			}

			switch (phase)
			{
				case Phase.Forming:
				{
					formingTicks += Ops.Info.MissionInterval;
					if (Engineer() != null)
					{
						phase = Phase.Moving;
						Log($"engineer en route to {Hut.Info.Name}@{Hut.Location}");
					}
					else if (formingTicks >= Ops.Info.BridgeFormingTimeout)
					{
						Log("no engineer available in time -> mission over");
						Finish();
					}

					break;
				}

				case Phase.Moving:
				{
					movingTicks += Ops.Info.MissionInterval;
					var engineer = Engineer();
					if (engineer == null)
						break;

					if ((engineer.Location - Hut.Location).LengthSquared <= Ops.Info.BridgeArriveRadius2)
					{
						phase = Phase.Repairing;
						break;
					}

					if (movingTicks >= Ops.Info.BridgeMovingTimeout)
					{
						Log($"engineer could not reach {Hut.Location} in time -> mission over");
						ReleaseEngineer();
						Finish();
						break;
					}

					if (engineer.IsIdle)
						MoveUnit(bot, engineer, Hut.Location, false);

					break;
				}

				case Phase.Repairing:
				{
					var engineer = Engineer();
					if (engineer != null && engineer.IsIdle)
						bot.QueueOrder(new Order("RepairBridge", engineer, Target.FromActor(Hut), false));

					break;
				}
			}
		}

		// Hands a still-living engineer back instead of stranding it wherever the mission gave up.
		void ReleaseEngineer()
		{
			var survivors = Units.Where(a => !Ops.CannotOrder(a)).ToList();
			if (survivors.Count == 0)
				return;

			foreach (var a in survivors)
				Units.Remove(a);

			Ops.ReleaseToPool(this, survivors);
		}
	}

	// ======================================================================
	// Engineer Raid (User-Briefing 2026-08-03). Two delivery flavours, one shared body:
	//
	//   Heli  (Age 1, after the refinery): squad + transport helicopter, approaches along the map
	//         edge to come at the enemy base from BEHIND, drops off, then captures.
	//   Ground(Age 2): squad + APC, same idea on wheels.
	//
	// Squad is 3 engineers + 2 rocket troopers in both cases (User: "wir aendern in 3 engineers, 2
	// rockettrooper ... analog zum boden-raid!"). The rocket troopers are not escort decoration:
	// they shoot a single fence cell open so the engineers can reach a walled-in building, which is
	// why fenced targets are explicitly allowed rather than filtered out.
	//
	// Requested sequentially (same reason as the derrick squad, FIX 2026-07-31h): five parallel
	// requests compete inside one single-slot infantry queue with no regard for which role matters,
	// and starved the mandatory engineer for a whole match once.
	// ======================================================================
	public abstract class AotEngineerRaidMission : AotMissionWithOrders
	{
		protected enum Phase { Forming, Loading, Delivering, Unloading, Raiding }

		protected Phase phase = Phase.Forming;
		protected int formingTicks;
		protected int phaseTicks;
		protected CPos dropCell;
		protected Actor target;

		readonly (string Role, string[] Chain)[] squadOrder;
		int squadStep;

		protected AotEngineerRaidMission(AotOperationsBotModule ops, string name)
			: base(ops, name)
		{
			squadOrder =
			[
				("engineer", ops.Info.EngineerTypes),
				("engineer", ops.Info.EngineerTypes),
				("engineer", ops.Info.EngineerTypes),
				("rocket", ops.Info.RocketInfantryTypes),
				("rocket", ops.Info.RocketInfantryTypes),
			];

			TickSquadForming();
		}

		protected abstract string[] TransportChain { get; }
		protected abstract int FormingTimeout { get; }

		// ⚠️ NEVER gate a transport order on IsIdle alone. Aircraft rest in a perpetual FlyIdle
		// activity rather than a null CurrentActivity, so IsIdle is structurally always false for
		// them -- the order then simply never goes out and the helicopter hovers where it is until
		// the mission times out. This is the exact trap AotAirRaidMission already documents (root
		// cause 2026-07-22, hit AGAIN here 2026-08-03: "er hing lange am rand ... ein zweiter hovert
		// grad in base"). Ground transports are unaffected but share the helper for symmetry.
		protected static bool ReadyForOrders(Actor a) => a.IsIdle || a.CurrentActivity is FlyIdle;

		// Gets the squad out before the mission lets go of it. Without this an abort while loaded
		// pools the transport WITH five infantry still inside -- they are in Units, so they count as
		// released, but physically they are gone (and the loaded transport then gets adopted as a
		// combat reinforcement, which is how a Chinook ended up following an attack wave).
		protected void AbortWithUnload(IBot bot)
		{
			var transport = Transport();
			var cargo = transport?.TraitOrDefault<Cargo>();
			if (transport != null && cargo != null && cargo.PassengerCount > 0)
				bot.QueueOrder(new Order("Unload", transport, false));

			Finish();
		}

		protected IEnumerable<Actor> Engineers() =>
			Units.Where(a => Ops.Info.EngineerTypes.Contains(a.Info.Name) && !Ops.CannotOrder(a));

		protected List<Actor> Rockets() =>
			Units.Where(a => Ops.Info.RocketInfantryTypes.Contains(a.Info.Name) && !Ops.CannotOrder(a)).ToList();

		protected Actor Transport() =>
			Units.FirstOrDefault(a => TransportChain.Contains(a.Info.Name) && !Ops.CannotOrder(a));

		protected List<Actor> Passengers() =>
			Units.Where(a => !TransportChain.Contains(a.Info.Name) && !Ops.CannotOrder(a)).ToList();

		// Counts occurrences rather than Any(): three steps share the engineer chain, so a naive
		// "do we have one" check would satisfy all three off a single arrival (derrick lesson).
		void TickSquadForming()
		{
			// TRANSPORT FIRST (User 2026-08-03: "heli & subterrain apc nicht als letztes sondern immer
			// als erstes zu bauen und dann das squad").
			//
			// It used to be requested LAST, on the reasoning that it is the expensive part and useless
			// without a squad to carry. That was backwards. The transport comes out of a DIFFERENT
			// queue (Aircraft.Nod / Starport) than the infantry, so asking for it up front costs the
			// squad nothing -- whereas asking for it last meant a squad that never finished assembling
			// never even got round to requesting it. Confirmed over a 5-bot session: every ground raid
			// ended on "squad never came together" and not a single subterranean APC was ever built,
			// because its request sat behind five sequential infantry arrivals that never all landed.
			//
			// Waiting for the transport to actually EXIST before asking for infantry also means a raid
			// that simply cannot get one (no helipad, upgrade not bought) burns no infantry at all,
			// instead of parking five bodies in the base until the forming timeout.
			if (TransportChain.Length > 0 && Transport() == null)
			{
				if (Ops.OpenRequests(this, "transport") == 0)
					Ops.QueueRequest(this, "transport", TransportChain, 1);

				return;
			}

			while (squadStep < squadOrder.Length)
			{
				var (role, chain) = squadOrder[squadStep];
				if (chain.Length == 0)
				{
					squadStep++;
					continue;
				}

				var wantedSoFar = squadOrder.Take(squadStep + 1).Count(s => s.Chain == chain);
				var haveSoFar = Units.Count(a => chain.Contains(a.Info.Name));
				if (haveSoFar >= wantedSoFar)
				{
					squadStep++;
					continue;
				}

				if (Ops.OpenRequests(this, role) == 0)
					Ops.QueueRequest(this, role, chain, 1);

				return;
			}
		}

		protected bool SquadReady() =>
			Engineers().Any() && Transport() != null && Ops.OpenRequests(this) == 0;

		// A cell "behind" the target as seen from our own base: keep going past the target along the
		// same line, then snap to something actually passable. Distance grows with the danger tier so
		// a raid that got shot down last time comes down further out (User 2026-08-03).
		protected CPos BehindTarget(CPos targetCell, int distance)
		{
			var from = Ops.BaseCentre();
			var dx = targetCell.X - from.X;
			var dy = targetCell.Y - from.Y;
			var len = Math.Max(1, (int)Math.Sqrt((dx * dx) + (dy * dy)));
			var ideal = new CPos(targetCell.X + (dx * distance / len), targetCell.Y + (dy * distance / len));

			if (Ops.World.Map.Contains(ideal) && Ops.Intel.IsPassable(ideal))
				return ideal;

			for (var r = 1; r <= 8; r++)
				foreach (var c in AotOpsUtils.Ring(ideal, r))
					if (Ops.World.Map.Contains(c) && Ops.Intel.IsPassable(c))
						return c;

			return targetCell;
		}

		// Capture anything of the enemy's that is actually capturable, nearest to the drop first.
		// Fenced buildings are deliberately NOT excluded -- that is what the rocket troopers are for.
		protected Actor PickCaptureTarget(CPos near)
		{
			return Ops.World.Actors
				.Where(a => !a.IsDead && a.IsInWorld
					&& a.Owner != Ops.Player
					&& Ops.Player.RelationshipWith(a.Owner) != PlayerRelationship.Ally
					&& a.Info.HasTraitInfo<BuildingInfo>()
					&& a.Info.HasTraitInfo<CapturableInfo>())
				.OrderBy(a => (a.Location - near).LengthSquared)
				.FirstOrDefault();
		}

		protected void TickForming(IBot bot)
		{
			formingTicks += Ops.Info.MissionInterval;
			TickSquadForming();

			if (SquadReady())
			{
				phase = Phase.Loading;
				phaseTicks = 0;
				Log($"squad ready ({Passengers().Count} passengers) -> loading");
			}
			else if (formingTicks >= FormingTimeout)
			{
				Log("squad never came together -> mission over");
				Finish();
			}
		}

		// Loading is identical for both flavours: order everyone aboard, wait until the hold is full
		// enough (or the timeout gives up on stragglers) -- promotion is observed via PassengerCount,
		// not by trusting the order, exactly like the naval transit service does.
		protected void TickLoading(IBot bot)
		{
			phaseTicks += Ops.Info.MissionInterval;

			var transport = Transport();
			if (transport == null)
			{
				Log("transport lost while loading -> mission over");
				Finish();
				return;
			}

			var cargo = transport.TraitOrDefault<Cargo>();
			var aboard = cargo?.PassengerCount ?? 0;
			var waiting = Passengers();

			if (waiting.Count == 0 || aboard >= Ops.Info.RaidSquadSize || phaseTicks >= Ops.Info.RaidLoadTimeout)
			{
				if (aboard == 0)
				{
					Log("nobody boarded -> mission over");
					Finish();
					return;
				}

				phase = Phase.Delivering;
				phaseTicks = 0;
				Log($"loaded {aboard} -> delivering to {dropCell}");
				return;
			}

			foreach (var u in waiting)
				if (u.IsIdle)
					bot.QueueOrder(new Order("EnterTransport", u, Target.FromActor(transport), false));
		}

		protected void TickUnloading(IBot bot)
		{
			phaseTicks += Ops.Info.MissionInterval;

			var transport = Transport();
			var cargo = transport?.TraitOrDefault<Cargo>();
			if (transport == null || cargo == null || cargo.PassengerCount == 0)
			{
				phase = Phase.Raiding;
				phaseTicks = 0;
				Log("squad ashore -> raiding");
				return;
			}

			if (ReadyForOrders(transport))
				bot.QueueOrder(new Order("Unload", transport, false));

			if (phaseTicks >= Ops.Info.RaidUnloadTimeout)
			{
				Log("unload timed out -> raiding with whoever got out");
				phase = Phase.Raiding;
				phaseTicks = 0;
			}
		}

		// Engineers walk in and capture; rocket troopers force-fire whatever blocks the way (the fence
		// cell). Same force-fire pattern the derrick squad uses -- plain AttackMove never shoots a wall.
		protected void TickRaiding(IBot bot)
		{
			phaseTicks += Ops.Info.MissionInterval;

			var engineers = Engineers().ToList();
			if (engineers.Count == 0)
			{
				Log("no engineers left -> mission over");
				Finish();
				return;
			}

			if (target == null || target.IsDead || !target.IsInWorld || target.Owner == Ops.Player)
			{
				target = PickCaptureTarget(dropCell);
				if (target == null)
				{
					Log("nothing capturable in reach -> mission over");
					Finish();
					return;
				}

				Log($"capture target -> {target.Info.Name}@{target.Location}");
			}

			var blockers = Ops.World.FindActorsInCircle(Ops.World.Map.CenterOfCell(target.Location), WDist.FromCells(3))
				.Where(a => !a.IsDead && a.IsInWorld
					&& a.Info.HasTraitInfo<LineBuildInfo>()
					&& a.Info.HasTraitInfo<HealthInfo>()
					&& a.Owner != Ops.Player
					&& Ops.Player.RelationshipWith(a.Owner) != PlayerRelationship.Ally)
				.OrderBy(a => (a.Location - target.Location).LengthSquared)
				.ToList();

			var rockets = Rockets();
			if (blockers.Count > 0 && rockets.Count > 0)
				for (var i = 0; i < rockets.Count; i++)
					if (rockets[i].IsIdle)
						ForceAttack(bot, rockets[i], blockers[i % blockers.Count]);

			foreach (var e in engineers)
				if (e.IsIdle)
					bot.QueueOrder(new Order("CaptureActor", e, Target.FromActor(target), true));

			if (phaseTicks >= Ops.Info.RaidExecutingTimeout)
			{
				Log("raid timed out -> mission over");
				Finish();
			}
		}
	}

	// ======================================================================
	// Heli Engineer Raid (Age 1, after the refinery). Flies the squad around the map edge so it
	// arrives behind the enemy base rather than straight through its air defence.
	//
	// Danger analysis (User 2026-08-03: "wenn beim ersten anlauf beim yard von AA abgeschossen wird,
	// dann beim 2. versuch entfernter absetzen. dann wirkt es natuerlicher"): the drop distance is
	// NOT a per-mission constant but a per-BOT tier held on the Ops module. Losing the transport (or
	// taking it home badly damaged) before the squad is out raises the tier, so the next raid comes
	// down further away on its own.
	// ======================================================================
	public sealed class AotHeliRaidMission : AotEngineerRaidMission
	{
		readonly List<CPos> route = [];
		int leg;
		bool transportSurvivedDelivery;

		public AotHeliRaidMission(AotOperationsBotModule ops, Actor enemyYard)
			: base(ops, $"heli-raid-{enemyYard.ActorID}")
		{
			target = null;

			var tier = ops.HeliRaidDropTier;
			var distance = ops.Info.HeliRaidDropDistance + (tier * ops.Info.HeliRaidDropDistanceStep);
			dropCell = BehindTarget(enemyYard.Location, distance);

			BuildEdgeRoute(ops.BaseCentre());

			Log($"heli raid planned: drop {dropCell} (tier {tier}, {distance} cells behind), " +
				$"edge route [{string.Join(" -> ", route)}]");
		}

		// Flies ALONG THE MAP EDGE (User 2026-08-03: "einfach nur zur kartenecke koennte ihn genau
		// durch eine basis fuehren. ich sagte AM KARTENRAND ENTLANG").
		//
		// A single "nearest corner" waypoint was wrong: both the leg out to that corner and the leg
		// from it to the drop zone are straight lines across open map, and either can cut right
		// through a base. Instead the route leaves our base to the nearest edge, then follows the
		// perimeter -- corner by corner, the short way round -- to the edge point nearest the drop
		// zone, and only then turns inland. The only leg that crosses open ground is the last one.
		void BuildEdgeRoute(CPos from)
		{
			var b = Ops.World.Map.Bounds;

			// One cell inside the bounds: the outermost row/column is often unreachable map border.
			var left = b.Left + 1;
			var top = b.Top + 1;
			var right = b.Right - 2;
			var bottom = b.Bottom - 2;
			if (right <= left || bottom <= top)
			{
				// Degenerate/tiny map -- nothing sensible to hug, fly direct.
				route.Add(dropCell);
				return;
			}

			var w = right - left;
			var h = bottom - top;
			var perimeter = 2 * (w + h);

			// Projects a cell onto the nearest edge and returns both the edge cell and how far along
			// the perimeter it sits, measured clockwise from the top-left corner.
			(CPos Cell, int S) ToEdge(CPos c)
			{
				var x = Math.Clamp(c.X, left, right);
				var y = Math.Clamp(c.Y, top, bottom);

				var dLeft = x - left;
				var dRight = right - x;
				var dTop = y - top;
				var dBottom = bottom - y;
				var best = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));

				if (best == dTop)
					return (new CPos(x, top), x - left);
				if (best == dRight)
					return (new CPos(right, y), w + (y - top));
				if (best == dBottom)
					return (new CPos(x, bottom), w + h + (right - x));

				return (new CPos(left, y), w + h + w + (bottom - y));
			}

			var start = ToEdge(from);
			var end = ToEdge(dropCell);

			route.Add(start.Cell);

			// Corner scalars in clockwise order, and the corner cells they belong to.
			var cornerS = new[] { 0, w, w + h, w + h + w };
			var cornerCell = new[]
			{
				new CPos(left, top), new CPos(right, top), new CPos(right, bottom), new CPos(left, bottom)
			};

			var cw = ((end.S - start.S) % perimeter + perimeter) % perimeter;
			var goClockwise = cw <= perimeter - cw;

			// Emit every corner strictly between start and end, in travel direction, so the transport
			// actually traces the border instead of cutting the chord across the map.
			for (var i = 0; i < 4; i++)
			{
				// Walk the four corners in travel order starting after the start point.
				var idx = goClockwise ? i : 3 - i;
				var s = cornerS[idx];
				var along = goClockwise
					? ((s - start.S) % perimeter + perimeter) % perimeter
					: ((start.S - s) % perimeter + perimeter) % perimeter;
				var total = goClockwise ? cw : perimeter - cw;

				if (along > 0 && along < total)
					route.Add(cornerCell[idx]);
			}

			route.Add(end.Cell);
			route.Add(dropCell);
		}

		CPos CurrentWaypoint => route[Math.Min(leg, route.Count - 1)];

		protected override string[] TransportChain => Ops.Info.HeliRaidTransportTypes;
		protected override int FormingTimeout => Ops.Info.HeliRaidFormingTimeout;

		public override void Tick(IBot bot)
		{
			if (Units.Count == 0 && Ops.OpenRequests(this) == 0)
			{
				Finish();
				return;
			}

			switch (phase)
			{
				case Phase.Forming: TickForming(bot); break;
				case Phase.Loading: TickLoading(bot); break;

				case Phase.Delivering:
				{
					phaseTicks += Ops.Info.MissionInterval;
					var transport = Transport();
					if (transport == null)
					{
						// Shot down with the squad aboard: exactly the case the tier exists for.
						Ops.RaiseHeliRaidDropTier();
						Log("transport lost on approach -> next raid drops further out");
						Finish();
						return;
					}

					if ((transport.Location - dropCell).LengthSquared <= Ops.Info.RaidArriveRadius2)
					{
						transportSurvivedDelivery = true;
						phase = Phase.Unloading;
						phaseTicks = 0;
						break;
					}

					if (phaseTicks >= Ops.Info.HeliRaidDeliverTimeout)
					{
						Ops.RaiseHeliRaidDropTier();
						Log($"could not reach the drop zone (stuck on leg {leg}/{route.Count - 1} " +
							$"at {transport.Location}) -> next raid drops further out");
						AbortWithUnload(bot);
						break;
					}

					// Advance leg by leg instead of queueing the whole route at once: a helicopter
					// that gets pushed off course still resumes at the leg it is actually on, and the
					// log shows where along the border it currently is.
					if (leg < route.Count - 1
						&& (transport.Location - CurrentWaypoint).LengthSquared <= Ops.Info.RaidArriveRadius2)
					{
						leg++;
						Log($"edge leg {leg}/{route.Count - 1} -> {CurrentWaypoint}");
					}

					if (ReadyForOrders(transport))
						MoveUnit(bot, transport, CurrentWaypoint, false);

					break;
				}

				case Phase.Unloading: TickUnloading(bot); break;

				case Phase.Raiding:
				{
					// Send the empty helicopter home instead of leaving it hovering over enemy AA.
					var transport = Transport();
					if (transport != null && transportSurvivedDelivery && ReadyForOrders(transport))
						MoveUnit(bot, transport, Ops.BaseCentre(), false);

					TickRaiding(bot);
					break;
				}
			}
		}
	}

	// ======================================================================
	// Ground Engineer Raid (Age 2). Same squad, delivered by APC (or Subterranean APC, whichever the
	// chain resolves to first -- the sub APC simply tunnels past the front line, which needs no extra
	// logic here). Drives to a staging cell behind the enemy base, unloads and captures.
	//
	// Unlike the heli version there is no drop-distance tier: a ground transport that dies has been
	// stopped by the front line, not by air defence, and dropping further out would not help.
	// ======================================================================
	public sealed class AotGroundRaidMission : AotEngineerRaidMission
	{
		public AotGroundRaidMission(AotOperationsBotModule ops, Actor enemyYard)
			: base(ops, $"ground-raid-{enemyYard.ActorID}")
		{
			target = null;
			dropCell = BehindTarget(enemyYard.Location, ops.Info.GroundRaidDropDistance);
			Log($"ground raid planned: unload at {dropCell}");
		}

		protected override string[] TransportChain => Ops.Info.GroundRaidTransportTypes;
		protected override int FormingTimeout => Ops.Info.GroundRaidFormingTimeout;

		public override void Tick(IBot bot)
		{
			if (Units.Count == 0 && Ops.OpenRequests(this) == 0)
			{
				Finish();
				return;
			}

			switch (phase)
			{
				case Phase.Forming: TickForming(bot); break;
				case Phase.Loading: TickLoading(bot); break;

				case Phase.Delivering:
				{
					phaseTicks += Ops.Info.MissionInterval;
					var transport = Transport();
					if (transport == null)
					{
						Log("transport destroyed on the way -> mission over");
						Finish();
						return;
					}

					if ((transport.Location - dropCell).LengthSquared <= Ops.Info.RaidArriveRadius2)
					{
						phase = Phase.Unloading;
						phaseTicks = 0;
						break;
					}

					if (phaseTicks >= Ops.Info.GroundRaidDeliverTimeout)
					{
						Log("could not reach the unload point -> unloading where we stand");
						phase = Phase.Unloading;
						phaseTicks = 0;
						break;
					}

					if (ReadyForOrders(transport))
						MoveUnit(bot, transport, dropCell, false);

					break;
				}

				case Phase.Unloading: TickUnloading(bot); break;
				case Phase.Raiding: TickRaiding(bot); break;
			}
		}
	}

	// ======================================================================
	// Module 5: Base Defense (User 2026-07-22). Permanent standing garrison, created once at game
	// start and never finishes. Sized roughly to one regular attack wave (WaveVehiclesPerAge
	// [AgeTier()]); ProtectionMinProduced of that is guaranteed via dedicated production, the rest
	// opportunistically adopted from the shared pool (idle survivors from finished missions) at no
	// extra cost. Idle garrison waits at the shared muster point outside the buildable area
	// (Ops.GarrisonMusterPoint -- the same "halfway to choke" spot regular waves stage at) so it
	// never blocks construction. Periodically scans a radius around every protected building for
	// enemies; on detection, a threat-proportional slice of the garrison (never the whole thing
	// for a lone scout) is dispatched to engage while the rest keeps holding the muster point --
	// this covers flanking/rear attacks too, since detection isn't tied to the front chokepoint.
	// ======================================================================
	public sealed class AotBaseDefenseMission : AotMissionWithOrders
	{
		// The garrison takes anything -- see AotMission.AcceptsReinforcements.
		public override bool AcceptsReinforcements => true;

		readonly HashSet<Actor> responding = [];
		int scanTicks;

		public AotBaseDefenseMission(AotOperationsBotModule ops)
			: base(ops, "base-defense") { }

		int TargetSize()
		{
			var perAge = Ops.Info.WaveVehiclesPerAge;
			return perAge.Length > 0 ? perAge[Math.Min(Ops.AgeTier(), perAge.Length - 1)] : Ops.Info.ProtectionMinProduced;
		}

		public override void Tick(IBot bot)
		{
			responding.RemoveWhere(Ops.CannotOrder);

			MaintainGarrison();

			scanTicks += Ops.Info.MissionInterval;
			if (scanTicks >= Ops.Info.ProtectionScanInterval)
			{
				scanTicks = 0;
				ScanAndRespond(bot);
			}

			// Everyone not currently dispatched holds at the actual chokepoint, not the halfway muster
			// point (User 2026-07-31, screenshot: a lone FTUR sat at the choke while a large idle
			// infantry blob sat elsewhere in the base, not defending the gate at all). Falls back to the
			// muster point only when there's genuinely no chokepoint to hold (e.g. GDI has no planner
			// instance yet).
			var holding = Units.Where(a => !responding.Contains(a)).ToList();
			if (holding.Count > 0)
			{
				// Same fix as the starting-units chokeReserve (user-fund 2026-08-01): this garrison
				// ALSO holds at the raw choke cell, which is exactly where the gate-defence cluster is
				// independently biased to build -- a unit standing inside the cluster's own footprint
				// must be pushed clear before HoldAt is allowed to treat it as "in position".
				var insideCluster = holding.Where(a => Ops.IsInsideGateCluster(a.Location)).ToList();
				if (insideCluster.Count > 0)
				{
					var baseCentre = Ops.BaseCentre();
					foreach (var a in insideCluster)
					{
						var behind = AotBasePlannerBotModule.Cardinal(new CVec(baseCentre.X - a.Location.X, baseCentre.Y - a.Location.Y));
						bot.QueueOrder(new Order("AttackMove", a, Target.FromCell(Ops.World, a.Location + (behind * 3)), false));
					}
				}

				var outside = holding.Where(a => !insideCluster.Contains(a)).ToList();
				if (outside.Count > 0)
					HoldAt(bot, outside, Ops.ChokeProvider?.Chokepoint ?? Ops.GarrisonMusterPoint(), Ops.Info.GuardLeashRadius);
			}
		}

		void MaintainGarrison()
		{
			var target = TargetSize();

			// Before wave 1 has ever been scheduled, keep it cheap/simple (ProtectionFloorTypes)
			// while vehicle production is still uncertain. Once wave 1 proves the full tank/light/
			// support role mix is buildable, maintain the ENTIRE garrison that way instead (User
			// 2026-07-22: "gemischt mit V2 etc", not just tanks) -- and since this re-evaluates
			// every tick against the current Units count, a destroyed defender is automatically
			// replaced, not just produced once.
			var floor = Ops.FirstWaveScheduled() ? target : Math.Min(Ops.Info.ProtectionMinProduced, target);
			var pending = Units.Count + Ops.OpenRequests(this);
			if (pending < floor)
			{
				if (Ops.FirstWaveScheduled())
					RequestMixedRoles(floor - pending);
				else
				{
					var chain = Ops.Info.ProtectionFloorTypes.Length > 0 ? Ops.Info.ProtectionFloorTypes : Ops.Info.WaveLightTypes;
					if (chain.Length > 0)
						Ops.QueueRequest(this, "floor", chain, floor - pending);
				}
			}

			// Opportunistic top-up from the shared pool, no production cost.
			var shortfall = target - Units.Count - Ops.OpenRequests(this);
			if (shortfall > 0)
			{
				var fromPool = Ops.TakeAnyFromPool(shortfall);
				Ops.AssignFromPool(this, fromPool);
			}
		}

		// Splits `count` across the tank/light/support role chains by the same share percentages
		// regular waves use, so the garrison ends up mixed (e.g. NOD gets V2 artillery via
		// WaveSupportTypes) rather than a single unit type.
		void RequestMixedRoles(int count)
		{
			if (count <= 0)
				return;

			var roles = new List<(string[] Chain, int Share)>
			{
				(Ops.Info.WaveTankTypes, Ops.Info.WaveTankShare),
				(Ops.Info.WaveLightTypes, Ops.Info.WaveLightShare),
				(Ops.Info.WaveSupportTypes, Ops.Info.WaveSupportShare),
			}.Where(r => r.Chain.Length > 0).ToList();

			if (roles.Count == 0)
				return;

			var totalShare = roles.Sum(r => r.Share);
			var remaining = count;
			foreach (var r in roles)
			{
				var n = Math.Min(remaining, Math.Max(0, count * r.Share / Math.Max(1, totalShare)));
				if (n <= 0)
					continue;

				Ops.QueueRequest(this, "reserve", r.Chain, n);
				remaining -= n;
			}

			// Rounding leftover goes to the first (highest-share) role.
			if (remaining > 0)
				Ops.QueueRequest(this, "reserve", roles[0].Chain, remaining);
		}

		void ScanAndRespond(IBot bot)
		{
			var buildings = Ops.World.Actors
				.Where(a => a.Owner == Ops.Player && !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<BuildingInfo>()
					&& (Ops.Info.ProtectionTypes.Count == 0 || Ops.Info.ProtectionTypes.Contains(a.Info.Name)))
				.ToList();

			// includeAir: true (User 2026-07-31, screenshot: idle Rocket Troopers ignoring attacking
			// Hinds) -- this scan was blind to aircraft the whole time, the exact same class of bug
			// already fixed for GlobalUnitSelfDefense's pool-based scan but never carried over here.
			// Module 5's garrison holds Rocket Troopers specifically so it can answer air raids; without
			// this they were never even considered threats worth responding to.
			var threats = new HashSet<Actor>();
			foreach (var b in buildings)
				foreach (var a in Ops.World.FindActorsInCircle(b.CenterPosition, WDist.FromCells(Ops.Info.ProtectionScanRadius)))
					if (AotOpsUtils.IsPreferredEnemyUnit(Ops.Player, a, includeAir: true) && a.CanBeViewedByPlayer(Ops.Player))
						threats.Add(a);

			if (threats.Count == 0)
			{
				responding.Clear();
				return;
			}

			var available = Units.Where(a => !Ops.CannotOrder(a)).ToList();
			if (available.Count == 0)
				return;

			// NOT Math.Clamp: it throws when min > max, and that is exactly what happens once the
			// garrison is smaller than ProtectionMinResponse (default 2). With a single defender left,
			// Clamp(value, 2, 1) crashed the match -- reported 2026-07-27 right after the player killed
			// most of the AI's units. Availability is the hard ceiling; the minimum only applies as far
			// as there are units to send.
			var want = Math.Min(available.Count,
				Math.Max(Ops.Info.ProtectionMinResponse, threats.Count * Ops.Info.ProtectionResponseRatio));
			var threatCentre = ThreatCentroid(threats);
			var responders = available.OrderBy(a => (a.Location - threatCentre).LengthSquared).Take(want).ToList();

			responding.Clear();
			foreach (var r in responders)
				responding.Add(r);

			AttackMoveGroup(bot, responders, threatCentre);
			Log($"threat detected ({threats.Count} enemy unit(s) near base) -> dispatching {responders.Count}/{available.Count} responder(s) to {threatCentre}");
		}

		static CPos ThreatCentroid(IEnumerable<Actor> actors)
		{
			var list = actors.ToList();
			var x = 0;
			var y = 0;
			foreach (var a in list)
			{
				x += a.Location.X;
				y += a.Location.Y;
			}

			return new CPos(x / list.Count, y / list.Count);
		}
	}
}
