#region Copyright & License Information
/*
 * Age of Tiberium mod (aotmod) — SnapshotTransient marker trait.
 * Actors carrying this trait are NOT written into an AotWorldSnapshot save. Use it for actors
 * that another (saved) actor deterministically re-creates on load through INotifyAddedToWorld —
 * e.g. the ore mine's invisible keep-out zone (SpawnCompanionActor) and its scattered ore/gem
 * decoration (ScatterDecorationActors). Saving them as well would create a second copy on top
 * of the re-spawned one when the mine is restored (the same trap the bridge case documents in
 * AotWorldSnapshot.ShouldSave).
 */
#endregion

using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Marker: this actor is skipped by AotWorldSnapshot (its parent re-creates it on load).")]
	public class SnapshotTransientInfo : TraitInfo<SnapshotTransient> { }

	public class SnapshotTransient { }
}
