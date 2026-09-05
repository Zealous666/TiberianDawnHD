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
 * Enemies are unaffected: they still get the Shroud Generator's CreatesShroud (real black shroud).
 *
 * UNION RENDERING (2026-08-28) -- multiple generators must merge into ONE continuous fog sea, not
 * a set of overlapping discs. Each field used to draw its own disc with its own alpha-blended
 * tiles and its own soft edge ring, so where two fields overlapped you saw both: the overlap
 * region was drawn twice (denser fog) and both gradient rings showed up as internal seams. Now a
 * single World-level AotFogFieldManager collects every active field and paints the UNION of their
 * cells exactly once: a cell fogged by ANY field is a full interior tile, and the soft gradient
 * only lives on the outer perimeter of the whole union. Interior boundaries between overlapping
 * discs vanish and nothing is double-drawn. AotFogField itself is now just a registrar that hands
 * the manager its centre + radius (read live each frame, so mobile generators work too).
 *
 * EDGES -- mirrors ShroudRenderer.GetEdges() exactly:
 *   - A cell INSIDE the field renders the FULL fog tile (a fogged cell is always fully fogged).
 *   - The soft gradient tiles are drawn on the cells OUTSIDE the field, from the bitmask of which
 *     of their eight neighbours ARE fogged. The feathering lives in a one-cell ring around the
 *     union, fading inward.
 * Sprites/palette are the game's own fog art (image "shroud", fog-type{a..d} + fog-full, palette
 * "shroud", alpha 0.5), including the four random tile variants.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// ---------------------------------------------------------------------------------------------
	// Per-actor field: just registers itself with the world manager and exposes its live disc.
	// ---------------------------------------------------------------------------------------------
	[Desc("Marks this actor as a source of the cosmetic GDI Shroud Generator fog field. The actual",
		"drawing (and the merging of overlapping fields into one continuous fog sea) is done by the",
		"world-level AotFogFieldManager. See AotFogField.cs.")]
	public class AotFogFieldInfo : ConditionalTraitInfo
	{
		[Desc("Radius of the fog field. Should match the Shroud Generator's CreatesShroud range.")]
		public readonly WDist Range = WDist.FromCells(8);

		public override object Create(ActorInitializer init) { return new AotFogField(init.Self, this); }
	}

	public sealed class AotFogField : ConditionalTrait<AotFogFieldInfo>, INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly Actor self;

		public AotFogField(Actor self, AotFogFieldInfo info)
			: base(info)
		{
			this.self = self;
		}

		public WPos CenterPosition => self.CenterPosition;
		public WDist Range => Info.Range;
		public long RangeSq => (long)Info.Range.Length * Info.Range.Length;
		public Player Owner => self.Owner;

		// Contributes to the fog union only while enabled (power on, deployed, ...).
		public bool Active => !IsTraitDisabled;

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			self.World.WorldActor.TraitOrDefault<AotFogFieldManager>()?.Add(this);
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			self.World.WorldActor.TraitOrDefault<AotFogFieldManager>()?.Remove(this);
		}
	}

	// ---------------------------------------------------------------------------------------------
	// World manager: paints the union of all active fog fields as a single continuous layer.
	// ---------------------------------------------------------------------------------------------
	[Desc("aotmod: world-level renderer that merges every AotFogField into one continuous fog sea.",
		"Put one instance on the World actor. Holds the fog art shared by all fields.")]
	public class AotFogFieldManagerInfo : TraitInfo
	{
		[Desc("Sequence image that holds the fog frames (same as ShroudRenderer).")]
		public readonly string Sequence = "shroud";

		[SequenceReference(nameof(Sequence))]
		[Desc("Fog variants providing the directional edge frames (random per cell).")]
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

		public override object Create(ActorInitializer init) { return new AotFogFieldManager(init.Self, this); }
	}

	public sealed class AotFogFieldManager : IRenderAboveWorld, INotifyActorDisposing
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

		readonly AotFogFieldManagerInfo info;
		readonly World world;
		readonly Map map;
		readonly List<AotFogField> fields = [];
		readonly byte variantStride;
		readonly byte[] edgesToSpriteIndexOffset;

		(Sprite Sprite, float Scale, float Alpha)[] fogSprites;
		TerrainSpriteLayer fogLayer;
		PaletteReference fogPalette;
		bool built;
		bool disposed;

		// The set of active discs used for the last rebuild, to detect when nothing changed.
		readonly List<(CPos Cell, long RangeSq)> lastKey = [];
		Player lastRenderPlayer;
		bool haveKey;

		// Cells painted by the previous rebuild, so a shrinking union clears what it vacated.
		readonly HashSet<CPos> paintedCells = [];

		public AotFogFieldManager(Actor self, AotFogFieldManagerInfo info)
		{
			this.info = info;
			world = self.World;
			map = world.Map;

			variantStride = (byte)(info.Index.Length + 1);
			edgesToSpriteIndexOffset = new byte[(int)Edges.AllCorners + 1];
			for (var i = 0; i < info.Index.Length; i++)
				edgesToSpriteIndexOffset[info.Index[i]] = (byte)i;
			edgesToSpriteIndexOffset[info.OverrideFogIndex] = (byte)(variantStride - 1);
		}

		public void Add(AotFogField field)
		{
			if (!fields.Contains(field))
				fields.Add(field);
		}

		public void Remove(AotFogField field)
		{
			fields.Remove(field);
		}

		void Build(WorldRenderer wr)
		{
			var sequences = map.Sequences;
			var variantCount = info.FogVariants.Length;
			fogSprites = new (Sprite, float, float)[variantCount * variantStride];

			for (var j = 0; j < variantCount; j++)
			{
				var fogSequence = sequences.GetSequence(info.Sequence, info.FogVariants[j]);
				for (var i = 0; i < info.Index.Length; i++)
					fogSprites[j * variantStride + i] = (fogSequence.GetSprite(i), fogSequence.Scale, fogSequence.GetAlpha(i));

				var full = sequences.GetSequence(info.Sequence, info.OverrideFullFog);
				fogSprites[(j + 1) * variantStride - 1] = (full.GetSprite(0), full.Scale, full.GetAlpha(0));
			}

			fogPalette = wr.Palette(info.FogPalette);

			var reference = fogSprites[variantStride - 1].Sprite;
			var emptySprite = new Sprite(reference.Sheet, Rectangle.Empty, TextureChannel.Alpha);
			fogLayer = new TerrainSpriteLayer(world, wr, emptySprite, reference.BlendMode, false);

			built = true;
		}

		void IRenderAboveWorld.RenderAboveWorld(Actor self, WorldRenderer wr)
		{
			if (!built)
				Build(wr);

			// Owner and allies see the field; enemies get the real shroud instead (observers see all).
			var rp = wr.World.RenderPlayer;
			var active = fields
				.Where(f => f.Active && (rp == null || f.Owner == rp || f.Owner.IsAlliedWith(rp)))
				.ToList();

			if (KeyChanged(active, rp))
				Rebuild(active, wr);

			if (paintedCells.Count > 0)
				fogLayer.Draw(wr.Viewport);
		}

		// True when the union of active discs (or the render player) differs from the last rebuild.
		bool KeyChanged(List<AotFogField> active, Player rp)
		{
			var key = active
				.Select(f => (Cell: map.CellContaining(f.CenterPosition), f.RangeSq))
				.OrderBy(t => t.Cell.X).ThenBy(t => t.Cell.Y).ThenBy(t => t.RangeSq)
				.ToList();

			if (haveKey && rp == lastRenderPlayer && key.Count == lastKey.Count && key.SequenceEqual(lastKey))
				return false;

			lastKey.Clear();
			lastKey.AddRange(key);
			lastRenderPlayer = rp;
			haveKey = true;
			return true;
		}

		void Rebuild(List<AotFogField> active, WorldRenderer wr)
		{
			bool InAnyField(CPos c)
			{
				var p = map.CenterOfCell(c);
				foreach (var f in active)
				{
					var d = p - f.CenterPosition;
					if ((long)d.X * d.X + (long)d.Y * d.Y <= f.RangeSq)
						return true;
				}

				return false;
			}

			// Bounding box over all discs (one cell of gradient ring included), unioned with the
			// cells painted last time so a shrunk/moved union clears what it no longer covers.
			var toClear = new HashSet<CPos>(paintedCells);
			paintedCells.Clear();

			if (active.Count > 0)
			{
				var minX = int.MaxValue; var minY = int.MaxValue;
				var maxX = int.MinValue; var maxY = int.MinValue;
				foreach (var f in active)
				{
					var cell = map.CellContaining(f.CenterPosition);
					var r = f.Range.Length / 1024 + 2;
					minX = Math.Min(minX, cell.X - r); minY = Math.Min(minY, cell.Y - r);
					maxX = Math.Max(maxX, cell.X + r); maxY = Math.Max(maxY, cell.Y + r);
				}

				for (var y = minY; y <= maxY; y++)
				{
					for (var x = minX; x <= maxX; x++)
					{
						var cell = new CPos(x, y);
						if (!map.Contains(cell))
							continue;

						var edges = InAnyField(cell) ? (Edges)info.OverrideFogIndex : GetEdges(cell, InAnyField);
						var variant = (byte)Game.CosmeticRandom.Next(info.FogVariants.Length);
						var (sprite, scale, alpha) = GetSprite(edges, variant);
						if (sprite == null)
							continue;

						var uv = cell.ToMPos(map);
						var pos = map.CenterOfCell(cell);
						var screen = wr.Screen3DPosition(pos - new WVec(0, 0, pos.Z));
						screen += sprite.Offset - 0.5f * sprite.Size;
						fogLayer.Update(uv, sprite, fogPalette, screen, scale, alpha * info.Alpha, true);

						paintedCells.Add(cell);
						toClear.Remove(cell);
					}
				}
			}

			// Cells that were fog last time but are not any more.
			foreach (var cell in toClear)
				fogLayer.Clear(cell);
		}

		Edges GetEdges(CPos cell, Func<CPos, bool> inField)
		{
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

		void INotifyActorDisposing.Disposing(Actor self)
		{
			if (disposed)
				return;

			fogLayer?.Dispose();
			disposed = true;
		}
	}
}
