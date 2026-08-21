#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Like GrantConditionOnPrerequisite, but the condition LATCHES: once the prerequisites are first
 * satisfied the condition is granted permanently and never revoked -- not when the prerequisite is
 * later lost (e.g. the tech building is destroyed), and not when the actor is captured by a player
 * who has not reached that state. This makes age-state (and its sprite/bonuses) stick to the
 * building: a captured building keeps the age it was in, and destroying a tech prerequisite no
 * longer reverts it.
 */
#endregion

using System.Collections.Immutable;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Grants a condition once the prerequisites are first available, then keeps it permanently",
		"(never revoked, survives prerequisite loss and capture). See AotGrantConditionOnPrerequisiteLatched.cs.")]
	public class AotGrantConditionOnPrerequisiteLatchedInfo : TraitInfo
	{
		[FieldLoader.Require]
		[GrantedConditionReference]
		[Desc("The condition to grant (permanently, once prerequisites are first met).")]
		public readonly string Condition = null;

		[FieldLoader.Require]
		[Desc("List of required prerequisites that latch the condition when first all available.")]
		public readonly ImmutableArray<string> Prerequisites = [];

		[Desc("Ticks between prerequisite checks while not yet latched.")]
		public readonly int Interval = 5;

		public override object Create(ActorInitializer init) { return new AotGrantConditionOnPrerequisiteLatched(this); }
	}

	public class AotGrantConditionOnPrerequisiteLatched : INotifyCreated, ITick, INotifyOwnerChanged
	{
		readonly AotGrantConditionOnPrerequisiteLatchedInfo info;

		TechTree techTree;
		bool latched;
		int delay;

		public AotGrantConditionOnPrerequisiteLatched(AotGrantConditionOnPrerequisiteLatchedInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			techTree = self.Owner.PlayerActor.Trait<TechTree>();
			TryLatch(self);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			// Already latched -> the condition stays regardless of the new owner's tech state.
			// Not yet latched -> keep watching the NEW owner's tech tree so it can still latch later.
			if (latched)
				return;

			techTree = newOwner.PlayerActor.Trait<TechTree>();
			TryLatch(self);
		}

		void ITick.Tick(Actor self)
		{
			if (latched)
				return;

			if (--delay > 0)
				return;

			delay = info.Interval;
			TryLatch(self);
		}

		void TryLatch(Actor self)
		{
			if (latched || info.Prerequisites.Length == 0)
				return;

			if (!techTree.HasPrerequisites(info.Prerequisites))
				return;

			self.GrantCondition(info.Condition);
			latched = true;
		}
	}
}
