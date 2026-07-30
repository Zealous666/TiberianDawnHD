#region Copyright & License Information
/*
 * Age of Tiberium mod (aotmod) — SpawnCompanionActor trait.
 * Spawns a single co-located companion actor on AddedToWorld and removes it again
 * when this actor leaves the world.
 *
 * Used to give the Ore Mine an oversized, invisible, passable keep-out footprint
 * (a larger non-buildable area) WITHOUT touching the mine's own Building.Dimensions —
 * which would otherwise also move its bib (WithBuildingBib derives width/rows from
 * Dimensions) and its render center. The companion carries only the extra footprint.
 */
#endregion

using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Spawns a single co-located companion actor when added to the world, and removes it ",
		"again when this actor leaves the world. Does not spawn in the map editor.")]
	public class SpawnCompanionActorInfo : TraitInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Actor type to spawn.")]
		public readonly string Actor = null;

		[Desc("Top-left cell offset of the spawned actor relative to this actor's top-left cell.")]
		public readonly CVec Offset = CVec.Zero;

		public override object Create(ActorInitializer init) { return new SpawnCompanionActor(this); }
	}

	public class SpawnCompanionActor : INotifyAddedToWorld, INotifyRemovedFromWorld
	{
		readonly SpawnCompanionActorInfo info;
		Actor companion;

		public SpawnCompanionActor(SpawnCompanionActorInfo info) { this.info = info; }

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			// Runtime-only reservation: never materialise a phantom actor in the editor world.
			if (self.World.Type == WorldType.Editor)
				return;

			self.World.AddFrameEndTask(w =>
			{
				companion = w.CreateActor(info.Actor,
				[
					new LocationInit(self.Location + info.Offset),
					new OwnerInit(self.Owner),
				]);
			});
		}

		void INotifyRemovedFromWorld.RemovedFromWorld(Actor self)
		{
			var c = companion;
			companion = null;
			if (c != null && !c.IsDead)
				c.Dispose();
		}
	}
}
