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
using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Orders;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Attach this to the player actor.")]
	public class SupportPowerManagerInfo : TraitInfo, Requires<DeveloperModeInfo>, Requires<TechTreeInfo>
	{
		public override object Create(ActorInitializer init) { return new SupportPowerManager(init); }
	}

	public class SupportPowerManager : ITick, IResolveOrder, ITechTreeElement, INotifyCreated
	{
		public readonly Actor Self;
		public readonly Dictionary<string, SupportPowerInstance> Powers = [];

		public readonly DeveloperMode DevMode;
		public readonly TechTree TechTree;
		public readonly Lazy<RadarPings> RadarPings;

		public SupportPowerManager(ActorInitializer init)
		{
			Self = init.Self;
			DevMode = Self.Trait<DeveloperMode>();
			TechTree = Self.Trait<TechTree>();
			RadarPings = Exts.Lazy(Self.World.WorldActor.TraitOrDefault<RadarPings>);

			init.World.ActorAdded += ActorAdded;
			init.World.ActorRemoved += ActorRemoved;
		}

		// aotmod: Support Powers, die direkt auf dem SPIELER-Aktor sitzen, registrieren.
		// Der Spieler-Aktor wird mit "new Actor(...)" erzeugt und NIE ueber World.Add()
		// eingehaengt (Player.cs) -- das ActorAdded-Event oben feuert fuer ihn also nie, und
		// seine eigenen SupportPower-Traits blieben unsichtbar. INotifyCreated laeuft, nachdem
		// alle Traits des Aktors konstruiert sind, ist also der richtige Zeitpunkt dafuer
		// (im Konstruktor gaebe es die SupportPower-Traits je nach Reihenfolge noch nicht).
		// Age of Tiberium nutzt das fuer die Age-Aufstiegs-Powers: sie haengen damit am Spieler
		// statt am Bauhof und verschwinden nicht mehr, wenn der MCV eingepackt wird.
		void INotifyCreated.Created(Actor self)
		{
			ActorAdded(self);
		}

		static string MakeKey(SupportPower sp)
		{
			return sp.Info.AllowMultiple ? sp.Info.OrderName + "_" + sp.Self.ActorID : sp.Info.OrderName;
		}

		void ActorAdded(Actor a)
		{
			if (a.Owner != Self.Owner)
				return;

			foreach (var t in a.TraitsImplementing<SupportPower>())
			{
				var key = MakeKey(t);

				if (!Powers.TryGetValue(key, out var spi))
				{
					Powers.Add(key, spi = t.CreateInstance(key, this));

					if (t.Info.Prerequisites.Length > 0)
					{
						TechTree.Add(key, t.Info.Prerequisites, 0, this);
						TechTree.Update();
					}
				}

				spi.Instances.Add(t);
			}
		}

		void ActorRemoved(Actor a)
		{
			if (a.Owner != Self.Owner || !a.Info.HasTraitInfo<SupportPowerInfo>())
				return;

			foreach (var t in a.TraitsImplementing<SupportPower>())
			{
				var key = MakeKey(t);
				Powers[key].Instances.Remove(t);

				if (Powers[key].Instances.Count == 0 && !Powers[key].Disabled)
				{
					Powers.Remove(key);
					TechTree.Remove(key);
					TechTree.Update();
				}
			}
		}

		void ITick.Tick(Actor self)
		{
			foreach (var power in Powers.Values)
				power.Tick();
		}

		public void ResolveOrder(Actor self, Order order)
		{
			// order.OrderString is the key of the support power
			if (Powers.TryGetValue(order.OrderString, out var sp))
				sp.Activate(order);
		}

		static readonly SupportPowerInstance[] NoInstances = [];

		public IEnumerable<SupportPowerInstance> GetPowersForActor(Actor a)
		{
			if (Powers.Count == 0 || a.Owner != Self.Owner || !a.Info.HasTraitInfo<SupportPowerInfo>())
				return NoInstances;

			return a.TraitsImplementing<SupportPower>()
				.Select(t => Powers[MakeKey(t)])
				.Where(p => p.Instances.Any(i => !i.IsTraitDisabled && i.Self == a));
		}

		public void PrerequisitesAvailable(string key)
		{
			if (!Powers.TryGetValue(key, out var sp))
				return;

			sp.PrerequisitesAvailable(true);
		}

		public void PrerequisitesUnavailable(string key)
		{
			if (!Powers.TryGetValue(key, out var sp))
				return;

			sp.PrerequisitesAvailable(false);
		}

		public void PrerequisitesItemHidden(string key) { }
		public void PrerequisitesItemVisible(string key) { }
	}

	public class SupportPowerInstance
	{
		protected readonly SupportPowerManager Manager;

		public readonly string Key;

		public readonly List<SupportPower> Instances = [];
		public readonly int TotalTicks;

		protected int remainingSubTicks;
		public int RemainingTicks => remainingSubTicks / 100;
		public bool Active { get; private set; }
		public bool Disabled =>
			Manager.Self.Owner.WinState == WinState.Lost ||
			(!prereqsAvailable && !Manager.DevMode.AllTech) ||
			!instancesEnabled ||
			oneShotFired;

		public SupportPowerInfo Info { get { return Instances.Select(i => i.Info).FirstOrDefault(); } }
		public readonly string Name;
		public readonly string Description;
		// virtual fuer die Age-Aufstiege (aotmod 2026-08-05): die laufen nach Age-of-Empires-Muster
		// -- erst KAUFEN, dann forschen -- und muessen daher klickbar sein, BEVOR der Zaehler
		// gelaufen ist, und unklickbar, WAEHREND er laeuft. Genau umgekehrt zu Ion Cannon & Co.,
		// deren Verhalten hier unveraendert bleibt.
		public virtual bool Ready => Active && RemainingTicks == 0;

		bool instancesEnabled;
		bool prereqsAvailable = true;
		protected bool oneShotFired;
		protected bool notifiedCharging;
		bool notifiedReady;

		public void ResetTimer()
		{
			remainingSubTicks = TotalTicks * 100;
		}

		public SupportPowerInstance(string key, SupportPowerInfo info, SupportPowerManager manager)
		{
			Key = key;
			TotalTicks = info.ChargeInterval;
			remainingSubTicks = info.StartFullyCharged ? 0 : TotalTicks * 100;
			Name = info.Name == null ? string.Empty : FluentProvider.GetMessage(info.Name);
			Description = info.Description == null ? string.Empty : FluentProvider.GetMessage(info.Description);

			Manager = manager;
		}

		public virtual void PrerequisitesAvailable(bool available)
		{
			prereqsAvailable = available;

			if (!available)
				remainingSubTicks = TotalTicks * 100;
		}

		public virtual void Tick()
		{
			instancesEnabled = Instances.Any(i => !i.IsTraitDisabled);
			if (!instancesEnabled)
				remainingSubTicks = TotalTicks * 100;

			Active = !Disabled && Instances.Any(i => !i.IsTraitPaused);
			if (!Active)
				return;

			var power = Instances[0];
			if (Manager.DevMode.FastCharge && remainingSubTicks > 2500)
				remainingSubTicks = 2500;

			if (remainingSubTicks > 0)
				remainingSubTicks = (remainingSubTicks - 100).Clamp(0, TotalTicks * 100);

			if (!notifiedCharging)
			{
				power.Charging(power.Self, Key);
				notifiedCharging = true;
			}

			if (RemainingTicks == 0 && !notifiedReady)
			{
				power.Charged(power.Self, Key);
				notifiedReady = true;
			}
		}

		public virtual void Target()
		{
			if (!Ready)
				return;

			var power = Instances.FirstOrDefault(i => !i.IsTraitPaused);

			if (power == null)
				return;

			Game.Sound.PlayToPlayer(SoundType.UI, Manager.Self.Owner, Info.SelectTargetSound);
			Game.Sound.PlayNotification(power.Self.World.Map.Rules, power.Self.Owner, "Speech",
				Info.SelectTargetSpeechNotification, power.Self.Owner.Faction.InternalName);

			TextNotificationsManager.AddTransientLine(power.Self.Owner, Info.SelectTargetTextNotification);

			power.SelectTarget(power.Self, Key, Manager);
		}

		public virtual void Activate(Order order)
		{
			if (!Ready)
				return;

			var power = Instances.Where(i => !i.IsTraitPaused && !i.IsTraitDisabled)
				.MinByOrDefault(a =>
				{
					if (a.Self.OccupiesSpace == null || order.Target.Type == TargetType.Invalid)
						return 0;

					return (a.Self.CenterPosition - order.Target.CenterPosition).HorizontalLengthSquared;
				});

			if (power == null)
				return;

			// Note: order.Subject is the *player* actor
			power.Activate(power.Self, order, Manager);
			remainingSubTicks = TotalTicks * 100;
			notifiedCharging = notifiedReady = false;

			if (Info.OneShot)
			{
				PrerequisitesAvailable(false);
				oneShotFired = true;
			}
		}

		public virtual string IconOverlayTextOverride()
		{
			return null;
		}

		public virtual string TooltipTimeTextOverride()
		{
			return null;
		}
	}

	public class SelectGenericPowerTarget : OrderGenerator
	{
		protected override MouseActionType ActionType => MouseActionType.SupportPower;
		readonly SupportPowerManager manager;
		readonly SupportPowerInfo info;

		public string OrderKey { get; }

		public SelectGenericPowerTarget(string order, SupportPowerManager manager, SupportPowerInfo info)
			: base(manager.Self.World)
		{
			this.manager = manager;
			OrderKey = order;
			this.info = info;
		}

		protected override IEnumerable<Order> OrderInner(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			world.CancelInputMode();
			if (world.Map.Contains(cell))
				yield return new Order(OrderKey, manager.Self, Target.FromCell(world, cell), false) { SuppressVisualFeedback = true };
		}

		protected override void Tick(World world)
		{
			// Cancel the OG if we can't use the power
			if (!manager.Powers.TryGetValue(OrderKey, out var p) || !p.Active || !p.Ready)
				world.CancelInputMode();
		}

		protected override IEnumerable<IRenderable> Render(WorldRenderer wr, World world) { yield break; }
		protected override IEnumerable<IRenderable> RenderAboveShroud(WorldRenderer wr, World world) { yield break; }
		protected override IEnumerable<IRenderable> RenderAnnotations(WorldRenderer wr, World world) { yield break; }
		protected override string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			return world.Map.Contains(cell) ? info.Cursor : info.BlockedCursor;
		}
	}
}
