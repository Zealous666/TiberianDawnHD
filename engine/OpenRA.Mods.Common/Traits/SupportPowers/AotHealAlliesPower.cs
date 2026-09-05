#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * "Care under Fire" superpower (Civilian Hospital). Instantly heals every living, non-building
 * unit owned by the activating player or an ally to full HP, once. No target selection, no
 * timed buff — unlike AotIronDomePower this is a one-shot effect with nothing to tick down.
 */
#endregion

using System.Linq;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("aotmod: Care-under-Fire-Superpower. Heilt alle lebenden, nicht-Gebaeude-Einheiten des",
		"aktivierenden Spielers UND seiner Verbuendeten sofort auf volle HP. Keine Zielauswahl,",
		"kein Timer - einmaliger Effekt. Der Aktor (neutrale Struktur) muss von einem Ingenieur",
		"eingenommen werden.")]
	public class AotHealAlliesPowerInfo : SupportPowerInfo
	{
		[Desc("Sound beim Aktivieren (UI, nur lokaler Spieler).")]
		public readonly string ActivationSound = "";

		[Desc("Sound beim Aktivieren (World, global fuer alle Spieler hoerbar).")]
		public readonly string GlobalActivationSound = "";

		public override object Create(ActorInitializer init) { return new AotHealAlliesPower(init.Self, this); }
	}

	public class AotHealAlliesPower : SupportPower
	{
		readonly AotHealAlliesPowerInfo info;

		public AotHealAlliesPower(Actor self, AotHealAlliesPowerInfo info)
			: base(self, info)
		{
			this.info = info;
		}

		public override void SelectTarget(Actor self, string order, SupportPowerManager manager)
		{
			// Sofort ausloesen, kein Ziel-Cursor.
			self.World.IssueOrder(new Order(order, self.Owner.PlayerActor, Target.Invalid, false));
		}

		public override void Activate(Actor self, Order order, SupportPowerManager manager)
		{
			base.Activate(self, order, manager);
			PlayLaunchSounds();

			if (!string.IsNullOrEmpty(info.ActivationSound) && self.Owner == self.World.LocalPlayer)
				Game.Sound.Play(SoundType.UI, info.ActivationSound);

			if (!string.IsNullOrEmpty(info.GlobalActivationSound))
				Game.Sound.Play(SoundType.World, info.GlobalActivationSound, self.CenterPosition);

			foreach (var a in self.World.Actors)
			{
				if (a.IsDead || !a.IsInWorld)
					continue;

				// Nur Einheiten (Infanterie/Fahrzeuge/Schiffe/Flugzeuge) - keine Gebaeude.
				if (a.Info.HasTraitInfo<BuildingInfo>())
					continue;

				var relationship = self.Owner.RelationshipWith(a.Owner);
				if (relationship != PlayerRelationship.Ally)
					continue;

				var health = a.TraitsImplementing<IHealth>().FirstOrDefault();
				if (health == null || health.IsDead || health.HP >= health.MaxHP)
					continue;

				// Negativer Schaden = Heilung, ignoreModifiers=true damit Ruestungstyp/Versus
				// keine Rolle spielt - volle Heilung fuer jede Einheit unabhaengig vom Typ.
				health.InflictDamage(a, self, new Damage(-(health.MaxHP - health.HP)), true);
			}
		}
	}
}
