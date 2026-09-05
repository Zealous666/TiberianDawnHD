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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// aotmod: zeichnet im Karteneditor um JEDEN platzierten Aktor mit AotEditorBounds ein Rechteck.
	// Muss ein EditorWorld-Trait sein, weil platzierte Aktoren im Editor nur EditorActorPreviews
	// ohne tickende Traits sind. Dieser Welt-Trait laeuft (wie EditorActorLayer.RenderAnnotations)
	// und iteriert die Previews. Genutzt vom Expansion-Marker (cyan Compound) und der Ore-Mine
	// (rote Bau-Sperrzone).
	[TraitLocation(SystemActors.EditorWorld)]
	[Desc("aotmod: Umrahmt im Editor jeden Aktor mit AotEditorBounds mit dessen Rechteck.")]
	public class AotEditorBoundsOverlayInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new AotEditorBoundsOverlay(); }
	}

	public class AotEditorBoundsOverlay : IWorldLoaded, IRenderAnnotations
	{
		EditorActorLayer editorLayer;

		void IWorldLoaded.WorldLoaded(World w, WorldRenderer wr)
		{
			editorLayer = w.WorldActor.Trait<EditorActorLayer>();
		}

		IEnumerable<IRenderable> IRenderAnnotations.RenderAnnotations(Actor self, WorldRenderer wr)
		{
			var map = self.World.Map;
			foreach (var preview in editorLayer.Previews)
			{
				var b = preview.Info.TraitInfoOrDefault<AotEditorBoundsInfo>();
				if (b == null)
					continue;

				var loc = preview.Location;

				WPos Corner(int dx, int dy, int ox, int oy)
				{
					var c = map.CenterOfCell(loc + new CVec(dx, dy));
					return c + new WVec(ox, oy, 0);
				}

				// Half a cell = 512 world units -> Ecken auf den aeusseren Zellraendern.
				var tl = Corner(b.MinX, b.MinY, -512, -512);
				var tr = Corner(b.MaxX, b.MinY, 512, -512);
				var br = Corner(b.MaxX, b.MaxY, 512, 512);
				var bl = Corner(b.MinX, b.MaxY, -512, 512);

				yield return new LineAnnotationRenderable(tl, tr, b.Width, b.Color);
				yield return new LineAnnotationRenderable(tr, br, b.Width, b.Color);
				yield return new LineAnnotationRenderable(br, bl, b.Width, b.Color);
				yield return new LineAnnotationRenderable(bl, tl, b.Width, b.Color);
			}
		}

		bool IRenderAnnotations.SpatiallyPartitionable => false;
	}
}
