#region Copyright & License Information
/*
 * Age of Tiberium mod (aotmod) — WithRandomGroundSprite trait.
 * Renders one sprite as a GROUND decal that always draws beneath units. The sequence is taken
 * from a ScatterSpriteInit if the spawner supplied one (so it can vary density with distance),
 * otherwise it is picked deterministically from the actor's cell position. A large negative
 * z-offset keeps the decal below every unit sprite regardless of their relative screen position
 * (the renderable sort key is Pos.Y + Pos.Z + ZOffset), i.e. true ground-layer behaviour. Used
 * for the ore mine's scattered ore/gem clusters. No SharedRandom is drawn.
 */
#endregion

using System;
using OpenRA.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("Renders a single sprite (from a ScatterSpriteInit, or deterministically from the cell) ",
		"as a ground decal drawn beneath units. Does not animate.")]
	public class WithRandomGroundSpriteInfo : TraitInfo, Requires<RenderSpritesInfo>
	{
		[SequenceReference]
		[FieldLoader.Require]
		[Desc("Candidate sequences used when no ScatterSpriteInit is supplied.")]
		public readonly string[] Sequences = Array.Empty<string>();

		[PaletteReference]
		[Desc("Custom palette name (null uses the default).")]
		public readonly string Palette = null;

		[Desc("Z-offset for the decal. Large-negative keeps it beneath every unit regardless of their ",
			"relative screen position (ground-layer behaviour).")]
		public readonly int ZOffset = -8192;

		public override object Create(ActorInitializer init) { return new WithRandomGroundSprite(init, this); }
	}

	public class WithRandomGroundSprite : INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly WithRandomGroundSpriteInfo info;
		readonly RenderSprites rs;
		readonly string sequence;
		AnimationWithOffset awo;

		public WithRandomGroundSprite(ActorInitializer init, WithRandomGroundSpriteInfo info)
		{
			this.info = info;
			var self = init.Self;
			rs = self.Trait<RenderSprites>();

			// The spawner (ScatterDecorationActors) usually dictates the exact sprite so it can make
			// outer clusters weaker. Standalone, fall back to a deterministic per-cell pick.
			var forced = init.GetOrDefault<ScatterSpriteInit>()?.Value;
			if (!string.IsNullOrEmpty(forced))
				sequence = forced;
			else
			{
				var c = self.Location;
				var h = (uint)((c.X * 73856093) ^ (c.Y * 19349663) ^ (c.Layer * 83492791));
				sequence = info.Sequences[(int)(h % (uint)info.Sequences.Length)];
			}
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			var anim = new Animation(self.World, rs.GetImage(self));
			anim.PlayRepeating(sequence);
			anim.IsDecoration = true;

			awo = new AnimationWithOffset(anim, null, null, info.ZOffset);
			rs.Add(awo, info.Palette);
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			if (awo != null)
				rs.Remove(awo);

			awo = null;
		}
	}
}
