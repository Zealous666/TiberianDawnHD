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
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Renders the spy as a disguised E1 to enemy viewers and as the true spy sprite to own/allied viewers.")]
	sealed class AotSpyRendererInfo : TraitInfo, Requires<RenderSpritesInfo>
	{
		[Desc("Actor image to show to enemy viewers.")]
		public readonly string DisguiseImage = "e1";

		[Desc("Sequence to use when the spy is standing still.")]
		public readonly string StandSequence = "stand";

		[Desc("Sequence to use when the spy is moving.")]
		public readonly string WalkSequence = "run";

		public override object Create(ActorInitializer init) { return new AotSpyRenderer(init, this); }
	}

	sealed class AotSpyRenderer : IRenderModifier, ITick
	{
		readonly AotSpyRendererInfo info;
		readonly Animation disguiseAnim;
		readonly Mobile mobile;

		public AotSpyRenderer(ActorInitializer init, AotSpyRendererInfo info)
		{
			this.info = info;
			var self = init.Self;
			mobile = self.TraitOrDefault<Mobile>();
			disguiseAnim = new Animation(self.World, info.DisguiseImage, RenderSprites.MakeFacingFunc(self));
			disguiseAnim.PlayRepeating(info.StandSequence);
		}

		void ITick.Tick(Actor self)
		{
			disguiseAnim.Tick();

			var moving = mobile != null && mobile.IsMovingBetweenCells;
			var seq = moving && disguiseAnim.HasSequence(info.WalkSequence) ? info.WalkSequence : info.StandSequence;
			if (disguiseAnim.CurrentSequence?.Name != seq)
				disguiseAnim.PlayRepeating(seq);
		}

		IEnumerable<IRenderable> IRenderModifier.ModifyRender(Actor self, WorldRenderer wr, IEnumerable<IRenderable> r)
		{
			var renderPlayer = self.World.RenderPlayer;

			// Allies and own player always see the true spy sprite.
			if (renderPlayer == null || self.Owner.IsAlliedWith(renderPlayer))
				return r;

			// Enemy viewers see the disguise sprite with the viewer's own player color.
			var palette = wr.Palette("player" + renderPlayer.InternalName);
			var disguiseRenderables = disguiseAnim.Render(self.CenterPosition, WVec.Zero, 0, palette);

			// Keep decorations (health bar, pips, etc.) but replace body sprites.
			return r.Where(a => a.IsDecoration).Concat(disguiseRenderables);
		}

		IEnumerable<Rectangle> IRenderModifier.ModifyScreenBounds(Actor self, WorldRenderer wr, IEnumerable<Rectangle> bounds)
		{
			return bounds;
		}
	}
}
