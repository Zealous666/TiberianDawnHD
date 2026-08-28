#region Copyright & License Information
/*
 * Age of Tiberium mod (aotmod) — ScatterDecorationActors trait.
 * Spawns a set of non-blocking decoration actors (scattered ore / gem clusters) on a ring
 * around this actor when it is added to the world, and removes them again when it leaves
 * (so the clusters vanish together with the mine). The snow or temperate variant is chosen per
 * cell from its base-ground terrain type (Clear.Snow -> snow, grass/Clear -> temperate), so a
 * mixed AOT_ARCTIC map shows both. Which cells get a cluster is derived by hashing cell positions
 * — no SharedRandom is drawn, so the result is
 * identical on every client and reproducible across saves/reloads. Never runs in the editor.
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Spawns non-blocking decoration actors (e.g. scattered ore/gem clusters) on a ring ",
		"around this actor and removes them when it leaves the world. Picks the snow or the ",
		"temperate variant per cell from the terrain type. Deterministic (sync/replay safe). ",
		"Not spawned in the map editor.")]
	public class ScatterDecorationActorsInfo : TraitInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Decoration actor spawned on non-snow ground.")]
		public readonly string TemperateActor = null;

		[ActorReference]
		[FieldLoader.Require]
		[Desc("Decoration actor spawned on snow ground.")]
		public readonly string SnowActor = null;

		[Desc("Terrain types the clusters may be placed on. Deliberately only the flat, open base-ground ",
			"types -- this keeps clusters off cliffs, rough, roads, beaches and water, and (because those ",
			"shared types cannot be told snowy from temperate) avoids picking the wrong variant on them.")]
		public readonly HashSet<string> AllowedTerrainTypes = new() { "Clear", "Clear.Snow", "ClearNoSmudges" };

		[Desc("Of the allowed terrain types, the ones that use the SNOW variant. The rest use temperate. ",
			"So an AOT_ARCTIC map with both grass (Clear) and snow (Clear.Snow) picks each per cell.")]
		public readonly HashSet<string> SnowTerrainTypes = new() { "Clear.Snow" };

		[Desc("Top-left corner of the scatter box, relative to this actor's top-left cell.")]
		public readonly CVec TopLeft = new(-3, -3);

		[Desc("Bottom-right corner of the scatter box, relative to this actor's top-left cell.")]
		public readonly CVec BottomRight = new(4, 4);

		[Desc("Cells inside this box (relative to top-left) are skipped — the building + bib.")]
		public readonly CVec ExcludeTopLeft = new(0, 0);

		public readonly CVec ExcludeBottomRight = new(1, 3);

		[Desc("Outer radius (from the mine centre) of the scatter area. Cells beyond it are skipped, ",
			"so the clusters form a circle around the mine instead of filling the square box.")]
		public readonly WDist Radius = new(3072);

		[Desc("Distance within which clusters are full and densely placed. Beyond it, placement and ",
			"density fade towards the outer radius -- because the 2x2 mine fills the true centre, this ",
			"should sit at the ring of cells hugging the footprint so that ring reads as full/dense.")]
		public readonly WDist FullRadius = new(1280);

		[Desc("Inner radius: cells closer than this to the mine centre are skipped (0 = none).")]
		public readonly WDist InnerRadius = WDist.Zero;

		[Desc("Placement chance (%) for a cell at the mine centre. Falls off linearly to EdgeChance at ",
			"the outer radius, so the field is dense in the middle and sparse/irregular at the rim.")]
		public readonly int CentreChance = 90;

		[Desc("Placement chance (%) for a cell at the outer radius.")]
		public readonly int EdgeChance = 30;

		[Desc("Number of ore/gem shape variants baked into the sprite image (sequences are laid out as ",
			"variant*2 + densityLevel, where level 0 = weak/sparse and level 1 = full).")]
		public readonly int Variants = 4;

		public override object Create(ActorInitializer init) { return new ScatterDecorationActors(this); }
	}

	// Runtime-only init: tells a spawned decoration actor which sprite sequence to render, so the
	// spawner can make outer clusters weaker. Not saved -- the mine re-spawns the clusters on load.
	public class ScatterSpriteInit : ValueActorInit<string>, ISingleInstanceInit
	{
		public ScatterSpriteInit(string value)
			: base(value) { }
	}

	public class ScatterDecorationActors : INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly ScatterDecorationActorsInfo info;
		readonly List<Actor> spawned = new();

		public ScatterDecorationActors(ScatterDecorationActorsInfo info) { this.info = info; }

		static uint Hash(CPos c, int salt)
		{
			return (uint)((c.X * 73856093) ^ (c.Y * 19349663) ^ (salt * 83492791));
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			// Runtime-only decoration: never materialise phantom actors in the editor world.
			if (self.World.Type == WorldType.Editor)
				return;

			self.World.AddFrameEndTask(w =>
			{
				var map = w.Map;
				var origin = self.Location;

				var centre = self.CenterPosition;
				var outer = info.Radius.Length;
				var inner = info.InnerRadius.Length;
				var full = info.FullRadius.Length;
				var fade = Math.Max(1, outer - full);

				for (var dy = info.TopLeft.Y; dy <= info.BottomRight.Y; dy++)
				{
					for (var dx = info.TopLeft.X; dx <= info.BottomRight.X; dx++)
					{
						if (dx >= info.ExcludeTopLeft.X && dx <= info.ExcludeBottomRight.X &&
							dy >= info.ExcludeTopLeft.Y && dy <= info.ExcludeBottomRight.Y)
							continue;

						var cell = origin + new CVec(dx, dy);
						if (!map.Contains(cell))
							continue;

						// Circular scatter area (corners of the square box stay empty).
						var d = (map.CenterOfCell(cell) - centre).HorizontalLength;
						if (d > outer || d < inner)
							continue;

						// Only flat open base-ground -- keeps clusters off cliffs/rough/road/beach/water
						// (and off the shared types whose snow-vs-temperate variant can't be told apart).
						var type = map.GetTerrainInfo(cell).Type;
						if (!info.AllowedTerrainTypes.Contains(type))
							continue;

						if (w.ActorMap.AnyActorsAt(cell))
							continue;

						// 0 within FullRadius (the ring hugging the mine), rising to 1000 at the outer
						// radius -- drives an organic falloff: denser placement AND fuller sprites near
						// the mine, sparser/weaker at the rim.
						var t = (int)Math.Clamp((long)(d - full) * 1000 / fade, 0, 1000);

						// Placement chance falls off with distance (irregular via the per-cell hash).
						var chance = info.CentreChance + (info.EdgeChance - info.CentreChance) * t / 1000;
						if (Hash(cell, 1) % 100 >= (uint)Math.Clamp(chance, 0, 100))
							continue;

						// Density: full near the centre, weak near the rim, with a hashed boundary so
						// the transition is irregular rather than a clean concentric ring.
						var dense = Hash(cell, 3) % 1000 < (uint)(1000 - t);
						var variant = (int)(Hash(cell, 2) % (uint)Math.Max(1, info.Variants));
						var index = variant * 2 + (dense ? 1 : 0);
						var seq = index == 0 ? "idle" : "c" + index;

						// Variant strictly from the cell's own base-ground type (per-cell, so a mixed
						// arctic map picks snow on Clear.Snow and temperate on grass/Clear).
						var snow = info.SnowTerrainTypes.Contains(type);

						spawned.Add(w.CreateActor(snow ? info.SnowActor : info.TemperateActor,
						[
							new LocationInit(cell),
							new OwnerInit(self.Owner),
							new ScatterSpriteInit(seq),
						]));
					}
				}
			});
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			foreach (var a in spawned)
				if (a != null && !a.IsDead)
					a.Dispose();

			spawned.Clear();
		}
	}
}
