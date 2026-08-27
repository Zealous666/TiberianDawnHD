#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Renders a COSMETIC fog-of-war overlay over the owner's own base inside a radius (the GDI
 * Shroud Generator's field). This is the GDI counterpart to the NOD Stealth Generator: instead
 * of turning nearby buildings transparent, the GDI field paints the game's real fog-of-war look
 * over the whole zone. Unlike normal fog it has two twists (per design):
 *   1. Owner + allies ALWAYS see it, even where they have units/vision (real fog would clear).
 *   2. Own units and buildings are drawn UNDER it and stay visible -- the fog only lightly
 *      obscures them, it never hides them. This is achieved by drawing in the IRenderAboveWorld
 *      pass, which runs AFTER all actor sprites but BEFORE the real shroud/fog pass.
 * Enemies are unaffected: they still get the Shroud Generator's CreatesShroud (real black shroud),
 * which is drawn on top of this overlay for them, and the overlay is only drawn for allied viewers
 * anyway.
 *
 * EDGES -- this mirrors ShroudRenderer.GetEdges() exactly, and the subtlety is worth spelling out
 * because getting it wrong produces a hard, blocky border:
 *   - A cell INSIDE the field renders the FULL fog tile (ShroudRenderer.cs:284 -- a cell that is
 *     itself fogged is always fully fogged, never a gradient).
 *   - The soft gradient tiles are drawn on the cells OUTSIDE the field, using the bitmask of which
 *     of their eight neighbours ARE fogged. So the feathering lives in a one-cell ring around the
 *     disc, fading inward. Painting only the inside of the disc leaves no gradient ring at all.
 * Sprites/palette are the game's own fog art (image "shroud", fog-type{a..d} + fog-full, palette
 * "shroud", alpha 0.5), including the four random tile variants, so the field is visually
 * indistinguishable from genuine fog-of-war.
 */
#endregion

