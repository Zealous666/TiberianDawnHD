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
using OpenRA.GameRules;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[TraitLocation(SystemActors.World)]
	[Desc("aotmod: ground vehicles break through ice. Replaces the old per-cell AotIceBreaks actor",
		"trait -- this single world trait watches the few breaking vehicles instead of all 10k+ ice",
		"cells. A vehicle that stands still on an ice cell for Delay ticks sinks: the cell is removed",
		"from AotIceLayer and the vehicle is Disposed (a splash weapon replaces its death explosion).")]
	sealed class AotIceBreakLayerInfo : TraitInfo, IRulesetLoaded, Requires<AotIceLayerInfo>
	{
		[Desc("Ticks a breaking unit must STAND STILL on an ice cell before it gives way.",
			"Only stationary ticks count (see StationaryOnly), so driving across is always safe.")]
		public readonly int Delay = 15;

		[Desc("Only count ticks while the vehicle is stationary.")]
		public readonly bool StationaryOnly = true;

		[Desc("Locomotors heavy enough to break the ice.")]
		public readonly HashSet<string> BreakingLocomotors =
			["wheeled", "heavywheeled", "tracked", "heavytracked"];

		[Desc("Warning: played the moment a breaking vehicle drives onto the ice.")]
		public readonly string[] CrackSounds = ["icecrak1.aud", "icecrak2.aud", "icecrak3.aud"];

		[Desc("Played when the ice actually gives way and the vehicle goes under.")]
		public readonly string[] SplashSounds = ["ssplash1.aud", "ssplash2.aud", "ssplash3.aud"];

		[WeaponReference]
		[Desc("Effect spawned where the vehicle goes under (replaces its normal death explosion).")]
		public readonly string SplashWeapon = "AotIceSplash";

		public WeaponInfo SplashWeaponInfo { get; private set; }

		public void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			if (!string.IsNullOrEmpty(SplashWeapon))
			{
				if (!rules.Weapons.TryGetValue(SplashWeapon.ToLowerInvariant(), out var weapon))
					throw new YamlException($"Weapons Ruleset does not contain an entry '{SplashWeapon.ToLowerInvariant()}'");

				SplashWeaponInfo = weapon;
			}
		}

		public override object Create(ActorInitializer init) { return new AotIceBreakLayer(init.Self, this); }
	}

	sealed class AotIceBreakLayer : INotifyCreated, ITick
	{
		struct DwellState
		{
			public CPos Cell;
			public int Loaded;
			public bool Cracked;
		}

		readonly AotIceBreakLayerInfo info;
		readonly Dictionary<Actor, DwellState> tracking = [];
		readonly List<Actor> stale = [];
		AotIceLayer layer;

		public AotIceBreakLayer(Actor self, AotIceBreakLayerInfo info)
		{
			this.info = info;
		}

		void INotifyCreated.Created(Actor self)
		{
			// self IS the world actor. World.WorldActor is still null here (it is assigned only after
			// this actor finishes being created), so fetch the sibling trait from self directly.
			layer = self.Trait<AotIceLayer>();
		}

		bool Breaks(Actor a, Mobile mobile)
		{
			if (a.IsDead || !a.IsInWorld)
				return false;

			// Explicitly exempted actors (marker trait, inherited by every proxy/upgrade variant).
			if (a.Info.HasTraitInfo<AotIceUnbreakableInfo>())
				return false;

			return info.BreakingLocomotors.Contains(mobile.Info.Locomotor);
		}

		static bool Standing(Mobile mobile)
		{
			return !mobile.CurrentMovementTypes.HasFlag(MovementType.Horizontal);
		}

		void ITick.Tick(Actor self)
		{
			if (layer == null)
				return;

			var world = self.World;

			// Anything currently tracked that we do not touch this tick has left the ice (or died).
			stale.Clear();
			foreach (var a in tracking.Keys)
				stale.Add(a);

			foreach (var tp in world.ActorsWithTrait<Mobile>())
			{
				var actor = tp.Actor;
				var mobile = tp.Trait;
				if (!Breaks(actor, mobile))
					continue;

				var cell = actor.Location;
				if (!layer.Contains(cell))
					continue;

				stale.Remove(actor);

				if (!tracking.TryGetValue(actor, out var st) || st.Cell != cell)
					st = new DwellState { Cell = cell, Loaded = 0, Cracked = false };

				// The moment a breaking vehicle arrives, the ice cracks as a warning (once per cell).
				if (!st.Cracked)
				{
					st.Cracked = true;
					if (info.CrackSounds.Length > 0)
						Game.Sound.Play(SoundType.World, info.CrackSounds.Random(world.LocalRandom), actor.CenterPosition);
				}

				// Only standing still eats into the ice. Rolling across, however slowly, is safe.
				if (info.StationaryOnly && !Standing(mobile))
					st.Loaded = 0;
				else
					st.Loaded++;

				if (st.Loaded < info.Delay)
				{
					tracking[actor] = st;
					continue;
				}

				// The ice gives way.
				if (info.SplashSounds.Length > 0)
					Game.Sound.Play(SoundType.World, info.SplashSounds.Random(world.LocalRandom), actor.CenterPosition);

				// Dispose, not Kill: killing would trigger the vehicle's own FireWarheadsOnDeath
				// explosion. It sinks, it does not blow up.
				var pos = actor.CenterPosition;
				info.SplashWeaponInfo?.Impact(Target.FromPos(pos), self);
				actor.Dispose();

				layer.Remove(cell);
				tracking.Remove(actor);
			}

			foreach (var a in stale)
				tracking.Remove(a);
		}
	}

	[Desc("Marker: this ground vehicle is light enough that it does NOT break through ice.",
		"Put it on the base actor (JEEP/BGGY/BIKE/APC) so every proxy/upgrade variant inherits it.")]
	sealed class AotIceUnbreakableInfo : TraitInfo<AotIceUnbreakable> { }

	sealed class AotIceUnbreakable { }
}
