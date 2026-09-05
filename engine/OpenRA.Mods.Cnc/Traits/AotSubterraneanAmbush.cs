// === Age of Tiberium (aotmod) ===
// Lets a subterranean unit (Devil's Tongue) be pre-placed in the map editor already buried and
// stay buried, lying in wait like a Stealth Tank, until an enemy walks into weapon range.
//
// Why an engine trait and not YAML: two things are impossible in rules alone.
//   1. Starting submerged. The subterranean layer is only ever entered by the Move activity as
//      part of pathfinding; there is no "spawn on layer N" actor init.
//   2. Staying submerged. Mobile.OnBecomingIdle unconditionally queues a move back to the ground
//      layer whenever a unit on a custom layer goes idle (ICustomMovementLayer
//      .ReturnToGroundLayerOnIdle is true for SubterraneanActorLayer). That is vetoed per-actor
//      via IPreventsIdleLayerReturn while the ambush is armed.
// === Ende aotmod ===

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Allows a subterranean unit to be placed underground in the map editor and to stay",
		"buried until an enemy comes into weapon range, then surface and engage.")]
	public class AotSubterraneanAmbushInfo : ConditionalTraitInfo, IEditorActorOptions, Requires<MobileInfo>
	{
		[Desc("CHECKBOX \"Underground\": start buried. Map editor only -- units built from a",
			"production queue always start on the surface.")]
		public readonly bool Underground = false;

		[Desc("Display order for the \"Underground\" checkbox in the map editor.")]
		public readonly int EditorUndergroundDisplayOrder = 4;

		[GrantedConditionReference]
		[Desc("Condition granted while lying in ambush (buried and waiting for a target).")]
		public readonly string AmbushCondition = null;

		[Desc("Ticks between target scans while buried. The unit is doing nothing else meanwhile,",
			"so this can be fairly coarse.")]
		public readonly int ScanInterval = 11;

		[Desc("Surface when a hostile actor comes this close. Zero means: use the actor's own",
			"maximum weapon range, i.e. dig out exactly when it could start shooting.")]
		public readonly WDist ScanRange = WDist.Zero;

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			yield return new EditorActorCheckbox("Underground", EditorUndergroundDisplayOrder,
				actor => actor.GetInitOrDefault<AotUndergroundInit>()?.Value ?? Underground,
				(actor, value) => actor.ReplaceInit(new AotUndergroundInit(value)));
		}

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			// Same guard as GrantConditionOnSubterraneanLayer: without a subterranean locomotor there
			// is no layer to bury into, and the failure would otherwise be silent at runtime.
			var mobileInfo = ai.TraitInfoOrDefault<MobileInfo>();
			if (mobileInfo == null || mobileInfo.LocomotorInfo is not SubterraneanLocomotorInfo)
				throw new YamlException($"{nameof(AotSubterraneanAmbush)} requires Mobile to be linked to a SubterraneanLocomotor!");
		}

		public override object Create(ActorInitializer init) { return new AotSubterraneanAmbush(init, this); }
	}

	public class AotSubterraneanAmbush : ConditionalTrait<AotSubterraneanAmbushInfo>,
		INotifyAddedToWorld, ITick, IPreventsIdleLayerReturn
	{
		readonly Mobile mobile;
		readonly bool startUnderground;

		AttackBase[] attackBases;
		int conditionToken = Actor.InvalidConditionToken;
		int scanDelay;
		bool ambushing;

		public AotSubterraneanAmbush(ActorInitializer init, AotSubterraneanAmbushInfo info)
			: base(info)
		{
			mobile = init.Self.Trait<Mobile>();
			startUnderground = init.GetValue<AotUndergroundInit, bool>(info.Underground);
		}

		protected override void Created(Actor self)
		{
			attackBases = self.TraitsImplementing<AttackBase>().ToArray();
			base.Created(self);
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			if (!startUnderground || IsTraitDisabled)
				return;

			// Deferred to the end of the frame on purpose: Mobile registers the actor in the ActorMap
			// from its own INotifyAddedToWorld, and trait order is not guaranteed. Relocating the actor
			// before that registration would corrupt the influence layers.
			self.World.AddFrameEndTask(w => Submerge(self));
		}

		void Submerge(Actor self)
		{
			if (self.IsDead || !self.IsInWorld || IsTraitDisabled)
				return;

			var below = new CPos(self.Location.X, self.Location.Y, CustomMovementLayerType.Subterranean);

			// The cell may be un-buryable (building footprint, Fortified Foundation, ramp -- see
			// SubterraneanActorLayer.ValidTransitionCell). Then simply stay on the surface rather
			// than teleporting the unit somewhere it could never have dug in by itself.
			if (!mobile.CanEnterCell(below))
				return;

			// SetPosition sets FromCell and ToCell to the same cell, so INotifyCustomLayerChanged does
			// NOT fire and no dig-in animation plays -- correct here, the unit is meant to be buried
			// already.
			mobile.SetPosition(self, below);

			// ...but SetPosition also does "position -= (0, 0, DistanceAboveTerrain(position))", i.e. it
			// snaps the actor back onto the terrain surface - it exists to place units ON the ground.
			// The cell/layer would be right while the actor still floats at surface height, so
			// GrantConditionOnSubterraneanLayer never sees a depth below SubterraneanTransitionDepth
			// and "submerged" is never granted (unit visibly sits on top of the snow, not buried).
			// Push it down to the layer's real depth; this re-fires INotifyCenterPositionChanged,
			// which is what actually grants "submerged".
			var layer = self.World.GetCustomMovementLayers()[CustomMovementLayerType.Subterranean];
			mobile.SetCenterPosition(self, layer.CenterOfCell(below));

			ambushing = true;
			scanDelay = Info.ScanInterval;

			if (conditionToken == Actor.InvalidConditionToken && !string.IsNullOrEmpty(Info.AmbushCondition))
				conditionToken = self.GrantCondition(Info.AmbushCondition);
		}

		void StopAmbush(Actor self)
		{
			ambushing = false;

			if (conditionToken != Actor.InvalidConditionToken)
				conditionToken = self.RevokeCondition(conditionToken);
		}

		void ITick.Tick(Actor self)
		{
			if (!ambushing || IsTraitDisabled)
				return;

			// The player gave an order. Drop out of ambush and let Mobile's normal idle handling
			// surface the unit once that order finishes -- no need to queue anything ourselves.
			if (!self.IsIdle)
			{
				StopAmbush(self);
				return;
			}

			if (--scanDelay > 0)
				return;

			scanDelay = Info.ScanInterval;

			if (!HasTargetInRange(self))
				return;

			// Surface now. The unit is idle, so OnBecomingIdle will not fire again on its own and
			// would leave it buried forever; queue the dig-out explicitly. Once above ground the
			// normal AutoTarget handling takes over and engages.
			// The ambush is only ended once we know we can actually get out: if something is parked
			// on the cell above, ending it here would strand the unit underground with an enabled
			// but permanently paused attack. Staying in ambush means we simply retry next scan.
			var above = new CPos(self.Location.X, self.Location.Y);
			if (!mobile.CanEnterCell(above))
				return;

			StopAmbush(self);
			self.QueueActivity(false, mobile.MoveTo(above, 0));
		}

		bool HasTargetInRange(Actor self)
		{
			var range = Info.ScanRange;
			if (range == WDist.Zero)
			{
				foreach (var ab in attackBases)
				{
					// Deliberately not filtering on IsTraitPaused: the attack traits are paused *because*
					// the unit is submerged (PauseOnCondition: submerged), so honouring that here would
					// mean the ambush could never trigger.
					if (ab.IsTraitDisabled)
						continue;

					var abRange = ab.GetMaximumRange();
					if (abRange > range)
						range = abRange;
				}
			}

			if (range == WDist.Zero)
				return false;

			foreach (var a in self.World.FindActorsInCircle(self.CenterPosition, range))
			{
				if (a == self || a.IsDead || !a.IsInWorld)
					continue;

				if (!a.AppearsHostileTo(self))
					continue;

				var target = Target.FromActor(a);
				foreach (var ab in attackBases)
					if (!ab.IsTraitDisabled && ab.HasAnyValidWeapons(target))
						return true;
			}

			return false;
		}

		bool IPreventsIdleLayerReturn.PreventsIdleLayerReturn(Actor self)
		{
			return ambushing && !IsTraitDisabled;
		}

		protected override void TraitDisabled(Actor self) { StopAmbush(self); }
	}

	// Carries the map editor checkbox into the saved actor.
	public class AotUndergroundInit : ValueActorInit<bool>, ISingleInstanceInit
	{
		public AotUndergroundInit(bool value)
			: base(value) { }
	}
}
