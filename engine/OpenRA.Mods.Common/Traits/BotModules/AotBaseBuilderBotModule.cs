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

		[Desc("Minimum Chebyshev spacing between the two gate turrets.")]
		public readonly int TurretSpacing = 3;

		[ActorReference]
		[Desc("Naval production building (Sub Pen / Shipyard). Built ON DEMAND, outside the fixed Rhythm --",
			"triggered by RequestNavalProduction(), called by any Operations mission that actually needs",
			"ships/subs/vessels (user spec 2026-07-22: naval production is an Operations concern, not a",
			"base-rhythm step). Age-ordered variants; the first buildable one is used. Reuses the same",
			"wall-bridge machinery as every other out-of-reach plan step to reach a coastal site.")]
		public readonly string[] NavalPenTypes = [];

		[Desc("Radius (cells) around the base centre searched for a coastal site for the on-demand naval",
			"building. Kept close to MaxBridgeLength so the wall-bridge machinery can actually reach it.")]
		public readonly int NavalSiteSearchRadius = 20;

		public override object Create(ActorInitializer init) { return new AotBaseBuilderBotModule(init.Self, this); }
	}

	public class AotBaseBuilderBotModule : ConditionalTrait<AotBaseBuilderBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;

		AotBasePlannerBotModule planner;
		AotMapIntelBotModule intel;
		PowerManager playerPower;

		int ticks;
		int rebuildTicks;
		int waitLog;
		int pendingWaitLog;

		// On-demand naval production (Sub Pen / Shipyard): NOT part of the Rhythm. Requested by any
		// Operations mission that needs ships/subs/vessels; sticky (never cleared) so a later loss of the
		// building triggers an automatic rebuild the next time HasNavalProduction() is checked.
		bool navalRequested;
		AotPlanStep navalStep;
		int navalWaitLog;

		// The single in-flight production: the step it serves, the cell it will be placed at, and
		// whether the produced item is a bridge wall rather than the step's own building.
		AotPlanStep pending;
		string pendingType;
		CPos pendingCell;
		bool pendingIsBridgeWall;

		readonly List<CPos> bridgeWallCells = [];

		// Fence execution: remaining node cells of the fence step currently being built.
		AotPlanStep fenceStep;
		readonly Queue<CPos> fenceQueue = new();

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

			// Faction-agnostic, one instance per player -- same resolution AotOperationsBotModule uses.
			intel = self.Owner.PlayerActor.TraitsImplementing<AotMapIntelBotModule>().FirstOrDefault();
		}

		// ---------------------------------------------------------------- on-demand naval production

		public bool HasNavalProduction() =>
			Info.NavalPenTypes.Length > 0
			&& world.Actors.Any(a => a.Owner == player && !a.IsDead && a.IsInWorld && Info.NavalPenTypes.Contains(a.Info.Name));

		// Sticky: once any Operations mission has ever needed naval production, keep guaranteeing it exists
		// for the rest of the match (a later loss triggers an automatic rebuild -- see BotTick).
		public void RequestNavalProduction() => navalRequested = true;

		AotPlanStep BuildNavalStep()
		{
			if (Info.NavalPenTypes.Length == 0 || intel == null || !intel.Ready)
				return null;

			var site = FindNavalSite();
			if (site == null)
			{
				if (++navalWaitLog % 8 == 0)
					Log.Write("debug", "[AotBuild] Naval production requested but no coastal site found near the base yet");

				return null;
			}

			Log.Write("debug", $"[AotBuild] Naval production requested -> site {site.Value}");
			return new AotPlanStep { Kind = AotStepKind.Building, Role = "NAVAL", Variants = Info.NavalPenTypes, TopLeft = site.Value };
		}

		// Anchor on the nearest coastal cell reachable by land from the base (same concept as a wave's
		// ferry embark point), then spiral out from there for the first cell the actor's actual footprint
		// can occupy (the anchor itself is a 1x1 shoreline cell, rarely a match for a multi-tile building).
		// Reach (buildable area) is NOT checked here -- that is what TryBridgeStep is for.
		CPos? FindNavalSite()
		{
			var variant = Info.NavalPenTypes.FirstOrDefault(v => world.Map.Rules.Actors.ContainsKey(v));
			if (variant == null)
				return null;

			var ai = world.Map.Rules.Actors[variant];
			var bi = ai.TraitInfoOrDefault<BuildingInfo>();
			if (bi == null)
				return null;

			var anchor = intel.FindCoastalCellNear(intel.BaseCentre, Info.NavalSiteSearchRadius, requireOwnReachable: true)
				?? intel.FindCoastalCellNear(intel.BaseCentre, Info.NavalSiteSearchRadius, requireOwnReachable: false);
			if (anchor == null)
				return null;

			for (var r = 0; r <= 6; r++)
				for (var dx = -r; dx <= r; dx++)
					for (var dy = -r; dy <= r; dy++)
					{
						if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
							continue;

						var c = anchor.Value + new CVec(dx, dy);
						if (world.CanPlaceBuilding(c, ai, bi, null))
							return c;
					}

			return null;
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

			if (pending != null)
			{
				TickPending(bot);
				return;
			}

			// On-demand naval production takes priority over the Rhythm: an Operations mission is blocked
			// waiting for it. Re-evaluated every tick against HasNavalProduction(), so a destroyed pen is
			// picked back up automatically (navalStep is dropped once satisfied, forcing a fresh site
			// search -- the old building's site may no longer be valid, e.g. terrain changed).
			if (navalRequested)
			{
				if (HasNavalProduction())
					navalStep = null;
				else
				{
					navalStep ??= BuildNavalStep();
					if (navalStep != null)
					{
						StartStep(bot, navalStep);
						if (pending != null)
							return;
					}
				}
			}

			var step = ChooseStep();
			if (step == null)
				return;

			StartStep(bot, step);
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
			foreach (var step in planner.Rhythm)
			{
				if (!step.Done || step.Kind != AotStepKind.Building)
					continue;

				var alive = world.ActorsHavingTrait<Building>()
					.Any(a => a.Owner == player && !a.IsDead && a.Location == step.TopLeft && step.Variants.Contains(a.Info.Name));
				if (!alive)
				{
					step.Done = false;
					Log.Write("debug", $"[AotBuild] Plan building {step.Role} at {step.TopLeft} lost -> rebuild");
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
				Log.Write("debug", $"[AotBuild] Selling stray fence segment at {a.Location} (outside every ring's perimeter)");
				bot.QueueOrder(new Order("Sell", a, Target.FromActor(a), false));
			}
		}

		AotPlanStep ChooseStep()
		{
			var open = planner.Rhythm.Where(s => !s.Done).ToList();
			if (open.Count == 0)
				return null;

			// Power emergency: pull the next unbuilt power step forward.
			if (playerPower != null && playerPower.ExcessPower < Info.MinimumExcessPower)
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

		void StartStep(IBot bot, AotPlanStep step)
		{
			var (type, queue) = BuildableVariant(step);
			if (type == null)
			{
				if (++waitLog % 8 == 0)
					Log.Write("debug", $"[AotBuild] Waiting (age gate / prerequisites): {step.Role}");

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
						return;
				}

				// Out of buildable-area reach? Bridge along the actual path. Transient blockers are asked
				// to step aside (own units on the site), then we wait.
				if (OutOfReachOnly(step, type) && TryBridgeStep(bot, step))
					return;

				if (++waitLog % 8 == 0)
				{
					Log.Write("debug", $"[AotBuild] Waiting (target blocked): {step.Role} at {step.TopLeft}");
					DiagnoseBlock(step, type);
				}

				return;
			}

			pending = step;
			pendingType = type;
			pendingCell = cell.Value;
			pendingIsBridgeWall = false;
			pendingWaitLog = 0;
			bot.QueueOrder(Order.StartProduction(queue.Actor, type, 1));
			Log.Write("debug", $"[AotBuild] Start {step.Role} ({type}) -> {pendingCell}");
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
			Log.Write("debug", $"[AotBuild] Nudging {blockers.Count} own unit(s) off {type} site at {cell}");
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
			Log.Write("debug", $"[AotBuild] Diag {step.Role}@{target}: CanPlaceBuilding={canPlace} IsCloseEnoughToBase={closeEnough} inPocket={planner.Pocket.Contains(target)}");

			foreach (var t in bi.Tiles(target))
			{
				var terrain = world.Map.GetTerrainInfo(t).Type;
				var res = world.WorldActor.TraitOrDefault<IResourceLayer>()?.GetResource(t).Type;
				var actors = world.ActorMap.GetActorsAt(t).Select(a => $"{a.Info.Name}(owner={a.Owner.ResolvedPlayerName},mobile={a.Info.HasTraitInfo<MobileInfo>()})").ToList();
				Log.Write("debug", $"[AotBuild] Diag tile {t}: terrain={terrain} resource={res ?? "none"} inPocket={planner.Pocket.Contains(t)} actors=[{string.Join(",", actors)}]");
			}

			diagBridge = true;
			var frontier = BridgeFrontier(target);
			diagBridge = false;
			var ownCount = world.ActorsHavingTrait<Building>().Count(a => a.Owner == player && !a.IsDead);
			Log.Write("debug", $"[AotBuild] Diag bridge: ownBuildings={ownCount} frontier={(frontier.HasValue ? frontier.Value.ToString() : "NULL")}");
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
						if (tiles.Any(t => !planner.Pocket.Contains(t) || claimed.Contains(t)))
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

						Log.Write("debug", $"[AotBuild] Re-sited {step.Role}: {step.TopLeft} -> {c} (resources grew onto plan)");
						step.TopLeft = c;
						return true;
					}

			Log.Write("debug", $"[AotBuild] Re-site FAILED for {step.Role} at {step.TopLeft}");
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

			var target = step.Kind == AotStepKind.Fence && fenceQueue.Count > 0 ? fenceQueue.Peek() : step.TopLeft;
			var frontier = BridgeFrontier(target);
			if (frontier == null)
				return false;

			pending = step;
			pendingType = Info.WallType;
			pendingCell = frontier.Value;
			pendingIsBridgeWall = true;
			pendingWaitLog = 0;
			bot.QueueOrder(Order.StartProduction(wallQueue.Actor, Info.WallType, 1));
			Log.Write("debug", $"[AotBuild] Bridge wall #{bridgeWallCells.Count + 1} -> {pendingCell} (toward {target} for {step.Role})");
			return true;
		}

		// Furthest wall-placeable + in-area cell on the actual BFS PATH (within the pocket) from the
		// nearest own building to the target — a straight line breaks on rocks; the path never does.
		CPos? BridgeFrontier(CPos target)
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

					if (!InBounds(n) || !ResourceFree(n))
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
					Log.Write("debug", $"[AotBuild] Diag frontier blocked cell {c}: actors=[{string.Join(",", actors)}]");
				}
			}

			if (best == null && diagBridge)
				Log.Write("debug", $"[AotBuild] Diag frontier: target={target} met={met} pathLen={path.Count} (nudged blockers along path, see above)");

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

			Log.Write("debug", $"[AotBuild] Sold wall bridge ({sold} segments)");
			bridgeWallCells.Clear();
		}

		// ---------------------------------------------------------------- fences

		CPos? NextFenceCell(AotPlanStep step, ActorInfo ai, BuildingInfo bi)
		{
			if (fenceStep != step)
			{
				fenceStep = step;
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
					continue;
				}

				return null;
			}

			// All nodes handled -> fence complete.
			step.Done = true;
			Log.Write("debug", $"[AotBuild] Fence complete: {step.Role}");
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
				Log.Write("debug", $"[AotBuild] Lost production of {pendingType}, retrying");
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
					Log.Write("debug", $"[AotBuild] Still building {pending.Role} ({pendingType}): " +
						$"started={item?.Started} paused={item?.Paused} remainingTime={item?.RemainingTime}/{item?.TotalTime} " +
						$"remainingCost={item?.RemainingCost} excessPower={playerPower?.ExcessPower} " +
						$"queuePos={ourIndex}/{allItems.Count} queueItems=[{string.Join(",", allItems.Select(i => $"{i.Item}(done={i.Done},started={i.Started})"))}]");
				}

				if (ourIndex > 0 && pendingWaitLog > 100)
				{
					for (var i = 0; i < ourIndex; i++)
					{
						Log.Write("debug", $"[AotBuild] Cancelling stuck queue blocker ahead of {pendingType}: {allItems[i].Item}");
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
				Log.Write("debug", $"[AotBuild] Placed bridge wall at {pendingCell} ({bridgeWallCells.Count + 1} segments)");
				bridgeWallCells.Add(pendingCell);
				pendingIsBridgeWall = false;
				pending = null;
				return;
			}

			Log.Write("debug", $"[AotBuild] Place {pending.Role} ({pendingType}) at {pendingCell}");

			if (bridgeWallCells.Count > 0)
				SellBridge(bot);

			// Fence steps stay open until every node is placed (NextFenceCell marks them Done).
			if (pending.Kind != AotStepKind.Fence)
				pending.Done = true;

			pending = null;
		}
	}
}
