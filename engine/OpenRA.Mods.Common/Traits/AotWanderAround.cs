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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// aotmod (2026-08-21): Editor-Checkbox pro Critter. Wird ausserdem vom AotCritterSpawner
	// beim Spawn per Value-Ctor mitgegeben (nachgespawnte Critter sollen patrouillieren).
	public class AotWanderAroundInit : ValueActorInit<bool>, ISingleInstanceInit
	{
		public AotWanderAroundInit(TraitInfo info, bool value) : base(info, value) { }

		public AotWanderAroundInit(bool value) : base(value) { }
	}

	[Desc("aotmod: Editor-Checkbox \"Wander Around\" fuer Critter (Ameisen, Visceroid, Saurier).",
		"Das native Verhalten eines einzeln im Editor platzierten Critters ist STEHENBLEIBEN",
		"(Position halten, aber Feinde in Reichweite via AutoTarget angreifen). Erst wenn die",
		"Box gesetzt ist, wird eine Condition gewaehrt, die das Umherwandern",
		"(AotAntWander/AotVisceroidWander via RequiresCondition: aot-wander) einschaltet.",
		"Vom Spawner nachgespawnte Critter bekommen die Condition automatisch (Value-Init true),",
		"damit sie wie bisher patrouillieren.")]
	public class AotWanderAroundInfo : ConditionalTraitInfo, IEditorActorOptions
	{
		[Desc("Standardwert der Checkbox. false = Critter bleibt stehen (natives Verhalten).")]
		public readonly bool Default = false;

		[GrantedConditionReference]
		[Desc("Condition, solange \"Wander Around\" aktiv ist. Schaltet die Wander-Traits ein.")]
		public readonly string Condition = "aot-wander";

		[Desc("Reihenfolge im Editor-Panel (nach den Spawner-Optionen).")]
		public readonly int EditorDisplayOrder = 4;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			yield return new EditorActorCheckbox("Wander Around", EditorDisplayOrder,
				actor => actor.GetInitOrDefault<AotWanderAroundInit>(this)?.Value ?? Default,
				(actor, value) => actor.ReplaceInit(new AotWanderAroundInit(this, value), this));
		}

		public override object Create(ActorInitializer init) { return new AotWanderAround(init, this); }
	}

	public class AotWanderAround : ConditionalTrait<AotWanderAroundInfo>
	{
		readonly bool wander;
		int conditionToken = Actor.InvalidConditionToken;

		public AotWanderAround(ActorInitializer init, AotWanderAroundInfo info)
			: base(info)
		{
			wander = init.GetValue<AotWanderAroundInit, bool>(info.Default);
		}

		protected override void Created(Actor self)
		{
			// Muss VOR dem ersten Idle-Tick stehen, sonst wird ein Tick lang nicht gewandert
			// (bzw. bei Spawner-Crittern faellt der erste Patrouillengang aus).
			if (wander && !IsTraitDisabled && !string.IsNullOrEmpty(Info.Condition))
				conditionToken = self.GrantCondition(Info.Condition);

			base.Created(self);
		}

		protected override void TraitDisabled(Actor self)
		{
			if (conditionToken != Actor.InvalidConditionToken)
				conditionToken = self.RevokeCondition(conditionToken);
		}
	}
}