using System;
using System.Collections.Immutable;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Draws a cosmetic fog-of-war overlay over the owner's/allies' base inside a radius, above",
		"the actors so units and buildings stay visible under it. See AotFogField.cs.")]
	public class AotFogFieldInfo : ConditionalTraitInfo
	{
		[Desc("Radius of the fog field. Should match the Shroud Generator's CreatesShroud range.")]
		public readonly WDist Range = WDist.FromCells(8);

		[Desc("Sequence image that holds the fog frames (same as ShroudRenderer).")]
		public readonly string Sequence = "shroud";

		[SequenceReference(nameof(Sequence))]
		[Desc("Fog variants providing the 12 directional edge frames (random per cell).")]
		public readonly ImmutableArray<string> FogVariants = ["fog-typea", "fog-typeb", "fog-typec", "fog-typed"];

		[SequenceReference(nameof(Sequence))]
		[Desc("Fog frame for a fully-fogged interior cell.")]
		public readonly string OverrideFullFog = "fog-full";

		[PaletteReference]
		public readonly string FogPalette = "shroud";

		[Desc("Bitfield of fogged directions for each frame (see ShroudRenderer).")]
		public readonly ImmutableArray<int> Index = [12, 9, 8, 3, 1, 6, 4, 2, 13, 11, 7, 14];

		[Desc("Frame index used for the fully-fogged interior cell.")]
		public readonly int OverrideFogIndex = 15;

		[Desc("Extra opacity multiplier on top of the fog sequence's own alpha (0.5).",
			"1.0 => identical strength to the game's real fog.")]
		public readonly float Alpha = 1f;

		public override object Create(ActorInitializer init) { return new AotFogField(init.Self, this); }
	}

	public sealed class AotFogField : ConditionalTrait<AotFogFieldInfo>, IRenderAboveWorld, INotifyActorDisposing
	{
		[Flags]
		enum Edges : byte
		{
			None = 0,
			TopLeft = 0x01,
			TopRight = 0x02,
			BottomRight = 0x04,
			BottomLeft = 0x08,
			AllCorners = TopLeft | TopRight | BottomRight | BottomLeft,
			TopSide = 0x10,
			RightSide = 0x20,
			BottomSide = 0x40,
			LeftSide = 0x80,
			Top = TopSide | TopLeft | TopRight,
			Right = RightSide | TopRight | BottomRight,
			Bottom = BottomSide | BottomRight | BottomLeft,
			Left = LeftSide | TopLeft | BottomLeft,
		}

		readonly World world;
		readonly Map map;
		readonly byte variantStride;
		readonly byte[] edgesToSpriteIndexOffset;

		(Sprite Sprite, float Scale, float Alpha)[] fogSprites;
		TerrainSpriteLayer fogLayer;
		PaletteReference fogPalette;
		bool built;
		bool disposed;

		public AotFogField(Actor self, AotFogFieldInfo info)
			: base(info)
		{
			world = self.World;
			map = world.Map;

			// Mapping of fogged-direction bitset -> sprite slot, identical to ShroudRenderer
			// (non-extended: only the four corner bits are used).
			variantStride = (byte)(info.Index.Length + 1);
			edgesToSpriteIndexOffset = new byte[(int)Edges.AllCorners + 1];
			for (var i = 0; i < info.Index.Length; i++)
				edgesToSpriteIndexOffset[info.Index[i]] = (byte)i;
			edgesToSpriteIndexOffset[info.OverrideFogIndex] = (byte)(variantStride - 1);
		}

		void Build(WorldRenderer wr)
		{
			var sequences = map.Sequences;
			var variantCount = Info.FogVariants.Length;
			fogSprites = new (Sprite, float, float)[variantCount * variantStride];

			for (var j = 0; j < variantCount; j++)
			{
				var fogSequence = sequences.GetSequence(Info.Sequence, Info.FogVariants[j]);
				for (var i = 0; i < Info.Index.Length; i++)
					fogSprites[j * variantStride + i] = (fogSequence.GetSprite(i), fogSequence.Scale, fogSequence.GetAlpha(i));

				var full = sequences.GetSequence(Info.Sequence, Info.OverrideFullFog);
				fogSprites[(j + 1) * variantStride - 1] = (full.GetSprite(0), full.Scale, full.GetAlpha(0));
			}

			fogPalette = wr.Palette(Info.FogPalette);

			var reference = fogSprites[variantStride - 1].Sprite;
			var emptySprite = new Sprite(reference.Sheet, Rectangle.Empty, TextureChannel.Alpha);
			fogLayer = new TerrainSpriteLayer(world, wr, emptySprite, reference.BlendMode, false);

			built = true;
		}

		void Populate(Actor self, WorldRenderer wr)
		{
			// Buildings are static: compute the disc once from the actor's ground centre.
			var center = self.CenterPosition;
			var rangeSq = (long)Info.Range.Length * Info.Range.Length;

			bool InField(CPos c)
			{
				var d = map.CenterOfCell(c) - center;
				return (long)d.X * d.X + (long)d.Y * d.Y <= rangeSq;
			}

			// One cell wider than the disc: the outside ring carries the gradient tiles.
			var r = Info.Range.Length / 1024 + 2;
			for (var dy = -r; dy <= r; dy++)
			{
				for (var dx = -r; dx <= r; dx++)
				{
					var cell = self.Location + new CVec(dx, dy);
					if (!map.Contains(cell))
						continue;

					var variant = (byte)Game.CosmeticRandom.Next(Info.FogVariants.Length);
					var edges = InField(cell) ? (Edges)Info.OverrideFogIndex : GetEdges(cell, InField);
					var (sprite, scale, alpha) = GetSprite(edges, variant);

					var uv = cell.ToMPos(map);
					var pos = map.CenterOfCell(cell);
					var screen = wr.Screen3DPosition(pos - new WVec(0, 0, pos.Z));
					if (sprite != null)
						screen += sprite.Offset - 0.5f * sprite.Size;

					fogLayer.Update(uv, sprite, fogPalette, screen, scale, alpha * Info.Alpha, true);
				}
			}
		}

		Edges GetEdges(CPos cell, Func<CPos, bool> inField)
		{
			// Bit set where the neighbour IS fogged. A set side also implies its two corners --
			// exactly ShroudRenderer.GetEdges(), which is why the result is masked to the corners.
			var edges = Edges.None;
			if (inField(cell + new CVec(0, -1))) edges |= Edges.Top;
			if (inField(cell + new CVec(1, 0))) edges |= Edges.Right;
			if (inField(cell + new CVec(0, 1))) edges |= Edges.Bottom;
			if (inField(cell + new CVec(-1, 0))) edges |= Edges.Left;

			if (inField(cell + new CVec(-1, -1))) edges |= Edges.TopLeft;
			if (inField(cell + new CVec(1, -1))) edges |= Edges.TopRight;
			if (inField(cell + new CVec(1, 1))) edges |= Edges.BottomRight;
			if (inField(cell + new CVec(-1, 1))) edges |= Edges.BottomLeft;

			return edges & Edges.AllCorners;
		}

		(Sprite Sprite, float Scale, float Alpha) GetSprite(Edges edges, int variant)
		{
			if (edges == Edges.None)
				return (null, 1f, 1f);

			return fogSprites[variant * variantStride + edgesToSpriteIndexOffset[(byte)edges]];
		}

		void IRenderAboveWorld.RenderAboveWorld(Actor self, WorldRenderer wr)
		{
			if (IsTraitDisabled)
				return;

			if (!built)
			{
				Build(wr);
				Populate(self, wr);
			}

			// Owner and allies see the field; enemies get the real shroud instead (observers see all).
			var rp = wr.World.RenderPlayer;
			if (rp != null && self.Owner != rp && !self.Owner.IsAlliedWith(rp))
				return;

			fogLayer.Draw(wr.Viewport);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			if (disposed)
				return;

			fogLayer?.Dispose();
			disposed = true;
		}
	}
}
