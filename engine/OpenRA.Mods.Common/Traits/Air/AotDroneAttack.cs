#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Manages attack drone spawning for the Drone Attack Sub (aot-msub with drone upgrade).
 * Drones are launched when an attack order is given and recalled when the sub moves.
 * Drone count is tracked via an AmmoPool; destroyed drones are not restored until rearmed at pen.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Spawns attack drones when given an attack order. Drones fly back when recalled or idle.")]
	public class AotDroneAttackInfo : ConditionalTraitInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor type to spawn as a drone.")]
		public readonly string DroneActor = null;

		[Desc("Name of the AmmoPool that tracks the number of available drones.")]
		public readonly string AmmoPoolName = "drones";

		[Desc("Maximum range from the sub at which drones will be launched. Attack orders beyond this range are ignored.")]
		public readonly WDist MaxLaunchRange = WDist.FromCells(40);

		[GrantedConditionReference]
		[Desc("Condition granted while at least one drone is active in the world. Use to force the sub to surface.")]
		public readonly string DronesActiveCondition = null;

		public override object Create(ActorInitializer init) => new AotDroneAttack(init, this);
	}

	public class AotDroneAttack : ConditionalTrait<AotDroneAttackInfo>, INotifyCreated, ITick, IResolveOrder, INotifyKilled
	{
		readonly List<Actor> activeDrones = new List<Actor>();
		AmmoPool dronePool;
		int dronesActiveToken = Actor.InvalidConditionToken;

		public AotDroneAttack(ActorInitializer init, AotDroneAttackInfo info)
			: base(info) { }

		void INotifyCreated.Created(Actor self)
		{
			dronePool = self.TraitsImplementing<AmmoPool>()
				.FirstOrDefault(p => p.Info.Name == Info.AmmoPoolName);
		}

		// Called by AotDroneLogic when a drone spawns and registers itself with the sub.
		public void RegisterDrone(Actor self, Actor drone)
		{
			activeDrones.Add(drone);
			UpdateDronesActiveCondition(self);
		}

		// Called by AotDroneLogic when a drone returns to the sub after flying back.
		public void DroneReturned(Actor self, Actor drone)
		{
			activeDrones.Remove(drone);
			dronePool?.GiveAmmo(self, 1);
			UpdateDronesActiveCondition(self);
		}

		void ITick.Tick(Actor self)
		{
			// Silently drop dead drones; ammo is NOT restored (they were destroyed, need pen rearm).
			var hadDrones = activeDrones.Count > 0;
			activeDrones.RemoveAll(d => d.IsDead || !d.IsInWorld);
			if (hadDrones)
				UpdateDronesActiveCondition(self);
		}

		void UpdateDronesActiveCondition(Actor self)
		{
			if (string.IsNullOrEmpty(Info.DronesActiveCondition))
				return;

			var hasActive = activeDrones.Count > 0;
			if (hasActive && dronesActiveToken == Actor.InvalidConditionToken)
				dronesActiveToken = self.GrantCondition(Info.DronesActiveCondition);
			else if (!hasActive && dronesActiveToken != Actor.InvalidConditionToken)
			{
				self.RevokeCondition(dronesActiveToken);
				dronesActiveToken = Actor.InvalidConditionToken;
			}
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (IsTraitDisabled)
				return;

			if (order.OrderString == "Attack" || order.OrderString == "ForceAttack")
			{
				if (order.Target.Type == TargetType.Invalid)
					return;

				var targetPos = order.Target.CenterPosition;
				var dist = (self.CenterPosition - targetPos).HorizontalLength;
				if (dist > Info.MaxLaunchRange.Length)
					return;

				LaunchDrones(self, order.Target, order.OrderString == "ForceAttack");
			}
			else if (order.OrderString == "Move" || order.OrderString == "Stop")
			{
				RecallDrones(self);
			}
		}

		void LaunchDrones(Actor self, in Target target, bool forceAttack = false)
		{
			if (dronePool == null)
				return;

			activeDrones.RemoveAll(d => d.IsDead || !d.IsInWorld);

			var capturedTarget = target;
			var capturedForce = forceAttack;
			while (dronePool.HasAmmo)
			{
				dronePool.TakeAmmo(self, 1);
				var capturedSelf = self;

				self.World.AddFrameEndTask(w =>
				{
					if (capturedSelf.IsDead || !capturedSelf.IsInWorld)
						return;

					var td = new TypeDictionary
					{
						new OwnerInit(capturedSelf.Owner),
						new CenterPositionInit(capturedSelf.CenterPosition),
						new FactionInit(capturedSelf.Owner.Faction.InternalName),
						new AotDroneAttackInit(capturedSelf),
						new SkipMakeAnimsInit(),
					};

					var drone = w.CreateActor(Info.DroneActor, td);
					drone.QueueActivity(false, new FlyAttack(drone, AttackSource.Default, capturedTarget, capturedForce, Color.Red));
				});
			}
		}

		public void RecallDrones(Actor self)
		{
			// Tell each drone to fly back; ammo is restored when they physically arrive.
			foreach (var drone in activeDrones.Where(d => !d.IsDead && d.IsInWorld))
				drone.Trait<AotDroneLogic>().FlyBackToSub(drone);

			// Clear tracking — DroneReturned() will still restore ammo as each drone lands.
			activeDrones.Clear();
			UpdateDronesActiveCondition(self);
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			// Sub destroyed — drones go rogue (change to enemy faction, attack everything).
			foreach (var drone in activeDrones.Where(d => !d.IsDead && d.IsInWorld))
				drone.Trait<AotDroneLogic>().GoRogue(drone);

			activeDrones.Clear();
		}
	}

	// Passed to the drone actor on spawn so it can find its parent sub.
	public class AotDroneAttackInit : ValueActorInit<Actor>, ISuppressInitExport, ISingleInstanceInit
	{
		public AotDroneAttackInit(Actor sub) : base(sub) { }
	}
}
