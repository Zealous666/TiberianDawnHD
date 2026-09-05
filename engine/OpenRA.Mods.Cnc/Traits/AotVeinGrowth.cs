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
using System.Collections.Generic;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Grows Tiberium veins outward from this actor (the veinhole 'heart') across ground,",
		"irregularly and roughly circular, until it exhausts at MaxRadius. Direct clone of",
		"AotIceGrowth, but spreads over ground terrain instead of water.")]
	sealed class AotVeinGrowthInfo : ConditionalTraitInfo, IEditorActorOptions
	{
		[Desc("Display order for the vein spread size slider in the map editor.")]
		public readonly int EditorMaxRadiusDisplayOrder = 1;

		[Desc("Display order for the spread speed slider in the map editor.")]
		public readonly int EditorGrowthSpeedDisplayOrder = 2;

		[Desc("Upper end of the map editor's spread size slider.")]
		public readonly int EditorMaxRadiusLimit = 30;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			// 5th arg is Ticks = number of scale marks, NOT the step size. It MUST be 0 or >= 2:
			// SliderWidget.Draw divides by (Ticks - 1), so 1 crashes with DivideByZeroException.
			yield return new EditorActorSlider("Geflecht-Ausbreitung (Zellen)", EditorMaxRadiusDisplayOrder,
				1, EditorMaxRadiusLimit, 6,
				actor => actor.GetInitOrDefault<AotVeinMaxRadiusInit>()?.Value ?? MaxRadius,
				(actor, value) => actor.ReplaceInit(new AotVeinMaxRadiusInit((int)value)));

			yield return new EditorActorSlider("Wachstums-Tempo (%)", EditorGrowthSpeedDisplayOrder,
				1, 100, 5,
				actor => actor.GetInitOrDefault<AotVeinGrowthSpeedInit>()?.Value ?? GrowthSpeed,
				(actor, value) => actor.ReplaceInit(new AotVeinGrowthSpeedInit((int)value)));
		}

		[Desc("Base ticks between growth passes at GrowthSpeed 100.")]
		public readonly int Interval = 300;

		[Desc("Random variance in ticks added to Interval each pass (+/- value).")]
		public readonly int IntervalVariance = 125;

		[Desc("SLIDER 1-100: spread speed. 100 = fastest, lower = slower.",
			"Scales the interval between passes: effective = Interval * 100 / GrowthSpeed.")]
		public readonly int GrowthSpeed = 100;

		[Desc("SLIDER 1-x: exhaustion radius in cells; the spread chance reaches zero here.")]
		public readonly int MaxRadius = 14;

		[Desc("Spread chance per pass in percent, at the heart itself.")]
		public readonly int FreezeChance = 28;

		[Desc("Cells with fewer veined neighbours than this never grow. Keeps the mat compact:",
			"sparse cells cannot reach CornerThreshold and would render invisible but walkable.")]
		public readonly int MinFrozenNeighbours = 3;

		[Desc("Cells within this radius of the heart grow immediately at map start, so the",
			"3x3 heart sprite never has bare ground showing under its edges.")]
		public readonly int InitialRadius = 2;

		[Desc("Actor type spawned for each veined cell.")]
		[ActorReference]
		public readonly string SpawnActor = "aot-vein-cell";

		[Desc("Terrain types the veins may grow over (normal ground + desert).")]
		public readonly HashSet<string> GrowthTerrain = ["Clear", "Rough", "Road", "Sand", "Beach"];

		[Desc("Offset from self.Location (== Building.TopLeft for footprint-based actors, NOT",
			"the visual center) to the actual center cell used for seeding and distance math.",
			"A 3x3 Building footprint's center is TopLeft + (1,1); a single-cell Immobile host",
			"needs (0,0). Wrong offset skews the frozen area off-center from the sprite.")]
		public readonly CVec CenterOffset = CVec.Zero;

		public override object Create(ActorInitializer init) { return new AotVeinGrowth(init, this); }
	}

	sealed class AotVeinGrowth : ConditionalTrait<AotVeinGrowthInfo>, ITick, INotifyAddedToWorld
	{
		// Per-actor values from the map editor sliders; fall back to the rules defaults.
		readonly int maxRadius;
		readonly int growthSpeed;
		int ticks;
		AotVeinLayer layer;

		public AotVeinGrowth(ActorInitializer init, AotVeinGrowthInfo info)
			: base(info)
		{
			maxRadius = init.GetValue<AotVeinMaxRadiusInit, int>(info.MaxRadius);
			growthSpeed = init.GetValue<AotVeinGrowthSpeedInit, int>(info.GrowthSpeed);
			ticks = info.Interval;
		}

		bool CanGrowOn(Map map, CPos c)
		{
			return map.Contains(c) && Info.GrowthTerrain.Contains(map.GetTerrainInfo(c).Type);
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			layer = self.World.WorldActor.Trait<AotVeinLayer>();

			// Seed veins under and around the heart: its 3x3 sprite overhangs its cell, so without
			// this there is bare ground along the heart's edges. Square, not FindTilesInCircle
			// (that returns a plus at radius 1 -- see AotIceGrowth for the full explanation).
			var map = self.World.Map;
			var center = self.Location + Info.CenterOffset;
			for (var dy = -Info.InitialRadius; dy <= Info.InitialRadius; dy++)
			{
				for (var dx = -Info.InitialRadius; dx <= Info.InitialRadius; dx++)
				{
					var c = center + new CVec(dx, dy);
					if (layer.Contains(c) || !CanGrowOn(map, c))
						continue;

					var target = c;
					layer.Add(target);
					self.World.AddFrameEndTask(w =>
						w.CreateActor(Info.SpawnActor, new TypeDictionary
						{
							new LocationInit(target),
							new OwnerInit(self.Owner),
						}));
				}
			}

			layer.Add(center);
		}

		int NextInterval(Actor self)
		{
			var speed = growthSpeed.Clamp(1, 100);
			var baseInterval = Info.Interval * 100 / speed;
			var variance = Info.IntervalVariance * 100 / speed;
			return baseInterval + self.World.SharedRandom.Next(variance * 2 + 1) - variance;
		}

		int VeinedNeighbours(CPos c)
		{
			var n = 0;
			foreach (var d in CVec.Directions)
				if (layer.Contains(c + d))
					n++;

			return n;
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled)
				return;

			if (--ticks > 0)
				return;

			ticks = NextInterval(self);

			var map = self.World.Map;
			var hostLoc = self.Location + Info.CenterOffset;
			var radius = maxRadius;
			var newCells = new List<CPos>();

			foreach (var c in map.FindTilesInCircle(hostLoc, radius))
			{
				if (layer.Contains(c) || !CanGrowOn(map, c))
					continue;

				var veined = VeinedNeighbours(c);
				if (veined < Info.MinFrozenNeighbours)
					continue;

				var dist = Math.Max(Math.Abs(c.X - hostLoc.X), Math.Abs(c.Y - hostLoc.Y));
				if (dist >= radius)
					continue;

				var p = Info.FreezeChance * (radius - dist) / radius;
				p = p * (4 + veined) / 8;

				if (self.World.SharedRandom.Next(100) < p)
					newCells.Add(c);
			}

			foreach (var cell in newCells)
			{
				var target = cell;
				layer.Add(target);
				self.World.AddFrameEndTask(w =>
					w.CreateActor(Info.SpawnActor, new TypeDictionary
					{
						new LocationInit(target),
						new OwnerInit(self.Owner),
					}));
			}
		}
	}

	public class AotVeinMaxRadiusInit : ValueActorInit<int>, ISingleInstanceInit
	{
		public AotVeinMaxRadiusInit(int value)
			: base(value) { }
	}

	public class AotVeinGrowthSpeedInit : ValueActorInit<int>, ISingleInstanceInit
	{
		public AotVeinGrowthSpeedInit(int value)
			: base(value) { }
	}
}
