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

using System.Collections.Immutable;
using OpenRA.GameRules;
using OpenRA.Mods.Common.Effects;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("aotmod: Launches a missile visual from the actor, then reveals the entire map. " +
		"Disappears after use via RequiresCondition. Resets when the actor is rebuilt. " +
		"Resets map exploration when the actor is destroyed or sold.")]
	public class AotSatellitePowerInfo : SupportPowerInfo
	{
		[WeaponReference]
		[Desc("Dummy weapon for the NukeLaunch visual. Must exist in ruleset but needs no warheads.")]
		public readonly string MissileWeapon = "aot-satellite-launch";

		[Desc("Image set for the missile (uses 'up' and 'down' sequences — same as nuke).")]
		public readonly string MissileImage = "atomic";

		[Desc("Ascending missile sequence.")]
		public readonly string MissileUp = "up";

		[Desc("Descending missile sequence.")]
		public readonly string MissileDown = "down";

		[Desc("Missile flight velocity in WDist per tick.")]
		public readonly WDist FlightVelocity = new(512);

		[Desc("Total missile flight time in ticks.")]
		public readonly int FlightDelay = 150;

		[Desc("Ticks after activation before ExploreAll is called.")]
		public readonly int RevealDelay = 50;

		[PaletteReference]
		public readonly string MissilePalette = "effect";

		[Desc("Condition granted on the actor after the power fires. Use with RequiresCondition: !<this> to hide the power after use.")]
		public readonly string FiredCondition = "satellite-used";

		public WeaponInfo WeaponInfo { get; private set; }

		public override object Create(ActorInitializer init) { return new AotSatellitePower(init.Self, this); }

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			var weaponToLower = MissileWeapon.ToLowerInvariant();
			if (!rules.Weapons.TryGetValue(weaponToLower, out var weapon))
				throw new YamlException($"Weapons Ruleset does not contain an entry '{weaponToLower}'");
			WeaponInfo = weapon;
			base.RulesetLoaded(rules, ai);
		}
	}

	public class AotSatellitePower : SupportPower, ITick, INotifyKilled, INotifySold
	{
		readonly AotSatellitePowerInfo info;
		int revealCountdown = -1;
		bool satelliteUsed;

		public AotSatellitePower(Actor self, AotSatellitePowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			// Fire immediately without target selection cursor.
			self.World.IssueOrder(new Order(order, self.Owner.PlayerActor, Target.Invalid, false));
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			PlayLaunchSounds();

			var launchPos = self.CenterPosition;

			// Target is far above — missile ascends and "impacts" off-screen with no damage.
			var targetPos = launchPos + new WVec(0, 0, info.FlightVelocity.Length * info.FlightDelay);

			var missile = new NukeLaunch(
				self.Owner,
				info.MissileImage,
				info.WeaponInfo,
				info.MissilePalette,
				info.MissileUp,
				info.MissileDown,
				launchPos,
				targetPos,
				WDist.Zero,
				true,
				info.FlightVelocity,
				0,
				info.FlightDelay,
				false,
				null,
				ImmutableArray<string>.Empty,
				"effect",
				false,
				0,
				1);

			self.World.AddFrameEndTask(w => w.Add(missile));

			revealCountdown = info.RevealDelay;

			// Grant condition to disable/hide the power on this actor instance.
			// When ATEC is sold/destroyed and rebuilt, the new instance has no condition → power resets.
			if (!string.IsNullOrEmpty(info.FiredCondition))
				self.GrantCondition(info.FiredCondition);
		}

		void ITick.Tick(Actor self)
		{
			if (revealCountdown < 0)
				return;

			if (--revealCountdown == 0)
			{
				revealCountdown = -1;
				satelliteUsed = true;
				self.Owner.Shroud.ExploreAll();
			}
		}

		void ResetShroud(Actor self)
		{
			if (satelliteUsed)
				self.Owner.Shroud.ResetExploration();
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e) { ResetShroud(self); }
		void INotifySold.Selling(Actor self) { }
		void INotifySold.Sold(Actor self) { ResetShroud(self); }
	}
}
