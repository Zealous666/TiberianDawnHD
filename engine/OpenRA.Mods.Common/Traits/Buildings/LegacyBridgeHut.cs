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
using System.Linq;
using OpenRA.Effects;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Allows bridges to be targeted for demolition and repair.")]
	public class LegacyBridgeHutInfo : TraitInfo, IDemolishableInfo
	{
		[Desc("If > 0, the hut can be placed in the map editor without a parent bridge and will link to " +
			"every bridge whose footprint has a cell within this radius on world load.")]
		public readonly int StandaloneSearchRadius = 0;

		public bool IsValidTarget(ActorInfo actorInfo, Actor saboteur) { return false; } // TODO: bridges don't support frozen under fog

		public override object Create(ActorInitializer init) { return new LegacyBridgeHut(init); }
	}

	public class LegacyBridgeHut : IDemolishable
	{
		public Bridge FirstBridge { get; private set; }
		public Bridge Bridge { get; private set; }

		// Standalone huts (placed freely in the map editor) can cover several nearby bridge actors at
		// once - e.g. a long bridge built from multiple unlinked segments.
		readonly List<Bridge> standaloneBridges = new();
		bool isStandalone;

		public DamageState BridgeDamageState
		{
			get
			{
				if (isStandalone)
				{
					// Worst damage state across every linked bridge (and their linked spans).
					var worst = DamageState.Undamaged;
					foreach (var b in standaloneBridges)
					{
						var d = b.AggregateDamageState();
						if (d > worst)
							worst = d;
					}

					return worst;
				}

				return Bridge?.AggregateDamageState() ?? DamageState.Undamaged;
			}
		}

		// The engine's dangling check only makes sense for huts registered via Bridge.AddHut().
		// Standalone huts deliberately bypass that registration, so they are never "dangling".
		public bool BridgeIsDangling => !isStandalone && Bridge != null && Bridge.IsDangling;

		public bool Repairing => repairDirections > 0;
		int repairDirections = 0;

		// Whether a repair can currently be started: the bridge must be damaged and not already under
		// repair. Parented huts additionally require the bridge to be fully connected (a hut on both
		// ends); standalone huts manage their own bridge list and only need at least one linked bridge.
		public bool CanRepair
		{
			get
			{
				if (BridgeDamageState == DamageState.Undamaged || Repairing)
					return false;

				if (isStandalone)
					return standaloneBridges.Count > 0;

				return Bridge != null && Bridge.GetHut(0) != null && Bridge.GetHut(1) != null;
			}
		}

		public LegacyBridgeHut(ActorInitializer init)
		{
			var parentInit = init.GetOrDefault<ParentActorInit>();
			if (parentInit != null)
			{
				var bridge = parentInit.Value;
				init.World.AddFrameEndTask(_ =>
				{
					Bridge = bridge.Actor(init.World).Value.Trait<Bridge>();
					Bridge.AddHut(this);
					FirstBridge = Bridge.Enumerate(0, true).Last();
				});
			}
			else
			{
				// Standalone mode: link to every bridge with a footprint cell within the search radius,
				// measured ONCE on world load. The link is kept for the rest of the game regardless of the
				// bridges' later damage state.
				isStandalone = true;
				var self = init.Self;
				var radius = self.Info.TraitInfo<LegacyBridgeHutInfo>().StandaloneSearchRadius;
				if (radius > 0)
				{
					init.World.AddFrameEndTask(w =>
					{
						var radiusSq = (long)radius * radius;
						var nearestDistSq = long.MaxValue;

						foreach (var a in w.Actors)
						{
							// Deliberately NOT filtering on IsDead: a bridge painted in the editor already in
							// its destroyed template is created with 0 HP (Actor.IsDead == true) yet is fully
							// present in the world and must remain a valid repair target.
							if (a.Disposed || !a.IsInWorld || !a.Info.HasTraitInfo<BridgeInfo>())
								continue;

							var building = a.Info.TraitInfoOrDefault<BuildingInfo>();
							if (building == null)
								continue;

							// Bridges use an all-'_' (passable) footprint, so OccupiedCells() / OccupiedTiles()
							// are empty. Measure to every footprint cell instead (passable + occupied).
							var bestDistSq = long.MaxValue;
							foreach (var cell in building.PathableTiles(a.Location).Concat(building.OccupiedTiles(a.Location)))
							{
								var d = (cell - self.Location).LengthSquared;
								if (d < bestDistSq)
									bestDistSq = d;
							}

							if (bestDistSq > radiusSq)
								continue;

							var b = a.Trait<Bridge>();
							standaloneBridges.Add(b);

							if (bestDistSq < nearestDistSq)
							{
								nearestDistSq = bestDistSq;
								Bridge = b;
								FirstBridge = b;
							}
						}
					});
				}
			}
		}

		public void Repair(Actor repairer)
		{
			if (isStandalone)
			{
				if (standaloneBridges.Count == 0)
					return;

				repairDirections = standaloneBridges.Count;
				foreach (var b in standaloneBridges)
					b.Do((span, d) => span.Repair(repairer, d, () => repairDirections--));

				return;
			}

			if (Bridge == null)
				return;

			repairDirections = Bridge.GetHut(0) != this && Bridge.GetHut(1) != this ? 2 : 1;
			Bridge.Do((b, d) => b.Repair(repairer, d, () => repairDirections--));
		}

		bool IDemolishable.IsValidTarget(Actor self, Actor saboteur)
		{
			return BridgeDamageState != DamageState.Dead;
		}

		void IDemolishable.Demolish(Actor self, Actor saboteur, int delay, BitSet<DamageType> damageTypes)
		{
			// TODO: Handle using ITick
			self.World.Add(new DelayedAction(delay, () =>
			{
				if (self.IsDead)
					return;

				var modifiers = self.TraitsImplementing<IDamageModifier>()
					.Concat(self.Owner.PlayerActor.TraitsImplementing<IDamageModifier>())
					.Select(t => t.GetDamageModifier(self, null));

				if (Util.ApplyPercentageModifiers(100, modifiers) <= 0)
					return;

				if (isStandalone)
				{
					foreach (var b in standaloneBridges)
						b.Do((span, d) => span.Demolish(saboteur, d, damageTypes));
				}
				else
					Bridge?.Do((b, d) => b.Demolish(saboteur, d, damageTypes));
			}));
		}
	}
}
