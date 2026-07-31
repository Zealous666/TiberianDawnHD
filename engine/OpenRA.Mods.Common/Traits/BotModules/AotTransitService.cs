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

using System.Collections.Generic;
using System.Linq;

namespace OpenRA.Mods.Common.Traits
{
	// One end of a crossing: a persistent quay with three DISTINCT waiting areas.
	//
	//   Staging  -- the "Verfuegungsraum": a free patch of land well back from the water where
	//               every booked-but-not-yet-called group waits. Keeps the beach thin.
	//   Muster   -- the boarding lane right at the water. ONLY the group that has actually been
	//               called forward (a vessel is assigned and inbound) ever stands here.
	//   Berths   -- one water cell per vessel, reserved exclusively. Two ships ordered to the same
	//               cell meant one docked and the rest milled about outside.
	//
	// See memory/ai-transit-system.md for the full design.
	public sealed class AotTransitStop
	{
		public readonly CPos Shore;
		public readonly bool Home;
		public readonly List<CPos> Berths = [];
		public readonly List<CPos> Muster = [];
		public readonly List<CPos> Staging = [];
		public CPos? StagingCentre;

		// Berth -> the vessel holding it. Exclusive: two ships sent to the same cell meant one docked
		// and the rest milled about outside it.
		public readonly Dictionary<CPos, Actor> BerthClaims = [];

		public AotTransitStop(CPos shore, bool home)
		{
			Shore = shore;
			Home = home;
		}

		// Free berth for `ship`, or the one it already holds.
		//
		// `avoid` is the berth the ship has just proven it cannot reach. Without it the retry was a
		// no-op in exactly the case that matters: free the unreachable berth, ask again, and if the
		// sister ships hold the other two the same one comes straight back (observed 2026-07-29 as a
		// vessel stalling in ToPickup forever). With nothing else free we would rather take a berth
		// already claimed by another ship than hand back the known-bad one.
		public CPos ClaimBerth(Actor ship, CPos? avoid = null)
		{
			foreach (var (cell, holder) in BerthClaims)
				if (holder == ship)
					return cell;

			foreach (var b in Berths)
				if (!BerthClaims.ContainsKey(b) && b != avoid)
				{
					BerthClaims[b] = ship;
					return b;
				}

			foreach (var b in Berths)
				if (b != avoid)
				{
					BerthClaims[b] = ship;
					return b;
				}

			// Falls back to the shore cell so a stop whose berths could not be resolved still produces
			// a usable steer target.
			return Berths.Count > 0 ? Berths[0] : Shore;
		}

		public void FreeBerth(Actor ship)
		{
			foreach (var (cell, holder) in BerthClaims.ToList())
				if (holder == ship)
					BerthClaims.Remove(cell);
		}

		// Berths that vessels keep failing to reach. The naval flood says a cell is water our ships can
		// path through, which is not the same as "a hull can actually sit there" -- a one-cell notch
		// passes the flood and defeats every ship in practice. Without learning it, EVERY vessel
		// rediscovers the same bad berth on EVERY trip (2026-07-29: "could not reach berth 137,114"
		// over and over, from all three hulls).
		readonly Dictionary<CPos, int> berthFailures = [];

		// Returns true if the berth was struck off. Never drops the last one -- a stop with no berths
		// strands every booking routed through it.
		public bool ReportBerthFailure(CPos berth, int limit)
		{
			if (!Berths.Contains(berth) || Berths.Count <= 1)
				return false;

			berthFailures.TryGetValue(berth, out var n);
			berthFailures[berth] = ++n;
			if (n < limit)
				return false;

			Berths.Remove(berth);
			BerthClaims.Remove(berth);
			return true;
		}

		// Everything a change-detector needs: re-logging an unchanged stop every survey would bury
		// the interesting transitions in noise.
		public string Fingerprint =>
			$"{Shore}|{string.Join(",", Berths)}|{string.Join(",", Muster)}|{StagingCentre}|{Staging.Count}";
	}

	// Module-owned transit service. This half surveys the network (quays, boarding lanes, staging
	// grounds); AotTransitTraffic.cs runs the traffic on it (tickets, dispatch, per-vessel state).
	public sealed partial class AotTransitService
	{
		static readonly CVec[] Orthogonal = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

		readonly AotOperationsBotModule ops;
		readonly Dictionary<string, string> logged = [];

		int surveyTicks;

