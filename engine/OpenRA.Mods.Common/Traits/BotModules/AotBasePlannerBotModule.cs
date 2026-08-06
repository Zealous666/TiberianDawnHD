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
		public List<CPos> FencePerimeter = []; // fence: every ring cell (LineBuild's legitimate fill-in) -- used to tell an intended ring segment apart from a stray inter-ring bridge LineBuild auto-connected
		public bool Done;

		// Building steps that sit on water (the naval pen): the LAND cell the wall chain has to reach
		// for this site to end up inside buildable area. The site was validated against exactly this
		// cell, so the bridge must aim at it -- aiming at the site itself let the chain stop at a
		// different, closer shore cell and fall one tile short, forever (see FindNavalSite).
		public CPos? BridgeTarget;

		// Defence steps (SAM/FTUR/GUN/OBELISK + their gate fences) run through their OWN independent
		// chooser (AotBaseBuilderBotModule.ChooseStep(defense: true)), separate from the core economy/tech
		// Rhythm -- user spec 2026-07-31, after the single shared strict-first-open queue meant a single
		// unplaceable SAM site could permanently block every core building queued behind it (PROC, TECH,
		// the next Age's whole build-out). Only Defence steps ever time out and get skipped (StuckTicks/
		// Skipped below); a core step must NEVER be silently abandoned -- it may gate an Age upgrade.
		public bool Defense;

		// Ticks StartStep has found this step's target genuinely unplaceable (both PermanentlyBlocked's
		// TryResite AND OutOfReachOnly's TryBridgeStep failed to make progress this attempt). Reset to 0
		// the moment either recovery succeeds, or the step is finally built. ONLY consulted for Defense
		// steps -- see AotBaseBuilderBotModuleInfo.DefenseStepTimeoutTicks.
		public int StuckTicks;

		// Set once a Defense step's timeout fires: Done becomes true (so ChooseStep moves past it) but
		// Skipped marks that nothing was actually built, so RebuildScan must never reopen it -- retrying a
		// site that was already given up on would just restart the same stuck cycle forever.
		public bool Skipped;

		// Which Age tier (0-3) this step belongs to, set from BuildRhythm's own section boundaries.
		// Consumed by AotBaseBuilderBotModule.AgeRhythmComplete(tier) to gate the age-UPGRADE purchase
		// on "this tier's entire planned Rhythm is done" (user spec 2026-07-31), so it stops competing
		// for cash with a base that is still mid-build.
		public int Age;
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

		[ActorReference]
		[Desc("Resource-spreading actors (blossom trees). Buildings keep GrowthSourceMargin cells away —",
			"tiberium GROWS onto planned cells otherwise and deadlocks the build plan.")]
		public readonly HashSet<string> GrowthSourceTypes = [];

		[Desc("Building/fence cells keep this margin to resource-spreading actors.")]
		public readonly int GrowthSourceMargin = 4;

		[Desc("Radius around the PRIMARY chokepoint within which trees/scenery the AI will actually",
			"clear at runtime (AotOperationsBotModule.ChokeClearRadius) do NOT count as blocking the",
			"gate-defence cluster's own placement search. Should match ChokeClearRadius for the",
			"assumption to hold; kept as an independent value since planner and Operations are",
			"separate trait instances. Never applied to secondary approaches -- their trees are never",
			"actually cleared at runtime, so they must keep blocking exactly as before.")]
		public readonly int TreeClearRadius = 5;

		[Desc("Building/fence cells keep this margin to already-grown resource cells.")]
		public readonly int ResourceMargin = 2;

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
		[ActorReference] public readonly string[] GunTypes = [];
		[ActorReference] public readonly string[] SamTypes = [];
		[ActorReference] public readonly string[] ObeliskTypes = [];
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

		// The main gate-defence cluster's own reserved rectangle (buildings + fence ring), exposed so
		// the chokepoint garrison can be kept OUT of it -- both are independently biased toward the
		// same `choke` cell (garrison holds AT the choke; the cluster is placed as close to it as
		// possible), so without this a garrison unit "correctly" holding position sits right on top of
		// a planned building or fence node forever, permanently blocking it (user-fund 2026-08-01).
		public CPos? GateClusterTopLeft;
		public CVec GateClusterSize;

		public bool IsInsideGateCluster(CPos c) =>
			GateClusterTopLeft != null
			&& c.X >= GateClusterTopLeft.Value.X && c.X < GateClusterTopLeft.Value.X + GateClusterSize.X
			&& c.Y >= GateClusterTopLeft.Value.Y && c.Y < GateClusterTopLeft.Value.Y + GateClusterSize.Y;

		// Defence provider outputs (validated, user-approved): the two-sided corridor chokepoint that the
		// squad manager sends primary units to, and the classified approaches (incl. beach) for secondaries.
		CPos? defenceChokepoint;
		CVec defenceChokeAxis = new(1, 0);   // perpendicular to the corridor at defenceChokepoint -- lets gate turrets flank the neck side by side instead of stacking along the path
		CVec defenceChokePathDir = new(0, 1); // the corridor's own path direction at defenceChokepoint (along the enemy's approach, not across it)
		int defenceChokeAxisPlusDist = 3;    // cells from choke to the wall in the +defenceChokeAxis direction
		int defenceChokeAxisMinusDist = 3;   // cells from choke to the wall in the -defenceChokeAxis direction
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

		// Diagnostic (User 2026-08-01: "AI baut keine Gebaeude mehr" + the ops log showing
		// "no chokepoint provider -> skipping to ArcoRaid"). Both symptoms share one possible
		// root: EnsurePlanned() bails out through one of its four silent early returns and no
		// plan is ever produced -- there was no way to tell WHICH one from the outside. Logged
		// once per distinct reason (planBailReason) so a stuck planner names its own cause
		// instead of spamming every tick.
		string planBailReason;

		void BailPlan(string reason)
		{
			if (planBailReason == reason)
				return;

			planBailReason = reason;
			Log.Write("debug", $"[AotPlanner] {player.PlayerName}: not planning -- {reason}");
		}

		public void EnsurePlanned()
		{
			if (planned || IsTraitDisabled)
			{
				if (!planned)
					BailPlan("trait disabled");

				return;
			}

			if (Info.Faction != null && player.Faction.InternalName != Info.Faction)
			{
				BailPlan($"faction mismatch (player={player.Faction.InternalName}, module wants {Info.Faction})");
				return;
			}

			var conyard = world.ActorsHavingTrait<Building>()
				.FirstOrDefault(a => a.Owner == player && !a.IsDead && Info.ConstructionYardTypes.Contains(a.Info.Name));
			if (conyard == null)
			{
				var owned = world.ActorsHavingTrait<Building>()
					.Where(a => a.Owner == player && !a.IsDead)
					.Select(a => a.Info.Name)
					.Distinct();
				BailPlan($"no construction yard (ConstructionYardTypes=[{string.Join(", ", Info.ConstructionYardTypes)}], " +
					$"owned buildings=[{string.Join(", ", owned)}])");
				return;
			}

			yard = conyard.Location;
			var cbi = conyard.Info.TraitInfoOrDefault<BuildingInfo>();
			yardDim = cbi?.Dimensions ?? new CVec(3, 3);
			loco = world.WorldActor.TraitsImplementing<Locomotor>()
				.FirstOrDefault(l => l.Info.Name == Info.GroundLocomotor);
			navalLoco = world.WorldActor.TraitsImplementing<Locomotor>()
				.FirstOrDefault(l => l.Info.Name == Info.NavalLocomotor);
			resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (loco == null)
			{
				BailPlan($"ground locomotor '{Info.GroundLocomotor}' not found");
				return;
			}

			Plan();
			planned = true;
			Log.Write("debug", $"[AotPlanner] {player.PlayerName}: planned at yard={yard} " +
				$"rhythmSteps={Rhythm.Count} pocket={Pocket.Count} gates={Gates.Count} " +
				$"choke={(defenceChokepoint.HasValue ? defenceChokepoint.Value.ToString() : "NONE")} " +
				$"approaches={approaches.Count}");
		}

		// ------------------------------------------------------------------ analysis (exact port)

		HashSet<CPos> actorCells;

		// Cells within Info.TreeClearRadius of the MAIN chokepoint occupied by an actor the AI will
		// actually go clear at runtime (TickChokeHold -> ClearNearbyObstacles, primary choke only --
		// user spec 2026-08-01). Populated ONCE, right after defenceChokepoint is found in Plan(), so
		// it plays no part in choke DETECTION itself (Wall()/RayToWall still see the map as it stands
		// today -- deliberately: a corridor that only looks wide once trees are gone is not a
		// circularity worth untangling, and treating trees as walls during detection is if anything the
		// safer, more conservative choice). It DOES relax Passable/Buildable for everything computed
		// AFTER that point -- concretely just the gate-cluster's own LocalFlood/PlaceIn search -- so the
		// defence cluster is no longer planned as if trees the AI is about to bulldoze were permanent.
		// Deliberately NEVER populated for secondary approaches: ClearNearbyObstacles only ever runs at
		// the primary choke, so a secondary gate's trees genuinely are never cleared and must keep
		// blocking the planner exactly as before.
		HashSet<CPos> clearableTreeCells = [];

		bool Passable(CPos c) =>
			world.Map.Contains(c)
			&& loco.MovementCostForCell(c) != PathGraph.MovementCostForUnreachableCell
			&& (!actorCells.Contains(c) || clearableTreeCells.Contains(c));

		bool Buildable(CPos c)
		{
			if (!world.Map.Contains(c))
				return false;

			if (actorCells.Contains(c) && !clearableTreeCells.Contains(c))
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

			// Set when PlaceGateCluster could not find ANY position near its anchor that keeps the
			// choke corridor passable together with the fence -- the buildings still get built, just
			// without the surrounding wall, since an open passage matters more than a closed ring
			// (user spec 2026-08-01, "worst case, den zaun weglassen").
			public bool SkipFence;
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
			"FTUR" => Info.FturTypes,
			"GUN" => Info.GunTypes,
			"SAM" => Info.SamTypes,
			"OBELISK" => Info.ObeliskTypes,
			_ => [],
		};

		// Building occupied-cell offsets (relative to top-left), exact from rules (includes bib rows).
		Dictionary<string, List<CVec>> roleCells;
		Dictionary<string, CVec> roleDims;

		void BuildRoleGeometry()
		{
			roleCells = [];
			roleDims = [];
			foreach (var role in new[] { "NUKE", "NUK2", "SILO", "LITE", "HAND", "DOME", "STEC", "FIX", "HPAD", "PROC", "TMPL", "MSLO", "SHRN", "SGEN", "AFLD", "FTUR", "GUN", "SAM", "OBELISK" })
			{
				var variants = RoleVariants(role);
				if (variants.Length == 0)
					continue;

				// A bad/misspelled actor name in a Types list (e.g. "SAM" instead of the actual "sam" --
				// Rules.Actors keys are lowercase) used to crash the whole bot module here via a direct
				// indexer KeyNotFoundException. Log and skip the role instead: everything downstream
				// already treats a missing roleDims/roleCells entry as "this role isn't configured" (see
				// e.g. the SAM roleDims.ContainsKey guard in Plan()).
				if (!world.Map.Rules.Actors.TryGetValue(variants[0], out var ai))
				{
					Log.Write("debug", $"[AotPlan][{player.PlayerName}] WARNING: role {role} references unknown actor '{variants[0]}' — skipped");
					continue;
				}

				var bi = ai.TraitInfoOrDefault<BuildingInfo>();
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
						Log.Write("debug", $"[AotPlan][{player.PlayerName}] WARNING: {v} dims differ from {variants[0]} — plan uses {variants[0]}'s footprint");
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

		// Gate-defence bulk (user spec): Turret-FlameTurret-Turret in a single row or column, fenced as
		// ONE ring around all three -- exactly the same "planned bulk" shape as MixedPowerVariants (this
		// IS the gate's power-cluster equivalent). preferHorizontal should match the flanking axis's own
		// dominant direction (a vertical flanking axis means the row runs vertically too, continuing
		// outward along that axis rather than sticking out sideways into the driving lane); the other
		// orientation is kept as a fallback if the preferred one can't fit.
		List<Variant> GateClusterVariants(bool preferHorizontal)
		{
			var dg = roleDims["GUN"];
			var df = roleDims["FTUR"];

			var h = new Variant { W = dg.X + df.X + dg.X + 2, H = Math.Max(dg.Y, df.Y) + 2, Fenced = true };
			h.Buildings.Add((new CVec(1, 1), "GUN"));
			h.Buildings.Add((new CVec(1 + dg.X, 1), "FTUR"));
			h.Buildings.Add((new CVec(1 + dg.X + df.X, 1), "GUN"));

			var v = new Variant { W = Math.Max(dg.X, df.X) + 2, H = dg.Y + df.Y + dg.Y + 2, Fenced = true };
			v.Buildings.Add((new CVec(1, 1), "GUN"));
			v.Buildings.Add((new CVec(1, 1 + dg.Y), "FTUR"));
			v.Buildings.Add((new CVec(1, 1 + dg.Y + df.Y), "GUN"));

			return preferHorizontal ? [h, v] : [v, h];
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

			// See clearableTreeCells' own comment for the full reasoning. Populated ONLY around the
			// primary choke, ONLY now that its location is finally known -- everything computed above
			// this point (Pocket included) already ran against the strict, tree-blocking obstacle set,
			// so this cannot retroactively change anything already decided; it only relaxes what runs
			// AFTER it (the gate-cluster's own local search, right below).
			if (defenceChokepoint != null)
			{
				var choke = defenceChokepoint.Value;
				var r = Info.TreeClearRadius;
				foreach (var a in world.Actors)
				{
					if (a.IsDead || !a.IsInWorld || !a.Owner.NonCombatant
						|| !a.Info.HasTraitInfo<HealthInfo>() || a.Info.HasTraitInfo<ChangesTerrainInfo>())
						continue;

					var bi = a.Info.TraitInfoOrDefault<BuildingInfo>();
					if (bi == null)
						continue;

					foreach (var c in bi.Tiles(a.Location))
						if (Math.Max(Math.Abs(c.X - choke.X), Math.Abs(c.Y - choke.Y)) <= r)
							clearableTreeCells.Add(c);
				}
			}

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

			// Tiberium GROWS during the match: keep buildings away from current resource cells and (wider)
			// from the spreader actors, or the plan's cells get overgrown and the executor deadlocks.
			var growthHazard = new HashSet<CPos>();
			if (resourceLayer != null)
			{
				var b1 = world.Map.Bounds;
				for (var y = b1.Top; y < b1.Bottom; y++)
					for (var x = b1.Left; x < b1.Right; x++)
					{
						var c = new CPos(x, y);
						if (resourceLayer.GetResource(c).Type == null)
							continue;

						for (var dx = -Info.ResourceMargin; dx <= Info.ResourceMargin; dx++)
							for (var dy = -Info.ResourceMargin; dy <= Info.ResourceMargin; dy++)
								growthHazard.Add(c + new CVec(dx, dy));
					}
			}

			foreach (var a in world.Actors)
			{
				if (a.IsDead || !a.IsInWorld || !Info.GrowthSourceTypes.Contains(a.Info.Name))
					continue;

				for (var dx = -Info.GrowthSourceMargin; dx <= Info.GrowthSourceMargin; dx++)
					for (var dy = -Info.GrowthSourceMargin; dy <= Info.GrowthSourceMargin; dy++)
						growthHazard.Add(a.Location + new CVec(dx, dy));
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

			// Fences (AddFence/AddFenceFor, called later in BuildRhythm) are NOT part of the packer's own
			// collision system -- a LineBuild ring's cells were never reserved in bruttoCells, so a LATER
			// Placement (Place()/PlaceIn()) could legitimately pick a spot overlapping where a fence ring
			// will eventually stand. CanPlaceBuilding then permanently fails once the fence is actually
			// built there, and -- critically -- an OWN wall blocking a spot isn't handled by ANY existing
			// fallback (PermanentlyBlocked only checks resources/foreign actors, not own static actors),
			// so the step waited forever, stalling the whole strict rhythm (confirmed via debug.log: PROC's
			// own footprint had an aot-wall-nod sitting on it, CanPlaceBuilding=False forever, nothing after
			// PROC in the Rhythm ever got a turn). Reserving each fence's full ring the moment its owning
			// Placement is known (mirrors AddFence's own node-to-ring-fill geometry exactly) prevents any
			// later Placement from ever choosing an overlapping spot in the first place.
			void ReserveFenceRing(int x, int y, int w, int h)
			{
				for (var cx = x; cx < x + w; cx++)
				{
					bruttoCells.Add(new CPos(cx, y));
					bruttoCells.Add(new CPos(cx, y + h - 1));
				}

				for (var cy = y; cy < y + h; cy++)
				{
					bruttoCells.Add(new CPos(x, cy));
					bruttoCells.Add(new CPos(x + w - 1, cy));
				}
			}

			void ReserveFenceRingFor(Placement p)
			{
				if (p != null)
					ReserveFenceRing(p.Pos.X, p.Pos.Y, p.V.W, p.V.H);
			}

			double Dist((double X, double Y) a, CPos b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
			double DistC((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

			// Shared bookkeeping every successful placement performs: reserve its footprint, mark its
			// building cells built, register its centre for future spread scoring, and record it.
			// Factored out of Place()/PlaceIn() (previously duplicated verbatim in both) so
			// PlaceGateCluster below can commit through the exact same accounting.
			Placement Commit(string name, CPos pos, Variant v)
			{
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
						if ((bcells.Contains(c) || (v.Fenced && onRing)) && (!Buildable(c) || growthHazard.Contains(c)))
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
						return Commit(name, best.Value, v);
				}

				Log.Write("debug", $"[AotPlan][{player.PlayerName}] FAILED to place {name} — no valid position in pocket");
				return null;
			}

			// Same packer mechanism as Place()/Valid() (variant cascade, spread + bias scoring, strict
			// buildable footprint + fence ring), but scoped to an arbitrary local AREA instead of the
			// base's own Pocket. Needed for the gate-defence clusters: the validated defenceChokepoint can
			// legitimately sit just outside Pocket (DetectGates deliberately blocks a patch around every
			// gate so the PACKER doesn't spill past it -- see BridgeFrontier's matching relaxation), so
			// Place() itself could never find a spot there. Place()'s own bruttoCells/builtCells/centres
			// bookkeeping is still shared so a cluster placed this way is respected by (and respects) every
			// other placement in the plan.
			bool ValidIn(HashSet<CPos> area, CPos pos, Variant v)
			{
				var bcells = new HashSet<CPos>();
				foreach (var (off, role) in v.Buildings)
					foreach (var rc in roleCells[role])
						bcells.Add(pos + off + rc);

				for (var cx = pos.X; cx < pos.X + v.W; cx++)
					for (var cy = pos.Y; cy < pos.Y + v.H; cy++)
					{
						var c = new CPos(cx, cy);
						if (!area.Contains(c) || builtCells.Contains(c))
							return false;

						var onRing = cx == pos.X || cy == pos.Y || cx == pos.X + v.W - 1 || cy == pos.Y + v.H - 1;
						if ((bcells.Contains(c) || (v.Fenced && onRing)) && (!Buildable(c) || growthHazard.Contains(c)))
							return false;

						if (bcells.Contains(c) && bruttoCells.Contains(c))
							return false;
					}

				return true;
			}

			// useSpread=false skips the "maximise distance from every other placement in the whole base"
			// term (right for spreading buildings across the Pocket, wrong for a cluster anchored to one
			// specific point). extraFilter is a HARD straight-line-distance cap independent of the search
			// area's own shape -- the area itself can stay generous (a nearby valid spot can require a
			// detour around clutter that pushes its WALKING distance past a tight area cap even though its
			// straight-line distance is small), while extraFilter is what actually enforces "close enough,
			// not a real detour away from the anchor" (both lessons learned the hard way across several
			// earlier attempts at this exact search, documented in memory/ai-ground-defense.md).
			Placement PlaceIn(HashSet<CPos> area, string name, List<Variant> variants, Func<(double X, double Y), double> bias,
				bool useSpread = true, Func<(double X, double Y), bool> extraFilter = null)
			{
				var sortedArea = area.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();
				foreach (var v in variants)
				{
					CPos? best = null;
					var bestScore = double.MinValue;
					foreach (var p in sortedArea)
					{
						if (!ValidIn(area, p, v))
							continue;

						var c = (p.X + (v.W / 2.0), p.Y + (v.H / 2.0));
						if (extraFilter != null && !extraFilter(c))
							continue;

						var spread = useSpread && centres.Count > 0 ? centres.Min(ct => DistC(c, ct)) : 0;
						var score = spread + bias(c);
						if (score > bestScore)
						{
							bestScore = score;
							best = p;
						}
					}

					if (best != null)
						return Commit(name, best.Value, v);
				}

				Log.Write("debug", $"[AotPlan][{player.PlayerName}] FAILED to place {name} — no valid position in local area");
				return null;
			}

			// Local flood from `from`, treating `blocked` cells as solid, up to `budget` visits. Used to
			// verify the choke corridor stays passable once a candidate cluster+fence rectangle (the
			// fence ring is what actually seals a gap, not just the buildings) goes up there -- a
			// rectangle picked purely for "closest to choke" can otherwise wall off the very passage the
			// cluster is meant to guard (user spec 2026-08-01, screenshots of a sealed chokepoint).
			bool Reaches(CPos from, CPos to, HashSet<CPos> blocked, int budget)
			{
				if (blocked.Contains(from) || blocked.Contains(to))
					return false;

				var visited = new HashSet<CPos> { from };
				var q = new Queue<CPos>();
				q.Enqueue(from);

				while (q.Count > 0 && budget-- > 0)
				{
					var c = q.Dequeue();
					if (c == to)
						return true;

					foreach (var d in D4)
					{
						var n = c + d;
						if (!blocked.Contains(n) && Passable(n) && visited.Add(n))
							q.Enqueue(n);
					}
				}

				return false;
			}

			// Gate cluster placement that ALSO verifies the corridor survives the fence ring that will
			// stand there (user spec 2026-08-01: "verschieben bis ein Durchgang bleibt" as the primary
			// strategy). `pathDir` is the corridor's OWN direction (along the enemy's approach, NOT
			// across it -- see defenceChokePathDir); `baseSide`/`farSide` are fixed reference points well
			// clear of any candidate rectangle so the connectivity flood always has somewhere solid to
			// start and end. Candidates are still ranked "closest to the anchor first", exactly like
			// PlaceIn, but among those tied for best score, only ones that keep the corridor open are
			// accepted. If literally nothing does (a corridor tighter than the cluster itself), the
			// closest candidate of any kind is used anyway and flagged Placement.SkipFence -- BuildRhythm
			// then queues the buildings WITHOUT their fence ring, which is the one thing that can
			// actually seal a passage; ungated buildings are strictly better than no defence at all.
			Placement PlaceGateCluster(HashSet<CPos> area, string name, List<Variant> variants, CPos anchor, CVec pathDir)
			{
				var reach = Math.Max(area.Count > 0 ? (int)Math.Sqrt(area.Count) : 8, 8) + 4;
				var baseSide = NearestPassable(anchor - (pathDir * reach), 6) ?? anchor - (pathDir * reach);
				var farSide = NearestPassable(anchor + (pathDir * reach), 6) ?? anchor + (pathDir * reach);

				var sortedArea = area.OrderBy(c => c.Y).ThenBy(c => c.X).ToList();

				foreach (var v in variants)
				{
					CPos? bestConnected = null;
					var bestConnectedScore = double.MinValue;
					CPos? bestAny = null;
					var bestAnyScore = double.MinValue;

					foreach (var p in sortedArea)
					{
						if (!ValidIn(area, p, v))
							continue;

						var c = (p.X + (v.W / 2.0), p.Y + (v.H / 2.0));
						if (Dist(c, anchor) > 5)
							continue;

						var score = -Dist(c, anchor);

						if (score > bestAnyScore)
						{
							bestAnyScore = score;
							bestAny = p;
						}

						if (score <= bestConnectedScore)
							continue;

						var blocked = new HashSet<CPos>();
						for (var cx = p.X; cx < p.X + v.W; cx++)
							for (var cy = p.Y; cy < p.Y + v.H; cy++)
								blocked.Add(new CPos(cx, cy));

						if (!Reaches(baseSide, farSide, blocked, 1500))
							continue;

						bestConnectedScore = score;
						bestConnected = p;
					}

					if (bestConnected != null)
						return Commit(name, bestConnected.Value, v);

					if (bestAny != null)
					{
						var pl = Commit(name, bestAny.Value, v);
						pl.SkipFence = true;
						Log.Write("debug", $"[AotPlan][{player.PlayerName}] {name}: no position near {anchor} keeps the " +
							"corridor passable with a fence -- built WITHOUT one (worst case, user spec 2026-08-01)");
						return pl;
					}
				}

				Log.Write("debug", $"[AotPlan][{player.PlayerName}] FAILED to place {name} — no valid position in local area");
				return null;
			}

			HashSet<CPos> LocalFlood(CPos seed, int maxDist, int budget)
			{
				var dist = new Dictionary<CPos, int> { [seed] = 0 };
				var q = new Queue<CPos>();
				q.Enqueue(seed);
				var order = new List<CPos> { seed };
				while (q.Count > 0)
				{
					var c = q.Dequeue();
					if (dist[c] >= maxDist)
						continue;

					foreach (var d in D4)
					{
						var n = c + d;
						if (!dist.ContainsKey(n) && Passable(n))
						{
							dist[n] = dist[c] + 1;
							q.Enqueue(n);
							order.Add(n);
						}
					}
				}

				return order.Take(budget).ToHashSet();
			}

			// Centre cell of an already-placed block, for anchoring a search right beside it.
			static CPos BlockAnchor(Placement p) => p.Pos + new CVec(p.V.W / 2, p.V.H / 2);

			// SAM Sites clustered tight around named blocks (user spec 2026-07-31: air raids used to walk
			// straight through the middle of the base because the old 5 SAM Sites were scattered loosely
			// via the ordinary Pocket-wide Place(), with no guarantee any of them actually covered a given
			// building). PlaceIn's own collision check (ValidIn -> builtCells/bruttoCells) already makes it
			// impossible to land ON TOP of the anchor block or its fence ring -- both are reserved in
			// bruttoCells the moment the block itself is placed (ReserveFenceRingFor for fenced blocks, the
			// block's own W×H rectangle regardless of fencing). The tight LocalFlood radius + "closest cell
			// to the anchor" bias is what turns that baseline guarantee into "genuinely touching it" rather
			// than "somewhere in the general area", which is all Place()'s own base-wide spread term gives.
			Placement PlaceAdjacent(string name, string role, CPos anchor, int radius = 9, int budget = 260)
			{
				var area = LocalFlood(anchor, radius, budget);
				return PlaceIn(area, name, SingleVariants(role), c => -Dist(c, anchor), useSpread: false);
			}

			var gateList = gates;
			double GateBias((double X, double Y) c) => gateList.Count > 0 ? 0.5 * gateList.Min(g => Dist(c, g)) : 0;
			var mainGateBias = MainGate ?? yard;

			var pNuke = Place("POWER0", MixedPowerVariants(), c => -2 * Math.Abs(Dist(c, yard) - 7));
			ReserveFenceRingFor(pNuke); // PowerFence -- built in Age 0, reserve immediately

			var pSilo = Place("SILO", SingleVariants("SILO"), c => -2 * Dist(c, mine));
			var pProd = Place("PROD", PairVariants("LITE", "HAND"), c => -1.5 * Dist(c, mainGateBias));
			var pNuk2a = Place("NUK2a", QuadVariants("NUK2"), GateBias);
			ReserveFenceRingFor(pNuk2a); // Nuk2aFence -- built in Age 1, reserve immediately

			var pTech = Place("TECH", PairVariants("DOME", "STEC"), c => -0.5 * Dist(c, yard));
			var pFix = Place("FIX", SingleVariants("FIX"), c => -2 * Dist(c, mainGateBias));

			// Sub Pen / Shipyard: NOT a Rhythm step (user spec, 2026-07-22) — naval production is now built
			// on demand by AotBaseBuilderBotModule.RequestNavalProduction(), called by any Operations
			// mission that actually needs ships/subs/vessels (ferry, and future naval missions). That
			// mechanism reuses the same wall-bridge machinery to reach a coastal site even when the base's
			// own Pocket has none, so it fully supersedes the old coastal-Pocket-only placement here.

			var pProc = Place("PROC", SingleVariants("PROC"), c => -1.5 * Dist(c, tib));

			// Second Refinery: optional (user spec), only added when there's still room for one -- Place()
			// itself already returns null and no-ops downstream (AddBuilding skips a null Placement) when
			// nothing fits, so no separate space check is needed here.
			var pProc2 = Place("PROC2", SingleVariants("PROC"), c => -1.5 * Dist(c, tib));

			var pAfld = Place("AFLD", SingleVariants("AFLD"), _ => 0);
			var pTmpl = Place("TMPL", SingleVariants("TMPL"), _ => 0);
			var pHpad = Place("HPAD", SingleVariants("HPAD"), _ => 0);
			var pShrn = Place("SHRN", SingleVariants("SHRN"), _ => 0);
			var pMslo = Place("MSLO", SingleVariants("MSLO"), _ => 0);
			var pSgen1 = Place("SGEN1", SingleVariants("SGEN"), _ => 0);
			var sg1 = centres[^1];
			var pSgen2 = Place("SGEN2", SingleVariants("SGEN"), c => 1.5 * DistC(c, sg1));

			// Gate defence: a single Flame Turret directly ON the chokepoint (Age 0, no bulk/fence search --
			// user spec after repeated bulk-placement failures on cluttered terrain: a lone 1x1 building
			// just needs its exact cell, resolved at BUILD time by the same proven Bridge/Resite machinery
			// every other plan step already relies on, instead of a bespoke local-area search that kept
			// mis-firing on tight/rocky necks). Two Turrets attach directly beside it in Age 1 (left/right
			// or above/below, matching the flanking axis), THEN the whole 3-building cluster gets fenced.
			// One Obelisk sits directly behind the cluster (toward the yard) in Age 3. All of this is wired
			// into the Rhythm now, at plan time, even though most of it only becomes buildable many ages
			// later -- matches how every other age-gated step in this Rhythm already works.
			//
			// SAM Sites (user spec 2026-07-31, replacing the old base-wide scatter): each one sits
			// IMMEDIATELY beside a specific named block instead of loosely somewhere in the base -- the old
			// scatter left gaps an air raid could walk straight through with nothing reacting nearby.
			//   Age 0: 2x beside the construction yard (outside its YardFence).
			//   Age 1: 2x beside the tech pair, 1x beside the production pair, 2x beside power cluster 1 --
			//          all queued immediately after the Refinery in the Rhythm (see BuildRhythm).
			//   Age 2: 2x behind the gate defence cluster (with the Obelisk, once it exists) + 1x beside
			//          the Temple of Nod.
			//   Age 3: 1x beside the Shrine.
			// Anchors are taken from Placements already resolved above (pTech/pProd/pNuke/pTmpl/pShrn);
			// the gate-cluster-anchored pair is placed further down, once pGateCluster itself exists.
			Placement pSamYard1 = null, pSamYard2 = null;
			Placement pSamTech1 = null, pSamTech2 = null, pSamProd1 = null, pSamPower1a = null, pSamPower1b = null;
			Placement pSamTemple = null, pSamShrine = null;
			Placement pSamDefence1 = null, pSamDefence2 = null; // resolved further down, once pGateCluster exists
			Placement pSilo2 = null; // ditto -- anchored behind the Obelisk's own eventual spot
			if (Info.SamTypes.Length > 0 && roleDims.ContainsKey("SAM"))
			{
				pSamYard1 = PlaceAdjacent("SAM_Yard1", "SAM", yard);
				pSamYard2 = PlaceAdjacent("SAM_Yard2", "SAM", yard);

				if (pTech != null)
				{
					var techAnchor = BlockAnchor(pTech);
					pSamTech1 = PlaceAdjacent("SAM_Tech1", "SAM", techAnchor);
					pSamTech2 = PlaceAdjacent("SAM_Tech2", "SAM", techAnchor);
				}

				if (pProd != null)
					pSamProd1 = PlaceAdjacent("SAM_Prod1", "SAM", BlockAnchor(pProd));

				if (pNuke != null)
				{
					var powerAnchor = BlockAnchor(pNuke);
					pSamPower1a = PlaceAdjacent("SAM_Power1a", "SAM", powerAnchor);
					pSamPower1b = PlaceAdjacent("SAM_Power1b", "SAM", powerAnchor);
				}

				if (pTmpl != null)
					pSamTemple = PlaceAdjacent("SAM_Temple", "SAM", BlockAnchor(pTmpl));

				if (pShrn != null)
					pSamShrine = PlaceAdjacent("SAM_Shrine", "SAM", BlockAnchor(pShrn));
			}

			// Gate defence: a full bulk CLUSTER (user spec) -- Turret-FlameTurret-Turret in a single row or
			// column, fenced as one ring, exactly the same "planned bulk" shape as the power cluster. Found
			// via PlaceIn on a local flood around the chokepoint (the validated defenceChokepoint can
			// legitimately sit just outside Pocket -- see BridgeFrontier's matching relaxation), with a
			// straight-line distance cap so it stays close to the choke rather than wandering off to
			// wherever the nearest clear patch happens to be (both lessons learned the hard way across
			// several earlier attempts, see memory/ai-ground-defense.md). Only the middle (Flame Turret) is
			// queued in Age 0; the 2 Turrets + fence follow in Age 1 (see BuildRhythm).
			Placement pGateCluster = null;
			var secondaryClusters = new List<Placement>();
			if (defenceChokepoint != null && roleDims.ContainsKey("FTUR") && roleDims.ContainsKey("GUN"))
			{
				var choke = defenceChokepoint.Value;
				var axis = defenceChokeAxis;
				var chokeArea = LocalFlood(choke, 12, 300);
				var preferHorizontal = Math.Abs(axis.X) >= Math.Abs(axis.Y);
				pGateCluster = PlaceGateCluster(chokeArea, "GateCluster", GateClusterVariants(preferHorizontal),
					choke, defenceChokePathDir);

				if (pGateCluster != null)
				{
					GateClusterTopLeft = pGateCluster.Pos;
					GateClusterSize = new CVec(pGateCluster.V.W, pGateCluster.V.H);
				}

				// The 2 Age-2 SAM Sites that go BEHIND the gate cluster (user spec) -- same "behind"
				// direction the Age-3 Obelisk uses (Cardinal(yard - clusterCentre), i.e. back towards the
				// yard so the site faces out through the defended gate rather than the wrong way), just
				// anchored further back (radius pushes past the Obelisk's own eventual spot at ~4 cells
				// behind) so the two never contest the same ground.
				if (pGateCluster != null)
				{
					var clusterCentre = BlockAnchor(pGateCluster);
					var behindDefence = Cardinal(new CVec(yard.X - clusterCentre.X, yard.Y - clusterCentre.Y));
					var defenceSamAnchor = clusterCentre + (behindDefence * 7);
					pSamDefence1 = PlaceAdjacent("SAM_Defence1", "SAM", defenceSamAnchor);
					pSamDefence2 = PlaceAdjacent("SAM_Defence2", "SAM", defenceSamAnchor);

					// Second SILO (user spec 2026-08-01): 2-3 cells further back than the Age-3 Obelisk's
					// OWN spot, on the exact same line -- Cardinal(yard - fturPos) from the middle Flame
					// Turret, the identical formula BuildRhythm uses for the Obelisk itself, so the two
					// never drift apart even if the cluster's own layout changes later. Queued as the
					// LAST Age-0 core step (see BuildRhythm), which -- via the same strict-first-open
					// ordering AFLD/PROC already rely on -- naturally lands "once Tech Centre stands and
					// the Age-1 upgrade is about to start" without needing its own age check.
					var fturPos = pGateCluster.Pos + pGateCluster.V.Buildings[1].Off;
					var behindObelisk = Cardinal(new CVec(yard.X - fturPos.X, yard.Y - fturPos.Y));
					var obeliskAnchor = fturPos + (behindObelisk * 4);
					var silo2Anchor = obeliskAnchor + (behindObelisk * 3);
					pSilo2 = PlaceAdjacent("SILO2", "SILO", silo2Anchor);
				}

				// Secondary approaches (user spec): the SAME cluster shape at every other classified
				// approach into the base, each with its own local flanking axis (narrower of the two
				// ray-cast widths at that specific cell -- a different gate can face a completely different
				// direction than the main choke).
				foreach (var approach in approaches)
				{
					if ((approach.Gate - choke).LengthSquared <= 100)
						continue; // within ~10 cells of the main gate -- already defended there

					var gate = approach.Gate;

					int RayLocal(CVec dir)
					{
						for (var k = 1; k <= 8; k++)
							if (!Passable(gate + (dir * k)) && !bridgeCells.Contains(gate + (dir * k)))
								return k;

						return 9;
					}

					var horizWidth = RayLocal(new CVec(1, 0)) + RayLocal(new CVec(-1, 0));
					var vertWidth = RayLocal(new CVec(0, 1)) + RayLocal(new CVec(0, -1));
					var secAxis = horizWidth <= vertWidth ? new CVec(0, 1) : new CVec(1, 0);
					var secArea = LocalFlood(gate, 12, 300);
					var secPreferHorizontal = Math.Abs(secAxis.X) >= Math.Abs(secAxis.Y);

					// secAxis is the FLANKING axis (mirrors defenceChokeAxis); the corridor's own path
					// runs perpendicular to it, same relationship DetectChokepoint uses
					// (defenceChokeAxis = perp of defenceChokePathDir, i.e. perp = (-dir.Y, dir.X)).
					var secPathDir = new CVec(-secAxis.Y, secAxis.X);

					var cluster = PlaceGateCluster(secArea, $"SecondaryCluster_{gate.X}_{gate.Y}",
						GateClusterVariants(secPreferHorizontal), gate, secPathDir);
					if (cluster != null)
						secondaryClusters.Add(cluster);
				}
			}

			BuildRhythm(pNuke, pSilo, pSilo2, pProd, pNuk2a, pTech, pFix, pProc, pProc2, pAfld, pTmpl, pHpad, pShrn, pMslo, pSgen1, pSgen2,
				pSamYard1, pSamYard2, pSamTech1, pSamTech2, pSamProd1, pSamPower1a, pSamPower1b,
				pSamDefence1, pSamDefence2, pSamTemple, pSamShrine, pGateCluster, secondaryClusters);

			Log.Write("debug", $"[AotPlan][{player.PlayerName}] yard={yard} pocket={Pocket.Count} gates=[{string.Join(" ", gates)}] main={MainGate} " +
				$"placed={placements.Count}/15 rhythm={Rhythm.Count} steps");
		}

		// ------------------------------------------------------------------ rhythm

		void BuildRhythm(Placement nuke, Placement silo, Placement silo2, Placement prod, Placement nuk2a, Placement tech,
			Placement fix, Placement proc, Placement proc2, Placement afld, Placement tmpl, Placement hpad,
			Placement shrn, Placement mslo, Placement sgen1, Placement sgen2,
			Placement samYard1, Placement samYard2, Placement samTech1, Placement samTech2, Placement samProd1,
			Placement samPower1a, Placement samPower1b, Placement samDefence1, Placement samDefence2,
			Placement samTemple, Placement samShrine,
			Placement gateCluster, List<Placement> secondaryClusters)
		{
			// Which Age tier (0-3) a step belongs to -- bumped at each "AGE N" section boundary below.
			// Every AddBuilding/AddFence/AddAt call in between picks it up automatically (all four are
			// LOCAL to BuildRhythm now, purely so they can close over this). Consumed by
			// AotBaseBuilderBotModule.AgeRhythmComplete(tier) (user spec 2026-07-31): the age-UPGRADE
			// purchase now waits for the current tier's ENTIRE planned Rhythm -- core and defence alike --
			// to finish before it may even be attempted, so it stops competing for cash against a base
			// that is still mid-build.
			var currentAge = 0;

			void AddBuilding(Placement p, int index, string role, bool defense = false)
			{
				if (p == null)
					return;

				var (off, r) = p.V.Buildings[index];
				Rhythm.Add(new AotPlanStep
				{
					Kind = AotStepKind.Building,
					Role = role ?? r,
					Variants = RoleVariants(r),
					TopLeft = p.Pos + off,
					Defense = defense,
					Age = currentAge
				});
			}

			// Mixed clusters: address the n-th building of a given role, independent of variant layout order.
			void AddBuildingByRole(Placement p, string role, int occurrence, bool defense = false)
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
						AddBuilding(p, i, role, defense);
						return;
					}
				}
			}

			void AddFence(string label, int x, int y, int w, int h, bool defense = false)
			{
				// Ring node cells (corners + side mids); LineBuild spans the sides between nearby nodes.
				var nodes = new List<CPos>
				{
					new(x, y), new(x + w - 1, y), new(x + w - 1, y + h - 1), new(x, y + h - 1),
					new(x + (w / 2), y), new(x + w - 1, y + (h / 2)),
					new(x + (w / 2), y + h - 1), new(x, y + (h / 2))
				};
				// Full ring boundary -- every cell LineBuild is legitimately allowed to fill in between this
				// ring's own nodes. Anything the executor later finds outside the union of ALL fences' rings
				// is a stray inter-ring bridge (LineBuild auto-connected to the nearest wall of ANY fence,
				// not just this one) and gets sold -- see AotBaseBuilderBotModule.PruneStrayFenceSegments.
				var perimeter = new List<CPos>();
				for (var dx = 0; dx < w; dx++)
				{
					perimeter.Add(new CPos(x + dx, y));
					perimeter.Add(new CPos(x + dx, y + h - 1));
				}

				for (var dy = 0; dy < h; dy++)
				{
					perimeter.Add(new CPos(x, y + dy));
					perimeter.Add(new CPos(x + w - 1, y + dy));
				}

				Rhythm.Add(new AotPlanStep
				{
					Kind = AotStepKind.Fence,
					Role = label,

					// Fence steps drive off FenceNodes, but TopLeft is still read as a fallback target (the
					// wall bridge when the node queue is empty) and by the block diagnostics. Leaving it at
					// the default made both aim at 0,0 -- confirmed 2026-07-24 in-game: "Waiting (target
					// blocked): PowerFence at 0,0" and "Diag Nuk2aFence@0,0 ... inPocket=False".
					TopLeft = new CPos(x, y),
					Variants = Info.WallTypes,
					FenceNodes = nodes.Distinct().ToList(),
					FencePerimeter = perimeter.Distinct().ToList(),
					Defense = defense,
					Age = currentAge
				});
			}

			void AddFenceFor(Placement p, string label, bool defense = false)
			{
				if (p == null)
					return;

				// PlaceGateCluster's own worst-case fallback (user spec 2026-08-01): a cluster that
				// could not find ANY position keeping the choke corridor passable together with a
				// fence gets built without one -- an open passage matters more than a closed ring.
				if (p.SkipFence)
				{
					Log.Write("debug", $"[AotBuild][{player.PlayerName}] {label}: skipped (cluster has no fence, corridor stayed the priority)");
					return;
				}

				AddFence(label, p.Pos.X, p.Pos.Y, p.V.W, p.V.H, defense);
			}

			// Gate defence steps place directly at the validated chokepoint's own coordinates -- these are
			// instance fields (defenceChokepoint/-Axis/-PathDir), not Plan()-local packer state, so no
			// Placement/parameter is needed for them the way the packer-driven bulks above are.
			void AddAt(string role, CPos pos, bool defense = false)
			{
				var variants = RoleVariants(role);
				if (variants.Length == 0)
					return;

				Rhythm.Add(new AotPlanStep { Kind = AotStepKind.Building, Role = role, Variants = variants, TopLeft = pos, Defense = defense, Age = currentAge });
			}

			currentAge = 0;

			// AGE 0 — mixed power cluster (2×NUK2 + 2×NUKE, columns): the first NUKE comes first (NUK2
			// requires an existing nuke), the second NUKE closes the cluster. Yard fenced right after the
			// silo. STEC (the tech pair's second building) is deliberately moved to the very end of Age 0
			// (user spec) -- DOME still comes early with the power cluster.
			AddBuildingByRole(nuke, "NUKE", 0);
			AddBuilding(silo, 0, "SILO");
			AddFence("YardFence", yard.X - 1, yard.Y - 1, 5, 5);
			AddBuilding(prod, 0, "LITE");
			AddBuilding(prod, 1, "HAND");
			AddBuilding(tech, 0, "DOME");
			AddBuildingByRole(nuke, "NUK2", 0);
			AddBuildingByRole(nuke, "NUK2", 1);
			AddBuildingByRole(nuke, "NUKE", 1);
			AddFenceFor(nuke, "PowerFence");



			// Flame Turret first among the post-upgrade steps (user spec 2026-08-06): the order after the
			// Age upgrade is wave 1 -> FTUR -> Repair Facility -> wave 2 -> Helipad -> SAMs -> wave 3.
			// Gate defence master plan (user spec, conceptualised fully now even though most of it only
			// becomes buildable many ages later -- same as every other age-gated step in this Rhythm):
			//   Age 0: the cluster's Flame Turret (middle of the row/column). The cluster itself --
			//          Turret-FlameTurret-Turret in one row or column, fenced as a single ring, exactly
			//          like the power cluster -- is already fully planned NOW (gateCluster, computed in
			//          Plan()), only the middle building is queued this age.
			//   Age 1: the 2 Turrets flanking the already-standing Flame Turret, THEN the fence ring
			//          around all 3.
			//   Age 3: 1x Obelisk of Light directly behind the cluster (toward the yard, away from the
			//          chokepoint -- an Obelisk facing the wrong way is a wasted superweapon) + the same
			//          3-building fenced cluster at every other classified approach.
			AddBuilding(gateCluster, 1, "FTUR", defense: true);

			// STEC comes before the Repair Facility, the Helipad and the SAMs (user spec 2026-08-05):
			// the Tech Centre is what unlocks the next Age, and every credit spent ahead of it delays
			// the upgrade the whole build-up is aimed at. Everything below is built during the Age
			// research window instead -- see the sprint hold in AotBaseBuilderBotModule.
			AddBuilding(tech, 1, "STEC");

			// Second SILO immediately behind the Tech Centre (user spec 2026-08-06), and then a hard stop:
			// everything below waits for the Age upgrade to be started. Storage is the one thing still
			// worth buying first -- without it the credits being saved have nowhere to sit.
			// Second SILO (user spec 2026-08-01): the LAST Age-0 core step, 2-3 cells behind the Age-3
			// Obelisk's own eventual spot (see the anchor computed in Plan(), right after pGateCluster).
			// Being last in Age 0's core list means two things fall out for free from the strict
			// first-open ordering every other step here already relies on: it isn't attempted until
			// Tech Centre (STEC) stands, and -- since AgeRhythmComplete(0) waits for ALL of Age 0's
			// Rhythm, not just STEC -- the Age-1 upgrade purchase doesn't fire until this Silo is done
			// either, so it genuinely finishes right as Age 1 begins rather than merely "around" then.
			AddBuilding(silo2, 0, "SILO");

			AddBuilding(fix, 0, "FIX");
			AddBuilding(hpad, 0, "HPAD");

			// SAM Sites, Age 0 (user spec 2026-07-31): 2x directly beside the construction yard,
			// outside its YardFence -- see the anchored placement in Plan() for why (air cover for the
			// one building that can never be rebuilt if lost). Behind the Helipad, which is their
			// prerequisite anyway, and low priority besides: nothing is flying this early.
			AddBuilding(samYard1, 0, "SAM", defense: true);
			AddBuilding(samYard2, 0, "SAM", defense: true);

			currentAge = 1;

			// AGE 1 — the builder naturally waits at the first entry whose variants are not buildable yet.
			// Both PROC and AFLD are Age-1-only (age-gated via Prerequisites), so the strict rhythm blocks
			// here until the age upgrade finishes -- everything after them is genuinely age-gated, not just
			// ordered later in the list.
			//
			// REFINERY FIRST, AIRFIELD SECOND (user spec 2026-08-04). This used to be the other way round
			// and NOT by choice: aot-proc-nod's own Buildable.Prerequisites required afld, so a PROC-first
			// list would have deadlocked the strict rhythm (PROC waiting on a prerequisite that AFLD, stuck
			// behind it, never got a turn to satisfy). With the techtree rebuild of 2026-08-04 that
			// dependency is gone -- both now hang off the NOD Tiberium Secrets gatekeeper instead, which
			// the gatekeeper driver buys as the first thing in Age 1. The intended chain is therefore:
			//   NOD Tiberium Secrets -> Refinery -> Airfield -> SAMs -> Temple -> expansion -> upgrades
			// and the Refinery finally really is the top-priority Age-1 step the user asked for.
			AddBuilding(proc, 0, "PROC");
			AddBuilding(afld, 0, "AFLD");

			// SAM Sites, Age 1 (user spec 2026-07-31): queued IMMEDIATELY after the Refinery, ahead of
			// the Age-1 GUN turrets below and everything else in Age 1 -- 2x beside the tech pair, 1x
			// beside the production pair, 2x beside power cluster 1. Each was placed right next to its
			// named block in Plan() (PlaceAdjacent). Listed before the GUN turrets so the Defence
			// chooser (strict first-open within its OWN queue, same as core) reaches these first; the
			// Refinery itself is core and stays first on ITS queue regardless.
			AddBuilding(samTech1, 0, "SAM", defense: true);
			AddBuilding(samTech2, 0, "SAM", defense: true);
			AddBuilding(samProd1, 0, "SAM", defense: true);
			AddBuilding(samPower1a, 0, "SAM", defense: true);
			AddBuilding(samPower1b, 0, "SAM", defense: true);

			AddBuilding(gateCluster, 0, "GUN", defense: true);
			AddBuilding(gateCluster, 2, "GUN", defense: true);
			AddFenceFor(gateCluster, "GateFence", defense: true);

			for (var i = 0; i < 4; i++)
				AddBuilding(nuk2a, i, "NUK2");
			AddFenceFor(nuk2a, "Nuk2aFence");
			AddBuilding(tmpl, 0, "TMPL");
			AddBuilding(mslo, 0, "MSLO");

			currentAge = 2;

			// SAM Sites, Age 2 (user spec 2026-07-31): FIRST in Age 2, ahead of every other Age-2
			// building -- 2x behind the gate defence cluster (same side the Age-3 Obelisk will occupy),
			// 1x beside the Temple of Nod. Being listed first here is what it takes for the SAM/GUN/FTUR
			// Defence chooser to reach them right after Age 1's own defence steps finish; it does NOT
			// delay the second Refinery below in any way, since core and defence now run through two
			// fully independent choosers (see AotBaseBuilderBotModule.ChooseStep) -- "SAM first" and
			// "Refinery first" are both simultaneously true, one on each queue.
			AddBuilding(samDefence1, 0, "SAM", defense: true);
			AddBuilding(samDefence2, 0, "SAM", defense: true);
			AddBuilding(samTemple, 0, "SAM", defense: true);

			// Second Refinery FIRST among Age 2's CORE steps if the base still has room for one
			// (optional, user spec: Place() already just returns null/no-ops when nothing fits, so this
			// needs no separate space check). NO third power cluster (user spec: Age 0's mixed cluster +
			// Age 1's NUK2a quad are enough) -- the old Age-2 NUK2b quad + its fence are dropped
			// entirely. SHRN moved to almost the very end (user spec), right before the Stealth
			// Generators -- it's still directly ahead of them since SGEN's own Buildable.Prerequisites
			// requires aot-shrine to already exist.
			AddBuilding(proc2, 0, "PROC");

			AddBuilding(shrn, 0, "SHRN");
			AddBuilding(sgen1, 0, "SGEN");
			AddBuilding(sgen2, 0, "SGEN");

			currentAge = 3;

			// AGE 3 -- previously upgrade-only; the Obelisk + secondary-approach clusters are the new
			// base-rhythm additions here (user spec). Obelisk sits directly behind the gate cluster's
			// ACTUAL Flame Turret position (not the raw choke cell -- the cluster search may have shifted
			// it slightly to fit): Cardinal(yard - fturPos) gives the cardinal direction FROM the cluster
			// back TOWARDS the base, so the Obelisk faces out through the defended gate instead of pointing
			// the wrong way. Every OTHER classified approach into the base -- including Beach-type ones,
			// which previously got no fixed defence at all (only a mobile reserve, per the approach-type
			// doc comment) -- gets the SAME 3-building fenced cluster as the main gate, already fully
			// planned in Plan() (secondaryClusters), built here in one pass (no Age0->Age1 staging like the
			// main gate -- by Age 3 the match is already well underway).
			if (gateCluster != null)
			{
				var fturPos = gateCluster.Pos + gateCluster.V.Buildings[1].Off;
				var behind = Cardinal(new CVec(yard.X - fturPos.X, yard.Y - fturPos.Y));
				AddAt("OBELISK", fturPos + (behind * 4), defense: true);
			}

			// SAM Sites, Age 3 (user spec 2026-07-31): 1x beside the Shrine.
			AddBuilding(samShrine, 0, "SAM", defense: true);

			foreach (var cluster in secondaryClusters)
			{
				AddBuilding(cluster, 0, "GUN", defense: true);
				AddBuilding(cluster, 1, "FTUR", defense: true);
				AddBuilding(cluster, 2, "GUN", defense: true);
				AddFenceFor(cluster, $"SecondaryFence_{cluster.Pos.X}_{cluster.Pos.Y}", defense: true);
			}
		}

		// ------------------------------------------------------------ VALIDATED defence detection (ported)

		static int Manhattan(CPos a, CPos b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

		public static CVec Cardinal(CVec v)
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
					defenceChokeAxis = perp;
					defenceChokePathDir = dir;
					defenceChokeAxisPlusDist = left;
					defenceChokeAxisMinusDist = right;
				}
			}

			if (choke.HasValue)
			{
				Log.Write("debug", $"[AotPlan][{player.PlayerName}] Choke(defence)=two-sided {choke.Value} width={bestWidth} axis={defenceChokeAxis} " +
					$"plusDist={defenceChokeAxisPlusDist} minusDist={defenceChokeAxisMinusDist}");
				return choke.Value;
			}

			var front1 = path.FirstOrDefault(c => (c - yard).Length >= Info.BaseFrontExtent);
			var frontLine = front1 != default ? front1 : FallbackFrontLine(enemies);
			Log.Write("debug", $"[AotPlan][{player.PlayerName}] Choke(defence)=frontline {frontLine} (open terrain, no two-sided neck)");
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

		static readonly CVec[] Orthogonal = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

		// Orthogonal-only Water check: a diagonal-only Water sighting means the direct approach is
		// blocked by a corner (usually Rock) -- that's a water-CLIFF, not a landable beach, and must
		// not be classified the same way (confirmed empirically: on real water-cliffs Rock always sits
		// directly, orthogonally, between land and Water; a Clear cell only ever "sees" Water diagonally
		// there by peeking past the Rock corner, never legitimately).
		bool Beachy(CPos c)
		{
			if (world.Map.GetTerrainInfo(c).Type == "Beach")
				return true;

			foreach (var d in Orthogonal)
				if (world.Map.GetTerrainInfo(c + d).Type == "Water")
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
			Log.Write("debug", "[AotPlan][{player.PlayerName}] Approaches(defence): " + string.Join(", ", approaches.Select(a => $"{a.Type}@{a.Gate}")));
		}
	}
}
