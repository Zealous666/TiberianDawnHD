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

using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Scripting;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Scripting
{
	[ScriptPropertyGroup("Support Powers")]
	public class AotFirestormMatrixProperties : ScriptActorProperties, Requires<AotFirestormMatrixPowerInfo>
	{
		readonly AotFirestormMatrixPower matrix;

		public AotFirestormMatrixProperties(ScriptContext context, Actor self)
			: base(context, self)
		{
			matrix = self.TraitsImplementing<AotFirestormMatrixPower>().First();
		}

		[Desc("Issue the order to activate the actor's Firestorm Defense Matrix support power. " +
			"Same order as clicking the icon - a no-op if the power is not ready (still charging/paused).")]
		public void ActivateFirestormMatrix()
		{
			Self.World.IssueOrder(new Order(matrix.Info.OrderName, Self.Owner.PlayerActor, Target.Invalid, false));
		}

		[Desc("Whether the Firestorm Defense Matrix is currently active.")]
		public bool FirestormMatrixActive => matrix.IsActive;

		[Desc("Debug: 'ready|active|remainingTicks|disabled|paused' for the underlying support power instance.")]
		public string FirestormMatrixDebug
		{
			get
			{
				var manager = Self.Owner.PlayerActor.Trait<SupportPowerManager>();
				if (!manager.Powers.TryGetValue(matrix.Info.OrderName, out var instance))
					return "no-instance";

				var paused = matrix.IsTraitPaused;
				var disabled = matrix.IsTraitDisabled;
				return $"ready={instance.Ready}|active={instance.Active}|remaining={instance.RemainingTicks}|traitDisabled={disabled}|traitPaused={paused}";
			}
		}
	}
}
