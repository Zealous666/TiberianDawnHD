// === Age of Tiberium (aotmod) ===
// Broadcasts a global system message to all clients when an Age upgrade completes production.
// Placed on aot-age1/2/3-upgrade-gdi/nod actors. Fires via INotifyAddedToWorld because
// MoveIntoWorld:false actors are still Add()ed to the world when production completes.
// AddedToWorld runs in the deterministic game loop → fires on every client simultaneously
// → all players see the message without requiring a network order.
// === Ende aotmod ===

using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Broadcasts a system message to all players when this actor is added to the world (age upgrade production complete).")]
	public class AotAgeActivationNotificationInfo : TraitInfo
	{
		[Desc("Age display name used in the broadcast message.")]
		public readonly string AgeName = "1st Tiberium Age";

		[NotificationReference("Speech")]
		[Desc("Speech notification played to the advancing player only, announcing the newly unlocked build options.")]
		public readonly string NewOptionsNotification = "NewOptions";

		public override object Create(ActorInitializer init) => new AotAgeActivationNotification(init.Self, this);
	}

	public class AotAgeActivationNotification : INotifyAddedToWorld
	{
		readonly AotAgeActivationNotificationInfo info;

		public AotAgeActivationNotification(Actor self, AotAgeActivationNotificationInfo info)
		{
			this.info = info;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			TextNotificationsManager.AddSystemLine("AoT", $"{self.Owner.PlayerName} has evolved into the {info.AgeName}!");

			// aotmod (2026-08-01, User-Wunsch): "New build options"-Ansage beim Tier-Aufstieg.
			// Reaching a new Age tier reveals every upgrade of that tier at once (see the
			// ~aot-ageN visibility gates in aot-structures.yaml), so the vanilla NewOptions
			// speech fits exactly. Sound.PlayPredefined does NOT filter by LocalPlayer, so the
			// owner check here is what keeps it from playing on every client -- purely a
			// client-side audio decision, no gameplay state, so it cannot desync.
			if (self.Owner == self.World.LocalPlayer)
				Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech",
					info.NewOptionsNotification, self.Owner.Faction.InternalName);
		}
	}
}
