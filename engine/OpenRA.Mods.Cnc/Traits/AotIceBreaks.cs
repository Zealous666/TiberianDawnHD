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

using System.Collections.Generic;
using System.Linq;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Ground vehicles break through this ice cell: the ice cracks audibly as soon as one",
		"drives onto it, and after a delay it gives way -- the vehicle sinks and the cell is",
		"removed. Neighbouring cells re-pick their sprite automatically (AotIceLayer.Version),",
		"so the hole gets clean inner curves.")]
	sealed class AotIceBreaksInfo : ConditionalTraitInfo
	{
		[Desc("Ticks a breaking unit must STAND STILL on this cell before the ice gives way.",
			"Only stationary ticks count (see StationaryOnly), so driving across is always safe",
			"no matter how slow the vehicle is -- but stopping even briefly is enough to go under.")]
		public readonly int Delay = 15;

		[Desc("Only count ticks while the vehicle is stationary. With this off, slow vehicles",
			"break through while merely crossing a cell, because crossing takes longer than Delay.")]
		public readonly bool StationaryOnly = true;

		[Desc("Locomotors heavy enough to break the ice. Infantry (foot/chem) and anything",
			"hovering or amphibious (hover/aot-hvr/aot-lst/aot-amphibious) are deliberately absent.")]
		public readonly HashSet<string> BreakingLocomotors =
			["wheeled", "heavywheeled", "tracked", "heavytracked"];

		[Desc("Warning: played the moment a breaking vehicle drives onto the ice.")]
		public readonly string[] CrackSounds = ["icecrak1.aud", "icecrak2.aud", "icecrak3.aud"];

		[Desc("Played when the ice actually gives way and the vehicle goes under.")]
		public readonly string[] SplashSounds = ["ssplash1.aud", "ssplash2.aud", "ssplash3.aud"];

		[WeaponReference]
		[Desc("Effect spawned where the vehicle goes under. Replaces its normal death",
			"explosion: the vehicle is Disposed rather than Killed, so FireWarheadsOnDeath",
			"never fires and it sinks with a splash instead of blowing up.")]
		public readonly string SplashWeapon = "AotIceSplash";

		public WeaponInfo SplashWeaponInfo { get; private set; }

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);
			if (!string.IsNullOrEmpty(SplashWeapon))
			{
				if (!rules.Weapons.TryGetValue(SplashWeapon.ToLowerInvariant(), out var weapon))
					throw new YamlException($"Weapons Ruleset does not contain an entry '{SplashWeapon.ToLowerInvariant()}'");

				SplashWeaponInfo = weapon;
			}
		}

		public override object Create(ActorInitializer init) { return new AotIceBreaks(this); }
	}

	sealed class AotIceBreaks : ConditionalTrait<AotIceBreaksInfo>, ITick
	{
		int loaded;
		bool cracked;

		public AotIceBreaks(AotIceBreaksInfo info)
			: base(info) { }

		bool Breaks(Actor a)
		{
			if (a.IsDead || !a.IsInWorld)
				return false;

			// Explizit ausgenommene Aktoren (Marker-Trait, via Basis-Aktor an alle Proxies vererbt).
			if (a.Info.HasTraitInfo<AotIceUnbreakableInfo>())
				return false;

			var mobile = a.TraitOrDefault<Mobile>();
			return mobile != null && Info.BreakingLocomotors.Contains(mobile.Info.Locomotor);
		}

		static bool Standing(Actor a)
		{
			var mobile = a.TraitOrDefault<Mobile>();
			return mobile != null && !mobile.CurrentMovementTypes.HasFlag(MovementType.Horizontal);
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled)
				return;

			var onIce = self.World.ActorMap.GetActorsAt(self.Location).Where(Breaks).ToList();
			if (onIce.Count == 0)
			{
				// Drove off in time: the ice holds and can crack again on the next attempt.
				loaded = 0;
				cracked = false;
				return;
			}

			// The moment a breaking vehicle drives on, the ice cracks as a warning.
			if (!cracked)
			{
				cracked = true;
				if (Info.CrackSounds.Length > 0)
					Game.Sound.Play(SoundType.World, Info.CrackSounds.Random(self.World.SharedRandom), self.CenterPosition);
			}

			// Only standing still eats into the ice. Rolling across, however slowly, is safe.
			if (Info.StationaryOnly && !onIce.Any(Standing))
			{
				loaded = 0;
				return;
			}

			if (++loaded < Info.Delay)
				return;

			// The ice gives way.
			if (Info.SplashSounds.Length > 0)
				Game.Sound.Play(SoundType.World, Info.SplashSounds.Random(self.World.SharedRandom), self.CenterPosition);

			foreach (var a in onIce)
			{
				if (a.IsDead || !a.IsInWorld)
					continue;

				// Dispose, not Kill: killing would trigger the vehicle's own
				// FireWarheadsOnDeath explosion. It sinks, it does not blow up.
				var pos = a.CenterPosition;
				Info.SplashWeaponInfo?.Impact(Target.FromPos(pos), self);
				a.Dispose();
			}

			// Disposing drops the cell from AotIceLayer -> Version++ -> neighbours curve around it.
			self.Dispose();
		}
	}

	[Desc("Marker: this ground vehicle is light enough that it does NOT break through ice.",
		"Put it on the base actor (JEEP/BGGY/BIKE/APC) so every proxy/upgrade variant inherits it.")]
	sealed class AotIceUnbreakableInfo : TraitInfo<AotIceUnbreakable> { }

	sealed class AotIceUnbreakable { }
}
