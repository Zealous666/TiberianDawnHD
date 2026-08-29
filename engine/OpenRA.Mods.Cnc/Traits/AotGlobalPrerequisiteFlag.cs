#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * A world-level latch that turns permanently true once ANY player satisfies a
 * given prerequisite (e.g. aot-age1, provided by the Age-1 marker actor).
 * Other traits (TransformsNearResources.ForcePrerequisite) read the flag to cap
 * a timer against "the first player reached this age".
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("aotmod: Latch, der dauerhaft wahr wird, sobald der ERSTE Spieler das angegebene Prerequisite besitzt.",
		"Muss auf dem World-Aktor liegen. Wird von TransformsNearResources.ForcePrerequisite gelesen.")]
	public class AotGlobalPrerequisiteFlagInfo : TraitInfo
	{
		[FieldLoader.Require]
		[Desc("Prerequisite, dessen Erreichen (durch irgendeinen Spieler) den Latch setzt.")]
		public readonly string Prerequisite = null;

		[Desc("Abstand (in Ticks) zwischen den Prueflaeufen, solange der Latch noch nicht gesetzt ist.")]
		public readonly int Interval = 25;

		public override object Create(ActorInitializer init) { return new AotGlobalPrerequisiteFlag(this); }
	}

	public class AotGlobalPrerequisiteFlag : ITick
	{
		public readonly AotGlobalPrerequisiteFlagInfo Info;

		// Einmal wahr, bleibt wahr (auch nach Save/Load unkritisch: rein abgeleiteter Zustand,
		// wird beim naechsten Tick ohnehin wieder auf true gesetzt sobald der Marker existiert).
		public bool Reached { get; private set; }

		int nextCheck;

		public AotGlobalPrerequisiteFlag(AotGlobalPrerequisiteFlagInfo info)
		{
			Info = info;
		}

		void ITick.Tick(Actor self)
		{
			if (Reached)
				return;

			if (--nextCheck > 0)
				return;

			nextCheck = Info.Interval;

			var prereq = new[] { Info.Prerequisite };
			foreach (var p in self.World.Players)
			{
				if (p.NonCombatant)
					continue;

				var techTree = p.PlayerActor.TraitOrDefault<TechTree>();
				if (techTree != null && techTree.HasPrerequisites(prereq))
				{
					Reached = true;
					return;
				}
			}
		}
	}
}
