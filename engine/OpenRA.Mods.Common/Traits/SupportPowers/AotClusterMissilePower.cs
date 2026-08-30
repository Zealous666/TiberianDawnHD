#region Copyright & License Information
/*
 * Age of Tiberium mod — support power that launches the flying cluster missile
 * (AotClusterMissileLaunch) instead of the stock vertical NukeLaunch.
 *
 * It reuses the whole NukePowerInfo surface (weapon, beacon, camera, radar ping, charge,
 * notifications) and only swaps the delivery: the missile flies a continuous ballistic arc
 * from the silo into the target with a player-coloured contrail — see the effect for the
 * flight math. SupportPowerInfo.OrderName derives from the Info type name, so the three
 * concrete subclasses below give the three MSLO variants (Mine Cluster / Enhanced /
 * Tiberium) distinct order names while sharing one behavior class.
 */
#endregion

using System;
using System.Collections.Generic;
using OpenRA.Mods.Common.Effects;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	// Base carrying the extra flight fields + the swapped Create(). Not used directly in
	// YAML (would collide on OrderName) — the concrete subclasses at the bottom are.
	public abstract class AotClusterMissilePowerInfo : NukePowerInfo
	{
		[Desc("Apex height (in WDist) of the ballistic arc above the launch-to-target line.")]
		public readonly WDist CruiseAltitude = new(3072);

		[Desc("Constant horizontal flight speed in WDist/tick — travel time scales with distance",
			"so the missile always moves at the same pace (roughly a Firehawk).")]
		public readonly WDist MissileSpeed = new(224);

		[Desc("Minimum ticks in the air, so very close targets still arc instead of snapping.")]
		public readonly int MinFlightTicks = 40;

		[Desc("Where the arc peaks, as a percentage of the flight (smaller = steeper launch).")]
		public readonly int ApexPercent = 40;

		[Desc("Peak lateral sway (in WDist) of the mid-flight wobble; 0 disables it.")]
		public readonly WDist WobbleAmplitude = new(256);

		[Desc("Number of full side-to-side sways over the flight.")]
		public readonly int WobbleCycles = 2;

		[SequenceReference(nameof(MissileImage))]
		[Desc("Sprite sequence for the RGBA body of the missile.")]
		public readonly string BodySequence = "idle";

		[SequenceReference(nameof(MissileImage), allowNullImage: true)]
		[Desc("Indexed player-colour remap overlay sequence; empty for no overlay.")]
		public readonly string OverlaySequence = "idle-remap";

		[PaletteReference(nameof(OverlayIsPlayerPalette))]
		[Desc("Palette used to render the overlay sequence.")]
		public readonly string OverlayPalette = "player";

		[Desc("Overlay palette is a player palette BaseName.")]
		public readonly bool OverlayIsPlayerPalette = true;

		[Desc("Number of positions to draw in the trailing contrail; 0 disables it.")]
		public readonly int ContrailLength = 24;

		[Desc("Thickness of the contrail at the missile end.")]
		public readonly WDist ContrailStartWidth = new(96);

		[Desc("Thickness of the contrail at the tail; defaults to " + nameof(ContrailStartWidth) + ".")]
		public readonly WDist? ContrailEndWidth = null;

		[Desc("RGB colour at the contrail start (ignored when using the player colour).")]
		public readonly Color ContrailStartColor = Color.White;

		[Desc("Use the player remap colour at the contrail start.")]
		public readonly bool ContrailStartColorUsePlayerColor = true;

		[Desc("Alpha [0-255] of the contrail start colour.")]
		public readonly int ContrailStartColorAlpha = 255;

		[Desc("RGB colour at the contrail tail; defaults to the start colour.")]
		public readonly Color? ContrailEndColor = null;

		[Desc("Use the player remap colour at the contrail tail.")]
		public readonly bool ContrailEndColorUsePlayerColor = true;

		[Desc("Alpha [0-255] of the contrail tail colour.")]
		public readonly int ContrailEndColorAlpha = 0;

		[Desc("Delay in ticks before the contrail starts drawing.")]
		public readonly int ContrailDelay = 0;

		[Desc("Z offset applied to the contrail so it draws above the ground.")]
		public readonly int ContrailZOffset = 2047;

		[Desc("How far behind the missile centre (along its heading) the contrail is emitted,",
			"so the trail starts at the tail rather than the middle of the sprite.")]
		public readonly WDist ContrailRearOffset = new(320);

		public override object Create(ActorInitializer init) { return new AotClusterMissilePower(init.Self, this); }
	}

	sealed class AotClusterMissilePower : SupportPower
	{
		readonly AotClusterMissilePowerInfo info;
		BodyOrientation body;

		public AotClusterMissilePower(Actor self, AotClusterMissilePowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		protected override void Created(Actor self)
		{
			body = self.TraitOrDefault<BodyOrientation>();
			base.Created(self);
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			PlayLaunchSounds();

			Activate(self, order.Target.CenterPosition);
		}

		public void Activate(Actor self, WPos targetPosition)
		{
			var bodyPalette = info.IsPlayerPalette ? info.MissilePalette + self.Owner.InternalName : info.MissilePalette;
			var overlayPalette = info.OverlayIsPlayerPalette ? info.OverlayPalette + self.Owner.InternalName : info.OverlayPalette;
			var launchPos = self.CenterPosition + (body != null ? body.LocalToWorld(info.SpawnOffset) : info.SpawnOffset);

			var contrailStart = Color.FromArgb(info.ContrailStartColorAlpha, info.ContrailStartColor);
			var contrailEnd = Color.FromArgb(info.ContrailEndColorAlpha, info.ContrailEndColor ?? info.ContrailStartColor);

			// Constant ground speed: derive the flight time from the launch-to-target distance
			// (clamped so very close targets still arc), so it never speeds up over range.
			var distance = (targetPosition - launchPos).HorizontalLength;
			var speed = Math.Max(1, info.MissileSpeed.Length);
			var totalFlight = Math.Max(info.MinFlightTicks, distance / speed);

			var missile = new AotClusterMissileLaunch(self.Owner, info.MissileImage, info.WeaponInfo,
				bodyPalette, overlayPalette, info.BodySequence, info.OverlaySequence,
				launchPos, targetPosition, info.CruiseAltitude,
				info.MissileDelay, totalFlight, info.ApexPercent,
				info.WobbleAmplitude.Length, info.WobbleCycles,
				info.DetonationAltitude, info.RemoveMissileOnDetonation,
				info.ContrailLength, info.ContrailStartWidth, info.ContrailEndWidth ?? info.ContrailStartWidth,
				contrailStart, info.ContrailStartColorUsePlayerColor,
				contrailEnd, info.ContrailEndColor == null ? info.ContrailStartColorUsePlayerColor : info.ContrailEndColorUsePlayerColor,
				info.ContrailDelay, info.ContrailZOffset, info.ContrailRearOffset.Length);

			self.World.AddFrameEndTask(w => w.Add(missile));

			if (info.CameraRange != WDist.Zero)
			{
				var type = info.RevealGeneratedShroud ? Shroud.SourceType.Visibility
					: Shroud.SourceType.PassiveVisibility;

				self.World.AddFrameEndTask(w => w.Add(new RevealShroudEffect(targetPosition, info.CameraRange, type, self.Owner, info.CameraRelationships,
					totalFlight - info.CameraSpawnAdvance, info.CameraSpawnAdvance + info.CameraRemoveDelay)));
			}

			if (Info.DisplayBeacon)
			{
				var beacon = new Beacon(
					self.Owner,
					targetPosition,
					Info.BeaconPaletteIsPlayerPalette,
					Info.BeaconPalette,
					Info.BeaconImage,
					Info.BeaconPoster,
					Info.BeaconPosterPalette,
					Info.BeaconSequence,
					Info.ArrowSequence,
					Info.CircleSequence,
					Info.ClockSequence,
					() => missile.FractionComplete,
					Info.BeaconDelay,
					totalFlight - info.BeaconRemoveAdvance);

				self.World.AddFrameEndTask(w => w.Add(beacon));
			}
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			self.World.OrderGenerator = new SelectNukePowerTarget(order, manager, info);
		}
	}

	// Concrete variants — one OrderName each (GetType().Name + "Order"), all sharing the
	// AotClusterMissilePower behavior. Enhanced/Tiberium keep their historic class names so
	// their existing SupportPowerChargeBar / bot OrderName references stay valid; only the
	// base Mine Cluster variant gets a fresh name (was stock NukePower before).
	public sealed class AotMineClusterMissilePowerInfo : AotClusterMissilePowerInfo { }

	public sealed class AotMineClusterEnhancedPowerInfo : AotClusterMissilePowerInfo { }

	public sealed class AotTiberiumMissilePowerInfo : AotClusterMissilePowerInfo { }
}
