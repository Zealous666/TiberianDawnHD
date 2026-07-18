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
	[Desc("Picks the foundation bib sprite from the four cardinal neighbours.",
		"Frame index = N|E<<1|S<<2|W<<3 (0-15), where a bit is set when a foundation cell",
		"exists directly adjacent in that direction. Re-picks whenever the foundation set",
		"changes, so seams disappear as adjacent tiles are placed.")]
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
			var n = layer.Contains(new CPos(loc.X,     loc.Y - 1)) ? 1 : 0;
			var e = layer.Contains(new CPos(loc.X + 1, loc.Y    )) ? 2 : 0;
			var s = layer.Contains(new CPos(loc.X,     loc.Y + 1)) ? 4 : 0;
			var w = layer.Contains(new CPos(loc.X - 1, loc.Y    )) ? 8 : 0;
			frame = n | e | s | w;
		}

		void ITick.Tick(Actor self)
		{
			if (layer != null && layer.Version != lastVersion)
				Refresh(self);
		}
	}
}
