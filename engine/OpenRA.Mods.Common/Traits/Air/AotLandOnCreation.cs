#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Makes a freshly produced aircraft dock and land at a rearm pad instead of hovering at cruise
 * altitude, and KEEPS it landing until it is actually on a pad -- so that sibling helis built from
 * the same pad, which nudge each other off the landing approach, all settle onto the four pads
 * instead of only the first one landing while the rest hover.
 *
 * How "just spawned, should land" is told apart from "returning from combat, leave it alone":
 *   - A rally point / an early player order flies the aircraft away from where it was built. If, on
 *     an idle event, it is farther than MaxSpawnDistance from its spawn, we respect that and never
 *     pull it back (a padded rally point is honoured, and a unit sent to attack stays out).
 *   - A FULL magazine means it has not fired: this is the just-spawned or the nudged-off-while-
 *     guarding case, so land it. A depleted magazine means it is on a combat/rearm cycle -> leave it
 *     to AotReturnWhenEmpty, which lands + rearms + resumes the attack; forcing a landing here would
 *     eat the activity slot that resume needs.
 * Because we re-evaluate on every idle (not just the first), a landed heli that a later sibling
 * nudges off its pad simply lands again.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Docks and lands a freshly produced aircraft at a rearm pad whenever it is idle, near its",
		"spawn and has full ammo -- retrying through sibling nudges until it is actually on a pad.",
		"A rally point or an order that flew it away, or a non-full magazine (combat/rearm), makes it",
		"stay where it is instead. See AotLandOnCreation.cs.")]
	public class AotLandOnCreationInfo : TraitInfo, Requires<AircraftInfo>, Requires<RearmableInfo>
	{
		[Desc("Only force the landing while the aircraft is within this distance of its spawn position.",
			"A rally point or a player order flies it further than this, so those are respected.")]
		public readonly WDist MaxSpawnDistance = WDist.FromCells(2);

		public override object Create(ActorInitializer init) { return new AotLandOnCreation(this); }
	}

	public class AotLandOnCreation : INotifyCreated, INotifyBecomingIdle
	{
		readonly AotLandOnCreationInfo info;

		// Set once a rally point / player order has flown it away from the pad: from then on it is
		// under the player's control and we never drag it back.
		bool givenUp;
		WPos spawnPos;

		public AotLandOnCreation(AotLandOnCreationInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			spawnPos = self.CenterPosition;
		}

		void INotifyBecomingIdle.OnBecomingIdle(Actor self)
		{
			if (givenUp)
				return;

			var aircraft = self.Trait<Aircraft>();

			// Already sitting on a pad -> nothing to do, but stay eligible: a sibling built later can
			// still nudge it off, and then a following idle event lands it again.
			if (aircraft.GetActorBelow() != null)
				return;

			// A rally point or an early order flew it away from where it was built -> respect that.
			if ((self.CenterPosition - spawnPos).HorizontalLengthSquared > (long)info.MaxSpawnDistance.Length * info.MaxSpawnDistance.Length)
			{
				givenUp = true;
				return;
			}

			// Depleted magazine -> it is on a combat/rearm cycle; leave the landing to
			// AotReturnWhenEmpty (do not give up: after rearming it may still need to settle).
			if (!HasFullAmmo(self))
				return;

			self.QueueActivity(false, new ReturnToBase(self, null, true));
		}

		static bool HasFullAmmo(Actor self)
		{
			// True when every ammo pool is full, or the aircraft has no ammo pools (infinite ammo).
			return self.TraitsImplementing<AmmoPool>().All(p => p.HasFullAmmo);
		}
	}
}
