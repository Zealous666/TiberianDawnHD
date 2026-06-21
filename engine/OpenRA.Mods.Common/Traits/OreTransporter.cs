#region Copyright & License Information
/*
 * Age of Tiberium Mod (aotmod) — OreTransporter trait
 * DockClientBase subclass: docks at OreLoad (mine) then Unload (construction yard).
 * Does NOT use the terrain resource layer (IResourceLayer).
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Drives to an Ore Mine (DockHost type OreLoad), loads a fixed credit amount, " +
		"then drives to a construction yard (DockHost type Unload) to deliver the credits.")]
	public class OreTransporterInfo : DockClientBaseInfo
	{
		[Desc("Credits gained per load from the Ore Mine.")]
		public readonly int OreLoadAmount = 250;

		[Desc("Ticks to wait at the mine while loading (25 = 1 second at default game speed).")]
		public readonly int OreLoadDelay = 25;

		[Desc("Resource type used to drive the pip display (must match StoresResources).")]
		public readonly string OreResourceType = "Tiberium";

		public override object Create(ActorInitializer init) { return new OreTransporter(init.Self, this); }
	}

	public class OreTransporter : DockClientBase<OreTransporterInfo>, INotifyAddedToWorld
	{
		static readonly BitSet<DockType> OreLoadType = new("OreLoad");
		static readonly BitSet<DockType> UnloadType = new("OreDeliver");

		enum TransportState { Empty, Loading, Full }
		TransportState state = TransportState.Empty;
		int loadTicks;
		IStoresResources storesResources;
		readonly Actor self;

		public override BitSet<DockType> GetDockType =>
			state == TransportState.Full ? UnloadType : OreLoadType;

		public override bool CanDockAt(Actor hostActor, IDockHost host, bool forceEnter = false, bool ignoreOccupancy = false)
		{
			if (host.GetDockType.Overlaps(UnloadType) && hostActor.Owner != self.Owner)
				return false;
			return base.CanDockAt(hostActor, host, forceEnter, ignoreOccupancy);
		}

		public OreTransporter(Actor self, OreTransporterInfo info)
			: base(self, info) { this.self = self; }

		protected override void Created(Actor self)
		{
			storesResources = self.TraitsImplementing<IStoresResources>()
				.FirstOrDefault(sr => sr.HasType(Info.OreResourceType));
			base.Created(self);
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || !self.IsInWorld || self.CurrentActivity != null)
					return;
				self.QueueActivity(new MoveToDock(self));
			});
		}

		public override void OnDockStarted(Actor self, Actor hostActor, IDockHost host)
		{
			if (host.GetDockType.Overlaps(OreLoadType))
			{
				state = TransportState.Loading;
				loadTicks = Info.OreLoadDelay;
			}
		}

		public override bool OnDockTick(Actor self, Actor hostActor, IDockHost host)
		{
			if (IsTraitDisabled)
				return true;

			if (state == TransportState.Loading)
			{
				if (--loadTicks > 0)
					return false;

				state = TransportState.Full;
				storesResources?.AddResource(Info.OreResourceType, storesResources.Capacity);
				hostActor.TraitOrDefault<OreMineDurability>()?.OnTrip(hostActor, self);
				return true;
			}

			if (state == TransportState.Full)
			{
				var playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
				if (!playerResources.CanGiveResources(Info.OreLoadAmount))
					return false;

				playerResources.GiveResources(Info.OreLoadAmount);
				state = TransportState.Empty;
				storesResources?.RemoveResource(Info.OreResourceType, storesResources.Capacity);
				return true;
			}

			return true;
		}

		public override void OnDockCompleted(Actor self, Actor hostActor, IDockHost host)
		{
			var currentActivity = self.CurrentActivity;
			var hasNextActivity = currentActivity != null && currentActivity.NextActivity != null;

			if (host.GetDockType.Overlaps(OreLoadType) && state == TransportState.Full && !hasNextActivity)
				self.QueueActivity(true, new MoveToDock(self));
			else if (host.GetDockType.Overlaps(UnloadType) && state == TransportState.Empty && !hasNextActivity)
				self.QueueActivity(true, new MoveToDock(self));
		}
	}
}
