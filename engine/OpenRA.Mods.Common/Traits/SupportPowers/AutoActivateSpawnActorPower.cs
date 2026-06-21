// === Age of Tiberium (aotmod) ===
// AutoActivateSpawnActorPower: SpawnActorPower that fires when the player clicks the icon.
// AotAutoFireSupportPower: variant that fires automatically when ready (no click needed).
//
// AotAge1SuperPower/AotAge2SuperPower/AotAge3SuperPower are auto-fire variants:
//   - PauseOnCondition holds them until the corresponding upgrade is purchased
//   - ChargeInterval counts down 10 sec automatically once HOLD is released
//   - ITick detects Ready state and issues the order without player input
//   - Spawns the age marker which grants the actual aot-age1/2/3 prerequisite
//
// AotAge1Power/AotAge2Power/AotAge3Power are the old click-to-fire variants (kept for reference).
//
// Subclass pattern: unique type name → unique OrderName in SupportPowerManager.MakeKey()
// (MakeKey uses GetType().Name, so same type on same actor would collide).
// === Ende aotmod ===

using System.Collections.Generic;
using OpenRA;
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Base info for aotmod Age powers.")]
	public class AutoActivateSpawnActorPowerInfo : SupportPowerInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor to spawn when activated.")]
		public readonly string Actor = null;

		[Desc("Ticks to keep the spawned actor alive. -1 = permanent.")]
		public readonly int LifeTime = 250;

		[Desc("Ticks to wait after activation before the actor spawns. 0 = instant.")]
		public readonly int BuildDuration = 0;

		[Desc("Credit cost to activate. 0 = free.")]
		public readonly int Cost = 0;

		public override object Create(ActorInitializer init) { return new AutoActivateSpawnActorPower(init.Self, this); }
	}

	// IEffect-based delayed spawn — ticks independently of the SupportPower trait lifecycle.
	class AotDelayedSpawnEffect : IEffect
	{
		readonly string actorType;
		readonly Player owner;
		readonly CPos location;
		readonly int lifeTime;
		int countdown;

		public AotDelayedSpawnEffect(int delay, string actorType, Player owner, CPos location, int lifeTime)
		{
			countdown = delay;
			this.actorType = actorType;
			this.owner = owner;
			this.location = location;
			this.lifeTime = lifeTime;
		}

		public void Tick(World world)
		{
			if (--countdown > 0)
				return;

			world.Remove(this);
			world.AddFrameEndTask(w =>
			{
				var actor = w.CreateActor(actorType,
				[
					new LocationInit(location),
					new OwnerInit(owner),
				]);

				if (lifeTime > -1)
				{
					actor.QueueActivity(new Wait(lifeTime));
					actor.QueueActivity(new RemoveSelf());
				}
			});
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) => [];
		public IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr) => [];
		public IEnumerable<IRenderable> RenderAnnotations(WorldRenderer wr) => [];
	}

	// Click-to-fire base class (used by AotAge1Power/2/3Power).
	public class AutoActivateSpawnActorPower : SupportPower
	{
		public AutoActivateSpawnActorPower(Actor self, AutoActivateSpawnActorPowerInfo info)
			: base(self, info) { }

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			var info = Info as AutoActivateSpawnActorPowerInfo;

			if (info.Cost > 0)
			{
				var resources = self.Owner.PlayerActor.TraitOrDefault<PlayerResources>();
				if (resources == null || resources.GetCashAndResources() < info.Cost)
				{
					Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech", "InsufficientFunds", self.Owner.Faction.InternalName);
					return;
				}
			}

			self.World.IssueOrder(new Order(order, manager.Self, Target.FromCell(self.World, self.Location), false));
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			var info = Info as AutoActivateSpawnActorPowerInfo;

			if (info.Cost > 0)
			{
				var resources = self.Owner.PlayerActor.TraitOrDefault<PlayerResources>();
				resources?.TakeCash(info.Cost);
			}

			foreach (var notify in self.TraitsImplementing<INotifySupportPower>())
				notify.Activated(self);

			SpawnActor(self, info);
		}

		protected static void SpawnActor(Actor self, AutoActivateSpawnActorPowerInfo info)
		{
			if (info.BuildDuration <= 0)
			{
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
			else
			{
				self.World.Add(new AotDelayedSpawnEffect(
					info.BuildDuration, info.Actor, self.Owner, self.Location, info.LifeTime));
			}
		}
	}

	// Auto-fire base class: fires automatically when the power becomes Ready (no player click).
	// Used by AotAge1SuperPower/AotAge2SuperPower/AotAge3SuperPower.
	public class AotAutoFireSupportPowerInfo : AutoActivateSpawnActorPowerInfo
	{
		public override object Create(ActorInitializer init) { return new AotAutoFireSupportPower(init.Self, this); }
	}

	public class AotAutoFireSupportPower : AutoActivateSpawnActorPower, ITick
	{
		bool fired = false;

		public AotAutoFireSupportPower(Actor self, AotAutoFireSupportPowerInfo info)
			: base(self, info) { }

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech", "NewOptions", self.Owner.Faction.InternalName);
		}

		void ITick.Tick(Actor self)
		{
			if (fired || !self.IsInWorld)
				return;

			var manager = self.Owner.PlayerActor.TraitOrDefault<SupportPowerManager>();
			if (manager == null)
				return;

			var key = Info.AllowMultiple ? Info.OrderName + "_" + self.ActorID : Info.OrderName;
			if (manager.Powers.TryGetValue(key, out var instance) && instance.Ready)
			{
				fired = true;
				self.World.IssueOrder(new Order(key, manager.Self, Target.FromCell(self.World, self.Location), false));
			}
		}
	}

	// Auto-fire subclasses — unique type name per Age level.

	[Desc("Age 1 auto-fire super power. Activates automatically after charge.")]
	public class AotAge1SuperPowerInfo : AotAutoFireSupportPowerInfo
	{
		public override object Create(ActorInitializer init) { return new AotAge1SuperPower(init.Self, this); }
	}

	public class AotAge1SuperPower : AotAutoFireSupportPower
	{
		public AotAge1SuperPower(Actor self, AotAge1SuperPowerInfo info) : base(self, info) { }
	}

	[Desc("Age 2 auto-fire super power. Activates automatically after charge.")]
	public class AotAge2SuperPowerInfo : AotAutoFireSupportPowerInfo
	{
		public override object Create(ActorInitializer init) { return new AotAge2SuperPower(init.Self, this); }
	}

	public class AotAge2SuperPower : AotAutoFireSupportPower
	{
		public AotAge2SuperPower(Actor self, AotAge2SuperPowerInfo info) : base(self, info) { }
	}

	[Desc("Age 3 auto-fire super power. Activates automatically after charge.")]
	public class AotAge3SuperPowerInfo : AotAutoFireSupportPowerInfo
	{
		public override object Create(ActorInitializer init) { return new AotAge3SuperPower(init.Self, this); }
	}

	public class AotAge3SuperPower : AotAutoFireSupportPower
	{
		public AotAge3SuperPower(Actor self, AotAge3SuperPowerInfo info) : base(self, info) { }
	}

	// Click-to-fire subclasses (legacy, kept for reference).

	[Desc("Age 1 click-to-fire power.")]
	public class AotAge1PowerInfo : AutoActivateSpawnActorPowerInfo
	{
		public override object Create(ActorInitializer init) { return new AotAge1Power(init.Self, this); }
	}

	public class AotAge1Power : AutoActivateSpawnActorPower
	{
		public AotAge1Power(Actor self, AotAge1PowerInfo info) : base(self, info) { }
	}

	[Desc("Age 2 click-to-fire power.")]
	public class AotAge2PowerInfo : AutoActivateSpawnActorPowerInfo
	{
		public override object Create(ActorInitializer init) { return new AotAge2Power(init.Self, this); }
	}

	public class AotAge2Power : AutoActivateSpawnActorPower
	{
		public AotAge2Power(Actor self, AotAge2PowerInfo info) : base(self, info) { }
	}

	[Desc("Age 3 click-to-fire power.")]
	public class AotAge3PowerInfo : AutoActivateSpawnActorPowerInfo
	{
		public override object Create(ActorInitializer init) { return new AotAge3Power(init.Self, this); }
	}

	public class AotAge3Power : AutoActivateSpawnActorPower
	{
		public AotAge3Power(Actor self, AotAge3PowerInfo info) : base(self, info) { }
	}
}
