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
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public class DeliverBulkOrder : Activity
	{
		readonly Actor producer;
		readonly List<(ActorInfo Actor, int Resources, int Cash)> orderedActors;
		readonly string productionType;
		readonly BulkProductionQueue queue;
		readonly Cargo cargo;
		int delayBetweenUnloads = 0;

		public DeliverBulkOrder(Actor transport, Actor producer, List<(ActorInfo Actor, int Resources, int Cash)> orderedActors,
		string productionType, BulkProductionQueue queue)
		{
			this.producer = producer;
			this.orderedActors = orderedActors;
			this.productionType = productionType;
			this.queue = queue;
			cargo = transport.Trait<Cargo>();
		}

		protected override void OnFirstRun(Actor self)
		{
			// === aotmod 2026-08-05: echte Platzrunde, selbst gerechnet ===
			// Ziel (User-Spec): Anflug von Osten -> Ueberflug des Airfields -> volle Schleife ->
			// Landung GERADE von Ost nach West -> Abflug West.
			//
			// Warum nicht der Land-Aktivitaet ueberlassen: die berechnet ihren Anflugbogen ueber
			// Tangenten an zwei Wendekreise und traegt dort selbst ein offenes
			// "TODO: correctly handle CCW <-> CW turns". Steht das Flugzeug nach dem Ueberflug
			// JENSEITS des Ziels, liefert diese Mathematik einen Bogen, der die gewuenschte
			// Ausrichtung nicht mehr einholt -> der Flieger setzt schraeg auf. Deshalb hier eine
			// klassische Platzrunde aus dem tatsaechlichen WENDERADIUS, und die Landung bekommt
			// per skipApproach nur noch den fertig ausgerichteten Endanflug.
			var info = producer.Info.TraitInfo<ProductionBulkAirdropInfo>();
			var offset = info.LandOffset;
			var aircraft = self.Trait<Aircraft>();
			var cruiseZ = aircraft.Info.CruiseAltitude.Length;
			var landPos = producer.CenterPosition + offset;
			var landCell = self.World.Map.CellContaining(landPos);

			// Landerichtung aus den ECHTEN Positionen: der Flieger spawnt an der gegenueberliegenden
			// Kartenkante (ProductionBulkAirdrop), (Producer - Spawn) zeigt also exakt nach West.
			// Kein geratener WAngle -- FacingBetween liefert denselben Wert in der Render-Konvention.
			var toProducer = producer.CenterPosition - self.CenterPosition;
			var horiz = new WVec(toProducer.X, toProducer.Y, 0);
			if (horiz.HorizontalLengthSquared == 0)
				horiz = new WVec(-1024, 0, 0);

			var dir = 1024 * horiz / horiz.Length;      // Einheitsvektor (1024) in Landerichtung
			var side = new WVec(-dir.Y, dir.X, 0);      // 90 Grad dazu = Versatz der Gegenrichtung
			var landFacing = self.World.Map.FacingBetween(self.Location, landCell, aircraft.Facing);

			// Wenderadius wie die Engine ihn selbst rechnet (Fly.CalculateTurnRadius):
			// Umfang = Geschwindigkeit x Ticks pro Umdrehung, daraus der Radius.
			var turnRadius = Fly.CalculateTurnRadius(aircraft.MovementSpeed, aircraft.TurnSpeed);

			// Endanflug-Laenge: mindestens die Strecke, die der Sinkflug bei MaximumPitch braucht
			// (dieselbe Formel wie in Land.cs), sonst kaeme das Flugzeug zu hoch ueber der Schwelle an.
			var descentRun = cruiseZ * 1024 / aircraft.Info.MaximumPitch.Tan();
			var finalRun = Math.Max(descentRun, info.OverflyDistance.Length);

			// Die beiden Kehren versetzen die Bahn um je 2 x Wenderadius zur Seite -- das ist der
			// Abstand, den die Gegenrichtung (Downwind) von der Landeachse haben MUSS, damit die
			// zweite Kehre wieder exakt auf der Achse endet statt sie zu schneiden.
			var lateral = side * (2 * turnRadius) / 1024;
			var up = new WVec(0, 0, cruiseZ);

			// 1) Ueberflug: geradeaus ueber das Airfield hinaus.
			var overfly = landPos + dir * info.OverflyDistance.Length / 1024;

			// 2) Erste 180-Grad-Kehre -> Gegenrichtung, seitlich versetzt.
			var downwindStart = overfly + lateral;

			// 3) Gegenrichtung zurueck, bis hinter den Anflugpunkt.
			var downwindEnd = landPos - dir * finalRun / 1024 + lateral;

			// 4) Zweite 180-Grad-Kehre -> auf der Landeachse, ausgerichtet nach West.
			var finalFix = landPos - dir * finalRun / 1024;

			QueueChild(new Fly(self, Target.FromPos(overfly + up)));
			QueueChild(new Fly(self, Target.FromPos(downwindStart + up)));
			QueueChild(new Fly(self, Target.FromPos(downwindEnd + up)));
			QueueChild(new Fly(self, Target.FromPos(finalFix + up)));

			// skipApproach: die Platzrunde oben IST der Anflug. Land soll nur noch sinken.
			QueueChild(new Land(self, Target.FromActor(producer), WDist.FromCells(0), offset, landFacing, skipApproach: true));

			if (cargo.Info.BeforeUnloadDelay > 0)
				QueueChild(new Wait(cargo.Info.BeforeUnloadDelay));
		}

		protected override void OnLastRun(Actor self)
		{
			if (!producer.IsDead || producer.IsInWorld)
				foreach (var cargo in producer.TraitsImplementing<INotifyDelivery>())
					cargo.Delivered(producer);

			// "Reinforcements arrived" bei abgeschlossener Lieferung (User-Wunsch 2026-08-05).
			// ProductionBulkAirdropInfo.ReadyNotification existierte, wurde aber nirgends gespielt.
			var info = producer.Info.TraitInfo<ProductionBulkAirdropInfo>();
			if (!string.IsNullOrEmpty(info.ReadyNotification))
				Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech",
					info.ReadyNotification, self.Owner.Faction.InternalName);
			if (!string.IsNullOrEmpty(info.ReadyTextNotification))
				TextNotificationsManager.AddTransientLine(self.Owner, info.ReadyTextNotification);

			if (cargo.Info.AfterUnloadDelay > 0)
				Queue(new Wait(cargo.Info.AfterUnloadDelay));
			Queue(new FlyOffMap(self, Target.FromCell(self.World, self.World.Map.ChooseClosestEdgeCell(self.Location))));
			Queue(new RemoveSelf());
		}

		protected override void OnActorDispose(Actor self)
		{
			queue.DeliverFinished();
		}

		public override bool Tick(Actor self)
		{
			if (!producer.IsInWorld || producer.IsDead)
			{
				// Try to find another ProductionBulkAirDrop
				var newProducer = self.World.ActorsHavingTrait<ProductionBulkAirdrop>()
					.Where(a => a.Owner == self.Owner)
					.ClosestToIgnoringPath(self);
				if (newProducer != null)
				{
					Cancel(self);
					Queue(new DeliverBulkOrder(self, newProducer, orderedActors, productionType, queue));
					return true;
				}
				else
				{
					queue.DeliverFinished();
					return true;
				}
			}

			if (orderedActors == null || orderedActors.Count == 0)
			{
				queue.DeliverFinished();
				return true;
			}

			var actor = orderedActors[^1];
			var productionTrait = producer.Trait<ProductionBulkAirdrop>();
			var exit = productionTrait.PublicExit(producer, actor.Actor, productionType);
			if (exit == null)
				return false;

			if (delayBetweenUnloads > 0)
			{
				delayBetweenUnloads--;
				return false;
			}

			delayBetweenUnloads = cargo.Info.BetweenUnloadDelay;
			producer.World.AddFrameEndTask(ww =>
			{
				var inits = new TypeDictionary
				{
					new OwnerInit(self.Owner),
					new FactionInit(BuildableInfo.GetInitialFaction(actor.Actor, producer.Trait<ProductionBulkAirdrop>().Faction))
				};
				productionTrait.DoProduction(producer, actor.Actor, exit?.Info, productionType, inits);
				orderedActors.Remove(actor);
			});
			return false;
		}
	}
}
