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

using OpenRA.Mods.Common.Traits.Render;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Age of Tiberium: grants a condition while an ACTIVE NOD laser fence occupies a given ",
		"adjacent cell. Lets a Laser Gate render a laser stub that connects to the neighbouring ",
		"fence only on the side(s) where a laser fence is actually attached. Multi-instance ",
		"(one instance per gate end/side).")]
	public class AotGateLaserConnectorInfo : TraitInfo, Requires<BuildingInfo>
	{
		[Desc("Cell to test for an active laser fence, relative to the actor's top-left location ",
			"(the Building.Footprint origin). E.g. the cell just beyond an end post.")]
		public readonly CVec CheckCell = CVec.Zero;

		[GrantedConditionReference]
		[FieldLoader.Require]
		[Desc("Condition granted while an active laser fence occupies CheckCell.")]
		public readonly string Condition = null;

		[Desc("Ticks between neighbour checks.")]
		public readonly int Interval = 8;

		public override object Create(ActorInitializer init) { return new AotGateLaserConnector(this); }
	}

	public class AotGateLaserConnector : ITick
	{
		readonly AotGateLaserConnectorInfo info;
		int token = Actor.InvalidConditionToken;
		int delay;

		public AotGateLaserConnector(AotGateLaserConnectorInfo info)
		{
			this.info = info;
		}

		void ITick.Tick(Actor self)
		{
			if (--delay > 0)
				return;

			delay = info.Interval;

			// A live laser fence renders through AotWithWallPulseBody; that body is only enabled
			// while the fence has its laser upgrade (RequiresCondition: aot-laser-fence). A plain
			// barbwire fence therefore never triggers the connector.
			var cell = self.Location + info.CheckCell;
			var connected = false;
			foreach (var a in self.World.ActorMap.GetActorsAt(cell))
			{
				foreach (var pulse in a.TraitsImplementing<AotWithWallPulseBody>())
				{
					if (!pulse.IsTraitDisabled)
					{
						connected = true;
						break;
					}
				}

				if (connected)
					break;
			}

			if (connected && token == Actor.InvalidConditionToken)
				token = self.GrantCondition(info.Condition);
			else if (!connected && token != Actor.InvalidConditionToken)
				token = self.RevokeCondition(token);
		}
	}
}
