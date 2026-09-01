#region Copyright & License Information
/*
 * Age of Tiberium mod — Sub-Hunter Corvette's Orca Bomber drone.
 *
 * The corvette permanently carries ONE Orca Bomber. It is not directly selectable: either the
 * player gives the CORVETTE an attack order (also force-fire onto open water, so the bomber can
 * be sent to blanket a spot), or the trait auto-detects enemy submarines inside ScanRange.
 * The bomber then flies a straight pass, drops its parachuted torpedoes SHORT of the target,
 * overshoots, comes home, lands on the bow pad and rearms.
 *
 * While docked it stays visible on the pad, welded to the deck and aligned with the ship's
 * facing. A leash pulls it back if it strays too far or its target disappears, so it can never
 * "lose the connection" and idle forever somewhere on the map. If it is shot down the corvette
 * has to return to a shipyard (RearmActors) to receive a new one.
 */
#endregion

using System.Collections.Generic;
using System;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Carries a single auto-controlled bomber drone that attacks nearby submarines.")]
	public class AotCorvetteBomberInfo : ConditionalTraitInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor type spawned as the bomber.")]
		public readonly string BomberActor = null;

		[Desc("Submarines within this range are attacked automatically.")]
		public readonly WDist ScanRange = WDist.FromCells(12);

		[Desc("Maximum range for a player-issued attack order.")]
		public readonly WDist MaxLaunchRange = WDist.FromCells(14);

		[Desc("The bomber is recalled once it gets further than this from the corvette.")]
		public readonly WDist LeashRange = WDist.FromCells(20);

		[Desc("Hard timeout (ticks) for a single attack run before the bomber is recalled.")]
		public readonly int RunTimeout = 500;

		[Desc("Ticks between automatic target scans.")]
		public readonly int ScanInterval = 25;

		[Desc("Deck offset the bomber is parked at (relative to the ship, +X = bow). Only used as a "
			+ "fallback when DockOffsets is empty.")]
		public readonly WVec DockOffset = new(512, 0, 0);

		[Desc("WORLD-space pad offset per rendered hull facing (one entry per sprite facing, in "
			+ "frame order). Measured straight off the sprites, which is the only reliable way: "
			+ "the pre-rendered RA art does not follow OpenRA's perspective model, so no single "
			+ "rotating offset can sit on the pad in every frame.")]
		public readonly WVec[] DockOffsets = [];

		[Desc("Quantize the dock offset to this many facings. MUST match the corvette hull's "
			+ "sprite facings (8), otherwise the parked bomber drifts off the deck on diagonals "
			+ "-- BodyOrientation quantizes to the 16-facing gunboat 'idle' sequence.")]
		public readonly int DockFacings = 8;

		[Desc("Mirror the dock offset's rotation. The repacked RA ship hulls (gunboat/frigate/"
			+ "corvette/destroyer alike) store their facings in the opposite rotational order to "
			+ "the engine's, so the pad drawn on screen sits at the mirrored angle. Measured from "
			+ "the sprites: pad angle = -45 degrees per facing step.")]
		public readonly bool MirrorDockFacing = true;

		[Desc("Sound played when the bomber lifts off the pad.")]
		public readonly string TakeoffSound = null;

		[Desc("Sound played when the bomber sets down on the pad.")]
		public readonly string LandingSound = null;

		[Desc("The returning bomber counts as landed once it is this close to the pad.")]
		public readonly WDist DockSnapRange = new(768);

		[Desc("Ticks between re-issuing the flight home, so the bomber tracks the moving corvette.")]
		public readonly int ReturnRefreshInterval = 20;

		[Desc("Ticks after take-off before the bomber may release. Guarantees a visible approach "
			+ "instead of dropping right off the deck.")]
		public readonly int ArmDelay = 40;

		[Desc("Ticks the bomber keeps flying after its last torpedo before turning home.")]
		public readonly int PostAttackDelay = 25;

		[Desc("Ticks one bombing pass may take. With no burst and a one second reload the bomber "
			+ "only gets a single drop per pass, so it loops around for another run until its "
			+ "torpedoes are gone. Without this it would hover after the first drop.")]
		public readonly int PassTimeout = 90;

		[Desc("Ticks the bomber needs on deck to rearm before it can fly again.")]
		public readonly int RearmDelay = 75;

		[Desc("Fully repair the bomber the moment it sets down on the pad.")]
		public readonly bool RepairOnDock = true;

		[Desc("Drop the torpedoes this far SHORT of the target, so they splash down ahead of it "
			+ "and still have water to run through. Also the bomber's stand-off distance: it turns "
			+ "away here instead of overflying the target, so AA ships cannot shoot it down on top "
			+ "of them.")]
		public readonly WDist DropLead = new(6144);

		[Desc("How far the bomber continues PAST the drop point before turning home. Kept short so "
			+ "the stand-off distance from DropLead is not thrown away again.")]
		public readonly WDist Overshoot = new(2048);

		[Desc("Condition granted on the corvette while the bomber sits on the pad.")]
		public readonly string DockedCondition = null;

		[Desc("ExternalCondition granted on the BOMBER while it is on an attack run. Its Armament "
			+ "must require this, otherwise AttackBomber would drop torpedoes while parked.")]
		public readonly string AttackingCondition = null;

		[Desc("Name of the AmmoPool on the CORVETTE used purely to show pips for the bomber state.")]
		public readonly string AmmoPoolName = null;

		[Desc("Actor types that supply a replacement bomber when the old one was shot down.")]
		public readonly string[] RearmActors = [];

		[Desc("How close the corvette has to be to a RearmActor to receive a new bomber.")]
		public readonly WDist RearmActorRange = WDist.FromCells(6);

		public override object Create(ActorInitializer init) { return new AotCorvetteBomber(init.Self, this); }
	}

	public class AotCorvetteBomber : ConditionalTrait<AotCorvetteBomberInfo>, ITick, INotifyKilled,
		INotifyActorDisposing, IResolveOrder
	{
		readonly Actor self;
		enum State { Docked, Attacking, Returning }

		Actor bomber;
		State state = State.Docked;
		bool everHadBomber;
		bool spawnRequested;
		int rearmTicks;
		int scanTicks;
		int runTicks;
		int returnRefresh;
		int spentTicks;
		int passTicks;
		Target runTarget = Target.Invalid;
		Target orderedTarget = Target.Invalid;
		int dockedToken = Actor.InvalidConditionToken;
		int attackingToken = Actor.InvalidConditionToken;

		public AotCorvetteBomber(Actor self, AotCorvetteBomberInfo info)
			: base(info)
		{
			this.self = self;
		}

		// --- orders ---------------------------------------------------------------------

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (IsTraitDisabled)
				return;

			// The corvette carries a real AttackBase, so the engine drives the ship towards the
			// target. We must NOT drop the order just because the target is still out of range
			// here -- that is resolved once, at order time. Remember it instead and launch as
			// soon as the ship has closed in (see the Docked state).
			if (order.OrderString == "Attack" || order.OrderString == "ForceAttack")
			{
				if (order.Target.Type == TargetType.Invalid)
					return;

				orderedTarget = order.Target;
			}
			// Move/Stop breaks off the attack and brings the bomber straight home -- same behaviour
			// as the missile sub recalling its drones.
			else if (order.OrderString == "Stop" || order.OrderString == "Move"
				|| order.OrderString == "AttackMove" || order.OrderString == "AssaultMove")
			{
				orderedTarget = Target.Invalid;
				Recover();
			}
		}

		// --- bomber lifecycle ---------------------------------------------------------------

		void CreateBomber(Actor self)
		{
			if (spawnRequested || (bomber != null && !bomber.IsDead))
				return;

			spawnRequested = true;
			self.World.AddFrameEndTask(w =>
			{
				spawnRequested = false;
				if (self.IsDead || !self.IsInWorld || IsTraitDisabled)
					return;

				bomber = w.CreateActor(true, Info.BomberActor.ToLowerInvariant(),
				[
					new OwnerInit(self.Owner),
					new LocationInit(self.Location),
					new CenterPositionInit(DockPosition),
				]);

				state = State.Docked;
				everHadBomber = true;
				rearmTicks = 0;
				attackingToken = Actor.InvalidConditionToken;
				runTarget = Target.Invalid;

				// AttackBomber.Tick dereferences its target every tick -> it must never be invalid.
				bomber.TraitOrDefault<AttackBomber>()?.SetTarget(DockPosition);
				ParkBomber();
				GrantDocked(self);
				SyncAmmo();
			});
		}

		WPos DockPosition
		{
			get
			{
				var body = self.TraitOrDefault<BodyOrientation>();
				// Exact path: look the pad up for the hull frame that is actually being drawn.
				if (Info.DockOffsets.Length > 0)
				{
					var n = Info.DockOffsets.Length;
					var idx = Util.IndexFacing(self.Orientation.Yaw, n) % n;
					return self.CenterPosition + Info.DockOffsets[idx];
				}

				var yaw = body != null && Info.DockFacings > 0
					? body.QuantizeFacing(self.Orientation.Yaw, Info.DockFacings)
					: self.Orientation.Yaw;

				if (Info.MirrorDockFacing)
					yaw = new WAngle(-yaw.Angle);

				var orientation = body == null ? WRot.None : WRot.FromYaw(yaw);

				var offset = body != null
					? body.LocalToWorld(Info.DockOffset.Rotate(orientation))
					: Info.DockOffset;
				return self.CenterPosition + offset;
			}
		}

		// Glue the parked bomber to the deck AND align it with the ship (coaxial, like the LST).
		void ParkBomber()
		{
			if (bomber == null || bomber.IsDead)
				return;

			bomber.Trait<IPositionable>().SetCenterPosition(bomber, DockPosition);

			var bomberFacing = bomber.TraitOrDefault<IFacing>();
			var shipFacing = self.TraitOrDefault<IFacing>();
			if (bomberFacing != null && shipFacing != null)
				bomberFacing.Facing = shipFacing.Facing;
		}

		void GrantDocked(Actor self)
		{
			if (Info.DockedCondition != null && dockedToken == Actor.InvalidConditionToken)
				dockedToken = self.GrantCondition(Info.DockedCondition);
		}

		void RevokeDocked(Actor self)
		{
			if (dockedToken != Actor.InvalidConditionToken)
				dockedToken = self.RevokeCondition(dockedToken);
		}

		void SetAttacking(bool attacking)
		{
			if (Info.AttackingCondition == null || bomber == null || bomber.IsDead)
				return;

			var ec = bomber.TraitsImplementing<ExternalCondition>()
				.FirstOrDefault(t => t.Info.Condition == Info.AttackingCondition);
			if (ec == null)
				return;

			if (attacking)
			{
				if (attackingToken == Actor.InvalidConditionToken && ec.CanGrantCondition(this))
					attackingToken = ec.GrantCondition(bomber, this);
			}
			else if (attackingToken != Actor.InvalidConditionToken)
			{
				ec.TryRevokeCondition(bomber, this, attackingToken);
				attackingToken = Actor.InvalidConditionToken;
			}
		}

		// Pips on the CORVETTE mirror the bomber's REAL torpedo count (3/2/1/0), so the player can
		// see how many drops are left and when it has rearmed. 0 also means "bomber lost".
		void SyncAmmo()
		{
			if (Info.AmmoPoolName == null)
				return;

			var pool = self.TraitsImplementing<AmmoPool>()
				.FirstOrDefault(p => p.Info.Name == Info.AmmoPoolName);
			if (pool == null)
				return;

			// Full = a bomber is on board, empty = it was shot down. Rearmable reads this, which is
			// what turns the shipyard's repair cursor active while the corvette has no bomber.
			var want = bomber != null && !bomber.IsDead ? pool.Info.Ammo : 0;

			while (pool.CurrentAmmoCount < want && pool.GiveAmmo(self, 1)) { }
			while (pool.CurrentAmmoCount > want && pool.TakeAmmo(self, 1)) { }
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || self.IsDead || !self.IsInWorld)
				return;

			SyncAmmo();

			if (bomber == null || bomber.IsDead)
			{
				if (bomber != null)
				{
					bomber = null;
					RevokeDocked(self);
					SyncAmmo();

					// The token belonged to the dead bomber. Without clearing it, SetAttacking()
					// would think the condition is still granted and never arm the replacement.
					attackingToken = Actor.InvalidConditionToken;
					state = State.Docked;
					runTarget = Target.Invalid;
				}

				// The FIRST bomber is part of the ship (also for frigates upgraded at sea);
				// only a REPLACEMENT for a shot-down one has to be collected from a shipyard.
				if (--rearmTicks <= 0)
				{
					rearmTicks = 50;
					if (!everHadBomber || NearRearmActor())
						CreateBomber(self);
				}

				return;
			}

			switch (state)
			{
				case State.Docked:
					ParkBomber();
					GrantDocked(self);

					if (rearmTicks > 0)
					{
						rearmTicks--;

						// Feed the torpedoes back one at a time across RearmDelay so the pips fill
						// up gradually instead of snapping to full the instant it touches down.
						var pools = bomber.TraitsImplementing<AmmoPool>().ToArray();
						var capacity = pools.Sum(pool => pool.Info.Ammo);
						var step = Math.Max(1, Info.RearmDelay / Math.Max(1, capacity));
						if (rearmTicks % step == 0)
							foreach (var pool in pools)
								if (pool.GiveAmmo(bomber, 1))
									break;

						return;
					}

					// Safety net: never leave the pad without a full load.
					foreach (var pool in bomber.TraitsImplementing<AmmoPool>())
						while (pool.GiveAmmo(bomber, 1)) { }

					// A standing player order takes priority and reaches further than the
					// automatic scan: fire as soon as the ship has closed to MaxLaunchRange.
					if (orderedTarget.Type == TargetType.Actor
						&& (orderedTarget.Actor == null || orderedTarget.Actor.IsDead || !orderedTarget.Actor.IsInWorld))
						orderedTarget = Target.Invalid;

					if (orderedTarget.Type != TargetType.Invalid
						&& (self.CenterPosition - orderedTarget.CenterPosition).HorizontalLength <= Info.MaxLaunchRange.Length)
					{
						Launch(orderedTarget);
						return;
					}

					if (--scanTicks > 0)
						return;

					scanTicks = Info.ScanInterval;
					var target = FindSubmarine();
					if (target.Type != TargetType.Invalid)
						Launch(target);

					return;

				case State.Attacking:
				{
					runTicks++;

					// Hold fire for a moment after take-off so there is always a real approach.
					if (runTicks == Info.ArmDelay)
						SetAttacking(true);

					var targetLost = runTarget.Type == TargetType.Actor
						&& (runTarget.Actor == null || runTarget.Actor.IsDead || !runTarget.Actor.IsInWorld);

					// Torpedoes gone = run finished. Do not wait for IsIdle: a hovering helicopter
					// keeps an idle activity, so IsIdle alone would leave it loitering out there.
					var spent = !bomber.TraitsImplementing<AmmoPool>().Any(pool => pool.HasAmmo);

					// Keep flying briefly after the last torpedo instead of turning on the spot.
					if (spent && runTicks >= Info.ArmDelay)
					{
						if (++spentTicks < Info.PostAttackDelay)
							return;

						Recover();
						return;
					}

					if (targetLost || runTicks > Info.RunTimeout || Strayed())
					{
						Recover();
						return;
					}

					// Still armed but the pass is over (one drop per pass): loop around for another
					// run. Purely tick-driven -- a hovering helicopter's IsIdle is not reliable.
					if (++passTicks > Info.PassTimeout || bomber.IsIdle)
						StartPass();

					return;
				}

				case State.Returning:
				{
					runTicks++;

					if ((bomber.CenterPosition - DockPosition).HorizontalLength <= Info.DockSnapRange.Length)
					{
						Dock();
						return;
					}

					// The corvette keeps moving, so a one-shot Fly to a stale position would strand
					// the bomber. Re-issue the way home regularly (and whenever it fell idle).
					if (bomber.IsIdle || --returnRefresh <= 0)
					{
						returnRefresh = Info.ReturnRefreshInterval;
						bomber.CancelActivity();
						bomber.QueueActivity(new Fly(bomber, Target.FromPos(DockPosition)));
					}

					// Last resort so it can never be stranded for good.
					if (runTicks > Info.RunTimeout * 2)
						Dock();

					return;
				}
			}
		}

		bool Strayed()
		{
			return (bomber.CenterPosition - self.CenterPosition).HorizontalLength > Info.LeashRange.Length;
		}

		bool NearRearmActor()
		{
			if (Info.RearmActors.Length == 0)
				return true;

			return self.World.FindActorsInCircle(self.CenterPosition, Info.RearmActorRange)
				.Any(a => !a.IsDead && a.IsInWorld && a.Owner.RelationshipWith(self.Owner) == PlayerRelationship.Ally
					&& Info.RearmActors.Contains(a.Info.Name));
		}

		Target FindSubmarine()
		{
			var armament = bomber.TraitsImplementing<Armament>().FirstOrDefault();
			if (armament == null)
				return Target.Invalid;

			var candidate = self.World.FindActorsInCircle(self.CenterPosition, Info.ScanRange)
				.Where(a => !a.IsDead && a.IsInWorld
					&& self.Owner.RelationshipWith(a.Owner) == PlayerRelationship.Enemy
					&& a.CanBeViewedByPlayer(self.Owner)
					&& armament.Weapon.IsValidAgainst(Target.FromActor(a), self.World, self))
				.OrderBy(a => (a.CenterPosition - self.CenterPosition).HorizontalLengthSquared)
				.FirstOrDefault();

			return candidate != null ? Target.FromActor(candidate) : Target.Invalid;
		}

		void Launch(in Target target)
		{
			var captured = target;
			self.World.AddFrameEndTask(w =>
			{
				if (bomber == null || bomber.IsDead || self.IsDead)
					return;

				state = State.Attacking;
				runTicks = 0;
				spentTicks = 0;
				passTicks = 0;
				runTarget = captured;
				RevokeDocked(self);

				if (!string.IsNullOrEmpty(Info.TakeoffSound))
					Game.Sound.Play(SoundType.World, Info.TakeoffSound, bomber.CenterPosition);

				StartPass();
			});
		}

		// One bombing pass: approach the drop point, release, overshoot slightly, turn.
		void StartPass()
		{
			if (bomber == null || bomber.IsDead || runTarget.Type == TargetType.Invalid)
				return;

			passTicks = 0;
			var targetPos = runTarget.CenterPosition;

			// Aim SHORT of the target: the parachuted torpedoes cannot steer while falling, they
			// need water to run through. This is also the stand-off distance from AA ships.
			var approach = targetPos - bomber.CenterPosition;
			var hl = approach.HorizontalLength;
			var dropPos = hl > Info.DropLead.Length
				? targetPos - approach * Info.DropLead.Length / hl
				: targetPos;

			bomber.TraitOrDefault<AttackBomber>()?.SetTarget(dropPos);

			var overshoot = hl > 0
				? dropPos + approach * Info.Overshoot.Length / hl
				: dropPos;

			bomber.CancelActivity();
			bomber.QueueActivity(new Fly(bomber, Target.FromPos(dropPos)));
			bomber.QueueActivity(new Fly(bomber, Target.FromPos(overshoot)));
		}

		void Recover()
		{
			if (bomber == null || bomber.IsDead || state == State.Returning || state == State.Docked)
				return;

			state = State.Returning;
			runTicks = 0;
			returnRefresh = 0;
			runTarget = Target.Invalid;
			SetAttacking(false);

			bomber.CancelActivity();
			bomber.QueueActivity(new Fly(bomber, Target.FromPos(DockPosition)));
		}

		// Touch down: refill, park, and start the rearm timer.
		void Dock()
		{
			if (bomber == null || bomber.IsDead)
				return;

			state = State.Docked;
			runTicks = 0;
			SetAttacking(false);

			// Ammo is NOT restored here: it is fed back in one torpedo at a time while the bomber
			// sits on the pad (see the Docked state), so the reload is actually visible.

			// Servicing on deck also patches the bomber up.
			if (Info.RepairOnDock)
			{
				var health = bomber.TraitOrDefault<Health>();
				if (health != null && health.HP < health.MaxHP)
					health.InflictDamage(bomber, self, new Damage(-(health.MaxHP - health.HP)), true);
			}

			rearmTicks = Info.RearmDelay;
			bomber.CancelActivity();
			bomber.TraitOrDefault<AttackBomber>()?.SetTarget(DockPosition);
			ParkBomber();
			GrantDocked(self);

			if (!string.IsNullOrEmpty(Info.LandingSound))
				Game.Sound.Play(SoundType.World, Info.LandingSound, bomber.CenterPosition);
		}

		void RemoveBomber()
		{
			if (bomber == null)
				return;

			var b = bomber;
			bomber = null;
			self.World.AddFrameEndTask(w =>
			{
				if (!b.IsDead)
				{
					if (b.IsInWorld)
						w.Remove(b);

					b.Dispose();
				}
			});
		}

		protected override void TraitDisabled(Actor self)
		{
			RemoveBomber();
			RevokeDocked(self);
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e) { RemoveBomber(); }
		void INotifyActorDisposing.Disposing(Actor self) { RemoveBomber(); }
	}

}
