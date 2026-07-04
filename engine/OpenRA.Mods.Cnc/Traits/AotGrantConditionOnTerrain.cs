#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Grants a condition while the actor stands on specific terrain types.
 */
#endregion

using System.Collections.Immutable;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Grants a condition while standing on specific terrain types.")]
	public class AotGrantConditionOnTerrainInfo : TraitInfo
	{
		[FieldLoader.Require]
		[GrantedConditionReference]
		[Desc("Condition to grant.")]
		public readonly string Condition = null;

		[FieldLoader.Require]
		[Desc("Terrain types that activate the condition.")]
		public readonly ImmutableArray<string> TerrainTypes = [];

		public override object Create(ActorInitializer init) { return new AotGrantConditionOnTerrain(this); }
	}

	public class AotGrantConditionOnTerrain : ITick
	{
		readonly AotGrantConditionOnTerrainInfo info;
		int conditionToken = Actor.InvalidConditionToken;

		public AotGrantConditionOnTerrain(AotGrantConditionOnTerrainInfo info)
		{
			this.info = info;
		}

		void ITick.Tick(Actor self)
		{
			var loc = self.Location;
			if (!self.World.Map.Contains(loc))
				return;

			var terrain = self.World.Map.GetTerrainInfo(loc).Type;
			var onTerrain = info.TerrainTypes.Contains(terrain);

			if (onTerrain && conditionToken == Actor.InvalidConditionToken)
				conditionToken = self.GrantCondition(info.Condition);
			else if (!onTerrain && conditionToken != Actor.InvalidConditionToken)
				conditionToken = self.RevokeCondition(conditionToken);
		}
	}
}
