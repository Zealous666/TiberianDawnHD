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
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Will open and be passable for actors that appear friendly when there are no enemies in range.")]
	public class GateInfo : PausableConditionalTraitInfo, ITemporaryBlockerInfo, IBlocksProjectilesInfo, Requires<BuildingInfo>
	{
		public readonly string OpeningSound = null;
		public readonly string ClosingSound = null;

		[Desc("Ticks until the gate closes.")]
		public readonly int CloseDelay = 150;

		[Desc("Ticks until the gate is considered open.")]
		public readonly int TransitionDelay = 33;

		[Desc("Blocks bullets scaled to open value.")]
		public readonly WDist BlocksProjectilesHeight = new(640);

		[Desc("Determines what projectiles to block based on their allegiance to the gate owner.")]
		public readonly PlayerRelationship BlocksProjectilesValidRelationships = PlayerRelationship.Ally | PlayerRelationship.Neutral | PlayerRelationship.Enemy;

		// AOT: administrative lock (player toggle, heavy damage, low power, ...). Unlike
		// PauseOnCondition (which just freezes Tick() wherever Position happens to be),
		// this forces the gate immediately to Position 0 (closed) the moment it engages,
		// and blocks new auto-open triggers while active. Normal Tick()/auto-open/auto-close
		// behaviour for allies is otherwise untouched, and resumes as soon as unlocked.
		[ConsumedConditionReference]
		[Desc("Boolean expression that forces the gate immediately closed and prevents it from opening while true.")]
		public readonly BooleanExpression LockedCondition = null;

		public override object Create(ActorInitializer init) { return new Gate(init, this); }
	}

	public class Gate : PausableConditionalTrait<GateInfo>, ITick, ITemporaryBlocker, IBlocksProjectiles,
		INotifyAddedToWorld, INotifyRemovedFromWorld, INotifyBlockingMove, ISync
	{
		readonly Actor self;
		readonly Building building;
		IEnumerable<CPos> blockedPositions;
		public readonly IEnumerable<CPos> Footprint;

		public readonly int OpenPosition;

		[VerifySync]
		public int Position { get; private set; }

		int desiredPosition;
		int remainingOpenTime;
		bool locked;

		public Gate(ActorInitializer init, GateInfo info)
			: base(info)
		{
			self = init.Self;
			OpenPosition = Info.TransitionDelay;

			// AOT: start closed, not open. The original vanilla default set
			// Position = OpenPosition (fully open) at construction, which caused a
			// spurious "already open -> auto-close immediately" transition right at
			// build completion, before WithGateSpriteBody's renderOpen bookkeeping had
			// synced up — visible as a stuck/looping opening animation on freshly
			// built gates until manually toggled once.
			Position = 0;

			building = self.Trait<Building>();
			blockedPositions = building.Info.Tiles(self.Location);
			Footprint = blockedPositions;
		}

		public override IEnumerable<VariableObserver> GetVariableObservers()
		{
			foreach (var observer in base.GetVariableObservers())
				yield return observer;

			if (Info.LockedCondition != null)
				yield return new VariableObserver(LockedConditionChanged, Info.LockedCondition.Variables);
		}

		void LockedConditionChanged(Actor self, IReadOnlyDictionary<string, int> conditions)
		{
			var wasLocked = locked;
			locked = Info.LockedCondition.Evaluate(conditions);

			if (locked && !wasLocked)
				ForceClosedImmediately(self);
		}

		void ForceClosedImmediately(Actor self)
		{
			// Mirror the bookkeeping Tick() would perform when leaving the fully-open
			// state, then snap straight to closed instead of animating step by step.
			if (Position == OpenPosition)
			{
				Game.Sound.Play(SoundType.World, Info.ClosingSound, self.CenterPosition);
				self.World.ActorMap.AddInfluence(self, building);
			}

			Position = 0;
			desiredPosition = 0;
			remainingOpenTime = 0;
		}

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled || IsTraitPaused)
				return;

			if (desiredPosition < Position)
			{
				// Gate was fully open
				if (Position == OpenPosition)
				{
					Game.Sound.Play(SoundType.World, Info.ClosingSound, self.CenterPosition);
					self.World.ActorMap.AddInfluence(self, building);
				}

				Position--;
			}
			else if (desiredPosition > Position)
			{
				// Gate was fully closed
				if (Position == 0)
					Game.Sound.Play(SoundType.World, Info.OpeningSound, self.CenterPosition);

				Position++;

				// Gate is now fully open
				if (Position == OpenPosition)
				{
					self.World.ActorMap.RemoveInfluence(self, building);
					remainingOpenTime = Info.CloseDelay;
				}
			}

			if (Position == OpenPosition)
			{
				if (IsBlocked())
					remainingOpenTime = Info.CloseDelay;
				else if (--remainingOpenTime <= 0)
					desiredPosition = 0;
			}
		}

		bool ITemporaryBlocker.IsBlocking(Actor self, CPos cell)
		{
			return Position != OpenPosition && blockedPositions.Contains(cell);
		}

		bool ITemporaryBlocker.CanRemoveBlockage(Actor self, Actor blocking)
		{
			return CanRemoveBlockage(self, blocking);
		}

		void INotifyBlockingMove.OnNotifyBlockingMove(Actor self, Actor blocking)
		{
			if (Position != OpenPosition && CanRemoveBlockage(self, blocking))
				desiredPosition = OpenPosition;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			blockedPositions = Footprint;
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			blockedPositions = [];
		}

		bool CanRemoveBlockage(Actor self, Actor blocking)
		{
			return !IsTraitDisabled && !IsTraitPaused && !locked && blocking.AppearsFriendlyTo(self);
		}

		bool IsBlocked()
		{
			return blockedPositions.Any(loc => self.World.ActorMap.GetActorsAt(loc).Any(a => a != self));
		}

		WDist IBlocksProjectiles.BlockingHeight => new(Info.BlocksProjectilesHeight.Length * (OpenPosition - Position) / OpenPosition);

		PlayerRelationship IBlocksProjectiles.ValidRelationships => Info.BlocksProjectilesValidRelationships;
	}
}
