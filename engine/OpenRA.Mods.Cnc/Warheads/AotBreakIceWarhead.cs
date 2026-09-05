#region Copyright & License Information
/*
 * Age of Tiberium mod -- removes ice cells (AotIceLayer) at the point of impact, so weapon fire
 * punches holes in a frozen floe just like a ground vehicle breaking through it. Add it to the
 * shared impact warhead (^DamagingExplosion) so any explosive hit that already spawns impact smoke
 * also clears the ice underneath.
 */
#endregion

using OpenRA.GameRules;
using OpenRA.Mods.Cnc.Traits;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Warheads
{
	[Desc("Removes ice cells (AotIceLayer) within a radius of the impact.")]
	public class AotBreakIceWarhead : Warhead
	{
		[Desc("Ice cells within this many cells of the impact cell are removed. 0 = only the impact cell.")]
		public readonly int Radius = 0;

		[Desc("Splash sound(s) played when the ice actually breaks (one is picked at random).",
			"Same set the break-through-on-standing splash uses, so shooting and driving sound the same.")]
		public readonly string[] SplashSounds = ["ssplash1.aud", "ssplash2.aud", "ssplash3.aud"];

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			if (target.Type == TargetType.Invalid)
				return;

			var firedBy = args.SourceActor;
			if (firedBy == null)
				return;

			var world = firedBy.World;
			var layer = world.WorldActor.TraitOrDefault<AotIceLayer>();
			if (layer == null)
				return;

			var center = world.Map.CellContaining(target.CenterPosition);
			if (!world.Map.Contains(center))
				return;

			var broke = false;
			if (Radius <= 0)
			{
				if (layer.Contains(center))
				{
					layer.Remove(center);
					broke = true;
				}
			}
			else
			{
				foreach (var cell in world.Map.FindTilesInCircle(center, Radius))
				{
					if (layer.Contains(cell))
					{
						layer.Remove(cell);
						broke = true;
					}
				}
			}

			// Splash sound once per impact (only when the ice actually broke -- silent if we hit
			// solid ground or already-open water). Cosmetic RNG: LocalRandom, never SharedRandom.
			if (broke && SplashSounds != null && SplashSounds.Length > 0)
				Game.Sound.Play(SoundType.World, SplashSounds.Random(world.LocalRandom), target.CenterPosition);
		}
	}
}
