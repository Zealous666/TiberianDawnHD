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

using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Adds a localized circular light centered on the actor to the world's TerrainLightSource trait.")]
	// aotmod (2026-07-26): ConditionalTraitInfo instead of TraitInfo, so several condition-gated
	// instances can sit on one actor (used for the GDI/NOD faction tint on ^BaseBuilding -- NUKE,
	// SILO and FIX are built by BOTH factions, so the actor type alone cannot decide the colour).
	// Without a RequiresCondition a ConditionalTrait is always enabled, so existing uses are
	// unaffected.
	public class TerrainLightSourceInfo : ConditionalTraitInfo, INotifyEditorPlacementInfo, ILobbyCustomRulesIgnore
	{
		public readonly WDist Range = WDist.FromCells(10);
		public readonly float Intensity = 0;
		public readonly float RedTint = 0;
		public readonly float GreenTint = 0;
		public readonly float BlueTint = 0;

		object INotifyEditorPlacementInfo.AddedToEditor(EditorActorPreview preview, World editorWorld)
		{
			var tint = new float3(RedTint, GreenTint, BlueTint);
			return editorWorld.WorldActor.Trait<TerrainLighting>().AddLightSource(preview.CenterPosition, Range, Intensity, tint);
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

		public override object Create(ActorInitializer init) { return new TerrainLightSource(init.Self, this); }
	}

	public sealed class TerrainLightSource : ConditionalTrait<TerrainLightSourceInfo>, INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly TerrainLighting terrainLighting;
		int lightingToken = -1;
		bool inWorld;

		public TerrainLightSource(Actor self, TerrainLightSourceInfo info)
			: base(info)
		{
			terrainLighting = self.World.WorldActor.Trait<TerrainLighting>();
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
					new float3(Info.RedTint, Info.GreenTint, Info.BlueTint));
			else if (!wanted && lightingToken != -1)
			{
				terrainLighting.RemoveLightSource(lightingToken);
				lightingToken = -1;
			}
		}
	}
}
