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

using System;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Reserve landing places for aircraft.")]
	public sealed class ReservableInfo : ConditionalTraitInfo
	{
		[GrantedConditionReference]
		[Desc("Condition granted while an aircraft has this landing spot reserved.")]
		public readonly string ReservedCondition = null;

		[Desc("Landing offset from actor center for this slot. If non-zero, aircraft will land here instead of using the nearest Exit.")]
		public readonly WVec LandingOffset = WVec.Zero;

		[Desc("Facing the aircraft should adopt when landing at this slot. Leave empty to use the Exit-based or default facing.")]
		public readonly WAngle? LandingFacing = null;

		public override object Create(ActorInitializer init) { return new Reservable(this); }
	}

	public class Reservable : ConditionalTrait<ReservableInfo>, ITick, INotifyOwnerChanged, INotifySold, INotifyActorDisposing, INotifyCreated
	{
		int reservedToken = Actor.InvalidConditionToken;
		Actor reservedFor;
		Aircraft reservedForAircraft;
		RallyPoint rallyPoint;

		public Reservable(ReservableInfo info)
			: base(info) { }

		void INotifyCreated.Created(Actor self)
		{
			rallyPoint = self.TraitOrDefault<RallyPoint>();
		}

		void ITick.Tick(Actor self)
		{
			if (reservedFor == null)
				return;

			if (!Target.FromActor(reservedFor).IsValidFor(self))
			{
				reservedForAircraft.UnReserve();
				reservedFor = null;
				reservedForAircraft = null;
			}
		}

		public IDisposable Reserve(Actor self, Actor forActor, Aircraft forAircraft)
		{
			if (reservedForAircraft != null && reservedForAircraft.MayYieldReservation)
				UnReserve(self);

			reservedFor = forActor;
			reservedForAircraft = forAircraft;

			if (Info.ReservedCondition != null && reservedToken == Actor.InvalidConditionToken)
				reservedToken = self.GrantCondition(Info.ReservedCondition);

			// NOTE: we really don't care about the GC eating DisposableActions that apply to a world *other* than
			// the one we're playing in.
			return new DisposableAction(
				() =>
				{
					if (reservedToken != Actor.InvalidConditionToken)
						reservedToken = self.RevokeCondition(reservedToken);
					reservedFor = null;
					reservedForAircraft = null;
				},
				() => Game.RunAfterTick(() =>
				{
					if (Game.IsCurrentWorld(self.World))
						throw new InvalidOperationException(
							$"Attempted to finalize an undisposed DisposableAction. {forActor.Info.Name} ({forActor.ActorID}) reserved {self.Info.Name} ({self.ActorID})");
				}));
		}

		// Returns the first available non-disabled slot and its reservation, or (null, null) if none free.
		public static (Reservable Slot, IDisposable Reservation) TryReserve(Actor reservable, Actor forActor, Aircraft forAircraft)
		{
			foreach (var res in reservable.TraitsImplementing<Reservable>())
			{
				if (!res.IsTraitDisabled &&
					(res.reservedForAircraft == null || res.reservedForAircraft.MayYieldReservation))
				{
					var reservation = res.Reserve(reservable, forActor, forAircraft);
					return (res, reservation);
				}
			}

			return (null, null);
		}

		public static bool IsReserved(Actor a)
		{
			return a.TraitsImplementing<Reservable>()
				.Any(r => r.reservedForAircraft != null && !r.reservedForAircraft.MayYieldReservation);
		}

		public static bool IsAvailableFor(Actor reservable, Actor forActor)
		{
			var instances = reservable.TraitsImplementing<Reservable>().ToList();
			if (instances.Count == 0)
				return true;

			return instances.Any(r => !r.IsTraitDisabled &&
				(r.reservedForAircraft == null || r.reservedForAircraft.MayYieldReservation || r.reservedFor == forActor));
		}

		// Returns false if ALL slots are disabled (e.g. carrier not deployed) so aircraft won't wait near it.
		public static bool IsUsable(Actor reservable)
		{
			var instances = reservable.TraitsImplementing<Reservable>().ToList();
			return instances.Count == 0 || instances.Any(r => !r.IsTraitDisabled);
		}

		void UnReserve(Actor self)
		{
			if (reservedForAircraft != null)
			{
				if (reservedForAircraft.GetActorBelow() == self)
				{
					// HACK: Cache this in a local var, such that the inner activity of AttackMoveActivity can access the trait easily after reservedForAircraft was nulled
					var aircraft = reservedForAircraft;
					if (rallyPoint != null && rallyPoint.Path.Count > 0)
						foreach (var cell in rallyPoint.Path)
							reservedFor.QueueActivity(new AttackMoveActivity(reservedFor, () => aircraft.MoveTo(cell, 1, targetLineColor: Color.OrangeRed)));
					else
						reservedFor.QueueActivity(new TakeOff(reservedFor));
				}

				reservedForAircraft.UnReserve();
			}
		}

		void INotifyActorDisposing.Disposing(Actor self) { UnReserve(self); }

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner) { UnReserve(self); }

		void INotifySold.Selling(Actor self) { UnReserve(self); }
		void INotifySold.Sold(Actor self) { UnReserve(self); }
	}
}
