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
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("When placed, spawns a foundation cell actor on every tile of this actor's",
		"Building footprint, then disposes the placer. Skips tiles that already have",
		"foundation or contain another Building. Attach to a buildable Building actor.")]
	sealed class AotPlaceFoundationInfo : TraitInfo, Requires<BuildingInfo>
	{
		[ActorReference]
		[Desc("Actor type to spawn for each foundation cell.")]
		public readonly string CellActor = "aot-foundation-cell";

		public override object Create(ActorInitializer init) { return new AotPlaceFoundation(this); }
	}

	sealed class AotPlaceFoundation : INotifyAddedToWorld
	{
		readonly AotPlaceFoundationInfo info;

		public AotPlaceFoundation(AotPlaceFoundationInfo info) { this.info = info; }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			var layer = self.World.WorldActor.TraitOrDefault<AotFoundationLayer>();
			var buildingInfo = self.Info.TraitInfo<BuildingInfo>();
			var neutral = self.World.Players.First(p => p.NonCombatant);

			var toSpawn = new List<CPos>();
			foreach (var cell in buildingInfo.Tiles(self.Location))
			{
				if (!self.World.Map.Contains(cell)) continue;
				if (layer != null && layer.Contains(cell)) continue;

				// Skip cells occupied by any Building other than this placer itself.
				if (self.World.ActorMap.GetActorsAt(cell)
					.Where(a => a != self)
					.Any(a => a.Info.HasTraitInfo<BuildingInfo>()))
					continue;

				toSpawn.Add(cell);
			}

			self.World.AddFrameEndTask(w =>
			{
				foreach (var cell in toSpawn)
				{
					w.CreateActor(info.CellActor, new TypeDictionary
					{
						new LocationInit(cell),
						new OwnerInit(neutral),
					});
				}

				self.Dispose();
			});
		}
	}
}
