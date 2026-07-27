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
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Age of Tiberium: shared map intelligence for the AI operations framework.",
		"Static analysis (spawns, own map quarter, map-centre fallback) plus a periodically",
		"refreshed ground-reachability field from the own base. Faction-agnostic; one instance",
		"per bot player serves all AotOperationsBotModule instances.")]
	public class AotMapIntelBotModuleInfo : ConditionalTraitInfo
	{
		[ActorReference]
		[Desc("Actor types that are considered construction yards (base reference point).")]
		public readonly HashSet<string> ConstructionYardTypes = [];

		[Desc("Locomotor used for ground reachability checks.")]
		public readonly string GroundLocomotor = "foot";

		[ActorReference]
		[Desc("Neutral actors that drop a crate when destroyed (Starting-Unit raid targets).")]
		public readonly HashSet<string> ArcoTypes = [];

		[ActorReference]
		[Desc("Neutral/enemy capturable income actors (Derrick mission targets).")]
		public readonly HashSet<string> DerrickTypes = [];

		[ActorReference]
		[Desc("Crate actors dropped by destroyed raid targets.")]
		public readonly HashSet<string> CrateTypes = [];

		[Desc("Ticks between reachability refreshes (trees/walls change over time).")]
		public readonly int RefreshInterval = 500;

		public override object Create(ActorInitializer init) { return new AotMapIntelBotModule(init.Self, this); }
	}

	public class AotMapIntelBotModule : ConditionalTrait<AotMapIntelBotModuleInfo>, IBotTick, INotifyActorDisposing
	{
		public readonly World World;
		public readonly Player Player;

		readonly ActorIndex.NamesAndTrait<BuildingInfo> constructionYards;
		Locomotor loco;

		public bool Ready { get; private set; }
		public CPos OwnSpawn { get; private set; }
		public readonly List<CPos> AllSpawns = [];
		public readonly List<CPos> EnemySpawns = [];
		public CPos MapCentreFallback { get; private set; }
		public CPos BaseCentre { get; private set; }

		readonly HashSet<CPos> reachable = [];
		int refreshTicks;

		// Own quarter: the map quadrant (relative to the bounds centre) containing the own spawn.
		int quarterSignX, quarterSignY;
		CPos boundsCentre;

		public AotMapIntelBotModule(Actor self, AotMapIntelBotModuleInfo info)
			: base(info)
		{
			World = self.World;
			Player = self.Owner;
			constructionYards = new ActorIndex.NamesAndTrait<BuildingInfo>(World, info.ConstructionYardTypes);
		}

		protected override void Created(Actor self)
		{
			loco = World.WorldActor.TraitsImplementing<Locomotor>()
				.FirstOrDefault(l => l.Info.Name == Info.GroundLocomotor);
		}

		public bool IsPassable(CPos c) =>
			World.Map.Contains(c) && loco != null
			&& loco.MovementCostForCell(c) != PathGraph.MovementCostForUnreachableCell;

		public bool IsReachable(CPos c) => reachable.Contains(c);

		public bool IsInOwnQuarter(CPos c)
		{
			var sx = Math.Sign(c.X - boundsCentre.X);
			var sy = Math.Sign(c.Y - boundsCentre.Y);
			return (sx == 0 || sx == quarterSignX) && (sy == 0 || sy == quarterSignY);
		}

		Actor OwnYard() => constructionYards.Actors.FirstOrDefault(a => a.Owner == Player && !a.IsDead && a.IsInWorld);

		void IBotTick.BotTick(IBot bot)
		{
			if (!Ready)
			{
				var yard = OwnYard();
				if (yard == null)
					return;

				Initialize(yard);
				return;
			}

			if (--refreshTicks <= 0)
			{
				refreshTicks = Info.RefreshInterval;
				var yard = OwnYard();
				if (yard != null)
					BaseCentre = yard.Location;
				RefreshReachability();
			}
		}

		void Initialize(Actor yard)
		{
			BaseCentre = yard.Location;

			AllSpawns.Clear();
			foreach (var n in World.Map.ActorDefinitions)
				if (n.Value.Value == "mpspawn")
					AllSpawns.Add(new ActorReference(n.Key, n.Value).GetValue<LocationInit, CPos>());

			OwnSpawn = AllSpawns.Count > 0 ? AllSpawns.MinBy(s => (s - BaseCentre).LengthSquared) : BaseCentre;
			EnemySpawns.Clear();
			EnemySpawns.AddRange(AllSpawns.Where(s => s != OwnSpawn));

			var b = World.Map.Bounds;
			var tl = new MPos(b.Left, b.Top).ToCPos(World.Map);
			var br = new MPos(b.Right - 1, b.Bottom - 1).ToCPos(World.Map);
			boundsCentre = new CPos((tl.X + br.X) / 2, (tl.Y + br.Y) / 2);
			quarterSignX = Math.Sign(OwnSpawn.X - boundsCentre.X);
			quarterSignY = Math.Sign(OwnSpawn.Y - boundsCentre.Y);
			if (quarterSignX == 0)
				quarterSignX = 1;
			if (quarterSignY == 0)
				quarterSignY = 1;

			RefreshReachability();

			// Map-centre fallback: the reachable cell closest to the bounds centre.
			MapCentreFallback = reachable.Count > 0
				? reachable.MinBy(c => (c - boundsCentre).LengthSquared)
				: BaseCentre;

			refreshTicks = Info.RefreshInterval;
			Ready = true;
			Log.Write("debug", $"[AotIntel] {Player.PlayerName}: spawns={AllSpawns.Count} own={OwnSpawn} " +
				$"quarter=({quarterSignX},{quarterSignY}) centreFallback={MapCentreFallback} reachable={reachable.Count}");
		}

		void RefreshReachability()
		{
			reachable.Clear();
			if (loco == null)
				return;

			// Yard cells themselves are blocked by the building; seed from the nearest passable ring cell.
			CPos? start = null;
			for (var r = 0; r <= 4 && start == null; r++)
				foreach (var c in AotOpsUtils.Ring(BaseCentre, r))
					if (IsPassable(c))
					{
						start = c;
						break;
					}

			if (start == null)
				return;

			var queue = new Queue<CPos>();
			queue.Enqueue(start.Value);
			reachable.Add(start.Value);
			while (queue.Count > 0)
			{
				var c = queue.Dequeue();
				foreach (var d in CVec.Directions)
				{
					var n = c + d;
					if (!reachable.Contains(n) && IsPassable(n))
					{
						reachable.Add(n);
						queue.Enqueue(n);
					}
				}
			}
		}

		// ---- Live actor queries -------------------------------------------------

		public IEnumerable<Actor> Arcos() =>
			World.Actors.Where(a => !a.IsDead && a.IsInWorld && Info.ArcoTypes.Contains(a.Info.Name));

		public IEnumerable<Actor> UncontrolledDerricksInOwnQuarter() =>
			World.Actors.Where(a => !a.IsDead && a.IsInWorld
				&& Info.DerrickTypes.Contains(a.Info.Name)
				&& a.Owner != Player
				&& IsInOwnQuarter(a.Location));

		// Map-wide (not limited to the own quarter); the mission cap keeps the AI from
		// overcommitting to every derrick on the map.
		public IEnumerable<Actor> UncontrolledDerricksAnywhere() =>
			World.Actors.Where(a => !a.IsDead && a.IsInWorld
				&& Info.DerrickTypes.Contains(a.Info.Name)
				&& a.Owner != Player);

		public Actor CrateNear(CPos cell, int radius)
		{
			return World.FindActorsInCircle(World.Map.CenterOfCell(cell), WDist.FromCells(radius))
				.FirstOrDefault(a => !a.IsDead && a.IsInWorld && Info.CrateTypes.Contains(a.Info.Name));
		}

		// requireReachable=false: used once a wave has ferried across water and is standing on the
		// far shore, where the AI's own (base-side) ground-reachability set no longer applies.
		public Actor NearestEnemyYard(CPos from, bool requireReachable = true)
		{
			return constructionYards.Actors
				.Where(a => !a.IsDead && a.IsInWorld
					&& Player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy
					&& (!requireReachable || IsReachable(a.Location)))
				.MinByOrDefault(a => (a.Location - from).LengthSquared);
		}

		public CPos? NearestEnemySpawn(CPos from, bool requireReachable = true)
		{
			var candidates = requireReachable ? EnemySpawns.Where(IsReachable).ToList() : EnemySpawns;
			if (candidates.Count == 0)
				return null;
			return candidates.MinBy(s => (s - from).LengthSquared);
		}

		public Actor NearestVisibleEnemyHarvester(CPos from, bool requireReachable = true)
		{
			return World.ActorsHavingTrait<Harvester>()
				.Where(a => !a.IsDead && a.IsInWorld
					&& Player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy
					&& a.CanBeViewedByPlayer(Player)
					&& (!requireReachable || IsReachable(a.Location)))
				.MinByOrDefault(a => (a.Location - from).LengthSquared);
		}

		// ---- Coastal cells (naval ferry support) ---------------------------------

		static readonly CVec[] OrthogonalDirections = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

		// Orthogonal-only Water/River check: a diagonal-only sighting means the direct approach is
		// blocked by a corner (usually Rock) -- a water-CLIFF, not a boardable shore. On a real cliff
		// Rock always sits directly (orthogonally) between land and water; a Clear cell only ever
		// diagonally "sees" the water there by peeking past the Rock corner, never a legitimate
		// embark/disembark point for transport vessels.
		public bool IsCoastal(CPos c)
		{
			if (World.Map.GetTerrainInfo(c).Type == "Beach")
				return true;

			foreach (var d in OrthogonalDirections)
			{
				var n = c + d;
				if (!World.Map.Contains(n))
					continue;

				var t = World.Map.GetTerrainInfo(n).Type;
				if (t == "Water" || t == "River")
					return true;
			}

			return false;
		}

		// True "Beach" terrain only -- NOT the broader IsCoastal() definition, which also accepts a cliff
		// cell that merely touches Water at its edge. A cliff can satisfy the terrain-adjacency check
		// while being physically un-approachable by a real ship (confirmed 2026-07-23 by the user: the
		// same coastline had a genuine sandy beach further along, with tanks correctly waiting there, but
		// FindCoastalCellNear's plain "nearest coastal cell" search picked a nearby CLIFF section instead
		// because it happened to be a few cells closer to base -- the ship then repeatedly tried and
		// failed to reach it, never once considering the beach). Used to prefer Beach over any other
		// coastal cell when searching for an actual embark/landing point.
		public bool IsBeach(CPos c) => World.Map.GetTerrainInfo(c).Type == "Beach";

		// A cell a ship's own Locomotor considers passable (terrain-wise) can still be physically
		// occupied by a Building actor -- e.g. the Sub Pen's own footprint spilling one cell beyond
		// its reported anchor. MovementCostForCell only reflects terrain, never actor occupancy, so
		// without this check the reachable set (and anything picked from it, like a dock cell) could
		// include a cell no ship can ever actually stand on (confirmed 2026-07-22: ship stalled at
		// the exact same cell, 2 cells short of the computed dock, for both a tank wave AND a scout
		// group -- reproducible regardless of unit type, pointing at a fixed map obstruction rather
		// than a per-attempt fluke).
		bool NavalFree(CPos c) => World.Map.Contains(c) && !World.ActorMap.GetActorsAt(c).Any(a => a.TraitOrDefault<Building>() != null);

		// Ship-locomotor reachable set, flood-filled from the nearest cell to `seed` that locomotor can
		// actually stand on. Computed fresh per call (not cached/refreshed on a timer, unlike the ground
		// set above) -- this is only called a handful of times per match at specific decision points
		// (naval site planning, ferry embark/landing search), never every tick, so the cost is negligible.
		HashSet<CPos> NavalReachableFrom(CPos seed, Locomotor navalLoco)
		{
			var result = new HashSet<CPos>();
			CPos? start = null;
			for (var r = 0; r <= 24 && start == null; r++)
				foreach (var c in AotOpsUtils.Ring(seed, r))
					if (NavalFree(c) && navalLoco.MovementCostForCell(c) != PathGraph.MovementCostForUnreachableCell)
					{
						start = c;
						break;
					}

			if (start == null)
				return result;

			// Orthogonal-only expansion (NOT CVec.Directions, which is all 8 including diagonals): a ship
			// hugging a building's corner can be diagonally "visible" to the cell on the far side without
			// any real path there -- the same corner-peek issue already fixed for IsCoastal/Beachy this
			// session, just for buildings instead of Rock terrain. Using all 8 directions here let the
			// reachable set silently connect two cells split by the Sub Pen's own footprint (confirmed
			// 2026-07-23: ship stuck at a fixed cell literally attempted to detour around the pen -- "erst
			// runter, dann zur Seite" -- and got stuck mid-detour, because the diagonal "connection" the
			// BFS trusted was never a route the ship's own pathfinder would actually take).
			var queue = new Queue<CPos>();
			queue.Enqueue(start.Value);
			result.Add(start.Value);
			while (queue.Count > 0)
			{
				var c = queue.Dequeue();
				foreach (var d in OrthogonalDirections)
				{
					var n = c + d;
					if (!result.Contains(n) && NavalFree(n)
						&& navalLoco.MovementCostForCell(n) != PathGraph.MovementCostForUnreachableCell)
					{
						result.Add(n);
						queue.Enqueue(n);
					}
				}
			}

			return result;
		}

		// Given a coastal LAND cell (as returned by FindCoastalCellNear), find the specific orthogonally
		// adjacent WATER cell a ship using this locomotor can actually stand on. The land cell itself is
		// never enterable by a ship -- ordering a ship to "Move" straight at it only gets it generically
		// "close" via the pathfinder's own stopping tolerance, not necessarily truly adjacent to the
		// waiting units (confirmed 2026-07-22: ship parked dist2=5/8 from embark, tanks stood right at
		// the shore, EnterTransport never found anyone in interaction range -- the ship needs to be told
		// to go to the water cell that TOUCHES the shore, not the shore cell itself).
		public CPos? DockCellFor(CPos coastalCell, string navalLocomotor, CPos? navalSeed = null)
		{
			if (navalLocomotor == null)
				return null;

			var navalLoco = World.WorldActor.TraitsImplementing<Locomotor>().FirstOrDefault(l => l.Info.Name == navalLocomotor);
			if (navalLoco == null)
				return null;

			// Prefer a dock on the crossing sea (navalSeed) so the ship can actually reach it from open
			// water rather than a building-walled inlet -- see FindCoastalCellNear. Falls back to the
			// local flood if the crossing sea touches no neighbour of this shore cell, so a shore that
			// only the fallback search could justify still gets a usable dock instead of none.
			if (navalSeed != null)
			{
				var seaSet = NavalReachableFrom(navalSeed.Value, navalLoco);
				foreach (var d in OrthogonalDirections)
				{
					var n = coastalCell + d;
					if (seaSet.Contains(n))
						return n;
				}
			}

			var navalReachable = NavalReachableFrom(coastalCell, navalLoco);
			foreach (var d in OrthogonalDirections)
			{
				var n = coastalCell + d;
				if (navalReachable.Contains(n))
					return n;
			}

			return null;
		}

		// Nearest walkable coastal cell to `target` within `radius`. requireOwnReachable restricts
		// to the AI's own (base-side) ground-reachable set — use true for the embark point on home
		// soil, false for a landing cell near the enemy (by definition outside that set).
		//
		// navalLocomotor (optional): when given, the candidate must ALSO have an orthogonal water
		// neighbour that a ship using this locomotor can actually reach by pathing from open water near
		// `target` -- a land cell next to Water terrain in the raw terrain-type sense (IsCoastal) is not
		// necessarily a spot a ship can ever actually dock at: rocks can wall off a small inlet from the
		// open sea even though the two visually "touch". Without this check the AI could pick an embark
		// point a ship can approach only to within a few cells and never truly reach (confirmed
		// 2026-07-22: ship sat idle at dist2ToEmbark=5 for 14+ diagnostic samples, cargo never filled,
		// wave eventually timed out with nobody having boarded).
		// Minimum squared distance a candidate embark cell must keep from every cell in `exclude`
		// (already claimed by another concurrent ferry mission) -- 36 == 6 cells, enough that two
		// missions get genuinely separate stretches of shore rather than adjacent docks that still
		// crowd each other's approach.
		const int ExcludeRadius2 = 36;

		public CPos? FindCoastalCellNear(CPos target, int radius, bool requireOwnReachable, string navalLocomotor = null, IReadOnlyCollection<CPos> exclude = null, CPos? navalSeed = null)
		{
			var navalLoco = navalLocomotor != null
				? World.WorldActor.TraitsImplementing<Locomotor>().FirstOrDefault(l => l.Info.Name == navalLocomotor)
				: null;

			// Preferred: flood reachability from `navalSeed` (the far-shore/crossing sea). Seeding at
			// `target` instead finds the nearest water to the embark shore, which can be a small inlet
			// walled off from the open sea by a building (the Sub Pen's own footprint) or rock -- a ship
			// coming from the real sea could never reach a dock in that pocket (confirmed 2026-07-23:
			// embarkDock=35,13 sat in a pen-blocked notch, the ship oscillated 30s then timed out).
			//
			// This is a PREFERENCE, not a hard requirement: if no shore at all qualifies against the
			// crossing sea, fall back to the local seed (the old behaviour) rather than reporting "no
			// coastal cell" and killing the mission outright -- seeding from the far shore proved too
			// strict on some spawns and regressed groups that previously found a valid embark.
			if (navalLoco != null && navalSeed != null)
			{
				var seaSet = NavalReachableFrom(navalSeed.Value, navalLoco);
				var viaSea = SearchCoastal(target, radius, requireOwnReachable, seaSet, exclude);
				if (viaSea != null)
					return viaSea;
			}

			var localSet = navalLoco != null ? NavalReachableFrom(target, navalLoco) : null;
			return SearchCoastal(target, radius, requireOwnReachable, localSet, exclude);
		}

		// Two passes: real Beach terrain first (a genuine landing spot), only falling back to the
		// broader "any land touching Water" definition (which also matches an unapproachable cliff)
		// if no beach exists within radius at all. See IsBeach for why this matters (confirmed
		// 2026-07-23: a nearby cliff beat the true beach purely on raw distance, and no ship could
		// ever actually reach it).
		//
		// Within each pass, first try to steer clear of cells other concurrent missions already
		// claimed (spreading missions across different shore stretches instead of piling everyone
		// onto the same dock); if that's too restrictive to find anything, fall back to ignoring
		// the claims entirely rather than fail the mission outright.
		CPos? SearchCoastal(CPos target, int radius, bool requireOwnReachable, HashSet<CPos> navalReachable, IReadOnlyCollection<CPos> exclude)
		{
			var beachOnly = FindCoastalCellNearInner(target, radius, requireOwnReachable, navalReachable, requireBeach: true, exclude)
				?? FindCoastalCellNearInner(target, radius, requireOwnReachable, navalReachable, requireBeach: true, exclude: null);
			if (beachOnly != null)
				return beachOnly;

			return FindCoastalCellNearInner(target, radius, requireOwnReachable, navalReachable, requireBeach: false, exclude)
				?? FindCoastalCellNearInner(target, radius, requireOwnReachable, navalReachable, requireBeach: false, exclude: null);
		}

		CPos? FindCoastalCellNearInner(CPos target, int radius, bool requireOwnReachable, HashSet<CPos> navalReachable, bool requireBeach, IReadOnlyCollection<CPos> exclude)
		{
			CPos? best = null;
			var bestDist = int.MaxValue;
			for (var dy = -radius; dy <= radius; dy++)
			{
				for (var dx = -radius; dx <= radius; dx++)
				{
					var c = new CPos(target.X + dx, target.Y + dy);
					if (!World.Map.Contains(c) || !IsPassable(c))
						continue;

					if (requireOwnReachable && !IsReachable(c))
						continue;

					if (requireBeach ? !IsBeach(c) : !IsCoastal(c))
						continue;

					if (navalReachable != null && !OrthogonalDirections.Any(d => navalReachable.Contains(c + d)))
						continue;

					if (exclude != null && exclude.Any(e => (e - c).LengthSquared <= ExcludeRadius2))
						continue;

					var d = (c - target).LengthSquared;
					if (d < bestDist)
					{
						bestDist = d;
						best = c;
					}
				}
			}

			return best;
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			constructionYards.Dispose();
		}
	}
}
