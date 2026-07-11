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

using System.Linq;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Automatically disguises as a specified actor belonging to the first enemy player when spawned.")]
	sealed class AutoDisguiseOnSpawnInfo : TraitInfo, Requires<DisguiseInfo>
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor to disguise as on spawn.")]
		public readonly string Actor = null;

		public override object Create(ActorInitializer init) { return new AutoDisguiseOnSpawn(init, this); }
	}

	sealed class AutoDisguiseOnSpawn : INotifyAddedToWorld
	{
		readonly AutoDisguiseOnSpawnInfo info;
		readonly Disguise disguise;

		public AutoDisguiseOnSpawn(ActorInitializer init, AutoDisguiseOnSpawnInfo info)
		{
			this.info = info;
			disguise = init.Self.Trait<Disguise>();
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			if (!self.World.Map.Rules.Actors.TryGetValue(info.Actor, out var targetActorInfo))
				return;

			// Pick first enemy player; fall back to any other player
			var targetPlayer = self.World.Players
				.Where(p => p != self.Owner && !p.NonCombatant)
				.OrderBy(p => self.Owner.RelationshipWith(p) == PlayerRelationship.Enemy ? 0 : 1)
				.FirstOrDefault();

			if (targetPlayer == null)
				return;

			disguise.DisguiseAs(targetActorInfo, targetPlayer);
		}
	}
}
