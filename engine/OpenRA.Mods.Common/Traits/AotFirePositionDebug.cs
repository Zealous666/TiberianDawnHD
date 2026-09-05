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
	// Fire Position (AttackGarrisoned turret/garrison) diagnostics.
	// Off by default; enabled via AOT_DEBUG_FIREPOSITION=1 (see debug-fireposition.sh),
	// so long play sessions can be inspected from Support/Logs/fireposition.log instead
	// of being pasted into chat by hand.
	public static class AotFirePositionDebug
	{
		public static readonly bool Enabled = Environment.GetEnvironmentVariable("AOT_DEBUG_FIREPOSITION") == "1";

		static AotFirePositionDebug()
		{
			if (Enabled)
				OpenRA.Log.AddChannel("fireposition", "fireposition.log");
		}

		public static void Trace(string message)
		{
			if (Enabled)
				OpenRA.Log.Write("fireposition", message);
		}
	}
}
