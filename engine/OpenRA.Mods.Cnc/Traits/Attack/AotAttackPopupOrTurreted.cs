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
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	// Age of Tiberium (aotmod): einzelner Attack-Trait, der je nach Condition
	// zwischen zwei Verhaltensweisen umschaltet - Pop-up-Silo (wie
	// AttackPopupTurreted) oder normaler, staendig einsatzbereiter Turm (wie
	// AttackTurreted). Grund: OpenRA erlaubt pro Aktor nur EINE Instanz eines
	// von AttackBase abgeleiteten Traits (mehrere Engine-Stellen wie
	// RenderRangeCircle, Turreted.Created(), AttackFollow.AttackActivity nutzen
	// strikte Single()-Lookups ueber die abstrakte Basisklasse) - zwei separate
	// Traits (AttackTurreted + AttackPopupTurreted), auch nur conditional aktiv,
	// crashen daher zuverlaessig. Diese Klasse vereint beide Verhalten in EINER
	// Trait-Instanz, der Zustand (Bunkered ja/nein) wird per BooleanExpression
	// aus Conditions gelesen (IObservesVariables), nicht per RequiresCondition.
	[Desc("Like AttackPopupTurreted, but the popup behaviour is only active while BunkeredCondition is true.",
		"While false, behaves like a normal always-ready AttackTurreted.")]
	public class AotAttackPopupOrTurretedInfo : AttackTurretedInfo, Requires<BuildingInfo>, Requires<WithSpriteBodyInfo>
	{
		[Desc("Boolean expression defining the condition under which the popup (sink into ground) behaviour is active.")]
		public readonly BooleanExpression BunkeredCondition = null;

		[Desc("How many game ticks should pass before closing the actor's turret while bunkered.")]
		public readonly int CloseDelay = 125;

		public readonly WAngle DefaultFacing = WAngle.Zero;

		[Desc("The percentage of damage that is received while this actor is closed.")]
		public readonly int ClosedDamageMultiplier = 50;

		[SequenceReference]
		[Desc("Sequence to play when opening.")]
		public readonly string OpeningSequence = "opening";

		[SequenceReference]
		[Desc("Sequence to play when closing.")]
		public readonly string ClosingSequence = "closing";

		[SequenceReference]
		[Desc("Idle sequence to play when closed.")]
		public readonly string ClosedIdleSequence = "closed-idle";

		[Desc("Which sprite body to play the popup animation on (used only while bunkered).")]
		public readonly string Body = "body";

		public override object Create(ActorInitializer init) { return new AotAttackPopupOrTurreted(init, this); }
	}

	public class AotAttackPopupOrTurreted : AttackTurreted, INotifyIdle, IDamageModifier, IObservesVariables
	{
		enum PopupState { Open, Rotating, Transitioning, Closed }

		readonly AotAttackPopupOrTurretedInfo info;
		readonly WithSpriteBody wsb;
		readonly Turreted turret;

		bool bunkered;
		int idleTicks;
		PopupState state = PopupState.Open;

		public AotAttackPopupOrTurreted(ActorInitializer init, AotAttackPopupOrTurretedInfo info)
			: base(init.Self, info)
		{
			this.info = info;
			turret = turrets.FirstOrDefault();
			wsb = init.Self.TraitsImplementing<WithSpriteBody>().Single(w => w.Info.Name == info.Body);
		}

		// aotmod fix (User-Fund 2026-08-01: "SAMs/Bunkered SAMs schiessen auch wenn sie low power
		// sind -- korrekt abgedunkelt, feuern aber trotzdem"). Das war die Ursache, NICHT die YAML:
		// diese Methode war als EXPLIZITE Interface-Implementierung
		// (`IEnumerable<VariableObserver> IObservesVariables.GetVariableObservers()`) geschrieben
		// statt als `public override`. Die Engine holt die Observer ueber
		// TraitsImplementing<IObservesVariables>() -- also ueber das Interface -- und dort gewinnt
		// die explizite Implementierung und ERSETZT die der Basisklasse komplett. Damit wurden die
		// Observer fuer RequiresCondition UND PauseOnCondition nie registriert: IsTraitPaused und
		// IsTraitDisabled blieben dauerhaft false, egal welche Conditions anlagen. Die Abdunklung
		// kommt von ^DisabledOverlay (eigener Trait) und funktionierte deshalb weiter -- das Feuern
		// wurde nie pausiert. check-yaml konnte das nie finden: die YAML war immer korrekt
		// (PauseOnCondition: lowpower || build-incomplete, lowpower wird von ^DisabledOverlay
		// gegrantet), der Fehler lag ausschliesslich im C#.
		// PFLICHT: als `override` deklarieren und base mit ausgeben -- die Basisklasse warnt in
		// ConditionalTrait/PausableConditionalTrait genau davor.
		public override IEnumerable<VariableObserver> GetVariableObservers()
		{
			foreach (var observer in base.GetVariableObservers())
				yield return observer;

			if (info.BunkeredCondition != null)
				yield return new VariableObserver(BunkeredConditionChanged, info.BunkeredCondition.Variables);
		}

		void BunkeredConditionChanged(Actor self, IReadOnlyDictionary<string, int> conditions)
		{
			var wasBunkered = bunkered;
			bunkered = info.BunkeredCondition.Evaluate(conditions);

			if (AotSamDebug.Enabled && wasBunkered != bunkered)
				AotSamDebug.Trace($"tick={self.World.WorldTick} actor={self.Info.Name} owner={self.Owner.PlayerName} BunkeredConditionChanged: {wasBunkered} -> {bunkered}");

			// Leaving bunkered mode mid-close/transition: snap back to a plain open turret.
			if (wasBunkered && !bunkered && state != PopupState.Open)
			{
				state = PopupState.Open;
				wsb.PlayCustomAnimationRepeating(self, wsb.Info.Sequence);
				idleTicks = 0;
			}
		}

		// Traits/PausableConditionalTrait.cs: fires exactly when IsTraitPaused flips, driven by
		// PauseOnCondition (lowpower || build-incomplete on this actor). If the "SAM keeps firing
		// while dimmed" report is real, the crucial question is whether these fire at all during
		// the observed low-power window -- if they don't, "lowpower" never reached IsTraitPaused
		// here despite the visual dimming (which reads the same condition via ^DisabledOverlay,
		// but through a completely separate trait), pointing at a condition-plumbing gap instead
		// of a logic bug in CanAttack below.
		protected override void TraitPaused(Actor self)
		{
			if (AotSamDebug.Enabled)
				AotSamDebug.Trace($"tick={self.World.WorldTick} actor={self.Info.Name} owner={self.Owner.PlayerName} TraitPaused (bunkered={bunkered} state={state})");

			base.TraitPaused(self);
		}

		protected override void TraitResumed(Actor self)
		{
			if (AotSamDebug.Enabled)
				AotSamDebug.Trace($"tick={self.World.WorldTick} actor={self.Info.Name} owner={self.Owner.PlayerName} TraitResumed (bunkered={bunkered} state={state})");

			base.TraitResumed(self);
		}

		protected override bool CanAttack(Actor self, in Target target)
		{
			var result = CanAttackInner(self, target);

			if (AotSamDebug.Enabled)
				AotSamDebug.Trace($"tick={self.World.WorldTick} actor={self.Info.Name} owner={self.Owner.PlayerName} CanAttack -> {result} (bunkered={bunkered} state={state} IsTraitPaused={IsTraitPaused} IsTraitDisabled={IsTraitDisabled})");

			return result;
		}

		bool CanAttackInner(Actor self, in Target target)
		{
			if (!bunkered)
				return base.CanAttack(self, target);

			if (IsTraitPaused)
				return false;

			if (state == PopupState.Closed)
			{
				state = PopupState.Transitioning;
				wsb.PlayCustomAnimation(self, info.OpeningSequence, () =>
				{
					state = PopupState.Open;
					wsb.PlayCustomAnimationRepeating(self, wsb.Info.Sequence);
				});

				idleTicks = 0;
			}

			if (state == PopupState.Transitioning || !base.CanAttack(self, target))
				return false;

			idleTicks = 0;
			return true;
		}

		void INotifyIdle.TickIdle(Actor self)
		{
			if (!bunkered || IsTraitDisabled || IsTraitPaused)
				return;

			if (state == PopupState.Open && idleTicks++ > info.CloseDelay)
			{
				var facingOffset = new WVec(0, -1024, 0).Rotate(WRot.FromYaw(info.DefaultFacing));
				turret.FaceTarget(self, Target.FromPos(self.CenterPosition + facingOffset));
				state = PopupState.Rotating;
			}
			else if (state == PopupState.Rotating && turret.HasAchievedDesiredFacing)
			{
				state = PopupState.Transitioning;
				wsb.PlayCustomAnimation(self, info.ClosingSequence, () =>
				{
					state = PopupState.Closed;
					wsb.PlayCustomAnimationRepeating(self, info.ClosedIdleSequence);
					turret.FaceTarget(self, Target.Invalid);
				});
			}
		}

		int IDamageModifier.GetDamageModifier(Actor attacker, Damage damage)
		{
			return bunkered && state == PopupState.Closed ? info.ClosedDamageMultiplier : 100;
		}
	}
}
