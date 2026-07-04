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
	[Desc("aotmod: Erlaubt Locomotors mit passendem Crushes-Eintrag, diese Zelle frei zu ",
		"durchqueren (z.B. Zone Trooper ueber Mauern/Tore) -- OHNE Zerstoerungs-Wirkung. ",
		"Implementiert absichtlich NUR ICrushable (Pfadfindungs-Passierbarkeit), NICHT ",
		"INotifyCrushed -- es gibt also keinen Crush-Sound, keinen Schaden, keine ",
		"Zerstoerung. Kein echtes 'Crushen', nur eine Wiederverwendung des Engine-Hooks ",
		"fuer Zellen-Passierbarkeit. Ergaenzt bestehende Crushable-Traits, ersetzt sie nicht.")]
	sealed class AotHoverPassableInfo : TraitInfo
	{
		[Desc("Welche Locomotor-Crushes-Klassen diese Zelle frei durchqueren duerfen.")]
		public readonly BitSet<CrushClass> CrushClasses = new("aothover");

		public override object Create(ActorInitializer init) { return new AotHoverPassable(this); }
	}

	sealed class AotHoverPassable : ICrushable
	{
		readonly AotHoverPassableInfo info;

		public AotHoverPassable(AotHoverPassableInfo info)
		{
			this.info = info;
		}

		bool ICrushable.CrushableBy(Actor self, Actor crusher, BitSet<CrushClass> crushClasses)
		{
			return info.CrushClasses.Overlaps(crushClasses);
		}

		LongBitSet<PlayerBitMask> ICrushable.CrushableBy(Actor self, BitSet<CrushClass> crushClasses)
		{
			if (!info.CrushClasses.Overlaps(crushClasses))
				return self.World.NoPlayersMask;

			return self.World.AllPlayersMask;
		}
	}
}