		public AotTransitStop HomeStop { get; private set; }
		public readonly List<AotTransitStop> FarStops = [];
		readonly Dictionary<CPos, AotTransitStop> farBySpawn = [];

		public AotTransitService(AotOperationsBotModule ops)
		{
			this.ops = ops;
			surveyTicks = ops.World.LocalRandom.Next(0, ops.Info.TransitSurveyInterval);
		}

		public void Tick()
		{
			if (--surveyTicks > 0)
				return;

			surveyTicks = ops.Info.TransitSurveyInterval;
			Survey();
		}

		void Log(string message) => OpenRA.Log.Write("debug", $"[AotTransit][{ops.Player.PlayerName}] {message}");

		// Only log a given key when its value actually changed.
		void LogChanged(string key, string message)
		{
			if (logged.TryGetValue(key, out var previous) && previous == message)
				return;

			logged[key] = message;
			Log(message);
		}

		void Survey()
		{
			var intel = ops.Intel;
			if (intel == null || !intel.Ready)
				return;

			// Ships can only ever use the water their pen sits on -- every quay on both shores must be
			// measured against THAT sea, never against "some nearby water".
			var navalSeed = ops.NavalSite();
			if (navalSeed == null)
			{
				LogChanged("seed", "no naval site yet -- no stops can be surveyed");
				return;
			}

			LogChanged("seed", $"naval seed {navalSeed.Value}");

			var loco = ops.Info.FerryLocomotor;
			var baseCentre = ops.BaseCentre();

			// ---- Home stop -------------------------------------------------------------------
			// STICKY: a quay is only re-picked when the one we have has actually gone bad. Re-surveying
			// unconditionally moved the home stop the moment the base centre shifted (observed
			// 2026-07-29: 137,115 -> 134,114) -- harmless while nothing used it, but once troops are
			// waiting in its staging ground a moved quay sends the whole queue marching across the map,
			// and every vessel's reserved berth with it.
			if (!StillGood(HomeStop, home: true))
			{
				var homeShore = intel.FindCoastalCellNear(baseCentre, ops.Info.FerryEmbarkSearchRadius,
					requireOwnReachable: true, loco, exclude: null, navalSeed, strictNavalSeed: true);

				if (homeShore == null)
				{
					HomeStop = null;
					LogChanged("home", "home stop: none -- " +
						intel.DescribeCoastalSearch(baseCentre, ops.Info.FerryEmbarkSearchRadius, true, loco, navalSeed));
					return;
				}

				HomeStop = BuildStop(homeShore.Value, home: true, navalSeed.Value, loco);
				LogChanged("home", Describe("home stop", HomeStop));
			}

			if (HomeStop == null)
				return;

			// ---- Far stops -------------------------------------------------------------------
			// One per enemy spawn, measured as a SEA distance from our own prime berth, so the water
			// leg stays short and the group walks the rest (the old "coastal cell nearest the enemy"
			// rule unloaded convoys straight into the enemy's defences).
			var fromBerth = HomeStop.Berths.Count > 0 ? HomeStop.Berths[0] : HomeStop.Shore;

			foreach (var spawn in intel.EnemySpawns)
			{
				if (farBySpawn.TryGetValue(spawn, out var existing) && StillGood(existing, home: false))
					continue;

				var shore = intel.FindLandingShore(fromBerth, spawn, loco, navalSeed)
					?? intel.FindCoastalCellNear(spawn, ops.Info.FerrySearchRadius,
						requireOwnReachable: false, loco, exclude: null, navalSeed, strictNavalSeed: true);

				if (shore == null)
				{
					LogChanged($"far{spawn}", $"far stop for spawn {spawn}: none");
					continue;
				}

				// Two spawns behind the same beachhead legitimately share one quay -- don't build it twice.
				var stop = FarStops.FirstOrDefault(s => s.Shore == shore.Value);
				if (stop == null)
				{
					stop = BuildStop(shore.Value, home: false, navalSeed.Value, loco);
					FarStops.Add(stop);
					LogChanged($"far{spawn}", Describe($"far stop for spawn {spawn}", stop));
				}

				farBySpawn[spawn] = stop;
			}
		}

