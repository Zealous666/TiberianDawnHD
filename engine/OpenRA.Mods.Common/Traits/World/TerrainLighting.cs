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
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.World | SystemActors.EditorWorld)]
	[Desc("Add to the world actor to apply a global lighting tint and allow actors using the TerrainLightSource to add localised lighting.")]
	public class TerrainLightingInfo : TraitInfo, ILobbyCustomRulesIgnore
	{
		public readonly float Intensity = 1;
		public readonly float HeightStep = 0;
		public readonly float RedTint = 1;
		public readonly float GreenTint = 1;
		public readonly float BlueTint = 1;

		[Desc("Size of light source partition bins (cells)")]
		public readonly int BinSize = 10;

		public override object Create(ActorInitializer init) { return new TerrainLighting(init.World, this); }
	}

	public sealed class TerrainLighting : ITerrainLighting
	{
		sealed class LightSource(WPos pos, CPos cell, WDist range, float intensity, in float3 tint,
			float brightness, bool ignoresGlobalScale)
		{
			public readonly WPos Pos = pos;
			public readonly CPos Cell = cell;
			public readonly WDist Range = range;
			public readonly float Intensity = intensity;
			public readonly float3 Tint = tint;

			// aotmod (2026-07-28): per-source dimmer, 0-1. The rules value is the maximum.
			public readonly float Brightness = brightness;

			// aotmod (2026-07-28): "always on" -- the day/night fade below does not apply, so this
			// source burns at full strength at noon as well. The storm blackout still switches it off.
			public readonly bool IgnoresGlobalScale = ignoresGlobalScale;
		}

		readonly TerrainLightingInfo info;
		readonly Map map;
		readonly Dictionary<int, LightSource> lightSources = [];
		readonly SpatiallyPartitioned<LightSource> partitionedLightSources;

		// aotmod (2026-07-26): global tint/intensity are runtime-mutable so a day/night cycle can
		// drive them (see AotDayNightCycle). They start from the Info defaults, so a map without a
		// cycle actor behaves exactly as before.
		float3 globalTint;
		float globalIntensity;

		// aotmod (2026-07-26): scales the contribution of ALL local light sources (0 = off,
		// 1 = full). The day/night cycle drives this towards 1 as it gets dark, so lamps and
		// glowing tiberium fade in at dusk instead of washing out the map at noon. Stays at 1
		// on maps without a cycle actor, i.e. unchanged "always on" behaviour.
		float lightSourceScale = 1f;

		// aotmod (2026-07-28): hard off switch for ALL local light sources, including the ones flagged
		// "always on". Driven by the Ion Storm superpower -- during a storm the map goes fully dark.
		bool lightSourcesDisabled;
		int nextLightSourceToken = 1;

		public event Action<MPos> CellChanged = null;

		public TerrainLighting(World world, TerrainLightingInfo info)
		{
			this.info = info;
			map = world.Map;
			globalTint = new float3(info.RedTint, info.GreenTint, info.BlueTint);
			globalIntensity = info.Intensity;

			var tileScale = map.Grid.TileScale;
			partitionedLightSources = new SpatiallyPartitioned<LightSource>(
				(map.MapSize.Width + 1) * tileScale,
				(map.MapSize.Height + 1) * tileScale,
				info.BinSize * tileScale);
		}

		static Rectangle Bounds(LightSource source)
		{
			var c = source.Pos;
			var r = source.Range.Length;
			return new Rectangle(c.X - r, c.Y - r, 2 * r, 2 * r);
		}

		public int AddLightSource(WPos pos, WDist range, float intensity, in float3 tint,
			float brightness = 1f, bool ignoresGlobalScale = false)
		{
			var token = nextLightSourceToken++;
			var source = new LightSource(pos, map.CellContaining(pos), range, intensity, tint,
				brightness, ignoresGlobalScale);
			var bounds = Bounds(source);
			lightSources.Add(token, source);
			partitionedLightSources.Add(source, bounds);

			if (CellChanged != null)
				foreach (var c in map.FindTilesInCircle(source.Cell, (source.Range.Length + 1023) / 1024))
					CellChanged(c.ToMPos(map));

			return token;
		}

		// aotmod (2026-07-26): current global values, so a driver can interpolate from where it is.
		public float GlobalIntensity => globalIntensity;
		public float3 GlobalTint => globalTint;

		// aotmod (2026-07-26): set the global lighting without touching any cell. Deliberately does
		// NOT invalidate: a full-map invalidation is far too expensive to run per change (every
		// subscribed TerrainSpriteLayer -- 8 remaster terrain layers plus resource/smudge layers --
		// recomputes 4 TintAt samples per cell). The caller drives InvalidateRow incrementally
		// instead, spreading the cost over many ticks. Actors/voxels need no invalidation at all:
		// SpriteRenderable/ModelRenderable call TintAt live every frame.
		public void SetGlobalLighting(float intensity, in float3 tint)
		{
			globalIntensity = intensity;
			globalTint = tint;
		}

		// aotmod (2026-07-26): 0 = local lights fully off, 1 = full strength.
		public void SetLightSourceScale(float scale)
		{
			lightSourceScale = scale;
		}

		// aotmod (2026-07-28): true = every local light goes out, "always on" sources included.
		public void SetLightSourcesDisabled(bool disabled)
		{
			lightSourcesDisabled = disabled;
		}

		// aotmod (2026-07-26): invalidate one map row (all cells with this V coordinate).
		public void InvalidateRow(int v)
		{
			if (CellChanged == null || v < 0 || v >= map.MapSize.Height)
				return;

			for (var u = 0; u < map.MapSize.Width; u++)
				CellChanged(new MPos(u, v));
		}

		public void RemoveLightSource(int token)
		{
			if (!lightSources.TryGetValue(token, out var source))
				return;

			lightSources.Remove(token);
			partitionedLightSources.Remove(source);
			if (CellChanged != null)
				foreach (var c in map.FindTilesInCircle(source.Cell, (source.Range.Length + 1023) / 1024))
					CellChanged(c.ToMPos(map));
		}

		float3 ITerrainLighting.TintAt(WPos pos)
		{
			using (new PerfSample("terrain_lighting"))
			{
				var uv = map.CellContaining(pos).ToMPos(map);
				var tint = globalTint;
				if (!map.Height.Contains(uv))
					return tint;

				var intensity = globalIntensity + info.HeightStep * map.Height[uv];
				// aotmod (2026-07-28): the lightSourceScale == 0 shortcut cannot be taken any more --
				// "always on" sources ignore that scale and still have to be summed at high noon.
				if (lightSources.Count > 0 && !lightSourcesDisabled)
				{
					foreach (var source in partitionedLightSources.At(new int2(pos.X, pos.Y)))
					{
						var scale = (source.IgnoresGlobalScale ? 1f : lightSourceScale) * source.Brightness;
						if (scale <= 0f)
							continue;

						var range = source.Range.Length;
						var distance = (source.Pos - pos).Length;
						if (distance > range)
							continue;

						var falloff = (range - distance) * 1f / range * scale;
						intensity += falloff * source.Intensity;
						tint += falloff * source.Tint;
					}
				}

				return intensity * tint;
			}
		}
	}
}
