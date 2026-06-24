#region Copyright & License Information
/*
 * Age of Tiberium mod — carries ORET spawn state through MCV↔FACT transforms.
 * Only spawns ORET on the first FACT deployment per MCV lineage.
 */
#endregion

using OpenRA;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Spawns a free actor once per MCV lineage (not on re-deploy). "
		+ "Place on both FACT (with SpawnActor set) and MCV (without SpawnActor) "
		+ "so the spent state carries through transforms via ITransformActorInitModifier.")]
	public class OretSpawnStateCarrierInfo : TraitInfo
	{
		[ActorReference]
		[Desc("Actor to spawn on first AddedToWorld. Leave empty to only carry state (e.g. on MCV).")]
		public readonly string SpawnActor = null;

		[Desc("Offset relative to top-left cell of the building.")]
		public readonly CVec SpawnOffset = CVec.Zero;

		[Desc("Direction the spawned unit faces.")]
		public readonly WAngle Facing = WAngle.Zero;

		public override object Create(ActorInitializer init) { return new OretSpawnStateCarrier(init, this); }
	}

	public class OretSpawnStateCarrier : INotifyAddedToWorld, ITransformActorInitModifier
	{
		readonly OretSpawnStateCarrierInfo info;
		bool spawned;

		public OretSpawnStateCarrier(ActorInitializer init, OretSpawnStateCarrierInfo info)
		{
			this.info = info;
			spawned = init.GetValue<OretSpawnedInit, bool>(false);
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			if (spawned || info.SpawnActor == null)
				return;

			spawned = true;
			self.World.AddFrameEndTask(w =>
			{
				w.CreateActor(info.SpawnActor,
				[
					new ParentActorInit(self),
					new LocationInit(self.Location + info.SpawnOffset),
					new OwnerInit(self.Owner),
					new FacingInit(info.Facing),
				]);
			});
		}

		void ITransformActorInitModifier.ModifyTransformActorInit(Actor self, TypeDictionary init)
		{
			init.Add(new OretSpawnedInit(spawned));
		}
	}

	public class OretSpawnedInit(bool value) : ValueActorInit<bool>(value), ISingleInstanceInit { }
}
