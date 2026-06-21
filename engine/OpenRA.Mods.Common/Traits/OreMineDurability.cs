#region Copyright & License Information
/*
 * Age of Tiberium Mod (aotmod) — OreMineDurability trait
 * Fills the mine's StoresResources on creation and depletes it per ore trip.
 * When the store hits 0, the mine is destroyed (bypassing the damage system).
 */
#endregion

using System.Linq;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	/// <summary>Marker interface: actor has a Health trait but should not display the HP bar.</summary>
	public interface IHideHealthBar { }

	[Desc("Fills the actor's resource store on creation and destroys it when depleted by ore trips.")]
	public class OreMineDurabilityInfo : TraitInfo
	{
		[Desc("Resource type matching the StoresResources trait.")]
		public readonly string ResourceType = "Tiberium";

		public override object Create(ActorInitializer init) { return new OreMineDurability(this); }
	}

	public class OreMineDurability : INotifyCreated, ISelectionBar, IHideHealthBar
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

		float ISelectionBar.GetValue()
		{
			if (store == null || store.Capacity == 0) return 0f;
			var stored = concreteStore != null ? concreteStore.ContentsSum : 0;
			return (float)stored / store.Capacity;
		}

		Color ISelectionBar.GetColor() => Color.Red;
		bool ISelectionBar.DisplayWhenEmpty => false;
	}
}
