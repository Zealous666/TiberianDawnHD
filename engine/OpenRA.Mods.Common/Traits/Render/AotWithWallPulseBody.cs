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

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("Age of Tiberium: Wie WithWallSpriteBody, aber mit Puls-Animation. ",
		"Die Sequenz enthaelt PulseCount Bloecke zu je 16 Verbindungs-Frames; ",
		"gerenderter Frame = Nachbarschaftsmaske + 16 * aktuelle Pulsphase.")]
	sealed class AotWithWallPulseBodyInfo : WithSpriteBodyInfo, IWallConnectorInfo, Requires<BuildingInfo>
	{
		public readonly string Type = "wall";

		[Desc("Anzahl der Puls-Phasen (16-Frame-Bloecke in der Sequenz).")]
		public readonly int PulseCount = 4;

		[Desc("Ticks pro Puls-Phase.")]
		public readonly int PulseInterval = 4;

		public override object Create(ActorInitializer init) { return new AotWithWallPulseBody(init, this); }

		public override IEnumerable<IActorPreview> RenderPreviewSprites(ActorPreviewInitializer init, string image, int facings, PaletteReference p)
		{
			if (!EnabledByDefault)
				yield break;

			var adjacent = 0;
			var locationInit = init.GetOrDefault<LocationInit>();
			var neighbourInit = init.GetOrDefault<RuntimeNeighbourInit>();

			if (locationInit != null && neighbourInit != null)
			{
				var location = locationInit.Value;
				foreach (var kv in neighbourInit.Value)
				{
					var haveNeighbour = false;
					foreach (var n in kv.Value)
					{
						var rb = init.World.Map.Rules.Actors[n].TraitInfos<IWallConnectorInfo>().FirstEnabledTraitOrDefault();
						if (rb != null && rb.GetWallConnectionType() == Type)
						{
							haveNeighbour = true;
							break;
						}
					}

					if (!haveNeighbour)
						continue;

					if (kv.Key == location + new CVec(0, -1))
						adjacent |= 1;
					else if (kv.Key == location + new CVec(+1, 0))
						adjacent |= 2;
					else if (kv.Key == location + new CVec(0, +1))
						adjacent |= 4;
					else if (kv.Key == location + new CVec(-1, 0))
						adjacent |= 8;
				}
			}

			var anim = new Animation(init.World, image);
			anim.PlayFetchIndex(RenderSprites.NormalizeSequence(anim, init.GetDamageState(), Sequence), () => adjacent);

			yield return new SpriteActorPreview(anim, () => WVec.Zero, () => 0, p);
		}

		string IWallConnectorInfo.GetWallConnectionType()
		{
			return Type;
		}
	}

	sealed class AotWithWallPulseBody : WithSpriteBody, INotifyRemovedFromWorld, IWallConnector, ITick
	{
		readonly AotWithWallPulseBodyInfo wallInfo;
		int adjacent = 0;
		bool dirty = true;
		int tick = 0;
		int phase = 0;

		bool IWallConnector.AdjacentWallCanConnect(Actor self, CPos wallLocation, string wallType, out CVec facing)
		{
			facing = wallLocation - self.Location;
			return wallInfo.Type == wallType && Math.Abs(facing.X) + Math.Abs(facing.Y) == 1;
		}

		void IWallConnector.SetDirty() { dirty = true; }

		public AotWithWallPulseBody(ActorInitializer init, AotWithWallPulseBodyInfo info)
			: base(init, info)
		{
			wallInfo = info;
		}

		int CurrentFrame() { return adjacent + 16 * phase; }

		protected override void DamageStateChanged(Actor self)
		{
			if (IsTraitDisabled)
				return;

			DefaultAnimation.PlayFetchIndex(NormalizeSequence(self, Info.Sequence), CurrentFrame);
		}

		void ITick.Tick(Actor self)
		{
			if (++tick >= wallInfo.PulseInterval)
			{
				tick = 0;
				phase = (phase + 1) % wallInfo.PulseCount;
			}

			if (!dirty)
				return;

			// Update connection to neighbours
			var adjacentActors = CVec.Directions.SelectMany(dir =>
				self.World.ActorMap.GetActorsAt(self.Location + dir));

			adjacent = 0;
			foreach (var a in adjacentActors)
			{
				var wc = a.TraitsImplementing<IWallConnector>().FirstEnabledTraitOrDefault();
				if (wc == null || !wc.AdjacentWallCanConnect(a, self.Location, wallInfo.Type, out var facing))
					continue;

				if (facing.Y > 0)
					adjacent |= 1;
				else if (facing.X < 0)
					adjacent |= 2;
				else if (facing.Y < 0)
					adjacent |= 4;
				else if (facing.X > 0)
					adjacent |= 8;
			}

			dirty = false;
		}

		protected override void TraitEnabled(Actor self)
		{
			base.TraitEnabled(self);
			dirty = true;

			DefaultAnimation.PlayFetchIndex(NormalizeSequence(self, Info.Sequence), CurrentFrame);
			UpdateNeighbours(self);

			// Set the initial animation frame before the render tick (for frozen actor previews)
			self.World.AddFrameEndTask(_ => DefaultAnimation.Tick());
		}

		static void UpdateNeighbours(Actor self)
		{
			var adjacentActorTraits = CVec.Directions.SelectMany(dir =>
					self.World.ActorMap.GetActorsAt(self.Location + dir))
				.SelectMany(a => a.TraitsImplementing<IWallConnector>());

			foreach (var aat in adjacentActorTraits)
				aat.SetDirty();
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			UpdateNeighbours(self);
		}
	}
}
