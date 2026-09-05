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
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Tracks every cell currently covered by ice (hosts + grown cells). Attach to World.",
		"aotmod: the ice is a cell LAYER, not one actor per cell -- AotIceRenderer draws it and",
		"AotIceBreakLayer handles vehicles breaking through. This trait owns the cell set and the",
		"Ice terrain override, and records which cells changed so the renderer only refreshes those.")]
	sealed class AotIceLayerInfo : TraitInfo
	{
		[Desc("Terrain type the ice makes a water cell walkable as (movement speeds live in the Locomotors).")]
		public readonly string TerrainType = "Ice";

		public override object Create(ActorInitializer init) { return new AotIceLayer(init.World, this); }
	}

	sealed class AotIceLayer : IGameSaveTraitData
	{
		readonly World world;
		readonly AotIceLayerInfo info;
		readonly HashSet<CPos> ice = [];

		// Original CustomTerrain value per iced cell, so removing the ice restores exactly what was there.
		readonly Dictionary<CPos, byte> previousTerrain = [];

		// Cells whose rendered sprite must be recomputed (the changed cell plus its neighbours, whose
		// shared corners moved). AotIceRenderer drains this each render tick.
		readonly HashSet<CPos> dirty = [];

		byte iceTerrainIndex;
		bool terrainIndexResolved;

		public AotIceLayer(World world, AotIceLayerInfo info)
		{
			this.world = world;
			this.info = info;
		}

		byte IceTerrainIndex()
		{
			// Lazy so it never depends on trait-creation order (Map.Rules is ready by the first Add).
			if (!terrainIndexResolved)
			{
				iceTerrainIndex = world.Map.Rules.TerrainInfo.GetTerrainIndex(info.TerrainType);
				terrainIndexResolved = true;
			}

			return iceTerrainIndex;
		}

		public bool Contains(CPos c) { return ice.Contains(c); }

		public IReadOnlyCollection<CPos> Cells => ice;

		// Snapshot save/load: the grown ice is world state, not actors, so it must be carried explicitly
		// or a loaded game starts with no ice at all (only the growth hosts re-seed their immediate
		// cells). Add is idempotent, so re-adding a cell the hosts already re-seeded is harmless.
		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			if (ice.Count == 0)
				return null;

			var cells = string.Join(" ", ice.OrderBy(c => c.X).ThenBy(c => c.Y).Select(c => $"{c.X},{c.Y}"));
			return [new MiniYamlNode("Ice", cells)];
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, MiniYaml data)
		{
			var node = data.NodeWithKeyOrDefault("Ice");
			if (node == null)
				return;

			foreach (var pair in node.Value.Value.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = pair.Split(',');
				if (parts.Length == 2)
					Add(new CPos(Exts.ParseInt32Invariant(parts[0]), Exts.ParseInt32Invariant(parts[1])));
			}
		}

		public void Add(CPos c)
		{
			if (!ice.Add(c))
				return;

			previousTerrain[c] = world.Map.CustomTerrain[c];
			world.Map.CustomTerrain[c] = IceTerrainIndex();

			MarkDirtyWithNeighbours(c);
		}

		public void Remove(CPos c)
		{
			if (!ice.Remove(c))
				return;

			if (previousTerrain.Remove(c, out var prev))
				world.Map.CustomTerrain[c] = prev;

			MarkDirtyWithNeighbours(c);
		}

		void MarkDirtyWithNeighbours(CPos c)
		{
			dirty.Add(c);
			foreach (var d in CVec.Directions)
				dirty.Add(c + d);
		}

		/// <summary>Renderer pulls the set of cells that changed since the last render tick.</summary>
		public void DrainDirty(ICollection<CPos> into)
		{
			foreach (var c in dirty)
				into.Add(c);

			dirty.Clear();
		}

		/// <summary>
		/// A corner point is shared by the four cells around it. Returns how many of them are ice.
		/// Neighbouring cells query the same corner and therefore always agree -- this is what makes
		/// the rendered ice edge continuous across cell borders.
		/// </summary>
		public int CornerIceCount(CPos corner)
		{
			var n = 0;
			if (ice.Contains(new CPos(corner.X - 1, corner.Y - 1))) n++;
			if (ice.Contains(new CPos(corner.X, corner.Y - 1))) n++;
			if (ice.Contains(new CPos(corner.X - 1, corner.Y))) n++;
			if (ice.Contains(new CPos(corner.X, corner.Y))) n++;
			return n;
		}
	}
}
