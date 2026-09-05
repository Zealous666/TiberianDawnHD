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

namespace OpenRA.Mods.Common.Traits
{
	// ProductionQueue.PrerequisitesAvailable/Unavailable/ItemHidden/ItemVisible diagnostics.
	// User-Fund 2026-07-30: "aot-mtnk-base bleibt im Baumenue sichtbar, obwohl das Predator-
	// Upgrade gekauft ist und Tooltip/Tests zweifelsfrei belegen, dass es die falsche Proxy-
	// Definition ist." Statische Pruefung der Prerequisites-Strings/TechTree-Logik (mehrfach,
	// gegen den nachweislich funktionierenden JEEP/HUMVEE-Fall) fand keinen Unterschied -- diese
	// vier Methoden sind die einzige Stelle, an der die Engine tatsaechlich zwischen "sichtbar"/
	// "versteckt"/"baubar" umschaltet, ein Trace hier zeigt live, was wirklich passiert.
	// Off by default; aktiviert via AOT_DEBUG_PRODQUEUE=1 (siehe debug-prodqueue.sh). Optional
	// auf ein Actor-Namensfragment eingrenzen via AOT_DEBUG_PRODQUEUE_FILTER (z.B. "aot-mtnk"),
	// sonst feuert das bei JEDEM Buildable im Spiel und flutet das Log.
	public static class AotProductionQueueDebug
	{
		public static readonly bool Enabled = Environment.GetEnvironmentVariable("AOT_DEBUG_PRODQUEUE") == "1";
		static readonly string Filter = Environment.GetEnvironmentVariable("AOT_DEBUG_PRODQUEUE_FILTER");

		static AotProductionQueueDebug()
		{
			if (Enabled)
				Log.AddChannel("prodqueue", "prodqueue.log");
		}

		public static void Trace(string queueActor, string key, string message)
		{
			if (!Enabled)
				return;

			if (!string.IsNullOrEmpty(Filter) && !key.Contains(Filter, StringComparison.OrdinalIgnoreCase))
				return;

			Log.Write("prodqueue", $"queue-owner={queueActor} key={key} {message}");
		}
	}
}
