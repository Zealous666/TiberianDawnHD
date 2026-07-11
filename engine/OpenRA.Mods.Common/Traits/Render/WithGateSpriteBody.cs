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
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.Common.Graphics;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits.Render
{
	[Desc("This actor visually connects to walls and changes appearance when actors walk through it.")]
	sealed class WithGateSpriteBodyInfo : WithSpriteBodyInfo, IWallConnectorInfo, Requires<GateInfo>
	{
		[Desc("Cells (outside the gate footprint) that contain wall cells that can connect to the gate")]
		public readonly ImmutableArray<CVec> WallConnections = [];

		[Desc("Wall type for connections")]
		public readonly string Type = "wall";

		[SequenceReference]
		[Desc("Override sequence to use when fully open.")]
		public readonly string OpenSequence = null;

		// AOT: irregular flicker between the "idle" sequence's closed (frame 0) and
		// open-looking (frame 1) poses while the actor is Heavy/Critical damaged -
		// used for a gate whose closed/open states are a simple on/off visual (e.g.
		// a laser barrier) rather than a swinging door with many in-between frames.
		// Purely cosmetic: Gate.Position stays pinned at 0 the whole time via
		// LockedCondition (aot-gate-damage-lockout), so GetGateFrame() alone would
		// never show anything but frame 0 - this ticks independently of Position.
		[Desc("Minimum/maximum ticks between flicker toggles while Heavy/Critical damaged. Set both to 0 to disable.")]
		public readonly int2 FlickerInterval = int2.Zero;

		// AOT: ambient shimmer while the gate rests fully closed (Position == 0) and
		// undamaged - a laser barrier looks dead/static as a single frame, whereas the
		// real wall-laser fence segments already pulse (AotWithWallPulseBody). Position
		// never changes while resting, so GetGateFrame() alone can't drive this - needs
		// its own 2-frame variant sequence (A/B) cycled independently of Position.
		[SequenceReference]
		[Desc("2-frame (variant A/B) sequence to cycle through for the ambient shimmer. Null disables it.")]
		public readonly string ShimmerSequence = null;

		[Desc("Minimum/maximum ticks between shimmer toggles while resting closed. Set both to 0 to disable.")]
		public readonly int2 ShimmerInterval = int2.Zero;

		public override object Create(ActorInitializer init) { return new WithGateSpriteBody(init, this); }

		public override IEnumerable<IActorPreview> RenderPreviewSprites(ActorPreviewInitializer init, string image, int facings, PaletteReference p)
		{
			if (!EnabledByDefault)
				yield break;

			var anim = new Animation(init.World, image);
			anim.PlayFetchIndex(RenderSprites.NormalizeSequence(anim, init.GetDamageState(), Sequence), () => 0);

			yield return new SpriteActorPreview(anim, () => WVec.Zero, () => 0, p);
		}

		string IWallConnectorInfo.GetWallConnectionType()
		{
			return Type;
		}
	}

	sealed class WithGateSpriteBody : WithSpriteBody, INotifyRemovedFromWorld, IWallConnector, ITick
	{
		readonly WithGateSpriteBodyInfo gateBodyInfo;
		readonly Gate gate;
		bool renderOpen;

		// AOT: flicker state for FlickerInterval (see Info field for rationale).
		bool flickering;
		int flickerFrame;
		int flickerTicks;

		// AOT: shimmer state for ShimmerInterval (see Info field for rationale).
		bool shimmering;
		int shimmerFrame;
		int shimmerTicks;

		public WithGateSpriteBody(ActorInitializer init, WithGateSpriteBodyInfo info)
			: base(init, info)
		{
			gateBodyInfo = info;
			gate = init.Self.Trait<Gate>();
		}

		// AOT: WithMakeAnimation (used for build-incomplete condition bookkeeping) calls
		// the base WithSpriteBody.CancelCustomAnimation() once the make-sequence finishes,
		// which blindly does PlayRepeating(Info.Sequence) - looping frame 0..Length-1..0
		// forever. For a normal building that's fine (single idle pose repeated), but our
		// "idle" sequence holds all 11 gate-position frames, so PlayRepeating turned into
		// a perpetual "closed -> open -> snap closed -> open -> ..." animation immediately
		// after construction, until the first real Gate.Position change (triggered by a
		// unit) let WithGateSpriteBody.Tick() overwrite it with the correct frame again.
		// Overriding this to reuse our own UpdateState() picks the frame that actually
		// matches gate.Position/renderOpen instead of looping blindly.
		public override void CancelCustomAnimation(Actor self)
		{
			UpdateState(self);
		}

		void UpdateState(Actor self)
		{
			// AOT: do not force the "open" sequence just because the trait is paused.
			// A paused gate (locked/damaged/low-power) should freeze at its current
			// Position (which Gate.Tick() also stops advancing while paused), so the
			// sprite keeps showing whatever frame matches gate.Position/OpenPosition.
			// Only render the dedicated OpenSequence once the gate is actually fully
			// open (renderOpen), matching how a resting/looping open state should look.
			if (renderOpen)
				DefaultAnimation.PlayRepeating(NormalizeSequence(self, gateBodyInfo.OpenSequence));
			else
				DefaultAnimation.PlayFetchIndex(NormalizeSequence(self, Info.Sequence), GetGateFrame);
		}

		void ITick.Tick(Actor self)
		{
			// AOT: irregular flicker while Heavy/Critical damaged, independent of
			// Gate.Position (which LockedCondition pins to 0 for the whole damaged
			// duration - a plain GetGateFrame lerp would never move off frame 0).
			// Recovery back to the normal Position-driven frame happens for free via
			// DamageStateChanged -> UpdateState() once the damage state improves.
			if (gateBodyInfo.FlickerInterval.Y > 0 && self.GetDamageState() >= DamageState.Heavy)
			{
				if (!flickering)
				{
					flickering = true;
					flickerFrame = 0;
					flickerTicks = self.World.SharedRandom.Next(gateBodyInfo.FlickerInterval.X, gateBodyInfo.FlickerInterval.Y);
					DefaultAnimation.PlayFetchIndex(NormalizeSequence(self, Info.Sequence), () => flickerFrame);
				}
				else if (--flickerTicks <= 0)
				{
					flickerFrame ^= 1;
					flickerTicks = self.World.SharedRandom.Next(gateBodyInfo.FlickerInterval.X, gateBodyInfo.FlickerInterval.Y);
				}

				return;
			}

			flickering = false;

			// AOT: ambient shimmer while resting fully closed and undamaged (see Info
			// field for rationale). Explicitly excludes Heavy/Critical damage too, in
			// case FlickerInterval is disabled on this actor but the gate is damaged
			// regardless - shimmer must never fight the damaged look.
			if (gateBodyInfo.ShimmerInterval.Y > 0 && gateBodyInfo.ShimmerSequence != null &&
				gate.Position == 0 && self.GetDamageState() < DamageState.Heavy)
			{
				if (!shimmering)
				{
					shimmering = true;
					shimmerFrame = 0;
					shimmerTicks = self.World.SharedRandom.Next(gateBodyInfo.ShimmerInterval.X, gateBodyInfo.ShimmerInterval.Y);
					DefaultAnimation.PlayFetchIndex(NormalizeSequence(self, gateBodyInfo.ShimmerSequence), () => shimmerFrame);
				}
				else if (--shimmerTicks <= 0)
				{
					shimmerFrame ^= 1;
					shimmerTicks = self.World.SharedRandom.Next(gateBodyInfo.ShimmerInterval.X, gateBodyInfo.ShimmerInterval.Y);
				}

				return;
			}

			if (shimmering)
			{
				shimmering = false;
				UpdateState(self);
			}

			if (gateBodyInfo.OpenSequence == null)
				return;

			if (gate.Position == gate.OpenPosition ^ renderOpen)
			{
				renderOpen = gate.Position == gate.OpenPosition;
				UpdateState(self);
			}
		}

		int GetGateFrame()
		{
			return int2.Lerp(0, DefaultAnimation.CurrentSequence.Length - 1, gate.Position, gate.OpenPosition);
		}

		protected override void DamageStateChanged(Actor self)
		{
			UpdateState(self);
		}

		protected override void TraitEnabled(Actor self)
		{
			base.TraitEnabled(self);

			UpdateState(self);
			UpdateNeighbours(self);
		}

		void UpdateNeighbours(Actor self)
		{
			var footprint = gate.Footprint.ToArray();
			var adjacent = Util.ExpandFootprint(footprint, true).Except(footprint)
				.Where(self.World.Map.Contains).ToList();

			var adjacentActorTraits = adjacent.SelectMany(self.World.ActorMap.GetActorsAt)
				.SelectMany(a => a.TraitsImplementing<IWallConnector>());

			foreach (var rb in adjacentActorTraits)
				rb.SetDirty();
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			UpdateNeighbours(self);
		}

		bool IWallConnector.AdjacentWallCanConnect(Actor self, CPos wallLocation, string wallType, out CVec facing)
		{
			facing = wallLocation - self.Location;
			return wallType == gateBodyInfo.Type && gateBodyInfo.WallConnections.Contains(facing);
		}

		void IWallConnector.SetDirty() { }
	}
}
