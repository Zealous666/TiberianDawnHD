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
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("aotmod: Lets a Cargo transport take a ForceMove (Alt) order on terrain -> move to the ",
		"target location and unload its passengers there, like the Carryall's deliver-unit order. ",
		"Works for ground, naval and air movement (uses the actor's own IMove via UnloadCargo). ",
		"The normal in-place Unload deploy order and every other order are unaffected; the targeter ",
		"only fires when the transport actually carries something, so empty units keep normal force-move.")]
	public class AotMoveUnloadCargoInfo : ConditionalTraitInfo, Requires<CargoInfo>
	{
		[VoiceReference]
		[Desc("Voice to play when the move-unload order is issued. Optional -- left unset so it works ",
			"with any voice set; set it per-actor only if that actor's voice set defines the phrase.")]
		public readonly string Voice = null;

		[CursorReference]
		[Desc("Cursor for the ForceMove move-unload order. Default matches the Carryall's deliver cursor.")]
		public readonly string Cursor = "ability";

		[Desc("Order priority for the ForceMove targeter. Must be higher than the Move targeter (4).")]
		public readonly int OrderPriority = 6;

		[Desc("How close (in cells) to the resolved destination the transport must get before ",
			"unloading. 0 = drive/swim/fly right up to the nearest cell it can actually occupy. ",
			"A naval LST resolves to the shore water cell next to the target and unloads from there.")]
		public readonly int MoveNearEnough = 0;

		public override object Create(ActorInitializer init) => new AotMoveUnloadCargo(this);
	}

	public class AotMoveUnloadCargo : ConditionalTrait<AotMoveUnloadCargoInfo>, IIssueOrder, IResolveOrder, IOrderVoice
	{
		Cargo cargo;

		public AotMoveUnloadCargo(AotMoveUnloadCargoInfo info)
			: base(info) { }

		protected override void Created(Actor self)
		{
			base.Created(self);
			cargo = self.TraitOrDefault<Cargo>();
		}

		IEnumerable<IOrderTargeter> IIssueOrder.Orders
		{
			get
			{
				if (!IsTraitDisabled)
					yield return new AotMoveUnloadTargeter(Info.OrderPriority, Info.Cursor);
			}
		}

		Order IIssueOrder.IssueOrder(Actor self, IOrderTargeter order, in Target target, bool queued)
		{
			if (order.OrderID != "AotMoveUnloadAt")
				return null;

			return new Order("AotMoveUnloadAt", self, target, queued);
		}

		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != "AotMoveUnloadAt")
				return;

			if (IsTraitDisabled || cargo == null || cargo.IsEmpty())
				return;

			var move = self.TraitOrDefault<IMove>();
			if (move == null)
				return;

			// Move to the nearest cell the transport can actually occupy near the target and unload
			// there. evaluateNearestMovableCell makes the pathfinder pick a reachable cell when the
			// clicked cell itself is impassable for this mover -- e.g. a naval LST clicked onto land
			// resolves to the shore water cell it can reach, so it swims to the coast instead of
			// dropping troops on the spot. The in-place UnloadCargo then unloads once it has arrived.
			var cell = self.World.Map.CellContaining(order.Target.CenterPosition);
			self.QueueActivity(order.Queued, move.MoveTo(cell, Info.MoveNearEnough,
				evaluateNearestMovableCell: true, targetLineColor: Color.Green));
			self.QueueActivity(new UnloadCargo(self, cargo.Info.LoadRange));
		}

		string IOrderVoice.VoicePhraseForOrder(Actor self, Order order)
		{
			return order.OrderString == "AotMoveUnloadAt" ? Info.Voice : null;
		}
	}

	sealed class AotMoveUnloadTargeter : IOrderTargeter
	{
		readonly string cursor;

		public string OrderID => "AotMoveUnloadAt";
		public int OrderPriority { get; }
		public bool IsQueued { get; private set; }

		public AotMoveUnloadTargeter(int priority, string cursor)
		{
			OrderPriority = priority;
			this.cursor = cursor;
		}

		public bool TargetOverridesSelection(Actor self, in Target target, List<Actor> actorsAt, CPos xy, TargetModifiers modifiers) { return true; }

		public bool CanTarget(Actor self, in Target target, ref TargetModifiers modifiers, ref string cursor)
		{
			// Only intercept ForceMove (Alt+click) on open terrain.
			if (!modifiers.HasModifier(TargetModifiers.ForceMove))
				return false;

			if (target.Type != TargetType.Terrain)
				return false;

			// Fall through to the normal force-move order when there is nothing to unload.
			var cargo = self.TraitOrDefault<Cargo>();
			if (cargo == null || cargo.IsEmpty())
				return false;

			cursor = this.cursor;
			IsQueued = modifiers.HasModifier(TargetModifiers.ForceQueue);
			return true;
		}
	}
}
