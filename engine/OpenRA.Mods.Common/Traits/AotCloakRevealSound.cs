// === Age of Tiberium (aotmod) ===
// Spielt einen Sound ab, wenn dieser Aktor (z.B. die Spy-Plane-Aufklaerungskamera) in der Naehe
// getarnter FEINDLICHER Gebaeude erscheint. OpenRA hat keinen Hook fuer "Tarnung entdeckt":
// DetectCloaked macht getarnte Aktoren nur SICHTBAR (Cloak.IsVisible), es ruft KEIN Uncloak, es
// faellt also nirgends ein Sound an. User-Wunsch 2026-08-05: beim Aufdecken durch die Spy Plane
// soll das Gebaeude-Uncloak-Geraeusch kommen. Gehoert auf denselben Aktor wie DetectCloaked.
// === Ende aotmod ===

using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Plays a sound once when this actor enters the world near cloaked enemy buildings.",
		"Intended for a recon camera that also carries DetectCloaked.")]
	public class AotCloakRevealSoundInfo : TraitInfo
	{
		[Desc("Detection radius. Match the DetectCloaked range on the same actor.")]
		public readonly WDist Range = WDist.FromCells(10);

		[Desc("Sound played when at least one cloaked enemy building is found in range.")]
		public readonly string Sound = "trans1.aud";

		[Desc("Only react to cloaked actors that are buildings.")]
		public readonly bool BuildingsOnly = true;

		public override object Create(ActorInitializer init) { return new AotCloakRevealSound(this); }
	}

	public class AotCloakRevealSound : INotifyAddedToWorld
	{
		readonly AotCloakRevealSoundInfo info;

		public AotCloakRevealSound(AotCloakRevealSoundInfo info)
		{
			this.info = info;
		}

		void INotifyAddedToWorld.AddedToWorld(Actor self)
		{
			if (string.IsNullOrEmpty(info.Sound))
				return;

			// FrameEndTask: die Kamera und ihre Aufdeckung sind erst nach dem Einfuegen voll
			// wirksam. Der Sound haengt aber ohnehin nur an der Tarnung der Gebaeude, nicht an
			// der Sichtbarkeit -- der Delay schadet nicht und haelt es robust.
			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead || !self.IsInWorld)
					return;

				var found = w.FindActorsInCircle(self.CenterPosition, info.Range)
					.Where(a => a != self && !a.IsDead && a.IsInWorld
						&& a.Owner.RelationshipWith(self.Owner) == PlayerRelationship.Enemy
						&& (!info.BuildingsOnly || a.Info.HasTraitInfo<BuildingInfo>()))
					.FirstOrDefault(a => a.TraitsImplementing<Cloak>().Any(c => c.Cloaked));

				if (found != null)
					Game.Sound.Play(SoundType.World, info.Sound, found.CenterPosition);
			});
		}
	}
}
