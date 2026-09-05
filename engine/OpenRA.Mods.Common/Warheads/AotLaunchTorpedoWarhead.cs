#region Copyright & License Information
/*
 * Age of Tiberium mod — parachuted torpedo splashdown.
 *
 * Used by the Sub-Hunter Corvette's Orca Bomber: the bomber drops parachuted torpedoes, and
 * when one touches the water this warhead fires the real homing torpedo from the splash point
 * at the nearest valid submarine. Unlike FireClusterWarhead (which fires at fixed cells around
 * the impact) the torpedo gets the submarine itself as a guided target, so it swims after it.
 */
#endregion

using System.Linq;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Effects;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Warheads
{
	[Desc("Fires a homing weapon from the point of impact at the nearest valid target actor.")]
	public class AotLaunchTorpedoWarhead : Warhead, IRulesetLoaded<WeaponInfo>
	{
		[WeaponReference]
		[FieldLoader.Require]
		[Desc("Weapon to launch. Has to be defined in weapons.yaml as well.")]
		public readonly string Weapon = null;

		[Desc("Search radius for a target to home on.")]
		public readonly WDist SearchRadius = WDist.FromCells(12);

		WeaponInfo weapon;

		public void RulesetLoaded(Ruleset rules, WeaponInfo info)
		{
			if (!rules.Weapons.TryGetValue(Weapon.ToLowerInvariant(), out weapon))
				throw new YamlException($"Weapons Ruleset does not contain an entry '{Weapon.ToLowerInvariant()}'");
		}

		public override void DoImpact(in Target target, WarheadArgs args)
		{
			if (target.Type == TargetType.Invalid)
				return;

			var firedBy = args.SourceActor;
			if (firedBy == null || firedBy.IsDead)
				return;

			var world = firedBy.World;
			var pos = target.CenterPosition;

			// Pick the nearest actor the torpedo may attack (submarines/ships), enemies only.
			var candidate = world.FindActorsInCircle(pos, SearchRadius)
				.Where(a => a != firedBy && !a.IsDead && a.IsInWorld
					&& firedBy.Owner.RelationshipWith(a.Owner) == PlayerRelationship.Enemy
					&& weapon.IsValidAgainst(Target.FromActor(a), world, firedBy))
				.OrderBy(a => (a.CenterPosition - pos).HorizontalLengthSquared)
				.FirstOrDefault();

			var guided = candidate != null ? Target.FromActor(candidate) : Target.FromPos(pos);

			var projectileArgs = new ProjectileArgs
			{
				Weapon = weapon,
				Facing = (guided.CenterPosition - pos).Yaw,
				CurrentMuzzleFacing = () => (guided.CenterPosition - pos).Yaw,
				DamageModifiers = args.DamageModifiers,
				InaccuracyModifiers = [],
				RangeModifiers = [],
				Source = pos,
				CurrentSource = () => pos,
				SourceActor = firedBy,
				PassiveTarget = guided.CenterPosition,
				GuidedTarget = guided
			};

			if (projectileArgs.Weapon.Projectile == null)
				return;

			world.AddFrameEndTask(w =>
			{
				var projectile = projectileArgs.Weapon.Projectile.Create(projectileArgs);
				if (projectile != null)
					w.Add(projectile);

				if (projectileArgs.Weapon.Report != null && projectileArgs.Weapon.Report.Length > 0)
					Game.Sound.Play(SoundType.World, projectileArgs.Weapon.Report.Random(world.LocalRandom), pos);
			});
		}
	}
}
