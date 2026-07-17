#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Transforms the actor into another actor when ALL listed prerequisites are fulfilled
 * AND the guard condition (RequiresCondition) is active.
 *
 * Used on TTNK base actor:
 *   AotTransformOnPrerequisite:
 *     IntoActor: aot-ttnk-devil-base
 *     Prerequisites: aot-subterrain-upgrade, aot-ttnk-flame-upgrade
 *     RequiresCondition: aot-ttnk-flame    <- only fire when it's a flame variant
 *
 * Both triggers (prerequisites met AND RequiresCondition active) must hold before
 * the transform queues. Only fires once per actor (actor becomes a new type and the
 * trait is gone).
 */
#endregion

using System.Collections.Generic;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Transforms this actor into another actor when all listed prerequisites become " +
		"available. Optionally gated by a condition (RequiresCondition).")]
	public class AotTransformOnPrerequisiteInfo : ConditionalTraitInfo, ITechTreePrerequisiteInfo
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("Actor to transform into when prerequisites are met.")]
		public readonly string IntoActor = null;

		[FieldLoader.Require]
		[Desc("All prerequisites that must be present to trigger the transform.")]
		public readonly string[] Prerequisites = [];

		IEnumerable<string> ITechTreePrerequisiteInfo.Prerequisites(ActorInfo info) => Prerequisites;

		public override object Create(ActorInitializer init) { return new AotTransformOnPrerequisite(init.Self, this); }
	}

	public class AotTransformOnPrerequisite : ConditionalTrait<AotTransformOnPrerequisiteInfo>,
		INotifyOwnerChanged, ITechTreeElement
	{
		readonly Actor self;
		TechTree techTree;
		bool prerequisitesMet;
		bool transformed;

		public AotTransformOnPrerequisite(Actor self, AotTransformOnPrerequisiteInfo info)
			: base(info)
		{
			this.self = self;
		}

		protected override void Created(Actor self)
		{
			base.Created(self);
			techTree = self.Owner.PlayerActor.Trait<TechTree>();
			techTree.Add(this, Info.Prerequisites, 0, false);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			techTree.Remove(this);
			techTree = newOwner.PlayerActor.Trait<TechTree>();
			techTree.Add(this, Info.Prerequisites, 0, false);
		}

		void TryTransform()
		{
			if (transformed || !self.IsInWorld || self.IsDead)
				return;

			if (IsTraitDisabled || !prerequisitesMet)
				return;

			transformed = true;
			self.QueueActivity(false, new Transform(Info.IntoActor) { SkipMakeAnims = true });
		}

		protected override void TraitEnabled(Actor self) { TryTransform(); }

		void ITechTreeElement.PrerequisitesAvailable(string key)
		{
			prerequisitesMet = true;
			TryTransform();
		}

		void ITechTreeElement.PrerequisitesUnavailable(string key) { prerequisitesMet = false; }
		void ITechTreeElement.PrerequisitesItemHidden(string key) { }
	}
}
