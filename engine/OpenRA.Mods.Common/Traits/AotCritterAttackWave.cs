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

		public override object Create(ActorInitializer init) { return new AotCritterAttackWave(init, this); }
	}

	public class AotCritterAttackWave : ConditionalTrait<AotCritterAttackWaveInfo>, INotifyIdle
	{
		readonly bool attackWave;
		readonly IMove move;
		readonly AttackMoveInfo attackMoveInfo;

		int conditionToken = Actor.InvalidConditionToken;
		int countdown;

		public AotCritterAttackWave(ActorInitializer init, AotCritterAttackWaveInfo info)
			: base(info)
		{
			attackWave = init.GetValue<AotCritterAttackWaveInit, bool>(info.DefaultAttackWave);
			move = init.Self.Trait<IMove>();
			attackMoveInfo = init.Self.Info.TraitInfo<AttackMoveInfo>();
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

			var target = PickTargetCell(self);
			if (target == null)
				return;

			// AttackMoveActivity statt Move.MoveTo: der Critter greift unterwegs alles an, statt
			// stur bis zum Ziel durchzulaufen -- genau das Verhalten einer Angriffswelle.
			self.QueueActivity(new AttackMoveActivity(self,
				() => move.MoveTo(target.Value, 2, targetLineColor: attackMoveInfo.TargetLineColor)));
		}

		CPos? PickTargetCell(Actor self)
		{
			var world = self.World;

			bool Hostile(Actor a) =>
				a != self && a.IsInWorld && !a.IsDead &&
				self.Owner.RelationshipWith(a.Owner) == PlayerRelationship.Enemy;

			// Bevorzugt eine Basis: Gebaeude sind das lohnendere und stabilere Ziel, und ein
			// Marsch dorthin fuehrt den Critter zwangslaeufig durch die feindlichen Einheiten.
			var buildings = world.ActorsHavingTrait<Building>().Where(Hostile).ToList();
			if (buildings.Count > 0)
				return buildings[world.SharedRandom.Next(buildings.Count)].Location;

			// Keine Basis (mehr) -- dann irgendein zerstoerbares Feindziel.
			var units = world.ActorsHavingTrait<Health>().Where(Hostile).ToList();
			if (units.Count > 0)
				return units[world.SharedRandom.Next(units.Count)].Location;

			return null;
		}

		protected override void TraitDisabled(Actor self)
		{
			if (conditionToken != Actor.InvalidConditionToken)
				conditionToken = self.RevokeCondition(conditionToken);
		}
	}
}
