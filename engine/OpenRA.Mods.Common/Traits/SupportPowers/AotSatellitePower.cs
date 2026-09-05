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
using System.Collections.Immutable;
using System.Linq;
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

		[Desc("Ticks the entire map stays revealed through fog-of-war (units visible) after ExploreAll. " +
			"Once elapsed the fog returns; the shroud stays explored. 250 ticks = 10s at 25 ticks/s.")]
		public readonly int FogRevealDuration = 250;

		[PaletteReference]
		public readonly string MissilePalette = "effect";

		[GrantedConditionReference]
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

	public class AotSatellitePower : SupportPower, ITick, INotifyKilled, INotifySold, IGameSaveTraitData
	{
		readonly AotSatellitePowerInfo info;
		int revealCountdown = -1;
		int fogCountdown = -1;
		bool fogRevealed;
		bool satelliteUsed;
		int firedToken = Actor.InvalidConditionToken;

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
			if (!string.IsNullOrEmpty(info.FiredCondition) && firedToken == Actor.InvalidConditionToken)
				firedToken = self.GrantCondition(info.FiredCondition);
		}

		void ITick.Tick(Actor self)
		{
			if (revealCountdown > 0 && --revealCountdown == 0)
			{
				revealCountdown = -1;
				satelliteUsed = true;

				// Permanently explore the shroud (stays revealed afterwards), then also lift the
				// fog-of-war across the whole map for a limited time so enemy units become visible.
				self.Owner.Shroud.ExploreAll();
				RevealFog(self);
				fogCountdown = info.FogRevealDuration;
			}

			if (fogCountdown > 0 && --fogCountdown == 0)
			{
				fogCountdown = -1;
				HideFog(self);
			}
		}

		void RevealFog(Actor self)
		{
			if (fogRevealed)
				return;

			var cells = self.World.Map.ProjectedCells.ToArray();
			self.Owner.Shroud.AddSource(this, Shroud.SourceType.Visibility, cells);
			fogRevealed = true;
		}

		void HideFog(Actor self)
		{
			if (!fogRevealed)
				return;

			self.Owner.Shroud.RemoveSource(this);
			fogRevealed = false;
		}

		void ResetShroud(Actor self)
		{
			HideFog(self);
			if (satelliteUsed)
				self.Owner.Shroud.ResetExploration();
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e) { ResetShroud(self); }
		void INotifySold.Selling(Actor self) { }
		void INotifySold.Sold(Actor self) { ResetShroud(self); }

		// Snapshot save/load: the "already fired" state of this one-shot power lives ONLY in the
		// satellite-used condition token (granted on this building) plus the pending reveal countdown --
		// none of which SupportPowerInstance.SaveState captures. Without persisting it, a fresh actor on
		// load has no condition, so RequiresCondition:!satellite-used re-enables the power (the user saw
		// Satellite Surveillance come back "Ready" after loading a save where it was already used).
		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			var fired = firedToken != Actor.InvalidConditionToken;
			if (!fired && revealCountdown < 0 && fogCountdown < 0 && !satelliteUsed)
				return null;

			return
			[
				new MiniYamlNode("Fired", fired ? "true" : "false"),
				new MiniYamlNode("RevealCountdown", revealCountdown.ToStringInvariant()),
				new MiniYamlNode("FogCountdown", fogCountdown.ToStringInvariant()),
				new MiniYamlNode("SatelliteUsed", satelliteUsed ? "true" : "false"),
			];
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, MiniYaml data)
		{
			var d = data.ToDictionary();

			if (d.TryGetValue("Fired", out var f) && f.Value == "true"
				&& firedToken == Actor.InvalidConditionToken && !string.IsNullOrEmpty(info.FiredCondition))
				firedToken = self.GrantCondition(info.FiredCondition);

			if (d.TryGetValue("RevealCountdown", out var rc))
				revealCountdown = Exts.ParseInt32Invariant(rc.Value);

			if (d.TryGetValue("FogCountdown", out var fc))
				fogCountdown = Exts.ParseInt32Invariant(fc.Value);

			if (d.TryGetValue("SatelliteUsed", out var su))
				satelliteUsed = su.Value == "true";

			// If the fog was still lifted when the game was saved, re-establish the visibility source
			// so the remaining reveal window continues after load.
			if (fogCountdown > 0)
				RevealFog(self);
		}
	}
}
