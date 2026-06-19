#region Copyright & License Information
/*
 * Age of Tiberium Mod (aotmod) — OreMineDurability trait
 * Fills the mine's StoresResources on creation and depletes it per ore trip.
 * When the store hits 0, the mine is destroyed (bypassing the damage system).
 */
#endregion

using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Fills the actor's resource store on creation and destroys it when depleted by ore trips.")]
	public class OreMineDurabilityInfo : TraitInfo
	{
		[Desc("Resource type matching the StoresResources trait.")]
		public readonly string ResourceType = "Tiberium";

		public override object Create(ActorInitializer init) { return new OreMineDurability(this); }
	}

	public class OreMineDurability : INotifyCreated
	{
		readonly OreMineDurabilityInfo info;
		IStoresResources store;
		StoresResources concreteStore;

		public OreMineDurability(OreMineDurabilityInfo info) { this.info = info; }

		void INotifyCreated.Created(Actor self)
		{
			store = self.TraitsImplementing<IStoresResources>()
				.FirstOrDefault(s => s.HasType(info.ResourceType));
			store?.AddResource(info.ResourceType, store.Capacity);
			concreteStore = store as StoresResources;
		}

		public void OnTrip(Actor self, Actor transporter)
		{
			if (store == null) return;
			store.RemoveResource(info.ResourceType, 1);
			if (concreteStore != null && concreteStore.ContentsSum == 0)
				self.Kill(transporter);
		}
	}
}
