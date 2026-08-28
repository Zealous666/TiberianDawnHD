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

using System.Linq;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("Display the time remaining until the super weapon attached to the actor is ready.")]
	sealed class SupportPowerChargeBarInfo : ConditionalTraitInfo
	{
		[Desc("Defines to which players the bar is to be shown.")]
		public readonly PlayerRelationship DisplayRelationships = PlayerRelationship.Ally;

		[Desc("aotmod: OrderName of the specific support power this bar tracks (e.g. `IonCannonPowerOrder`).",
			"Leave empty for the legacy behaviour of tracking the first non-disabled power on the actor --",
			"which makes ALL charge bars on a multi-power building show the same progress (the bug this fixes).",
			"With several powers on one actor, give each SupportPowerChargeBar its own Power so the bars are independent.")]
		public readonly string Power = null;

		public readonly Color Color = Color.Magenta;

		public override object Create(ActorInitializer init) { return new SupportPowerChargeBar(init.Self, this); }
	}

	sealed class SupportPowerChargeBar : ConditionalTrait<SupportPowerChargeBarInfo>, ISelectionBar, INotifyOwnerChanged
	{
		readonly Actor self;
		SupportPowerManager spm;

		public SupportPowerChargeBar(Actor self, SupportPowerChargeBarInfo info)
			: base(info)
		{
			this.self = self;
			spm = self.Owner.PlayerActor.Trait<SupportPowerManager>();
		}

		float ISelectionBar.GetValue()
		{
			if (IsTraitDisabled)
				return 0;

			var powers = spm.GetPowersForActor(self).Where(sp => !sp.Disabled);
			var power = string.IsNullOrEmpty(Info.Power)
				? powers.FirstOrDefault()
				: powers.FirstOrDefault(sp => sp.Info != null && sp.Info.OrderName == Info.Power);
			if (power == null)
				return 0;

			var viewer = self.World.RenderPlayer ?? self.World.LocalPlayer;
			if (viewer != null && !Info.DisplayRelationships.HasRelationship(self.Owner.RelationshipWith(viewer)))
				return 0;

			return 1 - (float)power.RemainingTicks / power.TotalTicks;
		}

		Color ISelectionBar.GetColor() { return Info.Color; }
		bool ISelectionBar.DisplayWhenEmpty => false;

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			spm = newOwner.PlayerActor.Trait<SupportPowerManager>();
		}
	}
}
