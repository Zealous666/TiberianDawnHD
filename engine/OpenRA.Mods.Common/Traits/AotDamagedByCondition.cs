#region Copyright & License Information
/*
 * Age of Tiberium mod — periodic damage while a condition is active (e.g. standing in
 * toxic gas). Same shape as DamagedByTerrain, but condition-gated instead of terrain-gated
 * so it isn't tied to a fixed list of terrain types.
 */
#endregion

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

		public override object Create(ActorInitializer init) { return new AotDamagedByCondition(this); }
	}

	public class AotDamagedByCondition : ConditionalTrait<AotDamagedByConditionInfo>, ITick
	{
		int damageTicks;

		public AotDamagedByCondition(AotDamagedByConditionInfo info)
			: base(info) { }

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || --damageTicks > 0)
				return;

			if (!self.IsInWorld)
				return;

			self.InflictDamage(self.World.WorldActor, new Damage(Info.Damage, Info.DamageTypes));
			damageTicks = Info.DamageInterval;
		}
	}
}
