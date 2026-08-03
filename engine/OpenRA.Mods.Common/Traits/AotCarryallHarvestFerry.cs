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

// === Age of Tiberium (aotmod) - "Carryall Harvester Transport" (User-Wunsch 2026-08-01) ===
//
// Dune-2000-Verhalten: ein Carryall traegt Harvester zwischen Tiberiumfeld und Refinery, der
// Harvester wartet dabei auf der Stelle statt selbst zu fahren.
//
// Die eigentliche Transportschleife steckt bereits vollstaendig in OpenRA (Mods.Common, NICHT
// D2k-exklusiv): AutoCarryall + AutoCarryable + CarryableHarvester. CarryableHarvester meldet
// Transportbedarf in BEIDE Richtungen an -- beim Losfahren zum Feld und beim Andocken an die
// Refinery -- und AutoCarryall.AutoCarryCondition ist ein fertiges Boolean-Gate dafuer.
//
// Dieser Trait liefert nur, was dort fehlt:
//   1. Aktivierung: "Enter"-Cursor auf einer Refinery schaltet die Routine scharf (grantet die
//      Condition, die AutoCarryCondition abfragt). Die Refinery ist dabei bewusst NUR die Geste
//      -- der Carryall wird ihr NICHT fest zugeordnet und bedient danach alle Harvester.
//   2. Abbruch: jeder andere Spielerbefehl schaltet die Routine wieder ab.
//   3. Kreisen: im Leerlauf kehrt der Carryall zum letzten ABSETZORT zurueck und wartet dort.
//      Hat er an der Refinery abgesetzt, kreist er dort; hat er am Tiberiumfeld abgesetzt,
//      kreist er dort. Aircraft.IdleBehavior kennt kein "circle at point", deshalb hier.
// === Ende aotmod ===

