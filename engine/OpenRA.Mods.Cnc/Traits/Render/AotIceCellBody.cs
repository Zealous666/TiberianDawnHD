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

using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits.Render
{
	[Desc("Picks the ice sprite from the four shared corners of this cell plus the cell position.",
		"Neighbouring cells read the same corner points and the baked noise is periodic, so the",
		"rendered edge is continuous across cell borders by construction. Re-picks whenever the",
		"ice set changes, so edge cells turn into interior cells as the ice spreads.",
		"Render order is set via ZOffset on the sequence, not here.")]
	sealed class AotIceCellBodyInfo : WithSpriteBodyInfo
	{
		[Desc("Period of the baked noise/texture in cells. Frame layout is",
			"config * (Period*Period) + (y % Period) * Period + (x % Period).")]
		public readonly int Period = 4;

		[Desc("A corner counts as ice only when at least this many of its four cells are ice.",
			"Use 4 (all four): the cell always counts itself, so anything lower lights corners that",
			"face water. Measured on the Polar Panic lake: with 3, all 44 concave corners (only the",
			"diagonal missing) rendered as hard squares; with 4, all 44 render as curves.")]
		public readonly int CornerThreshold = 4;

		public override object Create(ActorInitializer init) { return new AotIceCellBody(init, this); }
	}

	sealed class AotIceCellBody : WithSpriteBody, ITick, INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly AotIceCellBodyInfo cellInfo;
		AotIceLayer layer;
		int lastVersion = -1;
		int frame;

		public AotIceCellBody(ActorInitializer init, AotIceCellBodyInfo info)
			: base(init, info)
		{
			cellInfo = info;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			layer = self.World.WorldActor.Trait<AotIceLayer>();
			layer.Add(self.Location);
			Refresh(self);

			DefaultAnimation.PlayFetchIndex(NormalizeSequence(self, Info.Sequence), () => frame);

			// Set the initial frame before the first render tick.
			self.World.AddFrameEndTask(_ => DefaultAnimation.Tick());
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			layer?.Remove(self.Location);
		}

		static int Mod(int v, int m)
		{
			var r = v % m;
			return r < 0 ? r + m : r;
		}

		void Refresh(Actor self)
		{
			lastVersion = layer.Version;

			var loc = self.Location;
			var p = cellInfo.Period;
			var t = cellInfo.CornerThreshold;

			// The four corners of this cell, each shared with the neighbouring cells.
			var nw = layer.CornerIceCount(loc) >= t ? 1 : 0;
			var ne = layer.CornerIceCount(new CPos(loc.X + 1, loc.Y)) >= t ? 2 : 0;
			var sw = layer.CornerIceCount(new CPos(loc.X, loc.Y + 1)) >= t ? 4 : 0;
			var se = layer.CornerIceCount(new CPos(loc.X + 1, loc.Y + 1)) >= t ? 8 : 0;

			var config = nw | ne | sw | se;
			frame = config * (p * p) + Mod(loc.Y, p) * p + Mod(loc.X, p);
		}

		void ITick.Tick(Actor self)
		{
			// Neighbours froze (or broke) -> our shared corners changed -> re-pick the sprite.
			if (layer != null && layer.Version != lastVersion)
				Refresh(self);
		}
	}
}
