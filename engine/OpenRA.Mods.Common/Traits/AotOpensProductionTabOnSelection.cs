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
	[Desc("aotmod: Wenn dieser Aktor selektiert wird, wechselt die Produktions-Sidebar ",
		"automatisch zur angegebenen Tab-Gruppe (z.B. ATEC/STEC -> Upgrade-Menue). ",
		"Reiner Marker -- die Logik liegt in ProductionTabsWidget.Tick().")]
	public sealed class AotOpensProductionTabOnSelectionInfo : TraitInfo
	{
		[FieldLoader.Require]
		[Desc("Name der Produktions-Tab-Gruppe, zu der beim Selektieren gewechselt wird (z.B. Upgrade).")]
		public readonly string Group = null;

		public override object Create(ActorInitializer init) { return new AotOpensProductionTabOnSelection(this); }
	}

	public sealed class AotOpensProductionTabOnSelection
	{
		public readonly AotOpensProductionTabOnSelectionInfo Info;
		public AotOpensProductionTabOnSelection(AotOpensProductionTabOnSelectionInfo info) { Info = info; }
	}
}
