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
using OpenRA.Effects;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Effects;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("aotmod: Flies dropships in from the map edge as a purely cosmetic effect. An invisible " +
		"per-drop timer (no actor, nothing visible until it fires) waits roughly as long as the " +
		"cosmetic flight would take, then bursts (a directly-spawned SpriteEffect, NOT a Warhead/" +
		"FireWarheadsOnDeath chain — that path proved unreliable for purely visual, non-combat " +
		"explosions in this mod) and delivers the trooper.")]
	public class AotZoneTrooperDropPowerInfo : SupportPowerInfo
	{
		[ActorReference(typeof(AircraftInfo))]
		[Desc("Actor used both for the cosmetic flying dropship and the stationary detonator prop.")]
		public readonly string Dropship = "aot-dropship";

		[ActorReference(typeof(ParachutableInfo))]
		[Desc("Trooper actor delivered at each drop point.")]
		public readonly string Trooper = "aot-zonetrooper";

		[Desc("Number of troopers/dropships to spawn.")]
		public readonly int Count = 5;

		[Desc("Radius around the target cell in which each detonation point is randomly picked.")]
		public readonly WDist Radius = WDist.FromCells(3);

		[Desc("Distance between the dropships in the incoming formation.")]
		public readonly WVec SquadOffset = new(-1536, 1536, 0);

		[Desc("Number of facings that the dropships may approach from.")]
		public readonly int QuantizedFacings = 8;

		[Desc("Spawn the dropships this far outside the map.")]
		public readonly WDist Cordon = new(5120);

		[Desc("Image containing the burst explosion sequence.")]
		public readonly string ExplosionImage = "explosion";

		[SequenceReference(nameof(ExplosionImage))]
		[Desc("Explosion sequence to play at each drop point.")]
		public readonly string ExplosionSequence = "small_building";

		[PaletteReference]
		public readonly string ExplosionPalette = "effect";

		[Desc("Sound to play at each drop point when it bursts.")]
		public readonly string ExplosionSound = "xplos.aud";

		[NotificationReference("Speech")]
		[Desc("Speech notification to play once the drop begins.")]
		public readonly string ReinforcementsArrivedSpeechNotification = null;

		[FluentReference(optional: true)]
		[Desc("Text notification to display once the drop begins.")]
		public readonly string ReinforcementsArrivedTextNotification = null;

		public override object Create(ActorInitializer init) { return new AotZoneTrooperDropPower(init.Self, this); }
	}

	public class AotZoneTrooperDropPower : SupportPower
	{
		readonly AotZoneTrooperDropPowerInfo info;

		public AotZoneTrooperDropPower(Actor self, AotZoneTrooperDropPowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);

			var targetCenter = order.Target.CenterPosition;

			var utLower = info.Dropship.ToLowerInvariant();
			if (!self.World.Map.Rules.Actors.TryGetValue(utLower, out var unitType))
				throw new YamlException($"Actors ruleset does not include the entry '{utLower}'");

			var aircraftInfo = unitType.TraitInfo<AircraftInfo>();
			var altitude = aircraftInfo.CruiseAltitude.Length;
			var speed = aircraftInfo.Speed;
			var facing = new WAngle(1024 * self.World.SharedRandom.Next(info.QuantizedFacings) / info.QuantizedFacings);
			var dropRotation = WRot.FromYaw(facing);
			var delta = new WVec(0, -1024, 0).Rotate(dropRotation);
			var approachTarget = targetCenter + new WVec(0, 0, altitude);
			var startEdge = approachTarget - (self.World.Map.DistanceToEdge(approachTarget, -delta) + info.Cordon).Length * delta / 1024;

			self.World.AddFrameEndTask(w =>
			{
				PlayLaunchSounds();

				Game.Sound.PlayNotification(self.World.Map.Rules, self.Owner, "Speech",
					info.ReinforcementsArrivedSpeechNotification, self.Owner.Faction.InternalName);
				TextNotificationsManager.AddTransientLine(self.Owner, info.ReinforcementsArrivedTextNotification);

				Actor distanceTestActor = null;
				var maxWaitTicks = 0;

				for (var i = -info.Count / 2; i <= info.Count / 2; i++)
				{
					// Even-sized squads skip the lead dropship.
					if (i == 0 && (info.Count & 1) == 0)
						continue;

					var so = info.SquadOffset;
					var spawnOffset = new WVec(i * so.Y, -System.Math.Abs(i) * so.X, 0).Rotate(dropRotation);

					// Randomized detonation point near the target for this dropship.
					var angle = WAngle.FromFacing(w.SharedRandom.Next(1024));
					var dist = w.SharedRandom.Next(info.Radius.Length);
					var offset = new WVec(dist, 0, 0).Rotate(WRot.FromYaw(angle));
					var burstPoint = targetCenter + offset + new WVec(0, 0, altitude);
					var spawnPos = startEdge + spawnOffset;

					// Purely cosmetic flyover. Its own arrival/collision/pathing state has NO
					// bearing on whether the burst below actually happens.
					var dropship = w.CreateActor(false, info.Dropship,
					[
						new CenterPositionInit(spawnPos),
						new OwnerInit(self.Owner),
						new FacingInit(facing),
					]);
					w.Add(dropship);
					dropship.QueueActivity(new Fly(dropship, Target.FromPos(burstPoint)));
					dropship.QueueActivity(new RemoveSelf());
					distanceTestActor ??= dropship;

					// Invisible timer (no actor, no render output at all -- nothing hangs in the air
					// beforehand) that waits roughly as long as the cosmetic dropship's flight would
					// take, then bursts and delivers the trooper. Fully decoupled from any actor's
					// activity/collision/pathing state, so it always fires reliably.
					var travelDistance = (spawnPos - burstPoint).Length;
					var waitTicks = speed > 0 ? travelDistance / speed : 0;
					if (waitTicks > maxWaitTicks)
						maxWaitTicks = waitTicks;
					w.Add(new AotZoneTrooperBurstTimer(waitTicks, burstPoint, self.Owner, info));
				}

				// On-map target marker showing the drop's own icon; the clock fills as the lead
				// dropship approaches, and the beacon auto-removes once the squad has arrived.
				if (Info.DisplayBeacon && distanceTestActor != null)
				{
					var distance = (startEdge - approachTarget).HorizontalLength;
					var beacon = new Beacon(
						self.Owner,
						targetCenter,
						Info.BeaconPaletteIsPlayerPalette,
						Info.BeaconPalette,
						Info.BeaconImage,
						Info.BeaconPoster,
						Info.BeaconPosterPalette,
						Info.BeaconSequence,
						Info.ArrowSequence,
						Info.CircleSequence,
						Info.ClockSequence,
						() => distance == 0 ? 1f : 1 - (distanceTestActor.CenterPosition - approachTarget).HorizontalLength * 1f / distance,
						Info.BeaconDelay,
						maxWaitTicks);

					w.Add(beacon);
				}
			});
		}
	}

	// Invisible countdown effect: no Render output, so nothing is visible until it fires. On
	// completion it spawns a burst SpriteEffect + sound and delivers the trooper, all at once.
	sealed class AotZoneTrooperBurstTimer : IEffect
	{
		readonly WPos pos;
		readonly Player owner;
		readonly AotZoneTrooperDropPowerInfo info;
		int delay;

		public AotZoneTrooperBurstTimer(int delay, WPos pos, Player owner, AotZoneTrooperDropPowerInfo info)
		{
			this.delay = delay;
			this.pos = pos;
			this.owner = owner;
			this.info = info;
		}

		public void Tick(World world)
		{
			if (--delay > 0)
				return;

			world.AddFrameEndTask(w =>
			{
				w.Remove(this);

				w.Add(new SpriteEffect(pos, w, info.ExplosionImage, info.ExplosionSequence,
					info.ExplosionPalette, visibleThroughFog: true));
				Game.Sound.Play(SoundType.World, info.ExplosionSound, pos);

				var trooper = w.CreateActor(false, info.Trooper,
				[
					new LocationInit(w.Map.CellContaining(pos)),
					new OwnerInit(owner),
				]);

				var positionable = trooper.Trait<IPositionable>();
				positionable.SetPosition(trooper, w.Map.CellContaining(pos));
				positionable.SetCenterPosition(trooper, pos);
				w.Add(trooper);
				trooper.QueueActivity(new Parachute(trooper));
			});
		}

		public IEnumerable<IRenderable> Render(WorldRenderer wr) { yield break; }
	}
}
