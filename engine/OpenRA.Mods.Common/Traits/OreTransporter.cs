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
		//
		// Straight-line distance ALONE is not enough (User 2026-08-01: "er hat seinen ORET mit dem
		// Yard Stacheldraht eingebaut" -- the transporter sat motionless at its spawn cell from tick
		// one and the yard fence simply closed around it later). The nearest mine by air can be
		// unreachable on foot (across water, behind a cliff): MoveTo then finds no path, the stall
		// watchdog cancels, and this method hands back the very same unreachable mine again --
		// an infinite retarget loop that never moves the transporter a single cell. Candidates are
		// therefore checked for an actual ground path and unreachable ones skipped, so the search
		// falls through to the nearest mine that can genuinely be driven to.
		TraitPair<IDockHost>? FindNearestDock(BitSet<DockType> type)
		{
			TraitPair<IDockHost>? best = null;
			var bestDist = long.MaxValue;
			var mobile = self.TraitOrDefault<Mobile>();
			foreach (var pair in self.World.ActorsWithTrait<IDockHost>())
			{
				var host = pair.Trait;
				if (!host.GetDockType.Overlaps(type) || !host.IsEnabledAndInWorld)
					continue;

				if (!CanDockAt(pair.Actor, host, false, true))
					continue;

				if (mobile != null && !mobile.PathFinder.PathExistsForLocomotor(
					mobile.Locomotor, self.Location, self.World.Map.CellContaining(host.DockPosition)))
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

						// Diagnostic (User 2026-08-01: "aber es waren Minen ganz in der Naehe!"). The stall
						// line above says the transporter is not moving, never WHY -- an unreachable target,
						// a blocked spawn cell and a blocked dock cell all look identical from outside. Dumps
						// the chosen target, whether a ground path to it exists at all, and which of the eight
						// neighbouring cells are currently enterable, so the next stalled game names its own
						// cause instead of leaving it to guesswork.
						var mob = self.TraitOrDefault<Mobile>();
						var target = FindNearestDock(GetDockType);
						if (mob != null)
						{
							var free = CVec.Directions.Count(d => mob.CanEnterCell(self.Location + d, null, BlockedByActor.All));
							var targetInfo = "NONE";
							if (target.HasValue)
							{
								var tc = self.World.Map.CellContaining(target.Value.Trait.DockPosition);
								targetInfo = $"{target.Value.Actor.Info.Name}@{tc} " +
									$"pathExists={mob.PathFinder.PathExistsForLocomotor(mob.Locomotor, self.Location, tc)}";
							}

							OpenRA.Log.Write("debug", $"  oret diag: target={targetInfo} freeNeighbourCells={free}/8 " +
								$"blockedBy=[{string.Join(", ", CVec.Directions
									.Where(d => !mob.CanEnterCell(self.Location + d, null, BlockedByActor.All))
									.SelectMany(d => self.World.ActorMap.GetActorsAt(self.Location + d))
									.Select(a => a.Info.Name).Distinct())}]");
						}

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
				return;
			}

			// Empty with no reachable mine: without this the transporter simply queues nothing and sits
			// there silently -- no activity means the stall watchdog above never fires either, so the
			// whole failure becomes invisible in the log (it only ever showed the retarget loop, which
			// needs an activity to exist). Throttled through the same counter as the "no delivery dock"
			// case so it reports the situation once in a while instead of every tick.
			if (--noDockWarningTicks <= 0)
			{
				noDockWarningTicks = Info.NoDockWarningInterval;
				var reachable = self.TraitOrDefault<Mobile>() != null;
				OpenRA.Log.Write("debug", $"[OreTransporter] {self.Owner.PlayerName} #{self.ActorID} at {self.Location}: " +
					$"empty but NO reachable ore mine found (mobile={reachable}) -- idling");
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
