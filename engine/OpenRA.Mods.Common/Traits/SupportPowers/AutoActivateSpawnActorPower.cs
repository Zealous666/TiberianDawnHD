// === Age of Tiberium (aotmod) ===
// AutoActivateSpawnActorPower: SpawnActorPower that fires immediately when the player
// clicks the icon — no map target selection required.
// SelectTarget issues the order directly at the building's own cell.
// Activate starts a BuildDuration countdown (ITick); the marker spawns only after it expires.
//
// AotAge1Power / AotAge2Power / AotAge3Power are thin subclasses with unique type names,
// giving each a unique OrderName in SupportPowerManager.MakeKey(). This is necessary
// because MakeKey uses GetType().Name — two instances of the same type on the same actor
// would produce the same key even with AllowMultiple: true (key = OrderName + "_" + ActorID).
// === Ende aotmod ===

using OpenRA.Mods.Common.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Base class for aotmod Age progression powers. Use AotAge1Power/AotAge2Power/AotAge3Power in YAML.")]
	public class AutoActivateSpawnActorPowerInfo : SupportPowerInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor to spawn.")]
		public readonly string Actor = null;

		[Desc("Amount of time to keep the actor alive in ticks. Value < 0 means this actor will not remove itself.")]
		public readonly int LifeTime = 250;

		[Desc("Ticks to wait after activation before the actor is spawned. 25 ticks = 1 second. 0 = instant.")]
		public readonly int BuildDuration = 0;

		public override object Create(ActorInitializer init) { return new AutoActivateSpawnActorPower(init.Self, this); }
	}

	public class AutoActivateSpawnActorPower : SupportPower, ITick
	{
		int buildCountdown = -1;

		public AutoActivateSpawnActorPower(Actor self, AutoActivateSpawnActorPowerInfo info)
			: base(self, info) { }

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			self.World.IssueOrder(new Order(order, manager.Self, Target.FromCell(self.World, self.Location), false));
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			var info = Info as AutoActivateSpawnActorPowerInfo;

			foreach (var notify in self.TraitsImplementing<INotifySupportPower>())
				notify.Activated(self);

			if (info.BuildDuration <= 0)
				SpawnMarker(self);
			else
				buildCountdown = info.BuildDuration;
		}

		void ITick.Tick(Actor self)
		{
			if (buildCountdown < 0)
				return;

			if (--buildCountdown == 0)
			{
				buildCountdown = -1;
				SpawnMarker(self);
			}
		}

		void SpawnMarker(Actor self)
		{
			var info = Info as AutoActivateSpawnActorPowerInfo;

			self.World.AddFrameEndTask(w =>
			{
				var actor = w.CreateActor(info.Actor,
				[
					new LocationInit(self.Location),
					new OwnerInit(self.Owner),
				]);

				if (info.LifeTime > -1)
				{
					actor.QueueActivity(new Wait(info.LifeTime));
					actor.QueueActivity(new RemoveSelf());
				}
			});
		}
	}

	// Thin subclasses — unique type name → unique OrderName in SupportPowerManager.MakeKey().

	[Desc("Age 1 progression power. Fires immediately on icon click.")]
	public class AotAge1PowerInfo : AutoActivateSpawnActorPowerInfo
	{
		public override object Create(ActorInitializer init) { return new AotAge1Power(init.Self, this); }
	}

	public class AotAge1Power : AutoActivateSpawnActorPower
	{
		public AotAge1Power(Actor self, AotAge1PowerInfo info) : base(self, info) { }
	}

	[Desc("Age 2 progression power. Fires immediately on icon click.")]
	public class AotAge2PowerInfo : AutoActivateSpawnActorPowerInfo
	{
		public override object Create(ActorInitializer init) { return new AotAge2Power(init.Self, this); }
	}

	public class AotAge2Power : AutoActivateSpawnActorPower
	{
		public AotAge2Power(Actor self, AotAge2PowerInfo info) : base(self, info) { }
	}

	[Desc("Age 3 progression power. Fires immediately on icon click.")]
	public class AotAge3PowerInfo : AutoActivateSpawnActorPowerInfo
	{
		public override object Create(ActorInitializer init) { return new AotAge3Power(init.Self, this); }
	}

	public class AotAge3Power : AutoActivateSpawnActorPower
	{
		public AotAge3Power(Actor self, AotAge3PowerInfo info) : base(self, info) { }
	}
}
