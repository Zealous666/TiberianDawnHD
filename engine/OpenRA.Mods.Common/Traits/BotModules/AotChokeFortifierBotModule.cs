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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Age of Tiberium: once the repair bay is up, bridges a chain of walls from the base out to the",
		"chokepoint so the buildable area reaches it, fortifies the chokepoint, then sells the wall bridge.",
		"Also rings the base with anti-air. Reads the chokepoint from IBotChokepointProvider (AotBaseLayoutManager).")]
	public class AotChokeFortifierBotModuleInfo : ConditionalTraitInfo
	{
		[ActorReference]
		[Desc("Construction yard actor types, used to find the base centre.")]
		public readonly HashSet<string> ConstructionYardTypes = [];

		[ActorReference]
		[Desc("Wall actor types used to bridge out to the chokepoint (faction-picked automatically).")]
		public readonly HashSet<string> WallTypes = [];

		[ActorReference]
		[Desc("Repair facility actor types. The bridge only starts once one of these exists.")]
		public readonly HashSet<string> RepairTypes = [];

		[Desc("Ticks between fortifier actions (throttle).")]
		public readonly int Interval = 40;

		[Desc("Stop bridging once a wall is within this many cells of the chokepoint.")]
		public readonly int ArriveRadius = 3;

		public override object Create(ActorInitializer init) { return new AotChokeFortifierBotModule(init.Self, this); }
	}

	public class AotChokeFortifierBotModule : ConditionalTrait<AotChokeFortifierBotModuleInfo>, IBotTick
	{
		enum Phase { Idle, Bridging, Fortifying, Retracting, Done }

		readonly World world;
		readonly Player player;

		IBotChokepointProvider chokeProvider;
		Phase phase = Phase.Idle;
		int ticks;
		int diagCount;

		// The wall currently in production and the cell it will be placed at (one at a time).
		CPos? pendingWallCell;

		// Every bridge wall we placed, so we can sell the whole bridge later.
		readonly List<CPos> bridgeWallCells = [];

		public AotChokeFortifierBotModule(Actor self, AotChokeFortifierBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			chokeProvider = self.Owner.PlayerActor.TraitsImplementing<IBotChokepointProvider>().FirstOrDefault();
		}

		void IBotTick.BotTick(IBot bot)
		{
			if (IsTraitDisabled)
				return;

			if (--ticks > 0)
				return;

			ticks = Info.Interval;

			var diag = phase == Phase.Idle && diagCount < 20;

			var choke = chokeProvider?.Chokepoint;
			if (choke == null)
			{
				if (diag) { diagCount++; Log.Write("debug", $"[AotFortify] Diag: chokeProvider={chokeProvider != null} choke=NULL"); }
				return;
			}

			var conYard = world.ActorsHavingTrait<Building>()
				.FirstOrDefault(a => a.Owner == player && !a.IsDead && Info.ConstructionYardTypes.Contains(a.Info.Name));
			if (conYard == null)
			{
				if (diag) { diagCount++; Log.Write("debug", $"[AotFortify] Diag: choke={choke} conYard=NULL"); }
				return;
			}

			var baseCentre = conYard.Location;

			switch (phase)
			{
				case Phase.Idle:
				{
					// Wait for a repair bay before spending on the bridge. Trait-based (RepairsUnits) instead
					// of a name list, so it can't silently miss a differently-named faction/age variant.
					var haveRepair = world.ActorsHavingTrait<Building>()
						.Any(a => a.Owner == player && !a.IsDead && a.Info.HasTraitInfo<RepairsUnitsInfo>());
					if (++diagCount % 5 == 0)
						Log.Write("debug", $"[AotFortify] Diag Idle: choke={choke} conYard={baseCentre} haveRepair={haveRepair}");
					if (haveRepair)
					{
						phase = Phase.Bridging;
						Log.Write("debug", $"[AotFortify] {player.ResolvedPlayerName} start wall-bridge base={baseCentre} -> choke={choke.Value}");
					}

					break;
				}

				case Phase.Bridging:
					TickBridging(bot, baseCentre, choke.Value);
					break;

				// Phase.Fortifying / Retracting / Done: implemented in the next pieces.
			}
		}

		void TickBridging(IBot bot, CPos baseCentre, CPos choke)
		{
			var wallType = Info.WallTypes.FirstOrDefault(w => FindQueue(w) != null);
			if (wallType == null)
			{
				if (++diagCount % 10 == 0)
					Log.Write("debug", $"[AotFortify] Diag Bridging: no queue can build any of [{string.Join(",", Info.WallTypes)}]");
				return;
			}

			var queue = FindQueue(wallType);
			var wallInfo = world.Map.Rules.Actors[wallType];
			var wallBi = wallInfo.TraitInfoOrDefault<BuildingInfo>();
			if (wallBi == null)
				return;

			// If a wall is already in production, place it once it is done, then advance.
			if (pendingWallCell != null)
			{
				var done = queue.AllQueued().Any(i => i.Done && i.Item == wallType);
				if (!done)
				{
					var queued = queue.AllQueued().Count(i => i.Item == wallType);
					if (++diagCount % 10 == 0)
						Log.Write("debug", $"[AotFortify] Diag Bridging: waiting for {wallType} (queued={queued}) for cell {pendingWallCell.Value}");

					// The queue lost our wall (e.g. cancelled by the base builder logic): re-order it.
					if (queued == 0)
						bot.QueueOrder(Order.StartProduction(queue.Actor, wallType, 1));

					return;
				}

				bot.QueueOrder(new Order("LineBuild", player.PlayerActor, Target.FromCell(world, pendingWallCell.Value), false)
				{
					TargetString = wallType,
					ExtraData = queue.Actor.ActorID,
					SuppressVisualFeedback = true
				});

				Log.Write("debug", $"[AotFortify] {player.ResolvedPlayerName} placed bridge wall #{bridgeWallCells.Count + 1} at {pendingWallCell.Value}");
				bridgeWallCells.Add(pendingWallCell.Value);
				pendingWallCell = null;
				return;
			}

			// Find the frontier: the furthest cell along the base->choke line where a wall can still be
			// placed right now (i.e. buildable area reaches it). Placing there extends the area toward the
			// choke, so the frontier advances each round until it arrives.
			var frontier = FindFrontier(baseCentre, choke, wallInfo, wallBi);
			if (frontier == null)
			{
				Log.Write("debug", $"[AotFortify] {player.ResolvedPlayerName} bridge blocked toward choke {choke}");
				return;
			}

			if ((frontier.Value - choke).Length <= Info.ArriveRadius)
			{
				Log.Write("debug", $"[AotFortify] {player.ResolvedPlayerName} bridge reached choke {choke} ({bridgeWallCells.Count} walls)");
				phase = Phase.Fortifying;
				return;
			}

			// Queue one wall for the frontier cell.
			pendingWallCell = frontier;
			bot.QueueOrder(Order.StartProduction(queue.Actor, wallType, 1));
		}

		// Walk the straight line base->choke; return the furthest cell that is currently placeable.
		CPos? FindFrontier(CPos baseCentre, CPos choke, ActorInfo wallInfo, BuildingInfo wallBi)
		{
			var from = world.Map.CenterOfCell(baseCentre);
			var v = world.Map.CenterOfCell(choke) - from;
			var len = v.Length / 1024;
			if (len == 0)
				return null;

			var unit = (v * 1024) / v.Length;

			// The frontier is the furthest cell that is BOTH physically placeable (CanPlaceBuilding checks
			// only terrain + occupancy) AND inside the current buildable area (IsCloseEnoughToBase). Without
			// the second check any clear cell — even right next to the choke — would count, so the bridge
			// would "arrive" instantly with zero walls. Each wall we place extends the area ~2 cells further.
			CPos? best = null;
			for (var k = 1; k <= len; k++)
			{
				var cell = world.Map.CellContaining(from + (unit * k));
				if (world.CanPlaceBuilding(cell, wallInfo, wallBi, null)
					&& wallBi.IsCloseEnoughToBase(world, player, wallInfo, cell))
					best = cell;
			}

			return best;
		}

		ProductionQueue FindQueue(string actorType)
		{
			var ai = world.Map.Rules.Actors[actorType];
			foreach (var q in AIUtils.FindQueuesByCategory(player).SelectMany(g => g))
				if (q.Enabled && q.CanBuild(ai))
					return q;

			return null;
		}
	}
}
