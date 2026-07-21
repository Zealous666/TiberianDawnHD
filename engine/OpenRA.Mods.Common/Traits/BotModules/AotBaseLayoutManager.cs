#region Copyright & License Information
/*
 * Age of Tiberium Mod (aotmod) — AotBaseLayoutManager (base "masterplan")
 * Plans a base layout that clusters buildings into separated functional bulks
 * (power / production / tech) and places the repair facility toward the base exit.
 * BaseBuilderBotModule's placement (ChooseBuildLocation) consults this to route
 * each building to its bulk anchor instead of the base centre.
 *
 * v1 scope: survey (con yard = centre, front = toward map centre as an enemy heuristic)
 * + anchor-based clustering. Strict gap reservation, con-yard ring, south gate and
 * age-2 fencing are later increments. See memory/ai-base-masterplan.md.
 */
#endregion

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Age of Tiberium: plans a base masterplan that clusters buildings into separated",
		"functional bulks (power / production / tech) around distinct anchors and places the",
		"repair facility toward the base exit. Consulted by BaseBuilderBotModule placement.")]
	public class AotBaseLayoutManagerInfo : ConditionalTraitInfo
	{
		[ActorReference]
		[Desc("Construction yard actor types, used to locate the base centre.")]
		public readonly FrozenSet<string> ConstructionYardTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Power plant actor types are clustered into the power bulk.")]
		public readonly FrozenSet<string> PowerBulkTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Production actor types (factories, barracks) are clustered into the production bulk.",
			"ORDER MATTERS: the layout packs them in this order, so list the primary/first-built buildings",
			"first (e.g. Light Factory + Barracks) — they land in the first row as a flush pair.")]
		public readonly string[] ProductionBulkTypes = [];

		[ActorReference]
		[Desc("Tech actor types (tech centre, temple, radar) are clustered into the tech bulk.",
			"ORDER MATTERS (see ProductionBulkTypes): list Radar + Tech Centre first.")]
		public readonly string[] TechBulkTypes = [];

		[ActorReference]
		[Desc("Repair facility actor types are placed toward the base exit (front).")]
		public readonly FrozenSet<string> FixTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Stealth generator actor types. Two are reserved, spread evenly (k=2) over the built-up",
			"base so their cloak radius covers it — not clustered at the chokepoint.")]
		public readonly FrozenSet<string> StealthTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Silo actor types. ONE silo slot is planned within yard reach toward the nearest ore mine;",
			"it doubles as the ZONE BRIDGE for the production block (like the NUKE bridges the power block).")]
		public readonly FrozenSet<string> SiloTypes = FrozenSet<string>.Empty;

		[ActorReference]
		[Desc("Ore mine actor types the silo slot is aimed at.")]
		public readonly FrozenSet<string> OreMineTypes = FrozenSet<string>.Empty;

		[Desc("Cell distance the production bulk anchor sits in front of the con yard (toward the enemy).")]
		public readonly int ProductionForwardOffset = 8;

		[Desc("Cell distance the repair facility anchor sits in front of the con yard (toward the exit).")]
		public readonly int FixForwardOffset = 11;

		[Desc("Cell distance the tech bulk anchor sits behind the con yard.")]
		public readonly int TechRearOffset = 8;

		[Desc("Cell distance the power bulk anchor sits behind the con yard.")]
		public readonly int PowerRearOffset = 8;

		[Desc("Sideways cell offset separating the two rear bulks (tech vs power).")]
		public readonly int RearSideOffset = 7;

		[Desc("Row height (cells) used to pack a bulk into flush, aligned rows.")]
		public readonly int BulkRowHeight = 3;

		[Desc("Maximum width (cells) of a bulk row before wrapping to the next row.")]
		public readonly int BulkMaxWidth = 12;

		[Desc("Maximum number of rows per bulk.")]
		public readonly int BulkMaxRows = 5;

		[Desc("Keep this many clear cells between the power bulk and the construction yard, so both",
			"the con yard's fence ring and the power block's own fence have room.")]
		public readonly int ConYardFenceGap = 2;

		[Desc("Ground locomotor name used to judge where ground units are funneled (chokepoints).")]
		public readonly string GroundLocomotor = "tracked";

		[Desc("Naval locomotor name used to judge where ships/transports can travel (naval approaches).")]
		public readonly string NavalLocomotor = "naval";

		[Desc("Extra enemy effort (in path cells) to cross a destroyed bridge, i.e. to rebuild it.",
			"Higher = the AI treats bridge crossings as a less urgent threat than direct land routes.")]
		public readonly int BridgeRebuildPenalty = 25;

		[Desc("Extra enemy effort (in path cells) to mount a naval landing (build a shipyard + transports",
			"and cross water). Higher = the AI treats beach landings as the least urgent threat.")]
		public readonly int NavalLandingPenalty = 50;

		[Desc("Radius (cells) around the con yard searched for the base's open area and its chokepoints.")]
		public readonly int ChokeSearchRadius = 40;

		[Desc("A cell belongs to the base's open working area only if its clearance (distance to the",
			"nearest wall) is at least this many cells. Narrower passable cells are treated as necks",
			"(the chokepoints that connect the base area to the outside).")]
		public readonly int ChokeClearanceThreshold = 3;

		[Desc("A point on the enemy approach only counts as a chokepoint if an impassable wall is found",
			"within this many cells on BOTH sides perpendicular to the path (a genuine two-sided squeeze,",
			"not just the path hugging a single wall or the map edge). Corridors wider than 2x this are open.")]
		public readonly int ChokeMaxCorridor = 8;

		[Desc("Number of power plants the base plans for; sizes the reserved power bulk.")]
		public readonly int PowerPlantCount = 6;

		[Desc("Reserved clear cells kept around bulks for stealth generators and fence lanes.")]
		public readonly int StealthGapCells = 2;

		[Desc("Extra clear cells kept between the base's built-up front (production + fix + fence) and",
			"the chosen chokepoint, so defences and a wall bridge still have room in front of the base.")]
		public readonly int ChokeBaseBuffer = 4;

		public override object Create(ActorInitializer init) { return new AotBaseLayoutManager(init, this); }
	}

	public class AotBaseLayoutManager : ConditionalTrait<AotBaseLayoutManagerInfo>, IBotChokepointProvider, IBotBaseApproachProvider
	{
		readonly World world;
		readonly Player player;

		// Every scored way an enemy can reach the base (land / bridge / destroyed bridge / beach),
		// classified once by DetectApproaches. Highest score first.
		readonly List<BaseApproach> approaches = [];

		bool planned;
		CPos baseCentre;
		CPos conYardMin, conYardMax;
		CPos powerAnchor, productionAnchor, techAnchor, fixAnchor;

		// Cells the base's built-up front reaches ahead of the con yard (toward the map centre),
		// derived from the planned bulk footprints. The chokepoint must sit beyond this.
		int baseFrontExtent;

		IResourceLayer resourceLayer;

		CPos? chokepoint;

		// One reserved building slot in a functional bulk: its footprint and top-left cell, plus whether
		// a building has already been routed to it. Filled once by the grow-block planner (PlanBulks).
		sealed class Slot
		{
			public CVec Dim;
			public CPos TopLeft;
			public bool Used;
		}

		// The masterplan's reserved slots per bulk ("power"/"prod"/"tech"), grown as contiguous fenceable
		// blocks. TryGetBulkGridSlot serves the next free slot whose footprint matches the requested actor.
		readonly Dictionary<string, List<Slot>> bulkSlots = [];

		// Every cell covered by a reserved bulk footprint. Non-bulk buildings (refinery, silo, defense,
		// superweapons, …) must NOT be placed onto these — only in the gaps BETWEEN the blocks — so the
		// masterplan's power/tech/prod/stealth blocks stay intact and fenceable.
		readonly HashSet<CPos> reservedCells = [];

		// Buildable-area sources for reach verification during planning (yard + silo bridge).
		readonly HashSet<CPos> seeds = [];

		CPos? IBotChokepointProvider.Chokepoint
		{
			get
			{
				EnsurePlanned();
				return chokepoint;
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

		public AotBaseLayoutManager(ActorInitializer init, AotBaseLayoutManagerInfo info)
			: base(info)
		{
			world = init.World;
			player = init.Self.Owner;
		}

		void EnsurePlanned()
		{
			if (planned)
				return;

			var conyard = world.ActorsHavingTrait<Building>()
				.FirstOrDefault(a => a.Owner == player && !a.IsDead && Info.ConstructionYardTypes.Contains(a.Info.Name));

			if (conyard == null)
				return;

			baseCentre = conyard.Location;
			var cbi = conyard.Info.TraitInfoOrDefault<BuildingInfo>();
			var cdim = cbi?.Dimensions ?? new CVec(3, 3);
			conYardMin = conyard.Location;
			conYardMax = conyard.Location + new CVec(cdim.X - 1, cdim.Y - 1);
			var baseW = world.Map.CenterOfCell(baseCentre);

			var b = world.Map.Bounds;
			var mapCentre = new CPos(b.Left + (b.Width / 2), b.Top + (b.Height / 2));
			var frontW = world.Map.CenterOfCell(mapCentre) - baseW;

			var loco = world.WorldActor.TraitsImplementing<Locomotor>()
				.FirstOrDefault(l => l.Info.Name == Info.GroundLocomotor);

			bool Passable(CPos c) => world.Map.Contains(c) && loco != null
				&& loco.MovementCostForCell(c) != PathGraph.MovementCostForUnreachableCell;

			// 1. Measure the buildable area around the con yard, 2. classify every enemy approach.
			HashSet<CPos> buildArea = null;
			Dictionary<CPos, int> clearance = null;
			if (loco != null)
			{
				var startCell = NearestPassable(baseCentre, Passable, 8) ?? baseCentre;
				clearance = BuildClearanceMap(startCell, Info.ChokeSearchRadius, Passable);
				buildArea = FloodOpenArea(startCell, Info.ChokeSearchRadius, Info.ChokeClearanceThreshold, clearance, Passable);
				DetectApproaches(Passable, clearance);
			}

			// Orientation chokepoint = the two-sided terrain neck guarding the base on the cheapest enemy
			// path (DetectChokepoint). This is the "clean" chokepoint the base faces. The DetectApproaches
			// list is a SEPARATE concern — it drives troop deployment (beaches get troops only). A false
			// one-sided gate next to a lone tiberium blob must NOT become the chokepoint, hence the two-sided
			// squeeze test. Island/open bases with no neck fall back to the best-scored approach (may be a
			// beach — allowed as a last resort).
			baseFrontExtent = Info.FixForwardOffset + Info.ChokeBaseBuffer;   // provisional; PlanBulks refines
			var choke = loco != null ? DetectChokepoint(frontW, buildArea, clearance, Passable) : null;
			if (choke == null && approaches.Count > 0)
				choke = approaches[0].Gate;

			var front = choke.HasValue ? CardinalToward(choke.Value - baseCentre) : CardinalToward(mapCentre - baseCentre);
			var side = new CVec(-front.Y, front.X);

			// 3. Fit the bulks into the measured area; fall back to fixed offsets if we could not measure.
			if (buildArea != null && buildArea.Count > 0)
			{
				PlanBulks(front, side, buildArea);
			}
			else
			{
				var fw = frontW.HorizontalLength > 0 ? (frontW * 1024) / frontW.Length : new WVec(0, 1024, 0);
				var sw = new WVec(-fw.Y, fw.X, 0);
				CPos Anchor(WVec fo, WVec so) => world.Map.CellContaining(baseW + fo + so);
				productionAnchor = Anchor(Info.ProductionForwardOffset * fw, WVec.Zero);
				fixAnchor = Anchor(Info.FixForwardOffset * fw, WVec.Zero);
				techAnchor = Anchor(-Info.TechRearOffset * fw, Info.RearSideOffset * sw);
				powerAnchor = Anchor(-Info.PowerRearOffset * fw, -Info.RearSideOffset * sw);
				baseFrontExtent = Info.FixForwardOffset + Info.ChokeBaseBuffer;
			}

			// The bulks stay axis-aligned (fenceable rectangles), so their front snaps to a cardinal. But the
			// repair bay + forward defence should sit on the EXACT base->choke line, otherwise a diagonal
			// choke leaves them offset to one side ("am Rand"). Re-aim fixAnchor along that true line.
			if (choke.HasValue && baseFrontExtent > 0)
			{
				var bc = world.Map.CenterOfCell(baseCentre);
				var v = world.Map.CenterOfCell(choke.Value) - bc;
				if (v.HorizontalLength > 0)
				{
					var unit = (v * 1024) / v.Length;
					var reach = Math.Min(baseFrontExtent + 2, v.Length / 1024);
					fixAnchor = world.Map.CellContaining(bc + (unit * reach));
				}
			}

			chokepoint = choke;

			// Steer the bot's defensive focus to the chokepoint (defenses, protection) — also makes
			// the detected chokepoint visible in-game for verification.
			if (chokepoint.HasValue)
				foreach (var t in player.PlayerActor.TraitsImplementing<IBotPositionsUpdated>())
					t.UpdatedDefenseCenter(chokepoint.Value);

			planned = true;

			Log.Write("debug", $"[AotLayout] PLANNED player={player.InternalName} base={baseCentre} front={front} " +
				$"power={powerAnchor} prod={productionAnchor} tech={techAnchor} fix={fixAnchor} " +
				$"choke={(chokepoint.HasValue ? chokepoint.ToString() : "none")}");
		}

		static readonly CVec[] Dirs4 = [new(0, -1), new(-1, 0), new(1, 0), new(0, 1)];
		static readonly CVec[] Dirs8 =
		[
			new(-1, -1), new(0, -1), new(1, -1),
			new(-1, 0), new(1, 0),
			new(-1, 1), new(0, 1), new(1, 1),
		];

		static CVec CardinalToward(CVec v)
		{
			if (v.X == 0 && v.Y == 0)
				return new CVec(0, 1);

			return Math.Abs(v.X) >= Math.Abs(v.Y) ? new CVec(Math.Sign(v.X), 0) : new CVec(0, Math.Sign(v.Y));
		}

		// Collapse an actor id to the functional building it represents, so age variants and the two
		// faction variants of the same slot count once (aot-nuke-age2-nod -> aot-nuke).
		static string BulkKey(string type)
		{
			var k = type;
			var i = k.IndexOf("-age", StringComparison.Ordinal);
			if (i >= 0)
			{
				var rest = k[(i + 4)..];
				var j = 0;
				while (j < rest.Length && char.IsDigit(rest[j]))
					j++;
				k = k[..i] + rest[j..];
			}

			if (k.EndsWith("-nod", StringComparison.Ordinal) || k.EndsWith("-gdi", StringComparison.Ordinal))
				k = k[..k.LastIndexOf('-')];

			return k;
		}

		BuildingInfo BuildingOf(string type) =>
			world.Map.Rules.Actors.TryGetValue(type, out var ai) ? ai.TraitInfoOrDefault<BuildingInfo>() : null;

		// Plan the base as CONTIGUOUS, fenceable blocks — one per functional bulk — grown building by
		// building inside the measured (non-leaking) area. Power must start adjacent to the con yard
		// (its first plant builds within the build radius); production faces the front, tech sits rear.
		// Each building's exact slot is reserved and later served through TryGetBulkGridSlot.
		void PlanBulks(CVec front, CVec side, HashSet<CPos> area)
		{
			bulkSlots.Clear();

			// The con yard plus its fence ring (Chebyshev 1) is hard-blocked — but WITHOUT a separation
			// margin: blocks sit directly beyond the ring, at Chebyshev 2 from the yard footprint, which is
			// exactly the reach of RequiresBuildableArea.Adjacent = 2 (cell-to-cell Chebyshev, verified in
			// Building.IsCloseEnoughToBase). Any extra margin here pushes every block out of reach and the
			// base can never bootstrap (proven via NoSlot logs). Between BLOCKS the 1-cell fence lane is
			// kept via `sep` as before.
			var hard = new HashSet<CPos>();
			for (var x = conYardMin.X - 1; x <= conYardMax.X + 1; x++)
				for (var y = conYardMin.Y - 1; y <= conYardMax.Y + 1; y++)
					hard.Add(new CPos(x, y));

			var occ = new HashSet<CPos>();

			// Zone seeds: cells that provide buildable area for a block's FIRST building (verified reach,
			// Chebyshev <= 2). Initially the yard; the silo (planned next) joins as the prod-block bridge.
			seeds.Clear();
			for (var x = conYardMin.X; x <= conYardMax.X; x++)
				for (var y = conYardMin.Y; y <= conYardMax.Y; y++)
					seeds.Add(new CPos(x, y));

			// SILO first: one slot inside yard reach, as close to the nearest ore mine as possible. It is
			// the ORET dock AND the zone bridge for the production block (user concept: like the NUKE
			// bridges the power block). Its cells become seeds; prod prefers the silo's direction.
			var siloDirs = new[] { front, side, -side, -front };
			var siloSlot = PlanSiloSlot(area, hard, occ);
			if (siloSlot != null)
			{
				bulkSlots["silo"] = [siloSlot];
				foreach (var c in FootprintCells(siloSlot.TopLeft, siloSlot.Dim))
				{
					hard.Add(c);   // blocks may chain directly beside the silo (no fence lane needed)
					seeds.Add(c);
				}

				var sc = siloSlot.TopLeft + new CVec(siloSlot.Dim.X / 2, siloSlot.Dim.Y / 2);
				var toSilo = CardinalToward(sc - baseCentre);
				siloDirs = new[] { toSilo, front, side, -side, -front };
			}

			var powerDims = PowerBlockDims();
			var prodDims = BulkBuildingDims(Info.ProductionBulkTypes);
			var techDims = BulkBuildingDims(Info.TechBulkTypes);

			// Every block cascades through candidate directions and only settles where the WHOLE block
			// fits AND its first building is verified to be inside seed reach; its yard-facing row is built
			// FIRST (row order flips with the direction), chaining outward row by row. The power block's
			// first slot is the small NUKE — the "bridge building" (sold again later per the build plan).
			// POWER (user spec): the bridge NUKE sits alone at the yard edge; the NUK2 GROUP is planned
			// SEPARATED from it by a lane (sep 1 -> chain distance exactly 2, still buildable) so the
			// group can be fenced autonomously once the bridge is sold — it must not compete with the
			// yard fence. Internal spacing 1 keeps each plant fenceable with room for stealth between.
			var powerSlots = new List<Slot>();
			var bridgeDim = powerDims.Count > 0 ? powerDims.OrderBy(d => d.X * d.Y).First() : default;
			var groupDims = powerDims.OrderByDescending(d => d.X * d.Y).Take(Math.Max(0, powerDims.Count - 1)).ToList();
			foreach (var dir in new[] { -front, side, -side, front })
			{
				var bridge = PlanEdgeSlot(dir, bridgeDim, area, hard, occ);
				if (bridge == null)
					continue;

				var bridgeCells = FootprintCells(bridge.TopLeft, bridge.Dim).ToList();
				foreach (var c in bridgeCells) { occ.Add(c); seeds.Add(c); }

				var group = GrowBlock(dir, groupDims, area, hard, occ, 1, 1);
				if (group.Count == groupDims.Count)
				{
					powerSlots.Add(bridge);
					powerSlots.AddRange(group);
					break;
				}

				foreach (var c in bridgeCells) { occ.Remove(c); seeds.Remove(c); }
			}

			StoreSlots("power", powerSlots, occ);

			// PRODUCTION chains off the silo bridge (its direction first), else front/sides/rear.
			StoreSlots("prod", GrowCascade(siloDirs, prodDims, area, hard, occ, 1), occ);
			StoreSlots("tech", GrowCascade(new[] { -front, side, -side, front }, techDims, area, hard, occ, 1), occ);

			// The blocks WILL be built (deterministic plan) — their cells count as buildable-area seeds for
			// everything planned after them (stealth k=2 spread needs this or its reach check always fails).
			foreach (var slots in bulkSlots.Values)
				foreach (var s in slots)
					foreach (var c in FootprintCells(s.TopLeft, s.Dim))
						seeds.Add(c);

			// Stealth generators: two, spread by a k=2 split of the built-up cells along the base's longer
			// axis so their cloak covers the whole base rather than clustering at one end.
			if (Info.StealthTypes.Count > 0)
			{
				var stealthDim = BuildingOf(Info.StealthTypes.First())?.Dimensions ?? new CVec(3, 3);
				var built = bulkSlots.Values.SelectMany(l => l).SelectMany(s => FootprintCells(s.TopLeft, s.Dim)).ToList();
				if (built.Count > 0)
				{
					var wide = (built.Max(c => c.X) - built.Min(c => c.X)) >= (built.Max(c => c.Y) - built.Min(c => c.Y));
					var mid = wide ? (built.Min(c => c.X) + built.Max(c => c.X)) / 2 : (built.Min(c => c.Y) + built.Max(c => c.Y)) / 2;
					var stealth = new List<Slot>();
					foreach (var lowHalf in new[] { true, false })
					{
						var group = built.Where(c => ((wide ? c.X : c.Y) <= mid) == lowHalf).ToList();
						if (group.Count == 0)
							continue;

						var centroid = new CPos((int)group.Average(c => c.X), (int)group.Average(c => c.Y));
						var placed = GrowFlexible(centroid, [stealthDim], area, hard, occ, 1);
						if (placed.Count > 0)
						{
							stealth.Add(placed[0]);
							foreach (var c in FootprintCells(placed[0].TopLeft, placed[0].Dim))
								occ.Add(c);
						}
					}

					bulkSlots["stealth"] = stealth;
				}
			}

			// Freeze the reserved footprints (plus a one-cell margin so a fence lane stays free around each
			// block): from now on non-bulk buildings are barred from these cells and only fill the gaps.
			reservedCells.Clear();
			foreach (var slots in bulkSlots.Values)
				foreach (var s in slots)
					foreach (var c in FootprintCells(s.TopLeft, s.Dim))
						for (var dx = -1; dx <= 1; dx++)
							for (var dy = -1; dy <= 1; dy++)
								reservedCells.Add(c + new CVec(dx, dy));

			// Also reserve the con yard plus its 1-cell fence ring (the yard fence goes directly around the
			// yard at Chebyshev 1). Blocks start at Chebyshev 2 — exactly within the buildable-area reach
			// (RequiresBuildableArea.Adjacent = 2, cell-to-cell Chebyshev), so the base can bootstrap from
			// the yard alone. A wider reservation makes EVERYTHING unreachable (proven via NoSlot logs).
			for (var x = conYardMin.X - 1; x <= conYardMax.X + 1; x++)
				for (var y = conYardMin.Y - 1; y <= conYardMax.Y + 1; y++)
					reservedCells.Add(new CPos(x, y));

			powerAnchor = BulkCentre("power");
			productionAnchor = BulkCentre("prod");
			techAnchor = BulkCentre("tech");

			// FIX sits just ahead of the production block toward the exit; the chokepoint must clear it.
			var prodFrontExtent = bulkSlots.TryGetValue("prod", out var ps) && ps.Count > 0
				? ps.Max(s => ((s.TopLeft + new CVec(s.Dim.X / 2, s.Dim.Y / 2)) - baseCentre).Length)
				: Info.ProductionForwardOffset;
			fixAnchor = baseCentre + (front * (prodFrontExtent + 4));
			baseFrontExtent = prodFrontExtent + Info.ChokeBaseBuffer + 2;

			foreach (var kv in bulkSlots)
				Log.Write("debug", $"[AotLayout] Slots {kv.Key}: " + string.Join(" ", kv.Value.Select(s => $"{s.TopLeft}={s.Dim.X}x{s.Dim.Y}")));

			Log.Write("debug", $"[AotLayout] Bulks(grow): power={PlacedCount("power")}/{powerDims.Count} " +
				$"prod={PlacedCount("prod")}/{prodDims.Count} tech={PlacedCount("tech")}/{techDims.Count} " +
				$"frontExtent={baseFrontExtent} area={area.Count}");
		}

		// Chebyshev ring of offsets at radius r (r == 0 yields just the centre).
		static IEnumerable<CVec> Ring(int r)
		{
			if (r == 0) { yield return CVec.Zero; yield break; }
			for (var ox = -r; ox <= r; ox++)
				for (var oy = -r; oy <= r; oy++)
					if (Math.Max(Math.Abs(ox), Math.Abs(oy)) == r)
						yield return new CVec(ox, oy);
		}

		static IEnumerable<CPos> FootprintCells(CPos topLeft, CVec dim)
		{
			for (var i = 0; i < dim.X; i++)
				for (var j = 0; j < dim.Y; j++)
					yield return topLeft + new CVec(i, j);
		}

		// A single building slot at the yard edge (Chebyshev 2, one cell beyond the fence ring) in the
		// given direction, laterally centred with deterministic slides. Used for the bridge NUKE.
		Slot PlanEdgeSlot(CVec dir, CVec dim, HashSet<CPos> area, HashSet<CPos> hard, HashSet<CPos> occ)
		{
			if (dim == default)
				return null;

			var yardMidX = (conYardMin.X + conYardMax.X) / 2;
			var yardMidY = (conYardMin.Y + conYardMax.Y) / 2;

			CPos TopLeft(int lateral)
			{
				if (dir.Y < 0)
					return new CPos(yardMidX - (dim.X / 2) + lateral, conYardMin.Y - 2 - (dim.Y - 1));
				if (dir.Y > 0)
					return new CPos(yardMidX - (dim.X / 2) + lateral, conYardMax.Y + 2);
				if (dir.X < 0)
					return new CPos(conYardMin.X - 2 - (dim.X - 1), yardMidY - (dim.Y / 2) + lateral);
				return new CPos(conYardMax.X + 2, yardMidY - (dim.Y / 2) + lateral);
			}

			for (var l = 0; l <= 4; l++)
				foreach (var lateral in l == 0 ? new[] { 0 } : new[] { l, -l })
				{
					var tl = TopLeft(lateral);
					var cells = FootprintCells(tl, dim).ToList();
					if (cells.Any(c => !area.Contains(c) || hard.Contains(c) || occ.Contains(c)))
						continue;

					if (!cells.Any(c => seeds.Any(s => Math.Abs(s.X - c.X) <= 2 && Math.Abs(s.Y - c.Y) <= 2)))
						continue;

					return new Slot { Dim = dim, TopLeft = tl };
				}

			return null;
		}

		// One silo slot inside yard reach (Chebyshev <= 2 of a yard cell), as close to the nearest ore
		// mine as possible — the ORET dock and the production block's zone bridge.
		Slot PlanSiloSlot(HashSet<CPos> area, HashSet<CPos> hard, HashSet<CPos> occ)
		{
			var siloType = Info.SiloTypes.FirstOrDefault(BuildableByFaction);
			var dim = siloType != null ? BuildingOf(siloType)?.Dimensions : null;
			if (dim == null)
				return null;

			var mine = world.Actors
				.Where(a => !a.IsDead && a.IsInWorld && Info.OreMineTypes.Contains(a.Info.Name))
				.OrderBy(a => (a.Location - baseCentre).LengthSquared)
				.FirstOrDefault();
			var target = mine?.Location ?? (baseCentre + new CVec(0, 4));

			Slot best = null;
			var bestDist = long.MaxValue;
			for (var dx = -5; dx <= 5; dx++)
				for (var dy = -5; dy <= 5; dy++)
				{
					var tl = new CPos(conYardMin.X + dx, conYardMin.Y + dy);
					var cells = FootprintCells(tl, dim.Value).ToList();
					if (cells.Any(c => !area.Contains(c) || hard.Contains(c) || occ.Contains(c)))
						continue;

					if (!cells.Any(c => seeds.Any(s => Math.Abs(s.X - c.X) <= 2 && Math.Abs(s.Y - c.Y) <= 2)))
						continue;

					var d = cells.Min(c => (long)(c - target).LengthSquared);
					if (d < bestDist)
					{
						bestDist = d;
						best = new Slot { Dim = dim.Value, TopLeft = tl };
					}
				}

			return best;
		}

		// Try each candidate direction in order; settle on the FIRST where the whole block fits. If none
		// fits fully (base jammed against an edge), keep the direction that placed the most buildings.
		// `hard` = cells that are forbidden but carry NO separation margin (the yard + its fence ring);
		// `occ` = other bulks' cells, kept at `sep` distance (their fence lane).
		List<Slot> GrowCascade(CVec[] dirs, List<CVec> dims, HashSet<CPos> area, HashSet<CPos> hard, HashSet<CPos> occ, int sep, int spacing = 0)
		{
			// Pass 1: RIGID row layout in each direction. Pass 2 (only if no direction fits rigidly):
			// flexible grow per direction. The flexible fallback must NOT run inside pass 1 — it always
			// "succeeds", so the cascade would accept an unreachable jumble in the first direction and
			// never try the free ones (found: tech block dumped in the NW corner while east was open).
			foreach (var dir in dirs)
			{
				var s = GrowBlock(dir, dims, area, hard, occ, sep, spacing);
				if (s.Count == dims.Count)
					return s;
			}

			List<Slot> best = null;
			foreach (var dir in dirs)
			{
				var along = dir.X != 0 ? 4 : 4;
				var f = GrowFlexible(baseCentre + (dir * (2 + along)), dims, area, hard, occ, sep);
				if (f.Count == dims.Count)
					return f;

				if (best == null || f.Count > best.Count)
					best = f;
			}

			return best ?? [];
		}

		// Lay the block out as aligned, flush ROWS: every building in a row shares its bottom edge, rows
		// are left-aligned, buildings keep their given order — a clean rectangle that is trivial to fence.
		// The block is placed snug beyond the yard's fence ring in direction `dir`, and its rows are
		// ORIENTED so the FIRST buildings sit on the yard-facing side: they are buildable from the yard's
		// area (Adjacent = 2) immediately, and each later row chains off the previous one. Without this
		// flip the first building lands on the far side, out of reach, and nothing can ever be placed.
		List<Slot> GrowBlock(CVec dir, List<CVec> dims, HashSet<CPos> area, HashSet<CPos> hard, HashSet<CPos> occ, int sep, int spacing = 0)
		{
			if (dims.Count == 0)
				return [];

			// Group buildings into rows, wrapping when the running width would exceed a ~square cap.
			// `spacing` inserts internal lanes BETWEEN the buildings (power block: 1 — each plant stays
			// individually fenceable and leaves room to walk/place stealth; a 1-cell gap still keeps the
			// build chain intact because Adjacent = 2 reaches across it).
			var totalArea = dims.Sum(d => d.X * d.Y);
			var cap = Math.Max(dims.Max(d => d.X), (int)Math.Ceiling(Math.Sqrt(totalArea))) + spacing;
			var rows = new List<List<CVec>>();
			var row = new List<CVec>();
			var rowW = 0;
			foreach (var d in dims)
			{
				if (rowW > 0 && rowW + d.X + spacing > cap) { rows.Add(row); row = []; rowW = 0; }
				row.Add(d);
				rowW += d.X + (rowW > 0 || spacing == 0 ? spacing : 0);
			}

			if (row.Count > 0)
				rows.Add(row);

			var rel = new List<(CVec Off, CVec Dim)>();
			int y = 0, blockW = 0;
			foreach (var r in rows)
			{
				var rowH = r.Max(d => d.Y);
				var x = 0;
				foreach (var d in r)
				{
					rel.Add((new CVec(x, y + rowH - d.Y), d));   // bottom-aligned within the row
					x += d.X + spacing;
				}

				blockW = Math.Max(blockW, x - spacing);
				y += rowH + spacing;
			}

			var blockH = y - spacing;

			// Orient toward the yard: rows stack top-down with building #1 in the top-left. When the block
			// sits NORTH of the yard (dir.Y < 0) the yard-facing side is the BOTTOM row -> flip vertically.
			// When it sits WEST (dir.X < 0) the yard-facing side is the RIGHT edge -> flip horizontally.
			if (dir.Y < 0 || dir.X < 0)
				rel = rel.Select(e => (
					new CVec(
						dir.X < 0 ? blockW - e.Off.X - e.Dim.X : e.Off.X,
						dir.Y < 0 ? blockH - e.Off.Y - e.Dim.Y : e.Off.Y),
					e.Dim)).ToList();

			bool RigidFits(CPos tl)
			{
				var own = new HashSet<CPos>();
				foreach (var (off, dim) in rel)
					foreach (var c in FootprintCells(tl + off, dim))
						if (!area.Contains(c) || hard.Contains(c) || occ.Contains(c) || !own.Add(c))
							return false;

				if (sep > 0)
					foreach (var c in own)
						for (var dx = -sep; dx <= sep; dx++)
							for (var dy = -sep; dy <= sep; dy++)
								if (occ.Contains(c + new CVec(dx, dy)) && !own.Contains(c + new CVec(dx, dy)))
									return false;

				// VERIFIED reach: the FIRST building of the block (the one built first, e.g. the bridge
				// NUKE / LITE / the radar) must actually touch the current buildable area (Chebyshev <= 2
				// of a seed cell: yard or silo). Geometry alone is not trusted — this check is what
				// guarantees the build chain cannot deadlock.
				var first = rel[0];
				return FootprintCells(tl + first.Off, first.Dim)
					.Any(c => seeds.Any(s => Math.Abs(s.X - c.X) <= 2 && Math.Abs(s.Y - c.Y) <= 2));
			}

			// EXACT placement from the yard edge: the yard-facing block edge sits at Chebyshev 2 from the
			// yard footprint (one cell beyond the reserved fence ring) — precisely the buildable-area reach.
			// Laterally centred on the yard. Deterministic fallback offsets: slide sideways first, then
			// step outward — no ring-search roulette that can settle a cell too far out.
			var yardMidX = (conYardMin.X + conYardMax.X) / 2;
			var yardMidY = (conYardMin.Y + conYardMax.Y) / 2;

			CPos BaseTopLeft(int outward, int lateral)
			{
				if (dir.Y < 0)
					return new CPos(yardMidX - (blockW / 2) + lateral, conYardMin.Y - 2 - outward - (blockH - 1));
				if (dir.Y > 0)
					return new CPos(yardMidX - (blockW / 2) + lateral, conYardMax.Y + 2 + outward);
				if (dir.X < 0)
					return new CPos(conYardMin.X - 2 - outward - (blockW - 1), yardMidY - (blockH / 2) + lateral);
				return new CPos(conYardMax.X + 2 + outward, yardMidY - (blockH / 2) + lateral);
			}

			for (var outward = 0; outward <= 4; outward++)
				for (var l = 0; l <= 10; l++)
					foreach (var lateral in l == 0 ? new[] { 0 } : new[] { l, -l })
					{
						var tl = BaseTopLeft(outward, lateral);
						if (RigidFits(tl))
							return rel.Select(e => new Slot { Dim = e.Dim, TopLeft = tl + e.Off }).ToList();
					}

			// No rigid fit in this direction — report failure so the cascade tries the next direction.
			return [];
		}

		// Flexible fallback: grow a contiguous block from an anchor, each building EDGE-ADJACENT at the most
		// compact position (min longest-side, then perimeter), deforming around obstacles. `sep` cells kept
		// clear to OTHER bulks (own buildings may touch). Never overlaps. Used when no clean rectangle fits.
		List<Slot> GrowFlexible(CPos anchor, List<CVec> dims, HashSet<CPos> area, HashSet<CPos> hard, HashSet<CPos> occ, int sep)
		{
			var slots = new List<Slot>();
			var own = new HashSet<CPos>();

			bool Valid(CPos tl, CVec dim)
			{
				foreach (var c in FootprintCells(tl, dim))
					if (!area.Contains(c) || hard.Contains(c) || occ.Contains(c) || own.Contains(c))
						return false;

				return true;
			}

			bool GapOk(CPos tl, CVec dim)
			{
				var cs = FootprintCells(tl, dim).ToHashSet();
				foreach (var c in cs)
					for (var dx = -sep; dx <= sep; dx++)
						for (var dy = -sep; dy <= sep; dy++)
						{
							var n = c + new CVec(dx, dy);
							if (occ.Contains(n) && !own.Contains(n) && !cs.Contains(n))
								return false;
						}

				return true;
			}

			foreach (var dim in dims)
			{
				CPos? best = null;
				if (own.Count == 0)
				{
					// The FIRST building must be inside seed reach (yard/silo, Chebyshev <= 2) or the
					// whole block is unreachable and the build plan livelocks on it.
					for (var r = 0; r <= Info.ChokeSearchRadius + 4 && best == null; r++)
						foreach (var off in Ring(r))
						{
							var tl = anchor - new CVec(dim.X / 2, dim.Y / 2) + off;
							if (!Valid(tl, dim) || !GapOk(tl, dim))
								continue;

							if (!FootprintCells(tl, dim).Any(c => seeds.Any(s => Math.Abs(s.X - c.X) <= 2 && Math.Abs(s.Y - c.Y) <= 2)))
								continue;

							best = tl;
							break;
						}
				}
				else
				{
					var bestScore = (long.MaxValue, long.MaxValue);
					var minx = own.Min(c => c.X) - dim.X;
					var maxx = own.Max(c => c.X) + 1;
					var miny = own.Min(c => c.Y) - dim.Y;
					var maxy = own.Max(c => c.Y) + 1;
					for (var x = minx; x <= maxx; x++)
						for (var y = miny; y <= maxy; y++)
						{
							var tl = new CPos(x, y);
							if (!Valid(tl, dim) || !GapOk(tl, dim))
								continue;

							var cs = FootprintCells(tl, dim).ToList();
							if (!cs.Any(c => Dirs4.Any(d => own.Contains(c + d))))
								continue;

							var all = own.Concat(cs).ToList();
							var w = all.Max(c => c.X) - all.Min(c => c.X) + 1;
							var h = all.Max(c => c.Y) - all.Min(c => c.Y) + 1;
							var sc = ((long)Math.Max(w, h), (long)(w + h));
							if (sc.CompareTo(bestScore) < 0) { bestScore = sc; best = tl; }
						}
				}

				if (best == null)
					break;

				slots.Add(new Slot { Dim = dim, TopLeft = best.Value });
				foreach (var c in FootprintCells(best.Value, dim))
					own.Add(c);
			}

			return slots;
		}

		void StoreSlots(string bulk, List<Slot> slots, HashSet<CPos> occ)
		{
			bulkSlots[bulk] = slots;
			foreach (var s in slots)
				foreach (var c in FootprintCells(s.TopLeft, s.Dim))
					occ.Add(c);
		}

		int PlacedCount(string bulk) => bulkSlots.TryGetValue(bulk, out var s) ? s.Count : 0;

		CPos BulkCentre(string bulk)
		{
			if (!bulkSlots.TryGetValue(bulk, out var s) || s.Count == 0)
				return baseCentre;

			var sx = (int)s.Average(x => x.TopLeft.X + (x.Dim.X / 2.0));
			var sy = (int)s.Average(x => x.TopLeft.Y + (x.Dim.Y / 2.0));
			return new CPos(sx, sy);
		}

		// Distinct building footprints of a bulk (age variants and faction variants collapse to one slot).
		// The building queue name for this player's faction ("Building.Nod" / "Building.GDI").
		string FactionBuildQueue() => player.Faction.InternalName == "nod" ? "Building.Nod" : "Building.GDI";

		// True if THIS faction can build the actor (its Buildable queue includes the faction queue). Shared
		// actors (both queues) count for both. Actors without a Buildable/queue are treated as buildable.
		bool BuildableByFaction(string type)
		{
			var bld = world.Map.Rules.Actors.TryGetValue(type, out var ai) ? ai.TraitInfoOrDefault<BuildableInfo>() : null;
			return bld == null || bld.Queue.Count == 0 || bld.Queue.Contains(FactionBuildQueue());
		}

		List<CVec> BulkBuildingDims(string[] types)
		{
			var seen = new HashSet<string>();
			var dims = new List<CVec>();
			foreach (var t in types)
			{
				// Only reserve slots for buildings THIS faction actually builds, otherwise a NOD base also
				// reserves GDI tech/production slots (atec vs stec, weap vs afld, gdi- vs nod-lite) that never
				// fill — leaving permanent gaps that make the block look scattered.
				if (!BuildableByFaction(t))
					continue;

				if (!seen.Add(BulkKey(t)))
					continue;

				var bi = BuildingOf(t);
				if (bi != null)
					dims.Add(bi.Dimensions);
			}

			return dims;
		}

		// Power block reserves 2 of the smallest plant (the basic NUKE the AI opens with) + PowerPlantCount
		// of the largest (the advanced NUK2), so the fully-teched block is symmetric and correctly sized.
		List<CVec> PowerBlockDims()
		{
			// Only this faction's plants (GDI has no NUK2, NOD has no turbine variant differences here).
			var dims = Info.PowerBulkTypes.Where(BuildableByFaction).Select(BuildingOf).Where(b => b != null).Select(b => b.Dimensions).ToList();
			if (dims.Count == 0)
				return [];

			// ONE small plant — the "bridge building" at the yard-facing corner of the block (built first,
			// sold again once the big plants stand, per the build plan) — plus PowerPlantCount big ones.
			var small = dims.OrderBy(d => d.X * d.Y).First();
			var large = dims.OrderByDescending(d => d.X * d.Y).First();
			var list = new List<CVec> { small };
			for (var i = 0; i < Info.PowerPlantCount; i++)
				list.Add(large);

			return list;
		}

		// The chokepoint is the narrowest point on the ground route an enemy actually takes to reach
		// the base. We already hold a BFS distance field from the enemy over passable terrain, which is
		// the same connectivity the game's pathfinder sees; the shortest enemy->base path is traced by
		// gradient descent on it. The path cell with the least clearance (distance to the nearest wall)
		// is the squeeze the enemy must pass — that is the chokepoint. Open corridors (no real squeeze)
		// fall back to a front line at the base's built-up depth toward the enemy.
		CPos? DetectChokepoint(WVec front, HashSet<CPos> region, Dictionary<CPos, int> clearance, Func<CPos, bool> passable)
		{
			var enemies = EnemyReferencePoints();

			// Multi-modal enemy-effort field: the minimum effort for the enemy to reach each cell, over
			// land (+1/cell), destroyed-bridge crossings (+BridgeRebuildPenalty) and naval landings
			// (+NavalLandingPenalty). The cheapest approach wins, so land is chosen first, then bridges,
			// then naval — exactly the requested priority.
			var navalLoco = world.WorldActor.TraitsImplementing<Locomotor>()
				.FirstOrDefault(l => l.Info.Name == Info.NavalLocomotor);
			bool WaterPassable(CPos c) => world.Map.Contains(c) && navalLoco != null
				&& navalLoco.MovementCostForCell(c) != PathGraph.MovementCostForUnreachableCell;

			var bridgeCells = new HashSet<CPos>();
			foreach (var bridge in world.ActorsWithTrait<Bridge>())
				foreach (var c in bridge.Trait.FootprintCells)
					bridgeCells.Add(c);

			var enemyDist = BuildThreatField(enemies, passable, WaterPassable, bridgeCells);

			var start = NearestPassable(baseCentre, passable, 8);
			if (start == null || !enemyDist.ContainsKey(start.Value))
			{
				// Enemy cannot reach the base by any mode (fully sealed): no chokepoint.
				var noLand = FallbackTowardEnemy(region, enemies, front, baseFrontExtent, passable);
				Log.Write("debug", $"[AotLayout] ChokeDetect: enemy unreachable -> fallback={noLand}");
				return noLand;
			}

			// Trace the cheapest enemy->base approach from the base by descending the effort field.
			var path = new List<CPos>();
			var cur = start.Value;
			var guard = 0;
			while (enemyDist.TryGetValue(cur, out var dcur) && dcur > 0 && guard++ < 8000)
			{
				path.Add(cur);
				var next = cur;
				var nextD = dcur;
				foreach (var dir in Dirs4)
				{
					var nc = cur + dir;
					if (enemyDist.TryGetValue(nc, out var dn) && dn < nextD)
					{
						nextD = dn;
						next = nc;
					}
				}

				if (next == cur)
					break;

				cur = next;
			}

			var floor = (conYardMax.X - conYardMin.X) + Info.ConYardFenceGap + 2;
			var maxDist = Info.ChokeSearchRadius;   // the gateway must be NEAR the base, not a cross-map neck
			var maxW = Info.ChokeMaxCorridor;

			// Cells to a wall along dir (up to maxW); bridges are crossings, not walls.
			bool Wall(CPos c) => !passable(c) && !bridgeCells.Contains(c);
			int RayToWall(CPos from, CVec dir)
			{
				for (var k = 1; k <= maxW; k++)
					if (Wall(from + (dir * k)))
						return k;

				return maxW + 1;
			}

			// A chokepoint is a point on the approach that is genuinely pinched: an impassable wall within
			// maxW on BOTH sides perpendicular to the path. That rejects the path merely hugging a single
			// wall or the map edge (one side open), which is why the block-and-refind approach gate (which
			// only takes the narrowest cell) picks false chokepoints next to a lone tiberium/rock blob.
			// Restrict to the [floor, maxDist] band around the base so we get the base's GATEWAY neck, not a
			// tighter neck far across the map; among real squeezes pick the narrowest, ties the CLOSER one.
			// Sizes of connected wall components: a REAL chokepoint is flanked by SUBSTANTIAL walls on both
			// sides (long ridges/bars). A point pinch beside a tiny rock island (comp size < ~12) is no
			// gate — the enemy simply walks around the island. Validated offline: preserves the approved
			// corridor gap (base 149,33 -> 125,48) and rejects the island pinch at 18,28 (compL = 2).
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
						foreach (var d8 in Dirs8)
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

			const int MinWallComponent = 12;
			bool Substantial(CPos c) => wallComp.TryGetValue(c, out var id) && compSize[id] >= MinWallComponent;

			CPos? choke = null;
			var bestWidth = int.MaxValue;
			var bestFwd = int.MaxValue;
			for (var i = 1; i < path.Count - 1; i++)
			{
				var c = path[i];
				var db = (c - baseCentre).Length;
				if (db < floor || db > maxDist || !(passable(c) || bridgeCells.Contains(c)))
					continue;

				var a = path[Math.Max(0, i - 1)];
				var b = path[Math.Min(path.Count - 1, i + 1)];
				var dir = CardinalToward(b - a);
				var perp = new CVec(-dir.Y, dir.X);

				var left = RayToWall(c, perp);
				var right = RayToWall(c, -perp);
				if (left > maxW || right > maxW)
					continue;

				if (!Substantial(c + (perp * left)) || !Substantial(c - (perp * right)))
					continue;   // flanked by a bypassable rock island, not a real gate

				var width = left + right;
				if (width < bestWidth || (width == bestWidth && db < bestFwd))
				{
					bestWidth = width;
					bestFwd = db;
					choke = c;
				}
			}

			// Which approach mode the chosen path used (for verification).
			var viaWater = path.Any(c => WaterPassable(c) && !passable(c));
			var viaBridge = path.Any(c => bridgeCells.Contains(c) && !passable(c));
			var mode = viaWater ? "naval" : viaBridge ? "bridge" : "land";

			if (choke.HasValue)
			{
				Log.Write("debug", $"[AotLayout] ChokeDetect: mode={mode} pathLen={path.Count} " +
					$"width={bestWidth} floor={floor} choke={choke.Value}");
				return choke.Value;
			}

			// No two-sided squeeze on the route (open terrain): hold a front line at the base's built-up depth.
			var front1 = path.FirstOrDefault(c => (c - baseCentre).Length >= baseFrontExtent);
			var frontLine = front1 != default ? front1 : FallbackTowardEnemy(region, enemies, front, baseFrontExtent, passable);
			Log.Write("debug", $"[AotLayout] ChokeDetect: mode={mode} pathLen={path.Count} " +
				$"width=none floor={floor} open terrain -> frontLine={frontLine}");
			return frontLine;
		}

		static int Manhattan(CPos a, CPos b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

		// Classify and score EVERY way an enemy can reach the base. Validated on zoomap (memory/
		// ai-ground-defense.md). Three sources, unioned:
		//  (A) land / bridge gates via iterative "block-and-refind": trace each enemy's shortest path,
		//      take the narrowest cell near the base as a gate, block that whole gap segment and re-flood
		//      so the enemy reroutes through any PARALLEL gate. Repeats until no new gate.
		//  (B) beach: shore segments (beach / water-adjacent land reachable from the base) — enemy lands.
		//  (C) destroyed bridge: a bridge hut hugging the base whose crossing is NOT land-reachable (an
		//      intact bridge would be walkable) and has no land/bridge gate beside it.
		void DetectApproaches(Func<CPos, bool> passable, Dictionary<CPos, int> clearance)
		{
			approaches.Clear();

			var enemies = EnemyReferencePoints();
			var huts = world.ActorsHavingTrait<LegacyBridgeHut>().Select(a => a.Location).ToList();
			var start = NearestPassable(baseCentre, passable, 8) ?? baseCentre;
			var r = Info.ChokeSearchRadius;

			int Clg(CPos c) => clearance.TryGetValue(c, out var v) ? v : int.MaxValue;
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

			(Dictionary<CPos, int> D, Dictionary<CPos, CPos> Pred) Bfs(HashSet<CPos> blocked)
			{
				var dist = new Dictionary<CPos, int> { [start] = 0 };
				var pred = new Dictionary<CPos, CPos>();
				var q = new Queue<CPos>();
				q.Enqueue(start);
				while (q.Count > 0)
				{
					var c = q.Dequeue();
					foreach (var d in Dirs4)
					{
						var n = c + d;
						if (!dist.ContainsKey(n) && passable(n) && !blocked.Contains(n))
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
					for (var dx = -1; dx <= 1; dx++)
						for (var dy = -1; dy <= 1; dy++)
						{
							var n = c + new CVec(dx, dy);
							if (!seen.Contains(n) && passable(n) && Clg(n) <= 2 && Manhattan(n, g) <= 4)
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
				var (dist, pred) = Bfs(blocked);
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
			{
				var type = huts.Any(h => Manhattan(h, g) <= 10) ? BaseApproachType.Bridge
					: Beachy(g) ? BaseApproachType.Beach : BaseApproachType.Land;
				approaches.Add(new BaseApproach(g, type));
			}

			// (B) beach shore landings
			var (full, _) = Bfs([]);
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
					for (var dx = -1; dx <= 1; dx++)
						for (var dy = -1; dy <= 1; dy++)
						{
							var n = cc + new CVec(dx, dy);
							if (!shoreSeen.Contains(n) && reach.Contains(n) && Beachy(n)) { shoreSeen.Add(n); q.Enqueue(n); }
						}
				}

				if (seg.Count < 3)
					continue;

				var rep = seg.MinBy(x => full[x]);
				if (!approaches.Any(a => Manhattan(a.Gate, rep) <= 6))
					approaches.Add(new BaseApproach(rep, BaseApproachType.Beach));
			}

			// (C) destroyed bridges: hut hugging the base core but not land-reachable across it
			var core = reach.Where(c => Clg(c) >= 3).ToList();
			foreach (var h in huts)
			{
				var eucl = core.Count > 0 ? core.Min(c => Math.Max(Math.Abs(h.X - c.X), Math.Abs(h.Y - c.Y))) : int.MaxValue;
				var landReach = full.Where(kv => Manhattan(kv.Key, h) <= 3).Select(kv => kv.Value).DefaultIfEmpty(int.MaxValue).Min();
				var beside = approaches.Any(a => (a.Type == BaseApproachType.Land || a.Type == BaseApproachType.Bridge) && Manhattan(a.Gate, h) <= 10);
				if (eucl <= 9 && landReach > 18 && !beside)
					approaches.Add(new BaseApproach(h, BaseApproachType.BridgeDestroyed));
			}

			// Dedupe near-duplicates keeping the higher score, then order by score.
			approaches.Sort((a, b) => b.Score.CompareTo(a.Score));
			var kept = new List<BaseApproach>();
			foreach (var a in approaches)
				if (!kept.Any(k => Manhattan(k.Gate, a.Gate) <= 6))
					kept.Add(a);

			approaches.Clear();
			approaches.AddRange(kept);

			Log.Write("debug", $"[AotLayout] Approaches: " + string.Join(", ", approaches.Select(a => $"{a.Type}@{a.Gate}")));
		}

		CPos? NearestPassable(CPos origin, Func<CPos, bool> passable, int maxRadius)
		{
			foreach (var c in world.Map.FindTilesInAnnulus(origin, 0, maxRadius)
				.OrderBy(c => (c - origin).LengthSquared))
				if (passable(c))
					return c;

			return null;
		}

		// Chebyshev distance from every in-window passable cell to the nearest wall (impassable cell
		// or the window border). Multi-source BFS seeded from all walls. Passable cells far from any
		// wall are simply absent from the map (treated as effectively unbounded clearance).
		Dictionary<CPos, int> BuildClearanceMap(CPos centre, int radius, Func<CPos, bool> passable)
		{
			var window = world.Map.FindTilesInAnnulus(centre, 0, radius + 1).ToHashSet();
			var clearance = new Dictionary<CPos, int>();
			var queue = new Queue<CPos>();

			foreach (var c in window)
				if (!passable(c))
				{
					clearance[c] = 0;
					queue.Enqueue(c);
				}

			while (queue.Count > 0)
			{
				var c = queue.Dequeue();
				var d = clearance[c] + 1;
				foreach (var dir in Dirs8)
				{
					var nc = c + dir;
					if (!window.Contains(nc) || clearance.ContainsKey(nc) || !passable(nc))
						continue;

					clearance[nc] = d;
					queue.Enqueue(nc);
				}
			}

			return clearance;
		}

		// Flood the base's open working area from the start cell over passable cells whose clearance
		// is at least the threshold. The fill naturally stops at necks (narrow gaps).
		HashSet<CPos> FloodOpenArea(CPos start, int radius, int threshold,
			Dictionary<CPos, int> clearance, Func<CPos, bool> passable)
		{
			int Clear(CPos c) => clearance.TryGetValue(c, out var v) ? v : int.MaxValue;

			var region = new HashSet<CPos> { start };
			var queue = new Queue<CPos>();
			queue.Enqueue(start);
			var r2 = radius * radius;

			while (queue.Count > 0)
			{
				var c = queue.Dequeue();
				foreach (var dir in Dirs4)
				{
					var nc = c + dir;
					if (region.Contains(nc) || (nc - start).LengthSquared > r2 || !passable(nc) || Clear(nc) < threshold)
						continue;

					region.Add(nc);
					queue.Enqueue(nc);
				}
			}

			return region;
		}

		// Minimum enemy effort (in path cells) to reach each cell from the enemy con yards, via Dijkstra
		// over three movement modes: land (+1 per cell), crossing a destroyed bridge (+BridgeRebuildPenalty,
		// the enemy would have to rebuild it) and a naval landing (+NavalLandingPenalty when embarking from
		// a shore into water, then +1 per water cell and to disembark). The penalties encode the threat
		// priority land < bridge < naval, so the cheapest field value at the base is its most likely front.
		Dictionary<CPos, int> BuildThreatField(List<CPos> seeds, Func<CPos, bool> landPassable,
			Func<CPos, bool> waterPassable, HashSet<CPos> bridgeCells)
		{
			var cost = new Dictionary<CPos, int>();
			var queue = new PriorityQueue<CPos, int>();
			foreach (var seed in seeds)
			{
				var s = NearestPassable(seed, landPassable, 8);
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

				var onWater = waterPassable(c) && !landPassable(c);
				foreach (var dir in Dirs4)
				{
					var n = c + dir;
					int step;
					if (onWater)
					{
						// Sailing across water, or disembarking onto land / a bridge.
						if ((waterPassable(n) && !landPassable(n)) || landPassable(n) || bridgeCells.Contains(n))
							step = 1;
						else
							continue;
					}
					else if (landPassable(n))
						step = 1;                             // walk along land
					else if (bridgeCells.Contains(n))
						step = Info.BridgeRebuildPenalty;     // cross (rebuild) a destroyed bridge
					else if (waterPassable(n))
						step = Info.NavalLandingPenalty;      // embark from the shore into the water
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

		// Enemy con yards are the natural "where the ground attack comes from" reference. Fall back to
		// the map centre when none can be found (very early, or spectators only).
		List<CPos> EnemyReferencePoints()
		{
			var refs = new List<CPos>();

			// The base is planned EARLY — before enemies have deployed their MCV into a con yard — so live
			// con yards are usually absent at this point. The map's spawn points are the reliable reference:
			// always land-valid and on the correct far side of the map, so the enemy-approach BFS routes
			// through the real terrain chokepoint (this matches the validated prototype, which used spawns).
			var spawns = new List<CPos>();
			foreach (var n in world.Map.ActorDefinitions)
				if (n.Value.Value == "mpspawn")
					spawns.Add(new ActorReference(n.Key, n.Value).GetValue<LocationInit, CPos>());

			if (spawns.Count > 1)
			{
				// Exclude the spawn closest to our own base (that is ours); the rest are the enemies'.
				var mine = spawns.MinBy(s => (s - baseCentre).LengthSquared);
				refs.AddRange(spawns.Where(s => s != mine));
			}

			// Additionally use any enemy con yard already visible (a stronger signal than the spawn point).
			foreach (var p in world.Players)
			{
				if (p == player || p.NonCombatant || player.RelationshipWith(p) != PlayerRelationship.Enemy)
					continue;

				var cy = world.ActorsHavingTrait<Building>()
					.FirstOrDefault(a => a.Owner == p && !a.IsDead && Info.ConstructionYardTypes.Contains(a.Info.Name));
				if (cy != null && !refs.Contains(cy.Location))
					refs.Add(cy.Location);
			}

			// Last resort (no spawns defined, no con yards): the map centre, snapped to a reachable cell.
			if (refs.Count == 0)
			{
				var b = world.Map.Bounds;
				var centre = new CPos(b.Left + (b.Width / 2), b.Top + (b.Height / 2));
				refs.Add(NearestPassable(centre, c => world.Map.Contains(c), 12) ?? centre);
			}

			return refs;
		}

		// Open-map fallback: aim minDist cells from the con yard toward the nearest enemy (snapped to
		// the nearest passable cell); if that is off-map, the base-area cell furthest toward the enemy.
		CPos FallbackTowardEnemy(HashSet<CPos> region, List<CPos> enemies, WVec front, int minDist, Func<CPos, bool> passable)
		{
			var dir = front;
			if (enemies.Count > 0)
			{
				var nearest = enemies.OrderBy(e => (e - baseCentre).LengthSquared).First();
				var v = world.Map.CenterOfCell(nearest) - world.Map.CenterOfCell(baseCentre);
				if (v.HorizontalLengthSquared > 0)
					dir = v;
			}

			if (dir.HorizontalLength > 0)
			{
				var unit = (dir * 1024) / dir.Length;
				var target = world.Map.CellContaining(world.Map.CenterOfCell(baseCentre) + (unit * minDist));
				var snapped = NearestPassable(target, passable, 6);
				if (snapped.HasValue)
					return snapped.Value;
			}

			var best = baseCentre;
			var bestDot = long.MinValue;
			var startW = world.Map.CenterOfCell(baseCentre);
			foreach (var c in region)
			{
				var o = world.Map.CenterOfCell(c) - startW;
				var dot = ((long)o.X * dir.X) + ((long)o.Y * dir.Y);
				if (dot > bestDot)
				{
					bestDot = dot;
					best = c;
				}
			}

			return best;
		}

		// Returns the bulk anchor a given building type should cluster toward, or false if the
		// building is not part of the masterplan (caller uses FindPos to place the nearest
		// buildable cell to this anchor, which handles buildable-area reachability).
		public bool TryGetBulkAnchor(string actorType, out CPos anchor)
		{
			anchor = CPos.Zero;

			if (IsTraitDisabled)
				return false;

			EnsurePlanned();
			if (!planned)
				return false;

			if (Info.PowerBulkTypes.Contains(actorType)) { anchor = powerAnchor; return true; }
			if (Info.ProductionBulkTypes.Contains(actorType)) { anchor = productionAnchor; return true; }
			if (Info.TechBulkTypes.Contains(actorType)) { anchor = techAnchor; return true; }
			if (Info.FixTypes.Contains(actorType)) { anchor = fixAnchor; return true; }

			return false;
		}

		// True if the cell belongs to a reserved bulk footprint (power/prod/tech/stealth block). The base
		// builder consults this so non-bulk buildings keep OUT of the blocks and only fill the gaps between
		// them. Bulk buildings themselves never reach this check — they are routed by TryGetBulkGridSlot.
		public bool IsReservedForBulk(CPos cell)
		{
			if (IsTraitDisabled)
				return false;

			EnsurePlanned();
			return reservedCells.Contains(cell);
		}

		string BulkOf(string actorType)
		{
			if (Info.PowerBulkTypes.Contains(actorType)) return "power";
			if (Info.ProductionBulkTypes.Contains(actorType)) return "prod";
			if (Info.TechBulkTypes.Contains(actorType)) return "tech";
			if (Info.StealthTypes.Contains(actorType)) return "stealth";
			if (Info.SiloTypes.Contains(actorType)) return "silo";
			return null;
		}

		// Serve the next free reserved slot whose footprint matches the requested building, from the bulk
		// it belongs to (power / production / tech). The masterplan reserved these as contiguous fenceable
		// blocks in PlanBulks, so buildings land where planned. Returns false (caller falls back to FindPos
		// clustering) when the actor is not a bulk building, no matching slot is free, or it is not
		// currently buildable (e.g. transiently blocked) — the BaseBuilder simply retries later.
		public bool TryGetBulkGridSlot(string actorType, ActorInfo ai, out CPos slot)
		{
			slot = CPos.Zero;

			if (IsTraitDisabled)
				return false;

			EnsurePlanned();
			if (!planned)
				return false;

			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return false;

			var bulk = BulkOf(actorType);
			if (bulk == null || !bulkSlots.TryGetValue(bulk, out var slots))
				return false;

			resourceLayer ??= world.WorldActor.TraitOrDefault<IResourceLayer>();

			// Contiguous fill: among the placeable free slots of matching footprint, take the one CLOSEST to
			// the bulk's already-built cells (or the block centre while still empty). Each block then grows as
			// one contiguous cluster — the first-built buildings (e.g. Radar + STEC, Light Factory + Barracks)
			// land directly next to each other and unbuilt slots stay at the OUTER edge — instead of scattering
			// by build order across the reserved footprints.
			var used = slots.Where(s => s.Used).SelectMany(s => FootprintCells(s.TopLeft, s.Dim)).ToList();
			var blockCentre = BulkCentre(bulk);

			Slot best = null;
			var bestDist = long.MaxValue;
			foreach (var s in slots)
			{
				if (s.Used || s.Dim.X != bi.Dimensions.X || s.Dim.Y != bi.Dimensions.Y)
					continue;

				if (!world.CanPlaceBuilding(s.TopLeft, ai, bi, null)
					|| !bi.IsCloseEnoughToBase(world, player, ai, s.TopLeft)
					|| !IsClearOfResources(bi, s.TopLeft))
					continue;

				var c = s.TopLeft + new CVec(s.Dim.X / 2, s.Dim.Y / 2);
				var d = used.Count > 0 ? used.Min(u => (long)(c - u).LengthSquared) : (long)(c - blockCentre).LengthSquared;
				if (d < bestDist)
				{
					bestDist = d;
					best = s;
				}
			}

			if (best == null)
			{
				var freeMatch = slots.Count(s => !s.Used && s.Dim.X == bi.Dimensions.X && s.Dim.Y == bi.Dimensions.Y);
				Log.Write("debug", $"[AotLayout] NoSlot {actorType} bulk={bulk} freeMatch={freeMatch} -> FindPos (block unreachable/blocked)");
				return false;
			}

			Log.Write("debug", $"[AotLayout] Serve {actorType} -> {best.TopLeft} bulk={bulk}");
			best.Used = true;
			slot = best.TopLeft;
			return true;
		}

		// A free, footprint-matching slot that is blocked ONLY by missing buildable area (out of reach).
		// The deterministic builder bridges a temporary wall chain toward it (walls give buildable area),
		// places the building, then sells the walls — the human technique for securing a base layout.
		// Slots blocked transiently (a unit standing on them) are NOT returned; those just need waiting.
		public bool TryPeekUnreachableSlot(string actorType, ActorInfo ai, out CPos slot)
		{
			slot = CPos.Zero;

			if (IsTraitDisabled)
				return false;

			EnsurePlanned();
			if (!planned)
				return false;

			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return false;

			var bulk = BulkOf(actorType);
			if (bulk == null || !bulkSlots.TryGetValue(bulk, out var slots))
				return false;

			foreach (var s in slots)
			{
				if (s.Used || s.Dim.X != bi.Dimensions.X || s.Dim.Y != bi.Dimensions.Y)
					continue;

				if (world.CanPlaceBuilding(s.TopLeft, ai, bi, null)
					&& !bi.IsCloseEnoughToBase(world, player, ai, s.TopLeft)
					&& IsClearOfResources(bi, s.TopLeft))
				{
					slot = s.TopLeft;
					return true;
				}
			}

			return false;
		}

		bool IsClearOfResources(BuildingInfo bi, CPos topLeft)
		{
			if (resourceLayer == null)
				return true;

			return bi.Tiles(topLeft).All(t => resourceLayer.GetResource(t).Type == null);
		}
	}
}
