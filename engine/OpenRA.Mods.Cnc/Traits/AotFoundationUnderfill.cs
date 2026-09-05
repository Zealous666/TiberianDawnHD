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
	[Desc("When a building is placed directly adjacent to an existing Fortified Foundation,",
		"extend the foundation under this building's footprint to close the gap. Attach to",
		"^Building. No-op when no foundation touches the footprint.")]
	sealed class AotFoundationUnderfillInfo : TraitInfo, Requires<BuildingInfo>
	{
		[ActorReference]
		[Desc("Foundation cell actor to spawn.")]
		public readonly string CellActor = "aot-foundation-cell";

		public override object Create(ActorInitializer init) { return new AotFoundationUnderfill(this); }
	}

	sealed class AotFoundationUnderfill : INotifyAddedToWorld
	{
		readonly AotFoundationUnderfillInfo info;

		public AotFoundationUnderfill(AotFoundationUnderfillInfo info) { this.info = info; }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			// The foundation placer fills its own footprint via AotPlaceFoundation; don't double up.
			if (self.Info.HasTraitInfo<AotPlaceFoundationInfo>())
				return;

			var world = self.World;
			var layer = world.WorldActor.TraitOrDefault<AotFoundationLayer>();
			if (layer == null)
				return;

			var buildingInfo = self.Info.TraitInfo<BuildingInfo>();
			var footprint = buildingInfo.Tiles(self.Location).ToHashSet();

			// "Directly attached" = at least one footprint cell shares an edge with a foundation
			// cell that is not part of this building's own footprint.
			var offsets = new[] { new CVec(0, -1), new CVec(0, 1), new CVec(-1, 0), new CVec(1, 0) };
			var attached = footprint.Any(c => offsets.Any(o =>
			{
				var n = c + o;
				return !footprint.Contains(n) && layer.Contains(n);
			}));

			if (!attached)
				return;

			var neutral = world.Players.First(p => p.NonCombatant);
			var toSpawn = new List<CPos>();
			foreach (var cell in footprint)
			{
				if (!world.Map.Contains(cell)) continue;
				if (layer.Contains(cell)) continue;
				if (world.Map.Ramp[cell] != 0) continue;
				if (!buildingInfo.TerrainTypes.Contains(world.Map.GetTerrainInfo(cell).Type)) continue;
				toSpawn.Add(cell);
			}

			if (toSpawn.Count == 0)
				return;

			world.AddFrameEndTask(w =>
			{
				foreach (var cell in toSpawn)
				{
					w.CreateActor(info.CellActor, new TypeDictionary
					{
						new LocationInit(cell),
						new OwnerInit(neutral),
					});
				}
			});
		}
	}
}
