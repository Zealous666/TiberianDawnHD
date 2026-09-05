#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Visually lowers (sinks) the actor's sprites when standing on specific terrain types.
 */
#endregion

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Visually offsets the actor downward when standing on specific terrain types.")]
	public class AotSinkOnWaterInfo : TraitInfo
	{
		[FieldLoader.Require]
		[Desc("Terrain types that trigger the sink offset.")]
		public readonly ImmutableArray<string> TerrainTypes = [];

		[Desc("Vertical sink distance in WDist units (positive = sink down).")]
		public readonly int SinkAmount = 384;

		public override object Create(ActorInitializer init) { return new AotSinkOnWater(this); }
	}

	public class AotSinkOnWater : ITick, IRenderModifier
	{
		readonly AotSinkOnWaterInfo info;
		bool sinking;

		public AotSinkOnWater(AotSinkOnWaterInfo info)
		{
			this.info = info;
		}

		void ITick.Tick(Actor self)
		{
			var loc = self.Location;
			if (!self.World.Map.Contains(loc))
				return;

			var terrain = self.World.Map.GetTerrainInfo(loc).Type;
			sinking = info.TerrainTypes.Contains(terrain);
		}

		IEnumerable<IRenderable> IRenderModifier.ModifyRender(Actor self, WorldRenderer wr, IEnumerable<IRenderable> r)
		{
			if (!sinking)
				return r;

			// Negative Z lowers the sprite on screen in isometric view
			var offset = new WVec(0, 0, -info.SinkAmount);
			return r.Select(renderable => renderable.OffsetBy(offset));
		}

		IEnumerable<Rectangle> IRenderModifier.ModifyScreenBounds(Actor self, WorldRenderer wr, IEnumerable<Rectangle> bounds)
		{
			return bounds;
		}
	}
}
