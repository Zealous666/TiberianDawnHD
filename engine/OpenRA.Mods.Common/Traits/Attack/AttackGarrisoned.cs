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

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;

using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public class FirePort
	{
		public WVec Offset;
		public WAngle Yaw;
		public WAngle Cone;
	}

	[Desc("Cargo can fire their weapons out of fire ports.")]
	public class AttackGarrisonedInfo : AttackFollowInfo, IRulesetLoaded, Requires<CargoInfo>
	{
		[FieldLoader.Require]
		[Desc("Fire port offsets in local coordinates.")]
		public readonly ImmutableArray<WVec> PortOffsets = default;

		[FieldLoader.Require]
		[Desc("Fire port yaw angles.")]
		public readonly ImmutableArray<WAngle> PortYaws = default;

		[FieldLoader.Require]
		[Desc("Fire port yaw cone angle.")]
		public readonly ImmutableArray<WAngle> PortCones = default;

		public ImmutableArray<FirePort> Ports { get; private set; }

		[PaletteReference]
		public readonly string MuzzlePalette = "effect";

		public override object Create(ActorInitializer init) { return new AttackGarrisoned(init.Self, this); }
		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			if (PortOffsets.Length == 0)
				throw new YamlException("PortOffsets must have at least one entry.");

			if (PortYaws.Length != PortOffsets.Length)
				throw new YamlException("PortYaws must define an angle for each port.");

			if (PortCones.Length != PortOffsets.Length)
				throw new YamlException("PortCones must define an angle for each port.");

			var ports = new FirePort[PortOffsets.Length];

			for (var i = 0; i < PortOffsets.Length; i++)
			{
				ports[i] = new FirePort
				{
					Offset = PortOffsets[i],
					Yaw = PortYaws[i],
					Cone = PortCones[i],
				};
			}

			Ports = ports.ToImmutableArray();

			base.RulesetLoaded(rules, ai);
		}
	}

	public class AttackGarrisoned : AttackFollow, INotifyPassengerEntered, INotifyPassengerExited, IRender
	{
		public new readonly AttackGarrisonedInfo Info;
		INotifyAttack[] notifyAttacks;
		readonly Lazy<BodyOrientation> coords;
		readonly List<Armament> armaments;
		readonly List<AnimationWithOffset> muzzles;
		readonly Dictionary<Actor, IFacing> paxFacing;
		readonly Dictionary<Actor, IPositionable> paxPos;
		readonly Dictionary<Actor, RenderSprites> paxRender;
		readonly Dictionary<Actor, Turreted[]> paxTurreted;
		readonly Dictionary<Armament, FirePort> armamentPort;

		public AttackGarrisoned(Actor self, AttackGarrisonedInfo info)
			: base(self, info)
		{
			Info = info;
			coords = Exts.Lazy(self.Trait<BodyOrientation>);
			armaments = [];
			muzzles = [];
			paxFacing = [];
			paxPos = [];
			paxRender = [];
			paxTurreted = [];
			armamentPort = [];
		}

		protected override void Created(Actor self)
		{
			notifyAttacks = self.TraitsImplementing<INotifyAttack>().ToArray();
			base.Created(self);
		}

		protected override Func<IEnumerable<Armament>> InitializeGetArmaments(Actor self)
		{
			return () => armaments;
		}

		// Passengers with their own turret keep whatever body facing they had when they
		// boarded (see DoAttack: only the turret rotates while attacking, not the hull).
		// Snap that parked facing onto the North/South axis so the chassis never ends up
		// sitting at a diagonal inside the fire port frame, no matter which direction the
		// vehicle approached from.
		static WAngle NearestNorthOrSouth(WAngle facing)
		{
			var angle = facing.Angle;
			var distanceToNorth = Math.Min(angle, 1024 - angle);
			var distanceToSouth = Math.Abs(angle - 512);
			return distanceToNorth <= distanceToSouth ? WAngle.Zero : new WAngle(512);
		}

		void INotifyPassengerEntered.OnPassengerEntered(Actor self, Actor passenger)
		{
			var passengerFacing = passenger.Trait<IFacing>();
			passengerFacing.Facing = NearestNorthOrSouth(passengerFacing.Facing);

			paxFacing.Add(passenger, passengerFacing);
			paxPos.Add(passenger, passenger.Trait<IPositionable>());
			paxRender.Add(passenger, passenger.Trait<RenderSprites>());
			paxTurreted.Add(passenger, passenger.TraitsImplementing<Turreted>().ToArray());

			foreach (var a in passenger.TraitsImplementing<Armament>())
			{
				if (Info.Armaments.Contains(a.Info.Name))
				{
					a.AddNotifyAttacks(self, notifyAttacks);
					armaments.Add(a);
				}
			}
		}

		void INotifyPassengerExited.OnPassengerExited(Actor self, Actor passenger)
		{
			paxFacing.Remove(passenger);
			paxPos.Remove(passenger);
			paxRender.Remove(passenger);
			paxTurreted.Remove(passenger);

			foreach (var a in armaments.ToList())
			{
				if (a.Actor == passenger)
				{
					a.RemoveNotifyAttacks(notifyAttacks);
					armaments.Remove(a);
					armamentPort.Remove(a);
				}
			}
		}

		bool PortFacesTarget(WAngle bodyYaw, FirePort port, WAngle targetYaw)
		{
			var yaw = bodyYaw + port.Yaw;
			var leftTurn = (yaw - targetYaw).Angle;
			var rightTurn = (targetYaw - yaw).Angle;
			return Math.Min(leftTurn, rightTurn) <= port.Cone.Angle;
		}

		FirePort SelectFirePort(Actor self, WAngle targetYaw)
		{
			// Pick a random port that faces the target
			var bodyYaw = facing != null ? facing.Facing : WAngle.Zero;
			var indices = Enumerable.Range(0, Info.Ports.Length).Shuffle(self.World.SharedRandom);
			foreach (var i in indices)
				if (PortFacesTarget(bodyYaw, Info.Ports[i], targetYaw))
					return Info.Ports[i];

			return null;
		}

		// Keep using the same port across ticks while it still faces the target, instead
		// of re-rolling a random (possibly different) matching port every tick. Passengers
		// with their own turret compute their aim relative to this port's position, so a
		// port that keeps jumping around every tick would make the turret jitter/never
		// converge instead of smoothly rotating onto the target.
		FirePort SelectFirePortSticky(Actor self, Armament a, WAngle targetYaw)
		{
			var bodyYaw = facing != null ? facing.Facing : WAngle.Zero;
			if (armamentPort.TryGetValue(a, out var cached) && PortFacesTarget(bodyYaw, cached, targetYaw))
				return cached;

			var port = SelectFirePort(self, targetYaw);
			if (port != null)
				armamentPort[a] = port;
			else
				armamentPort.Remove(a);

			return port;
		}

		WVec PortOffset(Actor self, FirePort p)
		{
			var bodyOrientation = coords.Value.QuantizeOrientation(self.Orientation);
			return coords.Value.LocalToWorld(p.Offset.Rotate(bodyOrientation));
		}

		public override void DoAttack(Actor self, in Target target)
		{
			if (!CanAttack(self, target))
			{
				if (AotFirePositionDebug.Enabled)
					AotFirePositionDebug.Trace($"tick={self.World.WorldTick} actor={self.Info.Name} DoAttack: CanAttack=false, aborting");
				return;
			}

			var pos = self.CenterPosition;
			var targetedPosition = GetTargetPosition(pos, target);
			var targetYaw = (targetedPosition - pos).Yaw;

			foreach (var a in Armaments)
			{
				if (a.IsTraitDisabled)
					continue;

				var port = SelectFirePortSticky(self, a, targetYaw);
				if (port == null)
				{
					if (AotFirePositionDebug.Enabled)
						AotFirePositionDebug.Trace($"tick={self.World.WorldTick} passenger={a.Actor.Info.Name} DoAttack: no fire port faces targetYaw={targetYaw}, aborting");
					return;
				}

				paxPos[a.Actor].SetCenterPosition(a.Actor, pos + PortOffset(self, port));

				// If the passenger has its own turret for this armament, rotate only
				// the turret toward the target (like a tank in a bunker - hull stays
				// put, turret aims). Otherwise (e.g. infantry with no turret) fall back
				// to turning the whole body, since that's the only way for it to aim.
				var hasMatchingTurret = false;
				if (paxTurreted.TryGetValue(a.Actor, out var turrets))
					foreach (var t in turrets)
						if (t.Name == a.Info.Turret)
						{
							hasMatchingTurret = true;
							var beforeYaw = t.LocalOrientation.Yaw;
							var achievedBefore = t.HasAchievedDesiredFacing;
							var faced = t.FaceTarget(a.Actor, target);
							if (AotFirePositionDebug.Enabled)
							{
								AotFirePositionDebug.Trace($"tick={self.World.WorldTick} passenger={a.Actor.Info.Name} turret={t.Name} " +
									$"targetYaw={targetYaw} portYaw={port.Yaw} bodyFacing={paxFacing[a.Actor].Facing} " +
									$"turnSpeed={t.Info.TurnSpeed} before={beforeYaw} achievedBefore={achievedBefore} after={t.LocalOrientation.Yaw} " +
									$"faceTargetResult={faced}");
							}
						}

				if (!hasMatchingTurret)
					paxFacing[a.Actor].Facing = targetYaw;

				var fired = a.CheckFire(a.Actor, facing, target);

				if (AotFirePositionDebug.Enabled)
				{
					AotFirePositionDebug.Trace($"tick={self.World.WorldTick} passenger={a.Actor.Info.Name} armament={a.Info.Name} weapon={a.Info.Weapon} " +
						$"CheckFire={fired} isReloading={a.IsReloading} isTraitPaused={a.IsTraitPaused} " +
						$"maxRange={a.MaxRange()} minRange={a.Weapon.MinRange} distToTarget={(targetedPosition - pos).Length}");
				}

				if (!fired)
					continue;

				if (a.Info.MuzzleSequence != null)
				{
					// Muzzle facing is fixed once the firing starts
					var muzzleAnim = new Animation(self.World, paxRender[a.Actor].GetImage(a.Actor), () => targetYaw);
					var sequence = a.Info.MuzzleSequence;
					var muzzleFlash = new AnimationWithOffset(muzzleAnim,
						() => PortOffset(self, port),
						() => false,
						p => RenderUtils.ZOffsetFromCenter(self, p, 1024));

					muzzles.Add(muzzleFlash);
					muzzleAnim.PlayThen(sequence, () => muzzles.Remove(muzzleFlash));
				}
			}
		}

		IEnumerable<IRenderable> IRender.Render(Actor self, WorldRenderer wr)
		{
			var pal = wr.Palette(Info.MuzzlePalette);

			// Display muzzle flashes
			foreach (var m in muzzles)
				foreach (var r in m.Render(self, pal))
					yield return r;
		}

		IEnumerable<Rectangle> IRender.ScreenBounds(Actor self, WorldRenderer wr)
		{
			// Muzzle flashes don't contribute to actor bounds
			yield break;
		}

		protected override void Tick(Actor self)
		{
			base.Tick(self);

			// Take a copy so that Tick() can remove animations
			foreach (var m in muzzles.ToArray())
				m.Animation.Tick();
		}
	}
}
