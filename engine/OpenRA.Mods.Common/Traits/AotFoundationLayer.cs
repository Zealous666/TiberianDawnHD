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
	}
}
