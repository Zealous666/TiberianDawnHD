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
	[Desc("Picks the vein sprite from the four shared corners of this cell plus the cell position.",
		"Direct clone of AotIceCellBody, backed by AotVeinLayer. Continuous edges by construction;",
		"re-picks whenever the vein set changes. Render order is set via ZOffset on the sequence.")]
	sealed class AotVeinCellBodyInfo : WithSpriteBodyInfo
	{
		[Desc("Period of the baked noise/texture in cells. Frame layout is",
			"config * (Period*Period) + (y % Period) * Period + (x % Period).")]
		public readonly int Period = 4;

		[Desc("A corner counts as veined only when at least this many of its four cells are veined.",
			"Use 4 (all four) for clean concave curves -- see AotIceCellBody for the measurement.")]
		public readonly int CornerThreshold = 4;

		public override object Create(ActorInitializer init) { return new AotVeinCellBody(init, this); }
	}

	sealed class AotVeinCellBody : WithSpriteBody, ITick, INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly AotVeinCellBodyInfo cellInfo;
		AotVeinLayer layer;
		int lastVersion = -1;
		int frame;

		public AotVeinCellBody(ActorInitializer init, AotVeinCellBodyInfo info)
			: base(init, info)
		{
			cellInfo = info;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			layer = self.World.WorldActor.Trait<AotVeinLayer>();
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

			var nw = layer.CornerVeinCount(loc) >= t ? 1 : 0;
			var ne = layer.CornerVeinCount(new CPos(loc.X + 1, loc.Y)) >= t ? 2 : 0;
			var sw = layer.CornerVeinCount(new CPos(loc.X, loc.Y + 1)) >= t ? 4 : 0;
			var se = layer.CornerVeinCount(new CPos(loc.X + 1, loc.Y + 1)) >= t ? 8 : 0;

			var config = nw | ne | sw | se;
			frame = config * (p * p) + Mod(loc.Y, p) * p + Mod(loc.X, p);
		}

		void ITick.Tick(Actor self)
		{
			if (layer != null && layer.Version != lastVersion)
				Refresh(self);
		}
	}
}
