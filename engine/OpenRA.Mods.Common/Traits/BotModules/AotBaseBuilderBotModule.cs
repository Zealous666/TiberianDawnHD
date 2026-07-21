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

		public override object Create(ActorInitializer init) { return new AotBaseBuilderBotModule(init.Self, this); }
	}

	public class AotBaseBuilderBotModule : ConditionalTrait<AotBaseBuilderBotModuleInfo>, IBotTick
	{
		readonly World world;
		readonly Player player;

		AotBasePlannerBotModule planner;
		PowerManager playerPower;

		int ticks;
		int rebuildTicks;
		int waitLog;

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
			}

			if (pending != null)
			{
				TickPending(bot);
				return;
			}

			var step = ChooseStep();
			if (step == null)
				return;

			StartStep(bot, step);
		}

		// Destroyed plan buildings reopen their steps; the rhythm order then rebuilds them first.
		void RebuildScan()
		{
			foreach (var step in planner.Rhythm)
			{
				if (!step.Done || step.Kind != AotStepKind.Building)
					continue;

				var alive = world.ActorMap.GetActorsAt(step.TopLeft)
					.Any(a => a.Owner == player && !a.IsDead && step.Variants.Contains(a.Info.Name));
				if (!alive)
				{
					step.Done = false;
					Log.Write("debug", $"[AotBuild] Plan building {step.Role} at {step.TopLeft} lost -> rebuild");
				}
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
				if (step.Kind == AotStepKind.Building)
				{
					NudgeBlockers(type, step.TopLeft);

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
					Log.Write("debug", $"[AotBuild] Waiting (target blocked): {step.Role} at {step.TopLeft}");

				return;
			}

			pending = step;
			pendingType = type;
			pendingCell = cell.Value;
			pendingIsBridgeWall = false;
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

			// BFS from the target back to any own cell, constrained to the pocket (plus own cells).
			var pocket = planner.Pocket;
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

					if (!pocket.Contains(n))
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
				if (world.CanPlaceBuilding(c, wai, wbi, null) && wbi.IsCloseEnoughToBase(world, player, wai, c))
					best = c;
			}

			return best;
		}

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
				if (blockedByBuilding)
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
				return;

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
