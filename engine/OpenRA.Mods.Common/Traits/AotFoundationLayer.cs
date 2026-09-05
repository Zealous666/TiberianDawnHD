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
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Tracks every cell covered by a Fortified Foundation tile. Attach to World.",
		"Lives in Common (not Cnc) so SubterraneanActorLayer can block dig transitions on it.")]
	public sealed class AotFoundationLayerInfo : TraitInfo
	{
		public override object Create(ActorInitializer init) { return new AotFoundationLayer(); }
	}

	public sealed class AotFoundationLayer : IGameSaveTraitData
	{
		readonly HashSet<CPos> cells = [];

		/// <summary>Incremented whenever the set changes, so cells can refresh their sprite.</summary>
		public int Version { get; private set; }

		/// <summary>
		/// CPos.Equals compares the full Bits field INCLUDING the movement-layer byte. Callers like
		/// the pathfinder pass cells with a custom-layer byte set (e.g. EntryMovementCost receives
		/// new CPos(x, y, Subterranean)), which would never match ground-layer entries in the set.
		/// Normalizing every query/mutation to layer 0 makes the set layer-agnostic by construction.
		/// </summary>
		static CPos Ground(CPos c) { return new CPos(c.X, c.Y); }

		public bool Contains(CPos c) { return cells.Contains(Ground(c)); }

		// Snapshot save/load: foundation is world state, carried explicitly (not actors).
		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			return cells.Count == 0 ? null
				: [new MiniYamlNode("Foundation", string.Join(" ", cells.OrderBy(c => c.X).ThenBy(c => c.Y).Select(c => $"{c.X},{c.Y}")))];
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, MiniYaml data)
		{
			var node = data.NodeWithKeyOrDefault("Foundation");
			if (node == null)
				return;

			foreach (var pair in node.Value.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = pair.Split(',');
				if (parts.Length == 2)
					Add(new CPos(Exts.ParseInt32Invariant(parts[0]), Exts.ParseInt32Invariant(parts[1])));
			}
		}

		public void Add(CPos c)
		{
			if (cells.Add(Ground(c)))
				Version++;
		}

		public void Remove(CPos c)
		{
			if (cells.Remove(Ground(c)))
				Version++;
		}

		/// <summary>
		/// A corner point is shared by the four cells around it. Returns how many of them are
		/// foundation. Neighbouring cells query the same corner and therefore always agree — this is
		/// what makes concave/convex corners render continuously across cell borders (dual grid).
		/// </summary>
		public int CornerCount(CPos corner)
		{
			// The new CPos(x, y) constructions are already layer-0, so no normalization needed here.
			var n = 0;
			if (cells.Contains(new CPos(corner.X - 1, corner.Y - 1))) n++;
			if (cells.Contains(new CPos(corner.X, corner.Y - 1))) n++;
			if (cells.Contains(new CPos(corner.X - 1, corner.Y))) n++;
			if (cells.Contains(new CPos(corner.X, corner.Y))) n++;
			return n;
		}
	}
}
