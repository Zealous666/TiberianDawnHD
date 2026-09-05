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
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("aotmod: On infiltration, spawns a camera actor owned by the infiltrator ON this building so",
		"the infiltrator both sees the sheltered area AND detects the cloaked buildings/units inside it",
		"(pure shroud reveal is NOT enough -- cloaked actors need DetectCloaked). Used on the Shroud/",
		"Stealth Generators. The camera persists for as long as THIS building stands; it is removed only",
		"when this building leaves the world (destroyed/sold). Give the camera actor a RevealsShroud +",
		"DetectCloaked range matching this generator's own field radius.")]
	sealed class AotInfiltrateForAreaRevealInfo : TraitInfo
	{
		[Desc("The `TargetTypes` from `Targetable` that are allowed to trigger this.")]
		public readonly BitSet<TargetableType> Types = default;

		[ActorReference]
		[FieldLoader.Require]
		[Desc("Camera actor spawned on this building for the infiltrator (RevealsShroud + DetectCloaked).")]
		public readonly string CameraActor = null;

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

		public override object Create(ActorInitializer init) { return new AotInfiltrateForAreaReveal(this); }
	}

	sealed class AotInfiltrateForAreaReveal : INotifyInfiltrated, INotifyRemovedFromWorld
	{
		readonly AotInfiltrateForAreaRevealInfo info;

		// One persistent camera per infiltrating player (re-infiltration by the same player does not stack).
		readonly Dictionary<Player, Actor> cameras = new();

		public AotInfiltrateForAreaReveal(AotInfiltrateForAreaRevealInfo info)
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

			var owner = infiltrator.Owner;

			// Already scanning for this player -> nothing to add.
			if (cameras.TryGetValue(owner, out var existing) && existing.IsInWorld)
				return;

			// A camera owned by the infiltrator, sitting on the generator. Its RevealsShroud lifts the
			// shroud over the field and its DetectCloaked uncovers the cloaked buildings/units inside it.
			var camera = self.World.CreateActor(false, info.CameraActor,
			[
				new LocationInit(self.Location),
				new OwnerInit(owner),
			]);

			cameras[owner] = camera;
			self.World.AddFrameEndTask(w => w.Add(camera));
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			// The generator the spy infiltrated is gone -> tear down every scan camera it granted.
			var toRemove = new List<Actor>(cameras.Values);
			cameras.Clear();

			self.World.AddFrameEndTask(w =>
			{
				foreach (var camera in toRemove)
					if (!camera.IsDead && camera.IsInWorld)
						camera.Dispose();
			});
		}
	}
}
