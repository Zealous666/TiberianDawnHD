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

using OpenRA.Mods.Common.Effects;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Grants Condition on subterranean layer. Also plays transition audio-visuals.")]
	public class GrantConditionOnSubterraneanLayerInfo : GrantConditionOnLayerInfo
	{
		[Desc("Dig animation image to play when transitioning.")]
		public readonly string SubterraneanTransitionImage = null;

		[SequenceReference(nameof(SubterraneanTransitionImage))]
		[Desc("Dig animation sequence to play when transitioning.")]
		public readonly string SubterraneanTransitionSequence = null;

		[PaletteReference]
		public readonly string SubterraneanTransitionPalette = "effect";

		[Desc("Dig sound to play when transitioning.")]
		public readonly string SubterraneanTransitionSound = null;

		[GrantedConditionReference]
		[Desc("Condition granted while transitioning between surface and subterranean layer (between layer switch and reaching TransitionDepth).")]
		public readonly string TransitionCondition = null;

		public override object Create(ActorInitializer init) { return new GrantConditionOnSubterraneanLayer(this); }

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			var mobileInfo = ai.TraitInfoOrDefault<MobileInfo>();
			if (mobileInfo == null || mobileInfo.LocomotorInfo is not SubterraneanLocomotorInfo)
				throw new YamlException("GrantConditionOnSubterraneanLayer requires Mobile to be linked to a SubterraneanLocomotor!");

			base.RulesetLoaded(rules, ai);
		}
	}

	public class GrantConditionOnSubterraneanLayer : GrantConditionOnLayer<GrantConditionOnSubterraneanLayerInfo>, INotifyCenterPositionChanged
	{
		WDist transitionDepth;
		int transitionConditionToken = Actor.InvalidConditionToken;

		public GrantConditionOnSubterraneanLayer(GrantConditionOnSubterraneanLayerInfo info)
			: base(info, CustomMovementLayerType.Subterranean) { }

		protected override void Created(Actor self)
		{
			var mobileInfo = self.Info.TraitInfo<MobileInfo>();
			var li = (SubterraneanLocomotorInfo)mobileInfo.LocomotorInfo;
			transitionDepth = li.SubterraneanTransitionDepth;
			base.Created(self);
		}

		void PlayTransitionAudioVisuals(Actor self, CPos fromCell)
		{
			if (!string.IsNullOrEmpty(Info.SubterraneanTransitionSequence))
				self.World.AddFrameEndTask(w => w.Add(new SpriteEffect(self.World.Map.CenterOfCell(fromCell), self.World,
					Info.SubterraneanTransitionImage,
					Info.SubterraneanTransitionSequence, Info.SubterraneanTransitionPalette)));

			if (!string.IsNullOrEmpty(Info.SubterraneanTransitionSound))
				Game.Sound.Play(SoundType.World, Info.SubterraneanTransitionSound);
		}

		void INotifyCenterPositionChanged.CenterPositionChanged(Actor self, byte oldLayer, byte newLayer)
		{
			var depth = self.World.Map.DistanceAboveTerrain(self.CenterPosition);

			// Grant submerged when depth crosses threshold during dig-in; also revoke transition condition.
			if (newLayer == ValidLayerType && depth < transitionDepth && conditionToken == Actor.InvalidConditionToken)
			{
				conditionToken = self.GrantCondition(Info.Condition);
				if (transitionConditionToken != Actor.InvalidConditionToken)
					transitionConditionToken = self.RevokeCondition(transitionConditionToken);
			}
			// Revoke submerged when depth crosses threshold during dig-out.
			// TransitionCondition is NOT granted here: UpdateConditions (CustomLayerChanged) fires before
			// CenterPositionChanged at layer-change time, so granting here would leave it stranded forever.
			else if (newLayer != ValidLayerType && depth > transitionDepth && conditionToken != Actor.InvalidConditionToken)
			{
				conditionToken = self.RevokeCondition(conditionToken);
				PlayTransitionAudioVisuals(self, self.Location);
			}
		}

		protected override void UpdateConditions(Actor self, byte oldLayer, byte newLayer)
		{
			if (newLayer == ValidLayerType && oldLayer != ValidLayerType)
			{
				// Layer switched to subterranean (dig-in start): grant transition condition + play dust.
				if (!string.IsNullOrEmpty(Info.TransitionCondition) && transitionConditionToken == Actor.InvalidConditionToken)
					transitionConditionToken = self.GrantCondition(Info.TransitionCondition);
				PlayTransitionAudioVisuals(self, self.Location);
			}
			else if (oldLayer == ValidLayerType && newLayer != ValidLayerType)
			{
				// Layer switched back to normal (dig-out finish): revoke transition condition.
				if (transitionConditionToken != Actor.InvalidConditionToken)
					transitionConditionToken = self.RevokeCondition(transitionConditionToken);
			}
		}

		protected override void TraitDisabled(Actor self)
		{
			base.TraitDisabled(self);
			if (transitionConditionToken != Actor.InvalidConditionToken)
				transitionConditionToken = self.RevokeCondition(transitionConditionToken);
		}
	}
}
