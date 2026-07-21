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

		public Actor NearestEnemyYard(CPos from)
		{
			return constructionYards.Actors
				.Where(a => !a.IsDead && a.IsInWorld
					&& Player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy
					&& IsReachable(a.Location))
				.MinByOrDefault(a => (a.Location - from).LengthSquared);
		}

		public CPos? NearestEnemySpawn(CPos from)
		{
			var candidates = EnemySpawns.Where(IsReachable).ToList();
			if (candidates.Count == 0)
				return null;
			return candidates.MinBy(s => (s - from).LengthSquared);
		}

		public Actor NearestVisibleEnemyHarvester(CPos from)
		{
			return World.ActorsHavingTrait<Harvester>()
				.Where(a => !a.IsDead && a.IsInWorld
					&& Player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy
					&& a.CanBeViewedByPlayer(Player)
					&& IsReachable(a.Location))
				.MinByOrDefault(a => (a.Location - from).LengthSquared);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			constructionYards.Dispose();
		}
	}
}
