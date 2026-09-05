#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Leashed air guard: a loitering/hovering aircraft leaves its loiter position to attack enemies
 * that appear within GuardRadius, but breaks off and flies back to that position once the target
 * (or itself) strays beyond LeashRadius, or the target dies. Prevents aircraft from being lured
 * across the map ("Zergen") while still letting them defend the area they are parked over.
 *
 * Design:
 *  - Target selection is fully delegated to the vanilla AutoTarget.ScanForTarget (respects stance,
 *    AutoTargetPriority, visibility, relationships). Set AutoTarget.ScanOnIdle: False so the vanilla
 *    idle-scan does not ALSO issue its own (unleashed) attacks; this trait owns idle engagement.
 *    AutoTarget.ScanRadius (cells) = the guard radius.
 *  - The "home" anchor is captured while loitering and frozen during an engagement, so after a chase
 *    the aircraft returns to where it was guarding, not to wherever the kill happened.
 *  - Only engages while idle (INotifyIdle) -> a player Move order (A->B) is never interrupted.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Loitering aircraft leaves its position to attack nearby enemies, then returns; will not be",
		"lured beyond LeashRadius. Target selection uses AutoTarget (set its ScanRadius to the guard",
		"radius and ScanOnIdle: False).")]
	public class AotAirGuardInfo : ConditionalTraitInfo, Requires<AutoTargetInfo>, Requires<AircraftInfo>
	{
		[Desc("Maximum distance the target (or the aircraft) may stray from the guarded home position",
			"before the aircraft breaks off and flies back. Should be >= the guard radius",
			"(AutoTarget.ScanRadius).")]
		public readonly WDist LeashRadius = WDist.FromCells(15);

		public override object Create(ActorInitializer init) => new AotAirGuard(this);
	}

	public class AotAirGuard : ConditionalTrait<AotAirGuardInfo>, INotifyIdle, ITick, IResolveOrder
	{
		AutoTarget autoTarget;

		WPos home;
		bool homeSet;
		bool returningHome;
		Target engaged = Target.Invalid;

		public AotAirGuard(AotAirGuardInfo info)
			: base(info) { }

		protected override void Created(Actor self)
		{
			autoTarget = self.Trait<AutoTarget>();
			base.Created(self);
		}

		static AttackBase ActiveAttack(Actor self) =>
			self.TraitsImplementing<AttackBase>().FirstOrDefault(a => !a.IsTraitDisabled);

		// At least one armament is loaded and ready to fire (not paused by an empty ammo pool).
		static bool CanFire(AttackBase attack) =>
			attack != null && attack.Armaments.Any(a => !a.IsTraitDisabled && !a.IsTraitPaused);

		bool BeyondLeash(WPos pos) =>
			(pos - home).HorizontalLengthSquared > (long)Info.LeashRadius.Length * Info.LeashRadius.Length;

		void INotifyIdle.TickIdle(Actor self)
		{
			if (IsTraitDisabled)
				return;

			// An engagement that ended leaves us idle (target dead / flown home). ITick handles the
			// fly-back; here we just make sure we don't re-anchor the guard home onto a kill location.
			if (engaged.Type != TargetType.Invalid)
				return;

			var attack = ActiveAttack(self);

			// Leergeschossen: NICHT angreifen. Das Aufmunitieren (Rueckflug + evtl. Wiederaufnahme des
			// Angriffs) uebernimmt AotReturnWhenEmpty. Wuerde die Guard-Routine hier ein neues FlyAttack
			// einreihen, ersetzte das den von AotReturnWhenEmpty eingereihten ReturnToBase -> der Jet
			// liesse sich "abziehen" und kreiste weiter statt aufzumunitieren.
			if (!CanFire(attack))
				return;

			if (returningHome)
				// We have arrived back at the guarded position: keep the existing home, resume guarding.
				returningHome = false;
			else
				// Normal loiter: this is the position we are guarding.
				home = self.CenterPosition;

			homeSet = true;

			var target = autoTarget.ScanForTarget(self, allowMove: true, allowTurn: true);
			if (target.Type == TargetType.Invalid)
				return;

			engaged = target;
			self.QueueActivity(false, attack.GetAttackActivity(self, AttackSource.AutoTarget, target, true, false, Color.Red));
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || engaged.Type == TargetType.Invalid || !homeSet)
				return;

			// Mid-engagement out of ammo: stop tracking and hand over to AotReturnWhenEmpty, which
			// queues the rearm return. Do NOT cancel the activity here -- that would kill its ReturnToBase.
			if (!CanFire(ActiveAttack(self)))
			{
				engaged = Target.Invalid;
				return;
			}

			var targetGone = engaged.Type == TargetType.Actor && (engaged.Actor.IsDead || !engaged.Actor.IsInWorld);

			if (targetGone || BeyondLeash(engaged.CenterPosition) || BeyondLeash(self.CenterPosition))
			{
				engaged = Target.Invalid;
				returningHome = true;

				// Break off the pursuit and fly back to the guarded position; IdleBehavior then
				// resumes the loiter/hover there. If we are already idle at home this is a no-op fly.
				self.CancelActivity();
				self.QueueActivity(false, new Fly(self, Target.FromPos(home)));
			}
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			// Any player/AI order relinquishes the current guard engagement so we never cancel a
			// commanded move/attack. When the unit next goes idle we re-anchor and resume guarding.
			if (order.OrderString == "Move" || order.OrderString == "AttackMove" || order.OrderString == "Attack" ||
				order.OrderString == "ForceAttack" || order.OrderString == "Stop" || order.OrderString == "Scatter" ||
				order.OrderString == "Guard" || order.OrderString == "Enter" || order.OrderString == "ReturnToBase" ||
				order.OrderString == "Land")
			{
				engaged = Target.Invalid;
				returningHome = false;
			}
		}

		protected override void TraitDisabled(Actor self)
		{
			engaged = Target.Invalid;
			returningHome = false;
		}
	}
}
