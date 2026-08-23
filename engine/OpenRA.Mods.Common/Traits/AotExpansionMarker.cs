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

using System.Collections.Generic;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// aotmod (2026-08-22): Optionaler Map-Editor-Marker, an dem die NOD-KI ihre Base-Expansion
	// bevorzugt gruendet. Muster wie aot-critter-spawner: unsichtbarer World-Aktor, im Editor
	// platzierbar, mit einer Editor-Checkbox. Die KI liest die Marker ueber
	// World.ActorsWithTrait<AotExpansionMarker>() in FindExpansionSite.
	public class AotPriorSpawnInit : ValueActorInit<bool>, ISingleInstanceInit
	{
		public AotPriorSpawnInit(TraitInfo info, bool value) : base(info, value) { }
	}

	[Desc("aotmod: Unsichtbarer Marker fuer die KI-Base-Expansion. Der Map-Maker platziert ihn",
		"optional dort, wo eine KI-Expansion entstehen soll. Zeigt im Editor ein Rechteck in der",
		"Groesse des Expansions-Compounds. Checkbox \"Prior Spawn Marker\": wird der Marker damit",
		"hoeher gewertet als ein leerer Spawn -- die KI zielt dann immer zuerst hierher.")]
	public class AotExpansionMarkerInfo : TraitInfo, IEditorActorOptions
	{
		[Desc("CHECKBOX \"Prior Spawn Marker\" (default aus): wenn an, bewertet die KI diesen Marker",
			"HOEHER als einen leeren Spawn-Punkt und versucht immer zuerst, hier zu expandieren.")]
		public readonly bool DefaultPriorSpawn = false;

		[Desc("Farbe des Editor-Rechtecks.")]
		public readonly Color Color = Color.FromArgb(160, Color.Cyan);

		[Desc("Linienbreite des Editor-Rechtecks.")]
		public readonly int Width = 2;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			yield return new EditorActorCheckbox("Prior Spawn Marker", 0,
				actor => actor.GetInitOrDefault<AotPriorSpawnInit>(this)?.Value ?? DefaultPriorSpawn,
				(actor, value) => actor.ReplaceInit(new AotPriorSpawnInit(this, value), this));
		}

		public override object Create(ActorInitializer init) { return new AotExpansionMarker(init, this); }
	}

	public class AotExpansionMarker : IRenderAnnotationsWhenSelected
	{
		readonly AotExpansionMarkerInfo info;

		// Read by AotOperationsBotModule.FindExpansionSite. PriorSpawn markers outrank empty spawns.
		public bool PriorSpawn { get; }

		public AotExpansionMarker(ActorInitializer init, AotExpansionMarkerInfo info)
		{
			this.info = info;
			PriorSpawn = init.GetValue<AotPriorSpawnInit, bool>(info.DefaultPriorSpawn);
		}

		// The walled compound the layout builds around the yard: x -4..+3, y -1..+7 relative to the
		// yard's top-left cell (see memory/ai-base-expansion-layout.md). The marker cell IS that yard
		// cell, so the rectangle shows the map-maker exactly how much clear ground an expansion needs.
		IEnumerable<IRenderable> IRenderAnnotationsWhenSelected.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			var map = self.World.Map;
			var loc = self.Location;

			WPos Corner(int dx, int dy, int ox, int oy)
			{
				var c = map.CenterOfCell(loc + new CVec(dx, dy));
				return c + new WVec(ox, oy, 0);
			}

			// Half a cell = 512 world units; push the corners to the outer cell edges.
			var tl = Corner(-4, -1, -512, -512);
			var tr = Corner(3, -1, 512, -512);
			var br = Corner(3, 7, 512, 512);
			var bl = Corner(-4, 7, -512, 512);

			yield return new LineAnnotationRenderable(tl, tr, info.Width, info.Color);
			yield return new LineAnnotationRenderable(tr, br, info.Width, info.Color);
			yield return new LineAnnotationRenderable(br, bl, info.Width, info.Color);
			yield return new LineAnnotationRenderable(bl, tl, info.Width, info.Color);
		}

		bool IRenderAnnotationsWhenSelected.SpatiallyPartitionable => false;
	}
}
