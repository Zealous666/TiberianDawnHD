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

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Tracks every cell currently covered by Tiberium veins (hearts + grown cells). Attach to World.",
		"A clone of AotIceLayer, kept separate so the ice and vein systems never interfere.")]
	sealed class AotVeinLayerInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new AotVeinLayer(); }
	}

	sealed class AotVeinLayer
	{
		readonly HashSet<CPos> veins = [];

		/// <summary>Incremented whenever the vein set changes, so cells can refresh their sprite.</summary>
		public int Version { get; private set; }

		public bool Contains(CPos c) { return veins.Contains(c); }

		/// <summary>Snapshot of all currently veined cells, for the heart-death recession sweep.</summary>
		public IReadOnlyCollection<CPos> Cells => veins;

		public void Add(CPos c)
		{
			if (veins.Add(c))
				Version++;
		}

		public void Remove(CPos c)
		{
			if (veins.Remove(c))
				Version++;
		}

		/// <summary>
		/// A corner point is shared by the four cells around it. Returns how many of them are veined.
		/// Neighbouring cells query the same corner and therefore always agree — this is what makes
		/// the rendered vein edge continuous across cell borders.
		/// </summary>
		public int CornerVeinCount(CPos corner)
		{
			var n = 0;
			if (veins.Contains(new CPos(corner.X - 1, corner.Y - 1))) n++;
			if (veins.Contains(new CPos(corner.X, corner.Y - 1))) n++;
			if (veins.Contains(new CPos(corner.X - 1, corner.Y))) n++;
			if (veins.Contains(new CPos(corner.X, corner.Y))) n++;
			return n;
		}
	}
}
