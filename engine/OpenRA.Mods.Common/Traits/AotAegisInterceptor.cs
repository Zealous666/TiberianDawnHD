#region Copyright & License Information
/*
 * Age of Tiberium mod — naval AEGIS point-defense.
 *
 * A range-based interceptor that shoots down incoming Age-of-Tiberium cluster missiles
 * (AotClusterMissileLaunch) that fly within Range of the carrying actor. Mirrors the
 * Firestorm Defense Matrix interception hook, but instead of a fixed cell barrier it uses a
 * mobile circular envelope around the actor (the Missile Destroyer). Only the cluster-missile
 * projectile queries this trait, so no per-projectile target filtering is needed here.
 *
 * A per-actor cooldown (ReloadDelay) prevents a single ship from trivially swatting every
 * missile in the same instant, and the trait is a ConditionalTrait so it can be gated to the
 * Destroyer upgrade state via RequiresCondition.
 */
#endregion

using OpenRA.Mods.Common.Effects;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Naval AEGIS point-defense: shoots down cluster missiles that fly within Range.")]
	public class AotAegisInterceptorInfo : ConditionalTraitInfo
	{
		[Desc("Cluster missiles passing within this horizontal radius of the actor are intercepted.")]
		public readonly WDist Range = WDist.FromCells(8);

		[Desc("Ticks between interceptions for this actor.")]
		public readonly int ReloadDelay = 200;

		[Desc("Explosion image spawned at the intercepted missile.")]
		public readonly string EffectImage = "explosion";

		[SequenceReference(nameof(EffectImage))]
		[Desc("Sequence of EffectImage to play at the intercepted missile.")]
		public readonly string EffectSequence = null;

		[PaletteReference]
		[Desc("Palette used to render the intercept effect.")]
		public readonly string EffectPalette = "effect";

		[Desc("Sound played at the intercepted missile when the interceptor hits.")]
		public readonly string InterceptSound = null;

		[Desc("Sound played at the interceptor when it fires (launch report).")]
		public readonly string LaunchSound = null;

		[Desc("Interceptor missile sprite image. Empty = invisible/instant intercept.")]
		public readonly string InterceptorImage = "missile";

		[SequenceReference(nameof(InterceptorImage))]
		[Desc("Sequence of InterceptorImage to play.")]
		public readonly string InterceptorSequence = "idle";

		[PaletteReference]
		[Desc("Palette used to render the interceptor missile.")]
		public readonly string InterceptorPalette = "effect";

		[Desc("Interceptor speed in WDist per tick.")]
		public readonly WDist InterceptorSpeed = new(384);

		[Desc("Detonate the interceptor when within this distance of the cluster missile.")]
		public readonly WDist InterceptorCloseEnough = new(426);

		[Desc("Safety cap on interceptor flight ticks before it self-detonates.")]
		public readonly int InterceptorMaxTicks = 60;

		[Desc("Vertical launch offset from the actor centre (deck height).")]
		public readonly WDist InterceptorLaunchHeight = new(128);

		[Desc("Smoke-trail image for the interceptor (empty = no trail).")]
		public readonly string TrailImage = "smokey";

		[SequenceReference(nameof(TrailImage), allowNullImage: true)]
		[Desc("Sequence of TrailImage to loop behind the interceptor.")]
		public readonly string TrailSequence = "idle";

		[PaletteReference]
		[Desc("Palette used to render the trail.")]
		public readonly string TrailPalette = "effect";

		[Desc("Interval in ticks between trail puffs.")]
		public readonly int TrailInterval = 2;

		public override object Create(ActorInitializer init) { return new AotAegisInterceptor(init.Self, this); }
	}

	public class AotAegisInterceptor : ConditionalTrait<AotAegisInterceptorInfo>, ITick
	{
		readonly Actor self;
		int cooldown;

		public AotAegisInterceptor(Actor self, AotAegisInterceptorInfo info)
			: base(info)
		{
			this.self = self;
		}

		void ITick.Tick(Actor self)
		{
			if (cooldown > 0)
				cooldown--;
		}

		bool ReadyToIntercept => !IsTraitDisabled && cooldown <= 0 && !self.IsDead && self.IsInWorld;

		/// <summary>Nearest ready AEGIS interceptor (enemy of firedBy) whose envelope contains pos, or null.</summary>
		public static AotAegisInterceptor FindInterceptorNear(World world, WPos pos, Player firedBy)
		{
			AotAegisInterceptor best = null;
			var bestDistSq = long.MaxValue;

			foreach (var pair in world.ActorsWithTrait<AotAegisInterceptor>())
			{
				var t = pair.Trait;
				if (!t.ReadyToIntercept)
					continue;

				var a = pair.Actor;
				if (firedBy != null && firedBy.RelationshipWith(a.Owner) != PlayerRelationship.Enemy)
					continue;

				var rangeSq = (long)t.Info.Range.Length * t.Info.Range.Length;

				// Horizontal envelope: a naval AA battery reaches out in a circle regardless of the
				// missile's current altitude along its arc.
				var delta = a.CenterPosition - pos;
				var distSq = (long)delta.X * delta.X + (long)delta.Y * delta.Y;
				if (distSq > rangeSq)
					continue;

				if (distSq < bestDistSq)
				{
					bestDistSq = distSq;
					best = t;
				}
			}

			return best;
		}

		/// <summary>Put the battery on cooldown, fire the launch report, and send a real interceptor
		/// missile homing on the (moving) cluster missile. The airburst + hit sound play when the
		/// interceptor arrives (see AotAegisMissile), so ranges read off the visible rocket flight.</summary>
		public void LaunchInterceptor(World world, Effects.AotClusterMissileLaunch missile)
		{
			cooldown = Info.ReloadDelay;

			if (!string.IsNullOrEmpty(Info.LaunchSound))
				Game.Sound.Play(SoundType.World, Info.LaunchSound, self.CenterPosition);

			var launchPos = self.CenterPosition + new WVec(WDist.Zero, WDist.Zero, Info.InterceptorLaunchHeight);

			world.AddFrameEndTask(w => w.Add(new Projectiles.AotAegisMissile(
				self, launchPos, () => missile.Position, () => missile.RemoveByInterceptor(w),
				Info.InterceptorImage, Info.InterceptorSequence, Info.InterceptorPalette,
				Info.InterceptorSpeed.Length, Info.InterceptorCloseEnough.Length, Info.InterceptorMaxTicks,
				Info.EffectImage, Info.EffectSequence, Info.EffectPalette, Info.InterceptSound,
				Info.TrailImage, Info.TrailSequence, Info.TrailPalette, Info.TrailInterval)));
		}
	}
}
