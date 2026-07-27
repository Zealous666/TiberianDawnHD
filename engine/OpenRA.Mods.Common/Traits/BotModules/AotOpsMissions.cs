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
					&& !a.Info.HasTraitInfo<LegacyBridgeHutInfo>())
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

			var inPosition = readyUnits.Where(a => (a.Location - choke.Value).LengthSquared <= holdR2).ToList();
			var outOfPosition = readyUnits.Where(a => (a.Location - choke.Value).LengthSquared > holdR2).ToList();

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
	// Shared naval-ferry helper (User 2026-07-22: "nimm die Routine auch in die Scout-Infanterie
	// auf, auch die sollten mit den Vessels übersetzen wollen um ihren Auftrag zu beginnen").
	// Transports are tracked separately from the passenger units they carry, reused across
	// missions via the pool. Any mission whose units need to cross water with no ground route can
	// own one of these and drive it every tick; it never touches the mission's own Phase enum or
	// Outcome directly, it just reports back InProgress/Ashore/Failed so the caller decides what
	// that means for its own state machine. This is a fresh, standalone extraction of the exact
	// embark/dock/board/disembark logic proven out (through several rounds of on-the-ground
	// debugging) in AotRegularWaveMission -- that mission's own fields/methods are deliberately
	// left untouched rather than refactored to share this, since it was mid-verification when this
	// was written and a refactor risks reintroducing bugs that were just fixed.
	// ======================================================================
	static class FerryUtils
	{
		// Ask our own units standing on the cells around `ship` to step aside, using the engine's own
		// nudge path (NotifyBlocker -> INotifyBlockingMove -> an idle friendly Mobile queues a Nudge).
		//
		// Nothing does this on its own for a transport: the engine only nudges when something PATHS
		// THROUGH an idle blocker, and a passenger stepping off a ship does not path -- it just needs
		// an adjacent cell to be free. So a couple of units loitering at the ramp silently stall the
		// whole unload even though free cells exist a tile further out (User 2026-07-24: "landingdock
		// hat schon freie nachbarzellen ... es blockieren höchstens andere einheiten").
		public static void NudgeAround(Actor ship, int radius = 1)
		{
			var cells = new List<CPos>();
			for (var dy = -radius; dy <= radius; dy++)
				for (var dx = -radius; dx <= radius; dx++)
				{
					if (dx == 0 && dy == 0)
						continue;

					var c = ship.Location + new CVec(dx, dy);
					if (ship.World.Map.Contains(c))
						cells.Add(c);
				}

			ship.NotifyBlocker(cells);
		}
	}

	sealed class FerryHelper
	{
		public enum Result { InProgress, Ashore, Failed }

		// Once the ship is within this squared-distance of its dock cell, treat it as "docked":
		// stop nudging it (so it holds still for loading/unloading) and let the engine's Passenger
		// activity handle boarding. Must be TIGHT (2 == 1 cell): the dock cell is the water cell
		// orthogonally touching the troops' beach, so only there is a land unit actually adjacent
		// enough to board. With 2 cells of slack the ship parked short at a cliff neighbour and
		// declared itself docked, and no unit could ever reach it (User 2026-07-23: "vessel fährt
		// wieder an falsche Stelle"; log showed idle@35,11 dist2=4 docked but pending stuck at 5).
		public const int DockedRadius2 = 2;

		readonly AotMission owner;
		readonly AotOperationsBotModule ops;
		readonly Action<string> log;

		readonly List<Actor> ferries = [];
		readonly HashSet<Actor> inTransit = [];
		readonly HashSet<Actor> boarded = [];
		readonly HashSet<Actor> ferriedAshore = [];
		CPos? embarkCell;
		CPos? landingCell;
		CPos? embarkDock;
		CPos? landingDock;
		int ferryTicks;
		int ferryDiagLog;
		bool ferryRequested;

		// Where TryStart was anchored, kept so the embark point can be re-derived once a real ship
		// exists (see RevalidateDock).
		CPos startBaseCentre;
		bool dockRevalidated;

		public IReadOnlyCollection<Actor> FerriedAshore => ferriedAshore;
		public CPos? LandingCell => landingCell;

		// True once an actual transport has been assigned to this ferry. Callers use it to avoid
		// marching a group to the beach to stand around waiting for a ship that does not exist yet.
		public bool HasShip => ferries.Count > 0;

		// Ships that have already begun the crossing with a load. Once a ship commits it must not turn
		// back just because another passenger showed up at the home shore -- without this latch a ship
		// that was already almost at the far shore sailed all the way back (observed 2026-07-24:
		// dist2ToLanding=4 with cargo=1, then back to embark because inTransit went 0 -> 1).
		readonly HashSet<Actor> crossing = [];

		// A few cells inland from the landing cell, in the direction pointing away from the water, so
		// disembarked units clear the exit instead of parking on it.
		CPos? DisembarkRally()
		{
			if (landingCell == null)
				return null;

			var dock = landingDock ?? landingCell;
			var inland = landingCell.Value - dock.Value;
			if (inland == CVec.Zero)
				return landingCell;

			var rally = landingCell.Value + inland * 3;
			return ops.World.Map.Contains(rally) ? rally : landingCell;
		}

		public FerryHelper(AotMission owner, AotOperationsBotModule ops, Action<string> log)
		{
			this.owner = owner;
			this.ops = ops;
			this.log = log;
		}

		// Call from the owning mission's OnUnitAssigned override for any actor whose type is in
		// Ops.Info.FerryTypes, BEFORE falling back to base.OnUnitAssigned for everything else.
		public bool TryClaim(Actor a)
		{
			if (!ops.Info.FerryTypes.Contains(a.Info.Name))
				return false;

			ferries.Add(a);
			return true;
		}

		public void Release()
		{
			if (ferries.Count > 0)
			{
				ops.ReleaseToPool(owner, ferries.ToList());
				ferries.Clear();
			}

			// Free the embark cell so a later/other mission can reuse this stretch of shore once
			// this one no longer needs it.
			if (embarkCell != null)
			{
				ops.ReleaseEmbarkCell(embarkCell.Value);
				embarkCell = null;
			}
		}

		// No ground path to the far shore: find a coastal embark/landing cell so the passengers can
		// stage at the coast. Transports are requested lazily in Tick() once naval production
		// exists -- until then the group just holds at the beach. Returns false only if ferrying
		// could never work at all (no chain configured, no coast found).
		// Once a real transport exists, its own position is the ground truth for "which water do our
		// ships actually operate in". Re-derive the embark shore and dock from THAT, requiring a dock
		// in the ship's own water component (no fallback here on purpose).
		//
		// This is what finally kills the recurring "ship parks two cells away and never docks" bug:
		// seeding the reachability flood from the shore instead can start the flood inside a one-cell
		// inlet that the Sub Pen walls off from the open sea. The flood then "proves" that inlet
		// reachable and hands it back as the dock, while no ship can ever sail into it (confirmed
		// 2026-07-24: derrick-47 embarkDock=35,13, two transports both idle at 35,11/36,11, dist²=4,
		// pending=5, nobody ever boarded).
		void RevalidateDock()
		{
			if (dockRevalidated || ferries.Count == 0 || ops.Info.FerryLocomotor == null || embarkCell == null)
				return;

			dockRevalidated = true;
			var seed = ferries[0].Location;

			var shore = ops.Intel.FindCoastalCellNear(startBaseCentre, ops.Info.FerrySearchRadius,
				requireOwnReachable: true, ops.Info.FerryLocomotor, ops.ClaimedEmbarkCells, navalSeed: seed);
			if (shore == null)
				return;

			var dock = ops.Intel.DockCellFor(shore.Value, ops.Info.FerryLocomotor, navalSeed: seed);
			if (dock == null || dock == shore)
				return;

			if (shore.Value != embarkCell.Value)
			{
				ops.ReleaseEmbarkCell(embarkCell.Value);
				ops.ClaimEmbarkCell(shore.Value);
			}

			if (shore.Value != embarkCell.Value || dock.Value != (embarkDock ?? embarkCell.Value))
				log($"embark re-derived from ship water: {embarkCell} -> {shore.Value} (dock {embarkDock} -> {dock.Value})");

			embarkCell = shore;
			embarkDock = dock;
		}

		public bool TryStart(CPos baseCentre, CPos farShoreRef)
		{
			if (ops.Info.FerryTypes.Length == 0)
				return false;

			startBaseCentre = baseCentre;

			landingCell = ops.Intel.FindCoastalCellNear(farShoreRef, ops.Info.FerrySearchRadius, requireOwnReachable: false, ops.Info.FerryLocomotor);

			// Steer away from embark cells other concurrent ferry missions already claimed, so
			// missions spread across different stretches of shore instead of piling everyone onto
			// the same dock (User 2026-07-23: "alle Küstenzellen sind von anderen Transport-
			// Wartegästen belegt").
			if (landingCell == null)
			{
				log("naval ferry unavailable: no coastal embark/landing cell found nearby");
				return false;
			}

			// Embark shore MUST share the same navigable sea as the landing shore -- seed the naval
			// reachability from landingCell so a beach whose only water access is a pen-blocked inlet is
			// rejected (the ship could never sail there from the crossing sea; see FindCoastalCellNear).
			// Also steer away from cells other concurrent ferry missions already claimed, so missions
			// spread across different stretches of shore instead of piling everyone onto the same dock
			// (User 2026-07-23: "alle Küstenzellen sind von anderen Transport-Wartegästen belegt").
			embarkCell = ops.Intel.FindCoastalCellNear(baseCentre, ops.Info.FerrySearchRadius, requireOwnReachable: true, ops.Info.FerryLocomotor, ops.ClaimedEmbarkCells, navalSeed: landingCell.Value);

			if (embarkCell == null)
			{
				log("naval ferry unavailable: no coastal embark/landing cell found nearby");
				return false;
			}

			ops.ClaimEmbarkCell(embarkCell.Value);

			// embarkCell/landingCell are LAND cells (that is what makes them valid staging points) --
			// a ship can never actually enter them. The dock cell is the specific WATER cell touching
			// that shore the ship itself must be ordered to, so it ends up truly adjacent to the
			// waiting units. Both docks are seeded from the crossing sea (landingCell) so they lie on
			// water the ship can actually reach and sail between.
			embarkDock = ops.Info.FerryLocomotor != null ? ops.Intel.DockCellFor(embarkCell.Value, ops.Info.FerryLocomotor, navalSeed: landingCell.Value) ?? embarkCell : embarkCell;
			landingDock = ops.Info.FerryLocomotor != null ? ops.Intel.DockCellFor(landingCell.Value, ops.Info.FerryLocomotor, navalSeed: landingCell.Value) ?? landingCell : landingCell;

			// Idempotent/cheap to call every time a group starts ferrying.
			ops.RequestNavalProduction();
			return true;
		}

		public Result Tick(IBot bot, HashSet<Actor> units)
		{
			ferries.RemoveAll(ops.CannotOrder);

			// Credit disembarkation BEFORE pruning dead units from `boarded`. A unit that made it off the
			// ship and was then immediately killed (e.g. by defenses waiting right at the landing zone)
			// genuinely crossed the water -- the ferry's job was done. Checking CannotOrder first (the old
			// order) silently erased that credit: the dead unit vanished from `boarded` before ever being
			// promoted to `ferriedAshore`, even though it was no longer in any ship's cargo by the time it
			// died. Confirmed 2026-07-22: cargo count dropped to 0 (someone left the ship) but
			// ferriedAshore stayed 0, and the mission timed out with "nobody made it across" despite units
			// having genuinely boarded and left the home shore.
			foreach (var u in boarded.ToList())
			{
				if (!ferries.Any(s => s.TraitOrDefault<Cargo>()?.Passengers.Contains(u) == true))
				{
					boarded.Remove(u);
					ferriedAshore.Add(u);
					log($"unit {u.Info.Name}@{u.Location} disembarked -> ferriedAshore={ferriedAshore.Count} (alive={!ops.CannotOrder(u)})");
				}
			}

			// A unit riding the ferry is alive but not in the world -- only real death removes it here,
			// otherwise boarding would erase it from tracking mid-crossing (see Ops.IsGone).
			ferriedAshore.RemoveWhere(ops.IsGone);
			inTransit.RemoveWhere(ops.IsGone);
			boarded.RemoveWhere(ops.IsGone);

			if (!ferryRequested)
			{
				if (ops.HasNavalProduction())
				{
					// Keep trying every tick, not just once: the fleet is globally capped and shared, so the
					// ship this group needs is often busy with another mission and only frees up later. The
					// old one-shot attempt made every mission that found the fleet fully allocated queue
					// nothing, and the check below then cancelled it instantly (User 2026-07-24: derrick
					// squad crossed, all other groups never even tried).
					if (ferries.Count < ops.Info.FerryCount)
					{
						var fromPool = ops.TakeFromPool(ops.Info.FerryTypes, ops.Info.FerryCount - ferries.Count);
						ops.AssignFromPool(owner, fromPool);
					}

					// Top up with fresh production only within the global cap.
					if (ferries.Count < ops.Info.FerryCount && ops.OpenRequests(owner) == 0)
					{
						var want = Math.Min(ops.Info.FerryCount - ferries.Count, ops.FerryBudget());
						if (want > 0)
							ops.QueueRequest(owner, AotOperationsBotModule.FerryRole, ops.Info.FerryTypes, want);
					}

					if (!ferryRequested)
					{
						ferryRequested = true;
						log("naval production ready -> transports requested");
					}
				}
			}

			// Only run the watchdog once this group actually has a ship (or one on the way). While it is
			// merely queued behind another mission it is waiting, not stalling, and must not time out.
			if (ferryRequested && (ferries.Count > 0 || ops.OpenRequests(owner) > 0))
				ferryTicks += ops.Info.MissionInterval;

			// Give up only when no transport can ever arrive -- none owned anywhere, none queued.
			if (ferryRequested && ferries.Count == 0 && ops.OpenRequests(owner) == 0 && ferriedAshore.Count == 0
				&& ops.OwnedFerryCount() == 0)
			{
				log("no transports available -> ferry cancelled");
				return Result.Failed;
			}

			// Pin the embark point to water our ships can genuinely reach, now that one exists.
			RevalidateDock();

			var pending = units.Where(a => !ops.CannotOrder(a) && !ferriedAshore.Contains(a) && !inTransit.Contains(a) && !boarded.Contains(a)).ToList();

			foreach (var u in pending)
				if (u.IsIdle && (u.Location - embarkCell.Value).LengthSquared > 9)
					bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(ops.World, embarkCell.Value), false));

			var logThisTick = ++ferryDiagLog % 8 == 0;
			var embarkDockCell = embarkDock ?? embarkCell.Value;
			var landingDockCell = landingDock ?? landingCell.Value;

			// Units whose EnterTransport we issued THIS tick. The order only resolves on a later
			// world tick, so the unit is still IsIdle right now -- without this guard the retry pass
			// below would demote it back to pending and re-issue next tick, churning RideTransport
			// forever and never letting it board.
			var orderedThisTick = new HashSet<Actor>();

			foreach (var ship in ferries)
			{
				var cargo = ship.TraitOrDefault<Cargo>();
				if (cargo == null)
					continue;

				// Stay in loading mode until the ship is full (weight-based: 1 tank OR 5 infantry
				// both hit MaxWeight) or nobody is left to board. Otherwise the moment the FIRST unit
				// boards, cargo != empty would flip us to depart mode and the ship would sail off with
				// a partial load, stranding units still walking over.
				var full = !cargo.HasSpace(1);
				var stillLoading = !full && (pending.Count > 0 || inTransit.Count > 0);

				// Commit latch: once a loaded ship sets off it must finish the delivery. Without this a
				// ship already almost at the far shore turned around because another passenger showed up
				// back home (observed 2026-07-24: dist2ToLanding=4 with cargo=1, then all the way back).
				// It rejoins the loading cycle only after unloading (cargo empty again).
				if (cargo.IsEmpty())
					crossing.Remove(ship);
				else if (!stillLoading)
					crossing.Add(ship);

				if (!crossing.Contains(ship) && (cargo.IsEmpty() || stillLoading))
				{
					var distToEmbark = (ship.Location - embarkDockCell).LengthSquared;
					var docked = distToEmbark <= DockedRadius2;

					// Don't fight the engine. While still approaching, nudge the ship toward the dock
					// ONLY when it has gone fully idle -- never every tick. Re-issuing Move each tick
					// cancels the in-progress path and keeps shoving the ship out from under passengers
					// mid-board, so nobody ever completes EnterTransport (the erratic "ship jitters at
					// the beach, no one boards" behaviour). Once docked, leave the ship completely alone.
					if (!docked)
					{
						if (ship.IsIdle)
							bot.QueueOrder(new Order("Move", ship, Target.FromCell(ops.World, embarkDockCell), false));
					}
					else
					{
						// Clear the ramp so a boarding unit can actually reach the ship.
						FerryUtils.NudgeAround(ship);

						// Ship is parked at the dock. Order EVERY waiting unit to board -- NOT only the
						// idle ones. In a crowd the units are perpetually stuck in an AttackMove toward a
						// blocked embark cell, so an IsIdle filter finds nobody and inTransit never leaves
						// 0 (observed 2026-07-23: pending=5 inTransit=0 forever). EnterTransport queues the
						// engine's own RideTransport activity, which replaces that AttackMove and handles
						// approach + retry itself -- issue it once per unit and then trust it; moving the
						// unit out of `pending` also stops the AttackMove loop above from fighting it.
						foreach (var u in pending.ToList())
						{
							bot.QueueOrder(new Order("EnterTransport", u, Target.FromActor(ship), false));
							inTransit.Add(u);
							orderedThisTick.Add(u);
							pending.Remove(u);
						}
					}

					if (logThisTick)
						log($"ferry ship {ship.Info.Name}@{ship.Location}: idle={ship.IsIdle} activity={ship.CurrentActivity?.GetType().Name ?? "none"} " +
							$"dist2ToEmbark={distToEmbark} docked={docked} embarkDock={embarkDockCell} pending={pending.Count} inTransit={inTransit.Count}");
				}
				else
				{
					var distToLanding = (ship.Location - landingDockCell).LengthSquared;
					var atLanding = distToLanding <= DockedRadius2;

					// Same discipline as the embark side: nudge toward the landing dock only while idle
					// and still approaching, then hold still and unload once there.
					if (!atLanding)
					{
						if (ship.IsIdle)
							bot.QueueOrder(new Order("Move", ship, Target.FromCell(ops.World, landingDockCell), false));
					}
					else
					{
						// Free the ramp first, then unload -- idle units loitering next to the ship silently
						// stall the whole unload even when free cells exist one tile further out.
						FerryUtils.NudgeAround(ship);
						bot.QueueOrder(new Order("Unload", ship, false));
					}

					if (logThisTick)
						log($"ferry ship {ship.Info.Name}@{ship.Location}: idle={ship.IsIdle} activity={ship.CurrentActivity?.GetType().Name ?? "none"} " +
							$"dist2ToLanding={distToLanding} atLanding={atLanding} landingDock={landingDockCell} cargo={cargo.Passengers.Count()}");
				}
			}

			// Confirm actual boarding before treating "not currently in any ship's cargo" as
			// disembarked -- otherwise "just issued the order, hasn't boarded yet" and "boarded,
			// crossed, disembarked" are indistinguishable, crediting units as landed while they never
			// left the home shore.
			foreach (var u in inTransit.ToList())
			{
				if (ferries.Any(s => s.TraitOrDefault<Cargo>()?.Passengers.Contains(u) == true))
				{
					inTransit.Remove(u);
					boarded.Add(u);

					// Genuine progress: a unit boarded. Reset the watchdog so a ferry that legitimately
					// needs several trips is never killed mid-progress -- the timeout measures time since
					// the LAST progress, so it still fires when the ship is truly stuck and nobody boards.
					ferryTicks = 0;
				}
			}

			// Retry failed boarding attempts -- EnterTransport is a one-shot order; if it ends
			// (unit idle again) without the unit ever showing up in cargo, that attempt failed and
			// the unit goes back to the pending pool for a fresh try next tick.
			foreach (var u in inTransit.ToList())
				if (u.IsIdle && !orderedThisTick.Contains(u))
					inTransit.Remove(u);

			foreach (var u in boarded.ToList())
			{
				if (!ferries.Any(s => s.TraitOrDefault<Cargo>()?.Passengers.Contains(u) == true))
				{
					boarded.Remove(u);
					ferriedAshore.Add(u);
					ferryTicks = 0; // a unit completed the crossing -- progress, reset the watchdog.

					// Clear the exit. A unit that just stepped off otherwise stays parked right on the
					// landing cell and blocks the next passenger from disembarking (User 2026-07-24:
					// "sie blockieren sich am exit-punkt und nudgen sich nicht zur seite"). The engine's
					// nudge cannot fix this -- it only fires when something paths THROUGH an idle blocker,
					// and a disembarking passenger does not path, it just needs the cell free. So actively
					// send every new arrival a few cells inland.
					var rally = DisembarkRally();
					if (rally != null)
						bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(ops.World, rally.Value), false));
				}
			}

			// Riders are NOT lost -- counting them via CannotOrder made a fully loaded ferry look like a
			// wiped-out group and cancelled the crossing (User 2026-07-24: embark but never disembark).
			var stillToGo = units.Count(a => !ops.IsGone(a) && !ferriedAshore.Contains(a));
			if (stillToGo == 0)
			{
				if (ferriedAshore.Count == 0)
				{
					log("wave lost (wiped out crossing, or while waiting for naval production)");
					return Result.Failed;
				}

				log($"ferry complete: {ferriedAshore.Count} unit(s) landed");
				return Result.Ashore;
			}

			if (ferryRequested && ferryTicks >= ops.Info.FerryTimeout)
			{
				if (ferriedAshore.Count > 0)
				{
					log($"ferry timeout -> proceeding with {ferriedAshore.Count} unit(s) already ashore");
					return Result.Ashore;
				}

				log("ferry timeout, nobody made it across -> cancelled");
				return Result.Failed;
			}

			return Result.InProgress;
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
		public override bool AcceptsReinforcements => true;

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

		// Naval ferry (no land route to the enemy): transports are tracked separately from the
		// combat Units they carry, reused across waves via the pool.
		readonly List<Actor> ferries = [];
		readonly HashSet<Actor> inTransit = [];
		readonly HashSet<Actor> boarded = [];
		readonly HashSet<Actor> ferriedAshore = [];

		// Ships that have begun a crossing with a load -- see FerryHelper.crossing for why the latch
		// exists (a committed ship must not sail back for a late passenger).
		readonly HashSet<Actor> crossing = [];
		bool dockRevalidated;

		// Re-derive the embark shore/dock from a real ship's own water once one exists -- see
		// FerryHelper.RevalidateDock for why seeding from the shore can hand back an inlet no ship
		// can ever enter.
		void RevalidateDock()
		{
			if (dockRevalidated || ferries.Count == 0 || Ops.Info.FerryLocomotor == null || embarkCell == null)
				return;

			dockRevalidated = true;
			var seed = ferries[0].Location;

			var shore = Ops.Intel.FindCoastalCellNear(Ops.BaseCentre(), Ops.Info.FerrySearchRadius,
				requireOwnReachable: true, Ops.Info.FerryLocomotor, navalSeed: seed);
			if (shore == null)
				return;

			var dock = Ops.Intel.DockCellFor(shore.Value, Ops.Info.FerryLocomotor, navalSeed: seed);
			if (dock == null || dock == shore)
				return;

			if (shore.Value != embarkCell.Value || dock.Value != (embarkDock ?? embarkCell.Value))
				Log($"embark re-derived from ship water: {embarkCell} -> {shore.Value} (dock {embarkDock} -> {dock.Value})");

			embarkCell = shore;
			embarkDock = dock;
		}
		CPos? embarkCell;
		CPos? ferryLandingCell;
		CPos? embarkDock;
		CPos? landingDock;
		int ferryTicks;
		int ferryDiagLog;
		bool ferryRequested;
		bool ashore;

		// A few cells inland from the landing cell so disembarked units clear the exit instead of
		// parking on it and blocking the next passenger -- see FerryHelper.DisembarkRally.
		CPos? DisembarkRally()
		{
			if (ferryLandingCell == null)
				return null;

			var dock = landingDock ?? ferryLandingCell;
			var inland = ferryLandingCell.Value - dock.Value;
			if (inland == CVec.Zero)
				return ferryLandingCell;

			var rally = ferryLandingCell.Value + inland * 3;
			return Ops.World.Map.Contains(rally) ? rally : ferryLandingCell;
		}

		public AotRegularWaveMission(AotOperationsBotModule ops, int index, bool useSecondaryRoute)
			: base(ops, $"wave-{index}")
		{
			this.index = index;
			this.useSecondaryRoute = useSecondaryRoute;
		}

		public override void OnUnitAssigned(Actor a)
		{
			if (Ops.Info.FerryTypes.Contains(a.Info.Name))
				ferries.Add(a);
			else
				base.OnUnitAssigned(a);
		}

		// Return every transport this wave holds to the shared pool.
		void ReleaseFerries()
		{
			if (ferries.Count == 0)
				return;

			Ops.ReleaseToPool(this, ferries.ToList());
			ferries.Clear();
		}

		void FinishWave()
		{
			ReleaseFerries();

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
					$"staging at embark={embarkCell} (landing={ferryLandingCell}), " +
					$"{(Ops.HasNavalProduction() ? "requesting transports" : "waiting for naval production")}");
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
		bool TryStartFerry()
		{
			if (Ops.Info.FerryTypes.Length == 0)
				return false;

			if (Ops.Intel.EnemySpawns.Count == 0)
				return false;

			var enemyRef = Ops.Intel.EnemySpawns.MinBy(s => (s - Ops.BaseCentre()).LengthSquared);
			ferryLandingCell = Ops.Intel.FindCoastalCellNear(enemyRef, Ops.Info.FerrySearchRadius, requireOwnReachable: false, Ops.Info.FerryLocomotor);

			if (ferryLandingCell == null)
			{
				Log("naval ferry unavailable: no coastal embark/landing cell found nearby");
				return false;
			}

			// Embark shore must share the same navigable sea as the landing shore -- seed reachability
			// from ferryLandingCell so a beach walled off from the crossing sea (e.g. behind the Sub Pen)
			// is rejected rather than picked and then never reached (see FindCoastalCellNear).
			embarkCell = Ops.Intel.FindCoastalCellNear(Ops.BaseCentre(), Ops.Info.FerrySearchRadius, requireOwnReachable: true, Ops.Info.FerryLocomotor, navalSeed: ferryLandingCell.Value);

			if (embarkCell == null)
			{
				Log("naval ferry unavailable: no coastal embark/landing cell found nearby");
				return false;
			}

			// embarkCell/ferryLandingCell are LAND cells (that is what makes them valid staging points for
			// ground units) -- a ship can never actually enter them. The dock cell is the specific WATER
			// cell touching that shore that the ship itself should be ordered to, so it ends up truly
			// adjacent to the waiting units instead of wherever the pathfinder's own "close enough"
			// tolerance happens to stop for an unreachable land target (confirmed 2026-07-22: ships parked
			// 2+ cells short of the shore, tanks waited right at the water's edge, nobody ever boarded).
			// Falls back to the land cell itself if no locomotor is configured (old terrain-only behaviour).
			embarkDock = Ops.Info.FerryLocomotor != null ? Ops.Intel.DockCellFor(embarkCell.Value, Ops.Info.FerryLocomotor, navalSeed: ferryLandingCell.Value) ?? embarkCell : embarkCell;
			landingDock = Ops.Info.FerryLocomotor != null ? Ops.Intel.DockCellFor(ferryLandingCell.Value, Ops.Info.FerryLocomotor, navalSeed: ferryLandingCell.Value) ?? ferryLandingCell : ferryLandingCell;

			// This mission needs naval production to exist -- ask the base builder to guarantee it (built
			// on demand, outside the fixed Rhythm; user spec 2026-07-22). Idempotent/cheap to call every
			// time a wave starts ferrying.
			Ops.RequestNavalProduction();

			return true;
		}

		void TickFerrying(IBot bot)
		{
			ferries.RemoveAll(Ops.CannotOrder);
			// Only real death removes a rider here -- see Ops.IsGone.
			ferriedAshore.RemoveWhere(Ops.IsGone);
			inTransit.RemoveWhere(Ops.IsGone);
			boarded.RemoveWhere(Ops.IsGone);

			if (Ops.HasNavalProduction())
			{
				// Retry every tick, not once: the globally capped fleet is shared, so a transport this
				// wave needs is often busy elsewhere and only frees up later (see FerryHelper).
				if (ferries.Count < Ops.Info.FerryCount)
				{
					var fromPool = Ops.TakeFromPool(Ops.Info.FerryTypes, Ops.Info.FerryCount - ferries.Count);
					Ops.AssignFromPool(this, fromPool);
				}


				// Top up with fresh production only within the GLOBAL cap (see Ops.FerryBudget).
				if (ferries.Count < Ops.Info.FerryCount && Ops.OpenRequests(this) == 0)
				{
					var want = Math.Min(Ops.Info.FerryCount - ferries.Count, Ops.FerryBudget());
					if (want > 0)
						Ops.QueueRequest(this, AotOperationsBotModule.FerryRole, Ops.Info.FerryTypes, want);
				}

				if (!ferryRequested)
				{
					ferryRequested = true;
					Log("naval production ready -> transports requested");
				}
			}

			// Else: no Sub Pen/Shipyard yet. No timeout here -- the wave just holds at the coast until
			// one is eventually built.

			// Watchdog only once this wave actually has a ship (or one on the way) -- while merely queued
			// behind another mission it is waiting, not stalling, and must not time out.
			if (ferryRequested && (ferries.Count > 0 || Ops.OpenRequests(this) > 0))
				ferryTicks += Ops.Info.MissionInterval;

			// Pin the embark point to water our ships can genuinely reach, now that one exists.
			RevalidateDock();

			// Give up only when no transport can ever arrive -- none owned anywhere, none queued.
			if (ferryRequested && ferries.Count == 0 && Ops.OpenRequests(this) == 0 && ferriedAshore.Count == 0
				&& Ops.OwnedFerryCount() == 0)
			{
				Log("no transports available -> ferry cancelled, wave dissolved");
				Outcome = AotMissionOutcome.Failure;
				FinishWave();
				return;
			}

			var pending = Units.Where(a => !Ops.CannotOrder(a) && !ferriedAshore.Contains(a) && !inTransit.Contains(a) && !boarded.Contains(a)).ToList();

			// Walk not-yet-embarked units to the coast.
			foreach (var u in pending)
				if (u.IsIdle && (u.Location - embarkCell.Value).LengthSquared > 9)
					bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(Ops.World, embarkCell.Value), false));

			// Throttled per-ship diagnostic (User 2026-07-22: transport visibly never approached the
			// shore -- IsIdle/distance/current-activity make the exact reason visible on the next test
			// instead of guessing further, same pattern as the earlier AotBuild production stall diags).
			var logThisTick = ++ferryDiagLog % 8 == 0;

			// The ship itself must be ordered to the WATER cell touching the shore (embarkDock/landingDock),
			// never to embarkCell/ferryLandingCell directly -- those are LAND cells a ship can never enter,
			// so a "Move" order aimed at them only gets the ship generically "close" via the pathfinder's
			// own stopping tolerance for an unreachable destination, not truly adjacent to the waiting
			// units (see TryStartFerry for the full story). Units still walk to the LAND cell, since that
			// is what they can actually stand on.
			var embarkDockCell = embarkDock ?? embarkCell.Value;
			var landingDockCell = landingDock ?? ferryLandingCell.Value;

			// See FerryHelper: guards freshly-issued EnterTransport orders (not yet resolved this
			// tick, so the unit is still IsIdle) from being demoted+re-issued, which churns forever.
			var orderedThisTick = new HashSet<Actor>();

			foreach (var ship in ferries)
			{
				var cargo = ship.TraitOrDefault<Cargo>();
				if (cargo == null)
					continue;

				// Stay in loading mode until the ship is full (weight-based: 1 tank OR 5 infantry
				// both hit MaxWeight) or nobody is left to board. Otherwise the moment the FIRST unit
				// boards, cargo != empty would flip us to depart mode and the ship would sail off with
				// a partial load, stranding units still walking over.
				var full = !cargo.HasSpace(1);
				var stillLoading = !full && (pending.Count > 0 || inTransit.Count > 0);

				// Commit latch: once a loaded ship sets off it must finish the delivery. Without this a
				// ship already almost at the far shore turned around because another passenger showed up
				// back home (observed 2026-07-24: dist2ToLanding=4 with cargo=1, then all the way back).
				// It rejoins the loading cycle only after unloading (cargo empty again).
				if (cargo.IsEmpty())
					crossing.Remove(ship);
				else if (!stillLoading)
					crossing.Add(ship);

				if (!crossing.Contains(ship) && (cargo.IsEmpty() || stillLoading))
				{
					var distToEmbark = (ship.Location - embarkDockCell).LengthSquared;
					var docked = distToEmbark <= FerryHelper.DockedRadius2;

					// Don't fight the engine. Nudge the ship toward the dock ONLY while idle and still
					// approaching -- re-issuing Move every tick cancels the in-progress path and shoves
					// the ship out from under passengers mid-board, so nobody completes EnterTransport
					// (the erratic "jitters at the beach, no one boards" behaviour). Once docked, hold
					// still and order EVERY idle waiting unit to board at once, like a player selecting
					// the whole group and right-clicking the transport.
					if (!docked)
					{
						if (ship.IsIdle)
							bot.QueueOrder(new Order("Move", ship, Target.FromCell(Ops.World, embarkDockCell), false));
					}
					else
					{
						// Clear the ramp so a boarding unit can actually reach the ship.
						FerryUtils.NudgeAround(ship);

						// Order EVERY waiting unit (not only idle ones): in a crowd units are stuck in an
						// AttackMove to a blocked embark cell and never go idle, so an IsIdle filter boards
						// nobody. EnterTransport's RideTransport activity replaces that move and handles
						// approach+retry itself; issue once per unit and trust it.
						foreach (var u in pending.ToList())
						{
							bot.QueueOrder(new Order("EnterTransport", u, Target.FromActor(ship), false));
							inTransit.Add(u);
							orderedThisTick.Add(u);
							pending.Remove(u);
						}
					}

					if (logThisTick)
						Log($"ferry ship {ship.Info.Name}@{ship.Location}: idle={ship.IsIdle} activity={ship.CurrentActivity?.GetType().Name ?? "none"} " +
							$"dist2ToEmbark={distToEmbark} docked={docked} embarkDock={embarkDockCell} pending={pending.Count} inTransit={inTransit.Count}");
				}
				else
				{
					var distToLanding = (ship.Location - landingDockCell).LengthSquared;
					var atLanding = distToLanding <= FerryHelper.DockedRadius2;

					// Same discipline as the embark side: nudge only while idle and approaching, then
					// hold still and unload once at the landing dock.
					if (!atLanding)
					{
						if (ship.IsIdle)
							bot.QueueOrder(new Order("Move", ship, Target.FromCell(Ops.World, landingDockCell), false));
					}
					else
					{
						// Free the ramp first, then unload -- idle units loitering next to the ship silently
						// stall the whole unload even when free cells exist one tile further out.
						FerryUtils.NudgeAround(ship);
						bot.QueueOrder(new Order("Unload", ship, false));
					}

					if (logThisTick)
						Log($"ferry ship {ship.Info.Name}@{ship.Location}: idle={ship.IsIdle} activity={ship.CurrentActivity?.GetType().Name ?? "none"} " +
							$"dist2ToLanding={distToLanding} atLanding={atLanding} landingDock={landingDockCell} cargo={cargo.Passengers.Count()}");
				}
			}

			// Confirm actual boarding: EnterTransport is an ORDER, not an instant state change -- the unit
			// still has to walk to the ship and be picked up, which takes at least one more tick. Promote
			// inTransit -> boarded only once the unit is physically observed inside a ferry's Cargo.
			foreach (var u in inTransit.ToList())
			{
				if (ferries.Any(s => s.TraitOrDefault<Cargo>()?.Passengers.Contains(u) == true))
				{
					inTransit.Remove(u);
					boarded.Add(u);
					ferryTicks = 0; // progress: a unit boarded -- reset the watchdog (multi-trip safe).
				}
			}

			// Retry failed boarding attempts. EnterTransport is issued once (StartStep-style, matching
			// every other one-shot order in this AI) and never retried on its own -- if the activity ends
			// (unit goes idle again) without the unit ever showing up in the ship's Cargo, that attempt
			// failed (ship not actually reachable/adjacent at interaction range, even though it satisfied
			// the coarser "within 3 cells" distance check used to decide when to TRY boarding -- confirmed
			// 2026-07-22: `pending=0 inTransit=5` sat frozen with the ship `idle=True activity=none` for
			// 14+ consecutive diagnostic samples, cargo never stopped being empty, wave eventually timed
			// out). Demoting back to `pending` lets the normal walk-to-embark/EnterTransport loop above
			// try again next tick (ship may have moved, or a retry may simply succeed this time) instead of
			// leaving the unit permanently stuck in limbo.
			foreach (var u in inTransit.ToList())
			{
				if (u.IsIdle && !orderedThisTick.Contains(u))
					inTransit.Remove(u);
			}

			// Detect disembarked units: confirmed aboard a ferry at some earlier tick, no longer aboard any
			// now. Checking this against `inTransit` directly (as before) could not tell "just issued the
			// EnterTransport order, hasn't actually boarded yet" apart from "boarded, crossed, disembarked"
			// -- both look identical as "not currently in any ship's Passengers list". On the very next
			// MissionInterval tick after EnterTransport was queued (before boarding physically completed),
			// every unit failed this check and was credited as having landed near the enemy while still
			// standing at the home embark point (confirmed 2026-07-22: log claimed "ferry complete: 5
			// unit(s) landed" one tick after the transport was even claimed -- nowhere near enough time to
			// actually cross open water -- while the units visibly never left the home shore in-game).
			foreach (var u in boarded.ToList())
			{
				if (!ferries.Any(s => s.TraitOrDefault<Cargo>()?.Passengers.Contains(u) == true))
				{
					boarded.Remove(u);
					ferriedAshore.Add(u);
					ferryTicks = 0; // progress: a unit completed the crossing -- reset the watchdog.

					// Clear the exit so the next passenger can step off -- see FerryHelper for why the
					// engine's nudge cannot do this on its own.
					var rally = DisembarkRally();
					if (rally != null)
						bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(Ops.World, rally.Value), false));
				}
			}

			var stillToGo = Units.Count(a => !Ops.IsGone(a) && !ferriedAshore.Contains(a));
			if (stillToGo == 0)
			{
				if (ferriedAshore.Count == 0)
				{
					Log("wave lost (wiped out crossing, or while waiting for naval production)");
					Outcome = AotMissionOutcome.Failure;
					FinishWave();
					return;
				}

				ReleaseFerries();
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
					ReleaseFerries();
					ashore = true;
					ChooseTarget();
					phase = Phase.Executing;
					Log($"ferry timeout -> proceeding with {ferriedAshore.Count} unit(s) already ashore");
				}
				else
				{
					Log("ferry timeout, nobody made it across -> wave cancelled");
					Outcome = AotMissionOutcome.Failure;
					FinishWave();
				}
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

		// Naval ferry (User 2026-07-22): a spawn with no ground route gets crossed to instead of
		// just skipped, exactly like a regular attack wave -- see FerryHelper. Lazily created only
		// once a spawn is actually found unreachable by land, and reused across every such spawn
		// this group ever tours (transports are pooled/reused, not rebuilt per spawn).
		FerryHelper ferry;
		bool ashoreForCurrentSpawn;

		// Ferry started (transport requested) but the group is still doing useful scouting on our own
		// side until a ship actually shows up -- see TickTouring's no-ground-route branch.
		bool ferryArmed;

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
			if (ferry != null && ferry.TryClaim(a))
				return;

			base.OnUnitAssigned(a);
		}

		public override void Tick(IBot bot)
		{
			if (Units.Count == 0 && Ops.OpenRequests(this) == 0)
			{
				ferry?.Release();
				Done = true;
				return;
			}

			// A transport finally exists for the spawn we couldn't reach by land -- stop whatever
			// touring/holding we were doing meanwhile and go make the crossing.
			if (ferryArmed && phase != Phase.Ferrying && ferry != null && ferry.HasShip)
			{
				Log("transport available -> heading to the beach to cross");
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
					var result = ferry.Tick(bot, Units);
					if (result == FerryHelper.Result.Ashore)
					{
						// Ashore on the FAR side now, outside our own base-side reachable set by
						// definition -- resume touring the same spawn from here with the reachability
						// requirement dropped, exactly like AotRegularWaveMission's `ashore` flag.
						ashoreForCurrentSpawn = true;
						ferryArmed = false;
						currentWaypoints = null;

						// Hand the transports back at once -- the fleet is globally capped and other groups are
						// queued behind it. A fresh FerryHelper is created if a later spawn needs another crossing.
						ferry.Release();
						ferry = null;
						phase = Phase.Touring;
					}
					else if (result == FerryHelper.Result.Failed)
					{
						// Couldn't get anyone across -- give up on this one spawn (not the whole
						// mission) and move on to the next, same as the pre-existing "no waypoints"
						// skip behaviour for a spawn with no ground route at all.
						spawnCursor++;
						currentWaypoints = null;
						ashoreForCurrentSpawn = false;
						ferryArmed = false;
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
					// Arm the ferry (which requests the transport) but do NOT switch to Ferrying yet:
					// marching to the beach now would just park the squad there doing nothing for as
					// long as the AI takes to afford a ship -- on a poor spawn that can be the whole
					// match (User 2026-07-24: "mindestens die scouts sollten ja zügig los marschieren
					// während solche gebäude gebaut werden"). Keep touring reachable ground instead;
					// Tick() flips to Ferrying the moment a transport actually exists.
					if (!ashoreForCurrentSpawn && !ferryArmed)
					{
						ferry ??= new FerryHelper(this, Ops, Log);
						if (ferry.TryStart(Ops.BaseCentre(), spawn))
						{
							ferryArmed = true;
							Log("no ground route -> transport requested, scouting reachable ground meanwhile");
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

			if (live.All(a => a.IsIdle))
				AttackMoveGroup(bot, live, target);
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

		// Derricks across water are now valid targets (User 2026-07-24) -- the squad crosses with a
		// transport exactly like a scout group or an attack wave. Lazily created: only a derrick with
		// no land route ever builds a ferry.
		FerryHelper ferry;
		bool ashore;

		public AotDerrickMission(AotOperationsBotModule ops, Actor derrick)
			: base(ops, $"derrick-{derrick.ActorID}")
		{
			Derrick = derrick;

			// Allocated ONCE per mission (not re-queried every tick) so concurrent missions (up to
			// DerrickMaxTargets derrick squads plus scout groups) don't all pile onto the same
			// single cell -- see AllocateInfantryStagingCell.
			stagingCell = ops.AllocateInfantryStagingCell();

			ops.QueueRequest(this, "engineer", ops.Info.EngineerTypes, 1);
			ops.QueueRequest(this, "rocket", ops.Info.RocketInfantryTypes, 2);
			ops.QueueRequest(this, "mg", ops.Info.MgInfantryTypes, 2);
		}

		Actor Engineer() => Units.FirstOrDefault(a => Ops.Info.EngineerTypes.Contains(a.Info.Name));

		List<Actor> Escorts() => Units.Where(a => !Ops.Info.EngineerTypes.Contains(a.Info.Name) && !Ops.CannotOrder(a)).ToList();

		// Transports belong to the ferry, not to the capture squad -- see FerryHelper.TryClaim.
		public override void OnUnitAssigned(Actor a)
		{
			if (ferry != null && ferry.TryClaim(a))
				return;

			base.OnUnitAssigned(a);
		}

		public override void Tick(IBot bot)
		{
			if (Derrick.IsDead || !Derrick.IsInWorld)
			{
				Log("derrick destroyed -> mission over");
				ferry?.Release();
				Finish();
				return;
			}

			if (Units.Count == 0 && Ops.OpenRequests(this) == 0)
			{
				ferry?.Release();
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
					var result = ferry.Tick(bot, Units);
					if (result == FerryHelper.Result.Ashore)
					{
						// On the far shore now -- outside our own base-side reachable set by definition,
						// so the move leg must stop asking for ground reachability from here on.
						ashore = true;

						// Free the transports immediately: this mission ends in PERMANENT guard duty, so holding
						// them would lock the globally capped fleet away for the rest of the match.
						ferry.Release();
						ferry = null;

						phase = Phase.Moving;
						Log("ashore -> resuming approach to the derrick");
					}
					else if (result == FerryHelper.Result.Failed)
					{
						Log("could not ferry the capture squad across -> mission over");
						ferry.Release();
						Finish();
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

			// Wait for the full 5-man squad at the barracks rally point; the engineer is mandatory.
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
				ferry ??= new FerryHelper(this, Ops, Log);
				if (ferry.TryStart(Ops.BaseCentre(), Derrick.Location))
				{
					phase = Phase.Ferrying;
					Log($"no ground route to derrick @ {Derrick.Location} -> ferrying the capture squad across");
					return;
				}

				Log($"derrick @ {Derrick.Location} is unreachable and no ferry route exists -> mission over");
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
	// is owned, build AirRaidCount helicopters and send them straight at the enemy construction
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

			var fromPool = ops.TakeFromPool(ops.Info.AirRaidHelicopterTypes, ops.Info.AirRaidCount);
			ops.AssignFromPool(this, fromPool);
			if (ops.Info.AirRaidCount - fromPool.Count > 0)
				ops.QueueRequest(this, "airraid", ops.Info.AirRaidHelicopterTypes, ops.Info.AirRaidCount - fromPool.Count);
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

			// Everyone not currently dispatched holds the muster point, out of the builders' way.
			var holding = Units.Where(a => !responding.Contains(a)).ToList();
			if (holding.Count > 0)
				HoldAt(bot, holding, Ops.GarrisonMusterPoint(), Ops.Info.GuardLeashRadius);
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

			var threats = new HashSet<Actor>();
			foreach (var b in buildings)
				foreach (var a in Ops.World.FindActorsInCircle(b.CenterPosition, WDist.FromCells(Ops.Info.ProtectionScanRadius)))
					if (AotOpsUtils.IsPreferredEnemyUnit(Ops.Player, a) && a.CanBeViewedByPlayer(Ops.Player))
						threats.Add(a);

			if (threats.Count == 0)
			{
				responding.Clear();
				return;
			}

			var available = Units.Where(a => !Ops.CannotOrder(a)).ToList();
			if (available.Count == 0)
				return;

			var want = Math.Clamp(threats.Count * Ops.Info.ProtectionResponseRatio, Ops.Info.ProtectionMinResponse, available.Count);
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
