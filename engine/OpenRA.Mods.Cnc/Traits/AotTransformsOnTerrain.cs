#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * Automatically transforms the actor when it stands on specific terrain types,
 * OR when it stands adjacent to cells with AdjacentTerrainTypes.
 * Will NOT trigger if ExcludeIfAdjacentTo terrain is found nearby.
 * Supports RequiresCondition.
 */
#endregion

using System.Collections.Immutable;
using OpenRA.Mods.Common.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Cnc.Traits
{
	[Desc("Automatically transform into another actor when standing on specific terrain types.")]
	public class AotTransformsOnTerrainInfo : ConditionalTraitInfo
	{
		[FieldLoader.Require]
		[ActorReference]
		[Desc("Actor to transform into.")]
		public readonly string IntoActor = null;

		[FieldLoader.Require]
		[Desc("Terrain types that trigger the transformation.")]
		public readonly ImmutableArray<string> TerrainTypes = [];

		[Desc("Also trigger when standing on any terrain adjacent to cells with these types.")]
		public readonly ImmutableArray<string> AdjacentTerrainTypes = [];

		[Desc("Do NOT trigger if any adjacent cell has one of these terrain types.")]
		public readonly ImmutableArray<string> ExcludeIfAdjacentTo = [];

		[Desc("Skip the make animation when transforming.")]
		public readonly bool SkipMakeAnims = true;

		public override object Create(ActorInitializer init) { return new AotTransformsOnTerrain(init, this); }
	}

	public class AotTransformsOnTerrain : ConditionalTrait<AotTransformsOnTerrainInfo>, ITick
	{
		bool transformPending;

		public AotTransformsOnTerrain(ActorInitializer init, AotTransformsOnTerrainInfo info)
			: base(info) { }

		void ITick.Tick(Actor self)
		{
			if (IsTraitDisabled)
			{
				transformPending = false;
				return;
			}

			var loc = self.Location;
			if (!self.World.Map.Contains(loc))
				return;

			var currentTerrain = self.World.Map.GetTerrainInfo(loc).Type;

			// Check exclusion: abort if any adjacent cell matches ExcludeIfAdjacentTo
			if (Info.ExcludeIfAdjacentTo.Length > 0)
			{
				foreach (var neighbor in CVec.Directions)
				{
					var adjacent = loc + neighbor;
					if (!self.World.Map.Contains(adjacent))
						continue;
					if (Info.ExcludeIfAdjacentTo.Contains(self.World.Map.GetTerrainInfo(adjacent).Type))
					{
						transformPending = false;
						return;
					}
				}
			}

			var triggered = Info.TerrainTypes.Contains(currentTerrain);

			if (!triggered && Info.AdjacentTerrainTypes.Length > 0)
			{
				foreach (var neighbor in CVec.Directions)
				{
					var adjacent = loc + neighbor;
					if (!self.World.Map.Contains(adjacent))
						continue;
					if (Info.AdjacentTerrainTypes.Contains(self.World.Map.GetTerrainInfo(adjacent).Type))
					{
						triggered = true;
						break;
					}
				}
			}

			if (!triggered)
			{
				transformPending = false;
				return;
			}

			if (transformPending)
				return;

			transformPending = true;

			var transform = new Transform(Info.IntoActor)
			{
				SkipMakeAnims = Info.SkipMakeAnims,
				Facing = self.TraitOrDefault<IFacing>()?.Facing ?? WAngle.Zero
			};

			self.CancelActivity();
			self.QueueActivity(false, transform);
		}
	}
}