		// A quay stays in service as long as it is still physically usable. Anything less strict would
		// be churn; anything stricter (re-scoring against fresh candidates) is exactly the wandering
		// this replaced.
		bool StillGood(AotTransitStop stop, bool home)
		{
			if (stop == null || stop.Berths.Count == 0)
				return false;

			if (!Walkable(stop.Shore, home))
				return false;

			// Berths blocked by a building that went up after the survey (our own pen, most likely).
			return stop.Berths.Any(Free);
		}

		// The far quay serving `target`: the one whose spawn it was surveyed for, else the nearest.
		public AotTransitStop FarStopFor(CPos target)
		{
			if (farBySpawn.TryGetValue(target, out var exact))
				return exact;

			return FarStops.Count == 0 ? null : FarStops.MinBy(s => (s.Shore - target).LengthSquared);
		}

		string Describe(string what, AotTransitStop stop) =>
			$"{what} {stop.Shore}: {stop.Berths.Count} berth(s) {string.Join(" ", stop.Berths)}, " +
			$"muster {string.Join(" ", stop.Muster)}, " +
			$"staging {(stop.StagingCentre?.ToString() ?? "NONE")} ({stop.Staging.Count} cell(s))";

		AotTransitStop BuildStop(CPos shore, bool home, CPos navalSeed, string loco)
		{
			var stop = new AotTransitStop(shore, home);
			stop.Berths.AddRange(SpacedBerths(shore, loco, navalSeed));
			stop.Muster.AddRange(MusterCells(shore, home));

			var staging = FindStaging(shore, home);
			if (staging != null)
			{
				stop.StagingCentre = staging.Value.Centre;
				stop.Staging.AddRange(staging.Value.Cells);
			}

			return stop;
		}

		// A berth needs BOTH properties: water our ships can path to, AND orthogonally alongside ground
		// the troops can stand on. Enforcing only the first is what put the fleet out to sea.
		//
		// Berths must also not be neighbours -- three hulls converging on one corner of a bay simply
		// wedge each other (2026-07-29: all three vessels in a pile beside the pen). But spacing them
		// via DockCellsFor's outward ring walk pushed berths 2 and 3 three to four cells OFFSHORE,
		// where no ground unit can ever board (2026-07-29, second run: ships reported Loading while
		// lying in open water and the troops stopped at the water's edge). So the spread has to run
		// ALONG THE SHORE, which is what scanning for quay cells and thinning them achieves.
		IEnumerable<CPos> SpacedBerths(CPos shore, string loco, CPos navalSeed)
		{
			var want = System.Math.Max(1, ops.Info.FerryCount);
			var spacing2 = ops.Info.TransitBerthSpacing * ops.Info.TransitBerthSpacing;

			var sea = loco == null ? [] : ops.Intel.NavalWaterFrom(navalSeed, loco);
			var quays = new List<CPos>();
			for (var r = 1; r <= ops.Info.TransitBerthSearchRadius; r++)
				foreach (var c in AotOpsUtils.Ring(shore, r))
					if (sea.Contains(c) && Orthogonal.Any(d => ops.Intel.IsPassable(c + d)))
						quays.Add(c);

			// Ring order already sorts by distance from the shore cell, so the prime berth stays the
			// closest one and the spread walks outward along the coast from there.
			var chosen = new List<CPos>();
			foreach (var c in quays)
			{
				if (chosen.Count >= want)
					break;

				if (chosen.All(b => (b - c).LengthSquared >= spacing2))
					chosen.Add(c);
			}

			// A tight bay may simply not hold `want` well-separated quays. Two ships sharing a crowded
			// stretch of real quay beats a ship parked on open water it can never load from.
			foreach (var c in quays)
			{
				if (chosen.Count >= want)
					break;

				if (!chosen.Contains(c))
					chosen.Add(c);
			}

			// Last resort only: no quay cell found at all (odd coastline). Better an approximate dock
			// than a stop with no berths, which would strand every booking through it.
			if (chosen.Count == 0)
				chosen.AddRange(ops.Intel.DockCellsFor(shore, loco, navalSeed, want));

			return chosen;
		}

		// The boarding lane: one land cell per berth, hugging the shore. Deliberately tiny -- everyone
		// who is not next in line belongs in the staging ground, not here.
		IEnumerable<CPos> MusterCells(CPos shore, bool home)
		{
			var result = new List<CPos> { shore };
			for (var r = 1; r <= 2 && result.Count < ops.Info.FerryCount; r++)
				foreach (var c in AotOpsUtils.Ring(shore, r))
				{
					if (result.Count >= ops.Info.FerryCount)
						break;

					if (Walkable(c, home) && !result.Contains(c))
						result.Add(c);
				}

			return result;
		}

