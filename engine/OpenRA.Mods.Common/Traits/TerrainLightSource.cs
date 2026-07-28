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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Adds a localized circular light centered on the actor to the world's TerrainLightSource trait.")]
	// aotmod (2026-07-26): ConditionalTraitInfo instead of TraitInfo, so several condition-gated
	// instances can sit on one actor (used for the GDI/NOD faction tint on ^BaseBuilding -- NUKE,
	// SILO and FIX are built by BOTH factions, so the actor type alone cannot decide the colour).
	// Without a RequiresCondition a ConditionalTrait is always enabled, so existing uses are
	// unaffected.
	public class TerrainLightSourceInfo : ConditionalTraitInfo, INotifyEditorPlacementInfo, IEditorActorOptions, ILobbyCustomRulesIgnore
	{
		public readonly WDist Range = WDist.FromCells(10);
		public readonly float Intensity = 0;
		public readonly float RedTint = 0;
		public readonly float GreenTint = 0;
		public readonly float BlueTint = 0;

		// aotmod (2026-07-28): per-actor options for editor-placed lamps.
		[Desc("CHECKBOX \"Immer an\": ignore the day/night fade, i.e. light at full strength around",
			"the clock. An Ion Storm still switches it off -- that is the only thing that does.")]
		public readonly bool AlwaysOn = false;

		[Desc("SLIDER: dims this light source, 0 = off, 1 = the Intensity/tint configured above.",
			"The rules values are therefore the maximum, and the default. The map editor shows this",
			"as a percentage; the value stored here and in the map stays in the 0-1 range.")]
		public readonly float Brightness = 1f;

		[Desc("Expose the two settings above as map editor options. Off by default: several",
			"condition-gated instances share one actor (the GDI/NOD building tint), and each enabled",
			"instance would add its own identically labelled widget writing the same actor init.")]
		public readonly bool EditorConfigurable = false;

		[Desc("Display order for the \"always on\" checkbox in the map editor.")]
		public readonly int EditorAlwaysOnDisplayOrder = 1;

		[Desc("Display order for the brightness slider in the map editor.")]
		public readonly int EditorBrightnessDisplayOrder = 2;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			if (!EditorConfigurable)
				yield break;

			yield return new EditorActorCheckbox("Always on", EditorAlwaysOnDisplayOrder,
				actor => actor.GetInitOrDefault<TerrainLightAlwaysOnInit>()?.Value ?? AlwaysOn,
				(actor, value) => actor.ReplaceInit(new TerrainLightAlwaysOnInit(value)));

			// The slider runs in PERCENT, not in the trait's native 0-1 range: the editor's value box
			// formats with a plain (int) cast (ActorEditLogic), so a 0-1 slider would read "0" across
			// its whole travel and only flip to "1" at the very end -- indistinguishable from off.
			// 5th arg is the number of scale marks, NOT the step size, and must be 0 or >= 2 --
			// SliderWidget.Draw divides by (Ticks - 1) and a 1 crashes on selecting the actor.
			yield return new EditorActorSlider("Brightness (%)", EditorBrightnessDisplayOrder,
				0f, 100f, 11,
				actor => 100f * (actor.GetInitOrDefault<TerrainLightBrightnessInit>()?.Value ?? Brightness),
				(actor, value) => actor.ReplaceInit(new TerrainLightBrightnessInit((float)Math.Round(value) / 100f)));
		}

		object INotifyEditorPlacementInfo.AddedToEditor(EditorActorPreview preview, World editorWorld)
		{
			var tint = new float3(RedTint, GreenTint, BlueTint);

			// Honour the per-actor settings, so the editor preview shows what the map will look like.
			var brightness = preview.GetInitOrDefault<TerrainLightBrightnessInit>()?.Value ?? Brightness;
			var alwaysOn = preview.GetInitOrDefault<TerrainLightAlwaysOnInit>()?.Value ?? AlwaysOn;

			return editorWorld.WorldActor.Trait<TerrainLighting>()
				.AddLightSource(preview.CenterPosition, Range, Intensity, tint, brightness, alwaysOn);
		}

		void INotifyEditorPlacementInfo.RemovedFromEditor(EditorActorPreview preview, World editorWorld, object data)
		{
			editorWorld.WorldActor.Trait<TerrainLighting>().RemoveLightSource((int)data);
		}

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			if (!rules.Actors[SystemActors.World].HasTraitInfo<TerrainLightingInfo>())
				throw new YamlException($"{nameof(TerrainLightSource)} can only be used with the world {nameof(TerrainLighting)} trait.");
		}

		public override object Create(ActorInitializer init) { return new TerrainLightSource(init, this); }
	}

	public sealed class TerrainLightSource : ConditionalTrait<TerrainLightSourceInfo>, INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly TerrainLighting terrainLighting;

		// aotmod (2026-07-28): map editor overrides, falling back to the rules values.
		readonly float brightness;
		readonly bool alwaysOn;

		int lightingToken = -1;
		bool inWorld;

		public TerrainLightSource(ActorInitializer init, TerrainLightSourceInfo info)
			: base(info)
		{
			terrainLighting = init.Self.World.WorldActor.Trait<TerrainLighting>();
			brightness = init.GetValue<TerrainLightBrightnessInit, float>(info.Brightness);
			alwaysOn = init.GetValue<TerrainLightAlwaysOnInit, bool>(info.AlwaysOn);
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			inWorld = true;
			Refresh(self);
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			inWorld = false;
			Refresh(self);
		}

		protected override void TraitEnabled(Actor self) { Refresh(self); }
		protected override void TraitDisabled(Actor self) { Refresh(self); }

		// aotmod (2026-07-26): the light exists exactly while the actor is in the world AND the
		// trait is enabled. Killing/selling the actor removes it from the world, so the light dies
		// with it -- deliberately unlike Tiberian Sun, where a destroyed light post kept lighting.
		void Refresh(Actor self)
		{
			var wanted = inWorld && !IsTraitDisabled;
			if (wanted && lightingToken == -1)
				lightingToken = terrainLighting.AddLightSource(self.CenterPosition, Info.Range, Info.Intensity,
					new float3(Info.RedTint, Info.GreenTint, Info.BlueTint), brightness, alwaysOn);
			else if (!wanted && lightingToken != -1)
			{
				terrainLighting.RemoveLightSource(lightingToken);
				lightingToken = -1;
			}
		}
	}

	// aotmod (2026-07-28): carry the map editor settings into the saved actor.
	public class TerrainLightAlwaysOnInit : ValueActorInit<bool>, ISingleInstanceInit
	{
		public TerrainLightAlwaysOnInit(bool value)
			: base(value) { }
	}

	public class TerrainLightBrightnessInit : ValueActorInit<float>, ISingleInstanceInit
	{
		public TerrainLightBrightnessInit(float value)
			: base(value) { }
	}
}
