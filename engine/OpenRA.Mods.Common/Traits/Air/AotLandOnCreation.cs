#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Forces a freshly produced aircraft to dock and land at a rearm pad the first
 * time it becomes idle, instead of hovering at cruise altitude. Bounded to a short
 * window after creation: if the actor is given an order (e.g. attack) before ever
 * going idle, its true "first ever idle" can happen much later - e.g. when it
 * returns from combat to resupply. Without the window, this trait would wrongly
 * hijack that resupply-resume idle event and force yet another landing, eating the
 * activity slot that AotReturnWhenEmpty needs for its post-rearm FlyAttack resume.
 */
#endregion

using OpenRA.Mods.Common.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Docks and lands at a rearm pad if the actor becomes idle within a short window after creation. Ignored once the window has passed, so it can't misfire on a much later idle event (e.g. after a combat rearm).")]
	public class AotLandOnCreationInfo : TraitInfo, Requires<AircraftInfo>, Requires<RearmableInfo>
	{
		[Desc("Ticks after creation during which a becoming-idle event is treated as \"just spawned\" and forces a landing.")]
		public readonly int WindowTicks = 250;

		public override object Create(ActorInitializer init) { return new AotLandOnCreation(this); }
	}

	public class AotLandOnCreation : INotifyCreated, INotifyBecomingIdle
	{
		readonly AotLandOnCreationInfo info;
		bool handled;
		int createdTick;

		public AotLandOnCreation(AotLandOnCreationInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			createdTick = self.World.WorldTick;
		}

		void INotifyBecomingIdle.OnBecomingIdle(Actor self)
		{
			if (handled)
				return;

			handled = true;

			if (self.World.WorldTick - createdTick > info.WindowTicks)
				return;

			var aircraft = self.Trait<Aircraft>();
			if (aircraft.GetActorBelow() == null)
				self.QueueActivity(false, new ReturnToBase(self, null, true));
		}
	}
}