		bool Walkable(CPos c, bool requireOwnReachable) =>
			ops.World.Map.Contains(c)
			&& ops.Intel.IsPassable(c)
			&& (!requireOwnReachable || ops.Intel.IsReachable(c));

		// Free of anything that would stop a unit standing there. Buildings are the ones that matter;
		// other units move, so a temporarily occupied cell must not disqualify a staging ground.
		bool Free(CPos c) => !ops.World.ActorMap.GetActorsAt(c).Any(a => a.TraitOrDefault<Building>() != null);

		// The Verfuegungsraum: a compact free patch of land in a distance BAND behind the quay.
		//
		// Distance is measured as a walking distance from the shore (a flood), not as a straight line:
		// a patch 8 cells away across a cliff is not 8 cells away for the troops that have to reach it.
		// The band's lower bound is what actually decongests the beach; the upper bound keeps the walk
		// to the boarding lane short enough that calling a group forward isn't a journey of its own.
		(CPos Centre, List<CPos> Cells)? FindStaging(CPos shore, bool home)
		{
			var dist = new Dictionary<CPos, int> { [shore] = 0 };
			var q = new Queue<CPos>();
			q.Enqueue(shore);

			var max = ops.Info.StagingMaxDistance;
			while (q.Count > 0)
			{
				var c = q.Dequeue();
				if (dist[c] >= max)
					continue;

				foreach (var d in Orthogonal)
				{
					var n = c + d;
					if (Walkable(n, home) && dist.TryAdd(n, dist[c] + 1))
						q.Enqueue(n);
				}
			}

			var baseCentre = ops.BaseCentre();
			var spawns = ops.Intel.EnemySpawns;

			CPos? best = null;
			List<CPos> bestCells = null;
			var bestScore = int.MinValue;

			foreach (var (cell, d) in dist)
			{
				if (d < ops.Info.StagingMinDistance || !Free(cell))
					continue;

				// Never park the waiting army on top of the base builder's plots -- it would fight the
				// planner for the same ground for the rest of the match.
				if (home && (cell - baseCentre).LengthSquared < ops.Info.StagingBaseClearance * ops.Info.StagingBaseClearance)
					continue;

				var cells = FreeRegion(cell, home);
				if (cells.Count < ops.Info.StagingMinCells)
					continue;

				// Room is what matters: a big open field is what stops units wedging each other, and
				// the tighter the ground the more of them end up stuck (User 2026-07-29: "wichtiger
				// ist, dass moeglichst viele freie zellen da sind / es weniger gruende fuer stucking
				// gibt"). Enemy distance is only a weak tiebreak among genuinely equal grounds -- it
				// used to decide in practice, because the old 5x5 measure capped at 25 and left half
				// the map tied at the cap.
				var threat = spawns.Count == 0 ? 0 : spawns.Min(s => (s - cell).LengthSquared);
				var score = cells.Count * 1000 + System.Math.Min(threat, 10000) / 64 - d;
				if (score > bestScore)
				{
					bestScore = score;
					best = cell;
					bestCells = cells;
				}
			}

			return best == null ? null : (best.Value, bestCells);
		}

		// Contiguous free ground around a candidate, flood-filled rather than counted in a fixed 5x5
		// box. The box version topped out at 25 and made most candidates tie, so the tiebreak (enemy
		// distance) silently became the real decision; worse, a "25/25" box could be a corridor walled
		// off on three sides -- exactly the kind of ground units wedge in. The flood measures the room
		// units can actually spread into, and is capped only to bound the cost.
		List<CPos> FreeRegion(CPos centre, bool home)
		{
			var cells = new List<CPos>();
			var seen = new HashSet<CPos> { centre };
			var q = new Queue<CPos>();
			q.Enqueue(centre);

			while (q.Count > 0 && cells.Count < ops.Info.StagingMaxCells)
			{
				var c = q.Dequeue();
				cells.Add(c);

				foreach (var d in Orthogonal)
				{
					var n = c + d;
					if ((n - centre).LengthSquared > ops.Info.StagingRadius * ops.Info.StagingRadius)
						continue;

					if (Walkable(n, home) && Free(n) && seen.Add(n))
						q.Enqueue(n);
				}
			}

			return cells;
		}
	}
}
