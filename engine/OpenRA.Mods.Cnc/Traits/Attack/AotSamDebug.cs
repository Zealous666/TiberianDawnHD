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

namespace OpenRA.Mods.Cnc.Traits
{
	// AotAttackPopupOrTurreted (SAM) diagnostics -- User-Fund 2026-07-30: "SAM feuerte bei
	// Stromausfall weiter, obwohl sichtbar abgedunkelt". Statische Code-Pruefung (CanAttack,
	// PauseOnCondition-Verdrahtung, ^DisabledOverlay) fand keinen Fehler -- braucht einen echten
	// Trace zur Laufzeit. Off by default; aktiviert via AOT_DEBUG_SAM=1 (siehe debug-sam.sh),
	// damit lange Sessions ueber Support/Logs/sam.log statt Chat-Copy-Paste inspiziert werden.
	public static class AotSamDebug
	{
		public static readonly bool Enabled = Environment.GetEnvironmentVariable("AOT_DEBUG_SAM") == "1";

		static AotSamDebug()
		{
			if (Enabled)
				Log.AddChannel("sam", "sam.log");
		}

		public static void Trace(string message)
		{
			if (Enabled)
				Log.Write("sam", message);
		}
	}
}
