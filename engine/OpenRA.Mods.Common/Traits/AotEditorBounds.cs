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

using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// aotmod: Datenhaltung fuer ein Editor-Rechteck um den Aktor (Planungs-/Bau-Begrenzer-Hinweis).
	// Gezeichnet wird es von AotEditorBoundsOverlay (EditorWorld-Trait), weil platzierte Aktoren im
	// Editor nur EditorActorPreviews ohne tickende Traits sind und selbst nichts rendern koennen.
	// Offsets sind Zellen relativ zur (oberen linken) Aktor-Zelle; das Rechteck umschliesst
	// MinX..MaxX / MinY..MaxY inklusive (Kanten auf den aeusseren Zellraendern).
	[Desc("aotmod: Zeichnet im Karteneditor ein Rechteck um den Aktor (z.B. Bau-Sperrzone/Compound).")]
	public class AotEditorBoundsInfo : TraitInfo
	{
		[Desc("Linke Kante in Zellen relativ zur Aktor-Zelle.")]
		public readonly int MinX = -1;
		[Desc("Obere Kante in Zellen relativ zur Aktor-Zelle.")]
		public readonly int MinY = -1;
		[Desc("Rechte Kante in Zellen relativ zur Aktor-Zelle.")]
		public readonly int MaxX = 1;
		[Desc("Untere Kante in Zellen relativ zur Aktor-Zelle.")]
		public readonly int MaxY = 1;

		[Desc("Farbe des Editor-Rechtecks.")]
		public readonly Color Color = Color.FromArgb(160, Color.Cyan);

		[Desc("Linienbreite des Editor-Rechtecks.")]
		public readonly int Width = 2;

		public override object Create(ActorInitializer init) { return new AotEditorBounds(); }
	}

	public class AotEditorBounds { }
}
