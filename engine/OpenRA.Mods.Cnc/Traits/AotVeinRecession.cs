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
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("When this actor (the veinhole heart) dies, the vein field slowly recedes from the",
		"outside in until nothing is left, instead of freezing in place forever.")]
	sealed class AotVeinRecedesOnDeathInfo : TraitInfo
	{
		[Desc("Invisible controller actor spawned at the heart's location to drive the retreat",
			"after the heart itself is gone (a dead actor cannot keep ticking).")]
		[ActorReference]
		public readonly string ControllerActor = "aot-vein-recession";

		public override object Create(ActorInitializer init) { return new AotVeinRecedesOnDeath(this); }
	}

	sealed class AotVeinRecedesOnDeath : INotifyKilled, INotifyActorDisposing
	{
		readonly AotVeinRecedesOnDeathInfo info;
		bool spawned;

		public AotVeinRecedesOnDeath(AotVeinRecedesOnDeathInfo info)
		{
			this.info = info;
		}

		void Spawn(Actor self)
		{
			if (spawned)
				return;

			spawned = true;
			var origin = self.Location;
			var owner = self.Owner;
			self.World.AddFrameEndTask(w =>
				w.CreateActor(info.ControllerActor, new TypeDictionary
				{
					new LocationInit(origin),
					new OwnerInit(owner),
				}));
		}

		void INotifyKilled.Killed(Actor self, AttackInfo e) { Spawn(self); }
		void INotifyActorDisposing.Disposing(Actor self) { Spawn(self); }
	}

	[Desc("Controller spawned by AotVeinRecedesOnDeath: removes veined cells starting with the",
		"ones farthest from the origin, so the field visibly shrinks back towards where the",
		"heart used to be. Self-destructs once nothing is left to remove.")]
	sealed class AotVeinRecessionInfo : TraitInfo
	{
		[Desc("Ticks between each removal pass.")]
		public readonly int Interval = 5;

		[Desc("How many cells to remove per pass.")]
		public readonly int CellsPerPass = 1;

		public override object Create(ActorInitializer init) { return new AotVeinRecession(init, this); }
	}

	sealed class AotVeinRecession : ITick
	{
		readonly AotVeinRecessionInfo info;
		readonly CPos origin;
		int ticks;

		public AotVeinRecession(ActorInitializer init, AotVeinRecessionInfo info)
		{
			this.info = info;
			origin = init.GetValue<LocationInit, CPos>(CPos.Zero);
			ticks = info.Interval;
		}

		void ITick.Tick(Actor self)
		{
			if (--ticks > 0)
				return;

			ticks = info.Interval;

			var layer = self.World.WorldActor.Trait<AotVeinLayer>();
			var cells = layer.Cells;
			if (cells.Count == 0)
			{
				self.Dispose();
				return;
			}

			var farthest = cells
				.OrderByDescending(c => Math.Max(Math.Abs(c.X - origin.X), Math.Abs(c.Y - origin.Y)))
				.Take(info.CellsPerPass);

			foreach (var c in farthest)
				foreach (var a in self.World.ActorMap.GetActorsAt(c).ToList())
					if (a.Info.Name == "aot-vein-cell" && !a.IsDead && a.IsInWorld)
						a.Dispose();
		}
	}
}
