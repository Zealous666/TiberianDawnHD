#region Copyright & License Information
/*
 * Age of Tiberium mod — shared helper for actors that spawn things (gas clouds,
 * critters) at a host building. Spawning literally on top of the host's own
 * footprint traps the spawned actor behind the host's blocking cells until the
 * host dies (Locomotor treats a solid Building as an uncrushable stationary
 * blocker) — this picks a free cell just outside the footprint instead.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public static class AotFootprintUtils
	{
		// Findet eine freie Zelle direkt ausserhalb des Footprints von self (Ring um die
		// Bounding-Box). Faellt auf self.Location zurueck, falls nichts frei ist.
		public static CPos FindCellOutsideFootprint(Actor self)
		{
			var world = self.World;
			var footprint = new HashSet<CPos>();
			var occupies = self.OccupiesSpace;
			if (occupies != null)
				foreach (var (cell, _) in occupies.OccupiedCells())
					footprint.Add(cell);

			if (footprint.Count == 0)
				return self.Location;

			int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
			foreach (var c in footprint)
			{
				if (c.X < minX)
					minX = c.X;
				if (c.X > maxX)
					maxX = c.X;
				if (c.Y < minY)
					minY = c.Y;
				if (c.Y > maxY)
					maxY = c.Y;
			}

			var candidates = new List<CPos>();
			for (var y = minY - 1; y <= maxY + 1; y++)
			{
				for (var x = minX - 1; x <= maxX + 1; x++)
				{
					var c = new CPos(x, y);
					if (footprint.Contains(c))
						continue;

					if (x != minX - 1 && x != maxX + 1 && y != minY - 1 && y != maxY + 1)
						continue; // nur der direkte Ring, keine weiter entfernten Zellen

					if (!world.Map.Contains(c))
						continue;

					if (world.ActorMap.GetActorsAt(c).Any(a => a != self))
						continue;

					candidates.Add(c);
				}
			}

			if (candidates.Count == 0)
				return self.Location;

			return candidates[world.SharedRandom.Next(candidates.Count)];
		}
	}
}
