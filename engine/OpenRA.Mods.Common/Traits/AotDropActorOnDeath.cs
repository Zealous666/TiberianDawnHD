#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Spawn another actor on death in a random passable cell near the dying actor.",
		"Unlike SpawnActorOnDeath (fixed Offset), this trait picks a random cell around the",
		"footprint that the spawned actor can actually enter (passable terrain + unoccupied).",
		"Used e.g. by the Oil Pump to drop a cash crate in a random neighbouring cell.")]
	public class AotDropActorOnDeathInfo : TraitInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor to spawn on death.")]
		public readonly string Actor = null;

		[Desc("Probability (%) that the actor spawns at all.")]
		public readonly int Probability = 100;

		[Desc("Maximum cell distance from the footprint to look for a passable cell.",
			"1 = only the immediately neighbouring cells.")]
		public readonly int Range = 1;

		[Desc("Owner of the spawned actor. Allowed keywords: 'Victim', 'Killer' and 'InternalName'.")]
		public readonly OwnerType OwnerType = OwnerType.InternalName;

		[Desc("Map player to use when 'InternalName' is set on 'OwnerType'.")]
		public readonly string InternalOwner = "Neutral";

		[Desc("DeathType that triggers the spawn. Leave empty to ignore the DeathTypes.")]
		public readonly string DeathType = null;

		[Desc("Skips the spawned actor's make animations if true.")]
		public readonly bool SkipMakeAnimations = true;

		public override object Create(ActorInitializer init) { return new AotDropActorOnDeath(this); }
	}

	public class AotDropActorOnDeath : INotifyKilled, INotifyRemovedFromWorld
	{
		readonly AotDropActorOnDeathInfo info;
		Player attackingPlayer;

		public AotDropActorOnDeath(AotDropActorOnDeathInfo info)
		{
			this.info = info;
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			if (!self.IsInWorld)
				return;

			if (self.World.SharedRandom.Next(100) >= info.Probability)
				return;

			if (info.DeathType != null && !e.Damage.DamageTypes.Contains(info.DeathType))
				return;

			attackingPlayer = e.Attacker?.Owner;
		}

		// Wait for all RemovedFromWorld callbacks before adding the new actor.
		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			if (attackingPlayer == null)
				return;

			var world = self.World;

			// Actor definitions are keyed lower-case in the rules dictionary.
			if (!world.Map.Rules.Actors.TryGetValue(info.Actor.ToLowerInvariant(), out var spawnInfo))
				return;

			var positionable = spawnInfo.TraitInfoOrDefault<IPositionableInfo>();

			// Cells occupied by the dying actor's own footprint - never drop the crate onto those.
			var ownCells = self.Info.TraitInfoOrDefault<IOccupySpaceInfo>()
				?.OccupiedCells(self.Info, self.Location).Keys.ToHashSet() ?? new HashSet<CPos>();

			var candidates = new List<CPos>();
			for (var dy = -info.Range; dy <= info.Range; dy++)
			{
				for (var dx = -info.Range; dx <= info.Range; dx++)
				{
					if (dx == 0 && dy == 0)
						continue;

					var cell = self.Location + new CVec(dx, dy);
					if (ownCells.Contains(cell))
						continue;

					// If the spawned actor is positionable, only accept cells it can enter
					// (passable terrain for its type and no blocking actor). Fall back to accepting
					// any in-map cell if it has no such trait.
					if (positionable != null)
					{
						if (positionable.CanEnterCell(world, null, cell, SubCell.Any, self, BlockedByActor.All))
							candidates.Add(cell);
					}
					else if (world.Map.Contains(cell))
						candidates.Add(cell);
				}
			}

			if (candidates.Count == 0)
				return;

			var target = candidates[world.SharedRandom.Next(candidates.Count)];

			var td = new TypeDictionary
			{
				new ParentActorInit(self),
				new LocationInit(target),
			};

			if (info.OwnerType == OwnerType.Killer)
				td.Add(new OwnerInit(attackingPlayer));
			else if (info.OwnerType == OwnerType.Victim)
				td.Add(new OwnerInit(self.Owner));
			else
				td.Add(new OwnerInit(world.Players.First(p => p.InternalName == info.InternalOwner)));

			if (info.SkipMakeAnimations)
				td.Add(new SkipMakeAnimsInit());

			world.AddFrameEndTask(w => w.CreateActor(info.Actor, td));
		}
	}
}
