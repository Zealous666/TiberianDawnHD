#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Makes the tiberium vein carpet glow by feeding aggregated light sources into TerrainLighting.
 * Counterpart to AotResourceLighting, but driven by AotVeinLayer instead of the resource layer.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Adds a light source for each block of cells covered by tiberium veins, so the carpet",
		"glows at night. Cells are aggregated into fixed BlockSize x BlockSize blocks rather than",
		"lit individually: a vein field can easily reach several hundred cells, and one light",
		"source each would swamp the lighting partition.")]
	sealed class AotVeinLightingInfo : TraitInfo
	{
		[Desc("Edge length in cells of the aggregation block. One light source per non-empty block.")]
		public readonly int BlockSize = 6;

		[Desc("Radius of each block's light source.")]
		public readonly WDist Range = WDist.FromCells(4);

		[Desc("Brightness added by a lit block.")]
		public readonly float Intensity = 0.18f;

		[Desc("Colour added by a lit block. Reddish-orange for the toxic vein carpet.")]
		public readonly float RedTint = 0.30f;
		public readonly float GreenTint = 0.11f;
		public readonly float BlueTint = 0.01f;

		public override object Create(ActorInitializer init) { return new AotVeinLighting(init.Self, this); }
	}

	sealed class AotVeinLighting : ITick
	{
		readonly AotVeinLightingInfo info;
		readonly World world;

		// Block coordinate -> light source token, for every block currently lit.
		readonly Dictionary<int2, int> blockTokens = [];
		readonly HashSet<int2> occupied = [];

		AotVeinLayer layer;
		TerrainLighting lighting;
		bool initialised;
		int lastVersion = -1;

		public AotVeinLighting(Actor self, AotVeinLightingInfo info)
		{
			this.info = info;
			world = self.World;
		}

		int2 BlockOf(CPos cell)
		{
			var size = info.BlockSize < 1 ? 1 : info.BlockSize;

			// Floor division, so negative coordinates do not fold onto block 0 with the positives.
			var bx = cell.X >= 0 ? cell.X / size : (cell.X - size + 1) / size;
			var by = cell.Y >= 0 ? cell.Y / size : (cell.Y - size + 1) / size;
			return new int2(bx, by);
		}

		WPos CentreOfBlock(int2 block)
		{
			var size = info.BlockSize < 1 ? 1 : info.BlockSize;
			return world.Map.CenterOfCell(new CPos(block.X * size + size / 2, block.Y * size + size / 2));
		}

		void ITick.Tick(Actor self)
		{
			if (!initialised)
			{
				initialised = true;
				layer = self.TraitOrDefault<AotVeinLayer>();
				lighting = self.TraitOrDefault<TerrainLighting>();
			}

			if (layer == null || lighting == null || layer.Version == lastVersion)
				return;

			lastVersion = layer.Version;

			// Veins only change when the growth pass runs (a few hundred ticks apart), so a full
			// rebuild from the layer snapshot is cheap enough and avoids tracking per-cell deltas.
			occupied.Clear();
			foreach (var cell in layer.Cells)
				occupied.Add(BlockOf(cell));

			// Newly covered blocks light up.
			foreach (var block in occupied)
				if (!blockTokens.ContainsKey(block))
					blockTokens[block] = lighting.AddLightSource(CentreOfBlock(block), info.Range, info.Intensity,
						new float3(info.RedTint, info.GreenTint, info.BlueTint));

			// Blocks the veins receded from lose their glow with them.
			var stale = new List<int2>();
			foreach (var kv in blockTokens)
				if (!occupied.Contains(kv.Key))
					stale.Add(kv.Key);

			foreach (var block in stale)
			{
				lighting.RemoveLightSource(blockTokens[block]);
				blockTokens.Remove(block);
			}
		}
	}
}
