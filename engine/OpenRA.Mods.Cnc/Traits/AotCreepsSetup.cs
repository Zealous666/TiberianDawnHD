#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Makes the Creeps player hostile to all human-playable players automatically,
 * without requiring "Enemies: Multi0, Multi1, ..." in every map file.
 */
#endregion

using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("aotmod: Setzt den Creeps-Spieler beim Spielstart automatisch als Feind aller spielbaren Spieler.",
		"Muss auf dem World-Aktor platziert werden.")]
	public class AotCreepsSetupInfo : TraitInfo
	{
		[Desc("Interner Name des Creeps-Spielers.")]
		public readonly string CreepsPlayerName = "Creeps";

		public override object Create(ActorInitializer init) { return new AotCreepsSetup(this); }
	}

	public class AotCreepsSetup : IWorldLoaded
	{
		readonly AotCreepsSetupInfo info;

		public AotCreepsSetup(AotCreepsSetupInfo info)
		{
			this.info = info;
		}

		void IWorldLoaded.WorldLoaded(World world, OpenRA.Graphics.WorldRenderer wr)
		{
			var creeps = world.Players.FirstOrDefault(p => p.InternalName == info.CreepsPlayerName);
			if (creeps == null)
				return;

			foreach (var p in world.Players.Where(p => p.Playable))
			{
				creeps.EnemyPlayersMask = creeps.EnemyPlayersMask.Union(p.PlayerMask);
				p.EnemyPlayersMask = p.EnemyPlayersMask.Union(creeps.PlayerMask);
			}
		}
	}
}
