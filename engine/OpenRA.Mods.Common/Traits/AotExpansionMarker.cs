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

		// Das Editor-Rechteck (Compound-Groesse) traegt jetzt der generische AotEditorBounds-Trait,
		// gezeichnet von AotEditorBoundsOverlay. Hier nur noch die Checkbox + PriorSpawn-Daten.
		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			yield return new EditorActorCheckbox("Prior Spawn Marker", 0,
				actor => actor.GetInitOrDefault<AotPriorSpawnInit>(this)?.Value ?? DefaultPriorSpawn,
				(actor, value) => actor.ReplaceInit(new AotPriorSpawnInit(this, value), this));
		}

		public override object Create(ActorInitializer init) { return new AotExpansionMarker(init, this); }
	}

	// Reine Datenhaltung. Das Editor-Rechteck (Compound-Groesse) wird NICHT hier gezeichnet:
	// im Karteneditor sind platzierte Aktoren nur EditorActorPreviews ohne tickende Traits, daher
	// feuert IRenderAnnotations(WhenSelected) hier NIE. Das Rechteck rendert stattdessen
	// AotExpansionMarkerEditorOverlay (EditorWorld-Trait), das alle Marker-Previews einrahmt.
	public class AotExpansionMarker
	{
		// Read by AotOperationsBotModule.FindExpansionSite. PriorSpawn markers outrank empty spawns.
		public bool PriorSpawn { get; }

		public AotExpansionMarker(ActorInitializer init, AotExpansionMarkerInfo info)
		{
			PriorSpawn = init.GetValue<AotPriorSpawnInit, bool>(info.DefaultPriorSpawn);
		}

	}
}
