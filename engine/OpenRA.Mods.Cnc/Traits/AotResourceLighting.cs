#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Makes resource fields (tiberium) glow by feeding aggregated light sources into TerrainLighting.
 * One instance per resource type, so tiberium and blue tiberium can carry different colours.
 */
#endregion

using System.Collections.Generic;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("Adds a light source for each block of cells holding the given resource, so tiberium",
		"fields glow at night. Add one instance per resource type.",
		"Cells are aggregated into fixed BlockSize x BlockSize blocks rather than lit individually:",
		"a large field lights up across several blocks while a small patch lights one, and the",
		"number of light sources stays bounded no matter how much resource is on the map.")]
	sealed class AotResourceLightingInfo : TraitInfo
	{
		[FieldLoader.Require]
		[Desc("Resource type to light up, as named in the ResourceLayer (e.g. Tiberium).")]
		public readonly string ResourceType = null;

		[Desc("Edge length in cells of the aggregation block. One light source per non-empty block.")]
		public readonly int BlockSize = 6;

		[Desc("Radius of each block's light source.")]
		public readonly WDist Range = WDist.FromCells(4);

		[Desc("Brightness added by a lit block.")]
		public readonly float Intensity = 0.18f;

		[Desc("Colour added by a lit block.")]
		public readonly float RedTint = 0.02f;
		public readonly float GreenTint = 0.22f;
		public readonly float BlueTint = 0.04f;

		public override object Create(ActorInitializer init) { return new AotResourceLighting(init.Self, this); }
	}

	sealed class AotResourceLighting : ITick
	{
		readonly AotResourceLightingInfo info;
		readonly World world;

		// Cells currently holding our resource type, so a CellChanged can be turned into a delta.
		readonly HashSet<CPos> cells = [];

		// Block coordinate -> number of our cells in it, and the light source token while lit.
		readonly Dictionary<int2, int> blockCounts = [];
		readonly Dictionary<int2, int> blockTokens = [];

		IResourceLayer resourceLayer;
		TerrainLighting lighting;
		bool initialised;

		public AotResourceLighting(Actor self, AotResourceLightingInfo info)
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
			// Deferred to the first tick on purpose: doing this in IWorldLoaded would race the
			// ResourceLayer's own WorldLoaded, which is what fills in the map's starting resources.
			if (initialised)
				return;

			initialised = true;

			resourceLayer = self.TraitOrDefault<IResourceLayer>();
			lighting = self.TraitOrDefault<TerrainLighting>();
			if (resourceLayer == null || lighting == null)
				return;

			foreach (var cell in world.Map.AllCells)
				if (resourceLayer.GetResource(cell).Type == info.ResourceType)
					Add(cell);

			resourceLayer.CellChanged += OnCellChanged;
		}

		void OnCellChanged(CPos cell, string resourceType)
		{
			// The event also fires on pure density changes, so compare against what we track
			// instead of trusting the reported type.
			var has = resourceLayer.GetResource(cell).Type == info.ResourceType;
			var had = cells.Contains(cell);
			if (has == had)
				return;

			if (has)
				Add(cell);
			else
				Remove(cell);
		}

		void Add(CPos cell)
		{
			cells.Add(cell);
			var block = BlockOf(cell);
			blockCounts.TryGetValue(block, out var count);
			blockCounts[block] = count + 1;

			// First cell in this block: light it up.
			if (count == 0)
				blockTokens[block] = lighting.AddLightSource(CentreOfBlock(block), info.Range, info.Intensity,
					new float3(info.RedTint, info.GreenTint, info.BlueTint));
		}

		void Remove(CPos cell)
		{
			cells.Remove(cell);
			var block = BlockOf(cell);
			if (!blockCounts.TryGetValue(block, out var count))
				return;

			if (count <= 1)
			{
				blockCounts.Remove(block);

				// Last cell harvested away: the glow goes with it.
				if (blockTokens.TryGetValue(block, out var token))
				{
					lighting.RemoveLightSource(token);
					blockTokens.Remove(block);
				}
			}
			else
				blockCounts[block] = count - 1;
		}
	}
}
