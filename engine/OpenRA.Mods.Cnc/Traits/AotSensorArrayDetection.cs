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

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Sound;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Plays a speech notification and shows a radar ping when this actor detects a cloaked " +
		"or subterranean enemy unit. Requires DetectCloaked on the same actor. " +
		"Underground units (CloakType: Underground) use a separate notification.")]
	public class AotSensorArrayDetectionInfo : ConditionalTraitInfo
	{
		[NotificationReference("Speech")]
		[Desc("Speech notification key (NOD/neutral faction) for cloaked unit detection.")]
		public readonly string Notification = "CloakedUnitDetected";

		[NotificationReference("Speech")]
		[Desc("Speech notification key for GDI faction (GDI EVA voice). Falls back to Notification if empty.")]
		public readonly string GdiNotification = "CloakedUnitDetectedGdi";

		[NotificationReference("Speech")]
		[Desc("Speech notification key for subterranean unit detection (NOD/neutral).")]
		public readonly string SubterraneanNotification = "SubterraneanUnitDetected";

		[NotificationReference("Speech")]
		[Desc("Speech notification key for subterranean unit detection (GDI EVA voice). Falls back to SubterraneanNotification if empty.")]
		public readonly string SubterraneanGdiNotification = "SubterraneanUnitDetectedGdi";

		[Desc("CloakType value that identifies subterranean units (must match Cloak@UNDERGROUND.CloakType).")]
		public readonly string SubterraneanCloakType = "Underground";

		[FluentReference(optional: true)]
		[Desc("Text notification to display in the message log.")]
		public readonly string TextNotification = null;

		[Desc("Color of the radar ping shown at the detected unit's position.")]
		public readonly Color RadarPingColor = Color.FromArgb(180, 0, 220, 255);

		[Desc("Duration of the radar ping in ticks.")]
		public readonly int RadarPingDuration = 100;

		[Desc("Minimum ticks between detection notifications (prevents spam).")]
		public readonly int NotificationCooldown = 250;

		public override object Create(ActorInitializer init) { return new AotSensorArrayDetection(init.Self, this); }
	}

	public class AotSensorArrayDetection : ConditionalTrait<AotSensorArrayDetectionInfo>, ITick
	{
		readonly HashSet<Actor> previouslyDetected = [];
		readonly Lazy<RadarPings> radarPings;

		DetectCloaked detectCloaked;
		int cooldownTicks;

		public AotSensorArrayDetection(Actor self, AotSensorArrayDetectionInfo info)
			: base(info)
		{
			radarPings = Exts.Lazy(self.World.WorldActor.Trait<RadarPings>);
		}

		protected override void Created(Actor self)
		{
			detectCloaked = self.TraitOrDefault<DetectCloaked>();
			base.Created(self);
		}

		bool IsSubterranean(Actor a)
		{
			return a.TraitsImplementing<Cloak>()
				.Any(c => !c.IsTraitDisabled && c.Info.CloakType == Info.SubterraneanCloakType);
		}

		void ITick.Tick(Actor self)
		{
			if (cooldownTicks > 0)
				cooldownTicks--;

			if (IsTraitDisabled || detectCloaked == null || detectCloaked.IsTraitDisabled)
			{
				previouslyDetected.Clear();
				return;
			}

			var range = detectCloaked.Range;
			var currentlyDetected = self.World.FindActorsInCircle(self.CenterPosition, range)
				.Where(a => a != self
					&& !a.IsDead
					&& a.IsInWorld
					&& a.Owner.RelationshipWith(self.Owner) == PlayerRelationship.Enemy
					&& a.TraitsImplementing<Cloak>().Any(c => !c.IsTraitDisabled))
				.ToHashSet();

			if (cooldownTicks == 0)
			{
				var newlyDetected = currentlyDetected.Except(previouslyDetected).ToList();
				if (newlyDetected.Count > 0)
				{
					var localPlayer = self.World.LocalPlayer;
					if (localPlayer != null && !localPlayer.Spectating && self.Owner == localPlayer)
					{
						var isGdi = self.Owner.Faction.InternalName == "gdi";
						var firstSub = newlyDetected.FirstOrDefault(IsSubterranean);
						var firstCloaked = newlyDetected.FirstOrDefault(a => !IsSubterranean(a));

						if (firstSub != null)
						{
							var notifKey = isGdi && !string.IsNullOrEmpty(Info.SubterraneanGdiNotification)
								? Info.SubterraneanGdiNotification
								: Info.SubterraneanNotification;
							if (!string.IsNullOrEmpty(notifKey))
								Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner,
									"Speech", notifKey, self.Owner.Faction.InternalName);

							var pingPos = firstSub.CenterPosition;
							radarPings.Value?.Add(() => true, pingPos, Info.RadarPingColor, Info.RadarPingDuration);
						}

						if (firstCloaked != null)
						{
							var notifKey = isGdi && !string.IsNullOrEmpty(Info.GdiNotification)
								? Info.GdiNotification
								: Info.Notification;
							Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner,
								"Speech", notifKey, self.Owner.Faction.InternalName);

							TextNotificationsManager.AddTransientLine(self.Owner, Info.TextNotification);

							var pingPos = firstCloaked.CenterPosition;
							radarPings.Value?.Add(() => true, pingPos, Info.RadarPingColor, Info.RadarPingDuration);
						}
					}

					cooldownTicks = Info.NotificationCooldown;
				}
			}

			previouslyDetected.Clear();
			previouslyDetected.UnionWith(currentlyDetected);
		}
	}
}
