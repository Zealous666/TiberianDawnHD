#region Copyright & License Information
/*
 * Age of Tiberium mod addition.
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
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// Who gets carried first. A wave beats a scout squad, but age boosts a waiting ticket so nothing
	// starves behind a stream of higher-priority ones.
	public enum AotTransitPriority
	{
		Reinforcement = 0,
		Scout = 1,
		Expansion = 2,
		AttackWave = 3,
	}

	// A booking. Missions own tickets, never vessels -- that inversion is the whole point of the
	// rebuild: three ships coupled into one mission-owned convoy meant a single wedged ship froze the
	// entire fleet, and a mission that finished held ships other missions were queued for.
	public sealed class AotTransitTicket
	{
		public readonly int Id;
		public readonly AotMission Owner;
		public readonly CPos Target;
		public readonly AotTransitPriority Priority;
		public readonly int IssuedTick;

		// Still to be carried. Units move out of here into Delivered as they step ashore.
		public readonly HashSet<Actor> Waiting = [];
		public readonly HashSet<Actor> Delivered = [];

		public AotTransitStop From;
		public AotTransitStop To;

		// While held, the service assigns vessels to the ticket but does NOT move its units: a scout
		// squad keeps sweeping reachable ground rather than standing on a beach for the several minutes
		// it takes to afford a transport (User 2026-07-24). The owner clears the hold once VesselAssigned
		// tells it a ship is genuinely on the way.
		public bool Hold;
		public bool VesselAssigned;

		public bool Cancelled;
		public bool Failed;

		// Zero-progress watchdog. Reset by any boarding or landing, so a long crossing is never
		// mistaken for a stall (the old hard timeout killed waves mid-crossing on big maps).
		public int IdleTicks;

		// Once boarding has begun the ticket holds the head of the queue until it is fully shipped --
		// otherwise a higher-priority booking arriving mid-load leaves half a wave stranded forever.
		public bool Started;

		// How long no vessel has been inbound for this ticket. Drives the recall of called-forward
		// units back to the staging ground: without it, units called to the boarding lane for a vessel
		// that then sank stood on the beach for the rest of the match, and every lost ship added a few
		// more -- rebuilding exactly the congestion the staging ground exists to prevent.
		public int NoInboundTicks;

		public bool Complete => Waiting.Count == 0 && Delivered.Count > 0;
		public bool Finished => Cancelled || Failed || Complete;

		// Where the delivered units gather: the far staging ground. This is what makes an attack wave
		// land "closed" without coupling a single vessel -- the ships shuttle independently, and the
		// wave simply does not move off until Waiting is empty.
		public CPos? Rally => To?.StagingCentre ?? To?.Shore;

		public AotTransitTicket(int id, AotMission owner, CPos target, AotTransitPriority priority, int issuedTick)
		{
			Id = id;
			Owner = owner;
			Target = target;
			Priority = priority;
			IssuedTick = issuedTick;
		}
	}

	public sealed partial class AotTransitService
	{
		// Returning is the ABORT path, not the normal way home: a vessel that still has troops aboard
		// but can no longer complete its booking sails back to the home quay and puts them ashore
		// there. Without it a wedged loaded ship carried its passengers for the rest of the match --
		// the ticket timed out, the units stayed inside the hull, and both were lost to the AI.
		// (An EMPTY vessel does not need this: it simply goes Idle, and Idle steers to the home berth.)
		enum VesselState { Idle, ToPickup, Loading, Crossing, Unloading, Returning }

		sealed class Vessel
		{
			public Actor Actor;
			public VesselState State = VesselState.Idle;
			public int TicketId = -1;
			public AotTransitStop At;
			public CPos Berth;
			public int LoadTicks;

			// Ticks spent docked with an empty hold and nothing left to order aboard.
			public int EmptyLoadTicks;
			public int StallFingerprint;
			public int StallTicks;
			public int Recoveries;

			// Ticket this vessel just gave up on, and how long it stays barred from taking it again.
			// Without the bar the dispatcher handed the same booking straight back on the next tick
			// (AssignVessels runs before the state machine), so a vessel that could not reach its berth
			// retried the identical berth forever -- observed 2026-07-29 as ships idling un-docked.
			public int BarredTicketId = -1;
			public int BarredTicks;

			// Consecutive idle ticks spent failing to close on the berth.
			public int ApproachFails;

			// Berth changes tried during the CURRENT leg, and the state that leg belongs to. A wedged
			// vessel should exhaust the other berths at its stop before giving up on the leg.
			public int BerthSwaps;
			public VesselState SwapLeg = VesselState.Idle;
		}

		readonly List<Vessel> vessels = [];
		readonly List<Actor> escorts = [];
		readonly Dictionary<Actor, CPos> escortStations = [];
		readonly List<AotTransitTicket> tickets = [];

		// unit -> the vessel that ordered it aboard / is carrying it.
		readonly Dictionary<Actor, Vessel> boarding = [];
		readonly Dictionary<Actor, Vessel> aboard = [];

		// Units currently called forward to the boarding lane, per stop.
		readonly HashSet<Actor> called = [];

		AotTransitMission mission;
		int nextTicketId = 1;
		int diagTicks;

		public bool Available => ops.Info.FerryTypes.Length > 0 && HomeStop != null;

		// ---- Public API: missions book fahrten, they never see a ship --------------------------

		public AotTransitTicket Request(AotMission owner, IEnumerable<Actor> units, CPos target,
			AotTransitPriority priority, bool hold = false)
		{
			if (!Available)
				return null;

			var to = FarStopFor(target);
			if (to == null)
				return null;

			var ticket = new AotTransitTicket(nextTicketId++, owner, target, priority, ops.World.WorldTick)
			{
				From = HomeStop,
				To = to,
				Hold = hold,
			};

			foreach (var u in units)
				if (!ops.IsGone(u))
					ticket.Waiting.Add(u);

			if (ticket.Waiting.Count == 0)
				return null;

			tickets.Add(ticket);
			EnsureMission();
			ops.RequestNavalProduction();
			Log($"ticket #{ticket.Id} ({owner.Name}, {priority}): {ticket.Waiting.Count} unit(s) " +
				$"{ticket.From.Shore} -> {to.Shore} for target {target}{(hold ? " [held]" : "")}");
			return ticket;
		}

		public void Cancel(AotTransitTicket ticket)
		{
			if (ticket == null || ticket.Cancelled)
				return;

			ticket.Cancelled = true;
			Log($"ticket #{ticket.Id} cancelled ({ticket.Delivered.Count} delivered, {ticket.Waiting.Count} left)");
		}

		// The hidden mission that OWNS the fleet. Everything in this AI that produces, pools or claims
		// units is keyed on an AotMission, so the service borrows one rather than growing a parallel
		// ownership path -- and because it never finishes, ships are never orphaned by a mission ending.
		void EnsureMission()
		{
			if (mission != null)
				return;

			mission = new AotTransitMission(ops, this);
			ops.Missions.Add(mission);
		}

		// ---- Traffic ---------------------------------------------------------------------------

		public void RunTraffic(IBot bot)
		{
			// IsGone, NOT CannotOrder: a ship leaving the pen and a submerged sub are both briefly
			// !IsInWorld while perfectly alive. Pruning on that dropped ships permanently out of the
			// fleet -- twice now, once for passengers and once for ships.
			foreach (var v in vessels.Where(v => ops.IsGone(v.Actor)).ToList())
			{
				v.At?.FreeBerth(v.Actor);
				vessels.Remove(v);
			}

			// A boarding entry is only valid while THAT vessel is actually lying at the quay taking
			// passengers. It used to be cleaned up only from inside TickLoading, i.e. only while the
			// vessel was still Loading -- so a unit whose ship departed (or sank) before it got aboard
			// kept a permanent "already boarding" mark. StageAndCall skips such a unit outright: it
			// never got a staging order, never got called forward again and was never offered another
			// ship, so it simply stood on the beach while three vessels waited at the quay for nobody
			// (2026-07-29: "[Loading#1 Loading#1 Loading#1] ... [#1:2/3]" until the ticket timed out).
			foreach (var (u, v) in boarding.ToList())
				if (ops.IsGone(u) || ops.IsGone(v.Actor) || !vessels.Contains(v) || v.State != VesselState.Loading)
					boarding.Remove(u);

			escorts.RemoveAll(ops.IsGone);
			foreach (var gone in escortStations.Keys.Where(e => !escorts.Contains(e)).ToList())
				escortStations.Remove(gone);

			TrackPassengers();
			CreditLandings(bot);
			PruneTickets();

			AcquireVessels();

			var queue = ActiveTickets();
			AssignVessels(queue);
			StageAndCall(bot, queue);

			foreach (var v in vessels.ToList())
				TickVessel(bot, v);

			StationEscorts(bot);

			if (++diagTicks % 8 == 0 && (queue.Count > 0 || vessels.Count > 0))
				Log($"traffic: {vessels.Count} vessel(s) [{string.Join(" ", vessels.Select(v => $"{v.State}#{v.TicketId}"))}], " +
					$"{escorts.Count} escort(s), {queue.Count} ticket(s) " +
					$"[{string.Join(" ", queue.Select(t => $"#{t.Id}:{t.Waiting.Count}/{t.Delivered.Count}"))}]");
		}

		// Highest effective priority first. Age boost stops a scout ticket starving behind a stream of
		// waves; a ticket already loading outranks everything so half-shipped groups always finish.
		List<AotTransitTicket> ActiveTickets()
		{
			var now = ops.World.WorldTick;
			return tickets
				.Where(t => !t.Finished)
				.OrderByDescending(t =>
					(t.Started ? 100000 : 0)
					+ (int)t.Priority * 10000

					// The boost has to be able to OUTGROW a priority class, or it is decoration. It was
					// capped at 9x1000 while the gap between Scout and AttackWave is 20000, so a scout
					// booking could never overtake a wave no matter how long it waited -- and on
					// Hammerfest it duly waited forever (2026-07-29, ticket #1 never saw a ship).
					+ Math.Min((now - t.IssuedTick) / Math.Max(1, ops.Info.TicketAgeBoostTicks), 20) * 2000)
				.ThenBy(t => t.Id)
				.ToList();
		}

		void PruneTickets()
		{
			foreach (var t in tickets.ToList())
			{
				t.Waiting.RemoveWhere(ops.IsGone);
				t.Delivered.RemoveWhere(ops.IsGone);

				if (t.Finished)
				{
					if (t.Complete)
						Log($"ticket #{t.Id} complete: {t.Delivered.Count} unit(s) landed");

					ReleaseTicket(t);
					tickets.Remove(t);
					continue;
				}

				// Everyone died before anyone got across.
				if (t.Waiting.Count == 0 && t.Delivered.Count == 0)
				{
					t.Failed = true;
					Log($"ticket #{t.Id} failed: nobody left to carry");
					continue;
				}

				// Zero-progress only: boarding and landing both reset this, so a slow crossing or a long
				// wait behind a higher-priority ticket never trips it.
				t.IdleTicks += ops.Info.MissionInterval;
				if (t.IdleTicks >= ops.Info.TicketTimeoutTicks)
				{
					t.Failed = true;
					Log($"ticket #{t.Id} timed out with no progress ({t.Delivered.Count} delivered)");
				}
			}
		}

		void ReleaseTicket(AotTransitTicket t)
		{
			foreach (var v in vessels.Where(v => v.TicketId == t.Id))
			{
				v.TicketId = -1;
				if (v.State == VesselState.ToPickup || v.State == VesselState.Loading)
				{
					v.At?.FreeBerth(v.Actor);
					v.State = VesselState.Idle;
				}
			}

			foreach (var u in t.Waiting.Concat(t.Delivered))
				called.Remove(u);
		}

		// ---- Fleet acquisition -----------------------------------------------------------------

		bool noDemandLogged;

		void AcquireVessels()
		{
			if (mission == null || !ops.HasNavalProduction())
				return;

			// DEMAND-DRIVEN (User 2026-08-05). The fleet used to be a standing force: the moment a
			// shipyard existed, FerryCount transports were ordered whether or not anything had ever
			// asked for a crossing. On a map where all bridges had been destroyed the bots duly built
			// shipyards -- correctly, that IS a real transit request -- but some of them sat on a
			// closed lake with no far shore to serve, so the transports were built, idled and did
			// nothing at all ("1 vessel(s) [Idle#-1], 0 ticket(s)" in the log, for the whole run).
			//
			// Transports are now ordered only while a booking is actually open. Ships already owned
			// are kept: they cost nothing to keep, and the next crossing gets a head start. Escorts
			// are inside the same gate, so an idle lake fleet no longer grows a navy around itself.
			var demand = tickets.Count(t => !t.Finished);
			if (demand == 0)
			{
				if (!noDemandLogged && vessels.Count == 0)
				{
					noDemandLogged = true;
					Log("no crossing booked -> not ordering transports");
				}

				return;
			}

			noDemandLogged = false;

			var want = Math.Max(1, ops.Info.FerryCount);
			if (vessels.Count < want)
			{
				var fromPool = ops.TakeFromPool(ops.Info.FerryTypes, want - vessels.Count);
				ops.AssignFromPool(mission, fromPool);
			}

			if (vessels.Count < want && ops.OpenRequests(mission, AotOperationsBotModule.FerryRole) == 0)
			{
				var order = Math.Min(want - vessels.Count, ops.FerryBudget());
				if (order > 0)
					ops.QueueRequest(mission, AotOperationsBotModule.FerryRole, ops.Info.FerryTypes, order);
			}

			// Escorts are strictly optional and must never hold up a crossing: they join over time out
			// of spare money (user spec 2026-07-27). Own role, so they neither block the transports'
			// request slot nor inherit their cash-reserve exemption.
			var transportsSecured = vessels.Count > 0 && vessels.Count >= want;
			var escortOrdersOpen = ops.OpenRequests(mission, AotOperationsBotModule.FerryEscortRole) > 0;
			var wantEscorts = vessels.Count * ops.Info.FerryEscortPerVessel;

			if (transportsSecured && ops.Info.FerryEscortTypes.Length > 0 && escorts.Count < wantEscorts)
			{
				var fromPool = ops.TakeFromPool(ops.Info.FerryEscortTypes, wantEscorts - escorts.Count);
				ops.AssignFromPool(mission, fromPool);
				if (escorts.Count < wantEscorts && !escortOrdersOpen)
					ops.QueueRequest(mission, AotOperationsBotModule.FerryEscortRole, ops.Info.FerryEscortTypes, wantEscorts - escorts.Count);
			}

			if (transportsSecured && !escortOrdersOpen && ops.Info.FerryEscortSecondaryTypes.Length > 0
				&& escorts.Count < wantEscorts + ops.Info.FerryEscortSecondaryCount
				&& ops.FirstBuildable(ops.Info.FerryEscortSecondaryTypes) != null)
			{
				ops.QueueRequest(mission, AotOperationsBotModule.FerryEscortRole, ops.Info.FerryEscortSecondaryTypes, 1);
			}
		}

		// Called by AotTransitMission.OnUnitAssigned.
		public bool ClaimVessel(Actor a)
		{
			if (ops.Info.FerryTypes.Contains(a.Info.Name))
			{
				if (!vessels.Any(v => v.Actor == a))
					vessels.Add(new Vessel { Actor = a });

				return true;
			}

			if (ops.Info.FerryEscortTypes.Contains(a.Info.Name) || ops.Info.FerryEscortSecondaryTypes.Contains(a.Info.Name))
			{
				if (!escorts.Contains(a))
					escorts.Add(a);

				return true;
			}

			return false;
		}

		// ---- Dispatch --------------------------------------------------------------------------

		// Vessels are assigned INDIVIDUALLY. No ship ever waits for another: that coupling is what made
		// the old convoy freeze whole-fleet whenever one ship wedged, and why traffic came in bursts
		// instead of running continuously.
		void AssignVessels(List<AotTransitTicket> queue)
		{
			// Fair share: with more than one booking open, no single one may take the whole fleet.
			// Priority decides who is served FIRST, not who is served EXCLUSIVELY -- otherwise it is a
			// charter service, not public transport. Observed 2026-07-29: a 5-tank wave held all three
			// vessels while a scout squad watched from the shore.
			var waiting = queue.Count(t => t.Waiting.Count > 0);
			var shareCap = waiting <= 1 ? int.MaxValue : Math.Max(1, vessels.Count / waiting);

			foreach (var v in vessels)
			{
				if (v.BarredTicks > 0)
					v.BarredTicks -= ops.Info.MissionInterval;

				if (v.State != VesselState.Idle || v.TicketId >= 0)
					continue;

				// Skip the booking this vessel just failed at: handing it straight back produced an
				// endless "release -> reassign -> same unreachable berth" loop.
				var t = queue.FirstOrDefault(t => t.Waiting.Count > 0
					&& !(v.BarredTicketId == t.Id && v.BarredTicks > 0)
					&& vessels.Count(o => o.TicketId == t.Id) < shareCap);

				// Everyone is at their share cap but this hull is free -- serve the top booking anyway
				// rather than leaving a ship idle while units wait.
				t ??= queue.FirstOrDefault(t => t.Waiting.Count > 0
					&& !(v.BarredTicketId == t.Id && v.BarredTicks > 0));

				if (t == null)
					continue;

				v.TicketId = t.Id;
				v.State = VesselState.ToPickup;
				v.At = t.From;
				v.Berth = t.From.ClaimBerth(v.Actor);
				v.LoadTicks = 0;
				v.ApproachFails = 0;
				t.VesselAssigned = true;
			}
		}

		// The Verfuegungsraum in action: everyone waits inland, and ONLY the group whose ship is
		// actually inbound is called forward to the boarding lane. Without this the whole booked army
		// piles onto the beach and wedges itself (User 2026-07-29).
		void StageAndCall(IBot bot, List<AotTransitTicket> queue)
		{
			foreach (var t in queue)
			{
				if (t.Hold || t.From == null)
					continue;

				// Capacity actually inbound for this ticket right now.
				var inbound = vessels.Where(v => v.TicketId == t.Id
					&& (v.State == VesselState.ToPickup || v.State == VesselState.Loading)).ToList();

				var slots = inbound.Sum(v => Capacity(v.Actor));
				var staging = t.From.StagingCentre ?? t.From.Shore;

				// Nothing coming for a while (the vessel sank, or gave the booking back): send the
				// boarding lane back to the staging ground. The grace period stops units yo-yoing in
				// the normal gap between one vessel departing and the next being assigned.
				if (inbound.Count == 0)
					t.NoInboundTicks += ops.Info.MissionInterval;
				else
					t.NoInboundTicks = 0;

				if (t.NoInboundTicks >= ops.Info.TransitRecallGraceTicks)
				{
					var recalled = 0;
					foreach (var u in t.Waiting)
						if (called.Remove(u) && !aboard.ContainsKey(u) && !boarding.ContainsKey(u))
							recalled++;

					if (recalled > 0)
						Log($"ticket #{t.Id}: no vessel inbound -> recalling {recalled} unit(s) from the boarding lane");

					t.NoInboundTicks = 0;
				}

				foreach (var u in t.Waiting.OrderBy(u => (u.Location - t.From.Shore).LengthSquared))
				{
					if (aboard.ContainsKey(u) || boarding.ContainsKey(u))
						continue;

					if (slots > 0 && !called.Contains(u))
					{
						called.Add(u);
						slots -= WeightOf(u);
					}
					else if (called.Contains(u))
						slots -= WeightOf(u);

					var goal = called.Contains(u) ? Muster(t.From, u) : staging;

					// Issue once. Re-ordering every tick replaces the running activity -- the single
					// mistake this AI has made most often.
					if (u.IsIdle && (u.Location - goal).LengthSquared > ops.Info.TransitArriveRadius2)
						bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(ops.World, goal), false));
				}
			}
		}

		// Spread the called-forward units over the boarding lane instead of stacking them on one cell.
		static CPos Muster(AotTransitStop stop, Actor u) =>
			stop.Muster.Count == 0 ? stop.Shore : stop.Muster[(int)(u.ActorID % (uint)stop.Muster.Count)];

		static int WeightOf(Actor a) => a.Info.TraitInfoOrDefault<PassengerInfo>()?.Weight ?? 1;

		// Cargo exposes no remaining-weight figure, only HasSpace(w) -- probe down from the maximum.
		// MaxWeight is a handful of units, so this is cheaper than it looks.
		static int Capacity(Actor ship)
		{
			var cargo = ship.TraitOrDefault<Cargo>();
			if (cargo == null)
				return 0;

			for (var w = cargo.Info.MaxWeight; w > 0; w--)
				if (cargo.HasSpace(w))
					return w;

			return 0;
		}

		// ---- Per-vessel state machine ----------------------------------------------------------

		void TickVessel(IBot bot, Vessel v)
		{
			// Progress watchdog per SHIP, not per fleet. A ship that is busy but getting nowhere never
			// goes idle, so nothing re-commands it; Stop makes it idle again and the IsIdle-gated logic
			// below picks it straight back up.
			// An idle vessel lying still at its home berth is doing exactly the right thing -- its
			// fingerprint never changes by design, so running the watchdog there produced a permanent
			// "stalled in Idle" that masked the real ones.
			if (v.State == VesselState.Idle)
			{
				v.StallTicks = 0;
				v.Recoveries = 0;
				TickIdle(bot, v);
				return;
			}

			var fingerprint = (int)v.State * 1000003 + v.Actor.Location.X * 7 + v.Actor.Location.Y * 13
				+ (v.Actor.TraitOrDefault<Cargo>()?.PassengerCount ?? 0) * 101;
			if (fingerprint != v.StallFingerprint)
			{
				v.StallFingerprint = fingerprint;
				v.StallTicks = 0;
				v.Recoveries = 0;
			}
			else
			{
				v.StallTicks += ops.Info.MissionInterval;
				if (v.StallTicks >= ops.Info.StallRecoveryTicks)
				{
					v.StallTicks = 0;
					v.Recoveries++;
					bot.QueueOrder(new Order("Stop", v.Actor, false));
					Log($"vessel #{v.Actor.ActorID} stalled in {v.State} (attempt {v.Recoveries}) -> breaking orders");

					// Twice is enough: hand the booking back so another ship can take it, rather than
					// letting one wedged vessel hold a ticket hostage.
					if (v.Recoveries >= 2)
					{
						v.Recoveries = 0;

						// FIRST try the other berths at this stop. A wedge is usually local: the ramp
						// is boxed in by units that landed earlier, and a berth twenty metres along the
						// coast works fine. Sailing all the way home with a full hold because one exit
						// was crowded is a wildly disproportionate answer -- and that is what happened
						// (2026-07-29: "stalled in Unloading" twice, then "aborting with 3 unit(s)
						// aboard", while ten already-landed units stood around the ramp).
						if (v.SwapLeg != v.State)
						{
							v.SwapLeg = v.State;
							v.BerthSwaps = 0;
						}

						if (v.At != null && v.BerthSwaps < ops.Info.TransitBerthSwapLimit)
						{
							v.BerthSwaps++;
							RepickBerth(v);
							return;
						}

						// Already aborting: sending it "home" again just restarts the same doomed
						// approach, so it never unloads anywhere and shuttles nobody for the rest of
						// the match (2026-07-29: a Returning vessel sat in open water with three
						// passengers aboard, re-aborting forever). Put the troops off wherever it can.
						if (v.State == VesselState.Returning)
						{
							DumpAshore(bot, v);
							return;
						}

						// Out of berths. Loaded: never just drop the booking -- the troops are inside
						// the hull and would be lost with it. Sail home and put them ashore, where they
						// go back into the ticket's waiting list and can be re-booked.
						if (Loaded(v.Actor))
						{
							Abort(v, "repeated stalls, no usable berth");
							return;
						}

						if (v.TicketId >= 0)
						{
							Log($"vessel #{v.Actor.ActorID} released ticket #{v.TicketId} after repeated stalls");
							v.BarredTicketId = v.TicketId;
							v.BarredTicks = ops.Info.TransitReassignBarTicks;
							v.TicketId = -1;
							v.At?.FreeBerth(v.Actor);
							v.At = null;
							v.State = VesselState.Idle;
							return;
						}
					}
				}
			}

			var ticket = TicketById(v.TicketId);

			// The booking died under a LOADED vessel (cancelled by its owner, timed out, everyone
			// aboard killed off the roster, or the far quay went bad). Same rule as above: bring the
			// cargo home rather than stranding it.
			if (v.State != VesselState.Returning && v.State != VesselState.Idle && Loaded(v.Actor)
				&& (ticket == null || ticket.Cancelled || ticket.Failed || ticket.To == null))
			{
				Abort(v, ticket == null ? "booking gone" : "booking cancelled");
				return;
			}

			switch (v.State)
			{
				case VesselState.Idle: TickIdle(bot, v); break;
				case VesselState.ToPickup: TickToPickup(bot, v, ticket); break;
				case VesselState.Loading: TickLoading(bot, v, ticket); break;
				case VesselState.Crossing: TickCrossing(bot, v, ticket); break;
				case VesselState.Unloading: TickUnloading(bot, v, ticket); break;
				case VesselState.Returning: TickReturning(bot, v); break;
			}
		}

		static bool Loaded(Actor ship) => (ship.TraitOrDefault<Cargo>()?.PassengerCount ?? 0) > 0;

		// Docked means ALONGSIDE LAND, not merely "near the berth".
		//
		// EnterTransport does make the passenger walk to the ship -- but it can only walk on land, so a
		// hull lying four cells out to sea is unboardable no matter how close to its berth it is. A
		// plain distance tolerance therefore reported "Loading" for ships nobody could reach, and the
		// troops trudged to the water's edge and stopped (2026-07-29: "Panzer hält auf halber Strecke
		// an", "Scout-Squad bleibt abrupt stehen"). Being on the assigned berth counts; so does any
		// other spot near this stop that genuinely touches walkable ground, which keeps three hulls
		// from having to satisfy one exact cell each in a tight bay.
		bool Docked(Vessel v)
		{
			if ((v.Actor.Location - v.Berth).LengthSquared <= ops.Info.TransitArriveRadius2)
				return true;

			if (v.At == null || (v.Actor.Location - v.At.Shore).LengthSquared > ops.Info.TransitDockRadius2)
				return false;

			return Orthogonal.Any(d => ops.Intel.IsPassable(v.Actor.Location + d));
		}

		// Hand this vessel a DIFFERENT berth. Freeing and re-claiming alone was a no-op whenever the
		// sister ships held the others: the same unreachable cell came straight back.
		void RepickBerth(Vessel v)
		{
			v.ApproachFails = 0;
			var stop = v.At;
			if (stop == null)
				return;

			var bad = v.Berth;
			stop.FreeBerth(v.Actor);

			// Learn it: a berth that defeats vessel after vessel is not bad luck, it is a bad cell.
			if (stop.ReportBerthFailure(bad, ops.Info.TransitBerthFailureLimit))
				Log($"berth {bad} struck off {(stop.Home ? "home" : "far")} stop {stop.Shore} " +
					$"after repeated failures ({stop.Berths.Count} left)");

			var next = stop.ClaimBerth(v.Actor, avoid: bad);
			if (next == bad)
				return;

			Log($"vessel #{v.Actor.ActorID} could not reach berth {bad} -> trying {next}");
			v.Berth = next;
		}

		// Turn a loaded vessel around: home quay, unload, back into circulation.
		void Abort(Vessel v, string why)
		{
			if (HomeStop == null)
				return;

			Log($"vessel #{v.Actor.ActorID} aborting with {v.Actor.TraitOrDefault<Cargo>()?.PassengerCount ?? 0} " +
				$"unit(s) aboard ({why}) -> returning them to {HomeStop.Shore}");

			v.At?.FreeBerth(v.Actor);
			v.At = HomeStop;
			v.Berth = HomeStop.ClaimBerth(v.Actor);
			v.State = VesselState.Returning;
			v.StallTicks = 0;
			v.Recoveries = 0;
		}

		// Last resort for a loaded vessel that cannot reach any berth: put the troops off wherever it
		// happens to be, as long as that is alongside walkable ground. Being stuck at sea with a full
		// hold is strictly worse than an untidy landing.
		void DumpAshore(IBot bot, Vessel v)
		{
			if (!Orthogonal.Any(d => ops.Intel.IsPassable(v.Actor.Location + d)))
			{
				// Not alongside anything -- head for the quay first.
				var stop = v.At ?? HomeStop;
				if (stop != null)
				{
					v.Berth = stop.ClaimBerth(v.Actor);
					Steer(bot, v.Actor, v.Berth);
				}

				return;
			}

			if (!v.Actor.IsIdle)
				return;

			Log($"vessel #{v.Actor.ActorID} cannot reach a berth -> unloading at {v.Actor.Location}");
			NudgeAround(v.Actor, ops.Info.TransitUnloadNudgeRadius);
			bot.QueueOrder(new Order("Unload", v.Actor, false));
		}

		void TickReturning(IBot bot, Vessel v)
		{
			var cargo = v.Actor.TraitOrDefault<Cargo>();
			if (cargo == null || cargo.PassengerCount == 0)
			{
				// Everyone is back ashore on our own side -- CreditLandings has already put them back
				// into the ticket's waiting list, so the dispatcher can try again with any vessel.
				v.TicketId = -1;
				v.State = VesselState.Idle;
				return;
			}

			if (!Docked(v))
			{
				Steer(bot, v.Actor, v.Berth);
				return;
			}

			if (v.Actor.IsIdle)
			{
				NudgeAround(v.Actor);
				bot.QueueOrder(new Order("Unload", v.Actor, false));
			}
		}

		AotTransitTicket TicketById(int id) => id < 0 ? null : tickets.FirstOrDefault(t => t.Id == id);

		// An idle vessel does NOT loiter where it last unloaded: it goes back to the home quay, which is
		// where the next booking will need it. Empty ferries left on the far shore were the single most
		// visible failure of the old system (User 2026-07-29).
		void TickIdle(IBot bot, Vessel v)
		{
			// THE SHUTTLE LOOP. A vessel that unloaded keeps its booking on purpose -- it is meant to go
			// straight back for the next load -- but nothing sent it back: AssignVessels only looks at
			// vessels with NO ticket, so a ship holding one sat idle forever. Every vessel therefore
			// made exactly one trip and then parked, which is precisely the "no continuous service"
			// the whole rebuild is for (2026-07-29: "[Idle#2 Idle#1 Loading#2]" with both tickets
			// still holding waiting units).
			var held = TicketById(v.TicketId);
			if (held != null && !held.Finished && held.Waiting.Count > 0 && held.From != null)
			{
				v.At?.FreeBerth(v.Actor);
				v.At = held.From;
				v.Berth = held.From.ClaimBerth(v.Actor);
				v.State = VesselState.ToPickup;
				v.LoadTicks = 0;
				v.ApproachFails = 0;
				return;
			}

			// Booking done or gone: back into the free pool of hulls.
			if (held == null || held.Finished || held.Waiting.Count == 0)
				v.TicketId = -1;

			// An idle vessel must be an EMPTY vessel. Nothing said so before, so a hull whose booking
			// ended while troops were still inside (timed out, or the owner gave up) simply parked at
			// the pen with them aboard: the passengers were never seen again and the vessel's hold was
			// permanently occupied, so the dispatcher kept "using" a ship that could carry nobody
			// (2026-07-29: "eine vessel idlet die ganze zeit beim pen ... infanteristen stecken noch
			// drin"). Put them ashore on our own side; the transit service re-books them from there.
			if (Loaded(v.Actor))
			{
				Log($"vessel #{v.Actor.ActorID} idle with passengers aboard -> putting them ashore");
				v.State = VesselState.Returning;
				if (HomeStop != null)
				{
					v.At?.FreeBerth(v.Actor);
					v.At = HomeStop;
					v.Berth = HomeStop.ClaimBerth(v.Actor);
				}

				return;
			}

			if (HomeStop == null)
				return;

			if (v.At != HomeStop)
			{
				v.At?.FreeBerth(v.Actor);
				v.At = HomeStop;
				v.Berth = HomeStop.ClaimBerth(v.Actor);
			}

			Steer(bot, v.Actor, v.Berth);
		}

		void TickToPickup(IBot bot, Vessel v, AotTransitTicket t)
		{
			if (t == null)
			{
				v.State = VesselState.Idle;
				return;
			}

			if (!Docked(v))
			{
				// Idle but still short of the berth means the approach failed (usually another hull in
				// the way). Try a DIFFERENT berth rather than hammering the same one -- giving the
				// booking back instead just fed the release/reassign loop.
				if (v.Actor.IsIdle && ++v.ApproachFails >= ops.Info.TransitApproachRetries)
					RepickBerth(v);

				Steer(bot, v.Actor, v.Berth);
				return;
			}

			v.ApproachFails = 0;
			v.State = VesselState.Loading;
			v.LoadTicks = 0;
		}

		void TickLoading(IBot bot, Vessel v, AotTransitTicket t)
		{
			if (t == null)
			{
				v.State = VesselState.Idle;
				return;
			}

			var cargo = v.Actor.TraitOrDefault<Cargo>();
			if (cargo == null)
			{
				v.State = VesselState.Idle;
				return;
			}

			// Drifted well off its berth (or was shoved): get back before loading anything.
			if (!Docked(v))
			{
				Steer(bot, v.Actor, v.Berth);
				return;
			}

			// The load timer only runs once somebody it is waiting for has actually reached the boarding
			// lane. Starting it on arrival at the berth meant a squad recalled from an observation post
			// 26 cells inland "timed out" before it could possibly have walked there, and the vessel
			// reported "loaded nobody" and dropped a perfectly good booking (observed 2026-07-29).
			var laneReady = t.Waiting.Any(u => called.Contains(u) && !ops.CannotOrder(u)
				&& (u.Location - v.Berth).LengthSquared <= ops.Info.TransitBoardingRadius2);

			if (laneReady || v.Actor.TraitOrDefault<Cargo>()?.PassengerCount > 0)
				v.LoadTicks += ops.Info.MissionInterval;

			NudgeAround(v.Actor);

			var reserved = 0;
			foreach (var u in t.Waiting.Where(called.Contains).ToList())
			{
				if (aboard.ContainsKey(u) || boarding.ContainsKey(u) || ops.CannotOrder(u))
					continue;

				var w = WeightOf(u);
				if (!cargo.HasSpace(reserved + w))
					break;

				bot.QueueOrder(new Order("EnterTransport", u, Target.FromActor(v.Actor), false));
				boarding[u] = v;
				reserved += w;
			}

			// Promotion to "aboard" happens centrally in TrackPassengers, against EVERY hull -- a unit
			// often boards a sister ship rather than the one it was ordered onto. All that is left here
			// is releasing an attempt that fizzled without the unit appearing in any cargo, so it can
			// be ordered again.
			foreach (var (u, ship) in boarding.ToList())
				if (ship == v && u.IsIdle && reserved == 0)
					boarding.Remove(u);

			// Two vessels serving one booking both order the SAME called-forward units; the first takes
			// everyone who fits, the second finds nobody left to order and lies there with an empty
			// hold. Its fingerprint never changes, so the stall watchdog mistook that for a wedge and
			// confiscated its booking -- after which it was barred and never used again (2026-07-29:
			// "eine vessel wurde nie benutzt während die andern beiden die ganze arbeit hatten").
			// An empty ship with nothing to load is not stuck, it is simply free.
			var eligible = t.Waiting.Any(u => called.Contains(u)
				&& !aboard.ContainsKey(u) && !boarding.ContainsKey(u) && !ops.CannotOrder(u));

			if (cargo.PassengerCount == 0 && !eligible)
			{
				v.EmptyLoadTicks += ops.Info.MissionInterval;
				if (v.EmptyLoadTicks >= ops.Info.TransitEmptyLoadTicks)
				{
					v.EmptyLoadTicks = 0;
					v.At?.FreeBerth(v.Actor);
					v.At = null;
					v.TicketId = -1;
					v.State = VesselState.Idle;
					return;
				}
			}
			else
				v.EmptyLoadTicks = 0;

			// This vessel departs on ITS OWN terms. No waiting for sister ships: continuous shuttle
			// traffic instead of one synchronised convoy that only moves when every ship is ready.
			var nothingLeftHere = !t.Waiting.Any(u => called.Contains(u) && !aboard.ContainsKey(u));
			var full = !cargo.HasSpace(1);
			if (cargo.PassengerCount > 0 && (full || nothingLeftHere || v.LoadTicks >= ops.Info.FerryLoadTimeout))
			{
				v.At?.FreeBerth(v.Actor);
				v.At = t.To;
				v.Berth = t.To.ClaimBerth(v.Actor);
				v.State = VesselState.Crossing;
				t.Started = true;
				t.IdleTicks = 0;
				Log($"vessel #{v.Actor.ActorID} departs with {cargo.PassengerCount} unit(s) for ticket #{t.Id}");
			}
			else if (cargo.PassengerCount == 0 && v.LoadTicks >= ops.Info.FerryLoadTimeout)
			{
				// Nobody turned up even though the lane was manned. Give the booking back rather than
				// blocking a berth forever -- and bar this vessel from picking the same one straight
				// back up, or the dispatcher hands it over again on the very next tick.
				Log($"vessel #{v.Actor.ActorID} loaded nobody for ticket #{t.Id} -> back to the queue");
				v.TicketId = -1;
				v.BarredTicketId = t.Id;
				v.BarredTicks = ops.Info.TransitReassignBarTicks;
				v.State = VesselState.Idle;
			}
		}

		void TickCrossing(IBot bot, Vessel v, AotTransitTicket t)
		{
			var target = t?.To ?? v.At;
			if (target == null)
			{
				v.State = VesselState.Unloading;
				return;
			}

			if (!Docked(v))
			{
				if (v.Actor.IsIdle && ++v.ApproachFails >= ops.Info.TransitApproachRetries)
					RepickBerth(v);

				Steer(bot, v.Actor, v.Berth);
				return;
			}

			v.ApproachFails = 0;
			v.State = VesselState.Unloading;
			if (t != null)
				t.IdleTicks = 0;
		}

		void TickUnloading(IBot bot, Vessel v, AotTransitTicket t)
		{
			var cargo = v.Actor.TraitOrDefault<Cargo>();
			if (cargo == null || cargo.PassengerCount == 0)
			{
				// Empty: straight back into circulation. The dispatcher will either hand it the next
				// booking or send it home -- it never idles where it happened to unload.
				v.At?.FreeBerth(v.Actor);
				v.At = null;
				v.State = VesselState.Idle;
				if (t != null && t.Waiting.Count == 0)
					v.TicketId = -1;

				return;
			}

			if (!Docked(v))
			{
				if (v.Actor.IsIdle && ++v.ApproachFails >= ops.Info.TransitApproachRetries)
					RepickBerth(v);

				Steer(bot, v.Actor, v.Berth);
				return;
			}

			// Only while idle: "Unload" starts an UnloadCargo activity, and re-issuing it every tick
			// replaces the running one, so the unload restarts forever and nobody ever steps off.
			if (v.Actor.IsIdle)
			{
				// Wider than for loading: the far ramp is where every earlier landing gathered, so one
				// ring of nudging does not open a gap for the next group to step into.
				NudgeAround(v.Actor, ops.Info.TransitUnloadNudgeRadius);
				bot.QueueOrder(new Order("Unload", v.Actor, false));
			}
		}

		// Whoever is physically inside a hull is aboard THAT hull -- no matter which one we ordered them
		// onto. EnterTransport is a request, not a guarantee: a unit will happily walk onto whichever
		// transport is closest when it gets there. Promotion used to be checked only against the vessel
		// that issued the order, so a unit that took a sister ship was never recorded as aboard; it
		// crossed, was put ashore, and nobody credited the delivery. It then stood exactly where the
		// ramp had been, with no orders and still counted as "waiting", until the whole booking timed
		// out (2026-07-29: "2 blieben an der ausstiegsstelle stehen", ticket stuck at 2/3).
		void TrackPassengers()
		{
			foreach (var v in vessels)
			{
				var cargo = v.Actor.TraitOrDefault<Cargo>();
				if (cargo == null)
					continue;

				foreach (var p in cargo.Passengers)
				{
					// Keep the carrier current either way: CreditLandings decides "delivered" vs
					// "brought back" from the carrying vessel's state.
					if (aboard.ContainsKey(p))
					{
						aboard[p] = v;
						continue;
					}

					var t = tickets.FirstOrDefault(x => !x.Finished && x.Waiting.Contains(p));
					if (t == null)
						continue;

					aboard[p] = v;
					boarding.Remove(p);
					called.Remove(p);
					t.IdleTicks = 0;
				}
			}
		}

		// A passenger that is no longer inside any vessel has landed. Credited BEFORE any pruning: a
		// unit killed on the beach still crossed, the ferry did its job.
		void CreditLandings(IBot bot)
		{
			foreach (var (u, v) in aboard.ToList())
			{
				if (v.Actor != null && !ops.IsGone(v.Actor)
					&& v.Actor.TraitOrDefault<Cargo>()?.Passengers.Contains(u) == true)
					continue;

				aboard.Remove(u);

				var t = TicketById(v.TicketId) ?? tickets.FirstOrDefault(x => x.Waiting.Contains(u));

				// Stepped off on OUR side because the vessel aborted -- that is not a delivery. The unit
				// stays in the ticket's waiting list (so another vessel picks it up) and walks back to
				// the staging ground. Crediting it as landed would strand a wave that thinks it is
				// across when it is still at home.
				if (v.State == VesselState.Returning)
				{
					called.Remove(u);
					var back = HomeStop?.StagingCentre ?? HomeStop?.Shore;
					if (back != null && !ops.CannotOrder(u))
						bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(ops.World, back.Value), false));

					continue;
				}

				// Landed after its booking already ended (it timed out, or the owner gave up while this
				// unit was still at sea). It gets no orders from anyone at that moment, so without a
				// rally here it simply stands on the far beach until its mission happens to issue its
				// next order pass -- which is why two scouts sat at the landing point for minutes
				// after the other three had moved off (User 2026-07-29).
				if (t == null)
				{
					var stranded = v.At?.StagingCentre ?? v.At?.Shore;
					if (stranded != null && !ops.CannotOrder(u))
						bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(ops.World, stranded.Value), false));

					continue;
				}

				t.Waiting.Remove(u);
				t.Delivered.Add(u);
				t.IdleTicks = 0;

				// Gather at the far staging ground. THIS is how a wave lands closed without coupling a
				// single ship: the vessels shuttle independently and the owner simply waits until its
				// ticket is fully delivered.
				var rally = t.Rally;
				if (rally != null && !ops.CannotOrder(u))
					bot.QueueOrder(new Order("AttackMove", u, Target.FromCell(ops.World, rally.Value), false));
			}
		}

		// ---- Escorts ---------------------------------------------------------------------------

		// Untouched by the ticket flow by design: escorts secure the LANDING SITE and stay there, they
		// do not shadow transports and they never gate a departure.
		void StationEscorts(IBot bot)
		{
			if (escorts.Count == 0)
				return;

			var stop = FarStops.FirstOrDefault(s => tickets.Any(t => !t.Finished && t.To == s)) ?? FarStops.FirstOrDefault();
			if (stop == null)
				return;

			foreach (var e in escorts)
			{
				if (!escortStations.TryGetValue(e, out var station))
				{
					// Never on a berth -- an escort parked there blocks the very ship it covers. Take
					// station loosely AROUND the landing point instead (user spec 2026-07-27).
					var jitter = new CVec(ops.World.LocalRandom.Next(-4, 5), ops.World.LocalRandom.Next(-4, 5));
					var c = stop.Shore + jitter;
					station = ops.World.Map.Contains(c) && !stop.Berths.Contains(c) ? c : stop.Shore;
					escortStations[e] = station;
				}

				// Generous tolerance: they hold the AREA, not a cell, so a transport nudging one aside
				// does not start a fight over the same tile.
				if (e.IsIdle && (e.Location - station).LengthSquared > 25)
					bot.QueueOrder(new Order("AttackMove", e, Target.FromCell(ops.World, station), false));
			}
		}

		// ---- Shared primitives -----------------------------------------------------------------

		// Steer only while idle -- re-issuing Move every tick cancels the running path and shoves the
		// ship out from under boarding passengers.
		static void Steer(IBot bot, Actor ship, CPos to)
		{
			if (ship.IsIdle)
				bot.QueueOrder(new Order("Move", ship, Target.FromCell(ship.World, to), false));
		}

		// Ask our own units around `ship` to step aside via the engine's own nudge path. Nothing does
		// this on its own for a transport: the engine only nudges when something PATHS THROUGH an idle
		// blocker, and a passenger stepping off a ship does not path.
		void NudgeAround(Actor ship, int radius = 1)
		{
			var blockers = new List<Actor>();
			for (var dy = -radius; dy <= radius; dy++)
				for (var dx = -radius; dx <= radius; dx++)
				{
					if (dx == 0 && dy == 0)
						continue;

					var c = ship.Location + new CVec(dx, dy);
					if (!ship.World.Map.Contains(c))
						continue;

					foreach (var a in ship.World.ActorMap.GetActorsAt(c))
					{
						// Never shove our own fleet aside: every vessel has its own reserved berth, so
						// they are not in each other's way by design. Two transports docking together
						// used to nudge each other off, and the displaced one then idled forever.
						if (a != ship && !vessels.Any(v => v.Actor == a) && !escorts.Contains(a))
							blockers.Add(a);
					}
				}

			// Issue the engine's own "Scatter" order rather than calling NotifyBlocker directly: bot code
			// may only act through orders. NotifyBlocker made the blockers queue a Nudge activity on the
			// spot, whose SharedRandom draw (Nudge -> Mobile.GetAdjacentCell) is absent when a save
			// reloads with the bots suppressed -- the same save-load desync fixed in
			// AotBaseBuilderBotModule.NudgeBlockers. Only idle units, matching what the engine's
			// INotifyBlockingMove path did.
			// Own units only: NotifyBlocker used to reach the friendly check inside the engine
			// (Mobile.OnNotifyBlockingMove ignores anything not AppearsFriendlyTo), and an order aimed at
			// a foreign actor would just be thrown away by the order validator anyway.
			foreach (var blocker in blockers)
				if (blocker.Owner == ops.Player && blocker.IsIdle && !blocker.IsDead && blocker.IsInWorld)
					ops.World.IssueOrder(new Order("Scatter", blocker, false));
		}
	}

	// The service's own mission: it exists only to own the fleet within the AI's normal production /
	// pool / claim machinery, and it never finishes -- so ships are never orphaned by a mission ending,
	// which is exactly how the old per-mission convoys stranded the whole fleet.
	public sealed class AotTransitMission : AotMission
	{
		readonly AotTransitService service;

		public AotTransitMission(AotOperationsBotModule ops, AotTransitService service)
			: base(ops, "transit")
		{
			this.service = service;
		}

		public override void OnUnitAssigned(Actor a)
		{
			// Anything that is not a ship has no business here; hand it straight back to the pool.
			if (!service.ClaimVessel(a))
			{
				Ops.ReleaseToPool(this, [a]);
				return;
			}

			base.OnUnitAssigned(a);
		}

		protected override void TickMission(IBot bot)
		{
			service.RunTraffic(bot);
		}
	}
}
