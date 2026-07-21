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
	public enum AotStepKind { Building, Fence, Turret }

	public sealed class AotPlanStep
	{
		public AotStepKind Kind;
		public string Role;                 // "NUKE", "SILO", ... (or fence/turret label)
		public string[] Variants = [];      // age-ordered actor variants; builder picks the buildable one
		public CPos TopLeft;                // building: exact planned top-left / turret: the gate
		public List<CPos> FenceNodes = []; // fence: LineBuild node cells (corners + side mids)
		public bool Done;
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Age of Tiberium: full-base planning at match start. Floods the base POCKET (yard to all",
		"gates, bounded by cliffs/water/actors, path-distance capped), detects and seals the gates,",
		"then packs the ENTIRE base layout (fenced power clusters, paired prod/tech, gap fillers,",
		"stealth spread) into the pocket — reach is deliberately ignored (the builder wall-bridges).",
		"Exact port of the offline-validated pipeline. Exposes the main gate as the chokepoint and",
		"all gates as base approaches.")]
	public class AotBasePlannerBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Only plan for players of this faction (internal name).")]
		public readonly string Faction = null;

		[ActorReference]
		public readonly HashSet<string> ConstructionYardTypes = [];

		[ActorReference]
		public readonly HashSet<string> OreMineTypes = [];

		[Desc("Locomotor for ground passability.")]
		public readonly string GroundLocomotor = "foot";

		public readonly int GateSearchRadius = 40;
		public readonly int PocketMaxDistance = 34;
		public readonly int PocketBudget = 1400;
		public readonly int MinWallComponent = 12;

		[Desc("Chokepoint (for defence, NOT base packing): the two-sided terrain neck on the cheapest enemy",
			"path — the validated, user-approved detection. Config below is that detector.")]
		public readonly int ChokeMaxCorridor = 8;
		public readonly int ConYardFenceGap = 2;
		public readonly string NavalLocomotor = "naval";
		public readonly int BridgeRebuildPenalty = 25;
		public readonly int NavalLandingPenalty = 50;
		public readonly int BaseFrontExtent = 10;

		// Age-ordered NOD actor variants per role — the builder uses the first currently buildable one.
		[ActorReference] public readonly string[] NukeTypes = [];
		[ActorReference] public readonly string[] Nuk2Types = [];
		[ActorReference] public readonly string[] SiloTypes = [];
		[ActorReference] public readonly string[] LiteTypes = [];
		[ActorReference] public readonly string[] HandTypes = [];
		[ActorReference] public readonly string[] RadarTypes = [];
		[ActorReference] public readonly string[] StecTypes = [];
		[ActorReference] public readonly string[] FixTypes = [];
		[ActorReference] public readonly string[] HpadTypes = [];
		[ActorReference] public readonly string[] ProcTypes = [];
		[ActorReference] public readonly string[] TmplTypes = [];
		[ActorReference] public readonly string[] MsloTypes = [];
		[ActorReference] public readonly string[] ShrineTypes = [];
		[ActorReference] public readonly string[] SgenTypes = [];
		[ActorReference] public readonly string[] AfldTypes = [];
		[ActorReference] public readonly string[] FturTypes = [];
		[ActorReference] public readonly string[] WallTypes = [];

		public override object Create(ActorInitializer init) { return new AotBasePlannerBotModule(init.Self, this); }
	}

	public class AotBasePlannerBotModule : ConditionalTrait<AotBasePlannerBotModuleInfo>,
		IBotChokepointProvider, IBotBaseApproachProvider
	{
		readonly World world;
		readonly Player player;

		bool planned;
		CPos yard;
		Locomotor loco;
		IResourceLayer resourceLayer;

		public readonly List<AotPlanStep> Rhythm = [];
		public HashSet<CPos> Pocket = [];
		public List<CPos> Gates = [];
		public CPos? MainGate;                 // gate nearest the enemy — used only for base PACKING

		// Defence provider outputs (validated, user-approved): the two-sided corridor chokepoint that the
		// squad manager sends primary units to, and the classified approaches (incl. beach) for secondaries.
		CPos? defenceChokepoint;
		readonly List<BaseApproach> approaches = [];

		CVec yardDim = new(3, 3);
		HashSet<CPos> bridgeCells = [];
		Locomotor navalLoco;

		public AotBasePlannerBotModule(Actor self, AotBasePlannerBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		public bool Planned => planned;

		CPos? IBotChokepointProvider.Chokepoint
		{
			get
			{
				EnsurePlanned();
				return defenceChokepoint;
			}
		}

		IReadOnlyList<BaseApproach> IBotBaseApproachProvider.BaseApproaches
		{
			get
			{
				EnsurePlanned();
				return approaches;
			}
		}

		public void EnsurePlanned()
		{
			if (planned || IsTraitDisabled)
				return;

			if (Info.Faction != null && player.Faction.InternalName != Info.Faction)
				return;

			var conyard = world.ActorsHavingTrait<Building>()
				.FirstOrDefault(a => a.Owner == player && !a.IsDead && Info.ConstructionYardTypes.Contains(a.Info.Name));
			if (conyard == null)
				return;

			yard = conyard.Location;
			var cbi = conyard.Info.TraitInfoOrDefault<BuildingInfo>();
			yardDim = cbi?.Dimensions ?? new CVec(3, 3);
			loco = world.WorldActor.TraitsImplementing<Locomotor>()
				.FirstOrDefault(l => l.Info.Name == Info.GroundLocomotor);
			navalLoco = world.WorldActor.TraitsImplementing<Locomotor>()
				.FirstOrDefault(l => l.Info.Name == Info.NavalLocomotor);
			resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (loco == null)
				return;

			Plan();
			planned = true;
		}

		// ------------------------------------------------------------------ analysis (exact port)

		HashSet<CPos> actorCells;

		bool Passable(CPos c) =>
			world.Map.Contains(c)
			&& loco.MovementCostForCell(c) != PathGraph.MovementCostForUnreachableCell
			&& !actorCells.Contains(c);

		bool Buildable(CPos c)
		{
			if (!world.Map.Contains(c) || actorCells.Contains(c))
				return false;

			var t = world.Map.GetTerrainInfo(c).Type;
			if (t != "Clear" && t != "Road")
				return false;

			return resourceLayer == null || resourceLayer.GetResource(c).Type == null;
		}

		Dictionary<CPos, int> clearance;
		int Clg(CPos c) => clearance.TryGetValue(c, out var v) ? v : 0;

		static readonly CVec[] D4 = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1)];
		static readonly CVec[] D8 =
		[
			new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
			new(1, 1), new(1, -1), new(-1, 1), new(-1, -1)
		];

		void BuildActorCells()
		{
			// EVERY immobile occupying actor (trees, tree clumps, ore mines, civilian fences/buildings,
			// oil pumps, …) blocks movement AND building. Exact per-actor footprints via BuildingInfo.
			actorCells = [];
			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld || a.Owner == player)
					continue;

				var bi = a.Info.TraitInfoOrDefault<BuildingInfo>();
				if (bi == null)
					continue;

				foreach (var c in bi.Tiles(a.Location))
					actorCells.Add(c);
			}
		}

		void BuildClearance()
		{
			// Chebyshev distance to the nearest impassable cell (multi-source BFS, 8-neighbourhood).
			clearance = [];
			var q = new Queue<CPos>();
			var b = world.Map.Bounds;
			for (var y = b.Top; y < b.Bottom; y++)
				for (var x = b.Left; x < b.Right; x++)
				{
					var c = new CPos(x, y);
					if (!Passable(c))
					{
						clearance[c] = 0;
						q.Enqueue(c);
					}
				}

			while (q.Count > 0)
			{
				var c = q.Dequeue();
				foreach (var d in D8)
				{
					var n = c + d;
					if (!world.Map.Contains(n))
						continue;

					var nd = clearance[c] + 1;
					if (!clearance.TryGetValue(n, out var old) || old > nd)
					{
						clearance[n] = nd;
						q.Enqueue(n);
					}
				}
			}
		}

		List<CPos> EnemySpawns()
		{
			var spawns = new List<CPos>();
			foreach (var n in world.Map.ActorDefinitions)
				if (n.Value.Value == "mpspawn")
					spawns.Add(new ActorReference(n.Key, n.Value).GetValue<LocationInit, CPos>());

			if (spawns.Count <= 1)
				return spawns;

			var mine = spawns.MinBy(s => (s - yard).LengthSquared);
			return spawns.Where(s => s != mine).ToList();
		}

		(Dictionary<CPos, int> D, Dictionary<CPos, CPos> Pred) Bfs(CPos start, HashSet<CPos> blocked)
		{
			var dist = new Dictionary<CPos, int> { [start] = 0 };
			var pred = new Dictionary<CPos, CPos>();
			var q = new Queue<CPos>();
			q.Enqueue(start);
			while (q.Count > 0)
			{
				var c = q.Dequeue();
				foreach (var d in D4)
				{
					var n = c + d;
					if (!dist.ContainsKey(n) && Passable(n) && !blocked.Contains(n))
					{
						dist[n] = dist[c] + 1;
						pred[n] = c;
						q.Enqueue(n);
					}
				}
			}

			return (dist, pred);
		}

		void DetectGates(List<CPos> enemies, out List<CPos> gates, out HashSet<CPos> blocked)
		{
			var r = Info.GateSearchRadius;
			gates = [];
			blocked = [];

			CPos? GateOf(Dictionary<CPos, int> dist, Dictionary<CPos, CPos> pred, CPos e)
			{
				if (!dist.ContainsKey(e))
					return null;

				var path = new List<CPos>();
				var cur = e;
				while (pred.TryGetValue(cur, out var p)) { path.Add(cur); cur = p; }
				path.Add(yard);
				path.Reverse();

				CPos? best = null;
				var bestKey = (int.MaxValue, int.MaxValue);
				var any = false;
				for (var pass = 0; pass < 2 && !any; pass++)
					for (var i = 0; i < path.Count; i++)
					{
						var c = path[i];
						if (pass == 0 && (dist[c] < 6 || dist[c] > r - 2))
							continue;

						any = true;
						var key = (Clg(c), i);
						if (best == null || key.CompareTo(bestKey) < 0) { bestKey = key; best = c; }
					}

				return best;
			}

			void BlockGap(CPos g, HashSet<CPos> bl)
			{
				var seen = new HashSet<CPos> { g };
				var q = new Queue<CPos>();
				q.Enqueue(g);
				while (q.Count > 0)
				{
					var c = q.Dequeue();
					bl.Add(c);
					foreach (var d in D8)
					{
						var n = c + d;
						if (!seen.Contains(n) && Passable(n) && Clg(n) <= 2
							&& Math.Abs(n.X - g.X) + Math.Abs(n.Y - g.Y) <= 4)
						{
							seen.Add(n);
							q.Enqueue(n);
						}
					}
				}
			}

			void BlockPatch(CPos g, HashSet<CPos> bl)
			{
				for (var dx = -2; dx <= 2; dx++)
					for (var dy = -2; dy <= 2; dy++)
					{
						var n = g + new CVec(dx, dy);
						if (Passable(n))
							bl.Add(n);
					}
			}

			for (var it = 0; it < 20; it++)
			{
				var (dist, pred) = Bfs(yard, blocked);
				var reach = enemies.Where(e => dist.ContainsKey(e)).ToList();
				if (reach.Count == 0)
					break;

				var progressed = false;
				foreach (var e in reach)
				{
					var g = GateOf(dist, pred, e);
					if (g == null)
						continue;

					BlockGap(g.Value, blocked);
					BlockPatch(g.Value, blocked);
					if (!gates.Any(x => Math.Abs(g.Value.X - x.X) + Math.Abs(g.Value.Y - x.Y) <= 5))
						gates.Add(g.Value);

					progressed = true;
				}

				if (!progressed)
					break;
			}
		}

		HashSet<CPos> PocketArea(HashSet<CPos> blocked)
		{
			var dist = new Dictionary<CPos, int> { [yard] = 0 };
			var order = new List<CPos> { yard };
			var q = new Queue<CPos>();
			q.Enqueue(yard);
			while (q.Count > 0)
			{
				var c = q.Dequeue();
				if (dist[c] >= Info.PocketMaxDistance)
					continue;

				foreach (var d in D4)
				{
					var n = c + d;
					if (!dist.ContainsKey(n) && Passable(n) && !blocked.Contains(n))
					{
						dist[n] = dist[c] + 1;
						q.Enqueue(n);
						order.Add(n);
					}
				}
			}

			return order.Take(Info.PocketBudget).ToHashSet();
		}

		// ------------------------------------------------------------------ templates + packer (exact port)

		sealed class Variant
		{
			public int W, H;                                    // brutto incl. lane/fence
			public List<(CVec Off, string Role)> Buildings = []; // building top-lefts inside the rect
			public bool Fenced;
		}

		sealed class Placement
		{
			public string Name;
			public CPos Pos;
			public Variant V;
		}

		string[] RoleVariants(string role) => role switch
		{
			"NUKE" => Info.NukeTypes,
			"NUK2" => Info.Nuk2Types,
			"SILO" => Info.SiloTypes,
			"LITE" => Info.LiteTypes,
			"HAND" => Info.HandTypes,
			"DOME" => Info.RadarTypes,
			"STEC" => Info.StecTypes,
			"FIX" => Info.FixTypes,
			"HPAD" => Info.HpadTypes,
			"PROC" => Info.ProcTypes,
			"TMPL" => Info.TmplTypes,
			"MSLO" => Info.MsloTypes,
			"SHRN" => Info.ShrineTypes,
			"SGEN" => Info.SgenTypes,
			"AFLD" => Info.AfldTypes,
			_ => [],
		};

		// Building occupied-cell offsets (relative to top-left), exact from rules (includes bib rows).
		Dictionary<string, List<CVec>> roleCells;
		Dictionary<string, CVec> roleDims;

		void BuildRoleGeometry()
		{
			roleCells = [];
			roleDims = [];
			foreach (var role in new[] { "NUKE", "NUK2", "SILO", "LITE", "HAND", "DOME", "STEC", "FIX", "HPAD", "PROC", "TMPL", "MSLO", "SHRN", "SGEN", "AFLD" })
			{
				var variants = RoleVariants(role);
				if (variants.Length == 0)
					continue;

				var bi = world.Map.Rules.Actors[variants[0]].TraitInfoOrDefault<BuildingInfo>();
				if (bi == null)
					continue;

				var cells = bi.Tiles(CPos.Zero).Select(c => new CVec(c.X, c.Y)).ToList();
				roleCells[role] = cells;
				roleDims[role] = new CVec(cells.Max(c => c.X) + 1, cells.Max(c => c.Y) + 1);

				// Precision guard: every age variant of a role must share the footprint.
				foreach (var v in variants.Skip(1))
				{
					var vbi = world.Map.Rules.Actors[v].TraitInfoOrDefault<BuildingInfo>();
					if (vbi != null && (vbi.Dimensions.X != bi.Dimensions.X || vbi.Dimensions.Y != bi.Dimensions.Y))
						Log.Write("debug", $"[AotPlan] WARNING: {v} dims differ from {variants[0]} — plan uses {variants[0]}'s footprint");
				}
			}
		}

		List<Variant> QuadVariants(string role)
		{
			// 2x2 square (both orientations of the building grid), 4-in-a-row, 4-in-a-column — fenced.
			var d = roleDims[role];
			var q = new Variant { W = (2 * d.X) + 2, H = (2 * d.Y) + 2, Fenced = true };
			q.Buildings.Add((new CVec(1, 1), role));
			q.Buildings.Add((new CVec(1 + d.X, 1), role));
			q.Buildings.Add((new CVec(1, 1 + d.Y), role));
			q.Buildings.Add((new CVec(1 + d.X, 1 + d.Y), role));

			var row = new Variant { W = (4 * d.X) + 2, H = d.Y + 2, Fenced = true };
			for (var i = 0; i < 4; i++)
				row.Buildings.Add((new CVec(1 + (i * d.X), 1), role));

			var col = new Variant { W = d.X + 2, H = (4 * d.Y) + 2, Fenced = true };
			for (var i = 0; i < 4; i++)
				col.Buildings.Add((new CVec(1, 1 + (i * d.Y)), role));

			return [q, row, col];
		}

		// First power cluster (user spec): a NUK2 COLUMN beside a NUKE COLUMN (each type stacked
		// vertically), fenced — mirrored variant as fallback, then a single mixed row.
		List<Variant> MixedPowerVariants()
		{
			var dn = roleDims["NUKE"];
			var d2 = roleDims["NUK2"];
			var h = Math.Max(2 * dn.Y, 2 * d2.Y);

			var left = new Variant { W = d2.X + dn.X + 2, H = h + 2, Fenced = true };
			left.Buildings.Add((new CVec(1, 1), "NUK2"));
			left.Buildings.Add((new CVec(1, 1 + d2.Y), "NUK2"));
			left.Buildings.Add((new CVec(1 + d2.X, 1), "NUKE"));
			left.Buildings.Add((new CVec(1 + d2.X, 1 + dn.Y), "NUKE"));

			var right = new Variant { W = d2.X + dn.X + 2, H = h + 2, Fenced = true };
			right.Buildings.Add((new CVec(1 + dn.X, 1), "NUK2"));
			right.Buildings.Add((new CVec(1 + dn.X, 1 + d2.Y), "NUK2"));
			right.Buildings.Add((new CVec(1, 1), "NUKE"));
			right.Buildings.Add((new CVec(1, 1 + dn.Y), "NUKE"));

			var row = new Variant { W = (2 * d2.X) + (2 * dn.X) + 2, H = Math.Max(d2.Y, dn.Y) + 2, Fenced = true };
			row.Buildings.Add((new CVec(1, 1), "NUK2"));
			row.Buildings.Add((new CVec(1 + d2.X, 1), "NUK2"));
			row.Buildings.Add((new CVec(1 + (2 * d2.X), 1), "NUKE"));
			row.Buildings.Add((new CVec(1 + (2 * d2.X) + dn.X, 1), "NUKE"));

			return [left, right, row];
		}

		List<Variant> PairVariants(string a, string b)
		{
			var da = roleDims[a];
			var db = roleDims[b];
			var h = new Variant { W = da.X + db.X + 2, H = Math.Max(da.Y, db.Y) + 2, Fenced = false };
			h.Buildings.Add((new CVec(1, 1), a));
			h.Buildings.Add((new CVec(1 + da.X, 1), b));

			var v = new Variant { W = Math.Max(da.X, db.X) + 2, H = da.Y + db.Y + 2, Fenced = false };
			v.Buildings.Add((new CVec(1, 1), a));
			v.Buildings.Add((new CVec(1, 1 + da.Y), b));

			return [h, v];
		}

		List<Variant> SingleVariants(string role)
		{
			var d = roleDims[role];
			var pad = new Variant { W = d.X + 2, H = d.Y + 2, Fenced = false };
			pad.Buildings.Add((new CVec(1, 1), role));
			var tight = new Variant { W = d.X, H = d.Y, Fenced = false };
			tight.Buildings.Add((CVec.Zero, role));
			return [pad, tight];
		}

		void Plan()
		{
			BuildActorCells();
			BuildClearance();
			BuildRoleGeometry();

			var enemies = EnemySpawns();
			DetectGates(enemies, out var gates, out var blocked);
			Pocket = PocketArea(blocked);
			Gates = gates;

			// MainGate steers only the base PACKING (Prod/FIX orientation).
			MainGate = gates.Count > 0
				? gates.MinBy(g => enemies.Count > 0 ? enemies.Min(e => (e - g).LengthSquared) : 0)
				: null;

			// DEFENCE (validated, user-approved, restored 2026-07-20): the squad manager's chokepoint is the
			// two-sided terrain neck on the cheapest enemy path — NOT a base gate — and the approaches are
			// classified (land/beach/bridge) so weaker units guard the BEACH, not another land gate.
			bridgeCells = [];
			foreach (var bridge in world.ActorsWithTrait<Bridge>())
				foreach (var c in bridge.Trait.FootprintCells)
					bridgeCells.Add(c);

			defenceChokepoint = DetectChokepoint(enemies);
			DetectApproaches(enemies);

			var mines = world.Actors
				.Where(a => !a.IsDead && a.IsInWorld && Info.OreMineTypes.Contains(a.Info.Name))
				.Select(a => a.Location)
				.ToList();
			var mine = mines.Count > 0 ? mines.MinBy(m => (m - yard).LengthSquared) : yard;

			CPos tib = yard;
			if (resourceLayer != null)
			{
				var bestD = long.MaxValue;
				var b = world.Map.Bounds;
				for (var y = b.Top; y < b.Bottom; y++)
					for (var x = b.Left; x < b.Right; x++)
					{
						var c = new CPos(x, y);
						if (resourceLayer.GetResource(c).Type == null)
							continue;

						var d = (long)(c - yard).LengthSquared;
						if (d < bestD) { bestD = d; tib = c; }
					}
			}

			// ---- packer state (exact port: shared lanes, strict buildable for buildings + fence rings) ----
			var bruttoCells = new HashSet<CPos>();
			var builtCells = new HashSet<CPos>();
			for (var dx = -1; dx <= 3; dx++)
				for (var dy = -1; dy <= 3; dy++)
				{
					var c = new CPos(yard.X - 1 + dx, yard.Y - 1 + dy);
					bruttoCells.Add(c);
					if (dx is >= 0 and < 3 && dy is >= 0 and < 3)
						builtCells.Add(c);
				}

			var centres = new List<(double X, double Y)> { (yard.X + 1, yard.Y + 1) };
			var placements = new List<Placement>();
			var sortedPocket = Pocket.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();

			double Dist((double X, double Y) a, CPos b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
			double DistC((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

			bool Valid(CPos pos, Variant v)
			{
				var bcells = new HashSet<CPos>();
				foreach (var (off, role) in v.Buildings)
					foreach (var rc in roleCells[role])
						bcells.Add(pos + off + rc);

				for (var cx = pos.X; cx < pos.X + v.W; cx++)
					for (var cy = pos.Y; cy < pos.Y + v.H; cy++)
					{
						var c = new CPos(cx, cy);
						if (!Pocket.Contains(c) || builtCells.Contains(c))
							return false;

						var onRing = cx == pos.X || cy == pos.Y || cx == pos.X + v.W - 1 || cy == pos.Y + v.H - 1;
						if ((bcells.Contains(c) || (v.Fenced && onRing)) && !Buildable(c))
							return false;

						if (bcells.Contains(c) && bruttoCells.Contains(c))
							return false;
					}

				return true;
			}

			Placement Place(string name, List<Variant> variants, Func<(double X, double Y), double> bias)
			{
				foreach (var v in variants)
				{
					CPos? best = null;
					var bestScore = double.MinValue;
					foreach (var p in sortedPocket)
					{
						if (!Valid(p, v))
							continue;

						var c = (p.X + (v.W / 2.0), p.Y + (v.H / 2.0));
						var spread = centres.Min(ct => DistC(c, ct));
						var score = spread + bias(c);
						if (score > bestScore)
						{
							bestScore = score;
							best = p;
						}
					}

					if (best != null)
					{
						var pos = best.Value;
						for (var cx = pos.X; cx < pos.X + v.W; cx++)
							for (var cy = pos.Y; cy < pos.Y + v.H; cy++)
								bruttoCells.Add(new CPos(cx, cy));

						foreach (var (off, role) in v.Buildings)
							foreach (var rc in roleCells[role])
								builtCells.Add(pos + off + rc);

						centres.Add((pos.X + (v.W / 2.0), pos.Y + (v.H / 2.0)));
						var pl = new Placement { Name = name, Pos = pos, V = v };
						placements.Add(pl);
						return pl;
					}
				}

				Log.Write("debug", $"[AotPlan] FAILED to place {name} — no valid position in pocket");
				return null;
			}

			var gateList = gates;
			double GateBias((double X, double Y) c) => gateList.Count > 0 ? 0.5 * gateList.Min(g => Dist(c, g)) : 0;
			var mainGateBias = MainGate ?? yard;

			var pNuke = Place("POWER0", MixedPowerVariants(), c => -2 * Math.Abs(Dist(c, yard) - 7));
			var pSilo = Place("SILO", SingleVariants("SILO"), c => -2 * Dist(c, mine));
			var pProd = Place("PROD", PairVariants("LITE", "HAND"), c => -1.5 * Dist(c, mainGateBias));
			var pNuk2a = Place("NUK2a", QuadVariants("NUK2"), GateBias);
			var pTech = Place("TECH", PairVariants("DOME", "STEC"), c => -0.5 * Dist(c, yard));
			var pNuk2b = Place("NUK2b", QuadVariants("NUK2"), GateBias);
			var pFix = Place("FIX", SingleVariants("FIX"), c => -2 * Dist(c, mainGateBias));
			var pProc = Place("PROC", SingleVariants("PROC"), c => -1.5 * Dist(c, tib));
			var pAfld = Place("AFLD", SingleVariants("AFLD"), _ => 0);
			var pTmpl = Place("TMPL", SingleVariants("TMPL"), _ => 0);
			var pHpad = Place("HPAD", SingleVariants("HPAD"), _ => 0);
			var pShrn = Place("SHRN", SingleVariants("SHRN"), _ => 0);
			var pMslo = Place("MSLO", SingleVariants("MSLO"), _ => 0);
			var pSgen1 = Place("SGEN1", SingleVariants("SGEN"), _ => 0);
			var sg1 = centres[^1];
			var pSgen2 = Place("SGEN2", SingleVariants("SGEN"), c => 1.5 * DistC(c, sg1));

			BuildRhythm(pNuke, pSilo, pProd, pNuk2a, pTech, pNuk2b, pFix, pProc, pAfld, pTmpl, pHpad, pShrn, pMslo, pSgen1, pSgen2);

			Log.Write("debug", $"[AotPlan] yard={yard} pocket={Pocket.Count} gates=[{string.Join(" ", gates)}] main={MainGate} " +
				$"placed={placements.Count}/15 rhythm={Rhythm.Count} steps");
		}

		// ------------------------------------------------------------------ rhythm

		void AddBuilding(Placement p, int index, string role)
		{
			if (p == null)
				return;

			var (off, r) = p.V.Buildings[index];
			Rhythm.Add(new AotPlanStep
			{
				Kind = AotStepKind.Building,
				Role = role ?? r,
				Variants = RoleVariants(r),
				TopLeft = p.Pos + off
			});
		}

		// Mixed clusters: address the n-th building of a given role, independent of variant layout order.
		void AddBuildingByRole(Placement p, string role, int occurrence)
		{
			if (p == null)
				return;

			var seen = 0;
			for (var i = 0; i < p.V.Buildings.Count; i++)
			{
				if (p.V.Buildings[i].Role != role)
					continue;

				if (seen++ == occurrence)
				{
					AddBuilding(p, i, role);
					return;
				}
			}
		}

		void AddFence(string label, int x, int y, int w, int h)
		{
			// Ring node cells (corners + side mids); LineBuild spans the sides between nearby nodes.
			var nodes = new List<CPos>
			{
				new(x, y), new(x + w - 1, y), new(x + w - 1, y + h - 1), new(x, y + h - 1),
				new(x + (w / 2), y), new(x + w - 1, y + (h / 2)),
				new(x + (w / 2), y + h - 1), new(x, y + (h / 2))
			};
			Rhythm.Add(new AotPlanStep { Kind = AotStepKind.Fence, Role = label, Variants = Info.WallTypes, FenceNodes = nodes.Distinct().ToList() });
		}

		void AddFenceFor(Placement p, string label)
		{
			if (p == null)
				return;

			AddFence(label, p.Pos.X, p.Pos.Y, p.V.W, p.V.H);
		}

		void BuildRhythm(Placement nuke, Placement silo, Placement prod, Placement nuk2a, Placement tech,
			Placement nuk2b, Placement fix, Placement proc, Placement afld, Placement tmpl, Placement hpad,
			Placement shrn, Placement mslo, Placement sgen1, Placement sgen2)
		{
			// AGE 0 — mixed power cluster (2×NUK2 + 2×NUKE, columns): the first NUKE comes first (NUK2
			// requires an existing nuke), the NUK2s interleave with the tech pair, the second NUKE closes
			// the cluster. Yard fenced right after the silo.
			AddBuildingByRole(nuke, "NUKE", 0);
			AddBuilding(silo, 0, "SILO");
			AddFence("YardFence", yard.X - 1, yard.Y - 1, 5, 5);
			AddBuilding(prod, 0, "LITE");
			AddBuilding(prod, 1, "HAND");
			AddBuilding(tech, 0, "DOME");
			AddBuildingByRole(nuke, "NUK2", 0);
			AddBuilding(tech, 1, "STEC");
			AddBuildingByRole(nuke, "NUK2", 1);
			AddBuildingByRole(nuke, "NUKE", 1);
			AddFenceFor(nuke, "PowerFence");
			AddBuilding(hpad, 0, "HPAD");
			AddBuilding(fix, 0, "FIX");
			if (MainGate != null && Info.FturTypes.Length > 0)
			{
				Rhythm.Add(new AotPlanStep { Kind = AotStepKind.Turret, Role = "FTUR", Variants = Info.FturTypes, TopLeft = MainGate.Value });
				Rhythm.Add(new AotPlanStep { Kind = AotStepKind.Turret, Role = "FTUR", Variants = Info.FturTypes, TopLeft = MainGate.Value });
			}

			// AGE 1 — the builder naturally waits at the first entry whose variants are not buildable yet.
			AddBuilding(afld, 0, "AFLD");
			AddBuilding(proc, 0, "PROC");
			for (var i = 0; i < 4; i++)
				AddBuilding(nuk2a, i, "NUK2");
			AddFenceFor(nuk2a, "Nuk2aFence");
			AddBuilding(tmpl, 0, "TMPL");
			AddBuilding(mslo, 0, "MSLO");

			// AGE 2
			AddBuilding(shrn, 0, "SHRN");
			AddBuilding(sgen1, 0, "SGEN");
			AddBuilding(sgen2, 0, "SGEN");
			for (var i = 0; i < 4; i++)
				AddBuilding(nuk2b, i, "NUK2");
			AddFenceFor(nuk2b, "Nuk2bFence");
		}

		// ------------------------------------------------------------ VALIDATED defence detection (ported)

		static int Manhattan(CPos a, CPos b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

		static CVec Cardinal(CVec v)
		{
			if (v.X == 0 && v.Y == 0)
				return new CVec(0, 1);

			return Math.Abs(v.X) >= Math.Abs(v.Y) ? new CVec(Math.Sign(v.X), 0) : new CVec(0, Math.Sign(v.Y));
		}

		CPos? NearestPassable(CPos origin, int maxRadius)
		{
			foreach (var c in world.Map.FindTilesInAnnulus(origin, 0, maxRadius).OrderBy(c => (c - origin).LengthSquared))
				if (Passable(c))
					return c;

			return null;
		}

		bool WaterPassable(CPos c) => world.Map.Contains(c) && navalLoco != null
			&& navalLoco.MovementCostForCell(c) != PathGraph.MovementCostForUnreachableCell;

		// Multi-modal enemy-effort field (land +1, destroyed bridge +BridgeRebuildPenalty, naval +Naval).
		Dictionary<CPos, int> BuildThreatField(List<CPos> seeds)
		{
			var cost = new Dictionary<CPos, int>();
			var queue = new PriorityQueue<CPos, int>();
			foreach (var seed in seeds)
			{
				var s = NearestPassable(seed, 8);
				if (s.HasValue && !cost.ContainsKey(s.Value))
				{
					cost[s.Value] = 0;
					queue.Enqueue(s.Value, 0);
				}
			}

			while (queue.TryDequeue(out var c, out var g))
			{
				if (g > cost[c])
					continue;

				var onWater = WaterPassable(c) && !Passable(c);
				foreach (var dir in D4)
				{
					var n = c + dir;
					int step;
					if (onWater)
					{
						if ((WaterPassable(n) && !Passable(n)) || Passable(n) || bridgeCells.Contains(n))
							step = 1;
						else
							continue;
					}
					else if (Passable(n))
						step = 1;
					else if (bridgeCells.Contains(n))
						step = Info.BridgeRebuildPenalty;
					else if (WaterPassable(n))
						step = Info.NavalLandingPenalty;
					else
						continue;

					var ng = g + step;
					if (!cost.TryGetValue(n, out var known) || ng < known)
					{
						cost[n] = ng;
						queue.Enqueue(n, ng);
					}
				}
			}

			return cost;
		}

		CPos? DetectChokepoint(List<CPos> enemies)
		{
			var enemyDist = BuildThreatField(enemies);
			var start = NearestPassable(yard, 8);
			if (start == null || !enemyDist.ContainsKey(start.Value))
				return FallbackFrontLine(enemies);

			// Descend the effort field from the base to trace the cheapest enemy approach.
			var path = new List<CPos>();
			var cur = start.Value;
			var guard = 0;
			while (enemyDist.TryGetValue(cur, out var dcur) && dcur > 0 && guard++ < 8000)
			{
				path.Add(cur);
				var next = cur;
				var nextD = dcur;
				foreach (var dir in D4)
				{
					var nc = cur + dir;
					if (enemyDist.TryGetValue(nc, out var dn) && dn < nextD) { nextD = dn; next = nc; }
				}

				if (next == cur)
					break;

				cur = next;
			}

			var floor = yardDim.X + Info.ConYardFenceGap + 2;
			var maxDist = Info.GateSearchRadius;
			var maxW = Info.ChokeMaxCorridor;

			bool Wall(CPos c) => !Passable(c) && !bridgeCells.Contains(c);
			int RayToWall(CPos from, CVec dir)
			{
				for (var k = 1; k <= maxW; k++)
					if (Wall(from + (dir * k)))
						return k;

				return maxW + 1;
			}

			// Wall-component sizes: a real gate is flanked by SUBSTANTIAL walls, not a bypassable rock island.
			var wallComp = new Dictionary<CPos, int>();
			var compSize = new List<int> { 0 };
			var b0 = world.Map.Bounds;
			for (var wy = b0.Top; wy < b0.Bottom; wy++)
				for (var wx = b0.Left; wx < b0.Right; wx++)
				{
					var w0 = new CPos(wx, wy);
					if (!Wall(w0) || wallComp.ContainsKey(w0))
						continue;

					var id = compSize.Count;
					var n = 0;
					var q = new Queue<CPos>();
					q.Enqueue(w0);
					wallComp[w0] = id;
					while (q.Count > 0)
					{
						var wc = q.Dequeue();
						n++;
						foreach (var d8 in D8)
						{
							var nb = wc + d8;
							if (world.Map.Contains(nb) && Wall(nb) && !wallComp.ContainsKey(nb))
							{
								wallComp[nb] = id;
								q.Enqueue(nb);
							}
						}
					}

					compSize.Add(n);
				}

			bool Substantial(CPos c) => wallComp.TryGetValue(c, out var id) && compSize[id] >= Info.MinWallComponent;

			CPos? choke = null;
			var bestWidth = int.MaxValue;
			var bestFwd = int.MaxValue;
			for (var i = 1; i < path.Count - 1; i++)
			{
				var c = path[i];
				var db = (c - yard).Length;
				if (db < floor || db > maxDist || !(Passable(c) || bridgeCells.Contains(c)))
					continue;

				var a = path[Math.Max(0, i - 1)];
				var b = path[Math.Min(path.Count - 1, i + 1)];
				var dir = Cardinal(b - a);
				var perp = new CVec(-dir.Y, dir.X);

				var left = RayToWall(c, perp);
				var right = RayToWall(c, -perp);
				if (left > maxW || right > maxW)
					continue;

				if (!Substantial(c + (perp * left)) || !Substantial(c - (perp * right)))
					continue;

				var width = left + right;
				if (width < bestWidth || (width == bestWidth && db < bestFwd))
				{
					bestWidth = width;
					bestFwd = db;
					choke = c;
				}
			}

			if (choke.HasValue)
			{
				Log.Write("debug", $"[AotPlan] Choke(defence)=two-sided {choke.Value} width={bestWidth}");
				return choke.Value;
			}

			var front1 = path.FirstOrDefault(c => (c - yard).Length >= Info.BaseFrontExtent);
			var frontLine = front1 != default ? front1 : FallbackFrontLine(enemies);
			Log.Write("debug", $"[AotPlan] Choke(defence)=frontline {frontLine} (open terrain, no two-sided neck)");
			return frontLine;
		}

		CPos? FallbackFrontLine(List<CPos> enemies)
		{
			var dir = enemies.Count > 0 ? Cardinal(enemies.MinBy(e => (e - yard).LengthSquared) - yard) : new CVec(0, 1);
			for (var k = Info.BaseFrontExtent; k >= 1; k--)
			{
				var c = yard + (dir * k);
				if (Passable(c))
					return c;
			}

			return NearestPassable(yard, 8);
		}

		bool Beachy(CPos c)
		{
			if (world.Map.GetTerrainInfo(c).Type == "Beach")
				return true;

			for (var dx = -1; dx <= 1; dx++)
				for (var dy = -1; dy <= 1; dy++)
					if (world.Map.GetTerrainInfo(c + new CVec(dx, dy)).Type == "Water")
						return true;

			return false;
		}

		void DetectApproaches(List<CPos> enemies)
		{
			approaches.Clear();
			var start = NearestPassable(yard, 8) ?? yard;
			var r = Info.GateSearchRadius;

			(Dictionary<CPos, int> D, Dictionary<CPos, CPos> Pred) ApproachBfs(HashSet<CPos> blocked)
			{
				var dist = new Dictionary<CPos, int> { [start] = 0 };
				var pred = new Dictionary<CPos, CPos>();
				var q = new Queue<CPos>();
				q.Enqueue(start);
				while (q.Count > 0)
				{
					var c = q.Dequeue();
					foreach (var d in D4)
					{
						var n = c + d;
						if (!dist.ContainsKey(n) && Passable(n) && !blocked.Contains(n))
						{
							dist[n] = dist[c] + 1;
							pred[n] = c;
							q.Enqueue(n);
						}
					}
				}

				return (dist, pred);
			}

			CPos? GateOf(Dictionary<CPos, int> dist, Dictionary<CPos, CPos> pred, CPos e)
			{
				if (!dist.ContainsKey(e))
					return null;

				var path = new List<CPos>();
				var cur = e;
				while (pred.TryGetValue(cur, out var p)) { path.Add(cur); cur = p; }
				path.Add(start);
				path.Reverse();

				CPos? best = null;
				var bestKey = (int.MaxValue, int.MaxValue);
				var any = false;
				for (var pass = 0; pass < 2 && !any; pass++)
					for (var i = 0; i < path.Count; i++)
					{
						var c = path[i];
						if (pass == 0 && (dist[c] < 6 || dist[c] > r - 2))
							continue;

						any = true;
						var key = (Clg(c), i);
						if (best == null || key.CompareTo(bestKey) < 0) { bestKey = key; best = c; }
					}

				return best;
			}

			void BlockGap(CPos g, HashSet<CPos> blocked)
			{
				var seen = new HashSet<CPos> { g };
				var q = new Queue<CPos>();
				q.Enqueue(g);
				while (q.Count > 0)
				{
					var c = q.Dequeue();
					blocked.Add(c);
					foreach (var d in D8)
					{
						var n = c + d;
						if (!seen.Contains(n) && Passable(n) && Clg(n) <= 2 && Manhattan(n, g) <= 4)
						{
							seen.Add(n);
							q.Enqueue(n);
						}
					}
				}
			}

			// (A) land / bridge gates
			var gates = new List<CPos>();
			var blocked = new HashSet<CPos>();
			for (var it = 0; it < 6; it++)
			{
				var (dist, pred) = ApproachBfs(blocked);
				var newGates = new List<CPos>();
				foreach (var e in enemies)
				{
					var g = GateOf(dist, pred, e);
					if (g.HasValue && !gates.Concat(newGates).Any(x => Manhattan(x, g.Value) <= 6))
						newGates.Add(g.Value);
				}

				if (newGates.Count == 0)
					break;

				gates.AddRange(newGates);
				foreach (var g in newGates)
					BlockGap(g, blocked);
			}

			foreach (var g in gates)
				approaches.Add(new BaseApproach(g, Beachy(g) ? BaseApproachType.Beach : BaseApproachType.Land));

			// (B) beach shore landings
			var (full, _) = ApproachBfs([]);
			var reach = full.Keys.Where(c => full[c] <= r).ToHashSet();
			var shoreSeen = new HashSet<CPos>();
			foreach (var c in reach)
			{
				if (shoreSeen.Contains(c) || !Beachy(c))
					continue;

				var seg = new List<CPos>();
				var q = new Queue<CPos>();
				q.Enqueue(c);
				shoreSeen.Add(c);
				while (q.Count > 0)
				{
					var cc = q.Dequeue();
					seg.Add(cc);
					foreach (var d in D8)
					{
						var n = cc + d;
						if (!shoreSeen.Contains(n) && reach.Contains(n) && Beachy(n)) { shoreSeen.Add(n); q.Enqueue(n); }
					}
				}

				if (seg.Count < 3)
					continue;

				var rep = seg.MinBy(x => full[x]);
				if (!approaches.Any(a => Manhattan(a.Gate, rep) <= 6))
					approaches.Add(new BaseApproach(rep, BaseApproachType.Beach));
			}

			approaches.Sort((a, b) => b.Score.CompareTo(a.Score));
			var kept = new List<BaseApproach>();
			foreach (var a in approaches)
				if (!kept.Any(k => Manhattan(k.Gate, a.Gate) <= 6))
					kept.Add(a);

			approaches.Clear();
			approaches.AddRange(kept);
			Log.Write("debug", "[AotPlan] Approaches(defence): " + string.Join(", ", approaches.Select(a => $"{a.Type}@{a.Gate}")));
		}
	}
}
