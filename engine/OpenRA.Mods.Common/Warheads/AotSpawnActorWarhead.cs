#region Copyright & License Information
/*
 * Age of Tiberium mod — spawns a free-standing actor (e.g. a lingering hazard cloud)
 * at the point of impact, owned by the attacking player.
 */
#endregion

using OpenRA.GameRules;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Warheads
{
	[Desc("Spawns an actor at the point of impact.")]
	public class AotSpawnActorWarhead : Warhead
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("Actor type to spawn at the impact position.")]
		public readonly string Actor = null;

		[Desc("Skip spawning if an actor of the same type already exists within this range of the impact. Zero disables the check.")]
		public readonly WDist DedupeRange = WDist.Zero;

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			if (target.Type == TargetType.Invalid)
				return;

			var firedBy = args.SourceActor;
			var world = firedBy.World;
			var cell = world.Map.CellContaining(target.CenterPosition);

			if (!world.Map.Contains(cell))
				return;

			if (DedupeRange > WDist.Zero)
				foreach (var a in world.FindActorsInCircle(target.CenterPosition, DedupeRange))
					if (!a.IsDead && a.IsInWorld && a.Info.Name == Actor)
						return;

			world.AddFrameEndTask(w => w.CreateActor(Actor, new TypeDictionary
			{
				new LocationInit(cell),
				new OwnerInit(firedBy.Owner),
			}));
		}
	}
}
