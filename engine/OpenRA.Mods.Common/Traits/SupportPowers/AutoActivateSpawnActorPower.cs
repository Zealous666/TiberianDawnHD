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
using OpenRA.Mods.Common.Widgets;
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

		// Cost ist 2026-08-04 nach SupportPowerInfo hochgewandert, damit der Tooltip ihn generisch
		// anzeigen kann. Hier NICHT neu deklarieren -- das wuerde die Basisdefinition verdecken und
		// FieldLoader wuerde je nach Bindung ein anderes Feld befuellen als der Tooltip liest.

		[FluentReference(optional: true)]
		[Desc("Text notification shown when the player clicks the power without the cash for it.",
			"Tiberian Dawn has no EVA line for insufficient funds, so the speech notification this",
			"also plays stays silent -- without this line the click gives no feedback whatsoever",
			"(User 2026-08-05: \"es muss eine Not enough money meldung kommen\").")]
		public readonly string InsufficientFundsTextNotification = null;

		[Desc("Age-of-Empires research model (aotmod 2026-08-05): the icon is clickable as soon as the",
			"prerequisites are met, the FULL cost is taken once on that click, and only then does the",
			"timer run -- when it expires the age advances by itself, without a second click.",
			"ChargeInterval is therefore the RESEARCH time, not a wait before you may buy.",
			"Off by default so Ion Cannon, Atom Bomb and every other power keep charging first and",
			"firing on click, exactly as before.")]
		public readonly bool ResearchModel = false;

		public override object Create(ActorInitializer init) { return new AutoActivateSpawnActorPower(init.Self, this); }
	}

	// Buy first, research, then advance on its own -- the Age of Empires order, which is the reverse
	// of a normal support power (charge first, then click to fire).
	//
	// Lives in an instance rather than in the trait because charge state and the Ready flag are
	// instance-side: the icon has to report "clickable" while the counter is still full, and
	// "not clickable" while it runs down. Nothing here touches the base behaviour of any other power.
	public class AotAgeResearchInstance : SupportPowerInstance
	{
		readonly AutoActivateSpawnActorPowerInfo info;
		bool researching;

		public AotAgeResearchInstance(string key, AutoActivateSpawnActorPowerInfo info, SupportPowerManager manager)
			: base(key, info, manager)
		{
			this.info = info;

			// Offered immediately: the player pays to START the research, so there is nothing to wait
			// for beforehand.
			remainingSubTicks = 0;
		}

		// Buyable while idle, never while the research is already running.
		public override bool Ready => Active && !researching;

		public bool Researching => researching;

		// Bought already -- either being researched right now, or finished and spent. The AI fund uses
		// this to tell "not unlocked yet" (worth pre-funding) from "done with" (release the savings),
		// which Disabled alone cannot distinguish.
		public bool Purchased => researching || oneShotFired;

		public override void Tick()
		{
			if (!researching)
			{
				// Deliberately NOT calling base.Tick(): it would count the timer down before anything
				// was bought, which is the very behaviour this model replaces. Active still has to be
				// maintained, so the icon greys out while the prerequisites are missing.
				base.Tick();
				remainingSubTicks = 0;
				return;
			}

			base.Tick();

			if (RemainingTicks > 0)
				return;

			// Research finished -- advance the age without asking again.
			researching = false;
			oneShotFired = info.OneShot;

			var power = Instances.FirstOrDefault(i => !i.IsTraitDisabled);
			power?.Activate(power.Self, new Order(Key, Manager.Self, false), Manager);
		}

		// The tooltip must state how long the research WILL take while the icon is still just an offer
		// -- the default shows RemainingTicks, which is zero before purchase and read as "00:00", i.e.
		// no time at all. Once the research runs, the default remaining-time is exactly right again.
		public override string TooltipTimeTextOverride()
		{
			if (researching)
				return null;

			return WidgetUtils.FormatTime(TotalTicks, Manager.Self.World.Timestep);
		}

		// No "READY" or "ON HOLD" stamped across an Age icon (User 2026-08-05). Those labels describe a
		// charge cycle this power does not have: here the icon is simply buyable or it is not, which
		// the greyed-out state already says. An empty override still suppresses the default; during
		// the research null hands back to the normal countdown.
		public override string IconOverlayTextOverride()
		{
			return researching ? null : "";
		}

		public override void Activate(Order order)
		{
			if (researching || !Ready)
				return;

			var power = Instances.FirstOrDefault(i => !i.IsTraitPaused && !i.IsTraitDisabled);
			if (power == null)
				return;

			// The cash goes now, in full, once. SelectTarget has already refused the click if the
			// player cannot cover it.
			if (info.Cost > 0)
			{
				var resources = Manager.Self.Owner.PlayerActor.TraitOrDefault<PlayerResources>();
				if (resources == null || !resources.TakeCash(info.Cost))
					return;
			}

			researching = true;
			remainingSubTicks = TotalTicks * 100;
			notifiedCharging = false;
			power.Charging(power.Self, Key);
		}
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

		// aotmod: der Host darf auch der SPIELER-Aktor sein (Age-Powers haengen dort, damit sie
		// nicht vom Bauhof abhaengen). Der hat kein IOccupySpace -- self.Location wuerde in eine
		// NullReferenceException laufen. Dann die Startposition des Spielers nehmen: der gespawnte
		// Marker ist unsichtbar und dient nur als Prerequisite-Traeger, die Zelle ist egal, muss
		// aber gueltig sein.
		protected static CPos SpawnLocation(Actor self)
		{
			return self.OccupiesSpace != null ? self.Location : self.Owner.HomeLocation;
		}

		public override SupportPowerInstance CreateInstance(string key, SupportPowerManager manager)
		{
			var info = Info as AutoActivateSpawnActorPowerInfo;
			if (info.ResearchModel)
				return new AotAgeResearchInstance(key, info, manager);

			return base.CreateInstance(key, manager);
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			var info = Info as AutoActivateSpawnActorPowerInfo;

			if (info.Cost > 0)
			{
				var resources = self.Owner.PlayerActor.TraitOrDefault<PlayerResources>();
				if (resources == null || resources.GetCashAndResources() < info.Cost)
				{
					Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech", "InsufficientFunds", self.Owner.Faction.InternalName);
					TextNotificationsManager.AddTransientLine(self.Owner, info.InsufficientFundsTextNotification);
					return;
				}
			}

			self.World.IssueOrder(new Order(order, manager.Self, Target.FromCell(self.World, SpawnLocation(self)), false));
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			var info = Info as AutoActivateSpawnActorPowerInfo;

			// In the research model the instance already took the money when the research was STARTED.
			// Charging here as well would bill the player twice for one upgrade -- once on the click,
			// once when it completes.
			if (info.Cost > 0 && !info.ResearchModel)
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
			var location = SpawnLocation(self);
			if (info.BuildDuration <= 0)
			{
				self.World.AddFrameEndTask(w =>
				{
					var actor = w.CreateActor(info.Actor,
					[
						new LocationInit(location),
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
					info.BuildDuration, info.Actor, self.Owner, location, info.LifeTime));
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
				self.World.IssueOrder(new Order(key, manager.Self, Target.FromCell(self.World, SpawnLocation(self)), false));
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
