#region Copyright & License Information
/*
 * Age of Tiberium mod — periodic damage while a condition is active (e.g. standing in
 * toxic gas). Same shape as DamagedByTerrain, but condition-gated instead of terrain-gated
 * so it isn't tied to a fixed list of terrain types.
 */
#endregion

using System;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("This actor receives damage every DamageInterval ticks while the trait is enabled ",
		"(pair with RequiresCondition, e.g. an ExternalCondition granted by a nearby hazard).")]
	public class AotDamagedByConditionInfo : ConditionalTraitInfo, Requires<IHealthInfo>
	{
		[FieldLoader.Require]
		[Desc("Amount of damage received per DamageInterval ticks.")]
		public readonly int Damage = 0;

		[Desc("Delay between receiving damage.")]
		public readonly int DamageInterval = 0;

		[Desc("Apply the damage using these damagetypes.")]
		public readonly BitSet<DamageType> DamageTypes = default;

		[Desc("Never damage the actor below this percentage of its maximum health.",
			"0 (the default) means this damage can kill. Use e.g. 5 to wear a target down to a",
			"sliver without ever finishing it off -- Age of Tiberium uses this so the toxic gas",
			"of the Tiberium Missile ruins tree husks but leaves them standing.")]
		public readonly int MinHealthPercentage = 0;

		public override object Create(ActorInitializer init) { return new AotDamagedByCondition(this); }
	}

	public class AotDamagedByCondition : ConditionalTrait<AotDamagedByConditionInfo>, ITick
	{
		int damageTicks;
		IHealth health;

		public AotDamagedByCondition(AotDamagedByConditionInfo info)
			: base(info) { }

		protected override void Created(Actor self)
		{
			health = self.Trait<IHealth>();
			base.Created(self);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || --damageTicks > 0)
				return;

			if (!self.IsInWorld)
				return;

			var damage = Info.Damage;
			if (Info.MinHealthPercentage > 0)
			{
				// Auf den Rest ueber der Untergrenze kappen, statt den Schlag ganz zu verwerfen:
				// sonst bliebe das Ziel je nach Schadenshoehe weit ueber der gewuenschten Schwelle
				// stehen (ein 4000er Tick auf 1200 Rest-HP wuerde nie angewendet).
				var floor = health.MaxHP * Info.MinHealthPercentage / 100;
				damage = Math.Min(damage, health.HP - floor);
				if (damage <= 0)
				{
					damageTicks = Info.DamageInterval;
					return;
				}
			}

			self.InflictDamage(self.World.WorldActor, new Damage(damage, Info.DamageTypes));
			damageTicks = Info.DamageInterval;
		}
	}
}
