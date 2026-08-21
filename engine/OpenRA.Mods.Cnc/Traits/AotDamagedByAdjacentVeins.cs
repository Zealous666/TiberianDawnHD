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

using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Periodically damages this actor if any of its footprint cells, or their immediate",
		"neighbours, are covered by Tiberium veins. Unlike DamagedByTerrain (which only checks",
		"self.Location), this also covers multi-cell buildings and cells merely ADJACENT to",
		"veins -- matching Tiberian Sun, where veins constrict nearby structures, not just",
		"things standing directly on them.")]
	sealed class AotDamagedByAdjacentVeinsInfo : ConditionalTraitInfo, Requires<IHealthInfo>, Requires<IOccupySpaceInfo>
	{
		[Desc("Damage applied per interval.")]
		public readonly int Damage = 0;

		[Desc("Ticks between damage instances.")]
		public readonly int DamageInterval = 0;

		public readonly BitSet<DamageType> DamageTypes = default;

		public override object Create(ActorInitializer init) { return new AotDamagedByAdjacentVeins(this); }
	}

	sealed class AotDamagedByAdjacentVeins : ConditionalTrait<AotDamagedByAdjacentVeinsInfo>, ITick
	{
		AotVeinLayer layer;
		int damageTicks;

		public AotDamagedByAdjacentVeins(AotDamagedByAdjacentVeinsInfo info)
			: base(info) { }

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || --damageTicks > 0)
				return;

			layer ??= self.World.WorldActor.Trait<AotVeinLayer>();

			if (!self.IsInWorld)
				return;

			var occupies = self.OccupiesSpace;
			if (occupies == null)
				return;

			var adjacent = false;
			foreach (var (cell, _) in occupies.OccupiedCells())
			{
				if (layer.Contains(cell))
				{
					adjacent = true;
					break;
				}

				foreach (var d in CVec.Directions)
				{
					if (layer.Contains(cell + d))
					{
						adjacent = true;
						break;
					}
				}

				if (adjacent)
					break;
			}

			if (!adjacent)
				return;

			self.InflictDamage(self.World.WorldActor, new Damage(Info.Damage, Info.DamageTypes));
			damageTicks = Info.DamageInterval;
		}
	}
}
