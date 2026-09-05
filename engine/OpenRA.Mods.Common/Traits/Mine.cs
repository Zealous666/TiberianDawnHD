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
	public sealed class MineInfo : TraitInfo
	{
		public readonly BitSet<CrushClass> CrushClasses = default;
		public readonly bool AvoidFriendly = true;
		public readonly bool BlockFriendly = true;
		public readonly BitSet<CrushClass> DetonateClasses = default;

		[Desc("Ticks between triggering (surface) and detonation. 0 = instant.")]
		public readonly int DetonateDelay = 0;

		[GrantedConditionReference]
		[Desc("Condition granted when mine is triggered (before detonation). Use to pause Cloak so mine surfaces.")]
		public readonly string TriggeredCondition = null;

		public override object Create(ActorInitializer init) { return new Mine(this); }
	}

	public sealed class Mine : ICrushable, INotifyCrushed, ITick
	{
		readonly MineInfo info;
		int detonateCountdown = -1;
		Actor triggeringCrusher;
		BitSet<DamageType> crushDamageTypes;

		public Mine(MineInfo info)
		{
			this.info = info;
		}

		void INotifyCrushed.WarnCrush(Actor self, Actor crusher, BitSet<CrushClass> crushClasses) { }

		void INotifyCrushed.OnCrush(Actor self, Actor crusher, BitSet<CrushClass> crushClasses)
		{
			if (!info.CrushClasses.Overlaps(crushClasses))
				return;

			if (crusher.Info.HasTraitInfo<MineImmuneInfo>() || (self.Owner.RelationshipWith(crusher.Owner) == PlayerRelationship.Ally && info.AvoidFriendly))
				return;

			var mobile = crusher.TraitOrDefault<Mobile>();
			if (mobile != null && !info.DetonateClasses.Overlaps(mobile.Info.LocomotorInfo.Crushes))
				return;

			if (info.DetonateDelay <= 0)
			{
				self.Kill(crusher, mobile != null ? mobile.Info.LocomotorInfo.CrushDamageTypes : default);
				return;
			}

			// Already triggered — ignore repeated crushes
			if (detonateCountdown >= 0)
				return;

			triggeringCrusher = crusher;
			crushDamageTypes = mobile != null ? mobile.Info.LocomotorInfo.CrushDamageTypes : default;
			detonateCountdown = info.DetonateDelay;

			if (!string.IsNullOrEmpty(info.TriggeredCondition))
				self.GrantCondition(info.TriggeredCondition);
		}

		void ITick.Tick(Actor self)
		{
			if (detonateCountdown < 0)
				return;

			if (--detonateCountdown <= 0)
				self.Kill(triggeringCrusher, crushDamageTypes);
		}

		bool ICrushable.CrushableBy(Actor self, Actor crusher, BitSet<CrushClass> crushClasses)
		{
			if (info.BlockFriendly && !crusher.Info.HasTraitInfo<MineImmuneInfo>() && self.Owner.RelationshipWith(crusher.Owner) == PlayerRelationship.Ally)
				return false;

			return info.CrushClasses.Overlaps(crushClasses);
		}

		LongBitSet<PlayerBitMask> ICrushable.CrushableBy(Actor self, BitSet<CrushClass> crushClasses)
		{
			if (!info.CrushClasses.Overlaps(crushClasses))
				return self.World.NoPlayersMask;

			// Friendly units should move around!
			return info.BlockFriendly ? self.World.AllPlayersMask.Except(self.Owner.AlliedPlayersMask) : self.World.AllPlayersMask;
		}
	}

	[Desc("Tag trait for stuff that should not trigger mines.")]
	public sealed class MineImmuneInfo : TraitInfo<MineImmune> { }
	public sealed class MineImmune { }
}
