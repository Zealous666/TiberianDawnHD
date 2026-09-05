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
	[Desc("Grows ice outward from this actor across open water, irregularly and roughly circular,",
		"until it exhausts at MaxRadius. Each pass, every water cell touching ice may freeze with",
		"a probability that decays towards the radius.")]
	sealed class AotIceGrowthInfo : ConditionalTraitInfo, IEditorActorOptions
	{
		[Desc("Display order for the ice spread size slider in the map editor.")]
		public readonly int EditorMaxRadiusDisplayOrder = 1;

		[Desc("Display order for the freezing speed slider in the map editor.")]
		public readonly int EditorGrowthSpeedDisplayOrder = 2;

		[Desc("Upper end of the map editor's spread size slider.")]
		public readonly int EditorMaxRadiusLimit = 30;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			// 5th arg is Ticks = number of scale marks, NOT the step size. It MUST be 0 or >= 2:
			// SliderWidget.Draw divides by (Ticks - 1), so 1 crashes with DivideByZeroException
			// the moment the actor is selected in the editor.
			yield return new EditorActorSlider("Eis-Ausbreitung (Zellen)", EditorMaxRadiusDisplayOrder,
				1, EditorMaxRadiusLimit, 6,
				actor => actor.GetInitOrDefault<AotIceMaxRadiusInit>()?.Value ?? MaxRadius,
				(actor, value) => actor.ReplaceInit(new AotIceMaxRadiusInit((int)value)));

			yield return new EditorActorSlider("Gefrier-Tempo (%)", EditorGrowthSpeedDisplayOrder,
				1, 100, 5,
				actor => actor.GetInitOrDefault<AotIceGrowthSpeedInit>()?.Value ?? GrowthSpeed,
				(actor, value) => actor.ReplaceInit(new AotIceGrowthSpeedInit((int)value)));
		}

		[Desc("Base ticks between growth passes at GrowthSpeed 100.")]
		public readonly int Interval = 300;

		[Desc("Random variance in ticks added to Interval each pass (+/- value).")]
		public readonly int IntervalVariance = 125;

		[Desc("SLIDER 1-100: freezing speed. 100 = fastest (the tuned default), lower = slower.",
			"Scales the interval between passes: effective = Interval * 100 / GrowthSpeed.")]
		public readonly int GrowthSpeed = 100;

		[Desc("SLIDER 1-x: exhaustion radius in cells; the freeze chance reaches zero here and",
			"the ice stops spreading. 14 covers most of the Polar Panic lake.")]
		public readonly int MaxRadius = 14;

		[Desc("Freeze chance per pass in percent, at the host itself.")]
		public readonly int FreezeChance = 28;

		[Desc("Cells with fewer frozen neighbours than this never freeze. Keeps the sheet compact:",
			"sparse cells cannot reach CornerThreshold and would render invisible but walkable.",
			"Measured on the Polar Panic lake with CornerThreshold 4: 2 -> 11 invisible cells,",
			"3 -> only 3.")]
		public readonly int MinFrozenNeighbours = 3;

		[Desc("Cells within this radius of the host freeze immediately at map start, so the host",
			"floe sprite (up to 2x2 cells) never has open water showing under its edges.")]
		public readonly int InitialRadius = 1;

		[Desc("Terrain type of open water that ice may grow over.")]
		public readonly string WaterTerrain = "Water";

		public override object Create(ActorInitializer init) { return new AotIceGrowth(init, this); }
	}

	sealed class AotIceGrowth : ConditionalTrait<AotIceGrowthInfo>, ITick, INotifyAddedToWorld
	{
		// Per-actor values from the map editor sliders; fall back to the rules defaults.
		readonly int maxRadius;
		readonly int growthSpeed;
		int ticks;
		AotIceLayer layer;

		public AotIceGrowth(ActorInitializer init, AotIceGrowthInfo info)
			: base(info)
		{
			maxRadius = init.GetValue<AotIceMaxRadiusInit, int>(info.MaxRadius);
			growthSpeed = init.GetValue<AotIceGrowthSpeedInit, int>(info.GrowthSpeed);
			ticks = info.Interval;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			layer = self.World.WorldActor.Trait<AotIceLayer>();

			// NOTE: do NOT layer.Add(self.Location) before the seed loop -- the loop skips cells
			// already in the layer, so the host's own cell would never get an ice actor and open
			// water stayed visible in a one-cell square right under the floe. The seed below
			// covers the host cell itself; the loop adds it to the layer.

			// Seed ice under and around the host: its sprite overhangs the cell it sits on
			// (ICE01 is 2x2 cells), so without this there is open water along the floe's edges.
			// Deliberately a square, NOT FindTilesInCircle: that buckets by ceil(sqrt(dx^2+dy^2)),
			// so radius 1 returns a plus of 5 cells without the diagonals -- too small to cover a
			// 2x2 sprite, and it leaves the neighbours below MinFrozenNeighbours so nothing grows.
			var map = self.World.Map;
			for (var dy = -Info.InitialRadius; dy <= Info.InitialRadius; dy++)
			{
				for (var dx = -Info.InitialRadius; dx <= Info.InitialRadius; dx++)
				{
					var c = self.Location + new CVec(dx, dy);
					if (!map.Contains(c) || layer.Contains(c) || map.GetTerrainInfo(c).Type != Info.WaterTerrain)
						continue;

					// aotmod: ice is a cell layer now (see AotIceLayer/AotIceRenderer) -- just seed the cell.
					layer.Add(c);
				}
			}

			// Fallback: if the host does not sit on water its own cell got no actor above,
			// but it must still count as ice so the grown cells blend into the floe.
			layer.Add(self.Location);
		}

		int NextInterval(Actor self)
		{
			var speed = growthSpeed.Clamp(1, 100);
			var baseInterval = Info.Interval * 100 / speed;
			var variance = Info.IntervalVariance * 100 / speed;
			return baseInterval + self.World.SharedRandom.Next(variance * 2 + 1) - variance;
		}

		int FrozenNeighbours(CPos c)
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
			var hostLoc = self.Location;
			var maxRadius = this.maxRadius;
			var newCells = new List<CPos>();

			foreach (var c in map.FindTilesInCircle(hostLoc, maxRadius))
			{
				if (layer.Contains(c) || map.GetTerrainInfo(c).Type != Info.WaterTerrain)
					continue;

				var frozen = FrozenNeighbours(c);
				if (frozen < Info.MinFrozenNeighbours)
					continue;

				// Chebyshev distance keeps the frozen area round rather than diamond shaped.
				var dist = Math.Max(Math.Abs(c.X - hostLoc.X), Math.Abs(c.Y - hostLoc.Y));
				if (dist >= maxRadius)
					continue;

				// p = FreezeChance * (1 - dist/MaxRadius) * (0.5 + frozen/8), all in percent.
				var p = Info.FreezeChance * (maxRadius - dist) / maxRadius;
				p = p * (4 + frozen) / 8;

				if (self.World.SharedRandom.Next(100) < p)
					newCells.Add(c);
			}

			foreach (var cell in newCells)
			{
				// aotmod: ice is a cell layer now, not one actor per cell. Adding to the layer applies
				// the Ice terrain override and flags the cell (and its neighbours) for AotIceRenderer.
				layer.Add(cell);
			}
		}
	}

	public class AotIceMaxRadiusInit : ValueActorInit<int>, ISingleInstanceInit
	{
		public AotIceMaxRadiusInit(int value)
			: base(value) { }
	}

	public class AotIceGrowthSpeedInit : ValueActorInit<int>, ISingleInstanceInit
	{
		public AotIceGrowthSpeedInit(int value)
			: base(value) { }
	}
}
