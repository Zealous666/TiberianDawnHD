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
	[Desc("Tracks every cell currently covered by ice (hosts + grown cells). Attach to World.")]
	sealed class AotIceLayerInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new AotIceLayer(); }
	}

	sealed class AotIceLayer
	{
		readonly HashSet<CPos> ice = [];

		/// <summary>Incremented whenever the ice set changes, so cells can refresh their sprite.</summary>
		public int Version { get; private set; }

		public bool Contains(CPos c) { return ice.Contains(c); }

		public void Add(CPos c)
		{
			if (ice.Add(c))
				Version++;
		}

		public void Remove(CPos c)
		{
			if (ice.Remove(c))
				Version++;
		}

		/// <summary>
		/// A corner point is shared by the four cells around it. Returns how many of them are ice.
		/// Neighbouring cells query the same corner and therefore always agree — this is what makes
		/// the rendered ice edge continuous across cell borders.
		/// </summary>
		public int CornerIceCount(CPos corner)
		{
			var n = 0;
			if (ice.Contains(new CPos(corner.X - 1, corner.Y - 1))) n++;
			if (ice.Contains(new CPos(corner.X, corner.Y - 1))) n++;
			if (ice.Contains(new CPos(corner.X - 1, corner.Y))) n++;
			if (ice.Contains(new CPos(corner.X, corner.Y))) n++;
			return n;
		}
	}
}
