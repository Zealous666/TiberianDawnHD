#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Behaviour for drones spawned by AotDroneAttack.
 * The drone registers itself with the sub on creation and flies back when idle too long
 * or when the sub is given a move order. Ammo is only restored after the drone lands.
 * When the sub is destroyed, drones go rogue: they change to a hostile faction and
 * attack everything independently until shot down.
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Behaviour for AotDroneAttack drones. Registers with the sub and flies back when idle.")]
	public class AotDroneLogicInfo : TraitInfo
	{
		[Desc("Ticks without an active target before the drone flies back to the sub.")]
		public readonly int IdleReturnDelay = 150;

		[Desc("How close the drone must get to the sub before it despawns (WDist).")]
		public readonly WDist DockRange = WDist.FromCells(1);

		[Desc("Sound played when the drone launches from the sub.")]
		public readonly string TakeoffSound = null;

		[Desc("Sound played when the drone docks back at the sub.")]
		public readonly string LandingSound = null;

		[GrantedConditionReference]
		[Desc("Condition granted when the parent sub is destroyed.")]
		public readonly string RogueCondition = null;

		[Desc("Cell radius scanned for targets when the drone goes rogue.")]
		public readonly int RogueScanRadius = 10;

		public override object Create(ActorInitializer init) => new AotDroneLogic(init, this);
	}

	public class AotDroneLogic : INotifyCreated, ITick, INotifyIdle, INotifyKilled, INotifyAttack, INotifyAppliedDamage
	{
		readonly AotDroneLogicInfo info;
		readonly Actor sub;
		int idleTicks;
		bool registered;
		bool returning;
		bool subDeathHandled;
		bool isRogue;

		public AotDroneLogic(ActorInitializer init, AotDroneLogicInfo info)
		{
			this.info = info;
			sub = init.GetOrDefault<AotDroneAttackInit>()?.Value;
		}

		void INotifyCreated.Created(Actor self)
		{
			if (sub != null && !sub.IsDead && sub.IsInWorld)
			{
				sub.Trait<AotDroneAttack>().RegisterDrone(sub, self);
				registered = true;
			}

			if (!string.IsNullOrEmpty(info.TakeoffSound))
				Game.Sound.Play(SoundType.World, info.TakeoffSound, self.CenterPosition);
		}

		void ITick.Tick(Actor self)
		{
			if (!subDeathHandled && sub != null && (sub.IsDead || !sub.IsInWorld))
			{
				subDeathHandled = true;
				GoRogue(self);
			}
		}

		// Called by AotDroneAttack when the sub is destroyed — drone goes rogue.
		public void GoRogue(Actor self)
		{
			Log.Write("debug", "[Rogue Drone] GoRogue called");
			subDeathHandled = true;
			isRogue = true;
			registered = false;
			returning = false;
			self.CancelActivity();

			if (!string.IsNullOrEmpty(info.RogueCondition))
				self.GrantCondition(info.RogueCondition);
		}

		// Called by AotDroneAttack.RecallDrones when the sub moves — starts the fly-back.
		public void FlyBackToSub(Actor self)
		{
			if (returning) return;
			returning = true;
			registered = false;
			BeginReturnFlight(self);
		}

		void INotifyIdle.TickIdle(Actor self)
		{
			if (isRogue)
			{
				// Scan all world actors and attack nearest with Health (hostile to everyone).
				var target = self.World.Actors
					.Where(a => !a.IsDead && a.IsInWorld && a != self && a.Info.HasTraitInfo<HealthInfo>())
					.MinByOrDefault(a => (a.CenterPosition - self.CenterPosition).LengthSquared);

				Log.Write("debug", $"[Rogue Drone] TickIdle: target={target?.Info.Name ?? "NULL"}");

				if (target != null)
					self.QueueActivity(false, new FlyAttack(self, AttackSource.AutoTarget, Target.FromActor(target), true, Color.Red));
				return;
			}

			if (returning)
			{
				// Fly completed (drone is idle near sub) — dock and restore ammo.
				ArriveAtSub(self);
				return;
			}

			if (!registered) return;

			idleTicks++;
			if (idleTicks >= info.IdleReturnDelay)
			{
				returning = true;
				registered = false;
				BeginReturnFlight(self);
			}
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			registered = false;
			returning = false;
		}

		void INotifyAppliedDamage.AppliedDamage(Actor self, Actor damaged, AttackInfo e)
		{
			// Target destroyed — tell the sub to recall ALL drones, not just this one.
			if (damaged.IsDead && registered && !returning && sub != null && !sub.IsDead)
				sub.Trait<AotDroneAttack>().RecallDrones(sub);
		}

		void INotifyAttack.PreparingAttack(Actor self, in Target target, Armament a, Barrel barrel) { }

		void INotifyAttack.Attacking(Actor self, in Target target, Armament a, Barrel barrel)
		{
			idleTicks = 0;
		}

		void BeginReturnFlight(Actor self)
		{
			if (sub == null || sub.IsDead || !sub.IsInWorld)
			{
				GoRogue(self);
				return;
			}

			self.QueueActivity(false, new Fly(self, Target.FromActor(sub), info.DockRange));
		}

		void ArriveAtSub(Actor self)
		{
			returning = false;

			if (sub == null || sub.IsDead || !sub.IsInWorld)
			{
				GoRogue(self);
				return;
			}

			if (!string.IsNullOrEmpty(info.LandingSound))
				Game.Sound.Play(SoundType.World, info.LandingSound, self.CenterPosition);

			sub.Trait<AotDroneAttack>().DroneReturned(sub, self);
			self.World.AddFrameEndTask(w => { if (!self.IsDead) w.Remove(self); });
		}
	}
}
