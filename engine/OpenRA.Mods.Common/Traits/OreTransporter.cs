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

		[Desc("Interval in ticks between 'Silos Needed' warnings when no delivery dock exists.")]
		public readonly int NoDockWarningInterval = 1500;

		[Desc("Stall recovery (User 2026-07-31): if the transporter is EMPTY (travelling toward a mine)",
			"and CurrentActivity is set but its position hasn't changed in this many ticks, the activity",
			"is cancelled so Tick can pick a fresh target next tick. Covers two observed cases: a fresh",
			"spawn stuck at a congested factory exit, and a queued MoveToDock whose target mine died",
			"(destroyed, or tapped out by another transporter) between being queued and arriving -- either",
			"way CurrentActivity stays non-null forever with nothing actually happening, and the ITick",
			"guard above then never re-runs FindNearestDock. Only applies while Empty: Loading and",
			"waiting-for-silo-space are legitimate reasons to sit still and must never be interrupted.")]
		public readonly int StallRecoveryTicks = 150;

		[NotificationReference("Speech")]
		[Desc("Speech notification to play when full but no delivery dock is available.")]
		public readonly string NoDockNotification = "SilosNeeded";

		public override object Create(ActorInitializer init) { return new OreTransporter(init.Self, this); }
	}

	public class OreTransporter : DockClientBase<OreTransporterInfo>, ITick
	{
		static readonly BitSet<DockType> OreLoadType = new("OreLoad");
		static readonly BitSet<DockType> UnloadType = new("OreDeliver");

		enum TransportState { Empty, Loading, Full }
		TransportState state = TransportState.Empty;
		int loadTicks;
		int noDockWarningTicks;
		IStoresResources storesResources;
		IMove move;
		readonly Actor self;

		// Stall recovery, see StallRecoveryTicks.
		CPos lastPos;
		int stallTicks;
		bool stallTracking;

		public override BitSet<DockType> GetDockType =>
			state == TransportState.Full ? UnloadType : OreLoadType;

		// Scans the entire map for the nearest dockable host of the given type by straight-line
		// distance. Used for both legs of the cycle instead of DockClientManager.ClosestDock, whose
		// pathfinding-based search returns null (and strands the transporter) for distant targets.
		// A mine is destroyed the instant its store empties, so any live OreLoad host has resources.
		TraitPair<IDockHost>? FindNearestDock(BitSet<DockType> type)
		{
			TraitPair<IDockHost>? best = null;
			var bestDist = long.MaxValue;
			foreach (var pair in self.World.ActorsWithTrait<IDockHost>())
			{
				var host = pair.Trait;
				if (!host.GetDockType.Overlaps(type) || !host.IsEnabledAndInWorld)
					continue;

				if (!CanDockAt(pair.Actor, host, false, true))
					continue;

				var dist = (pair.Actor.CenterPosition - self.CenterPosition).HorizontalLengthSquared;
				if (dist < bestDist)
				{
					bestDist = dist;
					best = pair;
				}
			}

			return best;
		}

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
			move = self.Trait<IMove>();
			base.Created(self);
		}

		// The whole load/deliver cycle is driven from here based on state + idle. Once idle, queue the
		// correct next MoveToDock (targeting an explicit host so movement never depends on a distance-
		// sensitive dock search); the CurrentActivity guard prevents re-queueing while already moving.
		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled)
				return;

			// Stall recovery -- see StallRecoveryTicks. Only while Empty: Loading and waiting-for-silo-
			// space are legitimate reasons for CurrentActivity to sit non-null without the actor moving,
			// and must never be interrupted here.
			if (state == TransportState.Empty && self.CurrentActivity != null)
			{
				if (stallTracking && self.Location == lastPos)
				{
					if (++stallTicks >= Info.StallRecoveryTicks)
					{
						stallTicks = 0;
						OpenRA.Log.Write("debug", $"[OreTransporter] {self.Owner.PlayerName} #{self.ActorID} stalled at {self.Location} " +
							$"(activity={self.CurrentActivity?.GetType().Name}) -> cancelling to re-target");
						self.CancelActivity();
					}
				}
				else
				{
					lastPos = self.Location;
					stallTicks = 0;
					stallTracking = true;
				}
			}
			else
				stallTracking = false;

			if (self.CurrentActivity != null)
				return;

			if (state == TransportState.Full)
			{
				// Deliver: head to the nearest owned silo anywhere on the map.
				var silo = FindNearestDock(UnloadType);
				if (silo.HasValue)
				{
					self.QueueActivity(new MoveToDock(self, silo.Value.Actor, silo.Value.Trait));
					return;
				}

				// Full but no delivery dock exists: warn periodically ("Silos needed").
				if (--noDockWarningTicks <= 0)
				{
					noDockWarningTicks = Info.NoDockWarningInterval;
					var owner = self.Owner;
					Game.Sound.PlayNotification(self.World.Map.Rules, owner, "Speech", Info.NoDockNotification, owner.Faction.InternalName);
				}

				return;
			}

			// Empty (or freshly built): head to the nearest mine anywhere on the map that still has
			// resources, whether or not it was visited before.
			var mine = FindNearestDock(OreLoadType);
			if (mine.HasValue)
			{
				var host = mine.Value.Trait;

				// A mine hidden by fog is a "hidden actor": MoveToDock's Target.FromActor(mine) would
				// refuse to move toward it, stranding the transporter. So first drive to the mine's dock
				// cell with a plain cell-based Move (fog-immune); by the time we arrive the transporter's
				// own sight has revealed the mine, and the following MoveToDock docks normally.
				var dockCell = self.World.Map.CellContaining(host.DockPosition);
				self.QueueActivity(move.MoveTo(dockCell, 1));
				self.QueueActivity(new MoveToDock(self, mine.Value.Actor, host));
			}
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

		// No OnDockCompleted override: the load/deliver cycle is driven entirely from ITick.
	}
}
