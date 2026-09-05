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
			var world = self.World;
			var layer = world.WorldActor.TraitOrDefault<AotFoundationLayer>();
			var buildingInfo = self.Info.TraitInfo<BuildingInfo>();
			// aotmod (2026-07-29): world.Players.First(p => p.NonCombatant) picked whichever
			// non-combatant player happened to come first in the map's player list. On maps that
			// also define a "Creeps" player (NonCombatant too, see ant-critter-system), foundation
			// cells could end up owned by Creeps instead of Neutral, depending purely on player
			// definition order - RequiresSpecificOwners(ValidOwnerNames: Neutral) on
			// aot-foundation-cell only ever guards against wrong VALUES, it doesn't fix wrong
			// INTENT here. WorldActor.Owner is the actual canonical "Neutral" player (the one
			// CreateMapPlayers assigns via PlayerReference.OwnsWorld) - same pattern already used
			// by CrateSpawner/LegacyBridgeLayer/SpawnMapActors for exactly this purpose.
			var neutral = world.WorldActor.Owner;

			var toSpawn = new List<CPos>();
			foreach (var cell in buildingInfo.Tiles(self.Location))
			{
				if (!world.Map.Contains(cell)) continue;
				if (layer != null && layer.Contains(cell)) continue;

				// Skip invalid terrain (ramps / non-buildable types) so we match the red placement
				// preview and never lay foundation on water or cliffs.
				if (world.Map.Ramp[cell] != 0) continue;
				if (!buildingInfo.TerrainTypes.Contains(world.Map.GetTerrainInfo(cell).Type)) continue;

				// NOTE: cells occupied by buildings are NOT skipped. Foundation is a pure ground
				// smudge that renders below everything, so it slides under buildings to close gaps.
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
