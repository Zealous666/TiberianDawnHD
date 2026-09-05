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
			if (target == null)
			{
				// Kein erreichbares Feindziel -> nicht stur stehen bleiben, sondern im engen Umkreis
				// herumwandern (Requirement: Dinos streifen im Leerlauf umher). Die Wander-Traits sind
				// waehrend der Angriffswelle per Condition abgeschaltet, darum wandert die Welle selbst.
				Wander(self);
				return;
			}

			// ZELLBASIERTER AttackMove zur erreichbaren Anlaufzelle -- bewusst NICHT AttackTarget auf den
			// Aktor: der Aktor kann fuer den Creeps-Spieler im Nebel verborgen sein, und dann bricht die
			// Attack-Aktivitaet sofort ab, ohne loszulaufen. Ein Move auf eine ZELLE ist nebel-immun. Er
			// greift zudem unterwegs alles Gueltige an (Angriffswelle). Am Ziel angekommen (Sicht 6c ->
			// Gebaeude sichtbar), uebernimmt AutoTarget das Zubeissen.
			self.QueueActivity(new AttackMoveActivity(self,
				() => move.MoveTo(target.Value, 0, targetLineColor: attackMoveInfo.TargetLineColor)));
		}

		// Liefert die naechste WIRKLICH erreichbare Anlaufzelle neben einem Feindziel (Gebaeude ODER
		// Einheit) -- oder null, wenn der Critter (z.B. eingezaeunt) an keines herankommt.
		//
		// EINE echte, aktor-bewusste Pfadsuche vom Critter nach aussen. Kein optimistischer Fehlalarm
		// (der Zaun wird respektiert) und kein Gebaeude-Vorrang -- es gewinnt schlicht das naechste
		// Erreichbare, egal ob Gebaeude oder Einheit.
		CPos? PickTarget(Actor self)
		{
			var world = self.World;

			bool Hostile(Actor a) =>
				a != self && a.IsInWorld && !a.IsDead &&
				self.Owner.RelationshipWith(a.Owner) == PlayerRelationship.Enemy;

			// Ohne Mobile kein Pfadsystem -- dann grob das naechste Feindziel (Verhalten wie ganz frueher).
			if (mobile == null)
				return world.ActorsHavingTrait<Health>().Where(Hostile)
					.OrderBy(a => (a.CenterPosition - self.CenterPosition).HorizontalLengthSquared)
					.FirstOrDefault()?.Location;

			// Anlaufzellen: Zelle + Nachbarn jedes Feindziels.
			var approach = new HashSet<CPos>();
			foreach (var a in world.ActorsHavingTrait<Health>().Where(Hostile))
			{
				approach.Add(a.Location);
				foreach (var d in CVec.Directions)
					approach.Add(a.Location + d);
			}

			if (approach.Count == 0)
				return null;

			// BlockedByImmovable: Waende/Gebaeude zaehlen, bewegliche Einheiten nicht. Rueckgabe ist
			// target->source umgedreht, also ist path[0] die erreichbare Anlaufzelle. Leer = nichts erreichbar.
			var path = mobile.PathFinder.FindPathToTargetCellByPredicate(
				self, [self.Location], approach.Contains, BlockedByActor.Immovable);

			return path.Count > 0 ? (CPos?)path[0] : null;
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
