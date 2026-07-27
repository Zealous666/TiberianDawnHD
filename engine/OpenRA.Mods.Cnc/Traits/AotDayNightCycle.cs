#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Drives the world's TerrainLighting global tint/intensity on a virtual 24h clock, so the map
 * darkens towards night and brightens towards day. Placed as an invisible editor-only marker
 * actor (see aot-light-system), with map editor sliders for start time and cycle length.
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Runs a virtual day/night clock and feeds it into the world's TerrainLighting trait.",
		"Terrain is re-tinted by invalidating map rows incrementally (spread over SweepTicks) so a",
		"transition never causes a frame spike; actors and voxels need no invalidation at all since",
		"SpriteRenderable/ModelRenderable sample TintAt live each frame.")]
	sealed class AotDayNightCycleInfo : ConditionalTraitInfo, IEditorActorOptions
	{
		[Desc("Display order for the start time slider in the map editor.")]
		public readonly int EditorStartHourDisplayOrder = 1;

		[Desc("Display order for the cycle length slider in the map editor.")]
		public readonly int EditorCycleMinutesDisplayOrder = 2;

		[Desc("Display order for the freeze time checkbox in the map editor.")]
		public readonly int EditorFreezeTimeDisplayOrder = 3;

		[Desc("CHECKBOX: hold the clock at StartHour instead of advancing it. The lighting for that",
			"hour is still applied once, so the map keeps a fixed time of day.")]
		public readonly bool FreezeTime = false;

		[Desc("SLIDER 0-23: hour of the virtual day the map starts at.")]
		public readonly int StartHour = 12;

		[Desc("SLIDER: real-time minutes for one full 24h cycle. 60 = one hour of play per day.")]
		public readonly int CycleMinutes = 60;

		[Desc("Upper end of the map editor's cycle length slider (minutes).")]
		public readonly int EditorCycleMinutesLimit = 240;

		[Desc("Virtual hour at which dusk begins.")]
		public readonly int DuskHour = 18;

		[Desc("Virtual hour at which dawn begins.")]
		public readonly int DawnHour = 6;

		[Desc("Length of the dusk/dawn fades in virtual hours.")]
		public readonly int TransitionHours = 2;

		[Desc("Global lighting intensity at full day.")]
		public readonly float DayIntensity = 1.0f;

		[Desc("Global colour tint at full day (neutral by default).")]
		public readonly float DayRedTint = 1.0f;
		public readonly float DayGreenTint = 1.0f;
		public readonly float DayBlueTint = 1.0f;

		[Desc("Global lighting intensity at full night.")]
		public readonly float NightIntensity = 0.55f;

		[Desc("Global colour tint at full night. Slightly blue for a moonlit look.")]
		public readonly float NightRedTint = 0.85f;
		public readonly float NightGreenTint = 0.90f;
		public readonly float NightBlueTint = 1.15f;

		[Desc("Ticks a full-map terrain re-tint sweep is spread across. Higher = smaller per-tick",
			"cost but slower propagation. The sweep only runs while the lighting is actually",
			"changing, so a static day or night costs nothing.")]
		public readonly int SweepTicks = 25;

		[Desc("Minimum lighting change before a re-tint sweep is scheduled. Guards against sweeping",
			"every tick for changes far below the visible threshold.")]
		public readonly float Epsilon = 0.002f;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			// 5th arg is Ticks = number of scale marks, NOT the step size. It MUST be 0 or >= 2:
			// SliderWidget.Draw divides by (Ticks - 1), so 1 crashes with DivideByZeroException
			// the moment the actor is selected in the editor (same trap as AotIceGrowth).
			yield return new EditorActorSlider("Start-Uhrzeit (h)", EditorStartHourDisplayOrder,
				0, 23, 24,
				actor => actor.GetInitOrDefault<AotDayNightStartHourInit>()?.Value ?? StartHour,
				(actor, value) => actor.ReplaceInit(new AotDayNightStartHourInit((int)value)));

			yield return new EditorActorSlider("24h-Zyklus (Echtzeit-Minuten)", EditorCycleMinutesDisplayOrder,
				5, EditorCycleMinutesLimit, 12,
				actor => actor.GetInitOrDefault<AotDayNightCycleMinutesInit>()?.Value ?? CycleMinutes,
				(actor, value) => actor.ReplaceInit(new AotDayNightCycleMinutesInit((int)value)));

			yield return new EditorActorCheckbox("Zeit einfrieren", EditorFreezeTimeDisplayOrder,
				actor => actor.GetInitOrDefault<AotDayNightFreezeTimeInit>()?.Value ?? FreezeTime,
				(actor, value) => actor.ReplaceInit(new AotDayNightFreezeTimeInit(value)));
		}

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			if (!rules.Actors[SystemActors.World].HasTraitInfo<TerrainLightingInfo>())
				throw new YamlException($"{nameof(AotDayNightCycle)} requires the world {nameof(TerrainLighting)} trait.");
		}

		public override object Create(ActorInitializer init) { return new AotDayNightCycle(init, this); }
	}

	sealed class AotDayNightCycle : ConditionalTrait<AotDayNightCycleInfo>, ITick, INotifyAddedToWorld
	{
		// Per-actor values from the map editor sliders/checkbox; fall back to the rules defaults.
		readonly int startHour;
		readonly int cycleMinutes;
		readonly bool freezeTime;

		TerrainLighting lighting;
		Map map;

		long ticksPerDay;
		int rowsPerTick;

		int elapsed;
		int sweepRow;
		int pendingRows;

		float appliedIntensity = float.NaN;
		float3 appliedTint;

		public AotDayNightCycle(ActorInitializer init, AotDayNightCycleInfo info)
			: base(info)
		{
			startHour = init.GetValue<AotDayNightStartHourInit, int>(info.StartHour);
			cycleMinutes = init.GetValue<AotDayNightCycleMinutesInit, int>(info.CycleMinutes);
			freezeTime = init.GetValue<AotDayNightFreezeTimeInit, bool>(info.FreezeTime);
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			lighting = self.World.WorldActor.Trait<TerrainLighting>();
			map = self.World.Map;

			// Timestep is milliseconds per tick (40 by default = 25 ticks/second).
			var timestep = Math.Max(1, self.World.Timestep);
			ticksPerDay = Math.Max(1L, cycleMinutes.Clamp(1, 24 * 60) * 60L * 1000L / timestep);

			rowsPerTick = Math.Max(1, map.MapSize.Height / Math.Max(1, Info.SweepTicks));

			// Start at the configured hour rather than midnight.
			elapsed = (int)(startHour.Clamp(0, 23) * ticksPerDay / 24);
		}

		float CurrentHour()
		{
			return 24f * (elapsed % ticksPerDay) / ticksPerDay;
		}

		// 1 = full day, 0 = full night. Linear across the dusk/dawn windows; the caller smooths it.
		float DayFactor(float hour)
		{
			var transition = Math.Max(1, Info.TransitionHours);
			var duskStart = Info.DuskHour;
			var duskEnd = duskStart + transition;
			var dawnStart = Info.DawnHour;
			var dawnEnd = dawnStart + transition;

			if (hour >= dawnEnd && hour < duskStart)
				return 1f;

			if (hour >= duskStart && hour < duskEnd)
				return 1f - (hour - duskStart) / transition;

			if (hour >= dawnStart && hour < dawnEnd)
				return (hour - dawnStart) / transition;

			return 0f;
		}

		// Smoothstep: removes the hard corners at the start/end of each fade.
		static float Smooth(float t)
		{
			t = t.Clamp(0f, 1f);
			return t * t * (3f - 2f * t);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || lighting == null)
				return;

			// "Zeit einfrieren": the clock stays at StartHour. The lighting for that hour is still
			// applied on the first tick below (appliedIntensity is NaN until then), so the map just
			// keeps a fixed time of day instead of losing the tint entirely.
			if (!freezeTime)
				elapsed++;

			var t = Smooth(DayFactor(CurrentHour()));
			var intensity = float2.Lerp(Info.NightIntensity, Info.DayIntensity, t);
			var tint = new float3(
				float2.Lerp(Info.NightRedTint, Info.DayRedTint, t),
				float2.Lerp(Info.NightGreenTint, Info.DayGreenTint, t),
				float2.Lerp(Info.NightBlueTint, Info.DayBlueTint, t));

			if (float.IsNaN(appliedIntensity) || Changed(intensity, tint))
			{
				lighting.SetGlobalLighting(intensity, tint);

				// Local lights (lamps, buildings, tiberium) fade in as it gets dark: full strength
				// at night, off at noon. Without this they would wash out the map in broad daylight.
				lighting.SetLightSourceScale(1f - t);

				appliedIntensity = intensity;
				appliedTint = tint;

				// Schedule a full map's worth of rows. Repeated changes keep topping this up, so a
				// running transition sweeps continuously and a static day/night stops sweeping.
				pendingRows = map.MapSize.Height;
			}

			if (pendingRows > 0)
			{
				var n = Math.Min(rowsPerTick, pendingRows);
				for (var i = 0; i < n; i++)
				{
					lighting.InvalidateRow(sweepRow);
					sweepRow = (sweepRow + 1) % map.MapSize.Height;
				}

				pendingRows -= n;
			}
		}

		bool Changed(float intensity, in float3 tint)
		{
			var e = Info.Epsilon;
			return Math.Abs(intensity - appliedIntensity) > e
				|| Math.Abs(tint.X - appliedTint.X) > e
				|| Math.Abs(tint.Y - appliedTint.Y) > e
				|| Math.Abs(tint.Z - appliedTint.Z) > e;
		}
	}

	public class AotDayNightStartHourInit : ValueActorInit<int>, ISingleInstanceInit
	{
		public AotDayNightStartHourInit(int value)
			: base(value) { }
	}

	public class AotDayNightCycleMinutesInit : ValueActorInit<int>, ISingleInstanceInit
	{
		public AotDayNightCycleMinutesInit(int value)
			: base(value) { }
	}

	public class AotDayNightFreezeTimeInit : ValueActorInit<bool>, ISingleInstanceInit
	{
		public AotDayNightFreezeTimeInit(bool value)
			: base(value) { }
	}
}
