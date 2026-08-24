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
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("aotmod: draws all ice cells (from AotIceLayer) as one batched sprite layer instead of one",
		"actor per cell. The per-cell frame is picked from the four shared corners exactly as the old",
		"per-cell AotIceCellBody did, so the ice edge stays continuous. Attach to the world actor.")]
	sealed class AotIceRendererInfo : TraitInfo, Requires<AotIceLayerInfo>
	{
		[Desc("Image holding the ice sprite sequence.")]
		public readonly string Image = "aot-ice-cell";

		[Desc("Sequence within Image (the 256-frame quilt).")]
		public readonly string Sequence = "idle";

		[Desc("Period of the baked noise/texture in cells. Frame layout is",
			"config * (Period*Period) + (y % Period) * Period + (x % Period).")]
		public readonly int Period = 4;

		[Desc("A corner counts as ice only when at least this many of its four cells are ice.")]
		public readonly int CornerThreshold = 4;

		public override object Create(ActorInitializer init) { return new AotIceRenderer(init.Self, this); }
	}

	sealed class AotIceRenderer : INotifyCreated, IWorldLoaded, IRenderOverlay, ITickRender, INotifyActorDisposing
	{
		readonly AotIceRendererInfo info;
		readonly List<CPos> dirtyScratch = [];

		AotIceLayer layer;
		ISpriteSequence sequence;
		TerrainSpriteLayer spriteLayer;
		bool disposed;

		public AotIceRenderer(Actor self, AotIceRendererInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			// self IS the world actor; World.WorldActor is not assigned until it finishes creating.
			layer = self.Trait<AotIceLayer>();
		}

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			sequence = w.Map.Sequences.GetSequence(info.Image, info.Sequence);

			var first = sequence.GetSprite(0);
			var emptySprite = new Sprite(first.Sheet, Rectangle.Empty, TextureChannel.Alpha);

			// Truecolor (RGBA) sprite: TerrainSpriteLayer nulls the palette itself for RGBA sprites.
			spriteLayer = new TerrainSpriteLayer(w, wr, emptySprite, first.BlendMode, wr.World.Type != WorldType.Editor);

			// Draw whatever ice already exists (e.g. editor-placed hosts that seeded on the first tick).
			foreach (var cell in layer.Cells)
				UpdateCell(cell);
		}

		static int Mod(int v, int m)
		{
			var r = v % m;
			return r < 0 ? r + m : r;
		}

		void UpdateCell(CPos cell)
		{
			if (!layer.Contains(cell))
			{
				spriteLayer.Clear(cell);
				return;
			}

			var p = info.Period;
			var t = info.CornerThreshold;

			var nw = layer.CornerIceCount(cell) >= t ? 1 : 0;
			var ne = layer.CornerIceCount(new CPos(cell.X + 1, cell.Y)) >= t ? 2 : 0;
			var sw = layer.CornerIceCount(new CPos(cell.X, cell.Y + 1)) >= t ? 4 : 0;
			var se = layer.CornerIceCount(new CPos(cell.X + 1, cell.Y + 1)) >= t ? 8 : 0;

			var config = nw | ne | sw | se;
			var frame = config * (p * p) + Mod(cell.Y, p) * p + Mod(cell.X, p);

			spriteLayer.Update(cell, sequence, null, frame);
		}

		void ITickRender.TickRender(WorldRenderer wr, Actor self)
		{
			if (spriteLayer == null)
				return;

			dirtyScratch.Clear();
			layer.DrainDirty(dirtyScratch);
			foreach (var cell in dirtyScratch)
				UpdateCell(cell);
		}

		void IRenderOverlay.Render(WorldRenderer wr)
		{
			spriteLayer?.Draw(wr.Viewport);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			if (disposed)
				return;

			spriteLayer?.Dispose();
			disposed = true;
		}
	}
}
