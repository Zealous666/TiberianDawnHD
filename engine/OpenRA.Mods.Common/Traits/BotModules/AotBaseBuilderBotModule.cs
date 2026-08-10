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
	[TraitLocation(SystemActors.Player)]
	[Desc("Age of Tiberium: executes the AotBasePlanner's saved base plan step by step — ONE item in",
		"production at a time, in the planner's rhythm. A step whose target is out of buildable-area",
		"reach is connected with a temporary wall chain along the actual PATH (walls give buildable",
		"area), then built, then the chain is sold. Steps whose actors are not buildable yet (age",
		"gates) simply wait. Destroyed plan buildings are detected periodically and rebuilt in rhythm",
		"order. Owns its production queues exclusively.")]
	public class AotBaseBuilderBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Only run for players of this faction (internal name).")]
		public readonly string Faction = null;

		[Desc("Production queues this module fully controls.")]
		public readonly HashSet<string> BuildingQueues = [];

		[ActorReference]
		[Desc("Wall used for bridges and fences.")]
		public readonly string WallType = null;

		[Desc("Abort a wall bridge after this many segments.")]
		public readonly int MaxBridgeLength = 24;

		[Desc("Pull a power step (NUKE/NUK2) forward when excess power drops below this.")]
		public readonly int MinimumExcessPower = 10;

		[Desc("Ticks between bot decisions.")]
		public readonly int Interval = 25;

		[Desc("Ticks between rebuild scans (detects destroyed plan buildings).")]
		public readonly int RebuildScanInterval = 250;

		[Desc("Ticks a DEFENCE step (SAM/FTUR/GUN/OBELISK + their gate fences) may sit genuinely",
			"unplaceable -- re-site AND bridge both failing every attempt -- before it is skipped for",
			"good (user spec 2026-07-31). Defence runs through its own independent chooser, separate",
			"from the core economy/tech Rhythm, precisely so this timeout can never affect core: a",
			"core step must NEVER be silently abandoned, since a later Age upgrade may depend on it.")]
		public readonly int DefenseStepTimeoutTicks = 6000;

		[Desc("CORE roles that outrank everything else of their own Age: while one of these is still",
			"outstanding, the DEFENCE chooser stands down completely, and the generic upgrades module",
			"(BaseBuilderBotModule@aotupgrades) is not allowed to spend either. User spec 2026-08-02:",
			"'es sollte eine klare prio geben: airfield -> refinery -> sams -> upgrades, sonst dauert",
			"alles ewig weil der cashflow das nicht her gibt' -- reaching Age 1 previously started the",
			"Airfield, a scattering of SAM sites and a random assortment of upgrades all at once, three",
			"independent spenders competing for the same early-Age-1 income.")]
		public readonly string[] PriorityCoreRoles = ["AFLD", "PROC"];

		[Desc("Age tier whose building programme must be COMPLETE before any upgrade may be bought.",
			"User spec 2026-08-10: no upgrade at all before every building of Age 1 stands. The only",
			"upgrade available in Age 0 is the Foundation, and buying it while the Tech Centre is",
			"still going up is exactly the spending order this chain exists to prevent.")]
		public readonly int UpgradesAfterAge = 1;

		[Desc("Ticks either priority gate (defence-behind-core, upgrades-behind-both) may hold before",
			"it releases anyway. A core step is never skipped, so without a timeout one permanently",
			"unbuildable Airfield would",
			"freeze all defence and all upgrades for the rest of the match.")]
		public readonly int PriorityGateTimeoutTicks = 15000;

		[Desc("Cash (credits + ore) at which the UPGRADE gate releases regardless of what the rhythm",
			"is still doing. The gate only exists because early income cannot fund base, defence and",
			"upgrades at once -- once the AI is sitting on this much, that reason is gone. Note this is",
			"the WEAKEST of the three backups in practice: Operations keeps producing waves and helis,",
			"so a healthy AI rarely sits on a large balance (user observation 2026-08-02). The two",
			"tick-based backups below are the ones that actually guarantee upgrades happen.")]
		public readonly int PriorityGateCashOverride = 2500;

		[Desc("TOTAL ticks the UPGRADE gate may ever hold, summed over the whole match and never reset.",
			"PriorityGateTimeoutTicks alone only bounds ONE continuous stall: a gate that keeps briefly",
			"clearing and re-engaging (defence of the next Age tier opening right as the previous one",
			"finishes) would reset that counter forever and starve upgrades for the entire match without",
			"ever technically deadlocking. Once this budget is spent the upgrade gate stays open for",
			"good. Backup plan #2.")]
		public readonly int PriorityGateTotalBudgetTicks = 36000;

		[Desc("Ticks the BASE EXPANSION may withhold Operations' army production while still in Age 1.",
			"User spec 2026-08-02: 'insbesondere zu age-1 muss es vorallem um cashflow-optimierung",
			"mittels refinery und base expansion gehen' -- the Age-1 Refinery already pauses army",
			"production (see Age1RefineryPending), and the expansion is the other half of that same",
			"economy push. Tightly bounded and one-shot: army production is what keeps the AI alive, so",
			"once this budget is spent the expansion never pauses production again.")]
		public readonly int ExpansionPausesArmyBudgetTicks = 6000;

		[Desc("Ticks the Age-1 Refinery may withhold Operations' army production. Originally this pause",
			"needed no bound at all: it covered exactly one building, entered only once the Airfield",
			"ahead of it was already standing. The build-order swap of 2026-08-04 (Refinery first, then",
			"Airfield) made the Refinery the FIRST Age-1 step, so the pause now also spans the NOD",
			"Tiberium Secrets purchase that gates it -- and while that gatekeeper holds the core rhythm,",
			"an AI that cannot yet afford it would build nothing AND produce nothing, with no defence",
			"going up either. One-shot like the expansion pause: once spent, the Refinery never withholds",
			"unit production again.")]
		public readonly int RefineryPausesArmyBudgetTicks = 9000;

		[ActorReference]
		[Desc("Techtree GATEKEEPER upgrades: upgrades that are not optional combat perks but hard",
			"prerequisites for later PLAN buildings (Nod: aot-upgrade-nod-forbidden unlocks the Shrine,",
			"aot-upgrade-laser-fence unlocks the Obelisk). Fired directly the moment each becomes",
			"buildable, in the listed order -- no Rhythm gate, since the whole point is that the Rhythm",
			"CANNOT proceed without them. Leaving these to the weighted-random pick of",
			"BaseBuilderBotModule@aotupgrades deadlocked the base outright (user-fund 2026-08-02: SHRN is",
			"a CORE step, and core steps never time out, so a bot that never happened to roll",
			"aot-upgrade-nod-forbidden sat on 'Waiting (age gate / prerequisites): SHRN' forever -- 618",
			"such lines in one session, and across 5 Nod bots the Obelisk was never built even once).",
			"Must be removed from that module's BuildingFractions so nothing double-fires.")]
		public readonly string[] GatekeeperUpgradeTypes = [];

		[Desc("Prerequisites marking Age tiers 1-3, age-ordered. Same convention as",
			"AotOperationsBotModuleInfo.AgePrerequisites -- kept as an independent copy here rather",
			"than shared, since this module must be able to compute the current Age tier even when no",
			"Operations module exists for this faction.")]
		public readonly string[] AgePrerequisites = ["aot-age1", "aot-age2", "aot-age3"];

		[Desc("Minimum Chebyshev spacing between the two gate turrets.")]
		public readonly int TurretSpacing = 3;

		[ActorReference]
		[Desc("Naval production building (Sub Pen / Shipyard). Built ON DEMAND, outside the fixed Rhythm --",
			"triggered by RequestNavalProduction(), called by any Operations mission that actually needs",
			"ships/subs/vessels (user spec 2026-07-22: naval production is an Operations concern, not a",
			"base-rhythm step). Age-ordered variants; the first buildable one is used. Reuses the same",
			"wall-bridge machinery as every other out-of-reach plan step to reach a coastal site.")]
		public readonly string[] NavalPenTypes = [];

		[Desc("Locomotor name of the ferry ship that will dock here (e.g. \"aot-lst\") -- used to verify the",
			"chosen site's adjacent water is actually ship-navigable, not just orthogonally Water-typed",
			"terrain (a rock-enclosed inlet can satisfy the terrain check but be unreachable by a real ship).",
			"Leave empty to skip the check (falls back to the old terrain-only behaviour).")]
		public readonly string NavalLocomotor = null;

		// ---- Base expansion layout (User-Briefing 2026-08-03) ----
		// Fixed mini-layout, offsets hardcoded in RequestExpansionLayout from the arrangement the user
		// pre-built in the editor (see memory/ai-base-expansion-layout.md). Only the ACTOR chains are
		// configurable -- the geometry is deliberately not, because it was specified exactly.
		[ActorReference]
		[Desc("Power plant chain for the expansion (age-ordered, first buildable wins).")]
		public readonly string[] ExpansionPowerTypes = [];

		[ActorReference]
		[Desc("Refinery chain for the expansion.")]
		public readonly string[] ExpansionRefineryTypes = [];

		[ActorReference]
		[Desc("Silo chain for the expansion.")]
		public readonly string[] ExpansionSiloTypes = [];

		[ActorReference]
		[Desc("Anti-air chain for the expansion.")]
		public readonly string[] ExpansionSamTypes = [];

		[ActorReference]
		[Desc("Gate chain for the expansion. Occupies two cells, which is why the fence's bottom row",
			"leaves a two-cell gap at the gate position.")]
		public readonly string[] ExpansionGateTypes = [];

		[Desc("Age tier from which the expansion's fence and gate are built (user spec: Age 2).")]
		public readonly int ExpansionFenceAgeTier = 2;

		[Desc("Builder ticks a single expansion building may sit unfinished before it is skipped.",
			"Bounded on purpose: an expansion must never turn into a retry loop the way a stuck",
			"main-Rhythm step once did.")]
		public readonly int ExpansionStepTimeoutTicks = 400;

		public override object Create(ActorInitializer init) { return new AotBaseBuilderBotModule(init.Self, this); }
	}

	public class AotBaseBuilderBotModule : ConditionalTrait<AotBaseBuilderBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;

		AotBasePlannerBotModule planner;
		AotMapIntelBotModule intel;
		AotOperationsBotModule ops;
		PowerManager playerPower;
		PlayerResources playerResources;
		TechTree techTree;

		int ticks;
		int rebuildTicks;
		int waitLog;
		int pendingWaitLog;

		// On-demand naval production (Sub Pen / Shipyard): NOT part of the fixed Rhythm, but the SITE is
		// planned eagerly (navalPlanAttempted) as soon as map intel is ready -- independent of whether any
		// Operations mission has ever actually asked for it (user spec 2026-07-22: "sauber geplant, aber
		// nur bei request gebaut" -- know and log whether a coastal site exists at all well before it's
		// needed, but only queue the actual construction on-demand). Requested by any Operations mission
		// that needs ships/subs/vessels; sticky (never cleared) so a later loss of the building triggers an
		// automatic rebuild the next time HasNavalProduction() is checked.
		bool navalPlanAttempted;
		bool navalRequested;
		int economyPauseLog;
		AotPlanStep navalStep;
		int navalWaitLog;

		// The single in-flight production: the step it serves, the cell it will be placed at, and
		// whether the produced item is a bridge wall rather than the step's own building.
		AotPlanStep pending;
		string pendingType;
		CPos pendingCell;
		bool pendingIsBridgeWall;

		readonly List<CPos> bridgeWallCells = [];

		// Which step the standing wall chain belongs to. The chain is shared state, and selling it is
		// tied to "a building was placed" -- so a chain built for the naval pen was torn down again as
		// soon as ANY other step finished, and the pen step restarted from zero forever (confirmed
		// in-game 2026-07-27: exactly 6 segments, sold, rebuilt, endlessly). Only the owning step may
		// sell its own bridge.
		AotPlanStep bridgeOwner;

		// Fence execution: remaining node cells of the fence step currently being built.
		AotPlanStep fenceStep;
		readonly Queue<CPos> fenceQueue = new();

		// How many consecutive attempts a single fence node may stay blocked by something non-permanent
		// (an own unit parked on it) before the node is skipped, so one loiterer cannot stall the ring.
		const int FenceNodeMaxWaits = 12;
		int fenceNodeWaits;

		// Single-shot latch for Info.GatekeeperUpgradeTypes -- sized at Created time since that list is
		// mod-configured. Latching mirrors TryOreBoost's "retry every tick until buildable, then never
		// again" shape: the production queue keeps the item queued-but-paused on its own if cash is
		// short at the exact firing tick, so one StartProduction order is enough.
		bool[] gatekeeperFired;

		// Priority gates (user spec 2026-08-02, "airfield -> refinery -> sams -> upgrades"). Two
		// separate counters on purpose: the DEFENCE gate only waits on the core economy roles, while
		// the UPGRADE gate additionally waits out the defence steps behind them. Sharing one counter
		// would let the long defence stretch time out the (much shorter) core gate as a side effect.
		int coreGateTicks;
		int gatekeeperGateTicks;
		int upgradeGateTicks;

		// Never reset -- see PriorityGateTotalBudgetTicks. upgradeGateReleased latches the "this gate is
		// done holding, permanently" decision so the reason is logged exactly once.
		int upgradeGateSpentTicks;
		bool upgradeGateReleased;

		// Age-1 expansion-vs-army-production pause (see ExpansionPausesArmyBudgetTicks). One-shot.
		int expansionArmyPauseTicks;
		bool expansionArmyPauseRetired;

		// Same, for the Age-1 Refinery (see RefineryPausesArmyBudgetTicks). One-shot.
		int refineryArmyPauseTicks;
		bool refineryArmyPauseRetired;

		public AotBaseBuilderBotModule(Actor self, AotBaseBuilderBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			// NOTE: do NOT filter by IsTraitDisabled here — at Created time the bot condition is not yet
			// granted, so every trait reports disabled and we would resolve null forever. Match by faction
			// so the right planner is picked once multiple faction instances exist.
			var planners = self.Owner.PlayerActor.TraitsImplementing<AotBasePlannerBotModule>().ToList();
			planner = planners.FirstOrDefault(p => p.Info.Faction == Info.Faction) ?? planners.FirstOrDefault();
			playerPower = self.Owner.PlayerActor.TraitOrDefault<PowerManager>();
			playerResources = self.Owner.PlayerActor.TraitOrDefault<PlayerResources>();
			techTree = self.Owner.PlayerActor.Trait<TechTree>();

			// Faction-agnostic, one instance per player -- same resolution AotOperationsBotModule uses.
			intel = self.Owner.PlayerActor.TraitsImplementing<AotMapIntelBotModule>().FirstOrDefault();

			// Back-reference for FirstDerrickSquadPending() (User 2026-07-31) -- mirrors how Ops itself
			// resolves this module (builder), just in the other direction.
			var opsModules = self.Owner.PlayerActor.TraitsImplementing<AotOperationsBotModule>().ToList();
			ops = opsModules.FirstOrDefault(o => o.Info.Faction == Info.Faction) ?? opsModules.FirstOrDefault();

			gatekeeperFired = new bool[Info.GatekeeperUpgradeTypes.Length];
		}

		// Mirrors AotOperationsBotModule.AgeTier() exactly (same AgePrerequisites convention).
		// EXISTS HERE because none of the individual defence actors (SAM in particular: Buildable.
		// Prerequisites is just "anypower, aot-nod-radar", no age gate at all) carry their own
		// age-tier prerequisite -- before the Core/Defence split, that never mattered, because sharing
		// ONE strict list meant an Age-1 SAM simply could not be reached until everything positioned
		// ahead of it (all of Age 0, including AFLD/PROC) was Done, purely from list position. Splitting
		// Defence onto its own independent chooser removed that implicit brake and put nothing in its
		// place: confirmed 2026-08-01 -- the AI built nearly every SAM site (Age 0 through Age 3) while
		// still in Age 0, burning the early cash the split was supposed to protect. ChooseStep(defense:
		// true) now filters on AotPlanStep.Age <= AgeTier() to restore exactly the gate every OTHER
		// age-gated Rhythm step already gets from its own Buildable.Prerequisites.
		int AgeTier()
		{
			for (var i = Info.AgePrerequisites.Length - 1; i >= 0; i--)
				if (techTree.HasPrerequisites([Info.AgePrerequisites[i]]))
					return i + 1;

			return 0;
		}

		// ---------------------------------------------------------------- on-demand naval production

		public bool HasNavalProduction() =>
			Info.NavalPenTypes.Length > 0
			&& world.Actors.Any(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.NavalPenTypes.Contains(a.Info.Name));

		// Where ships will actually appear: the finished pen if there is one, otherwise the planned
		// site. The ferry seeds its water search from here so embark and landing are guaranteed to be
		// on the SAME body of water the ships can use.
		public CPos? NavalSite()
		{
			var pen = world.Actors.FirstOrDefault(a => a.Owner == player && !a.IsDead && a.IsInWorld
				&& Info.NavalPenTypes.Contains(a.Info.Name));
			return pen?.Location ?? navalStep?.TopLeft;
		}

		// Sticky: once any Operations mission has ever needed naval production, keep guaranteeing it exists
		// for the rest of the match (a later loss triggers an automatic rebuild -- see BotTick).
		public void RequestNavalProduction() => navalRequested = true;

		// ---- Base expansion (User-Briefing 2026-08-03) ----
		//
		// The expansion is a FIXED mini-layout, not a second planner run (user decision): the planner
		// packs exactly one base around exactly one construction yard, and teaching it a second one was
		// explicitly out of scope. Geometry comes from the layout the user pre-built in the editor and
		// is recorded in memory/ai-base-expansion-layout.md -- offsets are relative to the new yard's
		// top-left cell.
		//
		// Construction runs through THIS module rather than the mission, so the expansion inherits the
		// whole existing apparatus: variant selection per age, re-siting when a cell turns out blocked,
		// wall-bridge routing into buildable area, nudging own units off the site, and stuck detection.
		// Steps are marked Defense so they use the timeout-capable chooser -- an expansion that cannot
		// be finished must never wedge the main base's economy queue.
		readonly List<AotPlanStep> expansionSteps = [];
		CPos? expansionYard;

		public bool ExpansionPlanned => expansionYard != null;

		// ------------------------------------------------------------------------------------------
		// THE canonical description of the expansion compound. Both the construction below and the
		// SITE TEST in AotOperationsBotModule read it, so they cannot drift apart.
		//
		// They had drifted badly: the site test hard-coded a solid 8x10 rectangle and demanded that
		// all 80 cells be passable, resource-free and empty. The compound does not fill that
		// rectangle -- five buildings, a fence around the rim, and a courtyard that is never built on
		// at all. One rock in that empty courtyard, or a patch of tiberium nowhere near a foundation,
		// vetoed the whole site. That is why maps with plenty of usable ground came back with nothing
		// (User 2026-08-05: "ich kann dir im editor spielend belegen, an wievielen orten der feind
		// dieses template haette aufbauen koennen").
		// ------------------------------------------------------------------------------------------
		public readonly record struct ExpansionCell(CPos Cell, string Role, bool Critical);

		// Building anchors, in build order. The yard itself is first: the MCV has to deploy there.
		IEnumerable<(string Role, string[] Variants, CVec Offset)> ExpansionBuildings()
		{
			yield return ("NUKE", Info.ExpansionPowerTypes, new CVec(-3, 0));
			yield return ("PROC", Info.ExpansionRefineryTypes, new CVec(0, 3));
			yield return ("SILO", Info.ExpansionSiloTypes, new CVec(-3, 5));
			yield return ("SAM", Info.ExpansionSamTypes, new CVec(-1, 3));

			// Fence + gate are Age-2 items (user spec). The gate occupies TWO cells (-1,+7) and (0,+7),
			// which is why the bottom fence row has a gap there rather than a missing segment.
			yield return ("GATE", Info.ExpansionGateTypes, new CVec(-1, 7));
		}

		static IEnumerable<CPos> FencePerimeter(CPos yard)
		{
			for (var x = -4; x <= 3; x++)
			{
				yield return yard + new CVec(x, -1);
				if (x != -1 && x != 0)
					yield return yard + new CVec(x, 7);
			}

			for (var y = -1; y <= 7; y++)
			{
				yield return yard + new CVec(-4, y);
				yield return yard + new CVec(3, y);
			}

			yield return yard + new CVec(-2, 8);
			yield return yard + new CVec(1, 8);
		}

		// Every cell the compound will actually occupy, building footprints expanded. Critical cells
		// carry a foundation and must be clear; the fence is not critical -- a perimeter with a gap
		// is a perfectly good expansion, and refusing the whole site over one unbuildable wall cell is
		// how the search ended up rejecting everything.
		// yardTypes comes from the Operations module, which owns the MCV/yard actor names.
		public IEnumerable<ExpansionCell> ExpansionLayout(CPos yard, IEnumerable<string> yardTypes)
		{
			foreach (var c in Footprint(yardTypes, yard))
				yield return new ExpansionCell(c, "YARD", true);

			foreach (var (role, variants, offset) in ExpansionBuildings())
				foreach (var c in Footprint(variants, yard + offset))
					yield return new ExpansionCell(c, role, true);

			foreach (var c in FencePerimeter(yard).Distinct())
				yield return new ExpansionCell(c, "FENCE", false);
		}

		// Real footprint from the rules, not a guess. An unknown or unset variant falls back to the
		// anchor cell alone rather than silently claiming nothing.
		IEnumerable<CPos> Footprint(IEnumerable<string> variants, CPos topLeft)
		{
			var type = variants.FirstOrDefault(t => world.Map.Rules.Actors.ContainsKey(t));
			if (type == null)
				return [topLeft];

			var bi = world.Map.Rules.Actors[type].TraitInfoOrDefault<BuildingInfo>();
			return bi == null ? [topLeft] : bi.Tiles(topLeft);
		}

		public void RequestExpansionLayout(CPos yard)
		{
			if (expansionYard == yard)
				return;

			expansionSteps.Clear();
			expansionYard = yard;

			void Add(string role, string[] variants, CVec offset)
			{
				if (variants.Length > 0)
					expansionSteps.Add(new AotPlanStep
					{
						Kind = AotStepKind.Building,
						Role = "EXP_" + role,
						Variants = variants,
						TopLeft = yard + offset,
						Defense = true
					});
			}

			foreach (var (role, variants, offset) in ExpansionBuildings())
				Add(role, variants, offset);

			// Fence as one step PER CELL rather than a single Fence-kind step: the expansion runs on its
			// own simple driver (see TickExpansion) which knows nothing about node/perimeter bookkeeping,
			// and a wall carries LineBuildInfo anyway, so each cell is placed with a LineBuild order and
			// the engine connects the run itself. Gap at (-1,+7)/(0,+7) is the gate.
			if (Info.WallType != null)
			{
				foreach (var c in FencePerimeter(yard).Distinct())
					expansionSteps.Add(new AotPlanStep
					{
						Kind = AotStepKind.Building,
						Role = "EXP_FENCE",
						Variants = [Info.WallType],
						TopLeft = c,
						Defense = true
					});
			}

			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Expansion layout requested at {yard} " +
				$"({expansionSteps.Count} steps)");
		}

		public void ClearExpansionLayout()
		{
			expansionSteps.Clear();
			expansionYard = null;
		}

		// Age-gated exactly like the Rhythm's own defence steps: fence and gate only from Age 2 on
		// (user spec), everything else as soon as the yard stands.
		AotPlanStep ChooseExpansionStep()
		{
			if (expansionSteps.Count == 0)
				return null;

			var tier = ops?.AgeTier() ?? 0;
			foreach (var s in expansionSteps)
			{
				if (s.Done || s.Skipped)
					continue;

				var lateStep = s.Role == "EXP_FENCE" || s.Role == "EXP_GATE";
				if (lateStep && tier < Info.ExpansionFenceAgeTier)
					continue;

				return s;
			}

			return null;
		}

		// ---- Expansion build driver -------------------------------------------------------------
		//
		// Runs in PARALLEL with the main base's build slot, not behind it (User-Einwand 2026-08-03:
		// "bau einen zweiten builder? sobald der mcv als construction yard platziert ist, ist es ja
		// auch eine eigene queue"). Verified: ProductionQueue@NodBuilding sits on the FACT actor, not
		// on the player, so a second construction yard genuinely owns a second Building.* queue.
		//
		// That also means the shared QueueFor() is unsafe here -- it takes FirstOrDefault across all
		// of the player's queues of that category, so with two yards it would pick one arbitrarily and
		// the expansion could end up building out of the main base's slot (or vice versa). This driver
		// binds strictly to the queue instance owned by the expansion yard.
		//
		// Kept deliberately simple compared to StartStep: no re-siting, no wall-bridge routing. The
		// layout cells were validated when the site was chosen, and an expansion that cannot complete
		// must never turn into the kind of stuck retry loop that once starved the main Rhythm.
		string expansionPendingType;
		CPos expansionPendingCell;
		int expansionPendingTicks;

		ProductionQueue ExpansionQueueFor(string actorType, Actor yardActor)
		{
			var ai = world.Map.Rules.Actors[actorType];
			return yardActor.TraitsImplementing<ProductionQueue>()
				.FirstOrDefault(q => Info.BuildingQueues.Contains(q.Info.Type) && q.CanBuild(ai));
		}

		void TickExpansion(IBot bot)
		{
			if (expansionYard == null || expansionSteps.Count == 0)
				return;

			var yardActor = world.Actors.FirstOrDefault(a => a.Owner == player && !a.IsDead && a.IsInWorld
				&& a.Location == expansionYard.Value
				&& planner != null && planner.Info.ConstructionYardTypes.Contains(a.Info.Name));

			// Yard gone: the mission notices too and finishes, this just stops us building into a hole.
			if (yardActor == null)
				return;

			// Anything already standing on its planned cell counts as done -- same idea as the main
			// Rhythm's rebuild scan, and it makes the driver idempotent across save/reload.
			foreach (var s in expansionSteps)
				if (!s.Done && world.ActorMap.GetActorsAt(s.TopLeft).Any(a => a.Owner == player && s.Variants.Contains(a.Info.Name)))
					s.Done = true;

			if (expansionPendingType != null)
			{
				expansionPendingTicks++;
				var pendingQueue = ExpansionQueueFor(expansionPendingType, yardActor);
				var item = pendingQueue?.AllQueued().FirstOrDefault(i => i.Item == expansionPendingType && i.Done);
				if (item != null)
				{
					var ai = world.Map.Rules.Actors[expansionPendingType];
					var orderName = ai.HasTraitInfo<LineBuildInfo>() ? "LineBuild" : "PlaceBuilding";
					bot.QueueOrder(new Order(orderName, player.PlayerActor, Target.FromCell(world, expansionPendingCell), false)
					{
						TargetString = expansionPendingType,
						ExtraData = pendingQueue.Actor.ActorID,
						SuppressVisualFeedback = true
					});

					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Expansion placed {expansionPendingType} at {expansionPendingCell}");
					expansionPendingType = null;
					return;
				}

				// Give up on a single item rather than blocking the whole expansion behind it.
				if (expansionPendingTicks >= Info.ExpansionStepTimeoutTicks)
				{
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Expansion step {expansionPendingType} timed out -> skipped");
					var stuck = expansionSteps.FirstOrDefault(s => !s.Done && s.Variants.Contains(expansionPendingType) && s.TopLeft == expansionPendingCell);
					if (stuck != null)
						stuck.Skipped = true;

					expansionPendingType = null;
				}

				return;
			}

			var step = ChooseExpansionStep();
			if (step == null)
				return;

			foreach (var v in step.Variants)
			{
				var q = ExpansionQueueFor(v, yardActor);
				if (q == null)
					continue;

				bot.QueueOrder(Order.StartProduction(q.Actor, v, 1));
				expansionPendingType = v;
				expansionPendingCell = step.TopLeft;
				expansionPendingTicks = 0;
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Expansion building {v} for {step.Role} at {step.TopLeft}");
				return;
			}
		}

		AotPlanStep BuildNavalStep(bool logImmediately)
		{
			if (Info.NavalPenTypes.Length == 0 || intel == null || !intel.Ready)
				return null;

			var (site, anchor) = FindNavalSite();
			if (site == null)
			{
				if (logImmediately)
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Naval site planning: NO coastal site found near the base -- naval production will never be available if requested");
				else if (++navalWaitLog % 8 == 0)
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Naval production requested but no coastal site found near the base yet");

				return null;
			}

			Log.Write("debug", logImmediately
				? $"[AotBuild][{player.InternalName}/{player.PlayerName}] Naval site planned -> {site.Value} (proactive, not yet requested)"
				: $"[AotBuild][{player.InternalName}/{player.PlayerName}] Naval production requested -> site {site.Value}");
			return new AotPlanStep
			{
				Kind = AotStepKind.Building, Role = "NAVAL", Variants = Info.NavalPenTypes,
				TopLeft = site.Value,

				// The chain must end at the exact land cell this site was validated against.
				BridgeTarget = anchor
			};
		}

		// Anchor on the nearest coastal cell reachable by land from the base (same concept as a wave's
		// ferry embark point), then spiral out from there for the first cell the actor's actual footprint
		// can occupy (the anchor itself is a 1x1 shoreline cell, rarely a match for a multi-tile building).
		// Reach (buildable area) is NOT checked here -- that is what TryBridgeStep is for.
		// How far from a bridge WALL this actor may still be placed. ^Building carries both
		// RequiresBuildableArea (AreaTypes: building, Adjacent 2) and RequiresBuildableArea@OUTPOST
		// (AreaTypes: outpost, Adjacent 6); a wall only ever grants `building`, so only matching
		// AreaTypes count -- taking the largest across all instances claims a reach the chain cannot
		// actually deliver.
		int BuildableReachFor(ActorInfo ai)
		{
			var wallAreas = world.Map.Rules.Actors[Info.WallType]
				.TraitInfos<GivesBuildableAreaInfo>()
				.SelectMany(g => g.AreaTypes)
				.ToHashSet();

			return ai.TraitInfos<RequiresBuildableAreaInfo>()
				.Where(r => r.AreaTypes.Any(wallAreas.Contains))
				.Select(r => r.Adjacent)
				.DefaultIfEmpty(2)
				.Min();
		}

		// Cells no wall can ever occupy because a structure stands there. Own buildings count too --
		// they are already buildable-area seeds, so the flood does not need to pass through them.
		// Only FOREIGN structures are permanent obstacles. Counting our own too was a serious mistake:
		// the base is ringed by its own fences, and WallReach floods outward from our buildings -- so the
		// flood was trapped inside our own perimeter and never reached the coast. Whether it worked at
		// all then depended on a chance gap in the ring, which is exactly the "sometimes it builds a sub
		// pen, sometimes not" the user reported (2026-07-27). Our own buildings grant buildable area
		// anyway, so passing through them is legitimate; a neutral Ore Mine or an enemy structure is not.
		bool IsPermanentlyBlocked(CPos c) =>
			world.ActorMap.GetActorsAt(c).Any(a => !a.IsDead && a.Owner != player && a.Info.HasTraitInfo<BuildingInfo>());

		// Land cells a wall chain could EVER reach from our existing buildings, with the number of
		// wall segments needed. One flood fill replaces the old chain of single-point checks (nearest
		// coastal cell -> 6-cell spiral -> one BFS path -> one reach test): that chain gave up entirely
		// whenever its ONE anchor happened to be unusable, even when a perfectly good site existed
		// further along the same coast. Confirmed the hard way on real sea maps, where no pen was ever
		// built although water sits only 9-14 cells from every spawn (Polar Panic, Hammerfest).
		//
		// Deliberately terrain-based, NOT CanPlaceBuilding: a unit walking across a cell must not make
		// the coast look permanently unreachable. Transient blockers are handled by nudging when the
		// chain is actually built.
		Dictionary<CPos, int> WallReach()
		{
			var wai = world.Map.Rules.Actors[Info.WallType];
			var wbi = wai.TraitInfoOrDefault<BuildingInfo>();
			if (wbi == null)
				return null;

			var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			bool Usable(CPos c) =>
				world.Map.Contains(c)
				&& (resourceLayer == null || resourceLayer.GetResource(c).Type == null)
				&& wbi.TerrainTypes.Contains(world.Map.GetTerrainInfo(c).Type)

				// A STRUCTURE on the cell is permanent -- no wall will ever go there. Terrain alone said
				// "reachable" and the chain then dead-ended at e.g. a neutral Ore Mine, one segment in,
				// forever (User 2026-07-25, Hammerfest). Mobile units are deliberately NOT checked here:
				// those move on, and nudging handles them when the chain is actually built.
				&& !IsPermanentlyBlocked(c);

			var reach = new Dictionary<CPos, int>();
			var q = new Queue<CPos>();

			foreach (var a in world.ActorsHavingTrait<Building>())
			{
				if (a.Owner != player || a.IsDead || !a.IsInWorld || !a.Info.HasTraitInfo<GivesBuildableAreaInfo>())
					continue;

				var abi = a.Info.TraitInfoOrDefault<BuildingInfo>();
				if (abi == null)
					continue;

				foreach (var t in abi.Tiles(a.Location))
					if (reach.TryAdd(t, 0))
						q.Enqueue(t);
			}

			while (q.Count > 0)
			{
				var c = q.Dequeue();
				var d = reach[c];
				if (d >= Info.MaxBridgeLength)
					continue;

				foreach (var dir in new[] { new CVec(1, 0), new CVec(-1, 0), new CVec(0, 1), new CVec(0, -1) })
				{
					var n = c + dir;
					if (reach.ContainsKey(n) || !Usable(n))
						continue;

					reach[n] = d + 1;
					q.Enqueue(n);
				}
			}

			return reach;
		}

		// Size of the connected water body containing `seed`, capped at `cap` (we only care whether it
		// is the open sea or a puddle). Used to avoid planning naval production on a pond that leads
		// nowhere.
		static readonly CVec[] Orthogonal = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

		bool IsWaterCell(CPos c)
		{
			if (!world.Map.Contains(c))
				return false;

			var t = world.Map.GetTerrainInfo(c).Type;
			return t == "Water" || t == "River";
		}

		// Water cells our GROUND troops could actually board from: they touch a coastal cell that is both
		// walkable and reachable over land from our own base.
		//
		// Without this a pen was happily planned on a stretch of water the army can never get to -- on
		// aot_hammerfest the site landed on the western lake, which is cut off from the base by a river,
		// so every crossing then failed with "no embark cell". Ships could sail there; soldiers could not
		// walk there (User 2026-07-27, who spotted exactly this).
		HashSet<CPos> EmbarkableWater(CPos centre, int radius)
		{
			var result = new HashSet<CPos>();
			if (intel == null)
				return result;

			for (var dy = -radius; dy <= radius; dy++)
				for (var dx = -radius; dx <= radius; dx++)
				{
					var c = centre + new CVec(dx, dy);
					if (!world.Map.Contains(c) || !intel.IsPassable(c) || !intel.IsReachable(c) || !intel.IsCoastal(c))
						continue;

					foreach (var d in Orthogonal)
						if (IsWaterCell(c + d))
							result.Add(c + d);
				}

			return result;
		}

		// Size of the connected water body containing `seed`, and whether that same body can be boarded
		// from friendly soil. One flood answers both; the result is cached for every cell it visits, so
		// all further candidates on the same water are free.
		//
		// The flood is deliberately NOT capped. An earlier 400-cell limit (meant only to bucket "sea vs
		// puddle") silently truncated the connectivity test: on a 6636-cell sea the search ran out of
		// budget long before reaching the boardable beach, so a perfectly good site was rejected as
		// unloadable and the planner picked one 23 wall segments away instead of 8 -- both on the SAME
		// water (User 2026-07-27: "es ist doch egal wo er das pen baut, die vessels koennten doch zum
		// einstiegsplatz fahren"). Connectivity must never be judged from a partial flood.
		(int Size, bool Loadable) WaterInfo(CPos seed, HashSet<CPos> embarkable,
			Dictionary<CPos, (int Size, bool Loadable)> cache)
		{
			if (cache.TryGetValue(seed, out var known))
				return known;

			if (!IsWaterCell(seed))
				return (0, false);

			var seen = new HashSet<CPos> { seed };
			var q = new Queue<CPos>();
			q.Enqueue(seed);
			var loadable = embarkable.Contains(seed);
			while (q.Count > 0)
			{
				var c = q.Dequeue();
				foreach (var dir in Orthogonal)
				{
					var n = c + dir;
					if (!IsWaterCell(n) || !seen.Add(n))
						continue;

					if (embarkable.Contains(n))
						loadable = true;

					q.Enqueue(n);
				}
			}

			var info = (seen.Count, loadable);
			foreach (var c in seen)
				cache[c] = info;

			return info;
		}

		// Best site for the naval production building: every placeable footprint in range is scored,
		// instead of committing to a single anchor. A site counts as reachable when some footprint tile
		// lies within the building's own buildable-area reach of a cell the wall chain can get to.
		(CPos? Site, CPos? Anchor) FindNavalSite()
		{
			var variant = Info.NavalPenTypes.FirstOrDefault(v => world.Map.Rules.Actors.ContainsKey(v));
			if (variant == null)
				return (null, null);

			var ai = world.Map.Rules.Actors[variant];
			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return (null, null);

			var adjacent = BuildableReachFor(ai);
			var reach = WallReach();
			if (reach == null || reach.Count == 0)
			{
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Naval site: no wall-reachable land at all (no own buildings?)");
				return (null, null);
			}

			// Water that actually leads to the enemy. A pen on a landlocked pond is useless for the one
			// job it exists for -- confirmed in-game: the site landed on a "medium" body the ferry could
			// never use, so every wave reported "no landing cell on our ships' water" and launched with
			// no target at all.
			HashSet<CPos> enemyWater = null;
			if (Info.NavalLocomotor != null && intel.EnemySpawns.Count > 0)
			{
				var enemyRef = intel.EnemySpawns.MinBy(sp => (sp - intel.BaseCentre).LengthSquared);
				var enemyShore = intel.FindCoastalCellNear(enemyRef, 24, requireOwnReachable: false, Info.NavalLocomotor);
				if (enemyShore != null)
					enemyWater = intel.NavalWaterFrom(enemyShore.Value, Info.NavalLocomotor);
			}

			// Everything the wall chain plus the building's own reach can cover.
			var radius = Info.MaxBridgeLength + adjacent + 2;
			var centre = intel.BaseCentre;

			// Rank: furthest offshore first (a pen hugging the shore seals the water beside it into an
			// inlet no ship can enter), then the shortest wall chain, then closest to base. Water-body
			// size is a PREFERENCE, never a filter -- a small sea must still be usable if it is all
			// there is (a hard filter here is exactly how earlier attempts blocked naval production
			// entirely).
			// Which water can our army actually board from? A pen we cannot load at is worthless, so this
			// outranks everything else.
			var embarkable = EmbarkableWater(centre, radius);
			var waterCache = new Dictionary<CPos, (int Size, bool Loadable)>();

			CPos? best = null;
			CPos? bestAnchor = null;
			var bestKey = (Loadable: -1, Reaches: -1, Body: -1, Offshore: -1, Steps: int.MaxValue, Dist: int.MaxValue);
			var placeable = 0;
			var candidates = new List<CPos>();

			for (var dy = -radius; dy <= radius; dy++)
				for (var dx = -radius; dx <= radius; dx++)
				{
					var c = centre + new CVec(dx, dy);
					if (!world.Map.Contains(c) || !world.CanPlaceBuilding(c, ai, bi, null))
						continue;

					placeable++;
					candidates.Add(c);

					// Closest wall-reachable cell to any footprint tile.
					var offshore = int.MaxValue;
					var steps = int.MaxValue;
					CPos? anchor = null;
					foreach (var t in bi.Tiles(c))
						for (var oy = -adjacent; oy <= adjacent; oy++)
							for (var ox = -adjacent; ox <= adjacent; ox++)
							{
								var land = t + new CVec(ox, oy);
								if (!reach.TryGetValue(land, out var st))
									continue;

								var gap = Math.Max(Math.Abs(ox), Math.Abs(oy));
								if (gap < offshore || (gap == offshore && st < steps))
								{
									offshore = gap;
									steps = st;
									anchor = land;
								}
							}

					if (offshore == int.MaxValue)
						continue;

					// Does this site sit on the water that leads to the enemy?
					var reaches = enemyWater == null ? 1
						: bi.Tiles(c).Any(enemyWater.Contains) ? 1 : 0;

					var probe = bi.Tiles(c).First();
					var (body, loadable) = WaterInfo(probe, embarkable, waterCache);

					// Bucket the body size so a marginally bigger puddle cannot outrank a much better
					// placement on the same sea.
					var bodyBucket = body >= 400 ? 2 : body >= 60 ? 1 : 0;
					var dist = (c - centre).LengthSquared;
					// Cheap first, far out second. Ranking offshore ABOVE chain length picked a site that
					// needed 23 wall segments just to gain one more cell of clearance (Hammerfest) --
					// absurdly expensive and right at MaxBridgeLength, so it never completed. Staying off
					// the shore matters (it stops the pen sealing a one-cell inlet), but only as a
					// tiebreaker among sites that are similarly cheap to connect.
					// Reaching the enemy outranks everything: that is the only reason this building exists.
					// Then cheap to connect, then as far off the shore as that allows.
					var key = (loadable ? 1 : 0, reaches, bodyBucket, -steps, offshore, -dist);
					var cur = (bestKey.Loadable, bestKey.Reaches, bestKey.Body, -bestKey.Steps, bestKey.Offshore, -bestKey.Dist);
					if (best == null || key.CompareTo(cur) > 0)
					{
						best = c;
						bestAnchor = anchor;
						bestKey = (loadable ? 1 : 0, reaches, bodyBucket, offshore, steps, dist);
					}
				}

			if (best == null)
			{
				// Say BY HOW MUCH we missed. A large gap means the shore strip itself is out of bounds for
				// the chain -- walls need Clear/Road, so a wide Beach apron puts the water permanently out
				// of reach and no amount of bridging will help. That is a map/rules matter, not a bug, and
				// the number is what tells the two apart.
				var nearestMiss = int.MaxValue;
				foreach (var c in candidates)
					foreach (var r in reach.Keys)
						nearestMiss = Math.Min(nearestMiss, Math.Max(Math.Abs(r.X - c.X), Math.Abs(r.Y - c.Y)));

				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Naval site: none usable -- {placeable} placeable footprint(s) within {radius} of {centre}, " +
					$"wall chain reaches {reach.Count} land cell(s) (max {Info.MaxBridgeLength} segments), building reach {adjacent}, " +
					$"closest site is {(nearestMiss == int.MaxValue ? "n/a" : nearestMiss.ToString())} cell(s) from reachable land " +
					$"(needs <= {adjacent})");
				return (null, null);
			}

			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Naval site {best.Value}: anchor={bestAnchor}, {bestKey.Offshore} cell(s) offshore (max {adjacent}), " +
				$"{bestKey.Steps} wall segment(s) from base, water body {bestKey.Body switch { 2 => "sea", 1 => "medium", _ => "small" }}, " +
				$"reachesEnemy={bestKey.Reaches == 1}, troopsCanBoard={bestKey.Loadable == 1}, " +
				$"{placeable} candidate(s) considered");
			return (best, bestAnchor);
		}

		ProductionQueue QueueFor(string actorType)
		{
			var ai = world.Map.Rules.Actors[actorType];
			return AIUtils.FindQueuesByCategory(player)
				.Where(g => Info.BuildingQueues.Contains(g.Key))
				.SelectMany(g => g)
				.FirstOrDefault(q => q.CanBuild(ai));
		}

		// First variant of the step the player can produce RIGHT NOW (correct actor for the current age).
		(string Type, ProductionQueue Queue) BuildableVariant(AotPlanStep step)
		{
			foreach (var v in step.Variants)
			{
				if (!world.Map.Rules.Actors.ContainsKey(v))
					continue;

				var q = QueueFor(v);
				if (q != null)
					return (v, q);
			}

			return (null, null);
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled || planner == null)
				return;

			if (Info.Faction != null && player.Faction.InternalName != Info.Faction)
				return;

			if (--ticks > 0)
				return;

			ticks = Info.Interval;

			planner.EnsurePlanned();
			if (!planner.Planned)
				return;

			if (--rebuildTicks <= 0)
			{
				rebuildTicks = Info.RebuildScanInterval / Info.Interval;
				RebuildScan();
				PruneStrayFenceSegments(bot);
			}

			// Priority-gate bookkeeping (see PriorityCoreRoles). Each counter only accumulates while
			// its OWN gate is actually holding something back, and resets the moment it clears, so the
			// timeout measures one continuous stall rather than total elapsed match time.
			coreGateTicks = CorePriorityPending() ? coreGateTicks + Info.Interval : 0;
			gatekeeperGateTicks = GatekeeperPriorityPending() ? gatekeeperGateTicks + Info.Interval : 0;

			// upgradeGateTicks measures ONE continuous stall (resets whenever the chain clears);
			// upgradeGateSpentTicks is the never-reset lifetime budget. Both advance only while the gate
			// is genuinely holding upgrades back: a hold that the cash override is already waving through
			// costs no budget, so the "rich right now" case can never exhaust the backups meant for the
			// "actually stuck" case.
			if (!upgradeGateReleased && UpgradeHoldRequested()
				&& (playerResources == null || playerResources.GetCashAndResources() < Info.PriorityGateCashOverride))
			{
				upgradeGateTicks += Info.Interval;
				upgradeGateSpentTicks += Info.Interval;
			}
			else
				upgradeGateTicks = 0;

			if (ExpansionPausesArmy())
			{
				expansionArmyPauseTicks += Info.Interval;
				if (expansionArmyPauseTicks >= Info.ExpansionPausesArmyBudgetTicks)
				{
					expansionArmyPauseRetired = true;
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Expansion no longer pauses army production " +
						$"-- budget spent ({expansionArmyPauseTicks} ticks); units take priority again");
				}
			}

			if (RefineryPausesArmy())
			{
				refineryArmyPauseTicks += Info.Interval;
				if (refineryArmyPauseTicks >= Info.RefineryPausesArmyBudgetTicks)
				{
					refineryArmyPauseRetired = true;
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Age-1 Refinery no longer pauses army production " +
						$"-- budget spent ({refineryArmyPauseTicks} ticks); units take priority again");
				}
			}

			// Independent of `pending`/the Rhythm chooser entirely -- this fires its own StartProduction
			// order directly and never touches the single build-in-flight slot.
			TryFireGatekeeperUpgrades(bot);

			// Plan the site ONCE, eagerly, the moment map intel is ready -- regardless of whether anything
			// has actually asked for naval production yet, and regardless of whatever else is currently
			// mid-build (`pending`). Must run BEFORE the `pending != null` early-return below: during normal
			// play a build step is in flight almost every single tick, back-to-back, for minutes on end --
			// gating this behind "pending == null" meant it effectively never got a turn (confirmed
			// 2026-07-22: no "Naval site planned" line ever appeared despite reaching deep into Age 0). This
			// is pure reconnaissance (logs success/failure immediately), never touches `pending` itself.
			if (!navalPlanAttempted && intel != null && intel.Ready)
			{
				navalPlanAttempted = true;
				navalStep = BuildNavalStep(logImmediately: true);
			}

			// Expansion runs on the expansion yard's OWN queue, so it ticks BEFORE (and independently
			// of) the main base's single pending slot -- that is the whole point of the second yard
			// having its own Building.* queue. Putting it after the `pending` early-return would have
			// made the expansion wait for every main-base building instead.
			TickExpansion(bot);

			if (pending != null)
			{
				TickPending(bot);
				return;
			}

			// Cashflow priority for the very first Derrick squad (User 2026-07-31: "sobald barracks
			// steht, direkt bei engineer-squad sein und erst danach weitere gebäude gebaut werden ...
			// mit timeout, falls das nicht klappt"). Only withholds NEW construction (a build already
			// in flight above already had its cash spent and keeps going) -- bounded by
			// AotDerrickMission.StillFormingWithinTimeout's own DerrickFormingTimeout, so a genuinely
			// stuck squad (no reachable derrick at all) can never block base construction forever.
			//
			// MUST NOT fire while the Age-1 Refinery is the pending step: that one pauses Ops' army
			// production in the other direction, so the two gates would wait on each other forever --
			// the squad can't be built (production paused), so the pause never lifts, so the Refinery
			// that would lift the production pause never gets built. Observed in-game 2026-07-31: base
			// construction stopped dead the moment the barracks finished and never resumed. The
			// Refinery always wins that tie -- it is itself the economy step both sides depend on.
			// Widened from Age1RefineryPending() to EconomyPriorityPending() (2026-08-02) so the tie-break
			// covers the expansion pause too: that pause ALSO stops Ops from producing, so without this
			// the squad could never be built, FirstDerrickSquadPending() would stay true forever, and base
			// construction would sit withheld waiting on a squad that production is not allowed to make --
			// the exact deadlock shape the Refinery guard was added for. Economy pause always wins.
			if (ops != null && ops.Info.Faction == Info.Faction && !EconomyPriorityPending() && ops.FirstDerrickSquadPending())
				return;

			// On-demand naval production takes priority over the Rhythm: an Operations mission is blocked
			// waiting for it. Re-evaluated every tick against HasNavalProduction(), so a destroyed pen is
			// picked back up automatically (navalStep is dropped once satisfied, forcing a fresh site
			// search -- the old building's site may no longer be valid, e.g. terrain changed). Only THIS
			// branch ever actually queues construction (StartStep) -- the eager plan above never builds.
			if (navalRequested)
			{
				if (HasNavalProduction())
					navalStep = null;
				else
				{
					navalStep ??= BuildNavalStep(logImmediately: false);
					if (navalStep != null)
					{
						StartStep(bot, navalStep);
						if (pending != null)
							return;
					}
				}
			}

			// Core (economy/tech) and Defence (SAM/FTUR/GUN/OBELISK + gate fences) run through TWO fully
			// independent choosers now (user spec 2026-07-31), not one shared strict queue. Before this,
			// a single unplaceable defence site (a SAM squeezed tight against a block that later got
			// encroached, say) sat as the Rhythm's one "current" step forever -- and since ChooseStep was
			// strictly first-open, EVERY core building queued behind it (the next Refinery, the Age's
			// remaining Rhythm, potentially an Age-upgrade prerequisite) never got built either. Core still
			// gets first refusal on the shared production slot each tick (only one build can be in flight
			// at a time regardless), but its own progression no longer depends in any way on defence ever
			// resolving -- and defence gets its own timeout precisely because it no longer can hold core
			// hostage by being retried forever (see StartStep).
			var coreStep = ChooseStep(defense: false);
			if (coreStep != null)
			{
				StartStep(bot, coreStep);
				if (pending != null)
					return;
			}

			var defenseStep = ChooseStep(defense: true);
			if (defenseStep != null)
				StartStep(bot, defenseStep);
		}

		// Destroyed plan buildings reopen their steps; the rhythm order then rebuilds them first.
		//
		// Checks the actor's own recorded Location, NOT ActorMap occupancy at step.TopLeft: some
		// footprints (FIX is "_+_ +++ _+_" -- a plus shape) leave their own registered top-left corner
		// cell OUTSIDE the actual footprint, so ActorMap never reports an actor there even while the
		// building is alive right next to it. That false "not alive" reopened the step on every scan,
		// permanently blocking StartStep (the target cell is unbuildable -- the real building already
		// occupies its neighbours) and, since ChooseStep is strict first-open-step, starved every later
		// step in the Rhythm forever.
		void RebuildScan()
		{
			// Periodic watchdog, independent of any specific step firing (user-fund 2026-08-01, "4th
			// turret" screenshot): if something OUTSIDE this Rhythm is producing GUN/FTUR, the Soll/Ist
			// log at the "Place" order alone would never catch it -- this catches it here instead,
			// loudly, on the same cadence RebuildScan already runs at, regardless of source.
			var plannedGun = planner.Rhythm.Count(s => s.Role == "GUN");
			var plannedFtur = planner.Rhythm.Count(s => s.Role == "FTUR");
			var liveGun = world.Actors.Count(a => a.Owner == player && !a.IsDead && a.IsInWorld && planner.Info.GunTypes.Contains(a.Info.Name));
			var liveFtur = world.Actors.Count(a => a.Owner == player && !a.IsDead && a.IsInWorld && planner.Info.FturTypes.Contains(a.Info.Name));
			if (liveGun > plannedGun || liveFtur > plannedFtur)
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] WARNING: extra gate-defence turret(s) outside the Rhythm -- " +
					$"GUN {liveGun}/{plannedGun} planned, FTUR {liveFtur}/{plannedFtur} planned");

			foreach (var step in planner.Rhythm)
			{
				// Skipped (defence-timeout) steps never got built in the first place -- there is nothing
				// to "lose", so re-checking them here would just find no building at TopLeft and flip
				// Done back to false every scan, forever retrying the exact site the timeout just gave up
				// on. Skipped is permanent by design (see AotPlanStep.Skipped).
				if (!step.Done || step.Skipped || step.Kind != AotStepKind.Building)
					continue;

				// An FTUR that took the Turret upgrade is NOT gone -- AotTransformOnPrerequisite rebuilds
				// it IN PLACE into a Gun Turret on the very same cell (aot-structures.yaml, FTUR:
				// "IntoActor"), so the actor standing there simply has a different name than the step's
				// own Variants. Matching Variants alone therefore reported the upgraded turret as
				// "destroyed" every single scan: the step reopened, converted itself to GUN (below), found
				// its target cell occupied by the very turret it was looking for, re-sited -- and built a
				// FOURTH turret beside the finished 3-turret cluster. Confirmed 2026-08-02 across every
				// Cabal base in one session (user screenshots + "Re-sited GUN: 35,150 -> 35,151" one cell
				// over, immediately after "converting slot to GUN"). An already-upgraded FTUR slot counts
				// as alive, and the step is converted permanently so later scans compare against the right
				// actor list from the start.
				var accept = step.Role == "FTUR" && step.Defense
					? step.Variants.Concat(planner.Info.GunTypes).ToArray()
					: step.Variants;

				var standing = world.ActorsHavingTrait<Building>()
					.FirstOrDefault(a => a.Owner == player && !a.IsDead && a.Location == step.TopLeft && accept.Contains(a.Info.Name));

				if (standing != null && step.Role == "FTUR" && step.Defense && planner.Info.GunTypes.Contains(standing.Info.Name))
				{
					step.Role = "GUN";
					step.Variants = planner.Info.GunTypes;
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] FTUR at {step.TopLeft} was upgraded in place " +
						"to a Gun Turret -> slot now tracked as GUN (no rebuild needed)");
				}

				if (standing == null)
				{
					// A destroyed FTUR whose OWN variants have since become permanently unbuildable (the
					// Turret upgrade removes FTUR from the build menu for good, aot-structures.yaml:
					// ~!aot-turret-upgrade) would otherwise reopen as an FTUR step that can NEVER be
					// satisfied again -- the Defence timeout would eventually skip it, leaving a
					// permanent hole in the gate rather than what the SAME upgrade branch actually
					// intends to stand there: a Gun Turret (user question 2026-08-01: "ist
					// sichergestellt, dass nach Upgrade der GUN als Ersatz fuer zerstoerte FTUR gebaut
					// wird" -- it was not, until this). BuildableVariant still sees the OLD (FTUR)
					// Variants here, so a false-negative from a merely transient gate is not possible --
					// this step already built successfully once, so the only realistic reason its own
					// variants would stop being offered now is the one-way Turret upgrade.
					if (step.Role == "FTUR" && step.Defense && planner.Info.GunTypes.Length > 0
						&& BuildableVariant(step).Type == null)
					{
						step.Role = "GUN";
						step.Variants = planner.Info.GunTypes;
						Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Destroyed FTUR at {step.TopLeft} " +
							"can no longer be rebuilt (Turret upgrade taken) -> converting slot to GUN");
					}

					step.Done = false;
					step.StuckTicks = 0;
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Plan building {step.Role} at {step.TopLeft} lost -> rebuild");
				}
			}
		}

		// LineBuild (used for fences, see TickPending) auto-connects a placed node to the NEAREST own
		// wall segment of the same actor type, regardless of which ring it belongs to. When two fences
		// (e.g. YardFence and PowerFence) end up close together, the engine can silently splice a
		// connector segment between the two independent rings. Each fence step's FencePerimeter is the
		// full set of cells its OWN ring is allowed to occupy; any owned wall actor outside the union of
		// every fence's perimeter is such a stray inter-ring segment (or leftover from a sold/relocated
		// ring) and gets sold.
		void PruneStrayFenceSegments(IBot bot)
		{
			if (Info.WallType == null)
				return;

			var allowed = new HashSet<CPos>();
			foreach (var step in planner.Rhythm)
				if (step.Kind == AotStepKind.Fence)
					foreach (var c in step.FencePerimeter)
						allowed.Add(c);

			if (allowed.Count == 0)
				return;

			var stray = world.ActorsHavingTrait<Building>()
				.Where(a => a.Owner == player && !a.IsDead && a.Info.Name == Info.WallType
					&& !bridgeWallCells.Contains(a.Location) && !allowed.Contains(a.Location))
				.ToList();

			foreach (var a in stray)
			{
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Selling stray fence segment at {a.Location} (outside every ring's perimeter)");
				bot.QueueOrder(new Order("Sell", a, Target.FromActor(a), false));
			}
		}

		// True once EVERY planned Rhythm step (core AND defence) for the given Age tier is Done. A
		// timed-out defence step already counts as Done (Skipped is set alongside it), so this can
		// never hang on an unplaceable SAM -- only a permanently-stuck CORE step (which never times
		// out, by design) can keep it false forever. Kept public though nothing consumes it since the
		// age jump moved to AotAgePowerBotModule: it is the natural gate should that module ever want
		// "do not jump age while this tier is still half-built" too.
		// A Silo still owed at the current tier. The Age fund asks before it starts hoarding: the Silo
		// is exempt from the sprint's build hold, but exemption is worthless if the fund has already
		// taken every credit that could pay for it -- which is why the Silo was going up AFTER the
		// upgrade instead of before it (User 2026-08-06).
		// Is a step with this role at the current tier still outstanding? Needed because the opening
		// sequence alternates between the CORE and DEFENCE queues, which run in parallel -- list order
		// alone enforces nothing across them.
		public bool RolePending(string role)
		{
			if (planner == null || !planner.Planned)
				return false;

			var tier = AgeTier();
			return planner.Rhythm.Any(s => !s.Done && !s.Skipped && s.Role == role && s.Age <= tier);
		}

		public bool SiloPending()
		{
			if (planner == null || !planner.Planned)
				return false;

			var tier = AgeTier();
			return planner.Rhythm.Any(s => !s.Done && !s.Skipped && s.Role == "SILO" && s.Age <= tier);
		}

		public bool AgeRhythmComplete(int age) => planner.Rhythm.Where(s => s.Age == age).All(s => s.Done);

		// --- priority gates: airfield -> refinery -> sams -> upgrades (user spec 2026-08-02) ---

		// A core economy step (Info.PriorityCoreRoles) of the CURRENT age tier is still outstanding.
		// Age-gated so the Age-2 Refinery cannot retroactively re-block Age-1's defence once it is
		// planned but not yet reachable.
		bool CorePriorityPending()
		{
			if (Info.PriorityCoreRoles.Length == 0)
				return false;

			var tier = AgeTier();
			return planner.Rhythm.Any(s => !s.Done && !s.Defense && s.Age <= tier
				&& s.Kind == AotStepKind.Building && Info.PriorityCoreRoles.Contains(s.Role));
		}

		// Any defence step of the current age tier still outstanding. Skipped steps count as resolved
		// (they already gave up permanently), so a genuinely unplaceable SAM cannot hold this open.
		bool DefencePriorityPending()
		{
			var tier = AgeTier();
			return planner.Rhythm.Any(s => !s.Done && !s.Skipped && s.Defense && s.Age <= tier);
		}

		// A gatekeeper upgrade is offered by its queue right now, i.e. its own prerequisites are met but
		// we do not own it yet (BuildLimit 1 takes it out of the queue for good once bought). This is
		// what slots the gatekeepers INTO the priority chain rather than beside it (user spec
		// 2026-08-02: "airfield -> NOD TIBERIUM SECRETS -> refinery -> sams"): NOD Tiberium Secrets
		// only becomes offered once the Airfield stands, and the Refinery behind it is a core step, so
		// pausing core while this is true produces exactly that order without hardcoding any of it.
		bool GatekeeperPriorityPending() => NextGatekeeperIndex() >= 0;

		// Index of the first gatekeeper that is offered-but-unowned, or -1. Deliberately the FIRST
		// only: the list is in dependency order, so firing them strictly one at a time keeps a later
		// gatekeeper from competing for cash with the one currently blocking the rhythm.
		int NextGatekeeperIndex()
		{
			for (var i = 0; i < Info.GatekeeperUpgradeTypes.Length; i++)
			{
				if (string.IsNullOrEmpty(Info.GatekeeperUpgradeTypes[i]))
					continue;

				if (FindAgeUpgradeQueue(Info.GatekeeperUpgradeTypes[i]).Queue != null)
					return i;
			}

			return -1;
		}

		// The base expansion outranks the remaining upgrades but nothing else (user spec 2026-08-02:
		// "BASE EXPANSION (wenn möglich, darf kein showstopper sein) -> dann restliche upgrades").
		// Nothing waits ON the expansion except upgrade spending, and even that is bounded twice: each
		// expansion step has its own ExpansionStepTimeoutTicks, and the upgrade gate has its own.
		// The expansion counts as pending from the moment the MISSION exists, not from the moment its
		// yard is standing. ChooseExpansionStep only knows about steps, and the steps are registered
		// by RequestExpansionLayout -- which runs when the MCV has already deployed. So through the
		// entire saving-and-driving phase, the phase that actually needs the money, this returned
		// false and the upgrade gate let purchases through: the bots bought the 900-credit transport
		// gun upgrade while their expansion was still short of its 6000 (User 2026-08-05, and against
		// the standing rule that upgrades come last in Age 1).
		bool ExpansionPriorityPending() =>
			(ops != null && ops.Info.Faction == Info.Faction && ops.ExpansionHoldsPriority())
			|| ChooseExpansionStep() != null;

		// Asked by BaseBuilderBotModule@aotupgrades before it spends anything: upgrades are the LAST
		// tier of the priority chain, behind both the core economy roles and the defence steps of the
		// current age. Returns false for a player this module is not actually driving (disabled,
		// wrong faction, no plan yet) so the generic module keeps its unmodified behaviour there.
		public bool RhythmPriorityActive()
		{
			// EVERY "no" is logged from here on. Twice now this gate was reported as fixed and twice it
			// went on letting upgrades through -- with a Tech Centre still under construction, which
			// UpgradeHoldRequested plainly covers. Guessing at the reason from the outside has cost two
			// test runs, so the gate states it itself (User 2026-08-10).
			string open = null;

			if (IsTraitDisabled)
				open = "trait disabled";
			else if (planner == null || !planner.Planned)
				open = "no plan yet";
			else if (Info.Faction != null && player.Faction.InternalName != Info.Faction)
				open = $"wrong faction ({player.Faction.InternalName} vs {Info.Faction})";
			else if (upgradeGateReleased)
				open = "gate retired earlier";
			else if (!UpgradeHoldRequested())
				open = "nothing pending: no core role, no gatekeeper, no defence, no expansion, "
					+ $"no age saving, age {AgeTier()} rhythm complete";

			if (open != null)
			{
				if (++gateOpenLog % 32 == 1)
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Upgrade gate OPEN -- {open}");

				return false;
			}

			// Backup #1 -- ONE continuous stall ran too long: something in the chain is genuinely stuck
			// (an unbuildable Airfield, an expansion that never gets going, a defence step that neither
			// completes nor skips). Latches rather than merely returning false: without the latch the
			// counter would reset the very next tick and the gate would re-engage immediately, leaking
			// only a single tick of upgrade time per timeout period.
			if (upgradeGateTicks >= Info.PriorityGateTimeoutTicks)
			{
				upgradeGateReleased = true;
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Upgrade gate retired -- one continuous hold " +
					$"exceeded {Info.PriorityGateTimeoutTicks} ticks (rhythm appears stuck); upgrades now run unrestricted");
				return false;
			}

			// Backup #2 -- lifetime budget spent. Catches the flicker case the per-stall timeout above
			// cannot see: a gate that keeps clearing and re-engaging never accumulates one long stall,
			// but can still starve upgrades for the entire match.
			if (upgradeGateSpentTicks >= Info.PriorityGateTotalBudgetTicks)
			{
				upgradeGateReleased = true;
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Upgrade gate retired -- total budget spent " +
					$"({upgradeGateSpentTicks} ticks held across the match); upgrades now run unrestricted");
				return false;
			}

			// Backup #3 -- plenty of cash, so the cashflow argument for holding upgrades back does not
			// apply right now. Deliberately NOT latched: if the AI spends back down, prioritising again
			// is the correct behaviour, and this costs no budget either (see the counters in BotTick).
			if (playerResources != null && playerResources.GetCashAndResources() >= Info.PriorityGateCashOverride)
				return false;

			return true;
		}

		// The raw question, without any of the backup releases: is something ahead of upgrades in the
		// priority chain still outstanding?
		bool UpgradeHoldRequested() =>
			CorePriorityPending() || GatekeeperPriorityPending()
			|| DefencePriorityPending() || ExpansionPriorityPending()
			|| AgeSavingPending() || CurrentAgeIncomplete() || AgeProgrammeIncomplete();

		// Nothing at all until the building programme up to UpgradesAfterAge is finished. Age 0's own
		// rhythm going quiet is not enough: an upgrade bought there competes directly with the Age
		// upgrade the bot is saving for.
		bool AgeProgrammeIncomplete()
		{
			if (AgeTier() < Info.UpgradesAfterAge)
				return true;

			return planner.Rhythm.Any(s => !s.Done && !s.Skipped
				&& s.Age <= Info.UpgradesAfterAge && s.Kind == AotStepKind.Building);
		}

		// Saving for the next Age outranks any upgrade. Nothing else covered this: the sprint holds
		// the Aot builder, but the generic upgrades module is a separate spender and only consults
		// this gate.
		bool AgeSavingPending() =>
			ops != null && ops.Info.Faction == Info.Faction && ops.AgeSprintActive();

		// "Upgrades come LAST" in full, not just behind two named roles. PriorityCoreRoles is
		// ["AFLD", "PROC"] and both are AGE-1 steps, while CorePriorityPending filters on
		// `s.Age <= tier` -- so through the whole of Age 0 neither can ever be pending, the gate
		// opened as soon as Age 0's own rhythm was done, and upgrades were bought while the bot was
		// supposed to be saving for its Age upgrade. That is exactly the report: upgrades running
		// with no Airfield standing, because the Airfield could not count yet (User 2026-08-10).
		//
		// Any outstanding step of the CURRENT age holds instead. Skipped steps count as resolved, and
		// the gate's existing stall timeout and lifetime budget still retire it if the rhythm wedges,
		// so this cannot starve upgrades for good.
		bool CurrentAgeIncomplete()
		{
			var tier = AgeTier();
			return planner.Rhythm.Any(s => !s.Done && !s.Skipped && s.Age <= tier
				&& s.Kind == AotStepKind.Building);
		}

		// See AotBasePlannerBotModule.IsInsideGateCluster for why this needs to exist at all.
		public bool IsInsideGateCluster(CPos c) => planner != null && planner.IsInsideGateCluster(c);

		// Specifically the Age-1 Refinery, not the whole tier's Rhythm (unlike AgeRhythmComplete): user
		// spec 2026-07-31, after observing the AI build its Age-1 Airfield but never the Refinery behind
		// it, because AotOperationsBotModule's own unit production kept competing for the same cash. A
		// missing step (Place() never found room, or this isn't Nod) is vacuously "not pending" -- there
		// is nothing to wait on. Age 1 only, by design: the OPTIONAL second Refinery in Age 2 is a nice-
		// to-have that army production must never be made to wait on.
		public bool Age1RefineryPending()
		{
			var step = planner.Rhythm.FirstOrDefault(s => s.Age == 1 && s.Role == "PROC");
			if (step == null || step.Done)
				return false;

			// ONLY while the Refinery is genuinely the step being worked on right now, i.e. it is the
			// first still-open step in Rhythm order (exactly what ChooseStep picks). Without this the
			// method returned true from tick 0 of the match -- an Age-1 step is obviously not done during
			// Age 0 -- which paused AotOperationsBotModule's ENTIRE army production for the whole of Age 0
			// and beyond. Since Ops owns all combat unit production exclusively (UnitBuilderBotModule was
			// stripped of it 2026-07-22), that meant literally zero units all match: confirmed in-game
			// 2026-07-31 ("barracks werden gebaut aber ich sehe keine infanterie") and in the log, where
			// `produce` never appeared once across a full session. The pause is meant to win one specific
			// cash race, not to gate the early game.
			return planner.Rhythm.FirstOrDefault(s => !s.Done) == step;
		}

		// What Operations asks before spending on army units. Age 1 is the cashflow-optimisation phase
		// (user spec 2026-08-02): the Refinery AND the base expansion both outrank new units there,
		// because both of them are what pays for every unit after them. Everything else -- SAMs,
		// upgrades, later ages -- never touches army production; those are handled by the build-side
		// gates only, exactly because starving unit production is far more dangerous than delaying a
		// turret.
		public bool EconomyPriorityPending()
		{
			if (IsTraitDisabled || planner == null || !planner.Planned)
				return false;

			if (Info.Faction != null && player.Faction.InternalName != Info.Faction)
				return false;

			return RefineryPausesArmy() || ExpansionPausesArmy();
		}

		// Age-1 Refinery pause, budget-bounded and one-shot -- see RefineryPausesArmyBudgetTicks.
		bool RefineryPausesArmy() => !refineryArmyPauseRetired && Age1RefineryPending();

		// Age-1 expansion pause, budget-bounded and one-shot -- see ExpansionPausesArmyBudgetTicks.
		bool ExpansionPausesArmy() =>
			!expansionArmyPauseRetired && AgeTier() <= 1 && ExpansionPriorityPending();

		// Mirrors AotOperationsBotModule.FirstBuildable: is this SPECIFIC actor currently offered by any
		// of its own declared queue categories, right now (Prerequisites/BuildLimit/etc. all already
		// factored in by BuildableItems()). Deliberately NOT QueueFor()/Info.BuildingQueues -- the
		// age-upgrade actors live on Age.Nod, a category this module's own Rhythm queues never touch.
		(string Type, ProductionQueue Queue) FindAgeUpgradeQueue(string actorType)
		{
			if (!world.Map.Rules.Actors.TryGetValue(actorType, out var ai))
				return (null, null);

			var bi = ai.TraitInfoOrDefault<BuildableInfo>();
			if (bi == null)
				return (null, null);

			var queuesByCategory = AIUtils.FindQueuesByCategory(player);
			foreach (var category in bi.Queue)
				foreach (var queue in queuesByCategory[category])
					if (queue.BuildableItems().Any(i => i.Name == actorType))
						return (actorType, queue);

			return (null, null);
		}

		// The Age upgrades used to be fired from here too (an age-ordered AgeUpgradeTypes list gated on
		// AgeRhythmComplete). The techtree rebuild of 2026-08-04 turned the age jump into a Super Power on
		// the player actor -- the aot-ageN-upgrade-* actors lost their Buildable, so this path could never
		// find a queue again and had become silently dead code. AotAgePowerBotModule owns the age jump now.

		// Techtree gatekeepers (user spec 2026-08-02). Unlike the Age upgrades these get NO Rhythm gate:
		// the buildings they unlock ARE Rhythm steps (Shrine, and through it the Obelisk), so waiting for
		// the Rhythm to complete first would wait on the very step the upgrade unblocks. Fired in list
		// order the moment each is offered by its own queue; same single-shot latch as the Age upgrades,
		// for the same reason (a paused queue item survives a temporary cash shortfall on its own).
		void TryFireGatekeeperUpgrades(IBot bot)
		{
			// Strictly one at a time, in dependency order (see NextGatekeeperIndex): the rhythm is
			// paused on exactly this upgrade, so letting a later one queue up beside it would split the
			// cash the pause was meant to concentrate.
			var i = NextGatekeeperIndex();
			if (i < 0 || i >= gatekeeperFired.Length || gatekeeperFired[i])
				return;

			var (name, queue) = FindAgeUpgradeQueue(Info.GatekeeperUpgradeTypes[i]);
			if (name == null || queue == null)
				return;

			bot.QueueOrder(Order.StartProduction(queue.Actor, name, 1));
			gatekeeperFired[i] = true;
			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Gatekeeper upgrade ({name}) fired directly " +
				"-- rhythm holds until it is bought");
		}

		// Two fully independent strict-first-open queues over the SAME Rhythm list, split by
		// AotPlanStep.Defense (see BotTick for why). Core (defense: false) keeps its original,
		// unmodified semantics -- including the power-emergency pull-forward, which only ever matches
		// NUKE/NUK2 core steps anyway. Defence (defense: true) is otherwise identical (still strictly
		// first-open within its own sub-sequence -- the SAM/GUN staging order in BuildRhythm still
		// matters) but its steps are the only ones StartStep will ever time out and skip.
		//
		// Defence ALSO filters on AotPlanStep.Age <= AgeTier() -- core steps never needed this, since
		// AFLD/PROC/the age-upgrades etc. all carry their own age-tier Buildable.Prerequisites. SAM in
		// particular has none at all (just "anypower, aot-nod-radar"), so before this filter the
		// independent Defence queue raced straight through every Age's SAM sites while still in Age 0
		// (confirmed 2026-08-01: nearly all 11 SAM sites built before the base ever left Age 0, burning
		// exactly the early cash this whole rework was meant to protect). Before the Core/Defence split
		// this was never an issue -- sharing ONE strict list meant an Age-1 SAM simply could not be
		// reached until everything positioned ahead of it (all of Age 0) was Done, purely from list
		// position; splitting Defence onto its own chooser removed that implicit brake without
		// replacing it, until now.
		AotPlanStep ChooseStep(bool defense)
		{
			// Defence stands down entirely while the age's core economy roles (Airfield, Refinery) are
			// still outstanding -- user spec 2026-08-02, "airfield -> refinery -> sams". Before this,
			// reaching Age 1 fired the Airfield, five SAM sites and the upgrades module simultaneously
			// and the income covered none of them properly. Bounded by PriorityGateTimeoutTicks so a
			// permanently unbuildable core step cannot leave the base undefended for good.
			if (defense && CorePriorityPending() && coreGateTicks < Info.PriorityGateTimeoutTicks)
			{
				if (++waitLog % 32 == 0)
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Defence on hold -- core economy first " +
						$"({string.Join("/", Info.PriorityCoreRoles)} still open, {coreGateTicks} ticks)");

				return null;
			}

			// CORE itself stands down while a gatekeeper upgrade is offered but unbought. That is what
			// puts NOD Tiberium Secrets BETWEEN the Airfield and the Refinery instead of alongside
			// them: the upgrade only becomes offered once the Airfield stands, and the Refinery is the
			// next core step behind it. Defence is already held by the pending Refinery above, so the
			// whole chain falls out of these two gates without any per-role wiring.
			if (!defense && GatekeeperPriorityPending() && gatekeeperGateTicks < Info.PriorityGateTimeoutTicks)
			{
				if (++waitLog % 32 == 0)
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Core on hold -- gatekeeper upgrade first " +
						$"({Info.GatekeeperUpgradeTypes[NextGatekeeperIndex()]}, {gatekeeperGateTicks} ticks)");

				return null;
			}

			// CORE is age-filtered too, not just Defence (User-Fund 2026-08-05: "ich habe GESEHEN, dass
			// ein spieler in age 1 eine zweite refinery hatte"). The assumption above -- that core steps
			// gate themselves through their own Buildable.Prerequisites -- holds only where a step's
			// variants differ per age. The OPTIONAL second Refinery is the counter-example: both PROC
			// steps resolve through RoleVariants("PROC") to the very same actor list, so the Age-2 step
			// was perfectly buildable in Age 1 and the strict chooser walked straight into it as soon as
			// Age 1's own steps were done. The Age tag was decorative for core steps; now it binds.
			//
			// Safe against stalling the rhythm because the ages are contiguous blocks in Rhythm order:
			// filtering removes a whole trailing block, never a hole that lets a later step jump the
			// queue. The buildings that UNLOCK the next age sit in the age below by construction (STEC
			// at the end of Age 0 opens Age 1), so the chain cannot gate itself out.
			var ageTier = AgeTier();
			var open = planner.Rhythm.Where(s => !s.Done && s.Defense == defense && s.Age <= ageTier).ToList();
			if (open.Count == 0)
				return null;

			if (!defense && playerPower != null && playerPower.ExcessPower < Info.MinimumExcessPower)
			{
				var power = open.FirstOrDefault(s => s.Kind == AotStepKind.Building && (s.Role == "NUKE" || s.Role == "NUK2")
					&& BuildableVariant(s).Type != null);
				if (power != null)
					return power;
			}

			// Strict rhythm: the first open step. If its actors are age-gated (nothing buildable yet) we
			// WAIT — that is the age boundary, and the parallel upgrade module is saving toward it.
			return open[0];
		}

		// Is a refinery something this player could actually start right now? Mirrors BuildableVariant's
		// own test, but for the PROC role specifically and without needing a step object.
		bool RefineryBuildable()
		{
			if (planner == null)
				return false;

			foreach (var v in planner.Info.ProcTypes)
				if (world.Map.Rules.Actors.TryGetValue(v, out var ai) && QueueFor(v)?.BuildableItems().Any(b => b.Name == ai.Name) == true)
					return true;

			return false;
		}

		int expansionPauseLog;
		int gateOpenLog;
		int ageSprintPauseLog;
		int sequenceWaitLog;

		void StartStep(IBot bot, AotPlanStep step)
		{
			// ECONOMY EMERGENCY (User 2026-08-04: "alle prio in oreT bis mind. eine refinery inkl.
			// harvester steht"). With no ore transporter and no working refinery the bot has no income
			// worth the name, and every credit spent on anything else delays the one purchase that
			// restores it. Construction therefore stands down too -- Ops and the age fund already do.
			//
			// The REFINERY is the deliberate exception: it is the second way out of exactly this hole,
			// and in testing a bot rescued itself precisely that way, funding one from a derrick's
			// trickle. Blocking it would have removed the escape route this rule exists to protect.
			// ...and only once the refinery is genuinely BUILDABLE. It needs prerequisites of its own
			// (for Nod: airstrip, silo, the Secret Tech upgrade), so blocking every other step while
			// those are still missing would deadlock the very escape route above -- the refinery could
			// then never be reached (User 2026-08-04: "und für die voraussetzungen, die er für die
			// refinery braucht"). While it is not yet buildable the Rhythm runs normally and works
			// towards exactly those prerequisites; the hold bites the moment the refinery itself is
			// the thing that could be bought.
			if (ops != null && ops.Info.Faction == Info.Faction && ops.EconomyEmergency()
				&& step.Role != "PROC" && !step.Role.StartsWith("EXP_", StringComparison.Ordinal)
				&& RefineryBuildable())
			{
				if (++economyPauseLog % 16 == 0)
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Economy emergency: holding {step.Role} until the economy is back");

				return;
			}

			// AGE SPRINT (User 2026-08-05: "wenn das age-0 upgrade angestossen wurde"). Once the Tech
			// Centre stands and the bot has committed to buying its next Age, construction stops too --
			// not just the attack waves. Everything positioned after STEC (Repair Facility, Helipad,
			// the SAMs, the second Silo) is therefore built during the RESEARCH window instead, which
			// is dead time for the Age fund anyway. That is the human pattern being copied: put the
			// buildings up, build a wave's worth of units, then stop and take the rest to the price.
			//
			// PROC stays exempt: cutting income is never the way to reach a price faster.
			// SILO is exempt alongside PROC (user spec 2026-08-06): it sits directly behind the Tech
			// Centre in the plan and is the last thing built before the stop, because the credits being
			// saved need somewhere to sit.
			if (ops != null && ops.Info.Faction == Info.Faction && ops.AgeSprintActive()
				&& step.Role != "PROC" && step.Role != "SILO")
			{
				if (++ageSprintPauseLog % 16 == 0)
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Age sprint: holding {step.Role} until the upgrade is bought");

				return;
			}

			// THE OPENING SEQUENCE (user spec 2026-08-06):
			//
			//   Age upgrade -> wave 1 -> Flame Turret -> Repair Facility -> wave 2 -> Helipad
			//   -> SAMs -> wave 3
			//
			// Every link is spelled out here because LIST ORDER ALONE ENFORCES NOTHING across this
			// sequence: the Flame Turret and the SAMs are DEFENCE steps and the Repair Facility and
			// Helipad are CORE ones, and the two queues run in parallel. Left to the list, the Repair
			// Facility was finished before the first wave had even formed.
			if (ops != null && ops.Info.Faction == Info.Faction && step.Age == 0)
			{
				var waitingFor = step.Role switch
				{
					// FTUR is deliberately NOT in here: it sits ahead of the Tech Centre in the plan
					// (user spec 2026-08-06, "sonst ist er anfangs schnell nackt am eingang"), so
					// making it wait for a wave that waits for the upgrade would mean it never got
					// built early at all, which is the whole point of its position.
					"FIX" => ops.WavesScheduled < 1 ? "the first wave" : null,
					"HPAD" => ops.WavesScheduled < 2 ? "the second wave" : null,
					"SAM" => ops.WavesScheduled < 2 ? "the second wave"
						: RolePending("HPAD") ? "the Helipad" : null,
					_ => null,
				};

				if (waitingFor != null)
				{
					if (++sequenceWaitLog % 16 == 0)
						Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] {step.Role} held until {waitingFor} is done");

					return;
				}
			}

			// EXPANSION PRIORITY (User 2026-08-05: "expansion nach refinery absolute bevorzugung").
			// Once the refinery stands, founding the second base outranks everything else the bot could
			// build. Attack waves, engineer raids and the Age fund already stand down for it; the base
			// builder was the last hole in that bucket, and the biggest -- it kept spending the very
			// credits the convoy was saving for, so the expansion never reached its threshold.
			//
			// PROC and the expansion's own EXP_* steps stay exempt: a second refinery is income, and
			// blocking the expansion layout would deadlock the thing being prioritised. The hold ends
			// by itself when the yard deploys, and ExpansionHoldsPriority is bounded by
			// ExpansionPriorityTimeout, so an expansion that never works out cannot freeze the base.
			if (ops != null && ops.Info.Faction == Info.Faction && ops.ExpansionHoldsPriority()
				&& step.Role != "PROC" && !step.Role.StartsWith("EXP_", StringComparison.Ordinal))
			{
				if (++expansionPauseLog % 16 == 0)
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Expansion has priority: holding {step.Role}");

				return;
			}

			var (type, queue) = BuildableVariant(step);
			if (type == null)
			{
				if (++waitLog % 8 == 0)
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Waiting (age gate / prerequisites): {step.Role}");

				return;
			}

			var cell = ResolveCell(step, type);
			if (cell == null)
			{
				// Own idle units standing on the site block CanPlaceBuilding same as for a plain Building --
				// ask them to step aside regardless of step kind (this used to be Building-only, silently
				// leaving a unit parked on a turret's target cell forever unresolved).
				NudgeBlockers(type, step.TopLeft);

				if (step.Kind == AotStepKind.Building)
				{
					// PERMANENT blocker (resource grew there, or a static foreign actor sits on it — a
					// blossom tree is not Mobile and will never be nudged away). Re-site the single
					// building nearby instead of deadlocking the whole plan; this ALSO clears the reason
					// bridging gave up (CanPlaceBuilding was false at the target), so re-try bridging next.
					if (PermanentlyBlocked(type, step.TopLeft) && TryResite(step, type))
					{
						step.StuckTicks = 0;
						return;
					}
				}

				// Out of buildable-area reach? Bridge along the actual path. Transient blockers are asked
				// to step aside (own units on the site), then we wait.
				if (OutOfReachOnly(step, type) && TryBridgeStep(bot, step))
				{
					step.StuckTicks = 0;
					return;
				}

				// Both recovery attempts (re-site, bridge) failed to make progress THIS attempt -- for a
				// core step that is simply "wait, try again next tick", exactly as before: a core step
				// must never be silently abandoned, since a later Age upgrade or another core step may
				// depend on it (user spec 2026-07-31). A DEFENCE step, on the other hand, now has its own
				// timeout: it already cannot block core (separate chooser, see BotTick), but without a
				// timeout it would still sit here retrying forever and never free up its OWN queue for
				// the next defence site behind it.
				if (step.Defense)
				{
					step.StuckTicks += Info.Interval;
					if (step.StuckTicks >= Info.DefenseStepTimeoutTicks)
					{
						step.Done = true;
						step.Skipped = true;
						Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Defence step {step.Role} at {step.TopLeft} unplaceable " +
							$"after {step.StuckTicks} ticks (re-site and bridge both failed) -> skipped permanently");
						return;
					}
				}

				if (++waitLog % 8 == 0)
				{
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Waiting (target blocked): {step.Role} at {step.TopLeft}");
					DiagnoseBlock(step, type);
				}

				return;
			}

			step.StuckTicks = 0;
			pending = step;
			pendingType = type;
			pendingCell = cell.Value;
			pendingIsBridgeWall = false;
			pendingWaitLog = 0;
			bot.QueueOrder(Order.StartProduction(queue.Actor, type, 1));
			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Start {step.Role} ({type}) -> {pendingCell}");
		}

		CPos? ResolveCell(AotPlanStep step, string type)
		{
			var ai = world.Map.Rules.Actors[type];
			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return null;

			switch (step.Kind)
			{
				case AotStepKind.Building:
					return world.CanPlaceBuilding(step.TopLeft, ai, bi, null)
						&& bi.IsCloseEnoughToBase(world, player, ai, step.TopLeft)
						? step.TopLeft
						: null;

				case AotStepKind.Turret:
				{
					// Near the gate, keeping spacing to already-built turrets of the same role.
					var same = world.ActorsHavingTrait<Building>()
						.Where(a => a.Owner == player && !a.IsDead && step.Variants.Contains(a.Info.Name))
						.Select(a => a.Location)
						.ToList();
					for (var r = 0; r <= 5; r++)
						for (var dx = -r; dx <= r; dx++)
							for (var dy = -r; dy <= r; dy++)
							{
								if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
									continue;

								var c = step.TopLeft + new CVec(dx, dy);
								if (!world.CanPlaceBuilding(c, ai, bi, null)
									|| !bi.IsCloseEnoughToBase(world, player, ai, c))
									continue;

								if (same.Any(s => Math.Max(Math.Abs(s.X - c.X), Math.Abs(s.Y - c.Y)) < Info.TurretSpacing))
									continue;

								return c;
							}

					return null;
				}

				case AotStepKind.Fence:
					return NextFenceCell(step, ai, bi);

				default:
					return null;
			}
		}

		// Own units standing on the footprint are asked to step aside (the engine's own nudge routine:
		// NotifyBlocker -> INotifyBlockingMove -> idle friendly Mobile units queue a Nudge activity).
		void NudgeBlockers(string type, CPos cell)
		{
			var ai = world.Map.Rules.Actors[type];
			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return;

			var notifier = world.ActorsHavingTrait<Building>()
				.FirstOrDefault(a => a.Owner == player && !a.IsDead && a.IsInWorld);
			if (notifier == null)
				return;

			var blockers = bi.Tiles(cell)
				.SelectMany(world.ActorMap.GetActorsAt)
				.Where(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<MobileInfo>())
				.Distinct()
				.ToList();
			if (blockers.Count == 0)
				return;

			notifier.NotifyBlocker(blockers);
			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Nudging {blockers.Count} own unit(s) off {type} site at {cell}");
		}

		// Prints every fact needed to conclusively identify why a step's target is stuck, instead of
		// guessing: CanPlaceBuilding/IsCloseEnoughToBase results, per-tile terrain/resource/actors, whether
		// the target is still inside the frozen Pocket, and the bridge frontier's own diagnosis.
		void DiagnoseBlock(AotPlanStep step, string type)
		{
			var ai = world.Map.Rules.Actors[type];
			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return;

			var target = step.TopLeft;
			var canPlace = world.CanPlaceBuilding(target, ai, bi, null);
			var closeEnough = bi.IsCloseEnoughToBase(world, player, ai, target);
			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Diag {step.Role}@{target}: CanPlaceBuilding={canPlace} IsCloseEnoughToBase={closeEnough} inPocket={planner.Pocket.Contains(target)}");

			foreach (var t in bi.Tiles(target))
			{
				var terrain = world.Map.GetTerrainInfo(t).Type;
				var res = world.WorldActor.TraitOrDefault<IResourceLayer>()?.GetResource(t).Type;
				var actors = world.ActorMap.GetActorsAt(t).Select(a => $"{a.Info.Name}(owner={a.Owner.ResolvedPlayerName},mobile={a.Info.HasTraitInfo<MobileInfo>()})").ToList();
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Diag tile {t}: terrain={terrain} resource={res ?? "none"} inPocket={planner.Pocket.Contains(t)} actors=[{string.Join(",", actors)}]");
			}

			diagBridge = true;
			var frontier = BridgeFrontier(target);
			diagBridge = false;
			var ownCount = world.ActorsHavingTrait<Building>().Count(a => a.Owner == player && !a.IsDead);
			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Diag bridge: ownBuildings={ownCount} frontier={(frontier.HasValue ? frontier.Value.ToString() : "NULL")}");
		}

		// PERMANENT block: something on the footprint that NudgeBlockers cannot clear — either a resource
		// (tiberium grew there; CanPlaceBuilding doesn't even check the resource layer, so a building could
		// otherwise be dropped ON TOP of it) or a foreign, non-owned actor (a blossom tree/SPLIT2 etc. is
		// STATIC — no MobileInfo — so it never queues a Nudge activity and blocks CanPlaceBuilding forever).
		bool PermanentlyBlocked(string type, CPos cell)
		{
			var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			var bi = world.Map.Rules.Actors[type].TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return false;

			foreach (var t in bi.Tiles(cell))
			{
				if (resourceLayer != null && resourceLayer.GetResource(t).Type != null)
					return true;

				if (world.ActorMap.GetActorsAt(t).Any(a => a.Owner != player))
					return true;

				// An own STATIC actor (a wall or another building) sitting on the target is just as
				// permanent as a foreign one: NudgeBlockers only moves idle Mobile units out of the way, so
				// a wall segment left over there (e.g. a fence ring that ended up overlapping a plan step's
				// footprint -- confirmed in-game: a fence's own wall sat directly on a Refinery's planned
				// site, CanPlaceBuilding=False forever, nothing else in the Rhythm after it ever got a
				// turn) would otherwise wait here permanently with no recovery path at all.
				if (world.ActorMap.GetActorsAt(t).Any(a => a.Owner == player && a.TraitOrDefault<Mobile>() == null))
					return true;
			}

			return false;
		}

		// Move a single plan building to the nearest valid cell (spiral, r <= 8): placeable-or-out-of-reach
		// (a bridge handles reach), clear of resources, and not colliding with any OTHER planned footprint
		// (plus a 1-cell lane). The step's TopLeft is updated permanently — the plan adapts, once, locally.
		bool TryResite(AotPlanStep step, string type)
		{
			var ai = world.Map.Rules.Actors[type];
			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			if (bi == null)
				return false;

			// Cells claimed by every other plan step (footprints + 1-cell margin) and fence nodes.
			var claimed = new HashSet<CPos>();
			foreach (var other in planner.Rhythm)
			{
				if (other == step)
					continue;

				if (other.Kind == AotStepKind.Building && other.Variants.Length > 0
					&& world.Map.Rules.Actors.TryGetValue(other.Variants[0], out var oai))
				{
					var obi = oai.TraitInfoOrDefault<BuildingInfo>();
					if (obi == null)
						continue;

					foreach (var t in obi.Tiles(other.TopLeft))
						for (var dx = -1; dx <= 1; dx++)
							for (var dy = -1; dy <= 1; dy++)
								claimed.Add(t + new CVec(dx, dy));
				}
				else if (other.Kind == AotStepKind.Fence)
					foreach (var n in other.FenceNodes)
						claimed.Add(n);
			}

			for (var r = 1; r <= 8; r++)
				for (var dx = -r; dx <= r; dx++)
					for (var dy = -r; dy <= r; dy++)
					{
						if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
							continue;

						var c = step.TopLeft + new CVec(dx, dy);
						var tiles = bi.Tiles(c).ToList();

						// Gate-defence buildings (FTUR/GUN/SAM near the choke) are legitimately sited
						// just outside Pocket by design (AotBasePlannerBotModule: "the validated
						// defenceChokepoint can legitimately sit just outside Pocket"). Requiring full
						// Pocket containment here made every resite attempt for exactly those buildings
						// fail near-guaranteed -- confirmed 2026-08-02 (user report: destroyed gate FTUR/
						// GUN never rebuilt, log showed dozens of "Re-site FAILED" with the ORIGINAL site
						// itself already inPocket=False). Non-defence steps keep the strict check; the r<=8
						// ring search already bounds how far from the original site a defence building can
						// drift, so relaxing Pocket containment for them doesn't risk wandering off.
						if (tiles.Any(t => claimed.Contains(t) || (!step.Defense && !planner.Pocket.Contains(t))))
							continue;

						if (resourceLayer != null && tiles.Any(t => resourceLayer.GetResource(t).Type != null))
							continue;

						// Currently resource-free is not the same as safe -- a spot right next to a still-
						// alive growth source (blossom tree) is very likely to be overgrown again shortly
						// after, triggering another PermanentlyBlocked/resite cycle for the same reason.
						// Re-scans LIVE actors here (not the one-time Plan()-time growthHazard snapshot,
						// which only covers trees that already existed when the match started) so a tree
						// that appeared or spread since then is accounted for too.
						if (planner.Info.GrowthSourceTypes.Count > 0 && world.ActorsHavingTrait<Building>()
							.Any(a => !a.IsDead && planner.Info.GrowthSourceTypes.Contains(a.Info.Name)
								&& Math.Max(Math.Abs(a.Location.X - c.X), Math.Abs(a.Location.Y - c.Y)) <= planner.Info.GrowthSourceMargin))
							continue;

						if (!world.CanPlaceBuilding(c, ai, bi, null))
							continue;

						Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Re-sited {step.Role}: {step.TopLeft} -> {c} (resources grew onto plan)");
						step.TopLeft = c;
						return true;
					}

			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Re-site FAILED for {step.Role} at {step.TopLeft}");
			return false;
		}

		// True when the step's target fails ONLY the buildable-area check (bridge it), not occupancy.
		bool OutOfReachOnly(AotPlanStep step, string type)
		{
			var ai = world.Map.Rules.Actors[type];
			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return false;

			var target = step.Kind == AotStepKind.Turret ? step.TopLeft
				: step.Kind == AotStepKind.Building ? step.TopLeft
				: fenceQueue.Count > 0 ? fenceQueue.Peek() : step.FenceNodes.FirstOrDefault();

			if (step.Kind == AotStepKind.Building)
				return world.CanPlaceBuilding(target, ai, bi, null) && !bi.IsCloseEnoughToBase(world, player, ai, target);

			// Turret/fence targets: bridge when nothing near the target is inside the area yet.
			return !bi.IsCloseEnoughToBase(world, player, ai, target);
		}

		// ---------------------------------------------------------------- wall bridge (path-based)

		bool TryBridgeStep(IBot bot, AotPlanStep step)
		{
			if (Info.WallType == null || bridgeWallCells.Count >= Info.MaxBridgeLength)
				return false;

			var wallQueue = QueueFor(Info.WallType);
			if (wallQueue == null)
				return false;

			// A step with a BridgeTarget (the naval pen) is bridged to THAT land cell, not to the site
			// itself: the site was validated as "within building reach of this anchor", so the chain has
			// to actually arrive there. Aiming at the water site let the chain stop at a different shore
			// cell and fall one tile short forever (build/sell loop, confirmed in-game 2026-07-27).
			var target = step.Kind == AotStepKind.Fence && fenceQueue.Count > 0 ? fenceQueue.Peek()
				: step.BridgeTarget ?? step.TopLeft;

			// A target on water (the naval pen) can never be walled up to; that last stretch is covered
			// by the building's own buildable-area reach, so exempt it from the wall-terrain constraint.
			var stepInfo = step.Variants
				.Select(v => world.Map.Rules.Actors.TryGetValue(v, out var vi) ? vi : null)
				.FirstOrDefault(vi => vi != null);
			// Bridging to a land anchor needs no water exemption -- that is a plain land route.
			var freeRadius = step.BridgeTarget != null ? 0 : stepInfo != null ? BuildableReachFor(stepInfo) : 0;
			var frontier = BridgeFrontier(target, freeRadius, includeTarget: step.BridgeTarget != null);
			if (frontier == null)
				return false;

			pending = step;
			pendingType = Info.WallType;
			pendingCell = frontier.Value;
			pendingIsBridgeWall = true;
			pendingWaitLog = 0;
			bot.QueueOrder(Order.StartProduction(wallQueue.Actor, Info.WallType, 1));
			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Bridge wall #{bridgeWallCells.Count + 1} -> {pendingCell} (toward {target} for {step.Role})");
			return true;
		}

		// Furthest wall-placeable + in-area cell on the actual BFS PATH (within the pocket) from the
		// nearest own building to the target — a straight line breaks on rocks; the path never does.
		// Path of cells from one of our own buildings out to `target`, or null if no route exists.
		// Shared by BridgeFrontier (what is buildable right now) and BridgeReachEnd (how far the
		// chain can ultimately get).
		List<CPos> BridgePath(CPos target, int freeRadius = 0)
		{
			var wai = world.Map.Rules.Actors[Info.WallType];
			var wbi = wai.TraitInfoOrDefault<BuildingInfo>();
			if (wbi == null)
				return null;

			var own = world.ActorsHavingTrait<Building>()
				.Where(a => a.Owner == player && !a.IsDead)
				.SelectMany(a =>
				{
					var bi = a.Info.TraitInfoOrDefault<BuildingInfo>();
					return bi != null ? bi.Tiles(a.Location) : [];
				})
				.ToHashSet();
			if (own.Count == 0)
				return null;

			// A resource (tiberium/ore) OVERRIDES the cell's reported terrain type to its own type
			// (ResourceLayer.cs sets Map.CustomTerrain), and walls' TerrainTypes is "Clear, Road" only —
			// NO building can EVER stand on a resource cell, growth or no growth. Route AROUND resource
			// fields, not through them: excluded from the BFS just like an impassable wall, so the search
			// naturally finds the buildable perimeter instead of beelining into a field and dead-ending.
			var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();
			bool ResourceFree(CPos c) => resourceLayer == null || resourceLayer.GetResource(c).Type == null;

			bool WallTerrain(CPos c) => world.Map.Contains(c)
				&& wbi.TerrainTypes.Contains(world.Map.GetTerrainInfo(c).Type);

			// BFS from the target back to any own cell, constrained to the pocket (plus own cells) and
			// clear of resources (own cells are always allowed through — they're already built there).
			//
			// The defence chokepoint is deliberately allowed to sit just OUTSIDE the Pocket: DetectGates
			// (which bounds the Pocket) blocks a small patch around every gate specifically so the base
			// PACKER doesn't spill past it -- but that is exactly the neighbourhood a defence turret needs
			// to stand in. A target out there could never be bridged to (confirmed via debug.log: target
			// inPocket=False, frontier=NULL every time) since the BFS's neighbour filter rejected every
			// cell outside Pocket, including the target's own immediate surroundings. When the target
			// itself is outside the Pocket, also allow a bounded ring of non-Pocket cells around it --
			// capped at MaxBridgeLength (matches how far a bridge can ever actually reach anyway, so this
			// is never a wider licence to wander than the wall chain itself permits). Normal in-Pocket
			// targets (ordinary buildings/fences) are unaffected: their neighbourhood is already inside
			// Pocket, so this extra allowance never triggers for them. A coastal on-demand naval site
			// (user spec 2026-07-22) can sit considerably further out than a chokepoint turret, hence the
			// switch from a fixed 12-cell ring to the full bridge length.
			var pocket = planner.Pocket;
			var targetOutsidePocket = !pocket.Contains(target);
			var ringRadius = Info.MaxBridgeLength;
			bool InBounds(CPos c) => pocket.Contains(c) || (targetOutsidePocket && (c - target).LengthSquared <= ringRadius * ringRadius);

			var pred = new Dictionary<CPos, CPos>();
			var q = new Queue<CPos>();
			q.Enqueue(target);
			pred[target] = target;
			CPos? met = null;
			while (q.Count > 0 && met == null)
			{
				var c = q.Dequeue();
				foreach (var d in new[] { new CVec(1, 0), new CVec(-1, 0), new CVec(0, 1), new CVec(0, -1) })
				{
					var n = c + d;
					if (pred.ContainsKey(n))
						continue;

					if (own.Contains(n))
					{
						pred[n] = c;
						met = n;
						break;
					}

					if (!InBounds(n) || !ResourceFree(n) || IsPermanentlyBlocked(n))
						continue;

					// A wall chain can only follow terrain a wall may actually stand on. Without this the BFS
					// happily beelined across water and handed back a route the chain could never build along
					// (User 2026-07-24: "berücksichtigt nicht sauber buildable und wählt falschen Pfad").
					// Cells within freeRadius of the target are exempt: for a naval site sitting offshore that
					// last stretch is bridged by the building's own buildable-area reach, not by walls.
					if (!WallTerrain(n) && (n - target).LengthSquared > freeRadius * freeRadius)
						continue;

					pred[n] = c;
					q.Enqueue(n);
				}
			}

			if (met == null)
				return null;

			// Walk from our building toward the target; the frontier is the FURTHEST cell along the path
			// that is placeable and still inside the current buildable area.
			var path = new List<CPos>();
			var cur = met.Value;
			while (pred[cur] != cur)
			{
				path.Add(cur);
				cur = pred[cur];
			}

			return path;
		}

		// includeTarget: normally the target cell is the BUILDING site and must stay free, so the path
		// deliberately excludes it. For a naval step the target is the wall ANCHOR -- the chain has to
		// end ON it, otherwise it stops one cell short and the pen never enters buildable area
		// (confirmed 2026-07-27: chain parked at 30,15 with anchor 31,15, frontier NULL, naval step
		// silently abandoned while the rhythm carried on).
		CPos? BridgeFrontier(CPos target, int freeRadius = 0, bool includeTarget = false)
		{
			var wai = world.Map.Rules.Actors[Info.WallType];
			var wbi = wai.TraitInfoOrDefault<BuildingInfo>();
			if (wbi == null)
				return null;

			var path = BridgePath(target, freeRadius);
			if (path == null)
				return null;

			// Walk from our building toward the target; the frontier is the FURTHEST cell along the path
			// that is placeable and still inside the current buildable area.
			if (includeTarget)
				path.Add(target);

			CPos? best = null;
			foreach (var c in path)
			{
				if (!wbi.IsCloseEnoughToBase(world, player, wai, c))
					continue;

				if (world.CanPlaceBuilding(c, wai, wbi, null))
				{
					best = c;
					continue;
				}

				// In reach but not placeable — almost certainly an own idle unit wandering through the
				// base (terrain/resources were already ruled out for this area). Ask it to step aside so
				// a LATER bridge attempt (a few ticks on, once the Nudge activity resolves) can use this
				// cell — without this, units parked on the path deadlock the whole bridge forever.
				NudgeBlockers(Info.WallType, c);

				if (diagBridge)
				{
					var actors = world.ActorMap.GetActorsAt(c).Select(a => $"{a.Info.Name}(owner={a.Owner.ResolvedPlayerName},mobile={a.Info.HasTraitInfo<MobileInfo>()})");
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Diag frontier blocked cell {c}: actors=[{string.Join(",", actors)}]");
				}
			}

			if (best == null && diagBridge)
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Diag frontier: target={target} pathLen={path.Count} (nudged blockers along path, see above)");

			return best;
		}

		bool diagBridge;

		void SellBridge(IBot bot)
		{
			var sold = 0;
			foreach (var c in bridgeWallCells)
				foreach (var a in world.ActorMap.GetActorsAt(c).Where(a =>
					a.Owner == player && !a.IsDead && a.Info.Name == Info.WallType && a.TraitOrDefault<Sellable>() != null))
				{
					bot.QueueOrder(new Order("Sell", a, Target.FromActor(a), false));
					sold++;
				}

			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Sold wall bridge ({sold} segments)");
			bridgeWallCells.Clear();
			bridgeOwner = null;
		}

		// ---------------------------------------------------------------- fences

		CPos? NextFenceCell(AotPlanStep step, ActorInfo ai, BuildingInfo bi)
		{
			if (fenceStep != step)
			{
				fenceStep = step;
				fenceNodeWaits = 0;
				fenceQueue.Clear();
				foreach (var n in step.FenceNodes)
					fenceQueue.Enqueue(n);
			}

			var resourceLayer = world.WorldActor.TraitOrDefault<IResourceLayer>();

			while (fenceQueue.Count > 0)
			{
				var c = fenceQueue.Peek();
				if (world.CanPlaceBuilding(c, ai, bi, null) && bi.IsCloseEnoughToBase(world, player, ai, c))
				{
					fenceQueue.Dequeue();
					return c;
				}

				// Occupied by a building (own wall segment, own structure)? That node is covered — skip.
				var blockedByBuilding = bi.Tiles(c).Any(t => world.ActorMap.GetActorsAt(t).Any(a => a.TraitOrDefault<Building>() != null));

				// Grown over by Tiberium/Ore since the plan was made? A resource-covered cell can NEVER
				// become buildable on its own (nothing in this AI harvests a specific tile just to clear a
				// fence node, and resources don't retreat) -- waiting here is a PERMANENT deadlock, exactly
				// like the own-wall-blocks-a-building case, just for a fence node instead of a whole
				// building. Skip it too: the ring just has a gap where that one node would have connected,
				// same as skipping a building-blocked node.
				var blockedByResource = resourceLayer != null && bi.Tiles(c).Any(t => resourceLayer.GetResource(t).Type != null);

				if (blockedByBuilding || blockedByResource)
				{
					fenceQueue.Dequeue();
					fenceNodeWaits = 0;
					continue;
				}

				// Nothing permanent in the way -- almost always one of our own units parked on the node.
				// Ask it to step aside (the engine only nudges when something PATHS through a blocker,
				// and placing a building is not pathing, so nobody does this for us). If it still has
				// not cleared after a while, skip the node rather than stall the whole ring forever
				// (User 2026-07-24: fence completion sat blocked by waiting units and never resolved).
				NudgeBlockers(ai.Name, c);

				if (++fenceNodeWaits >= FenceNodeMaxWaits)
				{
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Fence node {c} still blocked after {fenceNodeWaits} tries -> skipping (ring keeps a gap)");
					fenceQueue.Dequeue();
					fenceNodeWaits = 0;
					continue;
				}

				return null;
			}

			// All nodes handled -> fence complete.
			step.Done = true;
			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Fence complete: {step.Role}");
			return null;
		}

		// ---------------------------------------------------------------- production/placement loop

		void TickPending(IBot bot)
		{
			var queue = QueueFor(pendingType);
			if (queue == null)
			{
				pending = null;
				pendingIsBridgeWall = false;
				return;
			}

			var queued = queue.AllQueued().Where(i => i.Item == pendingType).ToList();
			if (queued.Count == 0)
			{
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Lost production of {pendingType}, retrying");
				bot.QueueOrder(Order.StartProduction(queue.Actor, pendingType, 1));
				return;
			}

			if (!queued.Any(i => i.Done))
			{
				// ProductionQueue.TickInner only ever ticks Queue[0] of the ENGINE's own list for this
				// queue instance -- AllQueued() exposes that whole list, not just our own item. Confirmed
				// 2026-07-22 (FTUR stall): our item sat with Started=False forever despite plenty of power/
				// cash, which only happens if it is NOT at index 0 -- i.e. something else already sitting
				// in that same queue is wedged (Done but never placed, or otherwise orphaned) and silently
				// blocks everything queued behind it for the rest of the match, with zero recovery path on
				// the engine side. allItems/ourIndex below identify the real blocker; a stall long past any
				// normal build time (100 waiting ticks * Interval(25) = 2500 ticks, well beyond FTUR's own
				// ~600-tick build) triggers cancelling every item ahead of ours so this module can re-request
				// cleanly next tick instead of waiting forever.
				var allItems = queue.AllQueued().ToList();
				var ourIndex = allItems.FindIndex(i => i.Item == pendingType);

				if (++pendingWaitLog % 8 == 0)
				{
					var item = queued.FirstOrDefault();

					// A second, DIFFERENT stall shape confirmed 2026-07-22 (FIX): item sits genuinely at
					// queuePos=0 (not blocked by anything else), Started=True, Paused=False, excessPower
					// positive -- yet remainingTime/remainingCost stop changing entirely. The only path in
					// ProductionItem.Tick() that returns WITHOUT decrementing RemainingTime once Started &&
					// !Done && !Paused && power is Normal is the cash branch: costThisFrame computed but
					// pr.TakeCash(costThisFrame, true) fails, i.e. GetCashAndResources() < costThisFrame.
					// excessPower alone never proves "nothing is blocking" -- log actual spendable cash too.
					Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Still building {pending.Role} ({pendingType}): " +
						$"started={item?.Started} paused={item?.Paused} remainingTime={item?.RemainingTime}/{item?.TotalTime} " +
						$"remainingCost={item?.RemainingCost} excessPower={playerPower?.ExcessPower} " +
						$"cash={playerResources?.Cash} ore={playerResources?.Resources} " +
						$"queuePos={ourIndex}/{allItems.Count} queueItems=[{string.Join(",", allItems.Select(i => $"{i.Item}(done={i.Done},started={i.Started})"))}]");
				}

				if (ourIndex > 0 && pendingWaitLog > 100)
				{
					for (var i = 0; i < ourIndex; i++)
					{
						Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Cancelling stuck queue blocker ahead of {pendingType}: {allItems[i].Item}");
						bot.QueueOrder(Order.CancelProduction(queue.Actor, allItems[i].Item, 1));
					}

					pendingWaitLog = 0;
				}

				return;
			}

			var ai = world.Map.Rules.Actors[pendingType];
			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			if (bi != null && !world.CanPlaceBuilding(pendingCell, ai, bi, null))
			{
				// Ask own units on the site to step aside before waiting.
				NudgeBlockers(pendingType, pendingCell);

				if (pendingIsBridgeWall)
					return;   // frontier re-evaluated next bridge step

				var cell = ResolveCell(pending, pendingType);
				if (cell == null)
					return;   // wait for the blocker to move

				pendingCell = cell.Value;
			}

			// Bridge walls are SINGLE segments (PlaceBuilding): LineBuild would auto-connect intermediate
			// segments we could not sell later. Fences use LineBuild so the ring closes between nodes.
			var isLineBuildable = ai.HasTraitInfo<LineBuildInfo>();
			var orderName = !pendingIsBridgeWall && isLineBuildable ? "LineBuild" : "PlaceBuilding";
			bot.QueueOrder(new Order(orderName, player.PlayerActor, Target.FromCell(world, pendingCell), false)
			{
				TargetString = pendingType,
				ExtraData = queue.Actor.ActorID,
				SuppressVisualFeedback = true
			});

			if (pendingIsBridgeWall)
			{
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Placed bridge wall at {pendingCell} ({bridgeWallCells.Count + 1} segments)");
				bridgeWallCells.Add(pendingCell);
				bridgeOwner = pending;
				pendingIsBridgeWall = false;
				pending = null;
				return;
			}

			Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Place {pending.Role} ({pendingType}) at {pendingCell}");

			// Soll/Ist tally for gate-defence turrets (user-fund 2026-08-01, "4th turret" screenshot):
			// PLANNED is however many GUN/FTUR Rhythm steps exist in total (main cluster + however many
			// secondary clusters got planned -- 2 GUN + 1 FTUR each, so this number is self-consistent
			// with cluster count without needing it separately). LIVE is what's actually standing right
			// now. If LIVE ever exceeds PLANNED, something outside this Rhythm is building turrets --
			// if it stays <= PLANNED, whatever looked like "one too many" is a second (secondary-
			// approach) cluster the plan always intended, not a bug.
			if (pending.Role is "GUN" or "FTUR")
			{
				var plannedGun = planner.Rhythm.Count(s => s.Role == "GUN");
				var plannedFtur = planner.Rhythm.Count(s => s.Role == "FTUR");
				var liveGun = world.Actors.Count(a => a.Owner == player && !a.IsDead && a.IsInWorld && planner.Info.GunTypes.Contains(a.Info.Name));
				var liveFtur = world.Actors.Count(a => a.Owner == player && !a.IsDead && a.IsInWorld && planner.Info.FturTypes.Contains(a.Info.Name));
				Log.Write("debug", $"[AotBuild][{player.InternalName}/{player.PlayerName}] Gate-defence tally: GUN {liveGun}/{plannedGun} planned, FTUR {liveFtur}/{plannedFtur} planned");
			}

			// Only tear down the chain that was built FOR this step.
			if (bridgeWallCells.Count > 0 && bridgeOwner == pending)
				SellBridge(bot);

			// Fence steps stay open until every node is placed (NextFenceCell marks them Done).
			if (pending.Kind != AotStepKind.Fence)
				pending.Done = true;

			pending = null;
		}
	}
}
