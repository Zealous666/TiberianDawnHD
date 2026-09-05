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
using OpenRA.Activities;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("aotmod: Allows a cargo transport aircraft to drop all passengers via parachute from altitude. " +
		"Manual mode: Deploy button drops at current position. " +
		"Auto mode: ForceMove → fly to target, drop passengers, return to origin.")]
	public class AotParachuteDropCargoInfo : ConditionalTraitInfo
	{
		[VoiceReference]
		[Desc("Voice to play when the parachute-drop order is issued.")]
		public readonly string DropVoice = "Action";

		[CursorReference]
		[Desc("Cursor for the ForceMove auto-drop order.")]
		public readonly string DropCursor = "enter";

		[CursorReference]
		[Desc("Cursor when the drop target is blocked.")]
		public readonly string DropBlockedCursor = "enter-blocked";

		[Desc("Order priority for the ForceMove auto-drop targeter. Must be higher than the aircraft Move targeter (~4).")]
		public readonly int OrderPriority = 5;

		[Desc("Ticks between consecutive passenger drops. Creates spread as the helicopter drifts between drops.")]
		public readonly int DropInterval = 15;

		[Desc("Lateral spread distance (wu) between consecutive drops when hovering. Spread is perpendicular to helicopter facing.")]
		public readonly int DropSpread = 512;

		[Desc("Sound to play when each passenger is dropped (parachute opening).")]
		public readonly string DropSound = null;

		public override object Create(ActorInitializer init) => new AotParachuteDropCargo(init.Self, this);
	}

	public class AotParachuteDropCargo : ConditionalTrait<AotParachuteDropCargoInfo>,
		IIssueDeployOrder, IIssueOrder, IResolveOrder, IOrderVoice
	{
		Cargo cargo;

		public AotParachuteDropCargo(Actor self, AotParachuteDropCargoInfo info)
			: base(info) { }

		protected override void Created(Actor self)
		{
			base.Created(self);
			cargo = self.TraitOrDefault<Cargo>();
		}

		// --- Manual mode: Deploy button ---

		Order IIssueDeployOrder.IssueDeployOrder(Actor self, bool queued)
			=> new Order("AotParachuteDrop", self, queued);

		bool IIssueDeployOrder.CanIssueDeployOrder(Actor self, bool queued)
			=> !IsTraitDisabled && cargo != null && !cargo.IsEmpty();

		// --- Auto mode: ForceMove targeter ---

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				if (!IsTraitDisabled)
					yield return new AotParachuteDropTargeter(Info.OrderPriority, Info.DropCursor, Info.DropBlockedCursor);
			}
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			if (order.OrderID != "AotParachuteDropAt")
				return null;

			return new Order("AotParachuteDropAt", self, target, queued);
		}

		// --- Order resolution ---

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString == "AotParachuteDrop")
			{
				if (IsTraitDisabled || cargo == null || cargo.IsEmpty()) return;
				self.QueueActivity(order.Queued, new AotParachuteDropActivity(self, Info.DropInterval, Info.DropSpread, Info.DropSound));
			}
			else if (order.OrderString == "AotParachuteDropAt")
			{
				if (IsTraitDisabled) return;
				var origin = self.CenterPosition;
				self.QueueActivity(order.Queued, new Fly(self, order.Target));
				self.QueueActivity(new AotParachuteDropActivity(self, Info.DropInterval, Info.DropSpread, Info.DropSound));
				self.QueueActivity(new Fly(self, Target.FromPos(origin)));
			}
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order)
		{
			return order.OrderString is "AotParachuteDrop" or "AotParachuteDropAt" ? Info.DropVoice : null;
		}
	}

	sealed class AotParachuteDropTargeter : IOrderTargeter
	{
		readonly string cursor;
		readonly string blockedCursor;

		public string OrderID => "AotParachuteDropAt";
		public int OrderPriority { get; }
		public bool IsQueued { get; private set; }

		public AotParachuteDropTargeter(int priority, string cursor, string blockedCursor)
		{
			OrderPriority = priority;
			this.cursor = cursor;
			this.blockedCursor = blockedCursor;
		}

		public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) { return true; }

		public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
		{
			// Only intercept ForceMove (Ctrl+click), not normal move orders.
			if (!modifiers.HasModifier(TargetModifiers.ForceMove))
				return false;

			if (target.Type != TargetType.Terrain)
				return false;

			var cargo = self.TraitOrDefault<Cargo>();
			if (cargo == null || cargo.IsEmpty())
			{
				cursor = blockedCursor;
				return true; // show blocked cursor even when empty
			}

			cursor = this.cursor;
			IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);
			return true;
		}
	}

	/// <summary>
	/// Drops cargo passengers via parachute one at a time with a configurable interval between drops.
	/// While the helicopter drifts between drops, passengers land at slightly different positions.
	/// An additional perpendicular spread offset is applied so passengers never stack exactly.
	/// </summary>
	public class AotParachuteDropActivity : Activity
	{
		readonly Cargo cargo;
		readonly int dropInterval;
		readonly int dropSpread;
		readonly string dropSound;

		List<Actor> passengers;
		int nextDropIndex;
		int ticksUntilDrop;

		// Facing at first drop — used to compute consistent spread direction.
		WAngle spreadFacing;

		public AotParachuteDropActivity(Actor self, int dropInterval, int dropSpread, string dropSound)
		{
			cargo = self.Trait<Cargo>();
			this.dropInterval = dropInterval;
			this.dropSpread = dropSpread;
			this.dropSound = dropSound;
			IsInterruptible = false;
		}

		protected override void OnFirstRun(Actor self)
		{
			passengers = cargo.Passengers.ToList();
			spreadFacing = self.Orientation.Yaw;
			nextDropIndex = 0;
			ticksUntilDrop = 0;
		}

		public override bool Tick(Actor self)
		{
			if (nextDropIndex >= passengers.Count)
				return true;

			if (ticksUntilDrop-- > 0)
				return false;

			var passenger = passengers[nextDropIndex];

			// Lateral spread perpendicular to facing — alternates left/right per passenger.
			var perpAngle = new WAngle(spreadFacing.Angle + 256); // 90° clockwise
			var perp = new WVec(dropSpread, 0, 0).Rotate(WRot.FromYaw(perpAngle));
			var sign = (nextDropIndex % 2 == 0) ? 1 : -1;
			var step = (nextDropIndex + 1) / 2;
			var spreadOffset = perp * (sign * step);

			var dropPos = self.CenterPosition + spreadOffset;

			if (dropSound != null)
				Game.Sound.Play(SoundType.World, dropSound, dropPos);

			cargo.Unload(self, passenger);
			self.World.AddFrameEndTask(w =>
			{
				if (passenger.IsDead) return;
				passenger.Trait<IPositionable>().SetCenterPosition(passenger, dropPos);
				w.Add(passenger);
				passenger.QueueActivity(new Parachute(passenger));
			});

			nextDropIndex++;
			ticksUntilDrop = dropInterval;

			return nextDropIndex >= passengers.Count;
		}
	}
}
