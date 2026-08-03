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
using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public class Resupply : Activity
	{
		readonly IHealth health;
		readonly RepairsUnits[] allRepairsUnits;
		readonly Target host;
		readonly WDist closeEnough;
		readonly Repairable repairable;
		readonly RepairableNear repairableNear;
		readonly Rearmable rearmable;
		readonly INotifyResupply[] notifyResupplies;
		readonly INotifyDockHost[] notifyDockHosts;
		readonly INotifyDockClient[] notifyDockClients;
		readonly ICallForTransport[] transportCallers;
		readonly IMove move;
		readonly Aircraft aircraft;
		readonly IMoveInfo moveInfo;
		readonly bool stayOnResupplier;

		// aotmod: Anfahrt auch dann ausfuehren, wenn es NICHTS zu reparieren/aufmunitionieren gibt
		// (Repairable.AllowUndamagedDocking). Ohne das steht der Anfahrt-Move hinter
		// "activeResupplyTypes != 0" und ein unbeschaedigtes Fahrzeug faehrt nie los.
		readonly bool dockWhenIdle;
		readonly Sellable sellable;
		readonly bool wasRepaired;
		readonly PlayerResources playerResources;
		readonly int unitCost;
		readonly MoveCooldownHelper moveCooldownHelper;

		int remainingTicks;

		// aotmod: Nachlaufzeit, die das Fahrzeug nach fertiger Reparatur noch auf dem Depot steht
		// (RepairsUnitsInfo.LingerAfterRepair). -1 = noch nicht gestartet.
		int lingerTicks = -1;
		bool played;
		bool actualResupplyStarted;
		ResupplyType activeResupplyTypes = ResupplyType.None;

		public Resupply(Actor self, Actor host, WDist closeEnough, bool stayOnResupplier = false, bool dockWhenIdle = false)
		{
			this.host = Target.FromActor(host);
			this.closeEnough = closeEnough;
			this.stayOnResupplier = stayOnResupplier;
			this.dockWhenIdle = dockWhenIdle;
			sellable = self.TraitOrDefault<Sellable>();
			allRepairsUnits = host.TraitsImplementing<RepairsUnits>().ToArray();
			health = self.TraitOrDefault<IHealth>();
			repairable = self.TraitOrDefault<Repairable>();
			repairableNear = self.TraitOrDefault<RepairableNear>();
			rearmable = self.TraitOrDefault<Rearmable>();
			notifyResupplies = host.TraitsImplementing<INotifyResupply>().ToArray();
			notifyDockHosts = host.TraitsImplementing<INotifyDockHost>().ToArray();
			notifyDockClients = self.TraitsImplementing<INotifyDockClient>().ToArray();
			transportCallers = self.TraitsImplementing<ICallForTransport>().ToArray();
			move = self.Trait<IMove>();
			aircraft = move as Aircraft;
			moveInfo = self.Info.TraitInfo<IMoveInfo>();
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
			moveCooldownHelper = new MoveCooldownHelper(self.World, move as Mobile) { RetryIfDestinationBlocked = true };

			var valued = self.Info.TraitInfoOrDefault<ValuedInfo>();
			unitCost = valued != null ? valued.Cost : 0;

			var cannotRepairAtHost = health == null || health.DamageState == DamageState.Undamaged
				|| allRepairsUnits.Length == 0
				|| ((repairable == null || !repairable.Info.RepairActors.Contains(host.Info.Name))
					&& (repairableNear == null || !repairableNear.Info.RepairActors.Contains(host.Info.Name)));

			if (!cannotRepairAtHost)
			{
				activeResupplyTypes |= ResupplyType.Repair;

				// HACK: Reservable logic can't handle repairs, so force a take-off if resupply included repairs.
				// TODO: Make reservation logic or future docking logic properly handle this.
				wasRepaired = true;
			}

			var cannotRearmAtHost = rearmable == null || !rearmable.Info.RearmActors.Contains(host.Info.Name) || rearmable.RearmableAmmoPools.All(p => p.HasFullAmmo);
			if (!cannotRearmAtHost)
				activeResupplyTypes |= ResupplyType.Rearm;
		}

		public override bool Tick(Actor self)
		{
			// Wait for the cooldown to expire before releasing the unit if this was cancelled
			if (IsCanceling && remainingTicks > 0)
			{
				remainingTicks--;
				return false;
			}

			var isHostInvalid = host.Type != TargetType.Actor || !host.Actor.IsInWorld;
			var isCloseEnough = false;
			if (!isHostInvalid)
			{
				// Negative means there's no distance limit.
				// If RepairableNear, use TargetablePositions instead of CenterPosition
				// to ensure the actor moves close enough to the host.
				// Otherwise check against host CenterPosition.
				if (closeEnough < WDist.Zero)
					isCloseEnough = true;
				else if (repairableNear != null)
					isCloseEnough = host.IsInRange(self.CenterPosition, closeEnough);
				else
					isCloseEnough = (host.CenterPosition - self.CenterPosition).HorizontalLengthSquared <= closeEnough.LengthSquared;
			}

			// This ensures transports are also cancelled when the host becomes invalid
			if (!IsCanceling && isHostInvalid)
				Cancel(self, true);

			if (IsCanceling || isHostInvalid)
			{
				// Only tick host INotifyResupply traits one last time if host is still alive
				if (!isHostInvalid)
					foreach (var notifyResupply in notifyResupplies)
						notifyResupply.ResupplyTick(host.Actor, self, ResupplyType.None);

				// HACK: If the activity is cancelled while we're on the host resupplying (or about to start resupplying),
				// move actor outside the resupplier footprint to prevent it from blocking other actors.
				// Additionally, if the host is no longer valid, make aircraft take off.
				if (isCloseEnough || isHostInvalid)
					OnResupplyEnding(self, isHostInvalid);

				return true;
			}

			var result = moveCooldownHelper.Tick(false);
			if (result != null)
				return result.Value;

			if ((activeResupplyTypes != 0 || dockWhenIdle) && aircraft == null && !isCloseEnough)
			{
				var targetCell = self.World.Map.CellContaining(host.Actor.CenterPosition);

				// HACK: Repairable needs the actor to move to host center.
				// TODO: Get rid of this or at least replace it with something less hacky.
				moveCooldownHelper.NotifyMoveQueued();
				if (repairableNear == null)
					QueueChild(move.MoveOntoTarget(self, host, WVec.Zero, null, moveInfo.GetTargetLineColor()));
				else
					QueueChild(move.MoveWithinRange(host, closeEnough, targetLineColor: moveInfo.GetTargetLineColor()));

				var delta = (self.CenterPosition - host.CenterPosition).LengthSquared;
				transportCallers.FirstOrDefault(t => t.MinimumDistance.LengthSquared < delta)?.RequestTransport(self, targetCell);

				return false;
			}

			// We don't want to trigger this until we've reached the resupplier and can start resupplying
			if (!actualResupplyStarted && activeResupplyTypes > 0)
			{
				actualResupplyStarted = true;
				foreach (var t in transportCallers)
					t.MovementCancelled(self);

				foreach (var notifyResupply in notifyResupplies)
					notifyResupply.BeforeResupply(host.Actor, self, activeResupplyTypes);

				foreach (var nd in notifyDockClients)
					nd.Docked(self, host.Actor);

				foreach (var nd in notifyDockHosts)
					nd.Docked(host.Actor, self);
			}

			if (activeResupplyTypes.HasFlag(ResupplyType.Repair))
				RepairTick(self);

			if (activeResupplyTypes.HasFlag(ResupplyType.Rearm) && rearmable.RearmTick(self))
				activeResupplyTypes &= ~ResupplyType.Rearm;

			foreach (var notifyResupply in notifyResupplies)
				notifyResupply.ResupplyTick(host.Actor, self, activeResupplyTypes);

			if (activeResupplyTypes == 0)
			{
				// aotmod (User-Wunsch 2026-08-01): nach abgeschlossener REPARATUR noch kurz stehen
				// bleiben, statt im selben Tick loszufahren. Nur fuer bodengebundene Reparatur --
				// Flugzeuge (aircraft != null) bleiben unangetastet, deren Start-/Landelogik haengt
				// an eigenen Reservierungen (siehe die TakeOffOnResupply-HACK-Notiz unten).
				// dockWhenIdle mit drin (User-Wunsch 2026-08-01): auch eine UNBESCHAEDIGTE Einheit,
				// die nur zum Verkaufen aufs Depot geschickt wurde, muss lange genug stehen bleiben,
				// um den Sell-Cursor darauf ausrichten zu koennen -- sonst waere das Zeitfenster null.
				if ((wasRepaired || dockWhenIdle) && aircraft == null && lingerTicks != 0)
				{
					if (lingerTicks < 0)
					{
						var linger = 0;
						foreach (var r in allRepairsUnits)
							if (r.Info.LingerAfterRepair > linger)
								linger = r.Info.LingerAfterRepair;

						lingerTicks = linger;
					}

					if (lingerTicks > 0)
					{
						lingerTicks--;
						return false;
					}
				}

				OnResupplyEnding(self);
				return true;
			}

			return false;
		}

		public override void Cancel(Actor self, bool keepQueue = false)
		{
			// HACK: force move activities to ignore the transit-only cells when cancelling
			// The idle handler will take over and move them into a safe cell
			if (ChildActivity != null)
				foreach (var c in ChildActivity.ActivitiesImplementing<Move>())
					c.Cancel(self, false, true);

			foreach (var t in transportCallers)
				t.MovementCancelled(self);

			base.Cancel(self, keepQueue);
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (ChildActivity == null)
				yield return new TargetLineNode(host, moveInfo.GetTargetLineColor());
			else
			{
				var current = ChildActivity;
				while (current != null)
				{
					foreach (var n in current.TargetLineNodes(self))
						yield return n;

					current = current.NextActivity;
				}
			}
		}

		void OnResupplyEnding(Actor self, bool isHostInvalid = false)
		{
			var rp = !isHostInvalid ? host.Actor.TraitOrDefault<RallyPoint>() : null;
			if (aircraft != null)
			{
				if (wasRepaired || isHostInvalid || (!stayOnResupplier && aircraft.Info.TakeOffOnResupply))
				{
					if (self.CurrentActivity.NextActivity == null && rp != null && rp.Path.Count > 0)
					{
						moveCooldownHelper.NotifyMoveQueued();
						foreach (var cell in rp.Path)
							QueueChild(new AttackMoveActivity(self, () => move.MoveTo(
								cell,
								1,
								ignoreActor: repairableNear != null ? null : host.Actor,
								targetLineColor: aircraft.Info.TargetLineColor)));
					}
					else
						QueueChild(new TakeOff(self));

					aircraft.UnReserve();
				}

				// Aircraft without TakeOffOnResupply remain on the resupplier until something else needs it
				// The rally point location is queried by the aircraft before it takes off
				else
					aircraft.AllowYieldingReservation();
			}
			else if (!stayOnResupplier && !isHostInvalid && sellable?.IsSelling != true)
			{
				// If there's no next activity, move to rallypoint if available, else just leave host if Repairable.
				// Do nothing if RepairableNear (RepairableNear actors don't enter their host and will likely remain within closeEnough).
				// If there's a next activity and we're not RepairableNear, first leave host if the next activity is not a Move.
				moveCooldownHelper.NotifyMoveQueued();
				if (self.CurrentActivity.NextActivity == null)
				{
					if (rp != null && rp.Path.Count > 0)
						foreach (var cell in rp.Path)
							QueueChild(new AttackMoveActivity(self, () => move.MoveTo(cell, 1, repairableNear != null ? null : host.Actor, true, moveInfo.GetTargetLineColor())));
					else if (repairableNear == null)
						QueueChild(move.MoveToTarget(self, host));
				}
				else if (repairableNear == null && self.CurrentActivity.NextActivity is not Move)
					QueueChild(move.MoveToTarget(self, host));
			}

			foreach (var nd in notifyDockClients)
				nd.Undocked(self, host.Actor);

			foreach (var nd in notifyDockHosts)
				nd.Undocked(host.Actor, self);
		}

		void RepairTick(Actor self)
		{
			var repairsUnits = allRepairsUnits.FirstOrDefault(r => !r.IsTraitDisabled && !r.IsTraitPaused);
			if (repairsUnits == null)
			{
				if (!allRepairsUnits.Any(r => r.IsTraitPaused))
					activeResupplyTypes &= ~ResupplyType.Repair;

				return;
			}

			if (health.DamageState == DamageState.Undamaged)
			{
				if (host.Actor.Owner != self.Owner)
					host.Actor.Owner.PlayerActor.TraitOrDefault<PlayerExperience>()?.GiveExperience(repairsUnits.Info.PlayerExperience);

				Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech", repairsUnits.Info.FinishRepairingNotification, self.Owner.Faction.InternalName);
				TextNotificationsManager.AddTransientLine(self.Owner, repairsUnits.Info.FinishRepairingTextNotification);

				activeResupplyTypes &= ~ResupplyType.Repair;
				return;
			}

			if (remainingTicks == 0)
			{
				var hpToRepair = repairable != null && repairable.Info.HpPerStep > 0 ? repairable.Info.HpPerStep : repairsUnits.Info.HpPerStep;

				// Cast to long to avoid overflow when multiplying by the health
				var value = (long)unitCost * repairsUnits.Info.ValuePercentage;
				var cost = value == 0 ? 0 : Math.Max(1, (int)(hpToRepair * value / (health.MaxHP * 100L)));

				if (!played)
				{
					played = true;
					Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech", repairsUnits.Info.StartRepairingNotification, self.Owner.Faction.InternalName);
					TextNotificationsManager.AddTransientLine(self.Owner, repairsUnits.Info.StartRepairingTextNotification);
				}

				if (!playerResources.TakeCash(cost, true))
				{
					remainingTicks = 1;
					return;
				}

				self.InflictDamage(host.Actor, new Damage(-hpToRepair, repairsUnits.Info.RepairDamageTypes));
				remainingTicks = repairsUnits.Info.Interval;
			}
			else
				--remainingTicks;
		}
	}
}
