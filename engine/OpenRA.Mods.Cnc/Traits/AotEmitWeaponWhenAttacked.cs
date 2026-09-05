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

using System.Linq;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Fires a weapon at the actor's own position whenever it takes damage, with a cooldown.",
		"Optionally plays a one-shot sprite animation for the same event, then returns to idle.",
		"Used by the veinhole heart: it is indestructible but belches toxic gas when attacked.")]
	sealed class AotEmitWeaponWhenAttackedInfo : ConditionalTraitInfo, Requires<IHealthInfo>
	{
		[WeaponReference]
		[FieldLoader.Require]
		[Desc("Weapon fired at self when damaged.")]
		public readonly string Weapon = null;

		[Desc("Minimum ticks between emissions, so sustained fire does not spam clouds.")]
		public readonly int Cooldown = 75;

		[Desc("Ignore damage of these types (e.g. the gas cloud's own damage, to avoid feedback).")]
		public readonly BitSet<DamageType> IgnoreDamageTypes = default;

		[SequenceReference]
		[Desc("Optional one-shot WithSpriteBody sequence to play while emitting, e.g. the heart",
			"belching gas. Returns to the default idle sequence once it finishes. Leave empty",
			"to not touch the sprite body at all.")]
		public readonly string ReactionSequence = null;

		[Desc("Which WithSpriteBody (by Name) to play ReactionSequence on.")]
		public readonly string Body = "body";

		public WeaponInfo WeaponInfo { get; private set; }

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);
			if (!rules.Weapons.TryGetValue(Weapon.ToLowerInvariant(), out var weapon))
				throw new YamlException($"Weapons Ruleset does not contain an entry '{Weapon.ToLowerInvariant()}'");

			WeaponInfo = weapon;
		}

		public override object Create(ActorInitializer init) { return new AotEmitWeaponWhenAttacked(init.Self, this); }
	}

	sealed class AotEmitWeaponWhenAttacked : ConditionalTrait<AotEmitWeaponWhenAttackedInfo>, INotifyDamage, ITick
	{
		readonly WithSpriteBody wsb;
		int cooldown;
		bool reacting;

		public AotEmitWeaponWhenAttacked(Actor self, AotEmitWeaponWhenAttackedInfo info)
			: base(info)
		{
			if (!string.IsNullOrEmpty(info.ReactionSequence))
				wsb = self.TraitsImplementing<WithSpriteBody>().SingleOrDefault(w => w.Info.Name == info.Body);
		}

		void ITick.Tick(Actor self)
		{
			if (cooldown > 0)
				cooldown--;
		}

		void INotifyDamage.Damaged(Actor self, AttackInfo e)
		{
			if (IsTraitDisabled || e.Damage.Value <= 0 || cooldown > 0)
				return;

			if (!Info.IgnoreDamageTypes.IsEmpty && e.Damage.DamageTypes.Overlaps(Info.IgnoreDamageTypes))
				return;

			cooldown = Info.Cooldown;

			// Impact must land OUTSIDE self's own footprint: a spawned actor (e.g. the gas
			// cloud) created on top of a solid Building's blocking cells cannot path out until
			// that Building dies (Locomotor treats it as an uncrushable stationary blocker).
			var spawnCell = AotFootprintUtils.FindCellOutsideFootprint(self);
			Info.WeaponInfo.Impact(Target.FromPos(self.World.Map.CenterOfCell(spawnCell)), self);

			// One-shot reaction animation, then back to idle. Guarded by `reacting` so a hit
			// landing mid-animation does not restart it and get stuck away from idle.
			if (wsb != null && !reacting)
			{
				reacting = true;
				wsb.PlayCustomAnimation(self, Info.ReactionSequence, () => reacting = false);
			}
		}
	}
}
