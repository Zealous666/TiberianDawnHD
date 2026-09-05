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
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Temporarily reveals the entire map to the infiltrating player for a set duration.")]
	sealed class AotInfiltrateForScanInfo : TraitInfo
	{
		[Desc("The `TargetTypes` from `Targetable` that are allowed to trigger this.")]
		public readonly BitSet<TargetableType> Types = default;

		[Desc("Duration of the scan in ticks (25 ticks = 1 second).")]
		public readonly int Duration = 750;

		[Desc("Experience to grant to the infiltrating player.")]
		public readonly int PlayerExperience = 0;

		[NotificationReference("Speech")]
		public readonly string InfiltratedNotification = null;

		[FluentReference(optional: true)]
		public readonly string InfiltratedTextNotification = null;

		[NotificationReference("Speech")]
		public readonly string InfiltrationNotification = null;

		[FluentReference(optional: true)]
		public readonly string InfiltrationTextNotification = null;

		public override object Create(ActorInitializer init) { return new AotInfiltrateForScan(this); }
	}

	sealed class AotInfiltrateForScan : INotifyInfiltrated
	{
		readonly AotInfiltrateForScanInfo info;

		public AotInfiltrateForScan(AotInfiltrateForScanInfo info)
		{
			this.info = info;
		}

		void INotifyInfiltrated.Infiltrated(Actor self, Actor infiltrator, BitSet<TargetableType> types)
		{
			if (!info.Types.Overlaps(types))
				return;

			if (info.InfiltratedNotification != null)
				Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech", info.InfiltratedNotification, self.Owner.Faction.InternalName);

			if (info.InfiltrationNotification != null)
				Game.Sound.PlayNotification(self.World.Map.Rules, infiltrator.Owner, "Speech", info.InfiltrationNotification, infiltrator.Owner.Faction.InternalName);

			TextNotificationsManager.AddTransientLine(self.Owner, info.InfiltratedTextNotification);
			TextNotificationsManager.AddTransientLine(infiltrator.Owner, info.InfiltrationTextNotification);

			infiltrator.Owner.PlayerActor.TraitOrDefault<PlayerExperience>()?.GiveExperience(info.PlayerExperience);

			var shroud = infiltrator.Owner.Shroud;
			var enemyShroud = self.Owner.Shroud;
			var visibleCells = self.World.Map.ProjectedCells
				.Where(cell => enemyShroud.IsVisible(cell))
				.ToArray();
			shroud.AddSource(this, Shroud.SourceType.Visibility, visibleCells);

			infiltrator.World.AddFrameEndTask(w =>
			{
				w.Add(new AotScanEffect(shroud, this, info.Duration));
			});
		}
	}

	sealed class AotScanEffect : IEffect
	{
		readonly Shroud shroud;
		readonly object key;
		int remaining;

		public AotScanEffect(Shroud shroud, object key, int duration)
		{
			this.shroud = shroud;
			this.key = key;
			remaining = duration;
		}

		void IEffect.Tick(World world)
		{
			if (--remaining <= 0)
			{
				shroud.RemoveSource(key);
				world.AddFrameEndTask(w => w.Remove(this));
			}
		}

		IEnumerable<IRenderable> IEffect.Render(WorldRenderer wr)
		{
			return Enumerable.Empty<IRenderable>();
		}
	}
}
