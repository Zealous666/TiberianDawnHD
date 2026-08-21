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
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Ejects all cargo passengers at the actor's current location when the given condition is added. " +
		"Cargo.Unload() does not check IsTraitDisabled, so this works even when Cargo is simultaneously " +
		"disabled via RequiresCondition (e.g. carryall upgrade).")]
	public class AotEjectCargoOnConditionInfo : TraitInfo, IObservesVariablesInfo
	{
		[FieldLoader.Require]
		[ConsumedConditionReference]
		[Desc("Condition whose addition triggers the ejection.")]
		public readonly string Condition = null;

		public override object Create(ActorInitializer init) => new AotEjectCargoOnCondition(this);
	}

	public class AotEjectCargoOnCondition : IObservesVariables
	{
		readonly AotEjectCargoOnConditionInfo info;
		Cargo cargo;
		bool wasActive;

		public AotEjectCargoOnCondition(AotEjectCargoOnConditionInfo info)
		{
			this.info = info;
		}

		IEnumerable<VariableObserver> IObservesVariables.GetVariableObservers()
		{
			yield return new VariableObserver(ConditionChanged, new[] { info.Condition });
		}

		void ConditionChanged(Actor self, IReadOnlyDictionary<string, int> variables)
		{
			var isActive = variables.GetValueOrDefault(info.Condition) > 0;

			if (isActive && !wasActive)
			{
				cargo ??= self.TraitOrDefault<Cargo>();
				if (cargo != null && !cargo.IsEmpty())
				{
					var passengers = cargo.Passengers.ToList();
					foreach (var passenger in passengers)
					{
						cargo.Unload(self, passenger);

						self.World.AddFrameEndTask(w =>
						{
							if (passenger.IsDead)
								return;

							var positionable = passenger.Trait<IPositionable>();

							// Mirror EjectOnDeath: try transport cell first, then any adjacent cell.
							CPos target;
							if (positionable.CanEnterCell(self.Location, self, BlockedByActor.All))
							{
								target = self.Location;
							}
							else
							{
								target = cargo.CurrentAdjacentCells()
									.FirstOrDefault(c => positionable.CanEnterCell(c, null, BlockedByActor.None));

								if (target == default)
								{
									passenger.Kill(self);
									return;
								}
							}

							positionable.SetPosition(passenger, target);
							w.Add(passenger);
						});
					}
				}
			}

			wasActive = isActive;
		}
	}
}