using System.Collections.Generic;
using System.Collections.Frozen;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Orders;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Lets a carryall be switched into automatic harvester-ferrying mode by targeting a refinery.")]
	public class AotCarryallHarvestFerryInfo : ConditionalTraitInfo, Requires<CarryallInfo>, Requires<AircraftInfo>
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Refinery actor types that can be targeted to switch the ferry routine on.")]
		public readonly FrozenSet<string> RefineryActors = FrozenSet<string>.Empty;

		[GrantedConditionReference]
		[FieldLoader.Require]
		[Desc("Condition granted while the ferry routine is armed. Point AutoCarryall.AutoCarryCondition at this.")]
		public readonly string ArmedCondition = null;

		[CursorReference]
		[Desc("Cursor shown over a valid refinery.")]
		public readonly string EnterCursor = "enter";

		[CursorReference]
		[Desc("Cursor shown over a refinery that cannot be used.")]
		public readonly string EnterBlockedCursor = "enter-blocked";

		[Desc("Radius of the circle the carryall flies around its last drop-off point while idle.")]
		public readonly WDist LoiterRadius = new(4608);

		[Desc("How far around the circle to advance per leg, out of 1024 for a full turn.",
			"Smaller values give a rounder circle at the cost of more activity churn.")]
		public readonly int LoiterStep = 64;

		[VoiceReference]
		public readonly string Voice = "Action";

		public override object Create(ActorInitializer init) { return new AotCarryallHarvestFerry(init.Self, this); }
	}

	public class AotCarryallHarvestFerry : ConditionalTrait<AotCarryallHarvestFerryInfo>, IIssueOrder, IResolveOrder, IOrderVoice, ITick
	{
		const string OrderID = "AotHarvestFerry";

		readonly Carryall carryall;
		int armedToken = Actor.InvalidConditionToken;

		// Letzter Absetzort. Wird gesetzt, sobald der Carryall aufhoert zu tragen -- an genau der
		// Stelle, an der er die Fracht losgeworden ist.
		WPos? lastDropOff;
		bool wasCarrying;
		WAngle loiterAngle = WAngle.Zero;

		public AotCarryallHarvestFerry(Actor self, AotCarryallHarvestFerryInfo info)
			: base(info)
		{
			carryall = self.Trait<Carryall>();
		}

		bool IsArmed => armedToken != Actor.InvalidConditionToken;

		bool CanTargetRefinery(Actor target, TargetModifiers modifiers)
		{
			return Info.RefineryActors.Contains(target.Info.Name);
		}

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				if (IsTraitDisabled)
					yield break;

				yield return new EnterAlliedActorTargeter<BuildingInfo>(
					OrderID, 6, Info.EnterCursor, Info.EnterBlockedCursor,
					CanTargetRefinery, _ => true);
			}
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			return order.OrderID == OrderID ? new Order(order.OrderID, self, target, queued) : null;
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order)
		{
			return order.OrderString == OrderID ? Info.Voice : null;
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (IsTraitDisabled)
				return;

			if (order.OrderString == OrderID)
			{
				if (order.Target.Type != TargetType.Actor || !Info.RefineryActors.Contains(order.Target.Actor.Info.Name))
					return;

				// Die Refinery ist nur der Ausloeser: ab hier bedient der Carryall alle Harvester.
				// Als erster Kreis-Ankerpunkt dient sie trotzdem, damit er nicht dort stehenbleibt,
				// wo der Spieler gerade zufaellig geklickt hat.
				lastDropOff = order.Target.Actor.CenterPosition;
				Arm(self);
				return;
			}

			// Jeder andere Spielerbefehl beendet die Routine. Die von AutoCarryall selbst
			// eingereihten Aktivitaeten (FerryUnit) laufen NICHT ueber Orders und sind hier
			// deshalb nicht betroffen.
			Disarm(self);
		}

		void Arm(Actor self)
		{
			if (armedToken == Actor.InvalidConditionToken)
				armedToken = self.GrantCondition(Info.ArmedCondition);
		}

		void Disarm(Actor self)
		{
			if (armedToken != Actor.InvalidConditionToken)
				armedToken = self.RevokeCondition(armedToken);
		}

		protected override void TraitDisabled(Actor self) { Disarm(self); }

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled)
				return;

			// Absetzort mitschreiben: der Uebergang "trug etwas" -> "traegt nichts mehr" passiert
			// genau dort, wo abgesetzt wurde. Carryall bietet dafuer keinen eigenen Notify an.
			var carrying = carryall.State == Carryall.CarryallState.Carrying;
			if (wasCarrying && !carrying)
				lastDropOff = self.CenterPosition;

			wasCarrying = carrying;

			if (!IsArmed || lastDropOff == null || !self.IsInWorld)
				return;

			// Nur eingreifen, wenn wirklich nichts zu tun ist -- sonst wuerde das Kreisen eine
			// laufende Faehrt (FerryUnit) unterbrechen.
			// WICHTIG: ein Flugzeug hat im Leerlauf NIE CurrentActivity == null, es laeuft dann
			// FlyIdle (schweben/kreisen). Eine reine Null-Pruefung traf deshalb nie zu und der
			// Carryall blieb einfach stehen. AutoCarryall.Busy() macht genau dieselbe Ausnahme.
			var current = self.CurrentActivity;
			if (current != null && current is not FlyIdle)
				return;

			// Kreisen um den letzten Absetzort (User-Wunsch 2026-08-01: NUR im Faehr-Auftrag, und
			// mit deutlich groesserem Radius). Aircraft.IdleSpeed kann das nicht leisten: es ist
			// ein statisches Feld, laesst sich also nicht per Condition ein-/ausschalten, wuerde
			// jeden TRAN betreffen (auch den einfachen Transporthelikopter) und sein Radius ergibt
			// sich starr aus IdleSpeed/TurnSpeed. Deshalb hier als Folge von Anflugpunkten:
			// pro Bein ein Stueck weiter auf dem Kreis, das ergibt eine fortlaufende Kreisbahn.
			loiterAngle = new WAngle((loiterAngle.Angle + Info.LoiterStep) % 1024);
			var offset = new WVec(0, -Info.LoiterRadius.Length, 0).Rotate(WRot.FromYaw(loiterAngle));

			// Kreispunkt in die Karte klemmen (User-Fund 2026-08-01: "am Bildrand unterbricht er
			// und faellt aus der Zuweisung"). Liegt der Absetzort nah am Rand, zeigt ein Teil der
			// Kreisbahn nach draussen -- ein Fly dorthin kommt nie an, und das Aircraft geraet in
			// seine Kartenrand-Behandlung. Clamp haelt die Bahn immer im gueltigen Bereich; der
			// Kreis wird am Rand dadurch flachgedrueckt statt abzubrechen.
			var map = self.World.Map;
			var target = map.CenterOfCell(map.Clamp(map.CellContaining(lastDropOff.Value + offset)));

			// queued: false -- das laufende FlyIdle muss weichen, sonst haenge ich den Flug nur
			// hinter ein Activity, das von sich aus nie endet.
			self.QueueActivity(false, new Fly(self, Target.FromPos(target)));
		}
	}
}
