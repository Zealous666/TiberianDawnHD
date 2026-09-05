#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Grants a condition while standing on a cell occupied by one of a list of
 * actor types (e.g. Zone Trooper hovering over walls/gates — these are
 * actors, not terrain types, so AotGrantConditionOnTerrain cannot see them).
 */
#endregion

using System.Collections.Immutable;
using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Grants a condition while standing on a cell occupied by one of the listed actor types.")]
	public class AotGrantConditionOnActorInfo : TraitInfo
	{
		[FieldLoader.Require]
		[GrantedConditionReference]
		[Desc("Condition to grant.")]
		public readonly string Condition = null;

		[FieldLoader.Require]
		[Desc("Actor types (Info.Name, lowercase) that activate the condition when present in the same cell.")]
		public readonly ImmutableArray<string> ActorTypes = [];

		public override object Create(ActorInitializer init) { return new AotGrantConditionOnActor(this); }
	}

	public class AotGrantConditionOnActor : ITick
	{
		readonly AotGrantConditionOnActorInfo info;
		int conditionToken = Actor.InvalidConditionToken;

		public AotGrantConditionOnActor(AotGrantConditionOnActorInfo info)
		{
			this.info = info;
		}

		void ITick.Tick(Actor self)
		{
			var onActor = self.World.ActorMap.GetActorsAt(self.Location)
				.Any(a => a != self && info.ActorTypes.Contains(a.Info.Name));

			if (onActor && conditionToken == Actor.InvalidConditionToken)
				conditionToken = self.GrantCondition(info.Condition);
			else if (!onActor && conditionToken != Actor.InvalidConditionToken)
				conditionToken = self.RevokeCondition(conditionToken);
		}
	}
}
