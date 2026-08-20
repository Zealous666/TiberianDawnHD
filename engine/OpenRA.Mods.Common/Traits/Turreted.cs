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
using System.Linq;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	public class TurretedInfo : PausableConditionalTraitInfo, Requires<BodyOrientationInfo>, IActorPreviewInitInfo, IEditorActorOptions
	{
		public readonly string Turret = "primary";

		[Desc("Speed at which the turret turns.")]
		public readonly WAngle TurnSpeed = new(512);

		public readonly WAngle InitialFacing = WAngle.Zero;

		[Desc("Number of ticks before turret is realigned. (-1 turns off realignment)")]
		public readonly int RealignDelay = 40;

		[Desc("Muzzle position relative to turret or body. (forward, right, up) triples")]
		public readonly WVec Offset = WVec.Zero;

		[Desc("Display order for the turret facing slider in the map editor")]
		public readonly int EditorTurretFacingDisplayOrder = 4;

		IEnumerable<ActorInit> IActorPreviewInitInfo.ActorPreviewInits(ActorInfo ai, ActorPreviewType type)
		{
			yield return new TurretFacingInit(this, InitialFacing);
		}

		IEnumerable<EditorActorOption> IEditorActorOptions.ActorOptions(ActorInfo ai, World world)
		{
			yield return new EditorActorSlider("Turret", EditorTurretFacingDisplayOrder, 0, 1023, 8,
				actor =>
				{
					var init = actor.GetInitOrDefault<TurretFacingInit>(this);
					if (init != null)
						return init.Value.Angle;

					return InitialFacing.Angle;
				},
				(actor, value) => actor.ReplaceInit(new TurretFacingInit(this, new WAngle((int)value)), this));
		}

		public static Func<WAngle> WorldFacingFromInit(IActorInitializer init, TraitInfo info, WAngle defaultFacing)
		{
			// (Dynamic)TurretFacingInit is specified relative to the actor body.
			// We need to add the body facing to return an absolute world angle.
			// Dynamic facing takes priority, matching ActorPreview.GetFacing().
			Func<WAngle> bodyFacing = null;
			var dynamicBodyFacingInit = init.GetOrDefault<DynamicFacingInit>();
			if (dynamicBodyFacingInit != null)
				bodyFacing = dynamicBodyFacingInit.Value;
			else
			{
				var facingInit = init.GetOrDefault<FacingInit>();
				if (facingInit != null)
				{
					var facing = facingInit.Value;
					bodyFacing = () => facing;
				}
			}

			var turretFacingInit = init.GetOrDefault<TurretFacingInit>(info);
			if (turretFacingInit != null)
			{
				var facing = turretFacingInit.Value;
				return bodyFacing != null ? () => bodyFacing() + facing : () => facing;
			}

			var dynamicFacingInit = init.GetOrDefault<DynamicTurretFacingInit>(info);
			if (dynamicFacingInit != null)
				return bodyFacing != null ? () => bodyFacing() + dynamicFacingInit.Value() : dynamicFacingInit.Value;

			return bodyFacing ?? (() => defaultFacing);
		}

		public Func<WAngle> WorldFacingFromInit(IActorInitializer init)
		{
			return WorldFacingFromInit(init, this, InitialFacing);
		}

		public Func<WAngle> LocalFacingFromInit(IActorInitializer init)
		{
			var turretFacingInit = init.GetOrDefault<TurretFacingInit>(this);
			if (turretFacingInit != null)
			{
				var facing = turretFacingInit.Value;
				return () => facing;
			}

			var dynamicFacingInit = init.GetOrDefault<DynamicTurretFacingInit>(this);
			if (dynamicFacingInit != null)
				return dynamicFacingInit.Value;

			return () => InitialFacing;
		}

		// Turret offset in world-space
		public Func<WVec> PreviewPosition(ActorPreviewInitializer init, Func<WRot> orientation)
		{
			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			return () => body.LocalToWorld(Offset.Rotate(orientation()));
		}

		// Orientation in world-space
		public Func<WRot> PreviewOrientation(ActorPreviewInitializer init, Func<WRot> orientation, int facings)
		{
			var body = init.Actor.TraitInfo<BodyOrientationInfo>();
			var turretFacing = LocalFacingFromInit(init);

			WRot World() => WRot.FromYaw(turretFacing()).Rotate(orientation());
			if (facings == 0)
				return World;

			// Quantize orientation to match a rendered sprite
			// Implies no pitch or roll
			return () => WRot.FromYaw(body.QuantizeFacing(World().Yaw, facings));
		}

		public override object Create(ActorInitializer init) { return new Turreted(init, this); }
	}

	public class Turreted : PausableConditionalTrait<TurretedInfo>, ITick, IDeathActorInitModifier, IActorPreviewInitModifier, ISync
	{
		AttackTurreted attack;
		IFacing facing;
		BodyOrientation body;
		int quantizedFacings;

		[VerifySync]
		public int QuantizedFacings
		{
			get => quantizedFacings;
			set
			{
				if (value == 0)
					throw new ArgumentOutOfRangeException(nameof(value), value, "Expected nonzero facings for turret.");
				quantizedFacings = value;
			}
		}

		WVec desiredDirection;
		int realignTick = 0;
		bool realignDesired;

		public WRot WorldOrientation
		{
			get
			{
				var world = facing != null ? LocalOrientation.Rotate(facing.Orientation) : LocalOrientation;
				if (QuantizedFacings == 0)
					return world;

				// Quantize orientation to match a rendered sprite
				// Implies no pitch or roll
				return WRot.FromYaw(body.QuantizeFacing(world.Yaw, QuantizedFacings));
			}
		}

		public WRot LocalOrientation { get; private set; }

		// For subclasses that want to move the turret relative to the body
		protected WVec localOffset = WVec.Zero;

		public WVec Offset => Info.Offset + localOffset;
		public string Name => Info.Turret;

		public Turreted(ActorInitializer init, TurretedInfo info)
			: base(info)
		{
			LocalOrientation = WRot.FromYaw(info.LocalFacingFromInit(init)());
		}

		protected override void Created(Actor self)
		{
			base.Created(self);
			attack = self.TraitsImplementing<AttackTurreted>().SingleOrDefault(at => ((AttackTurretedInfo)at.Info).Turrets.Contains(Info.Turret));
			facing = self.TraitOrDefault<IFacing>();
			body = self.Trait<BodyOrientation>();
		}

		void ITick.Tick(Actor self)
		{
			// Actors sitting in Cargo (e.g. garrisoned in a Fire Position) are ticked even
			// though they are IsInWorld == false - TraitDictionary.ApplyToActorsWithTrait<ITick>
			// does not filter by world membership. Left unguarded, this trait's own realign-to-
			// InitialFacing logic below would fight AttackGarrisoned's explicit FaceTarget() calls
			// every tick (the passenger's own AttackFollow never has a target of its own while
			// garrisoned, so Tick() always takes the "not aiming, realign" branch), snapping the
			// turret back by roughly one TurnSpeed step as fast as FaceTarget turns it away -
			// the turret never converges and CanFire's alignment check never passes.
			if (!self.IsInWorld)
				return;

			Tick(self);
		}

		protected virtual void Tick(Actor self)
		{
			if (IsTraitDisabled)
				return;

			// NOTE: FaceTarget is called in AttackTurreted.CanAttack if the turret has a target.
			if (attack != null)
			{
				// Only realign while not attacking anything
				if (attack.IsAiming)
				{
					realignTick = 0;
					return;
				}

				if (realignTick < Info.RealignDelay)
					realignTick++;
				else if (Info.RealignDelay > -1)
				{
					realignDesired = true;
					desiredDirection = WVec.Zero;
				}

				MoveTurret();
			}
			else
			{
				realignTick = 0;
				MoveTurret();
			}
		}

		WAngle DesiredLocalFacing
		{
			get
			{
				// A zero value means that we have a target, but it is on top of us
				if (desiredDirection == WVec.Zero)
					return LocalOrientation.Yaw;

				if (facing == null)
					return desiredDirection.Yaw;

				// PERF: If the turret rotation axis is vertical we can directly take the difference in facing/yaw
				var orientation = facing.Orientation;
				if (orientation.Pitch == WAngle.Zero && orientation.Roll == WAngle.Zero)
					return desiredDirection.Yaw - orientation.Yaw;

				// If the turret rotation axis is not vertical we must transform the
				// target direction into the turrets local coordinate system
				return desiredDirection.Rotate(-orientation).Yaw;
			}
		}

		void MoveTurret()
		{
			var desired = realignDesired ? Info.InitialFacing : DesiredLocalFacing;
			if (desired == LocalOrientation.Yaw)
				return;

			LocalOrientation = LocalOrientation.WithYaw(Util.TickFacing(LocalOrientation.Yaw, desired, Info.TurnSpeed));

			if (desired == LocalOrientation.Yaw)
			{
				realignDesired = false;
				desiredDirection = WVec.Zero;
			}
		}

		public bool FaceTarget(Actor self, in Target target)
		{
			if (IsTraitDisabled || IsTraitPaused || attack == null || attack.IsTraitDisabled || attack.IsTraitPaused)
				return false;

			if (target.Type == TargetType.Invalid)
			{
				desiredDirection = WVec.Zero;
				return false;
			}

			var turretPos = self.CenterPosition + Position(self);
			var targetPos = attack.GetTargetPosition(turretPos, target);
			desiredDirection = targetPos - turretPos;
			realignDesired = false;

			MoveTurret();
			return HasAchievedDesiredFacing;
		}

		// aotmod 2026-08-20: Turm sofort auf eine WELT-Yaw schnappen, UNABHAENGIG vom eigenen
		// Attack-Trait des Aktors. Grund: ein garrisonierter Passagier (AttackGarrisoned) hat sein
		// eigenes AttackTurreted im Cargo DEAKTIVIERT -> das regulaere FaceTarget brach fruehzeitig
		// ab (attack.IsTraitDisabled) und der Turm blieb auf der eingefrorenen Koerper-Richtung (Nord)
		// stehen. Homing-Raketen kaschierten das bis ~90 Grad; bei GENAU 180 Grad (Ziel im Sueden)
		// musste die Rakete eine 180-Grad-Kehre fliegen und schlug in der eigenen Pillbox ein.
		// Der Host (AttackGarrisoned) kennt die korrekte Ziel-Yaw und uebergibt sie hier direkt.
		public void FaceWorldYawInstantly(Actor self, WAngle worldYaw)
		{
			var bodyYaw = facing != null ? facing.Orientation.Yaw : WAngle.Zero;
			LocalOrientation = LocalOrientation.WithYaw(worldYaw - bodyYaw);

			// Kein turn-rate-getriebenes Nachdrehen: Ziel ist erreicht.
			desiredDirection = WVec.Zero;
			realignDesired = false;
		}

		public virtual bool HasAchievedDesiredFacing
		{
			get
			{
				var desired = realignDesired ? Info.InitialFacing : DesiredLocalFacing;
				return desired == LocalOrientation.Yaw;
			}
		}

		// Turret offset in world-space
		public WVec Position(Actor self)
		{
			var bodyOrientation = body.QuantizeOrientation(self.Orientation);
			return body.LocalToWorld(Offset.Rotate(bodyOrientation));
		}

		public void ModifyDeathActorInit(Actor self, TypeDictionary init)
		{
			init.Add(new TurretFacingInit(Info, LocalOrientation.Yaw));
		}

		void IActorPreviewInitModifier.ModifyActorPreviewInit(Actor self, TypeDictionary inits)
		{
			inits.Add(new DynamicTurretFacingInit(Info, () => LocalOrientation.Yaw));
		}

		protected override void TraitDisabled(Actor self)
		{
			if (attack != null && attack.IsAiming)
				attack.OnStopOrder(self);
		}
	}

	public class TurretFacingInit : ValueActorInit<WAngle>
	{
		public TurretFacingInit(TraitInfo info, WAngle value)
			: base(info, value) { }

		public TurretFacingInit(string instanceName, WAngle value)
			: base(instanceName, value) { }

		public TurretFacingInit(WAngle value)
			: base(value) { }
	}

	public class DynamicTurretFacingInit : ValueActorInit<Func<WAngle>>
	{
		public DynamicTurretFacingInit(TraitInfo info, Func<WAngle> value)
			: base(info, value) { }
	}
}
