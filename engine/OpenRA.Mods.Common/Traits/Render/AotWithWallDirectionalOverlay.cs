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

using System;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("aotmod: Wie WithIdleOverlay, aber berechnet selbst die Wall-Nachbarschaftsmaske ",
		"(wie WithWallSpriteBody) und spielt je nach Segment-Ausrichtung eine andere Sequenz: ",
		"SequenceVertical fuer Segmente mit Nord/Sued-Nachbarn, SequenceHorizontal fuer reine ",
		"Ost/West-Segmente. Beide Sequenzen duerfen eigene klassische Pixel-Offsets tragen, damit ",
		"der Firestorm-Beam auf horizontalen und vertikalen Mauerstuecken jeweils am Zaun/Draht ",
		"verankert bleibt statt bei einem einzigen Kompromiss-Offset zu driften.")]
	sealed class AotWithWallDirectionalOverlayInfo : PausableConditionalTraitInfo, IWallConnectorInfo, Requires<RenderSpritesInfo>
	{
		[Desc("Wall connection type, muss zum LineBuild/WithWallSpriteBody-Type des Aktors passen.")]
		public readonly string Type = "wall";

		[Desc("Image used for this decoration. Defaults to the actor's type.")]
		public readonly string Image = null;

		[SequenceReference(nameof(Image), allowNullImage: true)]
		[Desc("Sequenz fuer Segmente mit Nord- oder Sued-Nachbar (auch Ecken/Kreuzungen).")]
		public readonly string SequenceVertical = "idle-overlay";

		[SequenceReference(nameof(Image), allowNullImage: true)]
		[Desc("Sequenz fuer Segmente mit AUSSCHLIESSLICH Ost/West-Nachbarn.")]
		public readonly string SequenceHorizontal = "idle-overlay";

		[PaletteReference(nameof(IsPlayerPalette))]
		public readonly string Palette = null;

		public readonly bool IsPlayerPalette = false;

		public readonly bool IsDecoration = false;

		public override object Create(ActorInitializer init) { return new AotWithWallDirectionalOverlay(init.Self, this); }

		string IWallConnectorInfo.GetWallConnectionType() { return Type; }
	}

	sealed class AotWithWallDirectionalOverlay : PausableConditionalTrait<AotWithWallDirectionalOverlayInfo>,
		INotifyRemovedFromWorld, IWallConnector, ITick
	{
		const int North = 1, South = 4;

		readonly AotWithWallDirectionalOverlayInfo info;
		readonly Animation overlay;
		int adjacent;
		bool dirty = true;
		bool currentIsVertical = true;

		public AotWithWallDirectionalOverlay(Actor self, AotWithWallDirectionalOverlayInfo info)
			: base(info)
		{
			this.info = info;

			var rs = self.Trait<RenderSprites>();
			var image = info.Image ?? rs.GetImage(self);
			overlay = new Animation(self.World, image);
			overlay.PlayRepeating(RenderSprites.NormalizeSequence(overlay, self.GetDamageState(), info.SequenceVertical));

			var anim = new AnimationWithOffset(overlay,
				null,
				() => IsTraitDisabled,
				p => RenderUtils.ZOffsetFromCenter(self, p, 1));

			rs.Add(anim, info.Palette, info.IsPlayerPalette);
		}

		bool IWallConnector.AdjacentWallCanConnect(Actor self, CPos wallLocation, string wallType, out CVec facing)
		{
			facing = wallLocation - self.Location;
			return info.Type == wallType && Math.Abs(facing.X) + Math.Abs(facing.Y) == 1;
		}

		void IWallConnector.SetDirty() { dirty = true; }

		void ITick.Tick(Actor self)
		{
			if (!dirty)
				return;

			var adjacentActors = CVec.Directions.SelectMany(dir =>
				self.World.ActorMap.GetActorsAt(self.Location + dir));

			adjacent = 0;
			foreach (var a in adjacentActors)
			{
				var wc = a.TraitsImplementing<IWallConnector>().FirstEnabledTraitOrDefault();
				if (wc == null || !wc.AdjacentWallCanConnect(a, self.Location, info.Type, out var facing))
					continue;

				if (facing.Y > 0)
					adjacent |= North;
				else if (facing.Y < 0)
					adjacent |= South;
			}

			dirty = false;

			var isVertical = (adjacent & (North | South)) != 0 || adjacent == 0;
			if (isVertical != currentIsVertical)
			{
				currentIsVertical = isVertical;
				overlay.PlayRepeating(RenderSprites.NormalizeSequence(overlay, self.GetDamageState(),
					isVertical ? info.SequenceVertical : info.SequenceHorizontal));
			}
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			var adjacentActorTraits = CVec.Directions.SelectMany(dir =>
					self.World.ActorMap.GetActorsAt(self.Location + dir))
				.SelectMany(a => a.TraitsImplementing<IWallConnector>());

			foreach (var aat in adjacentActorTraits)
				aat.SetDirty();
		}
	}
}
