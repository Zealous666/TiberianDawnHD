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
using OpenRA.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("aotmod: Flies a transport aircraft in from the map edge, lands at the target, unloads " +
		"a set of vehicles (tier depends on the player's current upgrades), then takes off and " +
		"leaves the map. Uses the engine's Land/UnloadCargo/FlyOffMap activities directly.")]
	public class AotOrcaTransportPowerInfo : SupportPowerInfo
	{
		[ActorReference(typeof(AircraftInfo))]
		[Desc("Transport aircraft used for delivery.")]
		public readonly string ActorType = "aot-orcatran";

		[Desc("Number of facings the aircraft may approach from.")]
		public readonly int QuantizedFacings = 8;

		[Desc("Spawn/remove the aircraft this far outside the map.")]
		public readonly WDist Cordon = new(5120);

		[Desc("Range around the target at which the aircraft starts landing.")]
		public readonly WDist LandRange = WDist.FromCells(2);

		[ActorReference(typeof(PassengerInfo))]
		public readonly string JeepBase = "aot-jeep-base";

		[ActorReference(typeof(PassengerInfo))]
		public readonly string JeepHumvee = "aot-jeep-humvee";

		[ActorReference(typeof(PassengerInfo))]
		public readonly string JeepApc = "APC";

		[Desc("Prerequisite that upgrades the delivered light vehicle to the Humvee variant.")]
		public readonly string HumveePrerequisite = "aot-humvee-upgrade";

		[Desc("Prerequisite that upgrades the delivered light vehicle to the APC variant.")]
		public readonly string ApcPrerequisite = "aot-apc-upgrade";

		[Desc("Number of light vehicles to deliver.")]
		public readonly int JeepCount = 2;

		[ActorReference(typeof(PassengerInfo))]
		public readonly string TankBase = "aot-mtnk-base";

		[ActorReference(typeof(PassengerInfo))]
		public readonly string TankPredator = "aot-mtnk-pred";

		[ActorReference(typeof(PassengerInfo))]
		public readonly string TankRailgun = "aot-mtnk-rail";

		[Desc("Prerequisite that upgrades the delivered tank to the Predator variant.")]
		public readonly string PredatorPrerequisite = "aot-mtnk-predator-upgrade";

		[Desc("Prerequisite that upgrades the delivered tank to the Railgun variant.")]
		public readonly string RailgunPrerequisite = "aot-mtnk-railgun-upgrade";

		[Desc("Number of tanks to deliver.")]
		public readonly int TankCount = 2;

		[NotificationReference("Speech")]
		[Desc("Speech notification to play once the transport lands.")]
		public readonly string ReinforcementsArrivedSpeechNotification = null;

		[FluentReference(optional: true)]
		[Desc("Text notification to display once the transport lands.")]
		public readonly string ReinforcementsArrivedTextNotification = null;

		public override object Create(ActorInitializer init) { return new AotOrcaTransportPower(init.Self, this); }
	}

	public class AotOrcaTransportPower : SupportPower
	{
		readonly AotOrcaTransportPowerInfo info;

		public AotOrcaTransportPower(Actor self, AotOrcaTransportPowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);

			var target = order.Target.CenterPosition;

			var utLower = info.ActorType.ToLowerInvariant();
			if (!self.World.Map.Rules.Actors.TryGetValue(utLower, out var unitType))
				throw new YamlException($"Actors ruleset does not include the entry '{utLower}'");

			var altitude = unitType.TraitInfo<AircraftInfo>().CruiseAltitude.Length;
			var facing = new WAngle(1024 * self.World.SharedRandom.Next(info.QuantizedFacings) / info.QuantizedFacings);
			var dropRotation = WRot.FromYaw(facing);
			var delta = new WVec(0, -1024, 0).Rotate(dropRotation);
			var approachTarget = target + new WVec(0, 0, altitude);
			var startEdge = approachTarget - (self.World.Map.DistanceToEdge(approachTarget, -delta) + info.Cordon).Length * delta / 1024;
			var finishEdge = approachTarget + (self.World.Map.DistanceToEdge(approachTarget, delta) + info.Cordon).Length * delta / 1024;

			// Pick the current tier of each vehicle type based on the player's upgrades.
			var techTree = self.Owner.PlayerActor.Trait<TechTree>();

			var jeepActor = info.JeepBase;
			if (techTree.HasPrerequisites(new[] { info.ApcPrerequisite }))
				jeepActor = info.JeepApc;
			else if (techTree.HasPrerequisites(new[] { info.HumveePrerequisite }))
				jeepActor = info.JeepHumvee;

			var tankActor = info.TankBase;
			if (techTree.HasPrerequisites(new[] { info.RailgunPrerequisite }))
				tankActor = info.TankRailgun;
			else if (techTree.HasPrerequisites(new[] { info.PredatorPrerequisite }))
				tankActor = info.TankPredator;

			var dropItems = new List<string>();
			for (var i = 0; i < info.JeepCount; i++)
				dropItems.Add(jeepActor);
			for (var i = 0; i < info.TankCount; i++)
				dropItems.Add(tankActor);

			// Create actors up-front, add to world in the frame-end task below (matches the
			// engine's own ParatroopersPower pattern).
			var aircraft = self.World.CreateActor(false, info.ActorType,
			[
				new CenterPositionInit(startEdge),
				new OwnerInit(self.Owner),
				new FacingInit(facing),
			]);

			var units = new List<Actor>();
			foreach (var actorType in dropItems)
				units.Add(self.World.CreateActor(false, actorType.ToLowerInvariant(), [new OwnerInit(self.Owner)]));

			self.World.AddFrameEndTask(w =>
			{
				PlayLaunchSounds();

				w.Add(aircraft);

				var cargo = aircraft.Trait<Cargo>();
				foreach (var unit in units)
					cargo.Load(aircraft, unit);

				// UnloadCargo internally queues Land + unloads all passengers + takes off again.
				aircraft.QueueActivity(new UnloadCargo(aircraft, Target.FromPos(target), info.LandRange));
				aircraft.QueueActivity(new CallFunc(() =>
				{
					Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech",
						info.ReinforcementsArrivedSpeechNotification, self.Owner.Faction.InternalName);
					TextNotificationsManager.AddTransientLine(self.Owner, info.ReinforcementsArrivedTextNotification);
				}));
				aircraft.QueueActivity(new FlyOffMap(aircraft, Target.FromPos(finishEdge)));
				aircraft.QueueActivity(new RemoveSelf());
			});
		}
	}
}
