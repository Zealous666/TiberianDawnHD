// === Age of Tiberium (aotmod) ===
// Broadcasts a global system message to all clients when an Age upgrade completes production.
// Placed on aot-age1/2/3-upgrade-gdi/nod actors. Fires via INotifyAddedToWorld because
// MoveIntoWorld:false actors are still Add()ed to the world when production completes.
// AddedToWorld runs in the deterministic game loop → fires on every client simultaneously
// → all players see the message without requiring a network order.
// === Ende aotmod ===

using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Broadcasts a system message to all players when this actor is added to the world (age upgrade production complete).")]
	public class AotAgeActivationNotificationInfo : TraitInfo
	{
		[Desc("Age display name used in the broadcast message.")]
		public readonly string AgeName = "1st Tiberium Age";

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
		}
	}
}
