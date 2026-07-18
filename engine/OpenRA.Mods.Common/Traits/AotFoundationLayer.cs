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
	[Desc("Tracks every cell covered by a Fortified Foundation tile. Attach to World.",
		"Lives in Common (not Cnc) so SubterraneanActorLayer can block dig transitions on it.")]
	public sealed class AotFoundationLayerInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new AotFoundationLayer(); }
	}

	public sealed class AotFoundationLayer
	{
		readonly HashSet<CPos> cells = [];

		/// <summary>Incremented whenever the set changes, so cells can refresh their sprite.</summary>
		public int Version { get; private set; }

		public bool Contains(CPos c) { return cells.Contains(c); }

		public void Add(CPos c)
		{
			if (cells.Add(c))
				Version++;
		}

		public void Remove(CPos c)
		{
			if (cells.Remove(c))
				Version++;
		}

		/// <summary>
		/// A corner point is shared by the four cells around it. Returns how many of them are
		/// foundation. Neighbouring cells query the same corner and therefore always agree — this is
		/// what makes concave/convex corners render continuously across cell borders (dual grid).
		/// </summary>
		public int CornerCount(CPos corner)
		{
			var n = 0;
			if (cells.Contains(new CPos(corner.X - 1, corner.Y - 1))) n++;
			if (cells.Contains(new CPos(corner.X, corner.Y - 1))) n++;
			if (cells.Contains(new CPos(corner.X - 1, corner.Y))) n++;
			if (cells.Contains(new CPos(corner.X, corner.Y))) n++;
			return n;
		}
	}
}
