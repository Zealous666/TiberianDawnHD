#region Copyright & License Information
/*
 * Age of Tiberium mod — visible AEGIS interceptor missile.
 *
 * Launched by the Missile Destroyer's AotAegisInterceptor when an incoming cluster missile
 * enters range. Unlike a hitscan intercept, this flies a real sprite from the ship straight
 * to the (moving) cluster missile: it homes on a live position delegate, so the player can
 * watch the rocket close the distance and judge intercept ranges. On arrival it fires the
 * onHit callback (which destroys the cluster missile) and spawns the airburst effect.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Effects;
using OpenRA.GameRules;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Effects;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Projectiles
{
	public class AotAegisMissile : IProjectile, ISpatiallyPartitionable
	{
		readonly Actor firedBy;
		readonly Animation anim;
		readonly string palette;
		readonly Func<WPos> target;
		readonly Action onHit;
		readonly int speed;
		readonly int closeEnough;
		readonly int maxTicks;

		readonly string explosionImage;
		readonly string explosionSequence;
		readonly string explosionPalette;
		readonly string explosionSound;

		readonly string trailImage;
		readonly string trailSequence;
		readonly string trailPalette;
		readonly int trailInterval;

		WPos pos;
		WVec lastMove;
		WAngle facing;
		int ticks;
		int ticksToNextSmoke;
		bool added;
		bool done;

		public AotAegisMissile(Actor firedBy, WPos launchPos, Func<WPos> target, Action onHit,
			string image, string sequence, string palette, int speed, int closeEnough, int maxTicks,
			string explosionImage, string explosionSequence, string explosionPalette, string explosionSound,
			string trailImage, string trailSequence, string trailPalette, int trailInterval)
		{
			this.firedBy = firedBy;
			this.target = target;
			this.onHit = onHit;
			this.palette = palette;
			this.speed = Math.Max(1, speed);
			this.closeEnough = closeEnough;
			this.maxTicks = maxTicks;
			this.explosionImage = explosionImage;
			this.explosionSequence = explosionSequence;
			this.explosionPalette = explosionPalette;
			this.explosionSound = explosionSound;
			this.trailImage = trailImage;
			this.trailSequence = trailSequence;
			this.trailPalette = trailPalette;
			this.trailInterval = Math.Max(1, trailInterval);

			pos = launchPos;
			var delta = target() - launchPos;
			facing = delta.HorizontalLengthSquared > 0 ? delta.Yaw : WAngle.Zero;

			if (!string.IsNullOrEmpty(image))
			{
				anim = new Animation(firedBy.World, image, () => facing);
				anim.PlayRepeating(sequence);
			}
		}

		public void Tick(World world)
		{
			if (done)
				return;

			if (!added)
			{
				if (anim != null)
					world.ScreenMap.Add(this, pos, anim.Image);

				added = true;
			}

			anim?.Tick();

			var tgt = target();
			var delta = tgt - pos;
			var dist = delta.Length;

			if (delta.HorizontalLengthSquared > 64)
				facing = delta.Yaw;

			if (dist <= closeEnough || dist <= speed || ++ticks >= maxTicks)
			{
				Detonate(world, tgt);
				return;
			}

			lastMove = new WVec(
				(int)((long)delta.X * speed / dist),
				(int)((long)delta.Y * speed / dist),
				(int)((long)delta.Z * speed / dist));

			pos += lastMove;

			// Smoke trail behind the nose (mirrors the stock Missile projectile).
			if (!string.IsNullOrEmpty(trailImage) && --ticksToNextSmoke < 0)
			{
				var smokePos = pos - 3 * lastMove / 2;
				world.AddFrameEndTask(w => w.Add(new SpriteEffect(smokePos, facing, w,
					trailImage, trailSequence, trailPalette)));

				ticksToNextSmoke = trailInterval;
			}

			if (anim != null)
				world.ScreenMap.Update(this, pos, anim.Image);
		}

		void Detonate(World world, WPos at)
		{
			done = true;

			onHit?.Invoke();

			world.AddFrameEndTask(w =>
			{
				if (!string.IsNullOrEmpty(explosionImage) && !string.IsNullOrEmpty(explosionSequence))
					w.Add(new SpriteEffect(at, w, explosionImage, explosionSequence, explosionPalette));

				w.Remove(this);
				w.ScreenMap.Remove(this);
			});

			if (!string.IsNullOrEmpty(explosionSound))
				Game.Sound.Play(SoundType.World, explosionSound, at);
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr)
		{
			if (done || anim == null)
				return Enumerable.Empty<IRenderable>();

			return anim.Render(pos, wr.Palette(palette));
		}

		public float FractionComplete => maxTicks > 0 ? ticks * 1f / maxTicks : 0f;
	}
}
