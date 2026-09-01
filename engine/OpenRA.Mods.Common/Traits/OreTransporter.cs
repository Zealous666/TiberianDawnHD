#region Copyright & License Information
/*
 * Age of Tiberium Mod (aotmod) — OreTransporter trait
 * DockClientBase subclass: docks at OreLoad (mine) then Unload (construction yard).
 * Does NOT use the terrain resource layer (IResourceLayer).
 */
#endregion

using System.Linq;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits.Render;
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

		[SequenceReference]
		[Desc("aotmod: Erz-Sammel-Animation (Schaufel rauf/runter), abgespielt waehrend des Ladens ",
			"an der Ore Mine. Leer = keine Animation. Wird auf dem WithSpriteBody 'body' abgespielt.")]
		public readonly string HarvestSequence = "harvest";

		[SequenceReference]
		[Desc("aotmod: Entlade-Animation (Ladeflaeche oeffnet) am Silo. Einmal abgespielt, dann DockLoopSequence.")]
		public readonly string DockSequence = "dock";

		[SequenceReference]
		[Desc("aotmod: Entlade-Schleife (Ladeflaeche gekippt) am Silo, waehrend die Credits uebergeben werden.")]
		public readonly string DockLoopSequence = "dock-loop";

		[Desc("aotmod: Ticks, die am Silo mit laufender Entlade-Animation verharrt wird, bevor die ",
			"Credits gutgeschrieben werden (nur wenn Lagerplatz frei ist). 0 = sofort ohne Animation.")]
		public readonly int UnloadDelay = 20;

		[Desc("aotmod: Fahrzeug-Ausrichtung, in die sich der ORET am Silo eindreht, BEVOR die Entlade-",
			"Animation startet. Die dock/dock-loop-Frames sind richtungslos gebacken (Ost-West-Optik), ",
			"daher muss der Koerper vorher dorthin drehen, sonst springt das Sprite. ",
			"WAngle: 0=Nord, 256=Ost, 512=Sued, 768=West.")]
		public readonly WAngle UnloadFacing = new(256);

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

	public class OreTransporter : DockClientBase<OreTransporterInfo>, ITick, IResolveOrder
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

		// aotmod: Sammel-/Entlade-Animation. Optional -- ohne WithSpriteBody laeuft der Zyklus wie bisher.
		WithSpriteBody wsb;
		IFacing facing;
		bool unloading;
		int unloadTicks;

		// Stall recovery, see StallRecoveryTicks.
		Actor lastDeliveryTarget;
		bool manualHold;
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

		// See manualHold in Tick: a hand-issued Move or Stop parks the transporter, and pointing it at
		// a mine or a silo puts it back to work. Bot owners are ignored entirely -- their own modules
		// issue Move orders for reasons that have nothing to do with parking.
		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (self.Owner.IsBot)
				return;

			if (order.OrderString is "Move" or "Stop" or "Scatter")
				manualHold = true;
			else if (order.OrderString is "Dock" or "Deliver" or "Harvest" or "Enter")
				manualHold = false;
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
			wsb = self.TraitsImplementing<WithSpriteBody>().FirstOrDefault();
			facing = self.TraitOrDefault<IFacing>();
			base.Created(self);
		}

		// The whole load/deliver cycle is driven from here based on state + idle. Once idle, queue the
		// correct next MoveToDock (targeting an explicit host so movement never depends on a distance-
		// sensitive dock search); the CurrentActivity guard prevents re-queueing while already moving.
		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled)
				return;

			// TARGET GONE -> RE-TARGET AT ONCE (User 2026-08-11: "wenn oremine kaputt geht, er
			// automatisch zum naechst besseren faehrt"). A mine is removed the moment it empties, so
			// this happens constantly. Without it the transporter keeps walking to where the mine used
			// to be and only recovers when the stall watchdog eventually fires -- a long detour for
			// something that can be noticed immediately.
			if (!manualHold && self.CurrentActivity != null && DockClientManager?.ReservedHostActor != null
				&& (DockClientManager.ReservedHostActor.IsDead || !DockClientManager.ReservedHostActor.IsInWorld))
			{
				self.CancelActivity();
				return;
			}

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

			// SENT SOMEWHERE BY HAND -> STAY THERE (User 2026-08-11). Without this the cycle simply
			// resumes the moment the move finishes and the transporter drives off to the nearest mine
			// again, so a player cannot park one anywhere. Cleared by ordering it back to a mine or a
			// silo, which is how you put it back to work.
			//
			// Only for human owners: a bot's own modules issue Move orders too -- nudging units off a
			// building site, for one -- and honouring those would silently retire the AI's economy.
			if (manualHold)
				return;

			if (state == TransportState.Full)
			{
				// Deliver: head to the nearest owned silo anywhere on the map.
				var silo = FindNearestDock(UnloadType);
				if (silo.HasValue)
				{
					// Logged whenever the chosen silo CHANGES. A transporter reported driving past a
					// newly built expansion silo all the way back to the main base, and there was no
					// way to tell whether it never considered the near one, could not path to it, or
					// could not dock there -- the stall diagnostics only ever cover the mine leg
					// (User 2026-08-11).
					if (silo.Value.Actor != lastDeliveryTarget)
					{
						lastDeliveryTarget = silo.Value.Actor;
						OpenRA.Log.Write("debug", $"[OreTransporter] {self.Owner.InternalName} #{self.ActorID} at {self.Location}: " +
							$"delivering to {silo.Value.Actor.Info.Name}@{silo.Value.Actor.Location} " +
							$"({(silo.Value.Actor.CenterPosition - self.CenterPosition).HorizontalLength / 1024} cells away)");
					}

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

				// Erz-Sammel-Animation (Schaufel rauf/runter) fuer die Dauer des Ladens laufen lassen.
				if (wsb != null && !string.IsNullOrEmpty(Info.HarvestSequence))
					wsb.PlayCustomAnimationRepeating(self, Info.HarvestSequence);
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

				// Laden fertig -> zurueck auf idle.
				wsb?.CancelCustomAnimation(self);
				return true;
			}

			if (state == TransportState.Full)
			{
				var playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
				if (!playerResources.CanGiveResources(Info.OreLoadAmount))
					return false;

				// Erst auf die Entlade-Ausrichtung (West) eindrehen -- die dock-Frames sind richtungslos
				// gebacken, ohne Eindrehen wuerde das Sprite beim Animationsstart springen. Nur wenn eine
				// Animation vorhanden ist; sonst liefert der ORET wie zuvor ohne Drehung ab.
				if (wsb != null && facing != null && facing.Facing != Info.UnloadFacing)
				{
					facing.Facing = Util.TickFacing(facing.Facing, Info.UnloadFacing, facing.TurnSpeed);
					return false;
				}

				// Entlade-Animation abspielen und dafuer kurz verharren, bevor gutgeschrieben wird.
				if (!unloading)
				{
					unloading = true;
					unloadTicks = Info.UnloadDelay;
					PlayUnloadAnimation(self);
				}

				if (--unloadTicks > 0)
					return false;

				playerResources.GiveResources(Info.OreLoadAmount);
				state = TransportState.Empty;
				storesResources?.RemoveResource(Info.OreResourceType, storesResources.Capacity);
				unloading = false;
				wsb?.CancelCustomAnimation(self);
				return true;
			}

			return true;
		}

		// aotmod: Ladeflaeche einmal oeffnen (dock), dann die Kipp-Schleife (dock-loop) waehrend des Verharrens.
		void PlayUnloadAnimation(Actor self)
		{
			if (wsb == null)
				return;

			if (!string.IsNullOrEmpty(Info.DockSequence))
				wsb.PlayCustomAnimation(self, Info.DockSequence, () =>
				{
					if (!string.IsNullOrEmpty(Info.DockLoopSequence))
						wsb.PlayCustomAnimationRepeating(self, Info.DockLoopSequence);
				});
			else if (!string.IsNullOrEmpty(Info.DockLoopSequence))
				wsb.PlayCustomAnimationRepeating(self, Info.DockLoopSequence);
		}

		// Safety-Reset: raeumt eine laufende Sammel-/Entlade-Animation weg, wenn ein Andockvorgang endet
		// (auch bei Abbruch, z.B. Mine zwischen An- und Abfahrt zerstoert). Ein waehrend des Ladens
		// (state==Loading) abgebrochener Dock wird auf Empty zurueckgesetzt, damit die Schaufel-Animation
		// nicht waehrend der Weiterfahrt haengen bleibt und der Zyklus sauber neu greift.
		public override void OnDockCompleted(Actor self, Actor hostActor, IDockHost host)
		{
			if (state == TransportState.Loading)
				state = TransportState.Empty;

			unloading = false;
			wsb?.CancelCustomAnimation(self);
		}
	}
}
