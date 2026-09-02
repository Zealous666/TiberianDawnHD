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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// aotmod (2026-07-29): Vom Spawner an den Critter weitergereicht (Editor-Checkbox "Attack Wave").
	public class AotCritterAttackWaveInit : ValueActorInit<bool>, ISingleInstanceInit
	{
		public AotCritterAttackWaveInit(TraitInfo info, bool value) : base(info, value) { }

		public AotCritterAttackWaveInit(bool value) : base(value) { }
	}

	[Desc("aotmod: Angriffswelle statt Patrouille. Der Critter sucht sich nach dem Spawn ein",
		"zufaelliges Feindziel -- bevorzugt ein Gebaeude (also eine Basis), sonst irgendeine",
		"feindliche Einheit -- und marschiert per AttackMove dorthin, greift also alles an, was",
		"ihm unterwegs begegnet. Ist das Ziel zerstoert oder erreicht, wird neu gewaehlt.",
		"Waehrend die Welle laeuft, wird Condition gewaehrt; die Wander-Traits sind darueber",
		"abgeschaltet (RequiresCondition: !aot-attack-wave), sonst wuerden beide im Idle",
		"konkurrierende Aktivitaeten einreihen.")]
	public class AotCritterAttackWaveInfo : ConditionalTraitInfo, Requires<IMoveInfo>, Requires<AttackMoveInfo>
	{
		[Desc("Angriffswelle auch ohne Spawner-Init aktiv (z.B. fuer direkt im Editor platzierte Critter).")]
		public readonly bool DefaultAttackWave = false;

		[GrantedConditionReference]
		[Desc("Condition, solange die Angriffswelle laeuft. Schaltet die Patrouille ab.")]
		public readonly string Condition = "aot-attack-wave";

		[Desc("Mindest-Wartezeit in Ticks, bevor im Idle ein neues Ziel gesucht wird.")]
		public readonly int MinRetargetDelay = 25;

		[Desc("Maximale Wartezeit in Ticks, bevor im Idle ein neues Ziel gesucht wird.",
			"Greift vor allem, wenn gerade gar kein Ziel existiert -- dann nicht jeden Tick suchen.")]
		public readonly int MaxRetargetDelay = 75;

		[Desc("Umkreis (Zellen), in dem der Critter herumwandert, solange KEIN erreichbares Feindziel",
			"existiert. 0 = stehen bleiben (altes Verhalten).")]
		public readonly int IdleWanderRadius = 3;

		public override object Create(ActorInitializer init) { return new AotCritterAttackWave(init, this); }
	}

	public class AotCritterAttackWave : ConditionalTrait<AotCritterAttackWaveInfo>, INotifyIdle
	{
		readonly bool attackWave;
		readonly IMove move;
		readonly Mobile mobile;
		readonly AttackMoveInfo attackMoveInfo;
		readonly AttackBase attack;

		int conditionToken = Actor.InvalidConditionToken;
		int countdown;

		public AotCritterAttackWave(ActorInitializer init, AotCritterAttackWaveInfo info)
			: base(info)
		{
			attackWave = init.GetValue<AotCritterAttackWaveInit, bool>(info.DefaultAttackWave);
			move = init.Self.Trait<IMove>();
			mobile = init.Self.TraitOrDefault<Mobile>();
			attackMoveInfo = init.Self.Info.TraitInfo<AttackMoveInfo>();
			attack = init.Self.TraitOrDefault<AttackBase>();
		}

		protected override void Created(Actor self)
		{
			// Die Condition muss VOR dem ersten Idle-Tick stehen, sonst laeuft die Patrouille
			// einmal mit an, bevor sie abgeschaltet wird.
			if (attackWave && !IsTraitDisabled && !string.IsNullOrEmpty(Info.Condition))
				conditionToken = self.GrantCondition(Info.Condition);

			base.Created(self);
		}

		void INotifyIdle.TickIdle(Actor self)
		{
			if (!attackWave || IsTraitDisabled)
				return;

			if (--countdown > 0)
				return;

			countdown = self.World.SharedRandom.Next(Info.MinRetargetDelay, Info.MaxRetargetDelay);

			var target = PickTarget(self);

			// DIAGNOSE (2026-09-01): Zeigt pro Retarget-Tick das gewaehlte (erreichbare) Ziel. Nach Abnahme raus.
			OpenRA.Log.Write("debug", $"[AotCritterAttackWave] #{self.ActorID} {self.Info.Name} @{self.Location}: " +
				$"target={(target != null ? $"{target.Info.Name} #{target.ActorID} @{target.Location} " +
					$"dist={(target.CenterPosition - self.CenterPosition).HorizontalLength / 1024}c" : "NONE")}");

			if (target == null)
			{
				// Kein erreichbares Feindziel -> nicht stur stehen bleiben, sondern in einem engen
				// Umkreis herumwandern (Requirement: Dinos sollen im Leerlauf umherstreifen). Die
				// Wander-Traits sind waehrend der Angriffswelle per Condition abgeschaltet, darum
				// macht die Angriffswelle das Wandern hier selbst.
				Wander(self);
				return;
			}

			// EXPLIZITER Angriff auf den Ziel-Aktor (allowMove + forceAttack), statt auf AttackMove +
			// AutoTarget-Idle-Scan zu hoffen. Genau daran hing es: der Critter lief bis zur Anlaufzelle,
			// stand ~1/2 Zelle ausserhalb der Bisswaffen-Reichweite und der Idle-Scan/AttackMove schloss
			// die Restdistanz nie -> er stand still neben dem Gebaeude (z.B. Hand of Nod), ohne je zu
			// beissen. AttackTarget(allowMove) laeuft die Restdistanz heran und beisst; forceAttack
			// ueberbrueckt jede Stance/Prioritaets-Bedingung. Waehrend der Angriff laeuft ist der Critter
			// nicht idle -> kein erneutes Picken, bis das Ziel faellt (dann greift der naechste Tick).
			if (attack != null)
				attack.AttackTarget(Target.FromActor(target), AttackSource.Default,
					false, true, true, attackMoveInfo.TargetLineColor);
			else
				self.QueueActivity(new AttackMoveActivity(self,
					() => move.MoveTo(target.Location, 1, targetLineColor: attackMoveInfo.TargetLineColor)));
		}

		// Liefert den naechsten WIRKLICH erreichbaren Feind-Aktor (Gebaeude ODER Einheit) -- oder null,
		// wenn der Critter (z.B. eingezaeunt) an keinen herankommt; dann wandert er im Leerlauf.
		//
		// Frueher wurde stur das naechste Gebaeude gewaehlt und mit einer OPTIMISTISCHEN Pfad-Naeherung
		// (PathMightExist..., darf laut Vertrag false-positive sein) auf Erreichbarkeit geprueft. Zwei
		// Fehler ergaben zusammen das beobachtete Verhalten:
		//   1) Der Zaun (Aktoren) rutschte durch die Naeherung -> ein Ferngebaeude galt als erreichbar,
		//      wurde gewaehlt, der echte Marsch scheiterte am Zaun -> Zappeln im Gehege.
		//   2) Gebaeude schlugen Einheiten IMMER -> selbst wenn der Feind naeher auf die eigene Insel
		//      baute (erreichbar!), gewann das unerreichbare Ferngebaeude.
		//
		// Jetzt: EINE echte, aktor-bewusste Pfadsuche vom Critter nach aussen zur naechsten Anlaufzelle
		// neben irgendeinem Feindziel. Der Aktor an dieser Zelle ist per Konstruktion das naechste
		// erreichbare Ziel -- kein Fehlalarm, kein Gebaeude-Vorrang.
		Actor PickTarget(Actor self)
		{
			var world = self.World;

			bool Hostile(Actor a) =>
				a != self && a.IsInWorld && !a.IsDead &&
				self.Owner.RelationshipWith(a.Owner) == PlayerRelationship.Enemy;

			// Ohne Mobile kein Pfadsystem -- dann grob der naechste Feind (Verhalten wie ganz frueher).
			if (mobile == null)
				return world.ActorsHavingTrait<Health>().Where(Hostile)
					.OrderBy(a => (a.CenterPosition - self.CenterPosition).HorizontalLengthSquared)
					.FirstOrDefault();

			// Anlaufzellen (Zelle + Nachbarn) jedes Feindziels, jeweils auf ihren Aktor abgebildet.
			var approach = new Dictionary<CPos, Actor>();
			var hostiles = 0;
			foreach (var a in world.ActorsHavingTrait<Health>().Where(Hostile))
			{
				hostiles++;
				approach.TryAdd(a.Location, a);
				foreach (var d in CVec.Directions)
					approach.TryAdd(a.Location + d, a);
			}

			if (approach.Count == 0)
				return null;

			// EINE echte Pfadsuche (BlockedByImmovable: Waende/Gebaeude zaehlen, bewegliche Einheiten
			// nicht -- denen weicht der Marsch ohnehin aus). Sucht vom Critter nach aussen bis zur
			// naechsten Anlaufzelle und respektiert den Zaun. Rueckgabe ist target->source umgedreht,
			// also ist path[0] die gefundene erreichbare Anlaufzelle. Leer = nichts erreichbar.
			var path = mobile.PathFinder.FindPathToTargetCellByPredicate(
				self, [self.Location], approach.ContainsKey, BlockedByActor.Immovable);

			var target = path.Count > 0 ? approach[path[0]] : null;

			// DIAGNOSE (2026-09-01): wie viele Feindziele existieren und ob eines erreichbar ist.
			OpenRA.Log.Write("debug", $"[AotCritterAttackWave] #{self.ActorID} pick: hostiles={hostiles} " +
				$"approachCells={approach.Count} reachable={(target != null ? "YES" : "NONE")}");

			return target;
		}

		// Enges Umherstreifen im Leerlauf (kein erreichbares Ziel). Sucht eine zufaellige, wirklich
		// betretbare Zelle im Umkreis IdleWanderRadius und laeuft dorthin. Ohne Mobile (theoretisch)
		// oder bei Radius 0 passiert nichts -> der Critter bleibt stehen.
		void Wander(Actor self)
		{
			var r = Info.IdleWanderRadius;
			if (mobile == null || r <= 0)
				return;

			for (var tries = 0; tries < 8; tries++)
			{
				var dx = self.World.SharedRandom.Next(-r, r + 1);
				var dy = self.World.SharedRandom.Next(-r, r + 1);
				if (dx == 0 && dy == 0)
					continue;

				var cell = self.Location + new CVec(dx, dy);
				if (!self.World.Map.Contains(cell) || !mobile.CanEnterCell(cell))
					continue;

				self.QueueActivity(move.MoveTo(cell, 0, targetLineColor: attackMoveInfo.TargetLineColor));
				return;
			}
		}

		protected override void TraitDisabled(Actor self)
		{
			if (conditionToken != Actor.InvalidConditionToken)
				conditionToken = self.RevokeCondition(conditionToken);
		}
	}
}
