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
	public static class AotOpsUtils
	{
		public static IEnumerable<CPos> Ring(CPos centre, int r)
		{
			if (r == 0)
			{
				yield return centre;
				yield break;
			}

			for (var x = -r; x <= r; x++)
			{
				yield return new CPos(centre.X + x, centre.Y - r);
				yield return new CPos(centre.X + x, centre.Y + r);
			}

			for (var y = -r + 1; y <= r - 1; y++)
			{
				yield return new CPos(centre.X - r, centre.Y + y);
				yield return new CPos(centre.X + r, centre.Y + y);
			}
		}

		// includeAir=true is for self-defense threat detection (a Hind overhead is very much a threat to
		// an AA-capable unit standing in the base) -- every other caller wants ground-only targeting
		// (attack-move destinations, capture candidates, etc.), where "Air" was always excluded.
		public static bool IsPreferredEnemyUnit(Player player, Actor a, bool includeAir = false)
		{
			if (a == null || a.IsDead || !a.IsInWorld
				|| player.RelationshipWith(a.Owner) != PlayerRelationship.Enemy
				|| a.Info.HasTraitInfo<HuskInfo>())
				return false;

			var targetTypes = a.GetEnabledTargetTypes();
			if (targetTypes.IsEmpty || (!includeAir && targetTypes.Contains("Air")))
				return false;

			var hasModifier = false;
			foreach (var v in a.TraitsImplementing<IVisibilityModifier>())
			{
				if (v.IsVisible(a, player))
					return true;
				hasModifier = true;
			}

			return !hasModifier;
		}
	}

	public sealed class AotProductionRequest
	{
		public AotMission Mission;
		public string Role;
		public string[] Chain = [];
		public int Remaining;
		public int Ordered;
		public int LastOrderTick;
	}

	// Reported by wave/air-raid missions on completion so AotOperationsBotModule can drive the
	// escalation ladder (secondary route after N failures, air raid after more). Unknown = the
	// mission never really got going (e.g. nothing buildable) and shouldn't move the streak either way.
	public enum AotMissionOutcome { Unknown, Success, Failure }

	public abstract class AotMission
	{
		protected readonly AotOperationsBotModule Ops;
		public readonly HashSet<Actor> Units = [];
		public readonly string Name;
		public bool Done { get; protected set; }
		public AotMissionOutcome Outcome { get; protected set; } = AotMissionOutcome.Unknown;

		protected AotMission(AotOperationsBotModule ops, string name)
		{
			Ops = ops;
			Name = name;
		}

		public abstract void Tick(IBot bot);

		public virtual void OnUnitAssigned(Actor a) { Units.Add(a); }

		// May this mission absorb spare units that have been sitting in the pool doing nothing?
		// Fixed-composition missions (scouts, derrick squads, ferries) say no -- they would be thrown
		// off by arbitrary extra units.
		public virtual bool AcceptsReinforcements => false;

		// Only AotTransitMission may hold ships. A transport or escort that turns up at any other
		// mission (a production order that outlived a crossing, say) goes straight back to the shared
		// pool, where the transit service picks it up. Filing it as an ordinary combat unit strands it
		// with a mission that will never command it again -- and since the fleet is globally capped,
		// that starves every later crossing (User 2026-07-27: three transports idling on different
		// shores while waves and scouts waited in the base).
		protected bool ReturnStrayNavalSupport(Actor a)
		{
			if (!Ops.IsNavalSupport(a))
				return false;

			Ops.ReleaseToPool(this, [a]);
			return true;
		}

		int stallFingerprint;
		int stallTicks;

		// Progress watchdog. Every order this AI issues is gated on IsIdle -- deliberately, so it does
		// not cancel running activities. The blind spot is a unit that is BUSY but getting nowhere
		// (blocked path, target it cannot reach, a transport wedged against another): it never goes idle,
		// so it is never re-ordered and the whole mission quietly stops. Callers pass a cheap fingerprint
		// of "what should change if we were making progress"; when that stands still long enough this
		// returns true once, so the caller can break the deadlock (usually a Stop, which makes everything
		// idle again and lets the normal logic re-issue orders next tick).
		protected bool NoProgress(int fingerprint, int limit)
		{
			if (fingerprint != stallFingerprint)
			{
				stallFingerprint = fingerprint;
				stallTicks = 0;
				return false;
			}

			stallTicks += Ops.Info.MissionInterval;
			if (stallTicks < limit)
				return false;

			stallTicks = 0;
			return true;
		}

		protected void Log(string message)
		{
			OpenRA.Log.Write("debug", $"[AotOps][{Ops.Player.PlayerName}][{Name}] {message}");
		}

		protected void Finish()
		{
			Done = true;
			Ops.ReleaseToPool(this, Units.ToList());
			Units.Clear();
		}
	}

	[TraitLocation(SystemActors.Player)]
	[Desc("Age of Tiberium: mission-based army operations framework. Owns all ground combat",
		"units exclusively (replaces SquadManagerBotModule), produces mission compositions",
		"itself and runs the operation types as data-driven missions with a shared lifecycle.",
		"One instance per faction (Faction filter), like AotBaseBuilderBotModule.")]
	public class AotOperationsBotModuleInfo : ConditionalTraitInfo
	{
		[Desc("Only act for players of this faction (internal name).")]
		public readonly string Faction = null;

		[ActorReference]
		[Desc("Actor types that are considered construction yards.")]
		public readonly HashSet<string> ConstructionYardTypes = [];

		[ActorReference]
		[Desc("Actor types never claimed for missions (economy, MCVs, special vehicles).")]
		public readonly HashSet<string> ExcludeFromOpsTypes = [];

		[Desc("Global self-defense (User 2026-07-30/31): staged units -- pooled/idle, Module 1's chokepoint/",
			"secondary reserve while it's actually standing post, or a Derrick's permanent guard -- react",
			"to threats INCLUDING aircraft (a Hind overhead is very much a threat to an AA-capable unit",
			"standing in the base). Pooled and reserve units react to any threat within SelfDefenseRegion",
			"Radius of BaseCentre() (AND still ground-reachable), not a tiny per-unit radius; a Derrick's",
			"guard (often outside that region entirely) uses THIS radius around its own post instead.",
			"Deliberately does NOT cover anything else mid-mission (forming/moving/executing/retreating",
			"waves, scouts, air raids, a Derrick squad still in transit) -- interrupting a unit about to",
			"depart for its own mission is exactly what must NOT happen. 0 disables the whole pass.")]
		public readonly int SelfDefenseScanRadius = 8;

		[Desc("Global self-defense region (User 2026-07-31 fix): Intel.IsReachable is NOT 'the base' -- it's",
			"an UNCAPPED flood-fill of the entire walkable landmass, which on an open map can reach most of",
			"the map, including near enemy bases. Using it alone as the self-defense threat scope made every",
			"AI's reserve react to ANY enemy unit anywhere on that landmass, and in a multi-bot match every",
			"bot doing that to every other bot's units turned into map-wide AI-vs-AI free-for-alls. The base",
			"region is properly defined now: the base PLANNER's own Pocket (the actual packed base footprint",
			"it already computed and used to place every building/chokepoint) dilated by this many cells --",
			"the dilation covers the chokepoint/gate approaches just OUTSIDE the Pocket by design (defence",
			"has to cover the point of actual contact, not just the buildings behind it). Only used when a",
			"planner instance exists for this faction; see SelfDefenseRegionRadius for the fallback.")]
		public readonly int SelfDefenseRegionMargin = 12;

		[Desc("Global self-defense region FALLBACK radius (cells) around BaseCentre() -- only used for a",
			"faction with no AotBasePlannerBotModule instance yet (GDI, for now; see SelfDefenseRegionMargin",
			"for the preferred Pocket-based region once a planner exists).")]
		public readonly int SelfDefenseRegionRadius = 40;

		[Desc("Emergency defense production (User 2026-07-31): while the base is under attack (any threat",
			"detected in the self-defense region scan above) and Module 5 (Base Defense) has no emergency",
			"batch outstanding, request a batch of RocketInfantryTypes (cheap, fast, anti-everything) --",
			"exempt from the cash reserve (an active attack can't wait for cash to build back up), but",
			"otherwise queued through the normal, FAIR requests pipeline like everything else. An earlier",
			"version fired a raw StartProduction order that cut ahead of every other pending request on the",
			"same production queue -- confirmed to permanently starve Module 4's Derrick mission (its",
			"mandatory Engineer request shares that queue) for an entire match. Repeats in fresh batches for",
			"as long as the base remains under attack. 0 disables it; requires EnableBaseDefense.")]
		public readonly int EmergencyDefenseBatchSize = 5;

		// ---- Feature flags (one per operation type, individually testable) ----
		public readonly bool EnableStartingUnits = true;
		public readonly bool EnableWaves = true;
		public readonly bool EnableScouts = true;
		public readonly bool EnableDerricks = true;
		public readonly bool EnableOreBoost = true;

		// ---- Module 0: OreT Boost (User 2026-07-22) ----
		[ActorReference]
		[Desc("Ore Transporter variant chain. A single SECOND one is ordered directly (bypasses the",
			"Ops claim/production pipeline entirely -- ORET is excluded from Ops via ExcludeFromOpsTypes",
			"and manages itself, same as the free-spawned starter). One-shot: fired once, early, purely",
			"for extra cashflow at match start -- never repeated, and NOT replaced if later destroyed",
			"(that would make it a standing rule, not a one-time boost).")]
		public readonly string[] OreBoostTypes = [];

		[Desc("Startup priority (User 2026-07-22): the OreT boost, the first Derrick scan, and every",
			"Scout group (if scouting is even possible on this map) all get a head start before the",
			"first Regular Attack Wave is allowed to fire -- cashflow and reconnaissance before the",
			"first real offensive. Only gates the VERY FIRST wave; every later wave/air-raid is",
			"unaffected. Safety net: if this many ticks have passed since the match started and the",
			"gate still isn't satisfied (e.g. radar was never built), the first wave fires anyway.")]
		public readonly int StartupPriorityTimeout = 6000;

		// ---- Module 1: Starting Unit Operations (ported approved choke behaviour) ----
		[Desc("Ground units held as stationary reserve at the chokepoint (approved behaviour).")]
		public readonly int ChokepointReserveSize = 4;
		public readonly int ChokepointHoldRadius = 4;
		public readonly int ChokeClearRadius = 5;

		[ActorReference]
		[Desc("Actor types excluded from chokepoint obstacle clearing.")]
		public readonly HashSet<string> ChokeClearExcludeTypes = [];

		[Desc("Maximum ARCO raid targets after the choke is cleared.")]
		public readonly int ArcoMaxTargets = 2;

		// ---- Module 2: Regular Attack Waves ----
		[ActorReference]
		[Desc("Tank role variant chain. Must contain ALL exclusive upgrade branches;",
			"the first currently buildable variant wins.")]
		public readonly string[] WaveTankTypes = [];

		[ActorReference]
		[Desc("Light vehicle role variant chain (all exclusive branches).")]
		public readonly string[] WaveLightTypes = [];

		[ActorReference]
		[Desc("Support/artillery role variant chain (all exclusive branches).")]
		public readonly string[] WaveSupportTypes = [];

		[Desc("Role shares in percent (tank, light, support). Ignored once any WaveSlotNTypes below is",
			"configured -- see WaveSlot1-4 (User 2026-07-31).")]
		public readonly int WaveTankShare = 60;
		public readonly int WaveLightShare = 25;
		public readonly int WaveSupportShare = 15;

		// ---- Module 2b: Wave slots (User 2026-07-31) ----
		// Replaces the tank/light/support share split above with up to 4 independent, SIMULTANEOUS unit
		// slots per wave -- the old design picked exactly ONE winning variant per role via FirstBuildable,
		// so two genuinely different vehicle lines sharing a role chain (e.g. NOD's Tank role mixing TTNK
		// and LTNK) could never both appear: whichever line had an always-buildable base variant (TTNK,
		// gated only by `lite`) permanently starved the other (LTNK, `~aot-age1`-gated) out, even long
		// after the other unlocked. A slot is still an AGE-ORDERED chain internally (its own upgrade
		// variants stay mutually exclusive, e.g. aot-bike-laser over aot-bike-base), but different slots
		// are fully independent and all fill in the same wave together. Only used when at least one
		// WaveSlotNTypes is non-empty; a faction with none configured keeps the legacy share behaviour
		// above unchanged (GDI, for now).
		[ActorReference]
		[Desc("Slot 1 variant chain (age-ordered, first currently buildable wins within the slot).")]
		public readonly string[] WaveSlot1Types = [];
		[Desc("Slot 1 minimum count per age tier [0,1,2,3] -- always requested if the slot is buildable.")]
		public readonly int[] WaveSlot1Min = [];
		[Desc("Slot 1 maximum count per age tier [0,1,2,3] -- ceiling even with full budget escalation.",
			"0 for a tier disables the slot entirely that tier (e.g. still tech-locked).")]
		public readonly int[] WaveSlot1Max = [];

		[ActorReference] public readonly string[] WaveSlot2Types = [];
		public readonly int[] WaveSlot2Min = [];
		public readonly int[] WaveSlot2Max = [];

		[ActorReference] public readonly string[] WaveSlot3Types = [];
		public readonly int[] WaveSlot3Min = [];
		public readonly int[] WaveSlot3Max = [];

		[ActorReference] public readonly string[] WaveSlot4Types = [];
		public readonly int[] WaveSlot4Min = [];
		public readonly int[] WaveSlot4Max = [];

		[ActorReference] public readonly string[] WaveSlot5Types = [];
		public readonly int[] WaveSlot5Min = [];
		public readonly int[] WaveSlot5Max = [];

		[Desc("Base vehicles per wave for age tier 0, 1, 2, 3. Still used for the adaptive share below;",
			"the slot system's own Min/Max control the static composition directly instead.")]
		public readonly int[] WaveVehiclesPerAge = [6, 8, 10, 12];

		[Desc("Prerequisites that mark age tiers 1-3.")]
		public readonly string[] AgePrerequisites = ["aot-age1", "aot-age2", "aot-age3"];

		[Desc("Budget escalation per successive wave, percent.")]
		public readonly int WaveBudgetEscalationPercent = 25;

		[Desc("Budget cap as percent of the tier base budget.")]
		public readonly int WaveBudgetCapPercent = 300;

		[Desc("Hard cap on wave unit count.")]
		public readonly int WaveMaxUnits = 20;

		[Desc("Share of the wave chosen adaptively against the observed enemy army, percent.")]
		public readonly int WaveAdaptiveSharePercent = 25;

		[ActorReference]
		[Desc("Adaptive counter chains. Fall back to the role chains when empty.")]
		public readonly string[] AntiInfantryTypes = [];

		[ActorReference]
		public readonly string[] AntiTankTypes = [];

		[ActorReference]
		public readonly string[] AntiAirTypes = [];

		[Desc("Percent of waves that target economy/outposts instead of the main base.")]
		public readonly int WaveEcoTargetPercent = 25;

		[Desc("Loss percent (by unit count) at which a wave retreats. 0 = fight to the death.")]
		public readonly int WaveRetreatLossPercent = 0;

		[Desc("Ticks between waves (after the previous wave ended).")]
		public readonly int WaveCooldown = 1500;

		[Desc("Delay before the first wave is formed.")]
		public readonly int WaveInitialDelay = 3000;

		[Desc("Forming timeout; the wave launches with what it has (if at least half).")]
		public readonly int WaveFormingTimeout = 4500;

		[Desc("Executing-phase stall safety net: if a launched wave neither wipes out, retreats nor",
			"reaches/loses its target within this many ticks (e.g. stuck on an unreachable waypoint",
			"or unclearable obstacle), it is abandoned as a failure so the escalation ladder and the",
			"wave scheduler are never blocked indefinitely by a single stuck wave (User 2026-07-22).")]
		public readonly int WaveExecutingTimeout = 9000;

		[Desc("Consecutive FAILED waves (wiped out, GDI retreat, or stall timeout) at the primary route",
			"before the next wave routes via a secondary approach instead (User 2026-07-22, reduced",
			"from 2). If no distinct secondary approach exists, the wave still launches via the",
			"primary route.")]
		public readonly int WaveSecondaryRouteAfterFailures = 1;

		[Desc("Consecutive failed waves (primary + secondary route attempts) before switching to an",
			"air raid instead of another ground wave (User 2026-07-22, reduced from 3). Only triggers",
			"once HelipadTypes exists; while it doesn't, ground waves keep escalating on the same",
			"counter. Once the first air raid has fired, every later escalation decision (4th attempt",
			"onward) is instead chosen at random each time -- primary choke, secondary choke, or",
			"another air raid -- rather than following this fixed ladder again (User 2026-07-22).")]
		public readonly int WaveAirRaidAfterFailures = 2;

		[ActorReference]
		[Desc("Helipad actor types. The air-raid escalation tier only triggers once one is owned.")]
		public readonly HashSet<string> HelipadTypes = [];

		[ActorReference]
		[Desc("Repair facility (FIX) actor types. A retreating wave sends damaged survivors here to",
			"repair before releasing them back to the pool (User 2026-07-22). No effect if empty",
			"or none owned/alive.")]
		public readonly HashSet<string> RepairTypes = [];

		[ActorReference]
		[Desc("Attack helicopter variant chain (all exclusive branches) used for the air-raid",
			"escalation tier.")]
		public readonly string[] AirRaidHelicopterTypes = [];

		[Desc("Number of helicopters built for an air raid.")]
		public readonly int AirRaidCount = 4;

		[Desc("Air raid forming timeout; launches short-handed if hit.")]
		public readonly int AirRaidFormingTimeout = 9000;

		[Desc("Air raid executing-phase stall safety net; same purpose as WaveExecutingTimeout.")]
		public readonly int AirRaidExecutingTimeout = 9000;

		[ActorReference]
		[Desc("Shipyard/Sub Pen actor types. A wave only switches to naval ferrying once one of",
			"these is owned and alive.")]
		public readonly HashSet<string> NavalProductionTypes = [];

		[ActorReference]
		[Desc("Transport Vessel / Hovercraft variant chain (all exclusive branches) used to ferry",
			"a wave across water when no ground route to the enemy exists.")]
		public readonly string[] FerryTypes = [];

		[Desc("Number of transports built (and reused across waves) to ferry a wave across water.")]
		public readonly int FerryCount = 3;

		[Desc("Ticks the convoy waits for a full load before departing with what it has. Prevents one",
			"stuck unit from freezing the whole shuttle service.")]
		public readonly int FerryLoadTimeout = 900;

		[ActorReference]
		[Desc("Escort chain: one per transport. These do NOT shadow the convoy -- they take station at",
			"the landing point and hold it, securing the beachhead (user spec 2026-07-27).")]
		public readonly string[] FerryEscortTypes = [];

		[Desc("Escorts requested per transport.")]
		public readonly int FerryEscortPerVessel = 1;

		[ActorReference]
		[Desc("Heavier escort chain, added once it becomes buildable (e.g. a missile sub from Age 1).")]
		public readonly string[] FerryEscortSecondaryTypes = [];

		[Desc("How many of the heavier escort to add once buildable.")]
		public readonly int FerryEscortSecondaryCount = 1;

		[Desc("Global cap on transports across ALL ferry missions at once. FerryCount is per mission,",
			"so without this each concurrent ferrying mission builds its own fleet.")]
		public readonly int FerryMaxTotal = 3;

		// Was 14, independent of every other coastal-search radius in this AI (NavalSiteSearchRadius=20,
		// MaxBridgeLength=24) -- confirmed root cause 2026-07-22 of a wave never requesting naval
		// production despite a real, buildable coastal site existing: yard-to-approach distance on the
		// test map was 19 cells, inside NavalSiteSearchRadius (so AotBaseBuilderBotModule found and
		// logged a valid Sub Pen site) but outside FerrySearchRadius (so TryStartFerry's OWN coastal
		// search failed first with "no coastal embark/landing cell found nearby" and never even reached
		// the RequestNavalProduction() call). Raised to match MaxBridgeLength so the same coast is
		// reachable by every coastal search in this AI, not just some of them.
		[Desc("Radius in cells to search for a coastal embark/landing cell around the reference point.")]
		public readonly int FerrySearchRadius = 24;

		[Desc("Radius searched for the EMBARK shore. Much wider than FerrySearchRadius on purpose: troops",
			"walk to the coast, so the boarding beach need not be next door -- it only has to be on the",
			"ships' water and reachable over land. With the old shared radius of 24 the search saw only",
			"river banks near the base and missed the actual beach a few cells further out, reporting",
			"'no embark cell' although the fleet could have sailed there easily (User 2026-07-27).")]
		public readonly int FerryEmbarkSearchRadius = 48;

		[Desc("Locomotor name of FerryTypes (e.g. \"aot-lst\") -- used to verify the chosen embark/landing",
			"cell's adjacent water is actually ship-navigable, not just orthogonally Water-typed terrain",
			"(a rock-enclosed inlet can satisfy the terrain check but be unreachable by a real ship, leaving",
			"the ship parked a few cells short forever -- confirmed 2026-07-22: ship idle at",
			"dist2ToEmbark=5 for 14+ ticks, nobody ever boarded). Leave empty to skip the check.")]
		public readonly string FerryLocomotor = null;

		[Desc("Ferry phase timeout; proceeds with whoever made it ashore, or cancels the wave if",
			"nobody did.")]
		public readonly int FerryTimeout = 9000;

		// ---- Transit service ("oeffentlicher Nahverkehr") -- see memory/ai-transit-system.md ----
		// Stage 1: the registry below is surveyed and logged only; no traffic runs off it yet.
		[Desc("Ticks between transit stop surveys (quays, boarding lanes, staging grounds).")]
		public readonly int TransitSurveyInterval = 500;

		[Desc("Closest a staging ground (\"Verfuegungsraum\") may sit to its quay, in walking cells.",
			"This lower bound is what actually decongests the beach: everyone who is not next in",
			"line waits here instead of crowding the boarding lane.")]
		public readonly int StagingMinDistance = 6;

		[Desc("Furthest a staging ground may sit from its quay, in walking cells. Keeps calling a",
			"group forward from being a journey of its own.")]
		public readonly int StagingMaxDistance = 14;

		[Desc("Minimum contiguous free cells for a staging ground to be usable.")]
		public readonly int StagingMinCells = 12;

		[Desc("Cap on the contiguous free area measured around a staging candidate. Bounds the cost",
			"and stops one huge plain from making every candidate on it score identically.")]
		public readonly int StagingMaxCells = 80;

		[Desc("Radius in cells the staging free-area flood may spread from its centre.")]
		public readonly int StagingRadius = 6;

		[Desc("Minimum distance a HOME staging ground keeps from the base centre, so the waiting",
			"army does not squat on the base builder's plots.")]
		public readonly int StagingBaseClearance = 8;

		[Desc("Squared distance within which a unit counts as having reached its staging/boarding cell.",
			"4 == 2 cells of slack.")]
		public readonly int TransitArriveRadius2 = 4;

		[Desc("Squared distance within which a vessel counts as DOCKED at its berth. Deliberately",
			"looser than TransitArriveRadius2: EnterTransport walks the passenger to the ship and",
			"Unload puts them off wherever it lies, so exact cell precision is a demand the engine",
			"never makes -- and in a tight bay three hulls cannot all satisfy it at once.")]
		public readonly int TransitDockRadius2 = 16;

		[Desc("Minimum distance between two berths at the same stop, so vessels do not wedge each",
			"other in one corner of the bay. The spread runs ALONG the shore, never out to sea:",
			"every berth must stay alongside walkable ground or nobody can board.")]
		public readonly int TransitBerthSpacing = 3;

		[Desc("How far along the coast a stop looks for quay cells (water alongside walkable land).")]
		public readonly int TransitBerthSearchRadius = 10;

		[Desc("Ticks of waiting after which a transit ticket's effective priority rises by one step",
			"(nine steps max). Stops a scout booking starving behind a stream of attack waves.")]
		public readonly int TicketAgeBoostTicks = 1500;

		[Desc("Ticks a transit ticket may make NO progress at all before it is failed back to its",
			"owner. Any boarding or landing resets it, so a long crossing never trips it.")]
		public readonly int TicketTimeoutTicks = 9000;

		[Desc("Squared distance from a berth within which a waiting unit counts as standing in the",
			"boarding lane. The load timer only starts once somebody is actually there.")]
		public readonly int TransitBoardingRadius2 = 36;

		[Desc("Ticks with no vessel inbound before units called forward to a boarding lane are sent",
			"back to the staging ground. The grace period stops them yo-yoing in the normal gap",
			"between one vessel departing and the next being assigned.")]
		public readonly int TransitRecallGraceTicks = 500;

		[Desc("Failed approaches, counted across ALL vessels, after which a berth is struck off its",
			"stop for good. The naval flood only proves a cell is water our ships can path through,",
			"not that a hull can sit there -- a one-cell notch passes the flood and defeats every",
			"ship in practice. A stop never drops its last berth.")]
		public readonly int TransitBerthFailureLimit = 3;

		[Desc("Berths a wedged vessel tries at its current stop before giving up on the leg. A blocked",
			"ramp is usually a local problem -- another berth along the same coast works fine, and",
			"sailing home with a full hold because one exit was crowded is a wild overreaction.")]
		public readonly int TransitBerthSwapLimit = 2;

		[Desc("Nudge radius used when unloading. Wider than for loading: the far ramp is where every",
			"earlier landing gathered.")]
		public readonly int TransitUnloadNudgeRadius = 2;

		[Desc("Idle approach attempts a vessel makes at one berth before trying a different one.")]
		public readonly int TransitApproachRetries = 3;

		[Desc("Ticks a vessel is barred from re-taking a booking it just gave up on. Without this the",
			"dispatcher hands the same one straight back and the vessel retries the same bad berth.")]
		public readonly int TransitReassignBarTicks = 1000;

		[Desc("Ticks a vessel may lie docked with an empty hold and nothing left to order aboard",
			"before it gives its booking back and becomes free again. Two vessels serving one booking",
			"is normal -- the first takes everyone who fits, and the second must not be mistaken for",
			"a wedged ship and barred from further work.")]
		public readonly int TransitEmptyLoadTicks = 300;

		// ---- Module 3: Scout Expeditions ----
		[ActorReference]
		[Desc("Scout role A variant chain (all exclusive branches). When roles B/C are empty,",
			"this role alone is duplicated ScoutGroupSize times (homogeneous vehicle group).",
			"When B and/or C are set, ScoutRoleACount/B/C units of each defined role form the",
			"group instead (mixed composition, e.g. an early-tier infantry scout squad).",
			"All roles in a mixed squad MUST have the same move speed -- mixed speeds make the",
			"group spread out badly, since they still path and move together as one unit.")]
		public readonly string[] ScoutTypes = [];

		[ActorReference]
		[Desc("Scout role B variant chain. Leave empty for a homogeneous ScoutTypes group.")]
		public readonly string[] ScoutRoleBTypes = [];

		[ActorReference]
		[Desc("Scout role C variant chain. Leave empty for a homogeneous ScoutTypes group.")]
		public readonly string[] ScoutRoleCTypes = [];

		[Desc("Group size for a homogeneous ScoutTypes group (Role B/C empty).")]
		public readonly int ScoutGroupSize = 2;

		[Desc("Role A unit count when Role B and/or C are set (mixed composition).")]
		public readonly int ScoutRoleACount = 1;

		[Desc("Role B unit count when set.")]
		public readonly int ScoutRoleBCount = 1;

		[Desc("Role C unit count when set.")]
		public readonly int ScoutRoleCCount = 1;

		[Desc("Waypoint ring radius around scouted spawns.")]
		public readonly int ScoutRingRadius = 8;

		[ActorReference]
		[Desc("Radar/HQ actor types. Scout expeditions only start once one of these is owned",
			"and alive (the scout mission's whole purpose is feeding the radar/minimap).")]
		public readonly HashSet<string> RadarTypes = [];

		[ActorReference]
		[Desc("Infantry-producing building types (Barracks/Hand). Without a rally point these have",
			"multiple Exit cells and the engine picks one at RANDOM per unit, scattering freshly",
			"produced infantry across the base. A fixed rally point near the building keeps every",
			"exit consistent and holds new units there until a mission claims them.")]
		public readonly HashSet<string> InfantryRallyTypes = [];

		[Desc("Ticks between checks for infantry-producing buildings that still need a rally point.")]
		public readonly int RallyCheckInterval = 50;

		// ---- Module 4: Derrick Engineer Squads ----
		[ActorReference]
		public readonly string[] EngineerTypes = [];

		[ActorReference]
		public readonly string[] RocketInfantryTypes = [];

		[ActorReference]
		public readonly string[] MgInfantryTypes = [];

		[Desc("Ticks between scans for uncontrolled derricks (map-wide, 10 min).")]
		public readonly int DerrickCheckInterval = 15000;

		[Desc("Maximum number of derricks to capture and hold at the same time.",
			"This caps derrick TARGETS, not squads: a captured derrick keeps its slot",
			"(permanent guard) until it is lost, so at most this many derricks are ever",
			"pursued or held simultaneously.")]
		public readonly int DerrickMaxTargets = 2;

		[Desc("Guard leash radius around held positions (derricks, posts).")]
		public readonly int GuardLeashRadius = 6;

		[Desc("Last-resort timeout before a derrick squad departs short-handed. Kept generous:",
			"EngineerTypes/RocketInfantryTypes/MgInfantryTypes can share an actor type with",
			"other missions (e.g. Scout), which can genuinely delay production well past a",
			"short timeout while the shared production queue works through other requests first.")]
		public readonly int DerrickFormingTimeout = 9000;

		// ---- Module 5: Base Defense (User 2026-07-22) ----
		public readonly bool EnableBaseDefense = true;

		[Desc("Minimum garrison size guaranteed via dedicated production (the floor). The rest of",
			"the garrison, up to ProtectionTargetIsWaveSize, is opportunistically filled from the",
			"shared unit pool (idle survivors from finished missions) at no extra production cost.")]
		public readonly int ProtectionMinProduced = 3;

		[ActorReference]
		[Desc("Variant chain used for the guaranteed production floor (fast, cheap responders).",
			"Falls back to WaveLightTypes when empty.")]
		public readonly string[] ProtectionFloorTypes = [];

		[ActorReference]
		[Desc("Buildings the garrison watches over. Empty = every owned building.")]
		public readonly HashSet<string> ProtectionTypes = [];

		[Desc("Radius (cells) around each protected building that is scanned for enemies.")]
		public readonly int ProtectionScanRadius = 10;

		[Desc("Ticks between threat scans. Kept short (User 2026-07-22: react immediately, even to a",
			"single infantry attacker anywhere near the base) -- this runs a cheap circle query per",
			"protected building, not per unit, so a short interval is not expensive.")]
		public readonly int ProtectionScanInterval = 25;

		[Desc("Responders sent per detected enemy (rounded up), so a lone scout doesn't empty the",
			"whole garrison. Always at least ProtectionMinResponse, never more than available.")]
		public readonly int ProtectionResponseRatio = 2;

		[Desc("Minimum responders dispatched once any threat is detected at all.")]
		public readonly int ProtectionMinResponse = 2;

		// ---- Production ----
		[Desc("Only order production above this cash level.")]
		public readonly int ProductionMinCash = 300;

		[Desc("Ticks between production pump runs.")]
		public readonly int ProductionInterval = 30;

		[Desc("Ticks between Starport bulk delivery flushes.")]
		public readonly int StarportFlushInterval = 250;

		[Desc("Ticks between mission ticks.")]
		public readonly int MissionInterval = 25;

		[Desc("A mission that shows no measurable progress for this long is nudged out of its stuck",
			"activity (Stop), so the normal per-tick logic can re-issue its orders. Recovery, not",
			"abandonment -- the separate timeouts still decide when to give up for good.")]
		public readonly int StallRecoveryTicks = 750;

		[Desc("Ticks a unit may sit unused in the shared pool before it is folded into an active",
			"combat mission. Without this, any unit whose type no mission happens to ask for parks in",
			"the base for the rest of the match.")]
		public readonly int PoolIdleReinforceTicks = 1500;

		public override object Create(ActorInitializer init) { return new AotOperationsBotModule(init.Self, this); }
	}

	public class AotOperationsBotModule : ConditionalTrait<AotOperationsBotModuleInfo>, IBotTick, INotifyActorDisposing
	{
		public readonly World World;
		public readonly Player Player;
		public AotMapIntelBotModule Intel { get; private set; }
		public IBotChokepointProvider ChokeProvider { get; private set; }
		public IBotBaseApproachProvider ApproachProvider { get; private set; }
		AotBaseBuilderBotModule builder;
		AotBasePlannerBotModule planner;
		HashSet<CPos> selfDefenseRegionCache;

		// Stage 1 of the ferry rebuild: surveys quays/boarding lanes/staging grounds and logs them.
		// Nothing consumes it yet -- see memory/ai-transit-system.md.
		public AotTransitService Transit { get; private set; }

		public readonly List<AotMission> Missions = [];
		readonly Dictionary<Actor, AotMission> owned = [];
		readonly List<Actor> pool = [];

		// World tick each pooled unit entered the pool, so idlers can be spotted and put to use.
		readonly Dictionary<Actor, int> pooledSince = [];
		readonly List<AotProductionRequest> requests = [];
		readonly HashSet<Actor> knownUnits = [];
		readonly Predicate<Actor> unitCannotBeOrdered;
		readonly ActorIndex.NamesAndTrait<BuildingInfo> constructionYards;

		PlayerResources playerResources;
		TechTree techTree;

		bool initialClaimDone;
		int waveIndex;
		int waveCooldownTicks;
		int waveFailureStreak;

		// Module 0: OreT Boost + startup priority gate (see Info.OreBoostTypes/StartupPriorityTimeout).
		bool oreBoostDone;
		bool derrickFirstScanDone;
		int ticksSinceStart;
		int selfDefenseDiagTicks;

		// Once the fixed 3-attempt ladder (primary -> secondary -> air raid) has fired its first
		// air raid, every later escalation decision is instead chosen at random each time (User
		// 2026-07-22) -- this flips permanently and never reverts to the deterministic ladder.
		bool randomEscalationPhase;

		int derrickTicks;
		int productionTicks;
		int starportTicks;
		int missionTicks;
		int rallyCheckTicks;
		readonly HashSet<int> scoutGroupsLaunched = [];
		readonly Dictionary<int, List<CPos>> scoutAssignments = [];

		// Every concurrent ferry mission (scouts + tank waves) independently searched for the
		// nearest coastal cell to the SAME BaseCentre(), so they all converged on the exact same
		// embark cell and dock -- crowding units from unrelated missions onto one tiny landing
		// spot, blocking each other's approach to the ship (User 2026-07-23: "alle Küstenzellen
		// sind von anderen Transport-Wartegästen belegt"). Missions claim an embark cell here once
		// found and release it when their ferry finishes/fails, so FindCoastalCellNear can steer
		// later callers to a different stretch of shore instead of piling onto the same one.
		readonly HashSet<CPos> claimedEmbarkCells = [];
		public IReadOnlyCollection<CPos> ClaimedEmbarkCells => claimedEmbarkCells;
		public void ClaimEmbarkCell(CPos c) => claimedEmbarkCells.Add(c);
		public void ReleaseEmbarkCell(CPos c) => claimedEmbarkCells.Remove(c);

		public AotOperationsBotModule(Actor self, AotOperationsBotModuleInfo info)
			: base(info)
		{
			World = self.World;
			Player = self.Owner;
			unitCannotBeOrdered = a => a == null || a.Owner != Player || a.IsDead || !a.IsInWorld;
			constructionYards = new ActorIndex.NamesAndTrait<BuildingInfo>(World, info.ConstructionYardTypes);
		}

		protected override void Created(Actor self)
		{
			Intel = self.Owner.PlayerActor.TraitsImplementing<AotMapIntelBotModule>().FirstOrDefault();
			ChokeProvider = self.Owner.PlayerActor.TraitsImplementing<IBotChokepointProvider>().FirstOrDefault();
			ApproachProvider = self.Owner.PlayerActor.TraitsImplementing<IBotBaseApproachProvider>().FirstOrDefault();
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
			techTree = self.Owner.PlayerActor.Trait<TechTree>();

			// Match by faction so the right builder is picked once multiple faction instances exist
			// (same pattern AotBaseBuilderBotModule itself uses to resolve its planner).
			var builders = self.Owner.PlayerActor.TraitsImplementing<AotBaseBuilderBotModule>().ToList();
			builder = builders.FirstOrDefault(b => b.Info.Faction == Info.Faction) ?? builders.FirstOrDefault();

			// Same resolution, for the self-defense region (User 2026-07-31: reuse the planner's own
			// Pocket instead of a guessed radius). null for a faction with no planner instance yet (GDI,
			// for now) -- GlobalUnitSelfDefense falls back to SelfDefenseRegionRadius in that case.
			var planners = self.Owner.PlayerActor.TraitsImplementing<AotBasePlannerBotModule>().ToList();
			planner = planners.FirstOrDefault(p => p.Info.Faction == Info.Faction) ?? planners.FirstOrDefault();

			Transit = new AotTransitService(this);
		}

		// Any mission that needs ships/subs/vessels calls this once it knows it needs them (e.g. a wave
		// switching to naval ferrying). Sticky on the builder side: guarantees naval production exists for
		// the rest of the match, rebuilding it if lost.
		public void RequestNavalProduction() => builder?.RequestNavalProduction();

		protected override void TraitEnabled(Actor self)
		{
			waveCooldownTicks = Info.WaveInitialDelay + World.LocalRandom.Next(0, 200);
			derrickTicks = 1500 + World.LocalRandom.Next(0, 200);
			productionTicks = World.LocalRandom.Next(0, Info.ProductionInterval);
			starportTicks = World.LocalRandom.Next(0, Info.StarportFlushInterval);
			missionTicks = World.LocalRandom.Next(0, Info.MissionInterval);
			rallyCheckTicks = World.LocalRandom.Next(0, Info.RallyCheckInterval);
		}

		public CPos BaseCentre()
		{
			var yard = constructionYards.Actors.FirstOrDefault(a => a.Owner == Player && !a.IsDead && a.IsInWorld);
			return yard?.Location ?? (Intel?.Ready == true ? Intel.BaseCentre : CPos.Zero);
		}

		public int AgeTier()
		{
			for (var i = Info.AgePrerequisites.Length - 1; i >= 0; i--)
				if (techTree.HasPrerequisites([Info.AgePrerequisites[i]]))
					return i + 1;

			return 0;
		}

		public bool HasNavalProduction() =>
			World.Actors.Any(a => a.Owner == Player && !a.IsDead && a.IsInWorld && Info.NavalProductionTypes.Contains(a.Info.Name));

		// See AotBaseBuilderBotModule.NavalSite -- the water our ships actually operate in.
		public CPos? NavalSite() => builder?.NavalSite();

		public bool HasRadar() =>
			World.Actors.Any(a => a.Owner == Player && !a.IsDead && a.IsInWorld && Info.RadarTypes.Contains(a.Info.Name));

		public bool HasHelipad() =>
			World.Actors.Any(a => a.Owner == Player && !a.IsDead && a.IsInWorld && Info.HelipadTypes.Contains(a.Info.Name));

		public Actor NearestOwnRepairFacility(CPos near) =>
			World.Actors
				.Where(a => a.Owner == Player && !a.IsDead && a.IsInWorld && Info.RepairTypes.Contains(a.Info.Name))
				.MinByOrDefault(a => (a.Location - near).LengthSquared);

		// Halfway between base and primary choke: out of the builders' way, on the way out. Shared
		// (User 2026-07-22) by both regular wave staging and the base defense garrison muster point.
		public CPos GarrisonMusterPoint()
		{
			var baseCentre = BaseCentre();
			var choke = ChokeProvider?.Chokepoint;
			if (!choke.HasValue)
				return baseCentre;

			return new CPos((baseCentre.X + choke.Value.X) / 2, (baseCentre.Y + choke.Value.Y) / 2);
		}

		// User 2026-07-22: the base defense garrison holds back a full wave-composition reserve
		// once wave 1 has been scheduled (i.e. "between wave 1 and wave 2") rather than at game
		// start, since the very first wave slot already establishes that the tank chain is
		// buildable -- see AotBaseDefenseMission.
		public bool FirstWaveScheduled() => waveIndex >= 1;

		// ROOT CAUSE FOUND (debug.log evidence): stock BaseBuilderBotModule (still active on @aot for
		// its PauseUnitProduction economy service) has its OWN rally-point assignment
		// (AssignRallyPointsInterval) that re-validates every RallyPoint-trait actor the player owns
		// via IsRallyPointValid -- which calls world.IsCellBuildable on the rally cell. A cell right
		// at the production apron/smudge (exactly where we want it) essentially NEVER passes that
		// check, so BaseBuilderBotModule perpetually considered our rally point "invalid" and
		// overwrote it with its own (distant) ChooseRallyLocationNear location every cycle.
		// Real fix: BaseBuilderBotModule.cs patched with a RallyPointExcludeTypes list
		// (RallyPointExcludeTypes: InfantryRallyTypes in aot-ai.yaml) so it never touches HAND/PYLE
		// at all. With that conflict gone, we set the engine RallyPoint ourselves (once per building)
		// so fresh exits walk straight to the smudge -- and additionally cache the cell ourselves so
		// TickForming's active gathering (pool-reused stragglers, multiple missions) doesn't depend
		// on re-reading the (still generally reassignable-by-us-only) RallyPoint.Path.
		readonly Dictionary<Actor, CPos> infantryRallyCells = [];
		readonly HashSet<Actor> rallySet = [];

		void EnsureInfantryRallyPoints(IBot bot)
		{
			if (Info.InfantryRallyTypes.Count == 0)
				return;

			foreach (var stale in infantryRallyCells.Keys.Where(a => unitCannotBeOrdered(a)).ToList())
				infantryRallyCells.Remove(stale);
			rallySet.RemoveWhere(a => unitCannotBeOrdered(a));

			foreach (var building in World.Actors)
			{
				if (building.Owner != Player || building.IsDead || !building.IsInWorld
					|| !Info.InfantryRallyTypes.Contains(building.Info.Name))
					continue;

				if (!infantryRallyCells.ContainsKey(building))
				{
					var cell = FindRallyCellNear(building);
					if (cell != null)
					{
						infantryRallyCells[building] = cell.Value;
						Log($"[AotRally] cell for {building.Info.Name}@{building.Location}#{building.ActorID} -> {cell.Value}");
					}
				}

				if (infantryRallyCells.TryGetValue(building, out var rallyCell) && rallySet.Add(building))
				{
					bot.QueueOrder(new Order("SetRallyPoint", building, Target.FromCell(World, rallyCell), false));
					Log($"[AotRally] QUEUED SetRallyPoint for {building.Info.Name}@{building.Location}#{building.ActorID} -> {rallyCell}");
				}
			}
		}

		// Use the building's own highest-priority Exit cell -- verified against the actually-loaded
		// ruleset (engine/mods/cnc/rules/structures.yaml): HAND's Exit@1 has an EXPLICIT Priority:2,
		// the inherited Exit@fallback1 has none (defaults to 1) -> NOT a tie, RandomExitOrDefault
		// always picks Exit@1 deterministically even without any rally point. Reading the live
		// Exit trait also automatically reflects the mod's aot-structures.yaml ExitCell override
		// (which merges into the base Priority, not replacing it), landing exactly on the "=="
		// (OccupiedPassable) footprint row -- the actual smudge -- without us having to duplicate
		// that footprint-row math ourselves. Geometry (Dimensions.Y - 1 = the smudge row, NOT
		// Dimensions.Y) is only a defensive fallback for a building with no Exit trait at all.
		CPos? FindRallyCellNear(Actor building)
		{
			var exit = building.TraitsImplementing<Exit>()
				.Where(e => !e.IsTraitDisabled)
				.OrderByDescending(e => e.Info.Priority)
				.FirstOrDefault();

			CPos candidate;
			if (exit != null)
				candidate = building.Location + exit.Info.ExitCell;
			else
			{
				var dims = building.Info.TraitInfoOrDefault<BuildingInfo>()?.Dimensions ?? new CVec(1, 1);
				candidate = building.Location + new CVec(dims.X / 2, dims.Y - 1);
			}

			if (Intel.IsPassable(candidate))
				return candidate;

			return null;
		}

		// The rally cell of the first infantry-producing building found -- OUR OWN computed cell
		// (infantryRallyCells), never the engine RallyPoint.Path (see EnsureInfantryRallyPoints for
		// why: BaseBuilderBotModule fights over that). Missions use this to actively gather ALL their
		// units there during Forming -- not just freshly produced ones, but also POOL-REUSED units,
		// which can be standing anywhere on the map wherever an earlier mission left them.
		public CPos? PrimaryInfantryRallyCell()
		{
			foreach (var building in World.Actors)
			{
				if (building.Owner != Player || building.IsDead || !building.IsInWorld
					|| !Info.InfantryRallyTypes.Contains(building.Info.Name))
					continue;

				if (infantryRallyCells.TryGetValue(building, out var cell))
					return cell;

				return building.Location;
			}

			return null;
		}

		// Concurrent missions (e.g. 2 derrick squads + 2 scout groups at once) must NOT all target
		// the exact same rally cell -- a cell only fits ~5 infantry via subcell stacking, so with
		// more than that constantly waiting, the engine keeps bumping the overflow to a free
		// neighbour, and our own active gathering (Forming) kept sending them straight back to the
		// same single point every tick -- a permanent tug-of-war that looked like nobody ever
		// settling. Each mission gets its OWN nearby staging cell instead, allocated once and cached
		// by the mission, cycling through a small spiral so several missions stay close together
		// without competing for one cell.
		static readonly CVec[] StagingOffsets =
		[
			new(0, 0), new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
			new(1, 1), new(-1, 1), new(1, -1), new(-1, -1),
			new(2, 0), new(-2, 0), new(0, 2), new(0, -2),
		];

		int stagingCellCursor;

		public CPos? AllocateInfantryStagingCell()
		{
			var baseCell = PrimaryInfantryRallyCell();
			if (baseCell == null)
				return null;

			var offset = StagingOffsets[stagingCellCursor % StagingOffsets.Length];
			stagingCellCursor++;
			var cell = baseCell.Value + offset;
			return Intel.IsPassable(cell) ? cell : baseCell.Value;
		}

		public bool CannotOrder(Actor a) => unitCannotBeOrdered(a);

		void IBotTick.BotTick(IBot bot)
		{
			if (Info.Faction != null && Player.Faction.InternalName != Info.Faction)
				return;

			if (Intel == null || !Intel.Ready)
				return;

			CleanDead();

			if (!initialClaimDone)
			{
				InitialClaim(bot);
				initialClaimDone = true;

				if (Info.EnableBaseDefense)
					Missions.Add(new AotBaseDefenseMission(this));
			}

			ClaimNewUnits(bot);

			ticksSinceStart++;
			TryOreBoost(bot);

			if (--productionTicks <= 0)
			{
				productionTicks = Info.ProductionInterval;
				PumpProduction(bot);
			}

			if (--starportTicks <= 0)
			{
				starportTicks = Info.StarportFlushInterval;
				FlushStarport(bot);
			}

			if (--rallyCheckTicks <= 0)
			{
				rallyCheckTicks = Info.RallyCheckInterval;
				EnsureInfantryRallyPoints(bot);
			}

			Transit?.Tick();

			Schedule(bot);
			SweepIdlePool(bot);

			if (--missionTicks <= 0)
			{
				missionTicks = Info.MissionInterval;
				foreach (var m in Missions.ToList())
				{
					if (!m.Done)
						m.Tick(bot);

					if (m.Done)
					{
						// Drives the wave escalation ladder (secondary route, then air raid) --
						// only wave/air-raid missions report an Outcome; other mission types stay
						// Unknown and don't touch the streak.
						if (m is AotAirRaidMission)
						{
							Log($"escalation: air raid finished ({m.Outcome}) -> streak reset, further attacks now random (secondary/primary/air raid)");
							waveFailureStreak = 0;
						}
						else if (m.Outcome == AotMissionOutcome.Failure)
						{
							waveFailureStreak++;
							Log($"escalation: {m.Name} failed -> streak={waveFailureStreak}");
						}
						else if (m.Outcome == AotMissionOutcome.Success)
						{
							if (waveFailureStreak > 0)
								Log($"escalation: {m.Name} succeeded -> streak reset (was {waveFailureStreak})");
							waveFailureStreak = 0;
						}

						CancelRequests(m);
						Missions.Remove(m);
					}
				}

				GlobalUnitSelfDefense(bot);
			}
		}

		// Global self-defense (User 2026-07-30): "einheiten die irgendwo in der base stehen ... sollten
		// nicht abbrechen [wenn mid-mission], ausser derrick escort" -- the shared POOL (idle survivors,
		// units staged between missions) plus a Derrick's permanent guard get a reflex to fight back,
		// regardless of what they're nominally about to do next. Deliberately excludes every other
		// mission's own units (forming/moving/executing/retreating waves, scouts en route, air raids,
		// a Derrick squad still in transit) -- interrupting a unit that is about to depart for its own
		// mission is exactly what must NOT happen, per the user's follow-up. Runs right after
		// Missions.Tick() each cycle, so a ForceAttack order here overrides whatever a mission just
		// decided for that unit -- no "paused task" bookkeeping needed: the instant no threat remains,
		// the owning system's normal order resumes on its own next cycle. Pool candidates are restricted
		// to the flooded base region (Intel.IsReachable) per the user's own framing ("die gesamte
		// geflutete base-region"); a Derrick's guard is exempt from that check since it stands watch
		// somewhere else entirely by definition. includeAir=true on the threat scan is the actual fix for
		// the reported symptom (Hinds raiding undisturbed despite AA units standing around) -- the normal
		// IsPreferredEnemyUnit excludes aircraft everywhere else in this file, which would otherwise make
		// this pass blind to the exact threat it exists to answer.
		// The actual base region for global self-defense (User 2026-07-31: "der base-planner flutet doch
		// am anfang den bau-bereich und entscheidet, wo chockepoint ist ... ich spreche von diesem
		// gebiet"). Reuses the planner's own Pocket -- the exact packed base footprint it already
		// computed -- dilated by SelfDefenseRegionMargin cells so the chokepoint/gate approaches just
		// OUTSIDE the Pocket (DetectGates deliberately blocks a patch around every gate so the packer
		// doesn't spill past it) are covered too; defence has to cover the point of actual contact, not
		// just the buildings behind it. The dilated set is computed once and cached: the Pocket itself
		// never changes after Plan() runs, so recomputing every tick would be pure waste. Falls back to a
		// plain radius around BaseCentre() for a faction with no planner instance yet (GDI, for now).
		bool InBaseRegion(CPos c)
		{
			if (!Intel.IsReachable(c))
				return false;

			if (planner == null || planner.Pocket.Count == 0)
			{
				var baseCentre = BaseCentre();
				return (c - baseCentre).LengthSquared <= Info.SelfDefenseRegionRadius * Info.SelfDefenseRegionRadius;
			}

			selfDefenseRegionCache ??= DilateCells(planner.Pocket, Info.SelfDefenseRegionMargin);
			return selfDefenseRegionCache.Contains(c);
		}

		// Multi-source BFS outward from every seed cell, `steps` rings deep -- cheap way to "grow" an
		// arbitrary cell set by a fixed margin without checking every cell's distance to every seed.
		static HashSet<CPos> DilateCells(HashSet<CPos> seeds, int steps)
		{
			var region = new HashSet<CPos>(seeds);
			var frontier = new List<CPos>(seeds);
			for (var i = 0; i < steps; i++)
			{
				var next = new List<CPos>();
				foreach (var c in frontier)
					foreach (var d in CVec.Directions)
					{
						var n = c + d;
						if (region.Add(n))
							next.Add(n);
					}

				frontier = next;
			}

			return region;
		}

		void GlobalUnitSelfDefense(IBot bot)
		{
			if (Info.SelfDefenseScanRadius <= 0)
				return;

			// Pool + Module 1's standing reserve: react to ANY threat within the base REGION (User
			// 2026-07-30: "die gesamte geflutete base-region sollte immer ... als schützenswert gelten"),
			// not just a small radius around each individual unit -- a per-unit radius meant an idle unit
			// standing even a little way from the actual breach never reacted at all (User 2026-07-31
			// report: infantry stood doing nothing while the base was under attack elsewhere). Confirmed
			// via live log that the pool is essentially ALWAYS empty (every combat unit is permanently
			// claimed by some mission) -- the chokepoint/secondary reserve, previously excluded as
			// "mid-mission", turned out to BE the units standing idle in every report; the user explicitly
			// asked to include it too. Reuses Module 5's own proportional-response knobs
			// (ProtectionResponseRatio/MinResponse) so a single scout doesn't empty the whole reserve.
			//
			// IMPORTANT (User 2026-07-31 follow-up: "die defense logik scheint die ganze map zu flooten,
			// alle gegner fangen direkt an übereinander herzufallen"): Intel.IsReachable is NOT "the base"
			// -- it's an uncapped flood-fill of the ENTIRE walkable landmass (AotMapIntelBotModule.
			// RefreshReachability has no distance/budget cap at all, unlike the base planner's own Pocket).
			// Using it alone as the threat scope meant this reacted to any enemy anywhere on that landmass,
			// and in a multi-bot match every bot's reserve doing that to every OTHER bot's units the same
			// way turned into map-wide AI-vs-AI free-for-alls. Both candidates and threats are now also
			// bounded to InBaseRegion (still reachability-gated on top, so nobody's ordered to swim).
			var poolCandidates = pool.Where(a => !CannotOrder(a) && InBaseRegion(a.Location)).ToList();
			foreach (var s in Missions.OfType<AotStartingUnitsMission>())
				poolCandidates.AddRange(s.ReserveUnits().Where(a => InBaseRegion(a.Location)));

			var threats = World.Actors
				.Where(e => AotOpsUtils.IsPreferredEnemyUnit(Player, e, true) && e.CanBeViewedByPlayer(Player) && InBaseRegion(e.Location))
				.ToList();

			// Throttled diagnostic (User 2026-07-31 report: "infantry stand around doing nothing"):
			// without this, the log gives no way to tell whether the candidate set is genuinely empty
			// (units are all mid-mission in some OTHER way -- a wave forming, a scout en route) versus
			// non-empty but somehow not reacting.
			if (++selfDefenseDiagTicks % 8 == 0)
				Log($"self-defense diag: pool={pool.Count} reachableCandidates={poolCandidates.Count} threats={threats.Count}");

			if (poolCandidates.Count > 0 && threats.Count > 0)
			{
				// Math.Clamp(value, min, max) throws ArgumentException when min > max -- crashed in-game
				// (User 2026-07-31) the moment poolCandidates.Count dropped below ProtectionMinResponse
				// (e.g. only 1 candidate left but the configured floor is 2). Never valid to ask for more
				// responders than actually exist, so cap with Min() first instead of trusting Clamp's own
				// bounds to always be ordered.
				var want = Math.Min(poolCandidates.Count, Math.Max(Info.ProtectionMinResponse, threats.Count * Info.ProtectionResponseRatio));
				var responders = poolCandidates
					.OrderBy(a => threats.Min(t => (a.Location - t.Location).LengthSquared))
					.Take(want)
					.ToList();

				foreach (var a in responders)
				{
					var target = threats.OrderBy(t => (a.Location - t.Location).LengthSquared).First();
					bot.QueueOrder(new Order("ForceAttack", a, Target.FromActor(target), false));
				}

				Log($"self-defense: {threats.Count} threat(s) in the base region -> dispatching {responders.Count}/{poolCandidates.Count} pooled unit(s)");
			}

			// Emergency defense production (User 2026-07-31: "wenn der gegner in eine base einfällt ...
			// sollte er sofort anfangen rocket trooper zu bauen in 5er wellen solange gegner noch in base
			// sind"). Checked against the SAME region-wide threat set as above, independent of whether any
			// responder was actually available this cycle -- the base can be under attack with the
			// reserve already fully committed, which is exactly when reinforcements matter most.
			if (threats.Count > 0)
				TryEmergencyDefenseProduction(bot);

			// Derrick guard: local radius around its OWN post instead -- a captured derrick often sits
			// far outside the base region entirely, so the region-wide check above would never cover it.
			foreach (var d in Missions.OfType<AotDerrickMission>())
			{
				foreach (var a in d.HoldingEscorts())
				{
					var threat = World.FindActorsInCircle(a.CenterPosition, WDist.FromCells(Info.SelfDefenseScanRadius))
						.Where(e => AotOpsUtils.IsPreferredEnemyUnit(Player, e, true) && e.CanBeViewedByPlayer(Player))
						.OrderBy(e => (e.Location - a.Location).LengthSquared)
						.FirstOrDefault();
					if (threat == null)
						continue;

					bot.QueueOrder(new Order("ForceAttack", a, Target.FromActor(threat), false));
					Log($"self-defense: derrick guard engaging {threat.Info.Name}@{threat.Location}");
				}
			}
		}

		// "Really gone", as opposed to CannotOrder's "cannot be given an order right now".
		//
		// A passenger riding a transport is alive but NOT in the world, so CannotOrder is true for it.
		// Using that to prune mission rosters silently deleted every unit the moment it boarded a ferry:
		// the mission then saw an empty roster, concluded the group had been wiped out and cancelled
		// itself mid-crossing -- the "embark but never disembark" behaviour (User 2026-07-24), and the
		// reason ferrying looked so erratic. Only death (or changing owner) removes a unit here.
		public bool IsGone(Actor a) => a == null || a.Owner != Player || a.IsDead;

		void CleanDead()
		{
			foreach (var m in Missions)
				m.Units.RemoveWhere(IsGone);
			pool.RemoveAll(a => IsGone(a));
			foreach (var gone in pooledSince.Keys.Where(IsGone).ToList())
				pooledSince.Remove(gone);
			knownUnits.RemoveWhere(IsGone);
			foreach (var dead in owned.Keys.Where(IsGone).ToList())
				owned.Remove(dead);
		}

		bool IsEligibleCombatUnit(Actor a)
		{
			if (a.Owner != Player || a.IsDead || !a.IsInWorld)
				return false;

			if (Info.ExcludeFromOpsTypes.Contains(a.Info.Name))
				return false;

			// Ground/naval combat units: mobile, not a harvester. Aircraft (Mobile == null) are
			// claimable too -- AotAirRaidMission requests helicopters -- but only via a genuine
			// pending request; see the AircraftInfo branch below.
			if (a.Info.HasTraitInfo<HarvesterInfo>())
				return false;

			if (a.TraitOrDefault<Mobile>() == null && !a.Info.HasTraitInfo<AircraftInfo>())
				return false;

			return true;
		}

		void InitialClaim(IBot bot)
		{
			var starting = Info.EnableStartingUnits ? new AotStartingUnitsMission(this) : null;
			if (starting != null)
				Missions.Add(starting);

			foreach (var a in World.ActorsHavingTrait<IPositionable>().Where(IsEligibleCombatUnit).ToList())
			{
				knownUnits.Add(a);
				if (starting != null)
				{
					owned[a] = starting;
					starting.OnUnitAssigned(a);
				}
				else
					PoolAdd(a);
			}

			Log($"initial claim: {(starting != null ? starting.Units.Count : pool.Count)} unit(s)");
		}

		void ClaimNewUnits(IBot bot)
		{
			foreach (var a in World.ActorsHavingTrait<IPositionable>()
				.Where(a => a.Owner == Player && !knownUnits.Contains(a)).ToList())
			{
				knownUnits.Add(a);
				if (!IsEligibleCombatUnit(a))
					continue;

				// Multiple concurrent missions can share an actor type (e.g. Scout's Role A and
				// Derrick's MG escort both use the same infantry). Prefer a request that actually
				// has a production order in flight (Ordered > 0) over one that's merely stalled
				// waiting its turn on a busy queue -- otherwise a stalled mission "steals" a unit
				// another mission's order actually paid for, and the stolen unit immediately walks
				// off toward the stalled mission's (unrelated) destination the moment it exits.
				var request = requests
					.Where(r => r.Remaining > 0 && r.Chain.Contains(a.Info.Name))
					.OrderByDescending(r => r.Ordered > 0)
					.FirstOrDefault();
				if (request != null)
				{
					request.Remaining--;
					if (request.Ordered > 0)
						request.Ordered--;
					owned[a] = request.Mission;
					request.Mission.OnUnitAssigned(a);
					Log($"claim {a.Info.Name}@{a.Location} -> {request.Mission.Name} ({request.Role}, {request.Remaining} open)");
				}
				else
				{
					PoolAdd(a);
					Log($"claim {a.Info.Name}@{a.Location} -> pool ({pool.Count})");
				}
			}

			requests.RemoveAll(r => r.Remaining <= 0);
		}

		// ---- Production -----------------------------------------------------

		// Role tag for naval ferry transports -- exempt from the production cash reserve, see PumpProduction.
		public const string FerryRole = "ferry";

		// Escorts are a SEPARATE role on purpose. They used to share FerryRole, which meant they blocked
		// the transports' request slot and inherited their cash-reserve exemption -- so a sub could be
		// paid for before the transport that actually unblocks a crossing. Escorts are optional: they
		// join over time when there is spare money (user spec 2026-07-27, "nicht auf den bau der uboote
		// warten bis er los legt").
		public const string FerryEscortRole = "ferry-escort";

		// Role tag for emergency defense production (see TryEmergencyDefenseProduction) -- exempt from
		// the cash reserve for the same reason as FerryRole (an active attack can't wait for cash to
		// build back up), but goes through the normal requests queue instead of a raw StartProduction
		// order like it originally did (User 2026-07-31: that bypassed EVERY other request's priority
		// entirely, cutting to the front of the shared Infantry queue on every single attack and
		// permanently starving longer-standing requests sharing that queue -- confirmed via log: 13
		// batches of 5 fired this match alone, and the Derrick mission's mandatory Engineer request,
		// stuck behind them the whole time, was claimed ZERO times all session).
		public const string EmergencyDefenseRole = "emergency-defense";

		public void QueueRequest(AotMission mission, string role, string[] chain, int count)
		{
			if (count <= 0 || chain.Length == 0)
				return;

			requests.Add(new AotProductionRequest { Mission = mission, Role = role, Chain = chain, Remaining = count });
		}

		public void CancelRequests(AotMission mission)
		{
			requests.RemoveAll(r => r.Mission == mission);
		}

		// Drop a mission's OUTSTANDING transport/escort orders. A ferry is released as soon as its
		// crossing is done, but its production requests kept running -- the ships then arrived at a
		// mission that no longer had a ferry, were filed as ordinary combat units and never reached the
		// pool again. With FerryMaxTotal exhausted, every later mission waited forever (confirmed
		// 2026-07-27: a finished derrick squad held all three transports while a wave sat at ships=0).
		public void CancelFerryRequests(AotMission mission)
		{
			requests.RemoveAll(r => r.Mission == mission && (r.Role == FerryRole || r.Role == FerryEscortRole));
		}

		// Every naval asset still booked to this mission, whether or not the mission still tracks it.
		// A safety net against leaks: anything that slipped out of a convoy's own lists would otherwise
		// stay owned forever and never reach the pool again.
		public void ReleaseAllNavalSupport(AotMission mission)
		{
			var strays = owned.Where(kv => kv.Value == mission && IsNavalSupport(kv.Key))
				.Select(kv => kv.Key)
				.ToList();
			if (strays.Count > 0)
				ReleaseToPool(mission, strays);
		}

		// Is this a convoy asset (transport or escort) rather than a fighting unit?
		public bool IsNavalSupport(Actor a) =>
			Info.FerryTypes.Contains(a.Info.Name)
			|| Info.FerryEscortTypes.Contains(a.Info.Name)
			|| Info.FerryEscortSecondaryTypes.Contains(a.Info.Name);

		public int OpenRequests(AotMission mission)
		{
			return requests.Where(r => r.Mission == mission).Sum(r => r.Remaining);
		}

		public int OpenRequests(AotMission mission, string role)
		{
			return requests.Where(r => r.Mission == mission && r.Role == role).Sum(r => r.Remaining);
		}

		// How many more transports may be produced right now, across the WHOLE AI.
		//
		// FerryCount is per mission, so with several ferry missions running at once (a derrick squad,
		// an attack wave and two scout groups all needing to cross) each independently asked for its
		// own FerryCount and the AI ended up building far more ships than intended -- confirmed
		// 2026-07-24: FerryCount=2 but four transports produced, most of them idling. This caps the
		// fleet globally; missions share the surplus through the pool instead of each building its own.
		public int FerryBudget()
		{
			var queued = requests.Where(r => r.Role == FerryRole).Sum(r => r.Remaining);
			return Math.Max(0, Info.FerryMaxTotal - OwnedFerryCount() - queued);
		}

		// Transports the AI owns right now, no matter which mission is using them. A mission that
		// currently has none must NOT conclude that ferrying is impossible while these exist -- they
		// are shared through the pool and become available again after each crossing.
		public int OwnedFerryCount() =>
			World.Actors.Count(a => a.Owner == Player && !a.IsDead && a.IsInWorld
				&& Info.FerryTypes.Contains(a.Info.Name));

		// Up to 5 independent wave slots (User 2026-07-31, see WaveSlot1-5 above). Empty ones (no Types
		// configured) are skipped -- a faction with none defined falls back to the legacy tank/light/
		// support share split in AotRegularWaveMission.Compose (GDI, for now).
		public List<(string Name, string[] Chain, int[] Min, int[] Max)> WaveSlots()
		{
			var list = new List<(string, string[], int[], int[])>();
			void Add(string name, string[] types, int[] min, int[] max)
			{
				if (types.Length > 0)
					list.Add((name, types, min, max));
			}

			Add("slot1", Info.WaveSlot1Types, Info.WaveSlot1Min, Info.WaveSlot1Max);
			Add("slot2", Info.WaveSlot2Types, Info.WaveSlot2Min, Info.WaveSlot2Max);
			Add("slot3", Info.WaveSlot3Types, Info.WaveSlot3Min, Info.WaveSlot3Max);
			Add("slot4", Info.WaveSlot4Types, Info.WaveSlot4Min, Info.WaveSlot4Max);
			Add("slot5", Info.WaveSlot5Types, Info.WaveSlot5Min, Info.WaveSlot5Max);
			return list;
		}

		public string FirstBuildable(string[] chain)
		{
			var queuesByCategory = AIUtils.FindQueuesByCategory(Player);
			return FirstBuildable(chain, queuesByCategory).Name;
		}

		// Module 0: OreT Boost -- a single one-time extra Ore Transporter for early cashflow (User
		// 2026-07-22). Fires a bare StartProduction order directly, bypassing the Ops claim/request
		// pipeline entirely: ORET is excluded from Ops via ExcludeFromOpsTypes (it must never be
		// commandeered as a combat unit) and manages itself once built, exactly like the free-spawned
		// starter ORET. Retries every tick until buildable (e.g. waits for the Light Factory), then never
		// fires again -- and if this bonus ORET is later destroyed, it is NOT replaced (that would make
		// it a standing rule instead of a one-time boost).
		void TryOreBoost(IBot bot)
		{
			if (oreBoostDone || !Info.EnableOreBoost || Info.OreBoostTypes.Length == 0)
				return;

			var queuesByCategory = AIUtils.FindQueuesByCategory(Player);
			var (name, queue) = FirstBuildable(Info.OreBoostTypes, queuesByCategory);
			if (name == null || queue == null)
				return;

			bot.QueueOrder(Order.StartProduction(queue.Actor, name, 1));
			oreBoostDone = true;
			Log($"ore boost: extra {name} ordered (one-time, early cashflow)");
		}

		// Emergency defense production (User 2026-07-31): the base is under attack, so rush a batch of
		// RocketInfantryTypes.
		//
		// FIX (User 2026-07-31 follow-up, "seit wir die rocket für defense rein genommen haben ... tauchte
		// er nicht auf"): this originally fired a bare StartProduction order directly (same pattern as
		// TryOreBoost), bypassing PumpProduction's whole requests queue -- which meant it cut to the FRONT
		// of the shared Infantry queue on every single attack, ahead of every already-pending request that
		// happened to share it. Confirmed via log: 13 batches of 5 fired in one match, and Module 4's
		// Derrick mission -- whose mandatory Engineer request (EngineerTypes) shares that exact queue --
		// never got claimed even ONCE all session, permanently stuck in Forming as a direct result. Now
		// goes through the normal QueueRequest/PumpProduction pipeline like everything else (still exempt
		// from the cash reserve via EmergencyDefenseRole, since an active attack can't wait for cash to
		// build back up), so it takes its turn in the SAME fair, insertion-order queue as any other
		// mission's request instead of always winning outright. Owned by Module 5 (Base Defense) if
		// enabled -- the natural home for "extra defenders"; its own ScanAndRespond/MaintainGarrison
		// already knows how to use them. Only queues a fresh batch once its own emergency requests are
		// fully spent (OpenRequests == 0), so it naturally repeats in batches for as long as
		// GlobalUnitSelfDefense keeps calling it (i.e. for as long as the base stays under attack).
		void TryEmergencyDefenseProduction(IBot bot)
		{
			if (Info.EmergencyDefenseBatchSize <= 0 || Info.RocketInfantryTypes.Length == 0)
				return;

			var garrison = Missions.OfType<AotBaseDefenseMission>().FirstOrDefault();
			if (garrison == null)
				return;

			if (OpenRequests(garrison, EmergencyDefenseRole) > 0)
				return;

			QueueRequest(garrison, EmergencyDefenseRole, Info.RocketInfantryTypes, Info.EmergencyDefenseBatchSize);
			Log($"emergency defense: base under attack -> requesting {Info.EmergencyDefenseBatchSize}x reinforcements");
		}

		// Startup priority gate for the first Regular Attack Wave (User 2026-07-22): cashflow (OreT
		// boost) and reconnaissance (first Derrick scan, every Scout group) get a head start. Vacuously
		// satisfied for whichever of these is disabled or inapplicable (e.g. a single-spawn map has no
		// scouting to wait for), so this can never gate on something that will never happen. The
		// StartupPriorityTimeout safety net additionally guarantees this can never deadlock the whole
		// offensive (e.g. radar destroyed before ever being built).
		bool StartupPriorityMet()
		{
			if (ticksSinceStart >= Info.StartupPriorityTimeout)
				return true;

			var oreBoostSatisfied = !Info.EnableOreBoost || Info.OreBoostTypes.Length == 0 || oreBoostDone;
			var derricksSatisfied = !Info.EnableDerricks || derrickFirstScanDone;
			var scoutsSatisfied = !Info.EnableScouts || Intel.AllSpawns.Count <= 1
				|| (scoutAssignments.Count > 0 && scoutGroupsLaunched.Count >= scoutAssignments.Count);

			return oreBoostSatisfied && derricksSatisfied && scoutsSatisfied;
		}

		(string Name, ProductionQueue Queue) FirstBuildable(string[] chain, ILookup<string, ProductionQueue> queuesByCategory)
		{
			foreach (var name in chain)
			{
				if (!World.Map.Rules.Actors.TryGetValue(name, out var actorInfo))
					continue;

				var bi = actorInfo.TraitInfoOrDefault<BuildableInfo>();
				if (bi == null)
					continue;

				foreach (var category in bi.Queue)
				{
					foreach (var queue in queuesByCategory[category])
					{
						if (queue.BuildableItems().Any(i => i.Name == name))
							return (name, queue);
					}
				}
			}

			return (null, null);
		}

		void PumpProduction(IBot bot)
		{
			if (requests.Count == 0)
				return;

			// Army production PAUSES ENTIRELY while the Age-1 Refinery is still being built (user spec
			// 2026-07-31): observed the AI put up its Age-1 Airfield but never the Refinery behind it,
			// because unit production kept winning the cash race against the Rhythm builder tick after
			// tick. Deliberately no FerryRole exemption here (unlike the reserve check below): that
			// exemption exists because cash can sit under the reserve INDEFINITELY on a poor spawn,
			// permanently stalling a mission -- this pause is bounded by "until the Refinery is built",
			// which is the very thing it exists to speed up, so a mission's transport waiting through it
			// is a short, deliberate delay, not a deadlock. `requests` keeps accumulating as normal;
			// this only withholds the StartProduction orders that actually spend cash, so everything
			// fires the instant the Refinery completes.
			if (builder != null && builder.Info.Faction == Info.Faction && builder.Age1RefineryPending())
				return;

			// The cash reserve keeps the AI from spending its last credits on ordinary units. It must
			// NOT apply to a ferry transport: an entire mission is blocked waiting for that one ship,
			// and on a poor spawn (no land route to the derricks) cash+ore can sit under the reserve
			// indefinitely -- confirmed 2026-07-24: cash=0/ore~250 for the whole match, "transports
			// requested" logged repeatedly, yet `produce aot-transport` never fired once and every
			// ferry timed out. Everything else still respects the reserve (User spec).
			var reserveMet = playerResources.GetCashAndResources() >= Info.ProductionMinCash;

			var queuesByCategory = AIUtils.FindQueuesByCategory(Player);
			var usedQueues = new HashSet<ProductionQueue>();

			foreach (var request in requests)
			{
				if (!reserveMet && request.Role != FerryRole && request.Role != EmergencyDefenseRole)
					continue;

				// Reconcile leaked orders: nothing of this chain is anywhere in production anymore.
				if (request.Ordered > 0 && World.WorldTick > request.LastOrderTick + 1500)
				{
					var stillProducing = queuesByCategory.SelectMany(g => g)
						.Any(q => q.AllQueued().Any(i => request.Chain.Contains(i.Item)));
					if (!stillProducing)
						request.Ordered = 0;
				}

				while (request.Ordered < request.Remaining)
				{
					var (name, queue) = FirstBuildable(request.Chain, queuesByCategory);
					if (name == null || queue == null || usedQueues.Contains(queue))
						break;

					// Keep regular queues at one outstanding item; bulk queues take a batch.
					if (queue is not BulkProductionQueue && queue.AllQueued().Any())
					{
						usedQueues.Add(queue);
						break;
					}

					bot.QueueOrder(Order.StartProduction(queue.Actor, name, 1));
					request.Ordered++;
					request.LastOrderTick = World.WorldTick;
					Log($"produce {name} for {request.Mission.Name} ({request.Role})");

					if (queue is not BulkProductionQueue)
					{
						usedQueues.Add(queue);
						break;
					}
				}
			}
		}

		void FlushStarport(IBot bot)
		{
			foreach (var queue in Player.PlayerActor.TraitsImplementing<BulkProductionQueue>())
			{
				if (queue.GetActorsReadyForDelivery().Count > 0 && !queue.HasDeliveryStarted())
				{
					bot.QueueOrder(new Order("PurchaseOrder", queue.Actor, false));
					Log("starport flush: delivery ordered");
				}
			}
		}

		// ---- Pool -----------------------------------------------------------

		void PoolAdd(Actor a)
		{
			pool.Add(a);
			pooledSince[a] = World.WorldTick;
		}

		void PoolRemove(Actor a)
		{
			pool.Remove(a);
			pooledSince.Remove(a);
		}

		// Units nobody asked for pile up in the pool and would otherwise stand around in the base for
		// the rest of the match (User 2026-07-25: "zieht er parkende einheiten nicht nach"). TakeFromPool
		// only matches a mission's own type chain, and base defense stops drawing once its garrison
		// target is met -- so any leftover type never gets picked up again. Fold long-idle units into an
		// active combat mission instead.
		void SweepIdlePool(IBot bot)
		{
			if (Info.PoolIdleReinforceTicks <= 0 || pool.Count == 0)
				return;

			var stale = pool.Where(a => !unitCannotBeOrdered(a)
				&& pooledSince.TryGetValue(a, out var since)
				&& World.WorldTick - since >= Info.PoolIdleReinforceTicks).ToList();
			if (stale.Count == 0)
				return;

			// Prefer an actual attack mission (gets them into the fight); fall back to the garrison.
			var target = Missions.FirstOrDefault(m => !m.Done && m.AcceptsReinforcements && m is AotRegularWaveMission)
				?? Missions.FirstOrDefault(m => !m.Done && m.AcceptsReinforcements);
			if (target == null)
				return;

			foreach (var a in stale)
				PoolRemove(a);

			AssignFromPool(target, stale);
			Log($"pool sweep: {stale.Count} idle unit(s) reinforced {target.Name}");
		}

		public void ReleaseToPool(AotMission mission, List<Actor> units)
		{
			foreach (var a in units)
			{
				if (unitCannotBeOrdered(a))
					continue;
				owned.Remove(a);
				PoolAdd(a);
			}
		}

		public List<Actor> TakeFromPool(string[] chain, int count)
		{
			var taken = pool.Where(a => chain.Contains(a.Info.Name)).Take(count).ToList();
			foreach (var a in taken)
				PoolRemove(a);
			return taken;
		}

		// Base Defense (User 2026-07-22): opportunistically adopts ANY idle pool unit regardless of
		// type, unlike TakeFromPool's chain filter -- the garrison isn't picky about composition,
		// it just wants bodies that are otherwise sitting around doing nothing.
		public List<Actor> TakeAnyFromPool(int count)
		{
			var taken = pool.Take(count).ToList();
			foreach (var a in taken)
				PoolRemove(a);
			return taken;
		}

		public void AssignFromPool(AotMission mission, List<Actor> units)
		{
			foreach (var a in units)
			{
				owned[a] = mission;
				mission.OnUnitAssigned(a);
			}
		}

		// ---- Scheduler ------------------------------------------------------

		void Schedule(IBot bot)
		{
			// Module 2: regular attack waves, with an escalation ladder (User 2026-07-22, reduced):
			// attempt 1 = primary choke; if it fails, attempt 2 = secondary choke (Wave-
			// SecondaryRouteAfterFailures=1); if that also fails, attempt 3 = an air raid instead of
			// a ground wave (WaveAirRaidAfterFailures=2, only once a helipad exists). From then on
			// (attempt 4 onward) the fixed ladder is abandoned for good -- randomEscalationPhase
			// picks, each time, at random between a ground wave via a random choke and another air
			// raid, so the enemy stops being predictable once it has escalated once already.
			// Startup priority (User 2026-07-22): only the very FIRST wave waits on this -- every later
			// wave/air-raid (waveIndex > 0) is unaffected. OreT boost + first Derrick scan + every Scout
			// group (cashflow and reconnaissance) get a head start before the first real offensive.
			if (Info.EnableWaves && !Missions.OfType<AotRegularWaveMission>().Any() && !Missions.OfType<AotAirRaidMission>().Any()
				&& (waveIndex > 0 || StartupPriorityMet()))
			{
				if (--waveCooldownTicks <= 0)
				{
					waveCooldownTicks = Info.WaveCooldown;

					var canAirRaid = Info.AirRaidHelicopterTypes.Length > 0 && HasHelipad();
					bool doAirRaid;
					bool useSecondaryRoute;

					if (randomEscalationPhase)
					{
						doAirRaid = canAirRaid && World.LocalRandom.Next(2) == 0;
						useSecondaryRoute = !doAirRaid && World.LocalRandom.Next(2) == 0;
					}
					else
					{
						doAirRaid = waveFailureStreak >= Info.WaveAirRaidAfterFailures && canAirRaid;
						useSecondaryRoute = !doAirRaid && waveFailureStreak >= Info.WaveSecondaryRouteAfterFailures;
					}

					if (doAirRaid)
					{
						Missions.Add(new AotAirRaidMission(this));
						randomEscalationPhase = true;
						Log($"air raid scheduled (streak={waveFailureStreak}, random={randomEscalationPhase})");
					}
					else
					{
						waveIndex++;
						var wave = new AotRegularWaveMission(this, waveIndex, useSecondaryRoute);
						Missions.Add(wave);
						Log($"wave {waveIndex} scheduled (tier {AgeTier()}, secondaryRoute={useSecondaryRoute}, streak={waveFailureStreak}, random={randomEscalationPhase})");
					}
				}
			}

			// Module 3: scout expeditions (50% spawn coverage). Gated on Radar/HQ: no point
			// scouting before there's a radar/minimap to show the results on. One-shot per group
			// (User 2026-07-22): each group launches exactly once and is never rebuilt, even if
			// wiped out en route -- scouting is a single initial sweep, not a standing patrol.
			if (Info.EnableScouts && Intel.AllSpawns.Count > 1 && HasRadar())
			{
				if (scoutAssignments.Count == 0)
					BuildScoutAssignments();

				foreach (var (index, spawns) in scoutAssignments)
				{
					if (scoutGroupsLaunched.Contains(index))
						continue;

					scoutGroupsLaunched.Add(index);
					Missions.Add(new AotScoutMission(this, index, spawns));
					Log($"scout group {index} scheduled ({spawns.Count} spawn(s))");
				}
			}

			// Module 4: derrick engineer squads (periodic 10-minute check, map-wide, capped
			// at DerrickMaxTargets DERRICKS held/pursued — not squads. A captured derrick's
			// mission never finishes (permanent guard), so it keeps occupying its slot.
			if (Info.EnableDerricks && --derrickTicks <= 0)
			{
				derrickTicks = Info.DerrickCheckInterval;
				derrickFirstScanDone = true;
				var baseCentre = BaseCentre();
				var pursued = Missions.OfType<AotDerrickMission>().ToList();
				var freeSlots = Info.DerrickMaxTargets - pursued.Count;

				if (freeSlots > 0)
				{
					// A derrick with no land route used to be filtered out here entirely, so a squad was
					// never even formed for it. The capture squad can now cross with a transport
					// (User 2026-07-24), so those count as candidates too -- still ranked behind every
					// walkable derrick, and only when a ferry chain is configured at all.
					var candidates = Intel.UncontrolledDerricksAnywhere()
						.Where(d => !pursued.Any(m => m.Derrick == d))
						.Where(d => Intel.IsReachable(d.Location) || Info.FerryTypes.Length > 0)
						.OrderBy(d => Intel.IsReachable(d.Location) ? 0 : 1)
						.ThenBy(d => (d.Location - baseCentre).LengthSquared)
						.Take(freeSlots);

					foreach (var derrick in candidates)
					{
						Missions.Add(new AotDerrickMission(this, derrick));
						Log($"derrick target acquired -> {derrick.Info.Name}@{derrick.Location} " +
							$"({pursued.Count + 1}/{Info.DerrickMaxTargets} derricks held/pursued)");
					}
				}
			}
		}

		void BuildScoutAssignments()
		{
			var groups = Math.Max(1, Intel.AllSpawns.Count / 2);
			var targets = Intel.EnemySpawns.OrderBy(s => (s - Intel.OwnSpawn).LengthSquared).ToList();
			var index = 0;
			for (var i = 0; i < targets.Count && index < groups; i += 2, index++)
				scoutAssignments[index] = targets.Skip(i).Take(2).ToList();
		}

		void Log(string message)
		{
			OpenRA.Log.Write("debug", $"[AotOps][{Player.PlayerName}] {message}");
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			constructionYards.Dispose();
		}
	}
}
