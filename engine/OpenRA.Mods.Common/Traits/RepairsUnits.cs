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
	public class RepairsUnitsInfo : PausableConditionalTraitInfo
	{
		[Desc("Cost in % of the unit value to fully repair the unit.")]
		public readonly int ValuePercentage = 20;

		public readonly int HpPerStep = 10;

		[Desc("Time (in ticks) between two repair steps.")]
		public readonly int Interval = 24;

		[Desc("Damage types used for the repair.")]
		public readonly BitSet<DamageType> RepairDamageTypes = default;

		[NotificationReference("Speech")]
		[Desc("Speech notification played when starting to repair a unit.")]
		public readonly string StartRepairingNotification = null;

		[FluentReference(optional: true)]
		[Desc("Text notification displayed when starting to repair a unit.")]
		public readonly string StartRepairingTextNotification = null;

		[NotificationReference("Speech")]
		[Desc("Speech notification played when repairing a unit is done.")]
		public readonly string FinishRepairingNotification = null;

		[FluentReference(optional: true)]
		[Desc("Text notification displayed when repairing a unit is done.")]
		public readonly string FinishRepairingTextNotification = null;

		[Desc("Experience gained by the player owning this actor for repairing an allied unit.")]
		public readonly int PlayerExperience = 0;

		// aotmod (User-Wunsch 2026-08-01): Fahrzeuge sollen nach abgeschlossener Reparatur noch
		// kurz auf dem Depot stehen bleiben, statt in dem Moment loszufahren, in dem der letzte
		// HP-Schritt faellt. Gibt dem Spieler ein Zeitfenster, das reparierte Fahrzeug dort noch
		// zu verkaufen (siehe AotSellableWhileDocked) und wirkt weniger abrupt.
		// 0 = altes Verhalten (sofort abfahren). Greift NUR fuer die Reparatur, nicht fuer Rearm.
		[Desc("Ticks the unit remains docked after the repair finished before it drives off.")]
		public readonly int LingerAfterRepair = 0;

		public override object Create(ActorInitializer init) { return new RepairsUnits(this); }
	}

	public class RepairsUnits : PausableConditionalTrait<RepairsUnitsInfo>
	{
		public RepairsUnits(RepairsUnitsInfo info)
			: base(info) { }
	}
}
