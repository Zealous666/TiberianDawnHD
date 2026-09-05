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

using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// Marker trait, no runtime behaviour.
	//
	// ProductionQueue.ReadyAudio/ReadyTextNotification are QUEUE-level settings, so every item
	// finishing in the Upgrade queue announces "Construction complete". For the Age upgrades
	// that is wrong twice over: they are not structures, and they already have their own,
	// more specific announcement (AotAgeActivationNotification -> "New build options" plus the
	// "<player> has evolved into the Nth Tiberium Age" system line). Two notifications firing
	// on the same tick just talked over each other.
	//
	// This lives in Mods.Common rather than Mods.Cnc on purpose: ProductionQueue is a
	// Mods.Common type and cannot reference Mods.Cnc without a circular assembly dependency,
	// so the age trait itself cannot be the thing ProductionQueue tests for.
	//
	// Attach to any producible actor whose completion should stay silent.
	// User-Wunsch 2026-08-01: "wenn ein age-tier-upgrade abgeschlossen wurde, sollte nie
	// 'construction complete' kommen".
	[Desc("Suppresses the producing queue's ReadyAudio/ReadyTextNotification for this actor.")]
	public class AotSuppressReadyNotificationInfo : TraitInfo<AotSuppressReadyNotification> { }

	public class AotSuppressReadyNotification { }
}
