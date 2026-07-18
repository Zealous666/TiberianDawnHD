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

using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits.Render
{
	[Desc("Picks the foundation bib sprite from the four shared corners of this cell (dual grid).",
		"Frame index = nw|ne<<1|sw<<2|se<<3 (0-15); a corner counts as solid when all four cells",
		"around that corner point are foundation. This distinguishes concave corners (inner curves)",
		"from full interior, so seams and inner corners render continuously across cell borders.",
		"Re-picks whenever the foundation set changes.")]
	sealed class AotFoundationCellBodyInfo : WithSpriteBodyInfo
	{
		public override object Create(ActorInitializer init) { return new AotFoundationCellBody(init, this); }
	}

	sealed class AotFoundationCellBody : WithSpriteBody, ITick, INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		AotFoundationLayer layer;
		int lastVersion = -1;
		int frame;

		public AotFoundationCellBody(ActorInitializer init, AotFoundationCellBodyInfo info)
			: base(init, info) { }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			layer = self.World.WorldActor.Trait<AotFoundationLayer>();
			layer.Add(self.Location);
			Refresh(self);

			DefaultAnimation.PlayFetchIndex(NormalizeSequence(self, Info.Sequence), () => frame);
			self.World.AddFrameEndTask(_ => DefaultAnimation.Tick());
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			layer?.Remove(self.Location);
		}

		void Refresh(Actor self)
		{
			lastVersion = layer.Version;
			var loc = self.Location;

			// The four corners of this cell, each shared with the neighbouring cells. A corner is
			// solid only when all four cells around it are foundation, so a missing diagonal leaves
			// the corner non-solid -> concave (inner) curve instead of a hard square.
			var nw = layer.CornerCount(loc) == 4 ? 1 : 0;
			var ne = layer.CornerCount(new CPos(loc.X + 1, loc.Y)) == 4 ? 2 : 0;
			var sw = layer.CornerCount(new CPos(loc.X, loc.Y + 1)) == 4 ? 4 : 0;
			var se = layer.CornerCount(new CPos(loc.X + 1, loc.Y + 1)) == 4 ? 8 : 0;
			frame = nw | ne | sw | se;
		}

		void ITick.Tick(Actor self)
		{
			if (layer != null && layer.Version != lastVersion)
				Refresh(self);
		}
	}
}
