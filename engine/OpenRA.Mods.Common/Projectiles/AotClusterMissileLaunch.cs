#region Copyright & License Information
/*
 * Age of Tiberium mod — flying cluster-missile projectile for the MSLO super weapons.
 *
 * Unlike the stock NukeLaunch (vertical up -> horizontal -> vertical down, which reads as a
 * helicopter hovering over the target and sinking) this effect flies a single continuous
 * ballistic arc from the silo to the target:
 *   - the horizontal position moves toward the target from the very first tick, and
 *   - the height follows a parabola whose apex can be biased early (ApexPercent) so the
 *     missile pitches up steeply off the pad, arcs over, and dives back into the target,
 *     still travelling horizontally at impact rather than dropping straight down.
 * A gentle lateral wobble keeps the TS "eiern" feel without breaking the arc.
 *
 * The sprite is the two-layer TS voxel bake (mislmlti.vxl): an RGBA body plus an indexed
 * player-colour remap overlay, both rendered facing the current horizontal heading. A
 * player-coloured contrail (like the aircraft) trails behind it.
 *
 * The Firestorm Defense Matrix interception hooks mirror NukeLaunch — and because this
 * missile really travels horizontally, the "flight column crosses a matrix cell" check now
 * covers the whole arc, not just the target column.
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Effects
{
	public class AotClusterMissileLaunch : IProjectile, ISpatiallyPartitionable
	{
		readonly Player firedBy;
		readonly Animation bodyAnim;
		readonly Animation overlayAnim;
		readonly WeaponInfo weapon;
		readonly string bodyPalette;
		readonly string overlayPalette;

		readonly WPos launchPos;
		readonly WPos targetPos;

		readonly int impactDelay;
		readonly double apexU;
		readonly int apexHeight;
		readonly int apexTick;
		readonly int wobbleAmplitude;
		readonly int wobbleCycles;

		readonly WDist detonationAltitude;
		readonly bool removeOnDetonation;

		readonly ContrailRenderable contrail;
		readonly bool hasContrail;
		readonly int contrailRearOffset;

		readonly WAngle heading;
		readonly WVec flatDir;
		WAngle renderFacing;

		WPos pos;
		WPos prevPos;
		int ticks;
		int launchDelay;
		bool isLaunched;
		bool detonated;

		// aotmod: Firestorm Defense Matrix Interception (mirrors NukeLaunch)
		bool aotIntercepted;
		bool aotEnclosureChecked;
		AotFirestormMatrixPower aotEnclosingMatrix;

		// aotmod: naval AEGIS interception — set while a real interceptor missile is inbound.
		bool aotInterceptPending;

		/// <summary>Live centre position, tracked by an inbound AEGIS interceptor missile.</summary>
		public WPos Position => pos;

		/// <summary>Called by the AEGIS interceptor missile when it reaches us: destroy the missile.</summary>
		public void RemoveByInterceptor(World world) { AotRemoveIntercepted(world); }

		public AotClusterMissileLaunch(Player firedBy, string image, WeaponInfo weapon,
			string bodyPalette, string overlayPalette, string bodySequence, string overlaySequence,
			WPos launchPos, WPos targetPos, WDist cruiseAltitude,
			int launchDelay, int flightDelay, int apexPercent,
			int wobbleAmplitude, int wobbleCycles,
			WDist detonationAltitude, bool removeOnDetonation,
			int contrailLength, WDist contrailStartWidth, WDist contrailEndWidth,
			Color contrailStartColor, bool contrailStartUsePlayerColor,
			Color contrailEndColor, bool contrailEndUsePlayerColor, int contrailDelay, int contrailZOffset,
			int contrailRearOffset)
		{
			this.contrailRearOffset = contrailRearOffset;
			this.firedBy = firedBy;
			this.weapon = weapon;
			this.bodyPalette = bodyPalette;
			this.overlayPalette = overlayPalette;
			this.launchDelay = launchDelay;
			impactDelay = Math.Max(1, flightDelay);
			apexU = Math.Clamp(apexPercent / 100.0, 0.05, 0.95);
			apexTick = (int)(impactDelay * apexU);
			apexHeight = cruiseAltitude.Length;
			this.wobbleAmplitude = wobbleAmplitude;
			this.wobbleCycles = Math.Max(0, wobbleCycles);
			this.detonationAltitude = detonationAltitude;
			this.removeOnDetonation = removeOnDetonation;

			this.launchPos = launchPos;
			this.targetPos = targetPos;

			flatDir = new WVec(targetPos.X - launchPos.X, targetPos.Y - launchPos.Y, 0);
			heading = flatDir.HorizontalLengthSquared > 0 ? flatDir.Yaw : WAngle.Zero;
			renderFacing = heading;

			if (!string.IsNullOrEmpty(image))
			{
				bodyAnim = new Animation(firedBy.World, image, () => renderFacing);
				bodyAnim.PlayRepeating(bodySequence);

				if (!string.IsNullOrEmpty(overlaySequence))
				{
					overlayAnim = new Animation(firedBy.World, image, () => renderFacing);
					overlayAnim.PlayRepeating(overlaySequence);
				}
			}

			if (contrailLength > 0)
			{
				hasContrail = true;
				contrail = new ContrailRenderable(firedBy.World, firedBy.PlayerActor,
					contrailStartColor, contrailStartUsePlayerColor,
					contrailEndColor, contrailEndUsePlayerColor,
					contrailStartWidth, contrailEndWidth, contrailLength, contrailDelay, contrailZOffset);
			}

			pos = launchPos;
			prevPos = launchPos;
		}

		static double Sqr(double v) { return v * v; }

		WPos ComputePosition(int t)
		{
			if (t <= 0)
				return launchPos;

			if (t >= impactDelay)
				return targetPos;

			var x = launchPos.X + (int)((long)(targetPos.X - launchPos.X) * t / impactDelay);
			var y = launchPos.Y + (int)((long)(targetPos.Y - launchPos.Y) * t / impactDelay);
			var lineZ = launchPos.Z + (int)((long)(targetPos.Z - launchPos.Z) * t / impactDelay);

			var u = (double)t / impactDelay;

			// Parabola through (0,0) and (1,0) peaking = 1 at u = apexU (biased ballistic arc).
			var arc = u <= apexU
				? 1 - Sqr((apexU - u) / apexU)
				: 1 - Sqr((u - apexU) / (1 - apexU));

			var basePos = new WPos(x, y, lineZ + (int)(apexHeight * arc));

			if (wobbleAmplitude > 0 && wobbleCycles > 0)
			{
				var dir = new WVec(targetPos.X - launchPos.X, targetPos.Y - launchPos.Y, 0);
				var hl = dir.HorizontalLength;
				if (hl > 0)
				{
					// Perpendicular unit vector in the ground plane.
					var px = -dir.Y / (double)hl;
					var py = dir.X / (double)hl;

					// wobbleCycles full turns across the flight (WAngle full circle = 1024),
					// tapered to zero at both ends so launch and dive stay clean.
					var phase = new WAngle((int)((long)t * wobbleCycles * 1024 / impactDelay));
					var s = phase.Sin() / 1024.0;
					var taper = Math.Sin(Math.PI * u);
					var sway = wobbleAmplitude * s * taper;
					basePos += new WVec((int)(px * sway), (int)(py * sway), 0);
				}
			}

			return basePos;
		}

		public void Tick(World world)
		{
			if (launchDelay-- > 0)
				return;

			if (!isLaunched)
			{
				if (weapon.Report != null && weapon.Report.Length > 0)
					Game.Sound.Play(SoundType.World, weapon.Report, world, pos);

				if (bodyAnim != null)
					world.ScreenMap.Add(this, pos, bodyAnim.Image);

				isLaunched = true;
			}

			bodyAnim?.Tick();
			overlayAnim?.Tick();

			prevPos = pos;
			pos = ComputePosition(ticks);

			// Nose follows the (arcing, wobbling) heading; hold the base heading if barely moving.
			var travel = new WVec(pos.X - prevPos.X, pos.Y - prevPos.Y, 0);
			if (travel.HorizontalLengthSquared > 64)
				renderFacing = travel.Yaw;

			if (hasContrail)
			{
				// Emit the contrail from the missile's tail, not its centre: step back along the
				// heading by contrailRearOffset.
				var contrailPos = pos;
				if (contrailRearOffset > 0)
				{
					var fwd = travel.HorizontalLengthSquared > 64 ? travel : flatDir;
					var hl = fwd.HorizontalLength;
					if (hl > 0)
						contrailPos -= new WVec(
							(int)((long)fwd.X * contrailRearOffset / hl),
							(int)((long)fwd.Y * contrailRearOffset / hl),
							0);
				}

				contrail.Update(contrailPos);
			}

			var dat = world.Map.DistanceAboveTerrain(pos);
			var isDescending = ticks >= apexTick;

			// aotmod: naval AEGIS point-defense (Missile Destroyer). A ready battery launches a real
			// interceptor missile at us; we keep flying (aotInterceptPending) until it arrives and
			// removes us — so the player can watch the rocket close the distance.
			if (!aotIntercepted && !aotInterceptPending && !detonated)
			{
				var aegis = AotAegisInterceptor.FindInterceptorNear(world, pos, firedBy);
				if (aegis != null)
				{
					aotInterceptPending = true;
					aegis.LaunchInterceptor(world, this);
				}
			}

			// aotmod: Firestorm Defense Matrix Interception (siehe AotFirestormMatrixPower).
			if (!aotIntercepted && !aotInterceptPending && !detonated)
			{
				var cell = world.Map.CellContaining(pos);
				var direct = AotFirestormMatrixPower.FindInterceptorAt(world, cell, firedBy);
				if (direct != null)
				{
					AotIntercept(world, direct);
					return;
				}

				if (isDescending)
				{
					if (!aotEnclosureChecked)
					{
						aotEnclosureChecked = true;
						aotEnclosingMatrix = AotFirestormMatrixPower.FindEnclosingInterceptor(
							world, world.Map.CellContaining(targetPos), firedBy);
					}

					if (aotEnclosingMatrix != null && dat <= aotEnclosingMatrix.InterceptAltitude)
					{
						AotIntercept(world, aotEnclosingMatrix);
						return;
					}
				}
			}

			// While an interceptor is inbound, hold the warhead: the missile is doomed, it just
			// hasn't been reached yet. ComputePosition clamps it at the target once ticks >= impactDelay.
			if (!aotInterceptPending && (ticks == impactDelay || (isDescending && dat <= detonationAltitude)))
				Explode(world, ticks == impactDelay || removeOnDetonation);

			if (bodyAnim != null)
				world.ScreenMap.Update(this, pos, bodyAnim.Image);

			ticks++;
		}

		void AotIntercept(World world, AotFirestormMatrixPower matrix)
		{
			matrix.PlayInterceptEffect(world, pos);
			AotRemoveIntercepted(world);
		}

		// Shared teardown for any interceptor (matrix or AEGIS): mark done, fade the contrail,
		// and remove the projectile at frame end.
		void AotRemoveIntercepted(World world)
		{
			aotIntercepted = true;
			detonated = true;

			if (hasContrail)
				world.AddFrameEndTask(w => w.Add(new ContrailFader(pos, contrail)));

			world.AddFrameEndTask(w => { w.Remove(this); w.ScreenMap.Remove(this); });
		}

		void Explode(World world, bool removeProjectile)
		{
			if (removeProjectile)
			{
				if (hasContrail)
					world.AddFrameEndTask(w => w.Add(new ContrailFader(pos, contrail)));

				world.AddFrameEndTask(w => { w.Remove(this); w.ScreenMap.Remove(this); });
			}

			if (detonated)
				return;

			var target = Target.FromPos(pos);
			var warheadArgs = new WarheadArgs
			{
				Weapon = weapon,
				Source = target.CenterPosition,
				SourceActor = firedBy.PlayerActor,
				WeaponTarget = target
			};

			weapon.Impact(target, warheadArgs);

			detonated = true;
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			if (!isLaunched)
				yield break;

			if (hasContrail)
				yield return contrail;

			if (bodyAnim != null)
			{
				foreach (var r in bodyAnim.Render(pos, wr.Palette(bodyPalette)))
					yield return r;

				if (overlayAnim != null)
					foreach (var r in overlayAnim.Render(pos, wr.Palette(overlayPalette)))
						yield return r;
			}
		}

		public float FractionComplete => ticks * 1f / impactDelay;
	}
}
