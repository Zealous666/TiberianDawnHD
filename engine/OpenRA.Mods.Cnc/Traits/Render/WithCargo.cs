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

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Mods.Common.Traits;
using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits.Render
{
	[Desc("Renders the cargo loaded into the unit.")]
	public class WithCargoInfo : TraitInfo, Requires<CargoInfo>, Requires<BodyOrientationInfo>
	{
		[Desc("Cargo position relative to turret or body in (forward, right, up) triples. The default offset should be in the middle of the list.")]
		public readonly ImmutableArray<WVec> LocalOffset = [WVec.Zero];

		[Desc("Passenger CargoType to display.")]
		public readonly FrozenSet<string> DisplayTypes = FrozenSet<string>.Empty;

		[Desc("Z-order bias for the rendered passenger's body/shadow relative to the carrier's own sprites. " +
			"Positive draws the passenger in front (e.g. a vehicle riding on top of a landing craft), " +
			"negative draws it behind (e.g. a vehicle sitting inside a garrisoned defense structure).")]
		public readonly int PassengerZOffset = 1;

		[Desc("Z-order bias for the rendered passenger's turret (if any). Defaults to PassengerZOffset. " +
			"Set higher than PassengerZOffset to let a turret poke out over the carrier's own sprites " +
			"(e.g. a sandbag frame) while the hull stays hidden behind them.")]
		public readonly int? PassengerTurretZOffset = null;

		public override object Create(ActorInitializer init) { return new WithCargo(init.Self, this); }
	}

	public class WithCargo : ITick, IRender, INotifyPassengerEntered, INotifyPassengerExited
	{
		sealed class PassengerPreview
		{
			public IActorPreview[] Body;
			public IActorPreview[] Turret;
		}

		readonly WithCargoInfo info;
		readonly Cargo cargo;
		readonly BodyOrientation body;
		readonly IFacing facing;
		readonly int turretZOffset;
		WAngle cachedFacing;

		readonly Dictionary<Actor, PassengerPreview> previews = [];

		// A passenger's turret/body sprite can be conditional (Predator/Railgun/Laser/Humvee/
		// Flame/Toxin/etc. upgrades). The cached preview above is only ever built once per
		// boarding, so if the passenger's relevant conditions change later - e.g. an upgrade
		// is researched while the vehicle is already garrisoned - the old sprite would keep
		// showing forever. Track each passenger's conditional render traits' enabled state and
		// null out its cached preview (forcing regeneration in Render()) the moment it changes.
		readonly Dictionary<Actor, IDisabledTrait[]> conditionalRenderTraits = [];
		readonly Dictionary<Actor, bool[]> conditionalRenderTraitStates = [];

		public WithCargo(Actor self, WithCargoInfo info)
		{
			this.info = info;

			cargo = self.Trait<Cargo>();
			body = self.Trait<BodyOrientation>();
			facing = self.TraitOrDefault<IFacing>();
			turretZOffset = info.PassengerTurretZOffset ?? info.PassengerZOffset;
		}

		void ITick.Tick(Actor self)
		{
			foreach (var (passenger, traits) in conditionalRenderTraits)
			{
				var states = conditionalRenderTraitStates[passenger];
				for (var i = 0; i < traits.Length; i++)
				{
					var disabled = traits[i].IsTraitDisabled;
					if (disabled == states[i])
						continue;

					states[i] = disabled;
					previews[passenger] = null;
				}
			}

			foreach (var preview in previews.Values)
			{
				if (preview == null)
					continue;

				foreach (var p in preview.Body)
					p.Tick();
				foreach (var p in preview.Turret)
					p.Tick();
			}

			// HACK: We don't have an efficient way to know when the preview
			// bounds change, so assume that we need to update the screen map
			// (only) when the facing changes. Carriers without IFacing (e.g.
			// stationary buildings) never rotate, so there's nothing to track.
			if (facing != null && facing.Facing != cachedFacing && previews.Count > 0)
			{
				self.World.ScreenMap.AddOrUpdate(self);
				cachedFacing = facing.Facing;
			}
		}

		IEnumerable<IRenderable> IRender.Render(Actor self, WorldRenderer wr)
		{
			var bodyOrientation = body.QuantizeOrientation(self.Orientation);
			var pos = self.CenterPosition;
			var i = 0;

			// Generate missing previews
			var missing = previews
				.Where(kv => kv.Value == null)
				.Select(kv => kv.Key)
				.ToList();

			foreach (var p in missing)
			{
				// Prefer the passenger's own facing (kept live by e.g. AttackGarrisoned
				// while it fires out of the carrier) over the carrier's facing, which
				// is all we have left when the carrier itself has no IFacing (buildings).
				// Quantize using the PASSENGER's own BodyOrientation/facings count, not
				// the carrier's - a static building carrier only has 1 facing, which
				// would otherwise collapse every passenger angle down to the same bucket.
				var passengerFacing = p.TraitOrDefault<IFacing>();
				var passengerBody = p.TraitOrDefault<BodyOrientation>();
				var passengerInits = new TypeDictionary()
				{
					new OwnerInit(p.Owner),
					new DynamicFacingInit(() =>
					{
						var rawFacing = passengerFacing?.Facing ?? facing?.Facing ?? WAngle.Zero;
						return passengerBody != null ? passengerBody.QuantizeFacing(rawFacing) : rawFacing;
					}),
				};

				foreach (var api in p.TraitsImplementing<IActorPreviewInitModifier>())
					api.ModifyActorPreviewInit(p, passengerInits);

				var init = new ActorPreviewInitializer(p.Info, wr, passengerInits);

				// Mirror RenderSpritesInfo.RenderPreview's image/facings/palette resolution
				// ourselves so we can split turret previews from body/shadow/other previews
				// and give them a different Z-order bias (see PassengerTurretZOffset).
				var rsInfo = p.Info.TraitInfo<RenderSpritesInfo>();
				var sequences = init.World.Map.Sequences;
				var faction = init.GetValue<FactionInit, string>(rsInfo);
				var ownerName = init.Get<OwnerInit>().InternalName;
				var image = rsInfo.GetImage(p.Info, faction);
				var palette = init.WorldRenderer.Palette(rsInfo.Palette ?? rsInfo.PlayerPalette + ownerName);

				var facings = 0;
				var bodyInfo = p.Info.TraitInfoOrDefault<BodyOrientationInfo>();
				if (bodyInfo != null)
				{
					facings = bodyInfo.QuantizedFacings;
					if (facings == -1)
					{
						var qbo = p.Info.TraitInfoOrDefault<IQuantizeBodyOrientationInfo>();
						facings = qbo?.QuantizedBodyFacings(p.Info, sequences, faction) ?? 1;
					}
				}

				var bodyPreviews = new List<IActorPreview>();
				var turretPreviews = new List<IActorPreview>();

				// WithSpriteTurret/WithSpriteBody(+WithFacingSpriteBody) are the conditional
				// sprite-switch traits every vehicle in this mod uses for turret/body upgrades
				// (Predator/Railgun/Laser/Humvee/Flame/Toxin/etc.). Their TraitInfo-level
				// RenderPreviewSprites() gates on EnabledByDefault, which is computed once at
				// ruleset load time assuming no conditions are granted - it never reflects a
				// real, live passenger's actual condition state (e.g. an upgrade researched
				// while the vehicle is already garrisoned in a Fire Position). Read the real,
				// live trait instance's IsTraitDisabled instead so the rendered sprite tracks
				// the passenger's actual, current tech level. Logic below mirrors
				// WithSpriteTurretInfo/WithSpriteBodyInfo/WithFacingSpriteBodyInfo's own
				// RenderPreviewSprites(), minus that stale gate.
				foreach (var t in p.TraitsImplementing<WithSpriteTurret>())
				{
					if (t.IsTraitDisabled)
						continue;

					var turretSpriteInfo = t.Info;
					var turretedInfo = p.Info.TraitInfos<TurretedInfo>().First(tt => tt.Turret == turretSpriteInfo.Turret);
					var turretFacing = turretedInfo.WorldFacingFromInit(init);
					var turretAnim = new Animation(init.World, image, turretFacing);
					turretAnim.Play(RenderSprites.NormalizeSequence(turretAnim, init.GetDamageState(), turretSpriteInfo.Sequence));

					var passengerFacingFunc = init.GetFacing();
					WRot TurretOrientation() => bodyInfo.QuantizeOrientation(WRot.FromYaw(passengerFacingFunc()), facings);
					WVec TurretOffset() => bodyInfo.LocalToWorld(turretedInfo.Offset.Rotate(TurretOrientation()));
					int TurretZOffset()
					{
						var tmpOffset = TurretOffset();
						return -(tmpOffset.Y + tmpOffset.Z) + 1;
					}

					var turretPalette = palette;
					if (turretSpriteInfo.IsPlayerPalette)
						turretPalette = init.WorldRenderer.Palette(turretSpriteInfo.Palette + ownerName);
					else if (turretSpriteInfo.Palette != null)
						turretPalette = init.WorldRenderer.Palette(turretSpriteInfo.Palette);

					turretPreviews.Add(new SpriteActorPreview(turretAnim, TurretOffset, TurretZOffset, turretPalette));
				}

				foreach (var t in p.TraitsImplementing<WithSpriteBody>())
				{
					if (t.IsTraitDisabled)
						continue;

					var spriteBodyInfo = t.Info;
					var bodyAnim = spriteBodyInfo is WithFacingSpriteBodyInfo
						? new Animation(init.World, image, init.GetFacing())
						: new Animation(init.World, image);
					bodyAnim.PlayRepeating(RenderSprites.NormalizeSequence(bodyAnim, init.GetDamageState(), spriteBodyInfo.Sequence));

					var bodyPalette = palette;
					if (spriteBodyInfo.IsPlayerPalette)
						bodyPalette = init.WorldRenderer.Palette(spriteBodyInfo.Palette + ownerName);
					else if (spriteBodyInfo.Palette != null)
						bodyPalette = init.WorldRenderer.Palette(spriteBodyInfo.Palette);

					bodyPreviews.Add(new SpriteActorPreview(bodyAnim, () => WVec.Zero, () => 0, bodyPalette));
				}

				// Anything else implementing IRenderActorPreviewSpritesInfo (e.g. shadows) isn't
				// conditionally sprite-switched in this mod, so the stock EnabledByDefault gate
				// (assumes no conditions granted) is fine as-is.
				foreach (var spi in p.Info.TraitInfos<IRenderActorPreviewSpritesInfo>())
				{
					if (spi is WithSpriteTurretInfo || spi is WithSpriteBodyInfo)
						continue;

					foreach (var preview in spi.RenderPreviewSprites(init, image, facings, palette))
						bodyPreviews.Add(preview);
				}

				previews[p] = new PassengerPreview { Body = bodyPreviews.ToArray(), Turret = turretPreviews.ToArray() };
			}

			foreach (var preview in previews.Values)
			{
				if (preview == null)
					continue;

				var index = cargo.PassengerCount > 1 ? i++ % info.LocalOffset.Length : info.LocalOffset.Length / 2;
				var localOffset = info.LocalOffset[index];
				var renderPos = pos + body.LocalToWorld(localOffset.Rotate(bodyOrientation));

				// Negative bias so the passenger renders behind the carrier's own
				// body sprite (e.g. a sandbag frame around a garrisoned vehicle)
				// instead of on top of it. Was a flat "+1" (effectively no bias).
				foreach (var p in preview.Body)
					foreach (var pp in p.Render(wr, renderPos))
						yield return pp.WithZOffset(info.PassengerZOffset);

				// Turret gets its own (usually less negative / positive) bias so it can
				// poke out over the carrier's own sprites while the hull stays hidden.
				foreach (var p in preview.Turret)
					foreach (var pp in p.Render(wr, renderPos))
						yield return pp.WithZOffset(turretZOffset);
			}
		}

		IEnumerable<Rectangle> IRender.ScreenBounds(Actor self, WorldRenderer wr)
		{
			var pos = self.CenterPosition;
			foreach (var preview in previews.Values)
			{
				if (preview == null)
					continue;

				foreach (var p in preview.Body)
					foreach (var b in p.ScreenBounds(wr, pos))
						yield return b;
				foreach (var p in preview.Turret)
					foreach (var b in p.ScreenBounds(wr, pos))
						yield return b;
			}
		}

		void INotifyPassengerEntered.OnPassengerEntered(Actor self, Actor passenger)
		{
			if (info.DisplayTypes.Contains(passenger.Trait<Passenger>().Info.CargoType))
			{
				previews.Add(passenger, null);

				var traits = passenger.TraitsImplementing<WithSpriteBody>().Cast<IDisabledTrait>()
					.Concat(passenger.TraitsImplementing<WithSpriteTurret>())
					.ToArray();
				conditionalRenderTraits[passenger] = traits;
				conditionalRenderTraitStates[passenger] = traits.Select(t => t.IsTraitDisabled).ToArray();

				self.World.ScreenMap.AddOrUpdate(self);
			}
		}

		void INotifyPassengerExited.OnPassengerExited(Actor self, Actor passenger)
		{
			previews.Remove(passenger);
			conditionalRenderTraits.Remove(passenger);
			conditionalRenderTraitStates.Remove(passenger);
			self.World.ScreenMap.AddOrUpdate(self);
		}
	}
}
